using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class GameLibraryRow : Control
	{
		private readonly ToolTip tips;

		private bool selected;

		private bool hovered;

		public Func<string, Bitmap> IconProvider;

		public readonly FamilySuppressionSwitch FamilySwitch;

		public readonly FamilySuppressionSwitch CoreSwitch;

		public readonly FamilySuppressionSwitch RebarSwitch;

		public readonly FamilySuppressionSwitch FullscreenOptimizationsSwitch;

		public readonly PillButton CoreButton;

		public readonly PillButton MoreButton;

		public readonly GameCardExtension Extension;

		public GameLibraryItem Item { get; private set; }

		public int ExtraHeight
		{
			get
			{
				return ExtraHeightForWidth(base.ClientSize.Width);
			}
		}

		public bool Selected
		{
			get
			{
				return selected;
			}
			set
			{
				if (selected != value)
				{
					selected = value;
					PushSurface();
					Invalidate();
				}
			}
		}

		private Color Surface
		{
			get
			{
				if (!selected)
				{
					if (!hovered)
					{
						return Theme.Card;
					}
					return Theme.CardHover;
				}
				return Col.Lerp(Theme.Card, Theme.Accent, 0.025f);
			}
		}

		private string MetadataText
		{
			get
			{
				int ordinaryOverrideCountResult = OrdinaryOverrideCount(Item.Profile);
				string libConfigTagText = ((ordinaryOverrideCountResult > 0) ? Lang.F("lib.config.tag", ordinaryOverrideCountResult) : "");
				if (Item.Profile.ForceTrigger)
				{
					libConfigTagText = Lang.T("v15.library.forced.tag") + ((libConfigTagText.Length > 0) ? (" · " + libConfigTagText) : "");
				}
				return libConfigTagText;
			}
		}

		public event EventHandler SelectionRequested;

		public event EventHandler FamilyRequested;

		public event EventHandler CoreRequested;

		public event EventHandler CoreToggleRequested;

		public event EventHandler RebarRequested;

		public event EventHandler FullscreenOptimizationsRequested;

		public event EventHandler MoreRequested;

		public GameLibraryRow(GameLibraryItem item, ToolTip toolTip)
		{
			Item = item;
			tips = toolTip;
			SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardDoubleClick | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
			base.TabStop = true;
			Cursor = Cursors.Hand;
			BackColor = Theme.Card;
			base.Margin = Padding.Empty;
			base.AccessibleRole = AccessibleRole.ListItem;
			CoreSwitch = new FamilySuppressionSwitch(true);
			CoreSwitch.Name = "gameCoreSwitch";
			CoreSwitch.TabIndex = 0;
			FamilySuppressionSwitch coreSwitch = CoreSwitch;
			EventHandler value = delegate
			{
				if (this.CoreToggleRequested != null)
				{
					this.CoreToggleRequested(this, EventArgs.Empty);
				}
			};
			coreSwitch.Click += value;
			CoreSwitch.Enter += delegate
			{
				if (this.SelectionRequested != null)
				{
					this.SelectionRequested(this, EventArgs.Empty);
				}
			};
			base.Controls.Add(CoreSwitch);
			CoreButton = new GameCardDetailsButton();
			CoreButton.Name = "gameCoreScheduling";
			CoreButton.TabIndex = 0;
			CoreButton.Click += delegate
			{
				if (this.CoreRequested != null)
				{
					this.CoreRequested(this, EventArgs.Empty);
				}
			};
			CoreButton.Enter += delegate
			{
				if (this.SelectionRequested != null)
				{
					this.SelectionRequested(this, EventArgs.Empty);
				}
			};
			CoreSwitch.Controls.Add(CoreButton);
			FamilySwitch = new FamilySuppressionSwitch();
			FamilySwitch.TabIndex = 1;
			FamilySwitch.Click += delegate
			{
				if (this.FamilyRequested != null)
				{
					this.FamilyRequested(this, EventArgs.Empty);
				}
			};
			FamilySwitch.Enter += delegate
			{
				if (this.SelectionRequested != null)
				{
					this.SelectionRequested(this, EventArgs.Empty);
				}
			};
			base.Controls.Add(FamilySwitch);
			RebarSwitch = new FamilySuppressionSwitch();
			RebarSwitch.Name = "gameRebarSwitch";
			RebarSwitch.TabIndex = 2;
			RebarSwitch.Text = Lang.T("lib.rebar");
			RebarSwitch.Click += delegate
			{
				if (this.RebarRequested != null)
				{
					this.RebarRequested(this, EventArgs.Empty);
				}
			};
			RebarSwitch.Enter += delegate
			{
				if (this.SelectionRequested != null)
				{
					this.SelectionRequested(this, EventArgs.Empty);
				}
			};
			base.Controls.Add(RebarSwitch);
			FullscreenOptimizationsSwitch = new FamilySuppressionSwitch();
			FullscreenOptimizationsSwitch.Name = "gameFullscreenOptimizationsSwitch";
			FullscreenOptimizationsSwitch.TabIndex = 3;
			FullscreenOptimizationsSwitch.Text = Lang.T("lib.fso");
			FullscreenOptimizationsSwitch.Click += delegate
			{
				if (this.FullscreenOptimizationsRequested != null)
				{
					this.FullscreenOptimizationsRequested(this, EventArgs.Empty);
				}
			};
			FullscreenOptimizationsSwitch.Enter += delegate
			{
				if (this.SelectionRequested != null)
				{
					this.SelectionRequested(this, EventArgs.Empty);
				}
			};
			base.Controls.Add(FullscreenOptimizationsSwitch);
			MoreButton = new GameCardMenuButton
			{
				Name = "gameMore",
				TabIndex = 4
			};
			MoreButton.Click += delegate
			{
				if (this.MoreRequested != null)
				{
					this.MoreRequested(this, EventArgs.Empty);
				}
			};
			base.Controls.Add(MoreButton);
			Extension = GameExtension.CreateCard((item != null) ? item.Profile : null);
			if (Extension != null)
			{
				Extension.SetSurface(Surface);
				Extension.Enter += delegate
				{
					if (this.SelectionRequested != null)
					{
						this.SelectionRequested(this, EventArgs.Empty);
					}
				};
				base.Controls.Add(Extension);
			}
			RefreshModel();
		}

		internal int ExtraHeightForWidth(int width)
		{
			if (Extension != null)
			{
				return Extension.PreferredHeightForWidth(Math.Max(1, width - Theme.S(28)));
			}
			return 0;
		}

		private void PushSurface()
		{
			FamilySwitch.Surface = Surface;
			FamilySwitch.Invalidate();
			RebarSwitch.Surface = Surface;
			RebarSwitch.Invalidate();
			FullscreenOptimizationsSwitch.Surface = Surface;
			FullscreenOptimizationsSwitch.Invalidate();
			CoreSwitch.Surface = Surface;
			CoreSwitch.Invalidate();
			CoreButton.Bg = Surface;
			CoreButton.Invalidate();
			MoreButton.Bg = Surface;
			MoreButton.Invalidate();
			if (Extension != null)
			{
				Extension.SetSurface(Surface);
			}
		}

		internal static int OrdinaryOverrideCount(GameProfile profile)
		{
			if (profile != null)
			{
				return profile.Overrides.Count - (profile.Overrides.ContainsKey("GameFamilyBackground") ? 1 : 0);
			}
			return 0;
		}

		public void SetItem(GameLibraryItem item)
		{
			Item = item;
			RefreshModel();
		}

		public void RefreshModel()
		{
			if (Item != null && Item.Profile != null)
			{
				CoreSwitch.Checked = PolicyResolver.For(Item.Profile).AutoCoresAllowed;
				CoreSwitch.Surface = Surface;
				CoreSwitch.AccessibleName = Item.Profile.Name + " · " + Lang.T("schedule.auto.allow");
				CoreSwitch.AccessibleDescription = Lang.T("lib.cores.toggle.tip");
				tips.SetToolTip(CoreSwitch, Lang.T("lib.cores.toggle.tip"));
				FamilySwitch.Checked = Item.Profile.SuppressFamilyBackground;
				RebarSwitch.Checked = PolicyResolver.For(Item.Profile).NvRebar;
				RebarSwitch.Surface = Surface;
				RebarSwitch.AccessibleName = Item.Profile.Name + " · " + Lang.T("lib.rebar");
				RebarSwitch.AccessibleDescription = Lang.T("lib.rebar.tip");
				tips.SetToolTip(RebarSwitch, Lang.T("lib.rebar.tip"));
				FullscreenOptimizationsSwitch.Checked = Item.Profile.DisableFullscreenOptimizations;
				FullscreenOptimizationsSwitch.Surface = Surface;
				FullscreenOptimizationsSwitch.AccessibleName = Item.Profile.Name + " · " + Lang.T("lib.fso");
				FullscreenOptimizationsSwitch.AccessibleDescription = Lang.T("lib.fso.tip");
				tips.SetToolTip(FullscreenOptimizationsSwitch, Lang.T("lib.fso.tip"));
				FamilySwitch.Gated = !Item.RendererObserved;
				FamilySwitch.Surface = Surface;
				string tResult = Lang.T(Item.RendererObserved ? "lib.renderer.observed" : "lib.renderer.pending");
				string tResult2 = Lang.T(Item.RendererObserved ? "lib.renderer.observed.tip" : "lib.renderer.pending.tip");
				string executablePathOffset = Item.Profile.ExecutablePath ?? Item.Profile.Root ?? "";
				base.AccessibleName = Item.Profile.Name;
				base.AccessibleDescription = tResult + " · " + MetadataText + "\r\n" + executablePathOffset + "\r\n" + tResult2;
				CoreButton.Bg = Surface;
				MoreButton.Bg = Surface;
				MoreButton.AccessibleName = Item.Profile.Name + " · " + Lang.T("workflow.more");
				MoreButton.AccessibleDescription = Lang.T("lib.rename") + " · " + Lang.T("v15.library.force") + " · " + Lang.T("btn.remove");
				tips.SetToolTip(MoreButton, Lang.T("workflow.more"));
				CoreButton.AccessibleName = Item.Profile.Name + " · " + Lang.T("nav.corescheduling");
				CoreButton.AccessibleDescription = Lang.T("lib.cores.tip");
				tips.SetToolTip(CoreButton, Lang.T("lib.cores.tip"));
				FamilySwitch.AccessibleName = Item.Profile.Name + " · " + Lang.T("lib.family.suppress");
				FamilySwitch.AccessibleDescription = tResult2 + "\r\n" + Lang.T("lib.family.tip");
				tips.SetToolTip(this, Item.Profile.Name + "\r\n" + executablePathOffset + "\r\n" + MetadataText + "\r\n\r\n" + tResult2);
				tips.SetToolTip(FamilySwitch, ((FamilySwitch.Gated && !FamilySwitch.Checked) ? (Lang.T("lib.family.gated.body") + "\r\n\r\n") : "") + Lang.T("lib.family.tip") + "\r\n\r\n" + tResult2);
				Invalidate();
				FamilySwitch.Invalidate();
			}
		}

		protected override void OnLayout(LayoutEventArgs levent)
		{
			base.OnLayout(levent);
			GameLibraryRowLayout gameLibraryRowLayout = GameLibraryRowLayout.ForSize(base.ClientSize);
			if (FamilySwitch != null)
			{
				FamilySwitch.Bounds = gameLibraryRowLayout.Policy;
			}
			if (RebarSwitch != null)
			{
				RebarSwitch.Bounds = gameLibraryRowLayout.Rebar;
			}
			if (FullscreenOptimizationsSwitch != null)
			{
				FullscreenOptimizationsSwitch.Bounds = gameLibraryRowLayout.FullscreenOptimizations;
			}
			if (CoreSwitch != null)
			{
				CoreSwitch.Bounds = gameLibraryRowLayout.Scheduling;
			}
			if (MoreButton != null)
			{
				MoreButton.Bounds = gameLibraryRowLayout.More;
			}
			if (CoreButton != null)
			{
				CoreButton.SetBounds(Math.Max(0, CoreSwitch.Width - Theme.S(25)), Theme.S(2), Theme.S(24), Math.Max(1, CoreSwitch.Height - Theme.S(4)));
			}
			if (Extension != null)
			{
				int heightForWidthResult = GameLibraryRowLayout.HeightForWidth(base.ClientSize.Width);
				Extension.Bounds = new Rectangle(Theme.S(14), heightForWidthResult, Math.Max(1, base.ClientSize.Width - Theme.S(28)), Math.Max(1, ExtraHeight - Theme.S(9)));
			}
		}

		protected override bool IsInputKey(Keys keyData)
		{
			Keys keys = keyData & Keys.KeyCode;
			if (keys == Keys.Up || keys == Keys.Down || keys == Keys.Home || keys == Keys.End || keys == Keys.Prior || keys == Keys.Next)
			{
				return true;
			}
			return base.IsInputKey(keyData);
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			base.OnKeyDown(e);
		}

		protected override void OnMouseDown(MouseEventArgs e)
		{
			if (e.Button == MouseButtons.Left)
			{
				Focus();
				if (this.SelectionRequested != null)
				{
					this.SelectionRequested(this, EventArgs.Empty);
				}
			}
			base.OnMouseDown(e);
		}

		protected override void OnEnter(EventArgs e)
		{
			base.OnEnter(e);
			if (this.SelectionRequested != null)
			{
				this.SelectionRequested(this, EventArgs.Empty);
			}
			Invalidate();
		}

		protected override void OnLeave(EventArgs e)
		{
			base.OnLeave(e);
			Invalidate();
		}

		protected override void OnMouseEnter(EventArgs e)
		{
			base.OnMouseEnter(e);
			hovered = true;
			PushSurface();
			Invalidate();
		}

		protected override void OnMouseLeave(EventArgs e)
		{
			base.OnMouseLeave(e);
			hovered = false;
			PushSurface();
			Invalidate();
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			if (Item == null || Item.Profile == null || base.Width < 2 || base.Height < 2)
			{
				return;
			}
			Graphics graphics = e.Graphics;
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			Rectangle rectangle = new Rectangle(0, 0, base.Width - 1, base.Height - 1);
			GameLibraryRowLayout gameLibraryRowLayout = GameLibraryRowLayout.ForSize(base.ClientSize);
			using (GraphicsPath path = Theme.TechPath(rectangle, Theme.S(8)))
			{
				using (LinearGradientBrush brush = new LinearGradientBrush(rectangle, Surface, Col.Lerp(Surface, Theme.Inset, 0.16f), LinearGradientMode.Horizontal))
				{
					graphics.FillPath(brush, path);
				}
				using (Pen pen = new Pen(selected ? Col.Alpha(Theme.Accent, 95) : (hovered ? Theme.StrokeHi : Theme.Stroke)))
				{
					graphics.DrawPath(pen, path);
				}
			}
			using (Pen pen2 = new Pen(Col.Alpha(Theme.Accent, selected ? 220 : 80), Math.Max(1, Theme.S(1))))
			{
				graphics.DrawLine(pen2, Theme.S(1), Theme.S(1), Theme.S(selected ? 62 : 32), Theme.S(1));
			}
			if (selected)
			{
				using (SolidBrush brush2 = new SolidBrush(Theme.Accent))
				{
					graphics.FillRectangle(brush2, 0, Theme.S(16), Theme.S(3), Theme.S(26));
				}
			}
			using (GraphicsPath path2 = Theme.TechPath(Rectangle.Inflate(gameLibraryRowLayout.Icon, Theme.S(4), Theme.S(4)), Theme.S(6)))
			{
				using (SolidBrush brush3 = new SolidBrush(Col.Lerp(Theme.Card, Theme.Inset, 0.5f)))
				{
					graphics.FillPath(brush3, path2);
				}
				using (Pen pen3 = new Pen(Theme.Stroke))
				{
					graphics.DrawPath(pen3, path2);
				}
			}
			Bitmap bitmap = null;
			try
			{
				if (IconProvider != null)
				{
					bitmap = IconProvider(Item.Profile.ExecutablePath);
				}
			}
			catch
			{
			}
			if (bitmap == null)
			{
				Glyphs.Draw(graphics, "game", Rectangle.Inflate(gameLibraryRowLayout.Icon, -Theme.S(6), -Theme.S(6)), Theme.Accent);
			}
			else
			{
				try
				{
					graphics.DrawImage(bitmap, gameLibraryRowLayout.Icon);
				}
				catch
				{
				}
			}
			TextRenderer.DrawText(graphics, Item.Profile.Name, Theme.UI(10f, true), gameLibraryRowLayout.Name, Theme.Fg, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			TextRenderer.DrawText(graphics, Item.Profile.ExecutablePath ?? Item.Profile.Root ?? "", Theme.UI(7.5f, false), gameLibraryRowLayout.Path, Theme.Dim, TextFormatFlags.NoPrefix | TextFormatFlags.PathEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			using (GraphicsPath path3 = Theme.TechPath(gameLibraryRowLayout.Running, Theme.S(4)))
			{
				using (SolidBrush brush4 = new SolidBrush(Item.Running ? Col.Lerp(Theme.Inset, Theme.Green, 0.12f) : Theme.Inset))
				{
					graphics.FillPath(brush4, path3);
				}
				using (Pen pen4 = new Pen(Item.Running ? Col.Alpha(Theme.Green, 100) : Theme.Stroke))
				{
					graphics.DrawPath(pen4, path3);
				}
			}
			TextRenderer.DrawText(graphics, Lang.T(Item.Running ? "v15.library.running" : "v15.library.ready"), Theme.UI(7.1f, true), gameLibraryRowLayout.Running, Item.Running ? Theme.Green : Theme.Dim, TextFormatFlags.EndEllipsis | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			string tResult = Lang.T(Item.RendererObserved ? "lib.renderer.observed" : "lib.renderer.pending");
			Color color = (Item.RendererObserved ? Theme.Green : Theme.Dim);
			int width = TextRenderer.MeasureText(tResult, Theme.UI(7.2f, false), Size.Empty, TextFormatFlags.NoPadding).Width;
			int val = Math.Min(gameLibraryRowLayout.Metadata.Width, width + Theme.S(12));
			Rectangle rectangle2 = new Rectangle(gameLibraryRowLayout.Metadata.X, gameLibraryRowLayout.Metadata.Y, Math.Max(1, val), gameLibraryRowLayout.Metadata.Height);
			int width2 = Math.Max(2, Theme.S(3));
			using (SolidBrush brush5 = new SolidBrush(color))
			{
				graphics.FillEllipse(brush5, rectangle2.Left, rectangle2.Top + (rectangle2.Height - width2) / 2, width2, width2);
			}
			TextRenderer.DrawText(graphics, tResult, Theme.UI(7.2f, false), new Rectangle(rectangle2.Left + Theme.S(10), rectangle2.Top, Math.Max(1, rectangle2.Width - Theme.S(10)), rectangle2.Height), color, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			string metadataText = MetadataText;
			int rightOffset = rectangle2.Right + Theme.S(10);
			if (gameLibraryRowLayout.Metadata.Right > rightOffset)
			{
				TextRenderer.DrawText(graphics, metadataText, Theme.UI(7.4f, false), new Rectangle(rightOffset, gameLibraryRowLayout.Metadata.Top, gameLibraryRowLayout.Metadata.Right - rightOffset, gameLibraryRowLayout.Metadata.Height), Theme.Dim, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			}
			if (Focused && ShowFocusCues)
			{
				ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(rectangle, -Theme.S(5), -Theme.S(5)), Theme.Accent, Surface);
			}
		}
	}
}
