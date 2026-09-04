// @author bdth 2074055628@qq.com
// 文件用途 压制核心的重压绑核分部 只给已隔离的后台条目改亲和 解除与还原一律回原值
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed partial class SuppressionCore
    {
        public bool SetSqueeze(int pid, long creation, string name, ulong squeezeMask, out bool changed)
        {
            return SetSqueeze(pid, creation, name, squeezeMask, SuppressReason.Background, out changed);
        }

        // 目标掩码为 0 表示解除 落点校验在 HeavySqueezePolicy.SqueezeTarget
        //   只认身份完全吻合的已落账条目 受保护条目 未落账的条目都不碰
        //   reason 说明这次是谁在下落点 后台路只认纯后台条目 反作弊路只认带反作弊原因的条目
        //   返回真表示条目现在处于期望状态 changed 表示这次调用真的改了亲和
        public bool SetSqueeze(int pid, long creation, string name, ulong squeezeMask,
            SuppressReason reason, out bool changed)
        {
            changed = false;
            Entry e;
            ulong target;
            lock (sync)
            {
                if (!map.TryGetValue(pid, out e) || !SqueezeOwnedBy(e, reason)
                    || e.OrigPri == uint.MaxValue || !e.Journaled || e.Creation <= 0
                    || e.Creation != creation || !SameName(e.Name, name)) return false;
                target = squeezeMask == 0 ? 0 : HeavySqueezePolicy.SqueezeTarget(
                    squeezeMask, e.OrigAff, e.OrigCpuSets, allMask, CpuTopology.MultiGroup);
                if (e.SqueezeAff == target) return true;
            }
            IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION
                | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                if (!SameProcess(h, e)) return false;
                lock (sync)
                {
                    Entry cur;
                    if (!map.TryGetValue(pid, out cur) || cur != e || !SqueezeOwnedBy(cur, reason)
                        || cur.OrigPri == uint.MaxValue || cur.Creation != creation) return false;
                    ulong previous = cur.SqueezeAff;
                    cur.SqueezeAff = target;
                    bool applied = ApplyThrottle(h, cur.Level, cur.OrigPri, cur.OrigAff, cur.OrigCpuSets,
                        DesiredGpu(cur), cur.OrigBoost, AntiCheatThrottled(cur), DesiredAffinityOf(cur));
                    if (!applied && target != 0)
                    {
                        // 绑不上就不记这笔账 亲和写回原值 Applied 与巡检节奏一律不动
                        //   否则一次亲和被拒会把整条压制判成失败 每轮重试还刷日志
                        //   调用方拿到 false 后按 pid 退避 见 GameMode.ApplyHeavySqueeze
                        cur.SqueezeAff = 0;
                        ulong original = cur.OrigAff != 0 ? cur.OrigAff : allMask;
                        if (!CpuTopology.MultiGroup && Native.QueryAffinity(h) != original)
                            RunMutation(delegate { return Native.SetProcessAffinityMask(h, (UIntPtr)original); });
                        return false;
                    }
                    cur.Applied = applied;
                    ScheduleAfterApply(cur, applied, pid);
                    changed = previous != cur.SqueezeAff;
                    return applied;
                }
            }
            finally { Native.CloseHandle(h); }
        }

        // 带反作弊原因的条目归反作弊路 其余带后台原因的归后台路
        private static bool SqueezeOwnedBy(Entry e, SuppressReason reason)
        {
            bool antiCheat = (e.Reasons & SuppressReason.AntiCheat) != 0;
            if (reason == SuppressReason.AntiCheat) return antiCheat;
            return !antiCheat && (e.Reasons & SuppressReason.Background) != 0;
        }

        // 开关局中关掉时把这一路已经绑上的全部放回去 返回真正解除的条数
        public int ClearSqueezes(SuppressReason reason)
        {
            var squeezed = new List<KeyValuePair<int, Entry>>();
            lock (sync)
                foreach (var kv in map)
                    if (kv.Value.SqueezeAff != 0 && SqueezeOwnedBy(kv.Value, reason)) squeezed.Add(kv);
            int released = 0;
            foreach (var kv in squeezed)
            {
                bool changed;
                SetSqueeze(kv.Key, kv.Value.Creation, kv.Value.Name, 0, reason, out changed);
                if (changed) released++;
            }
            return released;
        }

        public int SqueezedCount(SuppressReason reason)
        {
            int n = 0;
            lock (sync)
                foreach (var kv in map)
                    if (kv.Value.SqueezeAff != 0 && SqueezeOwnedBy(kv.Value, reason)) n++;
            return n;
        }

        public bool IsSqueezed(int pid)
        {
            lock (sync)
            {
                Entry e;
                return map.TryGetValue(pid, out e) && e.SqueezeAff != 0;
            }
        }
    }
}
