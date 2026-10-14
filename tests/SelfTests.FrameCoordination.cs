#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private sealed class CoordinationFake : IFrameThreads
        {
            internal Dictionary<int,int> Priorities = new Dictionary<int,int> { {101,0},{102,-1} };
            internal Dictionary<int,long> Births = new Dictionary<int,long> { {101,1001},{102,1002} };
            internal int Writes, DeniedWrite;
            internal bool Unavailable, Gone;
            public int Read(FrameThreadReceipt id,out int value)
            {
                value=0;
                if(Unavailable) return -1;
                if(Gone || !Births.ContainsKey(id.Tid) || Births[id.Tid]!=id.ThreadBirth || id.Pid!=505 || id.ProcessBirth!=999) return 1;
                value=Priorities[id.Tid];return 0;
            }
            public bool Set(FrameThreadReceipt id,int expected,int desired)
            {
                int current; if(Read(id,out current)!=0 || current!=expected) return false;
                if(++Writes==DeniedWrite) return false;
                string raw; if(!Settings.TryLoadStr(FrameCoordination.JournalKey,out raw) || raw.Length==0) throw new InvalidOperationException("No durable thread receipt");
                Priorities[id.Tid]=desired;return true;
            }
        }
        private static FrameCoordinationSample CoordinationSample()
        { return new FrameCoordinationSample { Pid=505,Birth=999,PresentTid=101,WorkerTid=102,Valid=true,Threads=new Dictionary<int,long>{{101,1001},{102,1002}} }; }
        private sealed class MemoryCoordinationFake : IFrameThreads, IFrameThreadMemory
        {
            internal readonly CoordinationFake Threads = new CoordinationFake();
            internal readonly Dictionary<int,int> Memory = new Dictionary<int,int>{{101,1},{102,4}};
            internal bool Pressure = true, DenyRead;
            internal int MemoryWrites, DeniedMemoryWrite;
            internal Action<int> OnMemoryWrite;
            public int Read(FrameThreadReceipt id,out int value) { return Threads.Read(id,out value); }
            public bool Set(FrameThreadReceipt id,int expected,int desired) { return Threads.Set(id,expected,desired); }
            public bool UnderPressure(int pid,long birth) { return Pressure && pid==505 && birth==999; }
            public int ReadMemory(FrameThreadReceipt id,out int value)
            { int ignored;value=0;int state=Threads.Read(id,out ignored);if(state!=0)return state;if(DenyRead)return -1;value=Memory[id.Tid];return 0; }
            public bool SetMemory(FrameThreadReceipt id,int expected,int desired)
            {
                int value;if(ReadMemory(id,out value)!=0||value!=expected)return false;
                if(++MemoryWrites==DeniedMemoryWrite)return false;
                string raw;if(!Settings.TryLoadStr(FrameMemoryProtection.JournalKey,out raw)||raw.Length==0)throw new Exception("Missing memory restore journal");
                Memory[id.Tid]=desired;if(OnMemoryWrite!=null)OnMemoryWrite(desired);return true;
            }
        }
        private static void CheckFrameMemoryProtection()
        {
            Settings.UseTransientStoreForCurrentProcess();var fake=new MemoryCoordinationFake();FrameCoordination.SetPlatformForTest(fake);
            fake.Pressure=false;Eq(true,FrameCoordination.Apply(CoordinationSample(),delegate{return true;}));
            Eq(0,fake.MemoryWrites);Eq(1,fake.Memory[101]);Eq(true,FrameCoordination.Restore());
            fake.Pressure=true;fake.Memory[101]=5;fake.Memory[102]=5;
            Eq(true,FrameCoordination.Apply(CoordinationSample(),delegate{return true;}));Eq(0,fake.MemoryWrites);Eq(true,FrameCoordination.Restore());
            fake.Memory[101]=1;fake.Memory[102]=4;
            Eq(true,FrameCoordination.Apply(CoordinationSample(),delegate{return true;}));Eq(5,fake.Memory[101]);Eq(5,fake.Memory[102]);
            Eq(true,FrameMemoryProtection.HasResidue);Eq(true,FrameMemoryProtection.Healthy());
            Eq(true,FrameCoordination.Restore());Eq(1,fake.Memory[101]);Eq(4,fake.Memory[102]);Eq(false,FrameMemoryProtection.HasResidue);
            Eq(0,fake.Threads.Priorities[101]);Eq(-1,fake.Threads.Priorities[102]);
            fake.MemoryWrites=0;fake.DeniedMemoryWrite=2;
            Eq(false,FrameCoordination.Apply(CoordinationSample(),delegate{return true;}));Eq(1,fake.Memory[101]);Eq(4,fake.Memory[102]);
            Eq(true,!string.IsNullOrEmpty(FrameMemoryProtection.ExecutionFailure));Eq(false,FrameMemoryProtection.HasResidue);
            Eq(0,fake.Threads.Priorities[101]);Eq(-1,fake.Threads.Priorities[102]);
            Eq(true,FrameCoordination.Restore());Eq(0,fake.Threads.Priorities[101]);Eq(-1,fake.Threads.Priorities[102]);fake.DeniedMemoryWrite=0;
            fake.MemoryWrites=0;fake.DeniedMemoryWrite=2;
            const string failedMemoryTarget="frame-memory-fault.exe";
            Eq(false,FrameCoordination.ApplyIsolated(CoordinationSample(),delegate{return true;},failedMemoryTarget));
            Eq(true,ActionFailureStore.IsIsolated(FrameCoordination.ActionKey,failedMemoryTarget));
            Eq(1,fake.Memory[101]);Eq(4,fake.Memory[102]);Eq(0,fake.Threads.Priorities[101]);Eq(-1,fake.Threads.Priorities[102]);
            int failedMemoryWrites=fake.MemoryWrites;fake.DeniedMemoryWrite=0;
            Eq(false,FrameCoordination.ApplyIsolated(CoordinationSample(),delegate{return true;},failedMemoryTarget));
            Eq(failedMemoryWrites,fake.MemoryWrites);
            ActionFailureStore.ReloadForTest();Eq(true,ActionFailureStore.IsIsolated(FrameCoordination.ActionKey,failedMemoryTarget));
            foreach(var failedMemoryRecord in ActionFailureStore.Records())
                if(failedMemoryRecord.Action==FrameCoordination.ActionKey)Eq(true,ActionFailureStore.RequestRetry(failedMemoryRecord.Key));
            Eq(true,FrameCoordination.ApplyIsolated(CoordinationSample(),delegate{return true;},failedMemoryTarget));
            Eq(false,ActionFailureStore.IsIsolated(FrameCoordination.ActionKey,failedMemoryTarget));Eq(true,FrameCoordination.Restore());
            Eq(true,FrameCoordination.Apply(CoordinationSample(),delegate{return true;}));fake.DenyRead=true;
            Eq(false,FrameCoordination.Restore());Eq(true,FrameMemoryProtection.HasResidue);
            Eq(0,fake.Threads.Priorities[101]);Eq(-1,fake.Threads.Priorities[102]);fake.DenyRead=false;
            Eq(true,FrameCoordination.Restore());Eq(1,fake.Memory[101]);Eq(4,fake.Memory[102]);
            Eq(true,FrameCoordination.Apply(CoordinationSample(),delegate{return true;}));
            fake.DeniedMemoryWrite=fake.MemoryWrites+1;
            Eq(false,FrameCoordination.Restore());Eq(true,FrameMemoryProtection.HasResidue);
            Eq(5,fake.Memory[101]);Eq(4,fake.Memory[102]);Eq(0,fake.Threads.Priorities[101]);Eq(-1,fake.Threads.Priorities[102]);
            fake.DeniedMemoryWrite=0;Eq(true,FrameCoordination.Restore());Eq(1,fake.Memory[101]);Eq(false,FrameMemoryProtection.HasResidue);
            bool live=true;fake.OnMemoryWrite=delegate(int desired){if(desired==5)live=false;};
            Eq(false,FrameCoordination.Apply(CoordinationSample(),delegate{return live;}));fake.OnMemoryWrite=null;
            Eq(1,fake.Memory[101]);Eq(4,fake.Memory[102]);Eq(false,FrameMemoryProtection.HasResidue);
            Eq(0,fake.Threads.Priorities[101]);Eq(-1,fake.Threads.Priorities[102]);
            Eq(true,FrameCoordination.Apply(CoordinationSample(),delegate{return true;}));fake.Memory[101]=3;
            Eq(false,FrameMemoryProtection.Healthy());Eq(true,FrameCoordination.Restore());Eq(3,fake.Memory[101]);Eq(4,fake.Memory[102]);
            fake.Memory[101]=1;Eq(true,FrameCoordination.Apply(CoordinationSample(),delegate{return true;}));
            fake.Threads.Births[101]=7000;fake.Memory[101]=2;
            Eq(true,FrameCoordination.Restore());Eq(2,fake.Memory[101]);Eq(4,fake.Memory[102]);fake.Threads.Births[101]=1001;
            Settings.UseTransientStoreForCurrentProcess();Settings.SaveStr(FrameMemoryProtection.JournalKey,"corrupt");
            Eq(false,FrameCoordination.Restore());Eq("corrupt",Settings.LoadStr(FrameMemoryProtection.JournalKey,""));
            foreach(string invalid in new[]{"v1;1|2|3|4|5","v1;1|2|3|4|1;1|2|3|4|2","v1;1|2|3|4|1;2|2|5|6|2","v1;0|2|3|4|1"})
            {Settings.UseTransientStoreForCurrentProcess();Settings.SaveStr(FrameMemoryProtection.JournalKey,invalid);Eq(false,FrameCoordination.Restore());Eq(invalid,Settings.LoadStr(FrameMemoryProtection.JournalKey,""));}
            Settings.UseTransientStoreForCurrentProcess();FrameCoordination.SetPlatformForTest(null);
        }
        private static FrameCoordinationSampler.Evidence CaptureEvidence()
        {
            return new FrameCoordinationSampler.Evidence { Sample = CoordinationSample(), Begin = 100, Half = 200,
                End = 300, CpuMsForTest = delegate { return 0; } };
        }
        private static Native.EventRecord CaptureEvent(Guid provider, uint pid, uint tid, long time,
            byte opcode, ushort id, IntPtr data, ushort bytes)
        {
            return new Native.EventRecord { EventHeader = new Native.EventHeader { ProviderId = provider,
                ProcessId = pid, ThreadId = tid, TimeStamp = time,
                EventDescriptor = new Native.EventDescriptor { Opcode = opcode, Id = id } },
                UserData = data, UserDataLength = bytes };
        }
        private static void CheckFrameTraceModes()
        {
            var create = typeof(FrameCoordinationTrace).GetMethod("Props", BindingFlags.Static | BindingFlags.NonPublic,
                null, new[] { typeof(string), typeof(bool), typeof(bool) }, null);
            Eq(true, create != null);
            foreach (bool presenterOnly in new[] { false, true })
            {
                IntPtr props = (IntPtr)create.Invoke(null, new object[] { "Pavise.FrameCoordination.fixture", true, presenterOnly });
                try
                {
                    var value = (Native.EventTraceProperties)Marshal.PtrToStructure(props, typeof(Native.EventTraceProperties));
                    Eq(Native.EventTraceRealTimeMode | (presenterOnly ? 0u : 0x02000000u), value.LogFileMode);
                    Eq(presenterOnly ? 0u : 0x0812u, value.EnableFlags);
                    Eq(64u, value.BufferSize); Eq(8u, value.MinimumBuffers); Eq(32u, value.MaximumBuffers);
                }
                finally { Marshal.FreeHGlobal(props); }
            }
        }
        private static void CheckFrameEventBudgets()
        {
            IntPtr data = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.Copy(new byte[24], 0, data, 24);
                var evidence = CaptureEvidence();
                Marshal.WriteInt32(data, 777);
                var foreign = CaptureEvent(FrameCoordinationSampler.KernelThread, 777, 777, 150, 36, 0, data, 24);
                var outside = CaptureEvent(FrameCoordinationSampler.D3d9, 505, 101, 99, 0, 1, data, 4);
                for (int i = 0; i <= FrameCoordinationSampler.MaxRelevantEvents; ++i)
                {
                    if ((i & 1) == 0) evidence.Receive(ref foreign);
                    else evidence.Receive(ref outside);
                }
                Eq(false, evidence.Invalid); Eq(FrameCaptureIssue.None, evidence.Fault);
                Eq(750001, evidence.Count); Eq(0, evidence.RelevantCount);
                var wrongPid = CaptureEvent(FrameCoordinationSampler.D3d9, 777, 101, 150, 0, 1, data, 4);
                evidence.Receive(ref wrongPid); Eq(0, evidence.RelevantCount);
                Marshal.WriteInt32(data, 0);
                foreach (long time in new long[] { 150, 250 })
                {
                    var start = CaptureEvent(FrameCoordinationSampler.D3d9, 505, 101, time, 0, 1, data, 4);
                    var end = CaptureEvent(FrameCoordinationSampler.D3d9, 505, 101, time, 0, 2, data, 4);
                    for (int i = 0; i < 18; ++i) { evidence.Receive(ref start); evidence.Receive(ref end); }
                }
                Eq(false, evidence.Invalid); Eq(72, evidence.RelevantCount);
                Eq(101, FrameCoordinationSampler.Dominant(evidence.Presents[0], 16, .7));
                Eq(101, FrameCoordinationSampler.Dominant(evidence.Presents[1], 16, .7));

                var switchFlood = CaptureEvidence();
                var gameSwitch = CaptureEvent(FrameCoordinationSampler.KernelThread, 505, 102, 150, 36, 0, data, 24);
                Marshal.WriteInt32(data, 102);
                for (int i = 0; i <= FrameCoordinationSampler.MaxRelevantEvents; ++i) switchFlood.Receive(ref gameSwitch);
                Eq(false, switchFlood.Invalid); Eq(0, switchFlood.RelevantCount); Eq(750001, switchFlood.Count);
                Eq(750001, switchFlood.SchedulerSwitches);
                Marshal.WriteInt32(data, 0);
                foreach (long time in new long[] { 150, 250 })
                {
                    var start = CaptureEvent(FrameCoordinationSampler.D3d9, 505, 101, time, 0, 1, data, 4);
                    var end = CaptureEvent(FrameCoordinationSampler.D3d9, 505, 101, time, 0, 2, data, 4);
                    for (int i = 0; i < 18; ++i) { switchFlood.Receive(ref start); switchFlood.Receive(ref end); }
                }
                Eq(false, switchFlood.Invalid); Eq(72, switchFlood.RelevantCount);
                Eq(101, FrameCoordinationSampler.Dominant(switchFlood.Presents[0], 16, .7));
                Eq(101, FrameCoordinationSampler.Dominant(switchFlood.Presents[1], 16, .7));

                var systemBounded = CaptureEvidence();
                for (int i = 0; i < FrameCoordinationSampler.MaxSystemEvents; ++i) systemBounded.Receive(ref outside);
                Eq(false, systemBounded.Invalid); Eq(FrameCoordinationSampler.MaxSystemEvents, systemBounded.Count);
                systemBounded.Receive(ref outside);
                Eq(true, systemBounded.Invalid); Eq(FrameCaptureIssue.SystemEventBudget, systemBounded.Fault);
                Eq(FrameCoordinationSampler.MaxSystemEvents + 1, systemBounded.Count); Eq(0, systemBounded.RelevantCount);
                systemBounded.Receive(ref gameSwitch);
                Eq(FrameCoordinationSampler.MaxSystemEvents + 1, systemBounded.Count);

                var presenterOnly = CaptureEvidence(); presenterOnly.PresenterOnly = true;
                presenterOnly.Sample.PresenterOnly = true;
                var presenterSwitch = CaptureEvent(FrameCoordinationSampler.KernelThread, 505, 102, 150, 36, 0, data, 24);
                var presenterReady = CaptureEvent(FrameCoordinationSampler.KernelThread, 505, 101, 151, 50, 0, data, 8);
                for (int i = 0; i < 100; ++i)
                {
                    Marshal.WriteInt32(data, 102); presenterOnly.Receive(ref presenterSwitch);
                    Marshal.WriteInt32(data, 101); presenterOnly.Receive(ref presenterReady);
                }
                Eq(0, presenterOnly.RelevantCount); Eq(0, presenterOnly.Edges[0].Count); Eq(0, presenterOnly.Edges[1].Count);
                Eq(0, presenterOnly.SchedulerSwitches);
                Marshal.WriteInt32(data, 0);
                foreach (long time in new long[] { 150, 250 })
                {
                    var start = CaptureEvent(FrameCoordinationSampler.D3d9, 505, 101, time, 0, 1, data, 4);
                    var end = CaptureEvent(FrameCoordinationSampler.D3d9, 505, 101, time, 0, 2, data, 4);
                    for (int i = 0; i < 18; ++i) { presenterOnly.Receive(ref start); presenterOnly.Receive(ref end); }
                }
                Eq(false, presenterOnly.Invalid); Eq(72, presenterOnly.RelevantCount);
                Eq(101, FrameCoordinationSampler.Dominant(presenterOnly.Presents[0], 16, .7));
                Eq(101, FrameCoordinationSampler.Dominant(presenterOnly.Presents[1], 16, .7));
                long workerEdge = ((long)101 << 32) | 102;
                presenterOnly.Edges[0][workerEdge] = 20; presenterOnly.Edges[1][workerEdge] = 20;
                Eq(0, FrameCoordinationSampler.SelectWorker(presenterOnly, 101));
                presenterOnly.PresenterOnly = false;
                Eq(102, FrameCoordinationSampler.SelectWorker(presenterOnly, 101));

                var bounded = CaptureEvidence();
                var gameStart = CaptureEvent(FrameCoordinationSampler.D3d9, 505, 101, 150, 0, 1, data, 4);
                for (int i = 0; i < FrameCoordinationSampler.MaxRelevantEvents; ++i) bounded.Receive(ref gameStart);
                Eq(false, bounded.Invalid);
                bounded.Receive(ref gameStart);
                Eq(true, bounded.Invalid); Eq(FrameCaptureIssue.EventBudget, bounded.Fault);
                Eq(750001, bounded.RelevantCount);

                double cpuMs = 0;
                var cpuBounded = CaptureEvidence(); cpuBounded.CpuMsForTest = delegate { return cpuMs; };
                cpuBounded.Receive(ref foreign);
                cpuMs = 501;
                for (int i = 1; i < FrameCoordinationSampler.CpuCheckInterval; ++i) cpuBounded.Receive(ref foreign);
                Eq(true, cpuBounded.Invalid); Eq(FrameCaptureIssue.CpuBudget, cpuBounded.Fault);
                Eq(FrameCoordinationSampler.CpuCheckInterval, cpuBounded.Count); Eq(0, cpuBounded.RelevantCount);
                var presenterCpuBounded = CaptureEvidence(); presenterCpuBounded.PresenterOnly = true;
                presenterCpuBounded.CpuMsForTest = delegate { return 500.1; };
                presenterCpuBounded.Receive(ref foreign);
                Eq(true, presenterCpuBounded.Invalid); Eq(FrameCaptureIssue.CpuBudget, presenterCpuBounded.Fault);
                Eq(0, presenterCpuBounded.SchedulerSwitches); Eq(0, presenterCpuBounded.RelevantCount);

                var edges = CaptureEvidence();
                var switched = CaptureEvent(FrameCoordinationSampler.KernelThread, 0, 0, 150, 36, 0, data, 24);
                var ready = CaptureEvent(FrameCoordinationSampler.KernelThread, 0, 0, 151, 50, 0, data, 8);
                long edge = ((long)101 << 32) | 102;
                Marshal.WriteInt32(data, 102); edges.Receive(ref switched);
                Marshal.WriteInt32(data, 101); edges.Receive(ref ready);
                Eq(1, edges.Edges[0][edge]); Eq(1, edges.RelevantCount);
                switched.EventHeader.TimeStamp = 152;
                Marshal.WriteInt32(data, 777); edges.Receive(ref switched);
                ready.EventHeader.TimeStamp = 153;
                Marshal.WriteInt32(data, 101); edges.Receive(ref ready);
                Eq(1, edges.Edges[0][edge]); Eq(1, edges.RelevantCount);
                switched.EventHeader.TimeStamp = 154;
                Marshal.WriteInt32(data, 102); edges.Receive(ref switched);
                ready.EventHeader.TimeStamp = 155; ready.BufferContext = 1;
                Marshal.WriteInt32(data, 101); edges.Receive(ref ready);
                Eq(1, edges.Edges[0][edge]);
                ready.BufferContext = 0; Marshal.WriteByte(data, 6, 1); edges.Receive(ref ready);
                Eq(1, edges.Edges[0][edge]);
                Marshal.WriteByte(data, 6, 0);
                switched.EventHeader.TimeStamp = 149;
                Marshal.WriteInt32(data, 102); edges.Receive(ref switched);
                ready.EventHeader.TimeStamp = 156;
                Marshal.WriteInt32(data, 101); edges.Receive(ref ready);
                Eq(1, edges.Edges[0][edge]);
                switched.EventHeader.TimeStamp = 157;
                Marshal.WriteInt32(data, 102); edges.Receive(ref switched);
                switched.EventHeader.TimeStamp = 158; switched.UserDataLength = 4;
                Marshal.WriteInt32(data, 777); edges.Receive(ref switched);
                ready.EventHeader.TimeStamp = 159;
                Marshal.WriteInt32(data, 101); edges.Receive(ref ready);
                Eq(1, edges.Edges[0][edge]);

                var edgeBounded = CaptureEvidence();
                for (int i = 0; i <= 2048; ++i)
                {
                    int sender = 1000 + i; edgeBounded.Sample.Threads[sender] = 5000 + i;
                    var next = CaptureEvent(FrameCoordinationSampler.KernelThread, 505, (uint)sender, 150, 36, 0, data, 24);
                    var wake = CaptureEvent(FrameCoordinationSampler.KernelThread, 505, 101, 150, 50, 0, data, 8);
                    Marshal.WriteInt32(data, sender); edgeBounded.Receive(ref next);
                    Marshal.WriteInt32(data, 101); edgeBounded.Receive(ref wake);
                    if (i == 2047) { Eq(false, edgeBounded.Invalid); Eq(2048, edgeBounded.Edges[0].Count); }
                }
                Eq(true, edgeBounded.Invalid); Eq(FrameCaptureIssue.EdgeBudget, edgeBounded.Fault);
                Eq(2048, edgeBounded.Edges[0].Count);
            }
            finally { Marshal.FreeHGlobal(data); }
        }
        private static void CheckFrameRetry()
        {
            var retry = new FrameCoordinationRetry();
            var sample = new FrameCoordinationSample().Reject(FrameCaptureIssue.EventBudget);
            retry.Observe(sample, 0); Eq(30000, retry.DelayMs); Eq(true, retry.ShouldLog);
            retry.Observe(sample, 30000); Eq(60000, retry.DelayMs); Eq(false, retry.ShouldLog);
            retry.Observe(sample, 90000); Eq(120000, retry.DelayMs); Eq(false, retry.ShouldLog);
            retry.Observe(sample, 210000); Eq(240000, retry.DelayMs); Eq(false, retry.ShouldLog);
            retry.Observe(sample, 450000); Eq(300000, retry.DelayMs); Eq(true, retry.ShouldLog);
            retry.Observe(sample, 750000); Eq(300000, retry.DelayMs); Eq(true, retry.ShouldLog);
            sample.Reject(FrameCaptureIssue.EventsLost);
            retry.Observe(sample, 750001); Eq(true, retry.ShouldLog); Eq(300000, retry.DelayMs);
            sample.TraceCode = 5;
            retry.Observe(sample, 750002); Eq(true, retry.ShouldLog);
            retry.Reset(); Eq(0, retry.DelayMs); Eq(false, retry.ShouldLog);
            retry.Observe(sample, 750003); Eq(30000, retry.DelayMs); Eq(true, retry.ShouldLog);
            retry.Observe(null, 750004); Eq(60000, retry.DelayMs); Eq(true, retry.ShouldLog);
            sample.Reject(FrameCaptureIssue.InsufficientEvidence); sample.TraceCode = 0;
            for (int i = 0; i < 10; ++i) { retry.Observe(sample, 800000 + i * 30000); Eq(30000, retry.DelayMs); }
            sample.Reject(FrameCaptureIssue.EventBudget);
            retry.Observe(sample, 1200000); Eq(30000, retry.DelayMs);
            retry.Observe(sample, 1230000); Eq(60000, retry.DelayMs);
            sample.Valid = true; sample.Issue = FrameCaptureIssue.None;
            retry.Observe(sample, 1260000); Eq(30000, retry.DelayMs);
        }
        private static void CheckFramePumpCpuHandle()
        {
            var evidence = CaptureEvidence(); evidence.CpuMsForTest = null;
            var pump = new Thread(delegate()
            {
                var record = CaptureEvent(Guid.Empty, 0, 0, 150, 0, 0, IntPtr.Zero, 0);
                evidence.Receive(ref record);
            });
            pump.IsBackground = true; pump.Start();
            Eq(true, pump.Join(10000)); Eq(1, evidence.Count);
            Eq(false, evidence.Invalid); Eq(FrameCaptureIssue.None, evidence.Fault);
        }
        private static void CheckFrameCleanupFailures()
        {
            FrameCoordinationTrace trace = null;
            int status = 5, closeStatus = 0, controls = 0, closes = 0;
            FrameCoordinationTrace.ControlForTest = delegate(ulong handle, string name, IntPtr props, uint command)
            { ++controls; return status; };
            FrameCoordinationTrace.CloseForTest = delegate { ++closes; return closeStatus; };
            try
            {
                Settings.UseTransientStoreForCurrentProcess();
                trace = FrameCoordinationTrace.CleanupFixtureForTest();
                string receipt = Settings.LoadStr(FrameCoordinationTrace.TraceKey, "");
                Eq(false, trace.Stop()); Eq(receipt, Settings.LoadStr(FrameCoordinationTrace.TraceKey, ""));
                Eq(false, trace.Stop()); Eq(receipt, Settings.LoadStr(FrameCoordinationTrace.TraceKey, ""));
                Eq(2, controls); Eq(1, closes);
                status = 0; trace.Stop();
                Eq(3, controls); Eq("", Settings.LoadStr(FrameCoordinationTrace.TraceKey, ""));

                controls = closes = 0; closeStatus = 5;
                trace = FrameCoordinationTrace.CleanupFixtureForTest();
                receipt = Settings.LoadStr(FrameCoordinationTrace.TraceKey, "");
                Eq(false, trace.Stop()); Eq(receipt, Settings.LoadStr(FrameCoordinationTrace.TraceKey, ""));
                Eq(false, trace.Stop()); Eq(1, controls); Eq(2, closes);
                closeStatus = 0; trace.Stop(); Eq(3, closes);
                Eq("", Settings.LoadStr(FrameCoordinationTrace.TraceKey, ""));

                closeStatus = 7007;
                trace = FrameCoordinationTrace.CleanupFixtureForTest();
                Eq(true, trace.Stop()); Eq("", Settings.LoadStr(FrameCoordinationTrace.TraceKey, ""));
                closeStatus = 0;
                status = 234;
                trace = FrameCoordinationTrace.CleanupFixtureForTest();
                Eq(false, trace.Stop()); Eq("", Settings.LoadStr(FrameCoordinationTrace.TraceKey, ""));
                status = 0;

                trace = FrameCoordinationTrace.CleanupFixtureForTest();
                receipt = Settings.LoadStr(FrameCoordinationTrace.TraceKey, "");
                Settings.SuspendWritesForReset();
                Eq(false, trace.Stop()); Eq(receipt, Settings.LoadStr(FrameCoordinationTrace.TraceKey, ""));
                Eq(false, trace.Stop()); Eq(receipt, Settings.LoadStr(FrameCoordinationTrace.TraceKey, ""));
                Settings.UseTransientStoreForCurrentProcess();
                Eq(true, FrameCoordinationTrace.Heal());
                Eq(false, trace.Stop());

                trace = FrameCoordinationTrace.CleanupFixtureForTest();
                Settings.SaveStr(FrameCoordinationTrace.TraceKey, "external-receipt");
                Eq(false, trace.Stop()); Eq("external-receipt", Settings.LoadStr(FrameCoordinationTrace.TraceKey, ""));
                Eq(false, FrameCoordinationTrace.Heal());
                Settings.UseTransientStoreForCurrentProcess();

                string stale = "Pavise.FrameCoordination.505." + Guid.NewGuid().ToString("N");
                Settings.SaveStr(FrameCoordinationTrace.TraceKey, stale); status = 5;
                Eq(false, FrameCoordinationTrace.Heal()); Eq(stale, Settings.LoadStr(FrameCoordinationTrace.TraceKey, ""));
                status = 4201; Eq(true, FrameCoordinationTrace.Heal());
                Eq("", Settings.LoadStr(FrameCoordinationTrace.TraceKey, ""));

                status = 0;
                trace = FrameCoordinationTrace.CleanupFixtureForTest();
                bool rejectReadback = false;
                Settings.BeforeStrictStringReadForTest = delegate(string key)
                {
                    if (key == FrameCoordinationTrace.TraceKey && Settings.LoadStr(key, "").Length == 0 && !rejectReadback)
                    { rejectReadback = true; throw new InvalidOperationException("Receipt readback denied"); }
                };
                Eq(false, trace.Stop()); Eq(true, rejectReadback);
                Settings.BeforeStrictStringReadForTest = null;
                Eq(true, FrameCoordinationTrace.Heal());
            }
            finally
            {
                status = closeStatus = 0; Settings.BeforeStrictStringReadForTest = null;
                if (trace != null) trace.Stop();
                FrameCoordinationTrace.ControlForTest = null; FrameCoordinationTrace.CloseForTest = null;
                Settings.UseTransientStoreForCurrentProcess();
            }
        }
        private static void CheckFrameCleanupDrain()
        {
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            {
                var pump = new Thread(delegate() { entered.Set(); release.WaitOne(); });
                FrameCoordinationTrace trace = null;
                pump.IsBackground = true; pump.Start();
                FrameCoordinationTrace.ControlForTest = delegate { return 0; };
                FrameCoordinationTrace.CloseForTest = delegate { return 7007; };
                FrameCoordinationTrace.StopWaitMsForTest = 1;
                try
                {
                    Eq(true, entered.WaitOne(10000));
                    trace = FrameCoordinationTrace.CleanupFixtureForTest(pump);
                    string receipt = Settings.LoadStr(FrameCoordinationTrace.TraceKey, "");
                    Eq(false, trace.Stop()); Eq(receipt, Settings.LoadStr(FrameCoordinationTrace.TraceKey, ""));
                    Eq(false, FrameCoordinationTrace.Heal());
                    var other = new FrameCoordinationTrace();
                    Eq(false, other.Start(505, delegate(ref Native.EventRecord r) { }));
                    Eq(170, other.Error); other.Stop();
                    Eq(receipt, Settings.LoadStr(FrameCoordinationTrace.TraceKey, ""));
                    release.Set(); Eq(true, pump.Join(10000));
                    Eq(true, FrameCoordinationTrace.Heal());
                    Eq("", Settings.LoadStr(FrameCoordinationTrace.TraceKey, ""));
                }
                finally
                {
                    release.Set(); pump.Join(10000);
                    if (trace != null) trace.Stop();
                    FrameCoordinationTrace.ControlForTest = null; FrameCoordinationTrace.CloseForTest = null;
                    FrameCoordinationTrace.StopWaitMsForTest = 3000;
                    Settings.UseTransientStoreForCurrentProcess();
                }
            }
        }
        private static void CheckFrameCleanupConcurrent()
        {
            int calls = 0;
            FrameCoordinationTrace trace = null;
            FrameCoordinationTrace.ControlForTest = delegate { Interlocked.Increment(ref calls); Thread.Sleep(2); return 0; };
            FrameCoordinationTrace.CloseForTest = delegate { return 0; };
            try
            {
                Settings.UseTransientStoreForCurrentProcess();
                trace = FrameCoordinationTrace.CleanupFixtureForTest();
                var cleanup = new Thread[8];
                var clean = new bool[8];
                for (int i = 0; i < cleanup.Length; ++i)
                {
                    int index = i;
                    cleanup[i] = new Thread(delegate() { clean[index] = index % 2 == 0 ? trace.Stop() : FrameCoordinationTrace.Heal(); });
                    cleanup[i].IsBackground = true; cleanup[i].Start();
                }
                for (int i = 0; i < cleanup.Length; ++i) { Eq(true, cleanup[i].Join(10000)); Eq(true, clean[i]); }
                Eq(1, calls); Eq("", Settings.LoadStr(FrameCoordinationTrace.TraceKey, ""));
            }
            finally
            {
                if (trace != null) trace.Stop();
                FrameCoordinationTrace.ControlForTest = null; FrameCoordinationTrace.CloseForTest = null;
                Settings.UseTransientStoreForCurrentProcess();
            }
        }
        private static void CheckFrameRecoveryDebt(CoordinationFake fake)
        {
            Settings.UseTransientStoreForCurrentProcess();
            Eq(false, FrameCoordination.HasResidue);
            Eq(true, FrameCoordination.Apply(CoordinationSample(), delegate { return true; }));
            Eq(true, FrameCoordination.HasResidue);
            int controls = 0;
            FrameCoordinationTrace.ControlForTest = delegate { ++controls; return 4201; };
            try
            {
                Settings.SaveStr(FrameCoordinationTrace.TraceKey, "Pavise.FrameCoordination.505." + Guid.NewGuid().ToString("N"));
                fake.Unavailable = true;
                Eq(false, FrameCoordination.ReleaseExcept(0, 0));
                Eq(1, controls);
                Eq(true, FrameCoordination.HasResidue); Eq(false, FrameCoordinationTrace.HasResidue);
                fake.Unavailable = false;
                Eq(true, FrameCoordination.ReleaseExcept(0, 0));
                Eq(false, FrameCoordination.HasResidue); Eq(0, fake.Priorities[101]); Eq(-1, fake.Priorities[102]);
                Settings.BeforeStrictStringReadForTest = delegate(string key)
                { if (key == FrameCoordination.JournalKey) throw new InvalidOperationException("Unreadable restore receipt"); };
                Eq(true, FrameCoordination.HasResidue); Eq(false, FrameCoordination.ReleaseExcept(0, 0));
                Settings.BeforeStrictStringReadForTest = null;
                Eq(false, FrameCoordination.HasResidue);
            }
            finally
            {
                fake.Unavailable = false; Settings.BeforeStrictStringReadForTest = null;
                FrameCoordinationTrace.ControlForTest = null; Settings.UseTransientStoreForCurrentProcess();
                fake.Writes = 0;
            }
        }
        private static void CheckFrameCaptureReports()
        {
            Eq(FrameCaptureIssue.None, FrameCoordinationSampler.CaptureIssue(true, 0, true, true, 0, 0, FrameCaptureIssue.None, 500));
            Eq(FrameCaptureIssue.CpuBudget, FrameCoordinationSampler.CaptureIssue(true, 0, true, true, 0, 0, FrameCaptureIssue.None, 500.1));
            Eq(FrameCaptureIssue.Canceled, FrameCoordinationSampler.CaptureIssue(false, 0, true, true, 0, 0, FrameCaptureIssue.None, 0));
            Eq(FrameCaptureIssue.TargetChanged, FrameCoordinationSampler.CaptureIssue(true, 1, true, true, 0, 0, FrameCaptureIssue.None, 0));
            Eq(FrameCaptureIssue.TargetUnavailable, FrameCoordinationSampler.CaptureIssue(true, -1, true, true, 0, 0, FrameCaptureIssue.None, 0));
            Eq(FrameCaptureIssue.TraceStopFailed, FrameCoordinationSampler.CaptureIssue(true, 0, false, false, 0, 0, FrameCaptureIssue.None, 0));
            Eq(FrameCaptureIssue.TraceStopFailed, FrameCoordinationSampler.CaptureIssue(false, 0, false, true, 5, 0, FrameCaptureIssue.None, 0));
            Eq(FrameCaptureIssue.EventsLost, FrameCoordinationSampler.CaptureIssue(true, 0, false, true, 0, 1, FrameCaptureIssue.None, 0));
            foreach (FrameCaptureIssue fault in new[] { FrameCaptureIssue.EventBudget, FrameCaptureIssue.SystemEventBudget,
                FrameCaptureIssue.EdgeBudget, FrameCaptureIssue.DecodeFailed, FrameCaptureIssue.CpuBudget })
                Eq(fault, FrameCoordinationSampler.CaptureIssue(true, 0, true, true, 0, 0, fault, 0));
            foreach (FrameCaptureIssue fault in new[] { FrameCaptureIssue.EventBudget, FrameCaptureIssue.SystemEventBudget,
                FrameCaptureIssue.EdgeBudget, FrameCaptureIssue.CpuBudget })
                Eq(fault, FrameCoordinationSampler.CaptureIssue(false, 0, true, true, 0, 0, fault, 0));

            int language = Lang.Cur;
            try
            {
                foreach (int current in new[] { 0, 1 })
                {
                    Lang.Cur = current;
                    foreach (FrameCaptureIssue issue in new[] { FrameCaptureIssue.InsufficientEvidence, FrameCaptureIssue.Canceled,
                        FrameCaptureIssue.TargetChanged, FrameCaptureIssue.TargetUnavailable, FrameCaptureIssue.ThreadBudget,
                        FrameCaptureIssue.TraceUnavailable, FrameCaptureIssue.TraceStopFailed, FrameCaptureIssue.EventsLost,
                        FrameCaptureIssue.EventBudget, FrameCaptureIssue.SystemEventBudget, FrameCaptureIssue.EdgeBudget, FrameCaptureIssue.DecodeFailed,
                        FrameCaptureIssue.CpuBudget, FrameCaptureIssue.CaptureFailed })
                    {
                        var sample = new FrameCoordinationSample { TraceCode = 0, LostEvents = 0, Events = 750001, CpuMs = 501 };
                        sample.Reject(issue);
                        string line = FrameCoordination.WaitingLine("error-game", sample);
                        LogEventSeverity expected = issue == FrameCaptureIssue.Canceled || issue == FrameCaptureIssue.TargetChanged
                            || issue == FrameCaptureIssue.InsufficientEvidence ? LogEventSeverity.Info
                            : issue == FrameCaptureIssue.TraceStopFailed || issue == FrameCaptureIssue.DecodeFailed
                            || issue == FrameCaptureIssue.CaptureFailed ? LogEventSeverity.Error : LogEventSeverity.Warning;
                        LogEventEntry parsed = LogStreamView.Parse(line);
                        Eq(expected, parsed.Severity);
                        Eq("FRAME", parsed.Module);
                        Eq(true, parsed.Message.Contains(sample.Detail));
                        Eq(true, parsed.Message.Contains("eventCount=750001 sampleCpuMs=501.0"));
                    }
                    const string legacy = "FRAME 线程协同待命：Discovery capture canceled, lost or over budget; error=0 lost=0";
                    string raw = "2026-10-03 22:21:03  " + legacy;
                    Eq(LogEventSeverity.Warning, LogStreamView.Parse(raw).Severity);
                    Eq(raw, LogStreamView.Parse(raw).Raw);
                    Eq(LogEventSeverity.Error, LogStreamView.Parse(Logger.FailTag + legacy).Severity);
                    Eq(LogEventSeverity.Error, LogStreamView.Parse(legacy.Replace("error=0", "error=5")).Severity);
                    Eq(LogEventSeverity.Error, LogStreamView.Parse(legacy + " 设置还原失败").Severity);
                }
            }
            finally { Lang.Cur = language; }
        }
        private static void CheckFrameCoordinationModes(CoordinationFake fake)
        {
            Settings.UseTransientStoreForCurrentProcess();
            Eq(false, PolicyResolver.Global().FrameCoordinationOn);
            Settings.Save(AutomaticPolicy.VersionKey, true);
            var profile = new GameProfile { Id = "coordination-mode", Name = "Coordination mode fixture" };
            profile.Overrides[PolicyCatalog.KeyPreset] = "4";
            profile.Overrides[FrameCoordination.ActionKey] = "0";
            profile.Overrides["AutoFrameCoordinationV1"] = "0";
            foreach (bool legacy in new[] { false, true })
            {
                Settings.Save("AutoFrameCoordinationV1", legacy);
                foreach (string preset in new[] { "0", "1", "4", "6", "2", "5", "99", "invalid" })
                {
                    Settings.SaveStr(PolicyCatalog.KeyPreset, preset);
                    bool expected = preset != "4";
                    Eq(expected, PolicyResolver.Global().FrameCoordinationOn);
                    Eq(expected, PolicyResolver.For(profile).FrameCoordinationOn);
                }
            }
            Eq(false, AutomaticPolicy.UsesFrameCoordination((PerformancePreset)99));
            Settings.SaveStr(PolicyCatalog.KeyPreset, "1");
            Eq(true, FrameCoordination.Apply(CoordinationSample(), delegate { return PolicyResolver.Global().FrameCoordinationOn; }));
            Settings.SaveStr(PolicyCatalog.KeyPreset, "4");
            Eq(true, FrameCoordination.ReleaseExcept(0, 0));
            Eq(0, fake.Priorities[101]); Eq(-1, fake.Priorities[102]);
            fake.Writes = 0;
            Eq(false, FrameCoordination.Apply(CoordinationSample(), delegate { return PolicyResolver.Global().FrameCoordinationOn; }));
            Eq(0, fake.Writes);
            Settings.UseTransientStoreForCurrentProcess();
        }
        private static void PrepareFrameWorker(CoordinationFake fake)
        {
            FrameCoordination.ResetWorkerForTest(); Settings.UseTransientStoreForCurrentProcess();
            fake.Unavailable = fake.Gone = false; fake.DeniedWrite = fake.Writes = 0;
            fake.Priorities[101] = 0; fake.Priorities[102] = -1;
            FrameCoordination.SetPlatformForTest(fake);
            FrameCoordination.ProcessStateForTest = delegate { return 0; };
        }
        private static void DrainFrameWorker()
        { Eq(true, FrameCoordination.DrainWorkerForTest(5000)); }
        private static void CheckFrameWorkerLifecycle(CoordinationFake fake)
        {
            bool admitted = true;
            Func<bool> admission = delegate { return admitted; };
            try
            {
                PrepareFrameWorker(fake);
                int captures = 0, waits = 0, identityReads = 0; long now = 0;
                FrameCoordination.ProcessStateForTest = delegate { identityReads++; return 0; };
                var modes = new List<bool>();
                FrameCoordination.ElapsedMsForTest = delegate { return now; };
                FrameCoordination.CaptureForTest = delegate(int target, long created, Func<bool> alive, bool presenterOnly)
                {
                    Eq(true, alive()); captures++; modes.Add(presenterOnly);
                    return new FrameCoordinationSample { Pid = target, Birth = created, PresenterOnly = presenterOnly }
                        .Reject(FrameCaptureIssue.SystemEventBudget);
                };
                FrameCoordination.WaitForTest = delegate(int ms, Func<bool> alive) { waits++; now += ms; return alive(); };
                FrameCoordination.Ensure(505, 999, "mock noisy renderer", admission); DrainFrameWorker();
                Eq(3, captures); Eq(1, waits); Eq(3, identityReads); Eq(false, modes[0]); Eq(true, modes[1]); Eq(true, modes[2]);
                var session = FrameCoordination.CaptureSessionForTest(505, 999);
                Eq(3, session.Failures); Eq(true, session.Stopped); Eq(true, session.PresenterOnly); Eq(4, FrameCoordination.State);
                Eq("v1;505|999|1|3|1|0", Settings.LoadStr(FrameCoordination.CaptureSessionKey, ""));
                Eq(0, fake.Writes); Eq(false, FrameCoordination.HasResidue);
                for (int i = 0; i < 30; ++i)
                {
                    admitted = false; Eq(true, FrameCoordination.ReleaseExcept(0, 0));
                    FrameCoordination.Ensure(505, 999, "disabled mock", admission);
                    admitted = true; now += 300000;
                    FrameCoordination.Ensure(505, 999, "same renderer after focus", admission); DrainFrameWorker();
                }
                Eq(3, captures); Eq(3, identityReads);
                FrameCoordination.ResetCaptureSessionsForTest();
                FrameCoordination.Ensure(505, 999, "persisted renderer", admission); DrainFrameWorker(); Eq(3, captures);
                Eq(true, FrameCoordination.CaptureSessionForTest(505, 999).Stopped);
                foreach (int replacementState in new[] { -1, 1 })
                {
                    FrameCoordination.ProcessStateForTest = delegate(int target, long created) { return created == 1000 ? replacementState : 0; };
                    FrameCoordination.Ensure(505, 1000, "unverified or stale replacement", admission); DrainFrameWorker(); Eq(3, captures);
                    Eq(999L, FrameCoordination.CaptureSessionForTest(505, 999).Birth);
                    Eq("v1;505|999|1|3|1|0", Settings.LoadStr(FrameCoordination.CaptureSessionKey, ""));
                }
                FrameCoordination.ProcessStateForTest = delegate { identityReads++; return 0; };
                FrameCoordination.Ensure(506, 999, "different process", admission); DrainFrameWorker(); Eq(6, captures);
                Eq(false, modes[3]); Eq(true, modes[4]); Eq(true, modes[5]);
                FrameCoordination.Ensure(505, 1000, "reused PID new creation", admission); DrainFrameWorker(); Eq(9, captures);
                Eq(false, modes[6]); Eq(true, modes[7]); Eq(true, modes[8]);
                Eq(true, FrameCoordination.Close(5000)); FrameCoordination.ResetWorkerForTest();
                FrameCoordination.ProcessStateForTest = delegate { return 0; };
                FrameCoordination.CaptureForTest = delegate { throw new InvalidOperationException("Persisted stopped process captured after restart"); };
                FrameCoordination.Ensure(505, 1000, "app restart same renderer", admission); DrainFrameWorker();
                Eq(true, FrameCoordination.CaptureSessionForTest(505, 1000).Stopped);

                PrepareFrameWorker(fake); captures = 0; modes.Clear();
                FrameCoordination.CaptureForTest = delegate(int target, long created, Func<bool> alive, bool presenterOnly)
                { captures++; modes.Add(presenterOnly); return new FrameCoordinationSample().Reject(FrameCaptureIssue.EventBudget); };
                FrameCoordination.WaitForTest = delegate { admitted = false; return false; };
                FrameCoordination.Ensure(505, 999, "focus interruption before terminal budget", admission); DrainFrameWorker();
                session = FrameCoordination.CaptureSessionForTest(505, 999);
                Eq(2, captures); Eq(2, session.Failures); Eq(true, session.PresenterOnly); Eq(false, session.Stopped);
                Eq(true, FrameCoordination.ReleaseExcept(0, 0)); FrameCoordination.ResetCaptureSessionsForTest(); admitted = true;
                FrameCoordination.WaitForTest = delegate { throw new InvalidOperationException("Reloaded final attempt entered another retry"); };
                FrameCoordination.Ensure(505, 999, "same identity after focus and reload", admission); DrainFrameWorker();
                session = FrameCoordination.CaptureSessionForTest(505, 999);
                Eq(3, captures); Eq(false, modes[0]); Eq(true, modes[1]); Eq(true, modes[2]);
                Eq(3, session.Failures); Eq(true, session.Stopped); Eq(0, fake.Writes);

                foreach (bool accessDenied in new[] { false, true })
                {
                    PrepareFrameWorker(fake); captures = waits = 0; fake.Unavailable = accessDenied;
                    FrameCoordination.CaptureForTest = delegate
                    { captures++; return accessDenied ? CoordinationSample() : new FrameCoordinationSample().Reject(FrameCaptureIssue.InsufficientEvidence); };
                    FrameCoordination.WaitForTest = delegate(int ms, Func<bool> alive) { waits++; return alive(); };
                    FrameCoordination.Ensure(505, 999, "mock weak or blocked renderer", admission); DrainFrameWorker();
                    session = FrameCoordination.CaptureSessionForTest(505, 999);
                    Eq(accessDenied ? 1 : 3, captures); Eq(accessDenied ? 0 : 2, waits);
                    Eq(accessDenied ? 1 : 3, session.Failures); Eq(true, session.Stopped);
                    Eq(accessDenied,ActionFailureStore.IsIsolated(FrameCoordination.ActionKey,"mock weak or blocked renderer"));
                    Eq(false, session.PresenterOnly); Eq(false, session.NoChangeNeeded); Eq(0, fake.Writes);
                    fake.Unavailable = false; Eq(true, FrameCoordination.Restore());
                }

                PrepareFrameWorker(fake); captures = 0;
                FrameCoordination.CaptureForTest = delegate
                { captures++; throw new InvalidOperationException("Injected capture failure"); };
                for (int attempt = 1; attempt <= 3; ++attempt)
                {
                    FrameCoordination.Ensure(505, 999, "throwing renderer", admission); DrainFrameWorker();
                    session = FrameCoordination.CaptureSessionForTest(505, 999);
                    Eq(1, captures); Eq(1, session.Failures); Eq(true, session.Stopped);
                    Eq(true,ActionFailureStore.IsIsolated(FrameCoordination.ActionKey,"throwing renderer"));
                    FrameCoordination.ResetCaptureSessionsForTest();
                }
                FrameCoordination.Ensure(505, 999, "throwing renderer after terminal failure", admission); DrainFrameWorker();
                Eq(1, captures); Eq(4, FrameCoordination.State); Eq(0, fake.Writes); Eq(false, FrameCoordination.HasResidue);

                foreach(FrameCaptureIssue fault in new[]{FrameCaptureIssue.CaptureFailed,FrameCaptureIssue.DecodeFailed,FrameCaptureIssue.TraceStopFailed})
                {
                    PrepareFrameWorker(fake);captures=waits=0;
                    FrameCoordination.CaptureForTest=delegate{captures++;return new FrameCoordinationSample().Reject(fault);};
                    FrameCoordination.WaitForTest=delegate{waits++;return true;};
                    FrameCoordination.Ensure(505,999,"capture execution fault",admission);DrainFrameWorker();
                    Eq(1,captures);Eq(0,waits);Eq(true,ActionFailureStore.IsIsolated(FrameCoordination.ActionKey,"capture execution fault"));
                    FrameCoordination.Ensure(505,999,"capture execution fault",admission);DrainFrameWorker();Eq(1,captures);
                }

                foreach (FrameCaptureIssue issue in new[] { FrameCaptureIssue.Canceled, FrameCaptureIssue.TargetChanged })
                {
                    PrepareFrameWorker(fake); captures = 0;
                    FrameCoordination.CaptureForTest = delegate { captures++; return new FrameCoordinationSample().Reject(issue); };
                    FrameCoordination.WaitForTest = delegate { throw new InvalidOperationException("Canceled capture waited for retry"); };
                    FrameCoordination.Ensure(505, 999, "canceled renderer", admission); DrainFrameWorker();
                    session = FrameCoordination.CaptureSessionForTest(505, 999);
                    Eq(1, captures); Eq(0, session.Failures); Eq(false, session.Stopped); Eq(0, fake.Writes);
                }
                PrepareFrameWorker(fake); captures = 0;
                FrameCoordination.CaptureForTest = delegate
                { captures++; admitted = false; return new FrameCoordinationSample().Reject(FrameCaptureIssue.EventBudget); };
                FrameCoordination.Ensure(505, 999, "focus lost during capture", admission); DrainFrameWorker();
                Eq(1, captures); Eq(1, FrameCoordination.CaptureSessionForTest(505, 999).Failures);
                Eq(true, FrameCoordination.CaptureSessionForTest(505, 999).PresenterOnly); admitted = true;
                PrepareFrameWorker(fake); captures = 0;
                FrameCoordination.ProcessStateForTest = delegate { return 1; };
                FrameCoordination.CaptureForTest = delegate { captures++; return CoordinationSample(); };
                FrameCoordination.Ensure(505, 999, "exited renderer", admission); DrainFrameWorker();
                Eq(0, captures); Eq(0, FrameCoordination.CaptureSessionForTest(505, 999).Failures);

                PrepareFrameWorker(fake); captures = 0; fake.Priorities[101] = 2; fake.Priorities[102] = 1;
                FrameCoordination.CaptureForTest = delegate { captures++; return CoordinationSample(); };
                FrameCoordination.WaitForTest = delegate { throw new InvalidOperationException("Game-managed priorities triggered retry"); };
                FrameCoordination.Ensure(505, 999, "game-managed renderer", admission); DrainFrameWorker();
                session = FrameCoordination.CaptureSessionForTest(505, 999);
                Eq(1, captures); Eq(0, fake.Writes); Eq(true, session.NoChangeNeeded); Eq(true, session.Stopped);
                Eq(0, session.Failures); Eq(0, FrameCoordination.State);
                Eq(true, FrameCoordination.ReleaseExcept(0, 0)); FrameCoordination.ResetCaptureSessionsForTest();
                FrameCoordination.Ensure(505, 999, "game-managed reload", admission); DrainFrameWorker(); Eq(1, captures);
                Eq(0, FrameCoordination.State);

                PrepareFrameWorker(fake); captures = 0; modes.Clear();
                FrameCoordination.CaptureForTest = delegate(int target, long created, Func<bool> alive, bool presenterOnly)
                {
                    captures++; modes.Add(presenterOnly);
                    if (!presenterOnly) return new FrameCoordinationSample().Reject(FrameCaptureIssue.CpuBudget);
                    var sample = CoordinationSample(); sample.PresenterOnly = true; sample.WorkerTid = 0; return sample;
                };
                FrameCoordination.WaitForTest = delegate(int ms, Func<bool> alive)
                { Eq(1000, ms); admitted = false; return false; };
                FrameCoordination.Ensure(505, 999, "presenter-only success", admission); DrainFrameWorker(); admitted = true;
                session = FrameCoordination.CaptureSessionForTest(505, 999);
                Eq(2, captures); Eq(false, modes[0]); Eq(true, modes[1]); Eq(0, session.Failures);
                Eq(true, session.PresenterOnly); Eq(false, session.Stopped); Eq(2, fake.Writes);
                Eq(0, fake.Priorities[101]); Eq(-1, fake.Priorities[102]); Eq(false, FrameCoordination.HasResidue);
                FrameCoordination.ResetCaptureSessionsForTest(); Eq(true, FrameCoordination.CaptureSessionForTest(505, 999).PresenterOnly);

                CheckFrameStoppedCleanup(fake, admission);
                CheckFrameWorkerClose(fake, admission);
                Console.WriteLine("PASS frame worker: first execution fault isolates, evidence lifetime budget, sticky presenter-only fallback, persistent reload, focus/feature cycling, PID/creation replacement, cancellation, no-change standby and cleanup/close");
            }
            finally
            {
                admitted = false; fake.Unavailable = false;
                FrameCoordination.Close(5000); FrameCoordination.ResetWorkerForTest();
                FrameCoordinationTrace.ControlForTest = null; FrameCoordinationTrace.CloseForTest = null;
                Settings.UseTransientStoreForCurrentProcess(); FrameCoordination.SetPlatformForTest(fake);
                fake.Priorities[101] = 0; fake.Priorities[102] = -1; fake.Writes = 0;
            }
        }
        private static void CheckFrameStoppedCleanup(CoordinationFake fake, Func<bool> admission)
        {
            PrepareFrameWorker(fake);
            Settings.SaveStr(FrameCoordination.CaptureSessionKey, "v1;505|999|1|3|1|0");
            FrameCoordination.ResetCaptureSessionsForTest();
            int captures = 0, controls = 0;
            bool traceClearedBeforeWait = false;
            FrameCoordination.CaptureForTest = delegate { captures++; throw new InvalidOperationException("Stopped target sampled during recovery"); };
            FrameCoordination.WaitForTest = delegate {
                traceClearedBeforeWait = controls == 1 && Settings.LoadStr(FrameCoordinationTrace.TraceKey, "").Length == 0;
                return false;
            };
            FrameCoordinationTrace.ControlForTest = delegate { controls++; return 4201; };
            FrameCoordinationTrace.CloseForTest = delegate { throw new InvalidOperationException("Unexpected consumer handle in recovery fixture"); };
            try
            {
                Eq(true, FrameCoordination.Apply(CoordinationSample(), admission));
                Settings.SaveStr(FrameCoordinationTrace.TraceKey, "Pavise.FrameCoordination.505." + Guid.NewGuid().ToString("N"));
                fake.Unavailable = true;
                FrameCoordination.Ensure(505, 999, "stopped renderer with recovery debt", admission); DrainFrameWorker();
                Eq(0, captures); Eq(1, controls); Eq(true, Settings.LoadStr(FrameCoordination.JournalKey, "").Length > 0);
                Eq(true, traceClearedBeforeWait);
                Eq("", Settings.LoadStr(FrameCoordinationTrace.TraceKey, ""));
                fake.Unavailable = false;
                FrameCoordination.Ensure(505, 999, "retry restoration only", admission); DrainFrameWorker();
                Eq(0, captures); Eq(0, fake.Priorities[101]); Eq(-1, fake.Priorities[102]); Eq(false, FrameCoordination.HasResidue);
                Eq(true, FrameCoordination.CaptureSessionForTest(505, 999).Stopped);
                Settings.SaveStr(FrameCoordinationTrace.TraceKey, "Pavise.FrameCoordination.506." + Guid.NewGuid().ToString("N"));
                FrameCoordination.Ensure(505, 999, "new global trace debt after earlier clean stop", admission); DrainFrameWorker();
                Eq(0, captures); Eq(2, controls); Eq(false, FrameCoordination.HasResidue);
            }
            finally { fake.Unavailable = false; FrameCoordinationTrace.ControlForTest = null; FrameCoordinationTrace.CloseForTest = null; }
        }
        private static void CheckFrameWorkerClose(CoordinationFake fake, Func<bool> admission)
        {
            PrepareFrameWorker(fake);
            using (var leased = new ManualResetEvent(false))
            {
                int captures = 0;
                FrameCoordination.CaptureForTest = delegate { captures++; return CoordinationSample(); };
                FrameCoordination.WaitForTest = delegate(int ms, Func<bool> alive)
                {
                    Eq(1000, ms); leased.Set();
                    while (alive()) Thread.Sleep(1);
                    return false;
                };
                try
                {
                    FrameCoordination.Ensure(505, 999, "owned mock lease", admission);
                    Eq(true, leased.WaitOne(5000)); Eq(1, captures);
                    Eq(1, fake.Priorities[101]); Eq(1, fake.Priorities[102]);
                    Eq(true, Settings.LoadStr(FrameCoordination.JournalKey, "").Length > 0);
                    Eq(true, FrameCoordination.Close(5000)); DrainFrameWorker();
                    Eq(0, fake.Priorities[101]); Eq(-1, fake.Priorities[102]); Eq(false, FrameCoordination.HasResidue);
                    FrameCoordination.Ensure(505, 999, "closed worker", admission); DrainFrameWorker(); Eq(1, captures);
                }
                finally { FrameCoordination.Close(5000); }
            }
        }
        internal static void RunFrameCoordinationTests()
        {
            var fake=new CoordinationFake();
            try
            {
                Settings.UseTransientStoreForCurrentProcess();
                FrameCoordination.SetPlatformForTest(fake);
                CheckFrameCleanupFailures();
                CheckFrameCleanupDrain();
                CheckFrameCleanupConcurrent();
                CheckFrameTraceModes();
                CheckFrameEventBudgets();
                CheckFrameRetry();
                CheckFramePumpCpuHandle();
                CheckFrameRecoveryDebt(fake);
                CheckFrameCaptureReports();
                CheckFrameCoordinationModes(fake);
                CheckFrameWorkerLifecycle(fake);
                Eq(true,FrameCoordination.Apply(CoordinationSample(),delegate{return true;}));
                Eq(1,fake.Priorities[101]);Eq(1,fake.Priorities[102]);
                Eq(true,FrameCoordination.Restore());Eq(0,fake.Priorities[101]);Eq(-1,fake.Priorities[102]);
                Eq("",Settings.LoadStr(FrameCoordination.JournalKey,""));

                fake.Writes=0;fake.DeniedWrite=2;
                Eq(false,FrameCoordination.Apply(CoordinationSample(),delegate{return true;}));
                Eq(0,fake.Priorities[101]);Eq(-1,fake.Priorities[102]);Eq("",Settings.LoadStr(FrameCoordination.JournalKey,""));
                fake.DeniedWrite=0;fake.Writes=0;
                Eq(false,FrameCoordination.Apply(CoordinationSample(),delegate{return fake.Writes==0;}));
                Eq(0,fake.Priorities[101]);Eq(-1,fake.Priorities[102]);

                Eq(true,FrameCoordination.Apply(CoordinationSample(),delegate{return true;}));
                fake.Priorities[101]=2;
                Eq(true,FrameCoordination.Restore());Eq(2,fake.Priorities[101]);Eq(-1,fake.Priorities[102]);
                fake.Priorities[101]=0;
                Eq(true,FrameCoordination.Apply(CoordinationSample(),delegate{return true;}));
                fake.Births[101]=5000;fake.Priorities[101]=0;
                Eq(true,FrameCoordination.Restore());Eq(0,fake.Priorities[101]);Eq(-1,fake.Priorities[102]);
                fake.Births[101]=1001;
                Eq(true,FrameCoordination.Apply(CoordinationSample(),delegate{return true;}));
                fake.Unavailable=true;Eq(false,FrameCoordination.Restore());Eq(true,Settings.LoadStr(FrameCoordination.JournalKey,"").Length>0);
                fake.Unavailable=false;Eq(true,FrameCoordination.Restore());

                fake.Priorities[101]=2;fake.Priorities[102]=1;
                var alreadyManaged = CoordinationSample();
                Eq(false,FrameCoordination.Apply(alreadyManaged,delegate{return true;}));Eq(2,fake.Priorities[101]);Eq(1,fake.Priorities[102]);
                Eq(true, alreadyManaged.NoChangeNeeded);
                Eq(LogEventSeverity.Info, LogStreamView.Parse(FrameCoordination.WaitingLine("already-managed", alreadyManaged)).Severity);
                fake.Unavailable = true;
                Eq(false, FrameCoordination.Apply(alreadyManaged, delegate { return true; }));
                Eq(false, alreadyManaged.NoChangeNeeded);
                Eq(LogEventSeverity.Warning, LogStreamView.Parse(FrameCoordination.WaitingLine("blocked", alreadyManaged)).Severity);
                fake.Unavailable = false;
                fake.Priorities[101]=0;fake.Priorities[102]=-1;
                var bad=CoordinationSample();bad.Threads.Remove(101);
                Eq(false,FrameCoordination.Apply(bad,delegate{return true;}));
                Settings.SuspendWritesForReset();Eq(false,FrameCoordination.Apply(CoordinationSample(),delegate{return true;}));
                Eq(0,fake.Priorities[101]);Eq(-1,fake.Priorities[102]);
                Settings.UseTransientStoreForCurrentProcess();Settings.SaveStr(FrameCoordination.JournalKey,"corrupt");
                Eq(false,FrameCoordination.Restore());Eq("corrupt",Settings.LoadStr(FrameCoordination.JournalKey,""));
                Settings.UseTransientStoreForCurrentProcess();
                List<FrameThreadReceipt> parsed;
                foreach(string invalid in new[]{"v1;1|2|3|4|2","v1;1|2|3|4|0;1|2|3|4|0","v1;1|2|3|4|0;2|2|5|6|0","v1;0|2|3|4|0","v1;1|2|3|0|0"})
                    Eq(false,FrameCoordination.Parse(invalid,out parsed));
                Eq(0,FrameCoordinationSampler.Dominant(new Dictionary<int,int>{{101,20},{102,20}},12,.75));
                Eq(101,FrameCoordinationSampler.Dominant(new Dictionary<int,int>{{101,30},{102,2}},12,.75));
                CheckFrameMemoryProtection();
                Console.WriteLine("PASS frame coordination: separate scheduler/evidence/system/CPU budgets, presenter-only trace and worker selection, foreign CPU state, bounded retry policy, automatic mode policy, durable paired writes, cancellation, reused IDs and evidence confidence");
            }
            finally { Settings.UseTransientStoreForCurrentProcess();FrameCoordination.SetPlatformForTest(null); }
        }
    }
}
#endif
