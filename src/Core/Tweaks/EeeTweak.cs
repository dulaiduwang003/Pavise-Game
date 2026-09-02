// @author bdth 2074055628@qq.com
// 文件用途 关闭物理有线网卡的链路节能 节能以太网 绿色以太网 链路降速 逐网卡逐属性记原值 按收据写回
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    // 802.3az 让链路在低流量时进入 LPI 睡眠 唤醒是微秒到毫秒级 表现为收发抖动尖峰
    //   改高级属性会让网卡重新协商链路 断几秒 所以这是环境页的持久项 不进会话路径
    //   各家驱动的键名不同 按 RegistryKeyword 和 DisplayName 两路匹配 只认 0 为关闭
    //   本机 Realtek 实测有一项 EEEMaxSupportSpeed 是速率不是开关 名字也以 EEE 开头
    //   所以只碰合法值里含 0 且不超过三档的枚举项 速率和数值项一律不动
    //   链路降速那几项 Realtek 的 Gigabit Lite 和 Power Saving Mode 本机实测键名 Intel 的 SipsEnabled 按文档
    //   它们在空闲时把链路从 1G 降到 100M 再协商回来 微软文档明说切换期间网卡会短暂断连
    //   一条记录一张收据 网卡名或键名含分隔符的跳过 不冒险
    internal static class EeeTweak
    {
        internal const string ReceiptKey = "EeeReceipt";
        private const string PendingKey = "EeePending";
        private static readonly object lk = new object();

        public static bool EnabledByPavise { get { return Settings.LoadStr(ReceiptKey, "").Length > 0; } }
        public static bool HasResidue() { return EnabledByPavise || Settings.Load(PendingKey, false); }

        public static int ReceiptAdapters()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string rec in Settings.LoadStr(ReceiptKey, "").Split(';'))
            {
                int bar = rec.IndexOf('|');
                if (bar > 0) names.Add(rec.Substring(0, bar));
            }
            return names.Count;
        }

        public static string Describe()
        {
            if (Settings.Load(PendingKey, false) && !EnabledByPavise) return Lang.T("t.eee.pending");
            int n = ReceiptAdapters();
            return n > 0 ? Lang.F("t.eee.applied", n.ToString()) : Lang.T("t.eee.off");
        }

        public static bool Enable()
        {
            lock (lk)
            {
                if (EnabledByPavise) return true;
                // 写之前先落一个未决标记 崩在写与记账之间时 启动能看见这里有一笔没对上的账
                Settings.Save(PendingKey, true);
                string output;
                if (!PsRunner.Run(ApplyScript, "eee-off-apply", 60000, out output))
                { Settings.Save(PendingKey, false); Logger.Warn(Lang.T("log.eee.1")); return false; }
                string receipt = ExtractReceipt(output);
                if (receipt == null) { Logger.Warn(Lang.T("log.eee.1")); return false; }
                if (receipt.Length == 0)
                {
                    Settings.Save(PendingKey, false);
                    Logger.Log(Lang.T("log.eee.2"));
                    return false;
                }
                Settings.SaveStr(ReceiptKey, receipt);
                if (Settings.LoadStr(ReceiptKey, "") != receipt)
                {
                    RestoreWithReceipt(receipt);
                    Settings.Save(PendingKey, false);
                    Logger.Log(Lang.T("log.eee.3"));
                    return false;
                }
                Settings.Save(PendingKey, false);
                Logger.Log(Lang.T("log.eee.4") + ReceiptAdapters());
                return true;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                string receipt = Settings.LoadStr(ReceiptKey, "");
                if (receipt.Length == 0)
                {
                    Settings.Save(PendingKey, false);
                    return true;
                }
                if (!RestoreWithReceipt(receipt)) { Logger.Log(Lang.T("log.eee.5")); return false; }
                Settings.SaveStr(ReceiptKey, "");
                Settings.Save(PendingKey, false);
                Logger.Log(Lang.T("log.eee.6"));
                return true;
            }
        }

        private static bool RestoreWithReceipt(string receipt)
        {
            string output;
            var args = new Dictionary<string, string> { { "PAVISE_EEE_RECEIPT", receipt } };
            if (!PsRunner.Run(RestoreScript, "eee-off-restore", 60000, args, out output)) return false;
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

        // 单次往返 逐网卡逐属性 记旧值 写 0 读回校验 不过当场写回旧值不入账
        //   改完的网卡各重启一次让值生效 -NoRestart 避免每改一项断一次链路
        private static readonly string ApplyScript = string.Join("\r\n", new[]
        {
            "$ErrorActionPreference = 'SilentlyContinue'",
            "$records = @()",
            "$changed = @()",
            "$adapters = Get-NetAdapter -Physical | Where-Object { $_.MediaType -eq '802.3' }",
            "foreach ($a in $adapters) {",
            "    $n = $a.Name",
            "    if ($n -match '[|;]') { continue }",
            "    $props = Get-NetAdapterAdvancedProperty -Name $n | Where-Object {",
            "        ($_.RegistryKeyword -match '^\\*?EEE$|^EEELinkAdvertisement$|^EnableGreenEthernet$|^GreenEthernet$|^AdvancedEEE$|^EnergyEfficientEthernet$|^\\*EeeEnable$|^GigaLite$|^PowerSavingMode$|^SipsEnabled$|^LinkSpeedBatterySaver$') -or",
            "        ($_.DisplayName -match 'Energy.?Efficient|^EEE$|Green Ethernet|Gigabit Lite|Power Saving Mode|System Idle Power Saver|Link Speed Battery Saver') }",
            "    foreach ($p in $props) {",
            "        $kw = $p.RegistryKeyword",
            "        if ($null -eq $kw -or $kw -match '[|;]') { continue }",
            "        $valid = @($p.ValidRegistryValues)",
            "        if ($valid.Count -eq 0 -or $valid.Count -gt 3 -or ($valid -notcontains '0')) { continue }",
            "        $oldv = ($p.RegistryValue | Select-Object -First 1)",
            "        if ($null -eq $oldv -or $oldv -match '[|;]') { continue }",
            "        if ([string]$oldv -eq '0') { continue }",
            "        try { Set-NetAdapterAdvancedProperty -Name $n -RegistryKeyword $kw -RegistryValue '0' -NoRestart -ErrorAction Stop } catch { continue }",
            "        $chk = Get-NetAdapterAdvancedProperty -Name $n -RegistryKeyword $kw",
            "        if ($null -ne $chk -and [string]($chk.RegistryValue | Select-Object -First 1) -eq '0') {",
            "            $records += ($n + '|' + $kw + '|' + $oldv)",
            "            if ($changed -notcontains $n) { $changed += $n }",
            "        } else {",
            "            try { Set-NetAdapterAdvancedProperty -Name $n -RegistryKeyword $kw -RegistryValue ([string]$oldv) -NoRestart -ErrorAction Stop } catch { }",
            "        }",
            "    }",
            "}",
            "foreach ($c in $changed) { Restart-NetAdapter -Name $c -ErrorAction SilentlyContinue }",
            "if ($records.Count -gt 0) { Write-Output ('RECEIPT:' + ($records -join ';')) } else { Write-Output 'NONE' }",
        });

        private static readonly string RestoreScript = string.Join("\r\n", new[]
        {
            "$ErrorActionPreference = 'SilentlyContinue'",
            "$ok = $true",
            "$changed = @()",
            "foreach ($rec in $env:PAVISE_EEE_RECEIPT -split ';') {",
            "    $f = $rec -split '\\|'",
            "    if ($f.Count -ne 3) { continue }",
            "    $n = $f[0]",
            "    if ($null -eq (Get-NetAdapter -Name $n -ErrorAction SilentlyContinue)) { continue }",
            "    try { Set-NetAdapterAdvancedProperty -Name $n -RegistryKeyword $f[1] -RegistryValue $f[2] -NoRestart -ErrorAction Stop } catch { $ok = $false; continue }",
            "    $chk = Get-NetAdapterAdvancedProperty -Name $n -RegistryKeyword $f[1]",
            "    if ($null -eq $chk -or [string]($chk.RegistryValue | Select-Object -First 1) -ne $f[2]) { $ok = $false }",
            "    if ($changed -notcontains $n) { $changed += $n }",
            "}",
            "foreach ($c in $changed) { Restart-NetAdapter -Name $c -ErrorAction SilentlyContinue }",
            "if ($ok) { Write-Output 'RESTOREOK' } else { Write-Output 'RESTOREFAIL' }",
        });
    }
}
