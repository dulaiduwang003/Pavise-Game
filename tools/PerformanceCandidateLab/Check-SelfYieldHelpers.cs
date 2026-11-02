#if PAVISE_SELFTEST && PAVISE_HELPER_CHECKS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PaviseApp
{
    internal static class SelfYieldHelperChecks
    {
        private static int assertions;
        private static void Check(bool ok,string detail){assertions++;if(!ok)throw new Exception(detail);}
        private sealed class FakeChild : IYieldChild
        {
            internal uint[] Current=new uint[0];internal int State,Writes,Closes;internal bool IsOwned=true,AllowWrite=true,FailAfterWrite,UnreadAfterWrite;
            public bool Owned {get{return IsOwned;}}
            public int Read(out uint[] ids){ids=State==0?(uint[])Current.Clone():null;return State;}
            public bool Write(uint[] expected,uint[] desired)
            {if(State!=0||!NativeYieldChild.Same(Current,expected)||!AllowWrite)return false;Writes++;Current=(uint[])desired.Clone();if(UnreadAfterWrite)State=-1;return !FailAfterWrite;}
            public void Dispose(){Closes++;}
        }
        private static void Fakes()
        {
            Check(SelfYield.HelperMasksSeparate(0x3ff,0xc00,0xfff),"Separate physical core masks admitted");
            Check(!SelfYield.HelperMasksSeparate(0xfff,0xc00,0xfff),"Full-core game denied");
            Check(!SelfYield.HelperMasksSeparate(0x800,0xc00,0xfff),"SMT sibling overlap denied");
            Check(!SelfYield.HelperMasksSeparate(0,0xc00,0xfff)&&!SelfYield.HelperMasksSeparate(0x3ff,0,0xfff),"Unknown masks denied");
            Check(!SelfYield.HelperMasksSeparate(0x1000,0xc00,0xfff),"Outside-topology mask denied");
            var wanted=new uint[]{266,267};FakeChild child;IDisposable scope;
            child=new FakeChild{IsOwned=false};scope=SelfYield.HelperScopeForTest(child,wanted);scope.Dispose();Check(child.Writes==0&&child.Closes==1,"Foreign helper untouched");
            child=new FakeChild{Current=new uint[]{7}};scope=SelfYield.HelperScopeForTest(child,wanted);scope.Dispose();Check(child.Writes==0&&child.Current[0]==7&&child.Closes==1,"Explicit child selection preserved");
            child=new FakeChild{State=-1};scope=SelfYield.HelperScopeForTest(child,wanted);scope.Dispose();Check(child.Writes==0&&child.Closes==1,"Unknown original skipped");
            child=new FakeChild();scope=SelfYield.HelperScopeForTest(child,new uint[]{266,266});scope.Dispose();Check(child.Writes==0&&child.Closes==1,"Invalid duplicate CPU sets skipped");
            child=new FakeChild();scope=SelfYield.HelperScopeForTest(child,wanted);Check(NativeYieldChild.Same(child.Current,wanted),"Owned default child follows");scope.Dispose();scope.Dispose();Check(child.Current.Length==0&&child.Writes==2&&child.Closes==1,"Exact default restored once");
            child=new FakeChild();scope=SelfYield.HelperScopeForTest(child,wanted);child.Current=new uint[]{999};scope.Dispose();Check(child.Current[0]==999&&child.Writes==1&&child.Closes==1,"Later external selection preserved");
            child=new FakeChild();scope=SelfYield.HelperScopeForTest(child,wanted);child.State=1;scope.Dispose();Check(child.Writes==1&&child.Closes==1,"Exited child releases handle without write");
            child=new FakeChild();scope=SelfYield.HelperScopeForTest(child,wanted);SelfYield.Release();Check(child.Current.Length==0&&child.Closes==1,"Release restores active child immediately");scope.Dispose();Check(child.Closes==1,"Release then dispose idempotent");
            child=new FakeChild();scope=SelfYield.HelperScopeForTest(child,wanted);child.AllowWrite=false;scope.Dispose();Check(SelfYield.PendingHelpersForTest==1&&child.Closes==0,"Restore denial retains exact owned handle");child.AllowWrite=true;SelfYield.Release();Check(child.Current.Length==0&&child.Closes==1&&SelfYield.PendingHelpersForTest==0,"Restore denial retries outside engaged state");
            child=new FakeChild{FailAfterWrite=true,UnreadAfterWrite=true};scope=SelfYield.HelperScopeForTest(child,wanted);Check(SelfYield.PendingHelpersForTest==1&&child.Closes==0,"Partial write unreadable rollback retained");child.State=0;child.UnreadAfterWrite=false;child.FailAfterWrite=false;SelfYield.Release();scope.Dispose();Check(child.Current.Length==0&&child.Closes==1&&SelfYield.PendingHelpersForTest==0,"Partial failure eventually restored");
            var pending=new List<FakeChild>();
            for(int i=0;i<32;i++){child=new FakeChild();scope=SelfYield.HelperScopeForTest(child,wanted);child.State=-1;scope.Dispose();pending.Add(child);}
            child=new FakeChild();scope=SelfYield.HelperScopeForTest(child,wanted);scope.Dispose();Check(SelfYield.PendingHelpersForTest==32&&child.Writes==0&&child.Closes==1,"Unavailable children cannot grow beyond cap");
            foreach(var item in pending)item.State=0;SelfYield.Release();Check(SelfYield.PendingHelpersForTest==0,"Bounded pending handles cleaned");foreach(var item in pending)Check(item.Closes==1&&item.Current.Length==0,"Pending native equivalent exact restore");
        }
        private static Process Child()
        {return Process.Start(new ProcessStartInfo{FileName=Process.GetCurrentProcess().MainModule.FileName,Arguments="--child",UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true});}
        private static void NativeCases()
        {
            // 核心调度只用硬亲和性：自让位只降低自身优先级与 I/O，不再给自身或 helper 写软 CPU Sets。
            Check(NativeYieldChild.Open(Native.GetCurrentProcess())==null,"Parent identity rejects own process as helper");
            IntPtr self=Native.GetCurrentProcess();uint[] original=Native.QueryCpuSets(self);uint priority=Native.GetPriorityClass(self);int io=Native.QueryIoPriority(self);
            Check(original!=null&&priority!=0&&io>=0,"Read own baseline");
            try
            {
                using(var renderer=Child())
                using(var child=Child())
                {
                    Check(renderer.StandardOutput.ReadLine()=="ready","Owned renderer ready");
                    ulong gameMask=CpuTopology.AllMask&~CpuTopology.BackgroundSqueezeMask();Check(gameMask!=0&&Native.SetProcessAffinityMask(renderer.Handle,(UIntPtr)gameMask),"Owned renderer distinct physical cores");
                    Check(child.StandardOutput.ReadLine()=="ready","Owned child ready");uint[] before=Native.QueryCpuSets(child.Handle);Check(before!=null&&before.Length==0,"Real child default sets");
                    SelfYield.Engage();SelfYield.TrackHelperRenderer(renderer.Id,renderer.StartTime.ToFileTimeUtc());
                    Check(SelfYield.Engaged,"Real parent SelfYield engaged");
                    Check(Native.CpuSetsMatch(self,original),"SelfYield wrote soft CPU Sets on its own process");
                    Check(Native.GetPriorityClass(self)==Native.BELOW_NORMAL_PRIORITY_CLASS,"SelfYield did not lower its own priority class");
                    using(SelfYield.OwnHelperScope(child.Handle))Check(Native.CpuSetsMatch(child.Handle,before),"Helper received soft CPU Sets");
                    Check(Native.CpuSetsMatch(child.Handle,before)&&SelfYield.PendingHelpersForTest==0,"Helper scope left a lease or changed the child");
                    string script=@"Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class PaviseChildSets { [DllImport(""kernel32.dll"")] private static extern bool GetProcessDefaultCpuSets(IntPtr h, uint[] ids, uint count, out uint needed); public static string Read() { uint needed; if (!GetProcessDefaultCpuSets(new IntPtr(-1),null,0,out needed) && needed==0) throw new Exception(""read""); uint[] ids=new uint[needed]; uint actual; if (needed>0 && (!GetProcessDefaultCpuSets(new IntPtr(-1),ids,needed,out actual)||actual!=needed)) throw new Exception(""readback""); return string.Join("","",ids); } }'; [Console]::WriteLine([PaviseChildSets]::Read())";
                    string stdout;Check(PsRunner.Run(script,"own-helper-read",10000,out stdout),"Actual production PowerShell command succeeds");
                    Check(stdout.Trim()==string.Join(",",original),"Production PowerShell helper inherited soft CPU Sets");Check(SelfYield.PendingHelpersForTest==0,"Success closes helper lease");
                    string failure;Check(!PsRunner.RunWithFailure("throw 'owned failure'","own-helper-failure",10000,out stdout,out failure)&&failure.Length>0,"PowerShell error unchanged");Check(SelfYield.PendingHelpersForTest==0,"Error closes helper lease");
                    Check(!PsRunner.RunWithFailure("Start-Sleep -Seconds 3","own-helper-timeout",200,out stdout,out failure)&&failure.Contains("200ms"),"PowerShell timeout unchanged");Check(SelfYield.PendingHelpersForTest==0,"Timeout closes exact helper handle");
                    child.StandardInput.WriteLine("exit");child.StandardInput.Flush();Check(child.WaitForExit(3000)&&child.ExitCode==0,"Owned child graceful exit");
                    renderer.StandardInput.WriteLine("exit");renderer.StandardInput.Flush();Check(renderer.WaitForExit(3000)&&renderer.ExitCode==0,"Owned renderer exits");
                }
            }
            finally{SelfYield.Release();Check(Native.CpuSetsMatch(self,original)&&Native.GetPriorityClass(self)==priority&&Native.QueryIoPriority(self)==io,"Own process CPU sets / class / I/O restored");}
        }
        private static int Main(string[] args)
        {
            Settings.UseTransientStoreForCurrentProcess();Logger.WritesEnabled=false;Lang.Init();
            using(var watchdog=new Timer(delegate{Environment.Exit(124);},null,30000,Timeout.Infinite))
            {
                if(args.Length==1&&args[0]=="--child"){Console.WriteLine("ready");return Console.ReadLine()=="exit"?0:2;}
                Fakes();NativeCases();Console.WriteLine("PASS own-helper yield: "+assertions+" assertions; fake helper scope rules, no soft CPU Sets on self/helper/PowerShell, priority and I/O lowered then restored, real PsRunner success/error/timeout");return 0;
            }
        }
    }
    internal static partial class SelfTests
    {public static bool TryHandleRuntimeMode(string[] args){throw new InvalidOperationException("Own-helper checks cannot launch normal runtime");}}
}
#endif
