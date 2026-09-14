// File purpose Isolated log switch correctness, temp log files only, no HKCU
// No UI and no real optimizations run
#if PAVISE_SELFTEST
using System;
using System.IO;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static int RunLogWritesRegressionTests()
        {
            string root = Path.Combine(Path.GetTempPath(), "PaviseLogWrites-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string oldLog = Logger.LogPath;
            Action<string>[] tests =
            {
                DisabledWritesAreDropped,
                ReEnableResumesWriting,
                ResetBarrierOutranksTheUserSwitch,
                ClearStillWorksWhileDisabled,
                TailStillReadableWhileDisabled,
                VersionAdvancesOnlyOnWrites,
                SettingsCachedReadFollowsWrites
            };
            try
            {
                foreach (Action<string> test in tests)
                {
                    Logger.ResetWriteBarrierForTest();
                    Lang.Cur = 0;
                    test(root);
                    Console.WriteLine("PASS " + test.Method.Name);
                }
                return 0;
            }
            finally
            {
                Logger.ResetWriteBarrierForTest();
                Logger.LogPath = oldLog;
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static string FreshLog(string root)
        {
            string path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".log");
            Logger.LogPath = path;
            return path;
        }

        private static string ReadLog(string path)
        {
            if (!File.Exists(path)) return "";
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
                return reader.ReadToEnd();
        }

        private static void LogWritesCheck(bool condition, string message)
        {
            if (!condition) throw new Exception("log writes: " + message);
        }

        private static void DisabledWritesAreDropped(string root)
        {
            string path = FreshLog(root);
            Logger.Log("before-switch");
            long lengthBefore = new FileInfo(path).Length;

            Logger.WritesEnabled = false;
            Logger.Log("must-not-appear");
            Logger.LogFailure("must-not-appear-either", new InvalidOperationException("x"));

            string text = ReadLog(path);
            LogWritesCheck(text.Contains("before-switch"), "the line written while enabled was lost");
            LogWritesCheck(text.IndexOf("must-not-appear", StringComparison.Ordinal) < 0,
                "a line was appended while writing was disabled");
            LogWritesCheck(new FileInfo(path).Length == lengthBefore,
                "the file grew while writing was disabled");
        }

        private static void ReEnableResumesWriting(string root)
        {
            string path = FreshLog(root);
            Logger.WritesEnabled = false;
            Logger.Log("dropped");
            Logger.WritesEnabled = true;
            Logger.Log("after-resume");

            string text = ReadLog(path);
            LogWritesCheck(text.IndexOf("dropped", StringComparison.Ordinal) < 0,
                "a line suppressed while disabled was replayed after re-enabling");
            LogWritesCheck(text.Contains("after-resume"), "writing did not resume");
        }

        // The user switch must not override the reset write barrier, the reset flow deletes files, any patch write meanwhile would recreate the directory
        private static void ResetBarrierOutranksTheUserSwitch(string root)
        {
            string path = FreshLog(root);
            Logger.Log("seed");
            long lengthBefore = new FileInfo(path).Length;

            Logger.SuspendWritesForReset();
            Logger.WritesEnabled = true;
            Logger.Log("must-not-appear");

            LogWritesCheck(new FileInfo(path).Length == lengthBefore,
                "the reset write barrier was defeated by the user switch");
        }

        private static void ClearStillWorksWhileDisabled(string root)
        {
            string path = FreshLog(root);
            Logger.Log("seed");
            LogWritesCheck(new FileInfo(path).Length > 0, "seed line was not written");

            Logger.WritesEnabled = false;
            Logger.Clear();

            LogWritesCheck(ReadLog(path).Length == 0, "clear did not empty the file while writing was disabled");
        }

        private static void TailStillReadableWhileDisabled(string root)
        {
            string path = FreshLog(root);
            Logger.Log("visible-line");
            Logger.WritesEnabled = false;

            string tail = Logger.Tail(50);
            LogWritesCheck(tail.Contains("visible-line"),
                "existing content became unreadable while writing was disabled");
        }

        // The log page relies on Version to decide whether to re-read the file, only a real disk write or clear advances it, tail reads and dropped writes don't
        private static void VersionAdvancesOnlyOnWrites(string root)
        {
            Logger.LogPath = Path.Combine(root, "version.log");
            Logger.WritesEnabled = true;
            long before = Logger.Version;
            Logger.Log("first");
            Eq(before + 1, Logger.Version);
            Logger.Tail(10);
            Eq(before + 1, Logger.Version);
            Logger.WritesEnabled = false;
            Logger.Log("dropped");
            Eq(before + 1, Logger.Version);
            Logger.WritesEnabled = true;
            Logger.Clear();
            Eq(before + 2, Logger.Version);
        }

        // Cached reads go back to source only after a write, any Save/Remove forces a re-read
        private static void SettingsCachedReadFollowsWrites(string root)
        {
            const string key = "SelfTestCachedFlag";
            Settings.Remove(key);
            Eq(false, Settings.LoadCached(key, false));
            Settings.Save(key, true);
            Eq(true, Settings.LoadCached(key, false));
            Eq(true, Settings.LoadCached(key, false));
            Settings.Save(key, false);
            Eq(false, Settings.LoadCached(key, false));
            Settings.Remove(key);
            Eq(true, Settings.LoadCached(key, true));
        }
    }
}
#endif
