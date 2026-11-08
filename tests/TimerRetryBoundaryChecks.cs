#if PAVISE_SELFTEST
using System;
using System.Reflection;
using System.Threading;
namespace PaviseApp
{
 internal static class TimerRetryBoundaryChecks
 {
  static readonly BindingFlags Fields=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
  static int checks;
  static void Check(bool ok,string why){checks++;if(!ok)throw new InvalidOperationException(why);}
  static void Fresh(bool keepSettings=false){
   if(!keepSettings)Settings.UseTransientStoreForCurrentProcess();Logger.WritesEnabled=false;
   foreach(FieldInfo field in typeof(ActionFailureStore).GetFields(Fields)){
    object value=field.GetValue(null);MethodInfo clear=value==null?null:value.GetType().GetMethod("Clear",Type.EmptyTypes);if(clear!=null)clear.Invoke(value,null);
   }
   typeof(ActionFailureStore).GetField("migrated",Fields).SetValue(null,false);
  }
  static string Intent(string action,string target){return ActionFailureStore.AttemptPrefix+ActionFailureStore.RecordKey(action,target).Substring(ActionFailureStore.Prefix.Length);}
  static void StoreCycles(){
   Fresh();string reason;
   Check(ActionFailureStore.TryBeginApply("a","t",out reason),"first admission");ActionFailureStore.CancelApply("a","t");
   Check(ActionFailureStore.IsBlocked("a","t")&&!ActionFailureStore.TryBeginApply("a","t",out reason),"cancel refunded admission");
   Check(ActionFailureStore.BeginAutomaticCycle("a","t","1\nA",3),"new stage");
   Check(ActionFailureStore.TryBeginApply("a","t",out reason),"failure1 admitted");ActionFailureStore.Trip("a","t","same reason");
   for(int i=0;i<10;i++){Check(ActionFailureStore.TryBeginApply("a","t",out reason),"healthy observation blocked");Check(ActionFailureStore.CompleteApply("a","t"),"successful observation completion");}
   Check(ActionFailureStore.TryBeginApply("a","t",out reason),"failure2 admitted");ActionFailureStore.Trip("a","t","same reason");
   Check(ActionFailureStore.TryBeginApply("a","t",out reason),"failure3 admitted");ActionFailureStore.Trip("a","t","same reason");
   Check(ActionFailureStore.IsBlocked("a","t"),"success refunded earlier failure budget");
   Check(!ActionFailureStore.BeginAutomaticCycle("a","t","1\nA",3),"poll reset same stage");
   Check(ActionFailureStore.BeginAutomaticCycle("a","t","1\nB",3),"material B stage");
   Check(ActionFailureStore.BeginAutomaticCycle("a","t","1\nA",3)&&ActionFailureStore.IsBlocked("a","t"),"A B A refilled stage");
   Check(ActionFailureStore.BeginAutomaticCycle("a","t","2\nA",3),"new real session");
   for(int i=0;i<3;i++){Check(ActionFailureStore.TryBeginApply("a","t",out reason),"new session attempt");ActionFailureStore.CancelApply("a","t");}
   Check(!ActionFailureStore.BeginAutomaticCycle("a","t","1\nB",3)&&ActionFailureStore.IsBlocked("a","t"),"stale worker refilled session");
   Check(!ActionFailureStore.BeginAutomaticCycle("a","t","2\nA",3)&&ActionFailureStore.IsBlocked("a","t"),"stale then new refilled budget");
   Console.WriteLine("PASS admission accounting, successful observation, cancellation, A-B-A and stale session");
  }
  static void HistoryAndIntent(){
   foreach(bool beginFirst in new[]{false,true}){
    Fresh();Settings.SaveStr("PowerPlanFailStreak","7");
    if(beginFirst)ActionFailureStore.BeginAutomaticCycle("PowerPlanOn","system","1\nA",3);
    Check(!ActionFailureStore.IsBlocked("PowerPlanOn","system"),"legacy history charged current stage");
    if(!beginFirst)ActionFailureStore.BeginAutomaticCycle("PowerPlanOn","system","1\nA",3);
    string reason;for(int i=0;i<3;i++){Check(ActionFailureStore.TryBeginApply("PowerPlanOn","system",out reason),"legacy consumed admitted allowance");ActionFailureStore.Trip("PowerPlanOn","system","same reason");}
    Check(ActionFailureStore.IsBlocked("PowerPlanOn","system"),"same reason failures not bounded");
   }
   Fresh();ActionFailureStore.Trip("history","system","old");Fresh(true);
   Check(ActionFailureStore.IsIsolated("history","system")&&!ActionFailureStore.IsBlocked("history","system"),"valid historical fault permanently blocked");
   Fresh();string why;Check(ActionFailureStore.TryBeginApply("crash","system",out why),"crash admission");string before=Settings.LoadStr(Intent("crash","system"),"");Fresh(true);
   ActionFailureStore.BeginAutomaticCycle("crash","system","2\nA");
   Check(ActionFailureStore.IsBlocked("crash","system")&&Settings.LoadStr(Intent("crash","system"),"")==before,"unconfirmed intent discarded or stage bypassed it");
   ActionFailureStore.CancelApply("crash","system");Check(!ActionFailureStore.IsBlocked("crash","system"),"verified recovery did not settle intent");
   Fresh();Check(ActionFailureStore.TryBeginApply("completion","system",out why),"completion admission");Settings.TryRemove(Intent("completion","system"),true);
   Check(!ActionFailureStore.CompleteApply("completion","system"),"missing intent accepted completion");Fresh(true);
   Check(ActionFailureStore.IsBlocked("completion","system")&&Settings.LoadStr(Intent("completion","system"),"").Length>0,"completion failure lost durable recovery debt");
   Fresh();ActionFailureStore.BeginAutomaticCycle("unticketed","system","1\nA",3);ActionFailureStore.Trip("unticketed","system","outside admission");
   Check(ActionFailureStore.IsBlocked("unticketed","system"),"unticketed failure left unmetered automatic path");
   Fresh();ActionFailureStore.Trip("shared","*","old");Fresh(true);
   Check(ActionFailureStore.TryBeginApply("shared","a",out why),"wildcard historical admission");
   Check(!ActionFailureStore.TryBeginApply("shared","b",out why),"wildcard admitted overlapping targets");
   Check(!ActionFailureStore.BeginAutomaticCycle("shared","b","1\nB"),"cycle bypassed wildcard owner");
   ActionFailureStore.CancelApply("shared","a");Check(ActionFailureStore.TryBeginApply("shared","b",out why),"wildcard owner never released");ActionFailureStore.CancelApply("shared","b");
   Console.WriteLine("PASS passive legacy import order, history, durable intent and shared wildcard owner");
  }
  static void RecoveryCycles(){
   var budget=new AutomaticRecoveryBudget();Check(budget.BeginCycle("1\nA"),"initial recovery stage");
   budget.Record(false);budget.Record(true);Check(budget.FailureCount==1,"successful recovery refunded earlier failure");
   budget.Record(false);budget.Record(false);Check(!budget.CanAttempt,"recovery allowance unbounded");
   budget.BeginCycle("1\nB");budget.Record(false);budget.BeginCycle("1\nA");Check(!budget.CanAttempt,"recovery A-B-A reset");
   Check(budget.BeginCycle("2\nA"),"new recovery session");budget.Record(false);budget.Record(false);budget.Record(false);
   Check(!budget.BeginCycle("1\nB")&&!budget.BeginCycle("2\nA")&&!budget.CanAttempt,"stale recovery reset");
  }
  static readonly string BootId=BootSessionIdentity.Create(new Guid("492d4395-a022-451e-a8b3-8481dff82763"),2);
  sealed class Fixture{
   internal int Boots,Reads,Enables,Restores;internal bool On,Owned,RestoreFails;internal string Boot=BootId;internal Exception Error;
   internal TimerModeController Controller(){return new TimerModeController{
    Boot=delegate{Boots++;return Boot;},TickOn=delegate{return true;},TickOwned=delegate{return false;},TickEnable=delegate{return true;},TickRestore=delegate{return true;},
    GlobalOn=delegate{Reads++;return On;},GlobalOwned=delegate{return Owned;},GlobalEnable=delegate{Enables++;Owned=true;if(Error!=null)throw Error;On=true;return true;},
    GlobalRestore=delegate{Restores++;if(RestoreFails)return false;Owned=On=false;return true;}
   };}
  }
  static long Due(TimerModeController timer){object schedule=typeof(TimerModeController).GetField("globalSchedule",Fields).GetValue(timer);return (long)schedule.GetType().GetField("NextCheck",Fields).GetValue(schedule);}
  static void TimerStages(){
   Fresh();var f=new Fixture{Boot=null};var timer=f.Controller();timer.BeginAutomaticCycle("1\ntimer");timer.Sync(false,true,22621,1000);
   Check(f.Boots==1&&f.Reads==1&&f.Enables==0,"unknown boot mutation");
   for(int i=0;i<100;i++)timer.Sync(false,true,22621,Due(timer)+TimeSpan.FromHours(i+1).Ticks);
   Check(f.Reads==1&&f.Boots==1,"unknown boot repeated read or Boot after spent phase");
   int boots=f.Boots;for(int i=0;i<100;i++)timer.Sync(false,true,22621,Due(timer)+i);Check(f.Boots==boots,"spent phase kept boot polling");
   timer.BeginAutomaticCycle("1\ntimer");timer.Sync(false,true,22621,Due(timer)+1);Check(f.Reads==1,"same session reset timer");
   timer.BeginAutomaticCycle("2\ntimer");timer.Sync(false,true,22621,Due(timer)+1);Check(f.Reads==2,"new real session did not open timer phase");
   foreach(Exception error in new Exception[]{new NotSupportedException(),new OperationCanceledException()}){
    Fresh();f=new Fixture{Error=error};timer=f.Controller();timer.Sync(false,true,22621,1000);
    for(int i=0;i<100;i++)timer.Sync(false,true,22621,Due(timer)+TimeSpan.FromHours(i+1).Ticks);
    Check(f.Enables==1&&f.Restores==1&&!f.Owned,"skipped attempt repeated or lost owner");
   }
   Fresh();f=new Fixture{Error=new NotSupportedException(),RestoreFails=true};timer=f.Controller();timer.BeginAutomaticCycle("1\ntimer");timer.Sync(false,true,22621,1000);
   Check(f.Restores==1&&f.Owned&&timer.GlobalState=="restore-failed","first recovery responsibility");long firstDue=Due(timer);
   timer.Sync(false,false,22621,firstDue-1);Check(f.Restores==1,"off bypassed recovery deadline");
   timer.Sync(false,false,22621,firstDue+1);Check(f.Restores==2,"second recovery absent");long secondDue=Due(timer);
   Check(secondDue>=firstDue+TimeSpan.FromSeconds(60).Ticks,"recovery backoff missing");timer.Sync(false,false,22621,secondDue+1);
   for(int i=0;i<100;i++){timer.Sync(false,i%2==0,22621,Due(timer)+TimeSpan.FromHours(i+1).Ticks);timer.BeginAutomaticCycle("1\ntimer");}
   Check(f.Restores==3&&f.Owned&&timer.GlobalState=="restore-failed","restore budget lost owner or was refilled");
   timer.BeginAutomaticCycle("2\ntimer");f.RestoreFails=false;timer.Sync(false,false,22621,Due(timer)+1);Check(f.Restores==4&&!f.Owned,"new session recovery failed");
   Fresh();Settings.SaveStr("AutoGlobalTimerBoot","on|"+BootSessionIdentity.Create(new Guid("492d4395-a022-451e-a8b3-8481dff82763"),1));f=new Fixture{On=true};timer=f.Controller();for(int i=0;i<20;i++)timer.Sync(false,true,22621,i==0?1000:Due(timer)+1);Check(f.Reads==40&&f.Enables==0,"normal healthy observation was capped");
   Console.WriteLine("PASS unknown boot/skip finite, 3 recovery failures, owner retention and healthy monitoring");
  }
  static void LeaseStages(){
   Fresh();var lease=new TimerResolutionLease();int acquire=0,release=0;bool failRelease=true;
   lease.Set=delegate(uint desired,bool on,out uint actual){actual=5000;if(on){acquire++;return 0;}release++;return failRelease?-1:0;};
   lease.Query=delegate(out uint c,out uint f,out uint a){c=156250;f=a=5000;return 0;};
   lease.BeginAutomaticCycle("1\ntimer");Check(lease.Update(true,1),"lease acquire");
   for(int i=0;i<30;i++)lease.Update(false,TimeSpan.FromHours(i+1).Ticks);
   Check(release==3&&lease.HasResidue&&lease.State=="restore-failed","lease recovery unbounded or ownership discarded");
   lease.BeginAutomaticCycle("1\ntimer");lease.Update(false,TimeSpan.FromDays(10).Ticks);Check(release==3,"same phase reset lease recovery");
   lease.BeginAutomaticCycle("2\ntimer");failRelease=false;Check(lease.Update(false,TimeSpan.FromDays(11).Ticks)&&release==4&&!lease.HasResidue,"new phase lease recovery");
   Check(acquire==1,"recovery reacquired lease");
   Check(lease.Update(true,TimeSpan.FromDays(12).Ticks),"new clean ownership not admitted");failRelease=true;int settledReleases=release;
   for(int i=0;i<10;i++)lease.Update(false,TimeSpan.FromDays(13+i).Ticks);
   Check(release==settledReleases+3&&lease.HasResidue,"new ownership reused exhausted old recovery episode");
  }
  static void RecoveryRouteReports(){
   Fresh();var f=new Fixture{Error=new NotSupportedException(),RestoreFails=true};var timer=f.Controller();long now=DateTime.UtcNow.Ticks;
   using(var trace=new RecoveryAttemptTrace()){timer.Sync(false,true,22621,now);Check(trace.Reported&&trace.Next==Due(timer),"timer actual failure did not report its deadline");}
   using(var trace=new RecoveryAttemptTrace()){timer.Sync(false,false,22621,now);Check(trace.Reported&&trace.Next==Due(timer)&&f.Restores==1,"timer cooldown was not reported");}
   timer.Sync(false,false,22621,Due(timer)+1);using(var trace=new RecoveryAttemptTrace()){timer.Sync(false,false,22621,Due(timer)+1);Check(trace.Reported&&trace.Next==long.MaxValue&&f.Restores==3,"timer exhaustion not reported");}
   Fresh();var lease=new TimerResolutionLease();int releases=0;lease.Set=delegate(uint desired,bool on,out uint actual){actual=5000;if(on)return 0;releases++;return -1;};lease.Query=delegate(out uint c,out uint finest,out uint actual){c=156250;finest=actual=5000;return 0;};Check(lease.Update(true,now),"route fixture acquire");
   var route=new RecoveryRouteSet(1,()=>now);Check(!route.Capture("timer",()=>lease.Update(false,now))&&releases==1,"route fixture first failure");long due=route.Next;Check(due>now&&due<long.MaxValue,"lease did not report finite own deadline");
   for(int i=0;i<1000;i++)route.RetryDue();Check(releases==1&&route.Next==due,"outer polling bypassed lease deadline");now=due+1;route.RetryDue();Check(releases==2&&route.Next>now,"outer route suppressed legitimate second owner attempt");now=route.Next+1;route.RetryDue();Check(releases==3&&route.Next==long.MaxValue&&lease.HasResidue,"route did not retain exhausted lease");
  }
  static int Main(){try{RecoveryRouteReports();StoreCycles();HistoryAndIntent();RecoveryCycles();TimerStages();LeaseStages();Console.WriteLine("PASS automatic timer/store boundaries assertions="+checks+" transient_settings=true native_mutations=0 product_startup=0");return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
 }
}
#endif
