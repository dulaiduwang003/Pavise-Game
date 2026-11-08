using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal sealed partial class CoreMotionScene : IDisposable
    {
        private const double Tau = Math.PI * 2;
        private readonly Pen pen = new Pen(Color.White, 1);
        private readonly SolidBrush brush = new SolidBrush(Color.White);
        private readonly Bitmap glow;
        private readonly ImageAttributes glowInk = new ImageAttributes();
        private readonly ColorMatrix tint = new ColorMatrix();
        private Graphics g;
        private Color accent, high, second, dark;
        private bool light;
#if PAVISE_SELFTEST
        internal bool UnclippedForTest;
        internal bool PanelGeometryForTest;
#endif

        internal CoreMotionScene()
        {
            pen.LineJoin = LineJoin.Round; pen.StartCap = pen.EndCap = LineCap.Round;
            glow = new Bitmap(96, 96, PixelFormat.Format32bppPArgb);
            int[] pixels = new int[96 * 96];
            for (int y = 0; y < 96; y++) for (int x = 0; x < 96; x++)
            {
                double r = Math.Sqrt((x-47.5)*(x-47.5)+(y-47.5)*(y-47.5))/47.5;
                int a = r >= 1 ? 0 : (int)(255 * Math.Pow(1-r, 2.4));
                pixels[y*96+x] = (a<<24)|(a<<16)|(a<<8)|a;
            }
            BitmapData data = glow.LockBits(new Rectangle(0,0,96,96), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); } finally { glow.UnlockBits(data); }
        }
        internal void Draw(Graphics target, RectangleF bounds, int id, double seconds, Color color, bool lightMode)
        {
            if (!CoreMotionCatalog.Valid(id) || CoreMotionCatalog.IsNative(id) || bounds.Width < 2 || bounds.Height < 2) return;
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) seconds = 0;
            seconds = Math.Max(0, seconds);
            g = target; accent = color; light = lightMode;
            pen.LineJoin = LineJoin.Round;
            high = Col.Lerp(color, Color.White, light ? .35f : .76f);
            second = Col.Lerp(color, Color.White, .25f); dark = Col.Lerp(color, Color.Black, .78f);
            GraphicsState state = g.Save();
            try
            {
#if PAVISE_SELFTEST
                if (!UnclippedForTest) g.SetClip(bounds, CombineMode.Intersect);
#else
                g.SetClip(bounds, CombineMode.Intersect);
#endif
                float scale = Math.Min(bounds.Width, bounds.Height) / (id == 6 || id == 11 ? 420f : 440f);
                g.TranslateTransform(bounds.X+bounds.Width/2, bounds.Y+bounds.Height/2);
                g.ScaleTransform(scale, scale); g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                switch(id)
                {
                    case 1: Seraph(seconds); break; case 4: Phoenix(seconds); break;
                    case 5: VoidDragon(seconds); break; case 7: Demon(seconds); break;
                    case 2: PlasmaStorm(seconds); break;
                    case 3: SpacetimeShock(seconds); break;
                    case 6: Gravity(seconds, false); break;
                    case 8: ReactorArray(seconds); break;
                    case 9: VoidPulse(seconds); break; case 10: PhotonWeave(seconds); break;
                    case 11: Gravity(seconds, true); break; case 12: NovaShock(seconds); break;
                    case 13: OrbitalStructure(seconds); break; case 14: TidalField(seconds); break;
                }
            }
            finally { g.Restore(state); g = null; }
        }
        private static PointF P(double x, double y) { return new PointF((float)x, (float)y); }
        private static double Fract(double x) { return x - Math.Floor(x); }
        private static PointF Polar(double r, double a) { return P(Math.Cos(a)*r, Math.Sin(a)*r); }
        private Color A(Color c, double alpha) {
#if PAVISE_SELFTEST
            if (PanelGeometryForTest) return Color.Transparent;
#endif
            return Color.FromArgb((int)Math.Max(0,Math.Min(255,alpha*(light?1.08:1))),c); }
        private void Fill(Color c, params PointF[] p) { brush.Color=c; g.FillPolygon(brush,p); }
        private void Line(Color c, float w, params PointF[] p) { if(p.Length<2)return; pen.Color=c;pen.Width=w;g.DrawLines(pen,p); }
        private void Polygon(Color c, float w, PointF[] p) { pen.Color=c;pen.Width=w;g.DrawPolygon(pen,p); }
        private void Dot(double x,double y,double r,Color c) { brush.Color=c;g.FillEllipse(brush,(float)(x-r),(float)(y-r),(float)(r*2),(float)(r*2)); }
        private void Box(double x,double y,double w,double h,Color c) { brush.Color=c;g.FillRectangle(brush,(float)x,(float)y,(float)w,(float)h); }
        private void Arc(double r,double a,double length,Color c,float width) { pen.Color=c;pen.Width=width;g.DrawArc(pen,(float)-r,(float)-r,(float)r*2,(float)r*2,(float)(a*180/Math.PI),(float)(length*180/Math.PI)); }
        private void Beam(Color c,float width,params PointF[] p) { Line(A(c,28),width+9,p);Line(A(c,65),width+3,p);Line(c,width,p); }
        private void Halo(double x,double y,double radius,Color c,double alpha)
        {
#if PAVISE_SELFTEST
            if (PanelGeometryForTest) return;
#endif
            tint.Matrix00=c.R/255f;tint.Matrix11=c.G/255f;tint.Matrix22=c.B/255f;tint.Matrix33=(float)Math.Max(0,Math.Min(1,alpha));
            glowInk.SetColorMatrix(tint);
            g.DrawImage(glow,new Rectangle((int)(x-radius),(int)(y-radius),(int)(radius*2),(int)(radius*2)),0,0,96,96,GraphicsUnit.Pixel,glowInk);
        }
        private static PointF[] Regular(int n,double r,double rotation)
        {
            PointF[] p=new PointF[n];for(int i=0;i<n;i++)p[i]=Polar(r,rotation+i*Tau/n);return p;
        }
        private void Sparks(double t,int count,double radius,double speed)
        {
            for(int i=0;i<count;i++){double a=i*2.399963+t*speed,r=radius+(i*19%39),s=.6+(i%4)*.22;PointF p=Polar(r,a);Dot(p.X,p.Y,s,A(second,30+65*(.5+.5*Math.Sin(t+i))));}
        }

