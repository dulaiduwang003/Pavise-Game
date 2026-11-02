#if PAVISE_SELFTEST && PAVISE_CORE_MOTION_CHECKS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static class CoreMotionChecks
    {
        private const BindingFlags Hidden=BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Instance;
        private static int checks,frames;
        private static void Check(bool value,string message){checks++;if(!value)throw new InvalidOperationException(message);}
        private static T Field<T>(object owner,string name){return (T)owner.GetType().GetField(name,Hidden).GetValue(owner);}
        private static void Pump(int ms){Stopwatch w=Stopwatch.StartNew();do{Application.DoEvents();Thread.Sleep(2);}while(w.ElapsedMilliseconds<ms);}
        private static string Hash(Bitmap b){using(var data=new MemoryStream()){b.Save(data,ImageFormat.Png);using(var sha=SHA256.Create())return Convert.ToBase64String(sha.ComputeHash(data.ToArray()));}}
        private static string Frame(PaviseCore core,Bitmap b,double t){using(Graphics g=Graphics.FromImage(b))core.PaintSnapshot(g,t);frames++;return Hash(b);}
        private static void SettingsChecks()
        {
            Settings.UseTransientStoreForCurrentProcess();
            int version=Settings.MutationGeneration;
            Check(CoreMotionCatalog.Count==16,"The unified library must contain two native and fourteen custom animations");
            var keys=new HashSet<string>();for(int i=0;i<CoreMotionCatalog.Count;i++){Check(keys.Add(CoreMotionCatalog.Key(i)),"Duplicate motion key");Check(CoreMotionCatalog.Parse(CoreMotionCatalog.Key(i))==i,"Unstable motion token");}
            Check(CoreMotionPreferences.Get(PerformancePreset.Standard)==0&&CoreMotionPreferences.Get(PerformancePreset.Competitive)==CoreMotionCatalog.CompetitiveNative,"Missing preferences must select each mode's initial recommendation");
            Check(Settings.MutationGeneration==version,"Reading defaults writes preferences");
            Settings.SaveStr(CoreMotionPreferences.PreferenceKey(PerformancePreset.Competitive),"default");
            int legacyGeneration=Settings.MutationGeneration;
            Check(CoreMotionPreferences.Get(PerformancePreset.Competitive)==CoreMotionCatalog.CompetitiveNative&&Settings.MutationGeneration==legacyGeneration,"Legacy default changed artwork or wrote during migration");
            Check(CoreMotionPreferences.TrySave(PerformancePreset.Competitive,0)&&CoreMotionPreferences.TrySave(PerformancePreset.Standard,0)
                &&CoreMotionPreferences.Get(PerformancePreset.Competitive)==0,"Both modes cannot choose the Smart native animation");
            Check(CoreMotionPreferences.TrySave(PerformancePreset.Competitive,CoreMotionCatalog.CompetitiveNative)&&CoreMotionPreferences.TrySave(PerformancePreset.Standard,CoreMotionCatalog.CompetitiveNative)
                &&CoreMotionPreferences.Get(PerformancePreset.Standard)==CoreMotionCatalog.CompetitiveNative,"Both modes cannot choose the Esports native animation");
            Check(CoreMotionCatalog.DisplayId(0)==0&&CoreMotionCatalog.DisplayId(1)==CoreMotionCatalog.CompetitiveNative
                &&Enumerable.Range(0,CoreMotionCatalog.Count).Select(CoreMotionCatalog.DisplayId).Distinct().Count()==CoreMotionCatalog.Count,"Shared display order omits or duplicates an animation");
            Check(CoreMotionPreferences.TrySave(PerformancePreset.Standard,4),"Cannot save Smart selection");
            Check(CoreMotionPreferences.TrySave(PerformancePreset.Competitive,11),"Cannot save Esports selection");
            Check(CoreMotionPreferences.Get(PerformancePreset.Standard)==4&&CoreMotionPreferences.Get(PerformancePreset.Competitive)==11,"Modes overwrite one another");
            Check(!CoreMotionPreferences.TrySave(PerformancePreset.Competitive,CoreMotionCatalog.Count)&&!CoreMotionPreferences.TrySave(PerformancePreset.Competitive,-1),"Invalid preset admitted");
            Check(!CoreMotionPreferences.TrySave((PerformancePreset)6,1),"Retired mode admitted");
            Settings.SaveStr(CoreMotionPreferences.PreferenceKey(PerformancePreset.Standard),"unknown-future-animation");
            Check(CoreMotionPreferences.Get(PerformancePreset.Standard)==0,"Unknown token does not fall back to default");
            Check(CoreMotionPreferences.Get(PerformancePreset.Competitive)==11,"Fallback affected another mode");
            foreach(PerformancePreset mode in PresetValue.VisibleOrder())
            {
                Settings.SaveStr(CoreMotionPreferences.PreferenceKey(mode),"abyss");
                int removedGeneration=Settings.MutationGeneration;
                Check(CoreMotionPreferences.Get(mode)==CoreMotionCatalog.DefaultFor(mode),"Removed Ripples does not fall back to the mode's native default");
                Check(Settings.MutationGeneration==removedGeneration,"Reading a removed animation rewrites preferences");
            }
            Settings.SaveStr(CoreMotionPreferences.PreferenceKey(PerformancePreset.Competitive),"hourglass");
            Check(CoreMotionCatalog.Key(CoreMotionPreferences.Get(PerformancePreset.Competitive))=="hourglass","Removing Ripples remapped the saved Tidal Field selection");
            Settings.SaveStr(CoreMotionPreferences.PreferenceKey(PerformancePreset.Competitive),"default-competitive");
            Check(CoreMotionPreferences.Get(PerformancePreset.Competitive)==CoreMotionCatalog.CompetitiveNative,"Removing Ripples remapped the saved native Esports selection");
            foreach(PerformancePreset mode in PresetValue.VisibleOrder())Check(AppDataReset.IsUserPreference(CoreMotionPreferences.PreferenceKey(mode)),"Reset omits motion preference");
            Settings.UseTransientStoreForCurrentProcess();
        }
        private static void Artwork(string output)
        {
            var hashes=new HashSet<string>();
            foreach(bool light in new[]{false,true})foreach(PerformancePreset mode in PresetValue.VisibleOrder())
            {
                Theme.SetLight(light);Theme.SetMode(mode,false);
                for(int id=0;id<CoreMotionCatalog.Count;id++)using(var core=new PaviseCore())using(var b=new Bitmap(420,420))
                {
                    core.Size=b.Size;core.Immersive=true;core.PreviewAppearance=true;core.SetState(mode,true,false);core.SetMotionPreview(id);
                    string first=Frame(core,b,1.25);Check(first==Frame(core,b,1.25),"Non-deterministic repeated frame: "+id);
                    Check(Field<PerformancePreset>(core,"mode")==mode,"Animation selection changed the actual performance mode");
                    Check(first!=Frame(core,b,3.8),"Animation does not move: "+id);
                    Check(!Field<bool>(core,"motionPaintFailed"),"Paint fallback entered: "+id);
                    if(!light&&mode==PerformancePreset.Competitive){Check(hashes.Add(first),"Two effects render identically: "+id);b.Save(Path.Combine(output,"motion-"+id.ToString("D2")+".png"));}
                    if(light&&mode==PerformancePreset.Standard)b.Save(Path.Combine(output,"light-"+id+".png"));
                    foreach(Size tiny in new[]{new Size(1,1),new Size(2,2),new Size(23,41)})using(var small=new Bitmap(tiny.Width,tiny.Height)){core.Size=tiny;Frame(core,small,12.7);}
                }
            }
            Theme.SetLight(false);Theme.SetMode(PerformancePreset.Competitive,false);
            foreach(int native in new[]{CoreMotionCatalog.SmartNative,CoreMotionCatalog.CompetitiveNative})
            using(var core=new PaviseCore())using(var shared=new Bitmap(420,420))
            {
                core.Size=shared.Size;core.Immersive=true;core.PreviewAppearance=false;core.SetMotionPreview(native);
                core.SetState(PerformancePreset.Standard,true,false);string smart=Frame(core,shared,2.4);
                core.SetState(PerformancePreset.Competitive,true,false);
                Check(smart==Frame(core,shared,2.4),"Native artwork still depends on the performance mode: "+native);
            }
            using(var core=new PaviseCore())using(var b=new Bitmap(360,360))
            {
                core.Size=b.Size;core.PreviewAppearance=true;core.SetState(PerformancePreset.Competitive,true,false);core.SetMotionPreview(11);
                string before=Frame(core,b,2.4);Theme.SetModeColorOverride(PerformancePreset.Competitive,Color.FromArgb(35,165,245));core.RefreshVisual();string after=Frame(core,b,2.4);
                Check(before!=after,"Theme accent does not reach the custom animation");Theme.ClearModeColorOverride(PerformancePreset.Competitive);
                // A damaged resource must stop just this animation and recover on selecting another.
                Frame(core,b,2.4);Field<CoreMotionScene>(core,"motionScene").Dispose();Frame(core,b,2.5);
                Check(Field<bool>(core,"motionPaintFailed"),"Drawing failure did not fall back");
                core.SetMotionPreview(2);Frame(core,b,2.5);Check(!Field<bool>(core,"motionPaintFailed"),"Changing animation cannot recover from a failed resource");
            }
            using(var scene=new CoreMotionScene())using(var b=new Bitmap(450,450))using(Graphics g=Graphics.FromImage(b))
            {
                g.TranslateTransform(3,7);g.SetClip(new Rectangle(35,40,250,230));RectangleF clip=g.ClipBounds;float[] transform=g.Transform.Elements;
                scene.Draw(g,new RectangleF(0,0,420,420),11,2.4,Theme.Accent,false);
                Check(clip==g.ClipBounds&&transform.SequenceEqual(g.Transform.Elements),"Motion drawing leaks graphics clip/transform");
            }
        }
        private static void Editor(string output)
        {
            Settings.UseTransientStoreForCurrentProcess();
            using(var form=new Form())using(var panel=new MotionAppearancePanel(PerformancePreset.Standard))
            {
                form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-12000,-12000);form.Size=new Size(1100,850);form.Controls.Add(panel);
                foreach(float scale in new[]{1f,1.5f,2f})foreach(int logicalWidth in new[]{320,620,1000})
                {
                    Dpi.Scale=scale;int width=Theme.S(logicalWidth);panel.Size=new Size(width,panel.MeasureHeight(width));panel.PerformLayout();
                    foreach(Control c in panel.Controls)Check(panel.ClientRectangle.Contains(c.Bounds),"Editor clips control "+c.Name+" scale="+scale+" width="+width+" "+c.Bounds+" / "+panel.ClientSize);
                }
                Dpi.Scale=1;panel.Size=new Size(1040,panel.MeasureHeight(1040));panel.PerformLayout();
                Check(panel.Controls.OfType<MotionPresetButton>().Count()==CoreMotionCatalog.Count,"Native animations are missing from the shared library");
                Check(panel.Draft==0&&!panel.Controls.Cast<Control>().Any(c=>c.Name=="MotionApply"),"Default selection or removed Apply control is incorrect");
                panel.SelectMotion(4);Check(CoreMotionPreferences.Get(PerformancePreset.Standard)==4,"Selection did not immediately persist Smart choice");
                Check(!Field<Label>(panel,"status").Visible,"Immediate animation selection shows redundant applied status");
                TierPicker picker=Field<TierPicker>(panel,"modes");picker.Index=1;picker.IndexChanged(1);
                Check(panel.EditingMode==PerformancePreset.Competitive&&panel.Draft==CoreMotionCatalog.CompetitiveNative,"Editing-mode switch does not load independent selection");
                panel.SelectMotion(11);Check(CoreMotionPreferences.Get(PerformancePreset.Competitive)==11,"Immediate selection failed for event horizon");
                Check(CoreMotionPreferences.Get(PerformancePreset.Standard)==4&&CoreMotionPreferences.Get(PerformancePreset.Competitive)==11,"Apply leaked to another mode");
                form.Show();Pump(25);panel.SetActivity(false,true,false);
                using(var bitmap=new Bitmap(panel.Width,panel.Height)){panel.DrawToBitmap(bitmap,panel.ClientRectangle);bitmap.Save(Path.Combine(output,"visual-customization.png"));}
                Field<PillButton>(panel,"reset").PerformClick();Check(CoreMotionPreferences.Get(PerformancePreset.Competitive)==CoreMotionCatalog.CompetitiveNative&&CoreMotionPreferences.Get(PerformancePreset.Standard)==4,"Restore default reset the wrong mode");
                panel.SelectMotion(0);Check(CoreMotionPreferences.Get(PerformancePreset.Competitive)==0,"Esports cannot immediately apply Smart's original animation from the common list");
                // Native Timer and Stopwatch are checked, rather than only testing a boolean formula.
                form.Show();panel.SetActivity(true,true,false);Pump(35);
                PaviseCore core=panel.Preview;core.SetAnimationEnabled(true);core.SetForeground(true);Check(Field<System.Windows.Forms.Timer>(core,"timer").Enabled,"Visible foreground preview did not start");
                core.SetForeground(false);Check(!Field<System.Windows.Forms.Timer>(core,"timer").Enabled&&!Field<Stopwatch>(core,"clock").IsRunning,"Lost focus leaves native animation work running");
                double stopped=Field<Stopwatch>(core,"clock").Elapsed.TotalSeconds;Pump(40);Check(Field<Stopwatch>(core,"clock").Elapsed.TotalSeconds==stopped,"Paused clock advances");
                core.SetForeground(true);Check(Field<System.Windows.Forms.Timer>(core,"timer").Enabled,"Focus restore does not resume");
                core.SetState(PerformancePreset.Competitive,true,true);Check(!Field<System.Windows.Forms.Timer>(core,"timer").Enabled,"Game state does not stop preview");
                panel.SetActivity(false,true,false);Check(!Field<System.Windows.Forms.Timer>(core,"timer").Enabled,"Inactive page keeps rendering");form.Hide();
                foreach(int mask in Enumerable.Range(0,128))
                {bool expected=(mask&63)==63&&(mask&64)==0;Check(PaviseCore.ShouldAnimate((mask&1)!=0,(mask&2)!=0,(mask&4)!=0,(mask&8)!=0,(mask&16)!=0?FormWindowState.Normal:FormWindowState.Minimized,(mask&64)!=0,(mask&32)!=0)==expected,"Existing activity gate changed");}
            }
        }
        private static void ContactSheet(string output, bool light)
        {
            const int columns=5,cellWidth=252,cellHeight=280;
            int rows=(CoreMotionCatalog.Count+columns-1)/columns;
            using(var sheet=new Bitmap(columns*cellWidth,rows*cellHeight))using(Graphics draw=Graphics.FromImage(sheet))
            {
                draw.Clear(light?Color.FromArgb(235,238,243):Color.FromArgb(12,15,20));draw.InterpolationMode=InterpolationMode.HighQualityBicubic;
                for(int i=0;i<CoreMotionCatalog.Count;i++)
                {
                    int id=CoreMotionCatalog.DisplayId(i),x=i%columns*cellWidth,y=i/columns*cellHeight;
                    using(var frame=Image.FromFile(Path.Combine(output,light?"light-"+id+".png":"motion-"+id.ToString("D2")+".png")))draw.DrawImage(frame,new Rectangle(x+6,y+6,240,240));
                    TextRenderer.DrawText(draw,CoreMotionCatalog.Name(id),Theme.UI(9f,true),new Rectangle(x+6,y+249,240,25),light?Color.FromArgb(25,30,40):Color.FromArgb(229,232,240),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);
                }
                sheet.Save(Path.Combine(output,light?"animation-library-light.png":"animation-library.png"),ImageFormat.Png);
            }
        }
        private static void Framing(string output)
        {
            const int size=700,inset=140,viewport=420;
            var pixels=new byte[size*size*4];
            using(var scene=new CoreMotionScene())using(var canvas=new Bitmap(size,size,PixelFormat.Format32bppPArgb))
            using(Graphics draw=Graphics.FromImage(canvas))
            {
                scene.UnclippedForTest=true;
                foreach(int id in Enumerable.Range(1,CoreMotionCatalog.Count-2))foreach(double seconds in Enumerable.Range(0,48).Select(i=>i*.431))
                {
                    draw.Clear(Color.Transparent);scene.Draw(draw,new RectangleF(inset,inset,viewport,viewport),id,seconds,Color.FromArgb(239,64,96),false);
                    BitmapData data=canvas.LockBits(new Rectangle(0,0,size,size),ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);
                    try{System.Runtime.InteropServices.Marshal.Copy(data.Scan0,pixels,0,pixels.Length);}finally{canvas.UnlockBits(data);}
                    int outside=0;
                    for(int y=0;y<size;y++)for(int x=0;x<size;x++)
                        if((x<inset||x>=inset+viewport||y<inset||y>=inset+viewport)&&pixels[(y*size+x)*4+3]>32)outside++;
                    if(outside>0)canvas.Save(Path.Combine(output,"framing-"+id+"-"+seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)+".png"),ImageFormat.Png);
                    Check(outside==0,"Visible animation extends beyond the viewport: id="+id+" time="+seconds+" pixels="+outside);
                }
            }
        }
        private static byte[] GeometryMask(Bitmap canvas)
        {
            var pixels=new byte[canvas.Width*canvas.Height*4];
            BitmapData data=canvas.LockBits(new Rectangle(0,0,canvas.Width,canvas.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);
            try{System.Runtime.InteropServices.Marshal.Copy(data.Scan0,pixels,0,pixels.Length);}finally{canvas.UnlockBits(data);}
            var mask=new byte[canvas.Width*canvas.Height];
            for(int i=0;i<mask.Length;i++)mask[i]=(byte)(pixels[i*4+3]>=128?1:0);
            return mask;
        }
        private static void Kinetics(string output)
        {
            const int size=300;
            var evidence=new System.Text.StringBuilder("id,peak_silhouette_xor_over_union,q0,q1,q2,q3\n");
            using(var scene=new CoreMotionScene())using(var canvas=new Bitmap(size,size,PixelFormat.Format32bppPArgb))using(Graphics draw=Graphics.FromImage(canvas))
            {
                // Neutralize every light/particle and render only opaque armor. A static emblem
                // with animated brightness cannot satisfy the requested body-motion coverage.
                scene.PanelGeometryForTest=true;
                foreach(int id in Enumerable.Range(1,CoreMotionCatalog.Count-2).Where(i=>i!=6&&i!=11))
                {
                    draw.Clear(Color.Transparent);scene.Draw(draw,new RectangleF(0,0,size,size),id,.35,Color.Red,false);
                    byte[] first=GeometryMask(canvas);
                    var regionPeak=new double[4];double peak=0;
                    foreach(double t in Enumerable.Range(1,18).Select(i=>.35+i*.43))
                    {
                        draw.Clear(Color.Transparent);scene.Draw(draw,new RectangleF(0,0,size,size),id,t,Color.Red,false);
                        byte[] now=GeometryMask(canvas);var changed=new int[4];var regionUnion=new int[4];int total=0,union=0;
                        for(int y=0;y<size;y++)for(int x=0;x<size;x++)
                        {
                            int index=y*size+x,q=(y<size/2?0:2)+(x<size/2?0:1);
                            if(first[index]!=0||now[index]!=0){union++;regionUnion[q]++;}
                            if(first[index]!=now[index]){total++;changed[q]++;}
                        }
                        peak=Math.Max(peak,total/(double)Math.Max(1,union));
                        for(int q=0;q<4;q++)regionPeak[q]=Math.Max(regionPeak[q],changed[q]/(double)Math.Max(1,regionUnion[q]));
                    }
                    evidence.Append(id).Append(',').Append(peak.ToString("F3",System.Globalization.CultureInfo.InvariantCulture));
                    foreach(double value in regionPeak)evidence.Append(',').Append(value.ToString("F3",System.Globalization.CultureInfo.InvariantCulture));evidence.AppendLine();
                    Check(peak>.28,"Subject geometry barely moves: id="+id+" peak="+peak);
                    Check(regionPeak.Count(r=>r>.18)>=2,"Motion is confined to a single small region: id="+id);
                }
            }
            File.WriteAllText(Path.Combine(output,"motion-silhouette.csv"),evidence.ToString());
        }
        private static byte[] ColorPixels(Bitmap canvas)
        {
            var pixels=new byte[canvas.Width*canvas.Height*4];
            BitmapData data=canvas.LockBits(new Rectangle(0,0,canvas.Width,canvas.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);
            try{System.Runtime.InteropServices.Marshal.Copy(data.Scan0,pixels,0,pixels.Length);}finally{canvas.UnlockBits(data);}
            return pixels;
        }
        private static void TimeContinuity(string output)
        {
            using(var scene=new CoreMotionScene())using(var canvas=new Bitmap(300,300,PixelFormat.Format32bppPArgb))using(Graphics draw=Graphics.FromImage(canvas))
            foreach(double wrap in new[]{0.0,.5/.13,1/.13,2/.13})
            {
                draw.Clear(Color.Transparent);scene.Draw(draw,new RectangleF(0,0,300,300),14,wrap-.000001,Color.Red,false);
                byte[] before=ColorPixels(canvas);
                draw.Clear(Color.Transparent);scene.Draw(draw,new RectangleF(0,0,300,300),14,wrap+.000001,Color.Red,false);
                byte[] after=ColorPixels(canvas);int maximum=0;
                for(int i=0;i<before.Length;i++)maximum=Math.Max(maximum,Math.Abs(before[i]-after[i]));
                Check(maximum<=16,"Tidal flow visibly jumps at a phase wrap: t="+wrap+" channel delta="+maximum);
            }
        }
        [STAThread] private static int Main(string[] args)
        {
            Console.OutputEncoding=new System.Text.UTF8Encoding(false);if(args.Length<1||args.Length>2)return 2;
            return PerformanceWorkspaceLayout.PrivateDesktop(delegate
            {
                try{Settings.UseTransientStoreForCurrentProcess();Logger.WritesEnabled=false;Dpi.Init();Lang.Init();
                    if(args.Length==2&&args[1]=="--continuity-only"){TimeContinuity(args[0]);Console.WriteLine("PASS phase continuity checks="+checks);return;}
                    SettingsChecks();Artwork(args[0]);Framing(args[0]);Kinetics(args[0]);TimeContinuity(args[0]);ContactSheet(args[0],false);ContactSheet(args[0],true);Editor(args[0]);Console.WriteLine("PASS motion customization checks="+checks+" frames="+frames+"; 672 unclipped poses, 228 geometry poses, 16 shared animations, transient settings, private desktop, no product entry point");}
                catch(Exception e){Console.Error.WriteLine(e);throw;}
            });
        }
    }
}
#endif
