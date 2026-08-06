// @author bdth 2074055628@qq.com
// 文件用途 实测 NVIDIA 驱动 Profile 的写入与回读是否真的生效

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static string NvFound(int found, uint value)
        {
            if (found < 0) return "读取失败";
            return found == 1 ? "0x" + value.ToString("X") + " (" + value + ")" : "未设置";
        }

        private static void RunNvProbe(string output, string exeArg)
        {
            string exeName = string.IsNullOrEmpty(exeArg) ? "PaviseNvProbe.exe" : exeArg;
            var sb = new StringBuilder();
            sb.AppendLine("=== NVIDIA 驱动 Profile 写入实测 ===");
            sb.AppendLine("目标 Profile: " + exeName);
            sb.AppendLine();

            sb.AppendLine("NvApi.Available = " + NvApi.Available);
            if (!NvApi.Available)
            {
                sb.AppendLine();
                sb.AppendLine("判定: 本机无可用的 NVIDIA 驱动接口，该功能整体停用。");
                File.WriteAllText(output, sb.ToString(), Encoding.UTF8);
                Console.WriteLine(sb.ToString());
                return;
            }

            IntPtr session;
            if (!NvApi.TryOpenSession(out session))
            {
                sb.AppendLine("判定: 无法打开驱动会话，写入不可能生效。");
                File.WriteAllText(output, sb.ToString(), Encoding.UTF8);
                Console.WriteLine(sb.ToString());
                return;
            }

            try
            {
                IntPtr profile;
                if (!NvApi.FindOrCreateAppProfile(session, exeName, out profile))
                {
                    sb.AppendLine("判定: 无法创建或找到应用 Profile，写入不可能生效。");
                    return;
                }
                sb.AppendLine("应用 Profile 已就绪");
                sb.AppendLine();

                sb.AppendLine("驱动版本: " + NvDrsTweaks.FormatDriver(NvApi.DriverVersion())
                    + "，DLSS 覆写门槛(566.14): " + (NvDrsTweaks.DlssOverrideSupported() ? "满足" : "不满足"));
                sb.AppendLine();

                var keys = new[]
                {
                    NvDrsTweaks.KeyPState, NvDrsTweaks.KeyFrl, NvDrsTweaks.KeyPreRender, NvDrsTweaks.KeyLowLatCpl,
                    NvDrsTweaks.KeyAnsel, NvDrsTweaks.KeyRebarFeat, NvDrsTweaks.KeyRebarOpt, NvDrsTweaks.KeyRebarSize,
                    NvDrsTweaks.KeyDlssOvr, NvDrsTweaks.KeyDlssPreset, NvDrsTweaks.KeyBattFps
                };
                var writeValues = new uint[]
                {
                    NvApi.PStatePreferMax, 120u, 1u, 3u,
                    0u, 1u, 1u, NvApi.RebarSizeDefault,
                    1u, NvApi.DlssPresetK, NvApi.BatteryFpsUncapped
                };

                sb.AppendLine("项目        设置 ID      原值         写入      回读         结论");
                for (int i = 0; i < keys.Length; i++)
                {
                    uint settingId = NvDrsTweaks.SettingIdOf(keys[i]);
                    uint before;
                    int foundBefore = NvApi.TryGetDword(session, profile, settingId, out before);

                    int status;
                    bool wrote = NvApi.SetDword(session, profile, settingId, writeValues[i], out status);
                    bool saved = wrote && NvApi.SaveSession(session);

                    uint after;
                    int foundAfter = NvApi.TryGetDword(session, profile, settingId, out after);

                    string verdict;
                    if (!wrote) verdict = "写入被拒绝 (NVAPI " + status + ")";
                    else if (!saved) verdict = "写入接受但保存失败";
                    else if (foundAfter == 1 && after == writeValues[i]) verdict = "生效";
                    else verdict = "写入报成功但回读不符 (" + NvFound(foundAfter, after) + ")";

                    sb.AppendLine(keys[i].PadRight(12)
                        + ("0x" + settingId.ToString("X8")).PadRight(13)
                        + NvFound(foundBefore, before).PadRight(13)
                        + writeValues[i].ToString().PadRight(10)
                        + NvFound(foundAfter, after).PadRight(13)
                        + verdict);

                    if (foundBefore == 1) NvApi.SetDword(session, profile, settingId, before);
                    else if (foundBefore == 0) NvApi.DeleteSetting(session, profile, settingId);
                    NvApi.SaveSession(session);
                }

                sb.AppendLine();
                sb.AppendLine("已按原值还原（原本未设置的项已删除）。");
                sb.AppendLine();
                sb.AppendLine("说明: pstate = 电源最高性能, frl = 帧率上限, prerender = 最大预渲染帧数,");
                sb.AppendLine("      lowlatcpl = 低延迟模式(已于 1.6.1 移除), ansel = Ansel 注入开关,");
                sb.AppendLine("      rebar* = ReBAR 强开三件套, dlss* = DLSS 覆写(566+ 驱动生效),");
                sb.AppendLine("      battfps = 电池限帧覆盖(0x3FF = 顶到上限)");
                sb.AppendLine();
                sb.AppendLine("后台硬限帧（设置 ID 按名称从驱动数据库运行时发现）:");
                uint bgId;
                string bgName;
                if (!NvGlobalTweaks.TryResolveBgSetting(out bgId, out bgName))
                {
                    uint[] allIds = NvApi.EnumAvailableSettingIds();
                    sb.AppendLine("  未发现后台帧率上限项（驱动可用设置枚举: "
                        + (allIds == null ? "失败" : allIds.Length + " 项") + "），该功能在本机停用");
                }
                else
                {
                    sb.AppendLine("  发现「" + bgName + "」= 0x" + bgId.ToString("X8"));
                    IntPtr baseProfile;
                    if (!NvApi.TryGetBaseProfile(session, out baseProfile))
                        sb.AppendLine("  基础 Profile 获取失败，写入实测跳过");
                    else
                    {
                        uint bgBefore;
                        int bgFound = NvApi.TryGetDword(session, baseProfile, bgId, out bgBefore);
                        int bgStatus;
                        bool bgWrote = NvApi.SetDword(session, baseProfile, bgId, 20u, out bgStatus);
                        bool bgSaved = bgWrote && NvApi.SaveSession(session);
                        uint bgAfter;
                        int bgFoundAfter = NvApi.TryGetDword(session, baseProfile, bgId, out bgAfter);
                        string bgVerdict = !bgWrote ? "写入被拒绝 (NVAPI " + bgStatus + ")"
                            : !bgSaved ? "写入接受但保存失败"
                            : bgFoundAfter == 1 && bgAfter == 20u ? "生效"
                            : "写入报成功但回读不符";
                        sb.AppendLine("  基础 Profile 写入实测(20fps): " + bgVerdict
                            + "（原值 " + NvFound(bgFound, bgBefore) + "，已按原值还原）");
                        if (bgWrote)
                        {
                            if (bgFound == 1) NvApi.SetDword(session, baseProfile, bgId, bgBefore);
                            else NvApi.DeleteSetting(session, baseProfile, bgId);
                            NvApi.SaveSession(session);
                        }
                    }
                }
            }
            finally { NvApi.CloseSession(session); }

            sb.AppendLine();
            {
                bool rebarOn;
                ulong rebarWindow;
                string rebarGpu;
                sb.AppendLine("ReBAR 检测: " + (RebarProbe.TryDetect(out rebarOn, out rebarWindow, out rebarGpu)
                    ? (rebarOn ? "已开启" : "未开启") + " · 窗口 " + RebarProbe.WindowText(rebarWindow)
                        + (rebarGpu == null ? "" : " · " + rebarGpu)
                    : "读取失败或无独显"));
            }

            sb.AppendLine();
            sb.AppendLine("驱动命名设置全量（诊断用）:");
            uint[] namedIds = NvApi.EnumAvailableSettingIds();
            if (namedIds != null)
            {
                var lines = new List<string>();
                foreach (uint id in namedIds)
                {
                    string nm;
                    if (NvApi.TryGetSettingName(id, out nm))
                        lines.Add("  0x" + id.ToString("X8") + "  " + nm);
                }
                lines.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string line in lines) sb.AppendLine(line);
            }

            string text = sb.ToString();
            File.WriteAllText(output, text, Encoding.UTF8);
            Console.WriteLine(text);
        }
    }
}