private static PointF DiskPoint(double a,double r,double tilt,double turn)
        {double x=Math.Cos(a)*r,y=Math.Sin(a)*r*tilt;return P(x*Math.Cos(turn)-y*Math.Sin(turn),x*Math.Sin(turn)+y*Math.Cos(turn));}
        private void Gravity(double t,bool flagship)
        {
            double turn=-.24+Math.Sin(t*.14)*.12,core=flagship?49:43;
            Halo(0,0,205,accent,flagship?.65:.35);
            int stars=flagship?230:95;
            for(int i=0;i<stars;i++)
            {
                double a=i*2.399963+t*(.014+(i%3)*.008),r=65+(i*37%133),x=Math.Cos(a)*r,y=Math.Sin(a)*r*.94,depth=(i%3+1)/3.0;
                if(flagship&&r<104){double bend=(104-r)/104*.65;a+=bend;x=Math.Cos(a)*r;y=Math.Sin(a)*r*.94;}
                Color ink=A(i%7==0?high:second,30+depth*100*(.7+.3*Math.Sin(t*.8+i)));Dot(x,y,.4+depth*.65,ink);
                if(flagship&&i%13==0)Line(A(high,75),.7f,P(x-2,y),P(x+2,y));
            }
            if(flagship)
            {
                for(int j=0;j<4;j++){double a=t*.04+j*1.57;Halo(Math.Cos(a)*107,Math.Sin(a)*125,90,accent,.12);}
            }
            // 视界和吸积盘共用缩放，背景星场与喷流端点保持原坐标。
            const float subjectScale = 1.32f;
            GraphicsState subject = g.Save(); g.ScaleTransform(subjectScale, subjectScale);
            if(flagship)
            {
                for(int band=0;band<10;band++)
                {PointF[] arc=new PointF[60];for(int j=0;j<arc.Length;j++){double a=Math.PI+j*Math.PI/(arc.Length-1);arc[j]=P(Math.Cos(a)*(core+7+band*1.7),Math.Sin(a)*(core+15+band*2.5)-5);}Line(A(band%3==0?high:accent,65+band*6),band%3==0?1.4f:.7f,arc);}
            }
            DrawAccretion(t,turn,flagship,false);
            Dot(0,0,core,Color.FromArgb(255,3,5,11));
            Arc(core+1,0,Tau,A(accent,45),10);Arc(core,0,Tau,A(accent,135),3.5f);Arc(core,0,Tau,A(high,245),1.2f);
            DrawAccretion(t,turn,flagship,true);
            g.Restore(subject);
            for(int k=0;k<(flagship?8:4);k++)
            {
                PointF[] p=new PointF[35];double side=k%2==0?1:-1,lane=k/2-1.5;
                double jetStart=core*subjectScale+10;
                for(int j=0;j<p.Length;j++)p[j]=P(lane*(2+j*.11)+Math.Sin(j*.21-t*2.6+lane)*(1+j*.035),side*(jetStart+j*(184-jetStart)/(p.Length-1)));
                Line(A(accent,flagship?38:54),.8f,p);
            }
            if(flagship)
            {
                for(int i=0;i<70;i++){double z=Fract(i*.618033+t*(.07+(i%4)*.012)),r=51+(1-z)*(1-z)*135,a=i*2.399963+t*.22+z*3.6;PointF p=DiskPoint(a,r,.64,turn),q=DiskPoint(a-.028-z*.09,r+1,.64,turn);Line(A(i%5==0?high:accent,Math.Sin(z*Math.PI)*210),.8f+(float)z*.8f,q,p);if(i%7==0){Halo(p.X,p.Y,6,accent,Math.Sin(z*Math.PI)*.75);Dot(p.X,p.Y,1.2,A(high,Math.Sin(z*Math.PI)*230));}}
                for(int j=0;j<2;j++){double z=Fract(t*.075+j*.5);Arc(128+z*59,.2+t*.03,Tau*.82,A(accent,(1-z)*20),.7f);}
            }
            else for(int k=0;k<3;k++){double a=t*.55+k*Tau/3;PointF p=P(Math.Cos(a)*95,Math.Sin(a)*151);Halo(p.X,p.Y,10,high,.9);Dot(p.X,p.Y,1.4,high);}
        }
        private void DrawAccretion(double t,double turn,bool flagship,bool front)
        {
            int count=flagship?370:190;
            for(int i=0;i<count;i++)
            {
                double r=59+(i*17%74),a=i*2.399963-t*(1.15-r*.0045);
                if((Math.Sin(a)>0)!=front)continue;
                PointF p=DiskPoint(a,r,.37,turn),q=DiskPoint(a+.035+(i%4)*.01,r,.37,turn);
                double heat=(133-r)/74;Color ink=Col.Lerp(accent,high,(float)(heat*.72));
                if(flagship&&i%3==0)Line(A(ink,40),4,p,q);
                Line(A(ink,105+heat*145),i%7==0?1.6f:.85f,p,q);
            }
            for(int b=0;b<(flagship?14:7);b++)
            {
                PointF[] pts=new PointF[61];double r=62+b*4.6;
                for(int j=0;j<pts.Length;j++){double a=(front?0:Math.PI)+j*Math.PI/(pts.Length-1);pts[j]=DiskPoint(a,r+Math.Sin(a*3+t)*1.2,.37,turn);}
                Line(A(b%4==0?high:accent,b%4==0?85:45),.7f,pts);
            }
        }

        public void Dispose() { ReleaseArmor();if(armorInverseBasis!=null){armorInverseBasis.Dispose();armorInverseBasis=null;}pen.Dispose();brush.Dispose();glow.Dispose();glowInk.Dispose(); }
    }
}
