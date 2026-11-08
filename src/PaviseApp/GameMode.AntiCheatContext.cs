namespace PaviseApp
{
	internal partial class GameMode
	{
		private int antiCheatStablePid;
		private long antiCheatStableBirth, antiCheatStableSerial, antiCheatStableSince;

		private static System.Collections.Generic.HashSet<int> VerifiedAntiCheatAncestors(ProcessSnapshot snapshot, GameDetection detection)
		{
			if (snapshot == null || detection == null) return null;
			ProcEntry renderer = snapshot.Find(detection.RendererPid);
			if (renderer == null || renderer.Creation != detection.RendererCreation || renderer.Creation <= 0) return null;
			var seeds = new System.Collections.Generic.HashSet<int>(detection.FamilyPids) { detection.RendererPid };
			var result = new System.Collections.Generic.HashSet<int>();
			foreach (int seed in seeds)
			{
				if (seed <= 4) continue;
				result.Add(seed);
				ProcEntry child = snapshot.Find(seed);
				var chain = new System.Collections.Generic.HashSet<int>();
				while (child != null && child.Pid > 4)
				{
					if (child.Creation <= 0 || !chain.Add(child.Pid)) return null;
					result.Add(child.Pid);
					if (child.ParentPid <= 4) break;
					ProcEntry parent = snapshot.Find(child.ParentPid);
					// A missing parent in the complete snapshot has exited; a newer birth proves PID reuse.
					if (parent == null || parent.Creation > child.Creation) break;
					child = parent;
				}
			}
			return result;
		}

		// The session clock is independent of first-boost timing and GPU automation; callers hold sync across focus changes.
		private bool AntiCheatContextStable(GameDetection detection, long now)
		{
			if (detection == null || now <= 0)
			{
				antiCheatStablePid = 0;
				antiCheatStableSince = 0;
				return false;
			}
			if (antiCheatStablePid != detection.RendererPid || antiCheatStableBirth != detection.RendererCreation
				|| antiCheatStableSerial != gameSessionStarts || antiCheatStableSince == 0 || now < antiCheatStableSince)
			{
				antiCheatStablePid = detection.RendererPid;
				antiCheatStableBirth = detection.RendererCreation;
				antiCheatStableSerial = gameSessionStarts;
				antiCheatStableSince = now;
				return false;
			}
			return now - antiCheatStableSince >= 3 * System.TimeSpan.TicksPerSecond;
		}
	}
}
