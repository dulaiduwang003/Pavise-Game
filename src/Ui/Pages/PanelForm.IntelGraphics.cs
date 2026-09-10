// 文件用途 Intel 厂商页 用户开启的是临时的全局驱动改动 不是 XeSS 注入
using System;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private Toggle swIntelLowLatency;
#if PAVISE_SELFTEST
        internal Func<bool> IntelLowLatencyConfirmationForTest;
#endif

        private void BuildIntelGraphicsPage(Control scroll)
        {
            bool available = IntelGraphicsTweaks.HasAvailable;
            bool supported = available && IntelGraphicsTweaks.LowLatencySupported;
            swIntelLowLatency = MakeSwitch(gameMode.IntelLowLatency, null);
            swIntelLowLatency.AccessibleName = Lang.T("set.intel.lowlatency");
            swIntelLowLatency.Enabled = supported || gameMode.IntelLowLatency;
            swIntelLowLatency.CheckedChanged += delegate
            {
                bool on = swIntelLowLatency.Checked;
                if (on && !ConfirmIntelLowLatency())
                {
                    swIntelLowLatency.SetSilently(gameMode.IntelLowLatency);
                    return;
                }
                gameMode.IntelLowLatency = on;
                SyncIntelGraphicsToggles();
            };
            int height;
            string detail = Lang.T(!available ? "set.intel.none" : supported
                ? "set.intel.lowlatency.n" : "set.intel.lowlatency.unsupported");
            MakeAutoCard(scroll, 6, 2, ScrollContentW,
                FullTextCardHeight(detail, ScrollContentW, swIntelLowLatency, 88),
                Lang.T("set.intel.lowlatency"), detail,
                swIntelLowLatency, out height);

            // Endurance Gaming 只在有电池的 Intel 显卡机器上有东西可关 台式机这一行只解释为什么不可用
            //   这一页在隔离回归里单独构建 没有图形页的同步表 开关和低延迟那只一样手接
            bool enduranceOk = available && Native.HasSystemBattery();
            swIntelEndurance = MakeSwitch(gameMode.IntelEnduranceOff, null);
            swIntelEndurance.Enabled = enduranceOk || gameMode.IntelEnduranceOff;
            swIntelEndurance.CheckedChanged += delegate
            {
                gameMode.IntelEnduranceOff = swIntelEndurance.Checked;
                SyncIntelGraphicsToggles();
            };
            string enduranceDetail = !available ? Lang.T("set.intel.none")
                : enduranceOk ? Lang.T("set.intel.endurance.n") : Lang.T("set.intel.endurance.desktop");
            int enduranceH;
            MakeAutoCard(scroll, 6, 2 + height + 8, ScrollContentW,
                FullTextCardHeight(enduranceDetail, ScrollContentW, swIntelEndurance, 88),
                Lang.T("set.intel.endurance"), enduranceDetail, swIntelEndurance, out enduranceH);
            SyncIntelGraphicsToggles();
            EnableCardCollapse(scroll);
        }

        private bool ConfirmIntelLowLatency()
        {
            if (!IntelGraphicsTweaks.LowLatencySupported) return false;
#if PAVISE_SELFTEST
            if (IntelLowLatencyConfirmationForTest != null) return IntelLowLatencyConfirmationForTest();
            throw new InvalidOperationException("Intel confirmation requires an injected test response.");
#elif PAVISE_PERFLAB
            throw new InvalidOperationException("Intel confirmation is unavailable in the performance lab.");
#else
            return PaviseDialog.Confirm(this, Lang.T("set.intel.lowlatency"),
                Lang.T("intel.lowlatency.warn"), DlgKind.Warn);
#endif
        }

        private Toggle swIntelEndurance;

        private void SyncIntelGraphicsToggles()
        {
            if (swIntelEndurance != null)
            {
                // 没有档位会强制这项 只剩"本机不适用"这一种锁
                SettingCard enduranceCard = swIntelEndurance.Parent as SettingCard;
                bool usable = IntelGraphicsTweaks.HasAvailable && Native.HasSystemBattery();
                swIntelEndurance.SetSilently(gameMode.IntelEnduranceOff);
                swIntelEndurance.Enabled = gameMode.IntelEnduranceOff || usable;
                if (enduranceCard != null)
                    enduranceCard.SetLock(!gameMode.IntelEnduranceOff && !usable ? Lang.T("lock.na") : "", false);
            }
            if (swIntelLowLatency == null) return;
            SettingCard lowLatencyCard = swIntelLowLatency.Parent as SettingCard;
            if (lowLatencyCard != null)
                lowLatencyCard.SetLock(!gameMode.IntelLowLatency && !IntelGraphicsTweaks.LowLatencySupported
                    ? Lang.T("lock.na") : "", false);
            swIntelLowLatency.SetSilently(gameMode.IntelLowLatency);
            swIntelLowLatency.Enabled = IntelGraphicsTweaks.LowLatencySupported || gameMode.IntelLowLatency;
        }
    }
}
