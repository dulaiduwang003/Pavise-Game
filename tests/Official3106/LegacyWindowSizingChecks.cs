using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp {
internal static partial class Official3106UiChecks {
 sealed class LegacySizingCase {
  internal string Name; internal Rectangle Work; internal int Dpi; internal float Expected;
  internal LegacySizingCase(string name,Rectangle work,int dpi,float expected){Name=name;Work=work;Dpi=dpi;Expected=expected;}
 }
 static void RunLegacySizingModels(){
  bool savedNoFit=Dpi.NoFit;float savedScale=Dpi.Scale;Rectangle savedWork=Dpi.UiFixturePrimaryArea;int savedDpi=Dpi.UiFixtureWindowDpi;
  try{
   Dpi.NoFit=false;
   var cases=new[]{
    new LegacySizingCase("fhd100",new Rectangle(0,0,1920,1040),96,1f),
    new LegacySizingCase("fhd150",new Rectangle(0,0,1920,1040),144,1040f/760f),
    new LegacySizingCase("fhd200",new Rectangle(0,0,1920,1040),192,1040f/760f),
    new LegacySizingCase("qhd125",new Rectangle(0,0,2560,1400),120,1.25f),
    new LegacySizingCase("qhd200",new Rectangle(0,0,2560,1400),192,1400f/760f),
    new LegacySizingCase("4k100",new Rectangle(0,0,3840,2080),96,1f),
    new LegacySizingCase("4k150",new Rectangle(0,0,3840,2080),144,1.5f),
    new LegacySizingCase("4k200",new Rectangle(0,0,3840,2080),192,2f),
    new LegacySizingCase("4k300",new Rectangle(0,0,3840,2080),288,2080f/760f),
    new LegacySizingCase("8k400-old-no-global-3x-cap",new Rectangle(0,0,7680,4280),384,4f),
    new LegacySizingCase("negative-primary-origin",new Rectangle(-1920,-40,1920,1040),144,1040f/760f),
    new LegacySizingCase("left-taskbar",new Rectangle(64,0,1856,1040),144,1040f/760f),
    new LegacySizingCase("thick-taskbar",new Rectangle(0,0,1920,952),144,952f/760f),
    new LegacySizingCase("below-design-old-minimum-1x",new Rectangle(0,0,1280,680),144,1f),
    new LegacySizingCase("narrow-primary-old-minimum-1x",new Rectangle(0,0,720,1200),192,1f),
    new LegacySizingCase("invalid-primary-falls-back",Rectangle.Empty,144,1.5f)
   };
   foreach(var item in cases){
    context="legacy3105 sizing/"+item.Name;Dpi.UiFixturePrimaryArea=item.Work;Dpi.Scale=item.Dpi/96f;Dpi.SetDesignSize(1220,760);
    float actual=Dpi.ScaleFor(item.Dpi);
    Check(Math.Abs(actual-item.Expected)<0.00001f,"primary-screen DPI cap changed: actual="+actual+" expected="+item.Expected);
    Check(Math.Abs(Dpi.Scale-item.Expected)<0.00001f,"SetDesignSize did not apply the same old cap");
    Check(new Size(Dpi.S(1220),Dpi.S(760))==new Size((int)Math.Round(1220*item.Expected),(int)Math.Round(760*item.Expected)),"fixed design pixel dimensions changed");
    foreach(var client in new[]{new Size(640,480),new Size(1220,760),new Size(1920,1080),new Size(3840,2160)}){
     var layout=new WorkspaceLayout(client,actual);
     Check(layout.Width==1220&&layout.Height==760,"client size changed the logical canvas");
     Check(layout.RailWidth==0&&layout.HeaderHeight==60,"legacy sizing reverted current bottom-nav/header UI geometry");
    }
    sizingModelCases++;
   }
   context="legacy3105 sizing/FitScale";Dpi.UiFixturePrimaryArea=new Rectangle(0,0,3840,2160);Dpi.Scale=1f;Dpi.SetDesignSize(1220,760);
   Check(Dpi.FitScale(610,380)==1f,"old fit unexpectedly shrinks below 1x");
   Check(Math.Abs(Dpi.FitScale(1830,1140)-1.5f)<0.00001f,"client-proportional fit changed");
   Check(Math.Abs(Dpi.FitScale(2440,760)-1f)<0.00001f,"fit stretches axes independently");
   Check(Dpi.FitScale(12200,7600)==3f,"old client-fit 3x maximum changed");
   Check(Dpi.FitScale(0,760)==Dpi.Scale&&Dpi.FitScale(1220,0)==Dpi.Scale,"invalid client size no longer keeps current scale");
   Check(!Dpi.FitDiffers(1230,760),"old fit epsilon became too sensitive");
   Check(Dpi.FitDiffers(1830,1140),"legacy fit no longer detects a proportional client change");
   Check(!Dpi.WouldChange(0),"invalid window DPI requests a rebuild");
   Check(Dpi.Update(144)&&Math.Abs(Dpi.Scale-1.5f)<0.00001f,"old explicit window-DPI update changed");
   Check(!Dpi.Update(144),"unchanged DPI requests a second rebuild");
   Dpi.NoFit=true;Dpi.UiFixturePrimaryArea=new Rectangle(0,0,720,500);
   Check(Dpi.ScaleFor(192)==2f,"NoFit fixture no longer bypasses the screen cap");
   context="legacy3105 sizing/runtime config";
   string config=System.IO.File.ReadAllText(AppDomain.CurrentDomain.SetupInformation.ConfigurationFile);
   Check(config.IndexOf("System.Windows.Forms.ApplicationConfigurationSection",StringComparison.Ordinal)<0,"new WinForms DPI opt-in remains in the runtime config");
   Check(config.Contains(".NETFramework,Version=v4.7.2"),".NET runtime target changed with the sizing restoration");
  }finally{Dpi.NoFit=savedNoFit;Dpi.Scale=savedScale;Dpi.UiFixturePrimaryArea=savedWork;Dpi.UiFixtureWindowDpi=savedDpi;Theme.DropFontCache();}
 }
}
}
