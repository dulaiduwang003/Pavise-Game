using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed partial class PaviseCore
    {
        private int motionPreview = -1;
        private PerformancePreset artworkMode;
        private bool previewAppearance, motionPaintFailed;
        private CoreMotionScene motionScene;
        private Bitmap motionBackground;
        private Color motionBackgroundAccent;
        private bool motionBackgroundLight;
        private Size motionCanvasSize;
        private Point motionCanvasOffset;
        internal bool PreviewAppearance { get { return previewAppearance; } set { if(previewAppearance!=value){previewAppearance=value;RefreshVisual();} } }
        internal int SelectedMotion { get { return motionPreview >= 0 ? motionPreview : CoreMotionPreferences.Get(mode); } }
        internal void SetMotionPreview(int id)
        {
            if(id < -1 || id >= CoreMotionCatalog.Count) throw new ArgumentOutOfRangeException("id");
            if(motionPreview==id)return;
            motionPreview=id;DropCache();SyncAnimationTimer();Invalidate();
        }
        internal void PaintSnapshot(Graphics graphics,double seconds) { RenderFrame(graphics,seconds); }
        private void DropMotionCache()
        {
            if(motionScene!=null){motionScene.Dispose();motionScene=null;}
            if(motionBackground!=null){motionBackground.Dispose();motionBackground=null;}
            motionPaintFailed=false;
        }
        private void EnsureMotionBackground(Color color)
        {
            Form form=FindForm();Size canvas=form==null?ClientSize:form.ClientSize;Point offset=Point.Empty;
            for(Control c=this;c!=null&&!(c is Form);c=c.Parent)offset.Offset(c.Left,c.Top);
            if(motionBackground!=null&&motionBackground.Size==ClientSize&&motionBackgroundAccent==color&&motionBackgroundLight==Theme.LightMode&&motionCanvasSize==canvas&&motionCanvasOffset==offset)return;
            if(motionBackground!=null){motionBackground.Dispose();motionBackground=null;}
            Bitmap bitmap=new Bitmap(Width,Height,PixelFormat.Format32bppPArgb);
            try
            {
                using(Graphics g=Graphics.FromImage(bitmap))
                {
                    g.Clear(Theme.Bg);
                    if(immersive)WorkspaceCanvasSurface.Paint(g,this,ClientRectangle,color);
                    else
                    {
                        Rectangle r=new Rectangle(0,0,Math.Max(1,Width-1),Math.Max(1,Height-1));
                        using(GraphicsPath path=Theme.TechPath(r,Theme.S(13)))
                        {
                            using(var b=new LinearGradientBrush(r,Theme.Inset,Theme.Card,LinearGradientMode.ForwardDiagonal))g.FillPath(b,path);
                            using(var p=new Pen(Theme.Stroke))g.DrawPath(p,path);
                        }
                    }
                    DrawGrid(g);
                }
                motionBackground=bitmap;motionBackgroundAccent=color;motionBackgroundLight=Theme.LightMode;motionCanvasSize=canvas;motionCanvasOffset=offset;
            }
            catch { bitmap.Dispose();throw; }
        }
        private void RenderCustomMotion(Graphics g,double seconds,int id)
        {
            Color color=previewAppearance?Theme.ModeColor(mode):Theme.Accent;
            if(!guardEnabled)color=Col.Lerp(Theme.Faint,color,.55f);
            GraphicsState saved=g.Save();
            try
            {
                if(Backdrop.AppliesTo(this))Backdrop.Paint(g,this,ClientRectangle);
                else {EnsureMotionBackground(color);g.DrawImageUnscaled(motionBackground,0,0);}
                if(!motionPaintFailed)
                {
                    if(motionScene==null)motionScene=new CoreMotionScene();
                    RectangleF bounds=new RectangleF(0,0,Width,Height);
                    motionScene.Draw(g,bounds,id,seconds*(guardEnabled?1:.35),color,Theme.LightMode);
                }
                else DrawMotionFallback(g,color);
            }
            catch(ArgumentException error){ FailMotion(error);DrawMotionFallback(g,color); }
            catch(ExternalException error){ FailMotion(error);DrawMotionFallback(g,color); }
            catch(InvalidOperationException error){ FailMotion(error);DrawMotionFallback(g,color); }
            finally { g.Restore(saved); }
        }
        private void FailMotion(Exception error)
        {
            motionPaintFailed=true;timer.Stop();clock.Stop();
            Logger.RepeatedError("core-motion-paint","动效="+CoreMotionCatalog.Key(SelectedMotion)+"；尺寸="+Width+"x"+Height+"; "+error);
        }
        private void DrawMotionFallback(Graphics g,Color color)
        {
            try {TextRenderer.DrawText(g,CoreMotionCatalog.Name(SelectedMotion),Font,ClientRectangle,color,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);}
            catch(ArgumentException){}catch(ExternalException){}
        }
    }
}
