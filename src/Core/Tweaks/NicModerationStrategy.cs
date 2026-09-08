// @author bdth 2074055628@qq.com
// 文件用途 网卡中断合并实验策略的纯状态机 收据先行 身份核对 回读与保守回滚
using System;
using System.Collections.Generic;
using System.Text;

namespace PaviseApp
{
    // 标准 *InterruptModeration 只有 0 和 1 Adaptive 和 Medium 是厂商私有算法
    // 没做过 PCI 驱动 INF 指纹复核 就别在这猜值
    internal enum NicModerationMode
    {
        Unknown = -1,
        Off = 0,
        DriverManaged = 1
    }

    internal enum NicModerationScanIssue
    {
        None,
        ReadFailed,
        NoSupportedAdapter,
        NoActiveAdapter,
        AmbiguousActiveAdapters,
        NoDefaultRoute,
        DefaultRouteNotPhysical,
        UnsafeAdapter,
        UnsupportedValue,
        IdentityUnavailable,
        ReceiptUnreadable,
        ReceiptCorrupt,
        RecoveryPending,
        IdentityChanged,
        ExternalChanged,
        WriteFailed,
        ReceiptSaveFailed,
        AlreadyOff,
        Applied,
        Restored
    }

    internal enum NicModerationWriteResult
    {
        Applied,
        IdentityChanged,
        CurrentChanged,
        WriteFailed,
        ReadbackFailed
    }

    internal sealed class NicModerationTarget
    {
        public string SubKey = "";
        public string NetCfgInstanceId = "";
        public string DeviceInstanceId = "";
        public string Label = "";
        public string DriverVersion = "";
        public string Service = "";
        public string InfPath = "";
        public NicModerationMode Mode = NicModerationMode.Unknown;
        // Unknown 有两种 一种是真读到了私有值或者值缺失 一种是读取本身出错
        // 只有前者能算外部接管 才能结清恢复收据
        public bool ModeReadReliable = true;
        public bool LinkUp;
        public bool PhysicalWired;
        public bool Excluded;
        public int InterfaceIndex = -1;
    }

    internal sealed class NicModerationScan
    {
        public bool Success;
        public bool LiveStateReliable;
        public int BestInterfaceIndex = -1;
        public readonly List<NicModerationTarget> Targets = new List<NicModerationTarget>();
    }

    internal interface INicModerationPlatform
    {
        NicModerationScan Scan();
        NicModerationWriteResult CompareExchange(NicModerationTarget target,
            NicModerationMode expected, NicModerationMode desired);
    }

    internal interface INicModerationReceiptStore
    {
        bool TryRead(out string raw);
        bool SaveAndVerify(string raw);
    }

    internal sealed class NicModerationReceipt
    {
        public bool Applied;
        public string SubKey = "";
        public string NetCfgInstanceId = "";
        public string DeviceInstanceId = "";
        public string Label = "";
        public string DriverVersion = "";
        public string Service = "";
        public string InfPath = "";
        public NicModerationMode Original;
        public NicModerationMode Desired;
    }

