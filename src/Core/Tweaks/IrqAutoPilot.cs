// @author bdth 2074055628@qq.com
// 文件用途 自动中断编排 实验功能 按多局裁决自动把冲突设备的中断钉离游戏核
//   收据落地 重启后按本 boot 台账重新裁决验收 无效自动回滚并按驱动版本熔断
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PaviseApp
{
    // 自动中断编排 手动挪核流程的自动驾驶层 五件套之上不新增任何测量或写入机制
    //   测量 裁决 设备清单 亲和引擎 收据 全部复用现有的 这里只做三件事
    //   一 计划 局后拿严格裁决里 Worth 且可操作的设备 目标掩码用实测系统掩码去掉游戏掩码
    //   二 落地 独立引擎实例逐设备写入 收据与启动戳齐全 重启后生效
    //   三 验收 重启后攒满三局本 boot 台账 重新裁决 不再越线就算数 仍在越线自动回滚并熔断
    //
    // 边界 与手动路径一样一条不少 另加自动路径专属的三条
    //   多队列网卡和 StorPort 存储沿用清单层的排除 钉了伤并行度或压根无效
    //   键鼠链设备手动可以钉 自动不碰 输入的风险自动路径不该替人担
    //   拿不到已证实的游戏掩码就不生成计划 宁可这局不编排 也不猜核往哪搬
    internal static class IrqAutoPilot
    {
        internal const string EnabledKey = "IrqAutoOn";
        internal const string PlanKey = "IrqAutoPlanV1";
        internal const string FuseKey = "IrqAutoFuseV1";
        // 在途上限 计划里待重启的设备总数封顶 不是单次调用封顶
        //   否则一晚打几局就能攒下一堆 'P' 一次重启全部同时生效 出了问题没法定位
        internal const int MaxDevicesPerPlan = 3;
        internal const int FuseLimit = 64;
        // 已完结条目(A/R)只留最近这些 账本有界 P/V 永不剪
        internal const int PlanCompletedKeepLimit = 16;

        private static readonly object lk = new object();
        private static readonly IrqAffinityEngine engine =
            new IrqAffinityEngine("IrqAutoAppliedV1", "IrqAuto_", Lang.T("irqauto.logprefix"));

        public static bool Enabled { get { return Settings.Load(EnabledKey, false); } }

        public static bool HasResidue
        {
            get { return engine.HasResidue || Settings.LoadStr(PlanKey, "").Length != 0; }
        }

        public static List<string> TouchedDevices() { return engine.TouchedDevices(); }

        // 计划里的一台设备 状态机 P 已写等重启 → V 已重启攒验收局 → A 通过 / R 已回滚
        internal sealed class PlanEntry
        {
            public string DeviceId = "";
            public string Driver = "";
            public string DriverVersion = "";
            public double BaselinePerMin;
            public char State = 'P';
        }

        internal enum VerifyOutcome { KeepWaiting = 0, Accept = 1, RevertFuse = 2, RevertNoFuse = 3 }

        // 目标掩码 实测系统掩码去掉游戏掩码 两个掩码都要证实过才可用
        //   结果为空或等于全集就放弃 不能把设备钉到不存在的地方
        internal static ulong TargetMask(ulong gameMask, ulong systemMask, ulong allMask)
        {
            if (gameMask == 0 || systemMask == 0) return 0;
            if ((gameMask & ~systemMask) != 0) return 0;
            ulong target = systemMask & ~gameMask;
            if (allMask != 0) target &= allMask;
            if (target == 0 || target == allMask) return 0;
            return target;
        }

        // 验收 与计划时同一套裁决口径 在只含本 boot 对局的窗口上重新评一次
        //   钉离游戏核之后这台驱动不该再出现在"游戏核上越线"的证据里
        internal static VerifyOutcome VerifyEntry(PlanEntry entry,
            List<IrqDriverVerdict> verdicts, int usedSessions)
        {
            if (entry == null || entry.State != 'V') return VerifyOutcome.KeepWaiting;
            if (usedSessions < IrqSessionLedger.MinSessionsForVerdict) return VerifyOutcome.KeepWaiting;
            IrqDriverVerdict found = null;
            bool versionGone = false;
            if (verdicts != null)
                foreach (IrqDriverVerdict v in verdicts)
                {
                    if (v == null || !string.Equals(v.Driver, entry.Driver, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (string.Equals(v.DriverVersion ?? "", entry.DriverVersion, StringComparison.OrdinalIgnoreCase))
                    { found = v; break; }
                    versionGone = true;
                }
            // 驱动升级了 旧证据作废 掩码对新驱动是否合适无从谈起 回滚但不怪机器
            if (found == null && versionGone) return VerifyOutcome.RevertNoFuse;
            // 整个窗口里没有它的 DPC 记录 或不再 Worth 都说明冲突消失了
            if (found == null || !found.Worth) return VerifyOutcome.Accept;
            // 攒够了局 它仍然在游戏核上越线 钉了等于没钉 回滚并熔断这个驱动版本
            return VerifyOutcome.RevertFuse;
        }

        private const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
        private const string MediaClassGuid = "{4d36e96c-e325-11ce-bfc1-08002be10318}";

        // 自动路径不碰显卡和音频 手动路径保留人的判断
        //   显卡中断的既有策略是靠近渲染线程(RenderLane) 钉离游戏核正好反着来
        //   音频 DPC 挪到拥挤的低频小核上会跑得更慢 而验收只看游戏核干不干净
        //   看不见被挪走的设备自己变差 音频劈啪就是这么来的
        internal static bool AutoPathExcluded(IrqDevice device)
        {
            if (device == null) return true;
            if (string.Equals(device.ClassGuid, DisplayClassGuid, StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(device.ClassGuid, MediaClassGuid, StringComparison.OrdinalIgnoreCase))
                return true;
            return string.Equals(device.Bus, "HDAUDIO", StringComparison.OrdinalIgnoreCase);
        }

        // 候选挑选 纯过滤 清单层的 ActionableWorth 已排除多队列网卡与 StorPort 存储
        //   自动路径再加四条 不碰输入链 不碰显卡音频 不碰已钉 不碰计划内和熔断过的
        //   maxPick 由调用方按在途余额算 待重启的多了这局就少编排或不编排
        internal static List<IrqDevice> SelectCandidates(List<IrqDevice> devices,
            List<PlanEntry> plan, HashSet<string> fused, int maxPick)
        {
            var picked = new List<IrqDevice>();
            if (devices == null || maxPick <= 0) return picked;
            foreach (IrqDevice d in devices)
            {
                if (d == null || !d.ActionableWorth || d.IsPinned || d.InputRisk
                    || AutoPathExcluded(d)) continue;
                if (fused != null && d.Verdict != null
                    && fused.Contains(FuseTag(d.Verdict.Driver, d.Verdict.DriverVersion))) continue;
                bool planned = false;
                if (plan != null)
                    foreach (PlanEntry entry in plan)
                        if (entry != null && entry.State != 'R'
                            && string.Equals(entry.DeviceId, d.InstanceId, StringComparison.OrdinalIgnoreCase))
                        { planned = true; break; }
                if (planned) continue;
                picked.Add(d);
            }
            picked.Sort(delegate(IrqDevice a, IrqDevice b)
            {
                double ca = a.Verdict != null ? a.Verdict.Collisions : 0;
                double cb = b.Verdict != null ? b.Verdict.Collisions : 0;
                return cb.CompareTo(ca);
            });
            if (picked.Count > maxPick)
                picked.RemoveRange(maxPick, picked.Count - maxPick);
            return picked;
        }

        // 已完结的历史条目按先进先出剪掉 P/V 是活账永不剪
        internal static bool PruneCompleted(List<PlanEntry> plan)
        {
            if (plan == null) return false;
            int completed = 0;
            foreach (PlanEntry entry in plan)
                if (entry.State == 'A' || entry.State == 'R') completed++;
            bool pruned = false;
            for (int i = 0; i < plan.Count && completed > PlanCompletedKeepLimit; i++)
                if (plan[i].State == 'A' || plan[i].State == 'R')
                {
                    plan.RemoveAt(i--);
                    completed--;
                    pruned = true;
                }
            return pruned;
        }

        // 局后入口 先验收旧计划 再生成新计划 全程一把锁 与开关互斥
        public static void RunAfterMatch(List<IrqDriverVerdict> verdicts, int usedSessions,
            ulong gameMask, ulong systemMask)
        {
            if (!Enabled)
            {
                // 开关已关却还有残留 说明关闭那次还原失败过 每局末重试直到还清
                //   必须用全量口径 引擎还清了但计划清不掉的"幽灵 P"也算残留
                try { if (HasResidue) RevertAll(); } catch { }
                return;
            }
            try { if (!Native.IsElevated()) return; } catch { return; }
            lock (lk)
            {
                // 锁内必须复查开关 用户关闭走的 RevertAll 抢的是同一把锁
                //   没有这一查 关掉开关的下一瞬间还可能钉上新的核 而且再没人验收它
                if (!Enabled) return;
                List<PlanEntry> plan = ParsePlan(Settings.LoadStr(PlanKey, ""));
                bool planDirty = false;

                // 零 孤儿清理 引擎账本已无此设备且无启动戳的 P 条目
                //   说明还原早已完成或注册表写入从未落地 不能让它永久吃配额占预算
                //   账本"读不到"不等于"没有" 读失败这轮整段跳过 不许把好钉核误判成孤儿
                List<string> touchedNow;
                if (engine.TryTouchedDevices(out touchedNow))
                    foreach (PlanEntry entry in plan)
                    {
                        if (entry.State != 'P') continue;
                        bool owned = false;
                        foreach (string id in touchedNow)
                            if (string.Equals(id, entry.DeviceId, StringComparison.OrdinalIgnoreCase))
                            { owned = true; break; }
                        if (!owned && engine.GetRebootState(entry.DeviceId) == IrqRebootState.Unknown)
                        { entry.State = 'R'; planDirty = true; }
                    }

                // 一 状态推进 写过且重启过的进入验收态
                foreach (PlanEntry entry in plan)
                    if (entry.State == 'P'
                        && engine.GetRebootState(entry.DeviceId) == IrqRebootState.Rebooted)
                    { entry.State = 'V'; planDirty = true; }

                // 二 验收 逐台判 回滚只退这一台 别的照留
                foreach (PlanEntry entry in plan)
                {
                    if (entry.State != 'V') continue;
                    VerifyOutcome outcome = VerifyEntry(entry, verdicts, usedSessions);
                    if (outcome == VerifyOutcome.KeepWaiting) continue;
                    if (outcome == VerifyOutcome.Accept)
                    {
                        entry.State = 'A';
                        planDirty = true;
                        Logger.Log(Lang.T("log.irqauto.4") + entry.Driver);
                        continue;
                    }
                    bool reverted = IrqMutationBoundary.Run(delegate
                    { return engine.RestoreOnly(new List<string>(new[] { entry.DeviceId })); });
                    if (!reverted) continue;   // 退不掉先留着 下局再退 收据还在
                    entry.State = 'R';
                    planDirty = true;
                    if (outcome == VerifyOutcome.RevertFuse)
                    {
                        AddFuse(entry.Driver, entry.DriverVersion);
                        Logger.Log(Lang.T("log.irqauto.5") + entry.Driver + Lang.T("log.irqauto.6"));
                    }
                    else Logger.Log(Lang.T("log.irqauto.7") + entry.Driver);
                }

                // 三 计划 攒够局才有资格 拿不到已证实的游戏掩码就不猜
                //   多处理器组机器不编排 引擎在那里会丢掉掩码只写"就近处理器"
                //   那不是钉离游戏核 写了也验不过 只会白白烧穿驱动熔断
                //   在途配额按计划里待重启的总数算 不是按本次调用算
                int pendingCount = 0;
                foreach (PlanEntry entry in plan) if (entry.State == 'P') pendingCount++;
                if (usedSessions >= IrqSessionLedger.MinSessionsForVerdict
                    && !CpuTopology.MultiGroup)
                {
                    ulong target = TargetMask(gameMask, systemMask, CpuTopology.AllMask);
                    if (target != 0)
                    {
                        List<IrqDevice> devices = null;
                        try
                        {
                            devices = IrqDeviceInventory.Enumerate();
                            IrqDeviceInventory.AttachVerdicts(devices, verdicts);
                            IrqDeviceInventory.MarkOwnership(devices);
                        }
                        catch { devices = null; }
                        foreach (IrqDevice d in SelectCandidates(devices, plan, LoadFuses(),
                            MaxDevicesPerPlan - pendingCount))
                        {
                            IrqDevice picked = d;
                            bool ok = IrqMutationBoundary.Run(delegate
                            {
                                return engine.Enable(
                                    new List<string>(new[] { picked.InstanceId }), target);
                            });
                            if (!ok) continue;
                            plan.Add(new PlanEntry
                            {
                                DeviceId = picked.InstanceId,
                                Driver = picked.Verdict != null ? picked.Verdict.Driver : picked.Service,
                                DriverVersion = picked.Verdict != null
                                    ? picked.Verdict.DriverVersion ?? "" : "",
                                BaselinePerMin = picked.Verdict != null && picked.Verdict.ScoredSeconds > 0
                                    ? picked.Verdict.OverlapOver500 / (picked.Verdict.ScoredSeconds / 60.0) : 0,
                                State = 'P'
                            });
                            planDirty = true;
                            // 每写成一台立刻落一次账 注册表已改而计划没记上的窗口
                            //   一旦撞上崩溃 这台设备就成了绕过验收的永久钉核
                            Settings.SaveStr(PlanKey, EncodePlan(plan));
                            Logger.Log(Lang.T("log.irqauto.1") + picked.Name
                                + Lang.T("log.irqauto.2") + IrqRelocate.MaskText(target)
                                + Lang.T("log.irqauto.3"));
                        }
                    }
                }

                if (PruneCompleted(plan)) planDirty = true;
                if (planDirty) Settings.SaveStr(PlanKey, EncodePlan(plan));
            }
        }

        // 关掉开关 退回全部自动钉核 计划清空 熔断保留 那是这台机器验出来的事实
        public static bool RevertAll()
        {
            lock (lk)
            {
                bool ok = IrqMutationBoundary.Run(delegate { return engine.Disable(null); });
                // 计划清不掉也算失败 残留的 P 条目会永久吃配额并把观测预算拉满
                if (ok && !Settings.SaveStr(PlanKey, "")) ok = false;
                return ok;
            }
        }

        // 两种残局都要收 写到一半崩(引擎旗标未落) 或 开关已关但关闭时还原失败
        //   后者若不在启动时补撤 三条路(局末入口/补撤/手动按钮)都到不了它
        //   开关关闭侧用全量口径 引擎还清但计划残着的"幽灵 P"同样要清
        public static void HealFromCrash()
        {
            try
            {
                if (!Enabled && HasResidue) RevertAll();
                else if (engine.HasResidue && !engine.EnabledByPavise) RevertAll();
            }
            catch { }
        }

        // 有没有写了还没验收完的钉核 观测预算据此决定要不要全量观测
        public static bool HasPendingVerification()
        {
            foreach (PlanEntry entry in ParsePlan(Settings.LoadStr(PlanKey, "")))
                if (entry.State == 'P' || entry.State == 'V') return true;
            return false;
        }

        public static string Summarize()
        {
            List<PlanEntry> plan = ParsePlan(Settings.LoadStr(PlanKey, ""));
            int pending = 0, verifying = 0, accepted = 0, reverted = 0;
            foreach (PlanEntry entry in plan)
            {
                if (entry.State == 'P') pending++;
                else if (entry.State == 'V') verifying++;
                else if (entry.State == 'A') accepted++;
                else if (entry.State == 'R') reverted++;
            }
            if (pending + verifying + accepted + reverted == 0) return Lang.T("irqauto.state.idle");
            var parts = new List<string>();
            if (pending > 0) parts.Add(pending + Lang.T("irqauto.state.pending"));
            if (verifying > 0) parts.Add(verifying + Lang.T("irqauto.state.verifying"));
            if (accepted > 0) parts.Add(accepted + Lang.T("irqauto.state.accepted"));
            if (reverted > 0) parts.Add(reverted + Lang.T("irqauto.state.reverted"));
            return string.Join(" ", parts.ToArray());
        }

        internal static string FuseTag(string driver, string version)
        {
            return (driver ?? "").ToLowerInvariant() + "|" + (version ?? "").ToLowerInvariant();
        }

        // 熔断清单保序存放 先进先出剪裁才真的剪掉最旧的 HashSet 的枚举顺序靠不住
        internal static List<string> LoadFuseList()
        {
            var list = new List<string>();
            foreach (string line in Settings.LoadStr(FuseKey, "").Split('\n'))
                if (line.Length > 0 && !list.Contains(line)) list.Add(line);
            return list;
        }

        internal static HashSet<string> LoadFuses()
        {
            return new HashSet<string>(LoadFuseList(), StringComparer.Ordinal);
        }

        private static void AddFuse(string driver, string version)
        {
            List<string> fuses = LoadFuseList();
            string tag = FuseTag(driver, version);
            if (!fuses.Contains(tag)) fuses.Add(tag);
            while (fuses.Count > FuseLimit) fuses.RemoveAt(0);
            Settings.SaveStr(FuseKey, string.Join("\n", fuses.ToArray()));
        }

        // 重开清熔断 与显存驻留同一条房规 时钟被 NTP 前拨可能造出"假重启"
        //   让验收在钉核实际没生效时误判并熔断 用户重开开关就是明确的再试授权
        public static void ClearFuses()
        {
            lock (lk) Settings.SaveStr(FuseKey, "");
        }

        public static void ClearForReset()
        {
            lock (lk)
            {
                Settings.SaveStr(PlanKey, "");
                Settings.SaveStr(FuseKey, "");
                Settings.Save(EnabledKey, false);
            }
        }

        // 计划编解码 一行一台 "1|id|b64驱动|b64版本|基线千分值|状态"
        //   解不开的行整份丢弃 计划是可再生的 下局重新编排即可 不值得为它建恢复流程
        internal static string EncodePlan(List<PlanEntry> plan)
        {
            var lines = new List<string>();
            foreach (PlanEntry entry in plan)
            {
                if (entry == null || entry.DeviceId.Length == 0) continue;
                lines.Add("1|" + entry.DeviceId
                    + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Driver ?? ""))
                    + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.DriverVersion ?? ""))
                    + "|" + ((long)(entry.BaselinePerMin * 1000)).ToString(CultureInfo.InvariantCulture)
                    + "|" + entry.State);
            }
            return string.Join("\n", lines.ToArray());
        }

        internal static List<PlanEntry> ParsePlan(string raw)
        {
            var plan = new List<PlanEntry>();
            if (string.IsNullOrEmpty(raw)) return plan;
            foreach (string line in raw.Split('\n'))
            {
                if (line.Length == 0) continue;
                string[] parts = line.Split('|');
                long baseE3;
                if (parts.Length != 6 || parts[0] != "1" || parts[1].Length == 0
                    || parts[5].Length != 1 || "PVAR".IndexOf(parts[5][0]) < 0
                    || !long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out baseE3))
                    return new List<PlanEntry>();
                try
                {
                    plan.Add(new PlanEntry
                    {
                        DeviceId = parts[1],
                        Driver = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2])),
                        DriverVersion = Encoding.UTF8.GetString(Convert.FromBase64String(parts[3])),
                        BaselinePerMin = baseE3 / 1000.0,
                        State = parts[5][0]
                    });
                }
                catch { return new List<PlanEntry>(); }
            }
            return plan;
        }
    }
}
