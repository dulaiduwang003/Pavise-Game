using System;
using System.Text;

namespace PaviseApp
{
    internal static class AiTuningPrompt
    {
        internal static string Build(string hardware, string features, string goals)
        {
            if (string.IsNullOrWhiteSpace(features)) throw new ArgumentException("The complete feature description is required.", "features");
            if (Lang.Effective == Lang.Portuguese) return BuildPortuguese(hardware, features, goals);
            var stringBuilder = new StringBuilder();
            stringBuilder.AppendLine("按本机配置/需求逐项建议Pavise开/关/保留/按需并给短理由；模式/电源指定选项。");
            stringBuilder.AppendLine("据目录列条件/代价；缺信息保留并列待补项。不猜实测、不审实现、不写泛泛保证/免责/自行测试建议。");
            // 正文是交给 AI 读的材料 中英文界面均使用中文正文 英文界面请 AI 以英文回答
            if (Lang.Effective != Lang.Chinese) stringBuilder.AppendLine(Lang.Text("", "Answer in English."));
            stringBuilder.AppendLine();
            stringBuilder.AppendLine("Pavise " + App.Version + "；快照时间：" + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'"));
            if (!string.IsNullOrWhiteSpace(goals)) stringBuilder.AppendLine("用户补充：" + goals.Trim());
            stringBuilder.AppendLine();
            stringBuilder.AppendLine("【本机配置】");
            stringBuilder.AppendLine(string.IsNullOrWhiteSpace(hardware) ? "硬件未知（未读取）。" : hardware.Trim());
            stringBuilder.AppendLine();
            stringBuilder.AppendLine("【全部功能：用途与使用场景】");
            stringBuilder.AppendLine(features.Trim());
            // CF_UNICODETEXT 和原生 EDIT 控件都会在内嵌的 NUL 处截断
            return stringBuilder.ToString().Replace('\0', ' ');
        }
        private static string BuildPortuguese(string hardware, string features, string goals)
        {
            var text = new StringBuilder();
            text.AppendLine("Para cada recurso do Pavise, recomende ativar, desativar, manter ou usar conforme a necessidade, com uma breve justificativa baseada neste computador e nos objetivos informados. Indique opções para modo e energia. Responda em português do Brasil.");
            text.AppendLine("Informe as condições e os custos descritos no catálogo. Se faltarem dados, mantenha a configuração e diga quais informações faltam. Não invente medições, não revise a implementação e não inclua garantias, avisos ou sugestões genéricas para testar por conta própria.");
            text.AppendLine();
            text.AppendLine("Pavise " + App.Version + " · instantâneo em " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'"));
            if (!string.IsNullOrWhiteSpace(goals)) text.AppendLine("Objetivos informados: " + goals.Trim());
            text.AppendLine();
            text.AppendLine("【Configuração do computador】");
            text.AppendLine(string.IsNullOrWhiteSpace(hardware) ? "Hardware desconhecido (não foi lido)." : hardware.Trim());
            text.AppendLine();
            text.AppendLine("【Todos os recursos: finalidades e situações de uso】");
            text.AppendLine(features.Trim());
            return text.ToString().Replace('\0', ' ');
        }
    }
}
