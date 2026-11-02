#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static int PerformanceRestoreAssertions;
        private static void RestoreCheck(bool condition, string message)
        {
            PerformanceRestoreAssertions++;
            if (!condition) throw new InvalidOperationException("Performance restore: " + message);
        }
        private sealed class RestoreFixture
        {
            internal string Id, Target, Original, Applied, Value;
            internal int Reads, Writes, Service = 4;
            internal Func<int, string> OnRead;
            internal Func<string, bool> OnWrite;
            internal RestoreFixture(bool defender)
            {
                Settings.UseTransientStoreForCurrentProcess();
                Id = defender ? PerformanceActionCatalog.LegacyDefenderSession : PerformanceActionCatalog.Mitigations;
                Target = defender ? "Defender" : @"C:\InertFixture\NeverExisted.exe";
                Original = defender ? "False,False" : "ON:False;ON:False;ON:False;ON:False;ON:False;NOTSET:False";
                Applied = defender ? "True,True" : "OFF:False;OFF:False;OFF:False;OFF:False;OFF:False;NOTSET:False";
                Value = Original;
                PerformanceActionBackend.DefenderServiceForTest = delegate { return Service; };
                PerformanceActionBackend.ReadForTest = delegate { Reads++; if (OnRead != null) Value = OnRead(Reads); return Value; };
                PerformanceActionBackend.WriteForTest = delegate(string id, string target, string next)
                {
                    Writes++;
                    if (OnWrite != null) return OnWrite(next);
                    if (next == "off64" || next == "off32")
                    {
                        string[] parts = Value.Split(';');
                        for (int i = 0; i < (next == "off32" ? 6 : 5); i++)
                            parts[i] = "OFF:" + parts[i].Split(':')[1];
                        Value = string.Join(";", parts);
                    }
                    else Value = next;
                    return true;
                };
            }
            internal void Apply()
            {
                // Defender 项已只恢复不执行：直接放入旧版留下的已提交记录。
                if (Id == PerformanceActionCatalog.LegacyDefenderSession) { Value = Applied; Persist(true); }
                else RestoreCheck(PerformanceActionBackend.Apply(Id, Target, "off64"), "fixture apply");
                Reads = Writes = 0;
            }
            internal void Persist(bool committed)
            {
                RestoreCheck(Settings.SaveStrDurable(PerformanceActionStore.Slot(Id, Target), PerformanceActionStore.Pack(new PerformanceActionStore.Receipt {
                    Id = Id, Target = Target, Original = Original, Expected = Applied, Committed = committed })), "fixture receipt persisted");
            }
            internal bool Restore() { return PerformanceActionBackend.Restore(PerformanceActionStore.All()[0]); }
        }

        internal static void RunPerformanceRestoreRegressionTests()
        {
            bool oldWrites = Logger.WritesEnabled;
            Logger.WritesEnabled = false;
            try
            {
                RestoreComponentDrift(true); RestoreComponentDrift(false);
                RestoreAfterReapplyDrift(true); RestoreAfterReapplyDrift(false);
                RestorePersistentDrift(); RestoreWriteFailure(); RestoreVerificationFailure();
                RestoreMalformedCurrent(); RestoreExternalOwnership(); RestorePreparedComponents();
                RestoreInactiveDefender(); RestoreUnavailableProvider(); RestoreFailureHints();
                RestoreLegacyMitigationReceipt(true, false); RestoreLegacyMitigationReceipt(true, true);
                RestoreLegacyMitigationReceipt(false, false);
                ReapplyLegacyMitigationReceipt(false); ReapplyLegacyMitigationReceipt(true);
                ReapplyActualMitigationDrift(); ReapplyMitigationPersistentDrift();
                ReapplyMitigationSaveFailure(); ReapplyMitigationPartialWrite(); ReapplyMitigationMalformedReceipt();
            }
            finally
            {
                PerformanceActionBackend.ReadForTest = null; PerformanceActionBackend.WriteForTest = null;
                PerformanceActionBackend.DefenderServiceForTest = null; PerformanceActionBackend.NvAvailableForTest = null;
                Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = oldWrites;
            }
        }

        private static void RestoreComponentDrift(bool defender)
        {
            var f = new RestoreFixture(defender); f.Apply();
            string external = defender ? "False,True" : "ON:True;OFF:False;OFF:False;OFF:False;OFF:False;NOTSET:False";
            string expected = defender ? "False,False" : "ON:True;ON:False;ON:False;ON:False;ON:False;NOTSET:False";
            f.OnRead = delegate(int count) { return count == 2 ? external : f.Value; };
            RestoreCheck(f.Restore(), "one unrelated external component drift must remerge");
            RestoreCheck(f.Value == expected && f.Writes == 1, "restore owned components while preserving external component");
            RestoreCheck(f.Reads == 5 && PerformanceActionStore.All().Count == 0, "clear only after successful remerge and verification");
        }
        private static void RestoreAfterReapplyDrift(bool defender)
        {
            var f = new RestoreFixture(defender); f.Apply();
            string stored = Settings.LoadStr(PerformanceActionStore.Slot(f.Id, f.Target), "");
            f.Value = defender ? "False,True" : "ON:True;OFF:False;OFF:False;OFF:False;OFF:False;NOTSET:False";
            string external = f.Value;
            string expected = defender ? "False,False" : "ON:True;ON:False;ON:False;ON:False;ON:False;NOTSET:False";
            if (defender)
            {
                bool refused = false;
                try { PerformanceActionBackend.Apply(f.Id, f.Target, "True,True"); }
                catch (InvalidOperationException) { refused = true; }
                RestoreCheck(refused && f.Value == external && f.Reads == 0 && f.Writes == 0, "legacy Defender change must never be applied again");
                RestoreCheck(PerformanceActionStore.All().Count == 1
                    && Settings.LoadStr(PerformanceActionStore.Slot(f.Id, f.Target), "") == stored,
                    "refused reapplication must retain the legacy recovery record");
                RestoreCheck(f.Restore() && f.Value == expected && f.Writes == 1
                    && PerformanceActionStore.All().Count == 0, "retained Defender receipt restores owned components after drift");
            }
            else
            {
                RestoreCheck(PerformanceActionBackend.Apply(f.Id, f.Target, "off64") && f.Writes == 1,
                    "mitigation reapplication reconciles external component before one write");
                RestoreCheck(f.Value.StartsWith("OFF:True;", StringComparison.Ordinal), "reapplication preserves current external override");
                RestoreCheck(PerformanceActionStore.All()[0].Original == expected, "rebase retains owned originals and captures external component");
                RestoreCheck(f.Restore() && f.Value == expected && f.Writes == 2
                    && PerformanceActionStore.All().Count == 0, "reapplication restores external component and remaining owned originals");
            }
        }
        private static void RestorePersistentDrift()
        {
            var f = new RestoreFixture(true); f.Apply();
            f.OnRead = delegate(int count) { return count % 2 == 1 ? "True,True" : "False,True"; };
            RestoreCheck(!f.Restore(), "persistent drift must fail closed");
            RestoreCheck(f.Reads == 6 && f.Writes == 0 && PerformanceActionStore.All().Count == 1, "drift retry bounded at three without dropping recovery");
        }
        private static void RestoreWriteFailure()
        {
            var f = new RestoreFixture(true); f.Apply();
            string stored = Settings.LoadStr(PerformanceActionStore.Slot(f.Id, f.Target), "");
            f.OnWrite = delegate { f.Value = "False,True"; return false; };
            RestoreCheck(!f.Restore(), "partial failed write must report failure");
            RestoreCheck(Settings.LoadStr(PerformanceActionStore.Slot(f.Id, f.Target), "") == stored, "partial restore keeps original recovery receipt");
            f.OnWrite = null;
            RestoreCheck(f.Restore() && f.Value == f.Original && PerformanceActionStore.All().Count == 0, "retry restores remaining owned component");
        }
        private static void RestoreVerificationFailure()
        {
            var f = new RestoreFixture(true); f.Apply();
            string stored = Settings.LoadStr(PerformanceActionStore.Slot(f.Id, f.Target), "");
            f.OnRead = delegate(int count) { return count == 3 ? "False,True" : f.Value; };
            RestoreCheck(!f.Restore() && f.Writes == 1, "post-write mismatch must fail");
            RestoreCheck(Settings.LoadStr(PerformanceActionStore.Slot(f.Id, f.Target), "") == stored, "post-write drift never clears recovery");
        }
        private static void RestoreMalformedCurrent()
        {
            var f = new RestoreFixture(true); f.Apply(); f.Value = "True";
            RestoreCheck(!f.Restore() && f.Reads == 1 && f.Writes == 0 && PerformanceActionStore.All().Count == 1, "malformed provider snapshot preserves receipt");
        }
        private static void RestoreExternalOwnership()
        {
            var f = new RestoreFixture(false); f.Apply();
            f.Value = "ON:True;ON:True;ON:True;ON:True;ON:True;ON:True";
            string external = f.Value;
            RestoreCheck(f.Restore() && f.Value == external && f.Writes == 0 && PerformanceActionStore.All().Count == 0, "fully external state is preserved and relinquished");
        }
        private static void RestorePreparedComponents()
        {
            // 旧版写入只完成一半时留下的预备记录。
            var f = new RestoreFixture(true);
            f.Value = "True,False"; f.Persist(false);
            RestoreCheck(!PerformanceActionStore.All()[0].Committed, "partial application receipt remains prepared");
            RestoreCheck(f.Restore() && f.Value == f.Original && PerformanceActionStore.All().Count == 0, "prepared component restore remains supported");
            // 篡改防护拦下写入时，系统值从未改变，记录直接清除且不写入。
            var blocked = new RestoreFixture(true);
            blocked.Persist(false);
            RestoreCheck(blocked.Restore() && blocked.Value == blocked.Original && blocked.Writes == 0
                && PerformanceActionStore.All().Count == 0, "blocked legacy write clears its record without writing");
        }
        private static void RestoreInactiveDefender()
        {
            foreach (int state in new[] { 0, 1, 3 })
            {
                var f = new RestoreFixture(true); f.Apply(); f.Service = state;
                RestoreCheck(!f.Restore() && f.Reads == 0 && f.Writes == 0 && PerformanceActionStore.All().Count == 1, "inactive Defender remains a retained failure");
                string hint = PerformanceActionBackend.RestoreFailureHint(PerformanceActionStore.All()[0]);
                RestoreCheck(hint.Contains("Defender") && f.Reads == 0 && f.Writes == 0, "Defender hint does not read or write preferences");
                f.Service = 4;
                RestoreCheck(f.Restore() && f.Value == f.Original, "service return enables ordinary recovery");
            }
        }
        private static void RestoreUnavailableProvider()
        {
            Settings.UseTransientStoreForCurrentProcess(); string value = "absent"; int reads = 0;
            PerformanceActionBackend.ReadForTest = delegate { reads++; return value; };
            PerformanceActionBackend.WriteForTest = delegate(string id, string target, string next) { value = next; return true; };
            RestoreCheck(PerformanceActionBackend.Apply(PerformanceActionCatalog.OpenGl, @"C:\InertFixture\Game.exe", "1"), "driver fixture");
            PerformanceActionBackend.ReadForTest = delegate { throw new NotSupportedException("Driver unavailable"); };
            bool threw = false;
            try { PerformanceActionBackend.Restore(PerformanceActionStore.All()[0]); } catch (NotSupportedException) { threw = true; }
            RestoreCheck(threw && PerformanceActionStore.All().Count == 1, "unavailable driver preserves recovery");
        }
        private static void RestoreFailureHints()
        {
            int reads = 0, writes = 0;
            PerformanceActionBackend.ReadForTest = delegate { reads++; throw new IOException("Unexpected preference read"); };
            PerformanceActionBackend.WriteForTest = delegate { writes++; return false; };
            PerformanceActionBackend.NvAvailableForTest = delegate { return false; };
            string driver = PerformanceActionBackend.RestoreFailureHint(new PerformanceActionStore.Receipt { Id = PerformanceActionCatalog.OpenGl });
            RestoreCheck((driver.Contains("驱动") || driver.Contains("driver")) && reads == 0 && writes == 0, "driver failure hint is read-only");
            string generic = PerformanceActionBackend.RestoreFailureHint(new PerformanceActionStore.Receipt { Id = PerformanceActionCatalog.Quantum });
            RestoreCheck((generic.Contains("复核") || generic.Contains("verification")) && reads == 0 && writes == 0, "generic failure hint does not query provider");
        }
        private static void RestoreLegacyMitigationReceipt(bool committed, bool upgradedExpected)
        {
            var f = new RestoreFixture(false);
            f.Value = "OFF:False;OFF:False;OFF:False;OFF:False;OFF:False;ON:True";
            var receipt = new PerformanceActionStore.Receipt {
                Id = f.Id, Target = f.Target,
                Original = "ON:False;NOTSET:False;ON:True;NOTSET:True;ON:False",
                Expected = committed ? "OFF:False;OFF:False;OFF:False;OFF:False;OFF:False" + (upgradedExpected ? ";OFF:False" : "") : "off64",
                Committed = committed
            };
            string key = PerformanceActionStore.Slot(f.Id, f.Target);
            RestoreCheck(Settings.SaveStrDurable(key, PerformanceActionStore.Pack(receipt)), "legacy receipt persisted");
            RestoreCheck(f.Restore(), "legacy five-field receipt restores against six-field native query");
            RestoreCheck(f.Value == receipt.Original + ";ON:True", "legacy receipt preserves unrecorded current SimExec");
            RestoreCheck(f.Writes == 1 && PerformanceActionStore.All().Count == 0, "legacy restoration clears only after write/read verification");
        }
        private static void ReapplyLegacyMitigationReceipt(bool committed)
        {
            var f = new RestoreFixture(false);
            f.Value = "OFF:False;OFF:False;OFF:False;OFF:False;OFF:False;ON:True";
            var receipt = new PerformanceActionStore.Receipt {
                Id = f.Id, Target = f.Target, Original = "ON:False;ON:False;ON:False;ON:False;ON:False",
                Expected = committed ? f.Value : "off64", Committed = committed
            };
            string key = PerformanceActionStore.Slot(f.Id, f.Target), packed = PerformanceActionStore.Pack(receipt);
            RestoreCheck(Settings.SaveStrDurable(key, packed), "legacy reapply fixture persisted");
            RestoreCheck(PerformanceActionBackend.Apply(f.Id, f.Target, "off32") && f.Writes == 1,
                "off32 extends an old receipt only after capturing current SimExec");
            var upgraded = PerformanceActionStore.All()[0];
            RestoreCheck(upgraded.Original == receipt.Original + ";ON:True" && upgraded.Committed,
                "sixth original captured before write while old owned originals stay intact");
            RestoreCheck(f.Value.EndsWith(";OFF:True", StringComparison.Ordinal), "off32 preserves current SimExec override");
            RestoreCheck(f.Restore() && f.Value == receipt.Original + ";ON:True" && f.Writes == 2,
                "upgraded six-field receipt fully restores original five and newly captured sixth");
        }
        private static void ReapplyActualMitigationDrift()
        {
            var f = new RestoreFixture(false);
            f.Original = f.Value = "NOTSET:False;NOTSET:False;NOTSET:False;NOTSET:False;NOTSET:False;NOTSET:False";
            f.Apply();
            f.Value = "NOTSET:False;OFF:False;OFF:False;OFF:False;OFF:False;NOTSET:False";
            RestoreCheck(PerformanceActionBackend.Apply(f.Id, f.Target, "off64") && f.Writes == 1,
                "actual League drift vector reapplication succeeds");
            RestoreCheck(PerformanceActionStore.All()[0].Original == f.Original, "actual drift preserves all six original NOTSET values");
            RestoreCheck(f.Restore() && f.Value == f.Original && f.Writes == 2 && PerformanceActionStore.All().Count == 0,
                "actual drift eventually restores all six NOTSET values and clears recovery");
        }
        private static void ReapplyMitigationPersistentDrift()
        {
            var f = new RestoreFixture(false); f.Apply();
            string key = PerformanceActionStore.Slot(f.Id, f.Target), stored = Settings.LoadStr(key, "");
            f.OnRead = delegate(int count) { return (count % 2 == 1 ? "NOTSET:False" : "ON:True")
                + ";OFF:False;OFF:False;OFF:False;OFF:False;NOTSET:False"; };
            RestoreCheck(!PerformanceActionBackend.Apply(f.Id, f.Target, "off64"), "persistent apply drift fails closed");
            RestoreCheck(f.Reads == 4 && f.Writes == 0 && Settings.LoadStr(key, "") == stored,
                "three stable-read attempts retain old receipt without writes");
            string hint = PerformanceActionBackend.ApplyFailureHint(f.Id, f.Target);
            RestoreCheck((hint.Contains("持续") || hint.Contains("kept changing")) && f.Reads == 4 && f.Writes == 0,
                "persistent apply drift has a stable no-query failure hint");
        }
        private static void ReapplyMitigationSaveFailure()
        {
            var f = new RestoreFixture(false); f.Apply();
            string key = PerformanceActionStore.Slot(f.Id, f.Target), stored = Settings.LoadStr(key, "");
            f.Value = "ON:True;OFF:False;OFF:False;OFF:False;OFF:False;NOTSET:False";
            Settings.SuspendWritesForReset();
            RestoreCheck(!PerformanceActionBackend.Apply(f.Id, f.Target, "off64") && f.Writes == 0,
                "durable prepared record save failure precedes every native write");
            RestoreCheck(Settings.LoadStr(key, "") == stored, "save failure preserves old owned component receipt");
            int reads = f.Reads;
            string hint = PerformanceActionBackend.ApplyFailureHint(f.Id, f.Target);
            RestoreCheck((hint.Contains("保存失败") || hint.Contains("Could not save")) && f.Reads == reads,
                "save failure hint is specific without extra provider reads");
            Settings.UseTransientStoreForCurrentProcess();
        }
        private static void ReapplyMitigationPartialWrite()
        {
            var f = new RestoreFixture(false);
            f.Original = f.Value = "NOTSET:False;NOTSET:False;NOTSET:False;NOTSET:False;NOTSET:False;NOTSET:False";
            f.Apply();
            f.Value = "ON:True;OFF:False;OFF:False;OFF:False;OFF:False;ON:True";
            f.OnWrite = delegate(string next) {
                RestoreCheck(next == "off32", "partial retry uses requested managed operation");
                f.Value = "OFF:True;OFF:False;OFF:False;OFF:False;OFF:False;ON:True";
                return false;
            };
            RestoreCheck(!PerformanceActionBackend.Apply(f.Id, f.Target, "off32") && f.Writes == 1,
                "partial failed reconcile write reports failure");
            var pending = PerformanceActionStore.All()[0];
            RestoreCheck(!pending.Committed && pending.Expected == "OFF:True;OFF:False;OFF:False;OFF:False;OFF:False;OFF:True",
                "prepared recovery records precise planned states and overrides");
            RestoreCheck(pending.Original == "ON:True;NOTSET:False;NOTSET:False;NOTSET:False;NOTSET:False;ON:True",
                "prepared receipt keeps old owned originals and external CFG/SimExec values");
            f.OnWrite = null;
            RestoreCheck(f.Restore() && f.Value == pending.Original && f.Writes == 2 && PerformanceActionStore.All().Count == 0,
                "partial retry restores precisely owned components and preserves unwritten SimExec");
        }
        private static void ReapplyMitigationMalformedReceipt()
        {
            foreach (int malformed in new[] { 0, 1, 2 })
            {
                var f = new RestoreFixture(false); f.Apply();
                var receipt = PerformanceActionStore.All()[0];
                if (malformed == 0) receipt.Original = "invalid";
                else if (malformed == 1) receipt.Expected = "invalid";
                else { receipt.Committed = false; receipt.Expected = "invalid"; }
                string key = PerformanceActionStore.Slot(f.Id, f.Target), stored = PerformanceActionStore.Pack(receipt);
                RestoreCheck(Settings.SaveStrDurable(key, stored), "malformed reapply fixture persisted");
                f.Value = "NOTSET:False;OFF:False;OFF:False;OFF:False;OFF:False;NOTSET:False";
                RestoreCheck(!PerformanceActionBackend.Apply(f.Id, f.Target, "off64") && f.Writes == 0
                    && Settings.LoadStr(key, "") == stored, "malformed reapply snapshot retains receipt and performs no write");
                int reads = f.Reads;
                string hint = PerformanceActionBackend.ApplyFailureHint(f.Id, f.Target);
                RestoreCheck((hint.Contains("格式") || hint.Contains("snapshot")) && f.Reads == reads,
                    "malformed snapshot hint is specific and read-only");
            }
        }
    }
}
#endif
