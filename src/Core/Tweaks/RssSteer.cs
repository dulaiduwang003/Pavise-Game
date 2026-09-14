// @author bdth 2074055628@qq.com
// File purpose Steering network receive off game cores is retired; only cleanup of legacy RSS receipts remains, written back per receipt on launch and on clear
using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
    internal static class RssSteer
    {
        // Shipped in 2.1.3.4 and pulled right after: changing the RSS range makes wired NIC drivers reinitialize, dropping the link briefly, twice per match; users reported losing network in game
        //   Receipt key and write-back script kept; machines the old version wrote to get written back per receipt on launch, then the ledger is cleared
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
