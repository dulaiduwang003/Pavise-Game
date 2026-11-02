#if PAVISE_SELFTEST && PAVISE_MEMORY_CRASH
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class CrashMemoryRecovery
    {
        [DllImport("advapi32.dll")] private static extern int RegOverridePredefKey(IntPtr predefined, IntPtr replacement);
        [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetProcessWorkingSetSizeEx(IntPtr process,out UIntPtr min,out UIntPtr max,out uint flags);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool SetProcessWorkingSetSizeEx(IntPtr process,UIntPtr min,UIntPtr max,uint flags);
        private const string Prefix = @"Software\PaviseMemoryCrashLab\";
        private static readonly IntPtr Hkcu = new IntPtr(unchecked((int)0x80000001));
        private static void Check(bool ok,string detail) { if(!ok)throw new Exception(detail+" error="+Marshal.GetLastWin32Error()); }
        private sealed class Mapping : IDisposable
        {
            private RegistryKey key;
            internal Mapping(string path)
            {
                Guid id;Check(path.StartsWith(Prefix,StringComparison.Ordinal)&&Guid.TryParseExact(path.Substring(Prefix.Length),"N",out id),"Invalid owned registry path");
                key=Registry.CurrentUser.OpenSubKey(path,true);Check(key!=null,"Owned lab root missing");
                Check(RegOverridePredefKey(Hkcu,key.Handle.DangerousGetHandle())==0,"HKCU process-local isolation");
                Registry.CurrentUser.SetValue("PaviseLabSentinel",path);Check((string)key.GetValue("PaviseLabSentinel")==path,"Isolation readback");
            }
            public void Dispose(){Check(RegOverridePredefKey(Hkcu,IntPtr.Zero)==0,"HKCU unmap");key.Dispose();}
        }
        private static FrameThreadReceipt OwnIdentity()
        {
            var process=Process.GetCurrentProcess();long birth,exit,kernel,user;
            Check(Native.GetThreadTimes(GetCurrentThread(),out birth,out exit,out kernel,out user),"Own thread identity");
            return new FrameThreadReceipt{Pid=process.Id,ProcessBirth=process.StartTime.ToFileTimeUtc(),Tid=(int)GetCurrentThreadId(),ThreadBirth=birth,Original=0};
        }
        private static void Seed(NativeFrameThreads adapter,FrameThreadReceipt id,int memory)
        {int current;Check(adapter.ReadMemory(id,out current)==0&&adapter.SetMemory(id,current,memory),"Own page seed");Check(adapter.Read(id,out current)==0&&adapter.Set(id,current,0),"Own CPU seed");}
        private static int Renderer()
        {
            var adapter=new NativeFrameThreads();var main=OwnIdentity();int original;
            Check(adapter.ReadMemory(main,out original)==0,"Renderer original memory");
            UIntPtr min,max;uint flags;Check(GetProcessWorkingSetSizeEx(GetCurrentProcess(),out min,out max,out flags),"Own limits read");
            bool bounded=false;FrameThreadReceipt workerId=null;Exception workerError=null;
            using(var ready=new ManualResetEvent(false))using(var stop=new ManualResetEvent(false))
            {
                var worker=new Thread(delegate()
                {
                    var id=OwnIdentity();int saved=0;
                    try{Check(adapter.ReadMemory(id,out saved)==0,"Worker memory read");Seed(adapter,id,4);workerId=id;ready.Set();stop.WaitOne();}
                    catch(Exception error){workerError=error;ready.Set();}
                    finally{int actual;if(saved>0&&adapter.ReadMemory(id,out actual)==0)adapter.SetMemory(id,actual,saved);}
                });worker.IsBackground=true;
                try
                {
                    Seed(adapter,main,1);Check(SetProcessWorkingSetSizeEx(GetCurrentProcess(),new UIntPtr(65536),new UIntPtr(32*1024*1024),6),"Own hard quota");bounded=true;
                    worker.Start();Check(ready.WaitOne(3000)&&workerError==null&&workerId!=null,"Renderer worker ready");
                    Console.WriteLine("v1;"+main.Encode()+";"+workerId.Encode());Console.Out.Flush();
                    Check(Console.ReadLine()=="exit","Owned renderer IPC exit");
                    return 0;
                }
                finally
                {
                    stop.Set();Check(!worker.IsAlive||worker.Join(3000),"Owned worker joined");int actual;
                    Check(adapter.ReadMemory(main,out actual)==0&&adapter.SetMemory(main,actual,original),"Renderer seed restore");
                    if(bounded){uint restore=((flags&1)!=0?1u:2u)|((flags&4)!=0?4u:8u);Check(SetProcessWorkingSetSizeEx(GetCurrentProcess(),min,max,restore),"Own quota restore");}
                }
            }
        }
        private static int Controller(string path,string receipt)
        {
            using(var mapping=new Mapping(path))
            {
                List<FrameThreadReceipt> ids;Check(FrameCoordination.Parse(receipt,out ids)&&ids.Count==2,"Controller identity receipt");
                var sample=new FrameCoordinationSample{Pid=ids[0].Pid,Birth=ids[0].ProcessBirth,PresentTid=ids[0].Tid,WorkerTid=ids[1].Tid,Valid=true};
                foreach(var id in ids)sample.Threads[id.Tid]=id.ThreadBirth;
                Check(FrameCoordination.Apply(sample,delegate{return true;}),"Production paired apply");
                Check(FrameMemoryProtection.HasResidue&&FrameMemoryProtection.Healthy(),"Real registry receipt and native memory lease");
                Console.WriteLine("ready");Console.Out.Flush();Thread.Sleep(Timeout.Infinite);return 1;
            }
        }
        private static int Recovery(string path)
        {
            using(var mapping=new Mapping(path))
            {
                Check(FrameCoordination.HealFromCrash(),"Production startup crash recovery");
                Check(!FrameCoordination.HasResidue&&!FrameMemoryProtection.HasResidue,"Both journals empty");
                Console.WriteLine("recovered");return 0;
            }
        }
        private static Process Start(string args)
        {return Process.Start(new ProcessStartInfo{FileName=Process.GetCurrentProcess().MainModule.FileName,Arguments=args,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true});}
        private static string Line(Process process)
        {var read=Task.Factory.StartNew(delegate{return process.StandardOutput.ReadLine();});Check(read.Wait(5000),"Owned child output timeout");if(read.Result==null)throw new Exception("Owned child returned no line: "+process.StandardError.ReadToEnd());return read.Result;}
        private static void Kill(Process process)
        {if(process!=null&&!process.HasExited){Check(TerminateProcess(process.Handle,123),"Terminate exact owned child handle");Check(process.WaitForExit(3000),"Killed owned child drained");}}
        private static void RunCase(string path,int scenario)
        {
            Process renderer=null,controller=null;
            try
            {
                renderer=Start("--renderer");string receipt=Line(renderer);List<FrameThreadReceipt> ids;Check(FrameCoordination.Parse(receipt,out ids)&&ids.Count==2,"Parent identities");
                controller=Start("--controller "+path+" "+receipt);Check(Line(controller)=="ready","Controller ready");
                var adapter=new NativeFrameThreads();foreach(var id in ids){int value;Check(adapter.ReadMemory(id,out value)==0&&value==5,"Actual boosted child memory");Check(adapter.Read(id,out value)==0&&value==1,"Actual boosted child CPU");}
                Kill(controller);
                if(scenario==1){Check(adapter.SetMemory(ids[1],5,3)&&adapter.Set(ids[1],1,2),"Owned external choice seed");}
                if(scenario==2)Kill(renderer);
                using(var recovery=Start("--recover "+path)){Check(Line(recovery)=="recovered","New process recovered");Check(recovery.WaitForExit(3000)&&recovery.ExitCode==0,"Recovery child success");}
                if(scenario!=2)
                {
                    for(int i=0;i<ids.Count;i++){int memory=0,cpu=0;Check(adapter.ReadMemory(ids[i],out memory)==0&&adapter.Read(ids[i],out cpu)==0,"Surviving target readable");Check(memory==(scenario==1&&i==1?3:i==0?1:4)&&cpu==(scenario==1&&i==1?2:0),"Original restore / preserve external choice");}
                    renderer.StandardInput.WriteLine("exit");renderer.StandardInput.Flush();Check(renderer.WaitForExit(5000)&&renderer.ExitCode==0,"Renderer graceful own seed cleanup");
                }
                Console.WriteLine("PASS cross-process crash case "+scenario+": "+(scenario==0?"live target exact restore":scenario==1?"external CPU/page choices preserved":"dead target journal cleared"));
            }
            finally{Kill(controller);Kill(renderer);if(controller!=null)controller.Dispose();if(renderer!=null)renderer.Dispose();}
        }
        private static int Main(string[] args)
        {
            Logger.WritesEnabled=false;
            using(var watchdog=new Timer(delegate{Environment.Exit(124);},null,60000,Timeout.Infinite))
            {
                if(args.Length==1&&args[0]=="--renderer")return Renderer();
                if(args.Length==3&&args[0]=="--controller")return Controller(args[1],args[2]);
                if(args.Length==2&&args[0]=="--recover")return Recovery(args[1]);
                if(args.Length!=0)return 2;
                string path=Prefix+Guid.NewGuid().ToString("N");
                using(var key=Registry.CurrentUser.CreateSubKey(path)){Check(key!=null,"Create owned isolated lab root");}
                try{for(int scenario=0;scenario<3;scenario++)RunCase(path,scenario);return 0;}
                finally{Registry.CurrentUser.DeleteSubKeyTree(path,false);Check(Registry.CurrentUser.OpenSubKey(path)==null,"Own registry root removed");Console.WriteLine("PASS isolated registry removed; live Pavise and user targets untouched");}
            }
        }
    }
    internal static partial class SelfTests
    {public static bool TryHandleRuntimeMode(string[] args){throw new InvalidOperationException("Memory crash fixture cannot launch normal runtime");}}
}
#endif
