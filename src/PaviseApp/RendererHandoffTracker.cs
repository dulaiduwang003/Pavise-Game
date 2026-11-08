using System;
using System.Collections.Generic;

namespace PaviseApp
{
	internal enum RendererProof
	{
		None,
		Gpu,
		Present,
		Rejected
	}

	internal sealed class RendererHandoffTracker
	{
		private sealed class Candidate
		{
			public GameDetection Detection;

			public long Generation;

			public long FirstSeenMs;

			public long LastForegroundMs;

			public bool Foreground;

			public bool AllowBackground;

			public bool RecoveryReady;

			public int Attempts;

			public long NextAttemptMs;

			public bool Verified;

			public double Utilization;

			public double PresentFps;

			public RendererProof Proof;

			public long ProofMs;

			public bool UncertainLogged;
		}

		internal const int ProbeWindowMs = 10000;

		internal const int ProbeCooldownMs = 8000;

		internal const int MaxProbeAttempts = 2;

		internal const int UnresolvedProbeCooldownMs = 30000;

		internal const int MaxUnresolvedProbeCooldownMs = 120000;

		internal const int ForegroundGraceMs = 1500;

		internal const int ProofLifetimeMs = 3000;

		internal const int ProbeResultMaxAgeMs = 5000;

		private readonly object gate = new object();

		private Candidate current;

		private RendererProbeTicket inFlight;

		private long generation;

		private long nextProbeMs;

		internal GameDetection Current
		{
			get
			{
				lock (gate)
				{
					return (current == null) ? null : Copy(current.Detection);
				}
			}
		}

		internal bool HasCandidate
		{
			get
			{
				lock (gate)
				{
					return current != null;
				}
			}
		}

		internal bool HasProbe
		{
			get
			{
				lock (gate)
				{
					return inFlight != null;
				}
			}
		}

		internal int Attempts
		{
			get
			{
				lock (gate)
				{
					return (current != null) ? current.Attempts : 0;
				}
			}
		}

		internal void Offer(GameDetection detection, long nowMs)
		{
			if (detection == null || detection.Profile == null || detection.RendererPid <= 0 || detection.RendererCreation <= 0 || string.IsNullOrEmpty(detection.RendererPath))
			{
				return;
			}
			lock (gate)
			{
				if (current == null || !SameIdentity(current.Detection, detection) || !SameProfile(current.Detection.Profile, detection.Profile) || current.Detection.RendererSafetyOnly != detection.RendererSafetyOnly)
				{
					CancelLocked();
					current = new Candidate
					{
						Generation = ++generation,
						FirstSeenMs = nowMs,
						LastForegroundMs = nowMs,
						Detection = Copy(detection)
					};
				}
				else
				{
					current.Detection = Copy(detection);
				}
				current.Foreground = detection.RendererForeground;
				if (current.Foreground)
				{
					current.LastForegroundMs = nowMs;
				}
				current.AllowBackground = !detection.RendererForeground && detection.RendererCandidateSelected && detection.RendererUserSelected && !detection.RequiresGpuConfirm && !detection.RendererSafetyOnly;
			}
		}

		internal void Refresh(bool identityValid, bool foreground, long nowMs)
		{
			lock (gate)
			{
				if (current == null)
				{
					return;
				}
				if (!identityValid)
				{
					ClearLocked();
					return;
				}
				ExpireProofLocked(nowMs);
				current.Foreground = foreground;
				if (foreground)
				{
					current.LastForegroundMs = nowMs;
					return;
				}
				current.Verified = false;
				CancelLocked();
				if (!current.AllowBackground && nowMs - current.LastForegroundMs > 1500)
				{
					ClearLocked();
				}
			}
		}

		internal void Recovery(GameDetection identity, bool ready)
		{
			lock (gate)
			{
				if (current != null && SameIdentity(current.Detection, identity))
				{
					current.RecoveryReady = ready;
					if (!ready)
					{
						current.Verified = false;
						CancelLocked();
					}
				}
			}
		}

		internal bool Protects(int pid, long creation, string path, long nowMs)
		{
			lock (gate)
			{
				return current != null && current.Detection.RendererPid == pid && creation > 0 && current.Detection.RendererCreation == creation && SamePath(current.Detection.RendererPath, path) && (current.Foreground || current.AllowBackground || nowMs - current.LastForegroundMs <= 1500);
			}
		}

