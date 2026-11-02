#if PAVISE_SELFTEST && PAVISE_FRAME_NATIVE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal static class FrameCoordinationNativeRunner
    {
        private static int Main(string[] args)
        {
            Settings.UseTransientStoreForCurrentProcess();Logger.WritesEnabled=false;Lang.Init();
            using(var watchdog=new Timer(delegate{Environment.Exit(124);},null,45000,Timeout.Infinite))
            {
                Process child=null;
                try
                {
                    string ready=Path.Combine(args[1],"owned-dxgi.csv");
                    child=Process.Start(new ProcessStartInfo(args[0],"\""+ready+"\""){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden});
                    var elapsed=Stopwatch.StartNew();while(!File.Exists(ready)&&!child.HasExited&&elapsed.ElapsedMilliseconds<10000)Thread.Sleep(100);
                    if(!File.Exists(ready))throw new Exception("Owned DXGI fixture initialization failed");
                    string[] ids=File.ReadAllLines(ready)[1].Split(',');int frame=int.Parse(ids[1]),worker=int.Parse(ids[2]);
                    long birth=child.StartTime.ToFileTimeUtc();
                    var sample=FrameCoordinationSampler.Capture(child.Id,birth,delegate{return !child.HasExited;});
                    if(!sample.Valid || sample.Issue!=FrameCaptureIssue.None || sample.Events<=0
                        || sample.TraceCode!=0 || sample.LostEvents!=0 || sample.PresentTid!=frame || sample.WorkerTid!=worker)
                        throw new Exception("Real DXGI/wake attribution failed: "+sample.Detail+" present="+sample.PresentTid+" worker="+sample.WorkerTid+" CPUms="+sample.CpuMs);
                    if(!FrameCoordination.Apply(sample,delegate{return !child.HasExited;}))throw new Exception("Real paired thread apply failed");
                    var native=new NativeFrameThreads();
                    foreach(int tid in new[]{frame,worker})
                    {
                        int priority;var id=new FrameThreadReceipt{Pid=child.Id,ProcessBirth=birth,Tid=tid,ThreadBirth=sample.Threads[tid]};
                        int expected=tid==frame?15:1;
                        if(native.Read(id,out priority)!=0||priority!=expected)throw new Exception("Actual Present/worker thread priority did not match TimeCritical/AboveNormal");
                    }
                    if(!FrameCoordination.ReleaseExcept(0,0))throw new Exception("Native restore failed");
                    foreach(int tid in new[]{frame,worker})
                    {
                        int priority;var id=new FrameThreadReceipt{Pid=child.Id,ProcessBirth=birth,Tid=tid,ThreadBirth=sample.Threads[tid]};
                        if(native.Read(id,out priority)!=0||priority!=0)throw new Exception("Actual original priority not restored");
                    }
                    if(!FrameCoordination.HealFromCrash())throw new Exception("Trace/session cleanup failed");
                    Console.WriteLine("PASS production managed DXGI Present + ReadyThread attribution, Present TimeCritical / worker AboveNormal, exact native restore; sampleCpuMs="+sample.CpuMs.ToString("F1"));
                    return 0;
                }
                catch(Exception error){Console.Error.WriteLine(error);return 1;}
                finally {FrameCoordination.Close(6000);if(child!=null){if(!child.HasExited)child.Kill();child.Dispose();}}
            }
        }
    }
    internal static partial class SelfTests
    {public static bool TryHandleRuntimeMode(string[] args){throw new InvalidOperationException("Native test cannot launch the application");}}
}
#endif
