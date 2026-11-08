using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PaviseApp
{
	internal partial class GameMode
	{
		private void TryAutoAddForegroundGame()
		{
			if (!autoAddOn || stopping || !enabled || ProfileStoreSaveFailed || autoIgnoreLoadFailed || libraryIgnoreTransaction.RecoveryPending)
			{
				return;
			}
			bool activeSnapshot;
			int epoch;
			lock (sync)
			{
				activeSnapshot = active;
				epoch = Volatile.Read(ref autoAddEpoch);
			}
			ForegroundGameWindowSnapshot window;
			if (activeSnapshot || !AutoAddWindow(out window) || window.Pid <= 0 || window.Pid == selfPid)
			{
				return;
			}
			int pid = window.Pid;
			long now = DateTime.UtcNow.Ticks;
			if (now < autoAddGateTicks)
			{
				return;
			}
			Interlocked.Exchange(ref autoAddGateTicks, now + TimeSpan.FromSeconds(5.0).Ticks);
			GameProcessSnapshot identity;
			if (!CaptureAutoAddIdentity(pid, out identity) || !GameSessionDetector.IsLibraryCandidate(identity.Name, identity.Path, windowsPrefix) || identity.Pid != pid || identity.Creation <= 0)
			{
				return;
			}
			identity = new GameProcessSnapshot
			{
				Pid = pid,
				Creation = identity.Creation,
				Name = identity.Name,
				Path = identity.Path
			};
			string path = identity.Path;
			if (!autoAddFailureBudget.Allowed(pid, identity.Creation, path, "capture")
				|| !autoAddFailureBudget.Allowed(pid, identity.Creation, path, "save")) return;
			lock (sync)
			{
				if (autoAddIgnore.Contains(path))
				{
					return;
				}
				AutoAddAttempt value;
				if (autoAddAttempts.TryGetValue(path, out value) && (value.Pid != pid || value.Creation != identity.Creation))
				{
					autoAddAttempts.Remove(path);
					autoAddRejectUntil.Remove(path);
				}
				long value2;
				if (autoAddRejectUntil.TryGetValue(path, out value2) && now < value2)
				{
					return;
				}
				foreach (GameProfile profile in profiles)
				{
					if (SameLibraryPath(profile.ExecutablePath, path) || SameLibraryPath(profile.LearnedExecutablePath, path) || profile.ContainsPath(path))
					{
						return;
					}
				}
				value = AutoAddAttemptLocked(path, now);
				value.Pid = pid;
				value.Creation = identity.Creation;
				value.LastSeen = now;
			}
			if (Interlocked.CompareExchange(ref rendererGpuSamplingBusy, 1, 0) != 0)
			{
				return;
			}
			bool autoAddForegroundGameReady = false;
			try
			{
				autoAddForegroundGameReady = ThreadPool.QueueUserWorkItem(delegate
				{
					try
					{
						FinishAutoAdd(identity, window.FullscreenLike, epoch, now);
					}
					catch (Exception ex)
					{
						if (!AutoAddCanceled(epoch))
						{
							RememberAutoAddReject(path, now);
							RecordAutoAddCaptureFailure(pid, identity.Creation, path, "capture", ex.GetType().Name);
							NoteAutoAdd(path, "CaptureUnavailable", ex.GetType().Name, now);
						}
					}
					finally
					{
						Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
					}
				});
			}
			catch
			{
			}
			finally
			{
				if (!autoAddForegroundGameReady)
				{
					RecordAutoAddCaptureFailure(pid, identity.Creation, path, "queue", "QueueUnavailable");
					RememberAutoAddReject(path, DateTime.UtcNow.Ticks);
					Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
				}
				else
				{
					Interlocked.Exchange(ref autoAddGateTicks, now + 300000000);
				}
			}
		}
		private bool AutoAddCanceled(int epoch)
		{
			if (!stopping && !panicReq && enabled && autoAddOn)
			{
				return Volatile.Read(ref autoAddEpoch) != epoch;
			}
			return true;
		}
		private void RecordAutoAddCaptureFailure(int pid, long creation, string path, string phase, string reason)
		{
			int count = autoAddFailureBudget.Failed(pid, creation, path, "capture");
			Logger.Warn("Auto-add " + phase + " failure=" + count + "/3 target=" + pid + "/" + creation
				+ " reason=" + reason + (count >= 3 ? "; exhausted for this process identity" : "; backing off"));
		}
		private void FinishAutoAdd(GameProcessSnapshot identity, bool fullscreen, int epoch, long now)
		{
			if (AutoAddCanceled(epoch))
			{
				return;
			}
			int pid = identity.Pid;
			string path = identity.Path;
			GpuSampleDiagnostics diagnostics = null;
			Dictionary<int, double> dictionary = GpuEvidence.Sample3D(2, 700, () => AutoAddCanceled(epoch), pid, out diagnostics);
			if (AutoAddCanceled(epoch))
			{
				return;
			}
			if (dictionary == null)
			{
				RecordAutoAddCaptureFailure(pid, identity.Creation, path, "capture", diagnostics == null ? "GpuUnavailable" : diagnostics.ToString());
				RememberAutoAddReject(path, now);
				NoteAutoAdd(path, "GpuUnavailable", (diagnostics == null) ? null : diagnostics.ToString(), now);
				return;
			}
			autoAddFailureBudget.Succeeded(pid, identity.Creation, path, "capture");
			double value;
			if (!dictionary.TryGetValue(pid, out value))
			{
				value = 0.0;
			}
			if (double.IsNaN(value) || double.IsInfinity(value) || value < 10.0)
			{
				lock (sync)
				{
					AutoAddAttempt value2;
					if (autoAddAttempts.TryGetValue(path, out value2))
					{
						value2.Confirmations = 0;
					}
				}
				RememberAutoAddReject(path, now);
				NoteAutoAdd(path, "ActivityWaiting", null, now);
				return;
			}
			bool activeSnapshot;
			lock (sync)
			{
				activeSnapshot = active;
			}
			ForegroundGameWindowSnapshot window;
			GameProcessSnapshot identity2;
			if (activeSnapshot || AutoAddCanceled(epoch) || !AutoAddWindow(out window) || window.Pid != pid || !CaptureAutoAddIdentity(pid, out identity2) || identity2 == null || identity2.Pid != pid || identity2.Creation != identity.Creation || !SameLibraryPath(identity2.Path, path))
			{
				return;
			}
			lock (sync)
			{
				if (AutoAddCanceled(epoch) || !AutoDetectedCommitAllowed(path))
				{
					return;
				}
				AutoAddAttempt autoAddAttempt = AutoAddAttemptLocked(path, now);
				if (autoAddAttempt.Pid != pid || autoAddAttempt.Creation != identity.Creation)
				{
					autoAddAttempt.Pid = pid;
					autoAddAttempt.Creation = identity.Creation;
					autoAddAttempt.Confirmations = (autoAddAttempt.Misses = 0);
				}
				autoAddAttempt.LastSeen = now;
				autoAddAttempt.Misses = 0;
				if ((!fullscreen || !window.FullscreenLike || !(value >= 20.0)) && (autoAddAttempt.Confirmations == 0 || now < autoAddAttempt.EvidenceAt || now - autoAddAttempt.EvidenceAt > 1200000000))
				{
					autoAddAttempt.Confirmations = 1;
					autoAddAttempt.EvidenceAt = now;
					autoAddRejectUntil[path] = now + 300000000;
					NoteAutoAdd(path, "ConfirmingActivity", null, now);
					return;
				}
			}
			string error;
			bool persistenceFailed;
			if (!autoAddFailureBudget.Allowed(pid, identity.Creation, path, "save")) return;
			if (!AddAutoDetectedExecutableCore(path, out error, out persistenceFailed, () => !AutoAddCanceled(epoch)))
			{
				if (persistenceFailed)
				{
					int failed = autoAddFailureBudget.Failed(pid, identity.Creation, path, "save");
					Logger.Warn("Auto-add durable save failure=" + failed + "/3 target=" + pid + "/" + identity.Creation
						+ " reason=" + error + (failed >= 3 ? "; automatic save stopped for this process identity" : ""));
				}
				RememberAutoAddReject(path, now);
				return;
			}
			autoAddFailureBudget.Succeeded(pid, identity.Creation, path, "save");
			lock (sync)
			{
				autoAddRejectUntil.Remove(path);
				autoAddAttempts.Remove(path);
			}
			Logger.Log(Lang.T("log.autoadd.3") + (int)value + Lang.T("log.autoadd.4") + identity.Name + Lang.T("log.autoadd.5") + path + Lang.T("log.autoadd.6"));
		}
		private AutoAddAttempt AutoAddAttemptLocked(string path, long now)
		{
			AutoAddAttempt value;
			if (autoAddAttempts.TryGetValue(path, out value))
			{
				return value;
			}
			if (autoAddAttempts.Count >= 64)
			{
				string key = null;
				long maxValue = long.MaxValue;
				foreach (KeyValuePair<string, AutoAddAttempt> autoAddAttempt2 in autoAddAttempts)
				{
					if (autoAddAttempt2.Value.LastSeen < maxValue)
					{
						key = autoAddAttempt2.Key;
						maxValue = autoAddAttempt2.Value.LastSeen;
					}
				}
				if (key != null)
				{
					autoAddAttempts.Remove(key);
					autoAddRejectUntil.Remove(key);
				}
			}
			AutoAddAttempt autoAddAttempt = new AutoAddAttempt();
			autoAddAttempt.LastSeen = now;
			value = autoAddAttempt;
			autoAddAttempts[path] = value;
			return value;
		}
		private void RememberAutoAddReject(string path, long now)
		{
			lock (sync)
			{
				if (autoAddRejectUntil.Count >= 64)
				{
					List<string> list = new List<string>();
					foreach (KeyValuePair<string, long> item in autoAddRejectUntil)
					{
						if (item.Value <= now)
						{
							list.Add(item.Key);
						}
					}
					foreach (string item2 in list)
					{
						autoAddRejectUntil.Remove(item2);
					}
					if (autoAddRejectUntil.Count >= 64)
					{
						autoAddRejectUntil.Clear();
					}
				}
				AutoAddAttempt autoAddAttempt = AutoAddAttemptLocked(path, now);
				autoAddAttempt.LastSeen = now;
				autoAddAttempt.Confirmations = 0;
				if (autoAddAttempt.Misses < 4)
				{
					autoAddAttempt.Misses++;
				}
				int rememberAutoAddRejectValue = ((autoAddAttempt.Misses == 1) ? 30 : ((autoAddAttempt.Misses == 2) ? 60 : ((autoAddAttempt.Misses == 3) ? 120 : 300)));
				autoAddRejectUntil[path] = now + (long)rememberAutoAddRejectValue * 10000000L;
			}
		}
		private void NoteAutoAdd(string path, string reason, string detail, long now)
		{
			if (reason == "ActivityWaiting" || reason == "ConfirmingActivity") return;
			lock (sync)
			{
				AutoAddAttempt autoAddAttempt = AutoAddAttemptLocked(path, now);
				if (autoAddAttempt.Note == reason && now < autoAddAttempt.NoteAt)
				{
					return;
				}
				autoAddAttempt.Note = reason;
				autoAddAttempt.NoteAt = now + TimeSpan.FromMinutes(5.0).Ticks;
			}
			Logger.Info("AUTOADD exe=" + Path.GetFileName(path) + " result=" + reason + (string.IsNullOrEmpty(detail) ? "" : (" detail=" + detail)));
		}
		private bool AutoAddWindow(out ForegroundGameWindowSnapshot window)
		{
			return GameSessionDetector.TryForegroundGameWindow(out window);
		}
		private bool CaptureAutoAddIdentity(int pid, out GameProcessSnapshot identity)
		{
			identity = null;
			try
			{
				if (GameSessionDetector.TryCaptureProcessIdentity(pid, selfSession, out identity))
				{
					return true;
				}
				ProcessSnapshot processSnapshot = ProcessSnapshotSource.Capture(selfSession, 0);
				return processSnapshot != null && GameSessionDetector.TryCaptureProcessIdentity(processSnapshot.Find(pid), selfSession, out identity);
			}
			catch
			{
				identity = null;
				return false;
			}
		}
		public List<string> GetAutoAddIgnoredPaths()
		{
			List<string> list;
			lock (sync)
			{
				list = new List<string>(autoAddIgnore);
			}
			list.Sort(StringComparer.OrdinalIgnoreCase);
			return list;
		}
		public bool RemoveAutoAddIgnoredPath(string path)
		{
			if (string.IsNullOrWhiteSpace(path))
			{
				return false;
			}
			string pathTrimmed = path.Trim();
			lock (sync)
			{
				if (stopping || ProfileStoreSaveFailed || !EnsureLibraryReadyLocked() || !autoAddIgnore.Contains(pathTrimmed))
				{
					return false;
				}
				HashSet<string> hashSet = new HashSet<string>(autoAddIgnore, StringComparer.OrdinalIgnoreCase);
				hashSet.Remove(pathTrimmed);
				if (!CommitLibraryLocked(GetProfiles(), hashSet))
				{
					return false;
				}
				autoAddRejectUntil.Remove(pathTrimmed);
				autoAddAttempts.Remove(pathTrimmed);
				Interlocked.Increment(ref autoAddEpoch);
				Interlocked.Exchange(ref autoAddGateTicks, 0L);
			}
			KickGameDetectionNow();
			RaiseLibraryChanged();
			return true;
		}
		private bool AddAutoDetectedExecutableCore(string executablePath, out string error, Func<bool> canCommit = null)
		{
			bool persistenceFailed;
			return AddAutoDetectedExecutableCore(executablePath, out error, out persistenceFailed, canCommit);
		}
		private bool AddAutoDetectedExecutableCore(string executablePath, out string error, out bool persistenceFailed, Func<bool> canCommit = null)
		{
			error = null;
			persistenceFailed = false;
			lock (sync)
			{
				if (!AutoDetectedCommitAllowed(executablePath) || (canCommit != null && !canCommit()) || !EnsureLibraryReadyLocked())
				{
					return false;
				}
				List<GameProfile> next = GetProfiles();
				HashSet<string> nextIgnore = new HashSet<string>(autoAddIgnore, StringComparer.OrdinalIgnoreCase);
				if (!StageGameExecutable(null, executablePath, null, next, nextIgnore, out error) || !CommitLibraryLocked(next, nextIgnore, out persistenceFailed, () => AutoDetectedCommitAllowed(executablePath) && (canCommit == null || canCommit())))
				{
					return false;
				}
			}
			KickLibraryChanged();
			return true;
		}
		private bool AutoDetectedCommitAllowed(string path)
		{
			if (!stopping && !panicReq && enabled && autoAddOn && !active && !ProfileStoreSaveFailed && !autoIgnoreLoadFailed)
			{
				return !autoAddIgnore.Contains(path);
			}
			return false;
		}
		private bool VerifyRendererIdentity(IntPtr h, int pid, BoostPass pass, out long currentCreation)
		{
			string actualName = Native.ImageName(h);
			long cpu;
			ulong io;
			if (!Native.QueryProcessSample(h, out currentCreation, out cpu, out io))
			{
				Logger.Warn(Lang.T("log.gamemodeboost.5") + pass.RendererName + " pid " + pid + Lang.T("log.gamemodeboost.6"));
				return false;
			}
			if (!RendererIdentityMatches(pass.RendererPid, pass.RendererCreation, pass.RendererName, pid, currentCreation, actualName))
			{
				Logger.Log(Lang.T("log.gamemodeboost.7") + pid + Lang.T("log.gamemodeboost.8"));
				return false;
			}
			return true;
		}
		internal static bool RendererIdentityMatches(int expectedPid, long expectedCreation, string expectedName, int actualPid, long actualCreation, string actualName)
		{
			if (expectedPid > 0 && expectedPid == actualPid && expectedCreation > 0 && expectedCreation == actualCreation && !string.IsNullOrEmpty(expectedName) && !string.IsNullOrEmpty(actualName))
			{
				return string.Equals(expectedName, actualName, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}
		private static bool SameCriticalThreadDetection(GameDetection left, GameDetection right)
		{
			if (left == null || right == null)
			{
				return left == right;
			}
			if (left.RendererPid == right.RendererPid && left.RendererCreation == right.RendererCreation && left.RendererCandidateSelected == right.RendererCandidateSelected && left.RendererSafetyOnly == right.RendererSafetyOnly && left.RequiresGpuConfirm == right.RequiresGpuConfirm && string.Equals(left.RendererPath, right.RendererPath, StringComparison.OrdinalIgnoreCase) && left.Profile != null && right.Profile != null)
			{
				return left.Profile.Id == right.Profile.Id;
			}
			return false;
		}
		internal bool TryLearnConfirmedRenderer(GameDetection hit)
		{
			return TryLearnConfirmedRenderer(hit, null);
		}
		private bool TryLearnConfirmedRenderer(GameDetection hit, Func<bool> stillCurrent)
		{
			if (hit == null || hit.Profile == null || !hit.RendererCandidateSelected || !hit.RendererLearnable || hit.RendererSafetyOnly || hit.RequiresGpuConfirm || hit.Profile.ForceTrigger || hit.RendererPid <= 0 || hit.RendererCreation <= 0)
			{
				return false;
			}
			return TryLearnRendererCore(hit.Profile.Id, hit.RendererPath, hit.RendererName, hit.Profile, stillCurrent);
		}
		private bool TryLearnRendererCore(string profileId, string rendererPath, string rendererName, GameProfile observedProfile, Func<bool> stillCurrent)
		{
			if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(rendererPath) || string.IsNullOrWhiteSpace(rendererName))
			{
				return false;
			}
			string trimTrimmed = rendererPath.Trim().Trim('"');
			string executablePathValue = GameProfileStore.NormalizePath(trimTrimmed);
			if (executablePathValue == null || !Path.IsPathRooted(trimTrimmed))
			{
				return false;
			}
			string pathRoot = Path.GetPathRoot(trimTrimmed);
			if (string.IsNullOrEmpty(pathRoot) || pathRoot.Length < 3 || (!pathRoot.EndsWith("\\", StringComparison.Ordinal) && !pathRoot.EndsWith("/", StringComparison.Ordinal)))
			{
				return false;
			}
			string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(executablePathValue);
			if (string.IsNullOrEmpty(fileNameWithoutExtension) || !string.Equals(fileNameWithoutExtension, StripExe(rendererName), StringComparison.OrdinalIgnoreCase) || !GameSessionDetector.IsLibraryCandidate(fileNameWithoutExtension, executablePathValue, windowsPrefix))
			{
				return false;
			}
			string rootValue = ResolveLibraryInstallRoot(executablePathValue, GameScan.InferGameRoot(executablePathValue));
			if (rootValue != null && !FamilyBoundary.UnderRoot(executablePathValue, rootValue))
			{
				rootValue = null;
			}
			string name;
			lock (sync)
			{
				if (stopping || ProfileStoreSaveFailed || (stillCurrent != null && !stillCurrent()))
				{
					return false;
				}
				GameProfile gameProfile = FindProfileLocked(profileId);
				if (gameProfile == null || gameProfile.ForceTrigger || RendererPathClaimedLocked(gameProfile, executablePathValue))
				{
					return false;
				}
				string restrictFallbackResult = GameInstallScope.RestrictFallback(gameProfile.ExecutablePath, NormalizeGameRoot(gameProfile.Root));
				if (restrictFallbackResult != null && FamilyBoundary.UnderRoot(executablePathValue, restrictFallbackResult))
				{
					rootValue = restrictFallbackResult;
				}
				bool valuesEqual = string.Equals(gameProfile.ExecutablePath, executablePathValue, StringComparison.OrdinalIgnoreCase);
				if (!valuesEqual && observedProfile != null && (!string.Equals(gameProfile.ExecutablePath, observedProfile.ExecutablePath, StringComparison.OrdinalIgnoreCase) || !string.Equals(gameProfile.LearnedExecutablePath, observedProfile.LearnedExecutablePath, StringComparison.OrdinalIgnoreCase) || !string.Equals(gameProfile.Root, observedProfile.Root, StringComparison.OrdinalIgnoreCase)))
				{
					return false;
				}
				if (valuesEqual && gameProfile.LearnedExecutablePath == null && string.Equals(gameProfile.Root, rootValue, StringComparison.OrdinalIgnoreCase) && gameProfile.Entries.Count == 1 && gameProfile.Entries.Contains(fileNameWithoutExtension))
				{
					return true;
				}
				GameProfile gameProfile2 = gameProfile.Clone();
				gameProfile2.ExecutablePath = executablePathValue;
				gameProfile2.LearnedExecutablePath = null;
				gameProfile2.Root = rootValue;
				gameProfile2.Entries.Clear();
				gameProfile2.Entries.Add(fileNameWithoutExtension);
				int index = profiles.IndexOf(gameProfile);
				List<GameProfile> list = new List<GameProfile>(profiles);
				list[index] = gameProfile2;
				if (stillCurrent != null && !stillCurrent())
				{
					return false;
				}
				if (!SaveProfileSnapshotLocked(list, stillCurrent))
				{
					return false;
				}
				profiles[index] = gameProfile2;
				if (!valuesEqual)
				{
					ForgetRendererObservation(profileId);
				}
				name = gameProfile2.Name;
			}
			Logger.Log(Lang.T("log.gamemodelibrary.2") + name + Lang.T("log.gamemodelibrary.3") + fileNameWithoutExtension + Lang.T("log.gamemodelibrary.4") + executablePathValue + " ");
			InvalidateAutomaticFullscreenOptimizations();
			RequestFullGameDetection();
			RaiseLibraryChanged();
			return true;
		}
		private bool RendererPathClaimedLocked(GameProfile self, string resolved)
		{
			foreach (GameProfile profile in profiles)
			{
				if (!object.ReferenceEquals(profile, self))
				{
					if (string.Equals(profile.ExecutablePath, resolved, StringComparison.OrdinalIgnoreCase))
					{
						return true;
					}
					if (string.Equals(profile.LearnedExecutablePath, resolved, StringComparison.OrdinalIgnoreCase))
					{
						return true;
					}
				}
			}
			return false;
		}
		public void KickGameDetectionNow()
		{
			RequestFullGameDetection();
			try
			{
				kick.Set();
			}
			catch
			{
			}
		}
		private bool ShouldRunFullGameDetection()
		{
			long ticks = DateTime.UtcNow.Ticks;
			long lastFullGameDetectionTicksSnapshot = Interlocked.Read(ref lastFullGameDetectionTicks);
			int shouldRunFullGameDetectionValue = (ProcessEventsAvailable ? 20000 : 4000);
			if (armedAwaitingElection)
			{
				shouldRunFullGameDetectionValue = 4000;
			}
			bool anyConditionMet = lastFullGameDetectionTicksSnapshot <= 0 || ticks < lastFullGameDetectionTicksSnapshot || ticks - lastFullGameDetectionTicksSnapshot >= (long)shouldRunFullGameDetectionValue * 10000L;
			if (Interlocked.Exchange(ref gameDetectionDirty, 0) != 0 || anyConditionMet)
			{
				Interlocked.Exchange(ref lastFullGameDetectionTicks, ticks);
				return true;
			}
			return false;
		}
		private void RequestFullGameDetection()
		{
			Interlocked.Exchange(ref gameDetectionDirty, 1);
			Interlocked.Exchange(ref urgentProcessScan, 1);
			Interlocked.Exchange(ref processSetDirty, 1);
		}
		private static long RendererNowMs()
		{
			long timestamp = Stopwatch.GetTimestamp();
			long frequency = Stopwatch.Frequency;
			return timestamp / frequency * 1000 + timestamp % frequency * 1000 / frequency;
		}
		private int RendererForegroundPid()
		{
			return GameSessionDetector.ForegroundPid();
		}
		private void ObserveRendererForegroundChange()
		{
			int rendererForegroundPidResult = RendererForegroundPid();
			if (rendererForegroundPidResult != rendererObservedForeground)
			{
				rendererObservedForeground = rendererForegroundPidResult;
				rendererForegroundSinceMs = RendererNowMs();
				Interlocked.Exchange(ref gameDetectionDirty, 1);
			}
		}
		private long RendererForegroundHeldMs(int pid)
		{
			if (pid <= 0 || pid != rendererObservedForeground || RendererForegroundPid() != pid)
			{
				return 0L;
			}
			long remainingRendererNowMs = RendererNowMs() - rendererForegroundSinceMs;
			if (remainingRendererNowMs >= 0)
			{
				return remainingRendererNowMs;
			}
			return 0L;
		}
		private void InvalidateRendererHandoff()
		{
			lock (sync)
			{
				Interlocked.Increment(ref rendererHandoffEpoch);
				RendererHandoffTracker rendererHandoffTracker = rendererHandoff;
				if (rendererHandoffTracker != null)
				{
					rendererHandoffTracker.Clear();
				}
				rendererDirectPending = null;
			}
		}
		private bool RendererEpochCurrent(int epoch)
		{
			if (epoch == Volatile.Read(ref rendererHandoffEpoch) && enabled && !stopping && !panicReq)
			{
				return !ProfileStoreSaveFailed;
			}
			return false;
		}
		private bool VerifyRendererCandidate(GameDetection candidate)
		{
			if (candidate == null || candidate.RendererPid <= 0 || candidate.RendererCreation <= 0)
			{
				return false;
			}
			GameProcessSnapshot identity;
			if (GameSessionDetector.TryCaptureProcessIdentity(candidate.RendererPid, selfSession, out identity) && identity.Creation == candidate.RendererCreation && string.Equals(identity.Name, candidate.RendererName, StringComparison.OrdinalIgnoreCase))
			{
				return string.Equals(identity.Path, candidate.RendererPath, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}
		private static GameProfile RendererProfile(IList<GameProfile> library, string id)
		{
			if (library == null || string.IsNullOrEmpty(id))
			{
				return null;
			}
			foreach (GameProfile item in library)
			{
				if (item != null && string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
				{
					return item;
				}
			}
			return null;
		}
		private GameDetection CaptureRendererChallenger(ProcessSnapshot all, IList<GameProfile> library, GameDetection incumbent)
		{
			return GameSessionDetector.CaptureForegroundCandidate(all, selfSession, library, incumbent, FamilyEvidence);
		}
		private BackgroundReleaseState ReleaseRendererBackground(GameDetection candidate)
		{
			BackgroundReleaseState backgroundReleaseState = core.ReleaseBackgroundForRenderer(candidate.RendererPid, candidate.RendererCreation, candidate.RendererName);
			if (backgroundReleaseState == BackgroundReleaseState.IdentityMismatch && VerifyRendererCandidate(candidate) && core.DiscardReusedRendererTracking(candidate.RendererPid, candidate.RendererCreation, candidate.RendererName))
			{
				backgroundReleaseState = core.ReleaseBackgroundForRenderer(candidate.RendererPid, candidate.RendererCreation, candidate.RendererName);
			}
			if (backgroundReleaseState == BackgroundReleaseState.Ready || backgroundReleaseState == BackgroundReleaseState.Gone)
			{
				ReportUntrack(candidate.RendererPid);
			}
			return backgroundReleaseState;
		}
		private GameDetection ResolveRendererHandoff(ProcessSnapshot all, IList<GameProfile> library, GameDetection raw, out int epoch)
		{
			epoch = Volatile.Read(ref rendererHandoffEpoch);
			if (!enabled || stopping || panicReq || ProfileStoreSaveFailed)
			{
				InvalidateRendererHandoff();
				return null;
			}
			long nowMs = RendererNowMs();
			GameDetection gameDetection = stickyDetection;
			if (gameDetection != null && gameDetection.Profile != null && !RendererHandoffTracker.SameProfile(gameDetection.Profile, RendererProfile(library, gameDetection.Profile.Id)))
			{
				ClearSticky();
				InvalidateRendererHandoff();
				gameDetection = null;
				epoch = Volatile.Read(ref rendererHandoffEpoch);
			}
			GameDetection current = Handoff.Current;
			if (current != null && (current.Profile == null || !RendererHandoffTracker.SameProfile(current.Profile, RendererProfile(library, current.Profile.Id))))
			{
				Handoff.Clear();
			}
			GameDetection gameDetection2 = CaptureRendererChallenger(all, library, gameDetection);
			GameDetection gameDetection3 = gameDetection2;
			if (gameDetection3 == null && raw != null && !raw.RendererForeground && raw.RendererCandidateSelected && !raw.RequiresGpuConfirm && !RendererHandoffTracker.SameIdentity(raw, gameDetection))
			{
				gameDetection3 = raw;
			}
			lock (sync)
			{
				if (!RendererEpochCurrent(epoch))
				{
					return null;
				}
				if (gameDetection3 != null && gameDetection3.Profile != null && RendererHandoffTracker.SameProfile(gameDetection3.Profile, FindProfileLocked(gameDetection3.Profile.Id)))
				{
					Handoff.Offer(gameDetection3, nowMs);
				}
			}
			current = Handoff.Current;
			if (current != null)
			{
				bool identityValid = RendererHandoffTracker.SameProfile(current.Profile, RendererProfile(library, current.Profile.Id)) && VerifyRendererCandidate(current);
				Handoff.Refresh(identityValid, RendererForegroundPid() == current.RendererPid, nowMs);
				current = Handoff.Current;
				if (current != null)
				{
					BackgroundReleaseState backgroundReleaseState = ReleaseRendererBackground(current);
					if ((backgroundReleaseState == BackgroundReleaseState.Gone || backgroundReleaseState == BackgroundReleaseState.IdentityMismatch) && !VerifyRendererCandidate(current))
					{
						Handoff.Clear();
					}
					else
					{
						Handoff.Recovery(current, backgroundReleaseState == BackgroundReleaseState.Ready);
						TryStartRendererProbe(epoch);
						if (Handoff.TakeUncertainNotice(nowMs))
						{
							Logger.Log(Lang.T("log.renderer.unconfirmed") + current.RendererName);
						}
					}
				}
			}
			GameDetection gameDetection4 = Handoff.Confirmed(RendererNowMs());
			if (gameDetection4 != null && VerifyRendererCandidate(gameDetection4) && (!gameDetection4.RendererForeground || RendererForegroundPid() == gameDetection4.RendererPid) && RendererEpochCurrent(epoch))
			{
				return gameDetection4;
			}
			GameDetection gameDetection5 = ((raw != null && raw.RendererCandidateSelected && !raw.RendererForeground && !raw.RequiresGpuConfirm && !raw.RendererSafetyOnly) ? raw : rendererDirectPending);
			if (gameDetection5 != null && !RendererHandoffTracker.SameIdentity(gameDetection5, gameDetection))
			{
				if (gameDetection5.Profile != null && RendererHandoffTracker.SameProfile(gameDetection5.Profile, RendererProfile(library, gameDetection5.Profile.Id)) && VerifyRendererCandidate(gameDetection5) && (!gameDetection5.RendererForeground || RendererForegroundPid() == gameDetection5.RendererPid))
				{
					if (ReleaseRendererBackground(gameDetection5) == BackgroundReleaseState.Ready && RendererEpochCurrent(epoch))
					{
						rendererDirectPending = null;
						return gameDetection5;
					}
					lock (sync)
					{
						if (!RendererEpochCurrent(epoch))
						{
							return null;
						}
						rendererDirectPending = (VerifyRendererCandidate(gameDetection5) ? RendererHandoffTracker.Copy(gameDetection5) : null);
					}
				}
				else
				{
					rendererDirectPending = null;
				}
			}
			else
			{
				rendererDirectPending = null;
			}
			if (!RendererEpochCurrent(epoch))
			{
				return null;
			}
			if (raw == null || !raw.RendererCandidateSelected || raw.RequiresGpuConfirm || raw.RendererSafetyOnly || !RendererHandoffTracker.SameIdentity(raw, gameDetection))
			{
				return null;
			}
			return raw;
		}
		private void TryStartRendererProbe(int epoch)
		{
			if (Interlocked.CompareExchange(ref rendererGpuSamplingBusy, 1, 0) != 0)
			{
				return;
			}
			RendererProbeTicket ticket;
			lock (sync)
			{
				if (!RendererEpochCurrent(epoch))
				{
					Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
					return;
				}
				GameDetection current = Handoff.Current;
				if (current != null)
				{
					Handoff.Refresh(true, RendererForegroundPid() == current.RendererPid, RendererNowMs());
				}
				ticket = Handoff.BeginProbe(RendererNowMs());
			}
			if (ticket == null)
			{
				Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
				return;
			}
			if (!rendererProbeFailureBudget.Allowed(ticket.Detection.RendererPid, ticket.Detection.RendererCreation, ticket.Detection.RendererPath, "probe"))
			{
				Handoff.Complete(ticket, null, false, RendererNowMs());
				Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
				return;
			}
			try
			{
				if (!ThreadPool.QueueUserWorkItem(delegate
				{
					IDictionary<int, double> utilization = null;
					PresentActivitySample presents = null;
					bool identityValid = false;
					bool sampled = false;
					Func<bool> func = () => ticket.Canceled || !RendererEpochCurrent(epoch);
					try
					{
						if (!func() && VerifyRendererCandidate(ticket.Detection))
						{
							sampled = true;
							// Frame submissions and GPU utilization are observed over the same window.
							string presentFailure;
							PresentActivityCapture capture = TryStartRendererPresent(rendererProbeFailureBudget, ticket.Detection, out presentFailure);
							Stopwatch presentWindow = capture != null ? Stopwatch.StartNew() : null;
							try
							{
								utilization = SampleRendererGpu(rendererProbeFailureBudget, ticket.Detection, func,
									() => GpuEvidence.Sample3D(2, 700, func));
							}
							finally
							{
								if (presentWindow != null) WaitRendererPresentWindow(presentWindow, func);
								presents = FinishRendererPresent(rendererProbeFailureBudget, ticket.Detection, capture, presentFailure);
							}
							identityValid = !func() && VerifyRendererCandidate(ticket.Detection) && RendererForegroundPid() == ticket.Detection.RendererPid;
						}
					}
					catch
					{
					}
					finally
					{
						if (sampled && !func())
						{
							if (utilization == null && (presents == null || !presents.Valid)) RecordRendererProbeFailure(ticket, "capture");
							else rendererProbeFailureBudget.Succeeded(ticket.Detection.RendererPid, ticket.Detection.RendererCreation, ticket.Detection.RendererPath, "probe");
						}
						RendererProof verdict = Handoff.Complete(ticket, utilization, presents, identityValid, RendererNowMs());
						if (sampled) LogRendererProbe(ticket.Detection, verdict, utilization, presents);
						Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
						if (!stopping && enabled)
						{
							Interlocked.Exchange(ref urgentProcessScan, 1);
							Interlocked.Exchange(ref processSetDirty, 1);
							try
							{
								kick.Set();
							}
							catch
							{
							}
						}
					}
				}))
				{
					Handoff.Complete(ticket, null, false, RendererNowMs());
					RecordRendererProbeFailure(ticket, "queue");
					Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
				}
			}
			catch
			{
				Handoff.Complete(ticket, null, false, RendererNowMs());
				RecordRendererProbeFailure(ticket, "queue-exception");
				Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
			}
		}
		internal static PresentActivityCapture TryStartRendererPresent(ProcessActionFailureBudget budget, GameDetection candidate, out string failure)
		{
			if (!budget.Allowed(candidate.RendererPid, candidate.RendererCreation, candidate.RendererPath, "present"))
			{
				failure = "budget-exhausted";
				return null;
			}
			return PresentActivityCapture.TryStart(out failure);
		}
		internal static void WaitRendererPresentWindow(Stopwatch observed, Func<bool> canceled)
		{
			const int windowMs = GpuEvidence.BurstRounds * GpuEvidence.BurstIntervalMs;
			while (!canceled())
			{
				long remaining = windowMs - observed.ElapsedMilliseconds;
				if (remaining <= 0) return;
				Thread.Sleep((int)Math.Min(50, remaining));
			}
		}
		private static void LogRendererProbe(GameDetection candidate, RendererProof verdict, IDictionary<int, double> utilization, PresentActivitySample presents)
		{
			if (candidate == null) return;
			int bestPid = 0, bestFrames = 0;
			if (presents != null && presents.Valid)
				foreach (int pid in candidate.FamilyPids)
					if (pid != candidate.RendererPid && presents.FramesOf(pid) > bestFrames) { bestFrames = presents.FramesOf(pid); bestPid = pid; }
			double gpu;
			string gpuText = utilization != null && utilization.TryGetValue(candidate.RendererPid, out gpu) && !double.IsNaN(gpu) ? ((int)gpu).ToString() + "%" : "n/a";
			string presentText = presents == null ? "none" : presents.Valid
				? "frames=" + presents.FramesOf(candidate.RendererPid) + " fps=" + ((int)Math.Round(presents.FpsOf(candidate.RendererPid))) + " windowMs=" + presents.WindowMs
					+ " familyBest=" + (bestPid > 0 ? bestPid + ":" + bestFrames : "none") + " presenters=" + presents.ProcessCount
				: "unavailable=" + presents.Failure;
			Logger.Info("RENDERER probe " + candidate.RendererName + " pid=" + candidate.RendererPid + " verdict=" + verdict.ToString().ToLowerInvariant()
				+ " gpu3d=" + gpuText + " " + presentText);
		}
		private void RecordRendererProbeFailure(RendererProbeTicket ticket, string stage)
		{
			if (ticket.Canceled) return;
			GameDetection target = ticket.Detection;
			int count = rendererProbeFailureBudget.Failed(target.RendererPid, target.RendererCreation, target.RendererPath, "probe");
			Logger.Warn("Renderer GPU probe failure=" + count + "/3 stage=" + stage + " pid=" + target.RendererPid);
		}
		private bool FinalizeRendererSelection(GameDetection selected, int expectedEpoch)
		{
			if (selected == null || selected.Profile == null || !selected.RendererCandidateSelected || selected.RequiresGpuConfirm || selected.RendererSafetyOnly || stopping || !enabled || panicReq || ProfileStoreSaveFailed || expectedEpoch != Volatile.Read(ref rendererHandoffEpoch) || !VerifyRendererCandidate(selected) || !RendererCommitEvidenceCurrent(selected, expectedEpoch))
			{
				return false;
			}
			GameProfile gameProfile2;
			lock (sync)
			{
				GameProfile gameProfile = FindProfileLocked(selected.Profile.Id);
				gameProfile2 = ((gameProfile == null) ? null : gameProfile.Clone());
			}
			if (!RendererHandoffTracker.SameProfile(selected.Profile, gameProfile2))
			{
				return false;
			}
			GameDetection current = Handoff.Current;
			bool allConditionsMet = current != null && !RendererHandoffTracker.SameIdentity(current, selected);
			bool valuesEqual = string.Equals(gameProfile2.LearnedExecutablePath, selected.RendererPath, StringComparison.OrdinalIgnoreCase);
			bool valuesEqual2 = string.Equals(gameProfile2.ExecutablePath, selected.RendererPath, StringComparison.OrdinalIgnoreCase);
			if (!gameProfile2.ForceTrigger && !allConditionsMet && (selected.RendererLearnable || valuesEqual || (valuesEqual2 && !string.IsNullOrEmpty(gameProfile2.LearnedExecutablePath))))
			{
				GameDetection gameDetection = RendererHandoffTracker.Copy(selected);
				gameDetection.RendererLearnable = true;
				if (!TryLearnConfirmedRenderer(gameDetection, () => RendererCommitEvidenceCurrent(selected, expectedEpoch)))
				{
					return false;
				}
				lock (sync)
				{
					GameProfile gameProfile3 = FindProfileLocked(selected.Profile.Id);
					gameProfile2 = ((gameProfile3 == null) ? null : gameProfile3.Clone());
				}
				if (gameProfile2 == null)
				{
					return false;
				}
				selected.RendererLearnable = false;
				selected.RendererUserSelected = true;
			}
			selected.Profile = gameProfile2;
			return RendererCommitEvidenceCurrent(selected, expectedEpoch);
		}
		private bool RendererCommitEvidenceCurrent(GameDetection selected, int epoch)
		{
			if (!RendererEpochCurrent(epoch) || !VerifyRendererCandidate(selected))
			{
				return false;
			}
			lock (sync)
			{
				if (selected.Profile == null || !RendererHandoffTracker.SameProfile(selected.Profile, FindProfileLocked(selected.Profile.Id)))
				{
					return false;
				}
			}
			GameDetection gameDetection = stickyDetection;
			if (RendererHandoffTracker.SameIdentity(gameDetection, selected))
			{
				return RendererEpochCurrent(epoch);
			}
			if (selected.RendererGpuProofExpiresMs > 0)
			{
				long rendererNowMsResult = RendererNowMs();
				GameDetection a = Handoff.Confirmed(rendererNowMsResult);
				if (rendererNowMsResult > selected.RendererGpuProofExpiresMs || !RendererHandoffTracker.SameIdentity(a, selected))
				{
					return false;
				}
			}
			if (selected.RendererForeground && RendererForegroundPid() != selected.RendererPid)
			{
				return false;
			}
			if (gameDetection != null && gameDetection.Profile != null && selected.Profile != null && string.Equals(gameDetection.Profile.Id, selected.Profile.Id, StringComparison.OrdinalIgnoreCase) && VerifyRendererCandidate(gameDetection) && !FreshRendererMayReplaceSticky(selected, selected.RendererName, selected.RendererCreation, gameDetection.RendererCreation, RendererForegroundHeldMs(selected.RendererPid)))
			{
				return false;
			}
			return RendererEpochCurrent(epoch);
		}
		private void CompleteRendererSelection(GameDetection selected)
		{
			GameDetection current = Handoff.Current;
			if (RendererHandoffTracker.SameIdentity(current, selected))
			{
				if (current.RequiresGpuConfirm)
				{
					Logger.Log(Lang.T("log.renderer.confirmed") + selected.Profile.Name + " / " + selected.RendererName + " pid " + selected.RendererPid + " / " + selected.Evidence);
				}
				Handoff.Clear();
			}
			if (RendererHandoffTracker.SameIdentity(rendererDirectPending, selected))
			{
				rendererDirectPending = null;
			}
		}
		private bool IsRendererHandoffProtected(int pid, long creation, string path)
		{
			if (!enabled || stopping || panicReq || ProfileStoreSaveFailed)
			{
				return false;
			}
			RendererHandoffTracker rendererHandoffTracker = rendererHandoff;
			if (rendererHandoffTracker != null && rendererHandoffTracker.Protects(pid, creation, path, RendererNowMs()))
			{
				return true;
			}
			GameDetection gameDetection = rendererDirectPending;
			if (gameDetection != null && pid == gameDetection.RendererPid && creation > 0 && creation == gameDetection.RendererCreation)
			{
				return string.Equals(path, gameDetection.RendererPath, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}
		private void InitializeRendererObservations()
		{
			rendererObservations = new RendererObservationStore(dataDir);
		}
		public bool HasRendererObservation(GameProfile profile)
		{
			RendererObservationStore rendererObservationStore = rendererObservations;
			if (rendererObservationStore == null || profile == null)
			{
				return false;
			}
			if (!stopping && rendererObservationStore.NeedsValidation(profile.Id, profile.ExecutablePath, DateTime.UtcNow.Ticks))
			{
				QueueRendererObservationValidation();
			}
			return rendererObservationStore.Has(profile.Id, profile.ExecutablePath);
		}
		private void QueueRendererObservationValidation()
		{
			if (stopping || Interlocked.CompareExchange(ref rendererValidationBusy, 1, 0) != 0)
			{
				return;
			}
			try
			{
				if (!ThreadPool.QueueUserWorkItem(delegate
				{
					bool ready = false;
					try
					{
						if (stopping)
						{
							return;
						}
						foreach (GameProfile profile in GetProfiles())
						{
							if (stopping)
							{
								break;
							}
							if (rendererObservations.NeedsValidation(profile.Id, profile.ExecutablePath, DateTime.UtcNow.Ticks))
							{
								ready |= rendererObservations.Validate(profile.Id, profile.ExecutablePath);
							}
						}
					}
					finally
					{
						try
						{
							if (ready && !stopping)
							{
								rendererObservations.Persist();
								RaiseLibraryChanged();
							}
						}
						finally
						{
							Interlocked.Exchange(ref rendererValidationBusy, 0);
						}
					}
				}))
				{
					Interlocked.Exchange(ref rendererValidationBusy, 0);
				}
			}
			catch
			{
				Interlocked.Exchange(ref rendererValidationBusy, 0);
			}
		}
		private void ForgetRendererObservation(string profileId)
		{
			if (stopping || rendererObservations == null)
			{
				return;
			}
			rendererObservations.Forget(profileId);
			try
			{
				ThreadPool.QueueUserWorkItem(delegate
				{
					if (!stopping)
					{
						rendererObservations.Persist();
					}
				});
			}
			catch
			{
			}
		}
		private void MaybeObserveRendererActivity(GameDetection selected)
		{
			if (rendererObservations == null || selected == null || selected.Profile == null || !selected.RendererCandidateSelected || selected.RendererSafetyOnly || selected.RequiresGpuConfirm || !enabled || stopping || panicReq || ProfileStoreSaveFailed)
			{
				return;
			}
			int rendererForegroundPidResult = RendererForegroundPid();
			if ((rendererForegroundPidResult == selected.RendererPid) ? HasRendererObservation(selected.Profile) : rendererObservations.Has(selected.Profile.Id, selected.Profile.ExecutablePath))
			{
				return;
			}
			long rendererNowMsResult = RendererNowMs();
			int epoch = Volatile.Read(ref rendererHandoffEpoch);
			if (!RendererHandoffTracker.SameIdentity(rendererActivityTarget, selected) || rendererActivityTarget.Profile == null || !string.Equals(rendererActivityTarget.Profile.Id, selected.Profile.Id, StringComparison.OrdinalIgnoreCase) || rendererActivityEpoch != epoch)
			{
				rendererActivityTarget = RendererHandoffTracker.Copy(selected);
				rendererActivityAttempts = 0;
				rendererActivityNextMs = 0L;
				rendererActivityEpoch = epoch;
			}
			if (rendererActivityAttempts >= 5)
			{
				return;
			}
			if (rendererForegroundPidResult != selected.RendererPid)
			{
				TraceRendererObservationWait(selected, rendererNowMsResult, "ForegroundWaiting", "foreground=" + rendererForegroundPidResult);
			}
			else
			{
				if (rendererNowMsResult < rendererActivityNextMs || rendererNowMsResult < rendererActivityBudgetNextMs)
				{
					return;
				}
				if (Handoff.HasProbe)
				{
					TraceRendererObservationWait(selected, rendererNowMsResult, "SamplingBusy", "owner=handoff");
				}
				else
				{
					if (Interlocked.CompareExchange(ref rendererActivityBusy, 1, 0) != 0)
					{
						return;
					}
					if (Interlocked.CompareExchange(ref rendererGpuSamplingBusy, 1, 0) == 0)
					{
						rendererActivityAttempts++;
						rendererActivityBudgetNextMs = rendererNowMsResult + 10000;
						rendererActivityNextMs = rendererNowMsResult + ((rendererActivityAttempts <= 2) ? 10000 : ((rendererActivityAttempts <= 3) ? 30000 : ((rendererActivityAttempts <= 4) ? 60000 : 120000)));
						GameDetection target = RendererHandoffTracker.Copy(selected);
						TraceRendererObservation(target, "Started", "attempt=" + rendererActivityAttempts + " retryMs=" + (rendererActivityNextMs - rendererNowMsResult) + " exe=" + target.RendererPath);
						try
						{
							if (!ThreadPool.QueueUserWorkItem(delegate
							{
								string reason = "Canceled";
								string message = null;
								try
								{
									Func<bool> func = () => !RendererEpochCurrent(epoch);
									if (!func())
									{
										if (!VerifyRendererCandidate(target))
										{
											reason = "IdentityUnavailable";
											message = "phase=before";
										}
										else
										{
											RendererFileStamp rendererFileStamp = RendererFileStamp.Read(target.RendererPath);
											if (rendererFileStamp == null)
											{
												reason = "FileUnavailable";
											}
											else
											{
												GpuSampleDiagnostics diagnostics = null;
												IDictionary<int, double> dictionary = GpuEvidence.Sample3D(2, 700, func, target.RendererPid, out diagnostics);
												message = ((diagnostics == null) ? null : diagnostics.ToString());
												if (!func())
												{
													if (dictionary == null)
													{
														reason = "GpuUnavailable";
													}
													else
													{
														message = "pidCount=" + dictionary.Count + " " + message;
														double value;
														if (!dictionary.TryGetValue(target.RendererPid, out value))
														{
															reason = "PidMissing";
														}
														else
														{
															message = "gpu3d=" + value.ToString("R", CultureInfo.InvariantCulture) + "% " + message;
															int sampledForegroundPid = RendererForegroundPid();
															if (sampledForegroundPid != target.RendererPid)
															{
																reason = "ForegroundChanged";
																message = message + " foreground=" + sampledForegroundPid;
															}
															else if (!VerifyRendererCandidate(target))
															{
																reason = "IdentityUnavailable";
																message += " phase=after";
															}
															else if (double.IsNaN(value) || double.IsInfinity(value))
															{
																reason = "InvalidEvidence";
															}
															else if (value < 10.0)
															{
																reason = "BelowThreshold";
																message = message + " minimum=" + 10.0.ToString(CultureInfo.InvariantCulture) + "%";
															}
															else
															{
																RendererObservationWriteResult rendererObservationWriteResult;
																lock (sync)
																{
																	GameProfile gameProfile = FindProfileLocked(target.Profile.Id);
																	if (!RendererEpochCurrent(epoch))
																	{
																		return;
																	}
																	if (gameProfile == null || !string.Equals(gameProfile.ExecutablePath, target.RendererPath, StringComparison.OrdinalIgnoreCase))
																	{
																		reason = "ProfileChanged";
																		return;
																	}
																	if (activeDetection == null || !RendererHandoffTracker.SameIdentity(activeDetection, target))
																	{
																		reason = "ActiveChanged";
																		return;
																	}
																	if (!VerifyRendererCandidate(target))
																	{
																		reason = "IdentityUnavailable";
																		message += " phase=commit";
																		return;
																	}
																	rendererObservationWriteResult = rendererObservations.RecordActivityWithResult(gameProfile.Id, target.RendererPath, RendererObservationEvidence.Gpu3D, value, rendererFileStamp);
																}
																reason = rendererObservationWriteResult.ToString();
																if (rendererObservationWriteResult == RendererObservationWriteResult.Recorded)
																{
																	RaiseLibraryChanged();
																}
															}
														}
													}
												}
											}
										}
									}
								}
								catch (Exception ex2)
								{
									reason = "Error";
									message = ex2.GetType().Name;
								}
								finally
								{
									TraceRendererObservation(target, reason, message);
									Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
									Interlocked.Exchange(ref rendererActivityBusy, 0);
								}
							}))
							{
								TraceRendererObservation(target, "QueueFailed", null);
								Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
								Interlocked.Exchange(ref rendererActivityBusy, 0);
							}
							return;
						}
						catch (Exception ex)
						{
							TraceRendererObservation(target, "QueueFailed", ex.GetType().Name);
							Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
							Interlocked.Exchange(ref rendererActivityBusy, 0);
							return;
						}
					}
					Interlocked.Exchange(ref rendererActivityBusy, 0);
					TraceRendererObservationWait(selected, rendererNowMsResult, "SamplingBusy", "owner=gpu");
				}
			}
		}
		private void TraceRendererObservationWait(GameDetection target, long now, string reason, string detail)
		{
			if (now >= rendererActivityWaitLogMs)
			{
				rendererActivityWaitLogMs = now + 30000;
				TraceRendererObservation(target, reason, detail);
			}
		}
		private void TraceRendererObservation(GameDetection target, string reason, string detail)
		{
			try
			{
				string item = SeverityTagFor(reason) + Lang.F("lib.render.observation.trace", target.RendererName, target.RendererPid, Lang.T("lib.render.observation." + reason)) + " [reason=" + reason + "] profile=" + target.Profile.Id + (string.IsNullOrEmpty(detail) ? "" : (" " + detail));
				lock (rendererObservationTraceGate)
				{
					if (rendererObservationTraceLines.Count >= 8)
					{
						rendererObservationTraceLines.Dequeue();
					}
					rendererObservationTraceLines.Enqueue(item);
					if (rendererObservationTraceBusy != 0)
					{
						return;
					}
					rendererObservationTraceBusy = 1;
					try
					{
						if (ThreadPool.QueueUserWorkItem(delegate
						{
							DrainRendererObservationTrace();
						}))
						{
							return;
						}
					}
					catch
					{
					}
					rendererObservationTraceBusy = 0;
					rendererObservationTraceLines.Clear();
				}
			}
			catch
			{
			}
		}
		private void DrainRendererObservationTrace()
		{
			while (true)
			{
				string msg;
				lock (rendererObservationTraceGate)
				{
					if (rendererObservationTraceLines.Count == 0)
					{
						rendererObservationTraceBusy = 0;
						break;
					}
					msg = rendererObservationTraceLines.Dequeue();
				}
				Logger.Log(msg);
			}
		}
		private void SetActiveDetectionLocked(GameDetection detection)
		{
			string previousProfileId = activeDetection == null || activeDetection.Profile == null ? null : activeDetection.Profile.Id;
			string nextProfileId = detection == null || detection.Profile == null ? null : detection.Profile.Id;
			bool samePowerProfile = string.Equals(previousProfileId, nextProfileId, StringComparison.OrdinalIgnoreCase);
			bool samePowerIdentity = object.ReferenceEquals(activeDetection, detection)
				|| (activeDetection != null && detection != null && activeDetection.RendererPid == detection.RendererPid
					&& activeDetection.RendererCreation == detection.RendererCreation
					&& string.Equals(activeDetection.RendererPath, detection.RendererPath, StringComparison.OrdinalIgnoreCase)
					&& samePowerProfile);
			if (!samePowerIdentity)
			{
				if (!samePowerProfile)
				{
					powerSessionEnding = true;
				}
				InvalidatePowerPlanSession();
			}
			if (!SameCriticalThreadDetection(activeDetection, detection))
			{
				RevokeCriticalThreadIntent("SetActiveDetectionLocked");
			}
			if (activeDetection == null)
			{
				gameSessionStarts++;
			}
			activeDetection = detection;
		}
		private string FindRunningGame(ProcessSnapshot all, out HashSet<int> gamePids)
		{
			gamePids = new HashSet<int>();
			List<GameProfile> list;
			lock (sync)
			{
				list = new List<GameProfile>();
				foreach (GameProfile profile in profiles)
				{
					list.Add(profile.Clone());
				}
				CaptureGameFamily(all, list);
			}
			ObserveRendererForegroundChange();
			string armedProfile = null;
			string armedVia = null;
			GameDetection raw = null;
			bool shouldRunFullGameDetection = ShouldRunFullGameDetection();
			if (shouldRunFullGameDetection)
			{
				raw = GameSessionDetector.Detect(all, list, selfSession, out armedProfile, out armedVia, FamilyEvidence);
			}
			int epoch;
			GameDetection gameDetection = ResolveRendererHandoff(all, list, raw, out epoch);
			if (gameDetection != null && !FinalizeRendererSelection(gameDetection, epoch))
			{
				gameDetection = null;
			}
			if (epoch != Volatile.Read(ref rendererHandoffEpoch) || stopping || !enabled || panicReq || ProfileStoreSaveFailed)
			{
				return null;
			}
			GameDetection gameDetection2 = ApplyStickiness(gameDetection);
			if (gameDetection2 != null && !stickyGraceOnly && !VerifyRendererCandidate(gameDetection2))
			{
				ClearSticky();
				RequestFullGameDetection();
				return null;
			}
			if (gameDetection != null && RendererHandoffTracker.SameIdentity(gameDetection, gameDetection2))
			{
				CompleteRendererSelection(gameDetection2);
			}
			if (shouldRunFullGameDetection)
			{
				armedAwaitingElection = armedProfile != null && gameDetection2 == null;
				UpdateArmedStatus((gameDetection2 == null) ? armedProfile : null, armedVia, gameDetection2 != null);
				if (gameDetection2 == null && armedProfile != null)
				{
					PreStageDriverTuning(list, armedProfile);
				}
			}
			else if (gameDetection2 != null && armedAwaitingElection)
			{
				armedAwaitingElection = false;
				UpdateArmedStatus(null, null, true);
			}
			if (gameDetection2 == null)
			{
				return null;
			}
			if (stickyGraceOnly)
			{
				return null;
			}
			foreach (int familyPid in gameDetection2.FamilyPids)
			{
				gamePids.Add(familyPid);
			}
			lock (sync)
			{
				if (!RendererEpochCurrent(epoch) || gameDetection2.Profile == null || !RendererHandoffTracker.SameProfile(gameDetection2.Profile, FindProfileLocked(gameDetection2.Profile.Id)))
				{
					return null;
				}
				if (ShouldRearmLauncherTransition(activeDetection, gameDetection2))
				{
					transitionProbeRendererPid = 0;
					transitionProbeRendererCreation = 0L;
				}
				SetActiveDetectionLocked(gameDetection2);
			}
			SyncAutomaticFullscreenOptimizations(true, -1L);
			NotifyExtensionSession(true);
			MaybeObserveRendererActivity(gameDetection2);
			return gameDetection2.Profile.Name;
		}
		internal static bool FreshRendererMayReplaceSticky(GameDetection fresh, string verifiedName, long verifiedCreation, long stickyCreation, long foregroundHeldMs)
		{
			if (fresh == null || fresh.RendererPid <= 0 || string.IsNullOrEmpty(fresh.RendererName) || string.IsNullOrEmpty(verifiedName) || verifiedCreation <= 0 || stickyCreation <= 0 || (fresh.RendererCreation > 0 && fresh.RendererCreation != verifiedCreation) || !string.Equals(verifiedName, fresh.RendererName, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			if (verifiedCreation > stickyCreation)
			{
				return true;
			}
			if (fresh.RendererForeground)
			{
				if (fresh.RendererGpuProofExpiresMs <= 0)
				{
					return foregroundHeldMs >= 10000;
				}
				return true;
			}
			return false;
		}
	}
}
