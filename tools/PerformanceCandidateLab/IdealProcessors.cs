using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;

internal static class IdealProcessorCandidate
{
    [StructLayout(LayoutKind.Sequential)] private struct Processor { public ushort Group; public byte Number,Reserved; }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessorNumber();
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetThreadIdealProcessorEx(IntPtr h,out Processor value);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool SetThreadIdealProcessorEx(IntPtr h,ref Processor value,out Processor previous);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetLogicalProcessorInformationEx(int relation,IntPtr buffer,ref uint size);
    private static long checksum;
    private static void Check(bool ok,string operation) { if(!ok)throw new Exception(operation+" error="+Marshal.GetLastWin32Error()); }
    private static List<int[]> Cores()
    {
        uint size=0;GetLogicalProcessorInformationEx(0,IntPtr.Zero,ref size);if(size==0||size>1024*1024)throw new Exception("Topology size unavailable");
        IntPtr buffer=Marshal.AllocHGlobal((int)size);var cores=new List<int[]>();
        try
        {
            Check(GetLogicalProcessorInformationEx(0,buffer,ref size),"read own host topology");
            int offset=0;while(offset<size)
            {
                int bytes=Marshal.ReadInt32(buffer,offset+4);if(bytes<32||offset+bytes>size)throw new Exception("Invalid topology record");
                if(Marshal.ReadInt32(buffer,offset)==0)
                {
                    int groups=(ushort)Marshal.ReadInt16(buffer,offset+30);if(groups!=1)throw new Exception("Fixture requires one processor group");
                    if((ushort)Marshal.ReadInt16(buffer,offset+40)==0)
                    {
                        ulong mask=(ulong)Marshal.ReadInt64(buffer,offset+32);var processors=new List<int>();
                        for(int bit=0;bit<64;bit++)if((mask&(1UL<<bit))!=0)processors.Add(bit);cores.Add(processors.ToArray());
                    }
                }
                offset+=bytes;
            }
        }
        finally{Marshal.FreeHGlobal(buffer);}return cores;
    }
    private static void Work(bool shared,int[] data)
    {ulong value=17;for(int i=0;i<180000;i++)value=shared?(value^(uint)data[i&(data.Length-1)])*2862933555777941757UL+1:(value^(value>>9))*2862933555777941757UL+1;Interlocked.Add(ref checksum,(long)value);}
    private static double Ms(long ticks){return 1000.0*ticks/Stopwatch.Frequency;}
    private static double Percentile(List<double> rows,double fraction){rows.Sort();return rows.Count==0?0:rows[Math.Min(rows.Count-1,(int)(fraction*(rows.Count-1)))];}
    private static void Hint(int cpu){var value=new Processor{Group=0,Number=(byte)cpu};Processor old,actual;Check(SetThreadIdealProcessorEx(GetCurrentThread(),ref value,out old),"own ideal write");Check(GetThreadIdealProcessorEx(GetCurrentThread(),out actual)&&actual.Group==0&&actual.Number==cpu,"own ideal readback");}
    private static int Main(string[] args)
    {
        if(args.Length!=5)return 2;var result=new Dictionary<string,object>();result["mode"]=args[0];result["scenario"]=args[1];result["option"]=args[2];result["utc"]=DateTime.UtcNow.ToString("o");int exit=1;
        using(var watchdog=new Timer(delegate{Environment.Exit(124);},null,30000,Timeout.Infinite))
        using(var request=new AutoResetEvent(false))using(var done=new AutoResetEvent(false))using(var ready=new ManualResetEvent(false))
        {
            Processor original;bool originalRead=GetThreadIdealProcessorEx(GetCurrentThread(),out original);result["threadStateReadable"]=originalRead;
            var process=Process.GetCurrentProcess();var originalClass=process.PriorityClass;var originalPriority=Thread.CurrentThread.Priority;
            Thread worker=null;bool stop=false,workerRestored=false;Exception workerError=null;var mainCpu=new int[64];var workerCpu=new int[64];bool mainRestored=false;Processor workerOriginal=new Processor();
            try
            {
                Check(originalRead,"read own original ideal");double seconds=double.Parse(args[3],System.Globalization.CultureInfo.InvariantCulture);if(seconds<1||seconds>15)throw new Exception("Duration outside budget");
                if(args[0]!="ideal"||(args[2]!="baseline"&&args[2]!="candidate"&&args[2]!="candidate-tail"))throw new Exception("Invalid option");
                var cores=Cores();if(cores.Count<2||cores[0].Length<2)throw new Exception("Fixture requires two physical cores and SMT");
                bool bad=args[1].StartsWith("samecore"),shared=args[1].EndsWith("shared"),candidate=args[2]!="baseline";int selectedCore=args[2]=="candidate-tail"?cores.Count-2:0;
                int mainIdeal=cores[selectedCore][0],workerIdeal=candidate?cores[selectedCore+1][0]:cores[0][1];
                var data=new int[8192];for(int i=0;i<data.Length;i++)data[i]=i*17+11;
                process.PriorityClass=ProcessPriorityClass.High;Thread.CurrentThread.Priority=ThreadPriority.AboveNormal;
                if(bad||candidate)Hint(mainIdeal);
                worker=new Thread(delegate()
                {
                    Processor saved;bool savedRead=GetThreadIdealProcessorEx(GetCurrentThread(),out saved);workerOriginal=saved;var savedPriority=Thread.CurrentThread.Priority;
                    try
                    {
                        Check(savedRead,"worker original ideal");Thread.CurrentThread.Priority=ThreadPriority.AboveNormal;
                        if(bad||candidate)Hint(workerIdeal);ready.Set();
                        while(true){request.WaitOne();if(Volatile.Read(ref stop))break;uint cpu=GetCurrentProcessorNumber();if(cpu<64)workerCpu[cpu]++;Work(shared,data);done.Set();}
                    }
                    catch(Exception error){workerError=error;ready.Set();done.Set();}
                    finally{try{Processor discarded,actual;if(savedRead){Check(SetThreadIdealProcessorEx(GetCurrentThread(),ref saved,out discarded),"worker restore");Check(GetThreadIdealProcessorEx(GetCurrentThread(),out actual)&&actual.Group==saved.Group&&actual.Number==saved.Number,"worker restore readback");}Thread.CurrentThread.Priority=savedPriority;workerRestored=true;}catch(Exception error){workerError=error;}}
                });worker.IsBackground=true;worker.Start();Check(ready.WaitOne(2000),"worker ready");if(workerError!=null)throw workerError;
                long warmEnd=Stopwatch.GetTimestamp()+Stopwatch.Frequency/2;
                while(Stopwatch.GetTimestamp()<warmEnd){request.Set();Work(shared,data);Check(done.WaitOne(2000),"warm worker completed");}
                Array.Clear(workerCpu,0,workerCpu.Length);var samples=new List<double>();long begin=Stopwatch.GetTimestamp();double cpuBegin=process.TotalProcessorTime.TotalSeconds;
                while(Ms(Stopwatch.GetTimestamp()-begin)<seconds*1000){long start=Stopwatch.GetTimestamp();uint cpu=GetCurrentProcessorNumber();if(cpu<64)mainCpu[cpu]++;request.Set();Work(shared,data);Check(done.WaitOne(2000),"worker completed");if(workerError!=null)throw workerError;samples.Add(Ms(Stopwatch.GetTimestamp()-start));}
                double elapsed=Ms(Stopwatch.GetTimestamp()-begin)/1000;result["samples"]=samples.Count;result["loopsPerSecond"]=samples.Count/elapsed;result["p50Ms"]=Percentile(samples,.5);result["p95Ms"]=Percentile(samples,.95);result["p99Ms"]=Percentile(samples,.99);result["cpuSeconds"]=process.TotalProcessorTime.TotalSeconds-cpuBegin;
                result["mainCpuSamples"]=mainCpu;result["workerCpuSamples"]=workerCpu;result["candidateMainIdeal"]=mainIdeal;result["candidateWorkerIdeal"]=workerIdeal;result["physicalCoreMasks"]=cores;
                result["originalMainIdeal"]=original.Number;result["originalWorkerIdeal"]=workerOriginal.Number;result["writeReadbackVerified"]=true;exit=0;
            }
            catch(Exception error){result["error"]=error.ToString();}
            finally
            {
                try{Volatile.Write(ref stop,true);request.Set();if(worker!=null)Check(worker.Join(3000),"worker cleanup");if(workerError!=null)throw workerError;
                    if(originalRead){Processor discarded,actual;Check(SetThreadIdealProcessorEx(GetCurrentThread(),ref original,out discarded),"main restore");Check(GetThreadIdealProcessorEx(GetCurrentThread(),out actual)&&actual.Group==original.Group&&actual.Number==original.Number,"main restore readback");}
                    Thread.CurrentThread.Priority=originalPriority;process.PriorityClass=originalClass;mainRestored=true;result["restored"]=mainRestored&&(worker==null||workerRestored);
                }catch(Exception error){result["restoreError"]=error.ToString();result["restored"]=false;exit=1;}
            }
            File.WriteAllText(args[4],new JavaScriptSerializer().Serialize(result));return exit;
        }
    }
}
