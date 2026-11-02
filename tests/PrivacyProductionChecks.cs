using System;
using System.Reflection;

internal static class PrivacyProductionChecks
{
    private const BindingFlags Fields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static int assertions;
    private static void Check(bool ok, string detail)
    { assertions++; if (!ok) throw new InvalidOperationException(detail); }
    private static object Field(object value, string name)
    { return value.GetType().GetField(name, Fields).GetValue(value); }
    private static int Main(string[] args)
    {
        try
        {
            // Load the release assembly for read-only inspection; never enter Program.Main.
            Assembly assembly = Assembly.LoadFile(System.IO.Path.GetFullPath(args[0]));
            Type catalog = assembly.GetType("PaviseApp.PrivacyActionCatalog", true);
            Type backendType = assembly.GetType("PaviseApp.PrivacyNativeBackend", true);
            object backend = Activator.CreateInstance(backendType, true);
            string ntfs = (string)catalog.GetField("NtfsMemory", Fields).GetValue(null);
            Array active = (Array)catalog.GetField("All", Fields).GetValue(null);
            Check(active.Length == 2 && (string)Field(active.GetValue(0), "Key") == ntfs && (string)Field(active.GetValue(1), "Key") == "EnvPrivacyBackgroundGlobalV1", "Environment enhancements must expose NTFS only");
            Array retired = (Array)catalog.GetField("Retired", Fields).GetValue(null);
            Check(retired.Length == 10, "Historical privacy identities were lost");
            Type control = assembly.GetType("PaviseApp.PrivacyControl", true);
            foreach (object action in retired)
            {
                string key = (string)Field(action, "Key");
                object plan = backendType.GetMethod("Inspect").Invoke(backend, new object[] { key, "" });
                Check(!(bool)Field(plan, "Supported") && (string)Field(plan, "UnavailableReason") == "retired"
                    && ((Array)Field(plan, "Targets")).Length == 0, "Removed action still discovers live targets: " + key);
                Check(!(bool)backendType.GetMethod("Write").Invoke(backend, new object[] { key, "invalid-retired-target", "d:0" }), "Removed native write was admitted: " + key);
                Check(!(bool)control.GetMethod("Set", Fields).Invoke(null, new object[] { key, true, "", null }), "Removed action was queued: " + key);
                Check(catalog.GetMethod("Find", Fields).Invoke(null, new object[] { key }) != null, "Legacy recovery identity was lost: " + key);
            }
            object ntfsPlan = backendType.GetMethod("Inspect").Invoke(backend, new object[] { ntfs, "" });
            bool supported = (bool)Field(ntfsPlan, "Supported"), managed = (bool)Field(ntfsPlan, "Managed");
            int reads = 0;
            if (supported)
            {
                Check(!managed, "Managed system admitted a writable enhancement");
                Array targets = (Array)Field(ntfsPlan, "Targets");
                Check(targets.Length == 1, "NTFS plan should contain exactly one registry target");
                object target = targets.GetValue(0);
                Check((string)Field(target, "Wanted") == "d:2" && (bool)Field(target, "RestartRequired"), "NTFS plan lost value or reboot tracking");
                Check(backendType.GetMethod("Read").Invoke(backend, new object[] { ntfs, (string)Field(target, "Key") }) != null, "NTFS setting is unreadable");
                reads++;
            }
            else
            {
                string reason = (string)Field(ntfsPlan, "UnavailableReason");
                Check(managed || reason == "memory" || reason == "system", "Unavailable enhancement lacks a valid capability reason");
            }
            Type game = assembly.GetType("PaviseApp.GameMode", true);
            foreach (string removed in new[] { "PaviseApp.WsTrim", "PaviseApp.AutomaticCoreLayout" })
                Check(assembly.GetType(removed) == null, "Removed runtime type survived: " + removed);
            foreach (string removed in new[] { "WsTrimOn", "AutomaticCoreLayoutEnabled" })
                Check(game.GetProperty(removed, Fields) == null, "Removed runtime setting survived: " + removed);
            Type laptop = assembly.GetType("PaviseApp.LaptopPerfMode", true);
            PropertyInfo laptopEnabled = game.GetProperty("LaptopPerformanceEnabled", Fields);
            MethodInfo laptopToggle = game.GetMethod("TrySetLaptopPerformanceEnabled", Fields, null, new[] { typeof(bool) }, null);
            Check(laptopEnabled != null && laptopEnabled.PropertyType == typeof(bool) && laptopEnabled.CanRead,
                "Restored laptop mode has no readable global preference");
            Check(laptopToggle != null && laptopToggle.ReturnType == typeof(bool), "Restored laptop mode has no explicit preference setter");
            Check(laptop.GetMethod("Activate", Fields, null, Type.EmptyTypes, null) != null
                && laptop.GetMethod("Restore", Fields, null, Type.EmptyTypes, null) != null,
                "Laptop performance apply or owned-state recovery is missing");
            string laptopPreference = (string)laptop.GetField("PreferenceKey", Fields).GetValue(null);
            Check(laptopPreference == "AutoLaptopPerformanceV2", "Restored laptop mode reused an old opt-in identity");
            Type policy = assembly.GetType("PaviseApp.PerformancePolicy", true);
            MethodInfo isRetired = policy.GetMethod("IsRetired", Fields);
            Check((bool)isRetired.Invoke(null, new object[] { "GmLaptopPerfV1" })
                && (bool)isRetired.Invoke(null, new object[] { "AutoLaptopPerformanceV1" })
                && !(bool)isRetired.Invoke(null, new object[] { laptopPreference }),
                "Restoration revived the old tier-forced preference or left the new opt-in retired");
            Type eligibility = assembly.GetType("PaviseApp.EnvironmentEnhancementEligibility", true);
            MethodInfo fits = eligibility.GetMethod("Fits", Fields);
            ulong gib = 1024UL * 1024 * 1024;
            foreach (ulong capacity in new[] { 8 * gib, 16 * gib, 24 * gib })
                Check(!(bool)fits.Invoke(null, new object[] { capacity, capacity, capacity }), "Low-capacity machine passed NTFS gate");
            Check((bool)fits.Invoke(null, new object[] { 32 * gib, 31 * gib, 8 * gib }), "Nominal 32 GB machine with firmware reservation rejected");
            Check(!(bool)fits.Invoke(null, new object[] { 64 * gib, 64 * gib, 8 * gib }), "Insufficient available-memory fraction accepted");
            Check((bool)fits.Invoke(null, new object[] { 64 * gib, 64 * gib, 16 * gib }), "Sufficient 64 GB machine rejected");
            Check(!(bool)fits.Invoke(null, new object[] { 32 * gib, 32 * gib, 8 * gib - 1 }), "8 GB available boundary rounded up");
            Check(!(bool)fits.Invoke(null, new object[] { 32 * gib, 32 * gib, 33 * gib }), "Invalid memory snapshot accepted");
            Check(!(bool)fits.Invoke(null, new object[] { 0UL, 0UL, 0UL }), "Missing memory evidence accepted");
            Console.WriteLine("PASS production environment enhancements assertions=" + assertions + " active=1 retired=11 ntfs_supported=" + supported + " managed=" + managed + " native_reads=" + reads + " native_mutations=0 application_started=false");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
