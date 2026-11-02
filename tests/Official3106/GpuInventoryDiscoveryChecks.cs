using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class GpuInventoryDiscoveryChecks
    {
        private const string Nvidia = "PCI\\VEN_10DE&DEV_2204&SUBSYS_000110DE&REV_A1\\A";
        private const string SameModel = "PCI\\VEN_10DE&DEV_2204&SUBSYS_000110DE&REV_A1\\B";
        private const string Intel = "PCI\\VEN_8086&DEV_A780&SUBSYS_00018086&REV_04\\C";
        private static int checks;
        private static void Check(bool condition, string message)
        { checks++; if (!condition) throw new InvalidOperationException(message); }
        private static string Key(string number) { return GpuInventoryDiscovery.DisplayClass + "\\" + number; }
        private static GpuDriverBinding[] Map(string[] instances, Dictionary<string,string> driver)
        {
            GpuDriverBinding[] mapped;
            Check(GpuInventoryDiscovery.TryMapPresent(instances, delegate(string instance) { return driver[instance]; }, out mapped), "Present map unexpectedly failed");
            return mapped;
        }
        private static void Reject(string driver)
        {
            GpuDriverBinding[] mapped;
            Check(!GpuInventoryDiscovery.TryMapPresent(new[]{Nvidia},delegate(string ignored){return driver;},out mapped),"Invalid driver mapping was accepted");
            Check(mapped.Length==0,"Invalid inventory retained a partial device");
        }
        private static int Main()
        {
            try
            {
                var one = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{Nvidia,Key("0003")}};
                // Registry can contain 0000/0001/0002 for previous installations of
                // the same model. Only the present instance's current 0003 is read.
                var mapped=Map(new[]{Nvidia},one);
                Check(mapped.Length==1&&mapped[0].SoftwareKey=="0003"&&mapped[0].InstanceId==Nvidia,"One physical card became multiple old software keys");
                var two = new Dictionary<string,string>(one,StringComparer.OrdinalIgnoreCase){{SameModel,Key("0004")}};
                mapped=Map(new[]{Nvidia,SameModel},two);
                Check(mapped.Length==2&&mapped[0].SoftwareKey!=mapped[1].SoftwareKey,"Two identical physical models were collapsed");
                two[SameModel]=Key("0003");
                mapped=Map(new[]{Nvidia,SameModel},two);
                Check(mapped.Length==2,"Distinct instances sharing a software key were collapsed");
                var hybrid=new Dictionary<string,string>(one,StringComparer.OrdinalIgnoreCase){{Intel,Key("0002")}};
                mapped=Map(new[]{Nvidia,Intel},hybrid);
                Check(mapped.Length==2&&mapped[1].InstanceId==Intel,"Integrated and discrete instances were not retained independently");
                int reads=0;
                Check(GpuInventoryDiscovery.TryMapPresent(new[]{Nvidia,Nvidia.ToLowerInvariant(),"ROOT\\DISPLAY\\0000","SWD\\REMOTEDISPLAYENUM\\RDP"},
                    delegate(string id){reads++;return one[id];},out mapped),"Duplicate/software filtering failed");
                Check(reads==1&&mapped.Length==1,"Duplicate instances or software adapters caused extra physical candidates");
                reads=0;
                Check(GpuInventoryDiscovery.TryMapPresent(new[]{"ROOT\\DISPLAY\\0000"},delegate(string id){reads++;throw new Exception();},out mapped)
                    &&mapped.Length==0&&reads==0,"Software display attempted a physical driver lookup");
                Check(GpuInventoryDiscovery.TryMapPresent(new string[0],delegate(string id){throw new Exception();},out mapped)&&mapped.Length==0,"Empty presence fell back to historical registry keys");
                foreach(string bad in new[]{null,"","0003",Key("00003"),Key("003"),Key("00A3"),Key("０００３"),Key("0003")+"\\child",Key("0003")+" ",
                    "{4d36e972-e325-11ce-bfc1-08002be10318}\\0003",Key("..\\x"),Key("0003")+"\0"}) Reject(bad);
                Check(!GpuInventoryDiscovery.TryMapPresent(new[]{Nvidia,SameModel},delegate(string id){if(id==SameModel)throw new Exception("Unreadable Enum key");return Key("0003");},out mapped)
                    &&mapped.Length==0,"A partial inventory was presented as one verified GPU");
                Check(!GpuInventoryDiscovery.TryMapPresent(null,delegate(string id){return Key("0003");},out mapped)&&mapped.Length==0,"Unknown presence returned a physical target");
                Check(!GpuInventoryDiscovery.TryMapPresent(new[]{"PCI\\VEN_BAD&DEV_2204\\X"},delegate(string id){return Key("0003");},out mapped)&&mapped.Length==0,"Malformed PCI identity was accepted");
                Check(!GpuInventoryDiscovery.TryMapPresent(new[]{"PCI\\VEN_10DE&DEV_2204"},delegate(string id){return Key("0003");},out mapped)&&mapped.Length==0,"A model id was accepted as a physical instance");
                Check(GpuInventoryDiscovery.MatchesInstance(Nvidia,"pci\\ven_10de&dev_2204&cc_0300"),"Valid broad MatchingDeviceId no longer matches its current instance");
                Check(!GpuInventoryDiscovery.MatchesInstance(Nvidia,"PCI\\VEN_10DE&DEV_2486"),"A stale different GPU model was bound to the current device");
                Check(!GpuInventoryDiscovery.MatchesInstance(Nvidia,"ROOT\\VEN_10DE&DEV_2204"),"Software identity passed the PCI match check");
                Console.WriteLine("PASS GPU inventory mapping assertions="+checks+" cases=single-with-stale,two-same-model,hybrid,virtual,duplicate,unknown; native writes=0 product_startup=0");
                return 0;
            }
            catch(Exception error){Console.Error.WriteLine(error);return 1;}
        }
    }
}
