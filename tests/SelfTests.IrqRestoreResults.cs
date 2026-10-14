using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static void TestIrqAffinityTransactions()
        {
            Eq(false,IrqAffinityEngine.SupportsExactMask(true,1,15));
            Eq(false,IrqAffinityEngine.SupportsExactMask(false,16,15));
            Eq(false,IrqAffinityEngine.SupportsExactMask(false,0,15));
            Eq(true,IrqAffinityEngine.SupportsExactMask(false,8,15));
            Eq(true,IrqAffinityEngine.SupportsExactMask(false,1UL << 63,ulong.MaxValue));
            int applies = 0, restores = 0; bool attempted; string saved = "";
            var result = new IrqWriteResult();
            Func<bool> write = delegate { applies++; return false; };
            Func<bool> restore = delegate { restores++; return false; };
            Eq(false,IrqAffinityEngine.ApplyTarget(result,false,TimerFixtureBoot(1),delegate { return false; },
                delegate { return saved; },write,restore,out attempted));
            Eq(false,attempted); Eq(0,applies); Eq(0,restores); Eq(1,result.Failed);
            Eq(false,IrqAffinityEngine.ApplyTarget(result,false,TimerFixtureBoot(1),delegate(string value) { saved = value; return true; },
                delegate { return saved; },write,restore,out attempted));
            Eq(true,attempted); Eq(1,applies); Eq(1,restores); Eq(1,result.RollbackFailed);
            Eq(false,IrqAffinityEngine.ApplyTarget(result,false,TimerFixtureBoot(1),delegate { return true; },
                delegate { return saved; },delegate { throw new IOException(); },delegate { return true; },out attempted));
            Eq(1,result.RolledBack);
            Eq(true,IrqAffinityEngine.ApplyTarget(result,true,"bad",null,null,write,restore,out attempted));
            Eq(false,attempted); Eq(1,result.Succeeded); Eq(1,applies);
            string raw = IrqPriorityTweak.EncodeJournal(new List<KeyValuePair<string,string>> {
                new KeyValuePair<string,string>("A","-"),new KeyValuePair<string,string>("B","2") });
            string remaining; int restoreCalls = 0;
            Eq(true,IrqPriorityTweak.RestoreJournal(raw,"a",delegate(string id,string value) {
                restoreCalls++; Eq("A",id); Eq("-",value); return true; },out remaining));
            Eq(1,restoreCalls); Eq(1,IrqPriorityTweak.DecodeJournal(remaining).Count);
            Eq("B",IrqPriorityTweak.DecodeJournal(remaining)[0].Key);
            Eq(false,IrqPriorityTweak.RestoreJournal(raw,"A",delegate { throw new IOException(); },out remaining)); Eq(raw,remaining);
            Eq(false,IrqPriorityTweak.RestoreJournal(raw + ";bad",null,delegate { restoreCalls++; return true; },out remaining));
            Eq(1,restoreCalls); Eq(raw + ";bad",remaining);
        }

        private static void TestIrqRestoreResults()
        {
            var affinity = new List<string> { "A", "B" };
            var priority = new List<string>();
            IrqReceiptReader readAffinity = delegate(out List<string> ids)
            { ids = new List<string>(affinity); return true; };
            IrqReceiptReader readPriority = delegate(out List<string> ids)
            { ids = new List<string>(priority); return true; };
            Func<List<string>> noOwners = delegate { return new List<string>(); };
            var result = IrqRestoreResult.Run(null,readAffinity,readPriority,noOwners,
                delegate(string id) { return id == "A"; },null);
            Eq(true,result.ScopeReadSucceeded); Eq(false,result.Success); Eq(2,result.Devices.Count);
            Eq(true,result.ForDevice("a").Success); Eq("ok",result.ForDevice("A").AffinityResult);
            Eq("skipped",result.ForDevice("A").PriorityResult);
            Eq(false,result.ForDevice("B").Success); Eq("failed",result.ForDevice("B").AffinityResult);
            result = IrqRestoreResult.Run(null,readAffinity,readPriority,noOwners,
                delegate(string id) { return id == "B"; },null);
            Eq(false,result.ForDevice("A").Success); Eq(true,result.ForDevice("B").Success);

            priority.Add("a"); priority.Add("C");
            int affinityCalls = 0, priorityCalls = 0;
            result = IrqRestoreResult.Run(null,readAffinity,readPriority,noOwners,
                delegate(string id) { affinityCalls++; if (id == "A") throw new IOException(); return true; },
                delegate { priorityCalls++; return true; });
            Eq(3,result.Devices.Count); Eq(2,affinityCalls); Eq(2,priorityCalls);
            Eq(false,result.ForDevice("A").Affinity); Eq(true,result.ForDevice("A").Priority);
            Eq(true,result.ForDevice("B").Success); Eq(true,result.ForDevice("C").Success);
            Eq("skipped",result.ForDevice("C").AffinityResult);
            result = IrqRestoreResult.Run(null,readAffinity,readPriority,noOwners,
                delegate { return true; },delegate(string id) { return id != "A"; });
            Eq(false,result.ForDevice("A").Success); Eq("failed",result.ForDevice("A").PriorityResult);
            Eq(true,result.ForDevice("B").Success); Eq(true,result.ForDevice("C").Success);

            affinityCalls = priorityCalls = 0;
            result = IrqRestoreResult.Run("a",readAffinity,readPriority,noOwners,
                delegate(string id) { affinityCalls++; Eq("A",id); return true; },
                delegate(string id) { priorityCalls++; Eq("A",id); return true; });
            Eq(true,result.Success); Eq(1,result.Devices.Count); Eq(1,affinityCalls); Eq(1,priorityCalls);
            Eq(true,result.ForDevice("B") == null);
            result = IrqRestoreResult.Run("unknown",readAffinity,readPriority,noOwners,
                delegate { affinityCalls++; return true; },delegate { priorityCalls++; return true; });
            Eq(true,result.Success); Eq(0,result.Devices.Count); Eq(1,affinityCalls); Eq(1,priorityCalls);

            int ownerReads = 0;
            Func<List<string>> owners = delegate { ownerReads++; return new List<string> { "a" }; };
            result = IrqRestoreResult.Run("A",readAffinity,readPriority,owners,
                delegate { affinityCalls++; return true; },delegate { priorityCalls++; return true; });
            Eq(false,result.Success); Eq(1,ownerReads); Eq(1,affinityCalls); Eq(1,priorityCalls);
            result = IrqRestoreResult.Run(null,readAffinity,readPriority,owners,
                delegate { return true; },delegate { return true; });
            Eq(true,result.Success); Eq(1,ownerReads);

            IrqReceiptReader unreadable = delegate(out List<string> ids)
            { ids = new List<string>(); return false; };
            IrqReceiptReader throwing = delegate(out List<string> ids)
            { ids = null; throw new IOException(); };
            foreach (IrqReceiptReader bad in new[] { unreadable,throwing })
            {
                result = IrqRestoreResult.Run(null,bad,readPriority,noOwners,
                    delegate { affinityCalls++; return true; },delegate { priorityCalls++; return true; });
                Eq(false,result.ScopeReadSucceeded); Eq(false,result.Success); Eq(0,result.Devices.Count);
                result = IrqRestoreResult.Run(null,readAffinity,bad,noOwners,
                    delegate { affinityCalls++; return true; },delegate { priorityCalls++; return true; });
                Eq(false,result.ScopeReadSucceeded); Eq(0,result.Devices.Count);
            }
            Eq(1,affinityCalls); Eq(1,priorityCalls);
            affinity.Clear(); priority.Clear();
            result = IrqRestoreResult.Run(null,readAffinity,readPriority,noOwners,
                delegate { affinityCalls++; return true; },delegate { priorityCalls++; return true; });
            Eq(true,result.Success); Eq(0,result.Devices.Count); Eq(1,affinityCalls); Eq(1,priorityCalls);
            result.AffinityFinalized = false; Eq(false,result.Success);

            var touched = new List<string> { "A", "B", "C" };
            List<string> saved = null; var forgotten = new List<string>(); int disabled = 0;
            Eq(false,IrqAffinityEngine.RestoreTouchedScope(touched,new List<string> { "A", "B" },
                delegate(string id) { if (id == "A") throw new IOException(); return true; },
                delegate(List<string> ids) { saved = new List<string>(ids); return true; },
                delegate(string id) { forgotten.Add(id); },delegate { disabled++; return true; }));
            Eq(3,touched.Count); Eq(2,saved.Count); Eq("A",saved[0]); Eq("C",saved[1]);
            Eq(1,forgotten.Count); Eq("B",forgotten[0]); Eq(0,disabled);
            Eq(false,IrqAffinityEngine.RestoreTouchedScope(new List<string> { "A" },new List<string> { "A" },
                delegate { return true; },delegate { return false; },null,delegate { disabled++; return true; }));
            Eq(1,disabled);
            Eq(true,IrqAffinityEngine.RestoreTouchedScope(new List<string> { "A" },new List<string> { "a" },
                delegate { return true; },delegate(List<string> ids) { Eq(0,ids.Count); return true; },
                null,delegate { disabled++; return true; })); Eq(2,disabled);
            Eq(false,IrqAffinityEngine.RestoreTouchedScope(new List<string> { "A" },new List<string> { "A" },
                delegate { return true; },delegate { return true; },null,delegate { return false; }));

            var retryReceipts = new List<string> { "A" };
            int retrySaves = 0, retryDisables = 0;
            Func<List<string>,bool> saveRetry = delegate(List<string> ids)
            { retrySaves++; retryReceipts = new List<string>(ids); return true; };
            Func<bool> finishRetry = delegate { return ++retryDisables > 1; };
            Eq(false,IrqAffinityEngine.RestoreTouchedScope(retryReceipts,new List<string> { "A" },
                delegate { return true; },saveRetry,null,finishRetry));
            Eq(0,retrySaves); Eq(1,retryReceipts.Count); Eq("A",retryReceipts[0]);
            Eq(true,IrqAffinityEngine.RestoreTouchedScope(retryReceipts,new List<string> { "A" },
                delegate { return true; },saveRetry,null,finishRetry));
            Eq(1,retrySaves); Eq(2,retryDisables); Eq(0,retryReceipts.Count);

            string raw = IrqPriorityTweak.EncodeJournal(new List<KeyValuePair<string,string>> {
                new KeyValuePair<string,string>("A","-"),new KeyValuePair<string,string>("B","2"),
                new KeyValuePair<string,string>("C","1") });
            string remaining;
            Eq(false,IrqPriorityTweak.RestoreJournal(raw,"B",delegate { throw new IOException(); },out remaining));
            Eq(raw,remaining);
            Eq(true,IrqPriorityTweak.RestoreJournal(raw,"B",delegate { return true; },out remaining));
            var receipts = IrqPriorityTweak.DecodeJournal(remaining);
            Eq(2,receipts.Count); Eq("A",receipts[0].Key); Eq("C",receipts[1].Key);

            var guarded = new IrqAffinityEngine("IrqRestoreResultsOn","IrqRestoreResults_","");
            Settings.SaveStr("IrqRestoreResults_Touched","A\nB");
            Action<string> previousRead = Settings.BeforeStrictStringReadForTest;
            try
            {
                Settings.BeforeStrictStringReadForTest = delegate(string key)
                {
                    if (key == "IrqRestoreResults_Touched" || key == "IrqPrio") throw new IOException();
                    if (previousRead != null) previousRead(key);
                };
                int generation = Settings.MutationGeneration;
                Eq(false,guarded.RestoreOnly(new List<string> { "A" }));
                Eq(false,guarded.Disable(null));
                List<string> unread;
                Eq(false,IrqPriorityTweak.TryTouchedDevices(out unread));
                Eq(false,IrqPriorityTweak.RestoreOnly("A"));
                Eq(generation,Settings.MutationGeneration);
                Eq("A\nB",Settings.LoadStr("IrqRestoreResults_Touched",""));
            }
            finally
            {
                Settings.BeforeStrictStringReadForTest = previousRead;
                Settings.Remove("IrqRestoreResults_Touched");
            }
        }
    }
}
