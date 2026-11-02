#if PAVISE_FILE_FILTER_CHECKS
using System;
using System.Collections.Generic;
using System.Linq;

namespace PaviseApp
{
#if PAVISE_FILE_FILTER_STANDALONE
    internal static class Lang { internal static int Cur; }
    internal static class Settings
    {
        internal static bool TryLoadStr(string key,out string value){throw new InvalidOperationException("Unmocked settings read");}
        internal static bool SaveStrDurable(string key,string value){throw new InvalidOperationException("Unmocked settings write");}
    }
#endif
    internal static class LaboratoryFileFilterChecks
    {
        private static int assertions;
        private static void Check(bool value,string message){assertions++;if(!value)throw new Exception(message);}
        private sealed class Store : ILaboratoryManualStore
        {
            internal string Value="";internal bool Readable=true;internal int Writes,FailWrite;
            public bool Read(out string value){value=Value;return Readable;}
            public bool Write(string value){Writes++;if(Writes==FailWrite)return false;Value=value;return true;}
        }
        private sealed class Fake : IFileFilterNative
        {
            internal List<FileFilterCandidate> Rows=new List<FileFilterCandidate>();
            internal bool Volume=true,Loaded=true,Identity=true,ReadFail,DetachFail,AttachFail,DetachKeeps,AttachWrong,FailAfterDetach;
            internal int Detaches,Attaches;
            public FileFilterCandidate[] Enumerate(out string error){error="";return Rows.Select(x=>x.Copy()).ToArray();}
            public bool Observe(FileFilterCandidate target,out FileFilterObservation value,out string error)
            {error="";value=null;if(ReadFail||(FailAfterDetach&&Detaches>0)){error="read denied";return false;}value=new FileFilterObservation {VolumePresent=Volume,FilterLoaded=Loaded,DriverMatches=Identity,Instances=Rows.Select(x=>x.Copy()).ToArray()};return true;}
            public bool Detach(FileFilterCandidate target,out string error){Detaches++;error="";if(DetachFail){error="denied";return false;}if(!DetachKeeps)Rows.Clear();return true;}
            public bool Attach(FileFilterCandidate target,out string error){Attaches++;error="";if(AttachFail){error="denied";return false;}var copy=target.Copy();if(AttachWrong)copy.Altitude="370001";Rows.Add(copy);return true;}
        }
        private static FileFilterCandidate Target()
        {return new FileFilterCandidate {VolumeGuid=@"\\?\Volume{11111111-2222-3333-4444-555555555555}\",VolumeDisplay="Q:",FilterName="ExampleLogFlt",InstanceName="ExampleLogFlt Instance",Altitude="370000.1",DriverPath=@"C:\Program Files\Example\logger.sys",DriverIdentity=new string('A',64)+":"+new string('B',40),Publisher="CN=Example Diagnostics Ltd",Description="Example File Operation Logger",Eligible=true};}
        private static LaboratoryFileFilterController Make(out Fake native,out Store store)
        {native=new Fake();native.Rows.Add(Target());store=new Store();return new LaboratoryFileFilterController(native,store);}
        private static int Main(string[] args)
        {
            try
            {
                if(args.Length==1&&args[0]=="--native-readonly")
                {string nativeError;var rows=new FileFilterNative().Enumerate(out nativeError);Console.WriteLine("READONLY instances="+rows.Length+" eligible="+rows.Count(x=>x.Eligible)+" error="+nativeError);Console.WriteLine(LaboratoryFileFilterController.DescribeChoices(rows,nativeError));foreach(var row in rows.GroupBy(x=>x.FilterName).Select(x=>x.First()))Console.WriteLine("IDENTITY "+row.FilterName+" | "+row.DriverPath+" | "+row.DriverIdentity);return string.IsNullOrEmpty(nativeError)?0:2;}
                Fake n;Store s;string error;var c=Make(out n,out s);
                Check(FileFilterAdmission.Reason(Target())=="","valid signed third-party metadata admitted");
                Check(!c.HasReceipt,"no default receipt");Check(c.Inspect().CanApply,"eligible query permits explicit selection");Check(n.Detaches==0&&n.Attaches==0,"inspection is read only");
                Check(c.Apply(Target(),out error),"apply");Check(n.Detaches==1&&s.Value.Contains("applied"),"intent and applied receipt");Check(c.HasReceipt&&c.Inspect().Applied,"active state");
                Check(!c.Apply(Target(),out error)&&n.Detaches==1,"no second application with debt");Check(c.Restore(out error),"restore");Check(n.Attaches==1&&!c.HasReceipt,"restored and receipt cleared");Check(c.Restore(out error)&&n.Attaches==1,"idempotent restore");

                string[] forbidden={"WdFilter","MessageTransfer","UCPD","ClawFlt","ahflt","CldFlt","Wof","FileInfo","BEDaisy","EasyAntiCheat","vgk"};
                foreach(string name in forbidden){c=Make(out n,out s);var t=Target();t.FilterName=name;Check(!c.Apply(t,out error),"blocked "+name);Check(n.Detaches==0&&s.Writes==0,"no mutation "+name);}
                foreach(string name in new[]{"bindflt","UCPD","luafv","Wof","FileInfo"})Check(FileFilterAdmission.ProtectedNameReason(name)=="Windows system filter","system attribution "+name);
                Check(FileFilterAdmission.ProtectedNameReason("WdFilter")=="Defender protection driver","Defender attribution");
                Check(FileFilterAdmission.ProtectedNameReason("MessageTransfer")=="Protected game or anti-cheat driver","game protection attribution");
                foreach(string name in new[]{"ClawFlt","ahflt"})Check(FileFilterAdmission.ProtectedNameReason(name)=="Protected filter with unverified ownership","unknown owner must not be labelled anti-cheat "+name);
                Check(FileFilterAdmission.ProtectedNameReason("CldFlt")=="Encryption or cloud-file filter","cloud-file attribution");
                c=Make(out n,out s);n.Rows.Clear();
                foreach(string name in new[]{"FileInfo","ClawFlt","ahflt"}){var row=Target();row.FilterName=name;row.Eligible=false;row.BlockReason=FileFilterAdmission.DisplayReason(FileFilterAdmission.ProtectedNameReason(name));n.Rows.Add(row);}
                var extra=n.Rows[0].Copy();extra.VolumeDisplay="R:";extra.VolumeGuid=@"\\?\Volume{22222222-2222-3333-4444-555555555555}\";n.Rows.Add(extra);
                LaboratoryManualState inventory=c.Inspect();
                Check(!inventory.CanApply&&inventory.Summary.Contains("4")&&inventory.Summary.Contains("0"),"unavailable state exposes observed and eligible instance counts");
                Check(inventory.Details.Contains("FileInfo")&&inventory.Details.Contains("ClawFlt")&&inventory.Details.Contains("ahflt"),"unavailable state retains every filter in details");
                Check(inventory.Details.Contains("Q:, R:")&&inventory.Details.IndexOf("FileInfo")==inventory.Details.LastIndexOf("FileInfo"),"details group identical filters across volumes");
                Check(inventory.Details.Contains(Target().DriverPath)&&n.Detaches==0&&n.Attaches==0,"diagnostic details retain image metadata without mutation");
                foreach(string owner in new[]{"CN=Microsoft Windows","CN=PerfectWorld Ltd","CN=Cloud Storage Ltd","CN=Disk Encryption Ltd","CN=FACEIT Ltd"}){c=Make(out n,out s);var t=Target();t.Publisher=owner;Check(!c.Apply(t,out error)&&n.Detaches==0,"blocked owner "+owner);}
                c=Make(out n,out s);var unknown=Target();unknown.DriverIdentity="";Check(!c.Apply(unknown,out error)&&s.Writes==0,"unknown identity denied");
                c=Make(out n,out s);s.FailWrite=1;Check(!c.Apply(Target(),out error)&&n.Detaches==0,"save fail before detach");
                c=Make(out n,out s);n.DetachFail=true;Check(!c.Apply(Target(),out error),"detach refusal reported");Check(n.Attaches==0&&!c.HasReceipt,"no debt when original remains");
                c=Make(out n,out s);n.DetachKeeps=true;Check(!c.Apply(Target(),out error)&&!c.HasReceipt,"false success readback refused");
                c=Make(out n,out s);s.FailWrite=2;Check(!c.Apply(Target(),out error),"applied state save failed");Check(n.Attaches==1&&!c.HasReceipt,"save failure rollback");
                c=Make(out n,out s);n.FailAfterDetach=true;Check(!c.Apply(Target(),out error)&&c.HasReceipt,"uncertain detach retains receipt");n.FailAfterDetach=false;Check(c.Restore(out error)&&!c.HasReceipt,"retry after readback available");
                c=Make(out n,out s);Check(c.Apply(Target(),out error),"apply before failed restore");n.AttachFail=true;Check(!c.Restore(out error)&&c.HasReceipt,"failed attach retains receipt");n.AttachFail=false;Check(c.Restore(out error),"failed attach retries");
                c=Make(out n,out s);Check(c.Apply(Target(),out error),"apply external instance test");var changed=Target();changed.InstanceName="External Instance";n.Rows.Add(changed);Check(!c.Restore(out error)&&n.Attaches==0&&c.HasReceipt,"external attachment preserved");
                c=Make(out n,out s);Check(c.Apply(Target(),out error),"apply changed driver test");n.Identity=false;Check(!c.Restore(out error)&&n.Attaches==0&&c.HasReceipt,"driver change refuses overwrite");
                c=Make(out n,out s);Check(c.Apply(Target(),out error),"apply absent volume test");n.Volume=false;Check(!c.Restore(out error)&&n.Attaches==0,"absent volume remains debt");n.Volume=true;Check(c.Restore(out error),"same guid after remount restores");
                c=Make(out n,out s);Check(c.Apply(Target(),out error),"apply verification test");n.AttachWrong=true;Check(!c.Restore(out error)&&c.HasReceipt,"wrong restored altitude not cleared");
                c=Make(out n,out s);s.Value="broken";Check(c.HasReceipt&&!c.Inspect().CanApply,"corrupt blocks new application");Check(!c.Restore(out error)&&n.Attaches==0,"corrupt never attaches");
                c=Make(out n,out s);s.Readable=false;Check(c.HasReceipt&&!c.Apply(Target(),out error)&&n.Detaches==0,"read failure fails closed");
                c=Make(out n,out s);n.Rows.Add(Target());Check(!c.Apply(Target(),out error)&&n.Detaches==0,"multi-instance observation denied");
                c=Make(out n,out s);Check(c.Apply(Target(),out error),"apply before receipt-clear failure");s.FailWrite=s.Writes+1;Check(!c.Restore(out error)&&c.HasReceipt&&n.Attaches==1,"receipt clear failure keeps recovery");Check(c.Restore(out error)&&n.Attaches==1&&!c.HasReceipt,"already attached retry avoids duplicate attach");
                FileFilterCandidate decoded;string phase;Check(LaboratoryFileFilterController.Decode(LaboratoryFileFilterController.Encode(Target(),"intent"),out decoded,out phase)&&phase=="intent"&&decoded.SameInstance(Target()),"receipt roundtrip");
                Check(!LaboratoryFileFilterController.Decode(new string('A',17000),out decoded,out phase),"receipt bound");
                c=Make(out n,out s);n.Rows[0].Description=new string('Z',2200);Check(!c.Apply(Target(),out error)&&n.Detaches==0,"unrecoverable overlong metadata rejected before detach");
                Check(!new FileFilterNative().Detach(new FileFilterCandidate {FilterName="WdFilter",Eligible=true},out error),"native mutation boundary independently rejects protected target");
                Console.WriteLine("PASS "+assertions+" file-filter assertions; simulated native state only");return 0;
            }
            catch(Exception e){Console.Error.WriteLine(e.ToString());return 1;}
        }
    }
}
#endif