		internal RendererProbeTicket BeginProbe(long nowMs)
		{
			lock (gate)
			{
				ExpireProofLocked(nowMs);
				if (current == null || inFlight != null || !current.Foreground || !current.RecoveryReady || current.Detection.RendererSafetyOnly || !current.Detection.RequiresGpuConfirm || current.Verified || nowMs < current.NextAttemptMs || nowMs < nextProbeMs)
				{
					return null;
				}
				current.Attempts++;
				nextProbeMs = nowMs + 8000;
				int num = 8000;
				if (current.Attempts >= 2 || nowMs - current.FirstSeenMs >= 10000)
				{
					num = 30000;
					for (int i = 2; i < current.Attempts; i++)
					{
						if (num >= 120000)
						{
							break;
						}
						num = Math.Min(120000, num * 2);
					}
				}
				current.NextAttemptMs = nowMs + num;
				inFlight = new RendererProbeTicket(current.Generation, nowMs, current.Detection);
				return inFlight;
			}
		}

		internal void Complete(RendererProbeTicket ticket, IDictionary<int, double> utilization, bool identityValid, long nowMs)
		{
			Complete(ticket, utilization, null, identityValid, nowMs);
		}

		internal RendererProof Complete(RendererProbeTicket ticket, IDictionary<int, double> utilization, PresentActivitySample presents, bool identityValid, long nowMs)
		{
			lock (gate)
			{
				if (!object.ReferenceEquals(inFlight, ticket))
				{
					return RendererProof.None;
				}
				inFlight = null;
				if (ticket != null && !ticket.Canceled && identityValid && current != null && current.Generation == ticket.Generation && current.Foreground && current.RecoveryReady && nowMs >= ticket.StartedMs && nowMs - ticket.StartedMs <= 5000)
				{
					double gpu, fps;
					RendererProof proof = ResolveEvidence(ticket.Detection, utilization, presents, out gpu, out fps);
					current.Verified = proof == RendererProof.Present || proof == RendererProof.Gpu;
					if (current.Verified)
					{
						current.Proof = proof;
						current.Utilization = gpu;
						current.PresentFps = fps;
						current.ProofMs = nowMs;
					}
					return proof;
				}
				return RendererProof.None;
			}
		}

		internal const int MinPresentFrames = 8;

		// Prefer presentation evidence; use GPU utilization only when frame counts are inconclusive.
		internal static RendererProof ResolveEvidence(GameDetection candidate, IDictionary<int, double> utilization, PresentActivitySample presents, out double gpu, out double fps)
		{
			gpu = 0.0;
			fps = 0.0;
			if (candidate == null || candidate.RendererPid <= 0)
			{
				return RendererProof.None;
			}
			if (presents != null && presents.Valid)
			{
				int own = presents.FramesOf(candidate.RendererPid);
				int other = 0;
				foreach (int familyPid in candidate.FamilyPids)
				{
					if (familyPid != candidate.RendererPid)
					{
						other = Math.Max(other, presents.FramesOf(familyPid));
					}
				}
				// Require a clear frame-count lead; web views can present at the display refresh rate too.
				if (own >= MinPresentFrames && (long)own * 2 >= (long)other * 3)
				{
					fps = presents.FpsOf(candidate.RendererPid);
					double observed;
					if (utilization != null && utilization.TryGetValue(candidate.RendererPid, out observed) && !double.IsNaN(observed) && !double.IsInfinity(observed))
					{
						gpu = observed;
					}
					return RendererProof.Present;
				}
				if (own == 0 && other >= MinPresentFrames)
				{
					// Frames submitted by another game process disqualify a foreground helper or overlay.
					return RendererProof.Rejected;
				}
			}
			return HasGpuEvidence(candidate, utilization, out gpu) ? RendererProof.Gpu : RendererProof.None;
		}

		internal GameDetection Confirmed(long nowMs)
		{
			lock (gate)
			{
				ExpireProofLocked(nowMs);
				if (current == null || !current.RecoveryReady || current.Detection.RendererSafetyOnly || (!current.Foreground && !current.AllowBackground))
				{
					return null;
				}
				bool flag = current.Detection.RendererCandidateSelected && !current.Detection.RequiresGpuConfirm;
				if (!flag && (!current.Verified || nowMs < current.ProofMs || nowMs - current.ProofMs > 3000))
				{
					return null;
				}
				GameDetection gameDetection = Copy(current.Detection);
				gameDetection.RendererForeground = current.Foreground;
				gameDetection.RendererCandidateSelected = true;
				gameDetection.RequiresGpuConfirm = false;
				if (!flag)
				{
					gameDetection.Evidence = current.Proof == RendererProof.Present
						? Lang.F("detect.present", (int)Math.Round(current.PresentFps))
						: Lang.F("detect.gpu", (int)current.Utilization);
					gameDetection.RendererGpuProofExpiresMs = current.ProofMs + 3000;
				}
				return gameDetection;
			}
		}