    internal static class NicModerationReceiptCodec
    {
        private const string Version = "NIM2";
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private static string B64(string text)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? ""));
        }

        private static bool TryB64(string text, out string value)
        {
            value = "";
            try
            {
                value = StrictUtf8.GetString(Convert.FromBase64String(text ?? ""));
                return true;
            }
            catch { return false; }
        }

        internal static string Encode(NicModerationReceipt receipt)
        {
            if (receipt == null) return "";
            return string.Join("|", new[]
            {
                Version,
                receipt.Applied ? "A" : "P",
                B64(receipt.SubKey),
                B64(receipt.NetCfgInstanceId),
                B64(receipt.DeviceInstanceId),
                B64(receipt.Label),
                B64(receipt.DriverVersion),
                B64(receipt.Service),
                B64(receipt.InfPath),
                ((int)receipt.Original).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ((int)receipt.Desired).ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        }

        internal static bool TryDecode(string raw, out NicModerationReceipt receipt)
        {
            receipt = null;
            if (string.IsNullOrEmpty(raw)) return false;
            string[] p = raw.Split('|');
            if (p.Length != 11 || p[0] != Version || (p[1] != "P" && p[1] != "A")) return false;

            var r = new NicModerationReceipt();
            r.Applied = p[1] == "A";
            if (!TryB64(p[2], out r.SubKey)
                || !TryB64(p[3], out r.NetCfgInstanceId)
                || !TryB64(p[4], out r.DeviceInstanceId)
                || !TryB64(p[5], out r.Label)
                || !TryB64(p[6], out r.DriverVersion)
                || !TryB64(p[7], out r.Service)
                || !TryB64(p[8], out r.InfPath)) return false;

            int original, desired;
            if (!int.TryParse(p[9], out original) || !int.TryParse(p[10], out desired)
                // NIM2 只表达这版唯一授权的方向 驱动管理 1 到实验 Off 0
                // 语法对但方向反过来的票据一律按损坏处理
                // 不能被它骗着去做本策略从来没建立过的写入
                || original != (int)NicModerationMode.DriverManaged
                || desired != (int)NicModerationMode.Off
                || string.IsNullOrEmpty(r.NetCfgInstanceId)
                || string.IsNullOrEmpty(r.DeviceInstanceId)) return false;
            r.Original = (NicModerationMode)original;
            r.Desired = (NicModerationMode)desired;
            receipt = r;
            return true;
        }
    }

    internal sealed class NicModerationStrategy
    {
        private readonly INicModerationPlatform platform;
        private readonly INicModerationReceiptStore store;

        internal NicModerationStrategy(INicModerationPlatform platform,
            INicModerationReceiptStore store)
        {
            if (platform == null) throw new ArgumentNullException("platform");
            if (store == null) throw new ArgumentNullException("store");
            this.platform = platform;
            this.store = store;
        }

        internal bool HasReceipt
        {
            get
            {
                string raw;
                return !store.TryRead(out raw) || !string.IsNullOrEmpty(raw);
            }
        }

        internal bool TryReceipt(out NicModerationReceipt receipt,
            out NicModerationScanIssue issue)
        {
            receipt = null;
            issue = NicModerationScanIssue.None;
            string raw;
            if (!store.TryRead(out raw))
            {
                issue = NicModerationScanIssue.ReceiptUnreadable;
                return false;
            }
            if (string.IsNullOrEmpty(raw)) return true;
            if (!NicModerationReceiptCodec.TryDecode(raw, out receipt))
            {
                issue = NicModerationScanIssue.ReceiptCorrupt;
                return false;
            }
            return true;
        }

        internal NicModerationTarget Inspect(out NicModerationScanIssue issue)
        {
            return SelectTarget(platform.Scan(), out issue);
        }

        internal static NicModerationTarget SelectTarget(NicModerationScan scan,
            out NicModerationScanIssue issue)
        {
            issue = NicModerationScanIssue.None;
            if (scan == null || !scan.Success)
            {
                issue = NicModerationScanIssue.ReadFailed;
                return null;
            }
            if (!scan.LiveStateReliable)
            {
                issue = NicModerationScanIssue.ReadFailed;
                return null;
            }
            if (scan.Targets.Count == 0)
            {
                issue = NicModerationScanIssue.NoSupportedAdapter;
                return null;
            }

            NicModerationTarget selected = null;
            int active = 0;
            foreach (NicModerationTarget target in scan.Targets)
            {
                // 先把活动的物理有线设备全数出来 再看唯一出口适不适合写
                // 不能只数暴露了 *InterruptModeration 的设备
                // 不然第二块不支持标准项的 USB 或厂商网卡会漏掉 第一块就被错放行
                if (target == null || !target.LinkUp || !target.PhysicalWired) continue;
                active++;
                selected = target;
            }
            if (active == 0)
            {
                issue = NicModerationScanIssue.NoActiveAdapter;
                return null;
            }
            if (active != 1)
            {
                issue = NicModerationScanIssue.AmbiguousActiveAdapters;
                return null;
            }
            if (scan.BestInterfaceIndex <= 0)
            {
                issue = NicModerationScanIssue.NoDefaultRoute;
                return null;
            }
            if (selected.InterfaceIndex <= 0 || selected.InterfaceIndex != scan.BestInterfaceIndex)
            {
                issue = NicModerationScanIssue.DefaultRouteNotPhysical;
                return null;
            }
            if (selected.Excluded)
            {
                issue = NicModerationScanIssue.UnsafeAdapter;
                return null;
            }
            if (string.IsNullOrEmpty(selected.DeviceInstanceId))
            {
                issue = NicModerationScanIssue.IdentityUnavailable;
                return null;
            }
            if (!selected.ModeReadReliable)
            {
                issue = NicModerationScanIssue.ReadFailed;
                return null;
            }
            if (selected.Mode == NicModerationMode.Unknown)
            {
                issue = NicModerationScanIssue.UnsupportedValue;
                return null;
            }
            return selected;
        }

        internal bool ApplyExperimentalOff(out NicModerationScanIssue issue)
        {
            issue = NicModerationScanIssue.None;
            NicModerationReceipt existing;
            if (!TryReceipt(out existing, out issue)) return false;
            if (existing != null)
            {
                issue = NicModerationScanIssue.RecoveryPending;
                return existing.Applied && existing.Desired == NicModerationMode.Off;
            }

            NicModerationTarget target = Inspect(out issue);
            if (target == null) return false;
            if (target.Mode == NicModerationMode.Off)
            {
                // 外部自己关掉的不算 Pavise 接管 别造假收据
                issue = NicModerationScanIssue.AlreadyOff;
                return true;
            }
            if (target.Mode != NicModerationMode.DriverManaged)
            {
                issue = NicModerationScanIssue.UnsupportedValue;
                return false;
            }

            var receipt = FromTarget(target, NicModerationMode.Off);
            if (!store.SaveAndVerify(NicModerationReceiptCodec.Encode(receipt)))
            {
                issue = NicModerationScanIssue.ReceiptSaveFailed;
                return false;
            }

            NicModerationWriteResult write = platform.CompareExchange(target,
                receipt.Original, receipt.Desired);
            if (write != NicModerationWriteResult.Applied)
            {
                // 这三类明确发生在写入之前 清掉 Prepared 就行
                // ReadbackFailed 有可能已经写进去了 得走同一条 CAS 恢复路径
                bool settled;
                if (write == NicModerationWriteResult.IdentityChanged
                    || write == NicModerationWriteResult.CurrentChanged
                    || write == NicModerationWriteResult.WriteFailed)
                    settled = store.SaveAndVerify("");
                else
                {
                    NicModerationScanIssue settle;
                    settled = RestoreReceipt(receipt, out settle);
                }
                issue = !settled ? NicModerationScanIssue.RecoveryPending
                    : write == NicModerationWriteResult.IdentityChanged
                        ? NicModerationScanIssue.IdentityChanged
                        : NicModerationScanIssue.WriteFailed;
                return false;
            }

            receipt.Applied = true;
            if (!store.SaveAndVerify(NicModerationReceiptCodec.Encode(receipt)))
            {
                NicModerationScanIssue settle;
                bool restored = RestoreReceipt(receipt, out settle);
                issue = restored ? NicModerationScanIssue.ReceiptSaveFailed
                    : NicModerationScanIssue.RecoveryPending;
                return false;
            }
            issue = NicModerationScanIssue.Applied;
            return true;
        }

        internal bool Restore(out NicModerationScanIssue issue)
        {
            issue = NicModerationScanIssue.None;
            NicModerationReceipt receipt;
            if (!TryReceipt(out receipt, out issue)) return false;
            if (receipt == null) return true;
            return RestoreReceipt(receipt, out issue);
        }

        // 启动对账要盖住两类崩溃窗口 一是 Prepared 写了没确认
        // 二是 Applied 已经写回 Original 收据还没来得及清
        // Applied+Desired 是用户自己建立的实验 只确认它在 正常启动不擅自还原
        internal bool ReconcileStartup(out NicModerationScanIssue issue)
        {
            issue = NicModerationScanIssue.None;
            NicModerationReceipt receipt;
            if (!TryReceipt(out receipt, out issue)) return false;
            if (receipt == null) return true;

            NicModerationScan scan = platform.Scan();
            if (scan == null || !scan.Success)
            {
                issue = NicModerationScanIssue.ReadFailed;
                return false;
            }
            NicModerationTarget current = FindIdentity(scan.Targets, receipt);
            if (current == null)
            {
                issue = NicModerationScanIssue.IdentityChanged;
                return false;
            }
            if (!current.ModeReadReliable)
            {
                issue = NicModerationScanIssue.ReadFailed;
                return false;
            }
            if (receipt.Applied && current.Mode == receipt.Desired)
                return true;
            return RestoreReceipt(receipt, out issue);
        }

        // 只在用户明确点清除全部时用 丢掉读不出来 损坏 或者设备已卸载的应用收据
        // 这个动作不碰系统网卡值 普通关闭路径永远不会调它
        internal bool DiscardReceiptForReset()
        {
            return store.SaveAndVerify("");
        }

        private bool RestoreReceipt(NicModerationReceipt receipt,
            out NicModerationScanIssue issue)
        {
            issue = NicModerationScanIssue.None;
            NicModerationScan scan = platform.Scan();
            if (scan == null || !scan.Success)
            {
                issue = NicModerationScanIssue.ReadFailed;
                return false;
            }
            NicModerationTarget current = FindIdentity(scan.Targets, receipt);
            if (current == null)
            {
                issue = NicModerationScanIssue.IdentityChanged;
                return false;
            }
            if (!current.ModeReadReliable)
            {
                issue = NicModerationScanIssue.ReadFailed;
                return false;
            }
            if (current.Mode == receipt.Original)
            {
                if (!store.SaveAndVerify(""))
                {
                    issue = NicModerationScanIssue.ReceiptSaveFailed;
                    return false;
                }
                issue = NicModerationScanIssue.Restored;
                return true;
            }
            if (current.Mode != receipt.Desired)
            {
                // 用户 驱动 或者别的工具已经接管 别拿旧收据去盖
                // 结清 Pavise 的所有权 日志里留一条 ExternalChanged 好查
                if (!store.SaveAndVerify(""))
                {
                    issue = NicModerationScanIssue.ReceiptSaveFailed;
                    return false;
                }
                issue = NicModerationScanIssue.ExternalChanged;
                return true;
            }

            NicModerationWriteResult write = platform.CompareExchange(current,
                receipt.Desired, receipt.Original);
            if (write != NicModerationWriteResult.Applied)
            {
                issue = write == NicModerationWriteResult.IdentityChanged
                    ? NicModerationScanIssue.IdentityChanged : NicModerationScanIssue.WriteFailed;
                return false;
            }
            if (!store.SaveAndVerify(""))
            {
                issue = NicModerationScanIssue.ReceiptSaveFailed;
                return false;
            }
            issue = NicModerationScanIssue.Restored;
            return true;
        }

        private static NicModerationReceipt FromTarget(NicModerationTarget target,
            NicModerationMode desired)
        {
            return new NicModerationReceipt
            {
                Applied = false,
                SubKey = target.SubKey ?? "",
                NetCfgInstanceId = target.NetCfgInstanceId ?? "",
                DeviceInstanceId = target.DeviceInstanceId ?? "",
                Label = target.Label ?? "",
                DriverVersion = target.DriverVersion ?? "",
                Service = target.Service ?? "",
                InfPath = target.InfPath ?? "",
                Original = target.Mode,
                Desired = desired
            };
        }

        internal static NicModerationTarget FindIdentity(IList<NicModerationTarget> targets,
            NicModerationReceipt receipt)
        {
            if (targets == null || receipt == null) return null;
            NicModerationTarget match = null;
            int matches = 0;
            foreach (NicModerationTarget target in targets)
            {
                if (target == null || !string.Equals(target.NetCfgInstanceId,
                        receipt.NetCfgInstanceId, StringComparison.OrdinalIgnoreCase)) continue;
                // NetCfg GUID 只管定位接口 完整 PnP 实例 ID 是必须对上的第二把锁
                // 哪一头缺了都不能退回成只凭 GUID 写旧值
                if (string.IsNullOrEmpty(receipt.DeviceInstanceId)
                    || string.IsNullOrEmpty(target.DeviceInstanceId)
                    || !string.Equals(target.DeviceInstanceId, receipt.DeviceInstanceId,
                        StringComparison.OrdinalIgnoreCase)) continue;
                // 驱动重装会短暂留下重复类键 证明不了哪一项是当前设备时
                // 别按枚举顺序随手挑一个写回去
                match = target;
                matches++;
            }
            return matches == 1 ? match : null;
        }

        internal static int CountIdentityMatches(IList<NicModerationTarget> targets,
            NicModerationReceipt receipt)
        {
            if (targets == null || receipt == null
                || string.IsNullOrEmpty(receipt.NetCfgInstanceId)
                || string.IsNullOrEmpty(receipt.DeviceInstanceId)) return 0;
            int count = 0;
            foreach (NicModerationTarget target in targets)
                if (target != null
                    && string.Equals(target.NetCfgInstanceId, receipt.NetCfgInstanceId,
                        StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(target.DeviceInstanceId)
                    && string.Equals(target.DeviceInstanceId, receipt.DeviceInstanceId,
                        StringComparison.OrdinalIgnoreCase)) count++;
            return count;
        }
    }
}
