// Optional, bounded history of observed 3D activity. Not a game-identity or safety
// certificate. Kept separate from the strict game library so a damaged cache can
// never invalidate the user's profiles.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal enum RendererObservationEvidence { None, Window, Forced, Gpu3D }

    internal sealed class RendererFileStamp
    {
        internal long Length;
        internal long WriteTicks;

        internal static RendererFileStamp Read(string path)
        {
            try
            {
                var file = new FileInfo(path);
                return file.Exists && file.Length > 0
                    ? new RendererFileStamp { Length = file.Length, WriteTicks = file.LastWriteTimeUtc.Ticks }
                    : null;
            }
            catch { return null; }
        }

        internal bool Same(RendererFileStamp other)
        {
            return other != null && Length == other.Length && WriteTicks == other.WriteTicks;
        }
    }

    internal sealed class RendererObservationStore
    {
        internal const string FileName = "Pavise.renderer-observations.dat";
        private const string Header = "PAVISE_RENDER_ACTIVITY_V1";
        private const int MaxRecords = 2048;
        private readonly object gate = new object();
        private bool closed;
        private readonly string path;
        private readonly Dictionary<string, Record> records = new Dictionary<string, Record>(StringComparer.OrdinalIgnoreCase);

        private sealed class Record
        {
            internal string Id;
            internal string Exe;
            internal RendererFileStamp Stamp;
            internal long ObservedTicks;
            internal bool Validated;
            internal long CheckedTicks;
        }

        internal RendererObservationStore(string directory)
        {
            path = Path.Combine(directory, FileName);
            Load();
        }

        // A successful close drains an in-progress atomic save and prevents
        // validation/Forget workers from recreating the cache during reset.
        internal bool Close(int timeoutMs)
        {
            if (timeoutMs < 0 || Monitor.IsEntered(gate)
                || !Monitor.TryEnter(gate, timeoutMs)) return false;
            try { closed = true; return true; }
            finally { Monitor.Exit(gate); }
        }

        // No filesystem access: safe for DrawItem, tooltips and UI event handlers.
        internal bool Has(string profileId, string executablePath)
        {
            lock (gate)
            {
                Record record;
                return profileId != null && records.TryGetValue(profileId, out record)
                    && record.Validated && SamePath(record.Exe, executablePath);
            }
        }

        internal bool NeedsValidation(string profileId, string executablePath, long nowTicks)
        {
            lock (gate)
            {
                Record record;
                return !closed && profileId != null && records.TryGetValue(profileId, out record)
                    && SamePath(record.Exe, executablePath)
                    && (record.CheckedTicks == 0 || nowTicks < record.CheckedTicks
                        || nowTicks - record.CheckedTicks >= TimeSpan.FromSeconds(5).Ticks);
            }
        }

        // Call only from a background worker or an isolated test.
        internal bool Validate(string profileId, string executablePath)
        {
            Record record;
            lock (gate)
                if (closed || profileId == null || !records.TryGetValue(profileId, out record)
                    || !SamePath(record.Exe, executablePath)) return false;
            // A queued validation of the previous executable must not invalidate
            // an observation already recorded for its replacement.
            RendererFileStamp stamp = RendererFileStamp.Read(executablePath);
            lock (gate)
            {
                Record current;
                if (closed || !records.TryGetValue(profileId, out current)
                    || !ReferenceEquals(record, current)) return false;
                bool valid = stamp != null && record.Stamp.Same(stamp);
                bool changed = record.Validated != valid;
                record.Validated = valid;
                record.CheckedTicks = DateTime.UtcNow.Ticks;
                // Once a file/path changes, an old observation must not revive if
                // a different file later happens to reuse the original timestamp.
                if (!valid) records.Remove(profileId);
                return changed || !valid;
            }
        }

        internal bool RecordActivity(string profileId, string executablePath,
            RendererObservationEvidence evidence, double utilization, RendererFileStamp before)
        {
            if (string.IsNullOrEmpty(profileId) || string.IsNullOrEmpty(executablePath)
                || evidence != RendererObservationEvidence.Gpu3D
                || double.IsNaN(utilization) || double.IsInfinity(utilization)
                || utilization < GpuEvidence.MinElectUtilization || before == null) return false;
            lock (gate) { if (closed) return false; }
            RendererFileStamp after = RendererFileStamp.Read(executablePath);
            if (!before.Same(after)) return false;
            lock (gate)
            {
                if (closed) return false;
                Record old;
                if (records.TryGetValue(profileId, out old) && SamePath(old.Exe, executablePath)
                    && old.Stamp.Same(after))
                {
                    old.Validated = true;
                    old.CheckedTicks = DateTime.UtcNow.Ticks;
                    return true;
                }
                var candidate = new Record
                {
                    Id = profileId, Exe = Path.GetFullPath(executablePath), Stamp = after,
                    ObservedTicks = DateTime.UtcNow.Ticks, Validated = true,
                    CheckedTicks = DateTime.UtcNow.Ticks
                };
                var next = new Dictionary<string, Record>(records, StringComparer.OrdinalIgnoreCase);
                next[profileId] = candidate;
                if (next.Count > MaxRecords)
                {
                    Record oldest = null;
                    foreach (Record item in next.Values)
                        if (oldest == null || item.ObservedTicks < oldest.ObservedTicks) oldest = item;
                    if (oldest != null) next.Remove(oldest.Id);
                }
                if (!Save(next)) return false;
                records.Clear();
                foreach (var item in next) records.Add(item.Key, item.Value);
                return true;
            }
        }

        // Invalidate immediately in memory; the caller may persist on a worker.
        internal void Forget(string profileId)
        {
            lock (gate) { if (!closed && profileId != null) records.Remove(profileId); }
        }

        internal void Persist()
        {
            lock (gate) Save(records);
        }

        private bool Save(IDictionary<string, Record> values)
        {
            if (closed) return false;
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
                {
                    writer.WriteLine(Header);
                    foreach (Record item in values.Values)
                        writer.WriteLine(B64(item.Id) + "|" + B64(item.Exe) + "|"
                            + item.Stamp.Length.ToString(CultureInfo.InvariantCulture) + "|"
                            + item.Stamp.WriteTicks.ToString(CultureInfo.InvariantCulture) + "|"
                            + item.ObservedTicks.ToString(CultureInfo.InvariantCulture) + "|gpu3d");
                    writer.Flush();
                    file.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogFailure(Lang.T("lib.render.observation.failed"), ex);
                return false;
            }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }

        private void Load()
        {
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length > 2 * 1024 * 1024) return;
                using (var reader = new StreamReader(path, new UTF8Encoding(false, true)))
                {
                    if (reader.ReadLine() != Header) return;
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (records.Count >= MaxRecords || line.Length > 32768) break;
                        string[] parts = line.Split('|');
                        long length, write, observed;
                        if (parts.Length != 6 || parts[5] != "gpu3d"
                            || !long.TryParse(parts[2], out length) || length <= 0
                            || !long.TryParse(parts[3], out write) || write <= 0
                            || !long.TryParse(parts[4], out observed) || observed <= 0) continue;
                        string id = Un64(parts[0]), exe = Un64(parts[1]);
                        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(exe)
                            || !Path.IsPathRooted(exe) || !SamePath(Path.GetFullPath(exe), exe)) continue;
                        records[id] = new Record { Id = id, Exe = exe,
                            Stamp = new RendererFileStamp { Length = length, WriteTicks = write }, ObservedTicks = observed };
                    }
                }
            }
            catch { records.Clear(); } // optional history; never touch the game library
        }

        private static string B64(string value) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(value)); }
        private static string Un64(string value) { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
        private static bool SamePath(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }
}