		internal bool TakeUncertainNotice(long nowMs)
		{
			lock (gate)
			{
				if (current == null || current.UncertainLogged || current.Verified || current.Detection.RendererSafetyOnly || !current.Detection.RequiresGpuConfirm || inFlight != null || nowMs - current.FirstSeenMs < 10000)
				{
					return false;
				}
				current.UncertainLogged = true;
				return true;
			}
		}

		internal void Clear()
		{
			lock (gate)
			{
				ClearLocked();
			}
		}

		private void ClearLocked()
		{
			CancelLocked();
			generation++;
			current = null;
		}

		private void CancelLocked()
		{
			if (inFlight != null)
			{
				inFlight.Canceled = true;
			}
		}

		private void ExpireProofLocked(long nowMs)
		{
			if (current != null && current.Verified && (nowMs < current.ProofMs || nowMs - current.ProofMs > 3000))
			{
				current.Verified = false;
			}
		}

		internal static bool HasGpuEvidence(GameDetection candidate, IDictionary<int, double> utilization, out double value)
		{
			value = 0.0;
			if (candidate == null || utilization == null || !utilization.TryGetValue(candidate.RendererPid, out value) || double.IsNaN(value) || double.IsInfinity(value) || value < 10.0)
			{
				return false;
			}
			foreach (int familyPid in candidate.FamilyPids)
			{
				double value2;
				if (familyPid != candidate.RendererPid && utilization.TryGetValue(familyPid, out value2) && !double.IsNaN(value2) && !double.IsInfinity(value2) && value2 > value)
				{
					return false;
				}
			}
			return true;
		}

		internal static bool SameIdentity(GameDetection a, GameDetection b)
		{
			if (a != null && b != null && a.RendererPid > 0 && a.RendererPid == b.RendererPid && a.RendererCreation > 0 && a.RendererCreation == b.RendererCreation && string.Equals(a.RendererName, b.RendererName, StringComparison.OrdinalIgnoreCase))
			{
				return SamePath(a.RendererPath, b.RendererPath);
			}
			return false;
		}

		internal static bool SameProfile(GameProfile a, GameProfile b)
		{
			if (a != null && b != null && string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase) && a.ForceTrigger == b.ForceTrigger && SamePath(a.Root, b.Root) && SamePath(a.ExecutablePath, b.ExecutablePath))
			{
				return SamePath(a.LearnedExecutablePath, b.LearnedExecutablePath);
			}
			return false;
		}

		private static bool SamePath(string a, string b)
		{
			return string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
		}

		internal static GameDetection Copy(GameDetection source)
		{
			if (source == null)
			{
				return null;
			}
			GameDetection gameDetection = new GameDetection();
			gameDetection.Profile = ((source.Profile == null) ? null : source.Profile.Clone());
			gameDetection.RendererPid = source.RendererPid;
			gameDetection.RendererCreation = source.RendererCreation;
			gameDetection.RendererName = source.RendererName;
			gameDetection.RendererPath = source.RendererPath;
			gameDetection.RendererForeground = source.RendererForeground;
			gameDetection.RendererCandidateSelected = source.RendererCandidateSelected;
			gameDetection.RendererUserSelected = source.RendererUserSelected;
			gameDetection.RendererLearnable = source.RendererLearnable;
			gameDetection.RequiresGpuConfirm = source.RequiresGpuConfirm;
			gameDetection.RendererSafetyOnly = source.RendererSafetyOnly;
			gameDetection.RendererGpuProofExpiresMs = source.RendererGpuProofExpiresMs;
			gameDetection.RendererMatchRank = source.RendererMatchRank;
			gameDetection.Evidence = source.Evidence;
			GameDetection gameDetection2 = gameDetection;
			gameDetection2.FamilyPids.UnionWith(source.FamilyPids);
			gameDetection2.FamilyNames.UnionWith(source.FamilyNames);
			return gameDetection2;
		}
	}
}
