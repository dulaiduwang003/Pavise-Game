#if PAVISE_SELFTEST && PAVISE_HELPER_LAB
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;

namespace PaviseApp
{
    internal static class OwnHelperPlacement
    {
        [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process,uint code);
        private static void Check(bool ok,string detail){if(!ok)throw new Exception(detail);}
        private static Dictionary<string,object> ReadSelf()
        {
            IntPtr self=new IntPtr(-1);int control,state;Native.TryQueryPowerThrottling(self,out control,out state);ulong affinity;Native.TryQueryAffinity(self,out affinity);
            return new Dictionary<string,object>{{"priority",Native.GetPriorityClass(self)},{"cpuSets",Native.QueryCpuSets(self)},{"io",Native.QueryIoPriority(self)},{"affinity",affinity},{"qosControl",control},{"qosState",state}};
        }
        private static int Main(string[] args)
        {
            Settings.UseTransientStoreForCurrentProcess();Logger.WritesEnabled=false;
            using(var watchdog=new Timer(delegate{Environment.Exit(124);},null,20000,Timeout.Infinite))
            {
                if(args.Length==1&&args[0]=="--child"){Console.WriteLine(new JavaScriptSerializer().Serialize(ReadSelf()));return 0;}
                if(args.Length!=0)return 2;
                IntPtr self=new IntPtr(-1);uint originalPriority=Native.GetPriorityClass(self);int originalIo=Native.QueryIoPriority(self);uint[] originalSets=Native.QueryCpuSets(self);
                if(originalPriority==0||originalIo<0||originalSets==null)throw new Exception("Original state unreadable");
                var report=new Dictionary<string,object>();var rows=new List<object>();int exit=1;
                try
                {
                    uint[] background=CpuTopology.BackgroundYieldCpuSetIds();Check(background!=null&&background.Length>0,"No production background partition");
                    Check(Native.TrySetCpuSets(self,background)&&Native.CpuSetsMatch(self,background),"Owned parent CPU sets");
                    Check(Native.TrySetIoPriority(self,1)&&Native.QueryIoPriority(self)==1,"Owned parent low I/O");
                    foreach(uint priority in new[]{Native.NORMAL_PRIORITY_CLASS,Native.BELOW_NORMAL_PRIORITY_CLASS})
                    {
                        Check(Native.SetPriorityClass(self,priority)&&Native.GetPriorityClass(self)==priority,"Owned parent priority");
                        var parent=ReadSelf();string exe=Process.GetCurrentProcess().MainModule.FileName;
                        var start=new ProcessStartInfo{FileName=exe,Arguments="--child",UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                        using(Process child=Process.Start(start))
                        {
                            IntPtr ownedHandle=child.Handle;
                            if(!child.WaitForExit(5000)){TerminateProcess(ownedHandle,124);child.WaitForExit(2000);throw new Exception("Owned child timeout");}
                            string stdout=child.StandardOutput.ReadToEnd(),stderr=child.StandardError.ReadToEnd();Check(child.ExitCode==0,"Owned child failed "+stderr);
                            rows.Add(new Dictionary<string,object>{{"parent",parent},{"child",new JavaScriptSerializer().DeserializeObject(stdout)}});
                        }
                    }
                    report["inheritance"]=rows;exit=0;
                }
                catch(Exception error){report["error"]=error.ToString();}
                finally
                {
                    bool clean=Native.RestoreCpuSetsVerified(self,originalSets);
                    clean=Native.SetPriorityClass(self,originalPriority)&&Native.GetPriorityClass(self)==originalPriority&&clean;
                    clean=Native.TrySetIoPriority(self,originalIo)&&Native.QueryIoPriority(self)==originalIo&&clean;
                    report["restored"]=clean;if(!clean)exit=1;
                }
                Console.WriteLine(new JavaScriptSerializer().Serialize(report));return exit;
            }
        }
    }
    internal static partial class SelfTests
    {public static bool TryHandleRuntimeMode(string[] args){throw new InvalidOperationException("Owned-helper fixture cannot launch normal runtime");}}
}
#endif
