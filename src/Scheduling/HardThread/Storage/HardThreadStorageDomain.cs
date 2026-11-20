using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PaviseApp
{
    // 线程硬绑定回执目录 只允许当前用户 SYSTEM 与管理员访问 拒绝重解析路径
    // 正向写入前确认没有历史回执欠账 Pavise 启动后接管上次留下的回执
    internal static class HardThreadStorageDomain
    {
        internal const string StoreDirectory = "CriticalThreadHardAffinityV1";
        private const int AttemptLimit = 3;

        private static readonly object historicalGate = new object();
        private static readonly Dictionary<string, int> historicalFailures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        internal static void RequireLocal(string canonicalRoot, Func<string, DriveType> readDriveType)
        {
            if (string.IsNullOrEmpty(canonicalRoot) || canonicalRoot.Length < 3 || ((canonicalRoot[0] < 'A' || canonicalRoot[0] > 'Z') && (canonicalRoot[0] < 'a' || canonicalRoot[0] > 'z')) || canonicalRoot[1] != ':' || canonicalRoot[2] != '\\')
            {
                throw new HardThreadStorageDomainException("Critical-thread placement requires a local Pavise data directory");
            }
            if (readDriveType == null)
            {
                throw new ArgumentNullException("readDriveType");
            }
            DriveType driveType;
            try
            {
                driveType = readDriveType(canonicalRoot.Substring(0, 3));
            }
            catch (Exception inner)
            {
                throw new HardThreadStorageDomainException("The local Pavise data directory could not be confirmed", inner);
            }
            if (driveType != DriveType.Fixed && driveType != DriveType.Removable && driveType != DriveType.Ram)
            {
                throw new HardThreadStorageDomainException("Critical-thread placement requires a local Pavise data directory");
            }
        }

        // 新会话的回执写入方 目录不存在时按受限权限创建 有历史欠账时拒绝写入
        internal static HardThreadFileStore OpenForward(string canonicalStore, HardThreadTarget target)
        {
            string root = Canonical(canonicalStore);
            RequireLocal(root, drive => new DriveInfo(drive).DriveType);
            if (!string.Equals(Path.GetFileName(root), StoreDirectory, StringComparison.Ordinal))
            {
                throw new InvalidDataException("original startup store required");
            }
            ProvisionRoot(root, UserSid());
            HardThreadFileStore store = null;
            try
            {
                store = new HardThreadFileStore(root);
                store.RequireForwardAdmission(target);
                return store;
            }
            catch (HardThreadOperationException)
            {
                if (store != null) store.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                if (store != null) store.Dispose();
                throw new HardThreadOperationException(HardThreadFailureKind.RecoveryPending, "original root writer or historical ledger is unavailable: " + ex.Message);
            }
        }

        // 接管上次 Pavise 留下的回执 游戏已退出的直接记为结清 仍在运行的只把仍是写入值的线程写回原值
        // 结清后删除回执 每份回执在游戏存活与退出两个阶段各最多尝试三次 用尽后保留回执等阶段变化
        internal static bool SettleHistorical(string dataDirectory)
        {
            if (string.IsNullOrEmpty(dataDirectory) || !Path.IsPathRooted(dataDirectory)) return false;
            string root = Canonical(Path.Combine(dataDirectory, StoreDirectory));
            lock (historicalGate)
            {
                try
                {
                    if (!Directory.Exists(root)) return true;
                    RequireAcl(root, UserSid());
                    RemoveRetiredGuards(root);
                    HardThreadJournal[] journals;
                    using (var store = new HardThreadFileStore(root)) journals = store.HistoricalJournals();
                    bool pending = false;
                    foreach (HardThreadJournal journal in journals)
                        if (!SettleJournal(root, journal)) pending = true;
                    return !pending && !HardThreadFileStore.CanonicalRootHasRecoveryDebt(root);
                }
                catch
                {
                    return false;
                }
            }
        }

        private static bool SettleJournal(string root, HardThreadJournal journal)
        {
            HardThreadTarget target = journal.Target;
            HardThreadGoneReason gone = HardThreadHistoricalPolicy.Gone(target, ProcessFact(target.ProcessId), ProcessFact(target.ProcessId));
            string key = target.SessionId.ToString("N") + (gone == HardThreadGoneReason.None ? "/live" : "/gone");
            if (!AttemptAllowed(key)) return false;
            bool settled = false;
            try
            {
                using (var store = new HardThreadFileStore(root))
                {
                    if (gone != HardThreadGoneReason.None)
                    {
                        store.CommitHistoricalGone(target, gone);
                        store.RemoveSettled(target);
                        settled = true;
                    }
                    else
                    {
                        using (CriticalThreadHardAffinitySession session = CriticalThreadHardAffinitySession.OpenHistorical(target, store))
                            settled = session.Restore();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(Logger.Format("线程硬绑定历史回执 会话={0} 进程编号={1} 接管失败 {2}", "Thread hard-binding history receipt session={0} pid={1} takeover failed {2}", target.SessionId.ToString("N"), target.ProcessId, ex.Message));
            }
            AttemptEnded(key, settled);
            return settled;
        }

        // 调用方持有 historicalGate 每个键是一份回执的一个阶段 失败三次后不再尝试 成功即清掉计数
        private static bool AttemptAllowed(string key)
        {
            int failures;
            return !historicalFailures.TryGetValue(key, out failures) || failures < AttemptLimit;
        }

        private static void AttemptEnded(string key, bool settled)
        {
            int failures;
            historicalFailures.TryGetValue(key, out failures);
            if (settled) historicalFailures.Remove(key);
            else historicalFailures[key] = failures + 1;
        }

        private static HardThreadProcessFact ProcessFact(int pid)
        {
            IntPtr handle = Native.OpenProcess(0x101000, false, pid);
            if (handle == IntPtr.Zero) return new HardThreadProcessFact(false, pid, 0L, false, uint.MaxValue, Marshal.GetLastWin32Error());
            try
            {
                long birth, cpu;
                ulong io;
                bool times = Native.QueryProcessSample(handle, out birth, out cpu, out io);
                return new HardThreadProcessFact(true, pid, birth, times, Native.WaitForSingleObject(handle, 0u), 0);
            }
            finally { Native.CloseHandle(handle); }
        }

        // 旧版独立守护在 guards 下留有 exe 副本与作用域文件 已无用处 删不掉的下次再试
        private static void RemoveRetiredGuards(string root)
        {
            string guards = Path.Combine(root, "guards");
            if (!Directory.Exists(guards) || (File.GetAttributes(guards) & FileAttributes.ReparsePoint) != 0) return;
            try { Directory.Delete(guards, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static string Canonical(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static void NoReparse(string path)
        {
            for (DirectoryInfo directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("hard thread store reparse directory refused");
                }
            }
        }

        private static string UserSid()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                return identity.User.Value;
            }
        }

        private static bool TrustedSid(string value, string user)
        {
            return value == user || value == "S-1-5-18" || value == "S-1-5-32-544";
        }

        private static void RequireAcl(string root, string sid)
        {
            NoReparse(root);
            DirectorySecurity security = Directory.GetAccessControl(root, AccessControlSections.Access | AccessControlSections.Owner);
            if (!security.AreAccessRulesProtected || !TrustedSid(security.GetOwner(typeof(SecurityIdentifier)).Value, sid))
            {
                throw new IOException("hard thread store owner is not trusted");
            }
            bool ownerHasFullControl = false;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                string value = rule.IdentityReference.Value;
                if (!TrustedSid(value, sid))
                {
                    throw new IOException("hard thread store has a foreign allow rule");
                }
                if (value == sid && (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl)
                {
                    ownerHasFullControl = true;
                }
            }
            if (!ownerHasFullControl)
            {
                throw new IOException("current user lacks full hard thread store ownership");
            }
        }

        private static void ProvisionRoot(string root, string sid)
        {
            if (!Directory.Exists(root))
            {
                Directory.CreateDirectory(root);
                NoReparse(root);
                DirectorySecurity security = new DirectorySecurity();
                security.SetOwner(new SecurityIdentifier(sid));
                security.SetAccessRuleProtection(true, false);
                foreach (string trusted in new[] { sid, "S-1-5-18", "S-1-5-32-544" })
                {
                    security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(trusted), FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                }
                Directory.SetAccessControl(root, security);
            }
            RequireAcl(root, sid);
        }
    }
}
