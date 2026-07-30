// @author bdth 2074055628@qq.com
// 文件用途 把会话报告与证据记录解析为摘要卡数据

using System;
using System.Collections.Generic;
using System.Globalization;

namespace AegisApp
{
    internal sealed class SessionSummary
    {
        public string Time;
        public DateTime Stamp;
        public string Game;
        public string Preset;
        public string Duration;
        public string BoostText;
        public bool BoostVerified;
        public string ControlText;
        public string AegisCpuText;
        public string AvgFps;
        public string Low1Fps;
        public string Low01Fps;
        public string FrameCount;
        public readonly List<string> Chips = new List<string>();
    }

    internal static class SessionSummaries
    {
        private const int EvidenceMatchToleranceSeconds = 10;

        public static List<SessionSummary> Parse(string reportsTail, string evidenceTail, int max)
        {
            var list = new List<SessionSummary>();
            if (!string.IsNullOrEmpty(reportsTail))
                foreach (string raw in reportsTail.Split('\n'))
                {
                    SessionSummary summary = ParseReportLine(raw.TrimEnd('\r'));
                    if (summary != null) list.Add(summary);
                }
            list.Reverse();
            if (max > 0 && list.Count > max) list.RemoveRange(max, list.Count - max);
            if (!string.IsNullOrEmpty(evidenceTail))
                foreach (string raw in evidenceTail.Split('\n'))
                    AttachEvidence(list, raw.TrimEnd('\r'));
            return list;
        }

        internal static SessionSummary ParseReportLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            string[] f = line.Split(new[] { " | " }, StringSplitOptions.None);
            if (f.Length < 4) return null;
            DateTime stamp;
            if (!DateTime.TryParseExact(f[0], "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out stamp))
                return null;
            var summary = new SessionSummary
            {
                Time = f[0], Stamp = stamp, Game = f[1], Preset = f[2], Duration = f[3]
            };
            if (f.Length > 4)
            {
                summary.BoostText = f[4];
                summary.BoostVerified = f[4] == Lang.T("report.boost.ok");
            }
            if (f.Length > 5) summary.ControlText = f[5];
            if (f.Length > 6) summary.AegisCpuText = f[6];
            return summary;
        }

        private static void AttachEvidence(List<SessionSummary> list, string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            string[] f = line.Split(new[] { " | " }, StringSplitOptions.None);
            if (f.Length < 4) return;
            DateTime stamp;
            if (!DateTime.TryParseExact(f[0], "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out stamp))
                return;
            SessionSummary target = null;
            foreach (SessionSummary summary in list)
                if (string.Equals(summary.Game, f[1], StringComparison.OrdinalIgnoreCase)
                    && Math.Abs((summary.Stamp - stamp).TotalSeconds) <= EvidenceMatchToleranceSeconds)
                { target = summary; break; }
            if (target == null) return;
            for (int i = 3; i < f.Length; i++)
            {
                string part = f[i];
                if (part.IndexOf(" fps", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string avg, low1, low01, frames;
                    if (TryParseFrameStats(part, out avg, out low1, out low01, out frames))
                    {
                        target.AvgFps = avg; target.Low1Fps = low1;
                        target.Low01Fps = low01; target.FrameCount = frames;
                    }
                    continue;
                }
                foreach (string chip in part.Split('，'))
                {
                    string trimmed = chip.Trim();
                    if (trimmed.Length > 0) target.Chips.Add(trimmed);
                }
            }
        }

        // 解析 "平均 116 fps · 1%Low 17 · 0.1%Low 5（32259 帧）"
        internal static bool TryParseFrameStats(
            string part, out string avg, out string low1, out string low01, out string frames)
        {
            avg = null; low1 = null; low01 = null; frames = null;
            if (string.IsNullOrEmpty(part)) return false;
            string[] seg = part.Split(new[] { " · " }, StringSplitOptions.None);
            if (seg.Length < 3) return false;
            avg = FirstNumericToken(seg[0]);
            low1 = LastSpaceToken(seg[1]);
            int paren = seg[2].IndexOf('（');
            string low01Part = paren >= 0 ? seg[2].Substring(0, paren) : seg[2];
            low01 = LastSpaceToken(low01Part);
            if (paren >= 0) frames = FirstNumericToken(seg[2].Substring(paren + 1));
            return avg != null && low1 != null && low01 != null;
        }

        private static string FirstNumericToken(string text)
        {
            if (text == null) return null;
            int start = -1;
            for (int i = 0; i <= text.Length; i++)
            {
                bool digit = i < text.Length && (char.IsDigit(text[i]) || text[i] == '.');
                if (digit && start < 0) start = i;
                else if (!digit && start >= 0) return text.Substring(start, i - start);
            }
            return null;
        }

        private static string LastSpaceToken(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string trimmed = text.Trim();
            int space = trimmed.LastIndexOf(' ');
            if (space < 0 || space == trimmed.Length - 1) return null;
            string token = trimmed.Substring(space + 1);
            foreach (char c in token) if (!char.IsDigit(c) && c != '.') return null;
            return token;
        }
    }
}
