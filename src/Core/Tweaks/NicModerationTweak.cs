// @author bdth 2074055628@qq.com
// 文件用途 关闭物理有线网卡的中断合并 收包立即触发中断换网络延迟 重启生效 可逆
using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Win32;

namespace PaviseApp
{
    // 网卡中断合并 驱动为省 CPU 把多个收包攒成一次中断 代价是每个包多等几十到几百微秒
    //   竞技网游的收包频率不高 攒包省下的 CPU 毫无意义 等待却直接进操作延迟
    //
    // 只动标准化关键字 *InterruptModeration 微软 NDIS 规范要求各家统一暴露 0 关 1 开
    //   各家私有的自适应/ITR 参数命名混乱 宁可漏也不猜
    // 只动物理有线网卡 *IfType=6 且 Characteristics 带 NCF_PHYSICAL
    //   无线网卡很少暴露该项 虚拟网卡动了没意义 幽灵配置项跳过
    //
    // 写注册表不重启网卡 语义与本页其它项一致 重启系统后生效
    //   游戏中途永远不会因为这个开关断网
    internal static class NicModerationTweak
    {
        private const string ClassRoot =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
        private const string ConnectionRoot =
            @"SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}";
        private const string ModerationValue = "*InterruptModeration";
        private const string ListKey = "NicImList";
        private const string FlagKey = "NicImOffByPavise";

        private static readonly object lk = new object();

        public static bool EnabledByPavise { get { return Settings.Load(FlagKey, false); } }

        internal sealed class Target
        {
            public string SubKey;
            public string Label;
            public bool ModerationOn;
        }

        public static List<Target> Scan()
        {
            var targets = new List<Target>();
            try
            {
                using (RegistryKey root = Registry.LocalMachine.OpenSubKey(ClassRoot))
                {
                    if (root == null) return targets;
                    foreach (string sub in root.GetSubKeyNames())
                    {
                        if (sub.Length != 4) continue;
                        using (RegistryKey node = root.OpenSubKey(sub))
                        {
                            if (node == null) continue;
                            object moderation = node.GetValue(ModerationValue);
                            // 三道过滤全部失败关闭 判断不了的宁可不动
                            if (!RowEligible(moderation, node.GetValue("*IfType"),
                                node.GetValue("Characteristics"))) continue;
                            string netCfg = node.GetValue("NetCfgInstanceId") as string;
                            if (string.IsNullOrEmpty(netCfg) || !ConnectionKnown(netCfg)) continue;
                            targets.Add(new Target
                            {
                                SubKey = sub,
                                Label = (node.GetValue("DriverDesc") as string) ?? sub,
                                ModerationOn = !string.Equals((string)moderation, "0", StringComparison.Ordinal)
                            });
                        }
                    }
                }
            }
            catch { }
            return targets;
        }

        // 行级判定 纯函数 各家 INF 写值的类型不统一
        //   *InterruptModeration 是标准化 REG_SZ 但 *IfType 和 Characteristics 常见 REG_DWORD
        //   数值和数字字符串都认 其余形态一律判失败关闭
        internal static bool RowEligible(object moderation, object ifType, object characteristics)
        {
            if (!(moderation is string)) return false;
            int typeValue, flags;
            if (!TryNumeric(ifType, out typeValue) || typeValue != 6) return false;
            return TryNumeric(characteristics, out flags) && (flags & 0x4) != 0;
        }

        internal static bool TryNumeric(object raw, out int value)
        {
            if (raw is int) { value = (int)raw; return true; }
            string text = raw as string;
            if (text != null && int.TryParse(text, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out value)) return true;
            value = 0;
            return false;
        }

