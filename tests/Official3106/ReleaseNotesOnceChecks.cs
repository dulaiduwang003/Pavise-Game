using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace PaviseApp {
internal static partial class Official3106UiChecks {
 const string NotesSeenKey="LastSeenNotesVersion";
 static int notesPresentations,notesForms;
 static void SetField(PanelForm form,string name,object value){typeof(PanelForm).GetField(name,Hidden).SetValue(form,value);}
 static bool NotesReady(PanelForm form){return (bool)typeof(PanelForm).GetMethod("ReadyForReleaseNotes",Hidden).Invoke(form,null);}
 static void DrainNotes(){Application.DoEvents();Application.DoEvents();}
 static void OpenNotes(PanelForm form){form.ShowPanel();DrainNotes();}
 static void WithNotesForm(string output,Action<PanelForm> action){
  string folder=Path.Combine(output,"release-notes-state-"+(++notesForms));Directory.CreateDirectory(folder);Paths.Data=folder;
  var core=new SuppressionCore();var mode=new GameMode(folder,core);
  using(var icon=IconArt.MakeIcon(24))using(var form=new PanelForm(new Tamer(core),mode,icon,false)){
   form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-32000,-32000);form.ShowInTaskbar=false;
   action(form);Check(!form.UiActive&&!mode.Enabled,"release notes admitted product workers");
  }
 }
 static void ResetNotes(){Settings.ResetUiFixtureMemory();notesPresentations=0;PaviseDialog.UiFixtureShow=delegate(IWin32Window owner,string title,string body,Control content,int width){
  notesPresentations++;var current=ReleaseNotes.Current;
  Check(current!=null&&title.Contains(current.Tag),"dialog did not identify the current version");
  Check(body.Contains(current.Date)&&body.Contains(current.Item(0)),"dialog did not use current localized release notes");
  Check(content==null&&width==640,"release note presentation contract changed");
 };}
 static void RunReleaseNotesOnce(string output){
  context="release notes once";int initial=checks;Logger.WritesEnabled=false;Dpi.NoFit=true;Dpi.Scale=1f;Theme.DropFontCache();Lang.Cur=0;Theme.SetLight(false);UiClock.Frozen=true;
  try{
   Check(ReleaseNotes.Current!=null,"current version has no release notes");
   ResetNotes();
   WithNotesForm(output,delegate(PanelForm form){
    var handle=form.Handle;DrainNotes();Check(notesPresentations==0&&ReleaseNotes.HasUnseen,"hidden background startup consumed notes");
    OpenNotes(form);Check(notesPresentations==1&&!ReleaseNotes.HasUnseen,"first main-window open did not show and acknowledge notes");
    Check(Settings.LoadStr(NotesSeenKey,"")==App.Version,"current version acknowledgement missing");
    form.ShowPanel();form.ShowPanel();DrainNotes();Check(notesPresentations==1,"repeated open showed notes twice");
    form.Hide();OpenNotes(form);Check(notesPresentations==1,"tray reopen repeated notes");
   });
   WithNotesForm(output,delegate(PanelForm form){OpenNotes(form);Check(notesPresentations==1,"a new instance repeated the acknowledged version");});

   ResetNotes();Settings.SaveStr(NotesSeenKey,"3.2.0.1");
   WithNotesForm(output,delegate(PanelForm form){OpenNotes(form);Check(notesPresentations==1&&!ReleaseNotes.HasUnseen,"upgrade did not show current notes");});

   ResetNotes();
   WithNotesForm(output,delegate(PanelForm form){
    form.Show();DrainNotes();Check(notesPresentations==0,"unarmed direct show opened notes");
    Call(form,"ShowReleaseNotes");Check(notesPresentations==1&&!ReleaseNotes.HasUnseen,"manual About presentation did not acknowledge notes");
    OpenNotes(form);Check(notesPresentations==1,"automatic presentation repeated manually read notes");
    Call(form,"ShowReleaseNotes");Check(notesPresentations==2,"manual release notes became unavailable after acknowledgement");
   });

   ResetNotes();
   WithNotesForm(output,delegate(PanelForm form){
    var normalPresenter=PaviseDialog.UiFixtureShow;
    PaviseDialog.UiFixtureShow=delegate(IWin32Window owner,string title,string body,Control content,int width){
     normalPresenter(owner,title,body,content,width);Check(ReleaseNotes.HasUnseen,"notes acknowledged before presentation returned");
     form.ShowPanel();Call(form,"ShowReleaseNotes");DrainNotes();Check(notesPresentations==1,"modal message-pump reentry duplicated notes");
    };
    OpenNotes(form);Check(notesPresentations==1&&!ReleaseNotes.HasUnseen,"reentrant presentation did not complete");
   });

   ResetNotes();
   WithNotesForm(output,delegate(PanelForm form){
    form.Show();DrainNotes();SetField(form,"releaseNotesAutoRequested",true);
    foreach(string flag in new[]{"shotMode","introPending","introActive","outroActive","moveSizeLoop","runtimeShutdownBusy"}){
     SetField(form,flag,true);Check(!NotesReady(form),flag+" admitted automatic notes");Call(form,"QueueUnseenReleaseNotes");DrainNotes();
     Check(notesPresentations==0&&ReleaseNotes.HasUnseen,flag+" consumed unseen notes");SetField(form,flag,false);
    }
    SetField(form,"introPending",true);form.WindowState=FormWindowState.Minimized;SetField(form,"introPending",false);
    Check(!NotesReady(form),"minimized window admitted notes");Call(form,"QueueUnseenReleaseNotes");DrainNotes();Check(notesPresentations==0&&ReleaseNotes.HasUnseen,"minimized window consumed notes");
    SetField(form,"introPending",true);form.WindowState=FormWindowState.Normal;form.Hide();SetField(form,"introPending",false);
    Check(!NotesReady(form),"hidden window admitted notes");Call(form,"QueueUnseenReleaseNotes");DrainNotes();Check(notesPresentations==0&&ReleaseNotes.HasUnseen,"hidden window consumed notes");
    OpenNotes(form);Check(notesPresentations==1&&!ReleaseNotes.HasUnseen,"deferred notes did not resume on main-window open");
   });

   ResetNotes();
   WithNotesForm(output,delegate(PanelForm form){
    form.ShowPanel();form.Hide();DrainNotes();Check(notesPresentations==0&&ReleaseNotes.HasUnseen,"stale queued callback opened on a hidden window");
    OpenNotes(form);Check(notesPresentations==1,"hidden queued callback prevented later presentation");
   });

   ResetNotes();
   WithNotesForm(output,delegate(PanelForm form){
    using(var other=new Form()){other.ShowInTaskbar=false;other.StartPosition=FormStartPosition.Manual;other.Location=new Point(-32000,-32000);other.Show();OpenNotes(form);
     Check(notesPresentations==0&&ReleaseNotes.HasUnseen,"automatic notes interrupted an existing dialog");other.Hide();}
    Call(form,"OnActivated",EventArgs.Empty);DrainNotes();Check(notesPresentations==1&&!ReleaseNotes.HasUnseen,"dialog deferral did not retry on activation");
   });

   ResetNotes();Settings.UiFixtureRejectedWrite=NotesSeenKey;
   WithNotesForm(output,delegate(PanelForm form){
    OpenNotes(form);Check(notesPresentations==1&&ReleaseNotes.HasUnseen,"failed persistence falsely acknowledged notes");
    OpenNotes(form);Check(notesPresentations==1,"failed persistence repeated notes within the same instance");
   });
   Settings.UiFixtureRejectedWrite=null;
   WithNotesForm(output,delegate(PanelForm form){OpenNotes(form);Check(notesPresentations==2&&!ReleaseNotes.HasUnseen,"failed persistence was not retried by the next instance");});

   ResetNotes();
   WithNotesForm(output,delegate(PanelForm form){
    var normalPresenter=PaviseDialog.UiFixtureShow;bool fail=true;
    PaviseDialog.UiFixtureShow=delegate(IWin32Window owner,string title,string body,Control content,int width){if(fail){fail=false;notesPresentations++;throw new InvalidOperationException("intentional notes presenter failure");}normalPresenter(owner,title,body,content,width);};
    OpenNotes(form);Check(notesPresentations==1&&ReleaseNotes.HasUnseen&&!Field<bool>(form,"releaseNotesAutoHandled"),"failed presentation consumed notes");
    OpenNotes(form);Check(notesPresentations==2&&!ReleaseNotes.HasUnseen,"failed presentation did not retry on the next open");
   });

   ResetNotes();ReleaseNote original=ReleaseNotes.Current;int currentIndex=Array.IndexOf(ReleaseNotes.All,original);
   try{ReleaseNotes.All[currentIndex]=new ReleaseNote("fixture-unpublished-version","",new string[0][]);
    WithNotesForm(output,delegate(PanelForm form){OpenNotes(form);Check(notesPresentations==0&&ReleaseNotes.HasUnseen&&!Field<bool>(form,"releaseNotesAutoHandled"),"missing current notes consumed acknowledgement");});
   }finally{ReleaseNotes.All[currentIndex]=original;}

   ResetNotes();
   WithNotesForm(output,delegate(PanelForm form){var handle=form.Handle;form.Dispose();Call(form,"QueueUnseenReleaseNotes");DrainNotes();Check(notesPresentations==0&&ReleaseNotes.HasUnseen,"disposed window consumed notes");});
   Console.WriteLine("PASS release notes once assertions="+(checks-initial)+" presentations=callback registry=in-memory private_desktop=true product_workers=false");
  }finally{PaviseDialog.UiFixtureShow=null;UiClock.Frozen=false;Dpi.NoFit=true;}
 }
}
}
