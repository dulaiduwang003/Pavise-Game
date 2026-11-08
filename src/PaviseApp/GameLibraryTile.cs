using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed partial class GameLibraryTile : Control
	{
		private sealed class TileAccessibility : ControlAccessibleObject
		{
			private readonly GameLibraryTile owner;

			private readonly AccessibleObject more;

			public override AccessibleStates State
			{
				get
				{
					return base.State | AccessibleStates.Selectable | (owner.Selected ? AccessibleStates.Selected : AccessibleStates.None) | ((owner.ConfigurationAvailable && !owner.ConfigurationModal) ? (owner.ConfigurationExpanded ? AccessibleStates.Expanded : AccessibleStates.Collapsed) : AccessibleStates.None);
				}
			}

			public override string DefaultAction
			{
				get
				{
					if (!owner.ConfigurationModal || !owner.ConfigurationAvailable)
					{
						return PerformancePolicy.Text("选择", "Select");
					}
					return PerformancePolicy.Text("打开深度调优", "Open advanced tuning");
				}
			}

			internal TileAccessibility(GameLibraryTile tile)
				: base(tile)
			{
				owner = tile;
				more = new MoreAccessibility(tile, this);
			}

			public override void DoDefaultAction()
			{
				owner.Focus();
				if (owner.ConfigurationModal && owner.ConfigurationAvailable)
				{
					owner.configurationButton.PerformClick();
				}
				else if (owner.SelectionRequested != null)
				{
					owner.SelectionRequested(owner, EventArgs.Empty);
				}
			}

			public override int GetChildCount()
			{
				return 1 + (owner.configurationAvailable ? 1 : 0) + ((owner.configurationContent != null) ? 1 : 0);
			}

			public override AccessibleObject GetChild(int index)
			{
				switch (index)
				{
				case 0:
					return more;
				case 1:
					if (owner.configurationAvailable)
					{
						return owner.configurationButton.AccessibilityObject;
					}
					break;
				}
				if (index == 2 && owner.configurationContent != null)
				{
					return owner.configurationContent.AccessibilityObject;
				}
				return null;
			}
		}

		private sealed class MoreAccessibility : AccessibleObject
		{
			private readonly GameLibraryTile owner;

			private readonly AccessibleObject parent;

			public override AccessibleObject Parent
			{
				get
				{
					return parent;
				}
			}

			public override string Name
			{
				get
				{
					return owner.moreButton.AccessibleName;
				}
				set
				{
				}
			}

			public override AccessibleRole Role
			{
				get
				{
					return AccessibleRole.PushButton;
				}
			}

			public override string KeyboardShortcut
			{
				get
				{
					return "Shift+F10";
				}
			}

			public override string DefaultAction
			{
				get
				{
					return PerformancePolicy.Text("打开菜单", "Open menu");
				}
			}

			public override Rectangle Bounds
			{
				get
				{
					return owner.moreButton.RectangleToScreen(owner.moreButton.ClientRectangle);
				}
			}

			public override AccessibleStates State
			{
				get
				{
					return (AccessibleStates)((int)(AccessibleStates.Focusable | (owner.moreButton.Focused ? AccessibleStates.Focused : AccessibleStates.None)) | ((!owner.moreButton.CanInteract) ? 1 : 0));
				}
			}

			internal MoreAccessibility(GameLibraryTile tile, AccessibleObject tileAccessibility)
			{
				owner = tile;
				parent = tileAccessibility;
			}

			public override void DoDefaultAction()
			{
				owner.OpenMore();
			}
		}

		private readonly ToolTip tips;

		private readonly GameCardMenuButton moreButton;

		private readonly GameLibraryConfigurationButton configurationButton;

		private Control configurationContent;

		private bool selected;

		private bool hovered;

		private bool configurationAvailable;

		private bool configurationModal;

		private bool measuringConfiguration;

		private int summaryHeight = Theme.S(170);

		public Func<string, Bitmap> IconProvider;

		public Func<GameProfile, Bitmap> CoverProvider;

		public GameLibraryItem Item { get; private set; }

		internal Control MoreButton
		{
			get
			{
				return moreButton;
			}
		}

		internal Control ConfigurationButton
		{
			get
			{
				return configurationButton;
			}
		}

		internal Control ConfigurationContent
		{
			get
			{
				return configurationContent;
			}
		}

		internal bool ConfigurationExpanded
		{
			get
			{
				return configurationContent != null;
			}
		}

		internal bool ConfigurationModal
		{
			get
			{
				return configurationModal;
			}
			set
			{
				if (configurationModal != value)
				{
					configurationModal = value;
					configurationButton.ModalPresentation = value;
					Invalidate();
				}
			}
		}

		internal bool ConfigurationAvailable
		{
			get
			{
				return configurationAvailable;
			}
			set
			{
				if (configurationAvailable != value)
				{
					configurationAvailable = value;
					if (!value)
					{
						SetConfigurationContent(null);
					}
					configurationButton.Visible = value;
					Invalidate();
				}
			}
		}

		internal Rectangle CoverBounds
		{
			get
			{
				return new Rectangle(1, 1, Math.Max(1, base.Width - 2), Math.Max(1, summaryHeight - Theme.S(61)));
			}
		}

		internal Rectangle OriginalIconBounds
		{
			get
			{
				return new Rectangle(Theme.S(11), summaryHeight - Theme.S(29), Theme.S(18), Theme.S(18));
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
					Invalidate();
				}
			}
		}

		private string StateText
		{
			get
			{
				if (Item == null || !Item.Running)
				{
					return "";
				}
				return Lang.T("v15.library.running");
			}
		}

		public event EventHandler SelectionRequested;

		public event EventHandler MoreRequested;

		public event EventHandler ConfigurationRequested;

		public event EventHandler ConfigurationHeightChanged;

		internal GameLibraryTile(GameLibraryItem item, ToolTip toolTip)
		{
			Item = item;
			tips = toolTip;
			BackColor = Theme.Card;
			SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
			base.TabStop = true;
			Cursor = Cursors.Hand;
			base.Margin = Padding.Empty;
			base.AccessibleRole = AccessibleRole.ListItem;
			moreButton = new GameCardMenuButton
			{
				Name = "gameMore",
				TabStop = true,
				Bg = Theme.Inset
			};
			GameCardMenuButton gameCardMenuButton = moreButton;
			MouseEventHandler value = delegate
			{
				if (this.SelectionRequested != null)
				{
					this.SelectionRequested(this, EventArgs.Empty);
				}
			};
			gameCardMenuButton.MouseDown += value;
			moreButton.Enter += delegate
			{
				if (this.SelectionRequested != null)
				{
					this.SelectionRequested(this, EventArgs.Empty);
				}
			};
			moreButton.KeyDown += delegate(object sender, KeyEventArgs e)
			{
				if (!DsKeys.IsActivation(e.KeyCode))
				{
					OnKeyDown(e);
				}
			};
			moreButton.Click += delegate
			{
				if (this.MoreRequested != null)
				{
					this.MoreRequested(this, EventArgs.Empty);
				}
			};
			configurationButton = new GameLibraryConfigurationButton
			{
				Name = "gameConfiguration",
				TabIndex = 1,
				Visible = false
			};
			configurationButton.Click += delegate
			{
				if (this.ConfigurationRequested != null)
				{
					this.ConfigurationRequested(this, EventArgs.Empty);
				}
				QueueAddonNoticeInspection(true);
			};
			configurationButton.Enter += delegate
			{
				if (this.SelectionRequested != null)
				{
					this.SelectionRequested(this, EventArgs.Empty);
				}
			};
			configurationButton.KeyDown += delegate(object sender, KeyEventArgs e)
			{
				if (!DsKeys.IsActivation(e.KeyCode))
				{
					OnKeyDown(e);
				}
			};
			base.Controls.Add(moreButton);
			base.Controls.Add(configurationButton);
			InitializeAddonNotice();
			RefreshModel();
		}

		public void SetItem(GameLibraryItem item)
		{
			Item = item;
			RefreshModel();
		}

		public void RefreshModel()
		{
			RefreshAddonNotice();
			if (Item != null && Item.Profile != null)
			{
				base.AccessibleName = Item.Profile.Name;
				base.AccessibleDescription = GameLibraryGallery.PlatformName(Item.Profile) + ((StateText.Length == 0) ? "" : (" · " + StateText)) + "\r\n" + (Item.Profile.ExecutablePath ?? Item.Profile.Root ?? "");
				moreButton.AccessibleName = Item.Profile.Name + " · " + Lang.T("workflow.more");
				moreButton.Bg = Theme.Inset;
				moreButton.Invalidate();
				configurationButton.AccessibleName = Item.Profile.Name + " · " + PerformancePolicy.Text("深度调优", "Advanced tuning");
				configurationButton.Text = addonNoticeAvailable ? PerformancePolicy.Text("有附加层可清理", "Add-ons available to remove") : PerformancePolicy.Text("深度调优", "Advanced tuning");
				configurationButton.Attention = addonNoticeAvailable;
				string addonTip = addonNoticeAvailable ? PerformancePolicy.Text("发现可删除的英雄联盟附加层。点击进入专属优化；删除前请完全退出英雄联盟和 WeGame。", "League add-ons were found. Open the dedicated settings here; fully exit League of Legends and WeGame before removal.") : "";
				if (addonNoticeAvailable)
				{
					base.AccessibleDescription += "\r\n" + addonTip;
					configurationButton.AccessibleName += " · " + configurationButton.Text;
				}
				tips.SetToolTip(configurationButton, addonTip);
				configurationButton.Expanded = ConfigurationExpanded;
				configurationButton.Bg = Theme.Card;
				configurationButton.Invalidate();
				tips.SetToolTip(this, Item.Profile.Name + "\r\n" + (Item.Profile.ExecutablePath ?? Item.Profile.Root ?? ""));
				tips.SetToolTip(moreButton, Lang.T("workflow.more"));
				Invalidate();
			}
		}

		internal void OpenMore()
		{
			moreButton.PerformClick();
		}

		internal void SetConfigurationContent(Control content)
		{
			if (object.ReferenceEquals(content, configurationContent))
			{
				return;
			}
			if (configurationContent != null)
			{
				Control control = configurationContent;
				configurationContent = null;
				control.SizeChanged -= ConfigurationContentSizeChanged;
				control.Disposed -= ConfigurationContentDisposed;
				base.Controls.Remove(control);
				control.Dispose();
			}
			configurationContent = content;
			if (content != null)
			{
				ScrollableControl scrollableControl = content as ScrollableControl;
				if (scrollableControl != null)
				{
					scrollableControl.AutoScroll = false;
					scrollableControl.AutoScrollMinSize = Size.Empty;
				}
				content.Dock = DockStyle.None;
				content.Margin = Padding.Empty;
				content.TabIndex = 2;
				content.SizeChanged += ConfigurationContentSizeChanged;
				content.Disposed += ConfigurationContentDisposed;
				base.Controls.Add(content);
			}
			configurationButton.Expanded = content != null;
			PerformLayout();
			Invalidate();
		}

		private void ConfigurationContentDisposed(object sender, EventArgs e)
		{
			if (object.ReferenceEquals(sender, configurationContent))
			{
				configurationContent.SizeChanged -= ConfigurationContentSizeChanged;
				configurationContent.Disposed -= ConfigurationContentDisposed;
				configurationContent = null;
				configurationButton.Expanded = false;
				if (this.ConfigurationHeightChanged != null)
				{
					this.ConfigurationHeightChanged(this, EventArgs.Empty);
				}
			}
		}

		private void ConfigurationContentSizeChanged(object sender, EventArgs e)
		{
			if (!measuringConfiguration && !base.IsDisposed && this.ConfigurationHeightChanged != null)
			{
				this.ConfigurationHeightChanged(this, EventArgs.Empty);
			}
		}

		internal int HeightForWidth(int width, int baseHeight)
		{
			summaryHeight = baseHeight;
			int baseHeightOffset = baseHeight + (configurationAvailable ? Theme.S(32) : 0);
			if (configurationContent == null)
			{
				return baseHeightOffset;
			}
			LayoutConfigurationContent(width);
			return baseHeightOffset + Math.Max(1, configurationContent.Height) + Theme.S(22);
		}

		private void LayoutConfigurationContent(int width)
		{
			if (configurationContent == null || measuringConfiguration)
			{
				return;
			}
			measuringConfiguration = true;
			try
			{
				int width2 = Math.Max(1, width - Theme.S(24));
				int top = summaryHeight + Theme.S(32) + Theme.S(10);
				configurationContent.SetBounds(Theme.S(12), top, width2, Math.Max(1, configurationContent.Height));
				if (configurationContent.AutoSize)
				{
					Size preferredSize = configurationContent.GetPreferredSize(new Size(width2, 0));
					if (preferredSize.Height > 0)
					{
						configurationContent.Height = preferredSize.Height;
					}
				}
			}
			finally
			{
				measuringConfiguration = false;
			}
		}

		protected override AccessibleObject CreateAccessibilityInstance()
		{
			return new TileAccessibility(this);
		}

		protected override void OnLayout(LayoutEventArgs levent)
		{
			base.OnLayout(levent);
			if (moreButton != null)
			{
				moreButton.SetBounds(Math.Max(0, base.Width - Theme.S(35)), Theme.S(9), Theme.S(26), Theme.S(23));
			}
			if (configurationButton != null)
			{
				configurationButton.SetBounds(1, summaryHeight, Math.Max(1, base.Width - 2), Theme.S(32));
			}
			LayoutConfigurationContent(base.Width);
		}

		protected override bool IsInputKey(Keys keyData)
		{
			if (GameLibraryGallery.IsNavigationKey(keyData & Keys.KeyCode))
			{
				return true;
			}
			return base.IsInputKey(keyData);
		}

		protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
		{
			Keys keys = keyData & Keys.KeyCode;
			if (configurationContent != null && configurationContent.ContainsFocus)
			{
				return base.ProcessCmdKey(ref msg, keyData);
			}
			if ((keyData & Keys.Modifiers) == 0 && (GameLibraryGallery.IsNavigationKey(keys) || keys == Keys.Delete))
			{
				KeyEventArgs keyEventArgs = new KeyEventArgs(keyData);
				OnKeyDown(keyEventArgs);
				if (keyEventArgs.Handled)
				{
					return true;
				}
			}
			return base.ProcessCmdKey(ref msg, keyData);
		}

		protected override void OnMouseDown(MouseEventArgs e)
		{
			if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
			{
				Focus();
				if (this.SelectionRequested != null)
				{
					this.SelectionRequested(this, EventArgs.Empty);
				}
			}
			base.OnMouseDown(e);
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				DisposeAddonNotice();
			}
			if (disposing && configurationContent != null)
			{
				Control control = configurationContent;
				configurationContent = null;
				control.SizeChanged -= ConfigurationContentSizeChanged;
				control.Disposed -= ConfigurationContentDisposed;
				base.Controls.Remove(control);
				control.Dispose();
			}
			base.Dispose(disposing);
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
			Invalidate();
		}

		protected override void OnMouseLeave(EventArgs e)
		{
			base.OnMouseLeave(e);
			hovered = false;
			Invalidate();
		}

		protected override void OnPaintBackground(PaintEventArgs e)
		{
			if (base.Parent != null)
			{
				using (SolidBrush brush = new SolidBrush(base.Parent.BackColor))
				{
					e.Graphics.FillRectangle(brush, base.ClientRectangle);
					return;
				}
			}
			base.OnPaintBackground(e);
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			if (Item == null || Item.Profile == null || base.Width < 4 || base.Height < 4)
			{
				return;
			}
			Graphics graphics = e.Graphics;
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			Rectangle rectangle = new Rectangle(0, 0, base.Width - 1, base.Height - 1);
			Rectangle coverBounds = CoverBounds;
			Bitmap icon = null;
			Bitmap bitmap = null;
			try
			{
				if (IconProvider != null)
				{
					icon = IconProvider(Item.Profile.ExecutablePath);
				}
			}
			catch
			{
			}
			try
			{
				if (CoverProvider != null)
				{
					bitmap = CoverProvider(Item.Profile);
				}
			}
			catch
			{
			}
			using (GraphicsPath graphicsPath = Theme.TechPath(rectangle, Theme.S(8)))
			{
				GraphicsState gstate = graphics.Save();
				graphics.SetClip(graphicsPath);
				using (LinearGradientBrush brush = new LinearGradientBrush(rectangle, Theme.Card, Theme.Inset, LinearGradientMode.ForwardDiagonal))
				{
					graphics.FillRectangle(brush, rectangle);
				}
				if (bitmap != null)
				{
					DrawCover(graphics, bitmap, coverBounds);
				}
				else
				{
					DrawDefaultCover(graphics, coverBounds, icon);
				}
				DrawFooter(graphics, coverBounds.Bottom, icon);
				if (selected)
				{
					int scaledDimension = Theme.S(34);
					using (SolidBrush brush2 = new SolidBrush(Theme.Accent))
					{
						graphics.FillPolygon(brush2, new Point[3]
						{
							new Point(0, 0),
							new Point(scaledDimension, 0),
							new Point(0, scaledDimension)
						});
					}
					using (Pen pen = new Pen(Theme.OnAccent, Math.Max(1.6f, Theme.S(2))))
					{
						pen.StartCap = LineCap.Round;
						pen.EndCap = LineCap.Round;
						graphics.DrawLines(pen, new Point[3]
						{
							new Point(Theme.S(7), Theme.S(13)),
							new Point(Theme.S(11), Theme.S(17)),
							new Point(Theme.S(19), Theme.S(8))
						});
					}
				}
				graphics.Restore(gstate);
				using (Pen pen2 = new Pen(selected ? Theme.Accent : (hovered ? Theme.StrokeHi : Col.Lerp(Theme.Stroke, Theme.StrokeHi, 0.35f)), selected ? Math.Max(1f, Theme.S(1)) : 1f))
				{
					graphics.DrawPath(pen2, graphicsPath);
				}
			}
			if (selected || hovered)
			{
				using (Pen pen3 = new Pen(Col.Alpha(Theme.Accent, selected ? 230 : 100), Math.Max(1f, Theme.S(1))))
				{
					graphics.DrawLine(pen3, Theme.S(9), 0, Math.Max(Theme.S(10), base.Width - Theme.S(10)), 0);
				}
			}
			if (Focused && ShowFocusCues)
			{
				ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(rectangle, -Theme.S(4), -Theme.S(4)), Theme.Accent, Theme.Card);
			}
		}

		private static void DrawCover(Graphics g, Bitmap cover, Rectangle target)
		{
			if (cover.Width < 1 || cover.Height < 1)
			{
				return;
			}
			float lowerBound = Math.Max((float)target.Width / (float)cover.Width, (float)target.Height / (float)cover.Height);
			float widthRatio = (float)target.Width / lowerBound;
			float heightRatio = (float)target.Height / lowerBound;
			RectangleF rectangleF = new RectangleF(((float)cover.Width - widthRatio) * 0.5f, ((float)cover.Height - heightRatio) * 0.5f, widthRatio, heightRatio);
			InterpolationMode interpolationMode = g.InterpolationMode;
			g.InterpolationMode = InterpolationMode.HighQualityBicubic;
			using (ImageAttributes imageAttributes = new ImageAttributes())
			{
				imageAttributes.SetWrapMode(WrapMode.TileFlipXY);
				g.DrawImage(cover, target, rectangleF.X, rectangleF.Y, rectangleF.Width, rectangleF.Height, GraphicsUnit.Pixel, imageAttributes);
			}
			g.InterpolationMode = interpolationMode;
			using (LinearGradientBrush brush = new LinearGradientBrush(target, Color.Transparent, Color.FromArgb(95, 0, 0, 0), LinearGradientMode.Vertical))
			{
				g.FillRectangle(brush, target);
			}
		}

		private void DrawDefaultCover(Graphics g, Rectangle cover, Bitmap icon)
		{
			Color color = (Theme.LightMode ? Color.FromArgb(26, 31, 38) : Color.FromArgb(11, 14, 18));
			Color color2 = (Theme.LightMode ? Color.FromArgb(42, 49, 59) : Color.FromArgb(23, 28, 35));
			using (LinearGradientBrush brush = new LinearGradientBrush(cover, color2, color, LinearGradientMode.ForwardDiagonal))
			{
				g.FillRectangle(brush, cover);
			}
			GraphicsState gstate = g.Save();
			g.SetClip(cover, CombineMode.Intersect);
			int width = cover.Width;
			int height = cover.Height;
			int left = cover.Left;
			int top = cover.Top;
			using (SolidBrush brush2 = new SolidBrush(Color.FromArgb(55, 0, 0, 0)))
			{
				g.FillPolygon(brush2, new Point[4]
				{
					new Point(left + width / 5, top),
					new Point(left + width / 2, top),
					new Point(left + width, top + height),
					new Point(left + width * 7 / 10, top + height)
				});
				g.FillPolygon(brush2, new Point[4]
				{
					new Point(left - width / 5, top),
					new Point(left + width / 7, top),
					new Point(left + width * 3 / 5, top + height),
					new Point(left + width / 4, top + height)
				});
			}
			int sLimit = Math.Max(4, Theme.S(9));
			using (Pen pen = new Pen(Color.FromArgb(11, 150, 164, 182)))
			{
				for (int i = left; i < cover.Right; i += sLimit)
				{
					g.DrawLine(pen, i, top, i, cover.Bottom);
				}
				for (int j = top; j < cover.Bottom; j += sLimit)
				{
					g.DrawLine(pen, left, j, cover.Right, j);
				}
			}
			using (Pen pen2 = new Pen(Color.FromArgb(155, 3, 5, 8)))
			{
				g.DrawLine(pen2, left + width / 5, top, left + width * 7 / 10, top + height);
				g.DrawLine(pen2, left + width / 2, top, left + width, top + height);
				g.DrawLine(pen2, left - width / 5, top, left + width / 4, top + height);
			}
			using (Pen pen3 = new Pen(Col.Alpha(Theme.Accent, 12), Theme.S(7)))
			{
				using (Pen pen4 = new Pen(Col.Alpha(Theme.Accent, 150), Math.Max(1f, Theme.S(1))))
				{
					Point pt = new Point(left, top + height / 3);
					Point pt2 = new Point(left + width / 3, top + height);
					Point pt3 = new Point(left + width * 3 / 5, top);
					Point pt4 = new Point(left + width, top + height * 4 / 5);
					g.DrawLine(pen3, pt, pt2);
					g.DrawLine(pen3, pt3, pt4);
					g.DrawLine(pen4, pt, pt2);
					g.DrawLine(pen4, pt3, pt4);
				}
			}
			int val = Math.Min(Theme.S(66), Math.Min(width - Theme.S(34), height - Theme.S(26)));
			val = Math.Max(1, val);
			Rectangle rectangle = new Rectangle(left + (width - val) / 2, top + (height - val) / 2 + Theme.S(2), val, val);
			Rectangle rect = Rectangle.Inflate(rectangle, Theme.S(20), Theme.S(15));
			using (GraphicsPath graphicsPath = new GraphicsPath())
			{
				graphicsPath.AddEllipse(rect);
				using (PathGradientBrush pathGradientBrush = new PathGradientBrush(graphicsPath))
				{
					pathGradientBrush.CenterColor = Col.Alpha(Theme.Accent, selected ? 60 : 35);
					pathGradientBrush.SurroundColors = new Color[1] { Color.Transparent };
					g.FillEllipse(pathGradientBrush, rect);
				}
			}
			if (icon == null)
			{
				Rectangle rectangle2 = Rectangle.Inflate(rectangle, Theme.S(4), Theme.S(4));
				using (GraphicsPath path = Theme.TechPath(rectangle2, Theme.S(9)))
				{
					using (LinearGradientBrush brush3 = new LinearGradientBrush(rectangle2, Color.FromArgb(30, 35, 43), Color.FromArgb(9, 12, 16), LinearGradientMode.ForwardDiagonal))
					{
						g.FillPath(brush3, path);
					}
					using (Pen pen5 = new Pen(Color.FromArgb(100, 111, 127)))
					{
						g.DrawPath(pen5, path);
					}
				}
				Glyphs.Draw(g, "gamepad", Rectangle.Inflate(rectangle, -Theme.S(10), -Theme.S(10)), Color.FromArgb(187, 195, 208));
			}
			else
			{
				DrawIcon(g, icon, rectangle);
			}
			g.Restore(gstate);
		}

		private void DrawFooter(Graphics g, int top, Bitmap icon)
		{
			using (Pen pen = new Pen(Col.Alpha(Theme.StrokeHi, 130)))
			{
				g.DrawLine(pen, 1, top, base.Width - 2, top);
			}
			TextRenderer.DrawText(g, Item.Profile.Name, Theme.UI(9.7f, true), new Rectangle(Theme.S(11), top + Theme.S(5), Math.Max(1, base.Width - Theme.S(22)), Theme.S(22)), Theme.Fg, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			Rectangle originalIconBounds = OriginalIconBounds;
			if (icon == null)
			{
				Glyphs.Draw(g, "gamepad", originalIconBounds, Theme.Dim);
			}
			else
			{
				DrawIcon(g, icon, originalIconBounds);
			}
			int maxLimit = (Item.Running ? Math.Min(Theme.S(71), Math.Max(Theme.S(45), base.Width / 3)) : 0);
			Rectangle rectangle = new Rectangle(Math.Max(0, base.Width - Theme.S(9) - maxLimit), originalIconBounds.Top - Theme.S(1), maxLimit, originalIconBounds.Height + Theme.S(2));
			if (Item.Running)
			{
				int width = Math.Max(3, Theme.S(5));
				using (SolidBrush brush = new SolidBrush(Theme.Green))
				{
					g.FillEllipse(brush, rectangle.Left, rectangle.Top + (rectangle.Height - width) / 2, width, width);
				}
				TextRenderer.DrawText(g, StateText, Theme.UI(7.4f, false), new Rectangle(rectangle.Left + Theme.S(9), rectangle.Top, Math.Max(1, rectangle.Width - Theme.S(9)), rectangle.Height), Theme.Green, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			}
			int rightOffset = originalIconBounds.Right + Theme.S(7);
			TextRenderer.DrawText(g, GameLibraryGallery.PlatformName(Item.Profile), Theme.UI(7.4f, false), new Rectangle(rightOffset, originalIconBounds.Top - Theme.S(1), Math.Max(1, rectangle.Left - Theme.S(7) - rightOffset), rectangle.Height), Theme.Dim, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
		}

		private static void DrawIcon(Graphics g, Bitmap icon, Rectangle target)
		{
			if (icon != null && icon.Width >= 1 && icon.Height >= 1)
			{
				float upperBound = Math.Min((float)target.Width / (float)icon.Width, (float)target.Height / (float)icon.Height);
				int lowerBound = Math.Max(1, (int)((float)icon.Width * upperBound));
				int lowerBound2 = Math.Max(1, (int)((float)icon.Height * upperBound));
				Rectangle rect = new Rectangle(target.Left + (target.Width - lowerBound) / 2, target.Top + (target.Height - lowerBound2) / 2, lowerBound, lowerBound2);
				InterpolationMode interpolationMode = g.InterpolationMode;
				g.InterpolationMode = InterpolationMode.HighQualityBicubic;
				try
				{
					g.DrawImage(icon, rect);
				}
				catch (ArgumentException)
				{
				}
				g.InterpolationMode = interpolationMode;
			}
		}
	}
}
