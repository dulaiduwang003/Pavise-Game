// @author bdth 2074055628@qq.com
// 文件用途 网卡中断合并实验策略的纯状态机 收据先行 身份核对 回读与保守回滚
using System;
using System.Collections.Generic;
using System.Text;

namespace PaviseApp
{
    // 标准 *InterruptModeration 只有 0/1。Adaptive/Medium 属于厂商私有算法，
    // 没有经过 PCI/驱动/INF 指纹复核时绝不能在这里猜值。
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
        // Unknown 既可能是已成功读到私有/缺失值，也可能是读取异常；只有前者
        // 才能视为外部接管并结清恢复收据。
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
                // NIM2 只表达本版本唯一授权的方向：驱动管理(1) -> 实验 Off(0)。
                // 语法正确但方向反转的票据也必须按损坏处理，恢复路径不能被它
                // 诱导去执行本策略从未建立过的写入。
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
                // 先统计所有活动物理有线设备，再检查唯一出口是否适合写入。
                // 不能只统计暴露 *InterruptModeration 的设备，否则第二块不支持
                // 标准项的 USB/厂商网卡会被漏掉，错误地放行第一块。
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
                // 外部已经关闭不等于 Pavise 接管，绝不制造一张假收据。
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
                // 这三类明确发生在写入之前，清掉 Prepared 即可；ReadbackFailed
                // 可能已经写入，必须走同一条 CAS 恢复路径。
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

        // 启动对账覆盖两类崩溃窗口：Prepared 写入未确认，以及 Applied 已写回
        // Original 但收据尚未来得及清除。Applied+Desired 是用户已建立的实验，
        // 只确认存在，不在正常启动时擅自还原。
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

        // 仅供用户明确执行“清除全部”时放弃不可读/损坏/设备已卸载的应用收据。
        // 此动作不触碰系统网卡值；普通关闭路径从不调用它。
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
                // 用户、驱动或其它工具已经接管。不能拿旧收据覆盖；结清 Pavise
                // 所有权，日志仍会保留 ExternalChanged 供诊断。
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
                // NetCfg GUID 只负责定位接口；完整 PnP 实例 ID 是必须匹配的第二把锁。
                // 任一侧缺失都不能退化成“只凭 GUID”写回旧值。
                if (string.IsNullOrEmpty(receipt.DeviceInstanceId)
                    || string.IsNullOrEmpty(target.DeviceInstanceId)
                    || !string.Equals(target.DeviceInstanceId, receipt.DeviceInstanceId,
                        StringComparison.OrdinalIgnoreCase)) continue;
                // 驱动重装可能短暂留下重复类键；无法证明哪一项代表当前设备时
                // 不能按枚举顺序任选一个写回。
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
