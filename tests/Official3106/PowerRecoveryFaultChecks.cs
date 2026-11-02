#if PAVISE_POWER_RECOVERY_FAULTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;

namespace PaviseApp
{
    internal static class PowerRecoveryFixture
    {
        internal static readonly Guid Plan=new Guid("eab91235-4b2b-43f4-9c33-68774103efc5");
        internal static readonly Guid Other=new Guid("ac4fbc1c-a495-42a4-ab8c-4bd0eef433ee");
        internal static readonly Dictionary<string,uint> Values=new Dictionary<string,uint>();
        internal static readonly List<string> Writes=new List<string>(),Trace=new List<string>(),DurableWrites=new List<string>();
        internal static readonly Queue<Guid?> CurrentReplies=new Queue<Guid?>();
        internal static Guid? Active;
        internal static string FailWrite,FailRead,LieWrite;
        internal static int SetFailures,SetCalls,ForbiddenCalls,TotalForbiddenCalls,RestoreCalls;
        internal static bool RestoreResult=true;
        internal static string Key(Guid scheme,Guid sub,Guid setting,bool ac)
        {return scheme+"/"+sub+"/"+setting+"/"+(ac?"AC":"DC");}
        internal static void Reset()
        {
            Values.Clear();Writes.Clear();Trace.Clear();DurableWrites.Clear();CurrentReplies.Clear();Active=Plan;
            FailWrite=FailRead=LieWrite=null;SetFailures=SetCalls=ForbiddenCalls=RestoreCalls=0;RestoreResult=true;
            Settings.UseTransientStoreForCurrentProcess();
            typeof(PowerPlan).GetField("cpuIdleReceipt",PowerRecoveryFaultChecks.Static).SetValue(null,null);
            foreach(var field in typeof(ActionFailureStore).GetFields(PowerRecoveryFaultChecks.Static))
            {
                object value=field.GetValue(null);if(value==null)continue;
                var clear=value.GetType().GetMethod("Clear",Type.EmptyTypes);if(clear!=null)clear.Invoke(value,null);
            }
            typeof(ActionFailureStore).GetField("migrated",PowerRecoveryFaultChecks.Static).SetValue(null,false);
        }
        internal static uint Read(Guid scheme,Guid sub,Guid setting,bool ac,out uint value)
        {
            string key=Key(scheme,sub,setting,ac);value=0;
            if(key==FailRead||!Values.TryGetValue(key,out value)){Trace.Add("read-failed "+key);return 2;}
            return 0;
        }
        internal static uint Write(Guid scheme,Guid sub,Guid setting,bool ac,uint value)
        {
            string key=Key(scheme,sub,setting,ac);Trace.Add("write "+key+"="+value+(key==FailWrite?" FAIL":""));
            if(key==FailWrite)return 5;
            Writes.Add(key+"="+value);if(key!=LieWrite)Values[key]=value;return 0;
        }
        internal static Guid? Current()
        {if(CurrentReplies.Count>0)Active=CurrentReplies.Dequeue();return Active;}
        internal static uint Set(Guid scheme)
        {SetCalls++;Trace.Add("activate-memory "+scheme);if(SetFailures>0){SetFailures--;return 5;}Active=scheme;return 0;}
        internal static Exception Forbidden()
        {ForbiddenCalls++;TotalForbiddenCalls++;Trace.Add("forbidden "+Environment.StackTrace);return new InvalidOperationException("Unexpected native power endpoint");}
        internal static bool Restore()
        {RestoreCalls++;Trace.Add("restore-memory="+RestoreResult);return RestoreResult;}
    }

