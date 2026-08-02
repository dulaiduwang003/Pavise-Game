// @author bdth 2074055628@qq.com
// 文件用途 诊断指定进程的调度与效率模式原始状态 只读不写

using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static void RunQosProbe(string output, string target)
        {
            var sb = new StringBuilder();
            var matches = new System.Collections.Generic.List<Process>();
            int pid;
            if (int.TryParse(target ?? "", out pid))
            {
                try { matches.Add(Process.GetProcessById(pid)); } catch { }
            }
            else if (!string.IsNullOrEmpty(target))
            {
                string want = target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? target.Substring(0, target.Length - 4) : target;
                matches.AddRange(Process.GetProcessesByName(want));
            }

            sb.AppendLine("=== 进程调度状态诊断（只读）===");
            if (matches.Count == 0)
            {
                sb.AppendLine("未找到目标进程：" + (target ?? "(未指定)"));
                File.WriteAllText(output, sb.ToString(), Encoding.UTF8);
                Environment.ExitCode = 2;
                return;
            }

            foreach (Process p in matches)
            {
                sb.AppendLine();
                sb.AppendLine("--- " + p.ProcessName + " (pid " + p.Id + ") ---");
                IntPtr h = Native.OpenProcess(
                    Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION
                    | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                if (h == IntPtr.Zero)
                {
                    sb.AppendLine("  打不开句柄（错误 "
                        + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + "）");
                    continue;
                }
                try
                {
                    uint pri = Native.GetPriorityClass(h);
                    sb.AppendLine("  优先级类   : 0x" + pri.ToString("X") + " " + PriorityName(pri));
                    sb.AppendLine("  IO 优先级  : " + Native.QueryIoPriority(h) + "（3=高）");

                    if (!Native.PowerThrottlingSupported)
                        sb.AppendLine("  效率模式   : 本系统不支持查询");
                    else
                    {
                        int ctl, st;
                        bool ok = Native.TryQueryPowerThrottling(h, out ctl, out st);
                        sb.AppendLine("  QoS 原始位 : 查询" + (ok ? "成功" : "失败")
                            + "  ControlMask=0x" + ctl.ToString("X") + "  StateMask=0x" + st.ToString("X"));
                        sb.AppendLine("    执行速度节流位（bit0）: 控制="
                            + ((ctl & 1) != 0 ? "由应用接管" : "交还系统")
                            + "，状态=" + ((st & 1) != 0 ? "开启节流(EcoQoS)" : "关闭节流"));
                        sb.AppendLine("    忽略计时器分辨率(bit2): 控制="
                            + ((ctl & 4) != 0 ? "由应用接管" : "交还系统")
                            + "，状态=" + ((st & 4) != 0 ? "置位" : "未置位"));
                        sb.AppendLine("  提优判据    : " + (GameMode.HighQoSVerified(h)
                            ? "已退出效率模式" : "未退出（判据要求 控制位=接管 且 状态位=关闭）"));
                    }
                }
                finally { Native.CloseHandle(h); }
            }

            sb.AppendLine();
            sb.AppendLine("说明：任务管理器显示绿叶子的条件不只看 EcoQoS，进程优先级为 Idle 时同样会显示。");
            sb.AppendLine("若上方 状态=关闭节流 且 优先级不是 Idle，而任务管理器仍显示效率模式，");
            sb.AppendLine("则多半是该进程的子进程仍处于效率模式，或任务管理器界面尚未刷新。");

            File.WriteAllText(output, sb.ToString(), Encoding.UTF8);
            Console.Write(sb.ToString());
            foreach (Process p in matches) { try { p.Dispose(); } catch { } }
        }

        private static string PriorityName(uint pri)
        {
            if (pri == Native.HIGH_PRIORITY_CLASS) return "(高)";
            if (pri == 0x20) return "(普通)";
            if (pri == 0x40) return "(Idle 空闲)";
            if (pri == 0x4000) return "(低于普通)";
            if (pri == 0x8000) return "(高于普通)";
            if (pri == 0x100) return "(实时)";
            return "";
        }
    }
}
