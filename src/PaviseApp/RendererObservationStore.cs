using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal sealed class RendererObservationStore
    {
        private sealed class Record
        {
            internal string Id;

            internal string Exe;

            internal RendererFileStamp Stamp;

            internal long ObservedTicks;

            internal bool Validated;

            internal long CheckedTicks;
        }

        internal const string FileName = "Pavise.renderer-observations.dat";

        private readonly object gate = new object();

        private bool closed;

        private readonly string path;

        private readonly Dictionary<string, Record> records = new Dictionary<string, Record>(StringComparer.OrdinalIgnoreCase);

        internal RendererObservationStore(string directory)
        {
            path = Path.Combine(directory, "Pavise.renderer-observations.dat");
            Load();
        }

        internal bool Close(int timeoutMs)
        {
            if (timeoutMs < 0 || Monitor.IsEntered(gate) || !Monitor.TryEnter(gate, timeoutMs))
            {
                return false;
            }
            try
            {
                closed = true;
                return true;
            }
            finally
            {
                Monitor.Exit(gate);
            }
        }

        internal bool Has(string profileId, string executablePath)
        {
            lock (gate)
            {
                Record value;
                return profileId != null && records.TryGetValue(profileId, out value) && value.Validated && SamePath(value.Exe, executablePath);
            }
        }

        internal bool NeedsValidation(string profileId, string executablePath, long nowTicks)
        {
            lock (gate)
            {
                Record value;
                return !closed && profileId != null && records.TryGetValue(profileId, out value) && SamePath(value.Exe, executablePath) && (value.CheckedTicks == 0 || nowTicks < value.CheckedTicks || nowTicks - value.CheckedTicks >= TimeSpan.FromSeconds(5.0).Ticks);
            }
        }

        internal bool Validate(string profileId, string executablePath)
        {
            Record record;
            lock (gate)
            {
                if (closed || profileId == null || !records.TryGetValue(profileId, out record) || !SamePath(record.Exe, executablePath))
                {
                    return false;
                }
            }
            RendererFileStamp rendererFileStamp = RendererFileStamp.Read(executablePath);
            lock (gate)
            {
                Record currentRecord;
                if (closed || !records.TryGetValue(profileId, out currentRecord) || !object.ReferenceEquals(record, currentRecord))
                {
                    return false;
                }
                bool validatedValue = rendererFileStamp != null && record.Stamp.Same(rendererFileStamp);
                bool validationChanged = record.Validated != validatedValue;
                record.Validated = validatedValue;
                record.CheckedTicks = DateTime.UtcNow.Ticks;
                if (!validatedValue)
                {
                    records.Remove(profileId);
                }
                return validationChanged || !validatedValue;
            }
        }

        internal bool RecordActivity(string profileId, string executablePath, RendererObservationEvidence evidence, double utilization, RendererFileStamp before)
        {
            return RecordActivityWithResult(profileId, executablePath, evidence, utilization, before) == RendererObservationWriteResult.Recorded;
        }

        internal RendererObservationWriteResult RecordActivityWithResult(string profileId, string executablePath, RendererObservationEvidence evidence, double utilization, RendererFileStamp before)
        {
            if (string.IsNullOrEmpty(profileId) || string.IsNullOrEmpty(executablePath) || evidence != RendererObservationEvidence.Gpu3D || double.IsNaN(utilization) || double.IsInfinity(utilization) || utilization < 10.0 || before == null)
            {
                return RendererObservationWriteResult.InvalidEvidence;
            }
            lock (gate)
            {
                if (closed)
                {
                    return RendererObservationWriteResult.Closed;
                }
            }
            RendererFileStamp rendererFileStamp = RendererFileStamp.Read(executablePath);
            if (!before.Same(rendererFileStamp))
            {
                return RendererObservationWriteResult.FileChanged;
            }
            lock (gate)
            {
                if (closed)
                {
                    return RendererObservationWriteResult.Closed;
                }
                Record currentRecord;
                if (records.TryGetValue(profileId, out currentRecord) && SamePath(currentRecord.Exe, executablePath) && currentRecord.Stamp.Same(rendererFileStamp))
                {
                    currentRecord.Validated = true;
                    currentRecord.CheckedTicks = DateTime.UtcNow.Ticks;
                    return RendererObservationWriteResult.Recorded;
                }
                Record candidateRecord = new Record();
                candidateRecord.Id = profileId;
                candidateRecord.Exe = Path.GetFullPath(executablePath);
                candidateRecord.Stamp = rendererFileStamp;
                candidateRecord.ObservedTicks = DateTime.UtcNow.Ticks;
                candidateRecord.Validated = true;
                candidateRecord.CheckedTicks = DateTime.UtcNow.Ticks;
                Dictionary<string, Record> recordsSnapshot = new Dictionary<string, Record>(records, StringComparer.OrdinalIgnoreCase);
                recordsSnapshot[profileId] = candidateRecord;
                if (recordsSnapshot.Count > 2048)
                {
                    Record resolvedRecord = null;
                    foreach (Record retainedRecord in recordsSnapshot.Values)
                    {
                        if (resolvedRecord == null || retainedRecord.ObservedTicks < resolvedRecord.ObservedTicks)
                        {
                            resolvedRecord = retainedRecord;
                        }
                    }
                    if (resolvedRecord != null)
                    {
                        recordsSnapshot.Remove(resolvedRecord.Id);
                    }
                }
                if (!Save(recordsSnapshot))
                {
                    return RendererObservationWriteResult.SaveFailed;
                }
                records.Clear();
                foreach (KeyValuePair<string, Record> recordsSnapshotEntry in recordsSnapshot)
                {
                    records.Add(recordsSnapshotEntry.Key, recordsSnapshotEntry.Value);
                }
                return RendererObservationWriteResult.Recorded;
            }
        }

        internal void Forget(string profileId)
        {
            lock (gate)
            {
                if (!closed && profileId != null)
                {
                    records.Remove(profileId);
                }
            }
        }

        internal void Persist()
        {
            lock (gate)
            {
                Save(records);
            }
        }

        private bool Save(IDictionary<string, Record> values)
        {
            if (closed)
            {
                return false;
            }
            string sourceFileName = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream fileStream = new FileStream(sourceFileName, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (StreamWriter streamWriter = new StreamWriter(fileStream, new UTF8Encoding(false)))
                    {
                        streamWriter.WriteLine("PAVISE_RENDER_ACTIVITY_V1");
                        foreach (Record record in values.Values)
                        {
                            streamWriter.WriteLine(B64(record.Id) + "|" + B64(record.Exe) + "|" + record.Stamp.Length.ToString(CultureInfo.InvariantCulture) + "|" + record.Stamp.WriteTicks.ToString(CultureInfo.InvariantCulture) + "|" + record.ObservedTicks.ToString(CultureInfo.InvariantCulture) + "|gpu3d");
                        }
                        streamWriter.Flush();
                        fileStream.Flush(true);
                    }
                }
                if (File.Exists(path))
                {
                    File.Replace(sourceFileName, path, null);
                }
                else
                {
                    File.Move(sourceFileName, path);
                }
                return true;
            }
            catch (Exception error)
            {
                Logger.LogFailure(Lang.T("lib.render.observation.failed", 0), error);
                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(sourceFileName))
                    {
                        File.Delete(sourceFileName);
                    }
                }
                catch
                {
                }
            }
        }

        private void Load()
        {
            try
            {
                FileInfo fileInfo = new FileInfo(path);
                if (!fileInfo.Exists || fileInfo.Length > 2097152)
                {
                    return;
                }
                using (StreamReader streamReader = new StreamReader(path, new UTF8Encoding(false, true)))
                {
                    if (streamReader.ReadLine() != "PAVISE_RENDER_ACTIVITY_V1")
                    {
                        return;
                    }
                    string line;
                    while ((line = streamReader.ReadLine()) != null && records.Count < 2048 && line.Length <= 32768)
                    {
                        string[] lineParts = line.Split('|');
                        long result;
                        long result2;
                        long result3;
                        if (lineParts.Length == 6 && lineParts[5] == "gpu3d" && long.TryParse(lineParts[2], out result) && result > 0 && long.TryParse(lineParts[3], out result2) && result2 > 0 && long.TryParse(lineParts[4], out result3) && result3 > 0)
                        {
                            string executablePath = Un64(lineParts[0]);
                            string un64Result2 = Un64(lineParts[1]);
                            if (!string.IsNullOrWhiteSpace(executablePath) && !string.IsNullOrWhiteSpace(un64Result2) && Path.IsPathRooted(un64Result2) && SamePath(Path.GetFullPath(un64Result2), un64Result2))
                            {
                                records[executablePath] = new Record
                                {
                                    Id = executablePath,
                                    Exe = un64Result2,
                                    Stamp = new RendererFileStamp
                                    {
                                        Length = result,
                                        WriteTicks = result2
                                    },
                                    ObservedTicks = result3
                                };
                            }
                        }
                    }
                }
            }
            catch
            {
                records.Clear();
            }
        }

        private static string B64(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }

        private static string Un64(string value)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }

        private static bool SamePath(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
