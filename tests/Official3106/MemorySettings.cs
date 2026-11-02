using System;
using System.Collections.Generic;
using System.Linq;
namespace PaviseApp
{
    // Test-only substitute for the registry store. All production callers are compiled unchanged.
    internal static class Settings
    {
        internal const string Key = "IN_MEMORY_ONLY", GpuIdentityReceiptKey = "MonitoringGpuIdentityTraceV1", GpuIdentitySessionPattern = "unused";
        private static readonly Dictionary<string, object> data = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        internal static int MutationGeneration;
        internal static Action<string> BeforeRead;
        internal static void UseTransientStoreForCurrentProcess() { lock(data) { data.Clear(); MutationGeneration++; } }
        internal static void SuspendWritesForReset() { }
        internal static void NoteExternalMutation() { MutationGeneration++; }
        internal static bool ClearUserPreferencesForReset(Predicate<string> selected, out int removed, out string failure)
        { lock(data) { var keys=data.Keys.Where(k=>selected(k)).ToArray(); foreach(var key in keys)data.Remove(key); removed=keys.Length; failure=null; return true; } }
        internal static bool TryLoadCached(string name, out bool value) { return TryLoadStoredBool(name,out value); }
        public static bool LoadCached(string name, bool def) { return Load(name,def); }
        internal static bool TryLoadStoredBool(string name, out bool value) { lock(data) { object x; bool ok=data.TryGetValue(name,out x)&&x is bool; value=ok&&(bool)x; return ok; } }
        internal static bool TryLoadBool(string name, bool fallback, out bool value) { value=Load(name,fallback); return true; }
        public static bool Load(string name, bool def) { if(BeforeRead!=null)BeforeRead(name); bool v; return TryLoadStoredBool(name,out v)?v:def; }
        public static bool Save(string name, bool value) { lock(data) { data[name]=value; MutationGeneration++; return true; } }
        public static void Remove(string name) { lock(data) { data.Remove(name); MutationGeneration++; } }
        internal static bool TryRemove(string name, bool durable) { Remove(name); return true; }
        internal static bool TryLoadStoredNames(string prefix, out string[] names) { lock(data) { names=data.Keys.Where(k=>k.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)).ToArray(); return true; } }
        internal static bool TryLoadStr(string name, out string value) { value=LoadStr(name,""); return true; }
        public static string LoadStr(string name, string def) { lock(data) { object value; return data.TryGetValue(name,out value)?(value as string??def):def; } }
        public static bool SaveStr(string name, string value) { lock(data) { data[name]=value; MutationGeneration++; return true; } }
        internal static bool SaveStrDurable(string name, string value) { return SaveStr(name,value); }
        internal static bool SaveDurable(string name, bool value) { return Save(name,value); }
        internal static bool SaveGpuIdentityReceipt(string value) { return SaveStr(GpuIdentityReceiptKey,value); }
    }
}
