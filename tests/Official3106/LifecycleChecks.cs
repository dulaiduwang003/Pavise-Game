using System;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
namespace PaviseApp
{
    internal static class OfficialLifecycleChecks
    {
        private static int checks;
        private static readonly BindingFlags PrivateInstance=BindingFlags.NonPublic|BindingFlags.Instance;
        private static readonly BindingFlags PrivateStatic=BindingFlags.NonPublic|BindingFlags.Static;
        private static void Check(bool ok,string detail) { checks++; if(!ok)throw new Exception(detail); }
        private static GameMode NewGame()
        {
            var game=(GameMode)FormatterServices.GetUninitializedObject(typeof(GameMode));
            typeof(GameMode).GetField("sync",PrivateInstance).SetValue(game,new object());
            typeof(GameMode).GetField("powerApplyGate",PrivateInstance).SetValue(game,new object());
            typeof(GameMode).GetField("profileStore",PrivateInstance).SetValue(game,FormatterServices.GetUninitializedObject(typeof(GameProfileStore)));
            typeof(GameMode).GetField("enabled",PrivateInstance).SetValue(game,true);
            typeof(GameMode).GetField("active",PrivateInstance).SetValue(game,true);
            return game;
        }
        private static void Sync(GameMode game) { typeof(GameMode).GetMethod("SyncLaboratory",PrivateInstance).Invoke(game,null); }
        private static LaboratorySession Session(GameMode game) { return (LaboratorySession)typeof(GameMode).GetField("laboratory",PrivateInstance).GetValue(game); }
        private static void CloseBeforeFirstPublication()
        {
            Settings.UseTransientStoreForCurrentProcess();
            var game=NewGame();
            Check(game.CloseLaboratory(100),"clean never-created session did not close");
            Sync(game);
            Check(Session(game)==null,"game scan created a new lab writer after shutdown latched");
            foreach(string receipt in new[]{LaboratoryPower.ReceiptKey,LaboratoryGpu.PowerReceipt,LaboratoryGpu.ClockReceipt})
            {
                Settings.UseTransientStoreForCurrentProcess(); Settings.SaveStr(receipt,"unresolved");
                game=NewGame();
                Check(!game.CloseLaboratory(100),"null session hid durable recovery debt: "+receipt);
                Sync(game); Check(Session(game)==null,"debt recovery was queued after session shutdown");
            }
            Settings.UseTransientStoreForCurrentProcess();
        }
        private static void CloseDuringSelectionRead()
        {
            Settings.UseTransientStoreForCurrentProcess();
            Settings.Save(LaboratoryGpu.PowerKey,true);
            var game=NewGame(); bool callback=false;
            Settings.BeforeRead=delegate(string key) {
                if(callback)return; callback=true;
                Check(game.CloseLaboratory(100),"session created before selection could not close");
            };
            try { Sync(game); }
            finally { Settings.BeforeRead=null; }
            var session=Session(game);
            Check(callback&&session!=null,"selection-read race was not exercised");
            Check(session.IsRestored&&session.Close(100),"captured intent rearmed the closing session");
            Check(typeof(LaboratorySession).GetField("worker",PrivateInstance).GetValue(session)==null,"closed selection started an action worker");
            Settings.UseTransientStoreForCurrentProcess();
        }
        private static void CloseInFlight()
        {
            Settings.UseTransientStoreForCurrentProcess();
            var game=NewGame(); var entered=new ManualResetEvent(false); var release=new ManualResetEvent(false);
            int applies=0,restores=0;
            var session=new LaboratorySession(new object());
            session.ApplyForTest=delegate {Interlocked.Increment(ref applies);entered.Set();release.WaitOne();};
            session.RestoreForTest=delegate {Interlocked.Increment(ref restores);return true;};
            typeof(GameMode).GetField("laboratory",PrivateInstance).SetValue(game,session);
            session.Queue(new LaboratorySelection{Power=true});
            Check(entered.WaitOne(3000),"in-flight apply did not start");
            Check(!game.CloseLaboratory(25),"shutdown misreported an active writer as stopped");
            Sync(game); release.Set();
            Check(game.CloseLaboratory(3000)&&applies==1&&restores==1,"closed GameMode reapplied or skipped restoration");
        }
        private static void SetPower(string name,object value) { typeof(PowerPlan).GetField(name,PrivateStatic).SetValue(null,value); }
        private static void PowerOwnerOrdering()
        {
            Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled=false;
            Check(PowerPlan.SelectPlan(PowerPlan.ManagedChoice),"could not select managed plan in memory");
            SetPower("active",true); SetPower("resolved",true); SetPower("targetOwned",true);
            SetPower("target",OfficialPlatformMocks.Plan); SetPower("saved",new Guid("20000000-0000-0000-0000-000000000002")); SetPower("tuneState",1);
            Settings.SaveStr(LaboratoryPower.ReceiptKey,"test-debt"); int generation=Settings.MutationGeneration, restores=0;
            PowerPlan.LaboratoryRestoreForTest=delegate {restores++;return false;};
            for(int i=0;i<30;i++) { Check(PowerPlan.EnforceSettled(true),"settled audit disturbed lab policy"); Check(PowerPlan.Enforce(true),"steady enforcement disturbed lab policy"); }
            Check(restores==0&&Settings.MutationGeneration==generation,"settled audits restored lab debt or rewrote policy");
            Check(!PowerPlan.Restore(),"ordinary restore released owner after lab restore failed");
            Check(!PowerPlan.RemoveManagedPlan(),"managed plan deletion ignored lab debt");
            Check(!PowerPlan.RemoveCreatedPlan(),"created plan deletion ignored lab debt");
            Check((Guid)typeof(PowerPlan).GetMethod("EnsureManagedPolicyPlan",PrivateStatic).Invoke(null,null)==Guid.Empty,"managed replacement ignored lab debt");
            Check(PowerPlan.DeleteManagedPlansByName()==0,"bulk cleanup ignored lab debt");
            Check(restores==5&&Settings.MutationGeneration==generation,"blocked recovery mutated settings or skipped owner guards");
            Check((Guid)typeof(PowerPlan).GetField("saved",PrivateStatic).GetValue(null)!=Guid.Empty,"blocked restore discarded prior plan");
            PowerPlan.LaboratoryRestoreForTest=null;
        }
        private static int Main()
        {
            try { Logger.WritesEnabled=false; CloseBeforeFirstPublication(); CloseDuringSelectionRead(); CloseInFlight(); PowerOwnerOrdering(); Console.WriteLine("PASS official lifecycle/power-owner assertions="+checks+" production_game_mode=true native_writes=false application_started=false"); return 0; }
            catch(Exception error) {Console.Error.WriteLine(error);return 1;}
        }
    }
}
