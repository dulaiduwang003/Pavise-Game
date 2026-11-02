using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;

public static unsafe class NativeCandidates
{
    [StructLayout(LayoutKind.Sequential)] struct Mem { public uint Priority; }
    [StructLayout(LayoutKind.Sequential)] struct WsEntry { public IntPtr Address; public UIntPtr Flags; }
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetThreadInformation(IntPtr h, int cls, out Mem value, uint size);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetThreadInformation(IntPtr h, int cls, ref Mem value, uint size);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetThreadPriorityBoost(IntPtr h, out bool disabled);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetThreadPriorityBoost(IntPtr h, bool disabled);
    [DllImport("kernel32.dll", SetLastError=true)] static extern UIntPtr SetThreadAffinityMask(IntPtr h, UIntPtr mask);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetProcessWorkingSetSizeEx(IntPtr h, out UIntPtr min, out UIntPtr max, out uint flags);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetProcessWorkingSetSizeEx(IntPtr h, UIntPtr min, UIntPtr max, uint flags);
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocation, uint protection);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool VirtualFree(IntPtr address, UIntPtr size, uint freeType);
    [DllImport("psapi.dll", SetLastError=true)] static extern bool QueryWorkingSetEx(IntPtr h, [In,Out] WsEntry[] entries, uint bytes);
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint milliseconds);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint milliseconds);
    static long checksum;
    static void Check(bool value, string operation) { if (!value) throw new Exception(operation+" error="+Marshal.GetLastWin32Error()); }
    static void Work(int count) { ulong value=17; for(int i=0;i<count;i++) value=(value^(value>>9))*2862933555777941757UL+3037000493UL; Interlocked.Add(ref checksum,(long)value); }
    static double Ms(long ticks) { return 1000.0*ticks/Stopwatch.Frequency; }
    static double Percentile(List<double> values,double fraction) { values.Sort(); return values.Count==0?0:values[Math.Min(values.Count-1,(int)(fraction*(values.Count-1)))]; }
    public sealed class Result
    {
        public string mode, scenario, option, error, utc;
        public int samples, workers, residentPages, targetPages;
        public double seconds, loopsPerSecond, p50Ms, p95Ms, p99Ms, cpuSeconds;
        public long checksum;
        public bool restored;
    }
    static Result Stats(string mode,string scenario,string option,List<double> values,double seconds,double cpu)
    {
        return new Result{mode=mode,scenario=scenario,option=option,utc=DateTime.UtcNow.ToString("o"),samples=values.Count,
            seconds=seconds,loopsPerSecond=values.Count/seconds,p50Ms=Percentile(values,.5),p95Ms=Percentile(values,.95),
            p99Ms=Percentile(values,.99),cpuSeconds=cpu,checksum=checksum,restored=true};
    }
    static Result Workers(string scenario,int count,double seconds)
    {
        var gate=new ManualResetEvent(false); var done=new ManualResetEvent(false);
        bool stopping=false; int epoch=0,next=0,remaining=0; var threads=new Thread[count];
        int jobs=256, work=scenario=="small"?2500:30000;
        Exception failure=null;
        for(int i=0;i<count;i++)
        {
            threads[i]=new Thread(delegate(){int seen=0;try{while(true){gate.WaitOne();if(Volatile.Read(ref stopping))break;
                int current=Volatile.Read(ref epoch);if(current==seen){Thread.Yield();continue;}seen=current;
                while(Interlocked.Increment(ref next)<=jobs)Work(work);
                if(Interlocked.Decrement(ref remaining)==0){gate.Reset();done.Set();}
            }}catch(Exception ex){failure=ex;done.Set();}});
            threads[i].IsBackground=true;threads[i].Start();
        }
        var values=new List<double>(); var timer=Stopwatch.StartNew(); long start=0;
        double cpuStart=Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds;
        try
        {
            while(timer.Elapsed.TotalSeconds<seconds+.5)
            {
                long frame=Stopwatch.GetTimestamp();next=0;remaining=count;done.Reset();Interlocked.Increment(ref epoch);gate.Set();
                if(!done.WaitOne(2000))throw new Exception("Worker group timeout");if(failure!=null)throw failure;
                Work(20000);
                if(timer.Elapsed.TotalSeconds>.5){if(start==0)start=frame;values.Add(Ms(Stopwatch.GetTimestamp()-frame));}
            }
            var result=Stats("workers",scenario,count.ToString(),values,Ms(Stopwatch.GetTimestamp()-start)/1000,
                Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds-cpuStart);result.workers=count;return result;
        }
        finally{Volatile.Write(ref stopping,true);gate.Set();foreach(var t in threads)if(!t.Join(2500))throw new Exception("Worker cleanup timeout");gate.Dispose();done.Dispose();}
    }
    static Result Wake(string scenario,string option,double seconds)
    {
        var request=new AutoResetEvent(false);var done=new AutoResetEvent(false);var ready=new ManualResetEvent(false);
        bool stop=false, restored=false; long sent=0;double latency=0;Exception failure=null;
        bool contention=scenario!="idle";
        Thread competitor=null;
        ulong allowed=(ulong)Process.GetCurrentProcess().ProcessorAffinity.ToInt64();ulong pin=allowed&(~allowed+1);
        if(contention){competitor=new Thread(delegate(){UIntPtr old=SetThreadAffinityMask(GetCurrentThread(),new UIntPtr(pin));
            if(old==UIntPtr.Zero){failure=new Exception("Competitor pin failed");return;}
            try{while(!Volatile.Read(ref stop))Work(40000);}finally{Check(SetThreadAffinityMask(GetCurrentThread(),old)!=UIntPtr.Zero,"restore competitor pin");}});
            competitor.IsBackground=true;competitor.Start();}
        Thread frame=new Thread(delegate(){IntPtr self=GetCurrentThread();bool original=false;UIntPtr old=UIntPtr.Zero;bool captured=false;
            try{Check(GetThreadPriorityBoost(self,out original),"read dynamic boost");captured=true;
                if(contention){old=SetThreadAffinityMask(self,new UIntPtr(pin));Check(old!=UIntPtr.Zero,"pin frame");}
                if(scenario=="coordinated")Thread.CurrentThread.Priority=ThreadPriority.AboveNormal;
                if(option!="default")Check(SetThreadPriorityBoost(self,option=="disabled"),"set dynamic boost");
                bool actual;Check(GetThreadPriorityBoost(self,out actual),"readback dynamic boost");
                if(option!="default"&&actual!=(option=="disabled"))throw new Exception("Boost readback mismatch");ready.Set();
                while(!Volatile.Read(ref stop)){if(!request.WaitOne(100))continue;if(Volatile.Read(ref stop))break;
                    latency=Ms(Stopwatch.GetTimestamp()-Interlocked.Read(ref sent));Work(10000);done.Set();}
            }catch(Exception ex){failure=ex;ready.Set();done.Set();}
            finally{try{if(captured){Check(SetThreadPriorityBoost(self,original),"restore dynamic boost");bool actual;
                    Check(GetThreadPriorityBoost(self,out actual),"verify boost restore");if(actual!=original)throw new Exception("Boost restore mismatch");}
                if(old!=UIntPtr.Zero)Check(SetThreadAffinityMask(self,old)!=UIntPtr.Zero,"restore frame pin");restored=true;
            }catch(Exception ex){failure=ex;}}});frame.IsBackground=true;
        if(scenario=="coordinated"||scenario=="idle")Process.GetCurrentProcess().PriorityClass=ProcessPriorityClass.High;
        var samples=new List<double>();frame.Start();timeBeginPeriod(1);
        try{if(!ready.WaitOne(2000)||failure!=null)throw failure??new Exception("Wake initialization timeout");
            var timer=Stopwatch.StartNew();double cpuStart=Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds;
            while(timer.Elapsed.TotalSeconds<seconds+.5){Interlocked.Exchange(ref sent,Stopwatch.GetTimestamp());request.Set();
                if(!done.WaitOne(1000))throw new Exception("Wake timeout");if(failure!=null)throw failure;
                if(timer.Elapsed.TotalSeconds>.5)samples.Add(latency);Thread.Sleep(1);}
            return Stats("wake",scenario,option,samples,timer.Elapsed.TotalSeconds-.5,
                Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds-cpuStart);
        }finally{Volatile.Write(ref stop,true);request.Set();if(!frame.Join(2500))throw new Exception("Wake cleanup timeout");
            if(competitor!=null&&!competitor.Join(2500))throw new Exception("Competitor cleanup timeout");timeEndPeriod(1);
            request.Dispose();done.Dispose();ready.Dispose();if(failure!=null)throw failure;if(!restored)throw new Exception("Wake restore missing");}
    }
    static Result Memory(string scenario,uint priority,double seconds)
    {
        const int targetBytes=16*1024*1024,backgroundBytes=128*1024*1024;
        IntPtr self=GetCurrentThread(),process=GetCurrentProcess(),target=IntPtr.Zero,background=IntPtr.Zero;
        Mem original;Check(GetThreadInformation(self,0,out original,4),"read page priority");
        UIntPtr oldMin,oldMax;uint oldFlags;Check(GetProcessWorkingSetSizeEx(process,out oldMin,out oldMax,out oldFlags),"read working set limits");
        bool bounded=false;
        try
        {
            bool existing=scenario.EndsWith("-existing");
            Mem state=new Mem{Priority=existing?1u:priority};Check(SetThreadInformation(self,0,ref state,4),"set target page priority");
            Mem actual;Check(GetThreadInformation(self,0,out actual,4),"readback target page priority");if(actual.Priority!=state.Priority)throw new Exception("Page priority mismatch");
            target=VirtualAlloc(IntPtr.Zero,new UIntPtr(targetBytes),0x3000,4);Check(target!=IntPtr.Zero,"allocate target");
            byte* t=(byte*)target;for(int i=0;i<targetBytes;i+=4096)t[i]=17;
            state.Priority=4;Check(SetThreadInformation(self,0,ref state,4),"set background page priority");
            background=VirtualAlloc(IntPtr.Zero,new UIntPtr(backgroundBytes),0x3000,4);Check(background!=IntPtr.Zero,"allocate background");
            if(scenario.StartsWith("bounded")) {Check(SetProcessWorkingSetSizeEx(process,new UIntPtr(65536),new UIntPtr(32*1024*1024),0x4|0x2),"bound owned working set");bounded=true;}
            var resident=new WsEntry[targetBytes/4096];for(int i=0;i<resident.Length;i++)resident[i].Address=IntPtr.Add(target,i*4096);
            var samples=new List<double>();var timer=Stopwatch.StartNew();int hits=0,iterations=0;
            double cpuStart=Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds;
            while(timer.Elapsed.TotalSeconds<seconds+.5)
            {
                if(existing){state.Priority=4;Check(SetThreadInformation(self,0,ref state,4),"background access priority");}
                byte* b=(byte*)background;for(int i=0;i<backgroundBytes;i+=4096)b[i]=(byte)(b[i]+1);
                Check(QueryWorkingSetEx(process,resident,(uint)(resident.Length*Marshal.SizeOf(typeof(WsEntry)))),"query residency");
                int valid=0;foreach(var entry in resident)if((entry.Flags.ToUInt64()&1)!=0)valid++;
                if(existing){state.Priority=priority;Check(SetThreadInformation(self,0,ref state,4),"target access priority");}
                long start=Stopwatch.GetTimestamp();for(int i=0;i<targetBytes;i+=4096)t[i]=(byte)(t[i]+1);
                if(timer.Elapsed.TotalSeconds>.5){samples.Add(Ms(Stopwatch.GetTimestamp()-start));hits+=valid;iterations++;}
            }
            var result=Stats("memory",scenario,priority.ToString(),samples,timer.Elapsed.TotalSeconds-.5,
                Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds-cpuStart);result.targetPages=resident.Length;result.residentPages=iterations==0?0:hits/iterations;return result;
        }
        finally
        {
            if(bounded){uint restoreFlags=((oldFlags&1)!=0?1u:2u)|((oldFlags&4)!=0?4u:8u);
                Check(SetProcessWorkingSetSizeEx(process,oldMin,oldMax,restoreFlags),"restore working set limits");
                UIntPtr actualMin,actualMax;uint actualFlags;
                Check(GetProcessWorkingSetSizeEx(process,out actualMin,out actualMax,out actualFlags),"verify working set restore");
                if(actualMin!=oldMin||actualMax!=oldMax||(actualFlags&5)!=(oldFlags&5))throw new Exception("Working set restore mismatch");}
            Check(SetThreadInformation(self,0,ref original,4),"restore page priority");Mem actual;Check(GetThreadInformation(self,0,out actual,4),"verify page restore");
            if(actual.Priority!=original.Priority)throw new Exception("Page restore mismatch");
            if(target!=IntPtr.Zero)Check(VirtualFree(target,UIntPtr.Zero,0x8000),"free target");
            if(background!=IntPtr.Zero)Check(VirtualFree(background,UIntPtr.Zero,0x8000),"free background");
        }
    }
    static int Main(string[] args)
    {
        if(args.Length!=5)return 2;Result result;
        using(var watchdog=new Timer(delegate{Environment.Exit(124);},null,30000,Timeout.Infinite))
        {
            try{double seconds=double.Parse(args[3],System.Globalization.CultureInfo.InvariantCulture);
                if(seconds<1||seconds>15)throw new Exception("Duration outside test budget");
                if(args[0]=="workers")result=Workers(args[1],int.Parse(args[2]),seconds);
                else if(args[0]=="wake")result=Wake(args[1],args[2],seconds);
                else if(args[0]=="memory")result=Memory(args[1],uint.Parse(args[2]),seconds);
                else throw new Exception("Unknown experiment");
            }catch(Exception error){result=new Result{mode=args[0],scenario=args[1],option=args[2],error=error.ToString(),restored=false};}
            File.WriteAllText(args[4],new JavaScriptSerializer().Serialize(result));return result.error==null?0:1;
        }
    }
}
