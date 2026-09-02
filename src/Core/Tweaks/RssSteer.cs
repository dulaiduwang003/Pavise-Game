// @author bdth 2074055628@qq.com
// 文件用途 对局中把网卡 RSS 收包处理引到压制核区 逐网卡收据记账 退局还原
using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
    internal static class RssSteer
    {
        // RSS 决定收包后续 DPC 和协议栈跑在哪些核 默认从 CPU0 铺开 会压上游戏核
        //   IRQ 亲和只管 ISR 这里补上 DPC 半边 目标区取拓扑的压制核掩码
        //   只动物理有线网卡 收据按网卡名记账 名字含分隔符的跳过不碰
        private const string ReceiptKey = "RssSteerReceipt";
        private static readonly object lk = new object();

        public static bool HasResidue { get { return Settings.LoadStr(ReceiptKey, "").Length > 0; } }

        // 压制核掩码里最低和最高的逻辑核号就是引导区间 区间无效就不支持
        internal static bool TryTargetRange(ulong throttleMask, ulong allMask, bool multiGroup,
            out int baseCpu, out int maxCpu)
        {
            baseCpu = maxCpu = 0;
            if (multiGroup || throttleMask == 0 || allMask == 0 || throttleMask == allMask) return false;
            int low = -1, high = -1;
            for (int i = 0; i < 64; i++)
                if ((throttleMask >> i & 1UL) != 0) { if (low < 0) low = i; high = i; }
            if (low < 0 || high == low) return false;
            baseCpu = low;
            maxCpu = high;
            return true;
        }

        public static bool Supported()
        {
            int baseCpu, maxCpu;
            return TryTargetRange(CpuTopology.ThrottleMask, CpuTopology.AllMask,
                CpuTopology.MultiGroup, out baseCpu, out maxCpu);
        }

        public static bool Activate()
        {
            lock (lk)
            {
                if (Settings.LoadStr(ReceiptKey, "").Length > 0) return true;
                int baseCpu, maxCpu;
                if (!TryTargetRange(CpuTopology.ThrottleMask, CpuTopology.AllMask,
                    CpuTopology.MultiGroup, out baseCpu, out maxCpu))
                { Logger.Log(Lang.T("log.rsssteer.1")); return false; }
                string output;
                var args = new Dictionary<string, string>
                {
                    { "PAVISE_RSS_BASE", baseCpu.ToString(CultureInfo.InvariantCulture) },
                    { "PAVISE_RSS_MAX", maxCpu.ToString(CultureInfo.InvariantCulture) },
                };
                if (!PsRunner.Run(ApplyScript, "rss-steer-apply", 30000, args, out output))
                { Logger.Warn(Lang.T("log.rsssteer.2")); return false; }
                string receipt = ExtractReceipt(output);
                if (receipt == null) { Logger.Warn(Lang.T("log.rsssteer.2")); return false; }
                if (receipt.Length == 0)
                {
                    // 没有一张网卡符合条件 不算失败也不留账 下轮还会再试
                    Logger.Log(Lang.T("log.rsssteer.3"));
                    return true;
                }
                Settings.SaveStr(ReceiptKey, receipt);
                if (Settings.LoadStr(ReceiptKey, "") != receipt)
                {
                    RestoreWithReceipt(receipt);
                    return false;
                }
                Logger.Log(Lang.T("log.rsssteer.4") + baseCpu + "-" + maxCpu);
                return true;
            }
        }

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

        // 单次往返 逐网卡 记旧值 写新值 读回校验 校验不过当场回写旧值不入账
        private static readonly string ApplyScript = string.Join("\r\n", new[]
        {
            "$ErrorActionPreference = 'SilentlyContinue'",
            "$base = [byte][int]$env:PAVISE_RSS_BASE",
            "$max = [byte][int]$env:PAVISE_RSS_MAX",
            "$records = @()",
            "$adapters = Get-NetAdapter -Physical | Where-Object { $_.Status -eq 'Up' -and $_.MediaType -eq '802.3' }",
            "foreach ($a in $adapters) {",
            "    $n = $a.Name",
            "    if ($n -match '[|;]') { continue }",
            "    $r = Get-NetAdapterRss -Name $n",
            "    if ($null -eq $r -or -not $r.Enabled) { continue }",
            "    if ($r.BaseProcessorGroup -ne 0) { continue }",
            "    $ob = $r.BaseProcessorNumber",
            "    $om = $r.MaxProcessorNumber",
            "    if ($null -eq $ob -or $null -eq $om) { continue }",
            "    if ($ob -eq $base -and $om -eq $max) { continue }",
            "    try { Set-NetAdapterRss -Name $n -BaseProcessorNumber $base -MaxProcessorNumber $max -ErrorAction Stop } catch { continue }",
            "    $chk = Get-NetAdapterRss -Name $n",
            "    if ($null -ne $chk -and $chk.BaseProcessorNumber -eq $base -and $chk.MaxProcessorNumber -eq $max) {",
            "        $records += ($n + '|' + $ob + '|' + $om)",
            "    } else {",
            "        try { Set-NetAdapterRss -Name $n -BaseProcessorNumber $ob -MaxProcessorNumber $om -ErrorAction Stop } catch { }",
            "    }",
            "}",
            "if ($records.Count -gt 0) { Write-Output ('RECEIPT:' + ($records -join ';')) } else { Write-Output 'NONE' }",
        });

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
