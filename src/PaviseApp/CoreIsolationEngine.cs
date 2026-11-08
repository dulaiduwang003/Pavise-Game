using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PaviseApp
{
	internal sealed class CoreIsolationEngine : IDisposable
	{
		private sealed class Entry
		{
			internal int Pid;

			internal long Creation;

			internal ulong Original;

			internal ulong Applied;

			internal IntPtr Handle;
		}

		private readonly ICoreIsolationPlatform os;

		private readonly ICoreIsolationStore store;

		private readonly List<Entry> entries = new List<Entry>();

		private string boot;

		private ulong all;

		private ulong isolated;

		private ulong pre;

		private bool systemPrepared;

		private bool manual;

        private sealed class RecoveryAttempt { internal int Failures; internal long Due; }
        private readonly Dictionary<string, RecoveryAttempt> recoveryAttempts = new Dictionary<string, RecoveryAttempt>();
        private readonly List<Entry> recoveryOwners = new List<Entry>();
        private long recoverySession;
        private bool ownersExited;
        private string ownedReceipt;
        private static long RecoveryClock() { return AutoCores.NowTicks; }
        internal void SyncAutomaticSession(long session)
        { if (session > recoverySession) recoverySession = session; }
        private string RecoveryPhase()
        {
            if (!ownersExited && recoveryOwners.Count > 0)
            {
                bool gone = true;
                foreach (Entry owner in recoveryOwners)
                {
                    try { if (!os.Gone(owner.Pid, owner.Creation)) { gone = false; break; } }
                    catch { gone = false; break; }
                }
                if (gone) ownersExited = true;
            }
            return recoverySession + (ownersExited ? "|exited" : "|live");
        }
        internal bool RecoveryDue
        {
            get
            {
                string phase = RecoveryPhase(); RecoveryAttempt attempt;
                if (!recoveryAttempts.TryGetValue(phase, out attempt) || (attempt.Failures < 3 && RecoveryClock() >= attempt.Due)) return true;
                ReportRecovery(phase, attempt); return false;
            }
        }
        private bool RunRecovery(Func<bool> operation)
        {
            string phase = RecoveryPhase(); RecoveryAttempt attempt;
            if (!recoveryAttempts.TryGetValue(phase, out attempt)) recoveryAttempts[phase] = attempt = new RecoveryAttempt();
            if (attempt.Failures >= 3 || RecoveryClock() < attempt.Due) { ReportRecovery(phase, attempt); return false; }
            bool restored = false;
            try { restored = operation(); return restored; }
            finally
            {
                if (restored) recoveryAttempts.Clear();
                else
                {
                    attempt.Failures++; attempt.Due = RecoveryClock() + TimeSpan.FromSeconds(30).Ticks;
                    ReportRecovery(phase, attempt);
                    if (attempt.Failures == 3) Logger.Log("CORE isolation recovery paused after 3 failures; journal and owner retained");
                }
            }
        }
        private void ReportRecovery(string phase, RecoveryAttempt attempt)
        {
            if (attempt.Failures >= 3) RecoveryAttemptTrace.Exhausted(() => RecoveryPhase() != phase);
            else RecoveryAttemptTrace.DeferredFor(TimeSpan.FromTicks(Math.Max(0, attempt.Due - RecoveryClock())));
        }

		internal string Error { get; private set; }

		internal bool Active { get; private set; }

		internal bool Pending
		{
			get
			{
				return isolated != 0;
			}
		}

		internal CoreIsolationEngine(ICoreIsolationPlatform platform, ICoreIsolationStore journal)
		{
			os = platform;
			store = journal;
		}

		private bool Fail(string reason)
		{
			Error = reason;
			return false;
		}

		private static string Hex(ulong value)
		{
			return value.ToString("X", CultureInfo.InvariantCulture);
		}

		private bool Persist()
		{
			StringBuilder stringBuilder = new StringBuilder(manual ? "3|" : "2|").Append(boot).Append('|').Append(Hex(all))
				.Append('|')
				.Append(Hex(isolated))
				.Append('|')
				.Append(Hex(pre))
				.Append('|')
				.Append(systemPrepared ? '1' : '0');
			foreach (Entry entry in entries)
			{
				stringBuilder.Append('|').Append(entry.Pid).Append(',')
					.Append(entry.Creation)
					.Append(',')
					.Append(Hex(entry.Original))
					.Append(',')
					.Append(Hex(entry.Applied));
			}
			string receipt = stringBuilder.ToString(), verified;
            if (!store.Write(receipt)) return false;
            ownedReceipt = receipt;
            return store.Read(out verified) && verified == receipt;
		}

		internal bool Recover()
        { return RunRecovery(RecoverCore); }
        private bool RecoverCore()
        {
            if (Pending) return RestoreCore();
			string value;
			if (!store.Read(out value))
			{
				return Fail("journal-read");
			}
			if (string.IsNullOrEmpty(value))
			{
				return true;
			}
			if (!Decode(value))
			{
				return Fail("journal-corrupt");
			}
            string current;
            if (ownedReceipt != null && (!store.Read(out current) || current != ownedReceipt)) return Fail("journal-owner-changed");
			string bootIdentityResult = os.BootIdentity();
			if (bootIdentityResult == null)
			{
				return Fail("boot-read");
			}
			if (bootIdentityResult != boot)
			{
				if (!ClearReceipt())
				{
					return Fail("journal-clear");
				}
				Reset();
				return true;
			}
			return RestoreCore();
		}

		private bool Decode(string raw)
		{
			if (raw.Length > 32768 || Pending)
			{
				return false;
			}
			string[] rawParts = raw.Split('|');
			ulong v = 0uL;
			bool allConditionsMet = rawParts.Length > 0 && rawParts[0] == "3";
			int decodeValue = ((rawParts.Length > 0 && (rawParts[0] == "2" || allConditionsMet)) ? 6 : 5);
			Guid result;
			ulong v2;
			ulong v3;
			if (rawParts.Length < decodeValue || rawParts.Length > decodeValue + 256 || (rawParts[0] != "1" && rawParts[0] != "2" && !allConditionsMet) || rawParts[1].Length != 32 || !Guid.TryParseExact(rawParts[1], "N", out result) || result == Guid.Empty || !ParseHex(rawParts[2], out v2) || !ParseHex(rawParts[3], out v3) || (decodeValue == 6 && !ParseHex(rawParts[4], out v)) || (rawParts[decodeValue - 1] != "0" && rawParts[decodeValue - 1] != "1") || v2 == 0 || v3 == 0 || (v3 & ~v2) != 0 || (!allConditionsMet && (v3 & 1) != 0) || (allConditionsMet && v != 0) || (v & ~v2) != 0 || (v & v3) != 0)
			{
				return false;
			}
			List<Entry> list = new List<Entry>();
			HashSet<int> hashSet = new HashSet<int>();
			for (int i = decodeValue; i < rawParts.Length; i++)
			{
				string[] parts = rawParts[i].Split(',');
				int result2;
				long result3;
				ulong v4;
				ulong v5;
				if (parts.Length != 4 || !int.TryParse(parts[0], out result2) || result2 <= 0 || !hashSet.Add(result2) || !long.TryParse(parts[1], out result3) || result3 <= 0 || !ParseHex(parts[2], out v4) || !ParseHex(parts[3], out v5) || (v4 & ~v2) != 0 || (v5 & ~v2) != 0 || (v4 & ~v5) != 0 || (v5 & ~v4 & ~v3) != 0)
				{
					return false;
				}
				list.Add(new Entry
				{
					Pid = result2,
					Creation = result3,
					Original = v4,
					Applied = v5
				});
			}
			boot = rawParts[1];
			all = v2;
			isolated = v3;
			pre = v;
			manual = allConditionsMet;
			systemPrepared = rawParts[decodeValue - 1] == "1";
			entries.AddRange(list);
            recoveryOwners.AddRange(list); ownedReceipt = raw;
			return true;
		}

		private static bool ParseHex(string s, out ulong v)
		{
			v = 0uL;
			if (s.Length == 0 || s.Length > 16)
			{
				return false;
			}
			foreach (char character in s)
			{
				if (!Uri.IsHexDigit(character))
				{
					return false;
				}
			}
			return ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
		}

		internal bool Begin(ulong mask, int pid, long creation, ulong gameMask, bool manualPlan = false)
		{
			if (Pending)
			{
				return Fail("lease-pending");
			}
			IsolationCpuState isolationCpuState = os.ReadSystem(IntPtr.Zero);
			string bootIdentityResult = os.BootIdentity();
			if (isolationCpuState == null || bootIdentityResult == null)
			{
				return Fail("system-read");
			}
			if ((isolationCpuState.Allocated & mask) != 0 || (manualPlan && isolationCpuState.Allocated != 0))
			{
				return Fail("external-allocation");
			}
			if (AutoCores.ValidateRange(mask, gameMask, isolationCpuState.All, isolationCpuState.Physical, isolationCpuState.Allocated, manualPlan) != null)
			{
				return Fail("invalid-range");
			}
			boot = bootIdentityResult;
			all = isolationCpuState.All;
			isolated = mask;
			pre = isolationCpuState.Allocated;
			manual = manualPlan;
			if (!Persist())
			{
				Reset();
				return Fail("journal-write");
			}
			if (!Allow(pid, creation, gameMask))
			{
				Restore();
				return false;
			}
			isolationCpuState = os.ReadSystem(IntPtr.Zero);
			if (isolationCpuState == null || isolationCpuState.All != all || isolationCpuState.Allocated != pre)
			{
				Fail("system-state-changed");
				Restore();
				return false;
			}
			systemPrepared = true;
			if (!Persist())
			{
				systemPrepared = false;
				Fail("journal-write");
				Restore();
				return false;
			}
			bool setSystemAllowedSucceeded = os.SetSystemAllowed(all & ~isolated & ~pre);
			isolationCpuState = os.ReadSystem(IntPtr.Zero);
			if (!setSystemAllowedSucceeded || isolationCpuState == null || isolationCpuState.All != all || isolationCpuState.Allocated != (pre | isolated))
			{
				Fail("system-write-readback");
				Restore();
				return false;
			}
			Active = true;
			if (!Audit())
			{
				Restore();
				return false;
			}
			return true;
		}

		internal bool Allow(int pid, long creation, ulong gameMask)
		{
			if (!Pending || pid <= 0 || creation <= 0 || gameMask == 0 || (gameMask & ~all) != 0)
			{
				return Fail("invalid-process");
			}
			foreach (Entry entry3 in entries)
			{
				if (entry3.Pid == pid)
				{
					return (entry3.Creation == creation && (gameMask & isolated & ~entry3.Applied) == 0 && Verify(entry3)) || Fail("process-changed");
				}
			}
			if (entries.Count >= 256)
			{
				return Fail("process-limit");
			}
			IntPtr intPtr = os.Open(pid, creation);
			if (intPtr == IntPtr.Zero)
			{
				return Fail("process-access");
			}
			ulong mask;
			if (!os.ReadAllowed(intPtr, out mask) || (mask & ~all) != 0)
			{
				os.Close(intPtr);
				return Fail("process-read");
			}
			Entry entry = new Entry();
			entry.Pid = pid;
			entry.Creation = creation;
			entry.Handle = intPtr;
			entry.Original = mask;
			entry.Applied = mask | (gameMask & isolated);
			Entry entry2 = entry;
			entries.Add(entry2);
            recoveryOwners.Add(entry2);
			if (!Persist())
			{
				entries.Remove(entry2);
                recoveryOwners.Remove(entry2);
				os.Close(intPtr);
				return Fail("journal-write");
			}
			if (!os.SetAllowed(intPtr, entry2.Applied) || !Verify(entry2))
			{
				return Fail("admission-write-readback");
			}
			return true;
		}

		private bool Verify(Entry e)
		{
			ulong mask;
			if (e.Handle == IntPtr.Zero || os.Exited(e.Handle) || !os.ReadAllowed(e.Handle, out mask) || mask != e.Applied)
			{
				return false;
			}
			if (!Active)
			{
				return true;
			}
			IsolationCpuState isolationCpuState = os.ReadSystem(e.Handle);
			if (isolationCpuState != null && isolationCpuState.All == all && isolationCpuState.Allocated == (pre | isolated))
			{
				return (isolationCpuState.Admitted & isolated) == (e.Applied & isolated);
			}
			return false;
		}

		internal bool Audit()
		{
			if (!Active)
			{
				return Fail("inactive");
			}
			IsolationCpuState isolationCpuState = os.ReadSystem(IntPtr.Zero);
			if (isolationCpuState == null || isolationCpuState.All != all || isolationCpuState.Allocated != (pre | isolated))
			{
				return Fail("system-state-changed");
			}
			bool auditReady = false;
			for (int entriesIndex = entries.Count - 1; entriesIndex >= 0; entriesIndex--)
			{
				Entry entry = entries[entriesIndex];
				if (os.Exited(entry.Handle))
				{
					os.Close(entry.Handle);
					entries.RemoveAt(entriesIndex);
					auditReady = true;
				}
				else if (!Verify(entry))
				{
					return Fail("admission-state-changed");
				}
			}
			if (auditReady && !Persist())
			{
				return Fail("journal-write");
			}
			return true;
		}

		internal bool Drop(int pid, long creation)
		{
			for (int i = 0; i < entries.Count; i++)
			{
				Entry entry = entries[i];
				if (entry.Pid == pid && entry.Creation == creation)
				{
					if (!RestoreEntry(entry))
					{
						return Fail("process-restore");
					}
					os.Close(entry.Handle);
					entries.RemoveAt(i);
					if (!Persist())
					{
						return Fail("journal-write");
					}
					return true;
				}
			}
			return true;
		}

		private bool RestoreEntry(Entry e)
		{
			if (e.Handle == IntPtr.Zero)
			{
				e.Handle = os.Open(e.Pid, e.Creation);
				if (e.Handle == IntPtr.Zero)
				{
					return os.Gone(e.Pid, e.Creation);
				}
			}
			if (os.Exited(e.Handle))
			{
				return true;
			}
			ulong mask;
			if (!os.ReadAllowed(e.Handle, out mask))
			{
				return false;
			}
			if (mask == e.Original)
			{
				return true;
			}
			if (mask != e.Applied)
			{
				return false;
			}
			if (os.SetAllowed(e.Handle, e.Original) && os.ReadAllowed(e.Handle, out mask))
			{
				return mask == e.Original;
			}
			return false;
		}

		internal bool Restore()
        { return !Pending || RunRecovery(RestoreCore); }
        private bool RestoreCore()
        {
			Active = false;
			if (!Pending)
			{
				return true;
			}
            string current;
            if (ownedReceipt != null && (!store.Read(out current) || current != ownedReceipt)) return Fail("journal-owner-changed");
			string bootIdentityResult = os.BootIdentity();
			if (bootIdentityResult == null)
			{
				return Fail("boot-read");
			}
			if (bootIdentityResult != boot)
			{
				if (!ClearReceipt())
				{
					return Fail("journal-clear");
				}
				Reset();
				return true;
			}
			bool isNotSystemPreparedSnapshot = !systemPrepared;
			IsolationCpuState isolationCpuState = os.ReadSystem(IntPtr.Zero);
			if (systemPrepared && isolationCpuState != null && isolationCpuState.All == all)
			{
				isNotSystemPreparedSnapshot = (isolationCpuState.Allocated & isolated) == 0;
				bool restoreCoreReady = true;
				foreach (Entry entry in entries)
				{
					if (entry.Handle == IntPtr.Zero)
					{
						entry.Handle = os.Open(entry.Pid, entry.Creation);
					}
					ulong mask;
					if (entry.Handle == IntPtr.Zero || os.Exited(entry.Handle) || !os.ReadAllowed(entry.Handle, out mask) || mask != entry.Applied)
					{
						restoreCoreReady = false;
					}
				}
				if ((isolationCpuState.Allocated & isolated) == isolated && (isolationCpuState.Allocated == (pre | isolated) || (restoreCoreReady && entries.Count > 0)))
				{
					ulong allocatedMask = isolationCpuState.Allocated & ~isolated;
					bool setSystemAllowedSucceeded = os.SetSystemAllowed(all & ~allocatedMask);
					isolationCpuState = os.ReadSystem(IntPtr.Zero);
					isNotSystemPreparedSnapshot = setSystemAllowedSucceeded && isolationCpuState != null && isolationCpuState.All == all && isolationCpuState.Allocated == allocatedMask;
				}
			}
			if (!isNotSystemPreparedSnapshot)
			{
				return Fail("restore-pending");
			}
			bool restoreCoreReady2 = true;
			foreach (Entry entry2 in entries)
			{
				if (!RestoreEntry(entry2))
				{
					restoreCoreReady2 = false;
				}
			}
			if (!isNotSystemPreparedSnapshot || !restoreCoreReady2)
			{
				return Fail("restore-pending");
			}
			if (!ClearReceipt())
			{
				return Fail("journal-clear");
			}
			Reset();
			return true;
		}

        private bool ClearReceipt()
        { string verify; return store.Write("") && store.Read(out verify) && string.IsNullOrEmpty(verify); }

		private void Reset()
		{
			foreach (Entry entry in entries)
			{
				os.Close(entry.Handle);
			}
			entries.Clear();
            recoveryOwners.Clear(); ownedReceipt = null; ownersExited = false; recoveryAttempts.Clear();
			isolated = (all = (pre = 0uL));
			boot = null;
			Active = (systemPrepared = (manual = false));
		}

        public void Dispose()
        {
            // Restoration belongs to the finite owner loop. Dispose must neither
            // perform another attempt nor drop handles for unresolved mutations.
            if (!Pending) Reset();
        }
    }
}
