using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;

internal static class ThreadQosCandidate
{
    [StructLayout(LayoutKind.Sequential)] private struct Qos { public uint Version,Control,State; }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetThreadInformation(IntPtr h,int kind,ref Qos value,uint bytes);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool SetThreadInformation(IntPtr h,int kind,ref Qos value,uint bytes);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetProcessInformation(IntPtr h,int kind,ref Qos value,uint bytes);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool SetProcessInformation(IntPtr h,int kind,ref Qos value,uint bytes);
    private static long checksum;
    private static void Check(bool ok,string operation) { if(!ok)throw new Exception(operation+" error="+Marshal.GetLastWin32Error()); }
    private static void Work(int count) { ulong value=17;for(int i=0;i<count;i++)value=(value^(value>>9))*2862933555777941757UL+3037000493UL;Interlocked.Add(ref checksum,(long)value); }
    private static double Ms(long ticks) { return 1000.0*ticks/Stopwatch.Frequency; }
    private static double Percentile(List<double> values,double fraction) { values.Sort();return values.Count==0?0:values[Math.Min(values.Count-1,(int)(fraction*(values.Count-1)))]; }
    private static int Main(string[] args)
    {
        if(args.Length!=5)return 2;
        var result=new Dictionary<string,object>();result["mode"]=args[0];result["scenario"]=args[1];result["option"]=args[2];result["utc"]=DateTime.UtcNow.ToString("o");
        int exit=1;bool seeded=false,processChanged=false;var originalThread=new Qos{Version=1};var originalProcess=new Qos{Version=1};
        Process process=Process.GetCurrentProcess();var originalPriority=process.PriorityClass;var originalThreadPriority=Thread.CurrentThread.Priority;
        using(var watchdog=new Timer(delegate{Environment.Exit(124);},null,30000,Timeout.Infinite))
        {
            try
            {
                double seconds=double.Parse(args[3],System.Globalization.CultureInfo.InvariantCulture);if(seconds<1||seconds>15)throw new Exception("Duration outside budget");
                if(args[0]!="qos"||(args[1]!="eco-continuous"&&args[1]!="eco-bursty"&&args[1]!="default-continuous"&&args[1]!="default-bursty")||(args[2]!="baseline"&&args[2]!="candidate"))throw new Exception("Unknown scenario");
                bool originalReadable=GetThreadInformation(GetCurrentThread(),3,ref originalThread,12);result["threadStateReadable"]=originalReadable;result["threadReadError"]=originalReadable?0:Marshal.GetLastWin32Error();
                Check(GetProcessInformation(GetCurrentProcess(),4,ref originalProcess,12),"read own process QoS");
                var high=new Qos{Version=1,Control=1,State=0};Check(SetProcessInformation(GetCurrentProcess(),4,ref high,12),"own process HighQoS");processChanged=true;
                process.PriorityClass=ProcessPriorityClass.High;Thread.CurrentThread.Priority=ThreadPriority.AboveNormal;
                var desired=args[2]=="candidate"?high:new Qos{Version=1,Control=args[1].StartsWith("eco")?1u:0u,State=args[1].StartsWith("eco")?1u:0u};
                Check(SetThreadInformation(GetCurrentThread(),3,ref desired,12),"own thread QoS write");seeded=true;
                var readback=new Qos{Version=1};bool readable=GetThreadInformation(GetCurrentThread(),3,ref readback,12);
                result["writeReadbackVerified"]=readable&&readback.Control==desired.Control&&readback.State==desired.State;
                if(readable&&(readback.Control!=desired.Control||readback.State!=desired.State))throw new Exception("QoS readback mismatch");
                bool bursty=args[1].EndsWith("bursty");long warmEnd=Stopwatch.GetTimestamp()+Stopwatch.Frequency/2;
                while(Stopwatch.GetTimestamp()<warmEnd){Work(120000);if(bursty)Thread.Sleep(2);}
                var values=new List<double>();long start=Stopwatch.GetTimestamp();double cpu=process.TotalProcessorTime.TotalSeconds;
                while(Ms(Stopwatch.GetTimestamp()-start)<seconds*1000){long begin=Stopwatch.GetTimestamp();Work(120000);values.Add(Ms(Stopwatch.GetTimestamp()-begin));if(bursty)Thread.Sleep(2);}
                double elapsed=Ms(Stopwatch.GetTimestamp()-start)/1000;result["samples"]=values.Count;result["loopsPerSecond"]=values.Count/elapsed;
                result["p50Ms"]=Percentile(values,.50);result["p95Ms"]=Percentile(values,.95);result["p99Ms"]=Percentile(values,.99);result["cpuSeconds"]=process.TotalProcessorTime.TotalSeconds-cpu;
                result["requestedControl"]=desired.Control;result["requestedState"]=desired.State;
                exit=0;
            }
            catch(Exception error){result["error"]=error.ToString();}
            finally
            {
                try
                {
                    if(seeded){bool readable=(bool)result["threadStateReadable"];var restore=readable?originalThread:new Qos{Version=1};Check(SetThreadInformation(GetCurrentThread(),3,ref restore,12),"restore own thread request");}
                    if(processChanged){Check(SetProcessInformation(GetCurrentProcess(),4,ref originalProcess,12),"restore own process QoS");var actual=new Qos{Version=1};Check(GetProcessInformation(GetCurrentProcess(),4,ref actual,12)&&actual.Control==originalProcess.Control&&actual.State==originalProcess.State,"verify process QoS restored");}
                    Thread.CurrentThread.Priority=originalThreadPriority;process.PriorityClass=originalPriority;
                    result["restored"]=true;
                    result["threadRestoreScope"]=(result.ContainsKey("threadStateReadable")&&(bool)result["threadStateReadable"])?"Original API state restored":"Owned fresh benchmark thread reset to system-managed, then process exits; not an external-thread restoration proof";
                }
                catch(Exception error){result["restoreError"]=error.ToString();result["restored"]=false;exit=1;}
            }
            File.WriteAllText(args[4],new JavaScriptSerializer().Serialize(result));return exit;
        }
    }
}
