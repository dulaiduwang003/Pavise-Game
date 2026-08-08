// @author bdth 2074055628@qq.com
// 文件用途 识别本机是否为可超频平台 供禁用 C-State 这类只对可超频平台有意义的功能门控

using System;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class OcPlatform
    {
        private static bool evaluated;
        private static bool supported;
        private static string reason = "";

        public static bool IdleDisableSupported
        {
            get { Ensure(); return supported; }
        }

        public static string IdleDisableGateReason
        {
            get { Ensure(); return reason; }
        }

        private static void Ensure()
        {
            if (evaluated) return;
            string cpu = "", board = "";
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    if (k != null) cpu = (k.GetValue("ProcessorNameString") as string) ?? "";
            }
            catch { }
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\BIOS"))
                    if (k != null) board = (k.GetValue("BaseBoardProduct") as string) ?? "";
            }
            catch { }
            supported = Evaluate(cpu, board, out reason);
            evaluated = true;
        }

        internal static bool Evaluate(string cpuName, string boardName, out string why)
        {
            string cpu = cpuName ?? "";
            string board = boardName ?? "";
            bool intel = cpu.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0;
            bool amd = cpu.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0;

            if (intel)
            {
                if (!Regex.IsMatch(cpu, @"\d{3,5}(KS|KF|K|XE|X)\b"))
                { why = "cpu-locked"; return false; }
                if (!Regex.IsMatch(board, @"\bZ\d{3}[A-Z]{0,2}\b|\bX(99|299)\b|\bW(680|790)\b"))
                { why = "board-locked"; return false; }
                why = "";
                return true;
            }
            if (amd)
            {
                if (cpu.IndexOf("Ryzen", StringComparison.OrdinalIgnoreCase) < 0
                    && cpu.IndexOf("Threadripper", StringComparison.OrdinalIgnoreCase) < 0)
                { why = "cpu-locked"; return false; }
                if (Regex.IsMatch(cpu, @"\d{3,5}\s*(U|H|HS|HX|M)\b"))
                { why = "cpu-mobile"; return false; }
                if (!Regex.IsMatch(board, @"\b[BX]\d{3}[A-Z]{0,2}\b|\bTRX\d{2}\b|\bWRX\d{2}\b"))
                { why = "board-locked"; return false; }
                why = "";
                return true;
            }
            why = "cpu-unknown";
            return false;
        }
    }
}
