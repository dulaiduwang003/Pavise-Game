using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace PaviseApp
{
    // 核验本地 WMI 及跨会话创建的作业继承边界。
    // 父 PID 不替代创建回执；此核验不枚举全部祖先作业或识别 DAM。
    internal sealed class HardThreadGuardianBrokerProof
    {
        internal bool LocalCreateSucceeded;
        internal int ReceiptPid, ParentPid, ChildPid, CreatorPid, ActualCreatorPid;
        internal int ParentSession = -1, ChildSession = -1, CreatorSession = -1;
        internal long RequestCreation, ParentCreation, ChildCreation, CreatorCreation;
        internal string CreatorPath, SystemProviderPath, SystemProviderPath32, CreatorSid;

        internal static string Validate(HardThreadGuardianBrokerProof value)
        {
            if (value == null || !value.LocalCreateSucceeded || value.ReceiptPid <= 0 || value.ReceiptPid != value.ChildPid)
                return "本地 WMI 成功创建回执与守护身份不符";
            if (value.ParentPid <= 0 || value.ParentCreation <= 0 || value.ChildPid <= 0 || value.ChildCreation <= 0
                || value.RequestCreation <= 0 || value.ChildCreation < value.RequestCreation || value.ChildCreation <= value.ParentCreation)
                return "调用方或守护创建时间不符";
            if (value.ParentSession <= 0 || value.ChildSession != value.ParentSession)
                return "守护与原调用方不在同一交互会话";
            if (value.CreatorPid <= 0 || value.CreatorPid != value.ActualCreatorPid || value.CreatorCreation <= 0
                || value.CreatorCreation >= value.ChildCreation || value.CreatorSession != 0)
                return "WMI 提供程序身份、创建时间或会话不符";
            if (string.IsNullOrEmpty(value.CreatorPath)
                || !((!string.IsNullOrEmpty(value.SystemProviderPath) && string.Equals(value.CreatorPath, value.SystemProviderPath, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrEmpty(value.SystemProviderPath32) && string.Equals(value.CreatorPath, value.SystemProviderPath32, StringComparison.OrdinalIgnoreCase))))
                return "WMI 提供程序不是系统目录中的 WmiPrvSE.exe";
            if (value.CreatorSid != "S-1-5-18" && value.CreatorSid != "S-1-5-19" && value.CreatorSid != "S-1-5-20")
                return "WMI 提供程序不是明确的系统服务账户";
            return null;
        }
    }

    internal sealed class HardThreadGuardianBrokerIdentity
    {
        internal int Pid, Session;
        internal long Creation;
        internal string Path;
    }

    // 台架替身只替换原生读取；创建回执、固定句柄复核顺序和最终判据仍由产品实现执行。
    internal interface IHardThreadGuardianBrokerNative
    {
        bool TryEnableDebugPrivilege();
        int ParentPid(IntPtr child);
        IntPtr OpenProvider(int pid, out string failure);
        bool ReadProcess(IntPtr handle, bool image, out HardThreadGuardianBrokerIdentity identity, out string failure);
        bool ReadSid(IntPtr process, out IntPtr token, out string sid, out string failure);
        string SystemProviderPath();
        string SystemProviderPath32();
        bool Close(IntPtr handle);
    }

    internal sealed class HardThreadGuardianBrokerPin : IDisposable
    {
        private readonly IHardThreadGuardianBrokerNative native;
        private readonly IntPtr parent, child;
        private readonly HardThreadGuardianActor parentActor, childActor;
        private readonly int receiptPid;
        private readonly long requestCreation;
        private IntPtr provider, token;
        private bool providerCloseFault, tokenCloseFault, disposed;
        private bool privilegeAttempted, privilegeEnabled;
        private string privilegeFailure;
        private HardThreadGuardianBrokerProof initial, latest;

        internal HardThreadGuardianBrokerPin(IntPtr parent, IntPtr child, HardThreadGuardianActor parentActor,
            HardThreadGuardianActor childActor, int receiptPid, long requestCreation, IHardThreadGuardianBrokerNative native = null)
        {
            this.parent = parent; this.child = child; this.parentActor = parentActor; this.childActor = childActor;
            this.receiptPid = receiptPid; this.requestCreation = requestCreation;
            this.native = native ?? new HardThreadGuardianBrokerNative();
        }

        internal string Verify()
        {
            string failure;
            var proof = new HardThreadGuardianBrokerProof { LocalCreateSucceeded = receiptPid > 0, ReceiptPid = receiptPid, RequestCreation = requestCreation };
            latest = proof;
            try
            {
                if (disposed) return "WMI 身份证明已关闭";
                if (providerCloseFault || tokenCloseFault) return "WMI 身份句柄关闭状态未结清";
                HardThreadGuardianBrokerIdentity caller, guardian, creator;
                if (!native.ReadProcess(parent, false, out caller, out failure)) return "调用方读取：" + failure;
                if (!native.ReadProcess(child, false, out guardian, out failure)) return "守护读取：" + failure;
                proof.ParentPid = caller.Pid; proof.ParentCreation = caller.Creation; proof.ParentSession = caller.Session;
                proof.ChildPid = guardian.Pid; proof.ChildCreation = guardian.Creation; proof.ChildSession = guardian.Session;
                if (parentActor == null || childActor == null || caller.Pid != parentActor.Pid || caller.Creation != parentActor.Creation
                    || guardian.Pid != childActor.Pid || guardian.Creation != childActor.Creation)
                    return "固定句柄与握手身份不符";
                proof.ActualCreatorPid = native.ParentPid(child);
                if (proof.ActualCreatorPid <= 0) return "守护创建者 PID 读取失败";
                if (!privilegeAttempted)
                {
                    // 跨账户查询服务令牌需要主动启用已有特权，不能依赖后台治理先执行。
                    // 缺少特权时仍让最小权限读取给出真实结果，不把启用结果当作读取成败。
                    privilegeAttempted = true;
                    try { privilegeEnabled = native.TryEnableDebugPrivilege(); }
                    catch (Exception error) { privilegeFailure = error.GetType().Name; }
                }
                if (provider == IntPtr.Zero)
                {
                    provider = native.OpenProvider(proof.ActualCreatorPid, out failure);
                    if (provider == IntPtr.Zero) return "提供程序句柄读取：" + failure;
                }
                if (!native.ReadProcess(provider, true, out creator, out failure)) return "提供程序读取：" + failure;
                proof.CreatorPid = creator.Pid; proof.CreatorCreation = creator.Creation; proof.CreatorSession = creator.Session;
                proof.CreatorPath = creator.Path; proof.SystemProviderPath = native.SystemProviderPath(); proof.SystemProviderPath32 = native.SystemProviderPath32();
                // armed 前重新打开主令牌，避免只复核已脱离进程的旧令牌。
                HardThreadRawHandleClosure.Close(ref token, ref tokenCloseFault, native.Close);
                string sid;
                if (!native.ReadSid(provider, out token, out sid, out failure)) return "提供程序令牌读取：" + failure;
                proof.CreatorSid = sid;
                HardThreadGuardianBrokerIdentity after;
                if (!native.ReadProcess(provider, true, out after, out failure)) return "提供程序令牌读取后复核：" + failure;
                if (after.Pid != creator.Pid || after.Creation != creator.Creation || after.Session != creator.Session
                    || !string.Equals(after.Path, creator.Path, StringComparison.OrdinalIgnoreCase)) return "提供程序在令牌读取期间变化";
                failure = HardThreadGuardianBrokerProof.Validate(proof);
                if (failure != null) return failure;
                if (initial != null && (proof.CreatorPid != initial.CreatorPid || proof.CreatorCreation != initial.CreatorCreation
                    || proof.CreatorSession != initial.CreatorSession || proof.CreatorSid != initial.CreatorSid
                    || proof.ParentSession != initial.ParentSession || proof.ChildSession != initial.ChildSession))
                    return "WMI 提供程序或会话在最终复核前变化";
                if (initial == null) initial = proof;
                return null;
            }
            catch (Exception error) { return "WMI 身份核验异常：" + error.GetType().Name + " " + error.Message; }
        }

        internal string Describe()
        {
            HardThreadGuardianBrokerProof p = latest ?? new HardThreadGuardianBrokerProof();
            return "调用方会话=" + p.ParentSession + " 守护会话=" + p.ChildSession + " 提供程序PID=" + p.ActualCreatorPid
                + " 提供程序创建时间=" + p.CreatorCreation + " 提供程序会话=" + p.CreatorSession + " 服务SID=" + (p.CreatorSid ?? "未知")
                + " SeDebugPrivilege=" + (!privilegeAttempted ? "未尝试" : privilegeEnabled ? "已启用" : "未启用")
                + (privilegeFailure == null ? "" : " 特权阶段异常=" + privilegeFailure);
        }

        internal bool ClosureCertain { get { return !providerCloseFault && !tokenCloseFault; } }
        public void Dispose()
        {
            disposed = true;
            Exception first = null;
            try { HardThreadRawHandleClosure.Close(ref token, ref tokenCloseFault, native.Close); } catch (Exception error) { first = error; }
            try { HardThreadRawHandleClosure.Close(ref provider, ref providerCloseFault, native.Close); } catch (Exception error) { if (first == null) first = error; }
            if (first != null) throw first;
        }
    }

    internal sealed class HardThreadGuardianBrokerNative : IHardThreadGuardianBrokerNative
    {
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetProcessId(IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(uint pid, out uint session);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder image, ref uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetSystemDirectory(StringBuilder path, uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetSystemWow64Directory(StringBuilder path, uint size);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int info, IntPtr buffer, int size, out int needed);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);

        public bool TryEnableDebugPrivilege() { return Native.TryEnableDebugPrivilege(); }
        public int ParentPid(IntPtr child) { return Native.ParentProcessId(child); }
        public IntPtr OpenProvider(int pid, out string failure)
        {
            // 这些读取均支持有限查询权限，不为独立性证明请求写入或同步权限。
            IntPtr handle = OpenProcess(0x1000, false, pid);
            failure = handle == IntPtr.Zero ? "OpenProcess win32=" + Marshal.GetLastWin32Error() : null;
            return handle;
        }
        public bool ReadProcess(IntPtr handle, bool image, out HardThreadGuardianBrokerIdentity identity, out string failure)
        {
            identity = null; failure = null;
            uint pid = GetProcessId(handle), session;
            long creation, exit, kernel, user;
            if (pid == 0 || !GetProcessTimes(handle, out creation, out exit, out kernel, out user))
            { failure = "GetProcessTimes/GetProcessId win32=" + Marshal.GetLastWin32Error(); return false; }
            if (creation <= 0 || exit != 0) { failure = "进程已退出或创建时间无效"; return false; }
            if (!ProcessIdToSessionId(pid, out session)) { failure = "ProcessIdToSessionId win32=" + Marshal.GetLastWin32Error(); return false; }
            string path = null;
            if (image)
            {
                var text = new StringBuilder(32768); uint size = (uint)text.Capacity;
                if (!QueryFullProcessImageName(handle, 0, text, ref size)) { failure = "QueryFullProcessImageName win32=" + Marshal.GetLastWin32Error(); return false; }
                path = text.ToString();
            }
            identity = new HardThreadGuardianBrokerIdentity { Pid = checked((int)pid), Creation = creation, Session = checked((int)session), Path = path };
            return true;
        }
        public bool ReadSid(IntPtr process, out IntPtr token, out string sid, out string failure)
        {
            token = IntPtr.Zero; sid = null; failure = null;
            if (!OpenProcessToken(process, 8, out token)) { failure = "OpenProcessToken win32=" + Marshal.GetLastWin32Error(); return false; }
            IntPtr buffer = Marshal.AllocHGlobal(512);
            try
            {
                int needed;
                if (!GetTokenInformation(token, 1, buffer, 512, out needed)) { failure = "GetTokenInformation win32=" + Marshal.GetLastWin32Error(); return false; }
                sid = new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
                return true;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        public string SystemProviderPath()
        {
            var text = new StringBuilder(32768);
            uint count = GetSystemDirectory(text, (uint)text.Capacity);
            return count == 0 || count >= text.Capacity ? null : Path.Combine(text.ToString(), "wbem", "WmiPrvSE.exe");
        }
        public bool Close(IntPtr handle) { return CloseHandle(handle); }
        public string SystemProviderPath32()
        {
            var text = new StringBuilder(32768);
            uint count = GetSystemWow64Directory(text, (uint)text.Capacity);
            return count == 0 || count >= text.Capacity ? null : Path.Combine(text.ToString(), "wbem", "WmiPrvSE.exe");
        }
    }
}
