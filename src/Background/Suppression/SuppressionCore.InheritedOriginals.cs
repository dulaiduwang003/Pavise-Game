using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed partial class SuppressionCore
    {
        private const uint IdlePriorityClass = 0x40, BelowNormalPriorityClass = 0x4000, NormalPriorityClass = 0x20;

        // 子进程继承父进程的低优先级和资源优先级
        // 只向创建于压制期间的子进程传递已知原值
        private void AdoptInheritedOriginalsLocked(IntPtr child, int childPid, long childCreation, ref uint priority, ref int ioPriority, ref int pagePriority)
        {
            if (childCreation <= 0) return;
            int parentPid = Native.ParentProcessId(child);
            Entry parent;
            if (parentPid <= 4 || parentPid == childPid || !map.TryGetValue(parentPid, out parent) || parent.OrigPri == uint.MaxValue
                || parent.Creation <= 0 || parent.Creation >= childCreation) return;
            IntPtr handle = Native.OpenProcess(0x1000, false, parentPid);
            if (handle == IntPtr.Zero) return;
            try
            {
                long creation, cpu;
                ulong io;
                if (!Native.QueryProcessSample(handle, out creation, out cpu, out io) || creation != parent.Creation) return;
                uint originalPriority = OriginalPriorityForInheritance(parent);
                uint parentPriority = priority == IdlePriorityClass || priority == BelowNormalPriorityClass
                    ? RestorablePressedPriority(handle, parent) : 0;
                int parentIo = Native.QueryIoPriority(handle);
                int parentPage = Native.QueryPagePriority(handle);
                if (parentPriority != 0 && priority == parentPriority && parentPriority != originalPriority)
                    priority = ChildPriorityOf(originalPriority);
                if (parent.OrigIo >= 0 && parentIo >= 0 && ioPriority == parentIo && parentIo != parent.OrigIo) ioPriority = parent.OrigIo;
                if (parent.OrigPg >= 0 && parentPage >= 0 && pagePriority == parentPage && parentPage != parent.OrigPg) pagePriority = parent.OrigPg;
            }
            finally { Native.CloseHandle(handle); }
        }

        // 只有 Idle 和低于正常会传给子进程 父进程原值是别的档位时子进程本来是正常档
        private static uint ChildPriorityOf(uint parentOriginal)
        { return parentOriginal == IdlePriorityClass || parentOriginal == BelowNormalPriorityClass ? parentOriginal : NormalPriorityClass; }

        // 产品启动时注入 按进程快照找出父进程被压以后新建的子孙 返回编号和创建时间 反作弊 系统组件和其他会话已排除
        // 空着表示不处理没登记过的子孙
        internal static Func<int, long, long, List<KeyValuePair<int, long>>> InheritanceCandidates;

        internal void RestoreProtectedChildInheritance(ProcEntry child)
        {
            int parentPid = child.ParentPid;
            if (child.Creation <= 0 || parentPid <= 4 || parentPid == child.Pid) return;
            string parentName;
            lock (sync)
            {
                Entry parent, trackedChild;
                if (!map.TryGetValue(parentPid, out parent) || !parent.Applied || parent.RestoreInFlight
                    || (parent.Reasons & SuppressReason.Background) == 0 || parent.OrigPri == uint.MaxValue
                    || parent.Creation <= 0 || child.Creation <= parent.Creation
                    || parent.PressedSince <= 0 || child.Creation <= parent.PressedSince) return;
                if (map.TryGetValue(child.Pid, out trackedChild) && trackedChild.Creation == child.Creation) return;
                if (!string.IsNullOrEmpty(child.Path) && !InheritedDescendants.CanRestore(child)) return;
                // 同一进程身份在父进程本次受压期间只检查一次
                long checkedCreation;
                if (parent.InheritanceChecks == null) parent.InheritanceChecks = new Dictionary<int, long>();
                if (parent.InheritanceChecks.TryGetValue(child.Pid, out checkedCreation) && checkedCreation == child.Creation) return;
                parent.InheritanceChecks[child.Pid] = child.Creation;

                IntPtr handle = Native.OpenProcess(0x1000, false, parentPid);
                if (handle == IntPtr.Zero) return;
                InheritedPriorities inherited;
                try
                {
                    if (Native.QueryProcessIdentityState(handle, parent.Creation) != 0) return;
                    uint expectedPriority = parent.OrdinaryValues ? IdlePriorityClass : DesiredPriority(parent.Level, parent.OrigPri, false);
                    int expectedIo = DesiredIoPriority(parent.Level);
                    int expectedPage = DesiredPagePriority(parent.Level, false);
                    inherited = new InheritedPriorities(parent, OriginalPriorityForInheritance(parent),
                        RestorablePressedPriority(handle, parent) == expectedPriority ? expectedPriority : 0,
                        Native.QueryIoPriority(handle) == expectedIo ? expectedIo : -1,
                        Native.QueryPagePriority(handle) == expectedPage ? expectedPage : -1);
                }
                finally { Native.CloseHandle(handle); }
                if (!inherited.HasChanges || !RestoreInheritedChild(child.Pid, child.Creation, inherited, child)) return;
                parentName = parent.Name;
            }
            Logger.Info(Logger.Format("后台治理 写回受保护子进程继承的压制值：进程={0} 编号={1} 父进程={2}",
                "Background control wrote back suppressed priorities inherited by a protected child: process={0} pid={1} parent={2}", child.Name, child.Pid, parentName));
        }

        private uint OriginalPriorityForInheritance(Entry entry)
        { return FocusPriorityStillOwned(entry) ? entry.FocusJob.Priority : entry.OrigPri; }

        private InheritedPriorities ReadInheritedPriorities(IntPtr handle, Entry entry)
        {
            lock (sync)
            {
                return new InheritedPriorities(entry, OriginalPriorityForInheritance(entry), RestorablePressedPriority(handle, entry),
                    Native.QueryIoPriority(handle), Native.QueryPagePriority(handle));
            }
        }

        private struct InheritedPriorities
        {
            internal readonly uint OriginalPriority, PressedPriority;
            internal readonly int OriginalIo, PressedIo, OriginalPage, PressedPage;

            internal InheritedPriorities(Entry parent, uint originalPriority, uint priority, int io, int page)
            {
                OriginalPriority = ChildPriorityOf(originalPriority);
                OriginalIo = parent.OrigIo;
                OriginalPage = parent.OrigPg;
                PressedPriority = (priority == IdlePriorityClass || priority == BelowNormalPriorityClass) && priority != originalPriority ? priority : 0;
                PressedIo = OriginalIo >= 0 && io >= 0 && io != OriginalIo ? io : -1;
                PressedPage = OriginalPage >= 0 && page >= 0 && page != OriginalPage ? page : -1;
            }

            internal bool HasChanges { get { return PressedPriority != 0 || PressedIo >= 0 || PressedPage >= 0; } }
        }

        // 只写当前值等于父进程被压值的项 子进程自己改过的项保留
        private static bool RestoreInheritedChild(int pid, long birth, InheritedPriorities inherited, ProcEntry protectedChild = null)
        {
            IntPtr handle = Native.OpenProcess(0x1200, false, pid);
            if (handle == IntPtr.Zero) return false;
            try
            {
                if (Native.QueryProcessIdentityState(handle, birth) != 0) return false;
                if (protectedChild != null && (Native.ParentProcessId(handle) != protectedChild.ParentPid
                    || (string.IsNullOrEmpty(protectedChild.Path)
                        && !InheritedDescendants.CanRestore(protectedChild.Session, protectedChild.Name, Native.ImagePath(handle))))) return false;
                bool wrote = false;
                if (inherited.PressedPriority != 0 && Native.GetPriorityClass(handle) == inherited.PressedPriority)
                    wrote |= Native.SetPriorityClass(handle, inherited.OriginalPriority) && Native.GetPriorityClass(handle) == inherited.OriginalPriority;
                if (inherited.PressedIo >= 0 && Native.QueryIoPriority(handle) == inherited.PressedIo)
                    wrote |= Native.TrySetIoPriority(handle, inherited.OriginalIo) && Native.QueryIoPriority(handle) == inherited.OriginalIo;
                if (inherited.PressedPage >= 0 && Native.QueryPagePriority(handle) == inherited.PressedPage)
                    wrote |= Native.TrySetPagePriority(handle, inherited.OriginalPage) && Native.QueryPagePriority(handle) == inherited.OriginalPage;
                return wrote;
            }
            finally { Native.CloseHandle(handle); }
        }

        // 父进程写回时同时处理没有单独登记的子孙
        private void RestoreInheritedDescendants(int pid, Entry parent, InheritedPriorities inherited)
        {
            Func<int, long, long, List<KeyValuePair<int, long>>> source = InheritanceCandidates;
            if (source == null || parent.PressedSince <= 0 || parent.OrigPri == uint.MaxValue || parent.Creation <= 0) return;
            if (!inherited.HasChanges) return;
            List<KeyValuePair<int, long>> candidates;
            try { candidates = source(pid, parent.Creation, parent.PressedSince); }
            catch (Exception) { return; }
            if (candidates == null || candidates.Count == 0) return;
            int restored = 0;
            foreach (KeyValuePair<int, long> candidate in candidates)
            {
                // 本方登记过的按它自己记下的原值恢复
                lock (sync)
                {
                    Entry tracked;
                    if (map.TryGetValue(candidate.Key, out tracked) && tracked.Creation == candidate.Value) continue;
                }
                if (RestoreInheritedChild(candidate.Key, candidate.Value, inherited)) restored++;
            }
            if (restored > 0) Logger.Info(Logger.Format("后台治理 写回继承了压制值的子进程：父进程={0} 进程数={1}", "Background control wrote back children that inherited suppressed values: parent={0} processes={1}", parent.Name, restored));
        }
    }
}
