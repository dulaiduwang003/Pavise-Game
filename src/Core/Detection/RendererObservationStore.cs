// File purpose Optional bounded history of 3D activity observations; it is neither proof of game identity nor a safety endorsement
// Stored apart from the strict game library, so a corrupt cache can never
// take the user's profiles down with it
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal enum RendererObservationEvidence { None, Window, Forced, Gpu3D }
    internal enum RendererObservationWriteResult { Recorded, InvalidEvidence, FileChanged, Closed, SaveFailed }

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

        // A successful close drains any in-progress atomic save and blocks validation and Forget
        // on the worker thread from rebuilding the cache during a reset
        internal bool Close(int timeoutMs)
        {
            if (timeoutMs < 0 || Monitor.IsEntered(gate)
                || !Monitor.TryEnter(gate, timeoutMs)) return false;
            try { closed = true; return true; }
            finally { Monitor.Exit(gate); }
        }

        // Never touches the file system, so it is safe to call from DrawItem, tooltips, and UI event handlers
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

        // Only call from the background worker thread or isolated tests
        internal bool Validate(string profileId, string executablePath)
        {
            Record record;
            lock (gate)
                if (closed || profileId == null || !records.TryGetValue(profileId, out record)
                    || !SamePath(record.Exe, executablePath)) return false;
            // A queued validation of an old executable must not invalidate the observation
            // already recorded for its replacement
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
                // Once the file or path has changed, even if another file later happens to reuse the original timestamp
                // the old observation must not come back to life
                if (!valid) records.Remove(profileId);
                return changed || !valid;
            }
        }

        internal bool RecordActivity(string profileId, string executablePath,
            RendererObservationEvidence evidence, double utilization, RendererFileStamp before)
        {
            return RecordActivityWithResult(profileId, executablePath, evidence, utilization, before)
                == RendererObservationWriteResult.Recorded;
        }

        internal RendererObservationWriteResult RecordActivityWithResult(string profileId, string executablePath,
            RendererObservationEvidence evidence, double utilization, RendererFileStamp before)
        {
            if (string.IsNullOrEmpty(profileId) || string.IsNullOrEmpty(executablePath)
                || evidence != RendererObservationEvidence.Gpu3D
                || double.IsNaN(utilization) || double.IsInfinity(utilization)
                || utilization < GpuEvidence.MinElectUtilization || before == null) return RendererObservationWriteResult.InvalidEvidence;
            lock (gate) { if (closed) return RendererObservationWriteResult.Closed; }
            RendererFileStamp after = RendererFileStamp.Read(executablePath);
            if (!before.Same(after)) return RendererObservationWriteResult.FileChanged;
            lock (gate)
            {
                if (closed) return RendererObservationWriteResult.Closed;
                Record old;
                if (records.TryGetValue(profileId, out old) && SamePath(old.Exe, executablePath)
                    && old.Stamp.Same(after))
                {
                    old.Validated = true;
                    old.CheckedTicks = DateTime.UtcNow.Ticks;
                    return RendererObservationWriteResult.Recorded;
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
                if (!Save(next)) return RendererObservationWriteResult.SaveFailed;
                records.Clear();
                foreach (var item in next) records.Add(item.Key, item.Value);
                return RendererObservationWriteResult.Recorded;
            }
        }

        // Invalidates in memory immediately; persisting is done by the caller on the worker thread
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
            catch { records.Clear(); } // optional history never touch the game library
        }

        private static string B64(string value) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(value)); }
        private static string Un64(string value) { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
        private static bool SamePath(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }
}
