using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
namespace PaviseApp {
internal static class Logger { internal static int Logs; internal static void Log(string s) { Logs++; } internal static void Warn(string s) { Logs++; } }
internal static class PowerPlan { internal static string Selected = "A"; internal static long Session; internal static void BeginAutomaticSession(long session) { Session = session; } internal static string AutomaticPolicySignature(bool aggressive) { return Selected + ":" + aggressive; } }
internal static class ActionFailureStore {
    internal static int Admissions; internal static bool Allow=true;
    internal static bool BeginAutomaticCycle(string a,string t,string c,int limit) { return true; }
    internal static bool TryBeginApply(string a,string t,out string reason) { reason="fixture"; if(!Allow)return false; Admissions++; return true; }
}
internal partial class GameMode {
    private RecoveryRouteSet sessionRecoveryRoutes, recoveryCaptureRoutes; private bool sessionRecoveryCaptured;
    private bool CapturingSessionRecovery { get { return false; } }
    private bool RunSessionRecoveryRoute(string key,Func<bool> work,string[] dependencies) { throw new Exception("Capture must use actual aggregate fixture"); }
    private static string[] EnvironmentRecoveryDependencies(string key) { return null; }
    private void SessionEnvironmentNeutral(string key) { throw new Exception("Capture must use actual aggregate fixture"); }
    private readonly object sync=new object(); private long gameSessionStarts;
    private int residueRetries; private long residueNextTryTicks;
    private readonly Dictionary<string,long> envNextAttempt=new Dictionary<string,long>();
    private readonly Dictionary<string,int> envFailures=new Dictionary<string,int>();
    private readonly HashSet<string> envRestorePending=new HashSet<string>();
    private static bool RunEnvironmentAction(Func<bool> action,out bool skipped,out string reason) {
        skipped=false; reason="fixture returned false";
        try { return action(); } catch(Exception e) {reason=e.GetType().Name; return false;}
    }
    private static int checks;
    private static void Check(bool ok,string message) {checks++;if(!ok)throw new Exception(message);}
    private static void PowerBudget() {
        var m=new GameMode();m.gameSessionStarts=10; string reason;
        PowerPlan.Selected="A"; var a=m.PowerAttemptPhase(2);
        Check(PowerPlan.Session==10,"power drift owner did not receive the real game serial");
        Check(m.BeginPowerAttempt(2,out reason),"first power apply blocked");
        m.CompletePowerAttempt(false,"first");
        long first=a.Next-DateTime.UtcNow.Ticks;
        Check(first>TimeSpan.FromSeconds(4).Ticks && first<=TimeSpan.FromSeconds(5).Ticks,"first failure completion cooldown");
        Check(!m.BeginPowerAttempt(2,out reason),"cooldown bypass");
        a.Next=0;Check(m.BeginPowerAttempt(2,out reason),"second attempt missing");m.CompletePowerAttempt(false,"second");
        Check(a.Next-DateTime.UtcNow.Ticks>TimeSpan.FromSeconds(29).Ticks,"second cooldown must be 30s");
        a.Next=0;Check(m.BeginPowerAttempt(2,out reason),"third attempt missing");m.CompletePowerAttempt(false,"third");
        int before=ActionFailureStore.Admissions, logs=Logger.Logs;
        for(int i=0;i<1000;i++)Check(!m.BeginPowerAttempt(2,out reason),"exhausted apply reentered");
        Check(ActionFailureStore.Admissions==before && Logger.Logs==logs,"exhausted polling touched admission/log");
        PowerPlan.Selected="B";Check(m.BeginPowerAttempt(2,out reason),"material plan B blocked");m.CompletePowerAttempt(false,"B");
        PowerPlan.Selected="A";Check(!m.BeginPowerAttempt(2,out reason),"A-B-A refilled A");
        m.gameSessionStarts=11;Check(m.BeginPowerAttempt(2,out reason),"real new session did not get budget");m.CompletePowerAttempt(false,"access denied",true);
        Check(PowerPlan.Session==11,"new game serial did not reach the power drift owner");
        Check(!m.BeginPowerAttempt(2,out reason),"permanent error retried");
        m.gameSessionStarts=12;
        for(int i=0;i<10;i++) {Check(m.BeginPowerAttempt(2,out reason),"successful independent maintenance capped");m.CompletePowerAttempt(true,"verified");}
        Console.WriteLine("PASS power 3 total/5s/30s, 1000 polls, A-B-A, new session, permanent stop");
    }
    private static void RecoveryBudget() {
        var m=new GameMode(); m.gameSessionStarts=20; int calls=0; Func<bool> fail=delegate {calls++;return false;};
        for(int i=0;i<3;i++) {Check(!m.RunAutomaticRestore("powerplan",fail),"restore failure succeeded");m.automaticRestores[m.AutomaticPhaseKey("powerplan")].Next=0;}
        for(int i=0;i<1000;i++)Check(!m.RunAutomaticRestore("powerplan",fail),"exhausted restore reentered");
        Check(calls==3 && m.envRestorePending.Contains("powerplan"),"budget discarded debt or repeated write");
        m.SyncAutomaticRetrySession();Check(!m.RunAutomaticRestore("powerplan",fail),"ordinary lifecycle sync refilled old budget");
        m.gameSessionStarts=21;m.SyncAutomaticRetrySession();
        Check(m.RunAutomaticRestore("powerplan",delegate {calls++;return true;}),"new phase restoration blocked");
        Check(!m.envRestorePending.Contains("powerplan"),"verified restore did not release debt");
        for(int i=0;i<100;i++)Check(m.RunAutomaticRestore("healthy",delegate {return true;}),"healthy/no-debt observation was capped");
        var entered=new ManualResetEvent(false);var finish=new ManualResetEvent(false);
        Task<bool> task=Task.Factory.StartNew(delegate {return m.RunAutomaticRestore("single",delegate {entered.Set();finish.WaitOne();return false;});});
        Check(entered.WaitOne(1000),"owner did not start");int duplicates=0;
        Check(!m.RunAutomaticRestore("single",delegate {duplicates++;return true;}),"second recovery owner admitted");
        finish.Set();Check(!task.Result && duplicates==0,"concurrent restore overlapped");
        var phase=m.automaticRestores[m.AutomaticPhaseKey("single")];
        Check(phase.Failures==1 && phase.Next>DateTime.UtcNow.Ticks,"completion cooldown/one failure lost");
        phase.Next=0;Check(!m.RunAutomaticRestore("single",delegate {throw new InvalidOperationException();}),"exception succeeded");
        Check(!phase.InFlight && phase.Failures==2,"exception lost owner or failure count");
        Console.WriteLine("PASS recovery 3 failures, no reset by polling, debt retained, true new phase, healthy observations, single owner, exceptions");
    }
    private static int Main() {try {PowerBudget();RecoveryBudget();Console.WriteLine("PASS checks="+checks+" native_writes=0 product_startup=false");return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}}
