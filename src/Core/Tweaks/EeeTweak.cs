// @author bdth 2074055628@qq.com
// File purpose Disable link power saving on physical wired NICs (Energy Efficient Ethernet, Green Ethernet, link speed downshift); record originals per NIC per property, write back by receipt
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    // 802.3az puts the link into LPI sleep at low traffic; wake-up is microseconds to milliseconds, showing up as TX/RX jitter spikes
    //   Changing advanced properties makes the NIC renegotiate the link, dropping it for seconds, so this is a persistent System environment page item, not on the session path
    //   Key names differ per vendor driver; match on both RegistryKeyword and DisplayName, and only accept 0 as off
    //   On the local Realtek, EEEMaxSupportSpeed turned out to be a speed not a switch, though its name starts with EEE
    //   So only touch enum items whose valid values include 0 and have at most three levels; speed and numeric items are never touched
    //   Link downshift items: Realtek's Gigabit Lite and Power Saving Mode are locally verified key names, Intel's SipsEnabled is per docs
    //   They drop the link from 1G to 100M when idle and renegotiate back; Microsoft docs state the NIC briefly disconnects during the switch
    //   One record per receipt; skip NICs or keys whose names contain the separator, don't risk it
    internal static class EeeTweak
    {
        internal const string ReceiptKey = "EeeReceipt";
        private const string PendingKey = "EeePending";
        private static readonly object lk = new object();

        public static bool EnabledByPavise { get { return Settings.LoadStr(ReceiptKey, "").Length > 0; } }
        public static bool HasResidue() { return EnabledByPavise || Settings.Load(PendingKey, false); }
        // Last write didn't finish bookkeeping; the user has to toggle off and on again; the only one of three states that truly needs a human, so the status color uses the accent only for it
        public static bool Pending { get { return Settings.Load(PendingKey, false) && !EnabledByPavise; } }

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
                // Drop a pending marker before writing; if we crash between the write and the bookkeeping, startup can see an unreconciled entry here
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

        // Single round trip, per NIC per property: record old value, write 0, read back to verify; on failure write the old value back on the spot and don't record it
        //   Restart each changed NIC once so values take effect; -NoRestart avoids dropping the link once per property
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
