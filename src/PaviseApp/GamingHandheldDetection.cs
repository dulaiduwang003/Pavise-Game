using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PaviseApp
{
    // Identify handhelds from verified platform identity, not battery, standby or CPU branding.
    internal static class GamingHandheldDetection
    {
        internal sealed class HardwareIdentity
        {
            internal string Manufacturer, Model, BoardManufacturer, BoardProduct;
        }

        internal sealed class Detection
        {
            internal readonly bool Detected;
            internal readonly string Reason;
            internal Detection(bool detected, string reason) { Detected = detected; Reason = reason; }
        }

        internal sealed class InventoryCache
        {
            internal const int RetryDelayMilliseconds = 5000, MaximumAttempts = 3;
            private readonly object gate = new object();
            private readonly Func<HardwareIdentity> probe;
            private readonly Func<long> now;
            private Detection cached;
            private bool complete;
            private int attempts;
            private long retryAfter;

            internal InventoryCache(Func<HardwareIdentity> probe, Func<long> now)
            {
                if (probe == null || now == null) throw new ArgumentNullException();
                this.probe = probe; this.now = now;
            }

            internal Detection Read()
            {
                lock (gate)
                {
                    if (complete || cached != null && now() < retryAfter) return cached;
                    attempts++;
                    try
                    {
                        HardwareIdentity identity = probe();
                        if (identity == null) throw new InvalidOperationException("Hardware identity unavailable");
                        if (!HasIdentityPair(identity.Manufacturer, identity.Model)
                            && !HasIdentityPair(identity.BoardManufacturer, identity.BoardProduct))
                            throw new InvalidOperationException("Hardware identity fields unavailable");
                        string reason = MatchSystem(identity.Manufacturer, identity.Model);
                        if (reason.Length == 0) reason = MatchBoard(identity.BoardManufacturer, identity.BoardProduct);
                        cached = new Detection(reason.Length > 0, reason.Length > 0 ? reason : "handheld-not-confirmed");
                        complete = true;
                    }
                    catch (Exception)
                    {
                        complete = attempts >= MaximumAttempts;
                        cached = new Detection(false, complete ? "inventory-read-failed; retries-exhausted" : "inventory-read-failed; retry-pending");
                        retryAfter = now() + RetryDelayMilliseconds;
                    }
                    return cached;
                }
            }
        }

        private static readonly InventoryCache inventory = new InventoryCache(ReadInventory, Milliseconds);

        internal static bool Detected { get { return inventory.Read().Detected; } }
        internal static string Reason { get { return inventory.Read().Reason; } }

        internal static bool IsHandheld(string manufacturer, string model, string cpuName)
        {
            // CPU branding requires a verified device family; generic laptops and mini PCs retain normal routing.
            return MatchSystem(manufacturer, model).Length > 0;
        }

        private static string Normalize(string value)
        { return Regex.Replace((value ?? "").Trim().ToUpperInvariant(), "[^A-Z0-9]+", " ").Trim(); }

        private static bool HasIdentityPair(string manufacturer, string model)
        { return Normalize(manufacturer).Length > 0 && Normalize(model).Length > 0; }

        private static bool Has(string value, string pattern)
        { return Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant); }

        private static string MatchSystem(string manufacturer, string model)
        {
            string maker = Normalize(manufacturer), product = Normalize(model);
            if (maker.Length == 0 || product.Length == 0) return "";
            if (Has(maker, @"\bASUS(?:TEK)?\b")
                && (Has(product, @"\bROG (?:XBOX )?ALLY\b")
                    || Has(product, @"\b(?:RC71L|RC72LA|RC73YA|RC73XA)\b"))) return "asus-rog-ally";
            if (Has(maker, @"\bLENOVO\b")
                && (Has(product, @"\bLEGION GO\b")
                    || Has(product, @"\b(?:83E1|83L3|83N6|83N0|83N1)(?:[A-Z0-9]{6})?\b"))) return "lenovo-legion-go";
            if (Has(maker, @"\bMSI\b|\bMICRO STAR\b") && Has(product, @"\bCLAW\b")) return "msi-claw";
            if (Has(maker, @"\bVALVE\b") && Has(product, @"^(?:STEAM ?DECK|JUPITER|GALILEO)$")) return "valve-steam-deck";
            if (Has(maker, @"\bGPD\b")
                && (Has(product, @"^(?:GPD ?)?WIN(?: ?(?:MAX|MINI))?(?: ?[0-9]+)?(?: |$)")
                    || Has(product, @"^(?:G1618 0[345]|G1617 (?:01|02(?: L)?)|G1619 0[145])$"))) return "gpd-win";
            if (Has(maker, @"\bAYANEO\b") && Has(product,
                @"^(?:AYANEO ?)?(?:AIR(?: 1S| PLUS| PRO)?|AIR1S|AIRPLUS|2S?|3|GEEK(?: 1S)?|GEEK1S|NEXT(?: PRO| II| 2)?|NEXTPRO|NEXTII|KUN|SLIDE|FLIP(?: KB| DS| 1S)?)(?: |$)")) return "ayaneo-handheld";
            if (Has(maker, @"\b(?:ONE NETBOOK|ONENETBOOK|ONEXPLAYER)\b")
                && Has(product, @"^(?:ONE ?XPLAYER|ONE ?XFLY)(?=[0-9]| |$)")) return "onexplayer-handheld";
            if (Has(maker, @"\bAOKZOE\b") && Has(product, @"^(?:AOKZOE )?A1(?: ?PRO)?(?: |$)")) return "aokzoe-handheld";
            return "";
        }

        private static string MatchBoard(string manufacturer, string productName)
        {
            // Use documented matching DMI pairs; never combine system and board identities.
            string maker = Normalize(manufacturer), product = Normalize(productName);
            if (maker == "AYANEO" && Has(product, @"^(?:AYANEO 2|AIR|AIR 1S|AIR PRO|AB05 MENDOCINO|FLIP|GEEK|KUN)$"))
                return "ayaneo-handheld; board-identity";
            if (maker == "ONE NETBOOK" && Has(product, @"^(?:ONE XPLAYER|ONEXPLAYER 2|ONEXPLAYER F1|ONEXPLAYER MINI A07|ONEXPLAYER MINI PRO|ONEXPLAYER X1)$"))
                return "onexplayer-handheld; board-identity";
            if (maker == "AOKZOE" && Has(product, @"^AOKZOE A1(?: AR07| PRO)$"))
                return "aokzoe-handheld; board-identity";
            return "";
        }

        private static HardwareIdentity ReadInventory()
        {
            var result = new HardwareIdentity();
            using (RegistryKey bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
            {
                if (bios != null)
                {
                    result.Manufacturer = bios.GetValue("SystemManufacturer") as string;
                    result.Model = bios.GetValue("SystemProductName") as string;
                    result.BoardManufacturer = bios.GetValue("BaseBoardManufacturer") as string;
                    result.BoardProduct = bios.GetValue("BaseBoardProduct") as string;
                }
            }
            return result;
        }

        private static long Milliseconds()
        {
            long ticks = Stopwatch.GetTimestamp(), frequency = Stopwatch.Frequency;
            return ticks / frequency * 1000 + ticks % frequency * 1000 / frequency;
        }
    }
}
