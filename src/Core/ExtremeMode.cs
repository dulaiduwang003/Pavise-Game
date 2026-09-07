// @author bdth 2074055628@qq.com
// 文件用途 极限档 解锁状态与重启门 覆盖清单 退出集 环境项认领账本
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    // 极限档压制口径与电竞完全一致 差异只在功能开启广度
    //   覆盖不改写 用户自己的开关配置原样保留 切走档位即恢复
    //   被证据排除的项不进清单也没有入口加回来 用户管理只做减法
    internal sealed class ExtremeItem
    {
        public readonly string Token;
        public readonly string LangKey;
        public readonly Func<bool> Eligible;
        public readonly Func<bool> Active;
        public readonly Func<bool> Enable;
        public readonly Func<bool> Revert;
        // Enable 失败后仍可能留下需要恢复的收据；有收据就必须进极限账本，
        // 否则回锁只遍历成功项会把恢复责任变成孤儿。
        public readonly Func<bool> OwnsState;

        public ExtremeItem(string token, string langKey,
            Func<bool> eligible, Func<bool> active, Func<bool> enable, Func<bool> revert)
            : this(token, langKey, eligible, active, enable, revert, null)
        {
        }

        public ExtremeItem(string token, string langKey,
            Func<bool> eligible, Func<bool> active, Func<bool> enable, Func<bool> revert,
            Func<bool> ownsState)
        {
            Token = token; LangKey = langKey;
            Eligible = eligible; Active = active; Enable = enable; Revert = revert;
            OwnsState = ownsState;
        }
    }

    internal static class ExtremeMode
    {
        private const string UnlockedKey = "ExtremeUnlocked";
        private const string UnlockTicksKey = "ExtremeUnlockTicks";
        private const string OptOutKey = "ExtremeOptOut";
        private const string LedgerKey = "ExtremeEnvApplied";
        private static readonly object lk = new object();

        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        public static bool Unlocked { get { return Settings.Load(UnlockedKey, false); } }

        internal static long UnlockTicksUtc { get { return UnlockTicks; } }

        private static long UnlockTicks
        {
            get
            {
                long parsed;
                return long.TryParse(Settings.LoadStr(UnlockTicksKey, ""), NumberStyles.None,
                    CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
            }
        }

        internal static long BootTicksUtc()
        {
            return DateTime.UtcNow.Ticks - (long)GetTickCount64() * TimeSpan.TicksPerMillisecond;
        }

        // 解锁后必须经过一次真实开机才出现 环境页持久项以重启为生效边界
        //   极限档出现即全生效 这个承诺只有重启后才成立
        internal static bool RebootGatePassed(long bootTicks, long unlockTicks)
        {
            return unlockTicks > 0 && bootTicks > unlockTicks;
        }

        // 可见性挂在热路径上 按 Settings 写代数缓存 任何配置写入立即失效
        //   重启门只在开机与解锁间比较 进程存续期内不会自己变
        private static int visibleGeneration = -1;
        private static bool visibleCached;

        public static bool Visible
        {
            get
            {
                int generation = Settings.MutationGeneration;
                if (generation != visibleGeneration)
                {
                    visibleCached = Unlocked && RebootGatePassed(BootTicksUtc(), UnlockTicks);
                    visibleGeneration = generation;
                }
                return visibleCached;
            }
        }

        public static bool PendingReboot { get { return Unlocked && !Visible; } }

        public static void MarkUnlocked()
        {
            Settings.SaveStr(UnlockTicksKey, DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
            Settings.Save(UnlockedKey, true);
        }

        public static void ClearUnlock()
        {
            Settings.Save(UnlockedKey, false);
            Settings.SaveStr(UnlockTicksKey, "");
            Settings.SaveStr(OptOutKey, "");
        }

        // 清除全部配置用 环境项的物理还原由各自的清除步骤负责 这里只清账
        public static void PurgeAll()
        {
            ClearUnlock();
            Settings.SaveStr(LedgerKey, "");
        }

        // ---- 覆盖清单 会话策略键 ----
        private static readonly HashSet<string> SessionKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            PolicyCatalog.KeyAggressive, PolicyCatalog.KeyGpuDemote, PolicyCatalog.KeyWsTrim,
            PolicyCatalog.KeyPauseDl, PolicyCatalog.KeyPauseUpdate, PolicyCatalog.KeyPauseMaintenance,
            PolicyCatalog.KeyPauseServices,
            // 无线扫描抑制已下架，不再提供会话或手动入口。
            //   旧实现切换媒体流模式曾触发驱动掉线，不列入极限清单。
            PolicyCatalog.KeyVramShield,
            PolicyCatalog.KeyAudioLowLat, PolicyCatalog.KeyEnglishInput, PolicyCatalog.KeyPowerYield,
            PolicyCatalog.KeyNvMaxPerf, PolicyCatalog.KeyNvShaderCache,
            PolicyCatalog.KeyAmdAntiLag, PolicyCatalog.KeyIntelLowLatency, PolicyCatalog.KeyIntelEndurance,
            // 候选线程提优 09-06 实测关掉掉三十帧 极限档强制开 CPU 饱和时仍由调度保护撤回
            PolicyCatalog.KeyRenderLane,
        };

        // 全局独立开关 无逐游戏覆盖 由 ApplyEnv 侧按当前档位取用
        private static readonly string[] GlobalTokens =
            { "dwmboost", "gpupower", "gpuprefstage", "autoecogpu" };

        public static IEnumerable<string> SessionPolicyKeys { get { return SessionKeys; } }
        public static string[] AllGlobalTokens { get { return (string[])GlobalTokens.Clone(); } }

        // 快照层只在自身档位是极限时来问 这里管可见性 清单归属和退出集
        //   禁止 CPU 空闲 待机清理 补帧 DLSS 覆写 ReBAR 不在清单 也永远不会在
        public static string ForcedPolicyValue(string key)
        {
            // 先查集合再查可见性 集合是纯内存 可见性有缓存但仍别在无关键上碰它
            if (key == null) return null;
            bool lowLat = key == PolicyCatalog.KeyNvLowLat;
            if (!lowLat && !SessionKeys.Contains(key)) return null;
            if (!Visible || OptedOut(key)) return null;
            // 低延迟强制到 on 不到 ultra ultra 把预渲染队列压到 1 CPU 瓶颈时掉帧率
            //   用户选了 ultra 的配置原样保留 极限期间照样按 on 生效 切走档位即恢复
            return lowLat ? "on" : "1";
        }

        // 不在策略目录里的极限专属键 走这里 语义与 ForcedPolicyValue 一致
        public static bool ForceItem(string key)
        {
            return ForcedPolicyValue(key) == "1";
        }

        // 解锁期间环境项按清单强制 环境页把开着的卡锁成预设强制开 停用只能走管理清单
        //   资格门与批量解锁同一道 本机不合格的项开着也只是用户自己开的 不能顶成预设强制
        //   账本里有的是极限自己翻的 哪怕资格后来变了也照旧锁住 停用仍走管理清单
        public static bool ForcesEnv(string token)
        {
            if (!Unlocked || OptedOut(token)) return false;
            if (LedgerContains(token)) return true;
            foreach (ExtremeItem item in EnvItems())
            {
                if (item.Token != token) continue;
                try { return item.Eligible(); }
                catch { return false; }
            }
            return false;
        }

        // 启动补写 解锁后才进清单的项 或上次没写成的项 这里再翻一次 已开着和停用的照旧跳过
        public static int ReconcileEnvItems(out int failed)
        {
            failed = 0;
            if (!Unlocked) return 0;
            return ApplyEnvItems(out failed);
        }

        public static bool ForceGlobal(string token)
        {
            if (!Visible || OptedOut("g:" + token)) return false;
            for (int i = 0; i < GlobalTokens.Length; i++)
                if (GlobalTokens[i] == token) return true;
            return false;
        }

        // ---- 退出集 用户管理只做减法 ----
        public static bool OptedOut(string id)
        {
            string raw = Settings.LoadStr(OptOutKey, "");
            if (raw.Length == 0) return false;
            foreach (string entry in raw.Split(','))
                if (entry == id) return true;
            return false;
        }

        public static void SetOptedOut(string id, bool optedOut)
        {
            lock (lk)
            {
                var set = new List<string>();
                string raw = Settings.LoadStr(OptOutKey, "");
                if (raw.Length > 0)
                    foreach (string entry in raw.Split(','))
                        if (entry.Length > 0 && entry != id) set.Add(entry);
                if (optedOut) set.Add(id);
                Settings.SaveStr(OptOutKey, string.Join(",", set.ToArray()));
            }
        }

        // ---- 环境项认领账本 只认领向导实际翻动的 用户早已自己开着的不记不动 ----
        public static bool LedgerContains(string token)
        {
            string raw = Settings.LoadStr(LedgerKey, "");
            if (raw.Length == 0) return false;
            foreach (string entry in raw.Split(','))
                if (entry == token) return true;
            return false;
        }

        private static void SetLedger(string token, bool present)
        {
            var set = new List<string>();
            string raw = Settings.LoadStr(LedgerKey, "");
            if (raw.Length > 0)
                foreach (string entry in raw.Split(','))
                    if (entry.Length > 0 && entry != token) set.Add(entry);
            if (present) set.Add(token);
            Settings.SaveStr(LedgerKey, string.Join(",", set.ToArray()));
        }

        private static bool OwnsStateNow(ExtremeItem item)
        {
            if (item == null || item.OwnsState == null) return false;
            try { return item.OwnsState(); }
            catch { return false; }
        }

        public static ExtremeItem[] EnvItems()
        {
            return new[]
            {
                // HAGS 只对帧生成需要它的显卡强制 借 Smooth Motion 的世代门 其余留给用户手开
                new ExtremeItem("hags", "set.hags",
                    delegate
                    {
                        bool sup, on;
                        HagsTweak.TryQueryState(out sup, out on);
                        return sup && NvDrsTweaks.SmoothMotionSupported();
                    },
                    delegate { return HagsTweak.EnabledByPavise || HagsTweak.CurrentlyOn(); },
                    HagsTweak.Enable, HagsTweak.Disable),
                // 虚拟化在用的机器不强制关 VBS 关了会带走 Hyper-V WSL2 沙盒 用户手开照旧
                // SAM 是 A 卡侧的 ReBAR ADLX 报支持才有 用户本来就开着的不记账
                new ExtremeItem("amdsam", "set.amdsam",
                    delegate { return AdlxTweaks.Available && AmdSamTweak.Supported(); },
                    delegate { return AmdSamTweak.EnabledByPavise || AmdSamTweak.CurrentlyOn(); },
                    AmdSamTweak.Enable, AmdSamTweak.Restore),
                new ExtremeItem("vbs", "set.vbs",
                    delegate
                    {
                        string k;
                        return !VbsTweak.BlockedReason(out k) && !VbsTweak.VirtualizationInUse();
                    },
                    delegate { return VbsTweak.DisabledByPavise; },
                    VbsTweak.Disable, VbsTweak.Restore),
                // 硬件已缓解的 CPU 卸载只剩安全代价 只在 KPTI 或老式 IBRS 在跑时强制
                new ExtremeItem("specmit", "set.specmit",
                    delegate
                    {
                        string k;
                        return !SpecMitigationTweak.BlockedReason(out k)
                            && SpecMitigationTweak.WorthDisabling(SpecMitigationTweak.Query());
                    },
                    delegate { return SpecMitigationTweak.DisabledByPavise; },
                    SpecMitigationTweak.Disable, SpecMitigationTweak.Restore),
                new ExtremeItem("gmguard", "set.gmguard",
                    delegate { return true; },
                    delegate { return GameModeGuard.EnabledByPavise; },
                    GameModeGuard.Enable, GameModeGuard.Restore),
                new ExtremeItem("windowedopt", "set.windowedopt",
                    delegate { return Native.OsBuild() >= 22000; },
                    delegate { return WindowedOptTweak.EnabledByPavise || WindowedOptTweak.CurrentlyOn(); },
                    WindowedOptTweak.Enable, WindowedOptTweak.Restore),
                new ExtremeItem("vrropt", "set.vrropt",
                    delegate { return VrrOptTweak.OsSupported(); },
                    delegate { return VrrOptTweak.EnabledByPavise || VrrOptTweak.CurrentlyOn(); },
                    VrrOptTweak.Enable, VrrOptTweak.Restore),
                // 两项计时器都不再由极限自动写入 节拍改的是时钟中断怎么来 有整机卡死的公开案例
                //   全局分辨率整机常驻 1ms 只涨功耗 没测出收益 两项默认关 想开的自己在环境页开
                //   留在清单里只为旧账本的回滚 资格恒为否 批量解锁与启动补写都跳过
                new ExtremeItem("timertick", "set.timertick",
                    delegate { return false; },
                    delegate { return TimerTickTweak.EnabledByPavise || TimerTickTweak.CurrentlyOn(); },
                    TimerTickTweak.Enable, TimerTickTweak.Restore),
                new ExtremeItem("gtimer", "set.gtimer",
                    delegate { return false; },
                    delegate { return GlobalTimerResTweak.EnabledByPavise; },
                    GlobalTimerResTweak.Enable, GlobalTimerResTweak.Restore),
                new ExtremeItem("devpower", "set.devpower",
                    delegate { return true; },
                    delegate { return DevicePowerTweak.EnabledByPavise; },
                    DevicePowerTweak.Enable, DevicePowerTweak.Restore),
                // 有物理有线网卡才有节能以太网可关 解锁后网卡会重新协商一次链路 反正紧接着就是重启
                new ExtremeItem("eee", "set.eee",
                    delegate
                    {
                        foreach (NicModerationTarget t in NicModerationTweak.Scan())
                            if (t != null && t.PhysicalWired) return true;
                        return false;
                    },
                    delegate { return EeeTweak.EnabledByPavise; },
                    EeeTweak.Enable, EeeTweak.Restore),
                new ExtremeItem("nicim", "set.nicim",
                    // 退役的自动项仍留在清单里承担旧极限账本的恢复责任，
                    // 但永远不再由批量解锁或“恢复全部跟随”主动关闭网卡中断合并。
                    delegate { return false; },
                    delegate { return NicModerationTweak.HasLegacyResidue; },
                    NicModerationTweak.MigrateLegacy, NicModerationTweak.MigrateLegacy,
                    delegate { return NicModerationTweak.HasLegacyResidue; }),
                new ExtremeItem("hidpower", "set.hidpower",
                    delegate { return true; },
                    delegate { return HidPowerTweak.EnabledByPavise; },
                    HidPowerTweak.Enable, HidPowerTweak.Restore),
                new ExtremeItem("accesskeys", "set.accesskeys",
                    delegate { return AccessibilityKeysTweak.NeedsFix() || AccessibilityKeysTweak.HasResidue(); },
                    delegate { return AccessibilityKeysTweak.HasResidue(); },
                    AccessibilityKeysTweak.Enable, AccessibilityKeysTweak.Restore),
                new ExtremeItem("memcompress", "set.memcompress",
                    MemCompressTweak.RamEligible,
                    delegate { return MemCompressTweak.EnabledByPavise
                        || MemCompressTweak.CurrentlyOff(); },
                    MemCompressTweak.Enable, MemCompressTweak.Restore,
                    delegate { return MemCompressTweak.OwnsState; }),
                // 内核保留核已下架 内核忽略普通进程对保留集合的软 CPU 集合写入 游戏根本进不去
                //   旧版写过的 ReservedCpuSets 由卸载与急救脚本按收据还原 代码里不再碰
            };
        }

        // 解锁应用 只翻不在开着状态的合格项 翻动成功的记入账本
        //   安全两项包含在解锁确认里 事后可在管理面单独停用
        public static int ApplyEnvItems(out int failed)
        {
            failed = 0;
            int flipped = 0;
            lock (lk)
            {
                foreach (ExtremeItem item in EnvItems())
                {
                    bool eligible, active;
                    try { eligible = item.Eligible(); active = item.Active(); }
                    catch { failed++; continue; }
                    // 收据可能已落盘，而进程在 SetLedger 前退出。下次见到已接管状态
                    // 必须先补账；外部已满足的项目 OwnsState=false，仍然只跳过。
                    if (active)
                    {
                        if (OwnsStateNow(item)) SetLedger(item.Token, true);
                        continue;
                    }
                    if (!eligible || OptedOut(item.Token)) continue;
                    bool ok;
                    try { ok = item.Enable(); }
                    catch { ok = false; }
                    if (ok)
                    {
                        // 有归属探针的项目只有真的拿到收据才算翻动；这也封住
                        // Active 与 Enable 两次查询之间外部恰好完成关闭的竞态。
                        if (item.OwnsState == null || OwnsStateNow(item))
                        { SetLedger(item.Token, true); flipped++; }
                    }
                    else
                    {
                        // 失败若仍留下可恢复收据，也必须让回锁/管理面看得到。
                        if (OwnsStateNow(item)) SetLedger(item.Token, true);
                        failed++;
                    }
                }
            }
            return flipped;
        }

        // 管理面的单项操作 归属规则与批量一致 只认领自己翻动的
        public static bool ApplySingle(ExtremeItem item)
        {
            lock (lk)
            {
                bool eligible, active;
                try { eligible = item.Eligible(); active = item.Active(); }
                catch { return false; }
                if (active)
                {
                    if (OwnsStateNow(item)) SetLedger(item.Token, true);
                    return true;
                }
                if (!eligible) return true;
                bool ok;
                try { ok = item.Enable(); }
                catch { ok = false; }
                if (ok && (item.OwnsState == null || OwnsStateNow(item)))
                    SetLedger(item.Token, true);
                else if (!ok && OwnsStateNow(item)) SetLedger(item.Token, true);
                return ok;
            }
        }

        public static bool RevertSingle(ExtremeItem item)
        {
            lock (lk)
            {
                if (!LedgerContains(item.Token)) return true;
                bool ok;
                try { ok = item.Revert(); }
                catch { ok = false; }
                if (ok) SetLedger(item.Token, false);
                return ok;
            }
        }

        // 下架自动环境项完成旧账还原后清掉极限账本 token。只开放给升级迁移，
        // 不能顺手清其它项目，否则回锁会失去恢复责任。
        internal static void RetireEnvLedgerToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return;
            lock (lk) SetLedger(token, false);
        }

        // 回锁只回滚账本上的 用户自己开的一概不碰 失败的留账下次再试
        public static bool RollbackEnvItems()
        {
            bool all = true;
            lock (lk)
            {
                foreach (ExtremeItem item in EnvItems())
                {
                    if (!LedgerContains(item.Token)) continue;
                    bool ok;
                    try { ok = item.Revert(); }
                    catch { ok = false; }
                    if (ok) SetLedger(item.Token, false);
                    else all = false;
                }
            }
            return all;
        }
    }
}
