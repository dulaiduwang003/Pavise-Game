#if PAVISE_SELFTEST && PAVISE_GUARDIAN_CLOSE_CHECKS
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("Product runtime is unavailable in guardian close checks"); }
    }
    internal static class GuardianCloseChecks
    {
        private static int checks;
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        private sealed class Resource : IDisposable
        {
            internal readonly int Id;
            internal readonly List<int> Order;
            internal int Calls, Failures;
            internal bool Permanent;
            internal Resource(int id, List<int> order) { Id = id; Order = order; }
            public void Dispose() { Calls++; Order.Add(Id); if (Permanent || Failures-- > 0) throw new IOException("fake original resource " + Id); }
        }
        private sealed class RawResource : IDisposable
        {
            internal IntPtr Handle = new IntPtr(901);
            internal bool Uncertain, Throw, Success;
            internal int RawCalls;
            public void Dispose() { HardThreadRawHandleClosure.Close(ref Handle, ref Uncertain, delegate(IntPtr original) {
                RawCalls++; Check(original == new IntPtr(901), "raw closer lost the retained original handle");
                if (Throw) throw new IOException("fake raw close failure"); return Success;
            }); }
        }
        private static AggregateException Fault(IDisposable original)
        {
            try { original.Dispose(); } catch (AggregateException error) { return error; }
            throw new InvalidOperationException("original close fault disappeared");
        }
        private static void IndependentResources()
        {
            for (int failed = 0; failed < 5; failed++) {
                var order = new List<int>(); var resources = new Resource[5];
                for (int index = 0; index < resources.Length; index++) resources[index] = new Resource(index, order);
                resources[failed].Failures = 1; var closure = new HardThreadResourceClosure(resources);
                Check(!closure.ResourcesClosed, "unattempted original resources were reported closed");
                var error = Fault(closure);
                Check(error.InnerExceptions.Count == 1 && order.Count == 5, "a failed closer prevented later original resources from being attempted");
                for (int index = 0; index < resources.Length; index++) Check(order[index] == index && resources[index].Calls == 1, "original resource close order changed or repeated");
                Check(!closure.ResourcesClosed, "a failed original resource was falsely reported closed");
                closure.Dispose(); Check(closure.ResourcesClosed, "managed resource closure could not resume after a transient failure");
                for (int index = 0; index < resources.Length; index++) Check(resources[index].Calls == (index == failed ? 2 : 1), "successful resource closure was repeated during retry");
                closure.Dispose(); Check(order.Count == 6, "completed resource closure was executed again");
            }
            var originals = new IDisposable[] { new Resource(0, new List<int>()) { Permanent = true }, new Resource(1, new List<int>()) };
            var retained = new HardThreadResourceClosure(originals); originals[0] = null;
            Check(Fault(retained).InnerExceptions.Count == 1 && !retained.ResourcesClosed, "caller replaced the retained original resource array");
            var all = new Resource[5]; for (int index = 0; index < all.Length; index++) all[index] = new Resource(index, new List<int>()) { Permanent = true };
            var allFailed = new HardThreadResourceClosure(all);
            Check(Fault(allFailed).InnerExceptions.Count == 5 && !allFailed.ResourcesClosed, "multiple original cleanup failures were lost or reported closed");
            var absent = new HardThreadResourceClosure(null, null, null, null, null); absent.Dispose(); Check(absent.ResourcesClosed, "partial empty setup could not close");
        }
        private static void OriginalRawHandles()
        {
            foreach (bool throws in new[] { false, true }) {
                var raw = new RawResource { Throw = throws };
                var other = new Resource(1, new List<int>()); var closure = new HardThreadResourceClosure(raw, other);
                Fault(closure);
                Check(raw.RawCalls == 1 && raw.Handle == IntPtr.Zero && raw.Uncertain && other.Calls == 1 && !closure.ResourcesClosed,
                    "unconfirmed raw closure lost its fault or skipped another original resource");
                Fault(closure);
                Check(raw.RawCalls == 1 && raw.Handle == IntPtr.Zero && raw.Uncertain && !closure.ResourcesClosed,
                    "an uncertain raw handle was retried or later reported closed");
            }
            var successful = new RawResource { Success = true }; successful.Dispose(); successful.Dispose();
            Check(successful.RawCalls == 1 && successful.Handle == IntPtr.Zero && !successful.Uncertain, "confirmed raw handle closure repeated its native attempt");
        }
        private static void ExitBoundary()
        {
            foreach (int prior in new[] { 0, 13, 14 }) {
                int code = prior; var failed = new Resource(0, new List<int>()) { Permanent = true }; var later = new Resource(1, new List<int>());
                var closure = new HardThreadResourceClosure(failed, later);
                try { HardThreadGuardianExit.CloseForExit(closure, ref code); }
                catch { throw new InvalidOperationException("cleanup failure escaped the guardian exit boundary"); }
                Check(code == 14 && later.Calls == 1 && !closure.ResourcesClosed, "guardian cleanup failure did not preserve failure exit and remaining obligations");
            }
            int original = 13; HardThreadGuardianExit.CloseForExit(new HardThreadResourceClosure(null, null), ref original);
            Check(original == 13, "successful cleanup erased the original protocol failure");
            original = 0; HardThreadGuardianExit.CloseForExit(null, ref original); Check(original == 0, "empty cleanup changed a successful exit");
        }
        private static void ActualRegistration()
        {
            Type guardian = typeof(CriticalThreadGuardian);
            Type registrationType = guardian.GetNestedType("NativeRegistration", BindingFlags.NonPublic);
            var registration = (IDisposable)Activator.CreateInstance(registrationType, true);
            Check(!(bool)registrationType.GetProperty("ResourcesClosed", Private).GetValue(registration, null), "unattempted registration reported resource closure");
            foreach (string name in new[] { "Target", "Parent", "Root" }) {
                var field = registrationType.GetField(name, Private);
                var pin = FormatterServices.GetUninitializedObject(field.FieldType); GC.SuppressFinalize(pin);
                field.FieldType.GetField("closeFault", Private).SetValue(pin, true);
                field.SetValue(registration, pin);
            }
            var faults = Fault(registration);
            Check(faults.InnerExceptions.Count == 3 && !(bool)registrationType.GetProperty("ResourcesClosed", Private).GetValue(registration, null),
                "actual registration did not retain every original close obligation");
            faults = Fault(registration);
            Check(faults.InnerExceptions.Count == 3 && !(bool)registrationType.GetProperty("ResourcesClosed", Private).GetValue(registration, null),
                "actual registration converted a latched pin fault into closed resources");
            int code = 0; HardThreadGuardianExit.CloseForExit(registration, ref code);
            Check(code == 14, "actual registration close failure was reported successful at exit");
            var empty = (IDisposable)Activator.CreateInstance(registrationType, true); empty.Dispose();
            Check((bool)registrationType.GetProperty("ResourcesClosed", Private).GetValue(empty, null), "actual empty registration could not complete resource closure");
        }
        private static void SpawnPairs()
        {
            for (int threadOutcome = 0; threadOutcome < 3; threadOutcome++) for (int processOutcome = 0; processOutcome < 3; processOutcome++) {
                IntPtr thread = new IntPtr(11), process = new IntPtr(22); bool threadUnknown = false, processUnknown = false;
                int threadCalls = 0, processCalls = 0; Exception failure = null;
                Func<IntPtr, bool> close = delegate(IntPtr value) {
                    int outcome;
                    if (value == new IntPtr(11)) { threadCalls++; outcome = threadOutcome; }
                    else { Check(value == new IntPtr(22), "spawn closer received a foreign raw handle"); processCalls++; outcome = processOutcome; }
                    if (outcome == 2) throw new IOException("fake raw close threw"); return outcome == 0;
                };
                try { HardThreadSpawnHandleClosure.Close(ref thread, ref threadUnknown, ref process, ref processUnknown, true, close); }
                catch (Exception error) { failure = error; }
                Check(threadCalls == 1 && processCalls == 1 && thread == IntPtr.Zero && process == IntPtr.Zero,
                    "spawn first close failure prevented the second original handle attempt");
                Check(threadUnknown == (threadOutcome != 0) && processUnknown == (processOutcome != 0), "spawn handle faults were shared or forgotten");
                Check((failure == null) == (threadOutcome == 0 && processOutcome == 0), "spawn close uncertainty did not reject preparation");
                int failures = (threadOutcome == 0 ? 0 : 1) + (processOutcome == 0 ? 0 : 1);
                Check(failures == 0 || failure is AggregateException && ((AggregateException)failure).InnerExceptions.Count == failures,
                    "independent spawn close failures were not preserved together");
                try { HardThreadSpawnHandleClosure.Close(ref thread, ref threadUnknown, ref process, ref processUnknown, true, close); } catch { }
                Check(threadCalls == 1 && processCalls == 1, "an uncertain spawn number was retried");
            }
            IntPtr retainedThread = new IntPtr(11), retainedProcess = new IntPtr(22); bool t = false, p = false; int calls = 0;
            HardThreadSpawnHandleClosure.Close(ref retainedThread, ref t, ref retainedProcess, ref p, false,
                delegate(IntPtr value) { calls++; Check(value == new IntPtr(11), "spawn process was closed before an original child pin existed"); return true; });
            Check(calls == 1 && retainedThread == IntPtr.Zero && retainedProcess == new IntPtr(22) && !p,
                "unduplicated original spawn process was lost before ChildGone proof");
            retainedThread = new IntPtr(11); retainedProcess = new IntPtr(22); t = false; p = false; calls = 0;
            try { HardThreadSpawnHandleClosure.Close(ref retainedThread, ref t, ref retainedProcess, ref p, false,
                delegate(IntPtr value) { calls++; Check(value == new IntPtr(11), "failed spawn thread cleanup closed the unduplicated process"); return false; }); } catch (AggregateException) { }
            Check(calls == 1 && retainedThread == IntPtr.Zero && t && retainedProcess == new IntPtr(22) && !p,
                "failed spawn thread cleanup lost the only original process proof");
        }
        private static void PinAndConstruction()
        {
            var imageFailure = new IOException("fake image close"); IntPtr handle = new IntPtr(901); bool unknown = false;
            int imageCalls = 0, rawCalls = 0; Exception combined = null;
            try { HardThreadPinClosure.Close(delegate { imageCalls++; throw imageFailure; }, ref handle, ref unknown,
                delegate { rawCalls++; return false; }); } catch (Exception error) { combined = error; }
            var aggregate = combined as AggregateException;
            Check(imageCalls == 1 && rawCalls == 1 && unknown && handle == IntPtr.Zero, "image close failure skipped the original raw handle attempt");
            Check(aggregate != null && aggregate.InnerExceptions.Count == 2 && aggregate.InnerExceptions[0].InnerException == imageFailure,
                "image and raw close failures were not preserved together");
            var cause = new IOException("ordinary verification failure"); var closing = new Resource(0, new List<int>()) { Permanent = true }; Exception construction = null;
            try { HardThreadConstructionCleanup.CloseFailed(closing, cause); } catch (Exception error) { construction = error; }
            aggregate = construction as AggregateException;
            Check(aggregate != null && aggregate.InnerExceptions.Count == 2 && aggregate.InnerExceptions[0] == cause
                && HardThreadClosureFailure.Contains(aggregate) && closing.Calls == 1,
                "constructor cleanup failure was hidden or lost its original verification cause");
            var good = new Resource(0, new List<int>()); HardThreadConstructionCleanup.CloseFailed(good, cause);
            Check(good.Calls == 1 && !HardThreadClosureFailure.Contains(cause), "ordinary verification failure was classified as closure uncertainty");
            int releases = 0; Exception acquisition = null;
            try { HardThreadConstructionCleanup.AcquireAndRelease(delegate { throw cause; }, delegate { releases++; throw construction; }); }
            catch (Exception error) { acquisition = error; }
            aggregate = acquisition as AggregateException;
            Check(releases == 1 && aggregate != null && aggregate.InnerExceptions[0] == cause && aggregate.InnerExceptions[1] == construction,
                "spawn acquisition and temporary cleanup failures were not preserved together");
            Exception ordinary = null;
            try { HardThreadConstructionCleanup.AcquireAndRelease(delegate { throw cause; }, delegate { releases++; }); }
            catch (Exception error) { ordinary = error; }
            Check(ReferenceEquals(ordinary, cause) && !HardThreadClosureFailure.Contains(ordinary) && releases == 2,
                "ordinary acquisition failure was permanently poisoned");
            Exception temporary = null;
            try { HardThreadConstructionCleanup.CloseOwned(new Resource(0, new List<int>()) { Permanent = true }); } catch (Exception error) { temporary = error; }
            Check(HardThreadClosureFailure.Contains(temporary), "temporary source cleanup uncertainty was swallowed");
        }
        private static void OwnerRemembering()
        {
            Type guardian = typeof(CriticalThreadGuardian), parentType = guardian.GetNestedType("ParentGuardian", BindingFlags.NonPublic);
            var parent = Activator.CreateInstance(parentType, Private, null, new object[] { Path.Combine(Path.GetTempPath(), "Pavise-ClosePolicy") }, null);
            parentType.GetField("cleanupComplete", Private).SetValue(parent, true); parentType.GetField("protectionReleased", Private).SetValue(parent, true);
            var remember = parentType.GetMethod("RememberClosureFailure", Private); var closed = parentType.GetProperty("ResourcesClosed", Private | BindingFlags.Public);
            Check(remember != null && closed != null, "actual parent cleanup policy members are missing");
            remember.Invoke(parent, new object[] { new IOException("Original handle closure is unconfirmed") });
            Check((bool)closed.GetValue(parent, null), "ordinary permission or verification IO permanently poisoned a parent controller");
            var marker = new AggregateException(new IOException("ordinary verification"), new HardThreadClosureFailure("typed actual closure failure"));
            remember.Invoke(parent, new object[] { marker });
            Check(!(bool)closed.GetValue(parent, null) && ReferenceEquals(parentType.GetField("unassignedCloseFailure", Private).GetValue(parent), marker),
                "unassigned original pin cleanup uncertainty was lost by its parent owner");
            parentType.GetField("spawnThreadCloseFault", Private).SetValue(parent, true); parentType.GetField("spawnProcessCloseFault", Private).SetValue(parent, true);
            Exception pair = null;
            try { parentType.GetMethod("ReleaseSpawnHandles", Private).Invoke(parent, new object[] { true }); } catch (TargetInvocationException error) { pair = error.InnerException; }
            Check(pair is AggregateException && ((AggregateException)pair).InnerExceptions.Count == 2 && !(bool)closed.GetValue(parent, null),
                "actual parent spawn pair discarded an independent latched fault");
            Type registrationType = guardian.GetNestedType("NativeRegistration", BindingFlags.NonPublic);
            var ordinary = (IDisposable)Activator.CreateInstance(registrationType, true);
            registrationType.GetMethod("RememberClosureFailure", Private).Invoke(ordinary, new object[] { new IOException("permission failure") }); ordinary.Dispose();
            Check((bool)registrationType.GetProperty("ResourcesClosed", Private).GetValue(ordinary, null), "ordinary validation IO permanently poisoned registration cleanup");
            var failed = (IDisposable)Activator.CreateInstance(registrationType, true);
            registrationType.GetMethod("RememberClosureFailure", Private).Invoke(failed, new object[] { marker });
            int code = 0; HardThreadGuardianExit.CloseForExit(failed, ref code);
            Check(code == 14 && !(bool)registrationType.GetProperty("ResourcesClosed", Private).GetValue(failed, null),
                "registration forgot an unassigned original source cleanup failure");
        }
        private static bool ManagedCall(MethodBase method, string type, string name, bool inCatch)
        {
            var body = method.GetMethodBody(); byte[] bytes = body.GetILAsByteArray(); var opcodes = new Dictionary<short, OpCode>();
            foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)) { var code = (OpCode)field.GetValue(null); opcodes[code.Value] = code; }
            for (int position = 0; position < bytes.Length;) {
                int at = position; short key = bytes[position++]; if (key == 0xfe) key = (short)(0xfe00 | bytes[position++]);
                OpCode code = opcodes[key]; int operand = position, size;
                switch (code.OperandType) {
                    case OperandType.InlineNone: size = 0; break;
                    case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: size = 1; break;
                    case OperandType.InlineVar: size = 2; break;
                    case OperandType.InlineI8: case OperandType.InlineR: size = 8; break;
                    case OperandType.InlineSwitch: size = 4 + 4 * BitConverter.ToInt32(bytes, position); break;
                    default: size = 4; break;
                }
                if (code.OperandType == OperandType.InlineMethod) {
                    var called = method.Module.ResolveMethod(BitConverter.ToInt32(bytes, operand));
                    if (called.DeclaringType.FullName == type && called.Name == name) {
                        if (!inCatch) return true;
                        foreach (ExceptionHandlingClause clause in body.ExceptionHandlingClauses)
                            if (clause.Flags == ExceptionHandlingClauseOptions.Clause && at >= clause.HandlerOffset && at < clause.HandlerOffset + clause.HandlerLength) return true;
                    }
                }
                position += size;
            }
            return false;
        }
        private static void ProductionFinally(string path)
        {
            ResolveEventHandler resolver = delegate(object sender, ResolveEventArgs args) { return Assembly.ReflectionOnlyLoad(args.Name); };
            AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += resolver;
            try {
            var assembly = Assembly.ReflectionOnlyLoadFrom(Path.GetFullPath(path));
            var method = assembly.GetType("PaviseApp.CriticalThreadGuardian", true).GetMethod("RunNative", BindingFlags.Static | BindingFlags.NonPublic);
            var body = method.GetMethodBody(); byte[] bytes = body.GetILAsByteArray();
            var opcodes = new Dictionary<short, OpCode>();
            foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)) {
                var opcode = (OpCode)field.GetValue(null); opcodes[opcode.Value] = opcode;
            }
            bool bound = false;
            for (int position = 0; position < bytes.Length;) {
                int at = position; short key = bytes[position++]; if (key == 0xfe) key = (short)(0xfe00 | bytes[position++]);
                OpCode opcode = opcodes[key]; int operand = position, size;
                switch (opcode.OperandType) {
                    case OperandType.InlineNone: size = 0; break;
                    case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: size = 1; break;
                    case OperandType.InlineVar: size = 2; break;
                    case OperandType.InlineI8: case OperandType.InlineR: size = 8; break;
                    case OperandType.InlineSwitch: size = 4 + 4 * BitConverter.ToInt32(bytes, position); break;
                    default: size = 4; break;
                }
                if (opcode.OperandType == OperandType.InlineMethod) {
                    var called = method.Module.ResolveMethod(BitConverter.ToInt32(bytes, operand));
                    if (called.DeclaringType.FullName == "PaviseApp.HardThreadGuardianExit" && called.Name == "CloseForExit")
                        foreach (ExceptionHandlingClause clause in body.ExceptionHandlingClauses)
                            if (clause.Flags == ExceptionHandlingClauseOptions.Finally && at >= clause.HandlerOffset && at < clause.HandlerOffset + clause.HandlerLength) bound = true;
                }
                position += size;
            }
            Check(bound, "actual production RunNative finally bypassed its protected close boundary");
            Type guardian = assembly.GetType("PaviseApp.CriticalThreadGuardian", true);
            foreach (string type in new[] { "RootPin", "ActorPin", "TargetPin", "DirectoryPin" }) {
                var constructors = guardian.GetNestedType(type, BindingFlags.NonPublic).GetConstructors(Private);
                Check(constructors.Length == 1 && ManagedCall(constructors[0], "PaviseApp.HardThreadConstructionCleanup", "CloseFailed", true),
                    "actual pin constructor bypassed protected construction cleanup");
            }
            Type parent = guardian.GetNestedType("ParentGuardian", BindingFlags.NonPublic);
            Check(ManagedCall(parent.GetMethod("ReleaseSpawnHandles", Private), "PaviseApp.HardThreadSpawnHandleClosure", "Close", false),
                "actual parent did not bind original spawn pair closure");
            Check(ManagedCall(guardian.GetMethod("Arm", BindingFlags.Static | BindingFlags.NonPublic), "PaviseApp.CriticalThreadGuardian+ParentGuardian", "RememberClosureFailure", true),
                "actual Arm did not retain typed preparation cleanup failure");
            Check(ManagedCall(method, "PaviseApp.CriticalThreadGuardian+NativeRegistration", "RememberClosureFailure", true),
                "actual child did not retain typed pin construction failure");
            Type files = parent.GetNestedType("CleanupFiles", BindingFlags.NonPublic);
            Check(ManagedCall(files.GetMethod("DeleteExact", Private | BindingFlags.Public), "PaviseApp.CriticalThreadGuardian+ParentGuardian", "RememberClosureFailure", true),
                "actual exact file cleanup swallowed typed closure uncertainty");
            } finally { AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve -= resolver; }
        }
        private static int Main(string[] args)
        {
            try {
                if (args.Length == 2 && args[0] == "--metadata-only") ProductionFinally(args[1]);
                else { IndependentResources(); OriginalRawHandles(); ExitBoundary(); ActualRegistration(); SpawnPairs(); PinAndConstruction(); OwnerRemembering(); ProductionFinally(args[0]); }
                Console.WriteLine("PASS guardian close checks=" + checks + " native=false raw_callbacks=fake main=false production=reflection_only"); return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
#endif
