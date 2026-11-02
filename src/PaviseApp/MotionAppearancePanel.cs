using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class MotionPresetButton : FxControl
    {
        internal readonly int MotionId;
        internal Action<int> Picked;
        internal bool Selected, Applied;
        private PerformancePreset mode;
        private Bitmap thumbnail;
        private Color thumbnailColor;
        private bool thumbnailLight;
        internal MotionPresetButton(int id)
        {
            MotionId=id;Text=CoreMotionCatalog.Name(id);Name="CoreMotionPreset"+id;
            BackColor=Theme.Card;Bg=Theme.Card;
            AccessibleName=Text;AccessibleDescription=CoreMotionCatalog.Description(id);
            AccessibleRole=AccessibleRole.PushButton;TabStop=true;
        }
        internal void SetMode(PerformancePreset value) { if(mode!=value){mode=value;DropThumbnail();}Invalidate(); }
        private void DropThumbnail(){if(thumbnail!=null){thumbnail.Dispose();thumbnail=null;}}
        protected override void Dispose(bool disposing){if(disposing)DropThumbnail();base.Dispose(disposing);}
        protected override void OnSizeChanged(EventArgs e){DropThumbnail();base.OnSizeChanged(e);}
        protected override void OnClick(EventArgs e){base.OnClick(e);if(CanInteract&&Picked!=null)Picked(MotionId);}
        protected override bool IsInputKey(Keys key){return DsKeys.IsActivation(key)||base.IsInputKey(key);}
        protected override void OnKeyDown(KeyEventArgs e){base.OnKeyDown(e);if(DsKeys.IsPlainActivation(e)&&CanInteract){e.SuppressKeyPress=true;OnClick(EventArgs.Empty);}}
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g=e.Graphics;FillBg(g);g.SmoothingMode=SmoothingMode.AntiAlias;
            Rectangle r=new Rectangle(0,0,Math.Max(1,Width-1),Math.Max(1,Height-1));Color color=Theme.ModeColor(mode);
            using(GraphicsPath path=Theme.TechPath(r,Theme.S(7)))
            {
                using(var b=new SolidBrush(Selected?Col.Lerp(Theme.Inset,color,Theme.LightMode?.11f:.15f):Theme.Inset))g.FillPath(b,path);
                Rectangle image=new Rectangle(Theme.S(4),Theme.S(3),Math.Max(1,Width-Theme.S(8)),Math.Max(1,Height-Theme.S(6)));
                if(thumbnail==null||thumbnailColor!=color||thumbnailLight!=Theme.LightMode)
                {
                    DropThumbnail();thumbnail=new Bitmap(image.Width,image.Height,PixelFormat.Format32bppPArgb);
                    using(Graphics draw=Graphics.FromImage(thumbnail))using(var sample=new PaviseCore())
                    {sample.Size=thumbnail.Size;sample.Immersive=true;sample.PreviewAppearance=true;sample.SetState(mode,true,false);sample.SetMotionPreview(MotionId);sample.PaintSnapshot(draw,2.4);}
                    thumbnailColor=color;thumbnailLight=Theme.LightMode;
                }
                GraphicsState saved=g.Save();g.SetClip(path,CombineMode.Intersect);g.DrawImageUnscaled(thumbnail,image.Location);g.Restore(saved);
                using(var p=new Pen(Selected?color:Theme.Stroke,Selected?Math.Max(1.5f,Theme.S(1)):1))g.DrawPath(p,path);
            }
            if(Applied){using(var p=new Pen(color,Math.Max(1.4f,Theme.S(1)))){g.DrawLines(p,new[]{new Point(Width-Theme.S(18),Theme.S(10)),new Point(Width-Theme.S(14),Theme.S(14)),new Point(Width-Theme.S(7),Theme.S(7))});}}
            if(WantsFocusCue)DsFocus.Draw(g,DsFocus.Inset(ClientRectangle,4),color,Theme.Inset);
        }
    }

    internal sealed class MotionAppearancePanel : DBPanel
    {
        private readonly Label title,hint,previewDescription,status;
        private readonly TierPicker modes;
        internal readonly PaviseCore Preview;
        private readonly MotionPresetButton[] presets=new MotionPresetButton[CoreMotionCatalog.Count];
        private readonly PillButton reset;
        private readonly int[] draft=new int[2];
        private PerformancePreset editing;
        private bool active,foreground,gameActive,arranging,initialized;
        internal event EventHandler Applied;
        internal PerformancePreset EditingMode {get{return editing;}}
        internal int Draft {get{return draft[(int)editing];}}
        private static string T(string zh,string en){return PerformancePolicy.Text(zh,en);}
        internal MotionAppearancePanel(PerformancePreset initial)
        {
            Name="SettingsCoreMotion";Tag="backdrop-card";AccessibleName=T("模式动效","Mode animation");
            draft[0]=CoreMotionPreferences.Get(PerformancePreset.Standard);draft[1]=CoreMotionPreferences.Get(PerformancePreset.Competitive);
            editing=PresetValue.IsValid((int)initial)?initial:PerformancePreset.Standard;
            title=Label(T("模式动效","Mode animation"),10.5f,true);title.Name="MotionTitle";
            hint=Label(T("选中即应用 · 两种模式均可选用","Select to apply · shared animation library"),8.5f,false);hint.ForeColor=Theme.Dim;
            modes=new TierPicker{Name="MotionEditingMode",Labels=PresetValue.VisibleOrder().Length==2?new[]{ModeButton.ModeName(PerformancePreset.Standard),ModeButton.ModeName(PerformancePreset.Competitive)}:new[]{"Smart","Esports"},Index=(int)editing};
            modes.IndexChanged=delegate(int id){if(id>=0&&id<2){editing=(PerformancePreset)id;SyncSelection();}};Controls.Add(modes);
            Preview=new PaviseCore{Name="SettingsMotionPreview",PreviewAppearance=true};Controls.Add(Preview);
            previewDescription=Label("",8.5f,false);previewDescription.ForeColor=Theme.Dim;
            for(int i=0;i<presets.Length;i++){var button=new MotionPresetButton(CoreMotionCatalog.DisplayId(i));button.TabIndex=i+1;button.Picked=SelectMotion;presets[i]=button;Controls.Add(button);}
            reset=new PillButton(T("恢复默认动画","Restore default")){Name="MotionRestoreDefault",TabIndex=CoreMotionCatalog.Count+1};reset.Click+=delegate{SelectMotion(CoreMotionCatalog.DefaultFor(editing));};Controls.Add(reset);
            status=Label("",8.2f,false);status.Name="MotionSaveStatus";
            initialized=true;
            SyncSelection();
            PerformLayout();
        }
        private Label Label(string text,float size,bool bold)
        {var label=new Label{Text=text,Font=Theme.UI(size,bold),ForeColor=Theme.Fg,BackColor=Color.Transparent,AutoSize=false};Controls.Add(label);return label;}
        internal void SelectMotion(int id)
        {if(!CoreMotionCatalog.Valid(id))return;draft[(int)editing]=id;ApplyDraft();}
        private void SyncSelection()
        {
            if(Preview==null)return;
            Preview.SetState(editing,true,gameActive);Preview.SetMotionPreview(Draft);
            previewDescription.Text=CoreMotionCatalog.Description(Draft);
            int saved=CoreMotionPreferences.Get(editing);
            foreach(var b in presets)if(b!=null){b.SetMode(editing);b.Selected=b.MotionId==Draft;b.Applied=b.MotionId==saved;b.Invalidate();}
            if(reset!=null)reset.Enabled=Draft!=CoreMotionCatalog.DefaultFor(editing)||saved!=CoreMotionCatalog.DefaultFor(editing);
            if(status!=null){status.ForeColor=Theme.Dim;status.Text="";status.Visible=false;}
            if(initialized)PerformLayout();
            SyncViewport();
        }
        internal bool ApplyDraft()
        {
            if(!CoreMotionPreferences.TrySave(editing,Draft))
            {draft[(int)editing]=CoreMotionPreferences.Get(editing);SyncSelection();status.ForeColor=Theme.Danger;status.Text=T("保存失败，原动画保留","Save failed; previous animation retained");status.Visible=true;return false;}
            SyncSelection();var handler=Applied;if(handler!=null)handler(this,EventArgs.Empty);return true;
        }
        internal void RefreshPalette(){Preview.RefreshVisual();foreach(var b in presets)b.Invalidate();Invalidate();}
        internal void SetActivity(bool value,bool inForeground,bool inGame)
        {
            active=value;foreground=inForeground;gameActive=inGame;
            Preview.SetForeground(foreground);Preview.SetState(editing,true,gameActive);SyncViewport();
        }
        internal void SyncViewport()
        {
            if(Preview==null||Preview.IsDisposed)return;
            bool visible=active&&Visible&&IsHandleCreated;
            if(visible)
            {
                Rectangle clip=Preview.RectangleToScreen(Preview.ClientRectangle);
                for(Control p=Preview.Parent;p!=null;p=p.Parent){if(!p.Visible){visible=false;break;}clip=Rectangle.Intersect(clip,p.RectangleToScreen(p.ClientRectangle));if(clip.Width<2||clip.Height<2){visible=false;break;}}
            }
            Preview.SetAnimationEnabled(visible);
        }
        internal int MeasureHeight(int width)
        {
            int pad=Theme.S(16),gap=Theme.S(16),inner=Math.Max(1,width-pad*2);bool split=width>=Theme.S(790);
            int previewWidth=split?Math.Min(Theme.S(350),inner*38/100):inner;
            int gridWidth=split?inner-previewWidth-gap:inner;
            int cols=gridWidth>=Theme.S(430)?4:gridWidth>=Theme.S(300)?3:2;
            int rows=(CoreMotionCatalog.Count+cols-1)/cols;
            int gridHeight=rows*Theme.S(86)+(rows-1)*Theme.S(8);
            int previewHeight=split?Math.Max(Theme.S(300),gridHeight-Theme.S(56)):Theme.S(236);
            int leftBottom=Theme.S(110)+previewHeight+Theme.S(37);
            int gridTop=split?Theme.S(110):leftBottom+gap;
            int bottom=Math.Max(leftBottom,gridTop+gridHeight)+Theme.S(16);
            return bottom+Theme.S(34)+Theme.S(50);
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);if(!initialized||Preview==null||arranging)return;arranging=true;
            try
            {
                int p=Theme.S(16),gap=Theme.S(16),inner=Math.Max(1,Width-p*2);bool split=Width>=Theme.S(790);
                title.SetBounds(p,Theme.S(12),Math.Max(1,inner),Theme.S(25));
                hint.SetBounds(p,Theme.S(40),Math.Max(1,inner),Theme.S(22));
                modes.SetBounds(p,Theme.S(68),Math.Min(inner,Theme.S(250)),Theme.S(30));
                int top=Theme.S(110),pw=split?Math.Min(Theme.S(350),inner*38/100):inner;
                int gx=split?p+pw+gap:p,gw=split?inner-pw-gap:inner;
                int cols=gw>=Theme.S(430)?4:gw>=Theme.S(300)?3:2,rows=(presets.Length+cols-1)/cols,cellGap=Theme.S(8),cw=Math.Max(1,(gw-cellGap*(cols-1))/cols),ch=Theme.S(86),gh=rows*ch+(rows-1)*cellGap;
                int ph=split?Math.Max(Theme.S(300),gh-Theme.S(56)):Theme.S(236);
                Preview.SetBounds(p,top,pw,ph);
                previewDescription.SetBounds(p,Preview.Bottom+Theme.S(8),pw,Theme.S(29));
                int gy=split?top:previewDescription.Bottom+gap;
                for(int i=0;i<presets.Length;i++)presets[i].SetBounds(gx+(i%cols)*(cw+cellGap),gy+(i/cols)*(ch+cellGap),i%cols==cols-1?gw-(cols-1)*(cw+cellGap):cw,ch);
                int bottom=Math.Max(previewDescription.Bottom,gy+gh)+Theme.S(16);
                int rw=Math.Min(inner,Math.Max(Theme.S(132),TextRenderer.MeasureText(reset.Text,reset.Font).Width+Theme.S(28)));
                reset.SetBounds(p,bottom,rw,Theme.S(34));
                status.SetBounds(p,reset.Bottom+Theme.S(8),inner,Theme.S(30));
            }
            finally{arranging=false;}SyncViewport();
        }
        protected override void OnVisibleChanged(EventArgs e){base.OnVisibleChanged(e);SyncViewport();}
        protected override void OnLocationChanged(EventArgs e){base.OnLocationChanged(e);SyncViewport();}
        protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);SyncViewport();}
        protected override void OnPaintBackground(PaintEventArgs e){DsPaint.CardBackground(e.Graphics,this,Theme.Card);}
        protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);using(var p=new Pen(Theme.Stroke)){e.Graphics.DrawRectangle(p,0,0,Math.Max(0,Width-1),Math.Max(0,Height-1));}}
    }
}
