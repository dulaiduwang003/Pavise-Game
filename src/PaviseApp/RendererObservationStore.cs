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

		private const string Header = "PAVISE_RENDER_ACTIVITY_V1";

		private const int MaxRecords = 2048;

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
			Record value;
			lock (gate)
			{
				if (closed || profileId == null || !records.TryGetValue(profileId, out value) || !SamePath(value.Exe, executablePath))
				{
					return false;
				}
			}
			RendererFileStamp rendererFileStamp = RendererFileStamp.Read(executablePath);
			lock (gate)
			{
				Record value2;
				if (closed || !records.TryGetValue(profileId, out value2) || !object.ReferenceEquals(value, value2))
				{
					return false;
				}
				bool validatedValue = rendererFileStamp != null && value.Stamp.Same(rendererFileStamp);
				bool matchesValidated = value.Validated != validatedValue;
				value.Validated = validatedValue;
				value.CheckedTicks = DateTime.UtcNow.Ticks;
				if (!validatedValue)
				{
					records.Remove(profileId);
				}
				return matchesValidated || !validatedValue;
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
				Record value;
				if (records.TryGetValue(profileId, out value) && SamePath(value.Exe, executablePath) && value.Stamp.Same(rendererFileStamp))
				{
					value.Validated = true;
					value.CheckedTicks = DateTime.UtcNow.Ticks;
					return RendererObservationWriteResult.Recorded;
				}
				Record record = new Record();
				record.Id = profileId;
				record.Exe = Path.GetFullPath(executablePath);
				record.Stamp = rendererFileStamp;
				record.ObservedTicks = DateTime.UtcNow.Ticks;
				record.Validated = true;
				record.CheckedTicks = DateTime.UtcNow.Ticks;
				Record value2 = record;
				Dictionary<string, Record> dictionary = new Dictionary<string, Record>(records, StringComparer.OrdinalIgnoreCase);
				dictionary[profileId] = value2;
				if (dictionary.Count > 2048)
				{
					Record record2 = null;
					foreach (Record value3 in dictionary.Values)
					{
						if (record2 == null || value3.ObservedTicks < record2.ObservedTicks)
						{
							record2 = value3;
						}
					}
					if (record2 != null)
					{
						dictionary.Remove(record2.Id);
					}
				}
				if (!Save(dictionary))
				{
					return RendererObservationWriteResult.SaveFailed;
				}
				records.Clear();
				foreach (KeyValuePair<string, Record> item in dictionary)
				{
					records.Add(item.Key, item.Value);
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
						foreach (Record value in values.Values)
						{
							streamWriter.WriteLine(B64(value.Id) + "|" + B64(value.Exe) + "|" + value.Stamp.Length.ToString(CultureInfo.InvariantCulture) + "|" + value.Stamp.WriteTicks.ToString(CultureInfo.InvariantCulture) + "|" + value.ObservedTicks.ToString(CultureInfo.InvariantCulture) + "|gpu3d");
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
				Logger.LogFailure(Lang.T("lib.render.observation.failed"), error);
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
						if (lineParts.Length == 6 && !(lineParts[5] != "gpu3d") && long.TryParse(lineParts[2], out result) && result > 0 && long.TryParse(lineParts[3], out result2) && result2 > 0 && long.TryParse(lineParts[4], out result3) && result3 > 0)
						{
							string un64Result = Un64(lineParts[0]);
							string un64Result2 = Un64(lineParts[1]);
							if (!string.IsNullOrWhiteSpace(un64Result) && !string.IsNullOrWhiteSpace(un64Result2) && Path.IsPathRooted(un64Result2) && SamePath(Path.GetFullPath(un64Result2), un64Result2))
							{
								records[un64Result] = new Record
								{
									Id = un64Result,
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
