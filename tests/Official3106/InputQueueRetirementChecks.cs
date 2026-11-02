#if PAVISE_SELFTEST && PAVISE_INPUT_QUEUE_RETIREMENT
using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Win32;

namespace PaviseApp
{
    // This backend is used only by a renamed frozen copy of ReversibleReg and
    // InputMythTweak. Actual Settings uses its existing transient self-test store.
    internal sealed class InputQueueFixtureValue
    { internal object Value; internal RegistryValueKind Kind; }
    internal static class InputQueueFixtureRegistry
    {
        internal static readonly InputQueueFixtureRegistryKey LocalMachine = new InputQueueFixtureRegistryKey("");
        internal static readonly Dictionary<string, Dictionary<string, InputQueueFixtureValue>> Keys = new Dictionary<string, Dictionary<string, InputQueueFixtureValue>>(StringComparer.OrdinalIgnoreCase);
        internal static readonly List<string> Writes = new List<string>();
        internal static string FailWritePath, FailOpenPath;
        internal static void Reset() { Keys.Clear(); Writes.Clear(); FailWritePath=FailOpenPath=null; Settings.UseTransientStoreForCurrentProcess(); }
        internal static void Seed(string path, string name, object value, RegistryValueKind kind)
        {
            Dictionary<string, InputQueueFixtureValue> key;
            if(!Keys.TryGetValue(path,out key))Keys[path]=key=new Dictionary<string, InputQueueFixtureValue>(StringComparer.OrdinalIgnoreCase);
            key[name]=new InputQueueFixtureValue {Value=value,Kind=kind};
        }
        internal static object Read(string path,string name)
        {
            Dictionary<string, InputQueueFixtureValue> key; InputQueueFixtureValue value;
            return Keys.TryGetValue(path,out key)&&key.TryGetValue(name,out value)?value.Value:null;
        }
        internal static void Remove(string path,string name)
        { Dictionary<string, InputQueueFixtureValue> key; if(Keys.TryGetValue(path,out key))key.Remove(name); }
    }
    internal sealed class InputQueueFixtureRegistryKey : IDisposable
    {
        private readonly string path;
        internal InputQueueFixtureRegistryKey(string path) { this.path=path; }
        public InputQueueFixtureRegistryKey OpenSubKey(string child) { return OpenSubKey(child,false); }
        public InputQueueFixtureRegistryKey OpenSubKey(string child,bool writable)
        {
            if(child==InputQueueFixtureRegistry.FailOpenPath)throw new UnauthorizedAccessException("Injected open failure");
            return InputQueueFixtureRegistry.Keys.ContainsKey(child)?new InputQueueFixtureRegistryKey(child):null;
        }
        public InputQueueFixtureRegistryKey CreateSubKey(string child)
        {
            if(child==InputQueueFixtureRegistry.FailOpenPath)throw new UnauthorizedAccessException("Injected create failure");
            if(!InputQueueFixtureRegistry.Keys.ContainsKey(child))InputQueueFixtureRegistry.Keys[child]=new Dictionary<string,InputQueueFixtureValue>(StringComparer.OrdinalIgnoreCase);
            return new InputQueueFixtureRegistryKey(child);
        }
        public object GetValue(string name) { return InputQueueFixtureRegistry.Read(path,name); }
        public RegistryValueKind GetValueKind(string name) { return InputQueueFixtureRegistry.Keys[path][name].Kind; }
        public void SetValue(string name,object value,RegistryValueKind kind)
        {
            if(path==InputQueueFixtureRegistry.FailWritePath)throw new UnauthorizedAccessException("Injected set failure");
            InputQueueFixtureRegistry.Writes.Add(path+"|"+name+"|"+kind+"|"+value);
            InputQueueFixtureRegistry.Seed(path,name,value,kind);
        }
        public void DeleteValue(string name,bool missing)
        {
            if(path==InputQueueFixtureRegistry.FailWritePath)throw new UnauthorizedAccessException("Injected delete failure");
            InputQueueFixtureRegistry.Writes.Add(path+"|delete|"+name); InputQueueFixtureRegistry.Remove(path,name);
        }
        public void Dispose() { }
    }
    internal static class InputQueueRetirementChecks
    {
        private const string Mouse=@"SYSTEM\CurrentControlSet\Services\mouclass\Parameters";
        private const string Kbd=@"SYSTEM\CurrentControlSet\Services\kbdclass\Parameters";
        private const string M="MouseDataQueueSize", K="KeyboardDataQueueSize";
        private static int assertions, scenarios;
        private static void Check(bool ok,string why) { assertions++; if(!ok)throw new InvalidOperationException(why); }
        private static void Start() { scenarios++; InputQueueFixtureRegistry.Reset(); }
        private static void Seed(int mouse,int keyboard)
        { InputQueueFixtureRegistry.Seed(Mouse,M,mouse,RegistryValueKind.DWord); InputQueueFixtureRegistry.Seed(Kbd,K,keyboard,RegistryValueKind.DWord); }
        private static bool Value(string path,string name,int value)
        { object actual=InputQueueFixtureRegistry.Read(path,name); return actual is int&&(int)actual==value; }
        private static void Receipts(string mouse,string keyboard,bool marker)
        {
            Check(Settings.SaveStr("PrevMouseQueue",mouse)&&Settings.SaveStr("PrevKbdQueue",keyboard)
                &&Settings.Save("InputMythRepaired",marker),"Could not seed legacy recovery records in transient Settings");
        }
        private static void RetiredSurface()
        {
            Start();
            MethodInfo[] methods=typeof(InputMythTweak).GetMethods(BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly);
            Check(methods.Length==2,"Retired input queue type exposed a forward operation");
            foreach(var method in methods)Check(method.Name=="Restore"||method.Name=="HasResidue","Unexpected retired API "+method.Name);
            Check(RepairCatalog.Find("inputmyth")==null,"Retired repair remained discoverable by key");
            int remaining=0; foreach(var item in RepairCatalog.All){remaining++;Check(item.Key!="inputmyth","Retired repair remained in one-click catalog");}
            Check(remaining>0,"Removing input queue repair emptied unrelated repair catalog entries");
            foreach(int queue in new[]{0,20,99,100,256})
            {
                Start();Seed(queue,queue);
                Check(!InputMythTweak.HasResidue()&&InputMythTweak.Restore(),"A fresh installation must have no input queue recovery work");
                Check(Value(Mouse,M,queue)&&Value(Kbd,K,queue)&&InputQueueFixtureRegistry.Writes.Count==0,
                    "Retirement changed an unowned queue to a default");
            }
            Start();Check(InputMythTweak.Restore()&&InputQueueFixtureRegistry.Keys.Count==0&&InputQueueFixtureRegistry.Writes.Count==0,"Retirement created absent driver keys");
            Start();Seed(20,30);Check(Settings.Save("InputMythRepaired",true)&&InputMythTweak.HasResidue(),"Legacy marker-only residue was lost");
            Check(InputMythTweak.Restore()&&!InputMythTweak.HasResidue()&&Value(Mouse,M,20)&&Value(Kbd,K,30)
                &&InputQueueFixtureRegistry.Writes.Count==0,"Marker-only recovery fabricated unknown originals");
        }
        private static void LegacyRecovery()
        {
            Start();Seed(100,100);Receipts("=20\u001f=100","=30\u001f=100",true);
            Check(InputMythTweak.HasResidue()&&InputMythTweak.Restore(),"Modern legacy receipts could not be restored");
            Check(Value(Mouse,M,20)&&Value(Kbd,K,30)&&InputQueueFixtureRegistry.Writes.Count==2,"Separate original mouse/keyboard values were crossed");
            Check(!InputMythTweak.HasResidue()&&!Settings.Load("InputMythRepaired",false),"Successful recovery retained marker or records");
            Check(InputMythTweak.Restore()&&InputQueueFixtureRegistry.Writes.Count==2,"Repeated restore must be a no-op");
            Start();Seed(100,100);Receipts("20","=30",false);
            Check(InputMythTweak.HasResidue()&&InputMythTweak.Restore()&&Value(Mouse,M,20)&&Value(Kbd,K,30),"Older original-only receipt formats became unrecoverable");
            Start();Seed(100,40);Receipts(InputQueueFixtureReversibleReg.Absent+"\u001f=100","",true);
            Check(InputMythTweak.Restore()&&InputQueueFixtureRegistry.Read(Mouse,M)==null&&Value(Kbd,K,40),"Absent original was not recovered by deleting only its override");
            Start();Seed(128,100);Receipts("=20\u001f=100","=30\u001f=100",true);
            Check(InputMythTweak.Restore()&&Value(Mouse,M,128)&&Value(Kbd,K,30)&&!InputMythTweak.HasResidue(),"Recovery overwrote an external changed value");
            Start();Seed(100,100);Receipts("=20\u001f=100","=30\u001f=100",true);InputQueueFixtureRegistry.Remove(Mouse,M);
            Check(InputMythTweak.Restore()&&InputQueueFixtureRegistry.Read(Mouse,M)==null&&Value(Kbd,K,30),"Recovery recreated an externally deleted override");
            Start();Receipts("=20\u001f=100","=30\u001f=100",true);
            Check(InputMythTweak.Restore()&&InputQueueFixtureRegistry.Keys.Count==0&&!InputMythTweak.HasResidue(),"Missing driver keys were recreated during recovery");
        }
        private static void PartialRecovery()
        {
            foreach(string failure in new[]{Mouse,Kbd})
            {
                Start();Seed(100,100);Receipts("=20\u001f=100","=30\u001f=100",true);InputQueueFixtureRegistry.FailWritePath=failure;
                Check(!InputMythTweak.Restore()&&InputMythTweak.HasResidue(),"Partial restoration failure was hidden");
                Check(Value(Mouse,M,failure==Mouse?100:20)&&Value(Kbd,K,failure==Kbd?100:30),"One failure prevented the independent queue from restoring");
                string pending=failure==Mouse?"PrevMouseQueue":"PrevKbdQueue";
                Check(Settings.LoadStr(pending,"").Length>0&&Settings.Load("InputMythRepaired",false),"Failed queue recovery lost its original receipt");
                InputQueueFixtureRegistry.FailWritePath=null;
                Check(InputMythTweak.Restore()&&Value(Mouse,M,20)&&Value(Kbd,K,30)&&!InputMythTweak.HasResidue(),"Partial recovery could not be retried");
            }
            Start();Seed(100,100);Receipts("=20\u001f=100","=30\u001f=100",true);InputQueueFixtureRegistry.FailOpenPath=Mouse;
            Check(!InputMythTweak.Restore()&&Value(Mouse,M,100)&&Value(Kbd,K,30),"Registry access failure was not isolated per queue");
            InputQueueFixtureRegistry.FailOpenPath=null;Check(InputMythTweak.Restore()&&!InputMythTweak.HasResidue(),"Registry access failure could not be retried");
            Start();Seed(100,100);Receipts("=20\u001f=100","=30\u001f=100",true);Settings.SuspendWritesForReset();
            Check(!InputMythTweak.Restore()&&Value(Mouse,M,20)&&Value(Kbd,K,30)&&InputMythTweak.HasResidue(),"Failed receipt cleanup must remain pending");
            Check(Settings.LoadStr("PrevMouseQueue","")=="=20\u001f=100"&&Settings.LoadStr("PrevKbdQueue","")=="=30\u001f=100","Failed settings writes lost recovery originals");
            typeof(Settings).GetField("writesSuspendedForReset",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,false);
            Check(InputMythTweak.Restore()&&!InputMythTweak.HasResidue(),"Receipt-cleanup retry failed");
        }
        private static void ResetPreservesOriginals()
        {
            Start();Seed(100,100);Receipts("=20\u001f=100","=30\u001f=100",true);
            foreach(string key in new[]{"PrevMouseQueue","PrevKbdQueue"})
                Check(RecoveryRecordKeys.Contains(key)&&!AppDataReset.IsUserPreference(key),"Reset classified a legacy original as a deletable preference");
            Check(!AppDataReset.IsUserPreference("InputMythRepaired"),"Reset discarded the legacy recovery marker");
            Check(Settings.SaveStr("Lang","1"),"Could not seed a normal preference");
            int removed;string error;
            Check(Settings.ClearUserPreferencesForReset(AppDataReset.IsUserPreference,out removed,out error)&&removed==1&&string.IsNullOrEmpty(error),"Transient reset did not remove exactly the normal preference");
            Check(Settings.LoadStr("PrevMouseQueue","")=="=20\u001f=100"&&Settings.LoadStr("PrevKbdQueue","")=="=30\u001f=100"
                &&Settings.Load("InputMythRepaired",false)&&InputMythTweak.HasResidue(),"Reset lost legacy recovery state");
            Check(InputQueueFixtureRegistry.Writes.Count==0,"Preference reset wrote to a driver key");
        }
        private static int Main()
        {
            try
            {
                Settings.UseTransientStoreForCurrentProcess();Logger.WritesEnabled=false;Lang.Init();
                RetiredSurface();LegacyRecovery();PartialRecovery();ResetPreservesOriginals();
                Console.WriteLine("PASS input queue retirement assertions="+assertions+" scenarios="+scenarios+" actual_registry_writes=0 ui_windows=0 device_operations=0");return 0;
            }
            catch(Exception error){Console.Error.WriteLine(error);return 1;}
        }
    }
    internal static partial class SelfTests
    {public static bool TryHandleRuntimeMode(string[] args){throw new InvalidOperationException("Application runtime is unavailable in input queue retirement checks");}}
}
#endif
