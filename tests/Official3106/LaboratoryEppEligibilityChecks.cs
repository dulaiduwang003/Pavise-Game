using System;

namespace PaviseApp
{
    internal static class LaboratoryEppEligibilityChecks
    {
        private static int checks;
        private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
        [STAThread]
        private static int Main()
        {
            try
            {
                foreach (int type in new[] { 8, 9, 10, 11, 14, 30, 31, 32 })
                    Check(LaboratoryEppEligibility.Classify(new[] { type }) == LaboratoryEppDeviceKind.Laptop, "portable chassis " + type);
                foreach (int type in new[] { 3, 4, 5, 6, 7, 13, 15, 17, 23, 28, 35 })
                    Check(LaboratoryEppEligibility.Classify(new[] { type }) == LaboratoryEppDeviceKind.Desktop, "desktop chassis " + type);
                foreach (int[] types in new[] { new int[0], new[] { 1 }, new[] { 2 }, new[] { 12 }, new[] { 9, 3 }, new[] { 99 } })
                    Check(LaboratoryEppEligibility.Classify(types) == LaboratoryEppDeviceKind.Unknown, "unverified or contradictory chassis fails closed");
                Check(LaboratoryEppEligibility.Classify(null) == LaboratoryEppDeviceKind.Unknown, "missing chassis fails closed");
                Check(LaboratoryEppEligibility.Classify(new[] { 9, 12 }) == LaboratoryEppDeviceKind.Laptop, "docked laptop remains laptop");
                LaboratoryEppAvailability desktop = LaboratoryEppEligibility.Evaluate(LaboratoryEppDeviceKind.Desktop, true, true);
                Check(!desktop.Available && desktop.Chinese.Contains("台式") && !desktop.Chinese.Contains("不支持EPP"), "desktop with UPS is excluded without claiming CPU EPP unsupported");
                Check(!LaboratoryEppEligibility.Evaluate(LaboratoryEppDeviceKind.Unknown, true, true).Available, "battery alone cannot identify laptop");
                Check(!LaboratoryEppEligibility.Evaluate(LaboratoryEppDeviceKind.Checking, true, true).Available, "pending discovery blocks enable");
                Check(!LaboratoryEppEligibility.Evaluate(LaboratoryEppDeviceKind.Laptop, false, true).Available, "unverified battery blocks enable");
                Check(!LaboratoryEppEligibility.Evaluate(LaboratoryEppDeviceKind.Laptop, true, false).Available, "DC or unverified AC blocks enable");
                LaboratoryEppAvailability laptop = LaboratoryEppEligibility.Evaluate(LaboratoryEppDeviceKind.Laptop, true, true);
                Check(laptop.Available, "verified AC laptop admitted to remaining runtime checks");
                using (var toggle = new Toggle())
                using (var card = new SettingCard())
                {
                    toggle.Enabled = true;
                    Check(!PanelForm.ApplyLaboratoryToggleAvailability(LaboratoryPower.EppKey, toggle, card, desktop), "desktop card reports unavailable");
                    Check(!toggle.Enabled && card.LockText == desktop.Detail, "desktop OFF is disabled and shows one explicit reason");
                    Check(card.StatusText.Length == 0, "no duplicate status reason");
                    toggle.SetSilently(true); toggle.Enabled = true;
                    Check(!PanelForm.ApplyLaboratoryToggleAvailability(LaboratoryPower.EppKey, toggle, card, desktop) && toggle.Enabled && toggle.Checked,
                        "unsupported saved ON intent is preserved and can be turned off");
                    toggle.SetSilently(false); toggle.Enabled = true;
                    Check(PanelForm.ApplyLaboratoryToggleAvailability(LaboratoryPower.EppKey, toggle, card, laptop) && toggle.Enabled && card.LockText.Length == 0,
                        "verified AC laptop clears unavailability and enables toggle");
                    toggle.Enabled = false;
                    Check(PanelForm.ApplyLaboratoryToggleAvailability(LaboratoryPower.EppKey, toggle, card, laptop) && !toggle.Enabled,
                        "capability does not override shutdown disable");
                    toggle.Enabled = true;
                    Check(PanelForm.ApplyLaboratoryToggleAvailability(LaboratoryGpu.PowerKey, toggle, card, desktop) && toggle.Enabled,
                        "other laboratory toggles are unaffected");
                }
                Console.WriteLine("PASS EPP device eligibility and UI assertions=" + checks + " power_writes=false wmi_queries=false windows_shown=false");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
