#if PAVISE_SELFTEST && PAVISE_SHUTDOWN_WRITER_PROOF_RUNNER
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("Normal runtime forbidden in shutdown proof tests"); }
    }
    internal static class ShutdownWriterProofRunner
    {
        private static int assertions, suites, failures;
        private static string output;
        private static GameMode Subject()
        { return (GameMode)FormatterServices.GetUninitializedObject(typeof(GameMode)); }
        private static void Check(bool value, string reason)
        { assertions++; if (!value) throw new InvalidOperationException(reason); }
        private static void Run(string name, Action test)
        {
            suites++;
            try { test(); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + name + " | " + error); }
        }
        private static bool Finish(GameMode game, Func<bool>[] steps)
        { return game.FinishShutdownRecovery(steps[0], steps[1], steps[2], steps[3], steps[4], steps[5]); }
        private static int Main(string[] args)
        {
            if (args.Length != 1) return 2;
            output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
            using (var watchdog = new Timer(delegate { Environment.Exit(124); }, null, 20000, Timeout.Infinite))
            {
                Run("complete boolean matrix retains strict cleanup and conservative helper-exit proof", Matrix);
                Run("each exception or missing dependency remains partial without skipping later closures", Exceptions);
                Run("writer proof is false throughout work and cannot inherit an earlier success", Publication);
                Run("synchronous recovery failure permits selective reset and preserves original records", ResetAfterRestoreFailure);
                Run("helper failure prevents preference deletion and file cleanup", ResetBlockedByHelper);
                Console.WriteLine("RESULT suites=" + suites + " failed=" + failures + " assertions=" + assertions
                    + " settings=transient data=owned-temp native_calls=0 normal_game_mode_created=false runtime_started=false");
                return failures == 0 ? 0 : 1;
            }
        }
        private static void Matrix()
        {
            for (int bits = 0; bits < 64; bits++)
            {
                GameMode game = Subject(); var calls = new List<int>(); var steps = new Func<bool>[6];
                for (int i = 0; i < steps.Length; i++)
                {
                    int index = i, mask = bits;
                    steps[i] = delegate { calls.Add(index); return (mask & (1 << index)) != 0; };
                }
                bool complete = Finish(game, steps);
                Check(complete == (bits == 63), "Legacy Stop cleanup meaning changed for matrix " + bits);
                Check(game.ShutdownWritersStopped == ((bits & 14) == 14), "Helper false was softened or synchronous failure blocked writers " + bits);
                Check(calls.Count == 6, "Failure skipped independent closure " + bits);
                for (int i = 0; i < 6; i++) Check(calls[i] == i, "Shutdown order changed " + bits);
            }
        }
        private static void Exceptions()
        {
            for (int index = 0; index < 6; index++)
                for (int variant = 0; variant < 2; variant++)
                {
                    GameMode game = Subject(); int calls = 0; var steps = new Func<bool>[6];
                    for (int i = 0; i < 6; i++) steps[i] = delegate { calls++; return true; };
                    steps[index] = variant == 0 ? (Func<bool>)delegate { calls++; throw new IOException("fake synchronous or helper failure"); } : null;
                    Check(!Finish(game, steps), "Exceptional or missing step claimed complete cleanup");
                    Check(game.ShutdownWritersStopped == (index == 0 || index >= 4), "Unsafe exception proof at step " + index);
                    Check(calls == (variant == 0 ? 6 : 5), "Exceptional step skipped following closures");
                }
        }
        private static void Publication()
        {
            GameMode game = Subject(); var good = new Func<bool>[6];
            for (int i = 0; i < 6; i++) good[i] = delegate { Check(!game.ShutdownWritersStopped, "Proof published before all guards complete"); return true; };
            Check(Finish(game, good) && game.ShutdownWritersStopped, "All clean closure lacked proof");
            good[2] = delegate { Check(!game.ShutdownWritersStopped, "Earlier proof leaked into next shutdown"); return false; };
            Check(!Finish(game, good) && !game.ShutdownWritersStopped, "Stale proof survived helper failure");
        }
        private static string Fixture(string name)
        {
            Settings.UseTransientStoreForCurrentProcess(); Logger.ResetWriteBarrierForTest(); Logger.WritesEnabled = false;
            string directory = Path.Combine(output, name + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "Pavise.games.txt"), "owned fixture");
            Settings.Save("UiLight", true); Settings.SaveStr("PrevPowerPlan", "original fixture");
            Settings.SaveStr("Crash_BoostEntriesV2", "pending fixture"); return directory;
        }
        private static void ResetAfterRestoreFailure()
        {
            string directory = Fixture("sync-restore-failure"); GameMode game = Subject(); int steps = 0;
            Func<bool> restore = delegate { steps++; return false; }, close = delegate { steps++; return true; };
            bool clean = game.FinishShutdownRecovery(restore, close, close, close, restore, restore);
            Check(!clean && game.ShutdownWritersStopped && steps == 6, "Synchronous restoration did not produce separate proof");
            ResetDataReport result = AppDataReset.Execute(directory, delegate { return clean || game.ShutdownWritersStopped; });
            Check(result.Completed && result.Files == 1 && !Settings.Load("UiLight", false), "Proved shutdown could not reset app preferences");
            Check(Settings.LoadStr("PrevPowerPlan", "") == "original fixture"
                && Settings.LoadStr("Crash_BoostEntriesV2", "") == "pending fixture", "Pending recovery records were erased");
        }
        private static void ResetBlockedByHelper()
        {
            foreach (int failedIndex in new[] { 1, 2, 3 })
            {
                string directory = Fixture("helper-failure-" + failedIndex); GameMode game = Subject(); var steps = new Func<bool>[6];
                for (int i = 0; i < 6; i++) steps[i] = delegate { return true; };
                steps[failedIndex] = delegate { return false; };
                bool clean = Finish(game, steps);
                ResetDataReport result = AppDataReset.Execute(directory, delegate { return clean || game.ShutdownWritersStopped; });
                Check(!result.Completed && result.Files == 0 && !game.ShutdownWritersStopped, "Unproved helper shutdown allowed reset");
                Check(File.Exists(Path.Combine(directory, "Pavise.games.txt")) && Settings.Load("UiLight", false), "Unproved helper shutdown erased user data");
                Check(Settings.Save("UiLight", true), "Blocked cleanup sealed preferences");
                Check(Settings.LoadStr("Crash_BoostEntriesV2", "") == "pending fixture", "Blocked cleanup erased receipt");
            }
        }
    }
}
#endif