        // 类键会留下已拔设备的幽灵配置 网络连接键在才算这块卡真实存在
        private static bool ConnectionKnown(string netCfgInstanceId)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    ConnectionRoot + "\\" + netCfgInstanceId + "\\Connection"))
                    return k != null;
            }
            catch { return false; }
        }

        public static bool ModerationActive()
        {
            foreach (Target t in Scan()) if (t.ModerationOn) return true;
            return false;
        }

        public static string Describe()
        {
            List<Target> targets = Scan();
            if (targets.Count == 0) return Lang.T("t.nicim.1");
            int on = 0;
            foreach (Target t in targets) if (t.ModerationOn) on++;
            if (EnabledByPavise)
            {
                if (on > 0) return Lang.T("t.nicim.2") + on + Lang.T("t.nicim.3");
                int changed = ParseList(Settings.LoadStr(ListKey, "")).Length;
                return Lang.T("t.nicim.4") + changed + Lang.T("t.nicim.5");
            }
            if (on == 0) return Lang.T("t.nicim.6");
            return targets.Count + Lang.T("t.nicim.7") + on + Lang.T("t.nicim.8");
        }

        public static bool Enable()
        {
            lock (lk)
            {
                var done = new List<string>();
                var ledger = new List<string>(ParseList(Settings.LoadStr(ListKey, "")));
                bool anyFail = false;
                foreach (Target t in Scan())
                {
                    if (!t.ModerationOn) continue;
                    // 清单外却有备份 = 上次信标路径的欠账(或极端情况下同索引换了新卡)
                    //   先按备份还原清槽再重新开账 否则旧备份会被当成这块卡的原值
                    //   还原失败槽还占着就跳过这块卡 带着陈旧原值开账等于认领错账
                    if (!ledger.Contains(t.SubKey) && Reg(t.SubKey).HasBackup)
                    {
                        Reg(t.SubKey).Restore();
                        if (Reg(t.SubKey).HasBackup)
                        {
                            anyFail = true;
                            Logger.Log(Lang.T("log.nicim.1") + t.Label);
                            continue;
                        }
                    }
                    bool applied = Reg(t.SubKey).Apply("0");
                    if (applied || Reg(t.SubKey).HasBackup) done.Add(t.SubKey);
                    // 写失败但旧备份在 也是失败 不能因为有账就谎报成功
                    if (!applied) { anyFail = true; Logger.Log(Lang.T("log.nicim.1") + t.Label); }
                }
                if (done.Count == 0)
                {
                    if (!anyFail)
                    {
                        Settings.Save(FlagKey, true);
                        Logger.Log(Lang.T("log.nicim.2"));
                    }
                    return !anyFail;
                }
                var merged = new List<string>(ParseList(Settings.LoadStr(ListKey, "")));
                foreach (string id in done)
                    if (!merged.Contains(id)) merged.Add(id);
                if (!Settings.SaveStr(ListKey, string.Join(";", merged.ToArray())))
                {
                    bool rolledBack = true;
                    foreach (string id in done) rolledBack &= Reg(id).Restore();
                    // 回滚也失败时备份槽里有账 清单却没写上 必须点亮标志当信标
                    //   否则残留对 HasResidue 隐形 Restore 的补账扫描会按备份把它找回来
                    if (!rolledBack) Settings.Save(FlagKey, true);
                    Logger.Log(Lang.T("log.nicim.3"));
                    return false;
                }
                Settings.Save(FlagKey, true);
                Logger.Log(Lang.T("log.nicim.4") + done.Count + Lang.T("log.nicim.5"));
                return !anyFail;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                var ids = new List<string>(ParseList(Settings.LoadStr(ListKey, "")));
                // 清单丢失或没写全时按当前网卡补账 槽里有备份就得还 信标场景的兜底
                foreach (Target t in Scan())
                    if (!ids.Contains(t.SubKey) && Reg(t.SubKey).HasBackup) ids.Add(t.SubKey);
                bool all = true;
                foreach (string id in ids)
                    all &= Reg(id).Restore();
                if (all)
                {
                    Settings.SaveStr(ListKey, "");
                    Settings.Save(FlagKey, false);
                    Logger.Log(Lang.T("log.nicim.6"));
                }
                else Logger.Log(Lang.T("log.nicim.7"));
                return all;
            }
        }

        public static bool HasResidue()
        {
            return Settings.Load(FlagKey, false) || ParseList(Settings.LoadStr(ListKey, "")).Length > 0;
        }

        private static ReversibleReg Reg(string subKey)
        {
            return new ReversibleReg(Registry.LocalMachine, ClassRoot + @"\" + subKey,
                ModerationValue, RegistryValueKind.String, "NicIm_" + subKey);
        }

        internal static string[] ParseList(string raw)
        {
            return (raw ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