    // A test-only environment backend, with the production Settings API shape.
    // PowerPlan, ActionFailureStore and GameMode recovery/admission bodies stay real.
    internal static class Settings
    {
        internal const string Key="IN_MEMORY_ONLY",GpuIdentityReceiptKey="MonitoringGpuIdentityTraceV1",GpuIdentitySessionPattern="unused";
        private static readonly Dictionary<string,object> data=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
        internal static int MutationGeneration;
        internal static Func<string,string,bool> RejectStringWrite;
        internal static Action<string,string> AfterStringWrite;
        internal static string FailReadName;
        internal static int FailReadCount;
        internal static void UseTransientStoreForCurrentProcess()
        {data.Clear();MutationGeneration++;RejectStringWrite=null;AfterStringWrite=null;FailReadName=null;FailReadCount=0;}
        internal static void SuspendWritesForReset(){}
        internal static void NoteExternalMutation(){MutationGeneration++;}
        internal static bool ClearUserPreferencesForReset(Predicate<string> selected,out int removed,out string failure)
        {var keys=data.Keys.Where(k=>selected(k)).ToArray();foreach(var key in keys)data.Remove(key);removed=keys.Length;failure=null;return true;}
        internal static bool TryLoadCached(string name,out bool value){return TryLoadStoredBool(name,out value);}
        public static bool LoadCached(string name,bool fallback){return Load(name,fallback);}
        internal static bool TryLoadStoredBool(string name,out bool value)
        {object raw;bool exists=data.TryGetValue(name,out raw)&&raw is bool;value=exists&&(bool)raw;return exists;}
        internal static bool TryLoadBool(string name,bool fallback,out bool value){value=Load(name,fallback);return true;}
        public static bool Load(string name,bool fallback){bool value;return TryLoadStoredBool(name,out value)?value:fallback;}
        public static bool Save(string name,bool value){data[name]=value;MutationGeneration++;return true;}
        public static void Remove(string name){data.Remove(name);MutationGeneration++;}
        internal static bool TryRemove(string name,bool durable){Remove(name);return true;}
        internal static bool TryLoadStoredNames(string prefix,out string[] names)
        {names=data.Keys.Where(k=>k.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)).ToArray();return true;}
        internal static bool TryLoadStr(string name,out string value)
        {value=LoadStr(name,"");if(name==FailReadName&&FailReadCount>0){FailReadCount--;return false;}return true;}
        public static string LoadStr(string name,string fallback)
        {object value;return data.TryGetValue(name,out value)?value as string??fallback:fallback;}
        public static bool SaveStr(string name,string value)
        {
            bool reject=RejectStringWrite!=null&&RejectStringWrite(name,value);
            PowerRecoveryFixture.Trace.Add("settings "+name+"="+value+(reject?" FAIL":""));
            if(reject)return false;data[name]=value;MutationGeneration++;
            var after=AfterStringWrite;if(after!=null)after(name,value);return true;
        }
        internal static bool SaveStrDurable(string name,string value)
        {PowerRecoveryFixture.DurableWrites.Add(name+"="+value);PowerRecoveryFixture.Trace.Add("durable "+name+"="+value);return SaveStr(name,value);}
        internal static bool SaveDurable(string name,bool value){return Save(name,value);}
        internal static bool SaveGpuIdentityReceipt(string value){return SaveStr(GpuIdentityReceiptKey,value);}
    }

    internal static class PowerRecoveryFaultChecks
    {
        internal const BindingFlags Static=BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public;
        private const BindingFlags Instance=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
        private static int checks,cpuCases,parkCases,admissionCases;
        private static readonly List<string> traces=new List<string>();
        private static void Check(bool ok,string message){checks++;if(!ok)throw new InvalidOperationException(message);}
        private static object Call(object target,string name,params object[] args)
        {
            try{return (target is Type?(Type)target:target.GetType()).GetMethod(name,target is Type?Static:Instance).Invoke(target is Type?null:target,args);}
            catch(TargetInvocationException error){throw error.InnerException;}
        }
        private static void Field(object target,string name,object value)
        {(target is Type?(Type)target:target.GetType()).GetField(name,target is Type?Static:Instance).SetValue(target is Type?null:target,value);}
        private static T Field<T>(object target,string name)
        {return (T)(target is Type?(Type)target:target.GetType()).GetField(name,target is Type?Static:Instance).GetValue(target is Type?null:target);}
        private static Guid Id(string name){return Field<Guid>(typeof(PowerPlan),name);}
        private static string Key(string setting,bool ac){return PowerRecoveryFixture.Key(PowerRecoveryFixture.Plan,Id("SubProcessor"),Id(setting),ac);}
        private static void SetValue(string setting,bool ac,uint value){PowerRecoveryFixture.Values[Key(setting,ac)]=value;}
        private static uint Value(string setting,bool ac){return PowerRecoveryFixture.Values[Key(setting,ac)];}
        private static void Trace(string label)
        {traces.Add("CASE "+label);traces.AddRange(PowerRecoveryFixture.Trace);}
        private static string Idle(string phase){return "2|"+PowerRecoveryFixture.Plan+"|0|"+phase;}
        private static string Idle3(int mask){return "3|"+PowerRecoveryFixture.Plan+"|0|R|"+mask;}
        private static void Cpu(uint ac,uint dc,string receipt)
        {cpuCases++;PowerRecoveryFixture.Reset();SetValue("IdleDisableSet",true,ac);SetValue("IdleDisableSet",false,dc);Settings.SaveStr("CpuIdleStateV1",receipt);}
        private static void ReloadCpu(){Field(typeof(PowerPlan),"cpuIdleReceipt",null);}
        private static void CpuRecovered()
        {Check(Value("IdleDisableSet",true)==0&&Value("IdleDisableSet",false)==0,"CPU idle original0/0 was not restored");Check(!PowerPlan.LegacyCpuIdleHasResidue&&Settings.LoadStr("CpuIdleStateV1","")=="","CPU idle recovery record was not cleared");}
        private static void CpuPartialFaults()
        {
            Cpu(1,1,Idle("O"));Check(PowerPlan.RestoreLegacyCpuIdle(),"Normal owned idle recovery failed");CpuRecovered();
            Check(PowerRecoveryFixture.DurableWrites.Contains("CpuIdleStateV1="+Idle3(3)),"CPU write authority was not saved through the durable settings API");
            foreach(bool restart in new[]{false,true})
            {
                Cpu(1,1,Idle("O"));PowerRecoveryFixture.FailWrite=Key("IdleDisableSet",false);
                Check(!PowerPlan.RestoreLegacyCpuIdle()&&Value("IdleDisableSet",true)==0&&Value("IdleDisableSet",false)==1,"DC failure was hidden or AC progress lost");
                Check(Settings.LoadStr("CpuIdleStateV1","")==Idle3(2)&&PowerPlan.LegacyCpuIdleHasResidue,"DC failure did not retain durable per-source ownership");
                int writes=PowerRecoveryFixture.Writes.Count;PowerRecoveryFixture.FailWrite=null;if(restart)ReloadCpu();
                Check(PowerPlan.RestoreLegacyCpuIdle()&&PowerRecoveryFixture.Writes.Count==writes+1,"Retry rewrote AC or skipped pending DC");CpuRecovered();Trace("idle DC failure restart="+restart);
            }
            foreach(uint external in new[]{1u,2u})
            {
                Cpu(external,1,Idle3(2));Check(PowerPlan.RestoreLegacyCpuIdle()&&Value("IdleDisableSet",true)==external&&Value("IdleDisableSet",false)==0,"Released AC ownership was reacquired after external change");
                Check(PowerRecoveryFixture.Writes.Count==1&&PowerRecoveryFixture.SetCalls==0&&!PowerPlan.LegacyCpuIdleHasResidue,"External AC policy was reapplied or debt remained");
            }
            Cpu(0,2,Idle3(2));Check(PowerPlan.RestoreLegacyCpuIdle()&&Value("IdleDisableSet",false)==2&&PowerRecoveryFixture.Writes.Count==0,"Pending side external value was overwritten");
            foreach(string phase in new[]{"P","R","O"})foreach(uint ac in new[]{0u,1u})
            {
                if(phase=="O"&&ac==1)continue;
                string receipt=Idle(phase);Cpu(ac,1,receipt);
                Check(!PowerPlan.RestoreLegacyCpuIdle()&&PowerRecoveryFixture.Writes.Count==0&&Settings.LoadStr("CpuIdleStateV1","")==receipt,"Ambiguous legacy receipt claimed an unproven value");
            }
            Cpu(0,0,Idle("R"));Check(PowerPlan.RestoreLegacyCpuIdle(),"Already restored legacy debt could not flush");CpuRecovered();
            foreach(bool restart in new[]{false,true})
            {
                Cpu(1,1,Idle("O"));PowerRecoveryFixture.SetFailures=1;
                Check(!PowerPlan.RestoreLegacyCpuIdle()&&Settings.LoadStr("CpuIdleStateV1","")==Idle3(0),"Set failure did not retain completed-values pending flush");
                int writes=PowerRecoveryFixture.Writes.Count;if(restart)ReloadCpu();
                Check(PowerPlan.RestoreLegacyCpuIdle()&&PowerRecoveryFixture.Writes.Count==writes,"Flush retry rewrote completed values");CpuRecovered();
            }
        }
        private static void CpuReceiptFailures()
        {
            Cpu(1,1,Idle("O"));Settings.RejectStringWrite=(name,value)=>name=="CpuIdleStateV1"&&value==Idle3(3);
            Check(!PowerPlan.RestoreLegacyCpuIdle()&&PowerRecoveryFixture.Writes.Count==0&&Settings.LoadStr("CpuIdleStateV1","")==Idle("O"),"Failed authority save permitted native writes");
            Settings.RejectStringWrite=null;ReloadCpu();Check(PowerPlan.RestoreLegacyCpuIdle(),"Authority-save retry failed");CpuRecovered();
            Cpu(1,1,Idle("O"));Settings.AfterStringWrite=delegate(string name,string value){if(name=="CpuIdleStateV1"&&value==Idle3(3)){Settings.AfterStringWrite=null;Settings.FailReadName=name;Settings.FailReadCount=1;}};
            Check(!PowerPlan.RestoreLegacyCpuIdle()&&PowerRecoveryFixture.Writes.Count==0&&PowerPlan.LegacyCpuIdleHasResidue,"Unverified authority write permitted native writes");
            ReloadCpu();Check(PowerPlan.RestoreLegacyCpuIdle(),"Authority readback-failure reload could not retry");CpuRecovered();
            foreach(bool restart in new[]{false,true})
            {
                Cpu(1,1,Idle("O"));Settings.RejectStringWrite=(name,value)=>name=="CpuIdleStateV1"&&value==Idle3(2);
                Check(!PowerPlan.RestoreLegacyCpuIdle()&&Value("IdleDisableSet",true)==0&&Value("IdleDisableSet",false)==1,"Failed AC progress save advanced to DC");
                Check(Settings.LoadStr("CpuIdleStateV1","").Length>0,"Failed progress save lost original recovery debt");
                Settings.RejectStringWrite=null;if(restart)ReloadCpu();Check(PowerPlan.RestoreLegacyCpuIdle(),"Progress-save retry failed");CpuRecovered();
            }
            Cpu(1,1,Idle("O"));PowerRecoveryFixture.LieWrite=Key("IdleDisableSet",true);
            Check(!PowerPlan.RestoreLegacyCpuIdle()&&Value("IdleDisableSet",false)==1&&Settings.LoadStr("CpuIdleStateV1","")==Idle3(3),"Failed native readback advanced ownership");
            PowerRecoveryFixture.LieWrite=null;ReloadCpu();Check(PowerPlan.RestoreLegacyCpuIdle(),"Native readback retry failed");CpuRecovered();
            Cpu(1,1,Idle("O"));Settings.RejectStringWrite=(name,value)=>name=="CpuIdleStateV1"&&value=="";
            Check(!PowerPlan.RestoreLegacyCpuIdle()&&Value("IdleDisableSet",true)==0&&Value("IdleDisableSet",false)==0&&PowerPlan.LegacyCpuIdleHasResidue,"Failed clear lost recovery record");
            Settings.RejectStringWrite=null;ReloadCpu();Check(PowerPlan.RestoreLegacyCpuIdle(),"Failed-clear reload could not retry");CpuRecovered();
            Cpu(1,1,Idle("O"));PowerRecoveryFixture.FailRead=Key("IdleDisableSet",false);
            Check(!PowerPlan.RestoreLegacyCpuIdle()&&PowerRecoveryFixture.Writes.Count==0&&Settings.LoadStr("CpuIdleStateV1","")==Idle("O"),"Unreadable original authority was claimed");
            foreach(string bad in new[]{"3|"+PowerRecoveryFixture.Plan+"|0|R|4","3|"+PowerRecoveryFixture.Plan+"|0|S|2","garbage"})
            {Cpu(1,1,bad);Check(!PowerPlan.RestoreLegacyCpuIdle()&&Settings.LoadStr("CpuIdleStateV1","")==bad&&PowerRecoveryFixture.Writes.Count==0,"Malformed receipt was cleared or applied");}
        }
        private static string ParkReceipt(){return PowerRecoveryFixture.Plan+"|20|30|40|50";}
        private static void Park()
        {parkCases++;PowerRecoveryFixture.Reset();foreach(string setting in new[]{"CpMinCores","CpMinCores1"})foreach(bool ac in new[]{false,true})SetValue(setting,ac,100);Settings.SaveStr("CoreParkSnap",ParkReceipt());}
        private static void ParkRecovered()
        {Check(!PowerPlan.HasParkResidue()&&Settings.LoadStr("CoreParkSnap","")=="","Park recovery lost completion or retained debt");}
        private static void ParkFaults()
        {
            Park();Check(PowerPlan.RestoreParkState(),"Normal parking recovery failed");Check(PowerRecoveryFixture.Writes.Count==4&&PowerRecoveryFixture.SetCalls==1,"Park values/activation were not restored");ParkRecovered();
            Park();PowerRecoveryFixture.SetFailures=1;
            Check(!PowerPlan.RestoreParkState()&&Settings.LoadStr("CoreParkSnap","")=="2|"+ParkReceipt()+"|F","Failed activation cleared parking snapshot or lost flush phase");
            Check(PowerRecoveryFixture.DurableWrites.Contains("CoreParkSnap=2|"+ParkReceipt()+"|F"),"Park flush authority was not saved through the durable settings API");
            int writes=PowerRecoveryFixture.Writes.Count;SetValue("CpMinCores",true,100);
            Check(PowerPlan.RestoreParkState()&&PowerRecoveryFixture.Writes.Count==writes&&Value("CpMinCores",true)==100,"Park flush retry reclaimed a later external100");ParkRecovered();Trace("park Set failure then external100");
            Park();Settings.SaveStr("CoreParkSnap","2|"+ParkReceipt()+"|F");
            Check(PowerPlan.RestoreParkState()&&PowerRecoveryFixture.Writes.Count==0&&Value("CpMinCores",true)==100&&Value("CpMinCores1",false)==100,
                "A fresh load of the durable park flush phase reclaimed external100 values");ParkRecovered();
            Park();PowerRecoveryFixture.FailWrite=Key("CpMinCores",false);
            for(int attempt=0;attempt<5;attempt++)Check(!PowerPlan.RestoreParkState()&&PowerPlan.HasParkResidue(),"Repeated partial parking failure dropped snapshot");
            Check(Settings.LoadStr("CoreParkTries","")=="3"&&Value("CpMinCores",true)==20&&Value("CpMinCores1",true)==40,"Partial park restore lost progress or failure cap");
            PowerRecoveryFixture.FailWrite=null;SetValue("CpMinCores",true,42);Check(PowerPlan.RestoreParkState()&&Value("CpMinCores",true)==42&&Value("CpMinCores",false)==30,"Partial parking retry overwrote external value or skipped remaining DC");ParkRecovered();
            Park();PowerRecoveryFixture.FailRead=Key("CpMinCores",true);Check(!PowerPlan.RestoreParkState()&&PowerPlan.HasParkResidue(),"Read failure was treated as a restored parking value");
            PowerRecoveryFixture.FailRead=null;Check(PowerPlan.RestoreParkState(),"Parking read-failure retry failed");ParkRecovered();
            Park();PowerRecoveryFixture.LieWrite=Key("CpMinCores",true);Check(!PowerPlan.RestoreParkState()&&PowerPlan.HasParkResidue(),"Parking write readback failure was accepted");
            PowerRecoveryFixture.LieWrite=null;Check(PowerPlan.RestoreParkState(),"Parking readback retry failed");ParkRecovered();
            Park();PowerRecoveryFixture.CurrentReplies.Enqueue(PowerRecoveryFixture.Plan);PowerRecoveryFixture.CurrentReplies.Enqueue(PowerRecoveryFixture.Other);
            Check(PowerPlan.RestoreParkState()&&PowerRecoveryFixture.SetCalls==0&&PowerRecoveryFixture.Active==PowerRecoveryFixture.Other,"Parking recovery pulled back an externally selected plan");ParkRecovered();
            Park();PowerRecoveryFixture.CurrentReplies.Enqueue(null);Check(!PowerPlan.RestoreParkState()&&PowerPlan.HasParkResidue(),"Unknown current plan cleared parking debt");
            PowerRecoveryFixture.Active=PowerRecoveryFixture.Plan;Check(PowerPlan.RestoreParkState(),"Current-plan query retry failed");ParkRecovered();
            Park();Settings.RejectStringWrite=(name,value)=>name=="CoreParkSnap"&&value=="";
            Check(!PowerPlan.RestoreParkState()&&PowerPlan.HasParkResidue(),"Failed parking receipt clear reported completion");
            writes=PowerRecoveryFixture.Writes.Count;Settings.RejectStringWrite=null;SetValue("CpMinCores",true,100);
            Check(PowerPlan.RestoreParkState()&&PowerRecoveryFixture.Writes.Count==writes&&Value("CpMinCores",true)==100,"Failed-clear retry rewrote a completed channel");ParkRecovered();
            Park();Settings.RejectStringWrite=(name,value)=>name=="CoreParkSnap"&&value.EndsWith("|F");
            Check(!PowerPlan.RestoreParkState()&&PowerRecoveryFixture.SetCalls==0&&Settings.LoadStr("CoreParkSnap","")==ParkReceipt(),"Failed flush receipt save activated or lost debt");
            Settings.RejectStringWrite=null;Check(PowerPlan.RestoreParkState(),"Parking phase-save retry failed");ParkRecovered();
            Park();Settings.FailReadName="CoreParkSnap";Settings.FailReadCount=1;
            Check(!PowerPlan.RestoreParkState()&&PowerRecoveryFixture.Writes.Count==0&&PowerPlan.HasParkResidue(),"Unreadable parking receipt was dropped");
            foreach(string invalid in new[]{"invalid",PowerRecoveryFixture.Plan+"|101|0|0|0",PowerRecoveryFixture.Plan+"|1|2"})
            {Park();Settings.SaveStr("CoreParkSnap",invalid);for(int i=0;i<4;i++)Check(!PowerPlan.RestoreParkState()&&Settings.LoadStr("CoreParkSnap","")==invalid,"Invalid parking recovery data was deleted");}
        }
        private static GameMode Mode()
        {
            var mode=(GameMode)FormatterServices.GetUninitializedObject(typeof(GameMode));
            Field(mode,"sync",new object());Field(mode,"powerApplyGate",new object());Field(mode,"powerStoppedKeys",new HashSet<int>());
            Field(mode,"envRestorePending",new HashSet<string>());Field(mode,"envNextAttempt",new Dictionary<string,long>());Field(mode,"envFailures",new Dictionary<string,int>());
            Field(mode,"profileStore",FormatterServices.GetUninitializedObject(typeof(GameProfileStore)));
            Field(mode,"enabled",true);Field(mode,"active",true);Field(mode,"planActive",true);Field(mode,"powerSessionGen",7);Field(mode,"lastPowerPolicyKey",3);
            Settings.SaveStr("PowerPlanChoice","v2:managed");return mode;
        }
        private static void Invalidate(GameMode mode,string state)
        {
            if(state=="off")Settings.SaveStr("PowerPlanChoice","v2:off");
            else if(state=="generation")Field(mode,"powerSessionGen",8);
            else if(state=="gameGoneSinceTicks")Field(mode,state,1L);
            else if(state=="profileSaveFailureSignaled")Field(mode,state,1);
            else Field(mode,state,state=="stopping"||state=="panicReq"||state=="powerSessionEnding");
        }
        private static void AdmissionFaults()
        {
            string[] states={"enabled","active","planActive","stopping","panicReq","powerSessionEnding","gameGoneSinceTicks","profileSaveFailureSignaled","generation","off"};
            PowerRecoveryFixture.Reset();var mode=Mode();int applies=0;
            Check((bool)Call(mode,"PowerPlanApplySessionCurrent",7),"Valid power session was denied");
            Check((bool)Call(mode,"RunPowerPlanApply",7,new Func<bool>(delegate{applies++;return true;}))&&applies==1&&PowerRecoveryFixture.RestoreCalls==0,"Valid power apply did not complete");
            foreach(string state in states)
            {
                admissionCases++;PowerRecoveryFixture.Reset();mode=Mode();Invalidate(mode,state);applies=0;
                Check(!(bool)Call(mode,"PowerPlanApplySessionCurrent",7),"Stale power session was admitted: "+state);
                Check(!(bool)Call(mode,"RunPowerPlanApply",7,new Func<bool>(delegate{applies++;return true;}))&&applies==0,"Late power delegate ran: "+state);
                Check(!(bool)Call(mode,"PowerPlanNotificationSessionCurrent",7,3),"Notification admitted an invalid power session: "+state);
                Call(mode,"RollbackPowerPlan","fixture recovery while admission denied");
                Check(PowerRecoveryFixture.RestoreCalls==1&&!Field<bool>(mode,"planActive"),"Admission denial blocked required restoration: "+state);
            }
            foreach(string state in states)
            {
                admissionCases++;PowerRecoveryFixture.Reset();mode=Mode();applies=0;GameMode captured=mode;
                Settings.AfterStringWrite=delegate(string name,string value){if(name.StartsWith("ActionAttempt_",StringComparison.Ordinal)&&value.Length>0){Settings.AfterStringWrite=null;Invalidate(captured,state);}};
                Check(!(bool)Call(mode,"RunPowerPlanApply",7,new Func<bool>(delegate{applies++;return true;}))&&applies==0&&PowerRecoveryFixture.RestoreCalls==1,"Admission changed before native delegate but apply was not cancelled: "+state);
            }
            foreach(string state in states)
            {
                admissionCases++;PowerRecoveryFixture.Reset();mode=Mode();applies=0;GameMode captured=mode;
                Check(!(bool)Call(mode,"RunPowerPlanApply",7,new Func<bool>(delegate{applies++;Invalidate(captured,state);return true;}))&&applies==1&&PowerRecoveryFixture.RestoreCalls==1,"Completed late apply did not roll back: "+state);
            }
            admissionCases++;PowerRecoveryFixture.Reset();mode=Mode();Field(mode,"powerPlanNotificationPending",1);Field(mode,"nextPowerAuditTicks",123L);
            Call(mode,"InvalidatePowerPlanSession");
            Check(Field<int>(mode,"powerSessionGen")==8&&Field<int>(mode,"powerPlanNotificationPending")==0&&Field<long>(mode,"nextPowerAuditTicks")==0&&Field<bool>(mode,"planActive"),"Session invalidation lost recovery debt or retained stale notifications");
            PowerRecoveryFixture.RestoreResult=false;Field(mode,"stopping",true);Call(mode,"RollbackPowerPlan","injected restore failure");
            Check(Field<bool>(mode,"planActive")&&Field<HashSet<string>>(mode,"envRestorePending").Contains("powerplan"),"Failed rollback lost the pending restore debt");
            PowerRecoveryFixture.RestoreResult=true;Call(mode,"RollbackPowerPlan","retry");Check(!Field<bool>(mode,"planActive")&&PowerRecoveryFixture.RestoreCalls==2,"Stopped-session rollback could not retry");
            Trace("late apply admission and independent recovery");
        }
        private static int Main(string[] args)
        {
            try
            {
                Logger.WritesEnabled=false;PowerRecoveryFixture.Reset();Lang.Init();
                CpuPartialFaults();CpuReceiptFailures();ParkFaults();AdmissionFaults();
                Check(PowerRecoveryFixture.TotalForbiddenCalls==0,"A native power endpoint was attempted in a recovery scenario");
                if(args.Length==1)File.WriteAllLines(Path.Combine(args[0],"fault-trace.txt"),traces);
                Console.WriteLine("PASS power recovery faults checks="+checks+" cpu_cases="+cpuCases+" park_cases="+parkCases+" admission_cases="+admissionCases+" actual_power_writes=0 product_startup=0 ui_windows=0");return 0;
            }
            catch(Exception error){Console.Error.WriteLine(error);return 1;}
        }
    }
}
#endif
