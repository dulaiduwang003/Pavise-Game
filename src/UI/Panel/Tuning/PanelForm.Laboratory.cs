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
                AutoText("支持绝对频率调节的 AMD 独显可在对局中将核心频率下限提高到当前设定的上限，关闭或退局后恢复原下限。NVIDIA 请求锁定最高核心频率，关闭或退局时恢复自动调频，其他工具的锁频设置会丢失。实际频率仍受温度和功耗限制。",
                "On AMD discrete GPUs that support absolute clock limits, Pavise raises the minimum core clock to the configured maximum and restores the previous minimum when disabled or the game exits. On NVIDIA, it requests a maximum clock lock and returns to automatic clocks when disabled or the game exits. This clears any previous NVIDIA clock lock. Temperature and power limits still apply."));
            AddLaboratoryToggle(parent, ref y, LaboratoryGpu.PowerKey, AutoText("GPU 功耗墙拉满", "Maximum GPU power limit"),
                AutoText("对局中，在驱动支持的 AMD 或 NVIDIA 独显上，将功耗上限设为驱动允许的最高值。可能增加发热。关闭或退局时恢复原上限，保留其他工具后续的修改。",
                "During a match, Pavise sets a supported AMD or NVIDIA GPU to its highest driver-allowed power limit. This may increase heat. Disabling the option or exiting the game restores the previous limit and preserves later changes from other tools."));
            string detail = AutoText("插电时调整当前电源计划的 CPU 空闲策略。切档、换计划或退局时恢复，保留外部修改。",
                "Adjusts CPU idle policy in the current power plan on AC power. Restores when changing strategy or plan, or leaving the game; preserves external edits.");
            // 不提供禁止 CPU 空闲这一档 新 CPU 上会压低睿频 帧数反而可能下降
            string[] titles = { AutoText("跟随原策略", "Follow original"), AutoText("提高空闲门槛", "Raise thresholds") };
            string[] descriptions = {
                AutoText("恢复 Pavise 写入的空闲设置，跟随当前电源计划。", "Restore idle settings written by Pavise and follow the current power plan."),
                AutoText("交流供电时提高深层空闲门槛，切档或退局恢复。", "Raise deep-idle thresholds on AC; restore on strategy change or exit.") };
            var picker = new PerformanceProfilePicker(new[] { 0, 1 }, index => titles[index], index => descriptions[index],
                () => AutoText("选择 CPU 空闲策略", "Choose CPU idle strategy")) { Name = "laboratoryCpuIdle",
                AccessibleName = AutoText("CPU 空闲策略", "CPU idle strategy"), AccessibleDescription = detail,
                Size = new Size(Theme.S(230), Theme.S(38)) };
            int height;
            var card = (PerformanceFeatureCard)MakeAutoCard(parent, 6, y, ScrollContentW, FullTextCardHeight(detail, ScrollContentW, picker, 100),
                picker.AccessibleName, detail, picker, out height);
            card.PickerInHeader = true;
            card.StartsNewRow = true;
            y += height + 8;
            string[] choices = { "default", "threshold" };
            // 重复选择当前项不重写配置
            int synchronizedIndex = picker.SelectedIndex;
            Action sync = delegate {
                picker.SetSilently(Array.IndexOf(choices, LaboratorySelection.CanonicalIdle(Settings.LoadStr(LaboratoryPower.IdleKey, "default"))));
                synchronizedIndex = picker.SelectedIndex;
                picker.AccessibleDescription = detail;
                picker.Enabled = !runtimeShutdownBusy && wipeBusy == 0;
                card.SetStatus(gameMode.Laboratory.State(LaboratoryPower.IdleKey), Theme.Dim);
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
                card.SetStatus(available ? gameMode.Laboratory.State(key) : "", Theme.Dim);
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
            // 既有开启意图始终可以关闭 能力发现不能删除已保存偏好
            toggle.Enabled = toggle.Enabled && (available || toggle.Checked);
            card.SetLock(available ? "" : availability == null
                ? PerformancePolicy.Text("无法核验笔记本设备类型，本策略不可用", "Laptop device type could not be verified; policy unavailable") : availability.Detail, false);
            return available;
        }
    }
}
