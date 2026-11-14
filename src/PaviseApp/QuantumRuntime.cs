using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal enum QuantumRuntimeState { Applied, Unavailable, Failed, Uncertain }

    internal sealed class QuantumRuntimeResult
    {
        internal readonly QuantumRuntimeState State;
        internal readonly int Status;
        internal readonly string Detail;
        internal readonly bool UsedSystemTask;
        internal bool Applied { get { return State == QuantumRuntimeState.Applied; } }

        internal QuantumRuntimeResult(QuantumRuntimeState state, int status, string detail, bool usedSystemTask = false)
        {
            State = state; Status = status; Detail = detail ?? ""; UsedSystemTask = usedSystemTask;
        }
    }

    internal static partial class QuantumRuntime
    {
        // 按 PHNT ntexapi.h 使用 SystemPrioritySeparation=39，需 SeTcbPrivilege；成功仅确认内核接受更新，无公开读回接口。

        private const int PrivilegeNotHeld = unchecked((int)0xc0000061);
        private const int AccessDenied = unchecked((int)0xc0000022);
        private const int NotAllAssigned = 0x106;
        private static readonly object gate = new object();

#if PAVISE_SELFTEST
        internal static Func<uint, QuantumRuntimeResult> DirectForTest, SystemTaskForTest;
        internal static Func<bool> SupportedForTest;
#endif

        [DllImport("ntdll.dll", ExactSpelling = true)]
        private static extern int NtSetSystemInformation(int informationClass, ref uint information, uint length);

        [DllImport("ntdll.dll", ExactSpelling = true)]
        private static extern int RtlAdjustPrivilege(uint privilege, [MarshalAs(UnmanagedType.U1)] bool enable,
            [MarshalAs(UnmanagedType.U1)] bool currentThread, [MarshalAs(UnmanagedType.U1)] out bool previous);

        internal static QuantumRuntimeResult TryApply(uint raw)
        {
            lock (gate)
            {
                try
                {
                    bool supported;
#if PAVISE_SELFTEST
                    supported = SupportedForTest != null && SupportedForTest();
#else
                    supported = Environment.Is64BitOperatingSystem && Native.OsBuild() >= 19041;
#endif
                    if (!supported) return new QuantumRuntimeResult(QuantumRuntimeState.Unavailable, 0, "Online quantum update is unavailable on this platform");
                    uint value = raw & 63u;
                    QuantumRuntimeResult direct;
#if PAVISE_SELFTEST
                    if (DirectForTest == null) return new QuantumRuntimeResult(QuantumRuntimeState.Unavailable, 0, "Native updates are disabled in self-tests");
                    direct = DirectForTest(value);
#else
                    // 已超时的旧辅助进程必须终结后，才能执行新的更新。
                    if (!ReapOwnedTasks()) return new QuantumRuntimeResult(QuantumRuntimeState.Uncertain, 0, "Previous online quantum helper has not terminated", true);
                    direct = ApplyDirect(value);
#endif
                    if (direct == null) return new QuantumRuntimeResult(QuantumRuntimeState.Failed, 0, "Online quantum update returned no result");
                    if (direct.Applied || direct.Status != PrivilegeNotHeld && direct.Status != AccessDenied && direct.Status != NotAllAssigned) return direct;
#if PAVISE_SELFTEST
                    return SystemTaskForTest == null
                        ? new QuantumRuntimeResult(QuantumRuntimeState.Unavailable, direct.Status, "SYSTEM helper is disabled in self-tests")
                        : SystemTaskForTest(value) ?? new QuantumRuntimeResult(QuantumRuntimeState.Failed, 0, "SYSTEM helper returned no result", true);
#else
                    return ApplyViaSystemTask(value);
#endif
                }
                catch (Exception error)
                {
                    return new QuantumRuntimeResult(QuantumRuntimeState.Failed, 0, error.GetType().Name + ": " + error.Message);
                }
            }
        }

        private static QuantumRuntimeResult ApplyDirect(uint value)
        {
            bool previous;
            int privilege = RtlAdjustPrivilege(7, true, false, out previous); // 所需权限：SeTcbPrivilege。
            if (privilege != 0) return new QuantumRuntimeResult(QuantumRuntimeState.Unavailable, privilege, "SeTcbPrivilege is unavailable");
            try { return DecodeStatus(NtSetSystemInformation(39, ref value, 4), false); }
            finally
            {
                bool ignored;
                if (RtlAdjustPrivilege(7, previous, false, out ignored) != 0)
                    throw new InvalidOperationException("Could not restore the quantum update privilege state");
            }
        }

        internal static QuantumRuntimeResult DecodeStatus(int status, bool systemTask)
        {
            // 状态仅表示内核是否接受请求。
            return new QuantumRuntimeResult(status == 0 ? QuantumRuntimeState.Applied : QuantumRuntimeState.Failed,
                status, status == 0 ? "Kernel accepted the online quantum update"
                    : "Online quantum update returned NTSTATUS 0x" + unchecked((uint)status).ToString("X8", CultureInfo.InvariantCulture), systemTask);
        }
    }
}
