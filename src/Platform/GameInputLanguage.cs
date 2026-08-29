// A single, bounded Windows input-language request. No layout is installed or
// activated in Pavise, and no synthetic key, injection or private IME message is used.
using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal struct GameInputProcess : IEquatable<GameInputProcess>
    {
        internal readonly int Pid;
        internal readonly long Creation;
        internal GameInputProcess(int pid, long creation) { Pid = pid; Creation = creation; }
        internal bool IsValid { get { return Pid > 0 && Creation > 0; } }
        public bool Equals(GameInputProcess other) { return Pid == other.Pid && Creation == other.Creation; }
        public override bool Equals(object other) { return other is GameInputProcess && Equals((GameInputProcess)other); }
        public override int GetHashCode() { return Pid ^ Creation.GetHashCode(); }
    }

    internal sealed class GameInputWindow
    {
        internal readonly IntPtr Foreground, Focus;
        internal readonly uint ThreadId;
        internal readonly int Pid;
        internal GameInputWindow(IntPtr foreground, IntPtr focus, uint threadId, int pid)
        { Foreground = foreground; Focus = focus; ThreadId = threadId; Pid = pid; }
        internal bool SameAs(GameInputWindow other)
        {
            return other != null && Foreground == other.Foreground && Focus == other.Focus
                && ThreadId == other.ThreadId && Pid == other.Pid;
        }
    }

    internal enum GameInputProcessState { Alive, Gone, Unknown }
    internal enum GameInputSendResult { NotIssued, Processed, Failed }
    internal enum EnglishInputResult
    {
        WaitingForGameWindow, AlreadyEnglish, Changed, Unconfirmed, Denied,
        Unavailable, NoEnglishLayout, TargetChanged, Expired, Canceled, HistoryFull
    }

    internal sealed class EnglishInputOutcome
    {
        internal readonly EnglishInputResult Result;
        internal readonly bool RequestAttempted;
        internal readonly int Error;
        internal EnglishInputOutcome(EnglishInputResult result) : this(result, false, 0) { }
        internal EnglishInputOutcome(EnglishInputResult result, bool attempted, int error)
        { Result = result; RequestAttempted = attempted; Error = error; }
    }

    internal interface IGameInputProcessLease : IDisposable
    {
        long Creation { get; }
        bool IsAlive { get; }
    }

    // Every native operation is injectable. Isolated tests never call user32/imm32.
    internal interface IGameInputLanguageApi
    {
        int CurrentProcessId { get; }
        bool TryGetForeground(out GameInputWindow window);
        IGameInputProcessLease OpenProcess(int pid, out bool knownGone);
        IntPtr[] GetKeyboardLayouts();
        IntPtr GetKeyboardLayout(uint threadId);
        bool IsIme(IntPtr layout);
        GameInputSendResult SendRequest(GameInputWindow window, IntPtr layout,
            uint timeoutMs, Func<bool> mayContinue, out int error);
    }

    internal interface IGameInputLanguage
    {
        EnglishInputOutcome TryOnce(GameInputProcess process, Func<bool> claim, Func<bool> mayContinue);
        GameInputProcessState Probe(GameInputProcess process);
    }

    internal sealed class GameInputLanguage : IGameInputLanguage
    {
        internal const uint RequestTimeoutMs = 500;
        private readonly IGameInputLanguageApi api;
        internal GameInputLanguage() : this(new WindowsGameInputLanguageApi()) { }
        internal GameInputLanguage(IGameInputLanguageApi value)
        { if (value == null) throw new ArgumentNullException("value"); api = value; }

        private static bool Allowed(Func<bool> check) { return check != null && check(); }

        private bool IsIndependentEnglish(IntPtr layout)
        {
            // PRIMARYLANGID, not a hard-coded US layout. Transient TSF LANGIDs
            // and an IME's English conversion mode cannot prove this condition.
            return layout != IntPtr.Zero && (layout.ToInt64() & 0x3ff) == 0x09 && !api.IsIme(layout);
        }

        private bool SameTarget(GameInputProcess process, IGameInputProcessLease lease,
            GameInputWindow window, Func<bool> mayContinue)
        {
            GameInputWindow current;
            return Allowed(mayContinue) && lease.Creation == process.Creation && lease.IsAlive
                && api.TryGetForeground(out current) && window.SameAs(current)
                && current.Pid == process.Pid;
        }

        public EnglishInputOutcome TryOnce(GameInputProcess process, Func<bool> claim, Func<bool> mayContinue)
        {
            bool attempted = false;
            try
            {
                if (!Allowed(mayContinue)) return new EnglishInputOutcome(EnglishInputResult.Canceled);
                if (!process.IsValid || process.Pid == api.CurrentProcessId)
                    return new EnglishInputOutcome(EnglishInputResult.Unavailable);
                GameInputWindow window;
                if (!api.TryGetForeground(out window) || window == null || window.Pid != process.Pid)
                    return new EnglishInputOutcome(EnglishInputResult.WaitingForGameWindow);
                bool knownGone;
                using (IGameInputProcessLease lease = api.OpenProcess(process.Pid, out knownGone))
                {
                    if (lease == null) return new EnglishInputOutcome(knownGone
                        ? EnglishInputResult.TargetChanged : EnglishInputResult.Unavailable);
                    if (!SameTarget(process, lease, window, mayContinue))
                        return new EnglishInputOutcome(EnglishInputResult.TargetChanged);
                    // Consume before inspecting/changing input: a refusal, missing
                    // layout or an already-English game never grants a second try.
                    if (!Allowed(claim)) return new EnglishInputOutcome(EnglishInputResult.Canceled);
                    IntPtr initial = api.GetKeyboardLayout(window.ThreadId);
                    if (initial == IntPtr.Zero) return new EnglishInputOutcome(EnglishInputResult.Unavailable);
                    if (IsIndependentEnglish(initial)) return new EnglishInputOutcome(EnglishInputResult.AlreadyEnglish);
                    IntPtr[] layouts = api.GetKeyboardLayouts();
                    if (layouts == null) return new EnglishInputOutcome(EnglishInputResult.Unavailable);
                    IntPtr english = IntPtr.Zero;
                    foreach (IntPtr layout in layouts)
                        if (IsIndependentEnglish(layout)) { english = layout; break; }
                    if (english == IntPtr.Zero) return new EnglishInputOutcome(EnglishInputResult.NoEnglishLayout);
                    if (!SameTarget(process, lease, window, mayContinue))
                        return new EnglishInputOutcome(EnglishInputResult.TargetChanged);
                    // A manual change during preparation wins, including a change
                    // to another non-English layout. Never correct it afterwards.
                    IntPtr beforeSend = api.GetKeyboardLayout(window.ThreadId);
                    if (beforeSend != initial)
                        return new EnglishInputOutcome(IsIndependentEnglish(beforeSend)
                            ? EnglishInputResult.AlreadyEnglish : EnglishInputResult.TargetChanged);
                    Func<bool> finalCheck = delegate
                    {
                        return SameTarget(process, lease, window, mayContinue)
                            && api.GetKeyboardLayout(window.ThreadId) == initial;
                    };
                    int error;
                    attempted = true; // If a native call throws, delivery is uncertain.
                    GameInputSendResult sent = api.SendRequest(window, english, RequestTimeoutMs, finalCheck, out error);
                    if (sent == GameInputSendResult.NotIssued)
                        return new EnglishInputOutcome(EnglishInputResult.Canceled);
                    if (sent == GameInputSendResult.Failed)
                        return new EnglishInputOutcome(error == 5 ? EnglishInputResult.Denied
                            : EnglishInputResult.Unavailable, true, error);
                    // Message completion is not application acceptance. Read once,
                    // only for the same target; never poll or restore the layout.
                    if (!SameTarget(process, lease, window, mayContinue))
                        return new EnglishInputOutcome(EnglishInputResult.Unconfirmed, true, 0);
                    bool changed = api.GetKeyboardLayout(window.ThreadId) == english;
                    return new EnglishInputOutcome(changed ? EnglishInputResult.Changed
                        : EnglishInputResult.Unconfirmed, true, 0);
                }
            }
            catch { return new EnglishInputOutcome(EnglishInputResult.Unavailable, attempted, 0); }
        }

        public GameInputProcessState Probe(GameInputProcess process)
        {
            if (!process.IsValid) return GameInputProcessState.Gone;
            try
            {
                bool knownGone;
                using (IGameInputProcessLease lease = api.OpenProcess(process.Pid, out knownGone))
                {
                    if (lease == null) return knownGone ? GameInputProcessState.Gone : GameInputProcessState.Unknown;
                    return lease.Creation != process.Creation || !lease.IsAlive
                        ? GameInputProcessState.Gone : GameInputProcessState.Alive;
                }
            }
            catch { return GameInputProcessState.Unknown; }
        }
    }

    internal sealed class WindowsGameInputLanguageApi : IGameInputLanguageApi
    {
        private const uint QueryLimited = 0x1000, Synchronize = 0x00100000;
        private const uint WmInputLanguageChangeRequest = 0x0050;
        private const uint SendFlags = 0x0001 | 0x0002 | 0x0020; // BLOCK | ABORTIFHUNG | ERRORONEXIT
        public int CurrentProcessId
        {
            get { RefuseNativeInTests(); return unchecked((int)GetCurrentProcessId()); }
        }

        public bool TryGetForeground(out GameInputWindow window)
        {
            RefuseNativeInTests();
            window = null;
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || !IsWindow(foreground) || !IsWindowVisible(foreground)
                || IsIconic(foreground)) return false;
            uint pid;
            uint thread = GetWindowThreadProcessId(foreground, out pid);
            if (thread == 0 || pid == 0) return false;
            GuiThreadInfo info = new GuiThreadInfo();
            info.Size = (uint)Marshal.SizeOf(typeof(GuiThreadInfo));
            if (!GetGUIThreadInfo(thread, ref info) || info.Focus == IntPtr.Zero
                || !IsWindow(info.Focus) || GetAncestor(info.Focus, 2) != foreground) return false;
            uint focusPid;
            uint focusThread = GetWindowThreadProcessId(info.Focus, out focusPid);
            if (focusThread != thread || focusPid != pid || GetForegroundWindow() != foreground) return false;
            window = new GameInputWindow(foreground, info.Focus, thread, unchecked((int)pid));
            return true;
        }

        public IGameInputProcessLease OpenProcess(int pid, out bool knownGone)
        {
            RefuseNativeInTests();
            knownGone = false;
            IntPtr handle = OpenProcessNative(QueryLimited | Synchronize, false, unchecked((uint)pid));
            if (handle == IntPtr.Zero)
            {
                knownGone = Marshal.GetLastWin32Error() == 87; // Invalid PID, not access denied.
                return null;
            }
            try
            {
                NativeFileTime creation, exit, kernel, user;
                if (!GetProcessTimes(handle, out creation, out exit, out kernel, out user)) return null;
                long birth = unchecked((long)(((ulong)creation.High << 32) | creation.Low));
                if (birth <= 0) return null;
                ProcessLease lease = new ProcessLease(handle, birth);
                handle = IntPtr.Zero;
                return lease;
            }
            finally { if (handle != IntPtr.Zero) CloseHandle(handle); }
        }

        public IntPtr[] GetKeyboardLayouts()
        {
            RefuseNativeInTests();
            int count = GetKeyboardLayoutList(0, null);
            if (count <= 0) return count == 0 ? new IntPtr[0] : null;
            if (count > 512) return null;
            IntPtr[] layouts = new IntPtr[count];
            int read = GetKeyboardLayoutList(layouts.Length, layouts);
            if (read <= 0 || read > layouts.Length) return null;
            if (read != layouts.Length) Array.Resize(ref layouts, read);
            return layouts;
        }

        public IntPtr GetKeyboardLayout(uint threadId)
        { RefuseNativeInTests(); return GetKeyboardLayoutNative(threadId); }
        public bool IsIme(IntPtr layout) { RefuseNativeInTests(); return ImmIsIME(layout); }
        public GameInputSendResult SendRequest(GameInputWindow window, IntPtr layout,
            uint timeoutMs, Func<bool> mayContinue, out int error)
        {
            RefuseNativeInTests();
            error = 0;
            if (mayContinue == null || !mayContinue()) return GameInputSendResult.NotIssued;
            UIntPtr result;
            SetLastError(0);
            // Windows/app input sharing is left unchanged. A timeout cannot retract
            // a message already being processed; it must NEVER cause a retry.
            IntPtr sent = SendMessageTimeout(window.Focus, WmInputLanguageChangeRequest,
                UIntPtr.Zero, layout, SendFlags, timeoutMs, out result);
            if (sent != IntPtr.Zero) return GameInputSendResult.Processed;
            error = Marshal.GetLastWin32Error();
            return GameInputSendResult.Failed;
        }

        private static void RefuseNativeInTests()
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            throw new InvalidOperationException("Game input native access requires an injected test double.");
#endif
        }

        private sealed class ProcessLease : IGameInputProcessLease
        {
            private IntPtr handle;
            private readonly long creation;
            internal ProcessLease(IntPtr value, long birth) { handle = value; creation = birth; }
            public long Creation { get { return creation; } }
            public bool IsAlive
            {
                get
                {
                    if (handle == IntPtr.Zero) throw new ObjectDisposedException("ProcessLease");
                    uint wait = WaitForSingleObject(handle, 0);
                    if (wait == 258) return true;
                    if (wait == 0) return false;
                    // An unreadable process is not evidence that an old runtime
                    // exited; retain its one-shot history on uncertain probes.
                    throw new InvalidOperationException("Process lifetime could not be verified");
                }
            }
            public void Dispose()
            {
                IntPtr old = handle; handle = IntPtr.Zero;
                if (old != IntPtr.Zero) CloseHandle(old);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFileTime { internal uint Low, High; }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct GuiThreadInfo
        {
            internal uint Size, Flags;
            internal IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
            internal NativeRect CaretRect;
        }
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
        [DllImport("user32.dll")] private static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[] layouts);
        [DllImport("user32.dll", EntryPoint = "GetKeyboardLayout")] private static extern IntPtr GetKeyboardLayoutNative(uint thread);
        [DllImport("imm32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ImmIsIME(IntPtr layout);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam,
            IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
        [DllImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
        private static extern IntPtr OpenProcessNative(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(IntPtr handle, out NativeFileTime creation, out NativeFileTime exit,
            out NativeFileTime kernel, out NativeFileTime user);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern void SetLastError(uint error);
    }
}
