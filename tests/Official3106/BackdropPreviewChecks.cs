using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class Official3106UiChecks
    {
        private static void SavePreview(Form form, string output, string name)
        { Application.DoEvents(); form.Refresh(); Application.DoEvents(); Screenshot(form, Path.Combine(output, name + ".png")); }
        private static void CheckCanvasPixel(Control control, Point sample)
        {
            using (var actual = new Bitmap(control.Width, control.Height))
            using (var expected = new Bitmap(control.Width, control.Height))
            {
                control.DrawToBitmap(actual, control.ClientRectangle);
                using (Graphics g = Graphics.FromImage(expected)) Backdrop.Paint(g, control, control.ClientRectangle);
                Color a = actual.GetPixel(sample.X, sample.Y), b = expected.GetPixel(sample.X, sample.Y);
                Check(Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B) <= 6, "opaque rectangle on " + control.Name + " at " + sample + ": " + a + " vs " + b);
            }
        }
        private static void PreviewSheet(string output)
        {
            string[] files = Directory.GetFiles(output, "cover-*.png").OrderBy(x => x).ToArray();
            int width = 384, height = 264, columns = 4, rows = (files.Length + columns - 1) / columns;
            using (var sheet = new Bitmap(columns * width, rows * height))
            using (var g = Graphics.FromImage(sheet))
            using (var font = new Font("Segoe UI", 10))
            {
                g.Clear(Color.FromArgb(28, 30, 34));
                for (int i=0;i<files.Length;i++) using (var pic = Image.FromFile(files[i]))
                {
                    int x=(i%columns)*width, y=(i/columns)*height;
                    g.DrawImage(pic, new Rectangle(x+4,y+24,width-8,height-28));
                    g.DrawString(Path.GetFileNameWithoutExtension(files[i]),font,Brushes.White,x+6,y+3);
                }
                sheet.Save(Path.Combine(output,"cover-contact-sheet.png"),ImageFormat.Png);
            }
        }
        private static void CheckChildBackground(Control child)
        {
            Control parent = child.Parent;
            using (var actual = new Bitmap(child.Width, child.Height))
            using (var expected = new Bitmap(parent.Width, parent.Height))
            {
                child.DrawToBitmap(actual, child.ClientRectangle);
                child.Hide();
                try { parent.DrawToBitmap(expected, parent.ClientRectangle); }
                finally { child.Show(); }
                Color a = actual.GetPixel(1, 1), b = expected.GetPixel(child.Left + 1, child.Top + 1);
                Check(Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B) <= 6,
                    "child background disagrees with parent: " + child.Name + " " + child.GetType().Name + ": " + a + " vs " + b);
            }
        }
        private static void RunBackdropPreview(string output, string cover)
        {
            context="custom cover and LoL preview";
            Logger.WritesEnabled=false; Settings.ResetUiFixtureMemory(); UiClock.Frozen=true;
            Dpi.NoFit=true; Dpi.Scale=1; Theme.DropFontCache(); Lang.Cur=0; Theme.SetLight(false);
            string data=Path.Combine(output,"mock-data");Directory.CreateDirectory(data);Paths.Data=data;
            if (!string.IsNullOrEmpty(cover)) File.Copy(cover,Backdrop.StorePath,true);
            else using(var b=new Bitmap(1220,760))using(var g=Graphics.FromImage(b))
            {
                using(var gradient=new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0,0,1220,760),Color.SlateBlue,Color.DarkGoldenrod,30))g.FillRectangle(gradient,0,0,1220,760);
                b.Save(Backdrop.StorePath,ImageFormat.Png);
            }
            Backdrop.Init(); Check(Backdrop.Active,"preview cover did not load");
            var core=new SuppressionCore();var mode=new GameMode(data,core);
            var profile=new GameProfile { Id="preview-lol",Name="英雄联盟",Root=@"C:\PaviseUiPreview\WeGameApps\英雄联盟",ExecutablePath=@"C:\PaviseUiPreview\WeGameApps\英雄联盟\LeagueClient.exe",LearnedExecutablePath=@"C:\PaviseUiPreview\WeGameApps\英雄联盟\Game\League of Legends.exe" };
            ((List<GameProfile>)typeof(GameMode).GetField("profiles",Hidden).GetValue(mode)).Add(profile);
            using(var icon=IconArt.MakeIcon(32))using(var form=new PanelForm(new Tamer(core),mode,icon,true))
            {
                form.ShowInTaskbar=false;form.Show();Application.DoEvents();Call(form,"StopPageReveal");
                foreach(PageId page in Enum.GetValues(typeof(PageId)))
                {
                    if(page==PageId.Count)continue;
                    form.ShowPageForShot(page);Call(form,"StopPageReveal");SavePreview(form,output,"cover-page-"+page);
                }
                form.ShowPageForShot(PageId.CoreScheduling);Call(form,"StopPageReveal");
                CheckChildBackground(Field<Control>(form,"coreEnableInfo"));
                CheckChildBackground(Field<Control>(form,"coreIsolationGlyph"));
                form.ShowPageForShot(PageId.Policy);Call(form,"StopPageReveal");
                var bar=Field<PerformanceCategoryBar>(form,"performanceCategories");
                for(int i=0;i<5;i++)
                {
                    bar.Select(i,true);Application.DoEvents();
                    CheckCanvasPixel(bar,new Point(Math.Max(1,bar.Width-6),Math.Max(1,bar.Height/2)));
                    Check(!Desc(form).Any(c => c.Name.StartsWith("laboratoryApply_Lab",StringComparison.Ordinal)),"retired laboratory action is still visible");
                    SavePreview(form,output,"cover-performance-"+i);
                }
                form.ShowPageForShot(PageId.Settings);Call(form,"StopPageReveal");
                var tabs=Field<TechTabs>(form,"settingsTabs");
                for(int i=0;i<4;i++)
                {
                    tabs.Index=i;Call(form,"LayoutSettingsWorkspace");Application.DoEvents();
                    var surface=Field<SettingsWorkspaceSurface>(form,"settingsWorkspaceSurface");
                    CheckCanvasPixel(surface,new Point(surface.Width-10,Math.Max(2,surface.FrameTop/2)));
                    SavePreview(form,output,"cover-settings-"+i);
                }
                form.ShowPageForShot(PageId.Audit);Call(form,"StopPageReveal");Call(form,"ShowAuditIdle");
                Check(!Field<Label>(form,"lblAuditStatus").Visible,"empty audit status overlays scanner");
                SavePreview(form,output,"cover-audit-idle");
                form.ShowPageForShot(PageId.Library);Call(form,"StopPageReveal");
                var nested=typeof(PanelForm).GetNestedType("LibraryGameConfiguration",BindingFlags.NonPublic);
                var item=new GameLibraryItem(profile,false,true);
                var content=(Control)Activator.CreateInstance(nested,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new object[]{form,item},null);
                using(var dialog=new GameConfigurationDialog(item,content,null))
                {
                    dialog.ShowInTaskbar=false;dialog.Show(form);Application.DoEvents();
                    SavePreview(dialog,output,"lol-advanced-tuning-top");
                    var viewport=(ScrollableControl)dialog.Controls["LibraryConfigurationViewport"];
                    viewport.AutoScrollPosition=new Point(0,100000);Application.DoEvents();
                    SavePreview(dialog,output,"lol-advanced-tuning-bottom");
                    Check(Desc(content).Any(c=>c is LolCardPanel),"LoL dedicated controls are missing");
                    Check(!Desc(content).Any(c=>c.Name=="libraryWeGameToggle"),"retired generic WeGame control reappeared");
                    Check(typeof(LolCardPanel).GetField("btnLaunch",Hidden)==null,"retired LoL launch button reappeared");
                    Check(typeof(LolOptimizationService).GetMethod("LaunchWeGame")==null,"retired manual WeGame launch API reappeared");
                    Check(!Desc(content).Any(c=>c.Text.Contains("脱壳启动") || c.Text.Contains("Launch without shell")),"retired shell-launch text reappeared");
                    dialog.Close();
                }
                Check(!form.UiActive&&!mode.Enabled,"preview started production activity");
            }
            foreach (bool light in new[] { false, true })
            foreach (int dim in new[] { 0, 1, 2 })
            {
                Theme.SetLight(light); Backdrop.Dim=dim;
                using(var icon=IconArt.MakeIcon(32))using(var form=new PanelForm(new Tamer(core),mode,icon,true))
                {
                    form.ShowInTaskbar=false;form.Show();Application.DoEvents();Call(form,"StopPageReveal");
                    form.ShowPageForShot(PageId.Policy);Call(form,"StopPageReveal");
                    var bar=Field<PerformanceCategoryBar>(form,"performanceCategories");
                    CheckCanvasPixel(bar,new Point(bar.Width-6,bar.Height/2));
                    SavePreview(form,output,"cover-material-"+(light?"light":"dark")+"-"+dim);
                    form.ShowPageForShot(PageId.Settings);Call(form,"StopPageReveal");
                    var surface=Field<SettingsWorkspaceSurface>(form,"settingsWorkspaceSurface");
                    CheckCanvasPixel(surface,new Point(surface.Width-10,Math.Max(2,surface.FrameTop/2)));
                    form.ShowPageForShot(PageId.CoreScheduling);Call(form,"StopPageReveal");
                    CheckChildBackground(Field<Control>(form,"coreEnableInfo"));
                    CheckChildBackground(Field<Control>(form,"coreIsolationGlyph"));
                    Check(!form.UiActive&&!mode.Enabled,"material check started production activity");
                }
            }
            PreviewSheet(output);
            Console.WriteLine("PASS backdrop preview checks="+checks+"; private desktop; mock LoL; no product workers or system actions");
        }
    }
}
