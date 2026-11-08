using System;
using System.Text;

namespace PaviseApp
{
    internal static class AiTuningPrompt
    {
        internal static string Build(string hardware, string features, string goals)
        {
            if (string.IsNullOrWhiteSpace(features)) throw new ArgumentException("The complete feature description is required.", "features");
            var stringBuilder = new StringBuilder();
            stringBuilder.AppendLine("按本机配置/需求逐项建议Pavise开/关/保留/按需并给短理由；模式/电源/预设指定选项。");
            stringBuilder.AppendLine("据目录列条件/代价；缺信息保留并列待补项。不猜实测、不审实现、不写泛泛保证/免责/自行测试建议。");
            stringBuilder.AppendLine();
            stringBuilder.AppendLine("Pavise " + App.Version + "；快照时间：" + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'"));
            if (!string.IsNullOrWhiteSpace(goals)) stringBuilder.AppendLine("用户补充：" + goals.Trim());
            stringBuilder.AppendLine();
            stringBuilder.AppendLine("【本机配置】");
            stringBuilder.AppendLine(string.IsNullOrWhiteSpace(hardware) ? "硬件未知（未读取）。" : hardware.Trim());
            stringBuilder.AppendLine();
            stringBuilder.AppendLine("【全部功能：用途与使用场景】");
            stringBuilder.AppendLine(features.Trim());
            // CF_UNICODETEXT and native EDIT controls terminate at an embedded NUL.
            return stringBuilder.ToString().Replace('\0', ' ');
        }
    }
}
