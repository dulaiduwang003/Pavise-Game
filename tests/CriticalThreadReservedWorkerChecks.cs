#if PAVISE_SELFTEST && PAVISE_CRITICAL_THREAD_RESERVED_WORKER_CHECKS
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Threading;

namespace PaviseApp
{
    internal static class SelfTests
    { internal static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Application startup is forbidden in reserved worker fixtures"); } }

    // A1  cores another CPU set owner already holds are neither candidate cores nor worker capacity
    // and the range bound for the other threads of the game leaves them out
    // Pure rule checks first  then the real CreateCriticalThreadTarget on an uninitialized GameMode with this test process
    // standing in for the renderer  then the real HardThreadPlan the session builds from that target
    // The only native calls are query limited reads of this test process
    internal static class CriticalThreadReservedWorkerChecks
    {
        private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
        private const ulong All = 0xFFUL, Candidate = 0x01UL;
        private static readonly ulong[] Physical = { 0x03UL, 0x0CUL, 0x30UL, 0xC0UL };
        private static int checks;
        private static void Check(bool value, string reason) { checks++; if (!value) throw new InvalidOperationException("FAIL " + reason); }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        private const uint QueryLimited = 0x1000;

        private static string FewWorkers()
        { return PerformancePolicy.Text("至少为其他线程保留两个不同物理核心上的逻辑线程", "Reserve logical processors on at least two other physical cores for other threads"); }
        private static string TakenCandidate()
        { return PerformancePolicy.Text("重点线程需要不同且未被占用的物理核心", "Candidates require separate, unreserved physical cores"); }

        // Four physical cores of two logical processors  one candidate on the first core and the core rule of the game is off
        // so every processor is allowed  Each unreserved physical core with an allowed processor counts as worker capacity
        private static void Rules()
        {
            string stamp = ManualCorePlan.Stamp(All, Physical);
            var rule = new CriticalThreadRule(stamp, true, Candidate);
            Check(rule.Validate(All, Physical, All, 0, true) == null, "a machine without held cores lost its worker capacity");
            Check(rule.Validate(All, Physical, All, 0xF0UL, true) == FewWorkers(), "workers counted cores another owner holds");
            Check(rule.Validate(All, Physical, All, 0xC0UL, true) == null, "a rule with two free worker cores was refused");
            // One held logical processor takes its whole physical core out of the count and nothing more
            Check(rule.Validate(All, Physical, All, 0x20UL, true) == null, "a rule with two free worker cores was refused");
            Check(rule.Validate(All, Physical, All, 0xA0UL, true) == FewWorkers(), "workers counted cores another owner holds");
            Check(new CriticalThreadRule(stamp, true, 0x10UL).Validate(All, Physical, All, 0x30UL, true) == TakenCandidate(),
                "a candidate on a core another owner holds was not refused first");
            Check(rule.Validate(All, Physical, 0x15UL, 0, true) == null, "one logical processor per worker core was refused");
            Check(rule.Validate(All, Physical, 0x55UL, 0x80UL, true) == null, "reserving one sibling removed unrelated worker cores");
            Check(rule.Validate(All, Physical, 0x15UL, 0x20UL, true) == FewWorkers(),
                "workers counted an allowed sibling of a reserved logical processor");
            Check(new CriticalThreadRule(stamp, true, 0x10UL).Validate(All, Physical, 0x55UL, 0x20UL, true) == TakenCandidate(),
                "a candidate used the allowed sibling of a reserved logical processor");
        }

