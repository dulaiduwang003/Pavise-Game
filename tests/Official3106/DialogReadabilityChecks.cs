using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace PaviseApp {
internal static partial class Official3106UiChecks {
 static PaviseDialog MakeDialog(string title,string body,Control extra,bool cancel){
  return (PaviseDialog)Activator.CreateInstance(typeof(PaviseDialog),BindingFlags.Instance|BindingFlags.NonPublic,null,
   new object[]{title,body,DlgKind.Info,null,cancel?Lang.T("dlg.cancel"):null,extra,640,null,null},null);
 }
 static string LongDialogBody(int language){
  string[] samples={"降低游戏过程中的后台负载，保留原有设置；退出游戏后恢复。滚动区域应保留全部说明，按钮始终可见。", "Reduce background load during a game and retain the original settings. Restore them after the game; all details remain readable while the buttons stay visible.", "ゲーム中のバックグラウンド負荷を減らし、元の設定を保持します。ゲーム終了時に復元し、説明をスクロールして確認できます。"};
  var text=new StringBuilder();for(int i=0;i<48;i++)text.Append(i+1).Append(". ").Append(samples[language]).AppendLine().AppendLine();
  return text.Append("END-OF-DIALOG / 最后一行 / 最後の行").ToString();
 }
 static void ShowFixtureDialog(PaviseDialog dialog){
  dialog.StartPosition=FormStartPosition.Manual;dialog.Location=new Point(-32000,-32000);dialog.ShowInTaskbar=false;
  dialog.Show();Application.DoEvents();dialog.PerformLayout();Application.DoEvents();
 }
 static void CheckDialogFrame(PaviseDialog dialog){
  Rectangle work=Screen.FromPoint(Cursor.Position).WorkingArea;
  Check(dialog.Height<=Theme.S(640)&&dialog.Height<=Math.Floor(work.Height*.8),"dialog exceeds logical or working-area height limit");
  Check(dialog.Width<=work.Width-Theme.S(40),"dialog exceeds working-area width");
  Check(dialog.AutoScaleMode==AutoScaleMode.None,"dialog applies a second font autoscale pass");
  Control accept=dialog.Controls["dialogAccept"],cancel=dialog.Controls["dialogCancel"];
  Check(accept!=null&&dialog.ClientRectangle.Contains(accept.Bounds),"dialog accept button is clipped");
  if(cancel!=null){Check(dialog.ClientRectangle.Contains(cancel.Bounds),"dialog cancel button is clipped");Check(!accept.Bounds.IntersectsWith(cancel.Bounds),"dialog buttons overlap");}
  var viewport=dialog.Controls["DialogContentViewport"] as ScrollableControl;
  Check(viewport!=null&&dialog.ClientRectangle.Contains(viewport.Bounds),"dialog content viewport is clipped or absent");
  Check(viewport.Bottom<accept.Top,"dialog content covers its fixed footer");
  Check(!viewport.HorizontalScroll.Visible,"dialog text needs horizontal scrolling");
 }
 static void CheckDialogScroll(PaviseDialog dialog,string body,bool expectedScroll){
  var viewport=(ScrollableControl)dialog.Controls["DialogContentViewport"];
  Control text=viewport.Controls["DialogBodyText"];
  Check(text!=null&&text.Text==body,"dialog body lost or changed text");
  Check(viewport.VerticalScroll.Visible==expectedScroll,"dialog scroll state does not match its content");
  Rectangle button=dialog.Controls["dialogAccept"].Bounds;
  if(expectedScroll){
   Check(viewport.AutoScrollMinSize.Height>viewport.ClientSize.Height,"long dialog does not have a full scroll range");
   viewport.AutoScrollPosition=new Point(0,1000000);Application.DoEvents();
   Check(viewport.AutoScrollPosition.Y<0,"dialog content did not scroll to the end");
   int contentBottom=viewport.Controls.Cast<Control>().Max(c=>c.Bottom);
   Check(contentBottom<=viewport.ClientSize.Height&&contentBottom>0,"last dialog content is inaccessible after scrolling");
   Check(dialog.Controls["dialogAccept"].Bounds==button,"dialog footer moves with the content");
   viewport.AutoScrollPosition=Point.Empty;Application.DoEvents();
  }
 }
 static string CurrentDialogNotes(){
  var current=ReleaseNotes.Current;var body=new StringBuilder(Lang.F("notes.sub",current.Tag)+"  |  "+current.Date+Environment.NewLine+Environment.NewLine);
  for(int i=0;i<current.Count;i++)body.Append(i+1).Append(". ").Append(current.Item(i)).AppendLine().AppendLine();return body.ToString().TrimEnd();
 }
 static void CheckPresetGuideReadability(string output,int dpi,int language){
  using(var guide=new ConfigurationPresetGuideDialog(ConfigurationPresetKind.Default)){
   guide.StartPosition=FormStartPosition.Manual;guide.Location=new Point(-32000,-32000);guide.ShowInTaskbar=false;
   guide.Show();Application.DoEvents();Size natural=guide.ClientSize;
   foreach(int width in new[]{Math.Min(natural.Width,Theme.S(340)),natural.Width}){
    guide.ClientSize=new Size(width,natural.Height);Application.DoEvents();
    var viewport=(ScrollableControl)guide.Controls["PresetGuideBody"];
    Check(guide.ClientRectangle.Contains(viewport.Bounds)&&!viewport.HorizontalScroll.Visible,"guide body clips or scrolls horizontally");
    Check(!guide.AuthorButton.Bounds.IntersectsWith(guide.ExtremeButton.Bounds),"guide choice cards overlap");
    Check(viewport.Controls.Cast<Control>().All(c=>c.Left>=0&&c.Right<=viewport.ClientSize.Width),"guide card exceeds its viewport");
    viewport.ScrollControlIntoView(guide.ExtremeButton);Application.DoEvents();
    Check(guide.ExtremeButton.Bottom<=viewport.ClientSize.Height,"stacked extreme choice cannot be reached");
    viewport.AutoScrollPosition=Point.Empty;
    var close=guide.Controls["PresetGuideClose"];
    Check(guide.ClientRectangle.Contains(close.Bounds)&&!close.Bounds.IntersectsWith(viewport.Bounds),"guide close button clips or overlaps choices");
   }
   Screenshot(guide,System.IO.Path.Combine(output,"preset-guide-"+dpi+"-"+language+".png"));
   guide.SetBusy(true);Check(guide.Busy&&!guide.AuthorButton.Enabled&&!guide.ExtremeButton.Enabled&&guide.Controls["PresetGuideClose"].Enabled,"guide busy state disables closing or permits duplicate application");
   guide.ShowIssue(LongDialogBody(language));Application.DoEvents();
   var status=(TextBox)guide.Controls["PresetGuideStatus"];
   Check(!guide.Busy&&status.ReadOnly&&status.Text.EndsWith("END-OF-DIALOG / 最后一行 / 最後の行")&&status.ScrollBars==ScrollBars.Vertical,"guide dropped long inline failure details");
   Check(guide.ClientRectangle.Contains(status.Bounds)&&!status.Bounds.IntersectsWith(guide.Controls["PresetGuideClose"].Bounds),"guide failure details overlap footer");
   if(language==0)Screenshot(guide,System.IO.Path.Combine(output,"preset-guide-issue-"+dpi+".png"));
   guide.Close();
  }
 }
 static void RunDialogReadability(string output){
  context="dialog readability";int initial=checks;Logger.WritesEnabled=false;Dpi.NoFit=true;UiClock.Frozen=true;Settings.ResetUiFixtureMemory();
  try{
   var heightLimit=typeof(PaviseDialog).GetMethod("GetHeightLimit",BindingFlags.Static|BindingFlags.NonPublic);
   Check(heightLimit!=null,"dialog lacks its working-area height calculation");
   foreach(int dpi in new[]{96,144,192}){
    Dpi.Scale=dpi/96f;Theme.DropFontCache();Theme.SetLight(false);
    foreach(int workHeight in new[]{360,600,1080,2160}){
     int limit=(int)heightLimit.Invoke(null,new object[]{new Rectangle(0,0,1920,workHeight)});
     Check(limit>0&&limit<=Theme.S(640)&&limit<=Math.Floor(workHeight*.8)&&limit<=Math.Max(1,workHeight-Theme.S(40)),"small-screen height limit exceeds its available area");
    }
    foreach(int language in new[]{0,1,2}){
     Lang.Cur=language;CheckPresetGuideReadability(output,dpi,language);
     Lang.Cur=language;context="dialog "+dpi+"/"+language;
     foreach(string body in new[]{"",language==0?"操作已完成。":language==1?"The action is complete.":"操作が完了しました。",LongDialogBody(language)}){
      bool longBody=body.Length>100;
      using(var dialog=MakeDialog(language==0?"更新说明":language==1?"Release notes":"更新内容",body,null,false)){
       ShowFixtureDialog(dialog);CheckDialogFrame(dialog);CheckDialogScroll(dialog,body,longBody);
       if(language==0&&longBody)Screenshot(dialog,System.IO.Path.Combine(output,"dialog-scroll-"+dpi+".png"));
      }
     }
     var extra=new Label{Name="fixtureDialogExtra",Text=language==0?"完整附加内容":language==1?"Complete additional content":"追加内容",AutoSize=false,Size=new Size(Theme.S(400),Theme.S(110)),Font=DsType.Font(DsText.Body)};
     string extraBody=LongDialogBody(language);
     using(var dialog=MakeDialog("Confirmation",extraBody,extra,true)){
      ShowFixtureDialog(dialog);CheckDialogFrame(dialog);CheckDialogScroll(dialog,extraBody,true);
      var viewport=(ScrollableControl)dialog.Controls["DialogContentViewport"];Check(extra.Parent==viewport,"additional content is outside the scroll viewport");
      viewport.ScrollControlIntoView(extra);Application.DoEvents();Check(viewport.ClientRectangle.Contains(extra.Bounds),"additional content cannot be reached by scrolling");
     }
    }
    Lang.Cur=0;
    using(var dialog=MakeDialog(Lang.T("notes.title")+"  "+ReleaseNotes.Current.Tag,CurrentDialogNotes(),null,false)){
     ShowFixtureDialog(dialog);CheckDialogFrame(dialog);Screenshot(dialog,System.IO.Path.Combine(output,"dialog-notes-"+dpi+".png"));
    }
    using(var dialog=MakeDialog("确认操作","是否应用所选设置？",null,true)){
     ShowFixtureDialog(dialog);CheckDialogFrame(dialog);Screenshot(dialog,System.IO.Path.Combine(output,"dialog-confirm-"+dpi+".png"));
    }
    using(var dialog=PaviseDialog.CreateRequiredRestart(delegate{return false;})){
     ShowFixtureDialog(dialog);CheckDialogFrame(dialog);Control feedback=Desc(dialog).First(c=>c.Name=="restartError");
     Check(feedback.Parent.ClientRectangle.Contains(feedback.Bounds),"required restart feedback is clipped");
     Screenshot(dialog,System.IO.Path.Combine(output,"dialog-restart-"+dpi+".png"));
    }
   }
   RunAiTuningPromptChecks(output);
   Console.WriteLine("PASS dialog readability assertions="+(checks-initial)+" languages=3 dpi=96,144,192 private_desktop=true product_workers=false");
  }finally{UiClock.Frozen=false;Dpi.NoFit=true;}
 }
}
}
