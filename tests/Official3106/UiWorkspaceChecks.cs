using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
namespace PaviseApp {
internal static partial class Official3106UiChecks {
 const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
 static int checks,performanceViewports,settingsViewports,overviewViewports,antiCheatViewports,windowProtocolCases,sizingModelCases;
 static string context;
 static void Check(bool value,string reason){checks++;if(!value)throw new InvalidOperationException(context+": "+reason);}
 static T Field<T>(PanelForm form,string name){return (T)typeof(PanelForm).GetField(name,Hidden).GetValue(form);}
 static void Call(PanelForm form,string name,params object[] args){typeof(PanelForm).GetMethod(name,Hidden).Invoke(form,args);}
 static IEnumerable<Control> Desc(Control root){foreach(Control child in root.Controls){yield return child;foreach(var next in Desc(child))yield return next;}}
 static void FitText(Control c,string text,Rectangle bounds,Font font,string label){if(string.IsNullOrEmpty(text))return;Check(c.ClientRectangle.Contains(bounds),label+" outside "+c.Name);int h=TextRenderer.MeasureText(text,font,new Size(Math.Max(1,bounds.Width),int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix|TextFormatFlags.NoPadding).Height;Check(h<=bounds.Height,label+" clipped "+c.Name+" expected="+h+" actual="+bounds.Height);}
 static void CheckGrid(PerformanceFeatureGrid grid){
  Check(grid.Visible,"active performance grid hidden");Check(!grid.HorizontalScroll.Visible,"horizontal performance overflow");
  var cards=Desc(grid).OfType<PerformanceFeatureCard>().ToArray();Check(cards.Length>0,"no feature cards");
  foreach(var card in cards){
   Check(card.Visible,"feature hidden: "+card.Title);Check(card.Left>=0&&card.Right<=card.Parent.ClientSize.Width,"feature width exceeds parent: "+card.Title);
   Check(card.ClientRectangle.Contains(card.TitleBounds),"title outside: "+card.Title);
   Check(card.Hosted!=null&&card.Hosted.Visible&&card.ClientRectangle.Contains(card.Hosted.Bounds),"host clips: "+card.Title+" card="+card.ClientSize+" host="+(card.Hosted==null?"null":card.Hosted.Bounds.ToString()));
   Check(!card.Hosted.Bounds.IntersectsWith(card.TitleBounds),"host/title overlap: "+card.Title);
   Check(card.AccessibleDescription.Contains(card.Desc),"full feature detail inaccessible: "+card.Title);
   if(card.RestartBounds.Width>0)Check(card.ClientRectangle.Contains(card.RestartBounds),"restart badge clips: "+card.Title);
  }
  var direct=grid.Controls.Cast<Control>().Where(c=>c.Visible).ToArray();for(int i=0;i<direct.Length;i++)for(int j=i+1;j<direct.Length;j++)Check(!direct[i].Bounds.IntersectsWith(direct[j].Bounds),"feature surfaces overlap");
  if(grid.VerticalScroll.Visible){grid.AutoScrollPosition=new Point(0,1000000);Application.DoEvents();Check(grid.AutoScrollPosition.Y<0,"native scroll did not move");grid.AutoScrollPosition=Point.Empty;Application.DoEvents();}
 }
 static void Screenshot(Form f,string path){using(var b=new Bitmap(f.Width,f.Height)){f.DrawToBitmap(b,new Rectangle(Point.Empty,f.Size));b.Save(path);}}
 [StructLayout(LayoutKind.Sequential)]struct WindowMinMax { public Point Reserved,MaxSize,MaxPosition,MinTrack,MaxTrack; }
 [StructLayout(LayoutKind.Sequential)]struct WindowRect {public int Left,Top,Right,Bottom;}
 [DllImport("user32.dll")]static extern bool GetWindowRect(IntPtr window,out WindowRect bounds);
 [DllImport("user32.dll",EntryPoint="SendMessageW")]static extern IntPtr WindowMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
 [DllImport("user32.dll",EntryPoint="GetWindowLongW")]static extern int WindowStyle(IntPtr window,int index);
 [DllImport("user32.dll")]static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll",EntryPoint="ShowWindow")]static extern bool ShowWindowWithoutActivation(IntPtr window,int command);
 [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int bytes);
 [DllImport("dwmapi.dll")]static extern int DwmGetWindowAttribute(IntPtr window,int attribute,out int value,int bytes);
 static IntPtr ScreenPoint(Point point){return new IntPtr(unchecked((point.Y<<16)|(point.X&0xffff)));}
 static string windowTrace;
 sealed class HiddenWindowProtocolForm:PanelForm{
  internal HiddenWindowProtocolForm(Tamer tamer,GameMode mode,Icon icon):base(tamer,mode,icon,false){}
  protected override CreateParams CreateParams{get{var value=base.CreateParams;value.ExStyle|=0x08000000;return value;}}
  protected override bool ShowWithoutActivation{get{return true;}}
  protected override void OnHandleCreated(EventArgs args){base.OnHandleCreated(args);int cloak=1;
   if(DwmSetWindowAttribute(Handle,13,ref cloak,4)!=0)throw new InvalidOperationException("test window cloak refused");
   int flags;if(DwmGetWindowAttribute(Handle,14,out flags,4)!=0||(flags&1)==0)throw new InvalidOperationException("test window cloak not verified");
  }
 }
 static void CheckWindowHidden(PanelForm form){int cloak;Check(DwmGetWindowAttribute(form.Handle,14,out cloak,4)==0&&(cloak&1)!=0,"test window is not DWM cloaked");Check(!form.ShowInTaskbar&&GetForegroundWindow()!=form.Handle,"test window entered taskbar or took focus");}
 static void FitWindow(PanelForm form){WindowStage(windowTrace,"fit begin bounds="+form.Bounds+" client="+form.ClientSize);Call(form,"OnFitTick",null,EventArgs.Empty);Call(form,"StopPageReveal");Application.DoEvents();WindowStage(windowTrace,"fit complete bounds="+form.Bounds+" client="+form.ClientSize);}
 static Control[] ResizeGrips(PanelForm form){return Desc(form).Where(c=>c.Name.StartsWith("WorkspaceResize",StringComparison.Ordinal)).ToArray();}
 static void CheckWindowViewport(PanelForm form){
  var root=Field<Control>(form,"root");Check(root.Size==new Size(Theme.S(1220),Theme.S(760)),"fixed 1220x760 logical canvas changed");
  Check(root.Left==Math.Max(0,(form.ClientSize.Width-root.Width)/2)&&root.Top==Math.Max(0,(form.ClientSize.Height-root.Height)/2),"fixed canvas is not centered");
  var top=root.Controls["WorkspaceTopBar"];Check(top!=null&&root.ClientRectangle.Contains(top.Bounds),"window header outside actual client bounds");
  var captions=new[]{top.Controls["WindowClose"],top.Controls["WindowMinimize"]};
  foreach(var caption in captions)Check(caption!=null&&top.ClientRectangle.Contains(caption.Bounds),"current caption control missing or outside header");
  for(int i=0;i<captions.Length;i++)for(int j=i+1;j<captions.Length;j++)Check(!captions[i].Bounds.IntersectsWith(captions[j].Bounds),"native caption buttons overlap");
  Check(top.Controls["WindowMaximize"]==null,"fixed-size window still exposes the removed maximize control");
  Check(captions[1].Right==captions[0].Left&&captions[1].Size==captions[0].Size,"remaining caption buttons are not adjacent equal-sized targets");
  var navigation=Field<NavRail>(form,"nav");Check(root.ClientRectangle.Contains(navigation.Bounds),"navigation outside resized window");
  var page=Field<Control>(form,"curPage");Check(page!=null&&root.ClientRectangle.Contains(page.Bounds),"active page outside resized window");
  Check(page.Bottom<=navigation.Top,"page covers bottom navigation after resize");
  Check(captions.All(c=>c.Enabled),"remaining caption controls are disabled");
  Check(!form.UiActive,"window interaction activated UI product workers");
 }
 static void WindowStage(string output,string stage){File.AppendAllText(Path.Combine(output,"window-protocol-progress.txt"),stage+Environment.NewLine);Console.WriteLine("WINDOW "+stage);}
 static void RunWindowProtocol(string output){
  windowTrace=output;WindowStage(output,"fixed3105 start");
  context="legacy3105 native window protocol";Settings.ResetUiFixtureMemory();Logger.WritesEnabled=false;
  Dpi.NoFit=false;Dpi.Scale=1f;Dpi.UiFixturePrimaryArea=new Rectangle(0,0,1920,1040);Dpi.UiFixtureWindowDpi=96;Dpi.SetDesignSize(1220,760);Theme.DropFontCache();Lang.Cur=1;Theme.SetLight(false);
  string folder=Path.Combine(output,"state-window-protocol");Directory.CreateDirectory(folder);Paths.Data=folder;
  var core=new SuppressionCore();var mode=new GameMode(folder,core);
  try{using(var icon=IconArt.MakeIcon(24))using(var form=new HiddenWindowProtocolForm(new Tamer(core),mode,icon)){
   WindowStage(output,"show fixed form");form.ShowInTaskbar=false;var handle=form.Handle;CheckWindowHidden(form);form.Show();Application.DoEvents();FitWindow(form);CheckWindowHidden(form);
   Check(!form.UiActive&&!mode.Enabled,"native fixture admitted product workers");
   Check(form.AutoScaleMode==AutoScaleMode.None,"fixed window enabled framework autoscaling");
   Check(form.FormBorderStyle==FormBorderStyle.None,"legacy borderless fixed-size window was not restored");
   Check(!form.MaximizeBox,"native maximize style is still enabled");
   Check((WindowStyle(form.Handle,-16)&0x00040000)==0,"native resize frame remains");
   using(var reference=new Form{FormBorderStyle=FormBorderStyle.None,MaximizeBox=false}){
    Check((WindowStyle(form.Handle,-16)&0x00cf0000)==(WindowStyle(reference.Handle,-16)&0x00cf0000),"native frame styles differ from a legacy borderless nonmaximizing Form");
   }
   Check(form.ClientSize==new Size(1220,760)&&form.Size==form.ClientSize,"fixed initial 96-DPI dimensions changed");
   Check(ResizeGrips(form).Length==0,"modern resize grips remain in the fixed window");
   CheckWindowViewport(form);
   var normal=form.Bounds;var root=Field<Control>(form,"root");var currentPage=Field<Control>(form,"curPage");var navigation=Field<NavRail>(form,"nav");float scale=Dpi.Scale;
   foreach(var local in new[]{new Point(1,1),new Point(form.ClientSize.Width-2,1),new Point(1,form.ClientSize.Height-2),new Point(form.ClientSize.Width-2,form.ClientSize.Height-2),new Point(1,form.ClientSize.Height/2)}){
    int hit=WindowMessage(form.Handle,0x84,IntPtr.Zero,ScreenPoint(form.PointToScreen(local))).ToInt32();
    Check(hit<10||hit>17,"fixed window exposes a native resize edge/corner");
   }
   var top=root.Controls["WorkspaceTopBar"];var brand=top.Controls["WorkspaceBrand"];
   Check(brand!=null,"titlebar brand is missing");
   foreach(var target in new[]{top,brand}){
    typeof(Control).GetMethod("OnMouseDoubleClick",Hidden).Invoke(target,new object[]{new MouseEventArgs(MouseButtons.Left,2,12,12,0)});Application.DoEvents();
    Check(form.WindowState==FormWindowState.Normal&&form.Bounds==normal,"titlebar double-click changes the fixed window");
   }
   Check(Field<Control>(form,"root")==root&&Field<Control>(form,"curPage")==currentPage&&Field<NavRail>(form,"nav")==navigation,"titlebar double-click rebuilt current UI");

   WindowStage(output,"display change stays pending-free");WindowMessage(form.Handle,0x7e,IntPtr.Zero,IntPtr.Zero);Application.DoEvents();FitWindow(form);
   Check(form.Bounds==normal&&Math.Abs(Dpi.Scale-scale)<.001f,"display change immediately reapplied modern adaptive sizing");
   Check(Field<Control>(form,"root")==root,"display change rebuilt current UI without an explicit DPI recheck");
   IntPtr proposed=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WindowRect)));
   try{
    Marshal.StructureToPtr(new WindowRect{Left=normal.Left+50,Top=normal.Top+50,Right=normal.Left+2000,Bottom=normal.Top+1400},proposed,false);
    WindowMessage(form.Handle,0x2e0,new IntPtr((192<<16)|192),proposed);Application.DoEvents();
   }finally{Marshal.FreeHGlobal(proposed);}
   Check(form.Bounds==normal&&Math.Abs(Dpi.Scale-scale)<.001f,"DPI suggested rectangle immediately resized the legacy window");
   Check(Field<Control>(form,"root")==root,"DPI message rebuilt UI before the old explicit DPI check");CheckWindowHidden(form);

   WindowStage(output,"minimize restore");WindowMessage(form.Handle,0x112,new IntPtr(0xf020),IntPtr.Zero);Application.DoEvents();
   Check(form.WindowState==FormWindowState.Minimized,"native minimize system command failed");CheckWindowHidden(form);
   ShowWindowWithoutActivation(form.Handle,4);Application.DoEvents();FitWindow(form);
   Check(form.WindowState==FormWindowState.Normal&&form.Bounds==normal,"minimize restore changed fixed size or position");CheckWindowHidden(form);
   Check(Field<Control>(form,"root")==root,"minimize restore rebuilt current UI at unchanged DPI");

   WindowStage(output,"old explicit DPI recheck");Dpi.UiFixtureWindowDpi=144;Call(form,"ApplyPendingDpiRebuild");Call(form,"StopPageReveal");Application.DoEvents();
   Check(Math.Abs(Dpi.Scale-1040f/760f)<.00001f,"old DPI recheck did not use primary-screen cap");
   Check(form.ClientSize==new Size(Theme.S(1220),Theme.S(760)),"explicit DPI recheck changed logical canvas dimensions");
   Check(Field<Control>(form,"root")!=root,"changed DPI did not rebuild scaled current controls");
   CheckWindowViewport(form);CheckWindowHidden(form);root=Field<Control>(form,"root");
   Call(form,"ApplyPendingDpiRebuild");Application.DoEvents();Check(Field<Control>(form,"root")==root,"unchanged capped DPI rebuilt UI twice");
   Dpi.UiFixtureWindowDpi=0;Call(form,"ApplyPendingDpiRebuild");Application.DoEvents();Check(Field<Control>(form,"root")==root,"unavailable window DPI rebuilt UI");
   Dpi.UiFixtureWindowDpi=96;Call(form,"ApplyPendingDpiRebuild");Call(form,"StopPageReveal");Application.DoEvents();
   Check(Dpi.Scale==1f&&form.ClientSize==new Size(1220,760),"returning to 96 DPI did not restore legacy base size");
   CheckWindowViewport(form);CheckWindowHidden(form);
   Check(!form.UiActive&&!mode.Enabled,"window protocol enabled actual product workers");
   Screenshot(form,Path.Combine(output,"official6-window-protocol.png"));windowProtocolCases++;WindowStage(output,"fixed3105 complete");
  }}finally{Dpi.NoFit=true;}
 }
 static Bitmap RenderBrand(WorkspaceBrand brand){
  var bitmap=new Bitmap(brand.Width,brand.Height);
  using(var graphics=Graphics.FromImage(bitmap)){
   var args=new PaintEventArgs(graphics,brand.ClientRectangle);
   typeof(WorkspaceBrand).GetMethod("OnPaintBackground",Hidden).Invoke(brand,new object[]{args});
   typeof(WorkspaceBrand).GetMethod("OnPaint",Hidden).Invoke(brand,new object[]{args});
  }
  return bitmap;
 }
 static void CheckBrandRecovery(){
  using(var brand=new WorkspaceBrand{Size=new Size(Theme.S(280),Theme.S(55))})
  using(var normal=RenderBrand(brand)){
   foreach(string fieldName in new[]{"logo","wordmarkPath","versionFont"}){
    var field=typeof(WorkspaceBrand).GetField(fieldName,Hidden);
    var original=(IDisposable)field.GetValue(brand);Check(original!=null,"brand cache not initialized: "+fieldName);
    original.Dispose();
    using(var recovered=RenderBrand(brand)){
     Check(!object.ReferenceEquals(original,field.GetValue(brand)),"brand cache was not rebuilt: "+fieldName);
     Check(!(bool)typeof(WorkspaceBrand).GetField("paintFallback",Hidden).GetValue(brand),"recoverable brand cache failure stayed in fallback");
     bool same=true;for(int y=0;y<normal.Height&&same;y++)for(int x=0;x<normal.Width;x++)if(normal.GetPixel(x,y)!=recovered.GetPixel(x,y)){same=false;break;}
     Check(same,"recovered brand differs from healthy rendering: "+fieldName);
    }
   }
   object healthyLogo=typeof(WorkspaceBrand).GetField("logo",Hidden).GetValue(brand);
   using(var unchanged=RenderBrand(brand))Check(object.ReferenceEquals(healthyLogo,typeof(WorkspaceBrand).GetField("logo",Hidden).GetValue(brand)),"healthy brand recreates resources each paint");
   int failures=0;
   var fault=typeof(WorkspaceBrand).GetField("UiFixturePaintFault",Hidden);
   fault.SetValue(brand,new Action(delegate{failures++;throw new ExternalException("Injected persistent GDI+ failure");}));
   using(var fallback=RenderBrand(brand)){
    Check((bool)typeof(WorkspaceBrand).GetField("paintFallback",Hidden).GetValue(brand),"persistent drawing error escaped bounded recovery");
    Check(failures==2,"persistent drawing failure retried without a bound");
    bool ink=false;for(int y=brand.WordmarkBounds.Top;y<brand.WordmarkBounds.Bottom&&!ink;y++)for(int x=brand.WordmarkBounds.Left;x<brand.WordmarkBounds.Right;x++)if(fallback.GetPixel(x,y).ToArgb()!=Theme.Bg.ToArgb()){ink=true;break;}
    Check(ink,"fallback brand text is blank");
   }
   using(var repeat=RenderBrand(brand))Check(failures==2,"fallback continuously retries bad resources");
   fault.SetValue(brand,null);brand.Width++;
   using(var normalAgain=RenderBrand(brand))Check(!(bool)typeof(WorkspaceBrand).GetField("paintFallback",Hidden).GetValue(brand),"brand did not leave fallback after a layout refresh");
  }
 }
 static void Check2223Defaults(GameMode mode){
  string[] whitelist=File.ReadAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"2223-preset-whitelist.txt"));
  Check(SystemProcessCatalog.PresetWhitelist.SequenceEqual(whitelist,StringComparer.OrdinalIgnoreCase),"preset whitelist differs from 2223 reference");
  Check(!BackgroundServicePolicy.SupportedMode(PerformancePreset.Competitive),"competitive suppression admits Session0 services");
  var service=new ProcEntry{Pid=23456,Session=0,Creation=12345,Name="VendorService",Path=@"C:\Vendor\VendorService.exe"};
  Check(!BackgroundServicePolicy.Eligible(PerformancePreset.Competitive,service,service.Path,null,@"C:\Windows"),"2223 competitive scope admitted a third-party service");
  var refresh=typeof(GameMode).GetMethod("SyncAutomationPolicy",Hidden);
  var mmcss=typeof(GameMode).GetField("mmcssOn",Hidden);
  Check(mode.KillGameDvr&&(bool)mmcss.GetValue(mode)&&!mode.AutoAddFullscreen,"2223 constructor defaults mismatch");
  refresh.Invoke(mode,null);
  Check(mode.KillGameDvr&&(bool)mmcss.GetValue(mode)&&!mode.AutoAddFullscreen,"policy refresh changed 2223 defaults");
  var enabled=new HashSet<string>(new[]{"GmSuppress","GmBoost","GmBoostCpuV1","GmBoostMemoryV1","GmBoostIoV1","GmBoostGpuV1","PowerPlanOn","GmPauseDl","GmPauseMaintenanceV1","GmAwake"});
  var policy=PolicyResolver.Global();
  foreach(var item in PolicyCatalog.All){
   string expected=item.Key=="PerformancePreset"?"0":item.Key=="GmThreadBoostStrategyV1"?"on":item.Key=="NvLowLat"?"off":enabled.Contains(item.Key)?"1":"0";
   Check(policy.ValueOf(item.Key)==expected,"default policy mismatch: "+item.Key);
  }
  Check(Settings.LoadCached("GpuPrefStageOn",true),"2223 high-performance GPU registration default changed");
  Check(!LaboratorySelection.Read(true,0,0).Any,"fresh defaults enabled an experiment");
  bool stored;
  foreach(string key in new[]{"GameDvrOff","GmMmcss","GmAutoAdd"})Check(!Settings.TryLoadStoredBool(key,out stored),"default was persisted over user intent: "+key);
  try{
   Settings.Save("GameDvrOff",false);Settings.Save("GmMmcss",false);Settings.Save("GmAutoAdd",true);
   refresh.Invoke(mode,null);
   Check(!mode.KillGameDvr&&!(bool)mmcss.GetValue(mode)&&mode.AutoAddFullscreen,"stored choices lost to new defaults");
  }finally{
   foreach(string key in new[]{"GameDvrOff","GmMmcss","GmAutoAdd"})Settings.Remove(key);
   refresh.Invoke(mode,null);
  }
  Check(mode.KillGameDvr&&(bool)mmcss.GetValue(mode)&&!mode.AutoAddFullscreen,"removing preferences did not restore 2223 defaults");
 }
 static void Run(string output,int dpi,int language,bool light){
  context=dpi+"/"+language+"/"+light;
  Console.WriteLine("CASE "+context);
  Settings.ResetUiFixtureMemory();Logger.WritesEnabled=false;
  Settings.Save("PerfMonitorEnabledV2",true);Settings.Save("GameRunReportsEnabledV1",true);
  Dpi.NoFit=true;Dpi.UiFixtureWindowDpi=dpi;Dpi.Scale=dpi/96f;Theme.DropFontCache();Lang.Cur=language;Theme.SetLight(light);
  CheckBrandRecovery();
  string folder=Path.Combine(output,"state-"+dpi+"-"+language+"-"+light);Directory.CreateDirectory(folder);Paths.Data=folder;
  var core=new SuppressionCore();var mode=new GameMode(folder,core);
  Check2223Defaults(mode);
  using(var icon=IconArt.MakeIcon(24))using(var form=new PanelForm(new Tamer(core),mode,icon,false)){
   form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-32000,-32000);form.ShowInTaskbar=false;form.Show();Application.DoEvents();
   Check(Math.Abs(Dpi.Scale-dpi/96f)<.001f,"synthetic DPI changed after showing the fixture");
   Check(!form.UiActive&&!mode.Enabled,"product workers admitted");
   Check(!Enum.GetNames(typeof(PageId)).Contains("Monitoring"),"retired game-performance page remains");
   Check(!Desc(form).Any(c=>c.Name=="monitoringEnabled"||c.Name=="gameRunReportsEnabled"||c.Name=="monitoringComponentInstall"),"retired monitoring controls remain");
   const string retiredVisualKey="MatchVisualEffectsV1";
   Check(!PerformanceActionCatalog.All.Any(a=>a.Key==retiredVisualKey)&&PerformanceActionCatalog.Retired.Any(a=>a.Key==retiredVisualKey),"retired visual feature remains available as an active action");
   Check(PerformanceActionCatalog.Find(retiredVisualKey)!=null&&PerformancePolicy.IsRetired(retiredVisualKey),"legacy visual recovery identity was lost");
   Check(!Desc(form).Any(c=>c.Name=="environmentVisualEffects"||c.Name=="manualPerformance_"+retiredVisualKey),"retired visual switch remains visible");
   try{
    Settings.Save(retiredVisualKey,true);
    Check(!MatchVisualEffects.Enabled,"saved legacy visual preference re-enabled the retired feature");
    Check(!mode.SetVisualEffectsReduction(true),"legacy visual feature can still be enabled");
    Check(!PerformanceActionBackend.Apply(retiredVisualKey,"WindowAnimation","0"),"retired visual backend still applies desktop changes");
   }finally{Settings.Remove(retiredVisualKey);}
   Check(Desc(form).OfType<Toggle>().Single(c=>c.Name=="manualPerformance_GmMmcss").Checked,"MMCSS UI disagrees with default runtime preference");
   Check(Field<Toggle>(form,"swEnvironmentGameDvr").Checked,"DVR UI disagrees with default runtime preference");
   var panels=Field<DBPanel[]>(form,"performanceTabPanels");var tabs=Field<TechTabs>(form,"performanceTabs");var categories=Field<PerformanceCategoryBar>(form,"performanceCategories");
   Check(panels.Length==5&&categories.Controls.Count==5,"five performance categories missing");
   CheckConfigurationPresetInteraction(form);
   if(dpi==96&&language==0&&!light)CheckConfigurationPresetApplyLifecycle(form);
   var laptopToggle=panels[0].Controls.Find("manualPerformance_"+LaptopPerfMode.PreferenceKey,true).OfType<Toggle>().Single();
   Check(!laptopToggle.Checked&&!mode.LaptopPerformanceEnabled,"vendor laptop profile must default off");
   Check(PanelForm.PerformanceRestartFor(LaptopPerfMode.PreferenceKey)==PerformanceRestartScope.None,"vendor profile was mislabeled as a reboot setting");
   foreach(var other in panels.Skip(1))Check(other.Controls.Find("manualPerformance_"+LaptopPerfMode.PreferenceKey,true).Length==0,"vendor profile duplicated in another performance category");
   foreach(var key in new[]{LaboratoryPower.EppKey,LaboratoryGpu.ClockKey,LaboratoryGpu.PowerKey,LaboratoryAudio.Key})Check(panels[4].Controls.Find("laboratory_"+key,true).Length==1,"lab toggle missing: "+key);
   Check(panels[4].Controls.Find("laboratoryCpuIdle",true).Length==1,"CPU idle strategy picker missing");
   foreach(var key in new[]{LaboratoryFileFilter.Key,LaboratoryClockRepair.Key,LaboratoryAudioEffects.Key}){
    Check(panels[4].Controls.Find("laboratory_"+key,true).Length==0,"retired manual laboratory card remains: "+key);
    Check(panels[4].Controls.Find("laboratoryApply_"+key,true).Length==0,"retired application control remains: "+key);
    Check(panels[4].Controls.Find("laboratoryRestore_"+key,true).Length==0,"retired manual recovery control remains: "+key);
   }
   Check(!LaboratoryManualActions.Busy,"navigation started manual native inspection");
   Check(panels[1].Controls.Find("privacyToggle"+PrivacyActionCatalog.BackgroundGlobal,true).Length==1,"normal Background UWP control missing");
   Check(panels[3].Controls.Find("privacyToggle"+PrivacyActionCatalog.BackgroundGlobal,true).Length==0&&panels[4].Controls.Find("privacyToggle"+PrivacyActionCatalog.BackgroundGlobal,true).Length==0,"UWP duplicated outside Background");
   form.ShowPageForShot(PageId.Policy);Call(form,"StopPageReveal");Application.DoEvents();var page=Field<DBPanel>(form,"pagePolicy");
   if(dpi==96&&language==0&&!light){categories.Select(0,true);Call(form,"LayoutPerformanceViewport");Application.DoEvents();Screenshot(form,Path.Combine(output,"configuration-presets-compact.png"));panels[0].AutoScrollPosition=new Point(0,1000000);Application.DoEvents();Screenshot(form,Path.Combine(output,"official6-laptop-performance.png"));panels[0].AutoScrollPosition=Point.Empty;}
   foreach(int category in Enumerable.Range(0,5))foreach(int width in new[]{960,560,1240}){
    context=dpi+"/"+language+"/"+light+" performance "+category+" width="+width;
    categories.Select(category,true);page.Width=Theme.S(width);Call(form,"LayoutPerformanceViewport");((PerformanceFeatureGrid)panels[category]).Reflow();Application.DoEvents();CheckConfigurationPresetLayout(form);CheckGrid((PerformanceFeatureGrid)panels[category]);performanceViewports++;
   }
   page.Width=Theme.S(960);categories.Select(4,true);Call(form,"LayoutPerformanceViewport");Application.DoEvents();
   if(dpi==96&&language==0&&!light)Screenshot(form,Path.Combine(output,"official6-laboratory.png"));
   form.ShowPageForShot(PageId.Settings);Call(form,"StopPageReveal");Application.DoEvents();
   Check(form.Controls.Find("HandheldModePicker",true).Length==0&&form.Controls.Find("SettingsHandheld",true).Length==0,"retired handheld adaptation controls remain in Settings");
   var settingsPanels=Field<DBPanel[]>(form,"settingsTabPanels");var settingsTabs=Field<TechTabs>(form,"settingsTabs");var settingsButtons=Field<List<SettingsWorkspaceTab>>(form,"settingsWorkspaceTabs");var settingsPage=Field<DBPanel>(form,"pageSettings");
   Check(settingsPanels.Length==3&&settingsButtons.Count==3,"settings must contain only its three active categories");
   foreach(var settingsPanel in settingsPanels)Check(settingsPanel.Controls.Find("manualPerformance_"+LaptopPerfMode.PreferenceKey,true).Length==0,"vendor profile was added to Settings instead of Performance");
   foreach(int category in Enumerable.Range(0,settingsPanels.Length))foreach(int width in new[]{960,560,1240}){
    context=dpi+"/"+language+"/"+light+" settings "+category+" width="+width;settingsTabs.Index=category;settingsPage.Width=Theme.S(width);Call(form,"LayoutSettingsWorkspace");Application.DoEvents();
    var surface=Field<SettingsWorkspaceSurface>(form,"settingsWorkspaceSurface");Check(settingsPanels[category].Visible,"active settings category hidden");Check(surface.ClientRectangle.Contains(settingsPanels[category].Bounds),"settings panel outside console");
    foreach(var button in settingsButtons){Check(surface.ClientRectangle.Contains(button.Bounds),"settings tab outside console");Check(button.Bottom<=settingsPanels[category].Top,"settings tab overlaps content");}
    for(int i=0;i<settingsButtons.Count;i++)for(int j=i+1;j<settingsButtons.Count;j++)Check(!settingsButtons[i].Bounds.IntersectsWith(settingsButtons[j].Bounds),"settings tabs overlap");
    Check(!settingsPanels[category].HorizontalScroll.Visible,"settings horizontal overflow");
    if(category==0){
     var generalTiles=settingsPanels[category].Controls.OfType<SettingsWorkspaceTile>().ToArray();
     Check(generalTiles.Length==6&&settingsPanels[category].AutoScrollMinSize.Height>Theme.S(200),"general settings content collapsed after removing a device tile");
     foreach(var tile in generalTiles)Check(tile.Width>Theme.S(100)&&tile.Height>=Theme.S(40)&&tile.TitleBounds.Width>0,"general settings tile has no usable layout: "+tile.Name);
     Check(settingsPanels[category].Controls["SettingsAiTuning"].Top>=settingsPanels[category].Controls["SettingsStandbyCleanerCard"].Bottom,"AI tuning overlaps standby cleaner after device tile removal");
    }
    if(category==2){
     var motion=settingsPanels[category].Controls["SettingsCoreMotion"] as MotionAppearancePanel;
     Check(motion!=null&&motion.Visible,"mode animation library missing from Personalization");
     Check(motion.Controls.OfType<MotionPresetButton>().Count()==CoreMotionCatalog.Count,"native and custom designs are not in the same library");
     foreach(Control child in motion.Controls)Check(motion.ClientRectangle.Contains(child.Bounds),"motion editor clips "+child.Name+" at width="+width);
     var tiles=motion.Controls.OfType<MotionPresetButton>().ToArray();
     for(int a=0;a<tiles.Length;a++)for(int b=a+1;b<tiles.Length;b++)Check(!tiles[a].Bounds.IntersectsWith(tiles[b].Bounds),"motion preset cards overlap");
     Check(motion.Preview.Parent==motion,"animation preview lost its native product renderer");
     if(dpi==96&&language==0&&!light&&width==960){
      int motionVersion=Settings.MutationGeneration;int previousMotion=CoreMotionPreferences.Get(motion.EditingMode);
      Settings.UiFixtureRejectedWrite=CoreMotionPreferences.PreferenceKey(motion.EditingMode);
      int nextMotion=previousMotion==11?4:11;
      try{motion.SelectMotion(nextMotion);Check(CoreMotionPreferences.Get(motion.EditingMode)==previousMotion&&motion.Draft==previousMotion,"failed immediate animation save replaced the applied selection or preview");}
      finally{Settings.UiFixtureRejectedWrite=null;}
      motion.SelectMotion(nextMotion);Check(Settings.MutationGeneration>motionVersion&&CoreMotionPreferences.Get(motion.EditingMode)==nextMotion,"animation selection did not apply immediately");
      Check(!motion.Controls.Cast<Control>().Any(c=>c.Name=="MotionApply"),"animation still requires an Apply button");
      settingsPanels[category].ScrollControlIntoView(motion.Preview);Application.DoEvents();
      Screenshot(form,Path.Combine(output,"official6-visual-customization.png"));
     }
    }
    settingsViewports++;
   }
   settingsPage.Width=Theme.S(960);settingsTabs.Index=0;Call(form,"LayoutSettingsWorkspace");Application.DoEvents();if(dpi==96&&language==0&&!light)Screenshot(form,Path.Combine(output,"official6-settings.png"));
   form.ShowPageForShot(PageId.Overview);Call(form,"StopPageReveal");
   Call(form,"PresentOverviewHardware",new OverviewHardwareInfo{CpuName="Intel Core i9-13900K",GpuName="NVIDIA GeForce RTX 3090",PhysicalCores=24,LogicalProcessors=32,MemoryType="DDR5",MemorySpeedMts=6000,InstalledMemoryBytes=34359738368UL,GpuMemoryBytes=25769803776UL,DisplayWidth=2560,DisplayHeight=1440,DisplayRefreshRate=165});
   Call(form,"PresentOverviewResources",new OverviewResourceSnapshot{CpuPercent=28.5,GpuPercent=94.2,TotalMemoryBytes=34359738368UL,UsedMemoryBytes=18000000000UL,GpuUsageDescription="Controlled UI fixture reading"});
   var overview=Field<DBPanel>(form,"pageOverview");foreach(int width in new[]{960,560,1240}){
    context=dpi+"/"+language+"/"+light+" overview width="+width;overview.Width=Theme.S(width);Call(form,"LayoutOverviewPage");Application.DoEvents();
    Check(Field<OverviewSpecItem[]>(form,"overviewSpecs").Length==4,"new overview hardware facts missing");Check(Field<OverviewUsageItem[]>(form,"overviewUsage").Length==3,"new overview live usage rows missing");Check(form.Controls.Find("overviewManualMemoryClean",true).Length==1,"manual memory cleanup button missing");Check(Field<OverviewMemoryCleanupControl>(form,"overviewMemoryCleanup")!=null,"manual cleanup control missing");Check(Field<OverviewTelemetryReader>(form,"overviewReader")==null,"layout fixture unexpectedly polled actual hardware");overviewViewports++;
   }
   overview.Width=Theme.S(960);Call(form,"LayoutOverviewPage");Application.DoEvents();if(dpi==96&&language==0&&!light)Screenshot(form,Path.Combine(output,"official6-overview.png"));
   CheckOverviewGamePause(form,mode,output);
   CheckOverviewGuardActivity(form,mode,output);
   form.ShowPageForShot(PageId.AntiCheat);Call(form,"StopPageReveal");Application.DoEvents();
   var antiCheatPage=Field<DBPanel>(form,"pageAntiCheat");
   var acRows=Field<List<SettingCard>>(form,"acCards").OfType<AntiCheatModuleRow>().ToArray();
   Check(acRows.Length==3,"hard affinity expanded the anti-cheat group boundary");
   foreach(int width in new[]{960,560,1240}){
    context=dpi+"/"+language+"/"+light+" anti-cheat width="+width;antiCheatPage.Width=Theme.S(width);
    for(int i=0;i<acRows.Length;i++){
     acRows[i].SetStatus(PerformancePolicy.Text("已压制 2 · ","Throttled 2 · ")+new[]{PerformancePolicy.Text("硬绑 2","Hard bound 2"),PerformancePolicy.Text("硬绑恢复待核验","Affinity recovery pending"),PerformancePolicy.Text("亲和性冲突","Affinity conflict")}[i],Theme.Dim);
     acRows[i].AdditionalDetails=PerformancePolicy.Text("硬亲和性状态样本；不执行真实绑核","Hard-affinity presentation fixture; no actual affinity writes");
    }
    Call(form,"LayoutAntiCheatViewport");
    foreach(var row in acRows){row.ArrangeContent();Check(row.ClientRectangle.Contains(row.HostBounds),"anti-cheat host clips");Check(row.ClientRectangle.Contains(row.StatusBounds),"anti-cheat status clips");Check(!row.HostBounds.IntersectsWith(row.StatusBounds),"anti-cheat host/status overlap");FitText(row,row.StatusText,row.StatusBounds,Theme.UI(8.5f,false),"anti-cheat status");Check(row.DetailsText.Contains(row.AdditionalDetails),"hard affinity details inaccessible");}
    antiCheatViewports++;
   }
   if(dpi==96&&language==0&&!light)Screenshot(form,Path.Combine(output,"official6-anticheat.png"));
   form.ShowPageForShot(PageId.CoreScheduling);Call(form,"StopPageReveal");Application.DoEvents();Check(Desc(form).Any(c=>c is CoreWorkspaceSurface),"official core workspace missing");
   var help=Field<Label>(form,"criticalThreadHelp");if(help!=null)Check(help.Text.Contains(language==0?"无需补全超线程":"without adding SMT siblings"),"new SMT help text missing");
   Check(!Settings.Load(LaboratoryPower.EppKey,false)&&!Settings.Load(LaboratoryGpu.ClockKey,false)&&!Settings.Load(LaboratoryGpu.PowerKey,false)&&!Settings.Load(LaboratoryAudio.Key,false),"navigation enabled a lab preference");
   Check(!form.UiActive&&!mode.Enabled,"layout navigation activated product workers");
  }
 }
 static void CheckOverviewGuardActivity(PanelForm form,GameMode mode,string output){
  var scan=new ProcessSnapshot(new[]{new ProcEntry{Pid=101,Creation=1001},new ProcEntry{Pid=102,Creation=1002},new ProcEntry{Pid=103,Creation=1003}});
  var receipts=new[]{new KeyValuePair<int,long>(101,1001),new KeyValuePair<int,long>(101,1001),new KeyValuePair<int,long>(102,1002),
   new KeyValuePair<int,long>(103,999),new KeyValuePair<int,long>(104,1004),new KeyValuePair<int,long>(105,0)};
  var snapshot=GuardActivitySnapshot.Create(mode.GuardActivitySession,scan,receipts,new[]{"WSearch","wsearch","",null});
  Check(snapshot.Processes.Length==2&&snapshot.Services.Length==1,"guard counts duplicate actions, dead/reused PIDs, or duplicate services");
  Check(!snapshot.Pending&&!snapshot.Summary.Contains("查看")&&!snapshot.Summary.Contains("Details"),"guard summary advertises a forbidden details entry");
  Check(GuardActivitySnapshot.Create(1,null,receipts,new string[0]).Pending,"missing guard scan is reported as verified zero");
  typeof(ProcessSnapshot).GetField("TakenTicks").SetValue(scan,DateTime.UtcNow.AddMinutes(-1).Ticks);
  Check(GuardActivitySnapshot.Create(1,scan,receipts,new string[0]).Pending,"stale guard scan remains verified");
  var core=(SuppressionCore)typeof(GameMode).GetField("core",Hidden).GetValue(mode);
  var map=(System.Collections.IDictionary)typeof(SuppressionCore).GetField("map",Hidden).GetValue(core);
  Check(map.Count==0,"guard UI fixture unexpectedly owns real process changes");
  Type entryType=typeof(SuppressionCore).GetNestedType("Entry",BindingFlags.NonPublic);
  try{
   for(int i=0;i<5;i++){
    object entry=Activator.CreateInstance(entryType,true);
    entryType.GetField("Reasons").SetValue(entry,i==2?SuppressReason.None:SuppressReason.Background);
    entryType.GetField("OrigPri").SetValue(entry,i==3?uint.MaxValue:128u);
    entryType.GetField("Applied").SetValue(entry,i!=1);
    entryType.GetField("RestoreInFlight").SetValue(entry,i==4);
    entryType.GetField("Creation").SetValue(entry,1001L+i);map.Add(101+i,entry);
   }
   var applied=core.CaptureAppliedActivity();Check(applied.Length==1&&applied[0].Key==101,"failed, unowned, or restoring suppression counted as applied");
  }finally{map.Clear();}
  var active=typeof(GameMode).GetField("active",Hidden);var enabled=typeof(GameMode).GetField("enabled",Hidden);
  var gameName=typeof(GameMode).GetField("activeGame",Hidden);string wasName=(string)gameName.GetValue(mode);
  bool wasActive=(bool)active.GetValue(mode),wasEnabled=(bool)enabled.GetValue(mode);
  var page=Field<DBPanel>(form,"pageOverview");int savedWidth=page.Width;
  try{
   active.SetValue(mode,true);enabled.SetValue(mode,true);gameName.SetValue(mode,"CS2");Call(form,"RefreshOverviewAttention");
   var label=Field<Label>(form,"overviewActivity");
   Check(label.Visible&&!(label is LinkLabel)&&!label.TabStop,"guard summary is missing or exposes interactive details");
   Call(form,"PresentOverviewGuardActivity",snapshot);
   foreach(int width in new[]{560,960,1240}){
    page.Width=Theme.S(width);Call(form,"LayoutOverviewPage");Application.DoEvents();
    Check(label.Top>=Field<Label>(form,"lblStatus").Bottom,"guard counts overlap the main status");
    Check(label.Left>=0&&label.Right<=page.ClientSize.Width,"guard counts exceed the viewport");
    var measured=TextRenderer.MeasureText(label.Text,label.Font,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine);
    Check(measured.Width<=label.Width&&measured.Height<=label.Height,"guard counts clip at width="+width);
    foreach(var item in Field<OverviewSpecItem[]>(form,"overviewSpecs").Cast<Control>().Concat(Field<OverviewUsageItem[]>(form,"overviewUsage")))
     Check(!label.Bounds.IntersectsWith(item.Bounds),"guard counts overlap hardware/usage rows");
   }
   page.Width=savedWidth;Call(form,"LayoutOverviewPage");
   if(Dpi.Scale==1f&&Lang.Cur==0&&!Theme.LightMode)Screenshot(form,Path.Combine(output,"official6-overview-guard-activity.png"));
   enabled.SetValue(mode,false);Call(form,"RefreshOverviewGuardActivity");
   Check(!label.Visible&&Field<GuardActivitySnapshot>(form,"overviewActivitySnapshot")==null,"pausing guard retains session counts");
   enabled.SetValue(mode,true);Call(form,"RefreshOverviewGuardActivity");
   Check(!label.Text.Contains("2"),"resuming guard reuses old session counts");
   active.SetValue(mode,false);Call(form,"RefreshOverviewGuardActivity");Check(!label.Visible,"leaving game retains session counts");
  }finally{
   active.SetValue(mode,wasActive);enabled.SetValue(mode,wasEnabled);gameName.SetValue(mode,wasName);page.Width=savedWidth;
   Call(form,"RefreshOverviewAttention");Call(form,"LayoutOverviewPage");
  }
 }
 static void CheckOverviewGamePause(PanelForm form,GameMode mode,string output){
  var activeField=typeof(GameMode).GetField("active",Hidden);
  bool wasActive=(bool)activeField.GetValue(mode);
  var usage=Field<OverviewUsageItem[]>(form,"overviewUsage");
  var specs=Field<OverviewSpecItem[]>(form,"overviewSpecs");
  string[] models=specs.Select(s=>s.Value).ToArray();
  string status=Lang.Cur==1?"In game":Lang.Cur==2?"ゲーム中":"游戏中";
  var reading=new OverviewResourceSnapshot{CpuPercent=28,GpuPercent=94,TotalMemoryBytes=34359738368UL,UsedMemoryBytes=18000000000UL};
  Action drain=delegate{var pending=form.UiFixtureOverviewWork.ToArray();form.UiFixtureOverviewWork.Clear();foreach(var work in pending)work();};
  try{
   activeField.SetValue(mode,false);Call(form,"SetOverviewTelemetryActive",true);
   Check(form.UiFixtureOverviewWork.Count==1,"idle overview did not queue its normal read");
   int idleGeneration=Field<int>(form,"overviewHudGeneration");
   activeField.SetValue(mode,true);
   // Run the already queued read before the UI has observed game entry.
   drain();
   Check(Field<OverviewTelemetryReader>(form,"overviewReader")==null,"queued overview read entered hardware APIs after game start");
   Call(form,"QueueOverviewTelemetry",false);drain();
   Check(Field<bool>(form,"overviewGamePaused")&&Field<int>(form,"overviewHudGeneration")!=idleGeneration,"game entry did not invalidate pending reads");
   Check(usage.All(u=>u.Value==status&&!u.Percent.HasValue&&u.Detail.Length==0),"in-game overview shows stale resource numbers or gauges");
   Call(form,"PresentOverviewResources",reading);
   Check(usage.All(u=>u.Value==status&&!u.Percent.HasValue)&&Field<OverviewResourceSnapshot>(form,"overviewResources")==null,"late resource result overwrote game status");
   for(int i=0;i<3;i++)Call(form,"QueueOverviewTelemetry",true);
   Check(form.UiFixtureOverviewWork.Count==0&&Field<OverviewTelemetryReader>(form,"overviewReader")==null,"game overview queued further hardware reads");
   Check(specs.Select(s=>s.Value).SequenceEqual(models),"game pause discarded cached hardware model labels");
   int savedLanguage=Lang.Cur;
   try{
    foreach(int gameLanguage in new[]{0,1,2}){
     Lang.Cur=gameLanguage;Call(form,"PresentOverviewResources",reading);
     string localizedStatus=new[]{"游戏中","In game","ゲーム中"}[gameLanguage];
     Check(usage.All(u=>u.Value==localizedStatus&&!u.Percent.HasValue),"localized game status is missing");
     Field<DBPanel>(form,"pageOverview").Width=Theme.S(560);Call(form,"LayoutOverviewPage");
     foreach(var item in usage)FitText(item,item.Value,item.ValueBounds,Theme.UI(item.ValuePointSize,true),"localized overview game status");
    }
   }finally{Lang.Cur=savedLanguage;Call(form,"PresentOverviewResources",reading);}
   var dormantReader=new OverviewTelemetryReader();typeof(PanelForm).GetField("overviewReader",Hidden).SetValue(form,dormantReader);
   Call(form,"ReleaseOverviewTelemetryReader");drain();
   bool disposed=false;try{dormantReader.ResetCounters();}catch(ObjectDisposedException){disposed=true;}
   Check(disposed&&Field<OverviewTelemetryReader>(form,"overviewReader")==null,"game pause retained the overview reader/counter lifetime");
   foreach(int width in new[]{560,960,1240}){
    Field<DBPanel>(form,"pageOverview").Width=Theme.S(width);Call(form,"LayoutOverviewPage");
    foreach(var item in usage){Check(item.ValueBounds.Height>0,"game status has no value bounds");FitText(item,item.Value,item.ValueBounds,Theme.UI(item.ValuePointSize,true),"overview game status");}
   }
   if(Dpi.Scale==1f&&Lang.Cur==0&&!Theme.LightMode)Screenshot(form,Path.Combine(output,"official6-overview-game.png"));
   // Hidden exit queues no sensor work; re-opening starts from an empty baseline.
   Call(form,"SetOverviewTelemetryActive",false);drain();activeField.SetValue(mode,false);
   Call(form,"QueueOverviewTelemetry",false);
   Check(form.UiFixtureOverviewWork.Count==0&&!Field<bool>(form,"overviewGamePaused"),"hidden game exit started an overview sample");
   Check(usage.All(u=>u.Value=="--"&&!u.Percent.HasValue),"game exit reused pre-game readings");
   Call(form,"SetOverviewTelemetryActive",true);
   Check(form.UiFixtureOverviewWork.Count==1,"visible overview did not resume after the game ended");
   Call(form,"SetOverviewTelemetryActive",false);drain();
   Check(Field<OverviewTelemetryReader>(form,"overviewReader")==null,"page hide admitted a pending overview read");
   Call(form,"PresentOverviewResources",reading);
   Check(usage[0].Value=="28%"&&usage[1].Value=="94%","idle resource presentation did not recover");
  }finally{
   activeField.SetValue(mode,wasActive);Call(form,"SetOverviewTelemetryActive",false);drain();
   Call(form,"PresentOverviewResources",reading);
  }
 }
 static void CheckConfigurationPresetLayout(PanelForm form){
  var strip=Field<ConfigurationPresetStrip>(form,"configurationPresetStrip");
  var tabs=Field<PerformanceCategoryBar>(form,"performanceCategories");
  var panels=Field<DBPanel[]>(form,"performanceTabPanels");
  Check(strip!=null&&strip.Visible&&strip.Parent==tabs.Parent,"preset is missing from category row");
  Check(strip.Height==Theme.S(32)&&tabs.Height==Theme.S(44),"preset grew into a large panel");
  Check(strip.Top>=tabs.Top&&strip.Bottom<=tabs.Bottom,"preset is not on the category row");
  Check(!strip.Bounds.IntersectsWith(tabs.Bounds),"preset overlaps category tabs");
  Check(strip.Right<=strip.Parent.ClientSize.Width,"preset exceeds page width");
  Check(panels[0].Top==Theme.S(78),"preset added vertical page height");
  Check(strip.Controls.Count==1&&strip.ClientRectangle.Contains(strip.GuideButton.Bounds),"single guide button is absent or clipped");
  Check(strip.GuideButton.Text==Lang.T("configpreset.guide"),"category row still exposes a preset selector or apply button");
  foreach(PerformanceActionButton tab in tabs.Controls){
   int inset=Theme.S(tab.CompactCategory?6:18);
   int needed=TextRenderer.MeasureText(tab.Text,Theme.UI(tab.CategoryTextSize,tab.CurrentCategory),Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine).Width;
   Check(needed<=tab.Width-inset*2,"category text clipped beside preset: "+tab.Text+" needed="+needed+" available="+(tab.Width-inset*2));
   Check(!string.IsNullOrEmpty(tab.AccessibleName),"compact category lost its full accessible name");
  }
 }
 static void CheckConfigurationPresetInteraction(PanelForm form){
  var strip=Field<ConfigurationPresetStrip>(form,"configurationPresetStrip");
  int version=Settings.MutationGeneration;
  var savedGuide=form.ConfigurationPresetGuideForTest;int opened=0;
  form.ConfigurationPresetGuideForTest=delegate(ConfigurationPresetGuideDialog guide){
   opened++;
   Check(!guide.Busy&&guide.AuthorButton.Enabled&&guide.ExtremeButton.Enabled,"guide opens in an applying state");
   Check(guide.AuthorButton.AccessibleName.Contains(Lang.T("configpreset.default"))&&guide.ExtremeButton.AccessibleName.Contains(Lang.T("configpreset.extreme")),"guide lacks both named choices");
  };
  try{strip.GuideButton.PerformClick();}finally{form.ConfigurationPresetGuideForTest=savedGuide;}
  Check(opened==1&&Settings.MutationGeneration==version&&!Field<ConfigurationPresetRuntime>(form,"configurationPresetRuntime").Busy,"opening the guide changed settings");
  using(var isolated=new ConfigurationPresetStrip()){
   isolated.Size=new Size(isolated.PreferredStripWidth,Theme.S(32));
   int clicks=0;isolated.GuideRequested+=delegate{clicks++;};
   isolated.SetState(ConfigurationPresetKind.Default,false,true,"");
   Check(isolated.AccessibleDescription.Contains(Lang.T("configpreset.default")),"current preset is not accessible");
   isolated.GuideButton.PerformClick();Check(clicks==1,"guide entry did not dispatch exactly once");
   isolated.SetState(ConfigurationPresetKind.Default,true,true,"");
   isolated.GuideButton.PerformClick();
   Check(clicks==1&&!isolated.GuideButton.Enabled,"busy preset accepts another operation");
   isolated.SetState(null,false,true,"");
   Check(isolated.AccessibleDescription.Contains(Lang.T("configpreset.custom")),"manual configuration lost custom presentation");
   isolated.SetState(null,false,false,"");isolated.GuideButton.PerformClick();
   Check(clicks==1&&!isolated.GuideButton.Enabled,"shutdown preset remains interactive");
  }
  Check(Settings.MutationGeneration==version,"preset-only UI test wrote preferences");
 }
 sealed class PresetUiBackend:IConfigurationPresetBackend{
  internal readonly Dictionary<string,string> Values=new Dictionary<string,string>();
  internal int Version,Writes;internal bool FailWrites;string identity="";readonly object sync=new object();
  internal PresetUiBackend(){foreach(var e in ConfigurationPresetCatalog.Entries)Values[e.Key]=e.DefaultValue;}
  public string Read(ConfigurationPresetEntry e){lock(sync)return Values[e.Key];}
  public ConfigurationPresetAvailability Inspect(ConfigurationPresetEntry e){return ConfigurationPresetAvailability.Yes();}
  public bool Write(ConfigurationPresetEntry e,string value,Func<bool> cancelled,out string detail){detail=FailWrites?"Fixture preset write failed":"UI fixture";if(cancelled()||FailWrites)return false;lock(sync){Values[e.Key]=value;Writes++;Interlocked.Increment(ref Version);return true;}}
  public bool SaveIdentity(string value){lock(sync){identity=value;Interlocked.Increment(ref Version);return true;}}
  public string LoadIdentity(){lock(sync)return identity;}
  internal void ManualChange(){lock(sync){Values["GmAwake"]="0";Interlocked.Increment(ref Version);}}
 }
 static void CheckConfigurationPresetApplyLifecycle(PanelForm form){
  var member=typeof(PanelForm).GetField("configurationPresetRuntime",Hidden);
  var saved=(ConfigurationPresetRuntime)member.GetValue(form);
  var strip=Field<ConfigurationPresetStrip>(form,"configurationPresetStrip");
  var fake=new PresetUiBackend();int dialogs=0,admissions=0;
  var savedDialog=PaviseDialog.UiFixtureShow;
  var savedGuide=form.ConfigurationPresetGuideForTest;
  var savedRestartReminder=form.ConfigurationPresetRestartReminderForTest;int restartReminders=0;
  string savedResult=Field<string>(form,"configurationPresetLastResult");
  string savedIssue=Field<string>(form,"configurationPresetLastIssue");
  int settingsVersion=Settings.MutationGeneration;
  using(var release=new ManualResetEvent(false)){
   var runtime=new ConfigurationPresetRuntime(fake,()=>Volatile.Read(ref fake.Version),work=>{Interlocked.Increment(ref admissions);release.WaitOne();return work();});
   member.SetValue(form,runtime);
   PaviseDialog.UiFixtureShow=delegate(IWin32Window owner,string title,string report,Control content,int width){dialogs++;};
   form.ConfigurationPresetRestartReminderForTest=delegate(string title,string body){restartReminders++;Check(title.Length>0&&body.Length<180&&!body.Contains("Fixture preset write failed"),"restart reminder leaks execution details");};
   try{
    form.ConfigurationPresetGuideForTest=delegate(ConfigurationPresetGuideDialog guide){
     guide.Show();guide.ExtremeButton.PerformClick();
     Check(runtime.Busy&&guide.Busy&&!strip.GuideButton.Enabled&&!guide.ExtremeButton.Enabled&&!guide.AuthorButton.Enabled,"guide did not acquire busy before returning");
     guide.ExtremeButton.PerformClick();strip.GuideButton.PerformClick();
     release.Set();PresetUiPump(delegate{return !runtime.Busy&&strip.GuideButton.Enabled;});
     Check(!guide.Visible,"successful setup did not close its guide");
    };
    strip.GuideButton.PerformClick();
    Check(!runtime.Busy&&dialogs==0&&admissions==1,"preset click duplicated or opened a completion popup");
    Check(restartReminders==1,"completed setup did not request a PC restart once");
    Check(fake.Writes==34&&Field<string>(form,"configurationPresetLastResult").Contains(ConfigurationPresetCatalog.Name(ConfigurationPresetKind.Extreme)),"real guide handler did not save the remaining coordinator entries");
    Check(runtime.CurrentPreset==ConfigurationPresetKind.Extreme,"successful application lost preset identity");
    fake.ManualChange();DateTime deadline=DateTime.UtcNow.AddSeconds(3);
    while(runtime.CurrentPreset.HasValue&&DateTime.UtcNow<deadline){Application.DoEvents();Thread.Sleep(5);}
    Call(form,"SyncConfigurationPresetControls");
    Check(strip.AccessibleDescription.Contains(Lang.T("configpreset.custom")),"manual edit did not display custom");
    release.Reset();Call(form,"ApplyConfigurationPreset",ConfigurationPresetKind.Default);
    Check(runtime.Busy,"second lifecycle operation was not admitted");
    int writes=fake.Writes;Call(form,"CancelConfigurationPresetWork");release.Set();
    Check(runtime.WaitForCompletion(3000),"cancelled application did not drain");
    deadline=DateTime.UtcNow.AddMilliseconds(150);while(DateTime.UtcNow<deadline){Application.DoEvents();Thread.Sleep(5);}
    Check(fake.Writes==writes&&dialogs==0,"cancelled late operation wrote settings or presented a stale dialog");
    // Hold the UI queue while two operations finish: only the latest result may present.
    Call(form,"SyncConfigurationPresetControls");
    Call(form,"ApplyConfigurationPreset",ConfigurationPresetKind.Extreme);Check(runtime.WaitForCompletion(3000),"first delayed result did not finish");
    Call(form,"ApplyConfigurationPreset",ConfigurationPresetKind.Default);Check(runtime.WaitForCompletion(3000),"second delayed result did not finish");
    PresetUiPump(delegate{return strip.GuideButton.Enabled&&Field<string>(form,"configurationPresetLastResult").StartsWith(ConfigurationPresetCatalog.Name(ConfigurationPresetKind.Default));});
    Check(dialogs==0,"a delayed result presented a completion popup");
    // A failed application stays in the same guide and can be retried.
    fake.FailWrites=true;
    form.ConfigurationPresetGuideForTest=delegate(ConfigurationPresetGuideDialog guide){
     guide.Show();guide.AuthorButton.PerformClick();PresetUiPump(delegate{return !runtime.Busy&&!guide.Busy;});
     Check(guide.Visible&&guide.AuthorButton.Enabled&&Desc(guide).Any(c=>c.Text.Contains(PerformancePolicy.Text("部分设置未能应用", "Some settings could not be applied")))
      &&!Desc(guide).Any(c=>c.Text.Contains("Fixture preset write failed")),"failed setup must show a concise retry hint without execution details");
     fake.FailWrites=false;guide.ExtremeButton.PerformClick();PresetUiPump(delegate{return !runtime.Busy&&strip.GuideButton.Enabled;});
     Check(!guide.Visible&&dialogs==0,"retry did not finish without a popup");
    };
    strip.GuideButton.PerformClick();
    // Closing the guide while applying leaves the owned background transaction intact.
    release.Reset();form.ConfigurationPresetGuideForTest=delegate(ConfigurationPresetGuideDialog guide){guide.Show();guide.AuthorButton.PerformClick();guide.Close();};
    strip.GuideButton.PerformClick();Check(runtime.Busy,"closing guide unexpectedly abandoned the application");
    release.Set();PresetUiPump(delegate{return !runtime.Busy&&strip.GuideButton.Enabled;});
    Check(dialogs==0,"closed guide caused a late completion popup");
    Check(Settings.MutationGeneration==settingsVersion,"preset lifecycle fixture reached real preferences");
   }finally{
    release.Set();runtime.Cancel();runtime.WaitForCompletion(3000);
    member.SetValue(form,saved);PaviseDialog.UiFixtureShow=savedDialog;form.ConfigurationPresetGuideForTest=savedGuide;form.ConfigurationPresetRestartReminderForTest=savedRestartReminder;
    typeof(PanelForm).GetField("configurationPresetLastResult",Hidden).SetValue(form,savedResult);
    typeof(PanelForm).GetField("configurationPresetLastIssue",Hidden).SetValue(form,savedIssue);
    Call(form,"SyncConfigurationPresetControls");
   }
  }
 }
 static void PresetUiPump(Func<bool> complete){
  DateTime deadline=DateTime.UtcNow.AddSeconds(10);
  while(!complete()&&DateTime.UtcNow<deadline){Application.DoEvents();Thread.Sleep(5);}
  Application.DoEvents();Check(complete(),"preset UI callback did not complete");
 }
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern IntPtr CreateDesktop(string name,IntPtr device,IntPtr mode,uint flags,uint access,IntPtr security);
 [DllImport("user32.dll")]static extern bool SetThreadDesktop(IntPtr desktop);
 [DllImport("user32.dll")]static extern bool CloseDesktop(IntPtr desktop);
 static int RunPrivateUi(string[] args,IntPtr desktop){
  try{
   if(desktop!=IntPtr.Zero&&!SetThreadDesktop(desktop))throw new InvalidOperationException("private desktop refused");
   Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
   Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);Dpi.Init();Lang.Init();Directory.CreateDirectory(args[0]);
   bool single=args.Length>1&&args[1]=="--single";bool windowOnly=args.Length>1&&args[1]=="--window-only";
   if(args.Length>1&&args[1]=="--release-notes-only"){RunReleaseNotesOnce(args[0]);return 0;}
   if(args.Length>1&&args[1]=="--dialog-only"){RunDialogReadability(args[0]);return 0;}
   if(args.Length>1&&args[1]=="--ai-prompt-only"){RunAiTuningPromptChecks(args[0]);return 0;}
   if(args.Length>1&&args[1]=="--backdrop-preview"){RunBackdropPreview(args[0],args.Length>2?args[2]:null);return 0;}
   RunLegacySizingModels();
   if(windowOnly)RunWindowProtocol(args[0]);
   else if(args.Length>2&&args[1]=="--dpi")Run(args[0],int.Parse(args[2]),0,false);
   else foreach(int dpi in single?new[]{96}:new[]{96,144,192})foreach(int language in single?new[]{0}:new[]{0,1})foreach(bool light in single?new[]{false}:new[]{false,true})Run(args[0],dpi,language,light);
   Console.WriteLine("PASS official6 UI assertions="+checks+" performance_viewports="+performanceViewports+" settings_viewports="+settingsViewports+" overview_viewports="+overviewViewports+" window_protocol_cases="+windowProtocolCases+" sizing_model_cases="+sizingModelCases+" private_desktop="+(desktop!=IntPtr.Zero).ToString().ToLowerInvariant()+" cloaked_window="+windowOnly.ToString().ToLowerInvariant()+" transient_settings=true product_workers=false");return 0;
  }catch(Exception error){Console.Error.WriteLine("Context: "+context+Environment.NewLine+error);return 1;}
  finally{Console.Out.Flush();Console.Error.Flush();}
 }
 [STAThread]static int Main(string[] args){
  if(args.Length==1&&args[0]=="--negative-control"){try{Check(false,"intentional UI migration negative control");return 99;}catch(InvalidOperationException error){Console.WriteLine("FAIL "+error.Message);return 3;}}
  if(args.Length>1&&args[1]=="--window-only")return RunPrivateUi(args,IntPtr.Zero);
  IntPtr desktop=CreateDesktop("PaviseOfficialUi-"+Guid.NewGuid().ToString("N"),IntPtr.Zero,IntPtr.Zero,0,0x01ff,IntPtr.Zero);if(desktop==IntPtr.Zero)return 2;
  int result=1;try{var worker=new Thread(delegate(){result=RunPrivateUi(args,desktop);Environment.Exit(result);});worker.SetApartmentState(ApartmentState.STA);worker.Start();worker.Join();return result;}
  finally{CloseDesktop(desktop);}
 }
}
}