        private sealed class Self { internal IntPtr Handle; internal int Pid; internal long Birth; internal ulong Mask; internal string Name, Path; }
        private static Self Open()
        {
            var self = new Self { Pid = System.Diagnostics.Process.GetCurrentProcess().Id };
            self.Handle = OpenProcess(QueryLimited, false, self.Pid);
            if (self.Handle == IntPtr.Zero) throw new InvalidOperationException("FAIL a query-limited handle to the test process could not be opened");
            long cpu; ulong io;
            if (!Native.QueryProcessSample(self.Handle, out self.Birth, out cpu, out io) || !Native.TryQueryAffinity(self.Handle, out self.Mask))
                throw new InvalidOperationException("FAIL the test process could not be read through its query-limited handle");
            self.Name = Native.ImageName(self.Handle); self.Path = Native.ImagePath(self.Handle);
            Check((self.Mask & All) == All && !string.IsNullOrEmpty(self.Name) && !string.IsNullOrEmpty(self.Path),
                "the test process affinity does not cover the fixture processors");
            return self;
        }
        private static void Set(GameMode mode, string name, object value)
        {
            FieldInfo field = typeof(GameMode).GetField(name, Instance);
            if (field == null) throw new InvalidOperationException("FAIL missing GameMode field " + name);
            field.SetValue(mode, value);
        }

        // One CreateCriticalThreadTarget call with the placement gate held  for this test process as the selected renderer
        // of a game whose core rule is off  while another owner holds the reserved mask
        private static GameMode.CriticalThreadTargetLease Issue(Self self, CriticalThreadRule rule, ulong reserved)
        {
            AutoCores.AllocatedForTest = delegate { return reserved; };
            var profile = new GameProfile { Id = "reserved-worker-fixture", Name = "Reserved worker fixture" };
            profile.Overrides[CriticalThreadRule.Key] = rule.Encode();
            profile.Overrides[PolicyCatalog.KeyAutoCores] = "0";
            var mode = (GameMode)FormatterServices.GetUninitializedObject(typeof(GameMode));
            object gate = new object();
            Set(mode, "sync", new object()); Set(mode, "criticalProcessGate", gate); Set(mode, "allMask", All);
            Set(mode, "dataDir", Path.Combine(Path.GetTempPath(), "pavise-reserved-worker-fixture"));
            Set(mode, "profileStore", FormatterServices.GetUninitializedObject(typeof(GameProfileStore)));
            Set(mode, "enabled", true); Set(mode, "active", true); Set(mode, "gameSessionStarts", 1L); Set(mode, "criticalIntentEpoch", 1L);
            Set(mode, "profiles", new List<GameProfile> { profile });
            Set(mode, "activeDetection", new GameDetection { Profile = profile.Clone(), RendererPid = self.Pid, RendererCreation = self.Birth,
                RendererName = self.Name, RendererPath = self.Path, RendererCandidateSelected = true });
            Type passType = typeof(GameMode).GetNestedType("BoostPass", BindingFlags.NonPublic);
            MethodInfo create = typeof(GameMode).GetMethod("CreateCriticalThreadTarget", Instance);
            if (passType == null || create == null) throw new InvalidOperationException("FAIL the hard-affinity target factory changed");
            object pass = Activator.CreateInstance(passType, true);
            passType.GetField("RendererPid").SetValue(pass, self.Pid);
            passType.GetField("RendererCreation").SetValue(pass, self.Birth);
            passType.GetField("RendererName").SetValue(pass, self.Name);
            Monitor.Enter(gate);
            try { return (GameMode.CriticalThreadTargetLease)create.Invoke(mode, new object[] { pass, self.Handle, rule, profile.Id, rule.Encode(), self.Mask, 1L, 1L }); }
            finally { Monitor.Exit(gate); }
        }

