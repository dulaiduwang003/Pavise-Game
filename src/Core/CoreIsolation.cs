using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PaviseApp
{
    internal interface ICoreIsolationStore
    {
        bool Read(out string value);
        bool Write(string value);
    }

    internal sealed class CoreIsolationSettingsStore : ICoreIsolationStore
    {
        internal const string Key = "Crash_CoreIsolationV1";
        public bool Read(out string value) { return Settings.TryLoadStr(Key, out value); }
        public bool Write(string value) { return Settings.SaveStrDurable(Key, value); }
    }

    // Single owner: the isolated worker owns the system lease and retained process
    // handles. It restores even when the UI/normal runtime crashes or is killed.
    internal sealed class CoreIsolationEngine : IDisposable
    {
        private sealed class Entry
        {
            internal int Pid;
            internal long Creation;
            internal ulong Original, Applied;
            internal IntPtr Handle;
        }
        private readonly ICoreIsolationPlatform os;
        private readonly ICoreIsolationStore store;
        private readonly List<Entry> entries = new List<Entry>();
        private string boot;
        private ulong all, isolated;
        private bool systemPrepared;
        internal string Error { get; private set; }
        internal bool Active { get; private set; }
        internal bool Pending { get { return isolated != 0; } }

        internal CoreIsolationEngine(ICoreIsolationPlatform platform, ICoreIsolationStore journal)
        { os = platform; store = journal; }

        private bool Fail(string reason) { Error = reason; return false; }
        private static string Hex(ulong value) { return value.ToString("X", CultureInfo.InvariantCulture); }
        private bool Persist()
        {
            var s = new StringBuilder("1|").Append(boot).Append('|').Append(Hex(all)).Append('|').Append(Hex(isolated))
                .Append('|').Append(systemPrepared ? '1' : '0');
            foreach (Entry e in entries) s.Append('|').Append(e.Pid).Append(',').Append(e.Creation)
                .Append(',').Append(Hex(e.Original)).Append(',').Append(Hex(e.Applied));
            return store.Write(s.ToString());
        }

        // Recover is called only while holding the machine-wide lease mutex.
        // System masks do not survive reboot: a different boot ID discards the
        // receipt without writing masks or targeting recycled process IDs.
        internal bool Recover()
        {
            string raw;
            if (!store.Read(out raw)) return Fail("journal-read");
            if (string.IsNullOrEmpty(raw)) return true;
            if (!Decode(raw)) return Fail("journal-corrupt");
            string currentBoot = os.BootIdentity();
            if (currentBoot == null) return Fail("boot-read");
            if (currentBoot != boot)
            {
                if (!store.Write("")) return Fail("journal-clear");
                Reset(); return true;
            }
            return Restore();
        }

        private bool Decode(string raw)
        {
            if (raw.Length > 32768 || Pending) return false;
            string[] p = raw.Split('|'); Guid id; ulong a, iso;
            if (p.Length < 5 || p.Length > 261 || p[0] != "1" || p[1].Length != 32
                || !Guid.TryParseExact(p[1], "N", out id) || id == Guid.Empty
                || !ParseHex(p[2], out a) || !ParseHex(p[3], out iso)
                || p[4] != "0" && p[4] != "1"
                || a == 0 || iso == 0 || (iso & ~a) != 0 || (iso & 1) != 0) return false;
            var parsed = new List<Entry>(); var pids = new HashSet<int>();
            for (int i = 5; i < p.Length; i++)
            {
                string[] bits = p[i].Split(','); int pid; long creation; ulong original, applied;
                if (bits.Length != 4 || !int.TryParse(bits[0], out pid) || pid <= 0 || !pids.Add(pid)
                    || !long.TryParse(bits[1], out creation) || creation <= 0
                    || !ParseHex(bits[2], out original) || !ParseHex(bits[3], out applied)
                    || (original & ~a) != 0 || (applied & ~a) != 0
                    || (original & ~applied) != 0 || (applied & ~original & ~iso) != 0) return false;
                parsed.Add(new Entry { Pid = pid, Creation = creation, Original = original, Applied = applied });
            }
            boot = p[1]; all = a; isolated = iso; systemPrepared = p[4] == "1"; entries.AddRange(parsed); return true;
        }
        private static bool ParseHex(string s, out ulong v)
        {
            v = 0;
            if (s.Length == 0 || s.Length > 16) return false;
            foreach (char c in s) if (!Uri.IsHexDigit(c)) return false;
            return ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
        }

        internal bool Begin(ulong mask, int pid, long creation, ulong gameMask)
        {
            if (Pending) return Fail("lease-pending");
            IsolationCpuState state = os.ReadSystem(IntPtr.Zero);
            string currentBoot = os.BootIdentity();
            if (state == null || currentBoot == null) return Fail("system-read");
            var plan = new CoreSchedulingPlan { GameMask = gameMask, IsolationOn = true, IsolationMask = mask,
                Topology = CoreScheduling.Stamp(state.All, state.Physical) };
            if (CoreScheduling.Validate(plan, state.All, state.Physical, false, true) != null) return Fail("invalid-range");
            // Existing allocations can be owned by another tool or the OS. Never
            // treat them as ours or reset them to the full machine mask.
            if (state.Allocated != 0) return Fail("external-allocation");
            boot = currentBoot; all = state.All; isolated = mask;
            if (!Persist()) { Reset(); return Fail("journal-write"); }
            if (!Allow(pid, creation, gameMask)) { Restore(); return false; }
            // Admission can take time. Check the global baseline again before
            // preparing the system write, so a failed grant cannot reset a mask
            // installed meanwhile by a different owner.
            state = os.ReadSystem(IntPtr.Zero);
            if (state == null || state.All != all || state.Allocated != 0)
            { Fail("system-state-changed"); Restore(); return false; }
            systemPrepared = true;
            if (!Persist()) { systemPrepared = false; Fail("journal-write"); Restore(); return false; }
            bool wrote = os.SetSystemAllowed(all & ~isolated);
            state = os.ReadSystem(IntPtr.Zero);
            if (!wrote || state == null || state.All != all || state.Allocated != isolated)
            { Fail("system-write-readback"); Restore(); return false; }
            Active = true;
            if (!Audit()) { Restore(); return false; }
            return true;
        }

        internal bool Allow(int pid, long creation, ulong gameMask)
        {
            if (!Pending || pid <= 0 || creation <= 0 || gameMask == 0 || (gameMask & ~all) != 0)
                return Fail("invalid-process");
            foreach (Entry old in entries)
                if (old.Pid == pid)
                    return old.Creation == creation && (gameMask & isolated & ~old.Applied) == 0
                        && Verify(old) ? true : Fail("process-changed");
            if (entries.Count >= 256) return Fail("process-limit");
            IntPtr handle = os.Open(pid, creation);
            if (handle == IntPtr.Zero) return Fail("process-access");
            ulong original;
            if (!os.ReadAllowed(handle, out original) || (original & ~all) != 0)
            { os.Close(handle); return Fail("process-read"); }
            var e = new Entry { Pid = pid, Creation = creation, Handle = handle,
                Original = original, Applied = original | (gameMask & isolated) };
            entries.Add(e);
            if (!Persist()) { entries.Remove(e); os.Close(handle); return Fail("journal-write"); }
            if (!os.SetAllowed(handle, e.Applied) || !Verify(e)) return Fail("admission-write-readback");
            return true;
        }

        private bool Verify(Entry e)
        {
            ulong current;
            if (e.Handle == IntPtr.Zero || os.Exited(e.Handle) || !os.ReadAllowed(e.Handle, out current)
                || current != e.Applied) return false;
            if (!Active) return true;
            IsolationCpuState state = os.ReadSystem(e.Handle);
            return state != null && state.All == all && state.Allocated == isolated
                && (state.Admitted & isolated) == (e.Applied & isolated);
        }

        internal bool Audit()
        {
            if (!Active) return Fail("inactive");
            IsolationCpuState state = os.ReadSystem(IntPtr.Zero);
            if (state == null || state.All != all || state.Allocated != isolated) return Fail("system-state-changed");
            bool removed = false;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                Entry e = entries[i];
                if (os.Exited(e.Handle)) { os.Close(e.Handle); entries.RemoveAt(i); removed = true; }
                else if (!Verify(e)) return Fail("admission-state-changed");
            }
            return !removed || Persist() || Fail("journal-write");
        }

        internal bool Drop(int pid, long creation)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                if (e.Pid != pid || e.Creation != creation) continue;
                if (!RestoreEntry(e)) return Fail("process-restore");
                os.Close(e.Handle); entries.RemoveAt(i);
                return Persist() || Fail("journal-write");
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
                    // Open failure alone does not prove exit; a query handle can
                    // distinguish PID reuse from a temporarily protected process.
                    return os.Gone(e.Pid, e.Creation);
                }
            }
            if (os.Exited(e.Handle)) return true;
            ulong now;
            if (!os.ReadAllowed(e.Handle, out now)) return false;
            if (now == e.Original) return true;
            if (now != e.Applied) return false;
            return os.SetAllowed(e.Handle, e.Original) && os.ReadAllowed(e.Handle, out now) && now == e.Original;
        }

        internal bool Restore()
        {
            Active = false;
            if (!Pending) return true;
            string currentBoot = os.BootIdentity();
            if (currentBoot == null) return Fail("boot-read");
            if (currentBoot != boot)
            {
                if (!store.Write("")) return Fail("journal-clear");
                Reset(); return true;
            }
            bool globalRestored = !systemPrepared;
            IsolationCpuState state = os.ReadSystem(IntPtr.Zero);
            if (systemPrepared && state != null && state.All == all)
            {
                globalRestored = state.Allocated == 0;
                if (state.Allocated == isolated)
                {
                    bool wrote = os.SetSystemAllowed(all);
                    state = os.ReadSystem(IntPtr.Zero);
                    globalRestored = wrote && state != null && state.All == all && state.Allocated == 0;
                }
            }
            bool processesRestored = true;
            foreach (Entry e in entries) if (!RestoreEntry(e)) processesRestored = false;
            if (!globalRestored || !processesRestored) return Fail("restore-pending");
            if (!store.Write("")) return Fail("journal-clear");
            Reset(); return true;
        }

        private void Reset()
        {
            foreach (Entry e in entries) os.Close(e.Handle);
            entries.Clear(); isolated = all = 0; boot = null; Active = systemPrepared = false;
        }
        public void Dispose() { try { Restore(); } finally { Reset(); } }
    }
}
