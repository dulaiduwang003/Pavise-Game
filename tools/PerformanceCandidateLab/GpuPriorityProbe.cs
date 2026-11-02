#if PAVISE_SELFTEST && PAVISE_GPU_PRIORITY_PROBE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Web.Script.Serialization;

namespace PaviseApp
{
    internal static class GpuPriorityProbe
    {
        private static int Main()
        {
            Settings.UseTransientStoreForCurrentProcess();Logger.WritesEnabled=false;
            using(var watchdog=new Timer(delegate{Environment.Exit(124);},null,10000,Timeout.Infinite))
            using(var own=Process.GetCurrentProcess())
            {
                var report=new Dictionary<string,object>();int original;
                int read=Native.D3DKMTGetProcessSchedulingPriorityClass(own.Handle,out original);
                report["originalReadStatus"]=read;report["original"]=original;
                if(read!=0){report["restored"]=true;report["scope"]="Own process read unavailable; no writes";Console.WriteLine(new JavaScriptSerializer().Serialize(report));return 0;}
                bool restored=false;
                try
                {
                    var rows=new List<object>();
                    foreach(int desired in new[]{4,5})
                    {
                        int current;int before=Native.D3DKMTGetProcessSchedulingPriorityClass(own.Handle,out current);
                        if(before!=0||current!=original)throw new Exception("Owned GPU original drift");
                        int write=Native.D3DKMTSetProcessSchedulingPriorityClass(own.Handle,desired);int actual;
                        int after=Native.D3DKMTGetProcessSchedulingPriorityClass(own.Handle,out actual);
                        rows.Add(new Dictionary<string,object>{{"desired",desired},{"writeStatus",write},{"writeStatusHex",unchecked((uint)write).ToString("x8")},{"readStatus",after},{"actual",actual}});
                        if(after!=0)throw new Exception("Owned GPU readback failed");
                        if(actual!=original&&Native.D3DKMTSetProcessSchedulingPriorityClass(own.Handle,original)!=0)throw new Exception("Owned GPU restore failed");
                    }
                    report["attempts"]=rows;
                }
                finally
                {
                    int actual;restored=Native.D3DKMTGetProcessSchedulingPriorityClass(own.Handle,out actual)==0&&(actual==original||Native.D3DKMTSetProcessSchedulingPriorityClass(own.Handle,original)==0);
                    restored=restored&&Native.D3DKMTGetProcessSchedulingPriorityClass(own.Handle,out actual)==0&&actual==original;
                    report["restored"]=restored;report["scope"]="Exact owned process handle; GPU idle API probe only";Console.WriteLine(new JavaScriptSerializer().Serialize(report));
                }
                return restored?0:1;
            }
        }
    }
    internal static partial class SelfTests
    {public static bool TryHandleRuntimeMode(string[] args){throw new InvalidOperationException("GPU priority fixture cannot launch normal runtime");}}
}
#endif
