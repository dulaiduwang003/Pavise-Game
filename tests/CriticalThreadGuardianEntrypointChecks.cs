#if PAVISE_GUARDIAN_ENTRY_CHECKS
using System;
using System.IO;
using System.Reflection;

namespace PaviseApp
{
    internal static class Paths
    {
        internal static Exception Failure;
        internal static int Calls;
        internal static string ResolveStartupDataArgument(string[] args)
        { Calls++; if (Failure != null) throw Failure; return "isolated-no-file-access"; }
    }
    internal static class CriticalThreadGuardian
    {
        internal const string Command = "--critical-thread-guardian";
        internal static Exception Failure;
        internal static int Calls, Code;
        internal static bool Recognized;
        internal static bool TryRun(string[] args, string data, out int code)
        { Calls++; if (Failure != null) throw Failure; code = Code; return Recognized; }
    }
    internal static class CriticalThreadGuardianEntrypointChecks
    {
        private static int assertions;
        private static void Check(bool value, string message)
        { assertions++; if (!value) throw new InvalidOperationException("FAIL " + message); }
        private static bool Invoke(string[] args)
        {
            try { return (bool)typeof(Program).GetMethod("TryHandleCriticalThreadGuardian", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { args }); }
            catch (TargetInvocationException error) { throw error.InnerException; }
        }
        private static void Reset()
        { Paths.Failure = null; Paths.Calls = 0; CriticalThreadGuardian.Failure = null; CriticalThreadGuardian.Calls = 0; CriticalThreadGuardian.Code = 0; CriticalThreadGuardian.Recognized = true; Environment.ExitCode = 0; }
        private static void Run()
        {
            foreach (var args in new[] { null, new string[0], new[] { "--ordinary-application-option" } }) {
                Reset(); Check(!Invoke(args) && Paths.Calls == 0 && CriticalThreadGuardian.Calls == 0,
                    "ordinary application arguments were consumed by the child entrypoint");
            }
            Reset(); CriticalThreadGuardian.Recognized = false;
            Check(Invoke(new[] { "malformed", CriticalThreadGuardian.Command }) && Environment.ExitCode == 13,
                "a malformed reserved invocation fell through or reported success");
            Reset(); Paths.Failure = new ArgumentException("isolated argument failure");
            Check(Invoke(new[] { CriticalThreadGuardian.Command }) && Environment.ExitCode == 13 && CriticalThreadGuardian.Calls == 0,
                "argument rejection entered the guardian or lost its failure exit");
            foreach (Exception error in new Exception[] { new IOException("isolated cleanup failure"), new AggregateException(new IOException("isolated close failure")), new InvalidOperationException("isolated child failure") }) {
                Reset(); CriticalThreadGuardian.Failure = error;
                bool consumed = false; Exception escaped = null;
                try { consumed = Invoke(new[] { CriticalThreadGuardian.Command }); } catch (Exception fault) { escaped = fault; }
                Check(escaped == null && consumed && Environment.ExitCode == 14,
                    "a reserved child failure escaped the entrypoint or reported a successful exit");
            }
            Reset(); Paths.Failure = new IOException("isolated startup data failure");
            Check(Invoke(new[] { CriticalThreadGuardian.Command }) && Environment.ExitCode == 14 && CriticalThreadGuardian.Calls == 0,
                "startup source I/O escaped the reserved child boundary");
            Reset();
            Check(Invoke(new[] { CriticalThreadGuardian.Command }) && Environment.ExitCode == 0 && CriticalThreadGuardian.Calls == 1,
                "a successful child exit was changed");
            Reset(); CriticalThreadGuardian.Code = 14;
            Check(Invoke(new[] { CriticalThreadGuardian.Command }) && Environment.ExitCode == 14,
                "a guardian protocol failure became success");
        }
        private static void Main()
        {
            try { Run(); Console.WriteLine("PASS guardian entrypoint assertions=" + assertions + "; exact Program partial with external fault doubles; no native/GUI/application Main"); Environment.ExitCode = 0; }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
        }
    }
}
#endif
