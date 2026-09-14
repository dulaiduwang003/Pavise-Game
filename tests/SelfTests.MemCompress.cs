// File purpose Isolated fake MMAgent checks, no PowerShell, no MMAgent changes, no system writes
// Memory compression regression: injected state and command stand-ins, verifies idempotence, receipts, post-check and failure recovery, never touches the system
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
                MemCompressRestoresOnlyWhatWasOn,
                MemCompressExternalOffIsSkippedWithoutOwnership,
                MemCompressOwnedStateRepairsMissingLedger,
                MemCompressFailedCommandStillUsesVerifiedState,
                MemCompressUnchangedFailureClearsFreshReceipt,
                MemCompressPartialFailureKeepsRecoveryLedger,
                MemCompressRestoreUsesVerifiedState,
                MemCompressZeroReceiptNeedsNoMMAgent,
                MemCompressFailedRestoreKeepsReceipt
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
            // In order: state before Enable, state after disable failure, state after restore failure
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
