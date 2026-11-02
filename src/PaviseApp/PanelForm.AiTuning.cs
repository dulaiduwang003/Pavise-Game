using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private bool aiTuningPromptOpen;

        private void BuildAiTuningSettingsCard(Control parent)
        {
            var button = new PillButton(PerformancePolicy.Text("生成提示词", "Generate prompt"))
            { Name = "SettingsAiTuningGenerate", Size = new Size(Theme.S(152), Theme.S(40)), Bg = Theme.Card };
            button.Click += delegate { ShowAiTuningPrompt(); };
            AddSettingsTile(parent, "SettingsAiTuning", PerformancePolicy.Text("AI 调优建议", "AI tuning advice"),
                PerformancePolicy.Text("读取本机配置，生成包含全部功能说明与当前设置的提示词，复制给 AI 获取建议。", "Read hardware and generate a prompt explaining every feature and current configuration for your AI."), "info", button);
        }

        private void ShowAiTuningPrompt()
        {
            if (aiTuningPromptOpen || IsDisposed || Disposing) return;
            aiTuningPromptOpen = true;
            try
            {
                string features = AiTuningFeatureCatalog.Describe(gameMode);
                string display = Screen.FromControl(this).DeviceName;
                using (var dialog = new AiTuningPromptDialog(features, display)) ShowDim(dialog);
            }
            catch (Exception)
            {
                PaviseDialog.Warn(this, PerformancePolicy.Text("AI 调优提示词", "AI tuning prompt"),
                    PerformancePolicy.Text("未能生成完整功能说明，请重试。", "Could not generate the complete feature description. Please retry."));
            }
            finally { aiTuningPromptOpen = false; }
        }
    }
}
