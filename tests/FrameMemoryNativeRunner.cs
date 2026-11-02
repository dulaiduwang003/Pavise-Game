#if PAVISE_SELFTEST && PAVISE_MEMORY_NATIVE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal static class FrameMemoryNativeRunner
    {
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError=true)] private static extern bool GetProcessWorkingSetSizeEx(IntPtr h,out UIntPtr min,out UIntPtr max,out uint flags);
        [DllImport("kernel32.dll", SetLastError=true)] private static extern bool SetProcessWorkingSetSizeEx(IntPtr h,UIntPtr min,UIntPtr max,uint flags);
        private static void Check(bool ok,string name) { if(!ok)throw new Exception(name+" error="+Marshal.GetLastWin32Error()); }
        private static int Main()
        {
            Settings.UseTransientStoreForCurrentProcess();Logger.WritesEnabled=false;Lang.Init();
            using(var watchdog=new Timer(delegate{Environment.Exit(124);},null,15000,Timeout.Infinite))
            {
                SelfTests.RunReleaseNotesRegressionTests();
                Console.WriteLine("PASS current release version, trilingual notes and release ordering");
                var adapter=new NativeFrameThreads();var id=new FrameThreadReceipt{Pid=Process.GetCurrentProcess().Id,
                    ProcessBirth=Process.GetCurrentProcess().StartTime.ToFileTimeUtc(),Tid=(int)GetCurrentThreadId(),Original=0};
                long exited,kernel,user;Check(Native.GetThreadTimes(GetCurrentThread(),out id.ThreadBirth,out exited,out kernel,out user),"thread identity");
                int original;Check(adapter.ReadMemory(id,out original)==0,"production native memory read");
                UIntPtr min,max;uint flags;Check(GetProcessWorkingSetSizeEx(GetCurrentProcess(),out min,out max,out flags),"read own limits");
                bool bounded=false;int result=1;
                try
                {
                    Check(adapter.SetMemory(id,original,1),"seed own low page priority");
                    Check(SetProcessWorkingSetSizeEx(GetCurrentProcess(),new UIntPtr(65536),new UIntPtr(32*1024*1024),6),"bound own working set");bounded=true;
                    Check(adapter.UnderPressure(id.Pid,id.ProcessBirth),"production pressure gate recognizes hard quota");
                    Check(FrameMemoryProtection.Apply(new List<FrameThreadReceipt>{id},adapter,delegate{return true;}),"production apply");
                    int actual;Check(adapter.ReadMemory(id,out actual)==0&&actual==5,"actual Normal priority readback");
                    Check(FrameMemoryProtection.HasResidue&&FrameMemoryProtection.Healthy(),"live receipt/lease");
                    Check(FrameCoordination.Restore(),"production lifecycle restore");
                    Check(adapter.ReadMemory(id,out actual)==0&&actual==1&&!FrameMemoryProtection.HasResidue,"exact original priority restore");
                    var reused=new FrameThreadReceipt{Pid=id.Pid,ProcessBirth=id.ProcessBirth,Tid=id.Tid,ThreadBirth=id.ThreadBirth+1,Original=0};
                    Check(adapter.ReadMemory(reused,out actual)==1&&!adapter.SetMemory(reused,1,5),"reused identity rejected");
                    Console.WriteLine("PASS production native memory priority, actual hard-quota gate, journal-before-write, Normal readback, exact lifecycle restore and reused identity protection");result=0;
                }
                catch(Exception error){Console.Error.WriteLine(error);}
                finally
                {
                    Check(FrameMemoryProtection.Restore(),"final receipt cleanup");int actual;
                    Check(adapter.ReadMemory(id,out actual)==0&&adapter.SetMemory(id,actual,original),"restore test seed");
                    if(bounded){uint restore=((flags&1)!=0?1u:2u)|((flags&4)!=0?4u:8u);
                        Check(SetProcessWorkingSetSizeEx(GetCurrentProcess(),min,max,restore),"restore working set");
                        UIntPtr finalMin,finalMax;uint finalFlags;Check(GetProcessWorkingSetSizeEx(GetCurrentProcess(),out finalMin,out finalMax,out finalFlags)
                            &&finalMin==min&&finalMax==max&&(finalFlags&5)==(flags&5),"verify working set restored");}
                    Console.WriteLine("PASS own test seed and process limits restored; persistent user settings untouched");
                }
                return result;
            }
        }
    }
    internal static partial class SelfTests
    {
        private static void Eq<T>(T expected,T actual)
        {if(!EqualityComparer<T>.Default.Equals(expected,actual))throw new InvalidOperationException("Expected "+expected+", actual "+actual);}
        public static bool TryHandleRuntimeMode(string[] args){throw new InvalidOperationException("Memory native runner cannot launch normal runtime");}
    }
}
#endif
