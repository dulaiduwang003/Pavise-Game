using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed partial class PaviseCore : Control
	{
		private sealed class CoreLayers : IDisposable
		{
			internal Bitmap StaticLayer;

			internal Bitmap ForegroundLayer;

			internal Bitmap HubLayer;

			internal Color Accent;

			internal Color Accent2;

			internal Color CanvasAccent;

			internal bool Backdrop;

			internal bool Light;

			internal Size SceneSize;

			internal Point SceneOffset;
			internal PerformancePreset Artwork;

			internal Rectangle HubBounds;

			internal bool Matches(int width, int height, Color accent, Color accent2, Color canvasAccent, bool backdrop, Size sceneSize, Point sceneOffset, PerformancePreset artwork)
			{
				if (StaticLayer != null && StaticLayer.Width == width && StaticLayer.Height == height && Accent == accent && Accent2 == accent2 && Backdrop == backdrop && Light == Theme.LightMode && CanvasAccent == canvasAccent && SceneSize == sceneSize)
				{
					return SceneOffset == sceneOffset && Artwork == artwork;
				}
				return false;
			}

			public void Dispose()
			{
				if (StaticLayer != null)
				{
					StaticLayer.Dispose();
					StaticLayer = null;
				}
				if (ForegroundLayer != null)
				{
					ForegroundLayer.Dispose();
					ForegroundLayer = null;
				}
				if (HubLayer != null)
				{
					HubLayer.Dispose();
					HubLayer = null;
				}
			}
		}

		private struct SmartPoint
		{
			internal float X;

			internal float Y;

			internal float Z;

			internal PointF Screen
			{
				get
				{
					float perspectiveScale = 340f / (340f - Z);
					return new PointF(X * perspectiveScale, Y * perspectiveScale);
				}
			}
		}

		private const double CompetitiveEyePeriod = 9.8;

		internal const int FrameMs = 15;

		private static readonly PointF[] CompetitiveCrown = new PointF[5]
		{
			new PointF(-73f, 1f),
			new PointF(-28f, -34f),
			new PointF(73f, -43f),
			new PointF(47f, -22f),
			new PointF(-25f, -18f)
		};

		private static readonly PointF[] CompetitiveBlade = new PointF[6]
		{
			new PointF(-64f, 10f),
			new PointF(-26f, 34f),
			new PointF(30f, 17f),
			new PointF(61f, -17f),
			new PointF(13f, 7f),
			new PointF(-24f, 22f)
		};

		private static readonly PointF[] CompetitiveIris = new PointF[5]
		{
			new PointF(-15f, 1f),
			new PointF(5f, -15f),
			new PointF(25f, -13f),
			new PointF(7f, 5f),
			new PointF(-8f, 10f)
		};

		private static readonly double[] CompetitiveGazeTimes = new double[9] { 0.0, 1.1, 2.35, 3.7, 4.95, 6.15, 7.5, 8.65, 9.8 };

		private static readonly PointF[] CompetitiveGazeTargets = new PointF[9]
		{
			new PointF(0f, 0f),
			new PointF(10f, -2f),
			new PointF(10f, -2f),
			new PointF(-11f, 2f),
			new PointF(-11f, 2f),
			new PointF(4f, 4f),
			new PointF(4f, 4f),
			new PointF(0f, 0f),
			new PointF(0f, 0f)
		};

		private readonly Stopwatch clock = new Stopwatch();

		private readonly Timer timer;

		private PerformancePreset mode;

		private bool guardEnabled = true;

		private bool gameActive;

		private bool animationRequested;

		private bool foreground = true;

		private CoreLayers staticLayers;

		private CoreLayers transitionLayers;

		private float layerBlend;

		private readonly ImageAttributes layerBlendAttributes = new ImageAttributes();

		private readonly ColorMatrix layerBlendMatrix = new ColorMatrix();

		private bool immersive;

		public bool Immersive
		{
			get
			{
				return immersive;
			}
			set
			{
				if (immersive != value)
				{
					immersive = value;
					DropCache();
					Invalidate();
				}
			}
		}

		private float DesignScale
		{
			get
			{
				float scaleRatio = ((!immersive) ? 0.00312f : ((artworkMode == PerformancePreset.Competitive) ? 0.004f : ((artworkMode == PerformancePreset.Handheld) ? 0.0037f : ((artworkMode == PerformancePreset.Extreme) ? 0.00365f : 0.0035f))));
				return (float)Math.Min(base.Width, base.Height) * scaleRatio;
			}
		}

		private float DesignCenterY
		{
			get
			{
				return (float)base.Height * (immersive ? 0.5f : 0.44f);
			}
		}

		private static PointF OrbitPoint(float rx, float ry, double degrees)
		{
			double radians = degrees * Math.PI / 180.0;
			return new PointF(rx * (float)Math.Cos(radians), ry * (float)Math.Sin(radians));
		}

		private static void DrawOrbitFlow(Graphics g, float rx, float ry, double angle, Color c, float power, bool front)
		{
			using (Pen pen = new Pen(c, 1.4f))
			{
				LineCap startCap = (pen.EndCap = LineCap.Round);
				pen.StartCap = startCap;
				for (int trailIndex = 23; trailIndex >= 0; trailIndex--)
				{
					double trailAngle = angle - (double)(trailIndex * 2);
					if (Math.Sin((trailAngle - 1.0) * Math.PI / 180.0) >= 0.0 == front)
					{
						float trailStrength = 1f - (float)trailIndex / 24f;
						pen.Color = Col.Alpha(c, (int)(185f * trailStrength * trailStrength * power));
						g.DrawLine(pen, OrbitPoint(rx, ry, trailAngle - 2.0), OrbitPoint(rx, ry, trailAngle));
					}
				}
			}
			if (Math.Sin(angle * Math.PI / 180.0) >= 0.0 != front)
			{
				return;
			}
			PointF pointF = OrbitPoint(rx, ry, angle);
			using (SolidBrush brush = new SolidBrush(Col.Alpha(c, (int)(30f * power))))
			{
				g.FillEllipse(brush, pointF.X - 5f, pointF.Y - 5f, 10f, 10f);
			}
			using (SolidBrush brush2 = new SolidBrush(Col.Alpha(Col.Lerp(c, Theme.Fg, 0.65f), (int)(255f * power))))
			{
				g.FillEllipse(brush2, pointF.X - 1.6f, pointF.Y - 1.6f, 3.2f, 3.2f);
			}
		}

		private static float CompetitiveEase(double value)
		{
			double progress = Math.Max(0.0, Math.Min(1.0, value));
			return (float)(progress * progress * progress * (progress * (progress * 6.0 - 15.0) + 10.0));
		}

		private static PointF CompetitiveGaze(double seconds)
		{
			double cycleTime = seconds % 9.8;
			for (int i = 1; i < CompetitiveGazeTimes.Length; i++)
			{
				if (!(cycleTime > CompetitiveGazeTimes[i]))
				{
					float gazeProgress = CompetitiveEase((cycleTime - CompetitiveGazeTimes[i - 1]) / (CompetitiveGazeTimes[i] - CompetitiveGazeTimes[i - 1]));
					PointF pointF = CompetitiveGazeTargets[i - 1];
					PointF pointF2 = CompetitiveGazeTargets[i];
					return new PointF(pointF.X + (pointF2.X - pointF.X) * gazeProgress, pointF.Y + (pointF2.Y - pointF.Y) * gazeProgress);
				}
			}
			return PointF.Empty;
		}

		private static float CompetitiveEyeOpen(double seconds)
		{
			double cycleTime = seconds % 9.8;
			double blinkElapsed = ((cycleTime >= 8.3) ? (cycleTime - 8.3) : (cycleTime - 3.0));
			float blinkClosure = ((blinkElapsed < 0.0 || blinkElapsed >= 0.38) ? 0f : ((blinkElapsed < 0.12) ? CompetitiveEase(blinkElapsed / 0.12) : ((blinkElapsed < 0.17) ? 1f : (1f - CompetitiveEase((blinkElapsed - 0.17) / 0.21)))));
			return (1f - 0.94f * blinkClosure) * (1f - 0.035f * (float)Math.Pow(Math.Sin(seconds * Math.PI / 9.8), 2.0));
		}

		private static void CompetitiveFacet(Graphics g, PointF[] points, Color bright, Color dark)
		{
			using (GraphicsPath graphicsPath = new GraphicsPath())
			{
				graphicsPath.AddPolygon(points);
				using (LinearGradientBrush brush = new LinearGradientBrush(graphicsPath.GetBounds(), bright, dark, 115f))
				{
					g.FillPath(brush, graphicsPath);
				}
			}
		}

		private void DrawCompetitiveStructure(Graphics g, Color c)
		{
			Halo(g, new RectangleF(-126f, -121f, 252f, 242f), c, Theme.LightMode ? 18 : 55);
			Color a = Col.Lerp(Theme.Card, c, Theme.LightMode ? 0.04f : 0.07f);
			for (int i = 0; i < 2; i++)
			{
				GraphicsState gstate = g.Save();
				g.RotateTransform(i * 180);
				PointF[] points = new PointF[7]
				{
					new PointF(-117f, -18f),
					new PointF(-86f, -58f),
					new PointF(-44f, -75f),
					new PointF(-56f, -54f),
					new PointF(-78f, -40f),
					new PointF(-103f, -7f),
					new PointF(-113f, 17f)
				};
				Armor(g, points, Col.Lerp(a, c, 0.35f));
				GlowLine(g, new PointF(-112f, -17f), new PointF(-85f, -51f), c, 0.65f);
				using (Pen pen = new Pen(Col.Alpha(c, 45), 0.65f))
				{
					g.DrawLines(pen, new PointF[3]
					{
						new PointF(-117f, 27f),
						new PointF(-107f, 47f),
						new PointF(-73f, 78f)
					});
				}
				using (Pen pen2 = new Pen(Col.Alpha(c, 145), 1.4f))
				{
					for (int j = 0; j < 3; j++)
					{
						g.DrawLine(pen2, 47 + j * 5, -83, 52 + j * 5, -91);
					}
				}
				g.Restore(gstate);
			}
			using (Pen pen3 = new Pen(Col.Alpha(c, 70), 0.7f))
			{
				g.DrawLines(pen3, new PointF[3]
				{
					new PointF(-38f, -97f),
					new PointF(-27f, -105f),
					new PointF(32f, -105f)
				});
				g.DrawLines(pen3, new PointF[3]
				{
					new PointF(38f, 89f),
					new PointF(27f, 97f),
					new PointF(-32f, 97f)
				});
			}
		}

		private void DrawCompetitiveHub(Graphics g, Color c)
		{
			Halo(g, new RectangleF(-94f, -65f, 188f, 126f), c, Theme.LightMode ? 25 : 75);
		}

		private void DrawCompetitiveEye(Graphics g, double seconds, Color c, float power)
		{
			GraphicsState gstate = g.Save();
			double seconds2 = (gameActive ? 0.0 : seconds);
			float eyeOpenness = CompetitiveEyeOpen(seconds2);
			using (Matrix matrix = new Matrix(1f, -0.24f * (1f - eyeOpenness), 0f, eyeOpenness, 0f, 0f))
			{
				g.MultiplyTransform(matrix);
			}
			PointF[] points = new PointF[6]
			{
				new PointF(-85f, 3f),
				new PointF(-31f, -43f),
				new PointF(89f, -53f),
				new PointF(62f, -25f),
				new PointF(-25f, -9f),
				new PointF(-57f, 12f)
			};
			PointF[] points2 = new PointF[8]
			{
				new PointF(-75f, 12f),
				new PointF(-28f, 45f),
				new PointF(39f, 23f),
				new PointF(73f, -17f),
				new PointF(64f, 12f),
				new PointF(40f, 39f),
				new PointF(-29f, 56f),
				new PointF(-79f, 23f)
			};
			Armor(g, points, Col.Lerp(Theme.Faint, c, 0.35f));
			Armor(g, points2, Col.Lerp(Theme.Faint, c, 0.35f));
			CompetitiveFacet(g, CompetitiveCrown, Col.Lerp(c, Theme.Fg, 0.3f), Col.Lerp(Theme.Inset, c, 0.55f));
			CompetitiveFacet(g, CompetitiveBlade, c, Col.Lerp(Theme.Inset, c, 0.3f));
			GraphicsState gstate2 = g.Save();
			using (GraphicsPath graphicsPath = new GraphicsPath())
			{
				graphicsPath.AddPolygon(new PointF[6]
				{
					new PointF(-61f, 5f),
					new PointF(-23f, -17f),
					new PointF(48f, -24f),
					new PointF(57f, -17f),
					new PointF(13f, 7f),
					new PointF(-24f, 21f)
				});
				g.SetClip(graphicsPath, CombineMode.Intersect);
				PointF pointF = CompetitiveGaze(seconds2);
				g.TranslateTransform(pointF.X, pointF.Y - pointF.X * 0.24f);
				CompetitiveFacet(g, CompetitiveIris, Col.Lerp(c, Theme.Fg, 0.8f), c);
				float eyePulse = 0.5f + 0.5f * (float)Math.Sin(seconds * Math.PI / 2.5);
				Plate(g, CompetitiveIris, Col.Lerp(c, Theme.Fg, 0.75f), (int)((30f + eyePulse * 90f) * power));
			}
			g.Restore(gstate2);
			GlowLine(g, new PointF(-68f, 0f), new PointF(-26f, -32f), c, 0.6f);
			GlowLine(g, new PointF(-26f, -32f), new PointF(69f, -41f), c, 0.8f);
			using (Pen pen = new Pen(Col.Alpha(c, 105), 0.6f))
			{
				g.DrawLine(pen, -21, 44, 34, 28);
				g.DrawLine(pen, -46, -37, -32, -49);
				g.DrawLine(pen, -32, -49, 18, -54);
			}
			float haloPulse = 0.5f + 0.5f * (float)Math.Sin(seconds * Math.PI / 2.5);
			using (Pen pen2 = new Pen(Col.Alpha(c, (int)((70f + 90f * haloPulse) * power)), 0.85f))
			{
				g.DrawLines(pen2, new PointF[3]
				{
					new PointF(-61f, 11f),
					new PointF(-25f, 32f),
					new PointF(29f, 16f)
				});
			}
			GraphicsState gstate3 = g.Save();
			using (GraphicsPath graphicsPath2 = new GraphicsPath())
			{
				graphicsPath2.AddPolygon(CompetitiveCrown);
				graphicsPath2.AddPolygon(CompetitiveBlade);
				g.SetClip(graphicsPath2, CombineMode.Intersect);
				double scanProgress = seconds / 3.6 % 1.0;
				float scanPosition = -105f + (float)scanProgress * 210f;
				RectangleF rect = new RectangleF(scanPosition - 20f, -65f, 40f, 130f);
				Color c2 = Col.Lerp(c, Theme.Fg, 0.8f);
				using (LinearGradientBrush linearGradientBrush = new LinearGradientBrush(rect, Color.Transparent, Color.Transparent, 0f))
				{
					linearGradientBrush.InterpolationColors = new ColorBlend
					{
						Colors = new Color[3]
						{
							Col.Alpha(c2, 0),
							Col.Alpha(c2, (int)(100.0 * Math.Sin(scanProgress * Math.PI) * (double)power)),
							Col.Alpha(c2, 0)
						},
						Positions = new float[3] { 0f, 0.5f, 1f }
					};
					g.FillRectangle(linearGradientBrush, rect);
				}
			}
			g.Restore(gstate3);
			g.Restore(gstate);
		}

		private static PointF CompetitiveTrack(double phase)
		{
			double trackPosition = (phase % 1.0 + 1.0) % 1.0 * 6.0;
			int segmentIndex = (int)trackPosition;
			float segmentProgress = (float)(trackPosition - (double)segmentIndex);
			PointF pointF = Polar(92f, -90 + segmentIndex * 60);
			PointF pointF2 = Polar(92f, -90 + (segmentIndex + 1) * 60);
			return new PointF(pointF.X + (pointF2.X - pointF.X) * segmentProgress, pointF.Y + (pointF2.Y - pointF.Y) * segmentProgress);
		}

		private void DrawCompetitiveMotion(Graphics g, double seconds, Color c, float power, bool front)
		{
			if (!front)
			{
				GraphicsState gstate = g.Save();
				g.RotateTransform((float)(seconds * 7.0));
				for (int i = 0; i < 6; i++)
				{
					float spokeAngle = -90 + i * 60;
					PointF[] array = new PointF[4]
					{
						Polar(91f, spokeAngle + 5f),
						Polar(91f, spokeAngle + 45f),
						Polar(96f, spokeAngle + 42f),
						Polar(96f, spokeAngle + 10f)
					};
					Plate(g, array, c, (int)(16f * power));
					using (Pen pen = new Pen(Col.Alpha(c, (int)(75f * power)), 0.75f))
					{
						g.DrawLine(pen, array[0], array[1]);
					}
				}
				using (Pen pen2 = new Pen(c, 1.5f))
				{
					LineCap startCap = (pen2.EndCap = LineCap.Round);
					pen2.StartCap = startCap;
					for (int j = 0; j < 2; j++)
					{
						for (int k = 0; k < 28; k++)
						{
							double trailPhase = seconds / 10.0 + (double)j * 0.5 - (double)k * 0.003;
							pen2.Color = Col.Alpha(c, (int)(210.0 * Math.Pow(1f - (float)k / 28f, 2.0) * (double)power));
							g.DrawLine(pen2, CompetitiveTrack(trailPhase), CompetitiveTrack(trailPhase - 0.003));
						}
					}
				}
				g.Restore(gstate);
			}
			else
			{
				DrawCompetitiveEye(g, seconds, c, power);
			}
		}

		public PaviseCore()
		{
			SetStyle(ControlStyles.UserPaint | ControlStyles.Opaque | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
			BackColor = Theme.Bg;
			Cursor = Cursors.Default;
			timer = new Timer();
			timer.Interval = 15;
			Timer obj = timer;
			EventHandler value = delegate
			{
				if (!CanAnimate())
				{
					timer.Stop();
					clock.Stop();
				}
				else
				{
					Invalidate();
				}
			};
			obj.Tick += value;
		}

		public void SetState(PerformancePreset value, bool enabled, bool active)
		{
			if (mode != value || guardEnabled != enabled || gameActive != active)
			{
				mode = value;
				guardEnabled = enabled;
				gameActive = active;
				DropCache();
				SyncAnimationTimer();
				Invalidate();
			}
		}

		public void RefreshVisual()
		{
			DropCache();
			SyncAnimationTimer();
			Invalidate();
		}

		public void SetAnimationEnabled(bool value)
		{
			animationRequested = value;
			SyncAnimationTimer();
			if (value)
			{
				Invalidate();
			}
		}

		public void SetForeground(bool value)
		{
			if (foreground != value)
			{
				foreground = value;
				SyncAnimationTimer();
				if (value)
				{
					Invalidate();
				}
			}
		}

		protected override void OnVisibleChanged(EventArgs e)
		{
			base.OnVisibleChanged(e);
			SyncAnimationTimer();
		}

		protected override void OnHandleCreated(EventArgs e)
		{
			base.OnHandleCreated(e);
			SyncAnimationTimer();
		}

		protected override void OnHandleDestroyed(EventArgs e)
		{
			timer.Stop();
			clock.Stop();
			DropCache();
			base.OnHandleDestroyed(e);
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				timer.Dispose();
				DropCache();
				layerBlendAttributes.Dispose();
			}
			base.Dispose(disposing);
		}

		internal static bool ShouldAnimate(bool requested, bool handleCreated, bool controlVisible, bool formVisible, FormWindowState windowState, bool gameActive)
		{
			return ShouldAnimate(requested, handleCreated, controlVisible, formVisible, windowState, gameActive, true);
		}

		internal static bool ShouldAnimate(bool requested, bool handleCreated, bool controlVisible, bool formVisible, FormWindowState windowState, bool gameActive, bool foregroundApp)
		{
			if (requested && handleCreated && controlVisible && formVisible && windowState != FormWindowState.Minimized && !gameActive)
			{
				return foregroundApp;
			}
			return false;
		}

		private bool CanAnimate()
		{
			Form form = FindForm();
			return !motionPaintFailed && ShouldAnimate(animationRequested, base.IsHandleCreated, base.Visible, form != null && form.Visible, (form == null) ? FormWindowState.Minimized : form.WindowState, gameActive, foreground);
		}

		private void SyncAnimationTimer()
		{
			if (CanAnimate())
			{
				clock.Start();
				timer.Start();
			}
			else
			{
				timer.Stop();
				clock.Stop();
			}
		}

		protected override void OnSizeChanged(EventArgs e)
		{
			DropCache();
			base.OnSizeChanged(e);
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			RenderFrame(e.Graphics, clock.Elapsed.TotalSeconds);
		}

		private void RenderFrame(Graphics g, double seconds)
		{
			if (base.Width < 2 || base.Height < 2)
			{
				return;
			}
			int motion = motionPreview >= 0 ? motionPreview : CoreMotionPreferences.Get(mode);
			if (!CoreMotionCatalog.IsNative(motion)) { RenderCustomMotion(g, seconds, motion); return; }
            artworkMode = motion == CoreMotionCatalog.CompetitiveNative ? PerformancePreset.Competitive : PerformancePreset.Standard;
			Color baseAccent = previewAppearance ? Theme.ModeColor(mode) : Theme.Accent;
			Color primaryAccent = (guardEnabled ? baseAccent : Col.Lerp(Theme.Faint, baseAccent, 0.55f));
			Color accent = (guardEnabled ? (previewAppearance ? Theme.ModeColor2(mode) : Theme.Accent2) : Theme.Faint);
			EnsureStaticLayer(primaryAccent, accent);
			if (Backdrop.AppliesTo(this))
			{
				Backdrop.Paint(g, this, base.ClientRectangle);
			}
			DrawStaticLayer(g);
			g.SmoothingMode = SmoothingMode.AntiAlias;
			g.PixelOffsetMode = PixelOffsetMode.HighQuality;
			if (artworkMode == PerformancePreset.Standard)
			{
				GraphicsState smartState = BeginDesign(g);
				DrawSmartMotion(g, seconds * (guardEnabled ? 1.0 : 0.35), primaryAccent);
				g.Restore(smartState);
				return;
			}
			GraphicsState motionState = BeginDesign(g);
			double animationTime = seconds * (guardEnabled ? 1.0 : 0.35);
			DrawModeMotion(g, animationTime, primaryAccent);
			g.Restore(motionState);
			float hubBobOffset = ((artworkMode == PerformancePreset.Handheld) ? ((float)Math.Sin(animationTime * Math.PI / 3.0) * 2.2f * DesignScale) : 0f);
			if (artworkMode == PerformancePreset.Handheld)
			{
				Rectangle rectangle = Rectangle.Ceiling(new RectangleF((float)base.Width * 0.5f - 83f * DesignScale, DesignCenterY - 42f * DesignScale, 166f * DesignScale, 87f * DesignScale));
				g.InterpolationMode = InterpolationMode.Bilinear;
				Rectangle source = rectangle;
				source.Offset(-staticLayers.HubBounds.X, -staticLayers.HubBounds.Y);
				DrawLayer(g, staticLayers.HubLayer, (transitionLayers == null) ? null : transitionLayers.HubLayer, new RectangleF(rectangle.X, (float)rectangle.Y + hubBobOffset, rectangle.Width, rectangle.Height), source);
			}
			else
			{
				DrawLayerAt(g, staticLayers.HubLayer, (transitionLayers == null) ? null : transitionLayers.HubLayer, staticLayers.HubBounds.Location);
			}
			motionState = BeginDesign(g);
			DrawFrontLight(g, animationTime, primaryAccent);
			g.Restore(motionState);
			DrawLayer(g, staticLayers.ForegroundLayer, (transitionLayers == null) ? null : transitionLayers.ForegroundLayer);
		}

		private void EnsureStaticLayer(Color accent, Color accent2)
		{
			bool usesBackdrop = Backdrop.AppliesTo(this);
			if (base.Width <= 0 || base.Height <= 0)
			{
				return;
			}
			Size sceneSize = Size.Empty;
			Point sceneOffset = Point.Empty;
			if (immersive && !usesBackdrop)
			{
				Form form = FindForm();
				sceneSize = ((form == null) ? base.ClientSize : form.ClientSize);
				Control control = this;
				while (control != null && !(control is Form))
				{
					sceneOffset.Offset(control.Left, control.Top);
					control = control.Parent;
				}
			}
			if (Theme.ThemeAnimating && !previewAppearance)
			{
				Color fromAccent = (guardEnabled ? Theme.TransitionFromAccent : Col.Lerp(Theme.Faint, Theme.TransitionFromAccent, 0.55f));
				Color fromSecondaryAccent = (guardEnabled ? Theme.TransitionFromAccent2 : Theme.Faint);
				Color targetAccent = (guardEnabled ? Theme.TransitionTargetAccent : Col.Lerp(Theme.Faint, Theme.TransitionTargetAccent, 0.55f));
				Color targetSecondaryAccent = (guardEnabled ? Theme.TransitionTargetAccent2 : Theme.Faint);
				if (staticLayers == null || !staticLayers.Matches(base.Width, base.Height, fromAccent, fromSecondaryAccent, Theme.TransitionFromAccent, usesBackdrop, sceneSize, sceneOffset, artworkMode))
				{
					if (staticLayers != null)
					{
						staticLayers.Dispose();
					}
					staticLayers = BuildLayers(fromAccent, fromSecondaryAccent, Theme.TransitionFromAccent, usesBackdrop, sceneSize, sceneOffset);
				}
				if (transitionLayers == null || !transitionLayers.Matches(base.Width, base.Height, targetAccent, targetSecondaryAccent, Theme.TransitionTargetAccent, usesBackdrop, sceneSize, sceneOffset, artworkMode))
				{
					if (transitionLayers != null)
					{
						transitionLayers.Dispose();
					}
					transitionLayers = BuildLayers(targetAccent, targetSecondaryAccent, Theme.TransitionTargetAccent, usesBackdrop, sceneSize, sceneOffset);
				}
				layerBlend = Theme.ThemeTransitionProgress;
				layerBlendMatrix.Matrix33 = layerBlend;
				layerBlendAttributes.SetColorMatrix(layerBlendMatrix);
				return;
			}
			if (transitionLayers != null && transitionLayers.Matches(base.Width, base.Height, accent, accent2, Theme.Accent, usesBackdrop, sceneSize, sceneOffset, artworkMode))
			{
				if (staticLayers != null)
				{
					staticLayers.Dispose();
				}
				staticLayers = transitionLayers;
				transitionLayers = null;
			}
			else
			{
				if (transitionLayers != null)
				{
					transitionLayers.Dispose();
					transitionLayers = null;
				}
				if (staticLayers == null || !staticLayers.Matches(base.Width, base.Height, accent, accent2, Theme.Accent, usesBackdrop, sceneSize, sceneOffset, artworkMode))
				{
					if (staticLayers != null)
					{
						staticLayers.Dispose();
					}
					staticLayers = BuildLayers(accent, accent2, Theme.Accent, usesBackdrop, sceneSize, sceneOffset);
				}
			}
			layerBlend = 0f;
		}

		private CoreLayers BuildLayers(Color accent, Color accent2, Color canvasAccent, bool backdrop, Size sceneSize, Point sceneOffset)
		{
			CoreLayers layerConfiguration = new CoreLayers();
			layerConfiguration.Accent = accent;
			layerConfiguration.Accent2 = accent2;
			layerConfiguration.CanvasAccent = canvasAccent;
			layerConfiguration.Backdrop = backdrop;
			layerConfiguration.Light = Theme.LightMode;
			layerConfiguration.SceneSize = sceneSize;
			layerConfiguration.SceneOffset = sceneOffset;
			layerConfiguration.Artwork = artworkMode;
			CoreLayers layers = layerConfiguration;
			try
			{
				layers.StaticLayer = new Bitmap(base.Width, base.Height, PixelFormat.Format32bppPArgb);
				if (artworkMode != PerformancePreset.Standard)
				{
					if (!immersive)
					{
						layers.ForegroundLayer = new Bitmap(base.Width, base.Height, PixelFormat.Format32bppPArgb);
					}
					layers.HubBounds = HubCacheBounds();
					layers.HubLayer = new Bitmap(layers.HubBounds.Width, layers.HubBounds.Height, PixelFormat.Format32bppPArgb);
				}
				using (Graphics surfaceGraphics = Graphics.FromImage(layers.StaticLayer))
				{
					surfaceGraphics.Clear((immersive || backdrop) ? Color.Transparent : Theme.Bg);
					surfaceGraphics.SmoothingMode = SmoothingMode.AntiAlias;
					surfaceGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
					Rectangle rectangle = new Rectangle(0, 0, base.Width - 1, base.Height - 1);
					if (immersive)
					{
						if (!backdrop)
						{
							WorkspaceCanvasSurface.Paint(surfaceGraphics, this, base.ClientRectangle, canvasAccent);
						}
						DrawGrid(surfaceGraphics);
					}
					else
					{
						using (GraphicsPath graphicsPath = Theme.TechPath(rectangle, Theme.S(15)))
						{
							Color gradientStart = Backdrop.CardFill(this, Theme.LightMode ? Col.Lerp(Theme.Inset, accent, 0.055f) : Theme.Inset);
							Color gradientEnd = Backdrop.CardFill(this, Theme.LightMode ? Col.Lerp(Theme.Card, accent, 0.018f) : Theme.Card);
							using (LinearGradientBrush brush = new LinearGradientBrush(rectangle, gradientStart, gradientEnd, LinearGradientMode.ForwardDiagonal))
							{
								surfaceGraphics.FillPath(brush, graphicsPath);
							}
							using (Pen pen = new Pen(Theme.Stroke))
							{
								surfaceGraphics.DrawPath(pen, graphicsPath);
							}
							surfaceGraphics.SetClip(graphicsPath);
							DrawGrid(surfaceGraphics);
							surfaceGraphics.ResetClip();
						}
					}
					if (artworkMode == PerformancePreset.Standard)
					{
						GraphicsState gstate = BeginDesign(surfaceGraphics);
						DrawSmartStructure(surfaceGraphics, accent);
						surfaceGraphics.Restore(gstate);
						if (!immersive)
						{
							DrawCoreLabels(surfaceGraphics, (float)base.Width * 0.5f, DesignCenterY, accent);
							DrawFrameDetails(surfaceGraphics, accent);
						}
					}
					else
					{
						GraphicsState gstate2 = BeginDesign(surfaceGraphics);
						DrawModeStructure(surfaceGraphics, accent);
						surfaceGraphics.Restore(gstate2);
					}
				}
				if (artworkMode == PerformancePreset.Standard)
				{
					return layers;
				}
				using (Graphics hubGraphics = Graphics.FromImage(layers.HubLayer))
				{
					hubGraphics.Clear(Color.Transparent);
					hubGraphics.SmoothingMode = SmoothingMode.AntiAlias;
					hubGraphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
					hubGraphics.TranslateTransform(-layers.HubBounds.X, -layers.HubBounds.Y);
					GraphicsState gstate3 = BeginDesign(hubGraphics);
					DrawModeHub(hubGraphics, accent, accent2);
					hubGraphics.Restore(gstate3);
				}
				if (layers.ForegroundLayer != null)
				{
					using (Graphics foregroundGraphics = Graphics.FromImage(layers.ForegroundLayer))
					{
						foregroundGraphics.Clear(Color.Transparent);
						foregroundGraphics.SmoothingMode = SmoothingMode.AntiAlias;
						foregroundGraphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
						if (!immersive)
						{
							DrawCoreLabels(foregroundGraphics, (float)base.Width * 0.5f, DesignCenterY, accent);
							DrawFrameDetails(foregroundGraphics, accent);
						}
					}
				}
				return layers;
			}
			catch
			{
				layers.Dispose();
				throw;
			}
		}

		private void DrawLayer(Graphics graphics, Bitmap from, Bitmap target)
		{
			DrawLayerAt(graphics, from, target, Point.Empty);
		}

		private void DrawStaticLayer(Graphics graphics)
		{
			Bitmap bitmap = ((transitionLayers == null) ? null : transitionLayers.StaticLayer);
			if (staticLayers.Backdrop)
			{
				DrawLayer(graphics, staticLayers.StaticLayer, bitmap);
				return;
			}
			CompositingMode compositingMode = graphics.CompositingMode;
			try
			{
				graphics.CompositingMode = CompositingMode.SourceCopy;
				graphics.DrawImageUnscaled((bitmap != null && layerBlend >= 1f) ? bitmap : staticLayers.StaticLayer, 0, 0);
				if (bitmap != null && layerBlend > 0f && layerBlend < 1f)
				{
					graphics.CompositingMode = CompositingMode.SourceOver;
					graphics.DrawImage(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height), 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel, layerBlendAttributes);
				}
			}
			finally
			{
				graphics.CompositingMode = compositingMode;
			}
		}

		private void DrawLayerAt(Graphics graphics, Bitmap from, Bitmap target, Point location)
		{
			if (from != null)
			{
				if (target == null || layerBlend <= 0f)
				{
					graphics.DrawImageUnscaled(from, location);
					return;
				}
				if (layerBlend >= 1f)
				{
					graphics.DrawImageUnscaled(target, location);
					return;
				}
				graphics.DrawImageUnscaled(from, location);
				graphics.DrawImage(target, new Rectangle(location, target.Size), 0, 0, target.Width, target.Height, GraphicsUnit.Pixel, layerBlendAttributes);
			}
		}

		private Rectangle HubCacheBounds()
		{
			RectangleF rectangleF = ((artworkMode == PerformancePreset.Handheld) ? new RectangleF(-84f, -43f, 168f, 90f) : ((artworkMode == PerformancePreset.Extreme) ? new RectangleF(-56f, -65f, 112f, 130f) : new RectangleF(-96f, -67f, 192f, 130f)));
			float designScale = DesignScale;
			return Rectangle.Ceiling(new RectangleF((float)base.Width * 0.5f + rectangleF.X * designScale, DesignCenterY + rectangleF.Y * designScale, Math.Max(1f, rectangleF.Width * designScale), Math.Max(1f, rectangleF.Height * designScale)));
		}

		private void DrawLayer(Graphics graphics, Bitmap from, Bitmap target, RectangleF destination, Rectangle source)
		{
			if (from != null)
			{
				if (target == null || layerBlend <= 0f)
				{
					graphics.DrawImage(from, destination, source, GraphicsUnit.Pixel);
					return;
				}
				if (layerBlend >= 1f)
				{
					graphics.DrawImage(target, destination, source, GraphicsUnit.Pixel);
					return;
				}
				graphics.DrawImage(from, destination, source, GraphicsUnit.Pixel);
				graphics.DrawImage(target, Rectangle.Round(destination), source.X, source.Y, source.Width, source.Height, GraphicsUnit.Pixel, layerBlendAttributes);
			}
		}

		private void DropCache()
		{
			DropMotionCache();
			if (staticLayers != null)
			{
				staticLayers.Dispose();
				staticLayers = null;
			}
			if (transitionLayers != null)
			{
				transitionLayers.Dispose();
				transitionLayers = null;
			}
			layerBlend = 0f;
		}

		private void DrawGrid(Graphics g)
		{
			using (Pen pen = new Pen(Color.FromArgb(5, 170, 185, 205)))
			{
				int gridSpacing = Math.Max(Theme.S(18), 18);
				for (int i = gridSpacing; i < base.Width; i += gridSpacing)
				{
					g.DrawLine(pen, i, 0, i, base.Height);
				}
				for (int j = gridSpacing; j < base.Height; j += gridSpacing)
				{
					g.DrawLine(pen, 0, j, base.Width, j);
				}
			}
			using (Pen pen2 = new Pen(Color.FromArgb(10, 150, 160, 178)))
			{
				g.DrawLine(pen2, base.Width / 2, Theme.S(20), base.Width / 2, base.Height - Theme.S(20));
				g.DrawLine(pen2, Theme.S(20), base.Height / 2, base.Width - Theme.S(20), base.Height / 2);
			}
		}

		private void DrawCoreLabels(Graphics g, float cx, float cy, Color accent)
		{
			float designScale = DesignScale;
			string text = (gameActive ? "ACTIVE" : (guardEnabled ? "STANDBY" : "OFFLINE"));
			float labelOffset = ((artworkMode == PerformancePreset.Competitive || artworkMode == PerformancePreset.Standard) ? 106 : 50);
			LabelText(g, ModeButton.ModeName(mode), Theme.UI(13f, true), Theme.Fg, new RectangleF(0f, base.Height - Theme.S(65), base.Width, Theme.S(25)));
			LabelText(g, text, Theme.Mono(6f), gameActive ? Theme.Green : (guardEnabled ? accent : Theme.Faint), new RectangleF(cx - 42f * designScale, cy + labelOffset * designScale, 84f * designScale, 10f * designScale));
		}

		private static void LabelText(Graphics g, string text, Font font, Color color, RectangleF box)
		{
			using (SolidBrush brush = new SolidBrush(color))
			{
				StringFormat stringFormat = new StringFormat();
				stringFormat.Alignment = StringAlignment.Center;
				stringFormat.LineAlignment = StringAlignment.Center;
				stringFormat.Trimming = StringTrimming.EllipsisCharacter;
				stringFormat.FormatFlags = StringFormatFlags.NoWrap;
				using (StringFormat format = stringFormat)
				{
					g.DrawString(text, font, brush, box, format);
				}
			}
		}

		private void DrawFrameDetails(Graphics g, Color accent)
		{
			string text = ((artworkMode == PerformancePreset.Extreme) ? "04 / ARC REACTOR" : ((artworkMode == PerformancePreset.Competitive) ? "02 / TARGET LOCK" : ((artworkMode == PerformancePreset.Handheld) ? "03 / MOBILE CORE" : "01 / ADAPTIVE CORE")));
			LabelText(g, "SYS / 3.0", Theme.Mono(6.5f), Theme.Faint, new RectangleF(Theme.S(12), Theme.S(12), Theme.S(64), Theme.S(14)));
			LabelText(g, gameActive ? "LIVE LINK" : (guardEnabled ? "READY" : "OFFLINE"), Theme.Mono(6.5f), gameActive ? Theme.Green : Theme.Faint, new RectangleF(base.Width - Theme.S(84), Theme.S(12), Theme.S(72), Theme.S(14)));
			LabelText(g, text, Theme.Mono(7f), accent, new RectangleF(0f, base.Height - Theme.S(30), base.Width, Theme.S(16)));
			using (Pen pen = new Pen(accent, Math.Max(1f, Theme.S(1))))
			{
				g.DrawLine(pen, Theme.S(1), Theme.S(1), Theme.S(72), Theme.S(1));
				g.DrawLine(pen, base.Width - Theme.S(18), Theme.S(2), base.Width - Theme.S(2), Theme.S(18));
			}
			using (Pen pen2 = new Pen(Col.Alpha(accent, 65), 1f))
			{
				g.DrawLine(pen2, Theme.S(16), base.Height - Theme.S(22), Theme.S(57), base.Height - Theme.S(22));
				g.DrawLine(pen2, base.Width - Theme.S(57), base.Height - Theme.S(22), base.Width - Theme.S(16), base.Height - Theme.S(22));
			}
		}

		private GraphicsState BeginDesign(Graphics g)
		{
			GraphicsState result = g.Save();
			g.TranslateTransform((float)base.Width * 0.5f, DesignCenterY);
			g.ScaleTransform(DesignScale, DesignScale);
			return result;
		}

		private static PointF Polar(float radius, double degrees)
		{
			double radians = degrees * Math.PI / 180.0;
			return new PointF(radius * (float)Math.Cos(radians), radius * (float)Math.Sin(radians));
		}

		private static PointF[] Capsule(float x, float y, float w, float h, float cut)
		{
			return new PointF[8]
			{
				new PointF(x + cut, y),
				new PointF(x + w - cut, y),
				new PointF(x + w, y + cut),
				new PointF(x + w, y + h - cut),
				new PointF(x + w - cut, y + h),
				new PointF(x + cut, y + h),
				new PointF(x, y + h - cut),
				new PointF(x, y + cut)
			};
		}

		private static void Edge(Graphics g, PointF[] points, Color color, int alpha, float width)
		{
			using (Pen pen = new Pen(Col.Alpha(color, alpha), width))
			{
				g.DrawPolygon(pen, points);
			}
		}

		private static void Plate(Graphics g, PointF[] points, Color color, int alpha)
		{
			using (SolidBrush brush = new SolidBrush(Col.Alpha(color, alpha)))
			{
				g.FillPolygon(brush, points);
			}
		}

		private static void Halo(Graphics g, RectangleF bounds, Color color, int alpha)
		{
			using (GraphicsPath graphicsPath = new GraphicsPath())
			{
				graphicsPath.AddEllipse(bounds);
				using (PathGradientBrush pathGradientBrush = new PathGradientBrush(graphicsPath))
				{
					pathGradientBrush.CenterColor = Col.Alpha(color, alpha);
					pathGradientBrush.SurroundColors = new Color[1] { Col.Alpha(color, 0) };
					g.FillPath(pathGradientBrush, graphicsPath);
				}
			}
		}

		private static void Armor(Graphics g, PointF[] points, Color color)
		{
			using (GraphicsPath graphicsPath = new GraphicsPath())
			{
				graphicsPath.AddPolygon(points);
				RectangleF bounds = graphicsPath.GetBounds();
				GraphicsState gstate = g.Save();
				g.TranslateTransform(0f, 3f);
				using (SolidBrush brush = new SolidBrush(Col.Alpha(Color.Black, Theme.LightMode ? 15 : 110)))
				{
					g.FillPath(brush, graphicsPath);
				}
				g.Restore(gstate);
				using (LinearGradientBrush brush2 = new LinearGradientBrush(bounds, Col.Lerp(Theme.Card, color, Theme.LightMode ? 0.06f : 0.16f), Theme.Inset, 100f))
				{
					g.FillPath(brush2, graphicsPath);
				}
				using (Pen pen = new Pen(Col.Alpha(color, 95), 0.65f))
				{
					g.DrawPath(pen, graphicsPath);
				}
				using (Pen pen2 = new Pen(Col.Alpha(Col.Lerp(color, Theme.Fg, 0.55f), 155), 0.6f))
				{
					g.DrawLines(pen2, new PointF[3]
					{
						points[points.Length - 1],
						points[0],
						points[1]
					});
				}
			}
		}

		private static void GlowLine(Graphics g, PointF a, PointF b, Color color, float strength)
		{
			using (Pen pen = new Pen(Col.Alpha(color, (int)(25f * strength)), 5f))
			{
				g.DrawLine(pen, a, b);
			}
			using (Pen pen2 = new Pen(Col.Alpha(color, (int)(230f * strength)), 1.25f))
			{
				g.DrawLine(pen2, a, b);
			}
		}

		private void DrawModeStructure(Graphics g, Color c)
		{
			if (artworkMode == PerformancePreset.Extreme)
			{
				DrawExtremeStructure(g, c);
			}
			else if (artworkMode == PerformancePreset.Handheld)
			{
				Halo(g, new RectangleF(-130f, -130f, 260f, 260f), c, Theme.LightMode ? 20 : 65);
				GraphicsState gstate = g.Save();
				g.RotateTransform(-22f);
				using (Pen pen = new Pen(Col.Alpha(c, 55), 0.8f))
				{
					g.DrawEllipse(pen, -117, -70, 234, 140);
				}
				using (Pen pen2 = new Pen(Col.Alpha(c, 120), 2f))
				{
					g.DrawArc(pen2, -122, -75, 244, 150, 203, 44);
					g.DrawArc(pen2, -122, -75, 244, 150, 23, 44);
				}
				g.Restore(gstate);
				Halo(g, new RectangleF(-88f, 53f, 176f, 39f), c, 65);
				for (int i = 0; i < 3; i++)
				{
					using (Pen pen3 = new Pen(Col.Alpha(c, 70 - i * 18), 0.8f))
					{
						g.DrawEllipse(pen3, -77 - i * 7, 67 - i * 5, 154 + i * 14, 20 + i * 10);
					}
				}
				for (int j = -1; j <= 1; j += 2)
				{
					gstate = g.Save();
					g.ScaleTransform(j, 1f);
					PointF[] points = new PointF[7]
					{
						new PointF(93f, -33f),
						new PointF(105f, -23f),
						new PointF(105f, 22f),
						new PointF(94f, 33f),
						new PointF(89f, 26f),
						new PointF(94f, 19f),
						new PointF(94f, -20f)
					};
					Armor(g, points, c);
					GlowLine(g, new PointF(105f, -16f), new PointF(105f, 16f), c, 0.8f);
					g.Restore(gstate);
				}
			}
			else
			{
				DrawCompetitiveStructure(g, c);
			}
		}

		private void DrawModeMotion(Graphics g, double seconds, Color c)
		{
			float motionStrength = (guardEnabled ? 1f : 0.38f);
			if (artworkMode == PerformancePreset.Extreme)
			{
				DrawExtremeMotion(g, seconds, c, motionStrength, false);
				return;
			}
			if (artworkMode == PerformancePreset.Handheld)
			{
				GraphicsState gstate = g.Save();
				g.RotateTransform(-22f);
				DrawOrbitFlow(g, 117f, 70f, seconds * 45.0, c, motionStrength, false);
				g.Restore(gstate);
				for (int i = 0; i < 12; i++)
				{
					float particleProgress = (float)((seconds / 3.0 + (double)i / 12.0) % 1.0);
					float particleAngle = -60 + i * 11;
					using (Pen pen = new Pen(Col.Alpha(c, (int)(65.0 * Math.Sin((double)particleProgress * Math.PI) * (double)motionStrength)), 0.8f))
					{
						g.DrawLine(pen, particleAngle, 72f - particleProgress * 80f, particleAngle, 65f - particleProgress * 80f);
					}
				}
				float pulseStrength = 0.5f + 0.5f * (float)Math.Sin(seconds * Math.PI / 3.0);
				using (Pen pen2 = new Pen(Col.Alpha(c, (int)((80f + 90f * pulseStrength) * motionStrength)), 1.6f))
				{
					g.DrawArc(pen2, -79, 64, 158, 25, 15, 145);
					return;
				}
			}
			DrawCompetitiveMotion(g, seconds, c, motionStrength, false);
		}

		private void DrawFrontLight(Graphics g, double seconds, Color c)
		{
			float lightStrength = (guardEnabled ? 1f : 0.38f);
			if (artworkMode == PerformancePreset.Extreme)
			{
				DrawExtremeMotion(g, seconds, c, lightStrength, true);
			}
			else if (artworkMode == PerformancePreset.Handheld)
			{
				GraphicsState gstate = g.Save();
				g.RotateTransform(-22f);
				using (Pen pen = new Pen(Col.Alpha(c, (int)(80f * lightStrength)), 0.8f))
				{
					g.DrawArc(pen, -117, -70, 234, 140, 0, 180);
				}
				DrawOrbitFlow(g, 117f, 70f, seconds * 45.0, c, lightStrength, true);
				g.Restore(gstate);
			}
			else
			{
				DrawCompetitiveMotion(g, seconds, c, lightStrength, true);
			}
		}

		private void DrawModeHub(Graphics g, Color a, Color b)
		{
			if (artworkMode == PerformancePreset.Extreme)
			{
				DrawExtremeHub(g, a);
			}
			else if (artworkMode == PerformancePreset.Handheld)
			{
				Armor(g, Capsule(-80f, -39f, 160f, 78f, 16f), a);
				Armor(g, Capsule(-44f, -31f, 88f, 62f, 7f), a);
				using (LinearGradientBrush brush = new LinearGradientBrush(new Rectangle(-37, -25, 74, 50), Col.Lerp(Theme.Inset, a, 0.25f), Theme.Inset, 60f))
				{
					g.FillRectangle(brush, -37, -25, 74, 50);
				}
				Halo(g, new RectangleF(-38f, -29f, 76f, 58f), a, 70);
				GlowLine(g, new PointF(-68f, -35f), new PointF(60f, -35f), a, 0.85f);
				using (Pen pen = new Pen(Col.Lerp(a, Theme.Fg, 0.6f), 2.3f))
				{
					g.DrawLine(pen, -69, -6, -53, -6);
					g.DrawLine(pen, -61, -14, -61, 2);
				}
				using (SolidBrush brush2 = new SolidBrush(a))
				{
					g.FillEllipse(brush2, 57, -15, 4, 4);
					g.FillEllipse(brush2, 66, -6, 4, 4);
					g.FillEllipse(brush2, 57, 3, 4, 4);
					g.FillEllipse(brush2, 48, -6, 4, 4);
				}
				using (Pen pen2 = new Pen(Col.Alpha(a, 110), 1.2f))
				{
					g.DrawEllipse(pen2, -62, 12, 10, 10);
					g.DrawEllipse(pen2, 53, 15, 8, 8);
				}
				PointF[] points = new PointF[6]
				{
					new PointF(5f, -20f),
					new PointF(-16f, 4f),
					new PointF(-2f, 4f),
					new PointF(-6f, 22f),
					new PointF(17f, -5f),
					new PointF(3f, -5f)
				};
				Plate(g, points, Col.Lerp(a, Theme.Fg, 0.25f), 255);
			}
			else
			{
				DrawCompetitiveHub(g, a);
			}
		}

		private void DrawExtremeStructure(Graphics g, Color c)
		{
			Halo(g, new RectangleF(-120f, -120f, 240f, 240f), c, Theme.LightMode ? 28 : 85);
			for (int i = 0; i < 3; i++)
			{
				GraphicsState gstate = g.Save();
				g.RotateTransform(i * 120 - 30);
				using (Pen pen = new Pen(Col.Alpha(c, 48), 0.65f))
				{
					g.DrawEllipse(pen, -112, -52, 224, 104);
				}
				using (Pen pen2 = new Pen(Col.Alpha(c, 105), 0.8f))
				{
					g.DrawArc(pen2, -117, -57, 234, 114, 190, 65);
					g.DrawArc(pen2, -117, -57, 234, 114, 10, 65);
					g.DrawLines(pen2, new PointF[3]
					{
						new PointF(-13f, -119f),
						new PointF(13f, -119f),
						new PointF(25f, -107f)
					});
				}
				GlowLine(g, new PointF(-13f, -119f), new PointF(8f, -119f), c, 0.8f);
				for (int j = 0; j < 5; j++)
				{
					using (Pen pen3 = new Pen(Col.Alpha(c, 100 - j * 14), 0.8f))
					{
						g.DrawLine(pen3, -27 + j * 3, -106, -24 + j * 3, -110);
					}
				}
				g.Restore(gstate);
			}
			using (Pen pen4 = new Pen(Col.Alpha(c, 50), 0.6f))
			{
				g.DrawLine(pen4, -130, 0, -102, 0);
				g.DrawLine(pen4, 102, 0, 130, 0);
				g.DrawLine(pen4, 0, -130, 0, -113);
			}
		}

		private void DrawExtremeMotion(Graphics g, double seconds, Color c, float power, bool front)
		{
			Color highlight = Col.Lerp(c, Theme.LightMode ? Theme.Fg : Color.White, 0.8f);
			for (int i = 0; i < 3; i++)
			{
				GraphicsState gstate = g.Save();
				g.RotateTransform(i * 120 - 30);
				DrawOrbitFlow(g, 112f, 52f, seconds * 65.0 + (double)(i * 120), c, power, front);
				g.Restore(gstate);
			}
			if (front)
			{
				float pulseStrength = 0.55f + 0.45f * (float)Math.Sin(seconds * Math.PI);
				GlowLine(g, new PointF(0f, -34f), new PointF(0f, 29f), highlight, (0.45f + pulseStrength * 0.45f) * power);
				for (int j = 0; j < 3; j++)
				{
					double waveProgress = (seconds / 2.4 + (double)j / 3.0) % 1.0;
					float waveStrength = (float)Math.Pow(Math.Sin(waveProgress * Math.PI), 6.0) * power;
					PointF a = Polar(42f, j * 120 - 90);
					PointF pointF = Polar(56f, j * 120 - 79);
					PointF b = Polar(70f, j * 120 - 90);
					GlowLine(g, a, pointF, c, waveStrength * 0.65f);
					GlowLine(g, pointF, b, highlight, waveStrength * 0.55f);
				}
			}
		}

		private void DrawExtremeHub(Graphics g, Color c)
		{
			Halo(g, new RectangleF(-54f, -63f, 108f, 126f), c, 110);
			PointF pointF = new PointF(0f, -39f);
			PointF pointF2 = new PointF(-25f, -7f);
			PointF pointF3 = new PointF(25f, -7f);
			PointF pointF4 = new PointF(0f, 34f);
			PointF pointF5 = new PointF(0f, 3f);
			Color highlight = Col.Lerp(c, Theme.LightMode ? Theme.Fg : Color.White, 0.68f);
			Plate(g, new PointF[3] { pointF, pointF2, pointF5 }, c, 95);
			Plate(g, new PointF[3] { pointF, pointF3, pointF5 }, highlight, 65);
			Plate(g, new PointF[3] { pointF2, pointF4, pointF5 }, c, 45);
			Plate(g, new PointF[3] { pointF3, pointF4, pointF5 }, c, 110);
			Edge(g, new PointF[4] { pointF, pointF3, pointF4, pointF2 }, highlight, 225, 1f);
			GlowLine(g, pointF, pointF5, highlight, 0.9f);
			using (Pen pen = new Pen(Col.Alpha(c, 190), 0.8f))
			{
				g.DrawLine(pen, pointF2, pointF5);
				g.DrawLine(pen, pointF3, pointF5);
				g.DrawLine(pen, pointF5, pointF4);
			}
			for (int i = -1; i <= 1; i += 2)
			{
				PointF[] points = new PointF[4]
				{
					new PointF(i * 13, -47f),
					new PointF(i * 35, -16f),
					new PointF(i * 35, 10f),
					new PointF(i * 19, 31f)
				};
				using (Pen pen2 = new Pen(Col.Alpha(c, 130), 0.8f))
				{
					g.DrawLines(pen2, points);
				}
			}
		}

		private static SmartPoint SmartTransform(float x, float y, float z, double turn, double tilt, double roll)
		{
			double turnedX = (double)x * Math.Cos(turn) + (double)z * Math.Sin(turn);
			double turnedZ = (double)(0f - x) * Math.Sin(turn) + (double)z * Math.Cos(turn);
			double tiltedY = (double)y * Math.Cos(tilt) - turnedZ * Math.Sin(tilt);
			double tiltedZ = (double)y * Math.Sin(tilt) + turnedZ * Math.Cos(tilt);
			SmartPoint result = default(SmartPoint);
			result.X = (float)(turnedX * Math.Cos(roll) - tiltedY * Math.Sin(roll));
			result.Y = (float)(turnedX * Math.Sin(roll) + tiltedY * Math.Cos(roll));
			result.Z = (float)tiltedZ;
			return result;
		}

		private static void DrawSmartStructure(Graphics g, Color c)
		{
			Halo(g, new RectangleF(-139f, -136f, 278f, 265f), c, Theme.LightMode ? 27 : 60);
			Halo(g, new RectangleF(-93f, 68f, 186f, 35f), c, Theme.LightMode ? 22 : 58);
			for (int i = 0; i < 3; i++)
			{
				using (Pen pen = new Pen(Col.Alpha(c, 72 - i * 19), 0.7f))
				{
					g.DrawEllipse(pen, -72 - 9 * i, 78 - i * 4, 144 + i * 18, 16 + i * 8);
				}
			}
			for (int j = -1; j <= 1; j += 2)
			{
				GraphicsState gstate = g.Save();
				g.ScaleTransform(j, 1f);
				PointF[] points = new PointF[8]
				{
					new PointF(83f, -63f),
					new PointF(108f, -43f),
					new PointF(119f, -5f),
					new PointF(108f, 38f),
					new PointF(89f, 54f),
					new PointF(97f, 28f),
					new PointF(103f, -4f),
					new PointF(95f, -36f)
				};
				Armor(g, points, c);
				GlowLine(g, new PointF(110f, -31f), new PointF(117f, -7f), c, 1.2f);
				using (Pen pen2 = new Pen(Col.Alpha(c, 100), 0.7f))
				{
					g.DrawLines(pen2, new PointF[3]
					{
						new PointF(73f, -77f),
						new PointF(101f, -57f),
						new PointF(111f, -32f)
					});
					g.DrawLine(pen2, 101, 51, 84, 65);
					for (int k = 0; k < 5; k++)
					{
						g.DrawLine(pen2, 125, -18 + k * 8, 128 + ((k == 2) ? 4 : 0), -18 + k * 8);
					}
				}
				g.Restore(gstate);
			}
			using (Pen pen3 = new Pen(Col.Alpha(c, 36), 0.6f))
			{
				g.DrawLine(pen3, 0, -107, 0, -83);
				g.DrawLine(pen3, -8, -97, 8, -97);
			}
		}

		private void DrawSmartMotion(Graphics g, double time, Color c)
		{
			float motionStrength = (guardEnabled ? 1f : 0.4f);
			double pulsePhase = 0.5 + 0.5 * Math.Sin(time * 1.6);
			float verticalDrift = (float)Math.Sin(time * 1.05) * 3f;
			for (int i = 0; i < 3; i++)
			{
				double ringPhase = time * 0.22 + (double)i / 3.0;
				ringPhase -= Math.Floor(ringPhase);
				float ringRadius = 26f + (float)ringPhase * 60f;
				using (Pen pen = new Pen(Col.Alpha(c, (int)(Math.Sin(ringPhase * Math.PI) * 60.0 * (double)motionStrength)), 0.8f))
				{
					g.DrawEllipse(pen, 0f - ringRadius, 86f - ringRadius * 0.12f, ringRadius * 2f, ringRadius * 0.24f);
				}
			}
			GraphicsState gstate = g.Save();
			g.TranslateTransform(0f, verticalDrift - 6f);
			SmartOrbit(g, time, c, motionStrength, false, 0);
			SmartOrbit(g, time, c, motionStrength, false, 1);
			Halo(g, new RectangleF(-60f, -68f, 120f, 136f), c, (int)((42.0 + pulsePhase * 17.0) * (double)motionStrength));
			DrawSmartCrystal(g, time, c, motionStrength);
			SmartOrbit(g, time, c, motionStrength, true, 0);
			SmartOrbit(g, time, c, motionStrength, true, 1);
			g.Restore(gstate);
		}

		private static SmartPoint SmartOrbitPoint(double angle, double time, int orbit)
		{
			float orbitRadius = ((orbit == 0) ? 101 : 115);
			return SmartTransform(orbitRadius * (float)Math.Cos(angle), orbitRadius * (float)Math.Sin(angle), 0f, 0.0, (orbit == 0) ? 0.91 : 1.13, ((orbit == 0) ? (-0.48) : 0.69) + Math.Sin(time * 0.19 + (double)orbit) * 0.09);
		}

		private static void SmartOrbit(Graphics g, double time, Color c, float power, bool front, int orbit)
		{
			Color orbitColor = ((orbit == 0) ? c : Col.Lerp(c, Theme.LightMode ? Theme.Fg : Color.White, 0.28f));
			double rotationPhase = time * ((orbit == 0) ? 0.52 : (-0.38)) + (double)(orbit * 2);
			using (Pen pen = new Pen(c, 0.85f))
			{
				SmartPoint smartPoint = SmartOrbitPoint(0.0, time, orbit);
				for (int i = 1; i <= 96; i++)
				{
					double segmentAngle = (double)i * Math.PI * 2.0 / 96.0;
					SmartPoint smartPoint2 = SmartOrbitPoint(segmentAngle, time, orbit);
					if (smartPoint2.Z + smartPoint.Z >= 0f == front)
					{
						double segmentBrightness = 0.5 + 0.5 * Math.Cos(segmentAngle - rotationPhase);
						int a = (int)((double)((float)(front ? 65 : 24) * power) + Math.Pow(segmentBrightness, 18.0) * (double)(front ? 145 : 60) * (double)power);
						pen.Color = Col.Alpha(orbitColor, a);
						g.DrawLine(pen, smartPoint.Screen, smartPoint2.Screen);
					}
					smartPoint = smartPoint2;
				}
			}
			for (int j = 0; j < 3; j++)
			{
				SmartPoint smartPoint3 = SmartOrbitPoint(rotationPhase + (double)j * Math.PI * 2.0 / 3.0, time, orbit);
				float depthBlend = Math.Max(0f, Math.Min(1f, (smartPoint3.Z + 9f) / 18f));
				float segmentStrength = power * (front ? depthBlend : (1f - depthBlend));
				PointF screen = smartPoint3.Screen;
				Halo(g, new RectangleF(screen.X - 7f, screen.Y - 7f, 14f, 14f), orbitColor, (int)(90f * segmentStrength));
				using (Pen pen2 = new Pen(Col.Alpha(orbitColor, (int)(210f * segmentStrength)), 0.8f))
				{
					g.DrawPolygon(pen2, new PointF[4]
					{
						new PointF(screen.X, screen.Y - 3f),
						new PointF(screen.X + 3f, screen.Y),
						new PointF(screen.X, screen.Y + 3f),
						new PointF(screen.X - 3f, screen.Y)
					});
				}
				using (SolidBrush brush = new SolidBrush(Col.Alpha(Theme.LightMode ? c : Color.White, (int)(235f * segmentStrength))))
				{
					g.FillEllipse(brush, screen.X - 1f, screen.Y - 1f, 2f, 2f);
				}
			}
		}

		private static void DrawSmartCrystal(Graphics g, double time, Color c, float power)
		{
			double turn = time * 0.23 + 0.45;
			double tilt = 0.2 + Math.Sin(time * 0.41) * 0.12;
			double roll = -0.13 + Math.Sin(time * 0.28) * 0.07;
			SmartPoint[] array = new SmartPoint[6]
			{
				SmartTransform(0f, -62f, 0f, turn, tilt, roll),
				SmartTransform(0f, 62f, 0f, turn, tilt, roll),
				SmartTransform(43f, 0f, 0f, turn, tilt, roll),
				SmartTransform(0f, 0f, 43f, turn, tilt, roll),
				SmartTransform(-43f, 0f, 0f, turn, tilt, roll),
				SmartTransform(0f, 0f, -43f, turn, tilt, roll)
			};
			int[] array2 = new int[8] { 0, 1, 2, 3, 4, 5, 6, 7 };
			float[] depth = new float[8];
			for (int i = 0; i < 8; i++)
			{
				depth[i] = (array[i / 4].Z + array[2 + i % 4].Z + array[2 + (i + 1) % 4].Z) / 3f;
			}
			Array.Sort(array2, (int a, int b) => depth[a].CompareTo(depth[b]));
			int[] array3 = array2;
			foreach (int faceIndex in array3)
			{
				PointF[] points = new PointF[3]
				{
					array[faceIndex / 4].Screen,
					array[2 + faceIndex % 4].Screen,
					array[2 + (faceIndex + 1) % 4].Screen
				};
				float faceBlend = Math.Max(0.08f, Math.Min(0.88f, 0.25f + depth[faceIndex] / 100f + ((faceIndex % 4 == 1) ? 0.17f : 0f)));
				Color faceStart = Col.Lerp(Theme.Inset, c, faceBlend * power);
				Color faceEnd = Col.Lerp(Theme.Card, c, (faceBlend + 0.17f) * power);
				using (GraphicsPath graphicsPath = new GraphicsPath())
				{
					graphicsPath.AddPolygon(points);
					using (LinearGradientBrush brush = new LinearGradientBrush(new RectangleF(-66f, -78f, 132f, 156f), faceEnd, faceStart, 62f))
					{
						g.FillPath(brush, graphicsPath);
					}
				}
				float frontBlend = Math.Max(0f, Math.Min(1f, (depth[faceIndex] + 8f) / 16f));
				using (Pen pen = new Pen(Col.Alpha(c, (int)((65f + frontBlend * 135f) * power)), 0.85f))
				{
					g.DrawPolygon(pen, points);
				}
			}
			for (int k = 0; k < 4; k++)
			{
				SmartPoint smartPoint = array[2 + k];
				if (smartPoint.Z < 0f)
				{
					continue;
				}
				float highlightDepth = Math.Min(1f, smartPoint.Z / 8f);
				for (int l = 0; l < 2; l++)
				{
					PointF screen = array[l].Screen;
					PointF screen2 = smartPoint.Screen;
					using (Pen pen2 = new Pen(Col.Alpha(c, (int)(65f * power * highlightDepth)), 0.55f))
					{
						g.DrawLine(pen2, screen.X * 0.76f, screen.Y * 0.76f, screen2.X * 0.76f, screen2.Y * 0.76f);
					}
					double sparkPhase = time * 0.25 + (double)k * 0.25 + (double)l * 0.5;
					sparkPhase -= Math.Floor(sparkPhase);
					float sparkProgress = (float)sparkPhase;
					PointF pointF = new PointF(screen.X + (screen2.X - screen.X) * sparkProgress, screen.Y + (screen2.Y - screen.Y) * sparkProgress);
					float sparkStrength = (float)Math.Sin(sparkPhase * Math.PI) * power * highlightDepth;
					Halo(g, new RectangleF(pointF.X - 5f, pointF.Y - 5f, 10f, 10f), c, (int)(130f * sparkStrength));
					using (SolidBrush brush2 = new SolidBrush(Col.Alpha(Theme.LightMode ? c : Color.White, (int)(230f * sparkStrength))))
					{
						g.FillEllipse(brush2, pointF.X - 1f, pointF.Y - 1f, 2f, 2f);
					}
				}
			}
			float pulseStrength = 0.5f + 0.5f * (float)Math.Sin(time * 1.6);
			Halo(g, new RectangleF(-25f, -30f, 50f, 60f), c, (int)((95f + 25f * pulseStrength) * power));
			PointF[] points2 = new PointF[4]
			{
				new PointF(0f, -16f),
				new PointF(9f, 0f),
				new PointF(0f, 16f),
				new PointF(-9f, 0f)
			};
			Plate(g, points2, Col.Lerp(c, Color.White, 0.65f), (int)(225f * power));
			Edge(g, points2, Theme.LightMode ? c : Color.White, (int)(240f * power), 0.8f);
		}
	}
}
