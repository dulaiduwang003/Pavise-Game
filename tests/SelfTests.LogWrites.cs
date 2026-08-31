// Isolated log-switch correctness: temporary log files only, no HKCU access,
// no UI and no real optimization run.
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
                TailStillReadableWhileDisabled
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

        // 用户开关不能盖过重置写屏障 重置流程要删文件 期间任何补写都会把目录又建出来
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
    }
}
#endif
