using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed partial class SuppressionCore
    {
        private const uint IdlePriorityClass = 0x40, BelowNormalPriorityClass = 0x4000, NormalPriorityClass = 0x20;

        // 父进程优先级是 Idle 或低于正常时 新建的子进程会继承它 I/O 和页面优先级也照搬父进程当时的值 EcoQoS 不继承 2026-11-19 实测过
        // 父进程正被本方压着时新建的子进程 读到的当前值是继承来的 不能当原值 否则释放时写回的还是被压的值
        // 父进程必须是本方条目且原值已知 早于子进程创建 现在仍是同一个进程 并且它的当前值确实不同于原值 才改用父进程的原值
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
                uint parentPriority = Native.GetPriorityClass(handle);
                int parentIo = Native.QueryIoPriority(handle);
                int parentPage = Native.QueryPagePriority(handle);
                if ((priority == IdlePriorityClass || priority == BelowNormalPriorityClass) && priority == parentPriority && parentPriority != parent.OrigPri)
                    priority = ChildPriorityOf(parent.OrigPri);
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

        // 本方没登记过的子孙 比如受保护的或扫描没覆盖到的 也从被压的父进程那里继承了值 父进程写回时一并写回
        // 只写当前值正好等于父进程被压值的那几项 子进程自己后来改过的不动
        private void RestoreInheritedDescendants(int pid, Entry parent, uint pressedPriority, int pressedIo, int pressedPage)
        {
            Func<int, long, long, List<KeyValuePair<int, long>>> source = InheritanceCandidates;
            if (source == null || parent.PressedSince <= 0 || parent.OrigPri == uint.MaxValue || parent.Creation <= 0) return;
            bool priorityPressed = (pressedPriority == IdlePriorityClass || pressedPriority == BelowNormalPriorityClass) && pressedPriority != parent.OrigPri;
            bool ioPressed = parent.OrigIo >= 0 && pressedIo >= 0 && pressedIo != parent.OrigIo;
            bool pagePressed = parent.OrigPg >= 0 && pressedPage >= 0 && pressedPage != parent.OrigPg;
            if (!priorityPressed && !ioPressed && !pagePressed) return;
            List<KeyValuePair<int, long>> candidates;
            try { candidates = source(pid, parent.Creation, parent.PressedSince); }
            catch (Exception) { return; }
            if (candidates == null || candidates.Count == 0) return;
            uint childPriority = ChildPriorityOf(parent.OrigPri);
            int restored = 0;
            foreach (KeyValuePair<int, long> candidate in candidates)
            {
                // 本方登记过的按它自己记下的原值恢复
                lock (sync)
                {
                    Entry tracked;
                    if (map.TryGetValue(candidate.Key, out tracked) && tracked.Creation == candidate.Value) continue;
                }
                IntPtr handle = Native.OpenProcess(0x1200, false, candidate.Key);
                if (handle == IntPtr.Zero) continue;
                try
                {
                    long creation, cpu;
                    ulong io;
                    if (!Native.QueryProcessSample(handle, out creation, out cpu, out io) || creation != candidate.Value) continue;
                    bool wrote = false;
                    if (priorityPressed && Native.GetPriorityClass(handle) == pressedPriority) wrote |= Native.SetPriorityClass(handle, childPriority);
                    if (ioPressed && Native.QueryIoPriority(handle) == pressedIo) wrote |= Native.TrySetIoPriority(handle, parent.OrigIo);
                    if (pagePressed && Native.QueryPagePriority(handle) == pressedPage) wrote |= Native.TrySetPagePriority(handle, parent.OrigPg);
                    if (wrote) restored++;
                }
                finally { Native.CloseHandle(handle); }
            }
            if (restored > 0) Logger.Info(Logger.Format("后台治理 写回继承了压制值的子进程：父进程={0} 进程数={1}", "Background control wrote back children that inherited suppressed values: parent={0} processes={1}", parent.Name, restored));
        }
    }
}
