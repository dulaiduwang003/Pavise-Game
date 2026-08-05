// @author bdth 2074055628@qq.com
// 文件用途 实测 NVIDIA 驱动 Profile 的写入与回读是否真的生效

using System;
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

                var keys = new[] { NvDrsTweaks.KeyPState, NvDrsTweaks.KeyFrl, NvDrsTweaks.KeyPreRender, NvDrsTweaks.KeyLowLatCpl };
                var writeValues = new uint[] { NvApi.PStatePreferMax, 120u, 1u, 3u };

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
                sb.AppendLine("说明: pstate = 电源管理模式最高性能, frl = 帧率上限,");
                sb.AppendLine("      prerender = 最大预渲染帧数, lowlatcpl = 低延迟模式(已于 1.6.1 移除)");
            }
            finally { NvApi.CloseSession(session); }

            string text = sb.ToString();
            File.WriteAllText(output, text, Encoding.UTF8);
            Console.WriteLine(text);
        }
    }
}
