#if PAVISE_INDEX_OWNERSHIP_RUNNER
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;

namespace PaviseApp
{
    internal static class PerformanceActionCatalog
    { internal const string Mitigations = "AutoGameMitigationsV1", VisualEffects = "MatchVisualEffectsV1"; }
    internal static class Settings
    {
        internal static DictionaryProvider Provider;
        internal static int Owner;
        internal static bool TryLoadStr(string key, out string value) { value = Provider.Read(key, Owner); return true; }
        internal static bool SaveStrDurable(string key, string value) { return Provider.Save(key, value); }
        internal static bool TryLoadStoredNames(string prefix, out string[] names) { names = Provider.Names(prefix); return true; }
    }
    public sealed class DictionaryProvider : MarshalByRefObject
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, string> values = new Dictionary<string, string>();
        private readonly ManualResetEvent readPaused = new ManualResetEvent(false), resume = new ManualResetEvent(false);
        private int pauseOwner = -1, saveCalls;
        public int RejectSaveNumber;
        public override object InitializeLifetimeService() { return null; }
        public string Read(string key, int owner)
        {
            string result;
            lock (gate) { if (!values.TryGetValue(key, out result)) result = ""; }
            if (key == PerformanceActionStore.ListKey && Interlocked.CompareExchange(ref pauseOwner, -1, owner) == owner)
            { readPaused.Set(); if (!resume.WaitOne(3000)) throw new TimeoutException("Mock writer did not resume"); }
            return result;
        }
        public bool Save(string key, string value)
        {
            lock (gate) { saveCalls++; if (saveCalls == RejectSaveNumber) return false; values[key] = value; return true; }
        }
        public void Arm(int owner) { readPaused.Reset(); resume.Reset(); pauseOwner = owner; }
        public bool WaitPaused() { return readPaused.WaitOne(3000); }
        public void Resume() { resume.Set(); }
        public void Reset(int reject) { lock (gate) { values.Clear(); saveCalls = 0; RejectSaveNumber = reject; } }
        public void RejectNext(int offset) { lock (gate) RejectSaveNumber = saveCalls + offset; }
        public int NonemptyCount()
        { lock (gate) { int result = 0; foreach (var p in values) if (p.Key.StartsWith("PerformanceReceipt_") && p.Value.Length > 0) result++; return result; } }
        public int IndexCount()
        { return Read(PerformanceActionStore.ListKey, -2).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Length; }
        public string[] Names(string prefix)
        { lock (gate) { var result = new List<string>(); foreach (string key in values.Keys) if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) result.Add(key); return result.ToArray(); } }
    }
    public sealed class IsolatedWriter : MarshalByRefObject
    {
        private readonly Dictionary<string, string> states = new Dictionary<string, string>();
        public override object InitializeLifetimeService() { return null; }
        public void Init(DictionaryProvider provider, int owner) { Settings.Provider = provider; Settings.Owner = owner; }
        public bool Apply(string target)
        {
            if (!states.ContainsKey(target)) states[target] = "0";
            return PerformanceActionStore.Change("mock-action", target, "1", delegate { return states[target]; },
                delegate(string value) { states[target] = value; return true; }, delegate(string value) { return value == "1"; });
        }
        public bool Restore(string target)
        {
            var r = new PerformanceActionStore.Receipt { Id = "mock-action", Target = target, Original = "0", Expected = "1", Committed = true };
            return PerformanceActionStore.Restore(r, delegate { return states[target]; },
                delegate(string value) { states[target] = value; return true; }, delegate(string value) { return value == "1"; });
        }
        public int VisibleCount() { return PerformanceActionStore.All().Count; }
    }
    internal static class PerformanceIndexOwnershipAudit
    {
        private static void Check(bool result, string message) { if (!result) throw new InvalidOperationException(message); }
        private static object Interleave(bool clear)
        {
            var provider = new DictionaryProvider();
            AppDomain a = AppDomain.CreateDomain("writer-A"), b = AppDomain.CreateDomain("writer-B");
            try
            {
                string type = typeof(IsolatedWriter).FullName, assembly = Assembly.GetExecutingAssembly().FullName;
                var wa = (IsolatedWriter)a.CreateInstanceAndUnwrap(assembly, type);
                var wb = (IsolatedWriter)b.CreateInstanceAndUnwrap(assembly, type);
                wa.Init(provider, 1); wb.Init(provider, 2);
                if (clear) Check(wa.Apply("A"), "Seed apply failed");
                provider.Arm(1); bool resultA = false; Exception failure = null;
                var thread = new Thread(delegate() { try { resultA = clear ? wa.Restore("A") : wa.Apply("A"); } catch (Exception error) { failure = error; } });
                thread.Start(); Check(provider.WaitPaused(), "Writer did not snapshot the old index");
                bool resultB = wb.Apply("B"); provider.Resume(); Check(thread.Join(3000), "Writer did not settle");
                if (failure != null) throw failure;
                Check(resultA && resultB, "An apply/clear unexpectedly failed");
                int allBefore = wa.VisibleCount(), nonempty = provider.NonemptyCount(), index = provider.IndexCount();
                Check(nonempty - index == 1, "Lost update did not reproduce a nonempty orphan");
                Check(allBefore == nonempty, "Recovered All still concealed the orphan from restoration");
                Check(wb.Apply("B"), "Repeated application failed");
                int allAfter = wa.VisibleCount();
                Check(allAfter == allBefore, "Repeat satisfied apply unexpectedly repaired the index");
                Check(provider.IndexCount() == index, "Repeat satisfied apply unexpectedly repaired index membership");
                return new { scenario = clear ? "cross-writer-clear-track" : "cross-writer-track-track", resultA, resultB,
                    nonemptyRecords = nonempty, indexCount = index, visibleToAll = allBefore, orphanCount = nonempty - index,
                    repeatedSatisfiedApplyRepairsIndex = false, realRegistryAccess = false };
            }
            finally { AppDomain.Unload(a); AppDomain.Unload(b); }
        }
        private static object SingleWriterFailures()
        {
            int checkedCases = 0;
            for (int reject = 1; reject <= 3; reject++)
            {
                var provider = new DictionaryProvider(); var writer = new IsolatedWriter(); writer.Init(provider, 0); provider.Reset(reject);
                writer.Apply("A");
                Check(provider.NonemptyCount() <= provider.IndexCount(), "Single apply failure produced a nonempty orphan"); checkedCases++;
            }
            for (int reject = 1; reject <= 2; reject++)
            {
                var provider = new DictionaryProvider(); var writer = new IsolatedWriter(); writer.Init(provider, 0);
                Check(writer.Apply("A"), "Single clear fixture seed failed"); provider.RejectNext(reject); writer.Restore("A");
                Check(provider.NonemptyCount() <= provider.IndexCount(), "Single clear failure produced a nonempty orphan"); checkedCases++;
            }
            return new { scenario = "single-writer-save-failures", checkedCases, nonemptyOrphans = 0, realRegistryAccess = false };
        }
        private static int Main()
        {
            try { Console.WriteLine(new JavaScriptSerializer().Serialize(new { tests = 7, sourceOnlyFixture = true,
                normalRuntimeStarted = false, systemWrites = false, results = new[] { SingleWriterFailures(), Interleave(false), Interleave(true) } })); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
#endif
