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
        private readonly Label title,hint,status;
        private readonly TierPicker modes;
        private readonly MotionPresetButton[] presets=new MotionPresetButton[CoreMotionCatalog.Count];
        private readonly PillButton reset;
        private readonly int[] draft=new int[2];
        private PerformancePreset editing;
        private bool arranging,initialized;
        internal event EventHandler Applied;
        internal PerformancePreset EditingMode {get{return editing;}}
        internal int Draft {get{return draft[(int)editing];}}
        private static string T(string zh,string en){return PerformancePolicy.Text(zh,en);}
        internal MotionAppearancePanel(PerformancePreset initial)
        {
            Name="SettingsCoreMotion";Tag="backdrop-card";AccessibleName=T("模式动效","Mode animation");
            draft[0]=CoreMotionPreferences.Get(PerformancePreset.Standard);draft[1]=CoreMotionPreferences.Get(PerformancePreset.Competitive);
            editing=PresetValue.IsValid((int)initial)?initial:PerformancePreset.Standard;
            title=Label(T("模式动效","Mode animation"),9f,true);title.Name="MotionTitle";
            hint=Label(T("选中即应用 · 两种模式均可选用","Select to apply · shared animation library"),8.5f,false);hint.ForeColor=Theme.Dim;
            modes=new TierPicker{Name="MotionEditingMode",TabIndex=0,Labels=PresetValue.VisibleOrder().Length==2?new[]{ModeButton.ModeName(PerformancePreset.Standard),ModeButton.ModeName(PerformancePreset.Competitive)}:new[]{"Smart","Esports"},Index=(int)editing};
            modes.IndexChanged=delegate(int id){if(id>=0&&id<2){editing=(PerformancePreset)id;SyncSelection();}};Controls.Add(modes);
            reset=new PillButton(T("恢复默认动画","Restore default")){Name="MotionRestoreDefault",TabIndex=1};reset.Click+=delegate{SelectMotion(CoreMotionCatalog.DefaultFor(editing));};Controls.Add(reset);
            for(int i=0;i<presets.Length;i++){var button=new MotionPresetButton(CoreMotionCatalog.DisplayId(i));button.TabIndex=i+2;button.Picked=SelectMotion;presets[i]=button;Controls.Add(button);}
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
            int saved=CoreMotionPreferences.Get(editing);
            foreach(var b in presets)if(b!=null){b.SetMode(editing);b.Selected=b.MotionId==Draft;b.Applied=b.MotionId==saved;b.Invalidate();}
            if(reset!=null)reset.Enabled=Draft!=CoreMotionCatalog.DefaultFor(editing)||saved!=CoreMotionCatalog.DefaultFor(editing);
            if(status!=null){status.ForeColor=Theme.Dim;status.Text="";status.Visible=false;hint.Visible=true;}
        }
        internal bool ApplyDraft()
        {
            if(!CoreMotionPreferences.TrySave(editing,Draft))
            {draft[(int)editing]=CoreMotionPreferences.Get(editing);SyncSelection();status.ForeColor=Theme.Danger;status.Text=T("保存失败，原动画保留","Save failed; previous animation retained");hint.Visible=false;status.Visible=true;return false;}
            SyncSelection();var handler=Applied;if(handler!=null)handler(this,EventArgs.Empty);return true;
        }
        internal void RefreshPalette()
        {
            foreach (var button in presets) button.Invalidate();
            Invalidate();
        }
        internal int MeasureHeight(int width)
        {
            return LayoutContent(width, false);
        }
        private int LayoutContent(int width, bool arrange)
        {
            int padding = Theme.S(16);
            int innerWidth = Math.Max(1, width - 2 * padding);
            int gap = Theme.S(8);
            int columns = Math.Max(1, Math.Min(presets.Length, (innerWidth + gap) / (Theme.S(128) + gap)));
            int rows = (presets.Length + columns - 1) / columns;
            int modesTop = Theme.S(68);
            int buttonHeight = Theme.S(30);
            int buttonGap = Theme.S(20);
            int resetWidth = Math.Min(innerWidth, Math.Max(Theme.S(132), TextRenderer.MeasureText(reset.Text, reset.Font).Width + Theme.S(28)));
            int remainingWidth = innerWidth - resetWidth - buttonGap;
            bool sameRow = remainingWidth >= Theme.S(128);
            int modesWidth = Math.Min(Theme.S(250), sameRow ? remainingWidth : innerWidth);
            int resetTop = sameRow ? modesTop : modesTop + buttonHeight + gap;
            int gridTop = resetTop + buttonHeight + Theme.S(12);
            int cellHeight = Theme.S(86);
            if (arrange)
            {
                title.SetBounds(padding, Theme.S(12), innerWidth, Theme.S(25));
                hint.SetBounds(padding, Theme.S(40), innerWidth, Theme.S(22));
                status.Bounds = hint.Bounds;
                modes.SetBounds(padding, modesTop, modesWidth, buttonHeight);
                reset.SetBounds(sameRow ? modes.Right + buttonGap : padding, resetTop, resetWidth, buttonHeight);
                int cellSpace = innerWidth - (columns - 1) * gap;
                for (int index = 0; index < presets.Length; index++)
                {
                    int column = index % columns;
                    int left = cellSpace * column / columns;
                    int right = cellSpace * (column + 1) / columns;
                    presets[index].SetBounds(padding + left + column * gap, gridTop + index / columns * (cellHeight + gap), right - left, cellHeight);
                }
            }
            return gridTop + rows * cellHeight + (rows - 1) * gap + padding;
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (!initialized || arranging) return;
            arranging = true;
            try { LayoutContent(Width, true); }
            finally { arranging = false; }
        }
        protected override void OnPaintBackground(PaintEventArgs e){DsPaint.CardBackground(e.Graphics,this,Theme.Card);}
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            DsPaint.TechFrame(e.Graphics, new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)),
                Theme.S(8), Backdrop.CardFill(this, Theme.Card), Col.Lerp(Theme.Card, Theme.Stroke, 0.62f));
        }
    }
}
