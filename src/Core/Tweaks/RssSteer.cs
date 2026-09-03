// @author bdth 2074055628@qq.com
// 文件用途 网络收包引离游戏核已下架 只保留旧版留下的 RSS 收据清收 启动与清除时按收据写回
using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
    internal static class RssSteer
    {
        // 2.1.3.4 上架 随后下架 改 RSS 区间会让有线网卡驱动重初始化 链路断一下 每局两次 用户反馈进游戏断网
        //   收据键与写回脚本保留 旧版写过的机器启动时按收据写回 之后清账
        private const string ReceiptKey = "RssSteerReceipt";
        private static readonly object lk = new object();

        public static bool HasResidue { get { return Settings.LoadStr(ReceiptKey, "").Length > 0; } }

        public static bool Restore()
        {
            lock (lk)
            {
                string receipt = Settings.LoadStr(ReceiptKey, "");
                if (receipt.Length == 0) return true;
                if (!RestoreWithReceipt(receipt)) { Logger.Log(Lang.T("log.rsssteer.5")); return false; }
                Settings.SaveStr(ReceiptKey, "");
                Logger.Log(Lang.T("log.rsssteer.6"));
                return true;
            }
        }

        public static void HealFromCrash()
        {
            if (HasResidue) Restore();
        }

        private static bool RestoreWithReceipt(string receipt)
        {
            string output;
            var args = new Dictionary<string, string> { { "PAVISE_RSS_RECEIPT", receipt } };
            if (!PsRunner.Run(RestoreScript, "rss-steer-restore", 30000, args, out output)) return false;
            return output.Contains("RESTOREOK");
        }

        internal static string ExtractReceipt(string output)
        {
            if (string.IsNullOrEmpty(output)) return null;
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim();
                if (line == "NONE") return "";
                if (line.StartsWith("RECEIPT:", StringComparison.Ordinal))
                    return line.Substring("RECEIPT:".Length);
            }
            return null;
        }

        private static readonly string RestoreScript = string.Join("\r\n", new[]
        {
            "$ErrorActionPreference = 'SilentlyContinue'",
            "$ok = $true",
            "foreach ($rec in $env:PAVISE_RSS_RECEIPT -split ';') {",
            "    $f = $rec -split '\\|'",
            "    if ($f.Count -ne 3) { continue }",
            "    $n = $f[0]",
            "    if ($null -eq (Get-NetAdapter -Name $n -ErrorAction SilentlyContinue)) { continue }",
            "    try { Set-NetAdapterRss -Name $n -BaseProcessorNumber ([byte][int]$f[1]) -MaxProcessorNumber ([byte][int]$f[2]) -ErrorAction Stop } catch { $ok = $false; continue }",
            "    $chk = Get-NetAdapterRss -Name $n",
            "    if ($null -eq $chk -or $chk.BaseProcessorNumber -ne [int]$f[1]) { $ok = $false }",
            "}",
            "if ($ok) { Write-Output 'RESTOREOK' } else { Write-Output 'RESTOREFAIL' }",
        });
    }
}
