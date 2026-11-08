using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace PaviseApp
{
	internal static class GameSessionDetector
	{
		private delegate bool EnumWindowsCallback(IntPtr window, IntPtr state);

		internal struct NativeRect
		{
			public int Left;

			public int Top;

			public int Right;

			public int Bottom;
		}

		private struct NativePoint
		{
			public int X;

			public int Y;
		}

		private struct MonitorInfo
		{
			public int Size;

			public NativeRect Monitor;

			public NativeRect Work;

			public uint Flags;
		}

		internal const int FullscreenCoveragePercent = 97;

		private const long VisibleCacheTtlTicks = 15000000L;

		private const uint GwOwner = 4u;

		private const int GwlStyle = -16;

		private const int GwlExStyle = -20;

		private const int WsExToolWindow = 128;

		private const int WsCaption = 12582912;

		private const uint DwmwaCloaked = 14u;

		private const uint MonitorDefaultToNearest = 2u;

		private static readonly string WindowsRootPrefix = CaptureWindowsRootPrefix();

		private static readonly object visibleCacheSync = new object();

		private static HashSet<int> visibleCacheAll;

		private static HashSet<int> visibleCacheShown;

		private static long visibleCacheAllTicks;

		private static long visibleCacheShownTicks;

		private static string CaptureWindowsRootPrefix()
		{
			try
			{
				string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
				return string.IsNullOrEmpty(folderPath) ? null : (folderPath.TrimEnd('\\') + "\\");
			}
			catch
			{
				return null;
			}
		}

		public static GameDetection Detect(Process[] all, IList<GameProfile> profiles)
		{
			bool armed;
			return Detect(all, profiles, out armed);
		}

		public static GameDetection Detect(Process[] all, IList<GameProfile> profiles, out bool armed)
		{
			armed = false;
			int sessionId;
			try
			{
				using (Process process = Process.GetCurrentProcess())
				{
					sessionId = process.SessionId;
				}
			}
			catch
			{
				return null;
			}
			return Detect(all, profiles, sessionId, out armed);
		}

		public static GameDetection Detect(Process[] all, IList<GameProfile> profiles, int ownerSession)
		{
			bool armed;
			return Detect(all, profiles, ownerSession, out armed);
		}

		public static GameDetection Detect(Process[] all, IList<GameProfile> profiles, int ownerSession, out bool armed)
		{
			string armedProfile;
			GameDetection result = Detect(all, profiles, ownerSession, out armedProfile);
			armed = armedProfile != null;
			return result;
		}

		public static GameDetection Detect(Process[] all, IList<GameProfile> profiles, int ownerSession, out string armedProfile)
		{
			armedProfile = null;
			if (all == null || profiles == null || profiles.Count == 0 || ownerSession < 0)
			{
				return null;
			}
			List<GameProcessSnapshot> list = new List<GameProcessSnapshot>();
			foreach (Process process in all)
			{
				try
				{
					GameProcessSnapshot identity;
					if (TryCaptureProcessIdentity(process.Id, ownerSession, out identity) && !AntiCheatCatalog.IsAntiCheatProcess(identity.Name, identity.Path))
					{
						list.Add(identity);
					}
				}
				catch
				{
				}
			}
			CaptureWindowEvidence(list);
			return DetectSnapshot((IList<GameProcessSnapshot>)list, profiles, out armedProfile);
		}

		public static GameDetection Detect(ProcessSnapshot processes, IList<GameProfile> profiles, int ownerSession, out string armedProfile)
		{
			string armedVia;
			return Detect(processes, profiles, ownerSession, out armedProfile, out armedVia);
		}

		public static GameDetection Detect(ProcessSnapshot processes, IList<GameProfile> profiles, int ownerSession, out string armedProfile, out string armedVia, GameFamilyEvidence familyEvidence = null)
		{
			armedProfile = null;
			armedVia = null;
			if (processes == null || profiles == null || profiles.Count == 0 || ownerSession < 0)
			{
				return null;
			}
			List<GameProcessSnapshot> list = new List<GameProcessSnapshot>();
			ProcEntry[] entries = processes.Entries;
			foreach (ProcEntry entry in entries)
			{
				GameProcessSnapshot identity;
				if (TryCaptureProcessIdentity(entry, ownerSession, out identity) && !AntiCheatCatalog.IsAntiCheatProcess(identity.Name, identity.Path))
				{
					list.Add(identity);
				}
			}
			CaptureWindowEvidence(list);
			return DetectSnapshot(list, profiles, out armedProfile, out armedVia, familyEvidence);
		}

		internal static bool TryCaptureProcessIdentity(ProcEntry entry, int ownerSession, out GameProcessSnapshot identity)
		{
			identity = null;
			if (entry == null || entry.Pid <= 0 || ownerSession < 0)
			{
				return false;
			}
			if (entry.Creation <= 0 || entry.Session != ownerSession)
			{
				return false;
			}
			string imageNameFromVerifiedPathResult = ImageNameFromVerifiedPath(entry.Path);
			if (string.IsNullOrEmpty(imageNameFromVerifiedPathResult))
			{
				return false;
			}
			identity = new GameProcessSnapshot
			{
				Pid = entry.Pid,
				ParentPid = entry.ParentPid,
				Creation = entry.Creation,
				Name = imageNameFromVerifiedPathResult,
				Path = entry.Path
			};
			return true;
		}

		internal static GameDetection CaptureForegroundCandidate(ProcessSnapshot processes, int ownerSession, GameProfile profile, GameDetection incumbent)
		{
			if (profile == null)
			{
				return null;
			}
			return CaptureForegroundCandidate(processes, ownerSession, new GameProfile[1] { profile }, incumbent);
		}

		internal static GameDetection CaptureForegroundCandidate(ProcessSnapshot processes, int ownerSession, IList<GameProfile> profiles, GameDetection incumbent, GameFamilyEvidence familyEvidence = null)
		{
			if (processes == null || ownerSession < 0 || profiles == null || profiles.Count == 0)
			{
				return null;
			}
			int foregroundPidResult = ForegroundPid();
			if (foregroundPidResult <= 0)
			{
				return null;
			}
			ProcEntry entry = processes.Find(foregroundPidResult);
			GameProcessSnapshot identity;
			if (!TryCaptureProcessIdentity(entry, ownerSession, out identity) || SameRendererIdentity(incumbent, identity))
			{
				return null;
			}
			if (!CandidateIdentityUsable(identity) || AntiCheatCatalog.IsAntiCheatProcess(identity.Name, identity.Path))
			{
				return null;
			}
			if (ElectionVetoed(identity.Name, identity.Path))
			{
				return null;
			}
			int pid;
			bool fullscreen;
			if (!TryCaptureCandidateWindow(out pid, out fullscreen) || pid != foregroundPidResult)
			{
				return null;
			}
			if (processes.ByPid.Count != processes.Count)
			{
				return null;
			}
			List<GameProcessSnapshot> list = new List<GameProcessSnapshot>();
			ProcEntry[] entries = processes.Entries;
			foreach (ProcEntry entry2 in entries)
			{
				GameProcessSnapshot identity2;
				if (TryCaptureProcessIdentity(entry2, ownerSession, out identity2))
				{
					identity2.Foreground = identity2.Pid == foregroundPidResult;
					identity2.Visible = identity2.Foreground;
					identity2.FullscreenLike = identity2.Foreground && fullscreen;
					list.Add(identity2);
				}
			}
			GameDetection gameDetection = FindForegroundCandidateSnapshot(list, profiles, incumbent, familyEvidence);
			if (gameDetection == null)
			{
				return null;
			}
			GameProcessSnapshot identity3;
			if (!TryCaptureProcessIdentity(gameDetection.RendererPid, ownerSession, out identity3) || identity3.Creation != gameDetection.RendererCreation || !SamePath(identity3.Path, gameDetection.RendererPath) || !string.Equals(identity3.Name, gameDetection.RendererName, StringComparison.OrdinalIgnoreCase) || ForegroundPid() != gameDetection.RendererPid)
			{
				return null;
			}
			return gameDetection;
		}

		internal static GameDetection FindForegroundCandidateSnapshot(IList<GameProcessSnapshot> snapshot, GameProfile profile, GameDetection incumbent, GameFamilyEvidence familyEvidence = null)
		{
			if (profile == null)
			{
				return null;
			}
			Dictionary<int, GameProcessSnapshot> byPid;
			GameProcessSnapshot foreground;
			if (!TryIndexForegroundCandidate(snapshot, out byPid, out foreground) || SameRendererIdentity(incumbent, foreground))
			{
				return null;
			}
			return FindForegroundCandidateInProfile(byPid, foreground, profile, familyEvidence, incumbent);
		}

		internal static GameDetection FindForegroundCandidateSnapshot(IList<GameProcessSnapshot> snapshot, IList<GameProfile> profiles, GameDetection incumbent, GameFamilyEvidence familyEvidence = null)
		{
			if (profiles == null || profiles.Count == 0)
			{
				return null;
			}
			Dictionary<int, GameProcessSnapshot> byPid;
			GameProcessSnapshot foreground;
			if (!TryIndexForegroundCandidate(snapshot, out byPid, out foreground) || SameRendererIdentity(incumbent, foreground))
			{
				return null;
			}
			GameDetection gameDetection = null;
			bool foregroundCandidateSnapshotReady = false;
			foreach (GameProfile profile in profiles)
			{
				if (profile == null)
				{
					continue;
				}
				GameDetection gameDetection2 = FindForegroundCandidateInProfile(byPid, foreground, profile, familyEvidence, incumbent);
				if (gameDetection2 != null)
				{
					int compareCandidateOwnershipResult = CompareCandidateOwnership(gameDetection2, gameDetection);
					if (compareCandidateOwnershipResult > 0)
					{
						gameDetection = gameDetection2;
						foregroundCandidateSnapshotReady = false;
					}
					else if (compareCandidateOwnershipResult == 0 && !SameCandidateOwner(gameDetection2.Profile, gameDetection.Profile))
					{
						foregroundCandidateSnapshotReady = true;
					}
				}
			}
			if (!foregroundCandidateSnapshotReady)
			{
				return gameDetection;
			}
			return null;
		}

		private static bool TryIndexForegroundCandidate(IList<GameProcessSnapshot> snapshot, out Dictionary<int, GameProcessSnapshot> byPid, out GameProcessSnapshot foreground)
		{
			byPid = new Dictionary<int, GameProcessSnapshot>();
			foreground = null;
			if (snapshot == null)
			{
				return false;
			}
			HashSet<int> hashSet = new HashSet<int>();
			HashSet<int> hashSet2 = new HashSet<int>();
			foreach (GameProcessSnapshot item in snapshot)
			{
				if (item != null && item.Pid > 0 && !hashSet.Add(item.Pid))
				{
					hashSet2.Add(item.Pid);
				}
			}
			foreach (GameProcessSnapshot item2 in snapshot)
			{
				if (!CandidateIdentityUsable(item2) || hashSet2.Contains(item2.Pid) || AntiCheatCatalog.IsAntiCheatProcess(item2.Name, item2.Path))
				{
					continue;
				}
				byPid.Add(item2.Pid, item2);
				if (item2.Foreground)
				{
					if (foreground != null)
					{
						return false;
					}
					foreground = item2;
				}
			}
			return foreground != null;
		}

		private static GameDetection FindForegroundCandidateInProfile(Dictionary<int, GameProcessSnapshot> byPid, GameProcessSnapshot foreground, GameProfile profile, GameFamilyEvidence familyEvidence, GameDetection incumbent = null)
		{
			bool matchesSamePath = SamePath(profile.ExecutablePath, foreground.Path) || SamePath(profile.LearnedExecutablePath, foreground.Path);
			// Fullscreen alone cannot promote a challenger while an existing renderer is alive.
			// Require GPU evidence to exclude fullscreen web/SDK overlays.
			bool challengesLiveRenderer = !matchesSamePath && IncumbentRendererLive(incumbent, profile, foreground, byPid);
			if (ElectionVetoed(foreground.Name, foreground.Path))
			{
				return null;
			}
			HashSet<int> hashSet = new HashSet<int>();
			foreach (GameProcessSnapshot value in byPid.Values)
			{
				if (IsDirectMember(profile, value, familyEvidence))
				{
					hashSet.Add(value.Pid);
				}
			}
			if (!hashSet.Contains(foreground.Pid) && !HasMemberAncestor(foreground, byPid, hashSet))
			{
				return null;
			}
			GameDetection gameDetection;
			if (profile.ForceTrigger && matchesSamePath)
			{
				gameDetection = ElectForced(profile, new List<GameProcessSnapshot> { foreground });
			}
			else if (!profile.ForceTrigger)
			{
				gameDetection = (foreground.FullscreenLike && !challengesLiveRenderer ? Elected(profile, foreground, !matchesSamePath, Lang.T("detect.fullscreen")) : (SamePath(profile.LearnedExecutablePath, foreground.Path) ? Elected(profile, foreground, false, Lang.T("detect.learned")) : ((!SamePath(profile.ExecutablePath, foreground.Path)) ? PendingGpuConfirm(profile, foreground) : Elected(profile, foreground, false, Lang.T("detect.window")))));
			}
			else
			{
				gameDetection = PendingGpuConfirm(profile, foreground);
				gameDetection.RendererSafetyOnly = true;
				gameDetection.RequiresGpuConfirm = false;
				gameDetection.RendererLearnable = false;
				gameDetection.Evidence = null;
			}
			foreach (GameProcessSnapshot value2 in byPid.Values)
			{
				if (hashSet.Contains(value2.Pid) || HasMemberAncestor(value2, byPid, hashSet))
				{
					gameDetection.FamilyPids.Add(value2.Pid);
					gameDetection.FamilyNames.Add(value2.Name);
				}
			}
			return gameDetection;
		}

		private static bool IncumbentRendererLive(GameDetection incumbent, GameProfile profile, GameProcessSnapshot foreground,
			Dictionary<int, GameProcessSnapshot> byPid)
		{
			if (incumbent == null || incumbent.Profile == null || profile == null || string.IsNullOrEmpty(profile.Id)
				|| !string.Equals(incumbent.Profile.Id, profile.Id, StringComparison.OrdinalIgnoreCase)
				|| !incumbent.RendererCandidateSelected || incumbent.RendererSafetyOnly || incumbent.RequiresGpuConfirm
				|| incumbent.RendererPid <= 0 || incumbent.RendererCreation <= 0 || incumbent.RendererPid == foreground.Pid)
			{
				return false;
			}
			GameProcessSnapshot live;
			return byPid.TryGetValue(incumbent.RendererPid, out live) && live.Creation == incumbent.RendererCreation
				&& SamePath(live.Path, incumbent.RendererPath);
		}

		private static int CompareCandidateOwnership(GameDetection candidate, GameDetection current)
		{
			if (current == null)
			{
				return 1;
			}
			int compareToResult = candidate.RendererMatchRank.CompareTo(current.RendererMatchRank);
			if (compareToResult != 0)
			{
				return compareToResult;
			}
			if (candidate.RendererMatchRank == 1)
			{
				int length = candidate.Profile.Root.TrimEnd('\\').Length;
				int length2 = current.Profile.Root.TrimEnd('\\').Length;
				return length.CompareTo(length2);
			}
			return 0;
		}

		private static bool SameCandidateOwner(GameProfile a, GameProfile b)
		{
			if (object.ReferenceEquals(a, b))
			{
				return true;
			}
			if (a != null && b != null && !string.IsNullOrEmpty(a.Id) && string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase) && a.ForceTrigger == b.ForceTrigger && string.Equals(a.Root ?? "", b.Root ?? "", StringComparison.OrdinalIgnoreCase) && string.Equals(a.ExecutablePath ?? "", b.ExecutablePath ?? "", StringComparison.OrdinalIgnoreCase))
			{
				return string.Equals(a.LearnedExecutablePath ?? "", b.LearnedExecutablePath ?? "", StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}

		private static bool CandidateIdentityUsable(GameProcessSnapshot identity)
		{
			if (identity == null || identity.Pid <= 0 || identity.Creation <= 0 || string.IsNullOrEmpty(identity.Name) || string.IsNullOrEmpty(identity.Path))
			{
				return false;
			}
			try
			{
				if (!Path.IsPathRooted(identity.Path) || !SamePath(Path.GetFullPath(identity.Path), identity.Path))
				{
					return false;
				}
			}
			catch
			{
				return false;
			}
			return string.Equals(identity.Name, ImageNameFromVerifiedPath(identity.Path), StringComparison.OrdinalIgnoreCase);
		}

		private static bool SameRendererIdentity(GameDetection incumbent, GameProcessSnapshot identity)
		{
			if (incumbent != null && identity != null && incumbent.RendererPid == identity.Pid && incumbent.RendererCreation > 0 && incumbent.RendererCreation == identity.Creation)
			{
				return SamePath(incumbent.RendererPath, identity.Path);
			}
			return false;
		}

		private static bool TryCaptureCandidateWindow(out int pid, out bool fullscreen)
		{
			pid = 0;
			fullscreen = false;
			try
			{
				IntPtr foregroundWindow = GetForegroundWindow();
				if (foregroundWindow == IntPtr.Zero || !IsWindowVisible(foregroundWindow) || IsIconic(foregroundWindow))
				{
					return false;
				}
				uint pid2;
				GetWindowThreadProcessId(foregroundWindow, out pid2);
				if (pid2 == 0 || pid2 > int.MaxValue)
				{
					return false;
				}
				pid = (int)pid2;
				NativeRect rect;
				fullscreen = GetWindowRect(foregroundWindow, out rect) && IsFullscreenLikeWindow(foregroundWindow, rect);
				return true;
			}
			catch
			{
				return false;
			}
		}

		internal static GameDetection DetectSnapshot(IList<GameProcessSnapshot> snapshot, IList<GameProfile> profiles)
		{
			bool armed;
			return DetectSnapshot(snapshot, profiles, out armed);
		}

		internal static GameDetection DetectSnapshot(IList<GameProcessSnapshot> snapshot, IList<GameProfile> profiles, out bool armed)
		{
			string armedProfile;
			GameDetection result = DetectSnapshot(snapshot, profiles, out armedProfile);
			armed = armedProfile != null;
			return result;
		}

		internal static GameDetection DetectSnapshot(IList<GameProcessSnapshot> snapshot, IList<GameProfile> profiles, out string armedProfile)
		{
			string armedVia;
			return DetectSnapshot(snapshot, profiles, out armedProfile, out armedVia);
		}

		internal static GameDetection DetectSnapshot(IList<GameProcessSnapshot> snapshot, IList<GameProfile> profiles, out string armedProfile, out string armedVia, GameFamilyEvidence familyEvidence = null)
		{
			armedProfile = null;
			armedVia = null;
			if (snapshot == null || profiles == null || profiles.Count == 0)
			{
				return null;
			}
			HashSet<int> hashSet = new HashSet<int>();
			HashSet<int> hashSet2 = new HashSet<int>();
			foreach (GameProcessSnapshot item in snapshot)
			{
				if (item != null && item.Pid > 0 && !hashSet.Add(item.Pid))
				{
					hashSet2.Add(item.Pid);
				}
			}
			Dictionary<int, GameProcessSnapshot> dictionary = new Dictionary<int, GameProcessSnapshot>();
			foreach (GameProcessSnapshot item2 in snapshot)
			{
				if (CandidateIdentityUsable(item2) && !hashSet2.Contains(item2.Pid) && !ElectionVetoed(item2.Name, item2.Path))
				{
					dictionary[item2.Pid] = item2;
				}
			}
			if (dictionary.Count == 0)
			{
				return null;
			}
			GameDetection gameDetection = null;
			foreach (GameProfile profile in profiles)
			{
				if (profile == null)
				{
					continue;
				}
				HashSet<int> hashSet3 = new HashSet<int>();
				List<GameProcessSnapshot> list = new List<GameProcessSnapshot>();
				foreach (GameProcessSnapshot value in dictionary.Values)
				{
					if (IsDirectMember(profile, value, familyEvidence))
					{
						hashSet3.Add(value.Pid);
						list.Add(value);
					}
				}
				if (list.Count == 0)
				{
					continue;
				}
				foreach (GameProcessSnapshot value2 in dictionary.Values)
				{
					if (!hashSet3.Contains(value2.Pid) && HasMemberAncestor(value2, dictionary, hashSet3))
					{
						hashSet3.Add(value2.Pid);
						list.Add(value2);
					}
				}
				if (armedProfile == null)
				{
					string armedViaResult = ArmedVia(profile, list);
					if (armedViaResult != null)
					{
						armedProfile = profile.Name;
						armedVia = armedViaResult;
					}
				}
				GameDetection gameDetection2 = Elect(profile, list);
				if (gameDetection2 == null)
				{
					continue;
				}
				foreach (GameProcessSnapshot item3 in list)
				{
					gameDetection2.FamilyNames.Add(item3.Name);
					gameDetection2.FamilyPids.Add(item3.Pid);
				}
				if (BetterHit(gameDetection2, gameDetection))
				{
					gameDetection = gameDetection2;
				}
			}
			return gameDetection;
		}

		internal static string ArmedVia(GameProfile profile, IList<GameProcessSnapshot> members)
		{
			if (profile == null || members == null)
			{
				return null;
			}
			List<string> list = new List<string>();
			foreach (GameProcessSnapshot member in members)
			{
				if (CandidateIdentityUsable(member) && !ElectionVetoed(member.Name, member.Path))
				{
					// Persistent launcher/platform descendants remain protected without keeping a closed game active.
					bool selected = SamePath(profile.ExecutablePath, member.Path) || SamePath(profile.LearnedExecutablePath, member.Path);
					if (!selected && ((!member.Visible && !member.Foreground) || PlatformRuntimePolicy.RuntimePlatform(member.Name) != null))
					{
						continue;
					}
					if (!list.Contains(member.Name))
					{
						list.Add(member.Name);
					}
					if (list.Count >= 3)
					{
						break;
					}
				}
			}
			if (list.Count != 0)
			{
				return string.Join(" ", list.ToArray());
			}
			return null;
		}

		private static bool IsDirectMember(GameProfile profile, GameProcessSnapshot identity, GameFamilyEvidence familyEvidence = null)
		{
			if (SamePath(profile.ExecutablePath, identity.Path))
			{
				return true;
			}
			if (SamePath(profile.LearnedExecutablePath, identity.Path))
			{
				return true;
			}
			if (!profile.ContainsPath(identity.Path))
			{
				if (familyEvidence != null)
				{
					return familyEvidence.Contains(profile, identity.Pid, identity.Creation, identity.Path);
				}
				return false;
			}
			return true;
		}

		private static bool HasMemberAncestor(GameProcessSnapshot identity, Dictionary<int, GameProcessSnapshot> byPid, HashSet<int> memberPids)
		{
			GameProcessSnapshot gameProcessSnapshot = identity;
			HashSet<int> hashSet = new HashSet<int>();
			hashSet.Add(identity.Pid);
			int processedCount = 0;
			while (gameProcessSnapshot.ParentPid > 0 && processedCount < 24)
			{
				int parentPid = gameProcessSnapshot.ParentPid;
				if (!hashSet.Add(parentPid))
				{
					return false;
				}
				GameProcessSnapshot value;
				if (!byPid.TryGetValue(parentPid, out value))
				{
					return false;
				}
				if (gameProcessSnapshot.Creation <= 0 || value.Creation <= 0 || gameProcessSnapshot.Creation < value.Creation)
				{
					return false;
				}
				if (memberPids.Contains(parentPid))
				{
					return true;
				}
				gameProcessSnapshot = value;
				processedCount++;
			}
			return false;
		}

		private static GameDetection Elect(GameProfile profile, List<GameProcessSnapshot> members)
		{
			if (profile.ForceTrigger)
			{
				return ElectForced(profile, members);
			}
			GameProcessSnapshot gameProcessSnapshot = null;
			GameProcessSnapshot gameProcessSnapshot2 = null;
			foreach (GameProcessSnapshot member in members)
			{
				if ((member.Visible || member.Foreground) && !ElectionVetoed(member.Name, member.Path))
				{
					if (SamePath(profile.LearnedExecutablePath, member.Path) && (gameProcessSnapshot == null || PreferSnapshot(member, gameProcessSnapshot)))
					{
						gameProcessSnapshot = member;
					}
					if (member.Foreground && (gameProcessSnapshot2 == null || member.Creation > gameProcessSnapshot2.Creation))
					{
						gameProcessSnapshot2 = member;
					}
				}
			}
			if (gameProcessSnapshot2 != null && gameProcessSnapshot2.FullscreenLike)
			{
				return Elected(profile, gameProcessSnapshot2, !SamePath(profile.ExecutablePath, gameProcessSnapshot2.Path) && !SamePath(profile.LearnedExecutablePath, gameProcessSnapshot2.Path), Lang.T("detect.fullscreen"));
			}
			if (gameProcessSnapshot != null)
			{
				return Elected(profile, gameProcessSnapshot, false, Lang.T("detect.learned"));
			}
			if (gameProcessSnapshot2 == null)
			{
				return null;
			}
			if (SamePath(profile.ExecutablePath, gameProcessSnapshot2.Path))
			{
				return Elected(profile, gameProcessSnapshot2, false, Lang.T("detect.window"));
			}
			return PendingGpuConfirm(profile, gameProcessSnapshot2);
		}

		private static GameDetection ElectForced(GameProfile profile, List<GameProcessSnapshot> members)
		{
			GameProcessSnapshot gameProcessSnapshot = null;
			foreach (GameProcessSnapshot member in members)
			{
				if ((SamePath(profile.ExecutablePath, member.Path) || SamePath(profile.LearnedExecutablePath, member.Path)) && (gameProcessSnapshot == null || PreferForced(member, gameProcessSnapshot)))
				{
					gameProcessSnapshot = member;
				}
			}
			if (gameProcessSnapshot == null)
			{
				return null;
			}
			GameDetection gameDetection = new GameDetection();
			gameDetection.Profile = profile.Clone();
			gameDetection.RendererPid = gameProcessSnapshot.Pid;
			gameDetection.RendererCreation = gameProcessSnapshot.Creation;
			gameDetection.RendererName = gameProcessSnapshot.Name;
			gameDetection.RendererPath = gameProcessSnapshot.Path;
			gameDetection.RendererForeground = gameProcessSnapshot.Foreground;
			gameDetection.RendererCandidateSelected = true;
			gameDetection.RendererUserSelected = true;
			gameDetection.RendererLearnable = false;
			gameDetection.RendererMatchRank = MatchRank(profile, gameProcessSnapshot.Path);
			gameDetection.Evidence = Lang.T("detect.forced");
			return gameDetection;
		}

		private static bool PreferForced(GameProcessSnapshot candidate, GameProcessSnapshot current)
		{
			if (candidate.Foreground != current.Foreground)
			{
				return candidate.Foreground;
			}
			if (candidate.Creation != current.Creation)
			{
				return candidate.Creation < current.Creation;
			}
			return candidate.Pid < current.Pid;
		}

		internal static bool ElectionVetoed(string name, string path)
		{
			if (string.IsNullOrEmpty(name))
			{
				return true;
			}
			return IsNonGameRole(name, path);
		}

		private static GameDetection Elected(GameProfile profile, GameProcessSnapshot selected, bool learnable, string evidence)
		{
			GameDetection gameDetection = new GameDetection();
			gameDetection.Profile = profile.Clone();
			gameDetection.RendererPid = selected.Pid;
			gameDetection.RendererCreation = selected.Creation;
			gameDetection.RendererName = selected.Name;
			gameDetection.RendererPath = selected.Path;
			gameDetection.RendererForeground = selected.Foreground;
			gameDetection.RendererCandidateSelected = true;
			gameDetection.RendererUserSelected = SamePath(profile.ExecutablePath, selected.Path) || SamePath(profile.LearnedExecutablePath, selected.Path);
			gameDetection.RendererLearnable = learnable;
			gameDetection.RendererMatchRank = MatchRank(profile, selected.Path);
			gameDetection.Evidence = evidence;
			return gameDetection;
		}

		private static GameDetection PendingGpuConfirm(GameProfile profile, GameProcessSnapshot candidate)
		{
			GameDetection gameDetection = new GameDetection();
			gameDetection.Profile = profile.Clone();
			gameDetection.RendererPid = candidate.Pid;
			gameDetection.RendererCreation = candidate.Creation;
			gameDetection.RendererName = candidate.Name;
			gameDetection.RendererPath = candidate.Path;
			gameDetection.RendererForeground = candidate.Foreground;
			gameDetection.RendererCandidateSelected = false;
			gameDetection.RendererUserSelected = false;
			gameDetection.RendererLearnable = true;
			gameDetection.RequiresGpuConfirm = true;
			gameDetection.RendererMatchRank = MatchRank(profile, candidate.Path);
			gameDetection.Evidence = Lang.T("detect.gpu.pending");
			return gameDetection;
		}

		private static int MatchRank(GameProfile profile, string path)
		{
			if (profile == null || string.IsNullOrEmpty(path))
			{
				return 0;
			}
			if (SamePath(profile.ExecutablePath, path))
			{
				return 3;
			}
			if (SamePath(profile.LearnedExecutablePath, path))
			{
				return 2;
			}
			if (!profile.ContainsPath(path))
			{
				return 0;
			}
			return 1;
		}

		private static bool PreferSnapshot(GameProcessSnapshot candidate, GameProcessSnapshot current)
		{
			if (candidate.Foreground != current.Foreground)
			{
				return candidate.Foreground;
			}
			if (candidate.Creation != current.Creation)
			{
				return candidate.Creation > current.Creation;
			}
			return candidate.Pid < current.Pid;
		}

		private static bool BetterHit(GameDetection candidate, GameDetection current)
		{
			if (candidate == null)
			{
				return false;
			}
			if (current == null)
			{
				return true;
			}
			bool isNotRequiresGpuConfirm = !candidate.RequiresGpuConfirm;
			bool isNotRequiresGpuConfirm2 = !current.RequiresGpuConfirm;
			if (isNotRequiresGpuConfirm != isNotRequiresGpuConfirm2)
			{
				return isNotRequiresGpuConfirm;
			}
			if (candidate.RendererForeground != current.RendererForeground)
			{
				return candidate.RendererForeground;
			}
			if (candidate.RendererPid == current.RendererPid && candidate.RendererCreation == current.RendererCreation)
			{
				if (candidate.RendererMatchRank != current.RendererMatchRank)
				{
					return candidate.RendererMatchRank > current.RendererMatchRank;
				}
				bool matchesContainsPath = candidate.Profile != null && candidate.Profile.ContainsPath(candidate.RendererPath);
				bool matchesContainsPath2 = current.Profile != null && current.Profile.ContainsPath(current.RendererPath);
				if (matchesContainsPath != matchesContainsPath2)
				{
					return matchesContainsPath;
				}
				int compareStableProfileResult = CompareStableProfile(candidate.Profile, current.Profile);
				if (compareStableProfileResult != 0)
				{
					return compareStableProfileResult < 0;
				}
			}
			if (candidate.RendererCreation != current.RendererCreation)
			{
				return candidate.RendererCreation > current.RendererCreation;
			}
			return candidate.RendererPid < current.RendererPid;
		}

		private static int CompareStableProfile(GameProfile a, GameProfile b)
		{
			if (object.ReferenceEquals(a, b))
			{
				return 0;
			}
			if (a == null)
			{
				return 1;
			}
			if (b == null)
			{
				return -1;
			}
			string[] textEntries = new string[5] { a.Id, a.Root, a.ExecutablePath, a.LearnedExecutablePath, a.Name };
			string[] textEntries2 = new string[5] { b.Id, b.Root, b.ExecutablePath, b.LearnedExecutablePath, b.Name };
			for (int i = 0; i < textEntries.Length; i++)
			{
				int compareResult = string.Compare(textEntries[i] ?? "", textEntries2[i] ?? "", StringComparison.OrdinalIgnoreCase);
				if (compareResult != 0)
				{
					return compareResult;
				}
				compareResult = string.CompareOrdinal(textEntries[i] ?? "", textEntries2[i] ?? "");
				if (compareResult != 0)
				{
					return compareResult;
				}
			}
			return 0;
		}

		internal static bool IsNonGameRole(string name, string path)
		{
			string trimmed = (name ?? "").Trim();
			if (AntiCheatCatalog.IsAntiCheatProcess(trimmed, path))
			{
				return true;
			}
			if (!string.IsNullOrEmpty(WindowsRootPrefix) && !string.IsNullOrEmpty(path) && path.StartsWith(WindowsRootPrefix, StringComparison.OrdinalIgnoreCase) && (SystemProcessCatalog.IsShellProcess(trimmed) || SystemProcessCatalog.IsCoreSystemProcess(trimmed, path, WindowsRootPrefix)))
			{
				return true;
			}
			string lowered = ((path ?? "") + "\\" + trimmed).ToLowerInvariant();
			return AntiCheatCatalog.ContainsToken(lowered);
		}

		internal static bool IsLibraryCandidate(string name, string path, string windowsRoot)
		{
			if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path))
			{
				return false;
			}
			if (ElectionVetoed(name, path))
			{
				return false;
			}
			if (GamePerformanceTargetPolicy.KnownNonGame(name, path))
			{
				return false;
			}
			if (!string.IsNullOrEmpty(windowsRoot))
			{
				return !path.StartsWith(windowsRoot, StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}

		private static bool SamePath(string a, string b)
		{
			if (!string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b))
			{
				return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}

		internal static bool IsProfileEntryName(GameProfile profile, string name)
		{
			if (profile != null && !string.IsNullOrEmpty(name) && profile.Entries != null)
			{
				if (!profile.Entries.Contains(name))
				{
					return IsFallbackEntryName(profile, name);
				}
				return true;
			}
			return false;
		}

		internal static bool IsProfileEntryProcess(GameProfile profile, string name, string path)
		{
			if (profile == null)
			{
				return false;
			}
			if (SamePath(profile.LearnedExecutablePath, path))
			{
				return true;
			}
			if (SamePath(profile.ExecutablePath, path))
			{
				return true;
			}
			return profile.ContainsPath(path);
		}

		private static bool IsFallbackEntryName(GameProfile profile, string name)
		{
			if (string.IsNullOrEmpty(profile.ExecutablePath) || string.IsNullOrEmpty(name))
			{
				return false;
			}
			string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(profile.ExecutablePath);
			if (string.IsNullOrEmpty(fileNameWithoutExtension) || fileNameWithoutExtension.Length < 3)
			{
				return false;
			}
			if (string.Equals(fileNameWithoutExtension, name, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			if (!name.StartsWith(fileNameWithoutExtension, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			return IsBitnessOrVersionSuffix(name.Substring(fileNameWithoutExtension.Length));
		}

		private static bool IsBitnessOrVersionSuffix(string suffix)
		{
			if (string.IsNullOrEmpty(suffix))
			{
				return false;
			}
			int isBitnessOrVersionSuffixValue = ((suffix[0] == '_' || suffix[0] == '-') ? 1 : 0);
			if (isBitnessOrVersionSuffixValue >= suffix.Length)
			{
				return false;
			}
			string substringResult = suffix.Substring(isBitnessOrVersionSuffixValue);
			bool isBitnessOrVersionSuffixReady = true;
			string message = substringResult;
			foreach (char c in message)
			{
				if (!char.IsDigit(c))
				{
					isBitnessOrVersionSuffixReady = false;
					break;
				}
			}
			if (isBitnessOrVersionSuffixReady)
			{
				return true;
			}
			string toLowerInvariantResult = substringResult.ToLowerInvariant();
			if (toLowerInvariantResult == "x64" || toLowerInvariantResult == "x86")
			{
				return true;
			}
			if (toLowerInvariantResult.Length >= 2 && toLowerInvariantResult[0] == 'v')
			{
				bool isBitnessOrVersionSuffixReady2 = true;
				for (int j = 1; j < toLowerInvariantResult.Length; j++)
				{
					if (!char.IsDigit(toLowerInvariantResult[j]))
					{
						isBitnessOrVersionSuffixReady2 = false;
						break;
					}
				}
				if (isBitnessOrVersionSuffixReady2)
				{
					return true;
				}
			}
			return false;
		}

		internal static bool TryCaptureProcessIdentity(int pid, int ownerSession, out GameProcessSnapshot identity)
		{
			identity = null;
			if (pid <= 0 || ownerSession < 0)
			{
				return false;
			}
			IntPtr intPtr = Native.OpenProcess(1052672, false, pid);
			if (intPtr == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				long creation;
				long exit;
				long kernel;
				long user;
				if (!GetProcessTimes(intPtr, out creation, out exit, out kernel, out user) || creation <= 0)
				{
					return false;
				}
				string imagePathResult = Native.ImagePath(intPtr);
				string imageNameFromVerifiedPathResult = ImageNameFromVerifiedPath(imagePathResult);
				int sessionId;
				if (string.IsNullOrEmpty(imageNameFromVerifiedPathResult) || !Native.TryGetLiveProcessSessionId(intPtr, pid, out sessionId) || sessionId != ownerSession)
				{
					return false;
				}
				identity = new GameProcessSnapshot
				{
					Pid = pid,
					ParentPid = Native.ParentProcessId(intPtr),
					Creation = creation,
					Name = imageNameFromVerifiedPathResult,
					Path = imagePathResult
				};
				return true;
			}
			finally
			{
				Native.CloseHandle(intPtr);
			}
		}

		internal static string ImageNameFromVerifiedPath(string imagePath)
		{
			try
			{
				if (string.IsNullOrWhiteSpace(imagePath))
				{
					return null;
				}
				string fileName = Path.GetFileName(imagePath.Trim());
				if (string.IsNullOrWhiteSpace(fileName))
				{
					return null;
				}
				string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
				return string.IsNullOrWhiteSpace(fileNameWithoutExtension) ? null : fileNameWithoutExtension.Trim();
			}
			catch
			{
				return null;
			}
		}

		private static void CaptureWindowEvidence(IList<GameProcessSnapshot> snapshot)
		{
			if (snapshot == null || snapshot.Count == 0)
			{
				return;
			}
			int foregroundPidResult = ForegroundPid();
			bool matchesForegroundWindowFullscreenLike = foregroundPidResult > 0 && ForegroundWindowFullscreenLike(foregroundPidResult);
			HashSet<int> hashSet = VisibleWindowPids(false);
			foreach (GameProcessSnapshot item in snapshot)
			{
				if (item != null && item.Pid > 0 && item.Creation > 0)
				{
					bool foregroundValue = item.Pid == foregroundPidResult;
					bool visibleValue = hashSet.Contains(item.Pid);
					if ((foregroundValue || visibleValue) && IsLiveProcessCreation(item.Pid, item.Creation))
					{
						item.Foreground = foregroundValue;
						item.Visible = visibleValue;
						item.FullscreenLike = foregroundValue && matchesForegroundWindowFullscreenLike;
					}
				}
			}
		}

		private static bool IsLiveProcessCreation(int pid, long expectedCreation)
		{
			if (pid <= 0 || expectedCreation <= 0)
			{
				return false;
			}
			IntPtr intPtr = Native.OpenProcess(1052672, false, pid);
			if (intPtr == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				long creation;
				long exit;
				long kernel;
				long user;
				int sessionId;
				return GetProcessTimes(intPtr, out creation, out exit, out kernel, out user) && creation == expectedCreation && Native.TryGetLiveProcessSessionId(intPtr, pid, out sessionId);
			}
			finally
			{
				Native.CloseHandle(intPtr);
			}
		}

		internal static bool TryForegroundFullscreen(out int pid)
		{
			pid = 0;
			try
			{
				IntPtr foregroundWindow = GetForegroundWindow();
				if (foregroundWindow == IntPtr.Zero)
				{
					return false;
				}
				uint pid2;
				GetWindowThreadProcessId(foregroundWindow, out pid2);
				if (pid2 == 0 || pid2 > int.MaxValue)
				{
					return false;
				}
				pid = (int)pid2;
				if (!IsWindowVisible(foregroundWindow) || IsIconic(foregroundWindow))
				{
					return false;
				}
				NativeRect rect;
				if (!GetWindowRect(foregroundWindow, out rect))
				{
					return false;
				}
				return IsFullscreenLikeWindow(foregroundWindow, rect);
			}
			catch
			{
				return false;
			}
		}

		internal static bool TryForegroundGameWindow(out ForegroundGameWindowSnapshot snapshot)
		{
			snapshot = null;
			try
			{
				IntPtr foregroundWindow = GetForegroundWindow();
				if (foregroundWindow == IntPtr.Zero || !IsWindowVisible(foregroundWindow) || IsIconic(foregroundWindow) || ((uint)GetWindowLong(foregroundWindow, -20) & 0x80u) != 0)
				{
					return false;
				}
				uint pid;
				if (GetWindowThreadProcessId(foregroundWindow, out pid) == 0 || pid == 0 || pid > int.MaxValue)
				{
					return false;
				}
				int value;
				if (DwmGetWindowAttribute(foregroundWindow, 14u, out value, 4) == 0 && value != 0)
				{
					return false;
				}
				NativeRect rect;
				NativeRect rect2;
				if (!GetWindowRect(foregroundWindow, out rect) || !RectHasArea(rect) || !TryGetScreenClientRect(foregroundWindow, out rect2))
				{
					return false;
				}
				bool fullscreenLike = CaptureFullscreenEvidence(foregroundWindow, rect, rect2);
				uint pid2;
				if (GetForegroundWindow() != foregroundWindow || GetWindowThreadProcessId(foregroundWindow, out pid2) == 0 || pid2 != pid)
				{
					return false;
				}
				snapshot = new ForegroundGameWindowSnapshot
				{
					Pid = (int)pid,
					FullscreenLike = fullscreenLike
				};
				return true;
			}
			catch
			{
				return false;
			}
		}

		private static bool ForegroundWindowFullscreenLike(int pid)
		{
			int pid2;
			if (TryForegroundFullscreen(out pid2))
			{
				return pid2 == pid;
			}
			return false;
		}

		internal static bool IsFullscreenLikeWindow(IntPtr window, NativeRect rect)
		{
			try
			{
				int windowLong = GetWindowLong(window, -16);
				if ((windowLong & 0xC00000) == 12582912)
				{
					return false;
				}
				IntPtr intPtr = MonitorFromWindow(window, 2u);
				if (intPtr == IntPtr.Zero)
				{
					return false;
				}
				MonitorInfo info = default(MonitorInfo);
				info.Size = Marshal.SizeOf(typeof(MonitorInfo));
				return GetMonitorInfo(intPtr, ref info) && RectCoversMonitor(rect, info.Monitor);
			}
			catch
			{
				return false;
			}
		}

		private static bool CaptureFullscreenEvidence(IntPtr window, NativeRect rect, NativeRect client)
		{
			try
			{
				int windowLong = GetWindowLong(window, -16);
				IntPtr intPtr = MonitorFromWindow(window, 2u);
				if (intPtr == IntPtr.Zero)
				{
					return false;
				}
				MonitorInfo info = default(MonitorInfo);
				info.Size = Marshal.SizeOf(typeof(MonitorInfo));
				if (!GetMonitorInfo(intPtr, ref info))
				{
					return false;
				}
				bool hasCaption = (windowLong & 0xC00000) == 12582912;
				return IsFullscreenLikeGeometry(hasCaption, rect, client, info.Monitor, info.Work);
			}
			catch
			{
				return false;
			}
		}

		private static bool TryGetScreenClientRect(IntPtr window, out NativeRect rect)
		{
			rect = default(NativeRect);
			try
			{
				NativeRect rect2;
				if (!GetClientRect(window, out rect2) || !RectHasArea(rect2))
				{
					return false;
				}
				NativePoint nativePoint = default(NativePoint);
				nativePoint.X = rect2.Left;
				nativePoint.Y = rect2.Top;
				NativePoint point = nativePoint;
				NativePoint nativePoint2 = default(NativePoint);
				nativePoint2.X = rect2.Right;
				nativePoint2.Y = rect2.Bottom;
				NativePoint point2 = nativePoint2;
				if (!ClientToScreen(window, ref point) || !ClientToScreen(window, ref point2))
				{
					return false;
				}
				rect = new NativeRect
				{
					Left = point.X,
					Top = point.Y,
					Right = point2.X,
					Bottom = point2.Y
				};
				return RectHasArea(rect);
			}
			catch
			{
				return false;
			}
		}

		internal static bool IsFullscreenLikeGeometry(bool hasCaption, NativeRect windowRect, NativeRect clientRect, NativeRect monitor, NativeRect work)
		{
			if (!RectHasArea(windowRect) || !RectHasArea(clientRect) || !RectHasArea(monitor))
			{
				return false;
			}
			if (hasCaption)
			{
				if (RectCoversMonitor(clientRect, monitor))
				{
					return RectEdgesMatch(clientRect, monitor);
				}
				return false;
			}
			if (RectCoversMonitor(windowRect, monitor))
			{
				return true;
			}
			if (RectHasArea(work) && work.Left >= monitor.Left && work.Top >= monitor.Top && work.Right <= monitor.Right && work.Bottom <= monitor.Bottom)
			{
				return RectEdgesMatch(windowRect, work);
			}
			return false;
		}

		private static bool RectHasArea(NativeRect rect)
		{
			if (rect.Right > rect.Left)
			{
				return rect.Bottom > rect.Top;
			}
			return false;
		}

		private static bool RectEdgesMatch(NativeRect rect, NativeRect bounds)
		{
			if (Math.Abs((long)rect.Left - (long)bounds.Left) <= 4 && Math.Abs((long)rect.Top - (long)bounds.Top) <= 4 && Math.Abs((long)rect.Right - (long)bounds.Right) <= 4)
			{
				return Math.Abs((long)rect.Bottom - (long)bounds.Bottom) <= 4;
			}
			return false;
		}

		internal static bool RectCoversMonitor(NativeRect rect, NativeRect monitor)
		{
			if (!RectHasArea(rect) || !RectHasArea(monitor))
			{
				return false;
			}
			double scaledValue = (double)((long)monitor.Right - (long)monitor.Left) * (double)((long)monitor.Bottom - (long)monitor.Top);
			int leftLimit = Math.Max(rect.Left, monitor.Left);
			int topLimit = Math.Max(rect.Top, monitor.Top);
			int rightLimit = Math.Min(rect.Right, monitor.Right);
			int bottomLimit = Math.Min(rect.Bottom, monitor.Bottom);
			double scaledValue2 = ((rightLimit > leftLimit && bottomLimit > topLimit) ? ((double)((long)rightLimit - (long)leftLimit) * (double)((long)bottomLimit - (long)topLimit)) : 0.0);
			return scaledValue2 * 100.0 >= scaledValue * 97.0;
		}

		internal static HashSet<int> VisibleWindowPids(bool includeMinimized)
		{
			long ticks = DateTime.UtcNow.Ticks;
			lock (visibleCacheSync)
			{
				HashSet<int> hashSet = (includeMinimized ? visibleCacheAll : visibleCacheShown);
				long visibleCacheAllTicksSnapshot = (includeMinimized ? visibleCacheAllTicks : visibleCacheShownTicks);
				if (hashSet != null && ticks >= visibleCacheAllTicksSnapshot && ticks - visibleCacheAllTicksSnapshot < 15000000)
				{
					return hashSet;
				}
			}
			bool succeeded;
			HashSet<int> result = VisibleWindowPids(includeMinimized, out succeeded);
			if (!succeeded)
			{
				return result;
			}
			lock (visibleCacheSync)
			{
				if (includeMinimized)
				{
					visibleCacheAll = result;
					visibleCacheAllTicks = ticks;
				}
				else
				{
					visibleCacheShown = result;
					visibleCacheShownTicks = ticks;
				}
			}
			return result;
		}

		internal static HashSet<int> VisibleWindowPids(bool includeMinimized, out bool succeeded)
		{
			HashSet<int> result = new HashSet<int>();
			succeeded = false;
			try
			{
				succeeded = EnumWindows(delegate(IntPtr window, IntPtr state)
				{
					try
					{
						if (!IsWindowVisible(window) || GetWindow(window, 4u) != IntPtr.Zero || ((uint)GetWindowLong(window, -20) & 0x80u) != 0 || (!includeMinimized && IsIconic(window)))
						{
							return true;
						}
						int value;
						if (DwmGetWindowAttribute(window, 14u, out value, 4) == 0 && value != 0)
						{
							return true;
						}
						NativeRect rect;
						if (!GetWindowRect(window, out rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
						{
							return true;
						}
						uint pid;
						GetWindowThreadProcessId(window, out pid);
						if (pid != 0 && pid <= int.MaxValue)
						{
							result.Add((int)pid);
						}
					}
					catch
					{
					}
					return true;
				}, IntPtr.Zero);
			}
			catch
			{
			}
			return result;
		}

		internal static int ForegroundPid()
		{
			try
			{
				uint pid;
				GetWindowThreadProcessId(GetForegroundWindow(), out pid);
				return (int)pid;
			}
			catch
			{
				return -1;
			}
		}

		[DllImport("user32.dll")]
		private static extern IntPtr GetForegroundWindow();

		[DllImport("user32.dll")]
		private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr state);

		[DllImport("user32.dll")]
		private static extern IntPtr GetWindow(IntPtr window, uint command);

		[DllImport("user32.dll")]
		private static extern int GetWindowLong(IntPtr window, int index);

		[DllImport("user32.dll")]
		private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

		[DllImport("user32.dll")]
		private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

		[DllImport("user32.dll")]
		private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);

		[DllImport("user32.dll")]
		private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

		[DllImport("user32.dll")]
		private static extern bool IsWindowVisible(IntPtr hwnd);

		[DllImport("user32.dll")]
		private static extern bool IsIconic(IntPtr hwnd);

		[DllImport("user32.dll")]
		private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

		[DllImport("user32.dll", CharSet = CharSet.Auto)]
		private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

		[DllImport("dwmapi.dll")]
		private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out int value, int size);
	}
}
