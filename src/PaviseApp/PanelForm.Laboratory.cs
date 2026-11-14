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
                AutoText("插电笔记本使用托管电源方案时，在显卡繁忙、CPU 有余量时调整 EPP。核验失败、负载变化或退局时恢复，帧率效果待实测。",
                "Adjusts EPP on AC-powered laptops with a managed plan when the GPU is busy and CPU capacity is available. Restores on failed checks, workload changes or game exit. FPS benefits are untested."));
            AddLaboratoryToggle(parent, ref y, LaboratoryGpu.ClockKey, AutoText("GPU 频率锁最大值", "Lock GPU to maximum clock"),
                AutoText("对局请求最高 GPU 核心频率，仍受温度与功耗限制。关闭或退局后，NVIDIA 恢复自动调频，AMD 恢复原值。NVIDIA 无法保留其他工具的锁频设置，请勿混用。",
                "Requests the highest GPU core clock within thermal and power limits. Off or game exit restores automatic clocks on NVIDIA and original values on AMD. NVIDIA cannot preserve other tools' clock locks; avoid using them together."));
            AddLaboratoryToggle(parent, ref y, LaboratoryGpu.PowerKey, AutoText("GPU 功耗墙拉满", "Maximum GPU power limit"),
                AutoText("对局使用驱动允许的最高 GPU 功耗上限，关闭或退局恢复。可能增加发热，适合受功耗限制的负载。",
                "Uses the maximum supported GPU power limit during matches and restores on Off or game exit. May help power-limited workloads and increase heat."));
            AddLaboratoryToggle(parent, ref y, LaboratoryAudio.Key, AutoText("音频低缓冲流", "Low-buffer audio stream"),
                AutoText("对局请求默认输出设备的较短共享音频周期，关闭或退局释放。仅影响同设备、同模式，跳过蓝牙；可能增加唤醒或爆音。",
                "Requests a shorter shared-audio period on the default output during matches; released on Off or game exit. Applies to the same device and mode, excluding Bluetooth. May increase wakeups or crackling."));
            string detail = AutoText("插电时调整当前电源计划的 CPU 空闲策略。切档、换计划或退局时恢复，保留外部修改。禁止空闲可能增加功耗、发热并影响睿频。",
                "Adjusts CPU idle policy in the current power plan on AC power. Restores when changing strategy or plan, or leaving the game; preserves external edits. Disabling idle may increase heat and power use and affect boost.");
            string[] titles = { AutoText("跟随原策略", "Follow original"), AutoText("提高空闲门槛", "Raise thresholds"),
                AutoText("禁止 CPU 空闲", "Disable CPU idle") };
            string[] descriptions = {
                AutoText("恢复 Pavise 写入的空闲设置，跟随当前电源计划。", "Restore idle settings written by Pavise and follow the current power plan."),
                AutoText("交流供电时提高深层空闲门槛，切档或退局恢复。", "Raise deep-idle thresholds on AC; restore on strategy change or exit."),
                AutoText("交流供电时禁止 CPU 空闲，可能增加功耗与发热；退局恢复。", "Disable CPU idle on AC; heat and power may rise; restore on exit.") };
            var picker = new PerformanceProfilePicker(new[] { 0, 1, 2 }, index => titles[index], index => descriptions[index],
                () => AutoText("选择 CPU 空闲策略", "Choose CPU idle strategy")) { Name = "laboratoryCpuIdle",
                AccessibleName = AutoText("CPU 空闲策略", "CPU idle strategy"), AccessibleDescription = detail,
                Size = new Size(Theme.S(230), Theme.S(38)) };
            int height;
            SettingCard card = MakeAutoCard(parent, 6, y, ScrollContentW, FullTextCardHeight(detail, ScrollContentW, picker, 100),
                picker.AccessibleName, detail, picker, out height);
            y += height + 8;
            string[] choices = { "default", "threshold", "disabled" };
            // 与原分段控件一致，重复选择当前项不重写配置。
            int synchronizedIndex = picker.SelectedIndex;
            Action sync = delegate {
                picker.SetSilently(Array.IndexOf(choices, LaboratorySelection.CanonicalIdle(Settings.LoadStr(LaboratoryPower.IdleKey, "default"))));
                synchronizedIndex = picker.SelectedIndex;
                picker.AccessibleDescription = detail;
                picker.Enabled = !runtimeShutdownBusy && wipeBusy == 0;
                card.SetStatus(gameMode.LaboratoryState(LaboratoryPower.IdleKey), Theme.Dim);
            };
            picker.SelectionChanged += delegate {
                int index = picker.SelectedIndex;
                if (automationUpdating || index < 0 || index >= choices.Length || index == synchronizedIndex) return;
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
            // 既有开启意图始终可以关闭；能力发现不能删除已保存偏好。
            toggle.Enabled = toggle.Enabled && (available || toggle.Checked);
            card.SetLock(available ? "" : availability == null
                ? PerformancePolicy.Text("无法核验笔记本设备类型，本策略不可用", "Laptop device type could not be verified; policy unavailable") : availability.Detail, false);
            return available;
        }
    }
}