        // The plan the session builds  the session gate over the lease  the machine as read and the same Create call
        private static HardThreadPlan Plan(Self self, GameMode.CriticalThreadTargetLease lease)
        {
            Type type = typeof(CriticalThreadHardAffinitySession).GetNestedType("NativeGate", BindingFlags.NonPublic);
            ConstructorInfo create = type == null ? null : type.GetConstructor(Instance, null, new[] { typeof(GameMode.CriticalThreadTargetLease) }, null);
            if (create == null) throw new InvalidOperationException("FAIL the session gate over a target lease changed");
            var gate = (IHardThreadGate)create.Invoke(new object[] { lease });
            Check(gate.Target.AllowedMask == lease.AllowedMask && gate.AllowWorkers, "the session target lost the issued range or the worker choice");
            var machine = new HardThreadMachine { Read = HardThreadRead.Known, GroupCount = 1, Group = 0, ProcessMask = self.Mask,
                PhysicalCores = (ulong[])Physical.Clone(), CpuSetsEmpty = true };
            return HardThreadPlan.Create(gate.Target, machine, new[] { new HardThreadChoice(new HardThreadIdentity(7001, 133000000000007001L), Candidate) },
                gate.AllowWorkers ? HardThreadWorkerMode.AvoidWorkers : HardThreadWorkerMode.CandidatesOnly);
        }

        private static void Mint()
        {
            Self self = Open();
            try
            {
                CpuTopology.InjectTopologyForTest(All, Physical, new ulong[0], 0, 0, 0, 0, false, false);
                CpuTopology.MultiGroup = false; CoreIsolationClient.ActiveMask = 0; AutoCores.SupportedForTest = true;
                var rule = new CriticalThreadRule(ManualCorePlan.Stamp(All, Physical), true, Candidate);
                // Nothing held  the whole allowed range is issued and bound as before
                GameMode.CriticalThreadTargetLease free = Issue(self, rule, 0);
                Check(free != null, "the fixture could not issue a target for this test process");
                Check(free.AllowedMask == All && Plan(self, free).Workers == 0xFCUL, "a machine without held cores lost part of its allowed range");
                // One core held  two free worker cores remain and neither the issued range nor the worker mask holds the held core
                GameMode.CriticalThreadTargetLease held = Issue(self, rule, 0xC0UL);
                Check(held != null, "a rule with two free worker cores was refused");
                Check(held.AllowedMask == 0x3FUL, "the bound range still holds cores another owner holds");
                HardThreadPlan plan = Plan(self, held);
                Check(plan.Workers == 0x3CUL && (plan.Workers & 0xC0UL) == 0, "worker threads were bound onto cores another owner holds");
                GameMode.CriticalThreadTargetLease halfHeld = Issue(self, rule, 0x80UL);
                Check(halfHeld != null && halfHeld.AllowedMask == 0x3FUL, "the bound range retained the sibling of a reserved logical processor");
                Check(Plan(self, halfHeld).Workers == 0x3CUL, "the worker plan retained the sibling of a reserved logical processor");
                // The A1 case  two cores held leave one free worker core and no target is issued
                Check(Issue(self, rule, 0xF0UL) == null, "a target was issued with fewer than two free worker cores");
                Check(Issue(self, new CriticalThreadRule(ManualCorePlan.Stamp(All, Physical), true, 0x10UL), 0x30UL) == null,
                    "a target was issued for a candidate on a core another owner holds");
            }
            finally { CloseHandle(self.Handle); }
        }

        private static int Main(string[] args)
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            if (args.Length == 1 && args[0] == "--negative-control") { Console.WriteLine("FAIL: intentional reserved worker negative control"); return 3; }
            bool logs = Logger.WritesEnabled;
            Func<ulong> allocated = AutoCores.AllocatedForTest;
            bool? supported = AutoCores.SupportedForTest;
            try
            {
                Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false;
                Lang.Cur = 0; Rules(); Mint();
                Lang.Cur = 1; Rules();
                Console.WriteLine("PASS critical thread reserved worker assertions=" + checks);
                return 0;
            }
            catch (Exception error)
            {
                Exception inner = error is TargetInvocationException && error.InnerException != null ? error.InnerException : error;
                Console.WriteLine(inner.Message.StartsWith("FAIL ", StringComparison.Ordinal) ? inner.Message : "FAIL unexpected " + inner);
                return 1;
            }
            finally { AutoCores.AllocatedForTest = allocated; AutoCores.SupportedForTest = supported; Logger.WritesEnabled = logs; Lang.Cur = 0; }
        }
    }
}
#endif
