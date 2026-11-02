using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private void BuildLaboratoryOptions(Control parent, ref int y)
        {
            AddLaboratoryToggle(parent, ref y, LaboratoryPower.EppKey, AutoText("EPP 功耗让路", "EPP power yield"),
                AutoText("仅插电笔记本、托管电源方案且功耗可读时尝试。显卡繁忙、CPU 有余量时调整 EPP，验证失败或瓶颈转移则恢复；退局恢复。对帧率的收益尚待验证。",
                "Tries EPP changes on AC-powered laptops with a managed plan and readable power. Reverts on failed verification, a shifted bottleneck or session exit. Frame-rate benefits are unverified."));
            AddLaboratoryToggle(parent, ref y, LaboratoryGpu.ClockKey, AutoText("GPU 频率锁最大值", "Lock GPU to maximum clock"),
                AutoText("对局中请求驱动支持的最高核心频率，仍受温度、功耗限制。NVIDIA 关闭或退局恢复自动调频，不能还原其他工具预先设置的锁频，也不能识别其后续锁频改动；勿与其他锁频工具同时使用。AMD 按保存原值恢复。",
                "Requests the highest supported core clock during matches, subject to thermal and power limits. NVIDIA returns to automatic clocks on Off/exit; prior or subsequent external locks cannot be preserved or detected. Avoid other clock-locking tools. AMD restores saved values."));
            AddLaboratoryToggle(parent, ref y, LaboratoryGpu.PowerKey, AutoText("GPU 功耗墙拉满", "Maximum GPU power limit"),
                AutoText("对局中把受支持显卡的功耗上限调到驱动允许的最大值，关闭或退局恢复原值。仅受功耗限制的场景可能获益，也可能增加发热；不改变电压。",
                "Uses the maximum driver-supported GPU power limit during matches, restoring the original on Off/exit. Only power-limited workloads may benefit; heat may increase. Does not change voltage."));
            AddLaboratoryToggle(parent, ref y, LaboratoryAudio.Key, AutoText("音频低缓冲流", "Low-buffer audio stream"),
                AutoText("对局中为默认输出设备申请较短共享音频周期，显示实际读回周期；仅影响同设备、同模式的共享音频。跳过蓝牙与不支持设备，可能增加唤醒或出现爆音，关闭或退局释放。",
                "Requests a shorter shared-engine period on the default output and reports the actual period. Covers only shared audio on that endpoint and mode. Skips Bluetooth/unsupported devices; may increase wakeups or cause crackles. Released on Off/exit."));
            string detail = AutoText("支持当前 Windows 电源计划，无需 Pavise 托管；仅交流供电时执行。提高深层空闲门槛与禁止 CPU 空闲二选一，切档或换计划先恢复已有改动；跟随原策略及退局恢复原值。外部修改优先保留。禁止空闲可能增加功耗、发热并影响睿频。",
                "Works with the current Windows power plan without requiring Pavise management, on AC power only. Choose raised deep-idle thresholds or disabled CPU idle. Restores existing changes before switching strategy or plan, and originals on Follow/exit. External edits take precedence. Disabling idle may increase heat/power and affect boost.");
            var picker = new TierPicker { Name = "laboratoryCpuIdle", Labels = new[] { AutoText("跟随原策略", "Follow original"),
                AutoText("提高空闲门槛", "Raise thresholds"), AutoText("禁止 CPU 空闲", "Disable CPU idle") },
                AccessibleName = AutoText("CPU 空闲策略", "CPU idle strategy"), AccessibleDescription = detail,
                Size = new Size(Theme.S(440), Theme.S(30)) };
            int height;
            SettingCard card = MakeAutoCard(parent, 6, y, ScrollContentW, FullTextCardHeight(detail, ScrollContentW, picker, 100),
                picker.AccessibleName, detail, picker, out height);
            y += height + 8;
            string[] choices = { "default", "threshold", "disabled" };
            Action sync = delegate {
                picker.Index = Array.IndexOf(choices, LaboratorySelection.CanonicalIdle(Settings.LoadStr(LaboratoryPower.IdleKey, "default")));
                picker.Enabled = !runtimeShutdownBusy && wipeBusy == 0;
                card.SetStatus(gameMode.LaboratoryState(LaboratoryPower.IdleKey), Theme.Dim);
            };
            picker.IndexChanged = delegate(int index) {
                if (automationUpdating || index < 0 || index >= choices.Length) return;
                bool ok = SaveLaboratoryPreference(LaboratoryPower.IdleKey, choices[index]);
                sync(); if (!ok) ShowManualPerformanceSaveFailure();
            };
            automationSync.Add(sync);
        }
        private bool SaveLaboratoryPreference(string key, string value)
        {
            if (runtimeShutdownBusy || wipeBusy != 0 || !LaboratorySelection.IsPreference(key)) return false;
            if (!Settings.SaveStrDurable(key, value) || Settings.LoadStr(key, "") != value) return false;
            gameMode.Poke();
            return true;
        }
        private void AddLaboratoryToggle(Control parent, ref int y, string key, string title, string detail)
        {
            Toggle toggle = MakeSwitch(Settings.Load(key, false), null);
            toggle.Name = "laboratory_" + key; toggle.AccessibleName = title; toggle.AccessibleDescription = detail;
            int height;
            SettingCard card = MakeAutoCard(parent, 6, y, ScrollContentW, FullTextCardHeight(detail, ScrollContentW, toggle, 108), title, detail, toggle, out height);
            y += height + 8;
            Action sync = delegate {
                toggle.SetSilently(Settings.Load(key, false)); toggle.Enabled = !runtimeShutdownBusy && wipeBusy == 0;
                bool available = ApplyLaboratoryToggleAvailability(key, toggle, card,
                    key == LaboratoryPower.EppKey ? LaboratoryEppEligibility.Current : null);
                card.SetStatus(available ? gameMode.LaboratoryState(key) : "", Theme.Dim);
            };
            toggle.CheckedChanged += delegate {
                if (automationUpdating) return;
                if (key == LaboratoryPower.EppKey && toggle.Checked && !LaboratoryEppEligibility.Current.Available) { sync(); return; }
                bool ok = SaveLaboratoryPreference(key, toggle.Checked ? "1" : "0");
                sync(); if (!ok) ShowManualPerformanceSaveFailure();
            };
            sync();
            automationSync.Add(sync);
        }
        internal static bool ApplyLaboratoryToggleAvailability(string key, Toggle toggle, SettingCard card, LaboratoryEppAvailability availability)
        {
            if (key != LaboratoryPower.EppKey) return true;
            bool available = availability != null && availability.Available;
            // Existing intent can still be turned off; discovery never erases saved preferences.
            toggle.Enabled = toggle.Enabled && (available || toggle.Checked);
            card.SetLock(available ? "" : availability == null
                ? PerformancePolicy.Text("无法核验笔记本设备类型，本策略不可用", "Laptop device type could not be verified; policy unavailable") : availability.Detail, false);
            return available;
        }
    }
}
