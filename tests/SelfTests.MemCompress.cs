#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int memCompressChecks;

        internal static int RunMemCompressRegressionTests()
        {
            Action[] tests =
            {
                MemCompressParsesInvariantBoolPair,
                MemCompressUnknownStateDoesNotWrite,
                MemCompressUnavailableOutputIsCached,
                MemCompressUnknownRestoreKeepsReceipt,
                MemCompressUnavailableRestoreKeepsReceipt,
                MemCompressQueryFailuresRemainRetryable,
                MemCompressRawOffStateStaysExternal,
                MemCompressRestoresOnlyWhatWasOn,
                MemCompressExternalOffIsSkippedWithoutOwnership,
                MemCompressOwnedStateRepairsMissingLedger,
                MemCompressFailedCommandStillUsesVerifiedState,
                MemCompressUnchangedFailureClearsFreshReceipt,
                MemCompressPartialFailureKeepsRecoveryLedger,
                MemCompressRestoreUsesVerifiedState,
                MemCompressZeroReceiptNeedsNoMMAgent,
                MemCompressFailedRestoreKeepsReceipt,
                MemCompressAuditCapacityAndState,
                MemCompressAuditRevalidatesOwnedState
            };
            memCompressChecks = 0;
            foreach (Action test in tests)
            {
                MemCompressReset();
                try { test(); }
                finally { MemCompressReset(); }
                Console.WriteLine("PASS " + test.Method.Name);
            }
            Console.WriteLine("PASS mem-compress assertions=" + memCompressChecks
                + " powershell=untouched mmagent=untouched windows_shown=false");
            return tests.Length;
        }

        private static void MemCompCheck(bool good, string message)
        {
            if (!good) throw new InvalidOperationException("Mem compress regression: " + message);
            memCompressChecks++;
        }

        private static void MemCompressAuditCapacityAndState()
        {
            ulong threshold = 24UL * 1048576;
            MemCompCheck(!MemCompressTweak.RamEligible(threshold - 1), "Below 24 GB must not qualify");
            MemCompCheck(MemCompressTweak.RamEligible(threshold), "Exactly 24 GB must qualify");
            foreach (ulong kb in new[] { 0UL, 16UL * 1048576, threshold, 32UL * 1048576 })
            foreach (bool capacityKnown in new[] { false, true })
            foreach (bool stateKnown in new[] { false, true })
            foreach (bool compression in new[] { false, true })
            foreach (bool combining in new[] { false, true })
            {
                var report = new AuditReport();
                SystemAudit.BuildMemoryCompression(report, capacityKnown, kb, stateKnown, compression, combining, false);
                bool wanted = capacityKnown && kb >= threshold && stateKnown && (compression || combining);
                MemCompCheck(report.MemoryCompressionCanDisable == wanted, "Audit eligibility must use installed capacity and known state");
                MemCompCheck(report.Persistent.Count == 1 && report.Persistent[0].FixKey == "memcompress", "Memory action must be part of audit");
                MemCompCheck(report.Persistent[0].Warn == wanted, "Already-off and ineligible machines must not be flagged for disabling");
            }
        }

        private static void MemCompressAuditRevalidatesOwnedState()
        {
            Settings.Save("MemCompressOffByPavise", true);
            Settings.SaveStr("PrevMMAgent", "1,0");
            var fake = new MemCompressFake();
            fake.State(true, true, true);
            fake.State(true, false, false);
            fake.CommandResults.Enqueue(true);
            fake.Install();
            MemCompCheck(MemCompressTweak.Enable(true), "Audit must handle state drift despite old ownership flag");
            MemCompCheck(fake.Queries == 2 && fake.Commands.Count == 1, "Audit action must read back actual MMAgent state");
            MemCompCheck(Settings.LoadStr("PrevMMAgent", "") == "1,0", "Reapply must preserve original recovery state");
        }

        private static void MemCompressParsesInvariantBoolPair()
        {
            bool compression, combining;
            MemCompCheck(MemCompressTweak.ParseState("True,False", out compression, out combining)
                && compression && !combining, "the .NET bool pair must parse position by position");
            MemCompCheck(MemCompressTweak.ParseState(" true , TRUE \r\n", out compression, out combining)
                && compression && combining, "whitespace and casing from the pipe must not matter");
            MemCompCheck(!MemCompressTweak.ParseState("", out compression, out combining),
                "empty output means the query failed, never a default state");
            MemCompCheck(!MemCompressTweak.ParseState("Enabled,Disabled", out compression, out combining),
                "localized or unexpected tokens must be rejected, not guessed");
            MemCompCheck(!MemCompressTweak.ParseState(MemCompressTweak.UnavailableOutput, out compression, out combining),
                "the unsupported-capability sentinel must never be parsed as an off-state pair");
        }

        private static void MemCompressUnknownStateDoesNotWrite()
        {
            foreach (bool compression in new[] { false, true })
            foreach (bool combining in new[] { false, true })
            {
                var fake = new MemCompressFake();
                fake.State(false, compression, combining);
                fake.State(false, compression, combining);
                fake.Install();
                int generation = Settings.MutationGeneration;
                MemCompCheck(!MemCompressTweak.CurrentlyOff(),
                    "an unreadable state must not be described as already off, regardless of output defaults");
                MemCompCheck(!MemCompressTweak.Enable(),
                    "an unreadable pre-state must reject apply without inventing a snapshot");
                MemCompCheck(fake.Queries == 2 && fake.Commands.Count == 0,
                    "unknown-state checks must never execute Disable-MMAgent");
                MemCompCheck(Settings.MutationGeneration == generation && !MemCompressTweak.OwnsState
                    && Settings.LoadStr("PrevMMAgent", "") == "",
                    "query failure must not write settings, ownership or an original-state receipt");
            }
        }

        private sealed class MemCompressScriptReply
        {
            internal readonly bool Success;
            internal readonly string Output;
            internal MemCompressScriptReply(bool success, string output) { Success = success; Output = output; }
        }

        private sealed class MemCompressScriptFake
        {
            internal readonly Queue<MemCompressScriptReply> Replies = new Queue<MemCompressScriptReply>();
            internal readonly List<string> Scripts = new List<string>(), Labels = new List<string>();
            internal void Reply(bool success, string output) { Replies.Enqueue(new MemCompressScriptReply(success, output)); }
            private bool Run(string script, string label, out string output)
            {
                Scripts.Add(script); Labels.Add(label);
                if (Replies.Count == 0) throw new InvalidOperationException("Unexpected MMAgent script invocation in an isolated test.");
                MemCompressScriptReply reply = Replies.Dequeue(); output = reply.Output; return reply.Success;
            }
            internal void Install() { MemCompressTweak.ScriptForTest = Run; }
        }

        private static void MemCompressUnavailableOutputIsCached()
        {
            var fake = new MemCompressScriptFake();
            fake.Reply(true, MemCompressTweak.UnavailableOutput + "\r\n");
            fake.Install();
            int generation = Settings.MutationGeneration;
            bool compression, combining;
            MemCompCheck(!MemCompressTweak.QueryState(out compression, out combining),
                "a successful unsupported-capability probe must remain an unknown state");
            MemCompCheck(!MemCompressTweak.CurrentlyOff() && !MemCompressTweak.Enable(),
                "cached missing MMAgent must never masquerade as a disabled feature");
            MemCompCheck(fake.Scripts.Count == 1 && fake.Scripts[0] == MemCompressTweak.QueryScript
                && fake.Labels[0] == "mmagent-query",
                "definitive capability absence must be cached instead of repeatedly launching query scripts");
            MemCompCheck(Settings.MutationGeneration == generation && !MemCompressTweak.OwnsState,
                "the missing-command path must not gain ownership or write configuration");
        }

        private static void MemCompressUnknownRestoreKeepsReceipt()
        {
            Settings.Save("MemCompressOffByPavise", true);
            Settings.SaveStr("PrevMMAgent", "1,0");
            var fake = new MemCompressFake();
            fake.State(false, true, false);
            fake.CommandResults.Enqueue(true);
            fake.Install();
            int generation = Settings.MutationGeneration;
            MemCompCheck(!MemCompressTweak.Restore(),
                "a successful restore command without readable post-state is not a verified restore");
            MemCompCheck(fake.Queries == 1 && fake.Commands.Count == 1
                && fake.Commands[0] == "Enable-MMAgent -MemoryCompression",
                "unverified restore must attempt only its originally-on feature and read back the state");
            MemCompCheck(Settings.MutationGeneration == generation && MemCompressTweak.EnabledByPavise
                && Settings.LoadStr("PrevMMAgent", "") == "1,0",
                "unknown restore results must retain the exact receipt and ownership for a later retry");
        }

        private static void MemCompressUnavailableRestoreKeepsReceipt()
        {
            Settings.Save("MemCompressOffByPavise", true);
            Settings.SaveStr("PrevMMAgent", "1,1");
            var fake = new MemCompressScriptFake();
            fake.Reply(true, MemCompressTweak.UnavailableOutput);
            fake.Install();
            int generation = Settings.MutationGeneration;
            MemCompCheck(!MemCompressTweak.Enable(true),
                "owned-state verification must reject a missing capability rather than assuming an old flag is proof");
            MemCompCheck(!MemCompressTweak.Restore(),
                "a cached unsupported capability cannot prove the original state was restored");
            MemCompCheck(fake.Scripts.Count == 1 && fake.Scripts[0] == MemCompressTweak.QueryScript,
                "known missing MMAgent must suppress both repeated queries and restore command launches");
            MemCompCheck(Settings.MutationGeneration == generation && MemCompressTweak.EnabledByPavise
                && Settings.LoadStr("PrevMMAgent", "") == "1,1",
                "capability absence during restore must preserve the original recovery receipt without rewriting it");
        }

        private static void MemCompressQueryFailuresRemainRetryable()
        {
            foreach (MemCompressScriptReply first in new[] {
                new MemCompressScriptReply(false, ""),
                new MemCompressScriptReply(false, MemCompressTweak.UnavailableOutput),
                new MemCompressScriptReply(true, "not-a-bool-pair") })
            {
                var fake = new MemCompressScriptFake();
                fake.Reply(first.Success, first.Output);
                fake.Reply(true, "True,False");
                fake.Install();
                int generation = Settings.MutationGeneration;
                bool compression, combining;
                MemCompCheck(!MemCompressTweak.QueryState(out compression, out combining),
                    "runner failures and malformed output must not be treated as a known state");
                MemCompCheck(MemCompressTweak.QueryState(out compression, out combining) && compression && !combining,
                    "ordinary failures must remain retryable instead of becoming a permanent missing-capability cache");
                MemCompCheck(fake.Scripts.Count == 2 && fake.Scripts.TrueForAll(delegate(string script) { return script == MemCompressTweak.QueryScript; })
                    && Settings.MutationGeneration == generation,
                    "query retries must stay read-only and retain the guarded production query script");
            }
        }

        private static void MemCompressRawOffStateStaysExternal()
        {
            var fake = new MemCompressScriptFake();
            fake.Reply(true, "False,False"); fake.Reply(true, " False , False\r\n");
            fake.Install();
            int generation = Settings.MutationGeneration;
            MemCompCheck(MemCompressTweak.Enable() && MemCompressTweak.CurrentlyOff(),
                "a supported, genuinely off state must remain recognizable through raw query output");
            MemCompCheck(fake.Scripts.Count == 2 && fake.Scripts.TrueForAll(delegate(string script) { return script == MemCompressTweak.QueryScript; })
                && Settings.MutationGeneration == generation && !MemCompressTweak.OwnsState,
                "an externally disabled state needs neither a write command nor a Pavise receipt");
        }

        private static void MemCompressRestoresOnlyWhatWasOn()
        {
            MemCompCheck(MemCompressTweak.RestoreArguments("1,1")
                == " -MemoryCompression -PageCombining", "both on before means both come back");
            MemCompCheck(MemCompressTweak.RestoreArguments("1,0") == " -MemoryCompression",
                "combining that was already off must stay off on restore");
            MemCompCheck(MemCompressTweak.RestoreArguments("0,0") == "",
                "nothing on before means restore re-enables nothing");
            MemCompCheck(MemCompressTweak.RestoreArguments("") == "",
                "a missing snapshot must never invent state");
            bool compression, combining;
            MemCompCheck(MemCompressTweak.ParseSnapshot("1,0", out compression, out combining)
                && compression && !combining, "a strict receipt must preserve both original bits");
            MemCompCheck(!MemCompressTweak.ParseSnapshot("True,False", out compression, out combining),
                "a receipt must reject non-canonical values");
            MemCompCheck(MemCompressTweak.SnapshotRestored("1,0", true, true),
                "restore verifies what was originally on and leaves external changes alone");
            MemCompCheck(!MemCompressTweak.SnapshotRestored("1,1", true, false),
                "restore cannot settle while an originally-on feature is still off");
        }

        private sealed class MemCompressFake
        {
            internal readonly Queue<bool> QueryResults = new Queue<bool>();
            internal readonly Queue<bool> Compression = new Queue<bool>();
            internal readonly Queue<bool> Combining = new Queue<bool>();
            internal readonly Queue<bool> CommandResults = new Queue<bool>();
            internal readonly List<string> Commands = new List<string>();
            internal int Queries;

            internal void State(bool ok, bool compression, bool combining)
            {
                QueryResults.Enqueue(ok);
                Compression.Enqueue(compression);
                Combining.Enqueue(combining);
            }

            internal bool Query(out bool compression, out bool combining)
            {
                Queries++;
                if (QueryResults.Count == 0)
                    throw new InvalidOperationException("Mem compress fake ran out of query states.");
                bool ok = QueryResults.Dequeue();
                compression = Compression.Dequeue();
                combining = Combining.Dequeue();
                return ok;
            }

            internal bool Run(string command, string label, out string output)
            {
                Commands.Add(command);
                output = "";
                if (CommandResults.Count == 0)
                    throw new InvalidOperationException("Mem compress fake ran out of command results.");
                return CommandResults.Dequeue();
            }

            internal void Install()
            {
                MemCompressTweak.QueryForTest = Query;
                MemCompressTweak.RunForTest = Run;
            }
        }

        private static void MemCompressReset()
        {
            MemCompressTweak.ResetForTest();
            Settings.Save("MemCompressOffByPavise", false);
            Settings.SaveStr("PrevMMAgent", "");
        }

        private static void MemCompressExternalOffIsSkippedWithoutOwnership()
        {
            var fake = new MemCompressFake();
            fake.State(true, false, false);
            fake.Install();
            bool ok = MemCompressTweak.Enable();
            MemCompCheck(ok, "an externally satisfied state must be accepted");
            MemCompCheck(fake.Queries == 1 && fake.Commands.Count == 0,
                "False/False must skip Disable-MMAgent entirely");
            MemCompCheck(!MemCompressTweak.OwnsState,
                "an external state must not gain a Pavise receipt");
        }

        private static void MemCompressFailedCommandStillUsesVerifiedState()
        {
            var fake = new MemCompressFake();
            fake.State(true, true, false);
            fake.State(true, false, false);
            fake.CommandResults.Enqueue(false);
            fake.Install();
            MemCompCheck(MemCompressTweak.Enable(),
                "a nonzero command with verified False/False is still a successful apply");
            MemCompCheck(fake.Queries == 2 && fake.Commands.Count == 1,
                "command failure must not short-circuit the post-state query");
            MemCompCheck(Settings.LoadStr("PrevMMAgent", "") == "1,0"
                && MemCompressTweak.EnabledByPavise,
                "a verified change must retain its exact original-state receipt");
        }

        private static void MemCompressOwnedStateRepairsMissingLedger()
        {
            Settings.Save("MemCompressOffByPavise", true);
            Settings.SaveStr("PrevMMAgent", "1,1");
            var fake = new MemCompressFake();
            fake.Install();
            bool ok = MemCompressTweak.Enable();
            MemCompCheck(ok && fake.Queries == 0 && fake.Commands.Count == 0,
                "an already-owned state must not rerun MMAgent commands");
            MemCompCheck(MemCompressTweak.OwnsState,
                "re-enabling an owned state keeps the receipt untouched");
        }

        private static void MemCompressUnchangedFailureClearsFreshReceipt()
        {
            var fake = new MemCompressFake();
            fake.State(true, true, false);
            fake.State(true, true, false);
            fake.CommandResults.Enqueue(false);
            fake.Install();
            MemCompCheck(!MemCompressTweak.Enable(), "an unchanged enabled state is not success");
            MemCompCheck(fake.Queries == 2,
                "the unchanged verdict must come from a real post-state query");
            MemCompCheck(!MemCompressTweak.OwnsState,
                "when the original state is proven intact the fresh receipt must be cleared");
        }

        private static void MemCompressPartialFailureKeepsRecoveryLedger()
        {
            var fake = new MemCompressFake();
            fake.State(true, true, true);
            fake.State(true, false, true);
            fake.State(true, false, true);
            fake.CommandResults.Enqueue(false);
            fake.CommandResults.Enqueue(false);
            fake.Install();
            bool ok = MemCompressTweak.Enable();
            MemCompCheck(!ok, "a partial False/True result must remain a failed apply");
            MemCompCheck(fake.Queries == 3 && fake.Commands.Count == 2
                && fake.Commands[1].StartsWith("Enable-MMAgent", StringComparison.Ordinal),
                "a partial write must attempt immediate rollback and verify it");
            MemCompCheck(Settings.LoadStr("PrevMMAgent", "") == "1,1"
                && MemCompressTweak.OwnsState,
                "failed rollback must keep the original receipt");
            MemCompCheck(MemCompressTweak.OwnsState,
                "a retained recovery receipt must stay reachable for the next restore");
        }

        private static void MemCompressRestoreUsesVerifiedState()
        {
            Settings.Save("MemCompressOffByPavise", true);
            Settings.SaveStr("PrevMMAgent", "1,0");
            var fake = new MemCompressFake();
            fake.State(true, true, false);
            fake.CommandResults.Enqueue(false);
            fake.Install();
            MemCompCheck(MemCompressTweak.Restore(),
                "a nonzero restore command is settled by the verified restored state");
            MemCompCheck(fake.Queries == 1 && fake.Commands.Count == 1,
                "restore command failure must not short-circuit its post-state query");
            MemCompCheck(!MemCompressTweak.OwnsState,
                "verified restore must clear both ownership markers");
        }

        private static void MemCompressFailedRestoreKeepsReceipt()
        {
            Settings.Save("MemCompressOffByPavise", true);
            Settings.SaveStr("PrevMMAgent", "1,1");
            var fake = new MemCompressFake();
            fake.State(true, true, false);
            fake.CommandResults.Enqueue(false);
            fake.Install();
            MemCompCheck(!MemCompressTweak.Restore(),
                "restore must fail while an originally-on feature is still off");
            MemCompCheck(MemCompressTweak.EnabledByPavise
                && Settings.LoadStr("PrevMMAgent", "") == "1,1",
                "failed restore must retain both ownership markers for retry");
        }

        private static void MemCompressZeroReceiptNeedsNoMMAgent()
        {
            Settings.Save("MemCompressOffByPavise", true);
            Settings.SaveStr("PrevMMAgent", "0,0");
            var fake = new MemCompressFake();
            fake.Install();
            MemCompCheck(MemCompressTweak.Restore(),
                "a legacy 0,0 receipt has no physical state to restore");
            MemCompCheck(fake.Queries == 0 && fake.Commands.Count == 0
                && !MemCompressTweak.OwnsState,
                "a no-op receipt must clear without touching a disabled MMAgent path");
        }
    }
}
#endif
