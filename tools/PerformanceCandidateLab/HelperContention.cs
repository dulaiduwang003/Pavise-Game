#if PAVISE_SELFTEST && PAVISE_HELPER_CONTENTION
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace PaviseApp
{
    internal static class HelperContention
    {
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll",SetLastError=true)] private static extern UIntPtr SetThreadAffinityMask(IntPtr thread,UIntPtr mask);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessorNumber();
        [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process,uint code);
        private static long checksum;
        private static void Check(bool ok,string detail){if(!ok)throw new Exception(detail+" error="+Marshal.GetLastWin32Error());}
        private static void Work(int count){ulong value=17;for(int i=0;i<count;i++)value=(value^(value>>9))*2862933555777941757UL+1;Interlocked.Add(ref checksum,(long)value);}
        private static double Ms(long ticks){return 1000.0*ticks/Stopwatch.Frequency;}
        private static double Percentile(List<double> rows,double p){rows.Sort();return rows[Math.Min(rows.Count-1,(int)(p*(rows.Count-1)))];}
        private static string ReadLine(Process child,int timeout)
        {var task=Task.Factory.StartNew(delegate{return child.StandardOutput.ReadLine();});Check(task.Wait(timeout),"Owned child output timeout");if(task.Result==null)throw new Exception("Owned child failed: "+child.StandardError.ReadToEnd());return task.Result;}
        private static Process Start(string args)
        {return Process.Start(new ProcessStartInfo{FileName=Process.GetCurrentProcess().MainModule.FileName,Arguments=args,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=true});}
        private static Process PowerShell()
        {string script=Path.Combine(Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName),"HelperLoop.ps1");Check(File.Exists(script),"Owned PowerShell fixture exists");return Process.Start(new ProcessStartInfo{FileName="powershell.exe",Arguments="-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \""+script+"\"",UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=true});}
        private static void Send(Process child,string text){child.StandardInput.WriteLine(text);child.StandardInput.Flush();}
        private static void Kill(Process child){if(child!=null&&!child.HasExited){Check(TerminateProcess(child.Handle,124),"Kill exact owned helper handle");Check(child.WaitForExit(3000),"Owned child drained");}}
        private static int Helper(int count)
        {
            Check(count==1||count==4,"Helper worker budget");var process=Process.GetCurrentProcess();Check(Native.GetPriorityClass(process.Handle)==Native.NORMAL_PRIORITY_CLASS,"Normal helper class");
            uint[] inherited=Native.QueryCpuSets(process.Handle);Check(inherited!=null&&inherited.Length==0,"No inherited helper CPU sets");
            Console.WriteLine("ready");Console.Out.Flush();Check(Console.ReadLine()=="go","Helper start IPC");
            bool stop=false;var threads=new Thread[count];var loops=new long[count];var histograms=new int[count][];long begin=Stopwatch.GetTimestamp();double cpuBegin=process.TotalProcessorTime.TotalSeconds;
            for(int i=0;i<count;i++){int slot=i;histograms[i]=new int[64];threads[i]=new Thread(delegate(){while(!Volatile.Read(ref stop)){uint cpu=GetCurrentProcessorNumber();if(cpu<64)histograms[slot][cpu]++;Work(180000);loops[slot]++;}});threads[i].IsBackground=true;threads[i].Start();}
            try{Check(Console.ReadLine()=="stop","Helper stop IPC");}
            finally{Volatile.Write(ref stop,true);foreach(var thread in threads)Check(thread.Join(3000),"Owned helper joined");}
            long total=0;foreach(long n in loops)total+=n;double elapsed=Ms(Stopwatch.GetTimestamp()-begin)/1000;
            Console.WriteLine(new JavaScriptSerializer().Serialize(new Dictionary<string,object>{{"loopsPerSecond",total/elapsed},{"cpuSeconds",process.TotalProcessorTime.TotalSeconds-cpuBegin},{"cpuSamples",histograms},{"cpuSets",Native.QueryCpuSets(process.Handle)},{"priority",Native.GetPriorityClass(process.Handle)},{"io",Native.QueryIoPriority(process.Handle)},{"restored",true}}));return 0;
        }
        private static int Foreground(double seconds,bool allCores)
        {
            var process=Process.GetCurrentProcess();uint originalClass=Native.GetPriorityClass(process.Handle);Check(originalClass!=0,"Foreground original class");
            ulong originalAffinity;Check(Native.TryQueryAffinity(process.Handle,out originalAffinity),"Foreground original affinity");
            var cores=new List<ulong>();ulong background=CpuTopology.BackgroundSqueezeMask();
            foreach(ulong mask in CpuTopology.PhysicalCoreMasks())if(allCores||(mask&background)==0)cores.Add(mask&(~mask+1));
            Check(cores.Count>=2&&cores.Count<=16,"Fixture foreground core budget");
            using(var dispatch=new ManualResetEvent(false))using(var done=new ManualResetEvent(false))
            {
                bool stop=false;int epoch=0,remaining=0,started=0;Exception failure=null;var threads=new Thread[cores.Count];
                try
                {
                    Check(Native.SetPriorityClass(process.Handle,Native.HIGH_PRIORITY_CLASS),"Own foreground High");
                    if(!allCores)Check(Native.SetProcessAffinityMask(process.Handle,(UIntPtr)(CpuTopology.AllMask&~background)),"Owned foreground physical background exclusion");
                    for(int i=0;i<threads.Length;i++){ulong mask=cores[i];threads[i]=new Thread(delegate()
                    {
                        UIntPtr original=SetThreadAffinityMask(GetCurrentThread(),new UIntPtr(mask));var priority=Thread.CurrentThread.Priority;int seen=0;
                        try{Check(original!=UIntPtr.Zero,"Owned foreground thread pin");Thread.CurrentThread.Priority=ThreadPriority.AboveNormal;Interlocked.Increment(ref started);
                            while(true){dispatch.WaitOne();if(Volatile.Read(ref stop))break;int current=Volatile.Read(ref epoch);if(current==seen){Thread.Yield();continue;}seen=current;Work(180000);if(Interlocked.Decrement(ref remaining)==0){dispatch.Reset();done.Set();}}}
                        catch(Exception error){failure=error;done.Set();Interlocked.Increment(ref started);}
                        finally{Thread.CurrentThread.Priority=priority;if(original!=UIntPtr.Zero&&!SetThreadAffinityMask(GetCurrentThread(),original).Equals(UIntPtr.Zero)){}else failure=new Exception("Owned foreground affinity restore failed");}
                    });threads[i].IsBackground=true;threads[i].Start();}
                    var ready=Stopwatch.StartNew();while(Volatile.Read(ref started)<threads.Length&&ready.ElapsedMilliseconds<3000)Thread.Sleep(1);Check(started==threads.Length&&failure==null,"Foreground workers ready");
                    Console.WriteLine("ready");Console.Out.Flush();Check(Console.ReadLine()=="go","Foreground start IPC");
                    var samples=new List<double>();long begin=Stopwatch.GetTimestamp();double cpuBegin=process.TotalProcessorTime.TotalSeconds;
                    while(Ms(Stopwatch.GetTimestamp()-begin)<(seconds+.5)*1000){long start=Stopwatch.GetTimestamp();remaining=threads.Length;done.Reset();Interlocked.Increment(ref epoch);dispatch.Set();Check(done.WaitOne(2000),"Foreground frame joined");if(failure!=null)throw failure;if(Ms(start-begin)>=500)samples.Add(Ms(Stopwatch.GetTimestamp()-start));}
                    double elapsed=Ms(Stopwatch.GetTimestamp()-begin)/1000-.5;
                    var report=new Dictionary<string,object>{{"loopsPerSecond",samples.Count/elapsed},{"p50Ms",Percentile(samples,.5)},{"p95Ms",Percentile(samples,.95)},{"p99Ms",Percentile(samples,.99)},{"cpuSeconds",process.TotalProcessorTime.TotalSeconds-cpuBegin},{"foregroundCores",cores},{"restored",true}};
                    Volatile.Write(ref stop,true);dispatch.Set();foreach(var thread in threads)Check(thread.Join(3000),"Foreground worker cleanup");if(failure!=null)throw failure;
                    Check(Native.SetPriorityClass(process.Handle,originalClass)&&Native.GetPriorityClass(process.Handle)==originalClass,"Foreground class restore");
                    Console.WriteLine(new JavaScriptSerializer().Serialize(report));return 0;
                }
                finally{Volatile.Write(ref stop,true);dispatch.Set();foreach(var thread in threads)if(thread!=null&&thread.IsAlive)thread.Join(3000);Check(Native.SetProcessAffinityMask(process.Handle,(UIntPtr)originalAffinity),"Foreground hard affinity restore");Native.SetPriorityClass(process.Handle,originalClass);}
            }
        }
        private static int Master(string[] args)
        {
            var result=new Dictionary<string,object>{{"scenario",args[1]},{"option",args[2]},{"utc",DateTime.UtcNow.ToString("o")}};int exit=1;var helpers=new List<Process>();var helperScopes=new List<IDisposable>();Process foreground=null;
            IntPtr self=new IntPtr(-1);uint priority=Native.GetPriorityClass(self);int io=Native.QueryIoPriority(self);uint[] sets=Native.QueryCpuSets(self);
            Check(priority!=0&&io>=0&&sets!=null,"Master original state");
            try
            {
                double seconds=double.Parse(args[3],System.Globalization.CultureInfo.InvariantCulture);Check(seconds>=1&&seconds<=15,"Duration budget");Check(args[0]=="helper"&&(args[1]=="one"||args[1]=="four"||args[1]=="psone"||args[1]=="psfour")&&(args[2]=="baseline"||args[2]=="candidate"),"Arguments");
                SelfYield.Engage();Check(SelfYield.Engaged,"Production self-yield engaged");uint[] background=Native.QueryCpuSets(self);Check(background!=null&&background.Length>0,"Production parent background CPU sets");
                bool powerShell=args[1].StartsWith("ps");int processCount=args[1]=="psfour"?4:1;
                for(int i=0;i<processCount;i++)helpers.Add(powerShell?PowerShell():Start("--helper "+(args[1]=="one"?1:4)));
                foreach(var helper in helpers){Check(ReadLine(helper,10000)=="ready","Helper ready");uint[] childSets=Native.QueryCpuSets(helper.Handle);Check(childSets!=null&&childSets.Length==0&&Native.GetPriorityClass(helper.Handle)==Native.NORMAL_PRIORITY_CLASS,"Real helper Normal / no inherited CPU sets");}
                foreground=Start((args.Length==6?"--foreground-all ":"--foreground ")+args[3]);Check(ReadLine(foreground,5000)=="ready","Foreground ready");
                SelfYield.TrackHelperRenderer(foreground.Id,foreground.StartTime.ToFileTimeUtc());
                if(args[2]=="candidate")foreach(var helper in helpers){helperScopes.Add(SelfYield.OwnHelperScope(helper.Handle));Check(Native.CpuSetsMatch(helper.Handle,args.Length==6?new uint[0]:background),"Production separation gate / scope readback");}
                foreach(var helper in helpers)Send(helper,"go");Send(foreground,"go");string data=ReadLine(foreground,20000);Check(foreground.WaitForExit(3000)&&foreground.ExitCode==0,"Foreground successful");
                foreach(var helper in helpers)Send(helper,"stop");var serializer=new JavaScriptSerializer();var helperRows=new List<object>();double helperLoops=0,helperCpu=0;
                foreach(var helper in helpers){string helperData=ReadLine(helper,5000);Check(helper.WaitForExit(3000)&&helper.ExitCode==0,"Helper successful");var row=(Dictionary<string,object>)serializer.DeserializeObject(helperData);helperLoops+=Convert.ToDouble(row["loopsPerSecond"]);helperCpu+=Convert.ToDouble(row["cpuSeconds"]);helperRows.Add(row);}
                foreach(var pair in (Dictionary<string,object>)serializer.DeserializeObject(data))result[pair.Key]=pair.Value;result["helper"]=new Dictionary<string,object>{{"loopsPerSecond",helperLoops},{"cpuSeconds",helperCpu},{"processes",helperRows}};result["backgroundCpuSets"]=background;result["allForegroundCores"]=args.Length==6;exit=0;
            }
            catch(Exception error){result["error"]=error.ToString();}
            finally
            {
                foreach(var helper in helpers)Kill(helper);foreach(var scope in helperScopes)scope.Dispose();foreach(var helper in helpers)helper.Dispose();Kill(foreground);if(foreground!=null)foreground.Dispose();SelfYield.Release();
                bool clean=Native.CpuSetsMatch(self,sets)&&Native.GetPriorityClass(self)==priority&&Native.QueryIoPriority(self)==io;result["restored"]=clean;if(!clean)exit=1;
            }
            File.WriteAllText(args[4],new JavaScriptSerializer().Serialize(result));return exit;
        }
        private static int Main(string[] args)
        {
            Settings.UseTransientStoreForCurrentProcess();Logger.WritesEnabled=false;
            using(var watchdog=new Timer(delegate{Environment.Exit(124);},null,45000,Timeout.Infinite))
            {if(args.Length==2&&args[0]=="--helper")return Helper(int.Parse(args[1]));if(args.Length==2&&(args[0]=="--foreground"||args[0]=="--foreground-all"))return Foreground(double.Parse(args[1],System.Globalization.CultureInfo.InvariantCulture),args[0]=="--foreground-all");if(args.Length==5||(args.Length==6&&args[5]=="all-cores"))return Master(args);return 2;}
        }
    }
    internal static partial class SelfTests
    {public static bool TryHandleRuntimeMode(string[] args){throw new InvalidOperationException("Owned-helper contention fixture cannot launch normal runtime");}}
}
#endif
