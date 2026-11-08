using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PaviseApp
{
	internal partial class PanelForm : Form
	{
		private sealed class AcViewState
		{
			internal string Drawer;

			internal bool BodyScroll;

			internal float InnerY;

			internal float OuterY;

			internal float BodyOrigin;
		}

		private sealed class AcConsoleSurface : DBPanel
		{
			internal int SplitX;

			internal Rectangle UtilityBounds;

			internal AcConsoleSurface()
			{
				BackColor = Theme.Bg;
			}

			protected override void OnPaintBackground(PaintEventArgs e)
			{
				Graphics graphics = e.Graphics;
				graphics.Clear(Theme.Bg);
				if (base.Width < 2 || base.Height < 2)
				{
					return;
				}
				graphics.SmoothingMode = SmoothingMode.AntiAlias;
				DsPaint.TechFrame(graphics, new Rectangle(0, 0, base.Width - 1, base.Height - 1), Theme.S(14), Theme.Card, Theme.StrokeHi);
				using (Pen pen = new Pen(Col.Alpha(Theme.Accent, 205), Math.Max(1, Theme.S(1))))
				{
					graphics.DrawLines(pen, new Point[4]
					{
						new Point(0, Theme.S(28)),
						new Point(0, Theme.S(14)),
						new Point(Theme.S(14), 0),
						new Point(Theme.S(28), 0)
					});
					graphics.DrawLines(pen, new Point[4]
					{
						new Point(base.Width - Theme.S(28), base.Height - 1),
						new Point(base.Width - Theme.S(14), base.Height - 1),
						new Point(base.Width - 1, base.Height - Theme.S(14)),
						new Point(base.Width - 1, base.Height - Theme.S(28))
					});
				}
				using (Pen pen2 = new Pen(Col.Lerp(Theme.Card, Theme.Stroke, 0.62f)))
				{
					if (SplitX > 0)
					{
						graphics.DrawLine(pen2, SplitX, Theme.S(12), SplitX, base.Height - Theme.S(12));
					}
					if (!UtilityBounds.IsEmpty)
					{
						graphics.DrawLine(pen2, UtilityBounds.Left, UtilityBounds.Top, UtilityBounds.Right, UtilityBounds.Top);
					}
				}
			}
		}

		private sealed class AcModuleSurface : DBPanel
		{
			internal AcModuleSurface() { Tag = "backdrop-card"; }
			protected override void OnPaintBackground(PaintEventArgs e)
			{
				DsPaint.CardBackground(e.Graphics, this, Theme.Card);
				using (Pen pen = new Pen(Col.Lerp(Theme.Card, Theme.Stroke, 0.55f)))
				{
					foreach (Control control in base.Controls)
					{
						if (control.Bottom < base.Height)
						{
							e.Graphics.DrawLine(pen, Theme.S(8), control.Bottom, base.Width - Theme.S(8), control.Bottom);
						}
					}
				}
			}
		}

		private sealed class AcGlyph : Control
		{
			internal string Key;

			internal AcGlyph()
			{
				SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
				BackColor = Theme.Card;
				base.TabStop = false;
			}

			protected override void OnPaint(PaintEventArgs e)
			{
				DsPaint.FillBackground(e.Graphics, this, Theme.Card);
				AntiCheatFeatureIcons.Draw(e.Graphics, base.ClientRectangle, Key, Theme.Accent);
			}
		}

		private sealed class AcDrawerButton : PillButton
		{
			private readonly string label;

			private readonly string icon;

			private int count;

			private int labelWidth;

			private int badgeWidth;

			private float labelScale;

			private string countText = "0";

			internal int Count
			{
				get
				{
					return count;
				}
				set
				{
					if (count != value)
					{
						count = value;
						countText = count.ToString();
						badgeWidth = 0;
						base.AccessibleName = label + " · " + count;
						Invalidate();
					}
				}
			}

			internal AcDrawerButton(string title, string glyph)
				: base(title)
			{
				label = title;
				icon = glyph;
				Bg = Theme.Card;
				base.AccessibleName = title + " · 0";
			}

			protected override AccessibleObject CreateAccessibilityInstance()
			{
				return new AcButtonAccessibility(this);
			}

			protected override void OnPaint(PaintEventArgs e)
			{
				Graphics graphics = e.Graphics;
				graphics.Clear(Theme.Card);
				graphics.SmoothingMode = SmoothingMode.AntiAlias;
				Color fill = Col.Lerp(Theme.Card, Theme.CardHover, hover.Value);
				DsPaint.TechFrame(graphics, new Rectangle(0, 0, base.Width - 1, base.Height - 1), Theme.S(8), fill, Focused ? Theme.Accent : Theme.Stroke);
				AntiCheatFeatureIcons.Draw(graphics, new Rectangle(Theme.S(16), (base.Height - Theme.S(20)) / 2, Theme.S(20), Theme.S(20)), icon, Theme.Dim);
				if (labelScale != Dpi.Scale || badgeWidth == 0)
				{
					labelScale = Dpi.Scale;
					labelWidth = TextRenderer.MeasureText(label, Theme.UI(9f, false), Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width;
					badgeWidth = Math.Max(Theme.S(28), TextRenderer.MeasureText(countText, Theme.UI(8f, false), Size.Empty, TextFormatFlags.NoPadding).Width + Theme.S(16));
				}
				int num = Math.Max(1, Math.Min(labelWidth, base.Width - Theme.S(92) - badgeWidth));
				TextRenderer.DrawText(graphics, label, Theme.UI(9f, false), new Rectangle(Theme.S(48), 0, num, base.Height), Theme.Fg, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
				Rectangle rectangle = new Rectangle(Theme.S(48) + num + Theme.S(9), (base.Height - Theme.S(22)) / 2, badgeWidth, Theme.S(22));
				DsPaint.TechFrame(graphics, rectangle, Theme.S(5), Theme.Inset, Theme.Stroke);
				TextRenderer.DrawText(graphics, countText, Theme.UI(8f, false), rectangle, Theme.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
				TextRenderer.DrawText(graphics, "›", Theme.UI(16f, false), new Rectangle(Math.Max(0, base.Width - Theme.S(30)), 0, Theme.S(20), base.Height), Theme.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
				if (base.WantsFocusCue)
				{
					DsFocus.Draw(graphics, Rectangle.Inflate(base.ClientRectangle, -Theme.S(3), -Theme.S(3)), Theme.Accent, Theme.Card);
				}
			}
		}

		private sealed class AcReadOnlyButton : PillButton
		{
			private readonly string title;

			private readonly string tag;

			private readonly bool record;

			internal AcReadOnlyButton(string name, string state, bool recordOnly)
				: base(name)
			{
				title = name;
				tag = state;
				record = recordOnly;
				Bg = Theme.Card;
			}

			protected override AccessibleObject CreateAccessibilityInstance()
			{
				return new AcButtonAccessibility(this);
			}

			protected override void OnPaint(PaintEventArgs e)
			{
				Graphics graphics = e.Graphics;
				graphics.Clear(Theme.Card);
				DsPaint.TechFrame(graphics, new Rectangle(0, 0, base.Width - 1, base.Height - 1), Theme.S(6), Col.Lerp(Theme.Card, Theme.CardHover, hover.Value), Focused ? Theme.Accent : Theme.Stroke);
				AntiCheatFeatureIcons.Draw(graphics, new Rectangle(Theme.S(12), (base.Height - Theme.S(20)) / 2, Theme.S(20), Theme.S(20)), record ? "records" : "master", Theme.Dim);
				int num = ((tag.Length != 0) ? Math.Min(Theme.S(94), base.Width / 3) : 0);
				TextRenderer.DrawText(graphics, title, Theme.UI(9f, true), new Rectangle(Theme.S(42), 0, Math.Max(1, base.Width - Theme.S(54) - num), base.Height), Theme.Fg, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
				if (num > 0)
				{
					TextRenderer.DrawText(graphics, tag, Theme.UI(8f, false), new Rectangle(base.Width - num - Theme.S(10), 0, num, base.Height), Theme.Dim, TextFormatFlags.NoPrefix | TextFormatFlags.Right | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
				}
				if (base.WantsFocusCue)
				{
					DsFocus.Draw(graphics, Rectangle.Inflate(base.ClientRectangle, -Theme.S(3), -Theme.S(3)), Theme.Accent, Theme.Card);
				}
			}
		}

		private sealed class AcButtonAccessibility : ControlAccessibleObject
		{
			private readonly PillButton button;

			public override string DefaultAction
			{
				get
				{
					return AutoText("打开", "Open");
				}
			}

			public override AccessibleStates State
			{
				get
				{
					return base.State | (button.Busy ? AccessibleStates.Busy : AccessibleStates.None);
				}
			}

			internal AcButtonAccessibility(PillButton owner)
				: base(owner)
			{
				button = owner;
			}

			public override void DoDefaultAction()
			{
				button.PerformClick();
			}
		}

		private sealed class AuditFix
		{
			public Func<bool> CanFix;

			public Func<bool> CanRevert;

			public Action Fix;

			public Action Revert;

			public string ConfirmKey;

			public string FixLabel;

			public Action Open;

			public Func<string> OpenText;
		}

		private sealed class CoreEditorDraftSnapshot
		{
			internal string Profile;

			internal string Expected;

			internal string Topology;

			internal string Error;

			internal ulong Mask;

			internal bool Isolation;
		}

		private sealed class CoreQuickChoice
		{
			internal ulong Mask;

			internal int Threads;
		}

		private sealed class CoreViewState
		{
			internal float MatrixY;

			internal float RailY;
		}

		private sealed class CoreRailGlyph : Control
		{
			private readonly string key;

			internal CoreRailGlyph(string glyph)
			{
				key = glyph;
				BackColor = Theme.Card;
				base.TabStop = false;
				SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
			}

			protected override void OnPaint(PaintEventArgs e)
			{
				DsPaint.FillBackground(e.Graphics, this, Theme.Card);
				if (key == "chip")
				{
					Glyphs.Draw(e.Graphics, key, base.ClientRectangle, Theme.Accent);
				}
				else
				{
					PerformanceFeatureIcons.Draw(e.Graphics, base.ClientRectangle, "GmThreadBoostStrategyV1", Theme.Accent);
				}
			}
		}

		private sealed class CoreThreadSurface : RoundPanel
		{
			internal CoreThreadSurface()
			{
				UseBackdrop = true;
				BackColor = Theme.Card;
			}

			protected override void OnPaintBackground(PaintEventArgs e)
			{
				DsPaint.CardBackground(e.Graphics, this, Theme.Card);
			}

			protected override void OnPaint(PaintEventArgs e)
			{
			}
		}

		internal sealed class EnvironmentPreferenceObservation
		{
			internal bool Readable;

			internal bool On;

			internal bool Owned;

			internal bool Supported;
		}

		internal sealed class EnvironmentGraphicsObservation
		{
			internal bool HagsKnown;

			internal bool HagsSupported;

			internal bool HagsOn;

			internal int? HagsConfigured;

			internal bool AmdAvailable;

			internal bool AmdReadable;

			internal bool AmdSupported;

			internal bool AmdOn;

			internal bool NvVrrApplicable;

			internal bool NvVrrOwned;

			internal EnvironmentPreferenceObservation Windowed;

			internal EnvironmentPreferenceObservation Vrr;

			internal bool IntelEnduranceSupported;

			internal bool CacheWarmOn;

			internal bool TimerTickOn;

			internal bool GlobalTimerOn;

			internal bool GlobalTimerOwned;

			internal EnvironmentTuningObservation Tuning;
		}

		internal sealed class EnvironmentTuningObservation
		{
			internal bool BundleOn;

			internal bool StorageOn;

			internal bool QuantumOn;

			internal bool CpuMitigationsOn;

			internal bool CpuMitigationsExpanded;

			internal bool LegacyQuantum;

			internal int QuantumSelection;

			internal string BundleState = "";

			internal string BundleLabel = "";

			internal string BundleDetail = "";

			internal string StorageState = "";

			internal string StorageLabel = "";

			internal string StorageDetail = "";

			internal string QuantumState = "";

			internal string CpuMitigationsState = "";
		}

		private sealed class EnvironmentObservationCycle
		{
			internal int Busy;

			internal int Version;

			internal int SettingsVersion = -1;

			internal DateTime StartedUtc = DateTime.MinValue;

			internal EnvironmentGraphicsObservation Snapshot;
		}

		internal sealed class HagsUiPresentation
		{
			internal bool Checked;

			internal bool Editable;

			internal bool External;

			internal string Status;

			internal Color Ink;
		}

		private sealed class LibrarySearchSurface : RoundPanel
		{
			protected override void OnPaint(PaintEventArgs e)
			{
				base.OnPaint(e);
				e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
				using (Pen pen = new Pen(Theme.Dim, Math.Max(1.2f, Theme.S(1))))
				{
					int num = Theme.S(14);
					int num2 = Theme.S(10);
					int num3 = Theme.S(12);
					e.Graphics.DrawEllipse(pen, num, num2, num3, num3);
					e.Graphics.DrawLine(pen, num + num3 - Theme.S(1), num2 + num3 - Theme.S(1), num + num3 + Theme.S(5), num2 + num3 + Theme.S(5));
				}
			}
		}

		private sealed class LibraryGameConfiguration : DBPanel
		{
			private readonly PanelForm owner;

			private readonly FamilySuppressionSwitch core;

			private readonly FamilySuppressionSwitch family;

			private readonly FamilySuppressionSwitch rebar;

			private readonly FamilySuppressionSwitch fullscreen;

			private readonly GameCardDetailsButton coreDetails;

			private readonly Label extensionTitle;

			private readonly ToolTip tips = new ToolTip();

			private GameLibraryItem item;

			private GameCardExtension extension;

			private bool arranging;

			private string extensionKey;

			internal LibraryGameConfiguration(PanelForm form, GameLibraryItem initial)
			{
				owner = form;
				base.Name = "LibraryCardConfiguration";
				BackColor = Theme.Card;
				AutoScroll = false;
				core = MakeOption("libraryCoreToggle", Lang.T("nav.corescheduling"), true, form.ToggleGameCoreScheduling);
				family = MakeOption("libraryFamilyToggle", Lang.T("lib.family.suppress"), false, form.ToggleGameFamilySuppression);
				rebar = MakeOption("libraryRebarToggle", Lang.T("lib.rebar"), false, form.ToggleGameRebar);
				fullscreen = MakeOption("libraryFullscreenToggle", Lang.T("lib.fso"), false, form.ToggleGameFullscreenOptimizations);
				core.IconKey = "core";
				core.DetailsWidth = 62;
				family.IconKey = "family";
				rebar.IconKey = "rebar";
				fullscreen.IconKey = "fullscreen";
				coreDetails = new GameCardDetailsButton
				{
					Name = "libraryCoreSettings",
					ShowAdjustLabel = true,
					AccessibleName = Lang.T("nav.corescheduling")
				};
				GameCardDetailsButton gameCardDetailsButton = coreDetails;
				EventHandler value = delegate
				{
					if (item != null)
					{
						owner.OpenLibraryCoreScheduling(item.Profile.Id);
					}
				};
				gameCardDetailsButton.Click += value;
				core.Controls.Add(coreDetails);
				extensionTitle = new Label
				{
					Name = "libraryExtensionTitle",
					Text = AutoText("游戏专属", "Game options"),
					Font = Theme.UI(9f, true),
					ForeColor = Theme.Dim,
					BackColor = Theme.Card,
					TextAlign = ContentAlignment.MiddleLeft,
					AutoSize = false,
					TabStop = false,
					UseMnemonic = false
				};
				base.Controls.Add(extensionTitle);
				owner.BuildCriticalThreadEditor(this, form.ContentW);
				owner.criticalThreadEditor.SizeChanged += delegate
				{
					Arrange();
				};
				RefreshItem(initial);
			}

			private FamilySuppressionSwitch MakeOption(string name, string text, bool isCore, Action<GameLibraryItem> action)
			{
				FamilySuppressionSwitch familySuppressionSwitch = new FamilySuppressionSwitch(isCore);
				familySuppressionSwitch.Name = name;
				familySuppressionSwitch.Text = text;
				familySuppressionSwitch.Surface = Theme.Card;
				FamilySuppressionSwitch familySuppressionSwitch2 = familySuppressionSwitch;
				familySuppressionSwitch2.Click += delegate
				{
					if (item != null)
					{
						action(item);
					}
				};
				base.Controls.Add(familySuppressionSwitch2);
				return familySuppressionSwitch2;
			}

			internal void RefreshItem(GameLibraryItem fresh)
			{
				item = fresh;
				if (item == null || item.Profile == null)
				{
					return;
				}
				GameProfile profile = item.Profile;
				owner.SyncCriticalThreadEditor(profile);
				core.Checked = PolicyResolver.For(profile).AutoCoresAllowed;
				family.Checked = profile.SuppressFamilyBackground;
				family.Gated = !item.RendererObserved;
				rebar.Checked = PolicyResolver.For(profile).NvRebar;
				fullscreen.Checked = profile.DisableFullscreenOptimizations;
				family.Enabled = !owner.familyChangeBusy;
				rebar.Enabled = !owner.rebarChangeBusy;
				fullscreen.Enabled = !owner.fullscreenOptimizationsChangeBusy;
				FamilySuppressionSwitch[] array = new FamilySuppressionSwitch[4] { core, family, rebar, fullscreen };
				string[] array2 = new string[4]
				{
					"lib.cores.toggle.tip",
					item.RendererObserved ? "lib.family.tip" : "lib.family.gated.body",
					"lib.rebar.tip",
					"lib.fso.tip"
				};
				for (int i = 0; i < array.Length; i++)
				{
					array[i].AccessibleName = profile.Name + " · " + array[i].Text;
					array[i].AccessibleDescription = Lang.T(array2[i]);
					tips.SetToolTip(array[i], array[i].AccessibleDescription);
				}
				string text = profile.Id + "|" + profile.Root + "|" + profile.ExecutablePath + "|" + profile.LearnedExecutablePath;
				if (extensionKey != text)
				{
					if (extension != null)
					{
						extension.Dispose();
					}
					extension = null;
					extensionKey = text;
					extension = GameExtension.CreateCard(profile);
					if (extension != null)
					{
						extension.Name = "LibraryGameExtension";
						extension.SetSurface(Theme.Card);
						base.Controls.Add(extension);
					}
				}
				Arrange();
				Invalidate(true);
			}

			protected override void OnSizeChanged(EventArgs e)
			{
				base.OnSizeChanged(e);
				Arrange();
			}

			private void Arrange()
			{
				if (arranging || core == null || coreDetails == null)
				{
					return;
				}
				arranging = true;
				try
				{
					int num = Math.Max(1, base.Width);
					FamilySuppressionSwitch[] array = new FamilySuppressionSwitch[4] { core, family, rebar, fullscreen };
					for (int i = 0; i < array.Length; i++)
					{
						array[i].SetBounds(0, Theme.S(4 + i * 48), num, Theme.S(40));
					}
					coreDetails.SetBounds(Math.Max(0, core.Width - Theme.S(65)), Theme.S(2), Theme.S(64), Theme.S(36));
					owner.criticalThreadEditor.SetBounds(0, fullscreen.Bottom + Theme.S(8), num, owner.criticalThreadEditor.Height);
					int num2 = Dpi.U(owner.criticalThreadEditor.Bottom) + 8;
					extensionTitle.Visible = extension != null;
					if (extension != null)
					{
						extensionTitle.SetBounds(Theme.S(4), Theme.S(num2 + 8), Math.Max(1, num - Theme.S(8)), Theme.S(24));
						int px = extension.PreferredHeightForWidth(num);
						extension.SetBounds(0, Theme.S(num2 + 40), num, px);
						extension.Visible = true;
						num2 += 44 + Dpi.U(px);
					}
					base.Height = Theme.S(num2);
				}
				finally
				{
					arranging = false;
				}
			}

			protected override void Dispose(bool disposing)
			{
				if (disposing)
				{
					tips.Dispose();
				}
				base.Dispose(disposing);
			}
		}

		private sealed class WhitelistItem
		{
			public readonly WhitelistRuleView View;

			public string Title;

			public string Subtitle;

			public string Badge;

			public string StateText;

			public string Glyph;

			public string Details;

			public bool Automatic;

			public WhitelistItem(WhitelistRuleView view)
			{
				View = view;
			}

			public override string ToString()
			{
				if (View != null)
				{
					return View.Rule.Value;
				}
				return Title ?? "";
			}
		}

		private sealed class PageHook
		{
			public readonly DBPanel Panel;

			public readonly Action<bool> OnActiveChanged;

			public readonly Action OnTick;

			public PageHook(DBPanel panel, Action<bool> onActiveChanged, Action onTick)
			{
				Panel = panel;
				OnActiveChanged = onActiveChanged;
				OnTick = onTick;
			}
		}

		[StructLayout(LayoutKind.Sequential, Pack = 4)]
		private struct PowerBroadcastSetting
		{
			internal Guid PowerSetting;

			internal uint DataLength;
		}

		private sealed class UiPalette
		{
			private readonly Color[] colors = new Color[27]
			{
				Theme.Bg,
				Theme.Nav,
				Theme.Card,
				Theme.CardHover,
				Theme.Inset,
				Theme.Stroke,
				Theme.StrokeHi,
				Theme.Fg,
				Theme.Dim,
				Theme.Faint,
				Theme.Green,
				Theme.Danger,
				Theme.Warning,
				Theme.TrackOff,
				Theme.Accent,
				Theme.Accent2,
				Theme.Sel,
				Theme.DimTint,
				Theme.OnAccent,
				Theme.ModeColor(PerformancePreset.Standard),
				Theme.ModeColor(PerformancePreset.Competitive),
				Theme.ModeColor(PerformancePreset.Extreme),
				Theme.ModeColor(PerformancePreset.Handheld),
				Theme.ModeColor2(PerformancePreset.Standard),
				Theme.ModeColor2(PerformancePreset.Competitive),
				Theme.ModeColor2(PerformancePreset.Extreme),
				Theme.ModeColor2(PerformancePreset.Handheld)
			};

			internal Color Remap(Color source, UiPalette next)
			{
				if (source.IsEmpty || source.A == 0)
				{
					return source;
				}
				int num = source.ToArgb() & 0xFFFFFF;
				for (int i = 0; i < colors.Length; i++)
				{
					if ((colors[i].ToArgb() & 0xFFFFFF) == num)
					{
						return Color.FromArgb(source.A, next.colors[i].R, next.colors[i].G, next.colors[i].B);
					}
				}
				return source;
			}
		}

		private const int AuditWindowMs = 120000;

		private const int SystemPerformanceTab = 3;

		private const int CollapsedCardH = 52;

		private const int ContentX = 30;

		private const string LastAdvancedPageKey = "DeepTuningLastPage";

		private const int WmPowerBroadcast = 536;

		private const int PbtPowerSettingChange = 32787;

		private const uint DeviceNotifyWindowHandle = 0u;

		internal const int DescMaxLines = 3;

		internal const int CollapseChevronW = 20;

		internal const float StatusFontMax = 14.5f;

		internal const float StatusFontMin = 9.5f;

		private const string AutoHideKey = "AutoHideOnGame";

		private const bool AutoHideDefault = true;

		private const int AutoHideDelayMs = 10000;

		private const int OutroMs = 220;

		private Label lblAboutUpdate;

		private AboutHeroPanel aboutHeroPanel;

		private RoundPanel aboutIdentityPanel;

		private RoundPanel aboutReleasePanel;

		private Label[] aboutHeroLabels;

		private Control[][] aboutMetricControls;

		private bool aboutArranging;

		private Size aboutViewport;

		private int aboutContentTop;

		private DBPanel acViewport;

		private DBPanel acList;

		private DBPanel acControlBay;

		private DBPanel acUtility;

		private DBPanel acDrawer;

		private DBPanel acDrawerBody;

		private AcConsoleSurface acRosterBar;

		private Label lblAcRoster;

		private Label acTitleLabel;

		private Label acStrengthLabel;

		private Label acDrawerTitle;

		private AntiCheatStrengthPicker acModePicker;

		private PillButton acHelp;

		private PillButton acClear;

		private PillButton acOtherToggle;

		private PillButton acRosterEntry;

		private PillButton acDrawerClose;

		private PillButton acStrengthHelp;

		private AcGlyph acTitleIcon;

		private bool acOtherOpen;

		private bool acArranging;

		private float acLayoutScale = 1f;

		private string acDrawerKind = "";

		private string[] acPresentedRoster = new string[0];

		private readonly List<AcGroup> acGroups = new List<AcGroup>();

		private readonly List<SettingCard> acCards = new List<SettingCard>();

		private readonly List<Toggle> acToggles = new List<Toggle>();

		private readonly List<Control> acReadOnlyCards = new List<Control>();

		private readonly List<Control> acRosterRows = new List<Control>();

		private bool auditArranging;

		private DBPanel pageAudit;

		private DBPanel auditScroll;

		private ScanView auditScan;

		private PillButton btnAuditStart;

		private Label lblAuditStatus;

		private PillButton btnAuditRun;

		private PillButton btnAuditFixAll;

		private PillButton btnAuditCancel;

		private int auditBusy;

		private int auditRunVersion;

		private bool auditRendered;

		private volatile bool auditCancelRequested;

		private AuditReport auditReport;

		private GpuIrqResult auditIrqFix;

		private bool autoIgnoreDialogOpen;

		private TechTabs performanceTabs;

		private PerformanceCategoryBar performanceCategories;

		private DBPanel[] performanceTabPanels;

		private int performanceViewportTop;

		private readonly List<Action> automationSync = new List<Action>();

		private bool automationUpdating;

		private DBPanel coreEditor;

		private CoreMatrix coreEditMatrix;

		private Toggle coreIsolationToggle;

		private Toggle coreEnableToggle;

		private Label coreEditorTitle;

		private Label coreEditorHelp;

		private Label coreEditorIsolation;

		private Label coreEditorResult;

		private Label coreEditorRuntime;

		private PlanComparison corePlanPreview;

		private PillButton coreEvidenceToggle;

		private string coreDraftProfile;

		private string coreDraftExpected;

		private string coreDraftTopology;

		private string coreSaveError;

		private CoreEditorDraftSnapshot coreRebuildDraft;

		private string coreProfileId;

		private AdvancedBackBar coreBackBar;

		private DBPanel coreScrollPanel;

		private CoreMatrix coreMatrix;

		private RoundPanel coreStatusPanel;

		private RoundPanel coreLivePanel;

		private RoundPanel coreMapPanel;

		private Label lblCoreState;

		private Label lblCoreReason;

		private Label lblCoreRule;

		private Label lblCorePlan;

		private Label lblCoreReserved;

		private Label lblCoreTelemetry;

		private PillButton btnCoreClearReserved;

		private Panel coreQuickPanel;

		private RoundPanel criticalThreadEditor;

		private Label criticalThreadTitle;

		private Label criticalThreadHelp;

		private Label criticalThreadCompanion;

		private Label criticalThreadResult;

		private Label criticalThreadRuntime;

		private FamilySuppressionSwitch criticalThreadEnabled;

		private PerformanceInfoButton criticalThreadInfo;

		private bool criticalThreadIssue;

		private string criticalThreadProfile;

		private string criticalThreadExpected;

		private string criticalThreadError;

		private ulong criticalThreadAll;

		private ulong criticalThreadAllowed;

		private ulong criticalThreadReserved;

		private ulong[] criticalThreadPhysical = new ulong[0];

		private bool criticalThreadTopologyTrusted;

		private bool criticalThreadSyncing;

		private string criticalThreadAllowedError;

		private CriticalThreadUiState criticalThreadView;

		private CoreIdentityStrip coreIdentity;

		private DBPanel coreMatrixViewport;

		private DBPanel coreRightRail;

		private Label coreSelectedCount;

		private Label coreTopologyCount;

		private Label coreIsolationHint;

		private Label coreEnableLabel;

		private PerformanceInfoButton coreRangeInfo;

		private PerformanceInfoButton coreIsolationInfo;

		private PerformanceInfoButton coreEnableInfo;

		private Control coreIsolationGlyph;

		private bool coreWorkspaceArranging;

		private bool coreWorkspaceSyncing;

		private bool coreWorkspaceCompact;

		private string coreWorkspaceLayoutKey;

		private int coreMatrixLayoutWidth;

		private string coreCpuLine;

		private float coreLayoutScale = 1f;

		private DataOperationProgressCard dataOperationPanel;

		private EnvironmentObservationCycle environmentObservation = new EnvironmentObservationCycle();

		private readonly ProcessActionFailureBudget environmentObservationFailures = new ProcessActionFailureBudget();
		private readonly ProcessActionFailureBudget runningObservationFailures = new ProcessActionFailureBudget();
		private int environmentConfigurationGeneration = -1;
		private string environmentConfigurationSignature;

		private readonly Dictionary<string, bool> environmentPreferenceOverrides = new Dictionary<string, bool>();

		private bool? environmentBundleOverride;

		private bool? environmentStorageOverride;

		private int? environmentQuantumOverride;

		private Toggle swHags;

		private Toggle swAmdSam;

		private SettingCard cardAmdSam;

		private SettingCard cardHags;

		private Toggle swWindowedOpt;

		private Toggle swVrrOpt;

		private Toggle swEnvNvVrr;

		private SettingCard cardWindowedOpt;

		private SettingCard cardVrrOpt;

		private Toggle swGameLowLatency;

		private SettingCard cardGameLowLatency;

		private readonly Dictionary<string, Toggle> environmentPerformanceSwitches = new Dictionary<string, Toggle>();

		private readonly Dictionary<string, SettingCard> environmentPerformanceCards = new Dictionary<string, SettingCard>();

		private PrivacyOptionsPanel environmentPrivacy;

		private readonly List<PrivacyOptionsPanel> performancePrivacyPanels = new List<PrivacyOptionsPanel>();

		private Toggle swEnvironmentGameDvr;

		private SettingCard cardEnvironmentGameDvr;

		private Toggle swPerformanceEnvironmentBundle;

		private SettingCard cardPerformanceEnvironmentBundle;

		private QuantumProfilePicker swPerformanceEnvironmentQuantum;

		private SettingCard cardPerformanceEnvironmentQuantum;

		private Toggle swPerformanceEnvironmentStorage;

		private SettingCard cardPerformanceEnvironmentStorage;

		private DBPanel pageIrq;

		private GameLibraryGallery lstGames;

		private FamilySuppressionSwitch swAutoAdd;

		private Label lblLibraryHint;

		private Label lblLibraryCount;

		private bool familyChangeBusy;

		private bool rebarChangeBusy;

		private bool fullscreenOptimizationsChangeBusy;

		private EmptyStatePanel gameListPanel;

		private readonly Dictionary<string, Bitmap> gameIconCache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

		private int runningBusy;

		private int runningPending;

		private int runningVersion;

		private long nextRunningProbeTicks;

		private static readonly long RunningProbeIntervalTicks = TimeSpan.FromSeconds(5.0).Ticks;

		private RoundPanel librarySearchPanel;

		private TextBox librarySearch;

		private TierPicker libraryPlatforms;

		private PillButton libraryAdd;

		private SettingsButton libraryAutoSettings;

		private string librarySelectedId;

		private string librarySearchText = "";

		private int libraryPlatformIndex;

		private bool libraryArranging;

		private Size libraryViewport;

		private GameConfigurationDialog libraryConfigurationDialog;

		private string libraryConfigurationCoreTarget;

		private LogStreamView logStream;

		private RoundPanel logWrap;

		private Label lblLogStreamHint;

		private Toggle swLogWrites;

		private bool logDiagnostics;

		private PillButton logActionsButton;

		private PillButton logDiagnosticsButton;

		private PillButton logExportButton;

		private long logSeenVersion = -1L;

		private LogStreamView logSeenStream;

		private OverviewMemoryCleanupControl overviewMemoryCleanup;

		private ManualMemoryReport manualMemoryReport;

		private int manualMemoryPending;

		private volatile bool manualMemoryDisposed;

		private bool manualMemoryDisposeRegistered;

		private int manualMemoryProgressNumber = 1;

		private ManualMemoryStep manualMemoryProgressStep;

		private OverviewSpecItem[] overviewSpecs;

		private OverviewUsageItem[] overviewUsage;

		private OverviewHardwareInfo overviewHardware;

		private OverviewResourceSnapshot overviewResources;

		private readonly object overviewReaderGate = new object();

		private OverviewTelemetryReader overviewReader;

		private volatile int overviewHudGeneration;

		private int overviewTelemetryPending;

		private int overviewReaderGeneration = -1;

		private volatile bool overviewTelemetryActive;

		private bool overviewDisposeRegistered;

		private volatile bool overviewTelemetryDisposed;

		private long overviewNextTelemetry;

		private Toggle swGame;

		private PaviseCore paviseCore;

		private StatusDot statusDot;

		private Label lblStatus;

		private Label lblOverviewBoost;

		private Label lblHeroMode;

		private Label lblHeroSource;

		private LinkLabel btnUpdateHint;

		private DBPanel updateBadge;

		private UpdateResult latestUpdate;

		private RogLinkButton btnDonate;

		private Label lblOverviewGuard;

		private Panel overviewStatusDivider;

		private bool overviewArranging;

		private Size overviewViewport;

		private string overviewAction;

		private long overviewLogVersion = -1L;

		private int overviewErrors;

		private volatile bool donateLoading;

		private DonateDialog donateDialog;

		private readonly List<Action> policySync = new List<Action>();

		private readonly Dictionary<Control, Dictionary<Control, int>> stackBase = new Dictionary<Control, Dictionary<Control, int>>();

		private Toggle swAuto;

		private Toggle swAutoHide;

		private SettingCard cardShader;

		private PillButton btnShaderGo;

		private PillButton btnBackdrop;

		private PillButton btnBackdropReset;

		private TierPicker pickBackdropDim;

		private BackdropPreview backdropPreview;

		private Label lblBackdropState;

		private readonly Label[] modeAccentLabels = new Label[7];

		private static readonly Color[] AccentPalette = new Color[10]
		{
			Color.FromArgb(239, 190, 66),
			Color.FromArgb(255, 140, 40),
			Color.FromArgb(255, 61, 82),
			Color.FromArgb(240, 90, 170),
			Color.FromArgb(178, 118, 255),
			Color.FromArgb(48, 180, 255),
			Color.FromArgb(40, 200, 200),
			Color.FromArgb(69, 224, 154),
			Color.FromArgb(150, 214, 80),
			Color.FromArgb(120, 140, 255)
		};

		private readonly List<ColorSwatch>[] modeSwatches = new List<ColorSwatch>[7]
		{
			new List<ColorSwatch>(),
			new List<ColorSwatch>(),
			new List<ColorSwatch>(),
			new List<ColorSwatch>(),
			new List<ColorSwatch>(),
			new List<ColorSwatch>(),
			new List<ColorSwatch>()
		};

		private static int shaderCleaning;

		private static int shaderCleanupVersion;

		private int slowBusy;

		private int slowVersion;

		private int slowPending;

		private int wipeBusy;

		private bool runtimeShutdownBusy;

		private PillButton btnWipeAll;

		private PillButton btnUninstall;

		private TechTabs settingsTabs;

		private DBPanel[] settingsTabPanels;

		private DBPanel settingsWorkspaceViewport;

		private SettingsWorkspaceSurface settingsWorkspaceSurface;

		private readonly List<SettingsWorkspaceTab> settingsWorkspaceTabs = new List<SettingsWorkspaceTab>();

		private bool arrangingSettingsWorkspace;

		private Toggle swPolicyStandbyCleaner;

		private SettingCard cardPolicyStandbyCleaner;

		private ListBox lstWhite;

		private EmptyStatePanel whitePanel;

		private Label lblWhiteHint;

		private WhitelistSearchSurface whiteSearchPanel;

		private TextBox whiteSearch;

		private TierPicker whiteFilters;

		private Label whiteCount;

		private PillButton whitePick;

		private PillButton whiteBrowse;

		private PillButton whiteReset;

		private List<WhitelistRuleView> whiteViews = new List<WhitelistRuleView>();

		private List<WhitelistItem> whiteRows = new List<WhitelistItem>();

		private string whiteSearchText = "";

		private int whiteFilterIndex;

		private bool whiteArranging;

		private ContextMenuStrip whiteMenu;

		private PillButton btnRestorePlatformProtection;

		private PillButton btnRemoveWhitelist;

		private readonly Dictionary<string, Bitmap> whiteIconCache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

		private readonly Dictionary<string, string> whiteNameCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		private int whiteBusy;

		private int whiteVersion;

		private int whitePending;

		private int whiteOpBusy;

		private bool shotMode;

		private readonly Tamer tamer;

		private readonly GameMode gameMode;

		private readonly bool elevated;

		private DBPanel pageOverview;

		private DBPanel pagePolicy;

		private DBPanel pageAntiCheat;

		private DBPanel pageLibrary;

		private DBPanel pageLog;

		private DBPanel pageSettings;

		private DBPanel pageAbout;

		private PictureBox aboutIcon;

		private DBPanel pageGraphics;

		private DBPanel pageEnvironment;

		private DBPanel pageWhitelist;

		private DBPanel pageCoreScheduling;

		private DBPanel[] pages;

		private NavRail nav;

		private AdvancedBackBar advBackBar;

		private ModeButton modeButton;

		private ThemeSwitch themeSwitch;

		private TechSelect powerButton;

		private StrategyFlyout modeFlyout;

		private readonly List<string> powerPlanChoices = new List<string>();

		private PerformancePreset visualMode;

		private bool visualEnabled;

		private bool visualActive;

		private string visualPolicySource;

		private bool modeVisualInitialized;

		private Label lblSub;

		private int builtLang;

		private System.Windows.Forms.Timer uiTimer;

		private volatile bool uiActive;

		private bool uiActivityKnown;

		private bool formFrameAttached;

		private DBPanel curPage;

		private int pageBaseLeft;

		private Icon appIcon;

		public bool RealExit;

		public Action ExitApp;

		public Action ResetApp;

		public Action UninstallApp;

		private DBPanel root;

		private System.Windows.Forms.Timer fitTimer;

		private bool fitting;

		private bool moveSizeLoop;

		private bool fitDeferredByDrag;

		private PageHook[] pageHooks;

		private RogLinkButton headerWebsite;

		private NavRail tuningNav;

		private AdvancedBackBar overviewBackBar;

		private int lastAdvancedPage = 2;

		private int mainReturnPage;

		private float navigationScale = 1f;

		private readonly Dictionary<int, PageViewPosition> pagePositions = new Dictionary<int, PageViewPosition>();

		private readonly Dictionary<DBPanel, Point> tabScrollPositions = new Dictionary<DBPanel, Point>();

		private PageReveal pageReveal;

		private static readonly Guid ActivePowerSchemeGuid = new Guid("31F9F286-5084-42FE-B720-2B0264993763");

		private IntPtr powerSchemeNotification;

		private readonly List<Label> accentLabels = new List<Label>();

		private readonly Dictionary<Control, DBPanel[]> pageTabPanels = new Dictionary<Control, DBPanel[]>();

		private Motion introMotion;

		private bool introActive;

		private bool introPending;

		private System.Windows.Forms.Timer autoHideTimer;

		private bool autoHideArmed;

		private bool lastGameActive;

		private System.Windows.Forms.Timer outroTimer;

		private readonly Stopwatch outroWatch = new Stopwatch();

		private bool outroActive;

		private int AuditContentWidth
		{
			get
			{
				return Math.Max(1, (int)Math.Floor((float)(auditScroll.Width - SystemInformation.VerticalScrollBarWidth - Theme.S(16)) / Dpi.Scale));
			}
		}

		private EnvironmentTuningObservation CachedEnvironmentTuning
		{
			get
			{
				if (environmentObservation.Snapshot != null)
				{
					return environmentObservation.Snapshot.Tuning;
				}
				return null;
			}
		}

		private Form LibraryDialogOwner
		{
			get
			{
				if (libraryConfigurationDialog == null || libraryConfigurationDialog.IsDisposed || !libraryConfigurationDialog.Visible)
				{
					return this;
				}
				return libraryConfigurationDialog;
			}
		}

		private static bool ShaderCleanupBusy
		{
			get
			{
				return Volatile.Read(ref shaderCleaning) != 0;
			}
		}

		private int BottomNavigationReserve
		{
			get
			{
				int num = Theme.S(WinW);
				int num2 = ((nav == null) ? Theme.S(76) : nav.MeasureFloatingDock(Math.Max(1, num - Theme.S(32))).Height);
				return num2 + Theme.S(24);
			}
		}

		private int WinW
		{
			get
			{
				return 1220;
			}
		}

		private int WinH
		{
			get
			{
				return 760;
			}
		}

		private int RailW
		{
			get
			{
				return 0;
			}
		}

		private int TopH
		{
			get
			{
				return 60;
			}
		}

		private int PageW
		{
			get
			{
				return WinW - RailW;
			}
		}

		private int PageH
		{
			get
			{
				return Math.Max(1, WinH - TopH - Dpi.U(BottomNavigationReserve));
			}
		}

		private int ContentW
		{
			get
			{
				return PageW - 60;
			}
		}

		private int ScrollContentW
		{
			get
			{
				return PageW - 40 - 12 - 20;
			}
		}

		protected override CreateParams CreateParams
		{
			get
			{
				CreateParams createParams = base.CreateParams;
				createParams.ExStyle |= 16;
				return createParams;
			}
		}

		private bool IsInDeepTuning
		{
			get
			{
				if (pages != null && curPage != null)
				{
					return IsAdvancedPage(Array.IndexOf(pages, curPage));
				}
				return false;
			}
		}

		protected override bool ShowWithoutActivation
		{
			get
			{
				return shotMode;
			}
		}

		internal bool UiActive
		{
			get
			{
				return uiActive;
			}
		}

		private void BuildAboutPage()
		{
			int num = (aboutContentTop = PageHeader(pageAbout, Lang.T("nav.about"), Lang.T("site.about.originalSub"), 1));
			aboutViewport = Size.Empty;
			pageAbout.AutoScroll = false;
			Native.Dark(pageAbout);
			AboutHeroPanel aboutHeroPanel = (this.aboutHeroPanel = new AboutHeroPanel());
			aboutHeroPanel.SetBounds(Theme.S(30), Theme.S(num), Theme.S(ContentW), Theme.S(130));
			pageAbout.Controls.Add(aboutHeroPanel);
			PictureBox pictureBox = new PictureBox();
			pictureBox.SetBounds(Theme.S(24), Theme.S(23), Theme.S(84), Theme.S(84));
			pictureBox.BackColor = Color.Transparent;
			pictureBox.SizeMode = PictureBoxSizeMode.Zoom;
			aboutIcon = pictureBox;
			OwnedImage(pictureBox, IconArt.Render(Theme.S(108), Theme.CurrentMode, true));
			aboutHeroLabels = new Label[4]
			{
				CardLabel(aboutHeroPanel, Lang.T("v20.about.system"), 132, 12, 360, 20, 7.1f, true, Theme.Faint),
				CardLabel(aboutHeroPanel, "PAVISE AUTO", 130, 31, 330, 40, 24f, true, Theme.Fg),
				AccentLabel(aboutHeroPanel, "AUTO GUARD  //  " + App.VersionTag, 132, 71, 360, 22, 8f, true),
				CardLabel(aboutHeroPanel, Lang.T("about.desc").Replace("\r\n", " "), 132, 96, 390, 27, 9f, false, Theme.Dim)
			};
			aboutHeroPanel.Controls.Add(pictureBox);
			aboutMetricControls = new Control[3][]
			{
				AddAboutMetric(aboutHeroPanel, Lang.T("v20.about.build"), App.VersionTag, 582, 27, 116, false),
				AddAboutMetric(aboutHeroPanel, Lang.T("v20.about.platform"), "WINDOWS x64", 706, 27, 124, false),
				AddAboutMetric(aboutHeroPanel, Lang.T("v20.about.state"), Lang.T("v20.about.ready"), 838, 27, 108, true)
			};
			int num2 = num + 130 + 12;
			int num3 = 454;
			int num4 = 14;
			int num5 = ContentW - num3 - num4;
			int num6 = 366;
			RoundPanel roundPanel = (aboutIdentityPanel = MakeConsolePanel(pageAbout, 30, num2, num3, num6, false));
			Label label = CardLabel(roundPanel, "PROJECT // IDENTITY", 20, 15, num3 - 126, 20, 7.6f, true, Theme.Faint);
			label.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			Label label2 = CardLabel(roundPanel, "NODE 01", num3 - 88, 15, 68, 18, 6.5f, false, Theme.Faint);
			label2.TextAlign = ContentAlignment.MiddleRight;
			label2.Anchor = AnchorStyles.Top | AnchorStyles.Right;
			string[] array = new string[4] { "about.author", "about.douyin", "about.repo", "about.lic" };
			string[] array2 = new string[4]
			{
				"bdth 2074055628@qq.com",
				"44601770838",
				App.RepoUrl.Replace("https://", ""),
				Lang.T("about.lic.value")
			};
			for (int i = 0; i < 4; i++)
			{
				int num7 = 47 + i * 44;
				Label label3 = AddAboutRow(roundPanel, Lang.T(array[i]).ToUpperInvariant(), array2[i], num7, num3, i == 2 || i == 3);
				if (i == 2 || i == 3)
				{
					string targetUrl = ((i == 2) ? App.RepoUrl : App.RepoUrl + "/blob/dev/LICENSE");
					label3.Cursor = Cursors.Hand;
					label3.Click += delegate
					{
						OpenExternal(targetUrl);
					};
				}
			}
			Label freeLabel = CardLabel(roundPanel, Lang.T("about.free"), 20, 231, num3 - 40, 22, 9.5f, true, Theme.Green);
			freeLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			Label refundHint = CardLabel(roundPanel, Lang.T("about.refund.hint"), 20, 258, num3 - 40, 44, 7.8f, false, Theme.Fg);
			refundHint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			Label label4 = CardLabel(roundPanel, Lang.T("about.contact.hint"), 20, 312, num3 - 40, 40, 7.8f, false, Theme.Dim);
			label4.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			RoundPanel roundPanel2 = (aboutReleasePanel = MakeConsolePanel(pageAbout, 30 + num3 + num4, num2, num5, num6, true));
			Label label5 = CardLabel(roundPanel2, "RELEASE // " + Lang.T("v20.about.versionstatus").ToUpperInvariant(), 20, 15, num5 - 126, 20, 7.6f, true, Theme.Faint);
			label5.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			StatusDot statusDot = new StatusDot();
			statusDot.SetBounds(Theme.S(num5 - 126), Theme.S(12), Theme.S(22), Theme.S(22));
			statusDot.Bg = Theme.Card;
			statusDot.Color = Theme.Green;
			statusDot.Anchor = AnchorStyles.Top | AnchorStyles.Right;
			roundPanel2.Controls.Add(statusDot);
			Label label6 = CardLabel(roundPanel2, Lang.T("v20.about.online"), num5 - 102, 12, 82, 24, 6.7f, true, Theme.Green);
			label6.TextAlign = ContentAlignment.MiddleRight;
			label6.Anchor = AnchorStyles.Top | AnchorStyles.Right;
			AccentLabel(roundPanel2, App.VersionTag, 20, 45, 254, 43, 23f, true);
			Label label7 = CardLabel(roundPanel2, Lang.T("v15.about.identity"), 22, 87, num5 - 44, 22, 8.2f, false, Theme.Dim);
			label7.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			Panel panel = new Panel();
			panel.BackColor = Theme.StrokeHi;
			panel.SetBounds(Theme.S(20), Theme.S(119), Theme.S(num5 - 40), 1);
			panel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			roundPanel2.Controls.Add(panel);
			CardLabel(roundPanel2, Lang.T("site.about.source"), 20, 132, num5 - 40, 18, 7f, true, Theme.Faint);
			RoundPanel roundPanel3 = MakeConsolePanel(roundPanel2, 20, 153, num5 - 40, 54, false);
			roundPanel3.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			CardLabel(roundPanel3, "PAVISE // OFFICIAL WEBSITE", 14, 6, num5 - 68, 18, 7.3f, true, Theme.Faint).Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			AccentLabel(roundPanel3, "pavise.club", 14, 25, num5 - 68, 25, 10f, true).Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			RoundPanel roundPanel4 = MakeConsolePanel(roundPanel2, 20, 218, num5 - 40, 60, false);
			roundPanel4.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			StatusDot statusDot2 = new StatusDot();
			statusDot2.SetBounds(Theme.S(14), Theme.S(18), Theme.S(22), Theme.S(22));
			statusDot2.Bg = Theme.Card;
			statusDot2.FollowAccent = true;
			roundPanel4.Controls.Add(statusDot2);
			CardLabel(roundPanel4, Lang.T("v20.about.privacy"), 44, 8, num5 - 116, 19, 7f, true, Theme.Faint).Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			CardLabel(roundPanel4, Lang.T("v20.about.privacy.value"), 44, 27, num5 - 116, 25, 9.4f, true, Theme.Fg).Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			lblAboutUpdate = CardLabel(roundPanel2, "", 20, num6 - 76, num5 - 40, 18, 7.7f, false, Theme.Faint);
			lblAboutUpdate.Name = "aboutUpdateStatus";
			lblAboutUpdate.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
			int num8 = (num5 - 52) / 2;
			PillButton pillButton = new PillButton(Lang.T("notes.open"), BtnKind.Primary);
			pillButton.Name = "aboutReleaseNotes";
			pillButton.Bg = Theme.Card;
			PillButton pillButton2 = pillButton;
			pillButton2.SetBounds(Theme.S(20), Theme.S(num6 - 50), Theme.S(num8), Theme.S(36));
			pillButton2.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
			pillButton2.Click += delegate
			{
				ShowReleaseNotes();
			};
			roundPanel2.Controls.Add(pillButton2);
			PillButton pillButton3 = new PillButton(PerformancePolicy.Text("检查与更新", "Check for updates"));
			pillButton3.Name = "aboutWebsiteDownload";
			pillButton3.Bg = Theme.Card;
			PillButton pillButton4 = pillButton3;
			pillButton4.SetBounds(Theme.S(32 + num8), Theme.S(num6 - 50), Theme.S(num8), Theme.S(36));
			pillButton4.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
			pillButton4.Click += delegate
			{
				OpenSoftwareUpdate();
			};
			roundPanel2.Controls.Add(pillButton4);
			RefreshAboutUpdate();
			pageAbout.SizeChanged += delegate
			{
				aboutViewport = Size.Empty;
				LayoutAboutViewport();
			};
			LayoutAboutViewport();
		}

		private void LayoutAboutViewport()
		{
			if (aboutArranging || pageAbout == null || aboutHeroPanel == null || aboutIdentityPanel == null || aboutReleasePanel == null || aboutHeroPanel.IsDisposed || aboutViewport == pageAbout.ClientSize)
			{
				return;
			}
			aboutArranging = true;
			pageAbout.SuspendLayout();
			try
			{
				for (int i = 0; i < 3; i++)
				{
					int num = Math.Max(240, Dpi.U(pageAbout.ClientSize.Width));
					int num2 = Math.Max(1, Dpi.U(pageAbout.ClientSize.Height));
					int num3 = ((num < 500) ? 16 : 24);
					int num4 = Math.Max(1, num - num3 * 2);
					bool flag = num < 900;
					bool flag2 = num4 < 430;
					int num5 = ((!flag) ? 130 : (flag2 ? 328 : 210));
					int num6 = aboutContentTop + num5 + 12;
					int num7 = (flag ? 366 : Math.Max(366, num2 - num6 - 16));
					int num8 = 14;
					int num9 = (flag ? num4 : ((num4 - num8) / 2));
					int v = (flag ? num4 : (num4 - num9 - num8));
					int num10 = (flag ? (num6 + num7 + 12) : num6);
					int v2 = num10 + num7 + 16;
					bool flag3 = Theme.S(v2) > pageAbout.ClientSize.Height;
					pageAbout.AutoScrollMargin = Size.Empty;
					pageAbout.AutoScrollMinSize = (flag3 ? new Size(0, Theme.S(v2)) : Size.Empty);
					pageAbout.AutoScroll = flag3;
					if (!flag3)
					{
						pageAbout.AutoScrollPosition = Point.Empty;
					}
					int num11 = pageAbout.AutoScrollPosition.Y;
					aboutHeroPanel.SetBounds(Theme.S(num3), Theme.S(aboutContentTop) + num11, Theme.S(num4), Theme.S(num5));
					aboutIdentityPanel.SetBounds(Theme.S(num3), Theme.S(num6) + num11, Theme.S(num9), Theme.S(num7));
					aboutReleasePanel.SetBounds(Theme.S(flag ? num3 : (num3 + num9 + num8)), Theme.S(num10) + num11, Theme.S(v), Theme.S(num7));
					int v3 = ((num4 < 430) ? 64 : 84);
					int num12 = ((num4 < 430) ? 100 : 132);
					aboutIcon.SetBounds(Theme.S(24), Theme.S(23), Theme.S(v3), Theme.S(v3));
					int num13 = ((!flag) ? Math.Max(132, Dpi.U(TextRenderer.MeasureText("WINDOWS x64", Theme.UI(9.2f, true), Size.Empty, TextFormatFlags.NoPadding).Width) + 4) : (flag2 ? (num4 - 40) : ((num4 - 56) / 3)));
					int num14 = ((!flag) ? (num4 - 20 - (num13 * 3 + 24)) : 20);
					int val = ((!flag) ? (num14 - num12 - 16) : (num4 - num12 - 20));
					int[] array = new int[4] { 12, 31, 71, 96 };
					int[] array2 = new int[4] { 20, 40, 22, 29 };
					for (int j = 0; j < aboutHeroLabels.Length; j++)
					{
						aboutHeroLabels[j].SetBounds(Theme.S(num12), Theme.S(array[j]), Theme.S(Math.Max(1, val)), Theme.S(array2[j]));
					}
					for (int k = 0; k < aboutMetricControls.Length; k++)
					{
						int v4 = (flag ? (flag2 ? 20 : (20 + k * (num13 + 8))) : (num14 + k * (num13 + 12)));
						int num15 = (flag ? (flag2 ? (140 + k * 60) : 140) : 27);
						Control[] array3 = aboutMetricControls[k];
						array3[0].SetBounds(Theme.S(v4), Theme.S(num15), Theme.S(num13), Theme.S(18));
						array3[1].SetBounds(Theme.S(v4), Theme.S(num15 + 22), Theme.S(num13), Theme.S(27));
						array3[2].SetBounds(Theme.S(v4), Theme.S(num15 + 52), Theme.S(Math.Max(1, num13 - 14)), Theme.S(1));
					}
					LayoutAboutActions(aboutReleasePanel, "aboutReleaseNotes", "aboutWebsiteDownload");
					if (Dpi.U(pageAbout.ClientSize.Width) == num)
					{
						break;
					}
				}
				aboutViewport = pageAbout.ClientSize;
			}
			finally
			{
				pageAbout.ResumeLayout(true);
				aboutArranging = false;
			}
		}

		private void LayoutAboutActions(Control card, string firstName, string secondName)
		{
			Control control = card.Controls[firstName];
			Control control2 = card.Controls[secondName];
			if (control != null && control2 != null)
			{
				int num = Math.Max(1, card.ClientSize.Width - Theme.S(52));
				int num2 = num / 2;
				int num3 = Math.Max(0, card.ClientSize.Height - Theme.S(50));
				control.SetBounds(Theme.S(20), num3, num2, Theme.S(36));
				control2.SetBounds(Theme.S(32) + num2, num3, num - num2, Theme.S(36));
			}
		}

		private void ShowReleaseNotes()
		{
			if (releaseNotesShowing || base.IsDisposed || base.Disposing) return;
			ReleaseNote current = ReleaseNotes.Current;
			if (current != null)
			{
				releaseNotesShowing = true;
				try
				{
					StringBuilder stringBuilder = new StringBuilder();
					stringBuilder.Append(Lang.F("notes.sub", current.Tag)).Append("  |  ").Append(current.Date)
						.AppendLine()
						.AppendLine();
					for (int i = 0; i < current.Count; i++)
					{
						stringBuilder.Append(i + 1).Append(". ").Append(current.Item(i))
							.AppendLine()
							.AppendLine();
					}
					PaviseDialog.Show(this, Lang.T("notes.title") + "  " + current.Tag, stringBuilder.ToString().TrimEnd(), null, 640);
					// A failed acknowledgement retries next launch, not every tray activation.
					releaseNotesAutoHandled = true;
					if (!ReleaseNotes.MarkSeen()) Logger.Warn("Release notes shown; version acknowledgement could not be saved");
				}
				finally { releaseNotesShowing = false; }
			}
		}

		private void RefreshAboutUpdate()
		{
			if (lblAboutUpdate != null && !lblAboutUpdate.IsDisposed)
			{
				bool flag = latestUpdate != null && UpdateChecker.IsNewer(latestUpdate.Latest, App.Version);
				lblAboutUpdate.Text = (flag ? Lang.F("site.about.found", latestUpdate.Latest) : ((latestUpdate != null) ? Lang.T("site.about.latest") : Lang.T("site.about.wait")));
				lblAboutUpdate.ForeColor = (flag ? Theme.Green : Theme.Faint);
			}
		}

		private Control[] AddAboutMetric(Control parent, string title, string value, int x, int y, int width, bool good)
		{
			Label label = CardLabel(parent, title.ToUpperInvariant(), x, y, width, 18, 6.7f, true, Theme.Faint);
			Label label2 = CardLabel(parent, value, x, y + 24, width, 27, 9.2f, true, good ? Theme.Green : Theme.Fg);
			label2.TextAlign = ContentAlignment.MiddleLeft;
			AccentLine accentLine = new AccentLine();
			accentLine.SetBounds(Theme.S(x), Theme.S(y + 60), Theme.S(width - 14), Theme.S(1));
			parent.Controls.Add(accentLine);
			return new Control[3] { label, label2, accentLine };
		}

		private Label AddAboutRow(Control parent, string title, string value, int y, int width, bool accent)
		{
			CardLabel(parent, title, 20, y, 112, 20, 7.2f, true, Theme.Faint);
			Label label = (accent ? AccentLabel(parent, value, 136, y - 2, width - 156, 25, 8.8f, true) : CardLabel(parent, value, 136, y - 2, width - 156, 25, 8.8f, false, Theme.Fg));
			label.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			Panel panel = new Panel();
			panel.BackColor = Theme.Stroke;
			panel.SetBounds(Theme.S(20), Theme.S(y + 31), Theme.S(width - 40), 1);
			panel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			parent.Controls.Add(panel);
			return label;
		}

		internal void RefreshAboutIcon()
		{
			if (aboutIcon == null || aboutIcon.IsDisposed)
			{
				return;
			}
			Image image = aboutIcon.Image;
			aboutIcon.Image = IconArt.Render(Theme.S(108), Theme.CurrentMode, true);
			if (image == null)
			{
				return;
			}
			try
			{
				image.Dispose();
			}
			catch
			{
			}
		}

		private static void OwnedImage(PictureBox pb, Image img)
		{
			pb.Image = img;
			pb.Disposed += delegate
			{
				try
				{
					if (pb.Image != null)
					{
						pb.Image.Dispose();
					}
				}
				catch
				{
				}
			};
		}

		private void BuildAntiCheatPage()
		{
			acArranging = false;
			acOtherOpen = false;
			acDrawerKind = "";
			acPresentedRoster = new string[0];
			foreach (Control acRosterRow in acRosterRows)
			{
				acRosterRow.Dispose();
			}
			acRosterRows.Clear();
			pageAntiCheat.AutoScroll = false;
			acViewport = new DBPanel
			{
				Name = "AntiCheatWorkspace",
				BackColor = Theme.Bg,
				AutoScroll = true
			};
			Native.Dark(acViewport);
			pageAntiCheat.Controls.Add(acViewport);
			acRosterBar = new AcConsoleSurface
			{
				Name = "AntiCheatConsole"
			};
			acViewport.Controls.Add(acRosterBar);
			acControlBay = new DBPanel
			{
				Name = "AntiCheatControlBay",
				BackColor = Theme.Card
			};
			acRosterBar.Controls.Add(acControlBay);
			acTitleIcon = new AcGlyph
			{
				Key = "master"
			};
			acControlBay.Controls.Add(acTitleIcon);
			acTitleLabel = CardLabel(acControlBay, AutoText("专项压制", "Suppression"), 0, 0, 120, 28, 11f, true, Theme.Fg);
			acTitleLabel.Name = "acTitleLabel";
			acHelp = new PerformanceInfoButton
			{
				Name = "acInfo",
				AccessibleName = AutoText("专项压制 · 说明", "Suppression · Info"),
				Bg = Theme.Card
			};
			acHelp.Click += delegate
			{
				ShowAcHelp();
			};
			acControlBay.Controls.Add(acHelp);
			acStrengthLabel = CardLabel(acControlBay, Lang.T("ac.mode"), 0, 0, 90, 22, 9f, false, Theme.Dim);
			acStrengthLabel.Name = "acStrengthLabel";
			acModePicker = new AntiCheatStrengthPicker
			{
				Name = "acStrength",
				Labels = new string[3]
				{
					Lang.T("ac.mode.gentle"),
					Lang.T("ac.mode.balanced"),
					Lang.T("ac.mode.isolated")
				},
				Index = (int)tamer.Mode,
				AccessibleName = Lang.T("ac.mode"),
				Bg = Theme.Card
			};
			acModePicker.IndexChanged = delegate(int index)
			{
				tamer.Mode = (AntiCheatMode)index;
				RefreshAcGroupStates();
			};
			acControlBay.Controls.Add(acModePicker);
			acStrengthHelp = new PerformanceActionButton(AutoText("档位说明  ⓘ", "Mode details  ⓘ"))
			{
				Name = "AntiCheatStrengthInfo",
				Quiet = true,
				Bg = Theme.Card
			};
			acStrengthHelp.Click += delegate
			{
				ShowAcHelp();
			};
			acControlBay.Controls.Add(acStrengthHelp);
			acList = new AcModuleSurface
			{
				Name = "AntiCheatModules",
				BackColor = Theme.Card
			};
			acRosterBar.Controls.Add(acList);
			acUtility = new DBPanel
			{
				Name = "AntiCheatUtilities",
				BackColor = Theme.Card
			};
			acRosterBar.Controls.Add(acUtility);
			acRosterEntry = new AcDrawerButton(AutoText("兼容名单", "Compatibility list"), "roster")
			{
				Name = "AntiCheatRoster"
			};
			acRosterEntry.Click += delegate
			{
				OpenAcDrawer("roster");
			};
			acUtility.Controls.Add(acRosterEntry);
			acOtherToggle = new AcDrawerButton(AutoText("兼容与记录", "Compatibility & records"), "records")
			{
				Name = "AntiCheatOtherGroups"
			};
			acOtherToggle.Click += delegate
			{
				if (acDrawerKind == "records")
				{
					CloseAcDrawer();
				}
				else
				{
					OpenAcDrawer("records");
				}
			};
			acUtility.Controls.Add(acOtherToggle);
			lblAcRoster = new Label
			{
				Name = "AntiCheatRosterCount",
				AutoSize = false,
				BackColor = Theme.Card,
				ForeColor = Theme.Dim,
				Font = Theme.UI(9f, false)
			};
			acDrawer = new DBPanel
			{
				Name = "AntiCheatDrawer",
				BackColor = Theme.Card,
				Visible = false
			};
			acRosterBar.Controls.Add(acDrawer);
			acDrawerTitle = new Label
			{
				Name = "AntiCheatDrawerTitle",
				BackColor = Theme.Card,
				ForeColor = Theme.Fg,
				Font = Theme.UI(11f, true)
			};
			acDrawer.Controls.Add(acDrawerTitle);
			acDrawer.Controls.Add(lblAcRoster);
			acDrawerClose = new PerformanceActionButton(AutoText("返回模块", "Back to modules"))
			{
				Name = "AntiCheatDrawerClose",
				Bg = Theme.Card
			};
			acDrawerClose.Click += delegate
			{
				CloseAcDrawer();
			};
			acDrawer.Controls.Add(acDrawerClose);
			acClear = new PerformanceActionButton(Lang.T("btn.roster.clear"))
			{
				Name = "acClearRoster",
				Bg = Theme.Card,
				ForeColor = Theme.Danger
			};
			acClear.Click += delegate
			{
				OnClearRoster();
			};
			acDrawer.Controls.Add(acClear);
			acDrawerBody = new DBPanel
			{
				Name = "AntiCheatDrawerBody",
				BackColor = Theme.Card,
				AutoScroll = true
			};
			Native.Dark(acDrawerBody);
			acDrawer.Controls.Add(acDrawerBody);
			RefreshAcList();
			SyncAcRoster();
			pageAntiCheat.SizeChanged += delegate
			{
				LayoutAntiCheatViewport();
			};
			LayoutAntiCheatViewport();
		}

		private void ShowAcHelp()
		{
			ShowAcInformation(Lang.T("v14.anticheat"), Lang.T("v14.anticheat.groups.sub") + "\r\n\r\n" + Lang.T("ac.mode." + AntiCheatModes.Token(tamer.Mode) + ".d") + "\r\n\r\n" + Lang.T("ac.mode.disclaimer"));
		}

		private void ShowAcInformation(string title, string detail)
		{
			PaviseDialog.Info(this, title, detail);
		}

		private void LayoutAntiCheatViewport()
		{
			if (acRosterBar == null || acList == null || acArranging || acViewport == null)
			{
				return;
			}
			AcViewState acViewState = CaptureAcViewState();
			acArranging = true;
			try
			{
				int num = Theme.S((pageAntiCheat.ClientSize.Width < Theme.S(500)) ? 16 : 24);
				int num2 = Theme.S(16);
				acViewport.SetBounds(num, num2, Math.Max(1, pageAntiCheat.ClientSize.Width - num * 2), Math.Max(1, pageAntiCheat.ClientSize.Height - num2 - Theme.S(8)));
				Point point = new Point(0, Math.Max(0, -acViewport.AutoScrollPosition.Y));
				acViewport.AutoScrollPosition = Point.Empty;
				int num3 = Math.Max(1, acViewport.Width);
				int num4 = Theme.S(8);
				int num5 = Theme.S(20);
				int num6 = Theme.S(64);
				bool flag = num3 < Theme.S(840);
				int num7 = ((!flag) ? Math.Max(Theme.S(290), Math.Min(Theme.S(360), (int)((float)num3 * 0.29f))) : 0);
				int num8 = (flag ? num4 : (num7 + num5));
				int num9 = Math.Max(1, num3 - num8 - num4);
				int num10 = (flag ? (num3 - num4 * 2) : (num7 - num4));
				int num11 = LayoutAcControlBay(num10, flag);
				int num12 = 0;
				foreach (SettingCard acCard in acCards)
				{
					num12 += ((AntiCheatModuleRow)acCard).MeasureHeight(num9);
				}
				num12 += Math.Max(0, acCards.Count - 1);
				int num13 = (flag ? (num11 + Theme.S(20)) : num4);
				int val = Math.Max((!flag) ? (num11 + num4) : 0, num13 + num12 + num6 + num4);
				int num14 = Math.Max(val, Math.Min(Theme.S(540), acViewport.Height));
				bool flag2 = num14 > acViewport.Height;
				if (flag2)
				{
					num3 = Math.Max(1, num3 - SystemInformation.VerticalScrollBarWidth);
					num7 = ((!flag) ? Math.Max(Theme.S(290), Math.Min(Theme.S(360), (int)((float)num3 * 0.29f))) : 0);
					num8 = (flag ? num4 : (num7 + num5));
					num9 = Math.Max(1, num3 - num8 - num4);
					num10 = (flag ? (num3 - num4 * 2) : (num7 - num4));
					num11 = LayoutAcControlBay(num10, flag);
					num13 = (flag ? (num11 + Theme.S(20)) : num4);
					num12 = 0;
					foreach (SettingCard acCard2 in acCards)
					{
						num12 += ((AntiCheatModuleRow)acCard2).MeasureHeight(num9);
					}
					num12 += Math.Max(0, acCards.Count - 1);
					num14 = Math.Max(num14, Math.Max((!flag) ? (num11 + num4) : 0, num13 + num12 + num6 + num4));
				}
				acRosterBar.SetBounds(0, 0, num3, num14);
				acControlBay.SetBounds(num4, num4, num10, flag ? num11 : (num14 - num4 * 2));
				int num15 = num14 - num4 - num6;
				int num16 = Math.Max(num12, num15 - num13);
				acList.SetBounds(num8, num13, num9, num16);
				int num17 = 0;
				int num18 = 0;
				int num19 = Math.Max(0, num16 - num12);
				foreach (SettingCard acCard3 in acCards)
				{
					AntiCheatModuleRow antiCheatModuleRow = (AntiCheatModuleRow)acCard3;
					int num20 = antiCheatModuleRow.MeasureHeight(num9) + num19 / Math.Max(1, acCards.Count) + ((num18 < num19 % Math.Max(1, acCards.Count)) ? 1 : 0);
					antiCheatModuleRow.SetBounds(0, num17, num9, num20);
					antiCheatModuleRow.ArrangeContent();
					num17 += num20 + 1;
					num18++;
				}
				acUtility.SetBounds(num8, num15, num9, num6);
				int num21 = Theme.S(16);
				int num22 = Math.Max(1, (num9 - num21) / 2);
				acRosterEntry.SetBounds(Theme.S(8), Theme.S(12), Math.Max(1, num22 - Theme.S(8)), Theme.S(42));
				acOtherToggle.SetBounds(num22 + num21, Theme.S(12), Math.Max(1, num9 - num22 - num21 - Theme.S(8)), Theme.S(42));
				acDrawer.SetBounds(num8, num13, num9, num16);
				LayoutAcDrawer(flag2);
				if (flag2 && acDrawer.Visible)
				{
					int num23 = Math.Max(0, acDrawerBody.AutoScrollMinSize.Height - acDrawerBody.Height);
					if (num23 > 0)
					{
						acDrawerBody.Height += num23;
						acDrawer.Height += num23;
						num14 += num23;
						acRosterBar.Height = num14;
						acUtility.Top += num23;
					}
				}
				acRosterBar.SplitX = ((!flag) ? (num7 + Theme.S(8)) : 0);
				acRosterBar.UtilityBounds = new Rectangle(num8, acUtility.Top, num9, 1);
				acRosterBar.Invalidate();
				acList.Invalidate();
				acViewport.AutoScrollMinSize = new Size(0, num14);
				acViewport.AutoScrollPosition = new Point(0, Math.Min(point.Y, DockScrollLimit(acViewport)));
				pageAntiCheat.AutoScrollMinSize = Size.Empty;
				acLayoutScale = Dpi.Scale;
				RestoreAcScrollPosition(acViewState);
			}
			finally
			{
				acArranging = false;
			}
		}

		private int LayoutAcControlBay(int width, bool stacked)
		{
			int num = Theme.S(stacked ? 14 : 22);
			int num2 = Theme.S(stacked ? 14 : 24);
			int num3 = Theme.S(28);
			int num4 = num + num3 + Theme.S(10);
			acTitleIcon.SetBounds(num, num2 + Theme.S(6), num3, num3);
			int val = Math.Max(1, width - num - num4 - Theme.S(26));
			acTitleLabel.SetBounds(num4, num2 + Theme.S(4), val, Theme.S(30));
			int val2 = TextRenderer.MeasureText(acTitleLabel.Text, acTitleLabel.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
			acHelp.SetBounds(num4 + Math.Min(val, val2), num2 + Theme.S(6), Theme.S(26), Theme.S(26));
			int num5 = num2 + Theme.S(stacked ? 55 : 86);
			acStrengthLabel.SetBounds(num, num5, width - num * 2, Theme.S(22));
			acModePicker.Horizontal = stacked;
			int num6 = acModePicker.MinimumContentHeight(Math.Max(1, width - num * 2));
			acModePicker.SetBounds(num, num5 + Theme.S(34), Math.Max(1, width - num * 2), num6);
			int num7 = acModePicker.Bottom + Theme.S(22);
			if (stacked)
			{
				acStrengthHelp.SetBounds(Math.Max(num, width - num - Theme.S(126)), num5 - Theme.S(2), Theme.S(126), Theme.S(26));
				return acModePicker.Bottom + Theme.S(16);
			}
			acStrengthHelp.SetBounds(num, num7, width - num * 2, Theme.S(32));
			return acStrengthHelp.Bottom + Theme.S(20);
		}

		private void LayoutAntiCheatCards()
		{
			LayoutAntiCheatViewport();
		}

		private AcViewState CaptureAcViewState()
		{
			if (curPage != pageAntiCheat || acDrawerBody == null || acViewport == null)
			{
				return null;
			}
			float num = Math.Max(0.01f, acLayoutScale);
			int num2 = Math.Max(0, -acViewport.AutoScrollPosition.Y);
			AcViewState acViewState = new AcViewState();
			acViewState.Drawer = acDrawerKind;
			acViewState.BodyScroll = acDrawerBody.AutoScroll;
			acViewState.InnerY = (float)Math.Max(0, -acDrawerBody.AutoScrollPosition.Y) / num;
			acViewState.OuterY = (float)num2 / num;
			acViewState.BodyOrigin = (float)(acRosterBar.Top + num2 + acDrawer.Top + acDrawerBody.Top) / num;
			return acViewState;
		}

		private void RestoreAcViewState(AcViewState state)
		{
			if (state != null && curPage == pageAntiCheat)
			{
				if (state.Drawer.Length > 0)
				{
					OpenAcDrawer(state.Drawer);
				}
				RestoreAcScrollPosition(state);
			}
		}

		private void RestoreAcScrollPosition(AcViewState state)
		{
			if (state != null && !(state.Drawer != acDrawerKind) && curPage == pageAntiCheat)
			{
				if (acDrawerBody.AutoScroll && state.Drawer.Length > 0)
				{
					float num = (state.BodyScroll ? state.InnerY : Math.Max(0f, state.OuterY - state.BodyOrigin));
					int val = DockScrollLimit(acDrawerBody);
					acDrawerBody.AutoScrollPosition = new Point(0, Math.Min((int)Math.Round(num * Dpi.Scale), val));
				}
				float num2 = state.OuterY;
				if (!acDrawerBody.AutoScroll && state.BodyScroll && state.Drawer.Length > 0)
				{
					int num3 = Math.Max(0, -acViewport.AutoScrollPosition.Y);
					num2 = (float)(acRosterBar.Top + num3 + acDrawer.Top + acDrawerBody.Top) / Dpi.Scale + state.InnerY;
				}
				int val2 = DockScrollLimit(acViewport);
				acViewport.AutoScrollPosition = new Point(0, Math.Min((int)Math.Round(num2 * Dpi.Scale), val2));
			}
		}

		private void OpenAcDrawer(string kind)
		{
			if (kind != "roster" && kind != "records")
			{
				return;
			}
			if (acDrawerKind != kind)
			{
				acDrawerBody.AutoScrollPosition = Point.Empty;
			}
			acDrawerKind = kind;
			acOtherOpen = kind == "records";
			if (kind == "roster")
			{
				SyncAcRoster();
			}
			acDrawerTitle.Text = AutoText((kind == "roster") ? "兼容名单" : "兼容与记录", (kind == "roster") ? "Compatibility list" : "Compatibility & records");
			acDrawer.Visible = true;
			acList.Visible = false;
			acClear.Visible = kind == "roster";
			lblAcRoster.Visible = kind == "roster";
			foreach (Control acReadOnlyCard in acReadOnlyCards)
			{
				acReadOnlyCard.Visible = kind == "records";
			}
			foreach (Control acRosterRow in acRosterRows)
			{
				acRosterRow.Visible = kind == "roster";
			}
			LayoutAntiCheatViewport();
			acDrawer.BringToFront();
			acDrawerClose.Focus();
		}

		private bool CloseAcDrawer()
		{
			if (acDrawerKind.Length == 0 || acDrawer == null)
			{
				return false;
			}
			string text = acDrawerKind;
			acDrawerKind = "";
			acOtherOpen = false;
			acDrawer.Visible = false;
			acList.Visible = true;
			LayoutAntiCheatViewport();
			((text == "roster") ? acRosterEntry : acOtherToggle).Focus();
			return true;
		}

		private void LayoutAcDrawer(bool outerScroll)
		{
			Point point = new Point(0, Math.Max(0, -acDrawerBody.AutoScrollPosition.Y));
			int num = Theme.S(8);
			int num2 = Theme.S(122);
			int num3 = Theme.S(52);
			acDrawerTitle.SetBounds(num, Theme.S(12), Math.Max(1, acDrawer.Width - num2 - num * 3), Theme.S(26));
			acDrawerClose.SetBounds(Math.Max(num, acDrawer.Width - num2 - num), Theme.S(8), num2, Theme.S(34));
			int num4 = num3;
			if (acDrawerKind == "roster")
			{
				lblAcRoster.SetBounds(num, num3, Math.Max(1, acDrawer.Width - Theme.S(120)), Theme.S(28));
				acClear.SetBounds(Math.Max(num, acDrawer.Width - Theme.S(96)), num3 - Theme.S(3), Theme.S(88), Theme.S(32));
				num4 += Theme.S(42);
			}
			acDrawerBody.SetBounds(num, num4, Math.Max(1, acDrawer.Width - num * 2), Math.Max(1, acDrawer.Height - num4 - num));
			acDrawerBody.AutoScrollPosition = Point.Empty;
			int num5 = Math.Max(1, acDrawerBody.Width - ((!outerScroll) ? SystemInformation.VerticalScrollBarWidth : 0));
			int num6 = 0;
			if (acDrawerKind == "records")
			{
				int num7 = ((num5 < Theme.S(620)) ? 1 : 2);
				int num8 = Theme.S(10);
				int num9 = Math.Max(1, (num5 - (num7 - 1) * num8) / num7);
				for (int i = 0; i < acReadOnlyCards.Count; i++)
				{
					acReadOnlyCards[i].SetBounds(i % num7 * (num9 + num8), i / num7 * Theme.S(62), num9, Theme.S(52));
				}
				num6 = (acReadOnlyCards.Count + num7 - 1) / num7 * Theme.S(62);
			}
			else if (acDrawerKind == "roster")
			{
				foreach (Control acRosterRow in acRosterRows)
				{
					acRosterRow.SetBounds(0, num6, num5, Theme.S(48));
					num6 += Theme.S(54);
				}
			}
			acDrawerBody.AutoScrollMinSize = new Size(0, num6);
			acDrawerBody.AutoScroll = !outerScroll;
			if (!outerScroll)
			{
				acDrawerBody.AutoScrollPosition = new Point(0, Math.Min(point.Y, DockScrollLimit(acDrawerBody)));
			}
		}

		private void SyncAcRoster()
		{
			if (acRosterEntry == null)
			{
				return;
			}
			string[] array = ProtectedGameRoster.Names();
			((AcDrawerButton)acRosterEntry).Count = array.Length;
			acRosterEntry.AccessibleDescription = ((array.Length == 0) ? Lang.T("ac.roster.empty") : string.Join(" · ", array));
			lblAcRoster.Text = array.Length + AutoText(" 项", " entries");
			acClear.Enabled = array.Length > 0;
			bool flag = array.Length != acPresentedRoster.Length;
			int num = 0;
			while (!flag && num < array.Length)
			{
				flag = array[num] != acPresentedRoster[num];
				num++;
			}
			if (!flag && acRosterRows.Count != 0)
			{
				return;
			}
			foreach (Control acRosterRow in acRosterRows)
			{
				acRosterRow.Dispose();
			}
			acRosterRows.Clear();
			acPresentedRoster = array;
			if (array.Length == 0)
			{
				Label label = new Label();
				label.Name = "AntiCheatRosterEmpty";
				label.Text = AutoText("暂无兼容名单", "The compatibility list is empty");
				label.BackColor = Theme.Card;
				label.ForeColor = Theme.Dim;
				label.Font = Theme.UI(10f, false);
				label.TextAlign = ContentAlignment.MiddleLeft;
				label.Visible = acDrawerKind == "roster";
				Label label2 = label;
				acDrawerBody.Controls.Add(label2);
				acRosterRows.Add(label2);
			}
			else
			{
				string[] array2 = array;
				foreach (string text in array2)
				{
					AcReadOnlyButton acReadOnlyButton = new AcReadOnlyButton(text, "", false);
					acReadOnlyButton.Name = "AntiCheatRosterItem";
					acReadOnlyButton.Visible = acDrawerKind == "roster";
					acReadOnlyButton.AccessibleDescription = text;
					AcReadOnlyButton acReadOnlyButton2 = acReadOnlyButton;
					string captured = text;
					acReadOnlyButton2.Click += delegate
					{
						ShowAcInformation(AutoText("兼容名单", "Compatibility list"), captured);
					};
					acDrawerBody.Controls.Add(acReadOnlyButton2);
					acRosterRows.Add(acReadOnlyButton2);
				}
			}
			if (acDrawerKind == "roster")
			{
				LayoutAntiCheatViewport();
			}
		}

		private void OnClearRoster()
		{
			if (ProtectedGameRoster.Names().Length != 0 && PaviseDialog.Confirm(this, Lang.T("ac.roster"), Lang.T("ac.roster.confirm"), DlgKind.Warn))
			{
				ProtectedGameRoster.Clear();
				SyncAcRoster();
			}
		}

		private void RefreshAcGroupStates()
		{
			if (acModePicker != null && acModePicker.Index != (int)tamer.Mode)
			{
				acModePicker.Index = (int)tamer.Mode;
			}
			if (acModePicker != null)
			{
				acModePicker.AccessibleDescription = Lang.T("ac.mode." + AntiCheatModes.Token(tamer.Mode) + ".d");
			}
			for (int i = 0; i < acGroups.Count && i < acCards.Count; i++)
			{
				int num = tamer.GroupState(acGroups[i].Key);
				acCards[i].SetStatus(tamer.GroupStatus(acGroups[i].Key), (num == 1) ? Theme.Green : Theme.Dim);
				AntiCheatModuleRow row = acCards[i] as AntiCheatModuleRow;
				if (row != null)
				{
					string detail = tamer.AffinityDetails(acGroups[i].Key);
					if (row.AdditionalDetails != detail) { row.AdditionalDetails = detail; row.Invalidate(); }
				}
				acToggles[i].SetSilently(tamer.IsGroupEnabled(acGroups[i].Key));
			}
			if (acDrawerBody != null)
			{
				SyncAcRoster();
			}
		}

		private void RefreshAcList()
		{
			acArranging = true;
			try
			{
				while (acList.Controls.Count > 0)
				{
					acList.Controls[0].Dispose();
				}
				foreach (Control acReadOnlyCard in acReadOnlyCards)
				{
					acReadOnlyCard.Dispose();
				}
				acGroups.Clear();
				acCards.Clear();
				acToggles.Clear();
				acReadOnlyCards.Clear();
				AcGroup[] groups = AntiCheatCatalog.Groups;
				foreach (AcGroup acGroup in groups)
				{
					if (!acGroup.Suppressible)
					{
						AddAcReadOnly(acGroup.Name, Lang.T("ac." + acGroup.Key + ".d"), acGroup.Procs, acGroup.RecordOnly);
						continue;
					}
					AcGroup captured = acGroup;
					Toggle toggle = MakeSwitch(tamer.IsGroupEnabled(acGroup.Key), null);
					toggle.Name = "anticheat_" + acGroup.Key;
					toggle.AccessibleName = acGroup.Name;
					toggle.Bg = Theme.Card;
					toggle.CheckedChanged += delegate
					{
						tamer.SetGroupEnabled(captured.Key, toggle.Checked);
						RefreshAcGroupStates();
					};
					AntiCheatModuleRow card = new AntiCheatModuleRow
					{
						Name = "AntiCheatCard_" + acGroup.Key,
						Title = acGroup.Name,
						Provider = ((acGroup.Key == "ace") ? AutoText("腾讯", "Tencent") : ((acGroup.Key == "eac") ? "Epic" : "")),
						IconKey = acGroup.Key,
						Desc = Lang.T("ac." + acGroup.Key + ".d"),
						AdditionalDetails = string.Join(" · ", acGroup.Procs) + "\r\n\r\n" + Lang.T("ac.mode.disclaimer")
					};
					if (acGroup.Key == "ace")
					{
						card.Title = AutoText("ACE 反作弊专家", "ACE Anti-Cheat Expert");
					}
					if (acGroup.Key == "eac")
					{
						card.Title = "EasyAntiCheat";
					}
					card.Host(toggle);
					card.DetailsRequested = delegate
					{
						ShowAcInformation(captured.Name, card.DetailsText);
					};
					card.ContentSizeChanged = LayoutAntiCheatViewport;
					acList.Controls.Add(card);
					acGroups.Add(acGroup);
					acCards.Add(card);
					acToggles.Add(toggle);
				}
				AcProtectionGroup[] protectionOnlyGroups = AntiCheatCatalog.ProtectionOnlyGroups;
				foreach (AcProtectionGroup acProtectionGroup in protectionOnlyGroups)
				{
					AddAcReadOnly(acProtectionGroup.Name, Lang.T("ac." + acProtectionGroup.Key + ".d"), acProtectionGroup.PatternsForDisplay(), false);
				}
				((AcDrawerButton)acOtherToggle).Count = acReadOnlyCards.Count;
				RefreshAcGroupStates();
			}
			finally
			{
				acArranging = false;
			}
			LayoutAntiCheatViewport();
		}

		private void AddAcReadOnly(string name, string description, string[] processes, bool recordOnly)
		{
			string tag = Lang.T(recordOnly ? "ac.recordonly" : "ac.protectiononly");
			if (tag == "ac.recordonly")
			{
				tag = AutoText("仅记录", "Record only");
			}
			AcReadOnlyButton acReadOnlyButton = new AcReadOnlyButton(name, tag, recordOnly);
			acReadOnlyButton.Name = "AntiCheatReadOnly";
			acReadOnlyButton.Visible = false;
			acReadOnlyButton.AccessibleName = name + " · " + tag;
			AcReadOnlyButton acReadOnlyButton2 = acReadOnlyButton;
			acReadOnlyButton2.Click += delegate
			{
				ShowAcInformation(name, tag + "\r\n\r\n" + description + "\r\n\r\n" + string.Join(" · ", processes));
			};
			acDrawerBody.Controls.Add(acReadOnlyButton2);
			acReadOnlyCards.Add(acReadOnlyButton2);
		}

		private void LayoutAuditViewport()
		{
			if (auditArranging || pageAudit == null || auditScan == null || btnAuditStart == null || auditScroll == null || auditScan.IsDisposed)
			{
				return;
			}
			auditArranging = true;
			pageAudit.SuspendLayout();
			try
			{
				int val = Math.Max(0, -pageAudit.AutoScrollPosition.Y);
				pageAudit.AutoScrollPosition = Point.Empty;
				pageAudit.AutoScroll = false;
				pageAudit.AutoScrollMinSize = Size.Empty;
				int num = Theme.S(24);
				int num2 = Theme.S(8);
				int num3 = Theme.S(16);
				int num4 = pageAudit.ClientSize.Height;
				bool flag = Volatile.Read(ref auditBusy) != 0;
				bool flag2 = auditReport != null && !flag;
				int num5 = ((flag2 || flag) ? (num3 + Theme.S(44)) : num3);
				int num6 = (flag2 ? Math.Max(1, num4 - num5 - num2) : Math.Max(Theme.S(400), num4 - num5 - num2));
				bool flag3 = !flag2 && num5 + num6 + num2 > num4;
				int num7 = Math.Max(1, pageAudit.ClientSize.Width - num * 2 - (flag3 ? SystemInformation.VerticalScrollBarWidth : 0));
				int num8 = Math.Min(Theme.S(150), Math.Max(1, (num7 - num2) / 2));
				btnAuditRun.SetBounds(num, num3, num8, Theme.S(34));
				btnAuditFixAll.SetBounds(num + num7 - num8, num3, num8, Theme.S(34));
				btnAuditCancel.Bounds = btnAuditFixAll.Bounds;
				lblAuditStatus.SetBounds(btnAuditRun.Right + num2, num3 + Theme.S(8), Math.Max(1, btnAuditFixAll.Left - btnAuditRun.Right - num2 * 2), Theme.S(20));
				auditScan.SetBounds(num, num5, num7, num6);
				int num9 = Math.Min(Theme.S(184), Math.Max(1, num7 - num2 * 2));
				int num10 = Theme.S(38);
				btnAuditStart.SetBounds((num7 - num9) / 2, Math.Min(auditScan.ContentBottom + Theme.S(20), num6 - num10 - num2), num9, num10);
				auditScroll.SetBounds(num, num5, num7, Math.Max(1, num4 - num5 - num2));
				LayoutAuditRows();
				if (flag3)
				{
					pageAudit.AutoScrollMinSize = new Size(0, num5 + num6 + num2);
					pageAudit.AutoScroll = true;
					pageAudit.AutoScrollPosition = new Point(0, Math.Min(val, DockScrollLimit(pageAudit)));
				}
			}
			finally
			{
				pageAudit.ResumeLayout(true);
				auditArranging = false;
			}
		}

		private void LayoutAuditRows()
		{
			int val = Math.Max(0, -auditScroll.AutoScrollPosition.Y);
			auditScroll.SuspendLayout();
			try
			{
				auditScroll.AutoScrollPosition = Point.Empty;
				Control[] array = new Control[auditScroll.Controls.Count];
				auditScroll.Controls.CopyTo(array, 0);
				Array.Sort(array, (Control a, Control b) => a.Top.CompareTo(b.Top));
				int num = 0;
				int auditContentWidth = AuditContentWidth;
				Control[] array2 = array;
				foreach (Control control in array2)
				{
					control.Top += num;
					AuditRowCard auditRowCard = control as AuditRowCard;
					if (auditRowCard != null)
					{
						int num2 = auditRowCard.Height;
						auditRowCard.SetLogicalWidth(auditContentWidth);
						num += auditRowCard.Height - num2;
					}
					else if (control is Label)
					{
						control.Width = Math.Max(1, Theme.S(auditContentWidth + 6) - control.Left);
					}
				}
			}
			finally
			{
				auditScroll.ResumeLayout(true);
			}
			auditScroll.AutoScrollPosition = new Point(0, Math.Min(val, DockScrollLimit(auditScroll)));
		}

		private void BuildAuditPage()
		{
			Native.Dark(pageAudit);
			int num = PageHeader(pageAudit, Lang.T("nav.audit"), Lang.T("audit.sub"), 2, false);
			btnAuditRun = new PillButton(Lang.F("audit.rerun", 120), BtnKind.Primary);
			btnAuditRun.SetBounds(Theme.S(30), Theme.S(num), Theme.S(150), Theme.S(34));
			btnAuditRun.Click += delegate
			{
				StartAudit();
			};
			pageAudit.Controls.Add(btnAuditRun);
			int num2 = 158;
			int num3 = 150;
			lblAuditStatus = CardLabel(pageAudit, "", 30 + num2, num + 8, ContentW - num2 - num3 - 12, 20, 8f, false, Theme.Dim);
			btnAuditFixAll = new PillButton(Lang.T("audit.fixall.none"), BtnKind.Primary);
			btnAuditFixAll.SetBounds(Theme.S(30 + ContentW - num3), Theme.S(num), Theme.S(num3), Theme.S(34));
			btnAuditFixAll.Enabled = false;
			btnAuditFixAll.Click += delegate
			{
				OnAuditFixAllClick();
			};
			pageAudit.Controls.Add(btnAuditFixAll);
			btnAuditCancel = new PillButton(Lang.T("dlg.cancel"), BtnKind.Normal);
			btnAuditCancel.Name = "auditCancel";
			btnAuditCancel.Bounds = btnAuditFixAll.Bounds;
			btnAuditCancel.Visible = false;
			btnAuditCancel.Click += delegate
			{
				auditCancelRequested = true;
				btnAuditCancel.Enabled = false;
				auditScan.ReportProgress(0f, Lang.T("irqcheck.dialog.stopping"));
			};
			pageAudit.Controls.Add(btnAuditCancel);
			num += 44;
			auditScroll = new DBPanel();
			auditScroll.SetBounds(Theme.S(20), Theme.S(num), Theme.S(PageW - 40), Theme.S(Math.Max(180, PageH - num - 8)));
			auditScroll.BackColor = Theme.Bg;
			auditScroll.AutoScroll = true;
			auditScroll.Visible = false;
			Native.Dark(auditScroll);
			pageAudit.Controls.Add(auditScroll);
			auditScan = new ScanView();
			auditScan.SetBounds(Theme.S(30), Theme.S(num + 6), Theme.S(ContentW), Theme.S(Math.Max(380, PageH - num - 20)));
			pageAudit.Controls.Add(auditScan);
			btnAuditStart = new PillButton(Lang.T("audit.start"), BtnKind.Primary);
			btnAuditStart.Bg = Theme.Card;
			btnAuditStart.Click += delegate
			{
				StartAudit();
			};
			auditScan.Controls.Add(btnAuditStart);
			RestoreAuditPagePresentation();
			pageAudit.SizeChanged += delegate
			{
				LayoutAuditViewport();
			};
			LayoutAuditViewport();
		}

		private void RestoreAuditPagePresentation()
		{
			auditRendered = false;
			if (Volatile.Read(ref auditBusy) != 0)
			{
				ShowAuditRunning();
			}
			else if (auditReport != null)
			{
				RenderAudit(auditReport);
			}
			else
			{
				ShowAuditIdle();
			}
		}

		private void ShowAuditIdle()
		{
			auditScan.SetIdle(Lang.T("audit.idle.title"), Lang.F("audit.idle.hint", 120));
			auditScan.Visible = true;
			auditScroll.Visible = false;
			SetToolbarVisible(false);
			int num = Theme.S(184);
			int num2 = Theme.S(38);
			btnAuditStart.SetBounds((auditScan.Width - num) / 2, auditScan.ContentBottom + Theme.S(20), num, num2);
			btnAuditStart.Visible = true;
			btnAuditStart.BringToFront();
			LayoutAuditViewport();
		}

		private void SetToolbarVisible(bool visible)
		{
			lblAuditStatus.Visible = visible;
			btnAuditRun.Visible = visible;
			btnAuditFixAll.Visible = visible;
			btnAuditCancel.Visible = false;
			if (visible)
			{
				UpdateFixAllState();
			}
		}

		private void StartAudit()
		{
			if (runtimeShutdownBusy || base.IsDisposed || Interlocked.CompareExchange(ref auditBusy, 1, 0) != 0)
			{
				return;
			}
			int version = Interlocked.Increment(ref auditRunVersion);
			auditCancelRequested = false;
			ShowAuditRunning();
			try
			{
				QueueUiStateWork(delegate
				{
					AuditReport report = null;
					try
					{
						Func<bool> cancelIrq = () => auditCancelRequested || base.IsDisposed || runtimeShutdownBusy || (gameMode != null && gameMode.IsActive);
						Action<float, string> progress = delegate(float ratio, string caption)
						{
							ReportAuditProgressForRun(version, ratio, caption);
						};
						report = SystemAudit.Collect(120000, cancelIrq, progress);
					}
					catch
					{
					}
					try
					{
						PostUiStateResult(delegate
						{
							CompleteAudit(version, report);
						});
					}
					catch
					{
						if (version == Volatile.Read(ref auditRunVersion))
						{
							Interlocked.Exchange(ref auditBusy, 0);
						}
					}
				});
			}
			catch
			{
				CompleteAudit(version, null);
			}
		}

		private void RefreshAuditAfterFix()
		{
			AuditReport previous = auditReport;
			if (previous == null)
			{
				auditIrqFix = null;
				StartAudit();
			}
			else
			{
				if (runtimeShutdownBusy || base.IsDisposed || Interlocked.CompareExchange(ref auditBusy, 1, 0) != 0)
				{
					return;
				}
				GpuIrqResult irqFix = auditIrqFix;
				auditIrqFix = null;
				int version = Interlocked.Increment(ref auditRunVersion);
				SetAuditButtons(false);
				lblAuditStatus.Text = PerformancePolicy.Text("正在刷新检查结果…", "Refreshing the results…");
				try
				{
					QueueUiStateWork(delegate
					{
						AuditReport report = null;
						try
						{
							report = SystemAudit.Refresh(previous, irqFix);
						}
						catch
						{
						}
						try
						{
							PostUiStateResult(delegate
							{
								CompleteAuditRefresh(version, report);
							});
						}
						catch
						{
							if (version == Volatile.Read(ref auditRunVersion))
							{
								Interlocked.Exchange(ref auditBusy, 0);
							}
						}
					});
				}
				catch
				{
					CompleteAuditRefresh(version, null);
				}
			}
		}

		private void CompleteAuditRefresh(int version, AuditReport report)
		{
			if (version != Volatile.Read(ref auditRunVersion))
			{
				return;
			}
			Interlocked.Exchange(ref auditBusy, 0);
			if (!base.IsDisposed && !runtimeShutdownBusy && auditScroll != null && !auditScroll.IsDisposed && lblAuditStatus != null && !lblAuditStatus.IsDisposed)
			{
				SetAuditButtons(true);
				if (report == null)
				{
					lblAuditStatus.Text = PerformancePolicy.Text("检查结果刷新失败，可点“重新体检”", "The results could not be refreshed; run the checkup again");
					UpdateFixAllState();
				}
				else
				{
					lblAuditStatus.Text = "";
					RenderAudit(report, true);
				}
			}
		}

		private void ShowAuditRunning()
		{
			lblAuditStatus.Visible = true;
			SetAuditButtons(false);
			btnAuditFixAll.Visible = false;
			btnAuditCancel.Visible = true;
			btnAuditCancel.Enabled = !auditCancelRequested;
			btnAuditStart.Visible = false;
			auditScroll.Visible = false;
			auditScan.Visible = true;
			auditScan.BeginScan(auditCancelRequested ? Lang.T("irqcheck.dialog.stopping") : Lang.F("audit.phase.sampling", 120));
			lblAuditStatus.Text = "";
			LayoutAuditViewport();
		}

		private void CompleteAudit(int version, AuditReport report)
		{
			if (version != Volatile.Read(ref auditRunVersion))
			{
				return;
			}
			Interlocked.Exchange(ref auditBusy, 0);
			if (base.IsDisposed || runtimeShutdownBusy || auditScan == null || auditScan.IsDisposed || btnAuditCancel == null || btnAuditCancel.IsDisposed)
			{
				return;
			}
			btnAuditCancel.Visible = false;
			auditScan.Stop();
			SetAuditButtons(true);
			if (report == null)
			{
				lblAuditStatus.Text = Lang.T((auditCancelRequested || (gameMode != null && gameMode.IsActive)) ? "audit.cancelled" : "audit.failed");
				if (!auditRendered)
				{
					ShowAuditIdle();
					return;
				}
				auditScan.Visible = false;
				auditScroll.Visible = true;
				SetToolbarVisible(true);
			}
			else
			{
				lblAuditStatus.Text = "";
				RenderAudit(report);
			}
		}

		private void ReportAuditProgress(float ratio, string caption)
		{
			ReportAuditProgressForRun(Volatile.Read(ref auditRunVersion), ratio, caption);
		}

		private void ReportAuditProgressForRun(int version, float ratio, string caption)
		{
			try
			{
				PostUiStateResult(delegate
				{
					if (!base.IsDisposed && !runtimeShutdownBusy && auditBusy != 0 && !auditCancelRequested && version == Volatile.Read(ref auditRunVersion) && auditScan != null && !auditScan.IsDisposed)
					{
						auditScan.ReportProgress(ratio, caption);
					}
				});
			}
			catch
			{
			}
		}

		private void SetAuditButtons(bool enabled)
		{
			btnAuditRun.Enabled = enabled;
			if (!enabled)
			{
				btnAuditFixAll.Enabled = false;
				btnAuditFixAll.Invalidate();
			}
		}

		private void RenderAudit(AuditReport report)
		{
			RenderAudit(report, false);
		}

		private void RenderAudit(AuditReport report, bool keepScroll)
		{
			Point autoScrollPosition = auditScroll.AutoScrollPosition;
			auditReport = report;
			auditScan.Stop();
			auditScan.Visible = false;
			btnAuditStart.Visible = false;
			auditScroll.Visible = true;
			SetToolbarVisible(true);
			auditScroll.SuspendLayout();
			auditScroll.AutoScrollPosition = Point.Empty;
			Control[] array = new Control[auditScroll.Controls.Count];
			auditScroll.Controls.CopyTo(array, 0);
			auditScroll.Controls.Clear();
			Control[] array2 = array;
			foreach (Control control in array2)
			{
				control.Dispose();
			}
			int sy = 2;
			sy = RenderAuditGroup(Lang.T("audit.sec.capability"), report.Capability, sy);
			sy = RenderAuditGroup(Lang.T("audit.sec.machine"), report.Machine, sy);
			sy = RenderAuditGroup(Lang.T("audit.sec.persistent"), report.Persistent, sy);
			sy = RenderAuditGroup(Lang.T("audit.sec.verdicts"), report.Verdicts, sy);
			CardLabel(auditScroll, Lang.T("audit.footer"), 8, sy + 4, AuditContentWidth - 16, 34, 7.6f, false, Theme.Faint);
			auditScroll.ResumeLayout();
			auditScroll.PerformLayout();
			LayoutAuditViewport();
			auditScroll.AutoScrollPosition = (keepScroll ? new Point(0, -autoScrollPosition.Y) : Point.Empty);
			auditRendered = true;
		}

		private Dictionary<string, AuditFix> BuildAuditFixes()
		{
			Dictionary<string, AuditFix> dictionary = new Dictionary<string, AuditFix>(StringComparer.Ordinal);
			dictionary.Add("memcompress", new AuditFix
			{
				CanFix = () => auditReport != null && auditReport.MemoryCompressionCanEnable,
				CanRevert = () => MemCompressTweak.OwnsState,
				Fix = delegate
				{
					ApplyAuditMemoryCompression(false);
				},
				Revert = delegate
				{
					ApplyAuditMemoryCompression(true);
				},
				FixLabel = "audit.mem.enable"
			});
			dictionary.Add("gpuirq", new AuditFix
			{
				CanFix = () => auditReport != null && auditReport.GpuIrq != null && (auditReport.GpuIrq.Outcome == GpuIrqOutcome.NeedsPlacement || auditReport.GpuIrq.Outcome == GpuIrqOutcome.NeedsVerification) && !GpuIrqCheck.AwaitingReboot(GpuIrqCheck.LoadState()) && !gameMode.IsActive,
				CanRevert = () => false,
				Fix = delegate
				{
					auditIrqFix = RunIrqCheck();
				},
				FixLabel = ((auditReport != null && auditReport.GpuIrq != null && auditReport.GpuIrq.Outcome == GpuIrqOutcome.NeedsVerification) ? "irqcheck.btn.verify" : "audit.irq.apply")
			});
			dictionary.Add("gpuirqrestore", new AuditFix
			{
				CanFix = () => false,
				CanRevert = () => GpuIrqCheck.HasResidue || IrqRelocate.HasResidue || IrqPriorityTweak.HasResidue,
				Revert = delegate
				{
					OnIrqRestore(null, EventArgs.Empty);
				}
			});
			dictionary.Add("msi", new AuditFix
			{
				CanFix = () => MsiModeTweak.Disabled().Count > 0,
				CanRevert = () => MsiModeTweak.EnabledByPavise,
				Fix = delegate
				{
					MsiModeTweak.Enable();
				},
				Revert = delegate
				{
					MsiModeTweak.Restore();
				},
				ConfirmKey = "audit.msi.confirm"
			});
			dictionary.Add("quantum", new AuditFix
			{
				CanFix = () => false,
				CanRevert = () => false,
				Open = delegate
				{
					ShowQuantumProfileSetting();
				},
				OpenText = () => PerformancePolicy.Text("前往时间片设置", "Open setting")
			});
			dictionary.Add("net", new AuditFix
			{
				CanFix = () => NetTweak.NeedsRepair(),
				CanRevert = () => NetTweak.RepairedByPavise,
				Fix = delegate
				{
					NetTweak.Repair();
				},
				Revert = delegate
				{
					NetTweak.Restore();
				}
			});
			dictionary.Add("linkmetric", new AuditFix
			{
				CanFix = () => LinkMetricTweak.NeedsRepair(),
				CanRevert = () => LinkMetricTweak.RepairedByPavise,
				Fix = delegate
				{
					LinkMetricTweak.Repair();
				},
				Revert = delegate
				{
					LinkMetricTweak.Restore();
				}
			});
			dictionary.Add("fth", new AuditFix
			{
				CanFix = () => FthTweak.NeedsRepair(),
				CanRevert = () => FthTweak.RepairedByPavise,
				Fix = delegate
				{
					FthTweak.Repair();
				},
				Revert = delegate
				{
					FthTweak.Restore();
				},
				ConfirmKey = "audit.fth.confirm"
			});
			return dictionary;
		}

		private void OnAuditFixClick(AuditFix fix)
		{
			if (auditBusy != 0)
			{
				return;
			}
			if (fix.Open != null && !fix.CanFix() && !fix.CanRevert())
			{
				fix.Open();
				return;
			}
			if (!elevated)
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.performance.needadmin"));
				return;
			}
			bool flag = fix.CanFix();
			bool flag2 = !flag && fix.CanRevert();
			if (!flag && !flag2)
			{
				RefreshAuditAfterFix();
			}
			else
			{
				if (!flag2 && fix.ConfirmKey != null && !PaviseDialog.Confirm(this, "PAVISE AUTO", Lang.T(fix.ConfirmKey), DlgKind.Warn))
				{
					return;
				}
				try
				{
					if (flag2)
					{
						fix.Revert();
					}
					else
					{
						fix.Fix();
					}
				}
				catch
				{
				}
				Logger.Log(flag2 ? Lang.T("log.panelformauditpage.1") : Lang.T("log.panelformauditpage.2"));
				RefreshAuditAfterFix();
			}
		}

		private void UpdateFixAllState()
		{
			int num = 0;
			foreach (AuditFix value in BuildAuditFixes().Values)
			{
				try
				{
					if (value.CanFix())
					{
						num++;
					}
				}
				catch
				{
				}
			}
			btnAuditFixAll.Enabled = num > 0;
			btnAuditFixAll.Text = ((num > 0) ? Lang.F("audit.fixall", num) : Lang.T("audit.fixall.none"));
			btnAuditFixAll.Invalidate();
		}

		private void OnAuditFixAllClick()
		{
			if (auditBusy != 0)
			{
				return;
			}
			if (!elevated)
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.performance.needadmin"));
				return;
			}
			int num = 0;
			foreach (AuditFix value in BuildAuditFixes().Values)
			{
				try
				{
					if (value.CanFix() && (value.ConfirmKey == null || PaviseDialog.Confirm(this, "PAVISE AUTO", Lang.T(value.ConfirmKey), DlgKind.Warn)))
					{
						value.Fix();
						num++;
					}
				}
				catch
				{
				}
			}
			Logger.Log((num > 0) ? (Lang.T("log.panelformauditpage.3") + num + Lang.T("log.panelformauditpage.4")) : Lang.T("log.panelformauditpage.5"));
			RefreshAuditAfterFix();
		}

		private int RenderAuditGroup(string title, List<AuditRow> rows, int sy)
		{
			Section(auditScroll, title, 6, sy);
			sy += 26;
			Dictionary<string, AuditFix> dictionary = BuildAuditFixes();
			foreach (AuditRow row in rows)
			{
				AuditFix value = null;
				bool flag = false;
				bool flag2 = false;
				bool flag3 = false;
				if (row.FixKey != null && dictionary.TryGetValue(row.FixKey, out value))
				{
					try
					{
						flag = value.CanFix();
						flag2 = !flag && value.CanRevert();
						flag3 = !flag && !flag2 && value.Open != null;
					}
					catch
					{
					}
				}
				bool flag4 = flag || flag2 || flag3;
				AuditFix boundFix = value;
				Action onClick = (flag4 ? ((Action)delegate
				{
					OnAuditFixClick(boundFix);
				}) : null);
				string btnText = (flag3 ? ((value.OpenText == null) ? "" : value.OpenText()) : Lang.T(flag ? (value.FixLabel ?? "audit.fix") : "audit.fix.revert"));
				AuditRowCard auditRowCard = new AuditRowCard(row.Name, row.Value, row.Note ?? "", Lang.F("audit.evidence.format", SystemAudit.EvidenceText(row.Evidence)), row.Warn, flag4, btnText, flag, onClick, AuditContentWidth);
				auditRowCard.SetBounds(Theme.S(6), Theme.S(sy), Theme.S(AuditContentWidth), Theme.S(auditRowCard.LogicalHeight));
				auditRowCard.Name = "auditRow_" + (row.FixKey ?? "info");
				auditScroll.Controls.Add(auditRowCard);
				sy += auditRowCard.LogicalHeight + 8;
			}
			sy += 8;
			return sy;
		}

		private void ApplyAuditMemoryCompression(bool restore)
		{
			if (restore ? MemCompressTweak.Restore() : MemCompressTweak.EnableCompression())
			{
				PaviseDialog.Info(this, "PAVISE AUTO", Lang.T(restore ? "log.memcompress.6" : "audit.mem.enabled"));
			}
			else
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("audit.mem.failed"));
			}
		}

		private void ShowAutoIgnoreDialog()
		{
			if (autoIgnoreDialogOpen || base.IsDisposed || base.Disposing)
			{
				return;
			}
			autoIgnoreDialogOpen = true;
			try
			{
				using (AutoIgnoreDialog dlg = new AutoIgnoreDialog(gameMode))
				{
					ShowDim(dlg);
				}
			}
			finally
			{
				autoIgnoreDialogOpen = false;
			}
		}

		private static int PerformanceActionCategory(PerformanceAction action)
		{
			if (!(action.Key == "AutoBackgroundFpsV1"))
			{
				if (!(action.Key == "AutoGameMitigationsV1") && !(action.Key == "AutoNtfsShortNamesV1"))
				{
					return 2;
				}
				return 0;
			}
			return 1;
		}

		private static bool HasAutomationInventory(int category, PerformanceRestartScope scope)
		{
			if ((category == 0 && PerformanceRestartFor("GmMmcss") == scope) || (category == 2 && (PerformanceRestartFor("GmEnglishInputV1") == scope || PerformanceRestartFor("GpuPrefStageOn") == scope)))
			{
				return true;
			}
			PerformanceAction[] all = PerformanceActionCatalog.All;
			foreach (PerformanceAction performanceAction in all)
			{
				if (!performanceAction.Environment && performanceAction.Key != "MatchVisualEffectsV1" && PerformanceActionCategory(performanceAction) == category && PerformanceRestartFor(performanceAction.Key) == scope)
				{
					return true;
				}
			}
			return false;
		}

		private void AddAutomationInventory(Control parent, ref int y, int category, PerformanceRestartScope scope)
		{
			if (category == 0 && PerformanceRestartFor("GmMmcss") == scope)
			{
				AddManualPerformanceToggle(parent, ref y, "GmMmcss", AutoText("多媒体调度", "Multimedia scheduling"), AutoText("调整已注册的游戏多媒体线程，目标需重新读取配置。", "Adjusts registered game multimedia threads; the target must reload the configuration."), () => Settings.LoadCached("GmMmcss", true), (bool value) => SaveManualPerformancePreference("GmMmcss", value));
			}
			if (category == 2 && PerformanceRestartFor("GmEnglishInputV1") == scope)
			{
				AddManualPerformanceToggle(parent, ref y, "GmEnglishInputV1", AutoText("游戏窗口切换英文输入", "Switch the game window to English input"), AutoText("入局时尝试一次，仅作用于确认的游戏窗口。", "Attempts once on session entry, only for the confirmed game window."), () => Settings.LoadCached("GmEnglishInputV1", false), (bool value) => SaveManualPerformancePreference("GmEnglishInputV1", value));
			}
			if (category == 2 && PerformanceRestartFor("GpuPrefStageOn") == scope)
			{
				AddManualPerformanceToggle(parent, ref y, "GpuPrefStageOn", AutoText("游戏高性能显卡预选", "Prefer the high-performance GPU for games"), AutoText("为游戏登记显卡偏好，下次启动生效。", "Registers the game GPU preference for the next launch."), () => Settings.LoadCached("GpuPrefStageOn", true), (bool value) => SaveManualPerformancePreference("GpuPrefStageOn", value), () => Native.OsBuild() >= 17134);
			}
			PerformanceAction[] all = PerformanceActionCatalog.All;
			foreach (PerformanceAction performanceAction in all)
			{
				if (!performanceAction.Environment && !(performanceAction.Key == "MatchVisualEffectsV1") && PerformanceActionCategory(performanceAction) == category && PerformanceRestartFor(performanceAction.Key) == scope)
				{
					PerformanceAction captured = performanceAction;
					AddManualPerformanceToggle(parent, ref y, performanceAction.Key, performanceAction.Title, ManualPerformanceActionDetail(performanceAction), () => Settings.LoadCached(captured.Key, false), (bool value) => SaveManualPerformanceAction(captured, value), () => ManualPerformanceActionSupported(captured.Key), null, () => gameMode.PerformanceExecutionState(captured.Key));
				}
			}
		}

		private void AddPerformanceRelatedSettings(Control parent, ref int y, int category)
		{
			if (category != 2)
			{
				AddAutomationSection(parent, ref y, AutoText("相关设置", "Related settings"));
			}
			if (category == 0)
			{
				AddAutomationDestination(parent, ref y, "GameSettings", "逐游戏设置", "Per-game settings", "核心范围、家族后台、ReBAR、禁用全屏优化与入库设置。", "Core selection, game-family control, ReBAR, fullscreen optimization and auto-add.", PageId.Library);
			}
			if (category == 1)
			{
				AddAutomationDestination(parent, ref y, "WhitelistSettings", "白名单", "Whitelist", "为需要保持正常运行的后台程序添加保护。", "Protect background applications that need to keep running normally.", PageId.Whitelist);
				AddAutomationDestination(parent, ref y, "AntiCheatSettings", "反作弊进程", "Anti-cheat processes", "兼容性专项，按明确的手动选择设置。", "Compatibility-specific options controlled by your explicit choices.", PageId.AntiCheat);
			}
			if (category == 0)
			{
				AddAutomationDestination(parent, ref y, "ProtectionSettings", "故障与恢复", "Faults & recovery", "查看动作故障与恢复记录，未完成的恢复不会因关闭功能丢失。", "Review action faults and recovery records; disabling a feature keeps unfinished recovery records.", PageId.Settings);
			}
		}

		private bool SaveManualPerformanceAction(PerformanceAction action, bool value)
		{
			if (action.Key == "MatchVisualEffectsV1")
			{
				return gameMode.SetVisualEffectsReduction(value);
			}
			if ((action.Key == "AutoGameMitigationsV1" || action.Key == "AutoNtfsShortNamesV1") && !elevated)
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.performance.needadmin"));
				return true;
			}
			if (value && action.Key == "AutoGameMitigationsV1" && !PaviseDialog.Confirm(this, action.Title, AutoText("这会为游戏库程序关闭部分漏洞利用防护，降低相应防护能力。下次启动游戏生效。\n\n确定手动开启？", "This disables selected exploit protections for library games and reduces their protection. Effective on the next game launch.\n\nEnable this manual option?"), DlgKind.Warn))
			{
				return true;
			}
			return SaveManualPerformancePreference(action.Key, value);
		}

		private static bool ManualPerformanceActionSupported(string key)
		{
			if (key == "AutoBackgroundFpsV1" || key == "AutoOpenGlThreadsV1")
			{
				return NvApi.Available;
			}
			return true;
		}

		private static string ManualPerformanceActionDetail(PerformanceAction action)
		{
			if (action.Key == "AutoGameMitigationsV1")
			{
				return AutoText("手动关闭游戏的部分漏洞利用防护；降低防护，下次启动生效。", "Manually disables selected game exploit protections; reduces protection, effective on next launch.");
			}
			if (action.Key == "AutoNtfsShortNamesV1")
			{
				return AutoText("游戏所在 NTFS 卷不再生成新的 8.3 短文件名，保留已有名称。", "Stops new 8.3 names on eligible NTFS game volumes while keeping existing names.");
			}
			if (action.Key == "AutoBackgroundFpsV1")
			{
				return AutoText("随后台治理，对无可见窗口的合格 NVIDIA 3D 程序限帧。竞技或智能升档不扩大范围，也不限制游戏前台。", "With background control active, caps eligible windowless NVIDIA 3D apps. Competitive and Smart escalation do not widen scope or cap the foreground game.");
			}
			if (action.Key == "AutoOpenGlThreadsV1")
			{
				return AutoText("仅供 NVIDIA OpenGL 游戏手动选择，下次启动生效。", "Manual choice for NVIDIA OpenGL games only; effective on the next launch.");
			}
			return action.Detail;
		}

		private void AddAutomationDestination(Control parent, ref int y, string id, string zh, string en, string detailZh, string detailEn, PageId target)
		{
			PillButton pillButton = new PillButton(AutoText("前往设置", "Configure"));
			pillButton.Name = "manualDestination_" + id;
			pillButton.Size = new Size(Theme.S(124), Theme.S(32));
			PillButton pillButton2 = pillButton;
			pillButton2.Click += delegate
			{
				nav.Select((int)target);
			};
			string desc = AutoText(detailZh, detailEn);
			int used;
			MakeAutoCard(parent, 6, y, ScrollContentW, FullTextCardHeight(desc, ScrollContentW, pillButton2, 82), AutoText(zh, en), desc, pillButton2, out used);
			y += used + 8;
		}

		private string PerGameAutomationLabel()
		{
			return AutoText("按游戏方案", "Per-game plan");
		}

		private string AutomationPreferenceStatus(string key)
		{
			string value = gameMode.AutomaticExecutionState(key);
			if (!string.IsNullOrEmpty(value))
			{
				return GameMode.AutomationStateLabel(value);
			}
			if (!PerformancePolicy.EnvironmentPreference(key))
			{
				return AutoText("已关闭", "Off");
			}
			return AutoText("已开启", "Enabled");
		}

		private static string AutoText(string zh, string en)
		{
			return PerformancePolicy.Text(zh, en);
		}

		private PillButton AddAutomationLink(Control parent, string zh, string en, PageId target, int x, int y, int width)
		{
			PillButton pillButton = new PillButton(AutoText(zh, en));
			pillButton.SetBounds(Theme.S(x), Theme.S(y), Theme.S(width), Theme.S(34));
			pillButton.Click += delegate
			{
				nav.Select((int)target);
			};
			parent.Controls.Add(pillButton);
			return pillButton;
		}

		private void BuildAutomationPage()
		{
			automationSync.Clear();
			ResetEnvironmentObservation();
			performancePrivacyPanels.Clear();
			environmentPrivacy = null;
			int num = 16;
			string[] array = new string[5]
			{
				AutoText("游戏性能", "Game"),
				AutoText("后台管理", "Background"),
				AutoText("图形与体验", "Graphics"),
				AutoText("延迟与系统", "System"),
				AutoText("实验室", "Laboratory")
			};
			performanceTabs = new TechTabs
			{
				Name = "performanceTabs",
				Visible = false,
				TabStop = false
			};
			performanceTabs.SetTabs(array, new string[5]
			{
				AutoText("调度与加载", "Scheduling and loading"),
				AutoText("管理竞争游戏资源的后台任务", "Manage competing background tasks"),
				AutoText("显卡偏好与使用体验", "GPU preferences and experience"),
				AutoText("低延迟、计时器与系统环境", "Latency, timers and system environment"),
				AutoText("手动选择实验策略，按条件执行", "Explicit experimental strategies")
			});
			pagePolicy.Controls.Add(performanceTabs);
			performanceCategories = new PerformanceCategoryBar(array)
			{
				Name = "PerformanceCategories"
			};
			performanceCategories.SetBounds(Theme.S(30), Theme.S(num), Theme.S(ContentW), Theme.S(44));
			pagePolicy.Controls.Add(performanceCategories);
			BuildConfigurationPresetControls();
			num += 58;
			performanceViewportTop = Theme.S(num);
			performanceTabPanels = new DBPanel[5];
			for (int i = 0; i < performanceTabPanels.Length; i++)
			{
				PerformanceFeatureGrid performanceFeatureGrid = new PerformanceFeatureGrid();
				performanceFeatureGrid.Name = "PerformanceCategory" + i;
				performanceFeatureGrid.Visible = i == 0;
				PerformanceFeatureGrid performanceFeatureGrid2 = performanceFeatureGrid;
				performanceFeatureGrid2.SetBounds(Theme.S(20), performanceViewportTop, Theme.S(PageW - 40), Theme.S(PageH - num - 8));
				pagePolicy.Controls.Add(performanceFeatureGrid2);
				performanceTabPanels[i] = performanceFeatureGrid2;
			}
			pageTabPanels[pagePolicy] = performanceTabPanels;
			performanceCategories.SelectionChanged = delegate(int index)
			{
				performanceTabs.Index = index;
			};
			performanceTabs.IndexChanged = delegate(int index)
			{
				for (int n = 0; n < performanceTabPanels.Length; n++)
				{
					DBPanel dBPanel2 = performanceTabPanels[n];
					if (dBPanel2.Visible)
					{
						tabScrollPositions[dBPanel2] = new Point(-dBPanel2.AutoScrollPosition.X, -dBPanel2.AutoScrollPosition.Y);
					}
					dBPanel2.Visible = n == index;
				}
				performanceCategories.Select(index, false);
				Point value;
				if (tabScrollPositions.TryGetValue(performanceTabPanels[index], out value))
				{
					performanceTabPanels[index].AutoScrollPosition = value;
				}
				((PerformanceFeatureGrid)performanceTabPanels[index]).Reflow();
				if (index == 2 || index == 3)
				{
					RefreshEnvironmentStateAsync();
				}
			};
			for (int j = 0; j < 3; j++)
			{
				int sy = 2;
				Control scroll = performanceTabPanels[j];
				List<PolicyItem> list = new List<PolicyItem>();
				foreach (PolicyItem item in PolicyCatalog.All)
				{
					if (!PerformancePolicy.IsIntent(item.Key) && !PerformancePolicy.IsRetired(item.Key) && !PerformancePolicy.IsGamePriorityOption(item.Key) && !(item.Key == "PowerPlanOn") && !(item.Key == "GmEnglishInputV1") && !(item.Key == "NvLowLat") && !(item.Key == "AutoTimerTick") && !(item.Key == "AutoGlobalTimer") && !(item.Key == "GmCacheWarmV2") && !(item.Key == "GmStandbyCleanerV2") && ManualPolicyCategory(item) == j)
					{
						list.Add(item);
					}
				}
				PerformanceRestartScope[] array2 = new PerformanceRestartScope[3]
				{
					PerformanceRestartScope.None,
					PerformanceRestartScope.ProcessRestart,
					PerformanceRestartScope.SystemRestart
				};
				foreach (PerformanceRestartScope scope in array2)
				{
					Predicate<PolicyItem> match = (PolicyItem item) => PerformanceRestartFor(item.Key) == scope;
					List<PolicyItem> list2 = list.FindAll(match);
					bool flag = HasAutomationInventory(j, scope) || (j == 0 && scope == PerformanceRestartScope.None) || (j == 2 && HasPerformanceEnvironmentGraphics(scope));
					if (list2.Count == 0 && !flag)
					{
						continue;
					}
					foreach (PolicyItem item2 in list2)
					{
						if (PrimaryPerformancePolicy(item2.Key))
						{
							AddManualPolicyControl(scroll, ref sy, item2);
						}
					}
					foreach (PolicyItem item3 in list2)
					{
						if (!PrimaryPerformancePolicy(item3.Key))
						{
							AddManualPolicyControl(scroll, ref sy, item3);
						}
					}
					AddAutomationInventory(scroll, ref sy, j, scope);
					if (j == 0 && scope == PerformanceRestartScope.None)
					{
						BuildEnvironmentPerformanceOptions(scroll, ref sy);
					}
					if (j == 2)
					{
						BuildPerformanceEnvironmentGraphics(scroll, ref sy, scope);
					}
				}
			}
			int sy2 = 2;
			BuildPerformanceSystemOptions(performanceTabPanels[3], ref sy2);
			PrivacyOptionsPanel backgroundPrivacy = BuildEnvironmentPrivacy(performanceTabPanels[1], PerformanceRestartScope.ProcessRestart, action => action.Key == PrivacyActionCatalog.BackgroundGlobal);
			backgroundPrivacy.Top = Theme.S(2);
			int labY = 2;
			BuildLaboratoryOptions(performanceTabPanels[4], ref labY);
			DBPanel[] array3 = performanceTabPanels;
			foreach (DBPanel dBPanel in array3)
			{
				ConfigurePerformanceCards(dBPanel);
			}
			OrderPerformanceConsole();
			foreach (DBPanel dBPanel in performanceTabPanels)
			{
				GroupPerformanceLifetimes(dBPanel);
			}
			pagePolicy.SizeChanged += delegate
			{
				LayoutPerformanceViewport();
			};
			LayoutPerformanceViewport();
			DBPanel[] array4 = performanceTabPanels;
			for (int m = 0; m < array4.Length; m++)
			{
				PerformanceFeatureGrid performanceFeatureGrid3 = (PerformanceFeatureGrid)array4[m];
				performanceFeatureGrid3.Complete();
			}
			RefreshAutomationPage();
		}

		private static int ManualPolicyCategory(PolicyItem item)
		{
			string key = item.Key;
			switch (key)
			{
			case "GmSuppress":
			case "GmPauseDl":
			case "GmPauseUpdate":
			case "GmPauseMaintenanceV1":
				return 1;
			default:
				if (item.GroupKey == "cfg.group.gpu" || key == "GmDwmBoost")
				{
					return 2;
				}
				return 0;
			}
		}

		private void ShowPerformanceSystemTab()
		{
			if (performanceTabs != null)
			{
				performanceTabs.Index = 3;
				performanceTabs.SnapToSelection();
			}
		}

		private static bool PrimaryPerformancePolicy(string key)
		{
			return key == "GmBoost";
		}

		internal static PerformanceRestartScope PerformanceRestartFor(string key)
		{
			if (key == PerformanceActionCatalog.Quantum) return PerformanceRestartScope.None;
			if (key == PrivacyActionCatalog.BackgroundGlobal) return PerformanceRestartScope.ProcessRestart;
			if (PerformancePolicy.IsIndependentManual(key))
			{
				return PerformanceRestartScope.SystemRestart;
			}
			switch (key)
			{
			case "GameNvRebarV1":
			case "GameDisableFullscreenOptimizationsV1":
			case "NvMaxPerf":
			case "NvShaderCache":
			case "NvLowLat":
			case "AutoOpenGlThreadsV1":
			case "AutoGameMitigationsV1":
			case "GpuPrefStageOn":
			case "WindowedOptimizations":
			case "VariableRefreshRate":
			case "NvVrrWindowed":
				return PerformanceRestartScope.ProcessRestart;
			default:
				return PerformanceRestartScope.None;
			}
		}

		private static string PerformanceScopeTitle(PerformanceRestartScope scope)
		{
			return PerformanceFeatureCard.RestartLabel(scope);
		}

		// Only settings restored by session teardown belong to the in-match group.
		internal static PerformanceLifetime PerformanceLifetimeFor(string key)
		{
			switch (key)
			{
			case "GmBoost":
			case "GmThreadBoostStrategyV1":
			case "GmPauseUpdate":
			case "GmPauseMaintenanceV1":
			case "GmPauseDl":
			case "GmAwake":
			case "GmDwmBoost":
			case "GmIntelEnduranceV1":
			case "GmEnglishInputV1":
			case "NvMaxPerf":
			case "NvShaderCache":
			case "NvLowLat":
			case "NvVrrWindowed":
			case "GpuPrefStageOn":
			case "LabEppYieldV1":
			case "LabGpuClockMaxV1":
			case "LabGpuPowerMaxV1":
			case "LabAudioLowBufferV1":
			case "LabCpuIdleStrategyV1":
				return PerformanceLifetime.Session;
			default:
				return PerformanceLifetime.Persistent;
			}
		}

		private static bool PerformanceControlLifetime(Control control, out PerformanceLifetime lifetime)
		{
			lifetime = PerformanceLifetime.Session;
			bool found = false;
			PerformanceFeatureCard card = control as PerformanceFeatureCard;
			if (card != null)
			{
				lifetime = card.Lifetime;
				return true;
			}
			foreach (Control child in control.Controls)
			{
				PerformanceLifetime nested;
				if (PerformanceControlLifetime(child, out nested))
				{
					found = true;
					if (nested == PerformanceLifetime.Persistent)
					{
						lifetime = PerformanceLifetime.Persistent;
					}
				}
			}
			if (!found)
			{
				lifetime = PerformanceLifetime.Persistent;
			}
			return found;
		}

		private static void GroupPerformanceLifetimes(Control grid)
		{
			List<Control> session = new List<Control>();
			List<Control> persistent = new List<Control>();
			List<Control> headings = new List<Control>();
			foreach (Control control in grid.Controls)
			{
				if (control is PerformanceSectionHeading)
				{
					headings.Add(control);
					continue;
				}
				PerformanceLifetime lifetime;
				PerformanceControlLifetime(control, out lifetime);
				(lifetime == PerformanceLifetime.Session ? session : persistent).Add(control);
			}
			grid.SuspendLayout();
			try
			{
				foreach (Control heading in headings)
				{
					grid.Controls.Remove(heading);
					heading.Dispose();
				}
				int index = 0;
				PerformanceLifetime[] order = new PerformanceLifetime[2] { PerformanceLifetime.Session, PerformanceLifetime.Persistent };
				foreach (PerformanceLifetime lifetime in order)
				{
					if (((lifetime == PerformanceLifetime.Session) ? session : persistent).Count > 0)
					{
						PerformanceSectionHeading heading = new PerformanceSectionHeading
						{
							Name = "performanceLifetime" + lifetime,
							Text = PerformanceSectionHeading.LifetimeTitle(lifetime),
							Lifetime = lifetime
						};
						grid.Controls.Add(heading);
						grid.Controls.SetChildIndex(heading, index++);
					}
					foreach (Control control in (lifetime == PerformanceLifetime.Session) ? session : persistent)
					{
						grid.Controls.SetChildIndex(control, index++);
					}
				}
			}
			finally
			{
				grid.ResumeLayout(false);
			}
		}

		private static void ConfigurePerformanceCards(Control parent)
		{
			foreach (Control control in parent.Controls)
			{
				PerformanceFeatureCard performanceFeatureCard = control as PerformanceFeatureCard;
				if (performanceFeatureCard != null && performanceFeatureCard.Hosted != null)
				{
					string text = PerformanceFeatureKey(performanceFeatureCard.Hosted.Name);
					performanceFeatureCard.RestartScope = ((text == "EnvPerformanceBundleV1" || text == "EnvStoragePowerGroupV1") ? PerformanceRestartScope.SystemRestart : PerformanceRestartFor(text));
					performanceFeatureCard.Lifetime = PerformanceLifetimeFor(text);
					performanceFeatureCard.RestartText = ((text == PrivacyActionCatalog.BackgroundGlobal) ? AutoText("重启相关应用", "Restart affected apps") : ((text == "NvLowLat") ? AutoText("部分游戏需重启", "Game restart may apply") : ""));
					performanceFeatureCard.Title = performanceFeatureCard.Title.Replace(" · 需重启", "").Replace(" · restart required", "");
					if (text == "EnvQuantumProfileV1")
					{
						performanceFeatureCard.Title = PerformanceActionCatalog.Find("EnvQuantumProfileV1").Title;
						performanceFeatureCard.RestartText = AutoText("优先在线应用", "Online update preferred");
					}
					ConfigurePerformanceConsole(performanceFeatureCard, text);
					performanceFeatureCard.ArrangePresentation();
				}
				ConfigurePerformanceCards(control);
			}
		}

		private void LayoutPerformanceViewport()
		{
			if (pagePolicy == null || pagePolicy.IsDisposed || performanceTabPanels == null)
			{
				return;
			}
			int num = ((root != null && !root.IsDisposed) ? root.Left : 0);
			int num2 = ((root != null && !root.IsDisposed) ? root.Top : 0);
			int num3 = Math.Max(0, Math.Min(pagePolicy.ClientSize.Width, base.ClientSize.Width - num - pagePolicy.Left));
			int num4 = Math.Max(0, Math.Min(pagePolicy.ClientSize.Height, base.ClientSize.Height - num2 - pagePolicy.Top));
			pagePolicy.AutoScroll = false;
			pagePolicy.AutoScrollMinSize = Size.Empty;
			int num5 = Theme.S(16);
			performanceViewportTop = num5 + Theme.S(62);
			int num6 = Theme.S(30);
			int num7 = Math.Max(num6, num3 - num6);
			if (performanceCategories != null)
			{
				performanceCategories.SetBounds(num6, num5, num7 - num6, Theme.S(44));
			}
			LayoutConfigurationPresetControls(num6, num5, num7);
			int num8 = Theme.S(20);
			int num9 = Theme.S(8);
			DBPanel[] array = performanceTabPanels;
			foreach (DBPanel dBPanel in array)
			{
				if (dBPanel != null && !dBPanel.IsDisposed)
				{
					dBPanel.SetBounds(num8, performanceViewportTop, Math.Max(0, num3 - num8 * 2), Math.Max(1, num4 - performanceViewportTop - num9));
				}
			}
		}

		private void AddAutomationSection(Control parent, ref int y, string text, PerformanceRestartScope scope = PerformanceRestartScope.None)
		{
		}

		private void AddManualPolicyControl(Control parent, ref int y, PolicyItem item)
		{
			string key = item.Key;
			if (key == BackgroundGovernancePolicy.SettingKey || key == "GmSuppress") return;
			string text = ((key == "GmSuppress") ? AutoText("后台治理", "Background protection") : ((key == "GmDwmBoost") ? AutoText("桌面合成调度", "Desktop compositor scheduling") : ((key == "GmThreadBoostStrategyV1") ? AutoText("游戏线程提优", "Game thread boost") : Lang.T(item.LangKey))));
			string text2 = ManualPolicyDescription(key);
			if (item.Kind == PolicyValueKind.Bool)
			{
				AddManualPerformanceToggle(parent, ref y, key, text, text2, () => PolicyResolver.RawGlobalValue(key) == "1", (bool value) => SaveManualPolicy(item, value ? "1" : "0"), () => ManualPolicySupported(key), () => gameMode.AutomationPolicySnapshot.ValueOf(key) == "1");
				return;
			}
			if (key == "GmThreadBoostStrategyV1")
			{
				AddManualPerformanceToggle(parent, ref y, key, text, text2, () => ThreadBoostSelection.IsOn(PolicyResolver.RawGlobalValue(key)), (bool value) => SaveManualPolicy(item, value ? "on" : "off"), null, null, gameMode.ThreadBoostExecutionState);
				return;
			}
			string[] choices = (string[])item.Choices.Clone();
			string[] array = new string[choices.Length];
			for (int i = 0; i < choices.Length; i++)
			{
				array[i] = ((choices[i] == "off") ? AutoText("关闭", "Off") : ((choices[i] == "on") ? AutoText("开启", "On") : choices[i]));
			}
			TierPicker picker = new TierPicker
			{
				Name = "manualPerformance_" + key,
				Labels = array,
				AccessibleName = text,
				AccessibleDescription = text2,
				Size = new Size(Theme.S(180), Theme.S(30))
			};
			int used;
			SettingCard card = MakeAutoCard(parent, 6, y, ScrollContentW, FullTextCardHeight(text2, ScrollContentW, picker, 82), text, text2, picker, out used);
			y += used + 8;
			Action sync = delegate
			{
				string text3 = PolicyResolver.RawGlobalValue(key);
				int num = Array.IndexOf(choices, text3);
				picker.Index = ((num >= 0) ? num : 0);
				bool flag2 = text3 != "0" && text3 != "off";
				bool flag3 = ManualPolicySupported(key);
				picker.Enabled = flag3 || flag2;
				SyncManualPerformanceStatus(card, key, flag2, flag3, null);
			};
			picker.IndexChanged = delegate(int index)
			{
				if (!automationUpdating)
				{
					if (index < 0 || index >= choices.Length)
					{
						sync();
					}
					else
					{
						bool flag = SaveManualPolicy(item, choices[index]);
						sync();
						if (!flag)
						{
							ShowManualPerformanceSaveFailure();
						}
					}
				}
			};
			automationSync.Add(sync);
		}

		private bool SaveManualPolicy(PolicyItem item, string value)
		{
			string text = PolicyCatalog.Canonical(item.Key, value);
			if (text == null)
			{
				return false;
			}
			bool flag = ((item.Key == "GmThreadBoostStrategyV1") ? ThreadBoostSelection.Save(text) : ((item.Kind == PolicyValueKind.Bool) ? Settings.Save(item.Key, text == "1") : Settings.SaveStr(item.Key, text)));
			if (flag)
			{
				gameMode.RefreshAutomaticPreferences();
				RefreshAutomationPage();
			}
			return flag;
		}

		private bool SaveManualPerformancePreference(string key, bool value)
		{
			if (!Settings.Save(key, value))
			{
				return false;
			}
			gameMode.RefreshAutomaticPreferences();
			return true;
		}

		private void AddManualPerformanceToggle(Control parent, ref int y, string key, string title, string detail, Func<bool> read, Func<bool, bool> write, Func<bool> supported = null, Func<bool> automaticValue = null, Func<string> execution = null)
		{
			Toggle toggle = MakeSwitch(read(), null);
			toggle.Name = "manualPerformance_" + key;
			toggle.AccessibleName = title;
			toggle.AccessibleDescription = detail;
			int used;
			SettingCard card = MakeAutoCard(parent, 6, y, ScrollContentW, FullTextCardHeight(detail, ScrollContentW, toggle, 82), title, detail, toggle, out used);
			y += used + 8;
			Action sync = delegate
			{
				bool flag2 = read();
				bool flag3 = supported == null || supported();
				toggle.SetSilently(flag2);
				toggle.Enabled = flag3 || flag2;
				SyncManualPerformanceStatus(card, key, flag2, flag3, execution);
			};
			toggle.CheckedChanged += delegate
			{
				if (!automationUpdating)
				{
					if (toggle.Checked && supported != null && !supported())
					{
						sync();
					}
					else
					{
						bool flag = write(toggle.Checked);
						sync();
						if (!flag)
						{
							ShowManualPerformanceSaveFailure();
						}
					}
				}
			};
			automationSync.Add(sync);
		}

		private void SyncManualPerformanceStatus(SettingCard card, string key, bool on, bool supported, Func<string> execution)
		{
			string text = ((execution != null) ? execution() : gameMode.AutomaticExecutionState(key));
			if (key == LaptopPerfMode.PreferenceKey)
			{
				card.SetStatus(text, gameMode.LaptopPerformanceState == "failed" || gameMode.LaptopPerformanceState == "restore-failed"
					? Theme.Warning : gameMode.LaptopPerformanceState == "applied" ? Theme.Accent : Theme.Dim);
				return;
			}
			int num;
			switch (text)
			{
			default:
				num = ((text == "cooldown") ? 1 : 0);
				break;
			case "failed":
			case "restore-failed":
			case "stopped":
				num = 1;
				break;
			}
			bool flag = (byte)num != 0;
			string text2;
			if (flag)
			{
				text2 = GameMode.AutomationStateLabel(text);
			}
			else if (!supported)
			{
				text2 = AutoText("本机不适用", "Not supported here");
			}
			else if (PerformancePolicy.IsIndependentManual(key))
			{
				object obj;
				switch (text)
				{
				default:
					obj = (on ? AutoText("已开启", "On") : AutoText("已关闭", "Off"));
					break;
				case "applied":
				case "configured":
				case "pending-reboot":
				case "restore-pending-reboot":
				case "unverified":
				case "unsupported":
					obj = GameMode.AutomationStateLabel(text);
					break;
				}
				string text3 = (string)obj;
				text2 = text3;
			}
			else
			{
				switch (text)
				{
				case "applied":
				case "configured":
				case "pending-reboot":
				case "restore-pending-reboot":
				case "unverified":
				case "unsupported":
					text2 = GameMode.AutomationStateLabel(text);
					break;
				default:
					text2 = (on ? AutoText("已开启 · 按适用条件执行", "Enabled · runs when eligible") : AutoText("已关闭", "Off"));
					break;
				}
			}
			if (PerformancePolicy.IsGamePriorityOption(key) && !gameMode.AutomationPolicySnapshot.BoostGame && !flag)
			{
				text2 = AutoText("游戏资源优先级总开关已关闭", "Game resource priorities are switched off");
			}
			bool flag2 = flag || text == "pending-reboot" || text == "restore-pending-reboot" || text == "unverified";
			card.SetStatus(text2, flag2 ? Theme.Warning : ((text == "applied" && supported) ? Theme.Accent : Theme.Dim));
		}

		private void ShowManualPerformanceSaveFailure()
		{
			PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.performance.savefail"));
		}

		private bool ManualPolicySupported(string key)
		{
			switch (key)
			{
			case "NvMaxPerf":
			case "NvShaderCache":
			case "NvLowLat":
				return NvApi.Available;
			case "GmIntelEnduranceV1":
				if (environmentObservation.Snapshot != null)
				{
					return environmentObservation.Snapshot.IntelEnduranceSupported;
				}
				return false;
			case "PowerPlanOn":
				return !PowerPlan.NoSwitchSelected;
			default:
				return true;
			}
		}

		private static string ManualPolicyDescription(string key)
		{
			switch (key)
			{
			case "GmSuppress":
				return Lang.T("background.governance.detail");
			case "GmBoost":
				return AutoText("统一调整游戏的 CPU、内存、磁盘 I/O 与 GPU 优先级，切出游戏保持，退局恢复。", "Adjusts game CPU, memory, disk I/O and GPU priorities together. Kept when you switch away; restored when the session ends.");
			case "GmBoostCpuV1":
				return AutoText("对局期间保持游戏 CPU 高优先级，持续满载或无线程提优也不降档。由资源优先级总开关控制，退局恢复原值。", "Keeps game CPU priority High during the session, including sustained full load or inactive thread boosting. Requires game resource priorities; restores the original priority on exit.");
			case "GmBoostMemoryV1":
				return AutoText("保持游戏内存优先级为标准 5，通常已是默认值。不增加内存，也不锁定页面。", "Keeps game memory priority at Normal 5, usually the default. Adds no RAM and does not lock pages.");
			case "GmBoostIoV1":
				return AutoText("请求游戏磁盘 I/O 高优先级，需权限及读回确认。失败不影响其他优先级。", "Requests High game disk I/O priority after privilege and readback checks. Failure leaves other priorities unchanged.");
			case "GmBoostGpuV1":
				return AutoText("提高游戏 GPU 调度优先级，需驱动支持及读回确认。退局恢复原值。", "Raises game GPU scheduling priority with driver support and readback checks. Restores on exit.");
			case "GmThreadBoostStrategyV1":
				return AutoText("开启后按游戏自动选择：认得出画面提交线程就提优渲染关键线程，认不出（如 Vulkan、OpenGL）改提最忙线程。仅手动执行，退局恢复。可与逐游戏关键线程硬绑定同时生效，组合效果未测。", "Picks per game: boosts the render-critical threads when the frame-submission thread is recognized, otherwise (e.g. Vulkan, OpenGL) the busiest thread. Manual only; restores on exit. Can run alongside per-game candidate thread affinity; the combination is untested.");
			case "PowerPlanOn":
				return AutoText("对局使用所选电源计划，“不切换”时停用。托管在智能／竞技中共用参数，仍按交流／电池、CPU 拓扑及设备特征适配，场景、自动升档和切出不改变参数；掌机不接管。指定已有计划只切换，退局恢复原计划；实验室覆盖独立恢复。", "Uses the selected match power plan unless Don't switch is chosen. Smart and Competitive share managed parameters, adapted to AC/battery power, CPU topology and device characteristics. Scene changes, automatic escalation and switching away do not change those parameters. Handhelds bypass power management. Existing plans are only activated and restored after the session; laboratory overrides retain separate recovery.");
			case "GmPauseDl":
				return AutoText("对局暂停 Windows 传递优化下载，结束恢复。", "Pauses Windows Delivery Optimization downloads during a match, then restores.");
			case "GmPauseUpdate":
				return AutoText("对局暂停 Windows 更新活动，结束恢复。", "Pauses Windows Update activity during a match, then restores.");
			case "GmPauseMaintenanceV1":
				return AutoText("对局暂停自动维护，结束恢复。", "Pauses automatic maintenance during a match, then restores.");
			case "GmAwake":
				return AutoText("游戏前台时阻止屏幕与电脑自动休眠。", "Prevents automatic display and system sleep while the game is foreground.");
			case "GmDwmBoost":
				return AutoText("调整桌面合成调度，效果依显示场景而异。", "Adjusts desktop compositor scheduling; results depend on the display workload.");
			case "GmIntelEnduranceV1":
				return AutoText("支持的 Intel 笔记本关闭图形续航限制，可能增加功耗。", "Disables supported Intel laptop graphics endurance limits; may increase power use.");
			case "NvMaxPerf":
				return AutoText("为游戏配置 NVIDIA 最高性能偏好，下次启动核实。", "Configures NVIDIA maximum performance for games; verify on next launch.");
			case "NvShaderCache":
				return AutoText("配置 NVIDIA 着色器缓存容量，下次启动核实。", "Configures NVIDIA shader cache capacity; verify on next launch.");
			case "NvLowLat":
				return AutoText("NVIDIA 预渲染限制；适用性与帧率取舍因游戏而异。", "NVIDIA pre-render limit; support and throughput tradeoffs vary by game.");
			default:
				return AutoText("由你决定是否启用，按适用条件执行。", "Choose whether to enable this feature; it runs when eligible.");
			}
		}

		private void RefreshAutomationPage()
		{
			if (automationUpdating || performanceTabs == null || performanceTabs.IsDisposed || performanceTabs.Parent != pagePolicy)
			{
				return;
			}
			automationUpdating = true;
			try
			{
				using (PolicyResolver.BeginReadScope())
				{
					foreach (Action item in automationSync)
					{
						item();
					}
					SyncPerformanceEnvironmentStatus();
				}
				SyncConfigurationPresetControls();
				LayoutPerformanceViewport();
			}
			finally
			{
				automationUpdating = false;
			}
		}

		private void BuildCoreEditor(int x, int y, int w)
		{
			if (coreDraftProfile != null && coreEditMatrix != null && coreIsolationToggle != null)
			{
				coreRebuildDraft = new CoreEditorDraftSnapshot
				{
					Profile = coreDraftProfile,
					Expected = coreDraftExpected,
					Topology = coreDraftTopology,
					Error = coreSaveError,
					Mask = coreEditMatrix.Selected,
					Isolation = coreIsolationToggle.Checked
				};
			}
			coreDraftProfile = null;
			coreEditor = new CoreWorkspaceSurface();
			coreScrollPanel.Controls.Add(coreEditor);
			coreEditor.Name = "coreManualEditor";
			coreEditorTitle = CardLabel(coreEditor, "", 18, 12, w - 120, 26, 11f, true, Theme.Fg);
			coreEnableToggle = new Toggle
			{
				Name = "coreSchedulingEnabled",
				AccessibleName = Lang.T("nav.corescheduling")
			};
			coreEnableToggle.SetBounds(Theme.S(w - 66), Theme.S(10), Theme.S(46), Theme.S(30));
			coreEnableToggle.CheckedChanged += delegate
			{
				if (coreDraftProfile != null)
				{
					GameProfile profile = FindLibraryProfile(coreDraftProfile);
					coreSaveError = SaveCoreConfigurationWithRender(profile, ManualCorePlan.Raw(profile), ManualCorePlan.Parse(ManualCorePlan.Raw(profile)), coreEnableToggle.Checked);
					SyncCoreEditor(CorePageProfile());
				}
			};
			coreIdentity.Controls.Add(coreEnableToggle);
			coreEditorHelp = CardLabel(coreEditor, "", 18, 46, w - 36, 40, 9f, false, Theme.Dim);
			coreEditorHelp.AutoEllipsis = false;
			coreEditorRuntime = CardLabel(coreEditor, "", 18, 0, w - 36, 40, 8.5f, true, Theme.Dim);
			coreEditorRuntime.Name = "coreManualRuntime";
			coreEditorRuntime.AutoEllipsis = false;
			corePlanPreview = new PlanComparison
			{
				Name = "CorePlanPreview",
				Width = Theme.S(w - 36)
			};
			corePlanPreview.Compact = true;
			coreRightRail.Controls.Add(corePlanPreview);
			coreEvidenceToggle = new PerformanceActionButton("")
			{
				Name = "CoreEvidenceToggle"
			};
			coreEvidenceToggle.SetBounds(Theme.S(18), 0, Theme.S(220), Theme.S(24));
			coreEvidenceToggle.Click += delegate
			{
				ShowCoreInformation(PerformancePolicy.Text("绑定证据", "Affinity evidence"), coreEditorRuntime.Text);
			};
			coreRightRail.Controls.Add(coreEvidenceToggle);
			BuildCoreShortcuts(w - 36);
			coreEditMatrix = new CoreMatrix
			{
				Name = "coreManualMatrix",
				Editable = true,
				MarkExclusive = false,
				BackColor = Theme.Card,
				PrimaryTag = Lang.T("schedule.manual.selected"),
				AccessibleName = Lang.T("schedule.manual.select")
			};
			coreEditMatrix.SetBounds(Theme.S(18), 0, Theme.S(w - 36), Theme.S(40));
			coreEditMatrix.Height = coreEditMatrix.LayoutFor(coreEditMatrix.Width);
			coreEditMatrix.SelectionChanged += delegate
			{
				coreDraftTopology = ManualCorePlan.CurrentStamp();
				SaveCoreDraft();
			};
			coreMatrixViewport = new DBPanel
			{
				Name = "coreMatrixViewport",
				BackColor = Theme.Card,
				AutoScroll = true
			};
			Native.Dark(coreMatrixViewport);
			coreEditor.Controls.Add(coreMatrixViewport);
			coreMatrixViewport.Controls.Add(coreEditMatrix);
			coreEditMatrix.FocusedCellChanged += RevealCoreCell;
			coreEditMatrix.GotFocus += delegate
			{
				RevealCoreCell(coreEditMatrix.FocusedCellBounds);
			};
			coreIsolationToggle = new Toggle
			{
				Name = "coreManualIsolation",
				AccessibleName = Lang.T("schedule.manual.isolation")
			};
			coreIsolationToggle.SetBounds(Theme.S(18), 0, Theme.S(46), Theme.S(30));
			coreIsolationToggle.CheckedChanged += delegate
			{
				SaveCoreDraft();
			};
			coreRightRail.Controls.Add(coreIsolationToggle);
			coreEditorIsolation = CardLabel(coreRightRail, Lang.T("schedule.manual.isolation"), 76, 0, w - 94, 30, 9f, true, Theme.Fg);
			coreEditorIsolation.AutoEllipsis = false;
			coreEditorResult = CardLabel(coreRightRail, "", 18, 0, w - 36, 36, 8.5f, false, Theme.Dim);
			coreEditorResult.Name = "coreManualResult";
			coreEditorResult.AutoEllipsis = false;
		}

		private void SyncCoreEditor(GameProfile profile)
		{
			coreEditor.Visible = profile != null;
			if (profile == null)
			{
				coreDraftProfile = null;
				coreRebuildDraft = null;
				return;
			}
			if (coreRebuildDraft != null && curPage == pageCoreScheduling)
			{
				CoreEditorDraftSnapshot coreEditorDraftSnapshot = coreRebuildDraft;
				coreRebuildDraft = null;
				if (string.Equals(coreEditorDraftSnapshot.Profile, profile.Id, StringComparison.OrdinalIgnoreCase))
				{
					coreDraftProfile = profile.Id;
					coreDraftExpected = coreEditorDraftSnapshot.Expected;
					coreDraftTopology = coreEditorDraftSnapshot.Topology;
					coreSaveError = coreEditorDraftSnapshot.Error;
					coreEditMatrix.ShowSelectionImmediately(coreEditorDraftSnapshot.Mask);
					coreIsolationToggle.SetSilently(coreEditorDraftSnapshot.Isolation);
				}
			}
			coreEnableToggle.SetSilently(PolicyResolver.For(profile).AutoCoresAllowed);
			coreEnableToggle.Enabled = PolicyResolver.For(profile).ManualCores != null;
			coreEditorTitle.Text = PerformancePolicy.Text("游戏核心范围", "Game CPU range");
			coreEditMatrix.DisabledMask = AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask;
			string text = ManualCorePlan.Raw(profile);
			if (coreDraftProfile != profile.Id || coreDraftExpected != text)
			{
				bool flag = coreDraftProfile == profile.Id;
				coreDraftProfile = profile.Id;
				coreDraftExpected = text;
				RestoreCoreSelection();
				coreSaveError = (flag ? "schedule.manual.changed" : null);
			}
			CoreDraftChanged();
		}

		private void RestoreCoreSelection()
		{
			ManualCorePlan manualCorePlan = ManualCorePlan.Parse(coreDraftExpected);
			coreDraftTopology = ((manualCorePlan != null) ? manualCorePlan.Topology : ManualCorePlan.CurrentStamp());
			coreEditMatrix.ShowSelectionImmediately((manualCorePlan != null) ? manualCorePlan.GameMask : 0);
			coreIsolationToggle.SetSilently(manualCorePlan != null && manualCorePlan.Isolation);
		}

		private ManualCorePlan CoreDraftPlan()
		{
			ManualCorePlan manualCorePlan = ManualCorePlan.Parse(coreDraftExpected);
			return new ManualCorePlan(coreEditMatrix.Selected, coreIsolationToggle.Checked, coreDraftTopology, manualCorePlan != null && manualCorePlan.HardAffinity);
		}

		private void CoreDraftChanged()
		{
			ManualCorePlan manualCorePlan = CoreDraftPlan();
			string text = manualCorePlan.ValidateCurrent(AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask);
			bool flag = manualCorePlan.Encode() != coreDraftExpected;
			coreEditorResult.Text = ((coreSaveError == "schedule.manual.changed") ? Lang.T(coreSaveError) : ((coreSaveError != null) ? Lang.F("schedule.manual.autofailed", Lang.T(coreSaveError)) : ((text == null) ? Lang.T(coreEnableToggle.Checked ? "schedule.manual.autosaved" : "schedule.manual.savedoff") : ((coreDraftExpected == null && coreEditMatrix.Selected == 0) ? Lang.T("schedule.manual.required") : Lang.F(flag ? "schedule.manual.unsaved" : "schedule.manual.invalid", Lang.T(text))))));
			coreEditorResult.ForeColor = ((coreSaveError != null || (text != null && (coreEditMatrix.Selected != 0 || coreDraftExpected != null))) ? Theme.Warning : Theme.Dim);
			coreEditorResult.Visible = coreSaveError != null || text != null;
			coreEditorHelp.Text = Lang.T("schedule.manual.help");
			SyncCoreRuntimePresentation();
			coreIsolationToggle.Enabled = coreIsolationToggle.Checked || new ManualCorePlan(coreEditMatrix.Selected, true, coreDraftTopology).ValidateCurrent(AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask) == null;
			SyncCoreShortcuts();
			LayoutCoreEditor();
			if (coreStatusPanel != null)
			{
				LayoutCorePage();
			}
		}

		private void SaveCoreDraft()
		{
			if (coreDraftProfile == null)
			{
				return;
			}
			coreSaveError = null;
			ManualCorePlan manualCorePlan = CoreDraftPlan();
			if (manualCorePlan.ValidateCurrent(AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask) != null)
			{
				CoreDraftChanged();
				return;
			}
			if (manualCorePlan.Encode() == coreDraftExpected)
			{
				CoreDraftChanged();
				return;
			}
			GameProfile profile = FindLibraryProfile(coreDraftProfile);
			bool enabled = profile != null && ManualCorePlan.Parse(coreDraftExpected) != null && PolicyResolver.For(profile).AutoCoresAllowed;
			string text = SaveCoreConfigurationWithRender(profile, coreDraftExpected, manualCorePlan, enabled);
			if (text == null)
			{
				coreDraftExpected = manualCorePlan.Encode();
			}
			else
			{
				RestoreCoreSelection();
			}
			SyncCoreEditor(CorePageProfile());
			coreSaveError = text;
			CoreDraftChanged();
		}

		private void LayoutCoreEditor()
		{
			LayoutCoreWorkspace();
		}

		private void SyncCoreRuntimePresentation()
		{
			AutoCoreStatus status = AutoCores.Status;
			bool flag = status.ProfileId == coreDraftProfile && status.Watching;
			ManualCorePlan manualCorePlan = ManualCorePlan.Parse(coreDraftExpected);
			string text = ((manualCorePlan == null) ? PerformancePolicy.Text("尚未保存游戏核心范围。", "No game CPU range saved.") : (PerformancePolicy.Text("已保存：", "Saved: ") + CpuTopology.DescribeMask(manualCorePlan.GameMask)));
			if (!flag)
			{
				text = text + "\n" + PerformancePolicy.Text("本局硬绑定：等待游戏进程验证。", "Session hard affinity: awaiting game-process verification.");
			}
			else if (status.Blocked != null)
			{
				text = text + "\n" + PerformancePolicy.Text("本局硬绑定已停用：", "Session hard affinity stopped: ") + Lang.T(status.Blocked);
			}
			else if (!status.Manual || status.ExpectedMask == 0)
			{
				text = text + "\n" + PerformancePolicy.Text("本局未采用手动选核；已保存的修改下次启动游戏生效。", "This session has no manual CPU range; saved changes apply on the next game launch.");
			}
			else
			{
				text = text + "\n" + PerformancePolicy.Text("本局目标：", "Session target: ") + CpuTopology.DescribeMask(status.ExpectedMask);
				string text2 = text;
				text = text2 + "\n" + PerformancePolicy.Text("最近回读：", "Last readback: ") + (status.AffinityRead ? CpuTopology.DescribeMask(status.ObservedMask) : PerformancePolicy.Text("尚未确认", "Unconfirmed")) + (status.AffinityVerified ? " · " + AutoCores.AffinityProtectionText(status) : PerformancePolicy.Text(" · 硬绑定未生效", " · hard affinity not active"));
				if (manualCorePlan == null || manualCorePlan.GameMask != status.ExpectedMask || manualCorePlan.Isolation != status.ManualIsolation)
				{
					text = text + "\n" + PerformancePolicy.Text("新保存的范围／隔离设置下次启动游戏生效。", "New CPU/isolation settings apply on the next game launch.");
				}
			}
			coreEditorRuntime.Text = text;
			coreEditorRuntime.ForeColor = ((flag && (status.Blocked != null || status.Pending)) ? Theme.Warning : ((flag && status.AffinityVerified) ? Theme.Accent : Theme.Dim));
			Color currentTone = Theme.Dim;
			string currentValue;
			string currentDetail;
			if (!flag)
			{
				currentValue = PerformancePolicy.Text("尚未确认", "Not verified yet");
				currentDetail = PerformancePolicy.Text("游戏运行后，读取并核对实际核心范围。", "The actual CPU range is checked once the game runs.");
			}
			else if (status.Blocked != null)
			{
				currentValue = PerformancePolicy.Text("硬绑定已停用", "Hard affinity stopped");
				currentDetail = Lang.T(status.Blocked);
				currentTone = Theme.Warning;
			}
			else if (!status.Manual || status.ExpectedMask == 0)
			{
				currentValue = PerformancePolicy.Text("本局未采用手动范围", "No manual range this session");
				currentDetail = PerformancePolicy.Text("保存的方案不会追改本局。", "Saved changes do not replace this session's plan.");
			}
			else if (status.AffinityRead && status.AffinityVerified)
			{
				currentValue = CpuTopology.DescribeMask(status.ObservedMask);
				currentDetail = AutoCores.AffinityProtectionText(status);
				currentTone = Theme.Accent;
			}
			else
			{
				currentValue = PerformancePolicy.Text("尚未确认生效", "Activation not verified");
				currentDetail = PerformancePolicy.Text("目标：", "Target: ") + CpuTopology.DescribeMask(status.ExpectedMask);
				currentTone = Theme.Warning;
			}
			string savedValue = ((manualCorePlan == null) ? PerformancePolicy.Text("尚未选择", "No range selected") : CpuTopology.DescribeMask(manualCorePlan.GameMask));
			string savedDetail = ((manualCorePlan == null) ? PerformancePolicy.Text("点击下方核心，选择会自动保存。", "Select cores below to save a range.") : ((!coreEnableToggle.Checked) ? PerformancePolicy.Text("方案已保存，核心调度当前关闭。", "Plan saved; core scheduling is switched off.") : (PerformancePolicy.Text("下次启动游戏使用此范围", "Applied on the next game launch") + (manualCorePlan.Isolation ? PerformancePolicy.Text(" · 含核心隔离", " · with isolation") : ""))));
			string text3 = ((manualCorePlan == null) ? null : manualCorePlan.ValidateCurrent(AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask));
			if (text3 != null)
			{
				savedDetail = PerformancePolicy.Text("需调整：", "Needs adjustment: ") + Lang.T(text3);
			}
			corePlanPreview.SetState(PerformancePolicy.Text("本局实际范围", "Current session"), currentValue, currentDetail, currentTone, PerformancePolicy.Text("已保存范围", "Saved range"), savedValue, savedDetail, (text3 != null) ? Theme.Warning : Theme.Fg);
		}

		private GameProfile CorePageProfile()
		{
			if (string.IsNullOrEmpty(coreProfileId))
			{
				return null;
			}
			foreach (GameProfile profile in gameMode.GetProfiles())
			{
				if (string.Equals(profile.Id, coreProfileId, StringComparison.OrdinalIgnoreCase))
				{
					return profile;
				}
			}
			return null;
		}

		private void OpenGameCoreScheduling(string profileId)
		{
			coreProfileId = profileId;
			if (CorePageProfile() == null)
			{
				coreProfileId = null;
				return;
			}
			nav.Select(12);
			coreScrollPanel.AutoScrollPosition = Point.Empty;
			SyncCorePage();
		}

		private static string CpuDisplayName()
		{
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0"))
				{
					string text = ((registryKey == null) ? null : (registryKey.GetValue("ProcessorNameString") as string));
					if (!string.IsNullOrEmpty(text))
					{
						return Regex.Replace(text, "\\s+", " ").Trim();
					}
				}
			}
			catch
			{
			}
			return Lang.T("core.topo.unknown");
		}

		private static string TopologyLine()
		{
            if (CpuTopology.MultiGroup)
                return PerformancePolicy.Text("多处理器组 · 全机逻辑核、物理核与核心类型未确认", "Multiple processor groups · total logical/physical core counts and types unconfirmed");
			int logical = CpuTopology.CountSetBits(CpuTopology.AllMask);
			if (!CoreMatrix.TopologyCountsConfirmed)
				return logical + PerformancePolicy.Text(" 个可用逻辑核 · 物理核数与 SMT 状态未确认 · 核心类型未确认",
					" available logical CPUs · physical core count and SMT state unconfirmed · core types unconfirmed");
			ulong[] cores = CpuTopology.PhysicalCoreMasks();
			int smt = 0;
			foreach (ulong core in cores)
				if (CpuTopology.CountSetBits(core & CpuTopology.AllMask) > 1) smt++;
			string text = cores.Length + PerformancePolicy.Text(" 物理核 · ", " physical cores · ") + logical
				+ PerformancePolicy.Text(" 逻辑核 · 当前 ", " logical CPUs · ") + smt
				+ PerformancePolicy.Text(" 个物理核含多个逻辑线程", " physical cores currently have multiple logical threads");
			if (!CpuTopology.CoreTypesConfirmed)
				text += PerformancePolicy.Text(" · 核心类型未确认", " · core types unconfirmed");
			return text;
		}

		private void BuildCoreSchedulingPage()
		{
			BuildCoreWorkspace();
		}

		private void ClearSuspectReservedCores()
		{
			ulong num = LegacyReservedCores.Suspect();
			if (num != 0 && PaviseDialog.Confirm(this, Lang.T("schedule.auto.reserved.clear"), Lang.F("schedule.auto.reserved.confirm", CpuTopology.DescribeMask(num)), DlgKind.Warn))
			{
				if (!LegacyReservedCores.ClearSuspect())
				{
					PaviseDialog.Warn(this, Lang.T("schedule.auto.reserved.clear"), Lang.T("schedule.auto.reserved.failed"));
				}
				SyncCorePage();
			}
		}

		private void LayoutCoreViewport()
		{
			LayoutCoreWorkspace();
		}

		private void LayoutCorePage()
		{
			LayoutCoreWorkspace();
		}

		private static int WrappedHeight(Label label)
		{
			return TextRenderer.MeasureText(label.Text, label.Font, new Size(Math.Max(1, label.Width), int.MaxValue), TextFormatFlags.TextBoxControl | TextFormatFlags.WordBreak).Height + Theme.S(2);
		}

		private void SyncCorePage()
		{
			if (lblCoreState != null && !lblCoreState.IsDisposed)
			{
				coreWorkspaceSyncing = true;
				try
				{
					AutoCoreStatus status = AutoCores.Status;
					GameProfile gameProfile = CorePageProfile();
					SyncCoreEditor(gameProfile);
					bool flag = status.Pending || CoreIsolationClient.State == "schedule.isolation.pending";
					bool flag2 = gameProfile != null && status.ProfileId == gameProfile.Id;
					lblCoreState.Text = Lang.T(flag ? "schedule.auto.state.pending" : ((flag2 && status.Blocked != null) ? "schedule.auto.state.blocked" : "schedule.manual.mode"));
					lblCoreReason.Text = Lang.T(flag ? "schedule.isolation.pending" : ((flag2 && status.Blocked != null) ? status.Blocked : ((gameProfile == null) ? "schedule.manual.pickgame" : "schedule.manual.required")));
					lblCoreRule.Text = Lang.T("schedule.manual.rule");
					lblCoreTelemetry.Text = "";
					ulong num = AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask;
					ulong num2 = ((num != 0) ? LegacyReservedCores.PendingReboot(num) : 0);
					ulong num3 = ((num2 == 0) ? LegacyReservedCores.Suspect() : 0);
					bool flag3 = num3 != 0 && (num3 & ~num) == 0;
					lblCoreReserved.Text = ((num2 != 0) ? Lang.F("schedule.auto.reserved.legacy", CpuTopology.DescribeMask(num2)) : (flag3 ? Lang.F("schedule.auto.reserved.suspect", CpuTopology.DescribeMask(num3)) : ((num != 0) ? Lang.F("schedule.auto.reserved.other", CpuTopology.DescribeMask(num)) : "")));
					btnCoreClearReserved.Visible = flag3;
					ManualCorePlan manualCorePlan = ((gameProfile != null) ? PolicyResolver.For(gameProfile).ManualCores : null);
					coreMatrix.PrimaryTag = Lang.T("schedule.manual.selected");
					coreMatrix.DisabledMask = num;
					coreMatrix.Selected = ((manualCorePlan != null) ? manualCorePlan.GameMask : 0);
					coreMatrix.SchedulingGameMask = ((manualCorePlan != null) ? manualCorePlan.GameMask : 0);
					coreMatrix.SchedulingIsolationMask = ((flag2 && status.Manual && status.ManualIsolation && status.Engaged && status.Plan != null) ? status.Plan.Exclusive : 0);
					lblCorePlan.Text = ((manualCorePlan != null) ? Lang.F("schedule.manual.plan", CpuTopology.DescribeMask(manualCorePlan.GameMask)) : Lang.T("schedule.manual.required"));
				}
				finally
				{
					coreWorkspaceSyncing = false;
				}
				LayoutCorePage();
			}
		}

		private void BuildCoreShortcuts(int width)
		{
			coreQuickPanel = new Panel
			{
				Name = "coreQuickSelection",
				BackColor = Theme.Card
			};
			coreQuickPanel.SetBounds(Theme.S(18), 0, Theme.S(width), Theme.S(30));
			coreEditor.Controls.Add(coreQuickPanel);
            if (CpuTopology.MultiGroup) return;
			AddCoreShortcut("all", Lang.T("schedule.quick.all"), CpuTopology.AllMask, 0);
			if (CpuTopology.Hybrid && CpuTopology.CoreTypesConfirmed && CoreMatrix.TopologyCountsConfirmed)
			{
				AddCoreShortcut("perf", Lang.T("schedule.quick.perf"), CpuTopology.PerfMask, 0);
				AddCoreShortcut("eff", Lang.T("schedule.quick.eff"), CpuTopology.EffMask, 0);
			}
			ulong[] array = CpuTopology.DieMasks();
			ulong num = CpuTopology.CacheHeavyMask();
			for (int i = 0; i < array.Length; i++)
			{
				AddCoreShortcut("ccd" + i, Lang.F(((array[i] & num) != 0) ? "core.band.ccd.cache" : "core.band.ccd", i), array[i], 0);
			}
			ulong[] array2 = CpuTopology.PhysicalCoreMasks();
			foreach (ulong num2 in array2)
			{
				if (CpuTopology.CountSetBits(num2 & CpuTopology.AllMask) > 1)
				{
					AddCoreShortcut("noSmt", Lang.T("schedule.quick.nosmt"), 0uL, 1);
					AddCoreShortcut("withSmt", Lang.T("schedule.quick.smt"), 0uL, 2);
					break;
				}
			}
		}

		private void AddCoreShortcut(string name, string title, ulong mask, int threads)
		{
			mask &= CpuTopology.AllMask;
			if (threads == 0 && mask == 0)
			{
				return;
			}
			foreach (Control control in coreQuickPanel.Controls)
			{
				CoreQuickChoice coreQuickChoice = (CoreQuickChoice)control.Tag;
				if (threads == 0 && coreQuickChoice.Threads == 0 && coreQuickChoice.Mask == mask)
				{
					return;
				}
			}
			CoreQuickChoice choice = new CoreQuickChoice
			{
				Mask = mask,
				Threads = threads
			};
			PillButton pillButton = new PillButton(title);
			pillButton.Name = "coreQuick_" + name;
			pillButton.Tag = choice;
			pillButton.AccessibleName = title;
			PillButton pillButton2 = pillButton;
			pillButton2.Size = new Size(Math.Max(Theme.S(72), TextRenderer.MeasureText(title, Theme.UI(9f, true)).Width + Theme.S(28)), Theme.S(30));
			pillButton2.Click += delegate
			{
				if (coreDraftProfile != null)
				{
					coreEditMatrix.Selected = CoreShortcutTarget(choice);
					coreDraftTopology = ManualCorePlan.CurrentStamp();
					SaveCoreDraft();
				}
			};
			coreQuickPanel.Controls.Add(pillButton2);
		}

		private ulong CoreShortcutTarget(CoreQuickChoice choice)
		{
			ulong num = CpuTopology.AllMask & ~(AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask);
			if (choice.Threads != 0)
			{
				return CoreShortcutThreads(coreEditMatrix.Selected, num, CpuTopology.PhysicalCoreMasks(), choice.Threads == 2);
			}
			return choice.Mask & num;
		}

		internal static ulong CoreShortcutThreads(ulong selected, ulong available, ulong[] physical, bool includeSiblings)
		{
			ulong num = 0uL;
			foreach (ulong num2 in physical)
			{
				ulong num3 = num2 & selected & available;
				if (num3 != 0)
				{
					num |= (includeSiblings ? (num2 & available) : (num3 & (~num3 + 1)));
				}
			}
			return num;
		}

		private void SyncCoreShortcuts()
		{
			if (coreQuickPanel == null)
			{
				return;
			}
			foreach (PillButton control in coreQuickPanel.Controls)
			{
				CoreQuickChoice coreQuickChoice = (CoreQuickChoice)control.Tag;
				ulong num = CoreShortcutTarget(coreQuickChoice);
				control.Enabled = CpuTopology.CountSetBits(num) >= 1 && (coreQuickChoice.Threads == 0 || num != coreEditMatrix.Selected);
				BtnKind btnKind = ((coreQuickChoice.Threads == 0 && num != 0 && num == coreEditMatrix.Selected) ? BtnKind.Primary : BtnKind.Normal);
				if (control.Kind != btnKind)
				{
					control.Kind = btnKind;
					control.Invalidate();
				}
			}
		}

		private void LayoutCoreShortcuts()
		{
			int num = 0;
			int num2 = 0;
			int num3 = Theme.S(6);
			int num4 = Theme.S(30);
			foreach (Control control in coreQuickPanel.Controls)
			{
				if (num > 0 && num + control.Width > coreQuickPanel.Width)
				{
					num = 0;
					num2 += num4 + num3;
				}
				control.Location = new Point(num, num2);
				num += control.Width + num3;
			}
			coreQuickPanel.Top = Theme.S(coreWorkspaceCompact ? 38 : 64);
			coreQuickPanel.Height = num2 + num4;
		}

		private static string ThreadUi(string zh, string en)
		{
			return PerformancePolicy.Text(zh, en);
		}

		private void BuildCriticalThreadEditor(Control parent, int width)
		{
			criticalThreadEditor = new CoreThreadSurface();
			parent.Controls.Add(criticalThreadEditor);
			criticalThreadProfile = (criticalThreadExpected = (criticalThreadError = null));
			criticalThreadEditor.Name = "CriticalThreadEditor";
			criticalThreadTitle = CardLabel(criticalThreadEditor, ThreadUi("渲染线程亲和性", "Render thread affinity"), 18, 12, width - 104, 30, 11f, true, Theme.Fg);
			criticalThreadEnabled = new FamilySuppressionSwitch(true)
			{
				Name = "libraryThreadAffinityToggle",
				DetailsWidth = 62,
				Text = ThreadUi("渲染线程亲和性", "Render thread affinity"),
				IconKey = "threads"
			};
			criticalThreadEnabled.Click += delegate
			{
				if (!criticalThreadSyncing)
				{
					SaveCriticalThreadSwitch(!criticalThreadEnabled.Checked);
				}
			};
			criticalThreadEditor.Controls.Add(criticalThreadEnabled);
			criticalThreadHelp = CardLabel(criticalThreadEditor, ThreadUi("逐游戏，默认关闭。通过独立弹窗选择自动或手动落核；手动只指定一个逻辑 CPU，仍自动识别关键线程。候选跟随核心调度范围，未开核心调度则显示全核；范围冲突在保存前处理。开启、换核与范围修改下局生效，关闭立即撤回。至少选中三个不同物理核，每核可只选一个逻辑线程，无需补全超线程。打开后：游戏在前台时，把一个持续占用至少 30% 单核的关键线程硬绑定到独立物理核心（渲染线程优先，需 DirectX 画面提交事件；识别不到时取持续 30 秒最忙且明显领先的线程，几个线程负载接近时视为工作线程池，交给系统调度）。其他持续接近跑满（至少 70%）的线程只设为优先核，核心忙时系统仍可把它挪走。各线程用不同物理核，数量按处理器拓扑自动定（最多 4 个；大小核只用性能核，优先优选核与 X3D 大缓存 CCD，尽量避开 CPU 0 所在核，至少给其他线程留两个物理核）；自动模式先让当前其他游戏线程避开该整颗物理核（含超线程同胞），全部回读通过后绑定关键线程并隔离普通外部进程；至少给工作线程保留两个物理核。新线程在下一次核验后接管；权限不足或线程已有自定义分配时撤回，不强制覆盖。旧手动规则仍按保存策略执行。切出游戏保留，退局时还原；游戏主动改回分配时让出，连续无法核验或候选反复更换时恢复并停止本局重绑。同一线程不叠加额外线程提优。失败后本局停止重新绑定。不隔离中断/DPC；外部程序自行硬绑定仍可能进入。新线程接管有间隔，不保证帧率收益。", "Per game, off by default. Use the separate dialog for Automatic or Manual CPU selection. Manual chooses one logical CPU while thread identification remains automatic. Candidates follow core scheduling, or all CPUs when it is off; conflicts must be resolved before saving. Enabling, CPU and range edits apply next session; disabling withdraws immediately. Select at least three distinct physical cores; one logical processor per core is enough, without adding SMT siblings. When on: while the game is in front, one critical thread that keeps at least 30% of one core busy gets hard affinity on its own physical core (the render thread first, from DirectX present events; without them, the busiest thread after 30 seconds when it clearly leads; several threads with similar loads are treated as a worker pool and left to the scheduler). Other threads that stay near saturation (at least 70%) only receive a preferred core, and Windows may still move them while that core is busy. Each thread uses a different physical core; the count follows the CPU topology (up to 4; hybrid CPUs use performance cores only; favored cores and the large-cache X3D CCD come first, the core of CPU 0 is avoided where possible, and two physical cores stay free for other threads). Automatic mode first moves the current game workers off the entire selected physical core, including its SMT sibling. Once every worker is verified, it binds the critical thread and reserves the core against ordinary external processes. At least two physical cores remain for workers. New threads are admitted at the next audit; missing permissions or custom placement withdraw the operation without forcing it. Older manual rules retain their saved policy. Switching away keeps the placement; it is restored when the session ends. Placement yields when the game changes it; repeated verification failures or candidate changes restore the original state and stop rebinding for the session. Extra priority boosts are not layered onto the same thread. Failed placement stops rebinding for the session. Interrupts/DPCs are not isolated, and an external program with explicit hard affinity may still enter. New thread admission has a delay; FPS gains are not guaranteed."), 18, 50, width - 36, 80, 9f, false, Theme.Dim);
			criticalThreadHelp.AutoEllipsis = false;
			criticalThreadCompanion = CardLabel(criticalThreadEditor, "", 18, 0, width - 36, 40, 9f, false, Theme.Dim);
			criticalThreadCompanion.Name = "CriticalThreadCompanion";
			criticalThreadCompanion.AutoEllipsis = false;
			criticalThreadResult = CardLabel(criticalThreadEditor, "", 18, 0, width - 36, 40, 9f, false, Theme.Dim);
			criticalThreadResult.Name = "CriticalThreadSaveResult";
			criticalThreadResult.AutoEllipsis = false;
			criticalThreadRuntime = CardLabel(criticalThreadEditor, "", 18, 0, width - 36, 60, 9f, false, Theme.Dim);
			criticalThreadRuntime.Name = "CriticalThreadRuntime";
			criticalThreadRuntime.AutoEllipsis = false;
			Label label = criticalThreadTitle;
			Label label2 = criticalThreadHelp;
			Label label3 = criticalThreadCompanion;
			bool flag2 = (criticalThreadRuntime.Visible = false);
			bool flag4 = (label3.Visible = flag2);
			bool visible = (label2.Visible = flag4);
			label.Visible = visible;
			criticalThreadInfo = new PerformanceInfoButton
			{
				Name = "LibraryThreadAffinityInfo",
				AccessibleName = ThreadUi("渲染线程亲和性说明与状态", "Render thread affinity details and status")
			};
			criticalThreadInfo.Click += delegate
			{
				string body = criticalThreadHelp.Text + "\r\n\r\n" + criticalThreadResult.Text + "\r\n\r\n" + criticalThreadCompanion.Text + "\r\n\r\n" + criticalThreadRuntime.Text;
				PaviseDialog.Info(LibraryDialogOwner, criticalThreadEnabled.Text, body);
			};
			criticalThreadEditor.Controls.Add(criticalThreadInfo);
			criticalThreadMode = new GameCardDetailsButton
			{
				Name = "RenderAffinitySettings",
				ShowAdjustLabel = true,
				AccessibleName = ThreadUi("设置渲染亲和性自动或手动选核", "Set automatic or manual render CPU placement")
			};
			criticalThreadMode.Click += delegate { OpenRenderAffinityEditor(); };
			criticalThreadEnabled.Controls.Add(criticalThreadMode);
			criticalThreadEditor.SizeChanged += delegate
			{
				LayoutCriticalThreadEditor();
			};
		}

		private void SyncCriticalThreadEditor(GameProfile profile)
		{
			if (criticalThreadEditor == null || criticalThreadEditor.IsDisposed)
			{
				return;
			}
			criticalThreadEditor.Visible = profile != null;
			if (profile == null)
			{
				criticalThreadProfile = null;
				return;
			}
			criticalThreadAll = CpuTopology.AllMask;
			criticalThreadPhysical = CpuTopology.PhysicalCoreMasks();
			criticalThreadReserved = AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask;
			CriticalThreadAllowedRange criticalThreadAllowedRange = CriticalThreadAllowedRange.For(profile);
			criticalThreadAllowed = criticalThreadAllowedRange.Mask;
			criticalThreadAllowedError = criticalThreadAllowedRange.Error;
			criticalThreadTopologyTrusted = IntPtr.Size == 8 && !CpuTopology.MultiGroup && CpuTopology.TopologySourcesAgree && !CpuTopology.AllMaskReconciled;
			string text = CriticalThreadRule.Raw(profile);
			if (criticalThreadProfile != profile.Id || criticalThreadExpected != text)
			{
				bool flag = criticalThreadProfile == profile.Id;
				criticalThreadProfile = profile.Id;
				criticalThreadExpected = text;
				criticalThreadError = (flag ? ThreadUi("配置已从外部变化，已重新载入。", "Configuration changed externally and was reloaded.") : null);
				if (text != null && CriticalThreadRule.Parse(text) == null)
				{
					criticalThreadError = ThreadUi("已存规则无法解析；请关闭开关后重新打开。", "The saved rule is invalid; turn the switch off and on again.");
				}
			}
			criticalThreadView = gameMode.CriticalThreadView(profile.Id);
			bool coupled;
			criticalThreadCompanion.Text = CriticalThreadCompanionText(profile, out coupled);
			criticalThreadCompanion.ForeColor = (coupled ? Theme.Warning : Theme.Dim);
			criticalThreadSyncing = true;
			try
			{
				criticalThreadEnabled.Checked = text != null;
			}
			finally
			{
				criticalThreadSyncing = false;
			}
			criticalThreadEnabled.AccessibleName = profile.Name + " · " + criticalThreadEnabled.Text;
			criticalThreadEnabled.AccessibleDescription = criticalThreadHelp.Text;
			RefreshCriticalThreadCard();
		}

		private static string CriticalThreadCompanionText(GameProfile profile, out bool coupled)
		{
			PolicySnapshot policySnapshot = PolicyResolver.For(profile);
			coupled = true;
			if (!policySnapshot.EffBoost)
			{
				coupled = false;
				return ThreadUi("本游戏的游戏资源优先级已关闭，线程提优不运行。", "Game resource priority is off for this game, so thread boost does not run.");
			}
			if (policySnapshot.FrameCoordinationOn)
			{
				return ThreadUi("本游戏选择了渲染关键线程提优。与此规则同用时，呈现关联或依赖关联候选可能同时被提优；组合效果未测。", "Render-critical thread boost is selected for this game. Used with this rule, presentation-related or dependency-related candidates may also be boosted; the combination is untested.");
			}
			if (policySnapshot.RenderLane && GameMode.LaneSupported(policySnapshot.Preset))
			{
				return ThreadUi("本游戏选择了高负载线程提优。与此规则同用时，可能作用于同一线程；组合效果未测。", "Busy-thread boost is selected for this game. Used with this rule, it may target the same thread; the combination is untested.");
			}
			coupled = false;
			return ThreadUi("本游戏不运行线程提优，不与此规则叠加。", "Thread boost does not run for this game and does not combine with this rule.");
		}

		private CriticalThreadRule CriticalThreadSwitchRule(out string error)
		{
			if (criticalThreadAllowedError != null)
			{
				error = criticalThreadAllowedError;
				return null;
			}
			CriticalThreadRule criticalThreadRule = CriticalThreadRule.Auto(true);
			error = criticalThreadRule.Validate(criticalThreadAll, criticalThreadPhysical, criticalThreadAllowed, criticalThreadReserved, criticalThreadTopologyTrusted);
			if (error != null)
			{
				return null;
			}
			return criticalThreadRule;
		}

		private void RefreshCriticalThreadCard()
		{
			string error;
			CriticalThreadRule criticalThreadRule = CriticalThreadSwitchRule(out error);
			CriticalThreadRule criticalThreadRule2 = CriticalThreadRule.ParseForRuntime(criticalThreadExpected);
			criticalThreadMode.AccessibleDescription = criticalThreadRule2 != null && criticalThreadRule2.ManualTarget
				? ThreadUi("手动 · ", "Manual · ") + CriticalThreadRule.ProcessorName(criticalThreadRule2.Targets[0])
				: ThreadUi("自动选核", "Automatic CPU");
			string text = ((criticalThreadExpected == null) ? ThreadUi("默认关闭；打开右上角开关即可启用。", "Off by default. Turn on the switch above to enable it.") : ((criticalThreadRule2 != null && !criticalThreadRule2.Automatic) ? ThreadUi("此游戏保存的是手动指定核心的旧规则，仍按原规则执行；关闭再打开开关即改为自动选核。", "This game keeps an older rule with manually chosen cores, which still applies; turn the switch off and on to choose cores automatically.") : ThreadUi("已启用；实际状态见下方。", "On; see the runtime state below.")));
			string text2 = ((criticalThreadExpected == null) ? error : criticalThreadAllowedError);
			criticalThreadResult.Text = ((criticalThreadError == null) ? "" : (criticalThreadError + "\n")) + (text2 ?? text);
			criticalThreadResult.ForeColor = ((criticalThreadError != null || text2 != null) ? Theme.Warning : Theme.Dim);
			criticalThreadEnabled.Enabled = criticalThreadExpected != null || criticalThreadRule != null;
			bool flag = criticalThreadView != null && criticalThreadView.Pending;
			if (flag && criticalThreadError == null && text2 == null)
			{
				Label label = criticalThreadResult;
				label.Text = label.Text + "\n" + ThreadUi("偏好可以保存；恢复完成前不会应用新规则。", "Preferences can be saved; new rules wait until recovery completes.");
			}
			criticalThreadRuntime.Text = CriticalThreadRuntimeText();
			criticalThreadIssue = flag || criticalThreadError != null || text2 != null || (criticalThreadRule2 != null && !criticalThreadRule2.Automatic);
			criticalThreadResult.Visible = criticalThreadIssue;
			LayoutCriticalThreadEditor();
		}

		private string CriticalThreadRuntimeText()
		{
			CriticalThreadUiState criticalThreadUiState = criticalThreadView;
			if (criticalThreadUiState == null)
			{
				return ThreadUi("尚无此游戏的运行快照。", "No runtime snapshot for this game.");
			}
			if (criticalThreadUiState.ProfileId != criticalThreadProfile)
			{
				if (!criticalThreadUiState.Pending)
				{
					return ThreadUi("尚无此游戏的运行快照。", "No runtime snapshot for this game.");
				}
				return ThreadUi("原会话恢复待办：新绑定被阻止。", "An earlier session has recovery pending; new affinity is blocked.") + "\n" + criticalThreadUiState.Detail;
			}
			long nowTicks = AutoCores.NowTicks;
			bool flag = criticalThreadUiState.SnapshotTicks > 0 && nowTicks >= criticalThreadUiState.SnapshotTicks && nowTicks - criticalThreadUiState.SnapshotTicks <= 65000000;
			CriticalThreadRule criticalThreadRule = CriticalThreadRule.ParseForRuntime(criticalThreadExpected);
			bool flag2 = criticalThreadRule != null && criticalThreadRule.Automatic && !criticalThreadUiState.Applied;
			string text = (criticalThreadUiState.Pending ? ThreadUi("恢复待办：新绑定被阻止。", "Recovery pending: new affinity is blocked.") : ((criticalThreadUiState.State == "off" || criticalThreadUiState.State == "waiting-game" || flag2) ? criticalThreadUiState.StateText : ((!flag) ? ThreadUi("运行快照待复核；当前硬绑定未确认。", "Runtime snapshot needs rechecking; current affinity is unconfirmed.") : criticalThreadUiState.StateText)));
			if (!flag && criticalThreadUiState.SnapshotTicks > 0)
			{
				text = text + "\n" + ThreadUi("上次状态：", "Previous status: ") + criticalThreadUiState.StateText;
			}
			if (flag && criticalThreadUiState.Applied)
			{
				text = text + "\n" + ThreadUi("最近原生回读已匹配；不代表性能收益。", "Last native readback matched; this does not prove a performance gain.");
			}
			string text2 = ThreadUi("运行状态（缓存快照）", "Runtime state (cached snapshot)") + "\n" + text;
			if (criticalThreadUiState.SessionActive && criticalThreadRule != null && criticalThreadRule.Encode() != criticalThreadUiState.RuleSignature)
			{
				text2 = text2 + "\n" + ThreadUi("本局策略与新保存规则不同；保存不等于已经应用。", "The session policy differs from the saved rule; saving does not mean it has been applied.");
			}
			object obj = text2;
			text2 = string.Concat(obj, "\n", ThreadUi("候选数：", "Candidates: "), criticalThreadUiState.Candidates);
			if (!string.IsNullOrEmpty(criticalThreadUiState.CoverageText))
			{
				text2 = text2 + "\n" + (flag ? "" : ThreadUi("上次覆盖：", "Previous coverage: ")) + criticalThreadUiState.CoverageText;
			}
			string text3 = ((criticalThreadUiState.SnapshotTicks > 0) ? criticalThreadUiState.CouplingText : "");
			if (!string.IsNullOrEmpty(text3))
			{
				text2 = text2 + "\n" + ((flag && !criticalThreadUiState.Pending && criticalThreadUiState.SessionActive) ? "" : ThreadUi("上次提优关系：", "Previous thread boost relation: ")) + text3;
			}
			if (criticalThreadUiState.RequestedWorkerAvoid)
			{
				text2 = text2 + "\n" + ((criticalThreadUiState.SnapshotTicks > 0) ? (ThreadUi("工作线程观测数（快照）：", "Observed workers (snapshot): ") + criticalThreadUiState.Workers) : ThreadUi("工作线程避让尚未核验。", "Worker avoidance has not been verified."));
				if (criticalThreadUiState.WorkerMask != 0)
				{
					text2 = text2 + "\n" + ThreadUi("工作线程目标范围（快照）：", "Worker target range (snapshot): ") + CpuTopology.DescribeMask(criticalThreadUiState.WorkerMask);
				}
			}
			else if (criticalThreadUiState.Coverage != CriticalThreadCoverageState.CandidatesOnly)
			{
				text2 = text2 + "\n" + ThreadUi("该游戏进程的其他线程：未请求限制。", "Other threads in this game process: no restrictions requested.");
			}
			string[] candidateLabels = criticalThreadUiState.CandidateLabels;
			ulong[] candidateMasks = criticalThreadUiState.CandidateMasks;
			for (int i = 0; i < candidateMasks.Length; i++)
			{
				object obj2 = text2;
				text2 = string.Concat(obj2, "\n", ThreadUi("候选 ", "Candidate "), i + 1, ": ", (i < candidateLabels.Length) ? candidateLabels[i] : "", " → ", CpuTopology.DescribeMask(candidateMasks[i]));
			}
			if (!string.IsNullOrEmpty(criticalThreadUiState.Detail))
			{
				text2 = text2 + "\n" + criticalThreadUiState.Detail;
			}
			return text2;
		}

		private void SaveCriticalThreadSwitch(bool enabled)
		{
			if (criticalThreadProfile == null || criticalThreadSyncing)
			{
				return;
			}
			string error = null;
			CriticalThreadRule criticalThreadRule = (enabled ? CriticalThreadSwitchRule(out error) : null);
			if (error == null)
			{
				error = gameMode.SaveCriticalThreadRule(criticalThreadProfile, criticalThreadExpected, criticalThreadRule);
				if (error == null)
				{
					criticalThreadExpected = CriticalThreadRule.Raw(FindLibraryProfile(criticalThreadProfile));
				}
			}
			criticalThreadError = error;
			SyncCriticalThreadEditor(FindLibraryProfile(criticalThreadProfile));
		}

		private void LayoutCriticalThreadEditor()
		{
			if (criticalThreadEditor != null && !criticalThreadEditor.IsDisposed && criticalThreadInfo != null && criticalThreadMode != null)
			{
				int num = Math.Max(1, criticalThreadEditor.Width);
				criticalThreadEnabled.SetBounds(0, 0, num, Theme.S(40));
				int num2 = TextRenderer.MeasureText(criticalThreadEnabled.Text, Theme.UI(8f, true)).Width;
				criticalThreadInfo.SetBounds(Math.Min(Theme.S(36) + num2, Math.Max(0, num - Theme.S(142))), Theme.S(4), Theme.S(28), Theme.S(32));
				criticalThreadMode.SetBounds(Math.Max(0, num - Theme.S(65)), Theme.S(2), Theme.S(64), Theme.S(36));
				criticalThreadResult.SetBounds(Theme.S(12), Theme.S(46), Math.Max(1, num - Theme.S(24)), Theme.S(30));
				if (criticalThreadIssue)
				{
					criticalThreadResult.Height = WrappedHeight(criticalThreadResult);
				}
				criticalThreadEditor.Height = (criticalThreadIssue ? (criticalThreadResult.Bottom + Theme.S(8)) : Theme.S(40));
			}
		}

		private void ShowCoreInformation(string title, string body)
		{
			PaviseDialog.Info(this, title, body);
		}

		private PerformanceInfoButton CoreInfo(Control parent, string name, string title, Func<string> body)
		{
			PerformanceInfoButton performanceInfoButton = new PerformanceInfoButton();
			performanceInfoButton.Name = name;
			performanceInfoButton.AccessibleName = title;
			performanceInfoButton.Bg = Theme.Card;
			PerformanceInfoButton performanceInfoButton2 = performanceInfoButton;
			performanceInfoButton2.Click += delegate
			{
				ShowCoreInformation(title, body());
			};
			parent.Controls.Add(performanceInfoButton2);
			return performanceInfoButton2;
		}

		private void BuildCoreWorkspace()
		{
			coreWorkspaceLayoutKey = null;
			coreMatrixLayoutWidth = 0;
			pageCoreScheduling.AutoScroll = false;
			coreCpuLine = CpuDisplayName() + " · " + TopologyLine();
			coreIdentity = new CoreIdentityStrip
			{
				Name = "CoreGameIdentity"
			};
			pageCoreScheduling.Controls.Add(coreIdentity);
			coreScrollPanel = new DBPanel
			{
				Name = "CoreWorkspace",
				BackColor = Theme.Bg,
				AutoScroll = false
			};
			pageCoreScheduling.Controls.Add(coreScrollPanel);
			coreRightRail = new CoreWorkspaceSurface
			{
				Name = "CoreControlRail",
				AutoScroll = true
			};
			Native.Dark(coreRightRail);
			coreScrollPanel.Controls.Add(coreRightRail);
			BuildCoreEditor(0, 0, ContentW);
			coreEnableLabel = CardLabel(coreIdentity, Lang.T("nav.corescheduling"), 0, 0, 100, 30, 10f, true, Theme.Fg);
			coreEnableLabel.TextAlign = ContentAlignment.MiddleRight;
			coreEnableInfo = CoreInfo(coreIdentity, "CoreSchedulingInfo", Lang.T("nav.corescheduling"), () => PerformancePolicy.Text("此开关控制游戏进程的核心范围与核心隔离。渲染线程亲和性在深度调优中独立控制，关闭核心调度不会同时关闭该功能。", "This switch controls the game's CPU range and core isolation. Render thread affinity has its own switch in advanced tuning and remains independent when core scheduling is off.") + "\r\n\r\n" + Lang.T("schedule.manual.help"));
			coreRangeInfo = CoreInfo(coreEditor, "CoreRangeInfo", PerformancePolicy.Text("游戏核心范围说明", "About the game CPU range"), () => Lang.T("schedule.manual.help"));
			coreSelectedCount = CardLabel(coreEditor, "", 0, 0, 120, 24, 9f, false, Theme.Dim);
			coreSelectedCount.TextAlign = ContentAlignment.MiddleRight;
			coreTopologyCount = CardLabel(coreEditor, "", 0, 0, 200, 22, 8f, false, Theme.Dim);
			coreIsolationGlyph = new CoreRailGlyph("chip");
			coreRightRail.Controls.Add(coreIsolationGlyph);
			coreIsolationInfo = CoreInfo(coreRightRail, "CoreIsolationInfo", Lang.T("schedule.manual.isolation"), () => Lang.T("schedule.manual.isolation"));
			coreIsolationHint = CardLabel(coreRightRail, PerformancePolicy.Text("至少保留两个完整物理核", "Keep at least two whole physical cores"), 0, 0, 200, 30, 8.5f, false, Theme.Dim);
			coreStatusPanel = MakeConsolePanel(coreRightRail, 0, 0, 300, 80, false);
			coreStatusPanel.Name = "coreStatusPanel";
			lblCoreState = CardLabel(coreStatusPanel, "", 0, 0, 280, 26, 9.5f, true, Theme.Warning);
			lblCoreReason = CardLabel(coreStatusPanel, "", 0, 28, 280, 26, 8.5f, false, Theme.Warning);
			lblCoreRule = CardLabel(coreStatusPanel, "", 0, 0, 280, 0, 8f, false, Theme.Dim);
			lblCoreTelemetry = CardLabel(coreStatusPanel, "", 0, 0, 280, 0, 8f, false, Theme.Dim);
			Label label = lblCoreRule;
			bool visible = (lblCoreTelemetry.Visible = false);
			label.Visible = visible;
			coreLivePanel = MakeConsolePanel(coreRightRail, 0, 0, 300, 1, false);
			coreLivePanel.Visible = false;
			coreMapPanel = MakeConsolePanel(coreRightRail, 0, 0, 300, 100, false);
			coreMapPanel.Name = "coreMapPanel";
			lblCorePlan = CardLabel(coreMapPanel, "", 0, 0, 280, 24, 9f, true, Theme.Fg);
			lblCorePlan.Visible = false;
			coreMatrix = new CoreMatrix
			{
				MarkExclusive = false,
				SchedulingOverlay = true,
				BackColor = Theme.Card,
				Visible = false
			};
			coreMapPanel.Controls.Add(coreMatrix);
			lblCoreReserved = CardLabel(coreMapPanel, "", 0, 0, 280, 40, 8.5f, false, Theme.Warning);
			btnCoreClearReserved = new PerformanceActionButton(Lang.T("schedule.auto.reserved.clear"))
			{
				Name = "coreClearReserved",
				Visible = false
			};
			btnCoreClearReserved.Click += delegate
			{
				ClearSuspectReservedCores();
			};
			coreMapPanel.Controls.Add(btnCoreClearReserved);
			pageCoreScheduling.SizeChanged += delegate
			{
				LayoutCoreViewport();
			};
			SyncCorePage();
		}

		private void LayoutCoreWorkspace()
		{
			if (coreWorkspaceArranging || coreWorkspaceSyncing || coreIdentity == null || coreIdentity.IsDisposed || coreMatrixViewport == null || coreRightRail == null || lblCoreState == null)
			{
				return;
			}
			string text = CoreWorkspaceLayoutKey();
			if (text == coreWorkspaceLayoutKey)
			{
				return;
			}
			coreWorkspaceArranging = true;
			CoreViewState coreViewState = CaptureCoreViewState();
			try
			{
				pageCoreScheduling.AutoScroll = false;
				pageCoreScheduling.AutoScrollMinSize = Size.Empty;
				coreWorkspaceCompact = pageCoreScheduling.ClientSize.Height < Theme.S(420);
				int num = Theme.S(24);
				int num2 = Theme.S(coreWorkspaceCompact ? 6 : 16);
				int num3 = Math.Max(1, pageCoreScheduling.ClientSize.Width - num * 2);
				coreIdentity.SetBounds(num, Theme.S(coreWorkspaceCompact ? 6 : 20), num3, Theme.S(coreWorkspaceCompact ? 56 : 88));
				coreEnableToggle.SetBounds(num3 - Theme.S(72), (coreIdentity.Height - Theme.S(30)) / 2, Theme.S(46), Theme.S(30));
				coreEnableInfo.SetBounds(num3 - Theme.S(110), (coreIdentity.Height - Theme.S(36)) / 2, Theme.S(32), Theme.S(36));
				coreEnableLabel.SetBounds(num3 - Theme.S(228), (coreIdentity.Height - Theme.S(36)) / 2, Theme.S(110), Theme.S(36));
				coreScrollPanel.SetBounds(num, coreIdentity.Bottom + num2, num3, Math.Max(1, pageCoreScheduling.ClientSize.Height - coreIdentity.Bottom - num2 - Theme.S(8)));
				coreScrollPanel.AutoScroll = false;
				int num4 = (int)Math.Round((double)(num3 - num2) * 0.59);
				coreEditor.SetBounds(0, 0, num4, coreScrollPanel.Height);
				((CoreWorkspaceSurface)coreEditor).Divider1 = Theme.S(coreWorkspaceCompact ? 36 : 54);
				coreRightRail.SetBounds(num4 + num2, 0, Math.Max(1, num3 - num4 - num2), coreScrollPanel.Height);
				coreRightRail.AutoScrollPosition = Point.Empty;
				int num5 = Theme.S(22);
				int num6 = Math.Max(1, num4 - num5 * 2);
				coreEditorTitle.SetBounds(num5, Theme.S(coreWorkspaceCompact ? 6 : 20), Math.Max(1, num6 - Theme.S(120)), Theme.S(30));
				coreRangeInfo.SetBounds(num5 + Math.Min(Theme.S(140), coreEditorTitle.Width), Theme.S(coreWorkspaceCompact ? 5 : 19), Theme.S(30), Theme.S(32));
				coreSelectedCount.SetBounds(Math.Max(num5, num4 - num5 - Theme.S(115)), Theme.S(coreWorkspaceCompact ? 8 : 22), Theme.S(115), Theme.S(26));
				coreSelectedCount.Text = CpuTopology.MultiGroup ? PerformancePolicy.Text("范围未确认", "Range unconfirmed") : PerformancePolicy.Text("已选 ", "Selected ") + CpuTopology.CountSetBits(coreEditMatrix.Selected) + " / " + CpuTopology.CountSetBits(CpuTopology.AllMask);
				coreQuickPanel.SetBounds(num5, Theme.S(coreWorkspaceCompact ? 38 : 64), num6, coreQuickPanel.Height);
				LayoutCoreShortcuts();
				coreTopologyCount.Text = CpuTopology.MultiGroup ? PerformancePolicy.Text("全机核心范围未确认", "Total CPU range unconfirmed") : CoreMatrix.TopologyCountsConfirmed ? CpuTopology.PhysicalCoreCount + PerformancePolicy.Text(" 物理核 / ", " cores / ") + CpuTopology.CountSetBits(CpuTopology.AllMask) + PerformancePolicy.Text(" 逻辑核", " logical CPUs") : CpuTopology.CountSetBits(CpuTopology.AllMask) + PerformancePolicy.Text(" 逻辑核 · 物理核数未确认", " logical CPUs · physical core count unconfirmed");
				int num7 = 0;
				foreach (Control control in coreQuickPanel.Controls)
				{
					num7 = Math.Max(num7, control.Right);
				}
				int num8 = TextRenderer.MeasureText(coreTopologyCount.Text, coreTopologyCount.Font).Width + Theme.S(6);
				coreTopologyCount.Visible = coreQuickPanel.Height <= Theme.S(30) && num7 + Theme.S(14) + num8 <= num6;
				coreTopologyCount.TextAlign = ContentAlignment.MiddleRight;
				coreTopologyCount.SetBounds(num4 - num5 - num8, coreQuickPanel.Top + Theme.S(3), num8, Theme.S(24));
				coreMatrixViewport.SetBounds(num5 - Theme.S(2), coreQuickPanel.Bottom + Theme.S(coreWorkspaceCompact ? 10 : 18), num6 + Theme.S(6), Math.Max(1, coreEditor.Height - coreQuickPanel.Bottom - Theme.S(coreWorkspaceCompact ? 18 : 34)));
				Point autoScrollPosition = coreMatrixViewport.AutoScrollPosition;
				coreMatrixViewport.AutoScrollPosition = Point.Empty;
				int num9 = Math.Max(1, coreMatrixViewport.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - Theme.S(4));
				if (coreMatrixLayoutWidth != num9)
				{
					coreMatrixLayoutWidth = num9;
					coreEditMatrix.SetBounds(Theme.S(2), 0, num9, coreEditMatrix.LayoutFor(num9));
				}
				coreMatrixViewport.AutoScrollMinSize = new Size(0, coreEditMatrix.Height);
				coreMatrixViewport.AutoScroll = true;
				coreMatrixViewport.AutoScrollPosition = new Point(0, Math.Max(0, -autoScrollPosition.Y));
				Label label = coreEditorHelp;
				bool visible = (coreEditorRuntime.Visible = false);
				label.Visible = visible;
				int num10 = Math.Max(1, coreRightRail.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
				int num11 = Math.Max(1, num10 - num5 * 2);
				coreIsolationGlyph.SetBounds(num5, Theme.S(27), Theme.S(28), Theme.S(28));
				coreEditorIsolation.Text = PerformancePolicy.Text("核心隔离", "Core isolation");
				bool flag2 = num11 < Theme.S(280);
				int num12 = ((!flag2) ? Theme.S(44) : 0);
				coreIsolationGlyph.Visible = !flag2;
				coreEditorIsolation.SetBounds(num5 + num12, Theme.S(24), Math.Max(1, num11 - num12 - Theme.S(flag2 ? 32 : 88)), Theme.S(30));
				coreEditorIsolation.Height = Math.Max(Theme.S(30), WrappedHeight(coreEditorIsolation));
				coreIsolationInfo.SetBounds(num10 - num5 - Theme.S(flag2 ? 28 : 88), Theme.S(24), Theme.S(28), Theme.S(32));
				coreIsolationToggle.SetBounds(num10 - num5 - Theme.S(46), flag2 ? (coreEditorIsolation.Bottom + Theme.S(8)) : Theme.S(25), Theme.S(46), Theme.S(30));
				coreIsolationHint.SetBounds(num5 + num12, Math.Max(Theme.S(64), Math.Max(coreEditorIsolation.Bottom, coreIsolationToggle.Bottom) + Theme.S(8)), Math.Max(1, num11 - num12), Theme.S(24));
				coreIsolationHint.Height = WrappedHeight(coreIsolationHint);
				int num13 = coreIsolationHint.Bottom + Theme.S(20);
				((CoreWorkspaceSurface)coreRightRail).Divider1 = num13 - Theme.S(10);
				((CoreWorkspaceSurface)coreRightRail).Divider2 = 0;
				corePlanPreview.SetBounds(num5, num13, num11, corePlanPreview.Height);
				num13 = corePlanPreview.Bottom + Theme.S(12);
				AutoCoreStatus status = AutoCores.Status;
				coreStatusPanel.Visible = coreDraftProfile == null || status.Pending || CoreIsolationClient.State == "schedule.isolation.pending" || (status.ProfileId == coreDraftProfile && status.Blocked != null);
				if (coreStatusPanel.Visible)
				{
					coreStatusPanel.SetBounds(num5, num13, num11, Theme.S(80));
					lblCoreState.SetBounds(0, 0, num11, Theme.S(26));
					lblCoreState.Height = WrappedHeight(lblCoreState);
					lblCoreReason.SetBounds(0, lblCoreState.Bottom + Theme.S(4), num11, Theme.S(30));
					lblCoreReason.Height = WrappedHeight(lblCoreReason);
					coreStatusPanel.Height = lblCoreReason.Bottom + Theme.S(8);
					num13 = coreStatusPanel.Bottom + Theme.S(8);
				}
				coreMapPanel.Visible = lblCoreReserved.Text.Length > 0;
				if (coreMapPanel.Visible)
				{
					coreMapPanel.SetBounds(num5, num13, num11, Theme.S(80));
					lblCoreReserved.SetBounds(0, 0, num11, Theme.S(40));
					lblCoreReserved.Height = WrappedHeight(lblCoreReserved);
					btnCoreClearReserved.SetBounds(0, lblCoreReserved.Bottom + Theme.S(6), num11, Theme.S(34));
					coreMapPanel.Height = (btnCoreClearReserved.Visible ? btnCoreClearReserved.Bottom : lblCoreReserved.Bottom) + Theme.S(8);
					num13 = coreMapPanel.Bottom + Theme.S(8);
				}
				coreEditorResult.SetBounds(num5, num13, num11, Theme.S(30));
				if (coreEditorResult.Visible)
				{
					coreEditorResult.Height = WrappedHeight(coreEditorResult);
					num13 = coreEditorResult.Bottom + Theme.S(10);
				}
				coreEvidenceToggle.Text = PerformancePolicy.Text("查看绑定证据  ›", "View affinity evidence  ›");
				coreEvidenceToggle.SetBounds(num5, num13, num11, Theme.S(40));
				coreRightRail.AutoScrollMinSize = new Size(0, coreEvidenceToggle.Bottom + Theme.S(18));
				coreRightRail.AutoScroll = true;
				GameProfile gameProfile = CorePageProfile();
				coreIdentity.SetIdentity((gameProfile == null) ? Lang.T("schedule.manual.pickgame") : gameProfile.Name, coreCpuLine, (gameProfile == null) ? null : GameIcon(gameProfile.ExecutablePath));
				coreLayoutScale = Dpi.Scale;
				RestoreCoreViewState(coreViewState);
				coreWorkspaceLayoutKey = CoreWorkspaceLayoutKey();
			}
			finally
			{
				coreWorkspaceArranging = false;
			}
		}

		private string CoreWorkspaceLayoutKey()
		{
			AutoCoreStatus status = AutoCores.Status;
			return string.Concat(pageCoreScheduling.ClientSize, "|", Dpi.Scale, "|", coreDraftProfile, "|", coreEditMatrix.Selected, "|", coreEditorResult.Visible, "|", coreEditorResult.Text, "|", corePlanPreview.Height, "|", status.Pending, "|", status.Blocked, "|", CoreIsolationClient.State, "|", lblCoreReason.Text, "|", lblCoreReserved.Text, "|", btnCoreClearReserved.Visible);
		}

		private void RevealCoreCell(Rectangle cell)
		{
			if (!cell.IsEmpty && coreMatrixViewport != null)
			{
				int num = Math.Max(0, -coreMatrixViewport.AutoScrollPosition.Y);
				int val = num;
				int num2 = coreMatrixViewport.ClientSize.Height;
				int top = cell.Top;
				int bottom = cell.Bottom;
				if (top < num)
				{
					val = Math.Max(0, top - Theme.S(4));
				}
				else if (bottom > num + num2)
				{
					val = bottom - num2 + Theme.S(4);
				}
				val = Math.Min(val, DockScrollLimit(coreMatrixViewport));
				if (val != num)
				{
					coreMatrixViewport.AutoScrollPosition = new Point(0, val);
				}
			}
		}

		private CoreViewState CaptureCoreViewState()
		{
			if (curPage != pageCoreScheduling || coreMatrixViewport == null || coreRightRail == null || coreRightRail.IsDisposed)
			{
				return null;
			}
			float num = Math.Max(0.01f, coreLayoutScale);
			CoreViewState coreViewState = new CoreViewState();
			coreViewState.MatrixY = (float)Math.Max(0, -coreMatrixViewport.AutoScrollPosition.Y) / num;
			coreViewState.RailY = (float)Math.Max(0, -coreRightRail.AutoScrollPosition.Y) / num;
			return coreViewState;
		}

		private void RestoreCoreViewState(CoreViewState state)
		{
			if (state != null && coreMatrixViewport != null && coreRightRail != null && curPage == pageCoreScheduling)
			{
				coreMatrixViewport.AutoScrollPosition = new Point(0, Math.Min((int)Math.Round(state.MatrixY * Dpi.Scale), DockScrollLimit(coreMatrixViewport)));
				coreRightRail.AutoScrollPosition = new Point(0, Math.Min((int)Math.Round(state.RailY * Dpi.Scale), DockScrollLimit(coreRightRail)));
			}
		}

		private void ShowDataOperationProgress(string message)
		{
			if (root == null || base.IsDisposed)
			{
				return;
			}
			if (dataOperationPanel == null)
			{
				dataOperationPanel = new DataOperationProgressCard();
				base.Controls.Add(dataOperationPanel);
				base.Resize += delegate
				{
					PositionDataOperationProgress();
				};
			}
			dataOperationPanel.Status.Text = message;
			PositionDataOperationProgress();
			dataOperationPanel.BringToFront();
		}

		private void PositionDataOperationProgress()
		{
			if (dataOperationPanel != null && !dataOperationPanel.IsDisposed)
			{
				dataOperationPanel.Location = new Point(Math.Max(0, (base.ClientSize.Width - dataOperationPanel.Width) / 2), Math.Max(0, (base.ClientSize.Height - dataOperationPanel.Height) / 2));
			}
		}

		private void ResetEnvironmentObservation()
		{
			environmentObservation = new EnvironmentObservationCycle();
			environmentPreferenceOverrides.Clear();
			environmentBundleOverride = (environmentStorageOverride = (environmentStorageOverride = null));
			environmentQuantumOverride = null;
			environmentCpuMitigationsOverride = null;
		}

		private static EnvironmentPreferenceObservation ReadEnvironmentPreference(DirectXGlobalPreference preference)
		{
			bool on;
			bool readable = preference.TryReadState(out on);
			EnvironmentPreferenceObservation environmentPreferenceObservation = new EnvironmentPreferenceObservation();
			environmentPreferenceObservation.Readable = readable;
			environmentPreferenceObservation.On = on;
			environmentPreferenceObservation.Owned = preference.HasResidue;
			environmentPreferenceObservation.Supported = preference.Supported;
			return environmentPreferenceObservation;
		}

		private static EnvironmentGraphicsObservation ReadEnvironmentGraphics()
		{
			EnvironmentGraphicsObservation environmentGraphicsObservation = new EnvironmentGraphicsObservation();
			environmentGraphicsObservation.HagsKnown = HagsTweak.TryQueryState(out environmentGraphicsObservation.HagsSupported, out environmentGraphicsObservation.HagsOn);
			environmentGraphicsObservation.HagsConfigured = HagsTweak.ConfiguredMode();
			environmentGraphicsObservation.Windowed = ReadEnvironmentPreference(DirectXGlobalPreference.Windowed);
			environmentGraphicsObservation.Vrr = ReadEnvironmentPreference(DirectXGlobalPreference.Vrr);
			environmentGraphicsObservation.AmdAvailable = AdlxTweaks.Available;
			environmentGraphicsObservation.AmdReadable = environmentGraphicsObservation.AmdAvailable && AdlxTweaks.SamGet(out environmentGraphicsObservation.AmdSupported, out environmentGraphicsObservation.AmdOn);
			environmentGraphicsObservation.NvVrrApplicable = NvVrrWindowed.Applicable();
			environmentGraphicsObservation.NvVrrOwned = NvVrrWindowed.HasResidue;
			environmentGraphicsObservation.IntelEnduranceSupported = IntelGraphicsApi.HasAvailable && IntelEndurance.Supported();
			environmentGraphicsObservation.CacheWarmOn = PerformancePolicy.EnvironmentPreference("GmCacheWarmV2");
			environmentGraphicsObservation.TimerTickOn = PerformancePolicy.EnvironmentPreference("AutoTimerTick");
			environmentGraphicsObservation.GlobalTimerOn = PerformancePolicy.EnvironmentPreference("AutoGlobalTimer");
			environmentGraphicsObservation.GlobalTimerOwned = GlobalTimerResTweak.OwnsState;
			environmentGraphicsObservation.Tuning = new EnvironmentTuningObservation
			{
				BundleOn = PerformanceTuning.EnvironmentBundleEnabled,
				BundleState = PerformanceTuning.EnvironmentBundleState(),
				BundleLabel = PerformanceTuning.EnvironmentBundleLabel(),
				BundleDetail = EnvironmentBundlePerformanceDetail(PerformanceTuning.EnvironmentBundleDetail),
				StorageOn = PerformanceTuning.StorageGroupEnabled,
				StorageState = PerformanceTuning.StorageGroupState(),
				StorageLabel = PerformanceTuning.StorageGroupLabel(),
				QuantumOn = Settings.LoadCached("EnvQuantumProfileV1", false),
				CpuMitigationsOn = Settings.LoadCached(PerformanceActionCatalog.CpuMitigations, false),
				CpuMitigationsExpanded = PerformanceTuning.CpuMitigationsExpanded,
				CpuMitigationsState = PerformanceTuning.State(PerformanceActionCatalog.CpuMitigations),
				QuantumState = PerformanceTuning.State("EnvQuantumProfileV1"),
				QuantumSelection = QuantumProfiles.Selected,
				LegacyQuantum = QuantumRecovery.LegacyRecordsForDisplay
			};
			environmentGraphicsObservation.Tuning.StorageDetail = StoragePerformanceDetail(environmentGraphicsObservation.Tuning.StorageState, PerformanceTuning.StorageGroupDetail, new string[3]
			{
				PerformanceTuning.State("EnvStorageIdleV1"),
				PerformanceTuning.State("EnvStorageRuntimeV1"),
				PerformanceTuning.State("EnvStorageD3ColdV1")
			});
			return environmentGraphicsObservation;
		}

		private string EnvironmentObservationConfiguration()
		{
			// MutationGeneration invalidates the cache; only normalized preference values identify the phase.
			int version = Settings.MutationGeneration;
			if (environmentConfigurationSignature != null && environmentConfigurationGeneration == version) return environmentConfigurationSignature;
			StringBuilder value = new StringBuilder();
			foreach (PerformanceAction action in PerformanceActionCatalog.All)
				if (action.Environment) value.Append(action.Key).Append('=').Append(Settings.LoadCached(action.Key, false) ? '1' : '0').Append(';');
			foreach (string key in new[] { "EnvVbsDisabledV1", "EnvCpuMitigationsExpandedV1", "GmCacheWarmV2", "AutoTimerTick", "AutoGlobalTimer", "GmIntelEnduranceV1" })
				value.Append(key).Append('=').Append(Settings.LoadCached(key, false) ? '1' : '0').Append(';');
			value.Append("quantum=").Append(QuantumProfiles.Selected);
			environmentConfigurationSignature = value.ToString();
			environmentConfigurationGeneration = version;
			return environmentConfigurationSignature;
		}

		private void QueueEnvironmentObservation(bool force)
		{
			if (!UiActive || base.IsDisposed || base.Disposing || !base.IsHandleCreated)
			{
				return;
			}
			EnvironmentObservationCycle cycle = environmentObservation;
			if (force)
			{
				cycle.Version++;
				cycle.StartedUtc = DateTime.MinValue;
			}
			if (cycle.Busy != 0 || (!force && cycle.SettingsVersion == Settings.MutationGeneration && DateTime.UtcNow - cycle.StartedUtc < TimeSpan.FromSeconds(15.0)))
			{
				return;
			}
			long observationSession = gameMode == null ? 0 : gameMode.AutomaticSessionSerial;
			string observationConfiguration = EnvironmentObservationConfiguration();
			if (!environmentObservationFailures.Allowed(0, observationSession, observationConfiguration, "environment")) return;
			cycle.Busy = 1;
			cycle.StartedUtc = DateTime.UtcNow;
			int version = cycle.Version;
			int settingsVersion = Settings.MutationGeneration;
			cycle.SettingsVersion = settingsVersion;
			Action work = delegate
			{
				EnvironmentGraphicsObservation observed = null;
				try
				{
					observed = ReadEnvironmentGraphics();
				}
				catch (Exception ex)
				{
					int failures = environmentObservationFailures.Failed(0, observationSession, observationConfiguration, "environment");
					Logger.Warn("Environment observation failure=" + failures + "/3: " + ex.Message);
				}
				if (observed != null) environmentObservationFailures.Succeeded(0, observationSession, observationConfiguration, "environment");
				Action apply = delegate
				{
					cycle.Busy = 0;
					if (!base.IsDisposed && !base.Disposing && object.ReferenceEquals(environmentObservation, cycle))
					{
						if (cycle.Version != version || Settings.MutationGeneration != settingsVersion)
						{
							cycle.StartedUtc = DateTime.MinValue;
							QueueEnvironmentObservation(false);
						}
						else
						{
							if (observed != null)
							{
								cycle.Snapshot = observed;
								environmentPreferenceOverrides.Clear();
								environmentBundleOverride = (environmentStorageOverride = (environmentStorageOverride = null));
								environmentQuantumOverride = null;
								environmentCpuMitigationsOverride = null;
							}
							ApplyEnvironmentObservation();
							RefreshAutomationPage();
						}
					}
				};
				try
				{
					if (!base.IsDisposed && base.IsHandleCreated)
					{
						BeginInvoke((MethodInvoker)delegate
						{
							apply();
						});
					}
				}
				catch (InvalidOperationException)
				{
					Interlocked.Exchange(ref cycle.Busy, 0);
				}
				catch (Win32Exception)
				{
					Interlocked.Exchange(ref cycle.Busy, 0);
				}
			};
			try
			{
				if (!ThreadPool.QueueUserWorkItem(delegate
				{
					work();
				}))
				{
					cycle.Busy = 0;
					int failures = environmentObservationFailures.Failed(0, observationSession, observationConfiguration, "environment");
					Logger.Warn("Environment observation queue failure=" + failures + "/3");
				}
			}
			catch
			{
				cycle.Busy = 0;
				int failures = environmentObservationFailures.Failed(0, observationSession, observationConfiguration, "environment");
				Logger.Warn("Environment observation queue exception=" + failures + "/3");
			}
		}

		private static void ApplyEnvironmentPreference(EnvironmentPreferenceObservation value, SettingCard card, Toggle toggle)
		{
			if (card != null && toggle != null)
			{
				if (value == null)
				{
					toggle.Enabled = false;
					card.SetStatus(Lang.T("env.graphics.unknown"), Theme.Dim);
					return;
				}
				toggle.SetSilently(value.Readable && value.On);
				toggle.Enabled = value.Readable && value.Supported;
				LockEnvCard(card, toggle, false);
				string status = !value.Readable ? Lang.T("env.graphics.unknown") : !value.Supported ? Lang.T("lock.na")
                    : value.Owned ? AutoText(value.On ? "已由 Pavise 开启" : "已由 Pavise 关闭", value.On ? "Enabled by Pavise" : "Disabled by Pavise")
                    : Lang.T(value.On ? "lock.external" : "env.graphics.off");
                card.SetStatus(status, value.Owned && value.Readable ? Theme.Accent : Theme.Dim);
			}
		}

		private void ApplyEnvironmentObservation()
		{
			EnvironmentGraphicsObservation snapshot = environmentObservation.Snapshot;
			ApplyEnvironmentPreference((snapshot == null) ? null : snapshot.Windowed, cardWindowedOpt, swWindowedOpt);
			ApplyEnvironmentPreference((snapshot == null) ? null : snapshot.Vrr, cardVrrOpt, swVrrOpt);
			SyncHagsPresentation();
			if (swAmdSam != null && cardAmdSam != null)
			{
				bool flag = Settings.LoadCached("AmdSamByPavise", false);
				bool flag2 = snapshot != null && snapshot.AmdReadable && snapshot.AmdSupported;
				bool flag3 = snapshot != null && snapshot.AmdOn;
				swAmdSam.SetSilently(snapshot != null && snapshot.AmdReadable && flag3);
				swAmdSam.Enabled = flag2;
				LockEnvCard(cardAmdSam, swAmdSam, false);
				cardAmdSam.Desc = ((snapshot == null || (snapshot.AmdAvailable && !snapshot.AmdReadable) || flag2 || flag) ? PerformanceGraphicsDetail("AmdSam") : Lang.T("set.amd.nosup"));
				string text = ((snapshot == null || (snapshot.AmdAvailable && !snapshot.AmdReadable)) ? Lang.T("env.graphics.unknown") : ((!flag2) ? Lang.T("t.amdsam.unsupported") : (flag3 ? Lang.T(flag ? "t.amdsam.byus" : "t.amdsam.external") : Lang.T(flag ? "t.amdsam.reverted" : "t.amdsam.off"))));
				cardAmdSam.SetStatus(text, StatusInk(snapshot != null && !flag3, flag && flag3));
				if (cardAmdSam.LockText == Lang.T("lock.na") && text == Lang.T("t.amdsam.unsupported"))
				{
					cardAmdSam.SetLock("", false);
				}
			}
			if (swEnvNvVrr != null)
			{
				swEnvNvVrr.SetSilently(gameMode.NvVrrWindowedEnabled);
				swEnvNvVrr.Enabled = (snapshot != null && snapshot.NvVrrApplicable) || gameMode.NvVrrWindowedEnabled || (snapshot != null && snapshot.NvVrrOwned);
				SettingCard settingCard = swEnvNvVrr.Parent as SettingCard;
				if (settingCard != null)
				{
					settingCard.Desc = PerformanceGraphicsDetail("NvVrrWindowed") + ((snapshot != null && !swEnvNvVrr.Enabled) ? ("\n" + Lang.T("set.nvvrr.na")) : "");
				}
			}
		}

		private bool CachedEnvironmentPreference(string key)
		{
			bool value;
			if (environmentPreferenceOverrides.TryGetValue(key, out value))
			{
				return value;
			}
			EnvironmentGraphicsObservation snapshot = environmentObservation.Snapshot;
			if (snapshot == null)
			{
				return Settings.LoadCached(key, false);
			}
			if (!(key == "GmCacheWarmV2"))
			{
				if (!(key == "AutoTimerTick"))
				{
					return snapshot.GlobalTimerOn;
				}
				return snapshot.TimerTickOn;
			}
			return snapshot.CacheWarmOn;
		}

		private void RefreshPerformanceEnvironmentObservation()
		{
			QueueEnvironmentObservation(true);
			SyncPerformanceTuningEnvironment();
			SyncEnvironmentPerformanceOptions();
		}

		private void BuildPerformanceSystemOptions(Control scroll, ref int sy)
		{
			PerformanceRestartScope[] array = new PerformanceRestartScope[3]
			{
				PerformanceRestartScope.None,
				PerformanceRestartScope.ProcessRestart,
				PerformanceRestartScope.SystemRestart
			};
			foreach (PerformanceRestartScope performanceRestartScope in array)
			{
				bool flag = false;
				PrivacyAction[] all = PrivacyActionCatalog.All;
				foreach (PrivacyAction privacyAction in all)
				{
					if (privacyAction.Key != PrivacyActionCatalog.BackgroundGlobal && PerformanceRestartFor(privacyAction.Key) == performanceRestartScope)
					{
						flag = true;
						break;
					}
				}
				bool flag2 = PerformanceRestartFor("NvLowLat") == performanceRestartScope;
				if (flag || flag2 || performanceRestartScope == PerformanceRestartScope.SystemRestart || performanceRestartScope == PerformanceRestartScope.None)
				{
					AddAutomationSection(scroll, ref sy, PerformanceScopeTitle(performanceRestartScope), performanceRestartScope);
					if (performanceRestartScope == PerformanceRestartScope.None) BuildPerformanceQuantumEnvironment(scroll, ref sy);
					if (flag2)
					{
						swGameLowLatency = MakeSwitch(PerformancePolicy.GameLowLatency, OnGameLowLatencyToggle);
						swGameLowLatency.Name = "environmentGameLowLatency";
						swGameLowLatency.AccessibleName = Lang.T("set.lowlatency");
						string desc = PerformanceGraphicsDetail("NvLowLat");
						int used;
						cardGameLowLatency = MakeAutoCard(scroll, 6, sy, ScrollContentW, FullTextCardHeight(desc, ScrollContentW, swGameLowLatency, 82) + 17, Lang.T("set.lowlatency"), desc, swGameLowLatency, out used);
						sy += used + 8;
					}
					if (performanceRestartScope == PerformanceRestartScope.SystemRestart)
					{
						BuildPerformanceTuningEnvironment(scroll, ref sy);
						BuildPerformanceStorageEnvironment(scroll, ref sy);
						BuildEnvironmentTimerOptions(scroll, ref sy);
					}
					if (flag)
					{
						PrivacyOptionsPanel privacyOptionsPanel = BuildEnvironmentPrivacy(scroll, performanceRestartScope);
						if (performanceRestartScope == PerformanceRestartScope.SystemRestart && scroll is PerformanceFeatureGrid)
						{
							privacyOptionsPanel.PrependPerformanceCard((PerformanceFeatureCard)environmentPerformanceCards["AutoGlobalTimer"]);
						}
						privacyOptionsPanel.Top = Theme.S(sy);
						sy += Dpi.U(privacyOptionsPanel.Height) + 8;
					}
				}
			}
			if (scroll is PerformanceFeatureGrid)
			{
				return;
			}
			Dictionary<Control, int> dictionary = new Dictionary<Control, int>();
			foreach (Control control in scroll.Controls)
			{
				dictionary[control] = control.Top;
			}
			stackBase[scroll] = dictionary;
		}

		private static bool HasPerformanceEnvironmentGraphics(PerformanceRestartScope scope)
		{
			string[] array = new string[6] { "Hags", "WindowedOptimizations", "VariableRefreshRate", "NvVrrWindowed", "AmdSam", "GameDvrOff" };
			foreach (string key in array)
			{
				if (PerformanceRestartFor(key) == scope)
				{
					return true;
				}
			}
			return false;
		}

		private static string PerformanceGraphicsDetail(string key)
		{
			switch (key)
			{
			case "Hags":
				return AutoText("启用 HAGS，需重启电脑；不保证提升平均帧率。", "Enables HAGS; a PC restart is required. Average FPS may not improve.");
			case "AmdSam":
				return AutoText("启用 AMD SAM，需主板和显卡支持。重启生效，关闭即关闭。", "Enables AMD SAM with motherboard/GPU support. Takes effect after restart; Off disables it.");
			case "WindowedOptimizations":
				return AutoText("面向 Win11 22H2+ 的兼容 DX10/11 窗口或无边框游戏，更改后重启游戏。开关直接设置开启或关闭；系统还原入口可恢复接管前值。", "For compatible DX10/11 windowed or borderless games on Windows 11 22H2+; restart the game after changes. The switch explicitly enables or disables it; system restoration restores the original value.");
			case "VariableRefreshRate":
				return AutoText("补充部分 DX11 全屏游戏的 VRR，需兼容显示器/驱动；不替代 FreeSync/G-SYNC 开关。更改后重启游戏，开关直接设置开启或关闭；系统还原入口保留原值恢复。", "Adds VRR for some DX11 fullscreen games with compatible displays/drivers; does not replace FreeSync/G-SYNC. Restart the game after changes; the switch explicitly enables or disables it and system restoration preserves the original value.");
			case "NvVrrWindowed":
				return AutoText("对局中为已开启全屏 G-SYNC 的机器补充窗口 VRR，所有模式共用。退局还原，部分面板可能闪烁。", "Adds windowed VRR during matches when fullscreen G-SYNC is enabled, shared by all game modes. Restores on exit; some panels may flicker.");
			default:
				return AutoText("对局启用 NVIDIA 驱动低延迟，关闭或退局还原；可能影响帧率。需驱动支持，部分游戏需重启。", "Enables NVIDIA low latency during matches; Off or exit restores it and FPS may be affected. Requires driver support; some games need a restart.");
			}
		}

		private void BuildPerformanceEnvironmentGraphics(Control scroll, ref int sy, PerformanceRestartScope scope)
		{
			int used;
			if (PerformanceRestartFor("Hags") == scope)
			{
				swHags = MakeSwitch(false, OnHagsToggle);
				swHags.Name = "environmentHags";
				cardHags = MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.hags"), PerformanceGraphicsDetail("Hags"), swHags, out used);
				SyncHagsPresentation();
				sy += used + 8;
			}
			if (PerformanceRestartFor("WindowedOptimizations") == scope)
			{
				swWindowedOpt = MakeSwitch(false, delegate
				{
					ChangeDirectXPreference(DirectXGlobalPreference.Windowed, swWindowedOpt);
				});
				swWindowedOpt.Name = "environmentWindowedOpt";
				string desc = PerformanceGraphicsDetail("WindowedOptimizations");
				cardWindowedOpt = MakeAutoCard(scroll, 6, sy, ScrollContentW, FullTextCardHeight(desc, ScrollContentW, swWindowedOpt, 88), Lang.T("env.windowed"), desc, swWindowedOpt, out used);
				sy += used + 8;
			}
			if (PerformanceRestartFor("VariableRefreshRate") == scope)
			{
				swVrrOpt = MakeSwitch(false, delegate
				{
					ChangeDirectXPreference(DirectXGlobalPreference.Vrr, swVrrOpt);
				});
				swVrrOpt.Name = "environmentVrrOpt";
				string desc2 = PerformanceGraphicsDetail("VariableRefreshRate");
				cardVrrOpt = MakeAutoCard(scroll, 6, sy, ScrollContentW, FullTextCardHeight(desc2, ScrollContentW, swVrrOpt, 88), Lang.T("env.vrr"), desc2, swVrrOpt, out used);
				sy += used + 8;
			}
			if (PerformanceRestartFor("NvVrrWindowed") == scope)
			{
				swEnvNvVrr = MakeSwitch(gameMode.NvVrrWindowedEnabled, delegate
				{
					bool @checked = swEnvNvVrr.Checked;
					gameMode.NvVrrWindowedEnabled = @checked;
					swEnvNvVrr.SetSilently(gameMode.NvVrrWindowedEnabled);
					if (@checked != swEnvNvVrr.Checked)
					{
						PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.graphics.failed"));
					}
				});
				swEnvNvVrr.Name = "environmentNvVrr";
				swEnvNvVrr.Enabled = gameMode.NvVrrWindowedEnabled;
				string desc3 = PerformanceGraphicsDetail("NvVrrWindowed");
				MakeAutoCard(scroll, 6, sy, ScrollContentW, FullTextCardHeight(desc3, ScrollContentW, swEnvNvVrr, 88), Lang.T("set.nvvrr"), desc3, swEnvNvVrr, out used);
				sy += used + 8;
			}
			if (PerformanceRestartFor("AmdSam") == scope)
			{
				bool flag = Settings.LoadCached("AmdSamByPavise", false);
				swAmdSam = MakeSwitch(flag, OnAmdSamToggle);
				swAmdSam.Name = "environmentAmdSam";
				swAmdSam.Enabled = flag;
				cardAmdSam = MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.amdsam"), PerformanceGraphicsDetail("AmdSam"), swAmdSam, out used);
				sy += used + 8;
			}
			if (PerformanceRestartFor("GameDvrOff") == scope)
			{
				BuildEnvironmentRecordingOption(scroll, ref sy);
			}
		}

		private void OnGameLowLatencyToggle(object sender, EventArgs e)
		{
			bool flag = gameMode.SetGameLowLatency(swGameLowLatency.Checked);
			SyncGameLowLatencyControl();
			if (!flag)
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("set.lowlatency.savefail"));
			}
		}

		private void SyncGameLowLatencyControl()
		{
			if (swGameLowLatency != null && cardGameLowLatency != null)
			{
				bool gameLowLatency = PerformancePolicy.GameLowLatency;
				swGameLowLatency.SetSilently(gameLowLatency);
				SyncManualPerformanceStatus(cardGameLowLatency, "NvLowLat", gameLowLatency, NvApi.Available, null);
			}
		}

		private static Color StatusInk(bool needsAction, bool doneByPavise)
		{
			if (doneByPavise)
			{
				return Theme.Green;
			}
			if (!needsAction)
			{
				return Theme.Faint;
			}
			return Theme.Accent;
		}

		private static void LockEnvCard(SettingCard card, Toggle toggle, bool externalOn)
		{
			if (card != null && toggle != null)
			{
				card.SetLock(externalOn ? Lang.T("lock.external") : ((!toggle.Enabled) ? Lang.T("lock.na") : ""), externalOn);
			}
		}

		private void SyncEnvStatus()
		{
			ApplyEnvironmentObservation();
			QueueEnvironmentObservation(false);
		}

		private void ChangeDirectXPreference(DirectXGlobalPreference preference, Toggle toggle)
		{
			bool flag = (toggle.Checked ? preference.Enable() : preference.Disable());
			Logger.Log("Environment graphics " + ((preference == DirectXGlobalPreference.Windowed) ? "windowed" : "VRR") + " request=" + toggle.Checked + " success=" + flag);
			QueueEnvironmentObservation(true);
			SyncEnvStatus();
			if (!flag)
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.graphics.failed"));
			}
		}

		private void OnAmdSamToggle(object s, EventArgs e)
		{
			EnvironmentGraphicsObservation snapshot = environmentObservation.Snapshot;
            if (RequireElevationFor(swAmdSam, snapshot != null && snapshot.AmdReadable && snapshot.AmdOn))
			{
				bool success = swAmdSam.Checked ? AmdSamTweak.Enable() : AmdSamTweak.Disable();
				QueueEnvironmentObservation(true);
				SyncEnvStatus();
                if (!success) PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.graphics.failed"));
			}
		}

		private bool RequireElevationFor(Toggle sw, bool restoredState)
		{
			if (elevated)
			{
				return true;
			}
			PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.performance.needadmin"));
			sw.SetSilently(restoredState);
			return false;
		}

		private void OnHagsToggle(object s, EventArgs e)
		{
			if (RequireElevationFor(swHags, CachedHagsPresentation().Checked))
			{
				if (swHags.Checked ? HagsTweak.Enable() : HagsTweak.Disable())
				{
					PaviseDialog.Info(this, "PAVISE AUTO", Lang.T("hags.reboot"));
				}
                else PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.graphics.failed"));
				QueueEnvironmentObservation(true);
				SyncHagsPresentation();
			}
		}

		private void RefreshEnvironmentStateAsync()
		{
			if (UiActive)
			{
				SyncEnvironmentToggles();
			}
		}

		private void SyncEnvironmentToggles()
		{
			SyncPerformanceEnvironmentStatus();
			SyncEnvironmentPrivacy();
			SyncEnvStatus();
		}

		internal static HagsUiPresentation DescribeHags(bool known, bool supported, bool on, bool owned, int? configured)
		{
			bool flag = configured == 2;
			bool flag2 = configured == 1;
			HagsUiPresentation hagsUiPresentation = new HagsUiPresentation();
			hagsUiPresentation.Checked = flag2 ? false : (owned || flag || (known && on));
			hagsUiPresentation.External = !owned && (flag || (known && on));
			hagsUiPresentation.Ink = Theme.Dim;
			HagsUiPresentation hagsUiPresentation2 = hagsUiPresentation;
			hagsUiPresentation2.Editable = known && supported;
			if (owned && !flag)
			{
				hagsUiPresentation2.Status = (flag2 ? AutoText("保存请求已改为关闭", "Saved request changed to off") : AutoText("保存请求未核验", "Saved request unverified"));
				hagsUiPresentation2.Status += ((!known) ? AutoText(" · 当前未核验", " · current state unverified") : (on ? AutoText(" · 当前已开启", " · currently on") : AutoText(" · 当前已关闭", " · currently off")));
				hagsUiPresentation2.Ink = Theme.Warning;
				return hagsUiPresentation2;
			}
			if (!known)
			{
				hagsUiPresentation2.Status = ((owned || flag) ? AutoText("已保存开启请求 · 当前未核验", "On request saved · current state unverified") : (flag2 ? AutoText("已配置关闭 · 当前未核验", "Off configured · current state unverified") : AutoText("当前状态未核验", "Current state unverified")));
				if (owned || flag)
				{
					hagsUiPresentation2.Ink = Theme.Warning;
				}
			}
			else if (!supported)
			{
				hagsUiPresentation2.Status = ((owned || flag) ? AutoText("已保存请求 · 当前设备不支持", "Request saved · current device unsupported") : AutoText("当前设备不支持", "Current device unsupported"));
				if (owned || flag)
				{
					hagsUiPresentation2.Ink = Theme.Warning;
				}
			}
			else if (on)
			{
				hagsUiPresentation2.Status = (flag2 ? AutoText("当前已开启 · 已请求关闭", "Currently on · off requested") : AutoText("当前已开启", "Currently on"));
				hagsUiPresentation2.Ink = (flag2 ? Theme.Warning : Theme.Accent);
			}
			else
			{
				hagsUiPresentation2.Status = ((owned || flag) ? AutoText("已请求开启 · 待重启核验", "On requested · verify after restart") : AutoText("当前已关闭", "Currently off"));
				if (owned || flag)
				{
					hagsUiPresentation2.Ink = Theme.Warning;
				}
			}
			return hagsUiPresentation2;
		}

		private HagsUiPresentation CachedHagsPresentation()
		{
			EnvironmentGraphicsObservation snapshot = environmentObservation.Snapshot;
			return DescribeHags(snapshot != null && snapshot.HagsKnown, snapshot != null && snapshot.HagsSupported, snapshot != null && snapshot.HagsOn, Settings.LoadCached("HagsOnByPavise", false), (snapshot == null) ? null : snapshot.HagsConfigured);
		}

		private void SyncHagsPresentation()
		{
			if (swHags != null && cardHags != null)
			{
				HagsUiPresentation hagsUiPresentation = CachedHagsPresentation();
				swHags.SetSilently(hagsUiPresentation.Checked);
				swHags.Enabled = hagsUiPresentation.Editable;
				cardHags.Desc = PerformanceGraphicsDetail("Hags");
				cardHags.SetStatus(hagsUiPresentation.Status, hagsUiPresentation.Ink);
				cardHags.SetLock("", false);
			}
		}

		private void SyncPerformanceEnvironmentStatus()
		{
			SyncEnvironmentPerformanceOptions();
			SyncPerformanceTuningEnvironment();
			SyncEnvironmentGameDvr();
			SyncGameLowLatencyControl();
			QueueEnvironmentObservation(false);
		}

		private void BuildEnvironmentPerformanceOptions(Control parent, ref int y)
		{
			environmentPerformanceSwitches.Clear();
			environmentPerformanceCards.Clear();
			AddEnvironmentPerformanceOption(parent, ref y, "GmCacheWarmV2", "gm.cachewarm", "env.cachewarm.manual");
			SyncEnvironmentPerformanceOptions();
		}

		private void BuildEnvironmentTimerOptions(Control parent, ref int y)
		{
			AddEnvironmentPerformanceOption(parent, ref y, "AutoTimerTick", "set.timertick", "env.timertick.manual");
			AddEnvironmentPerformanceOption(parent, ref y, "AutoGlobalTimer", "set.gtimer", "env.gtimer.manual");
			SyncEnvironmentPerformanceOptions();
		}

		private void AddEnvironmentPerformanceOption(Control parent, ref int y, string key, string title, string description)
		{
			Toggle toggle = MakeSwitch(CachedEnvironmentPreference(key), delegate
			{
				ChangeEnvironmentPerformanceOption(key);
			});
			toggle.Name = "environment" + key;
			toggle.AccessibleName = Lang.T(title);
			environmentPerformanceSwitches.Add(key, toggle);
			Control host = toggle;
			string desc = PerformanceEnvironmentExplanation(key, description);
			int num = FullTextCardHeight(desc, ScrollContentW, host, 88);
			if (key == "AutoGlobalTimer")
			{
				string readback = Lang.F("env.gtimer.readback", "0.5000", "15.6250", "0.5000");
				string[] array = new string[14]
				{
					"ready", "timer-locked", "timer-limited", "timer-unverified", "pending-reboot", "restore-pending-reboot", "unverified", "failed", "stopped", "restore-failed",
					"restored", "unsupported", "timer-requested", ""
				};
				foreach (string text in array)
				{
					num = Math.Max(num, FullTextCardHeight(GlobalTimerDescription(text, true, readback), ScrollContentW, host, 88));
					num = Math.Max(num, FullTextCardHeight(GlobalTimerDescription(text, false, readback), ScrollContentW, host, 88));
				}
			}
			int used;
			SettingCard settingCard = MakeAutoCard(parent, 6, y, ScrollContentW, num, Lang.T(title), desc, host, out used);
			settingCard.Height += Theme.S(17);
			y += used + 17 + 8;
			environmentPerformanceCards.Add(key, settingCard);
		}

		private void ChangeEnvironmentPerformanceOption(string key)
		{
			Toggle toggle = environmentPerformanceSwitches[key];
			bool restoredState = PerformancePolicy.EnvironmentPreference(key);
			if (!(key != "GmCacheWarmV2") || RequireElevationFor(toggle, restoredState))
			{
				bool flag = gameMode.SetEnvironmentPreference(key, toggle.Checked);
				if (flag)
				{
					environmentPreferenceOverrides[key] = toggle.Checked;
				}
				QueueEnvironmentObservation(true);
				SyncEnvironmentPerformanceOptions();
				if (!flag)
				{
					PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.performance.savefail"));
				}
			}
		}

		private void SyncEnvironmentPerformanceOptions()
		{
			foreach (KeyValuePair<string, Toggle> environmentPerformanceSwitch in environmentPerformanceSwitches)
			{
				string key = environmentPerformanceSwitch.Key;
				bool flag = CachedEnvironmentPreference(key);
				EnvironmentGraphicsObservation snapshot = environmentObservation.Snapshot;
				bool flag2 = snapshot != null && snapshot.GlobalTimerOwned;
				environmentPerformanceSwitch.Value.SetSilently(flag);
				environmentPerformanceSwitch.Value.Enabled = (key == "GmCacheWarmV2" || snapshot != null) && (key != "AutoGlobalTimer" || Native.OsBuild() >= 20348 || flag || flag2);
				SettingCard settingCard = environmentPerformanceCards[key];
				string text = gameMode.AutomaticExecutionState(key);
				object obj;
				if (key == "GmCacheWarmV2")
				{
					obj = (flag ? gameMode.CacheWarmStatus : Lang.T("env.performance.off"));
				}
				else
				{
					switch (text)
					{
					default:
						obj = (((key == "AutoGlobalTimer" && Native.OsBuild() < 20348 && !flag && !flag2) || text == "unsupported") ? Lang.T("lock.na") : (flag ? Lang.T("env.performance.pending") : Lang.T("env.performance.off")));
						break;
					case "restored":
						obj = PerformanceTuning.Label(text);
						break;
					case "ready":
						obj = Lang.T("env.performance.ready");
						break;
					case "timer-locked":
					case "timer-limited":
					case "timer-unverified":
						obj = GameMode.AutomationStateLabel(text);
						break;
					case "timer-requested":
						obj = Lang.T("env.performance.requested");
						break;
					case "failed":
					case "restore-failed":
						obj = Lang.T("env.performance.failed");
						break;
					case "stopped":
						obj = PerformanceTuning.Label(text);
						break;
					case "restore-pending-reboot":
						obj = Lang.T("env.performance.restore.reboot");
						break;
					case "pending-reboot":
						obj = Lang.T("env.performance.reboot");
						break;
					case "unverified":
						obj = Lang.T("env.performance.unverified");
						break;
					}
				}
				string text2 = (string)obj;
				Color ink;
				switch (text)
				{
				default:
					ink = (flag ? Theme.Accent : Theme.Dim);
					break;
				case "failed":
				case "restore-failed":
				case "stopped":
					ink = Theme.Warning;
					break;
				}
				settingCard.SetStatus(text2, ink);
				if (key == "AutoGlobalTimer")
				{
					string globalTimerResolutionDetail = gameMode.GlobalTimerResolutionDetail;
					settingCard.Desc = GlobalTimerDescription(text, flag, globalTimerResolutionDetail);
					settingCard.AccessibleDescription = settingCard.Desc;
				}
			}
		}

		private static string GlobalTimerDescription(string state, bool on, string readback)
		{
			string text;
			switch (state)
			{
			case "ready":
				text = "ready";
				break;
			case "timer-locked":
				text = "locked";
				break;
			case "timer-limited":
				text = "limited";
				break;
			case "timer-unverified":
				text = "query-unverified";
				break;
			case "pending-reboot":
			case "restore-pending-reboot":
			case "unverified":
			case "failed":
			case "stopped":
			case "restore-failed":
			case "restored":
			case "unsupported":
				text = state;
				break;
			case "timer-requested":
				text = "requested";
				break;
			default:
				text = (on ? "pending" : "off");
				break;
			}
			return PerformanceEnvironmentExplanation("AutoGlobalTimer", "env.gtimer.manual") + "\n" + Lang.T("env.gtimer.state." + text) + ((!on && !string.IsNullOrEmpty(readback)) ? ("\n" + Lang.T("env.gtimer.state.release-pending")) : "") + (string.IsNullOrEmpty(readback) ? "" : ("\n" + readback));
		}

		private static string PerformanceEnvironmentExplanation(string key, string fallback)
		{
			switch (key)
			{
			case "AutoGlobalTimer":
				return AutoText("开启或还原后需重启；仅对局请求 0.5 ms，退局释放。故障后可点重试，无需反复切换开关。实际精度由系统决定，可能增加功耗。", "Restart after enabling or restoring; requests 0.5 ms only during matches, then releases it. Use Retry after a failure. Windows decides the actual resolution; power use may rise.");
			case "AutoTimerTick":
				return AutoText("手动设置时钟与节拍启动参数，开关后需重启。切换模式不改动此项，关闭恢复原配置。", "Manual clock/tick boot settings; restart after changes. Mode switches leave this choice alone; Off restores originals.");
			case "GmCacheWarmV2":
				return AutoText("仅游戏待命且桌面空闲时预读，入局或关闭即取消。需交流电、足够内存与 SSD，不保证提帧。", "Pre-reads only while a game is armed and the desktop is idle; cancels on entry or Off. Requires AC, ample RAM and an SSD; no FPS gain is guaranteed.");
			default:
				return Lang.T(fallback);
			}
		}

		private PrivacyOptionsPanel BuildEnvironmentPrivacy(Control parent, PerformanceRestartScope scope, Predicate<PrivacyAction> filter = null)
		{
			PrivacyControl.AdmissionAllowed = () => !base.IsDisposed && !base.Disposing && !runtimeShutdownBusy && wipeBusy == 0 && !gameMode.IsActive;
			PrivacyOptionsPanel privacyOptionsPanel = new PrivacyOptionsPanel(() => !runtimeShutdownBusy && wipeBusy == 0 && !gameMode.IsActive, delegate(string message)
			{
				if (!base.IsDisposed && !base.Disposing)
				{
					PaviseDialog.Warn(this, "PAVISE AUTO", message);
				}
			}, elevated, (PrivacyAction action) => PerformanceRestartFor(action.Key) == scope && (filter == null ? action.Key != PrivacyActionCatalog.BackgroundGlobal : filter(action)), true);
			privacyOptionsPanel.Name = ((performancePrivacyPanels.Count == 0) ? "environmentPrivacy" : ("environmentPrivacy" + scope));
			privacyOptionsPanel.SetBounds(Theme.S(6), Theme.S(2), Theme.S(ScrollContentW), Math.Max(Theme.S(4), privacyOptionsPanel.Height));
			performancePrivacyPanels.Add(privacyOptionsPanel);
			if (environmentPrivacy == null)
			{
				environmentPrivacy = privacyOptionsPanel;
			}
			parent.Controls.Add(privacyOptionsPanel);
			privacyOptionsPanel.SyncCached();
			return privacyOptionsPanel;
		}

		private void SyncEnvironmentPrivacy()
		{
			foreach (PrivacyOptionsPanel performancePrivacyPanel in performancePrivacyPanels)
			{
				if (!performancePrivacyPanel.IsDisposed)
				{
					performancePrivacyPanel.SyncCached();
					performancePrivacyPanel.RefreshIfNeeded();
				}
			}
		}

		private static string PerformanceRecordingDetail()
		{
			return AutoText("守护期间关闭 Game DVR/Xbox 后台录制，即时回放与录制热键不可用。关闭开关、停守护或退出后还原；需要录屏时请关闭此项。", "Disables Game DVR/Xbox capture, instant replay and recording hotkeys while the guard runs. Off, guard stop or exit restores originals; turn this off to record.");
		}

		private void BuildEnvironmentRecordingOption(Control parent, ref int y)
		{
			swEnvironmentGameDvr = MakeSwitch(gameMode.KillGameDvr, OnEnvironmentGameDvrToggle);
			swEnvironmentGameDvr.Name = "environmentGameDvrOff";
			swEnvironmentGameDvr.AccessibleName = Lang.T("set.dvr");
			string text = PerformanceRecordingDetail();
			swEnvironmentGameDvr.AccessibleDescription = text;
			int used;
			cardEnvironmentGameDvr = MakeAutoCard(parent, 6, y, ScrollContentW, FullTextCardHeight(text, ScrollContentW, swEnvironmentGameDvr, 88), Lang.T("set.dvr"), text, swEnvironmentGameDvr, out used);
			cardEnvironmentGameDvr.Height += Theme.S(17);
			y += used + 17 + 8;
			SyncEnvironmentGameDvr();
		}

		private void OnEnvironmentGameDvrToggle(object sender, EventArgs e)
		{
			bool flag = gameMode.SetGameDvrSuppression(swEnvironmentGameDvr.Checked);
			SyncEnvironmentGameDvr();
			if (!flag)
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.performance.savefail"));
			}
		}

		private string EnvironmentGameDvrStatus()
		{
			string text = gameMode.AutomaticExecutionState("GameDvrOff");
			switch (text)
			{
			case "failed":
			case "restore-failed":
			case "stopped":
				return GameMode.AutomationStateLabel(text);
			default:
				if (!gameMode.KillGameDvr)
				{
					return Lang.T((text == "restored") ? "env.dvr.restored" : "env.performance.off");
				}
				if (!gameMode.Enabled)
				{
					return Lang.T("env.dvr.waiting");
				}
				return Lang.T((text == "applied") ? "env.dvr.active" : "env.performance.pending");
			}
		}

		private void SyncEnvironmentGameDvr()
		{
			if (swEnvironmentGameDvr != null && cardEnvironmentGameDvr != null)
			{
				swEnvironmentGameDvr.SetSilently(gameMode.KillGameDvr);
				string text = gameMode.AutomaticExecutionState("GameDvrOff");
				bool flag = text == "failed" || text == "restore-failed" || text == "stopped";
				cardEnvironmentGameDvr.SetStatus(EnvironmentGameDvrStatus(), flag ? Theme.Warning : (gameMode.KillGameDvr ? Theme.Accent : Theme.Dim));
			}
		}

		internal static string ConcisePerformanceDetail(string original, string explanation, bool keepStates = true)
		{
			int num = ((original == null) ? (-1) : original.IndexOf('\n'));
			if (num > 0 && original[num - 1] == '\r')
			{
				num--;
			}
			return explanation + ((keepStates && num >= 0) ? original.Substring(num) : "");
		}

		private static string EnvironmentBundlePerformanceDetail(string original = null)
		{
			return ConcisePerformanceDetail(original, AutoText("关闭 VBS/内存完整性和 CPU 缓解，调整 NTFS 访问时间与音频电源。需重启；降低防护并影响 WSL2/Hyper-V，关闭恢复原配置。", "Disables VBS/Memory Integrity and CPU mitigations; adjusts NTFS access times and audio power. Restart required; reduces protection and may affect WSL2/Hyper-V; Off restores originals."));
		}

		internal static string CondenseStoragePerformanceDetail(string original, string state, string[] memberStates)
		{
			bool flag = memberStates != null && memberStates.Length == 3 && (state == "off" || state == "restored" || state == "ready");
			if (flag)
			{
				foreach (string text in memberStates)
				{
					flag &= ((state == "ready") ? (text == "ready" || text == "applied" || text == "configured") : (text == "" || text == "off" || (state == "restored" && text == "restored")));
				}
			}
			return ConcisePerformanceDetail(original, AutoText("关闭 StorPort 设备的空闲、运行时与 D3Cold 节能；需重启，可能增加功耗。关闭恢复原配置，不适用项跳过。", "Disables StorPort idle/runtime/D3Cold power saving. Restart required; power use may rise; Off restores originals and unsupported devices are skipped."), !flag);
		}

		private static string StoragePerformanceDetail(string state, string original = null, string[] memberStates = null)
		{
			return CondenseStoragePerformanceDetail(original, state, memberStates);
		}

		private void BuildPerformanceTuningEnvironment(Control parent, ref int y)
		{
			swPerformanceEnvironmentBundle = MakeSwitch(false, ChangePerformanceTuningBundle);
			swPerformanceEnvironmentBundle.Name = "environmentEnvPerformanceBundleV1";
			swPerformanceEnvironmentBundle.AccessibleName = PerformanceTuning.EnvironmentBundleTitle;
			string desc = EnvironmentBundlePerformanceDetail();
			int used;
			cardPerformanceEnvironmentBundle = MakeAutoCard(parent, 6, y, ScrollContentW, FullTextCardHeight(desc, ScrollContentW, swPerformanceEnvironmentBundle, 100), PerformanceTuning.EnvironmentBundleTitle, desc, swPerformanceEnvironmentBundle, out used);
			cardPerformanceEnvironmentBundle.Height += Theme.S(17);
			y += used + 17 + 8;
			BuildPerformanceCpuMitigations(parent, ref y);
			SyncPerformanceTuningEnvironment();
		}

		private void BuildPerformanceQuantumEnvironment(Control parent, ref int y)
		{
			PerformanceAction performanceAction = PerformanceActionCatalog.Find("EnvQuantumProfileV1");
			string text = performanceAction.Title;
			string text2 = AutoText("游戏/日常选短可变 3:1，后台计算选长固定 1:1；比例不是帧率倍数。优先在线应用和恢复，系统拒绝或原值无法在线还原时提示重启电脑。“恢复原配置”也撤销旧版校正。", "Use short variable 3:1 for games/daily use or long fixed 1:1 for background work; these are turn ratios, not FPS multipliers. Apply and restore online where supported; a PC restart is shown if Windows rejects the update or the original cannot be restored online. Restore original also undoes legacy corrections.");
			int used;
			swPerformanceEnvironmentQuantum = new QuantumProfilePicker();
			swPerformanceEnvironmentQuantum.SetSilently(0);
			swPerformanceEnvironmentQuantum.SelectionChanged += ChangePerformanceTuningQuantum;
			swPerformanceEnvironmentQuantum.Name = "environment" + performanceAction.Key;
			swPerformanceEnvironmentQuantum.AccessibleName = text;
			swPerformanceEnvironmentQuantum.AccessibleDescription = text2;
			cardPerformanceEnvironmentQuantum = MakeAutoCard(parent, 6, y, ScrollContentW, FullTextCardHeight(text2, ScrollContentW, swPerformanceEnvironmentQuantum, 88), text, text2, swPerformanceEnvironmentQuantum, out used);
			cardPerformanceEnvironmentQuantum.Height += Theme.S(17);
			y += used + 17 + 8;
			SyncPerformanceTuningEnvironment();
		}

		private void ChangePerformanceTuningBundle(object sender, EventArgs args)
		{
			if (!RequireElevationFor(swPerformanceEnvironmentBundle, PerformanceTuning.EnvironmentBundleEnabled))
			{
				return;
			}
			bool @checked = swPerformanceEnvironmentBundle.Checked;
			bool flag = PerformanceTuning.SetEnvironmentBundle(@checked, delegate
			{
				try
				{
					if (!base.IsDisposed && base.IsHandleCreated)
					{
						BeginInvoke((Action)delegate
						{
							RefreshPerformanceEnvironmentObservation();
						});
					}
				}
				catch
				{
				}
			});
			if (flag)
			{
				environmentBundleOverride = @checked;
				gameMode.Poke();
			}
			RefreshPerformanceEnvironmentObservation();
			if (!flag)
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.performance.savefail"));
			}
		}

		private void ChangePerformanceTuningQuantum(object sender, EventArgs args)
		{
			int selectedIndex = swPerformanceEnvironmentQuantum.SelectedIndex;
			if (!elevated)
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.performance.needadmin"));
				SyncPerformanceTuningEnvironment();
				return;
			}
			bool flag = PerformanceTuning.SetQuantumProfile(selectedIndex, delegate
			{
				try
				{
					if (!base.IsDisposed && base.IsHandleCreated)
					{
						BeginInvoke((Action)delegate
						{
							RefreshPerformanceEnvironmentObservation();
						});
					}
				}
				catch
				{
				}
			});
			if (flag)
			{
				environmentQuantumOverride = selectedIndex;
				gameMode.Poke();
			}
			RefreshPerformanceEnvironmentObservation();
			if (!flag)
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.performance.savefail"));
			}
		}

		private void SyncPerformanceTuningEnvironment()
		{
			if (swPerformanceEnvironmentBundle == null || swPerformanceEnvironmentBundle.IsDisposed || cardPerformanceEnvironmentBundle == null || cardPerformanceEnvironmentBundle.IsDisposed)
			{
				return;
			}
			EnvironmentTuningObservation cachedEnvironmentTuning = CachedEnvironmentTuning;
			bool flag = PerformanceTuning.EnvironmentBundleBusy();
			bool flag2 = environmentBundleOverride ?? (cachedEnvironmentTuning != null && cachedEnvironmentTuning.BundleOn);
			string text = (flag ? "working" : ((cachedEnvironmentTuning == null) ? "unverified" : cachedEnvironmentTuning.BundleState));
			swPerformanceEnvironmentBundle.SetSilently(flag2);
			swPerformanceEnvironmentBundle.Enabled = cachedEnvironmentTuning != null && !flag;
			string text2 = ((cachedEnvironmentTuning == null) ? EnvironmentBundlePerformanceDetail() : cachedEnvironmentTuning.BundleDetail);
			if (cardPerformanceEnvironmentBundle.Desc != text2)
			{
				SettingCard settingCard = cardPerformanceEnvironmentBundle;
				settingCard.Desc = text2;
				if (settingCard is PerformanceFeatureCard)
				{
					((PerformanceFeatureGrid)settingCard.Parent).Reflow();
				}
				else
				{
					int num = Theme.S(FullTextCardHeight(text2, ScrollContentW, swPerformanceEnvironmentBundle, 100) + 17);
					int num2 = (settingCard.Collapsible ? settingCard.ExpandedHeight : settingCard.Height);
					Dictionary<Control, int> value;
					if (settingCard.Parent != null && stackBase.TryGetValue(settingCard.Parent, out value))
					{
						int num3 = value[settingCard];
						foreach (Control item in new List<Control>(value.Keys))
						{
							if (value[item] > num3)
							{
								value[item] += num - num2;
							}
						}
					}
					settingCard.ExpandedHeight = num;
					settingCard.Height = ((settingCard.Collapsible && !settingCard.Expanded) ? settingCard.CollapsedHeight : num);
					if (settingCard.Parent != null)
					{
						RestackCards(settingCard.Parent);
					}
				}
			}
			SettingCard settingCard2 = cardPerformanceEnvironmentBundle;
			string obj = ((flag || cachedEnvironmentTuning == null) ? PerformanceTuning.Label(text) : cachedEnvironmentTuning.BundleLabel);
			Color ink;
			switch (text)
			{
			default:
				ink = (flag2 ? Theme.Accent : Theme.Dim);
				break;
			case "failed":
			case "stopped":
			case "restore-failed":
			case "partial":
				ink = Theme.Warning;
				break;
			}
			settingCard2.SetStatus(obj, ink);
			SyncPerformanceCpuMitigations();
			SyncPerformanceStorageEnvironment();
			if (swPerformanceEnvironmentQuantum != null && !swPerformanceEnvironmentQuantum.IsDisposed && cardPerformanceEnvironmentQuantum != null && !cardPerformanceEnvironmentQuantum.IsDisposed)
			{
				bool flag3 = (environmentQuantumOverride.HasValue ? (environmentQuantumOverride.Value != 0) : (cachedEnvironmentTuning != null && cachedEnvironmentTuning.QuantumOn));
				string text3 = (flag ? "working" : ((cachedEnvironmentTuning == null) ? "unverified" : cachedEnvironmentTuning.QuantumState));
				int num4 = environmentQuantumOverride ?? ((cachedEnvironmentTuning != null) ? cachedEnvironmentTuning.QuantumSelection : 0);
				swPerformanceEnvironmentQuantum.SetSilently(num4);
				swPerformanceEnvironmentQuantum.AccessibleDescription = QuantumProfiles.Description(num4);
				swPerformanceEnvironmentQuantum.Enabled = cachedEnvironmentTuning != null && !flag;
				bool flag4 = text3 == "failed" || text3 == "stopped" || text3 == "restore-failed";
				bool flag5 = !flag4 && text3 != "working" && cachedEnvironmentTuning != null && cachedEnvironmentTuning.LegacyQuantum;
				string text4 = (flag5 ? PerformancePolicy.Text("旧版校正记录待恢复 · 选择「恢复原配置」", "Legacy correction pending · choose Restore original") : ((text3.Length == 0) ? Lang.T(flag3 ? "env.performance.pending" : "env.performance.off") : PerformanceTuning.Label(text3)));
				cardPerformanceEnvironmentQuantum.SetStatus(text4, (flag4 || flag5) ? Theme.Warning : (flag3 ? Theme.Accent : Theme.Dim));
				PerformanceFeatureCard quantumCard = cardPerformanceEnvironmentQuantum as PerformanceFeatureCard;
				if (quantumCard != null)
				{
					bool restart = text3 == "pending-reboot" || text3 == "restore-pending-reboot";
					quantumCard.RestartScope = restart ? PerformanceRestartScope.SystemRestart : PerformanceRestartScope.None;
					quantumCard.RestartText = restart ? AutoText("需重启电脑", "PC restart required") : AutoText("优先在线应用", "Online update preferred");
				}
			}
		}

		private void BuildPerformanceStorageEnvironment(Control parent, ref int y)
		{
			swPerformanceEnvironmentStorage = MakeSwitch(false, ChangePerformanceStorageEnvironment);
			swPerformanceEnvironmentStorage.Name = "environmentEnvStoragePowerGroupV1";
			swPerformanceEnvironmentStorage.AccessibleName = PerformanceTuning.StorageGroupTitle;
			string desc = StoragePerformanceDetail("off");
			int used;
			cardPerformanceEnvironmentStorage = MakeAutoCard(parent, 6, y, ScrollContentW, FullTextCardHeight(desc, ScrollContentW, swPerformanceEnvironmentStorage, 100), PerformanceTuning.StorageGroupTitle, desc, swPerformanceEnvironmentStorage, out used);
			cardPerformanceEnvironmentStorage.Height += Theme.S(17);
			y += used + 17 + 8;
			SyncPerformanceStorageEnvironment();
		}

		private void ChangePerformanceStorageEnvironment(object sender, EventArgs args)
		{
			if (!RequireElevationFor(swPerformanceEnvironmentStorage, PerformanceTuning.StorageGroupEnabled))
			{
				return;
			}
			bool @checked = swPerformanceEnvironmentStorage.Checked;
			bool flag = PerformanceTuning.SetStorageGroup(@checked, delegate
			{
				try
				{
					if (!base.IsDisposed && base.IsHandleCreated)
					{
						BeginInvoke((Action)delegate
						{
							RefreshPerformanceEnvironmentObservation();
						});
					}
				}
				catch
				{
				}
			});
			if (flag)
			{
				environmentStorageOverride = @checked;
				gameMode.Poke();
			}
			RefreshPerformanceEnvironmentObservation();
			if (!flag)
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.performance.savefail"));
			}
		}

		private void SyncPerformanceStorageEnvironment()
		{
			if (swPerformanceEnvironmentStorage == null || swPerformanceEnvironmentStorage.IsDisposed || cardPerformanceEnvironmentStorage == null || cardPerformanceEnvironmentStorage.IsDisposed)
			{
				return;
			}
			EnvironmentTuningObservation cachedEnvironmentTuning = CachedEnvironmentTuning;
			bool flag = PerformanceTuning.EnvironmentBundleBusy();
			bool flag2 = environmentStorageOverride ?? (cachedEnvironmentTuning != null && cachedEnvironmentTuning.StorageOn);
			string text = (flag ? "working" : ((cachedEnvironmentTuning == null) ? "unverified" : cachedEnvironmentTuning.StorageState));
			swPerformanceEnvironmentStorage.SetSilently(flag2);
			swPerformanceEnvironmentStorage.Enabled = cachedEnvironmentTuning != null && !flag;
			SettingCard settingCard = cardPerformanceEnvironmentStorage;
			string text2 = ((cachedEnvironmentTuning == null) ? StoragePerformanceDetail(text) : cachedEnvironmentTuning.StorageDetail);
			if (settingCard.Desc != text2)
			{
				settingCard.Desc = text2;
				if (settingCard is PerformanceFeatureCard)
				{
					((PerformanceFeatureGrid)settingCard.Parent).Reflow();
				}
				else
				{
					int num = Theme.S(FullTextCardHeight(text2, ScrollContentW, swPerformanceEnvironmentStorage, 100) + 17);
					int num2 = (settingCard.Collapsible ? settingCard.ExpandedHeight : settingCard.Height);
					Dictionary<Control, int> value;
					int value2;
					if (settingCard.Parent != null && stackBase.TryGetValue(settingCard.Parent, out value) && value.TryGetValue(settingCard, out value2))
					{
						foreach (Control item in new List<Control>(value.Keys))
						{
							if (value[item] > value2)
							{
								value[item] += num - num2;
							}
						}
					}
					settingCard.ExpandedHeight = num;
					settingCard.Height = ((settingCard.Collapsible && !settingCard.Expanded) ? settingCard.CollapsedHeight : num);
					if (settingCard.Parent != null)
					{
						RestackCards(settingCard.Parent);
					}
				}
			}
			string obj = ((flag || cachedEnvironmentTuning == null) ? PerformanceTuning.Label(text) : cachedEnvironmentTuning.StorageLabel);
			Color ink;
			switch (text)
			{
			default:
				ink = (flag2 ? Theme.Accent : Theme.Dim);
				break;
			case "failed":
			case "stopped":
			case "restore-failed":
			case "partial":
			case "recovery-pending":
				ink = Theme.Warning;
				break;
			}
			settingCard.SetStatus(obj, ink);
		}

		private void ShowQuantumProfileSetting()
		{
			ShowPage(5);
			SettingCard settingCard = cardPerformanceEnvironmentQuantum;
			if (settingCard != null)
			{
				ScrollableControl scrollableControl = settingCard.Parent as ScrollableControl;
				PerformanceFeatureGrid performanceFeatureGrid = scrollableControl as PerformanceFeatureGrid;
				if (performanceFeatureGrid != null)
				{
					performanceFeatureGrid.Reveal(settingCard);
				}
				else if (scrollableControl != null)
				{
					scrollableControl.ScrollControlIntoView(settingCard);
				}
				if (swPerformanceEnvironmentQuantum != null && swPerformanceEnvironmentQuantum.CanFocus)
				{
					swPerformanceEnvironmentQuantum.Focus();
				}
			}
		}

		internal static int NvllIndexOf(string mode)
		{
			if (!(mode == "on"))
			{
				if (!(mode == "ultra"))
				{
					return 0;
				}
				return 2;
			}
			return 1;
		}

		internal static string NvllModeOf(int index)
		{
			switch (index)
			{
			default:
				return "off";
			case 2:
				return "ultra";
			case 1:
				return "on";
			}
		}

		internal static int DlssIndexOf(string mode)
		{
			switch (mode)
			{
			default:
				return 0;
			case "k":
				return 3;
			case "j":
				return 2;
			case "latest":
				return 1;
			}
		}

		internal static string DlssModeOf(int index)
		{
			switch (index)
			{
			default:
				return "off";
			case 3:
				return "k";
			case 2:
				return "j";
			case 1:
				return "latest";
			}
		}

		private void MonoLabel(Control parent, string text, int x, int y, int w)
		{
			Label label = new Label();
			label.Text = text;
			label.ForeColor = Theme.Faint;
			label.BackColor = Color.Transparent;
			label.Font = Theme.Mono(6.2f);
			label.SetBounds(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(14));
			parent.Controls.Add(label);
		}

		private static string IrqReasonText(string key)
		{
			if (!string.IsNullOrEmpty(key))
			{
				return Lang.T(key);
			}
			return "";
		}

		internal static string IrqHintText(GpuIrqState state, bool awaiting, out Color color)
		{
			color = Theme.Dim;
			switch (state.Phase)
			{
			case GpuIrqPhase.Pending:
				color = Theme.Warning;
				return Lang.T(awaiting ? "irqcheck.hint.pending" : "irqcheck.hint.verify");
			case GpuIrqPhase.Applied:
				color = Theme.Green;
				return Lang.F("irqcheck.hint.applied", IrqRelocate.MaskText(state.Target));
			case GpuIrqPhase.Clear:
				color = Theme.Green;
				return Lang.T("irqcheck.hint.clear");
			case GpuIrqPhase.NoRegion:
				return Lang.T("irqcheck.hint.noregion");
			case GpuIrqPhase.RolledBack:
				color = Theme.Danger;
				return Lang.F("irqcheck.hint.rolledback", IrqReasonText(state.Reason));
			case GpuIrqPhase.Skipped:
				return Lang.F("irqcheck.hint.skipped", IrqReasonText(state.Reason));
			default:
				return Lang.T("irqcheck.hint.none");
			}
		}

		internal static string IrqBannerState(GpuIrqState state, bool awaiting, out Color color)
		{
			switch (state.Phase)
			{
			case GpuIrqPhase.Pending:
				color = Theme.Warning;
				if (!awaiting)
				{
					return "VERIFY PENDING";
				}
				return "RESTART PENDING";
			case GpuIrqPhase.Applied:
				color = Theme.Green;
				return "ROUTED";
			case GpuIrqPhase.Clear:
				color = Theme.Green;
				return "CLEAR";
			case GpuIrqPhase.NoRegion:
				color = Theme.Dim;
				return "NO GAME CORES";
			case GpuIrqPhase.RolledBack:
				color = Theme.Danger;
				return "ROLLED BACK";
			case GpuIrqPhase.Skipped:
				color = Theme.Dim;
				return "SKIPPED";
			default:
				color = Theme.Accent;
				return "READY";
			}
		}

		internal static string IrqStateText(GpuIrqState state, bool awaiting, out Color color)
		{
			color = Theme.Fg;
			switch (state.Phase)
			{
			case GpuIrqPhase.Pending:
				color = Theme.Warning;
				return Lang.T(awaiting ? "irqcheck.state.pending" : "irqcheck.state.verify");
			case GpuIrqPhase.Applied:
				color = Theme.Green;
				return Lang.T("irqcheck.state.applied");
			case GpuIrqPhase.Clear:
				color = Theme.Green;
				return Lang.T("irqcheck.state.clear");
			case GpuIrqPhase.NoRegion:
				color = Theme.Dim;
				return Lang.T("irqcheck.state.noregion");
			case GpuIrqPhase.RolledBack:
				color = Theme.Danger;
				return Lang.T("irqcheck.state.rolledback");
			case GpuIrqPhase.Skipped:
				color = Theme.Dim;
				return IrqReasonText(state.Reason);
			default:
				color = Theme.Dim;
				return Lang.T("irqcheck.state.none");
			}
		}

		internal static string IrqResultText(GpuIrqResult r, out DlgKind kind)
		{
			kind = DlgKind.Info;
			string text = ((r.Gpu == null) ? "—" : IrqRelocate.MaskText(r.Gpu.Seen));
			string text2;
			switch (r.Outcome)
			{
			case GpuIrqOutcome.Failed:
				kind = DlgKind.Danger;
				text2 = Lang.F("irqcheck.out.failed", r.Detail);
				break;
			case GpuIrqOutcome.AwaitingReboot:
				kind = DlgKind.Warn;
				text2 = Lang.T("irqcheck.out.awaiting");
				break;
			case GpuIrqOutcome.NoEvidence:
				kind = DlgKind.Warn;
				text2 = Lang.F("irqcheck.out.noevidence", (r.Gpu == null) ? 0 : r.Gpu.Events, 300L);
				break;
			case GpuIrqOutcome.NoRegion:
				text2 = Lang.T("irqcheck.out.noregion");
				break;
			case GpuIrqOutcome.Restored:
				kind = DlgKind.Warn;
				text2 = Lang.T("irqcheck.out.restored") + ((r.Detail.Length > 0) ? (" · " + r.Detail) : "");
				break;
			case GpuIrqOutcome.NoTarget:
				text2 = Lang.T("irqcheck.reason.notarget");
				break;
			case GpuIrqOutcome.Clear:
				kind = DlgKind.Success;
				text2 = Lang.F("irqcheck.out.clear", text, IrqRelocate.MaskText(r.Region));
				break;
			case GpuIrqOutcome.Scheduled:
				kind = DlgKind.Warn;
				text2 = Lang.F("irqcheck.out.scheduled", text, IrqRelocate.MaskText(r.Region), IrqRelocate.MaskText(r.Target));
				break;
			case GpuIrqOutcome.Verified:
				kind = DlgKind.Success;
				text2 = Lang.F("irqcheck.out.verified", text);
				break;
			case GpuIrqOutcome.RolledBack:
				kind = DlgKind.Danger;
				text2 = Lang.F("irqcheck.out.rolledback", r.Detail);
				break;
			case GpuIrqOutcome.Skipped:
				text2 = Lang.F("irqcheck.out.skipped", IrqReasonText(GpuIrqCheck.LoadState().Reason));
				break;
			default:
				text2 = Lang.T("irqcheck.out.cancelled");
				break;
			}
			if (r.Outcome == GpuIrqOutcome.Failed || r.Outcome == GpuIrqOutcome.AwaitingReboot || r.Outcome == GpuIrqOutcome.Cancelled)
			{
				return text2;
			}
			if (r.Slow == null || r.Slow.Count == 0)
			{
				return text2 + "\r\n\r\n" + Lang.T("irqcheck.out.noslow");
			}
			StringBuilder stringBuilder = new StringBuilder(text2);
			stringBuilder.Append("\r\n\r\n").Append(Lang.T("irqcheck.out.slow"));
			foreach (SlowDriver item in r.Slow)
			{
				stringBuilder.Append("\r\n· ").Append((item.Device.Length > 0) ? (item.Device + " (" + item.Driver + ")") : item.Driver).Append("  ")
					.Append(Lang.F("irqcheck.slow.row", item.MaxUs.ToString("F0"), item.P99Us.ToString("F0")));
			}
			return stringBuilder.ToString();
		}

		private void OnIrqCheck(object sender, EventArgs e)
		{
			RunIrqCheck();
		}

		private GpuIrqResult RunIrqCheck()
		{
			if (!Native.IsElevated())
			{
				PaviseDialog.Warn(this, Lang.T("nav.irq"), Lang.T("irqcheck.needadmin"));
				return null;
			}
			if (gameMode.IsActive)
			{
				PaviseDialog.Warn(this, Lang.T("nav.irq"), Lang.T("irqcheck.ingame"));
				return null;
			}
			if (GpuIrqCheck.AwaitingReboot(GpuIrqCheck.LoadState()))
			{
				PaviseDialog.Info(this, Lang.T("nav.irq"), Lang.T("irqcheck.out.awaiting"));
				return null;
			}
			GpuIrqResult result;
			using (IrqCheckDialog irqCheckDialog = new IrqCheckDialog((Func<bool> cancelled, Action<float> progress) => GpuIrqCheck.Run(() => cancelled() || gameMode.IsActive, progress)))
			{
				irqCheckDialog.ShowDialog(this);
				result = irqCheckDialog.Result;
			}
			if (result == null)
			{
				return null;
			}
			DlgKind kind;
			string body = IrqResultText(result, out kind);
			switch (kind)
			{
			case DlgKind.Success:
				PaviseDialog.Success(this, Lang.T("irqcheck.dialog.title"), body);
				break;
			case DlgKind.Danger:
				PaviseDialog.Error(this, Lang.T("irqcheck.dialog.title"), body);
				break;
			case DlgKind.Warn:
				PaviseDialog.Warn(this, Lang.T("irqcheck.dialog.title"), body);
				break;
			default:
				PaviseDialog.Info(this, Lang.T("irqcheck.dialog.title"), body);
				break;
			}
			return result;
		}

		private void OnIrqRestore(object sender, EventArgs e)
		{
			if (!Native.IsElevated())
			{
				return;
			}
			if (gameMode.IsActive)
			{
				PaviseDialog.Warn(this, Lang.T("nav.audit"), Lang.T("irqcheck.ingame"));
			}
			else
			{
				if (!PaviseDialog.Confirm(this, Lang.T("irqcheck.restore.btn"), Lang.T("irqcheck.restore.confirm"), DlgKind.Warn))
				{
					return;
				}
				bool flag = true;
				try
				{
					if (GpuIrqCheck.HasResidue)
					{
						flag &= GpuIrqCheck.RestoreAll();
					}
				}
				catch
				{
					flag = false;
				}
				try
				{
					if (IrqRelocate.HasResidue || IrqPriorityTweak.HasResidue)
					{
						IrqRestoreResult irqRestoreResult = IrqRelocate.RestoreDevices(null);
						flag &= irqRestoreResult != null && irqRestoreResult.Success;
					}
				}
				catch
				{
					flag = false;
				}
				if (flag)
				{
					PaviseDialog.Success(this, Lang.T("irqcheck.restore.btn"), Lang.T("irqcheck.restore.done"));
				}
				else
				{
					PaviseDialog.Warn(this, Lang.T("irqcheck.restore.btn"), Lang.T("irqcheck.restore.failed"));
				}
			}
		}

		private Dictionary<string, bool> QueryLibraryRunningState(List<string> paths)
		{
			return ProbeRunning(paths);
		}

		public void NotifyLibraryChanged()
		{
			try
			{
				if (!base.IsHandleCreated || base.IsDisposed)
				{
					return;
				}
				BeginInvoke((MethodInvoker)delegate
				{
					if (!base.IsDisposed && lstGames != null)
					{
						RefreshGames();
					}
				});
			}
			catch
			{
			}
		}

		private void ToggleGameCoreScheduling(GameLibraryItem item)
		{
			if (item == null || item.Profile == null)
			{
				return;
			}
			string id = item.Profile.Id;
			GameProfile gameProfile = FindLibraryProfile(id);
			if (gameProfile == null)
			{
				RefreshGames();
				return;
			}
			if (PolicyResolver.For(gameProfile).ManualCores == null)
			{
				OpenLibraryCoreScheduling(id);
				return;
			}
			bool flag = !PolicyResolver.For(gameProfile).AutoCoresAllowed;
			string error = SaveCoreConfigurationWithRender(gameProfile, ManualCorePlan.Raw(gameProfile), PolicyResolver.For(gameProfile).ManualCores, flag);
			if (error != null && !gameMode.ProfileStoreSaveFailed)
			{
				PaviseDialog.Warn(LibraryDialogOwner, Lang.T("nav.corescheduling"), Lang.T(error));
			}
			RefreshGames();
			SelectProfile(id);
		}

		private void ToggleGameFullscreenOptimizations(GameLibraryItem item)
		{
			if (fullscreenOptimizationsChangeBusy || item == null || item.Profile == null)
			{
				return;
			}
			fullscreenOptimizationsChangeBusy = true;
			string id = item.Profile.Id;
			try
			{
				GameProfile gameProfile = FindLibraryProfile(id);
				if (gameProfile != null && !gameMode.SetProfileFullscreenOptimizations(id, !gameProfile.DisableFullscreenOptimizations) && !gameMode.ProfileStoreSaveFailed)
				{
					PaviseDialog.Warn(LibraryDialogOwner, Lang.T("lib.fso"), Lang.T("lib.fso.failed"));
				}
			}
			finally
			{
				fullscreenOptimizationsChangeBusy = false;
				if (!base.IsDisposed && !base.Disposing)
				{
					RefreshGames();
					SelectProfile(id);
				}
			}
		}

		private async void ToggleGameRebar(GameLibraryItem item)
		{
			if (rebarChangeBusy || item == null || item.Profile == null)
			{
				return;
			}
			string id = item.Profile.Id;
			GameProfile current = FindLibraryProfile(id);
			if (current == null)
			{
				RefreshGames();
				return;
			}
			bool enable = !PolicyResolver.For(current).NvRebar;
			string targetPath = current.ExecutablePath;
			GameConfigurationDialog initiator = libraryConfigurationDialog;
			rebarChangeBusy = true;
			try
			{
				if (enable)
				{
					string blocked = await Task.Run((Func<string>)RebarProbe.NvidiaEnableBlockReason);
					if (base.IsDisposed || base.Disposing || (initiator != null && (initiator.IsDisposed || libraryConfigurationDialog != initiator || !initiator.Visible)))
					{
						return;
					}
					current = FindLibraryProfile(id);
					if (current == null || !string.Equals(current.ExecutablePath, targetPath, StringComparison.OrdinalIgnoreCase))
					{
						return;
					}
					if (blocked != null)
					{
						PaviseDialog.Warn(LibraryDialogOwner, Lang.T("lib.rebar"), Lang.T(blocked));
						return;
					}
					if (!PaviseDialog.Confirm(LibraryDialogOwner, Lang.T("lib.rebar"), Lang.T("lib.rebar.warn"), DlgKind.Warn) || base.IsDisposed || base.Disposing)
					{
						return;
					}
					current = FindLibraryProfile(id);
					if (current == null || !string.Equals(current.ExecutablePath, targetPath, StringComparison.OrdinalIgnoreCase))
					{
						return;
					}
				}
				if (!gameMode.SetProfileOverride(id, "GameNvRebarV1", enable ? "1" : "0") && !gameMode.ProfileStoreSaveFailed)
				{
					PaviseDialog.Warn(LibraryDialogOwner, Lang.T("lib.rebar"), Lang.T("lib.family.save.failed"));
				}
			}
			finally
			{
				rebarChangeBusy = false;
				if (!base.IsDisposed && !base.Disposing)
				{
					RefreshGames();
					SelectProfile(id);
				}
			}
		}

		private void ToggleGameFamilySuppression(GameLibraryItem item)
		{
			if (familyChangeBusy || item == null || item.Profile == null)
			{
				return;
			}
			familyChangeBusy = true;
			string id = item.Profile.Id;
			try
			{
				GameProfile gameProfile = FindLibraryProfile(id);
				if (gameProfile == null)
				{
					return;
				}
				bool flag = !gameProfile.SuppressFamilyBackground;
				string executablePath = gameProfile.ExecutablePath;
				if (flag && !item.RendererObserved)
				{
					PaviseDialog.Warn(LibraryDialogOwner, Lang.T("lib.family.suppress"), Lang.T("lib.family.gated.body"));
					return;
				}
				if (flag)
				{
					using (FamilySuppressionDialog familySuppressionDialog = new FamilySuppressionDialog(gameProfile.Name, executablePath))
					{
						if (familySuppressionDialog.ShowDialog(LibraryDialogOwner) != DialogResult.OK)
						{
							return;
						}
					}
					GameProfile gameProfile2 = FindLibraryProfile(id);
					if (gameProfile2 == null)
					{
						return;
					}
					if (!string.Equals(gameProfile2.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase))
					{
						PaviseDialog.Warn(LibraryDialogOwner, Lang.T("lib.family.suppress"), Lang.T("lib.family.changed"));
						return;
					}
				}
				if (!gameMode.SetProfileFamilySuppression(id, flag, executablePath) && !gameMode.ProfileStoreSaveFailed)
				{
					PaviseDialog.Warn(LibraryDialogOwner, Lang.T("lib.family.suppress"), Lang.T("lib.family.save.failed"));
				}
			}
			finally
			{
				familyChangeBusy = false;
				RefreshGames();
				SelectProfile(id);
			}
		}

		private GameProfile FindLibraryProfile(string id)
		{
			foreach (GameProfile profile in gameMode.GetProfiles())
			{
				if (string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase))
				{
					return profile;
				}
			}
			return null;
		}

		private void ShowLibraryMore(GameLibraryItem selected, Control anchor)
		{
			GameCardMenuButton gameCardMenuButton = anchor as GameCardMenuButton;
			if (selected != null && selected.Profile != null && gameCardMenuButton != null && !gameCardMenuButton.IsDisposed)
			{
				gameCardMenuButton.ShowMenu(BuildLibraryMoreMenu(selected));
			}
		}

		private ContextMenuStrip BuildLibraryMoreMenu(GameLibraryItem selected)
		{
			string id = selected.Profile.Id;
			ContextMenuStrip contextMenuStrip = new ContextMenuStrip();
			contextMenuStrip.Renderer = new TechMenuRenderer();
			contextMenuStrip.BackColor = Theme.Card;
			contextMenuStrip.ForeColor = Theme.Fg;
			contextMenuStrip.Font = Theme.UI(9f, false);
			contextMenuStrip.ShowImageMargin = false;
			contextMenuStrip.ShowCheckMargin = false;
			contextMenuStrip.AutoSize = false;
			contextMenuStrip.Padding = new Padding(Theme.S(6));
			contextMenuStrip.DropShadowEnabled = false;
			ContextMenuStrip contextMenuStrip2 = contextMenuStrip;
			Action<Action> invoke = delegate(Action action)
			{
				if (FindLibraryProfile(id) != null)
				{
					action();
				}
			};
			contextMenuStrip2.Items.Add(Lang.T("lib.rename"), null, delegate
			{
				invoke(delegate
				{
					RenameGame(id);
				});
			});
			contextMenuStrip2.Items.Add(AutoText("更换封面…", "Change cover…"), null, delegate
			{
				invoke(delegate
				{
					ChooseLibraryCover(id);
				});
			});
			ToolStripMenuItem toolStripMenuItem = new ToolStripMenuItem(AutoText("恢复默认封面", "Restore default cover"));
			toolStripMenuItem.Enabled = GameCoverStore.HasCustom(selected.Profile);
			ToolStripMenuItem toolStripMenuItem2 = toolStripMenuItem;
			toolStripMenuItem2.Click += delegate
			{
				invoke(delegate
				{
					ResetLibraryCover(id);
				});
			};
			contextMenuStrip2.Items.Add(toolStripMenuItem2);
			ToolStripMenuItem toolStripMenuItem3 = new ToolStripMenuItem(Lang.T(selected.Profile.ForceTrigger ? "v15.library.force.on" : "v15.library.force"));
			toolStripMenuItem3.Checked = selected.Profile.ForceTrigger;
			if (toolStripMenuItem3.Checked)
			{
				toolStripMenuItem3.Tag = new MenuBadge(PerformancePolicy.Text("已启用", "Enabled"), true);
			}
			toolStripMenuItem3.Click += delegate
			{
				invoke(delegate
				{
					ToggleForceTrigger(id);
				});
			};
			contextMenuStrip2.Items.Add(toolStripMenuItem3);
			contextMenuStrip2.Items.Add(new ToolStripSeparator());
			ToolStripMenuItem toolStripMenuItem4 = new ToolStripMenuItem(Lang.T("btn.remove"));
			toolStripMenuItem4.ForeColor = Theme.Danger;
			ToolStripMenuItem toolStripMenuItem5 = toolStripMenuItem4;
			toolStripMenuItem5.Click += delegate
			{
				invoke(delegate
				{
					RemoveLibraryGame(id);
				});
			};
			contextMenuStrip2.Items.Add(toolStripMenuItem5);
			int num = contextMenuStrip2.Padding.Vertical;
			int num2 = Theme.S(230);
			foreach (ToolStripItem item in contextMenuStrip2.Items)
			{
				item.AutoSize = false;
				item.Margin = Padding.Empty;
				item.Size = new Size(num2 - contextMenuStrip2.Padding.Horizontal, Theme.S((item is ToolStripSeparator) ? 9 : 36));
				num += item.Height;
			}
			contextMenuStrip2.Size = new Size(num2, num);
			contextMenuStrip2.PerformLayout();
			using (GraphicsPath path = Theme.TechPath(new Rectangle(0, 0, num2, num), Theme.S(8)))
			{
				contextMenuStrip2.Region = new Region(path);
				return contextMenuStrip2;
			}
		}

		private void RenameGame(string keepId)
		{
			GameProfile gameProfile = FindLibraryProfile(keepId);
			if (gameProfile != null)
			{
				string text = PaviseDialog.Prompt(this, Lang.T("lib.rename"), Lang.F("lib.rename.sub", gameProfile.Name), gameProfile.Name);
				if (!string.IsNullOrEmpty(text) && !(text == gameProfile.Name))
				{
					gameMode.RenameProfile(keepId, text);
					RefreshGames();
					SelectProfile(keepId);
				}
			}
		}

		private void ToggleForceTrigger(string keepId)
		{
			GameProfile gameProfile = FindLibraryProfile(keepId);
			if (gameProfile == null)
			{
				return;
			}
			bool flag = !gameProfile.ForceTrigger;
			if (flag && !PaviseDialog.Confirm(this, Lang.T("v15.library.force"), Lang.F("v15.library.force.confirm", gameProfile.Name), DlgKind.Warn))
			{
				return;
			}
			if (!gameMode.SetProfileForceTrigger(keepId, flag))
			{
				if (!gameMode.ProfileStoreSaveFailed)
				{
					PaviseDialog.Warn(this, Lang.T("v15.library.force"), Lang.T("v15.library.force.noexe"));
				}
			}
			else
			{
				RefreshGames();
				SelectProfile(keepId);
			}
		}

		private void SelectProfile(string profileId)
		{
			if (lstGames == null || string.IsNullOrEmpty(profileId))
			{
				return;
			}
			for (int i = 0; i < lstGames.Items.Count; i++)
			{
				GameLibraryItem gameLibraryItem = lstGames.Items[i];
				if (gameLibraryItem != null && gameLibraryItem.Profile != null && string.Equals(gameLibraryItem.Profile.Id, profileId, StringComparison.OrdinalIgnoreCase))
				{
					lstGames.SelectedIndex = i;
					break;
				}
			}
		}

		private void ShowAddGameDialog()
		{
			ShowAddGameDialog(null);
		}

		private void ShowAddGameDialog(IEnumerable<string> seeds)
		{
			List<string> list = new List<string>();
			foreach (GameProfile profile in gameMode.GetProfiles())
			{
				if (!string.IsNullOrEmpty(profile.ExecutablePath))
				{
					list.Add(profile.ExecutablePath);
				}
				if (!string.IsNullOrEmpty(profile.LearnedExecutablePath))
				{
					list.Add(profile.LearnedExecutablePath);
				}
			}
			using (AddGameDialog addGameDialog = new AddGameDialog(list, gameMode.ActiveGame == null, seeds))
			{
				if (ShowDim(addGameDialog) != DialogResult.OK || addGameDialog.Selected.Count == 0)
				{
					return;
				}
				string lastError;
				int num = gameMode.AddScannedGames(addGameDialog.Selected, out lastError);
				if (!gameMode.ProfileStoreSaveFailed)
				{
					RefreshGames();
					if (num > 0)
					{
						Logger.Log(Lang.F("scan.added", num));
					}
					else if (lastError != null)
					{
						PaviseDialog.Warn(this, "PAVISE AUTO", lastError);
					}
				}
			}
		}

		private void RefreshGames()
		{
			if (base.IsDisposed || lstGames == null || lstGames.IsDisposed)
			{
				return;
			}
			Interlocked.Increment(ref runningVersion);
			if (swAutoAdd != null)
			{
				swAutoAdd.Checked = gameMode.AutoAddFullscreen;
			}
			string text = null;
			GameLibraryItem selectedItem = lstGames.SelectedItem;
			if (selectedItem != null && selectedItem.Profile != null)
			{
				text = selectedItem.Profile.Id;
			}
			List<GameProfile> profiles = gameMode.GetProfiles();
			Dictionary<string, bool> dictionary = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
			foreach (GameLibraryItem item in lstGames.Items)
			{
				if (item != null && item.Profile != null && !string.IsNullOrEmpty(item.Profile.ExecutablePath))
				{
					dictionary[item.Profile.ExecutablePath] = item.Running;
				}
			}
			List<GameLibraryItem> list = new List<GameLibraryItem>();
			foreach (GameProfile item2 in profiles)
			{
				list.Add(new GameLibraryItem(item2, RunningIn(dictionary, item2.ExecutablePath), gameMode.HasRendererObservation(item2)));
			}
			lstGames.SetItems(list);
			if (text != null)
			{
				SelectProfile(text);
			}
			bool showEmpty = lstGames.Items.Count == 0;
			lstGames.Visible = true;
			if (gameListPanel != null)
			{
				gameListPanel.ShowEmpty = showEmpty;
				gameListPanel.Invalidate();
			}
			if (lblLibraryCount != null)
			{
				lblLibraryCount.Text = AutoText(profiles.Count + " 款游戏", profiles.Count + " games");
			}
			SyncLibraryHint();
			RefreshLibraryInspector();
			if (UiActive && curPage == pageLibrary)
			{
				RefreshGameRunningStates(true);
			}
		}

		private void SyncLibraryHint()
		{
			if (lblLibraryHint != null)
			{
				lblLibraryHint.Text = Lang.T("v15.library.drop");
				lblLibraryHint.ForeColor = Theme.Dim;
			}
		}

		private void RefreshGameRunningStates(bool force = false)
		{
			if (base.IsDisposed || !UiActive || lstGames == null || lstGames.IsDisposed)
			{
				return;
			}
			long ticks = DateTime.UtcNow.Ticks;
			if (!force && ticks < Interlocked.Read(ref nextRunningProbeTicks))
			{
				return;
			}
			if (Interlocked.Exchange(ref runningBusy, 1) == 1)
			{
				if (force)
				{
					Interlocked.Exchange(ref runningPending, 1);
					Interlocked.Exchange(ref nextRunningProbeTicks, 0L);
				}
				return;
			}
			Interlocked.Exchange(ref runningPending, 0);
			Interlocked.Exchange(ref nextRunningProbeTicks, ticks + RunningProbeIntervalTicks);
			int version = Volatile.Read(ref runningVersion);
			GameLibraryGallery list = lstGames;
			List<string> paths = new List<string>();
			foreach (GameLibraryItem item in list.Items)
			{
				GameLibraryItem gameLibraryItem = item as GameLibraryItem;
				if (gameLibraryItem != null && gameLibraryItem.Profile != null)
				{
					paths.Add(gameLibraryItem.Profile.ExecutablePath);
				}
			}
			paths.Sort(StringComparer.OrdinalIgnoreCase);
			string observationKey = string.Join("\n", paths.ToArray()).ToLowerInvariant();
			long observationSession = gameMode == null ? 0 : gameMode.AutomaticSessionSerial;
			if (!runningObservationFailures.Allowed(0, observationSession, observationKey, "running"))
			{
				Interlocked.Exchange(ref runningBusy, 0);
				return;
			}
			try
			{
				QueueUiStateWork(delegate
				{
					Dictionary<string, bool> states = null;
					try
					{
						states = QueryLibraryRunningState(paths);
						if (states != null) runningObservationFailures.Succeeded(0, observationSession, observationKey, "running");
					}
					catch
					{
						int failures = runningObservationFailures.Failed(0, observationSession, observationKey, "running");
						Logger.Warn("Library running observation failure=" + failures + "/3");
					}
					try
					{
						PostUiStateResult(delegate
						{
							try
							{
								if (!base.IsDisposed && UiActive && states != null && object.ReferenceEquals(list, lstGames) && !list.IsDisposed && version == Volatile.Read(ref runningVersion))
								{
									ApplyRunningStates(states);
								}
							}
							finally
							{
								FinishGameRunningRefresh();
							}
						});
					}
					catch
					{
						ReleaseGameRunningRefresh();
					}
				});
			}
			catch
			{
				int failures = runningObservationFailures.Failed(0, observationSession, observationKey, "running");
				Logger.Warn("Library running observation queue failure=" + failures + "/3");
				ReleaseGameRunningRefresh();
			}
		}

		private void ReleaseGameRunningRefresh()
		{
			Interlocked.Exchange(ref nextRunningProbeTicks, 0L);
			Interlocked.Exchange(ref runningBusy, 0);
		}

		private void FinishGameRunningRefresh()
		{
			Interlocked.Exchange(ref runningBusy, 0);
			if (Interlocked.Exchange(ref runningPending, 0) != 0 && !base.IsDisposed && UiActive && curPage == pageLibrary)
			{
				RefreshGameRunningStates(true);
			}
		}

		private void ApplyRunningStates(Dictionary<string, bool> states)
		{
			bool flag = false;
			foreach (GameLibraryItem item in lstGames.Items)
			{
				GameLibraryItem gameLibraryItem = item as GameLibraryItem;
				if (gameLibraryItem != null && gameLibraryItem.Profile != null)
				{
					string executablePath = gameLibraryItem.Profile.ExecutablePath;
					bool value;
					if (!string.IsNullOrEmpty(executablePath) && states.TryGetValue(executablePath, out value) && value != gameLibraryItem.Running)
					{
						gameLibraryItem.Running = value;
						flag = true;
					}
				}
			}
			if (flag)
			{
				lstGames.RefreshRows();
				RefreshLibraryInspector();
			}
		}

		private void AddDroppedGames(string[] files)
		{
			if (files == null)
			{
				return;
			}
			List<string> list = new List<string>();
			string error = null;
			foreach (string text in files)
			{
				if (!string.IsNullOrEmpty(text))
				{
					if (Directory.Exists(text))
					{
						list.Add(text);
					}
					else if (!gameMode.AddGameFile(text, out error) && error != Lang.T("t.gamemodelibrary.1"))
					{
						break;
					}
				}
			}
			if (!string.IsNullOrEmpty(error) && error != Lang.T("t.gamemodelibrary.1"))
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", error);
			}
			RefreshGames();
			if (list.Count > 0)
			{
				ShowAddGameDialog(list);
			}
		}

		private static bool RunningIn(Dictionary<string, bool> states, string executablePath)
		{
			bool value;
			if (!string.IsNullOrEmpty(executablePath) && states.TryGetValue(executablePath, out value))
			{
				return value;
			}
			return false;
		}

		private static Dictionary<string, bool> ProbeRunning(List<string> paths)
		{
			Dictionary<string, bool> dictionary = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
			Dictionary<string, List<string>> dictionary2 = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
			foreach (string path in paths)
			{
				if (string.IsNullOrEmpty(path) || dictionary.ContainsKey(path))
				{
					continue;
				}
				dictionary[path] = false;
				string fileNameWithoutExtension;
				try
				{
					fileNameWithoutExtension = Path.GetFileNameWithoutExtension(path);
				}
				catch
				{
					continue;
				}
				if (!string.IsNullOrEmpty(fileNameWithoutExtension))
				{
					List<string> value;
					if (!dictionary2.TryGetValue(fileNameWithoutExtension, out value))
					{
						value = (dictionary2[fileNameWithoutExtension] = new List<string>());
					}
					value.Add(path);
				}
			}
			if (dictionary2.Count == 0)
			{
				return dictionary;
			}
			Process[] array = null;
			try
			{
				array = Process.GetProcesses();
				Process[] array2 = array;
				foreach (Process process in array2)
				{
					List<string> value2 = null;
					int num = 0;
					try
					{
						dictionary2.TryGetValue(process.ProcessName, out value2);
						num = process.Id;
					}
					catch
					{
						continue;
					}
					if (value2 == null)
					{
						continue;
					}
					IntPtr intPtr = Native.OpenProcess(4096, false, num);
					if (intPtr == IntPtr.Zero)
					{
						continue;
					}
					string text;
					try
					{
						text = Native.ImagePath(intPtr);
					}
					finally
					{
						Native.CloseHandle(intPtr);
					}
					if (string.IsNullOrEmpty(text))
					{
						continue;
					}
					foreach (string item in value2)
					{
						if (string.Equals(item, text, StringComparison.OrdinalIgnoreCase))
						{
							dictionary[item] = true;
						}
					}
				}
			}
			catch
			{
			}
			finally
			{
				if (array != null)
				{
					Process[] array3 = array;
					foreach (Process process2 in array3)
					{
						process2.Dispose();
					}
				}
			}
			return dictionary;
		}

		private Bitmap GameIcon(string executablePath)
		{
			string key = executablePath ?? "";
			Bitmap value;
			if (gameIconCache.TryGetValue(key, out value))
			{
				return value;
			}
			try
			{
				using (Icon icon = Icon.ExtractAssociatedIcon(executablePath))
				{
					value = icon.ToBitmap();
				}
			}
			catch
			{
				value = null;
			}
			gameIconCache[key] = value;
			return value;
		}

		[DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
		private static extern IntPtr SendLibraryCue(IntPtr window, int message, IntPtr parameter, string text);

		private void BuildLibraryPage()
		{
			libraryViewport = Size.Empty;
			gameListPanel = null;
			lblLibraryHint = null;
			pageLibrary.AutoScroll = false;
			Native.Dark(pageLibrary);
			librarySearchPanel = new LibrarySearchSurface
			{
				Name = "librarySearchPanel",
				Fill = Theme.Card,
				Border = Theme.StrokeHi,
				Radius = Theme.S(10),
				BackColor = Theme.Bg
			};
			librarySearch = Theme.MakeTextBox(0, 0, Theme.S(220));
			librarySearch.Name = "librarySearch";
			librarySearch.BorderStyle = BorderStyle.None;
			librarySearch.BackColor = Theme.Card;
			librarySearch.Text = librarySearchText;
			librarySearch.AccessibleName = AutoText("搜索游戏", "Search games");
			librarySearch.HandleCreated += delegate
			{
				SendLibraryCue(librarySearch.Handle, 5377, new IntPtr(1), librarySearch.AccessibleName);
			};
			librarySearch.TextChanged += delegate
			{
				librarySearchText = librarySearch.Text;
				if (lstGames != null)
				{
					lstGames.FilterText = librarySearchText;
					AdjustLibraryCardDensity();
				}
			};
			librarySearchPanel.Controls.Add(librarySearch);
			pageLibrary.Controls.Add(librarySearchPanel);
			libraryPlatforms = new TierPicker
			{
				Name = "libraryPlatformFilter",
				Labels = new string[3]
				{
					AutoText("全部", "All"),
					"Steam",
					"WeGame"
				},
				StrongSelection = true,
				Index = libraryPlatformIndex,
				AccessibleName = AutoText("游戏平台筛选", "Filter by platform")
			};
			libraryPlatforms.IndexChanged = delegate(int index)
			{
				libraryPlatformIndex = index;
				ApplyLibraryFilter();
			};
			libraryAdd = new PillButton(Lang.T("v15.library.add"), BtnKind.Primary)
			{
				Name = "libraryAdd"
			};
			libraryAdd.Click += delegate
			{
				ShowAddGameDialog();
			};
			swAutoAdd = new FamilySuppressionSwitch
			{
				Name = "libraryAutoAdd",
				Text = Lang.T("v15.library.autoadd"),
				Checked = gameMode.AutoAddFullscreen,
				AccessibleName = Lang.T("v15.library.autoadd"),
				AccessibleDescription = Lang.T("v15.library.autoadd.desc")
			};
			swAutoAdd.Click += delegate
			{
				gameMode.AutoAddFullscreen = !gameMode.AutoAddFullscreen;
				swAutoAdd.Checked = gameMode.AutoAddFullscreen;
			};
			libraryAutoSettings = new SettingsButton(Lang.T("v15.library.autoadd.settings"), Lang.T("autoignore.title"))
			{
				Name = "libraryAutoIgnore"
			};
			libraryAutoSettings.Click += delegate
			{
				ShowAutoIgnoreDialog();
			};
			lblLibraryCount = CardLabel(pageLibrary, "", 0, 0, 120, 28, 8.2f, false, Theme.Dim);
			pageLibrary.Controls.AddRange(new Control[4] { libraryPlatforms, libraryAdd, swAutoAdd, libraryAutoSettings });
			lstGames = new GameLibraryGallery
			{
				Name = "LibraryGallery",
				IconProvider = GameIcon,
				CoverProvider = GameCoverStore.Load,
				ConfigurationPresenter = OpenLibraryConfiguration
			};
			lstGames.FilterText = librarySearchText;
			lstGames.SelectedIndexChanged += delegate
			{
				GameLibraryItem selectedItem = lstGames.SelectedItem;
				librarySelectedId = ((selectedItem == null) ? null : selectedItem.Profile.Id);
			};
			lstGames.MoreRequested += delegate(object sender, GameLibraryEventArgs e)
			{
				ShowLibraryMore(e.Item, e.Anchor);
			};
			lstGames.KeyDown += delegate(object sender, KeyEventArgs e)
			{
				if (e.KeyCode == Keys.Delete && e.Modifiers == Keys.None && lstGames.SelectedItem != null)
				{
					e.Handled = true;
					e.SuppressKeyPress = true;
					RemoveLibraryGame(lstGames.SelectedItem.Profile.Id);
				}
			};
			pageLibrary.Controls.Add(lstGames);
			pageLibrary.SizeChanged += delegate
			{
				libraryViewport = Size.Empty;
				LayoutLibraryViewport();
			};
			ApplyLibraryFilter();
			RefreshGames();
			if (!string.IsNullOrEmpty(librarySelectedId))
			{
				SelectProfile(librarySelectedId);
			}
			LayoutLibraryViewport();
		}

		private void ApplyLibraryFilter()
		{
			if (lstGames != null)
			{
				int platform = libraryPlatformIndex;
				lstGames.Filter = ((platform == 0) ? null : ((Predicate<GameLibraryItem>)((GameLibraryItem item) => item != null && string.Equals(GameLibraryGallery.SourceOf(item.Profile), (platform == 1) ? "Steam" : "WeGame", StringComparison.OrdinalIgnoreCase))));
				lstGames.FilterText = librarySearchText;
				AdjustLibraryCardDensity();
			}
		}

		private void LayoutLibraryViewport()
		{
			if (libraryArranging || pageLibrary == null || lstGames == null || libraryViewport == pageLibrary.ClientSize)
			{
				return;
			}
			libraryArranging = true;
			pageLibrary.SuspendLayout();
			try
			{
				int num = Math.Max(280, Dpi.U(pageLibrary.ClientSize.Width));
				int num2 = Dpi.U(pageLibrary.ClientSize.Height);
				int num3 = ((num < 500) ? 16 : 24);
				int num4 = num - num3 * 2;
				bool flag = num >= 1040;
				bool flag2 = num >= 570;
				int num5 = (flag ? 238 : (flag2 ? (num4 - 132) : num4));
				SetOverviewBounds(librarySearchPanel, num3, 16, num5, 36);
				librarySearch.SetBounds(Theme.S(42), Theme.S(9), Math.Max(1, librarySearchPanel.Width - Theme.S(54)), Theme.S(24));
				SetOverviewBounds(libraryAdd, num - num3 - 120, flag2 ? 16 : 62, 120, 36);
				int num6 = (flag ? 16 : 62);
				SetOverviewBounds(libraryPlatforms, flag ? (num3 + 250) : num3, num6, flag2 ? 214 : (num4 - 132), 36);
				int num7 = (flag ? 16 : (flag2 ? 62 : 108));
				int num8 = (flag ? (num - num3 - 120 - 12 - 196) : (flag2 ? (num - num3 - 196) : num3));
				SetOverviewBounds(swAutoAdd, num8, num7, 152, 36);
				SetOverviewBounds(libraryAutoSettings, num8 + 160, num7, 36, 36);
				int num9 = (flag ? (num3 + 480) : (flag2 ? (num3 + 226) : (num - num3 - 100)));
				SetOverviewBounds(lblLibraryCount, num9, flag ? 20 : (flag2 ? 66 : 112), flag ? Math.Max(0, num8 - num9 - 12) : (flag2 ? Math.Max(0, num8 - num9 - 12) : 100), 28);
				int num10 = (flag ? 68 : (flag2 ? 114 : 160));
				SetOverviewBounds(lstGames, num3, num10, num4, Math.Max(80, num2 - num10 - 8));
				lstGames.CardGap = 12;
				AdjustLibraryCardDensity();
				pageLibrary.AutoScroll = false;
				pageLibrary.AutoScrollMinSize = Size.Empty;
				libraryViewport = pageLibrary.ClientSize;
			}
			finally
			{
				pageLibrary.ResumeLayout(true);
				libraryArranging = false;
			}
		}

		private void RefreshLibraryInspector()
		{
			if (lstGames == null)
			{
				return;
			}
			lstGames.RefreshConfiguration();
			AdjustLibraryCardDensity();
			GameConfigurationDialog gameConfigurationDialog = libraryConfigurationDialog;
			if (gameConfigurationDialog != null && !gameConfigurationDialog.IsDisposed && gameConfigurationDialog.Visible)
			{
				GameLibraryItem gameLibraryItem = ConfigurationItem(gameConfigurationDialog.ProfileId);
				if (gameLibraryItem == null)
				{
					gameConfigurationDialog.Close();
					return;
				}
				((LibraryGameConfiguration)gameConfigurationDialog.ConfigurationContent).RefreshItem(gameLibraryItem);
				gameConfigurationDialog.RefreshGame(gameLibraryItem, GameIcon(gameLibraryItem.Profile.ExecutablePath));
			}
		}

		private GameLibraryItem ConfigurationItem(string id)
		{
			GameProfile gameProfile = FindLibraryProfile(id);
			if (gameProfile == null)
			{
				return null;
			}
			bool running = false;
			if (lstGames != null)
			{
				foreach (GameLibraryItem item in lstGames.Items)
				{
					if (item.Profile != null && item.Profile.Id == id)
					{
						running = item.Running;
						break;
					}
				}
			}
			return new GameLibraryItem(gameProfile, running, gameMode.HasRendererObservation(gameProfile));
		}

		private void OpenLibraryConfiguration(GameLibraryItem requested, Control anchor)
		{
			if (requested == null || requested.Profile == null)
			{
				return;
			}
			if (libraryConfigurationDialog != null && !libraryConfigurationDialog.IsDisposed)
			{
				libraryConfigurationDialog.Activate();
				return;
			}
			string id = requested.Profile.Id;
			GameLibraryItem gameLibraryItem = ConfigurationItem(id);
			if (gameLibraryItem == null)
			{
				RefreshGames();
				return;
			}
			libraryConfigurationCoreTarget = null;
			using (GameConfigurationDialog dlg = new GameConfigurationDialog(gameLibraryItem, new LibraryGameConfiguration(this, gameLibraryItem), GameIcon(gameLibraryItem.Profile.ExecutablePath)))
			{
				libraryConfigurationDialog = dlg;
				try
				{
					ShowDim(dlg);
				}
				finally
				{
					libraryConfigurationDialog = null;
				}
			}
			string text = libraryConfigurationCoreTarget;
			libraryConfigurationCoreTarget = null;
			if (base.IsDisposed || base.Disposing)
			{
				return;
			}
			if (!string.IsNullOrEmpty(text))
			{
				OpenGameCoreScheduling(text);
			}
			else if (curPage == pageLibrary)
			{
				SelectProfile(id);
				if (anchor != null && !anchor.IsDisposed && anchor.CanFocus)
				{
					anchor.Focus();
				}
				else if (lstGames != null && !lstGames.IsDisposed)
				{
					lstGames.Focus();
				}
			}
		}

		private void OpenLibraryCoreScheduling(string id)
		{
			if (libraryConfigurationDialog != null && !libraryConfigurationDialog.IsDisposed && libraryConfigurationDialog.Visible)
			{
				libraryConfigurationCoreTarget = id;
				libraryConfigurationDialog.Close();
			}
			else
			{
				OpenGameCoreScheduling(id);
			}
		}

		private void AdjustLibraryCardDensity()
		{
			for (int i = 0; i < 2; i++)
			{
				int num = (lstGames.VisibleItemCount + lstGames.ColumnCount - 1) / lstGames.ColumnCount;
				int num2 = Dpi.U(lstGames.ClientSize.Height);
				int val = ((num > 0 && num <= 3) ? ((num2 - 4 - Math.Max(0, num - 1) * lstGames.CardGap) / num - 32) : 170);
				lstGames.PreferredCardHeight = Math.Max(128, Math.Min(170, val));
			}
		}

		private void RemoveLibraryGame(string id)
		{
			gameMode.RemoveProfile(id);
			RefreshGames();
		}

		private void ChooseLibraryCover(string id)
		{
			GameProfile gameProfile = FindLibraryProfile(id);
			if (gameProfile == null)
			{
				return;
			}
			OpenFileDialog openFileDialog = new OpenFileDialog();
			openFileDialog.Title = AutoText("更换游戏封面", "Change game cover");
			openFileDialog.Filter = AutoText("图片", "Images") + "|*.jpg;*.jpeg;*.png;*.bmp";
			openFileDialog.CheckFileExists = true;
			openFileDialog.Multiselect = false;
			using (OpenFileDialog openFileDialog2 = openFileDialog)
			{
				if (openFileDialog2.ShowDialog(this) == DialogResult.OK)
				{
					string error;
					if (!GameCoverStore.TrySet(gameProfile, openFileDialog2.FileName, out error))
					{
						PaviseDialog.Warn(this, "PAVISE AUTO", error);
					}
					else
					{
						lstGames.RefreshRows();
					}
				}
			}
		}

		private void ResetLibraryCover(string id)
		{
			GameProfile gameProfile = FindLibraryProfile(id);
			if (gameProfile != null)
			{
				string error;
				if (!GameCoverStore.TryReset(gameProfile, out error))
				{
					PaviseDialog.Warn(this, "PAVISE AUTO", error);
				}
				else
				{
					lstGames.RefreshRows();
				}
			}
		}

		private void BuildLogPage()
		{
			pageLog.SetBounds(0, Theme.S(TopH), Theme.S(WinW), Theme.S(PageH));
			int num = ContentW + RailW;
			int num2 = PageHeader(pageLog, Lang.T("nav.log"), Lang.T("v20.log.sub"), 1, true, 0, num);
			RoundPanel roundPanel = MakeConsolePanel(pageLog, 30, num2, num, 64, true);
			roundPanel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			logActionsButton = new PillButton(Lang.T("log.category.actions"));
			logActionsButton.Name = "showActionLog";
			logActionsButton.SetBounds(Theme.S(18), Theme.S(14), Theme.S(128), Theme.S(36));
			logActionsButton.Click += delegate { SelectLogCategory(false); };
			logDiagnosticsButton = new PillButton(Lang.T("log.category.diagnostics"));
			logDiagnosticsButton.Name = "showDiagnosticLog";
			logDiagnosticsButton.SetBounds(Theme.S(154), Theme.S(14), Theme.S(144), Theme.S(36));
			logDiagnosticsButton.Click += delegate { SelectLogCategory(true); };
			roundPanel.Controls.AddRange(new Control[] { logActionsButton, logDiagnosticsButton });
			CardLabel(roundPanel, Lang.T("log.latest.session"), 320, 7, num - 338, 24, 10.5f, true, Theme.Fg).Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			lblLogStreamHint = new Label
			{
				Name = "logSessionHint",
				ForeColor = Theme.Faint,
				BackColor = Color.Transparent,
				Font = Theme.UI(7.8f, false),
				TextAlign = ContentAlignment.MiddleLeft,
				AutoEllipsis = true
			};
			lblLogStreamHint.SetBounds(Theme.S(320), Theme.S(31), Theme.S(num - 338), Theme.S(24));
			lblLogStreamHint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			roundPanel.Controls.Add(lblLogStreamHint);
			int num4 = num2 + 76;
			int num5 = Math.Max(PageH - 48, num4 + 190);
			logWrap = new RoundPanel();
			logWrap.SetBounds(Theme.S(30), Theme.S(num4), Theme.S(num), Theme.S(num5 - num4 - 10));
			logWrap.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
			logWrap.BackColor = Theme.Bg;
			logWrap.Fill = Theme.Inset;
			logWrap.Border = Theme.StrokeHi;
			logWrap.Radius = Theme.S(14);
			logWrap.AccentEdge = true;
			logWrap.Padding = new Padding(Theme.S(5));
			logStream = new LogStreamView();
			ConfigureLogView();
			logStream.Dock = DockStyle.Fill;
			Native.Dark(logStream);
			logWrap.Controls.Add(logStream);
			PillButton pillButton = new PillButton(Lang.T("v20.log.refresh"));
			pillButton.Name = "refreshSessionLog";
			PillButton pillButton2 = pillButton;
			pillButton2.SetBounds(Theme.S(30), Theme.S(num5), Theme.S(96), Theme.S(36));
			pillButton2.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
			pillButton2.Click += delegate
			{
				RefreshLog(true);
			};
			PillButton exportLog = new PillButton(Lang.T("log.export.file"));
			logExportButton = exportLog;
			exportLog.Name = "exportLogFile";
			exportLog.AccessibleName = Lang.T("log.export.file");
			exportLog.SetBounds(Theme.S(136), Theme.S(num5), Theme.S(112), Theme.S(36));
			exportLog.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
			exportLog.Click += ExportLogFile;
			PillButton pillButton3 = new PillButton(Lang.T("rep.clear.log"), BtnKind.Danger);
			pillButton3.SetBounds(Theme.S(258), Theme.S(num5), Theme.S(112), Theme.S(36));
			pillButton3.Name = "clearSessionLog";
			pillButton3.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
			pillButton3.Click += delegate
			{
				if (PaviseDialog.Confirm(this, "PAVISE AUTO", Lang.T("rep.clear.ask"), DlgKind.Warn))
				{
					Logger.Clear();
					Logger.Log(Lang.T("log.panelformlogpage.1"));
					Logger.RuntimeIdentity();
					RefreshLog(true);
				}
			};
			swLogWrites = MakeSwitch(Settings.Load("LogWritesEnabled", true), OnLogWritesToggle);
			swLogWrites.Location = new Point(Theme.S(30 + num) - swLogWrites.Width, Theme.S(num5 + 6));
			swLogWrites.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
			Label label = new Label();
			label.Text = Lang.T("set.logwrites");
			label.ForeColor = Theme.Dim;
			label.BackColor = Theme.Bg;
			label.Font = Theme.UI(8.6f, false);
			label.TextAlign = ContentAlignment.MiddleRight;
			Label label2 = label;
			label2.SetBounds(Theme.S(30 + num - 162) - swLogWrites.Width, Theme.S(num5), Theme.S(152), Theme.S(36));
			label2.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
			pageLog.Controls.AddRange(new Control[6] { logWrap, pillButton2, exportLog, pillButton3, label2, swLogWrites });
			RefreshLog(true);
		}

		private void ConfigureLogView()
		{
			if (logStream != null)
			{
				logStream.ActionEvents = !logDiagnostics;
				logStream.TechnicalEvents = logDiagnostics;
				logStream.Filter = 4;
			}
			if (logActionsButton != null)
			{
				logActionsButton.Kind = logDiagnostics ? BtnKind.Normal : BtnKind.Primary;
				logActionsButton.AccessibleDescription = Lang.T("log.actions.hint");
				logActionsButton.Invalidate();
			}
			if (logDiagnosticsButton != null)
			{
				logDiagnosticsButton.Kind = logDiagnostics ? BtnKind.Primary : BtnKind.Normal;
				logDiagnosticsButton.AccessibleDescription = Lang.T("log.diagnostics.hint");
				logDiagnosticsButton.Invalidate();
			}
			if (logExportButton != null)
				logExportButton.AccessibleDescription = Lang.T(logDiagnostics ? "log.export.diagnostics.detail" : "log.export.actions.detail");
		}

		private void SelectLogCategory(bool diagnostics)
		{
			if (logDiagnostics == diagnostics) return;
			logDiagnostics = diagnostics;
			ConfigureLogView();
			SyncLogHint();
		}

		private void OnLogWritesToggle(object s, EventArgs e)
		{
			if (!base.IsDisposed && swLogWrites != null && !swLogWrites.IsDisposed)
			{
				bool @checked = swLogWrites.Checked;
				if (!@checked)
				{
					Logger.Log(Lang.T("log.logwrites.off"));
				}
				Logger.WritesEnabled = @checked;
				Settings.Save("LogWritesEnabled", @checked);
				if (@checked)
				{
					Logger.Log(Lang.T("log.logwrites.on"));
				}
				swLogWrites.SetSilently(Settings.Load("LogWritesEnabled", true));
				RefreshLog(true);
			}
		}

		private void RefreshLog()
		{
			RefreshLog(false);
		}

		private void RefreshLog(bool force)
		{
			if (logStream == null)
			{
				return;
			}
			ConfigureLogView();
			SyncLogHint();
			logStream.SessionToken = gameMode.AutomationSession;
			long version = Logger.Version;
			if (force || version != logSeenVersion || !object.ReferenceEquals(logSeenStream, logStream))
			{
				logSeenVersion = version;
				logSeenStream = logStream;
				bool flag = logStream.SetText(Logger.Tail(2000));
				if (force && !flag)
				{
					logStream.Invalidate();
				}
			}
		}

		private void SyncLogHint()
		{
			if (lblLogStreamHint != null)
			{
				bool writesEnabled = Logger.WritesEnabled;
				lblLogStreamHint.Text = writesEnabled ? Lang.T(logDiagnostics ? "log.diagnostics.hint" : "log.actions.hint") : Lang.T("v20.log.paused");
				lblLogStreamHint.ForeColor = (writesEnabled ? Theme.Faint : LogStreamView.SeverityColor(LogEventSeverity.Warning));
			}
		}

		private void BuildManualMemoryCleaner(OverviewUsageItem memory)
		{
			overviewMemoryCleanup = new OverviewMemoryCleanupControl(elevated)
			{
				Name = "overviewMemoryCleanup"
			};
			memory.AttachMemoryAction(overviewMemoryCleanup);
			overviewMemoryCleanup.SetGamePolicy(gameMode.IsActive);
			if (manualMemoryReport != null)
			{
				overviewMemoryCleanup.ShowReport(manualMemoryReport);
			}
			else if (Volatile.Read(ref manualMemoryPending) != 0)
			{
				overviewMemoryCleanup.ShowProgress(manualMemoryProgressNumber, manualMemoryProgressStep);
			}
			overviewMemoryCleanup.GeometryChanged += delegate
			{
				overviewViewport = Size.Empty;
				LayoutOverviewPage();
			};
			overviewMemoryCleanup.CleanRequested += delegate
			{
				StartManualMemoryCleanup();
			};
			if (!manualMemoryDisposeRegistered)
			{
				manualMemoryDisposeRegistered = true;
				base.Disposed += delegate
				{
					manualMemoryDisposed = true;
				};
			}
		}

		private void StartManualMemoryCleanup()
		{
			if (!elevated || manualMemoryDisposed || base.IsDisposed || base.Disposing || !base.IsHandleCreated || Interlocked.CompareExchange(ref manualMemoryPending, 1, 0) != 0)
			{
				return;
			}
			int gameProtection = gameMode == null || gameMode.IsActive ? 1 : 0;
			Func<bool> gamePresent = delegate
			{
				if (Volatile.Read(ref gameProtection) != 0) return true;
				bool protect;
				try { protect = gameMode == null || gameMode.MemoryCleanupGamePresent(); }
				catch { protect = true; }
				if (protect) Interlocked.Exchange(ref gameProtection, 1);
				return protect;
			};
			IManualMemoryNative native = new WindowsManualMemoryNative(gamePresent);
			Action<int> wait = null;
			manualMemoryReport = null;
			manualMemoryProgressNumber = 1;
			manualMemoryProgressStep = gameMode.IsActive ? ManualMemoryStep.PaviseMemory : ManualMemoryCleaner.OrderedSteps[0];
			overviewMemoryCleanup.ShowProgress(1, manualMemoryProgressStep);
			Action work = delegate
			{
				try
				{
					ManualMemoryReport report = new ManualMemoryCleaner(native, wait, gamePresent).Run(() => !manualMemoryDisposed, delegate(int number, ManualMemoryStep step)
					{
						PostManualMemoryUi(delegate
						{
							manualMemoryProgressNumber = number;
							manualMemoryProgressStep = step;
							overviewMemoryCleanup.ShowProgress(number, step);
						});
					});
					PostManualMemoryUi(delegate
					{
						manualMemoryReport = report;
						overviewMemoryCleanup.ShowReport(report);
						Interlocked.Exchange(ref manualMemoryPending, 0);
						overviewNextTelemetry = 0L;
						QueueOverviewTelemetry(true);
					});
				}
				catch
				{
					PostManualMemoryUi(delegate
					{
						overviewMemoryCleanup.ShowError(ManualMemoryText.T("清理未完成 · 可重试", "Cleanup failed · retry available", "未完了 · 再試行できます"));
						Interlocked.Exchange(ref manualMemoryPending, 0);
					});
				}
			};
			try
			{
				if (!ThreadPool.QueueUserWorkItem(delegate
				{
					work();
				}))
				{
					throw new InvalidOperationException("Cannot queue manual memory cleanup");
				}
			}
			catch
			{
				Interlocked.Exchange(ref manualMemoryPending, 0);
				overviewMemoryCleanup.ShowError(ManualMemoryText.T("无法启动清理 · 可重试", "Cannot start cleanup · retry available", "開始できません · 再試行できます"));
			}
		}

		private void PostManualMemoryUi(Action action)
		{
			if (manualMemoryDisposed || base.IsDisposed || base.Disposing || !base.IsHandleCreated)
			{
				return;
			}
			try
			{
				BeginInvoke((MethodInvoker)delegate
				{
					if (!manualMemoryDisposed && !base.IsDisposed && !base.Disposing)
					{
						action();
					}
				});
			}
			catch (InvalidOperationException)
			{
			}
		}

		private void RefreshOverviewAttention()
		{
			if (lblStatus != null && !lblStatus.IsDisposed && gameMode != null)
			{
				string text = gameMode.ActiveGame ?? Lang.T("workflow.game.waiting");
				if (lblStatus.Text != text)
				{
					lblStatus.Text = text;
					FitLabelFont(lblStatus, true, 14.5f, 9.5f);
				}
				bool flag = gameMode.Enabled && gameMode.IsActive;
				statusDot.Color = ((!gameMode.Enabled) ? Theme.Dim : (flag ? Theme.Green : Theme.Accent));
				statusDot.Pulse = flag;
				swGame.SetSilently(gameMode.Enabled);
				swGame.AccessibleDescription = gameMode.StatusText;
				RefreshOverviewGuardActivity();
				paviseCore.AccessibleName = ModeButton.ModeName(gameMode.ActivePreset);
				paviseCore.AccessibleDescription = gameMode.StatusText;
				if (overviewLogVersion != Logger.Version)
				{
					overviewLogVersion = Logger.Version;
					int warnings;
					LogStreamView.CountIssues(Logger.Tail(220), out warnings, out overviewErrors);
				}
				overviewAction = ((!elevated) ? "admin" : (gameMode.ProfileStoreSaveFailed ? "library" : ((gameMode.Enabled && overviewErrors > 0) ? "logs" : "")));
			}
		}

		private void BuildOverviewHud()
		{
			overviewHudGeneration++;
			overviewSpecs = new OverviewSpecItem[4];
			overviewUsage = new OverviewUsageItem[3];
			string[] array = new string[4] { "cpu", "gpu", "memory", "display" };
			for (int i = 0; i < overviewSpecs.Length; i++)
			{
				overviewSpecs[i] = new OverviewSpecItem
				{
					Name = "overviewSpec" + array[i]
				};
				pageOverview.Controls.Add(overviewSpecs[i]);
			}
			for (int j = 0; j < overviewUsage.Length; j++)
			{
				overviewUsage[j] = new OverviewUsageItem
				{
					Name = "overviewUsage" + j
				};
				pageOverview.Controls.Add(overviewUsage[j]);
			}
			BuildManualMemoryCleaner(overviewUsage[2]);
			if (!overviewDisposeRegistered)
			{
				overviewDisposeRegistered = true;
				base.Disposed += delegate
				{
					DisposeOverviewTelemetry();
				};
			}
			PresentOverviewHardware(overviewHardware);
			PresentOverviewResources(overviewResources);
		}

		private static string OverviewCapacity(ulong bytes, string format)
		{
			return ((double)bytes / 1073741824.0).ToString(format, CultureInfo.InvariantCulture);
		}

		private static string OverviewKnown(string value)
		{
			if (!string.IsNullOrWhiteSpace(value))
			{
				return value.Trim();
			}
			return "--";
		}

		private static string OverviewModelName(string value, bool gpu)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				return "--";
			}
			string input = Regex.Replace(value, "\\((?:R|TM|C)\\)|[®™©]", "", RegexOptions.IgnoreCase);
			input = Regex.Replace(input, "\\s+", " ").Trim();
			if (gpu)
			{
				input = Regex.Replace(input, "\\s+(?:with\\s+)?Max-Q(?:\\s+Design)?\\b", "", RegexOptions.IgnoreCase);
			}
			else
			{
				input = Regex.Replace(input, "\\s*@\\s*\\d+(?:[.,]\\d+)?\\s*[GM]Hz\\s*$", "", RegexOptions.IgnoreCase);
				input = Regex.Replace(input, "\\s+CPU\\b|\\s+Processor\\s*$", "", RegexOptions.IgnoreCase);
			}
			return OverviewKnown(input);
		}

		private bool SetOverviewSpec(int index, string caption, string value, string detail, string icon)
		{
			OverviewSpecItem overviewSpecItem = overviewSpecs[index];
			bool result = overviewSpecItem.Caption != caption || overviewSpecItem.Value != value || overviewSpecItem.Detail != detail;
			overviewSpecItem.SetContent(caption, value, detail, icon);
			return result;
		}

		private void PresentOverviewHardware(OverviewHardwareInfo hardware)
		{
			overviewHardware = hardware;
			if (overviewSpecs != null)
			{
				hardware = hardware ?? new OverviewHardwareInfo();
				string text = ((hardware.PhysicalCores > 0 && hardware.LogicalProcessors > 0) ? (hardware.PhysicalCores + PerformancePolicy.Text(" 核 · ", " cores · ") + hardware.LogicalProcessors + PerformancePolicy.Text(" 线程", " threads")) : "");
				bool flag = SetOverviewSpec(0, "CPU", OverviewModelName(hardware.CpuName, false), text, "cpu");
				string text2 = ((hardware.GpuMemoryBytes != 0) ? (OverviewCapacity(hardware.GpuMemoryBytes, "0.#") + " GB" + PerformancePolicy.Text(" 显存", " VRAM")) : "");
				if (!string.IsNullOrEmpty(hardware.GpuName) && hardware.GpuName.IndexOf("Max-Q", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					text2 = text2 + ((text2.Length > 0) ? " · " : "") + "Max-Q";
				}
				flag |= SetOverviewSpec(1, "GPU", OverviewModelName(hardware.GpuName, true), text2, "gpu");
				if (!string.IsNullOrWhiteSpace(hardware.CpuName))
				{
					overviewSpecs[0].AccessibleDescription = overviewSpecs[0].Value + "\r\n" + hardware.CpuName.Trim() + "\r\n" + text;
				}
				if (!string.IsNullOrWhiteSpace(hardware.GpuName))
				{
					overviewSpecs[1].AccessibleDescription = overviewSpecs[1].Value + "\r\n" + hardware.GpuName.Trim() + "\r\n" + text2;
				}
				string text3 = ((hardware.InstalledMemoryBytes != 0) ? (OverviewCapacity(hardware.InstalledMemoryBytes, "0") + " GB") : "--");
				if (!string.IsNullOrWhiteSpace(hardware.MemoryType))
				{
					text3 = text3 + " " + hardware.MemoryType.Trim();
				}
				flag |= SetOverviewSpec(2, "MEMORY", text3, (hardware.MemorySpeedMts > 0) ? (hardware.MemorySpeedMts + " MT/s") : "", "memory");
				if (flag | SetOverviewSpec(3, "DISPLAY", (hardware.DisplayWidth > 0 && hardware.DisplayHeight > 0) ? (hardware.DisplayWidth + " × " + hardware.DisplayHeight) : "--", (hardware.DisplayRefreshRate > 0) ? (hardware.DisplayRefreshRate + " Hz") : "", "display"))
				{
					overviewViewport = Size.Empty;
					LayoutOverviewPage();
				}
			}
		}

		private static double? OverviewPercent(double? value)
		{
			if (value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value) && !(value.Value < 0.0) && !(value.Value > 100.0))
			{
				return value;
			}
			return null;
		}

		private static string OverviewPercentText(double? value)
		{
			if (!value.HasValue)
			{
				return "--";
			}
			return value.Value.ToString("0", CultureInfo.InvariantCulture) + "%";
		}

		private void PresentOverviewResources(OverviewResourceSnapshot resources)
		{
			overviewResources = resources;
			if (overviewUsage != null)
			{
				resources = resources ?? new OverviewResourceSnapshot();
				int[] array = new int[overviewUsage.Length];
				for (int i = 0; i < overviewUsage.Length; i++)
				{
					array[i] = overviewUsage[i].MeasureHeight(overviewUsage[i].Width);
				}
				double? num = OverviewPercent(resources.CpuPercent);
				double? num2 = OverviewPercent(resources.GpuPercent);
				overviewUsage[0].SetReading(PerformancePolicy.Text("CPU 占用", "CPU usage"), OverviewPercentText(num), "", num);
				overviewUsage[1].SetReading(PerformancePolicy.Text("GPU 占用", "GPU usage"), OverviewPercentText(num2), "", num2);
				if (!string.IsNullOrEmpty(resources.GpuUsageDescription))
				{
					overviewUsage[1].AccessibleDescription = overviewUsage[1].Value + "\r\n" + resources.GpuUsageDescription;
				}
				double? num3 = ((resources.TotalMemoryBytes != 0 && resources.UsedMemoryBytes <= resources.TotalMemoryBytes) ? new double?((double)resources.UsedMemoryBytes * 100.0 / (double)resources.TotalMemoryBytes) : null);
				string value = (num3.HasValue ? (OverviewCapacity(resources.UsedMemoryBytes, "0.0") + " / " + OverviewCapacity(resources.TotalMemoryBytes, "0.#") + " GB") : "--");
				overviewUsage[2].SetReading(PerformancePolicy.Text("内存", "Memory"), value, num3.HasValue ? OverviewPercentText(num3) : "", num3);
				bool flag = false;
				for (int j = 0; j < overviewUsage.Length; j++)
				{
					flag |= array[j] != overviewUsage[j].MeasureHeight(overviewUsage[j].Width);
				}
				if (flag)
				{
					overviewViewport = Size.Empty;
					LayoutOverviewPage();
				}
			}
		}

		private static int OverviewMeasuredHeight(Control control, int width)
		{
			OverviewSpecItem overviewSpecItem = control as OverviewSpecItem;
			OverviewUsageItem overviewUsageItem = control as OverviewUsageItem;
			int num = ((overviewSpecItem != null) ? overviewSpecItem.MeasureHeight(Theme.S(width)) : overviewUsageItem.MeasureHeight(Theme.S(width)));
			return Math.Max(1, (int)Math.Ceiling((float)num / Math.Max(0.1f, Dpi.Scale)));
		}

		private int ArrangeOverviewHud(int pageWidth, int pageHeight, ref int coreWidth, ref int coreHeight, ref int coreY, ref int statusY)
		{
			if (overviewSpecs == null || overviewUsage == null)
			{
				return pageHeight;
			}
			int num = ((pageWidth >= 1100) ? 64 : 28);
			int num2 = 20;
			int num3 = Math.Min(264, Math.Max(188, (pageWidth - 560 - (num + num2) * 2) / 2));
			int num4 = Math.Min(coreWidth, pageWidth - (num + num2 + num3) * 2);
			bool flag = pageWidth >= 900 && num4 >= 340;
			int num5 = 0;
			int num6 = 0;
			int num7 = Math.Min(208, num3);
			OverviewSpecItem[] array = overviewSpecs;
			foreach (OverviewSpecItem overviewSpecItem in array)
			{
				overviewSpecItem.Compact = !flag || pageWidth < 1080;
				num5 += OverviewMeasuredHeight(overviewSpecItem, num3);
			}
			OverviewUsageItem[] array2 = overviewUsage;
			foreach (OverviewUsageItem overviewUsageItem in array2)
			{
				overviewUsageItem.Compact = !flag || pageWidth < 1080;
				num6 += OverviewMeasuredHeight(overviewUsageItem, num7);
			}
			num5 += 36;
			num6 += 36;
			if (flag & (Math.Max(num5, num6) + 32 <= statusY))
			{
				coreWidth = num4;
				coreHeight = Math.Min(coreHeight, (int)Math.Round((float)coreWidth * 450f / 580f));
				coreY = Math.Max(0, (statusY - (overviewSecurityWarningShown ? 44 : 0) - coreHeight) / 2);
				int num8 = Math.Max(8, Math.Min((statusY - num5) / 2 + 24, statusY - num5 - 16));
				OverviewSpecItem[] array3 = overviewSpecs;
				foreach (OverviewSpecItem control in array3)
				{
					int num9 = OverviewMeasuredHeight(control, num3);
					SetOverviewBounds(control, num, num8, num3, num9);
					num8 += num9 + 12;
				}
				num8 = Math.Max(8, Math.Min((statusY - num6) / 2 + 24, statusY - num6 - 16));
				OverviewUsageItem[] array4 = overviewUsage;
				foreach (OverviewUsageItem control2 in array4)
				{
					int num10 = OverviewMeasuredHeight(control2, num7);
					SetOverviewBounds(control2, pageWidth - num - num7, num8, num7, num10);
					num8 += num10 + 18;
				}
				return pageHeight;
			}
			int num11 = Math.Max(1, pageWidth - Dpi.U(SystemInformation.VerticalScrollBarWidth));
			num = ((num11 >= 760) ? 32 : 20);
			bool flag2 = num11 >= 760;
			coreWidth = Math.Min(460, Math.Max(180, num11 - num * 2));
			coreHeight = Math.Min((int)Math.Round((float)coreWidth * 450f / 580f), Math.Max(1, pageHeight - 16));
			coreWidth = Math.Min(coreWidth, (int)Math.Round((float)coreHeight * 580f / 450f));
			coreY = 8;
			statusY = coreY + coreHeight + 8 + (overviewSecurityWarningShown ? 44 : 0);
			int num12 = statusY + 62 + (overviewActivityShown ? 30 : 0);
			int num13 = Math.Max(1, num11 - num * 2);
			int num14 = (flag2 ? ((num13 - 48) / 2) : num13);
			int num15 = num12;
			int num16 = num12;
			OverviewSpecItem[] array5 = overviewSpecs;
			foreach (OverviewSpecItem overviewSpecItem2 in array5)
			{
				overviewSpecItem2.Compact = true;
				int num17 = OverviewMeasuredHeight(overviewSpecItem2, num14);
				SetOverviewBounds(overviewSpecItem2, num, num16, num14, num17);
				num16 += num17 + 12;
			}
			num15 = num16;
			num16 = (flag2 ? num12 : (num16 + 16));
			OverviewUsageItem[] array6 = overviewUsage;
			foreach (OverviewUsageItem overviewUsageItem2 in array6)
			{
				overviewUsageItem2.Compact = true;
				int num18 = OverviewMeasuredHeight(overviewUsageItem2, num14);
				SetOverviewBounds(overviewUsageItem2, flag2 ? (num + num14 + 48) : num, num16, num14, num18);
				num16 += num18 + 18;
			}
			return Math.Max(pageHeight, Math.Max(num15, num16) + 12);
		}

		private void SetOverviewTelemetryActive(bool active)
		{
			if (!overviewTelemetryDisposed)
			{
				if (overviewTelemetryActive != active)
				{
					overviewTelemetryActive = active;
					overviewHudGeneration++;
					overviewNextTelemetry = 0L;
					if (!active)
					{
						overviewResources = null;
						PresentOverviewResources(null);
						ReleaseOverviewTelemetryReader();
					}
				}
				if (active)
				{
					QueueOverviewTelemetry(true);
				}
			}
		}

		private bool OverviewTelemetryRequestCurrent(int generation)
		{
			return !overviewTelemetryDisposed && overviewTelemetryActive
				&& generation == overviewHudGeneration;
		}

		private bool QueueOverviewTelemetryWork(Action work)
		{
			return ThreadPool.QueueUserWorkItem(delegate { work(); });
		}

		private void ReleaseOverviewTelemetryReader()
		{
			QueueOverviewTelemetryWork(delegate
			{
				lock (overviewReaderGate)
				{
					if (overviewTelemetryDisposed || !overviewTelemetryActive)
					{
						if (overviewReader != null) overviewReader.Dispose();
						overviewReader = null;
						overviewReaderGeneration = -1;
					}
				}
			});
		}

		private void QueueOverviewTelemetry(bool force)
		{
			if (!overviewTelemetryActive || overviewTelemetryDisposed || base.IsDisposed || base.Disposing || !base.IsHandleCreated)
			{
				return;
			}
			long timestamp = Stopwatch.GetTimestamp();
			if ((!force && timestamp < overviewNextTelemetry) || Interlocked.CompareExchange(ref overviewTelemetryPending, 1, 0) != 0)
			{
				return;
			}
			overviewNextTelemetry = timestamp + Stopwatch.Frequency * 2;
			int generation = overviewHudGeneration;
			string display = Screen.FromControl(this).DeviceName;
			Action work = delegate
			{
				OverviewHardwareInfo hardware = null;
				OverviewResourceSnapshot resources = null;
				try
				{
					if (OverviewTelemetryRequestCurrent(generation))
					{
						lock (overviewReaderGate)
						{
							if (!OverviewTelemetryRequestCurrent(generation))
							{
								return;
							}
							if (overviewReader == null)
							{
								overviewReader = new OverviewTelemetryReader();
							}
							if (overviewReaderGeneration != generation)
							{
								overviewReader.ResetCounters();
								overviewReaderGeneration = generation;
							}
							hardware = overviewReader.ReadHardware(display);
							if (!OverviewTelemetryRequestCurrent(generation)) return;
							resources = overviewReader.Sample();
						}
						if (OverviewTelemetryRequestCurrent(generation) && base.IsHandleCreated)
						{
							BeginInvoke((MethodInvoker)delegate
							{
								if (OverviewTelemetryRequestCurrent(generation) && !base.IsDisposed && !base.Disposing)
								{
									PresentOverviewHardware(hardware);
									PresentOverviewResources(resources);
								}
							});
						}
					}
				}
				catch (ObjectDisposedException)
				{
				}
				catch (InvalidOperationException)
				{
				}
				catch (Exception)
				{
				}
				finally
				{
					Interlocked.Exchange(ref overviewTelemetryPending, 0);
				}
			};
			try
			{
				if (!QueueOverviewTelemetryWork(work))
				{
					Interlocked.Exchange(ref overviewTelemetryPending, 0);
				}
			}
			catch
			{
				Interlocked.Exchange(ref overviewTelemetryPending, 0);
			}
		}

		private void DisposeOverviewTelemetry()
		{
			if (overviewTelemetryDisposed)
			{
				return;
			}
			overviewTelemetryDisposed = true;
			overviewTelemetryActive = false;
			overviewHudGeneration++;
			ThreadPool.QueueUserWorkItem(delegate
			{
				lock (overviewReaderGate)
				{
					if (overviewReader != null)
					{
						overviewReader.Dispose();
						overviewReader = null;
					}
				}
			});
		}

		private void BuildOverviewPage()
		{
			lblOverviewBoost = null;
			lblHeroMode = (lblHeroSource = null);
			btnUpdateHint = null;
			updateBadge = null;
			overviewViewport = Size.Empty;
			pageOverview.AutoScroll = false;
			Native.Dark(pageOverview);
			pageOverview.AutoScrollMinSize = Size.Empty;
			paviseCore = new PaviseCore
			{
				Name = "overviewAnimatedCore",
				Immersive = true,
				TabStop = false
			};
			paviseCore.SetState(gameMode.ActivePreset, gameMode.Enabled, gameMode.IsActive);
			pageOverview.Controls.Add(paviseCore);
			statusDot = new StatusDot
			{
				Name = "overviewStatusDot",
				Tag = "workspace-canvas",
				Bg = Theme.Bg,
				TabStop = false
			};
			statusDot.Color = (gameMode.Enabled ? Theme.Accent : Theme.Dim);
			statusDot.Pulse = gameMode.Enabled && gameMode.IsActive;
			pageOverview.Controls.Add(statusDot);
			lblStatus = CardLabel(pageOverview, gameMode.ActiveGame ?? Lang.T("workflow.game.waiting"), 0, 0, 280, 42, 14.5f, true, Theme.Fg);
			lblStatus.Name = "overviewGameStatus";
			lblStatus.TextAlign = ContentAlignment.MiddleLeft;
			lblStatus.TextChanged += delegate
			{
				overviewViewport = Size.Empty;
				LayoutOverviewPage();
			};
			overviewStatusDivider = new Panel
			{
				Name = "overviewStatusDivider",
				BackColor = Theme.StrokeHi
			};
			pageOverview.Controls.Add(overviewStatusDivider);
			string text = ((Lang.Cur == 0) ? "守护" : ((Lang.Cur == 2) ? "保護" : "Guard"));
			lblOverviewGuard = CardLabel(pageOverview, text, 0, 0, 42, 42, 11.5f, false, Theme.Dim);
			lblOverviewGuard.Name = "overviewGuardLabel";
			lblOverviewGuard.TextAlign = ContentAlignment.MiddleLeft;
			swGame = MakeSwitch(gameMode.Enabled, delegate
			{
				gameMode.Enabled = swGame.Checked;
				Settings.Save("GameModeOn", swGame.Checked);
				UpdateModePresentation(true);
				RefreshOverviewAttention();
			});
			swGame.Name = "overviewGuardToggle";
			swGame.OverviewGuard = true;
			swGame.Tag = "workspace-canvas";
			swGame.Bg = Theme.Bg;
			swGame.TabIndex = 0;
			swGame.AccessibleName = Lang.T("v14.master");
			pageOverview.Controls.Add(swGame);
			BuildOverviewSecurityWarning();
			BuildOverviewGuardActivity();
			BuildOverviewHud();
			RefreshUpdatePresentation();
			UpdateModePresentation(false);
			RefreshOverviewAttention();
			pageOverview.SizeChanged += delegate
			{
				LayoutOverviewPage();
			};
			LayoutOverviewPage();
		}

		private static void SetOverviewBounds(Control control, int x, int y, int width, int height)
		{
			Rectangle rectangle = new Rectangle(Theme.S(x), Theme.S(y), Theme.S(Math.Max(1, width)), Theme.S(Math.Max(1, height)));
			if (control.Bounds != rectangle)
			{
				control.Bounds = rectangle;
			}
		}

		private void LayoutOverviewPage()
		{
			if (overviewArranging || pageOverview == null || pageOverview.IsDisposed || paviseCore == null || swGame == null || lblOverviewGuard == null || overviewViewport == pageOverview.ClientSize)
			{
				return;
			}
			overviewArranging = true;
			pageOverview.SuspendLayout();
			try
			{
				Point point = new Point(0, Math.Max(0, -pageOverview.AutoScrollPosition.Y));
				pageOverview.AutoScrollPosition = Point.Empty;
				pageOverview.AutoScrollMinSize = Size.Empty;
				pageOverview.AutoScroll = false;
				int num = Math.Max(1, Dpi.U(pageOverview.ClientSize.Width));
				int num2 = Math.Max(1, Dpi.U(pageOverview.ClientSize.Height));
				int num3 = 42;
				int statusY = Math.Max(8, Math.Min(num2 - num3 - 8 - (overviewActivityShown ? 30 : 0), (int)Math.Round((float)num2 * 0.87f) - num3 / 2));
				int warningReserve = overviewSecurityWarningShown ? 44 : 0;
				int val = Math.Max(1, Math.Min(450, statusY - 26 - warningReserve));
				int num4 = Math.Max(1, Math.Min(580, num - 48));
				val = Math.Min(val, (int)Math.Round((float)num4 * 450f / 580f));
				num4 = Math.Min(num4, (int)Math.Round((float)val * 580f / 450f));
				int coreY = Math.Max(0, (statusY - warningReserve - val) / 2);
				int num5 = ArrangeOverviewHud(num, num2, ref num4, ref val, ref coreY, ref statusY);
				SetOverviewBounds(paviseCore, (num - num4) / 2, coreY, num4, val);
				int num6 = ((num5 > num2) ? Math.Max(1, num - Dpi.U(SystemInformation.VerticalScrollBarWidth)) : num);
				int num7 = (int)Math.Ceiling((float)TextRenderer.MeasureText(lblOverviewGuard.Text, lblOverviewGuard.Font, Size.Empty, TextFormatFlags.NoPadding).Width / Math.Max(0.1f, Dpi.Scale));
				int val2 = (int)Math.Ceiling((float)TextRenderer.MeasureText(lblStatus.Text, Theme.UI(14.5f, true), Size.Empty, TextFormatFlags.NoPadding).Width / Math.Max(0.1f, Dpi.Scale)) + 3;
				int num8 = 86 + num7 + 12 + 72;
				val2 = Math.Min(val2, Math.Max(24, num6 - 32 - num8));
				int num9 = num8 + val2;
				int num10 = Math.Max(8, (num6 - num9) / 2);
				SetOverviewBounds(statusDot, num10, statusY + 9, 24, 24);
				num10 += 33;
				SetOverviewBounds(lblStatus, num10, statusY, val2, num3);
				FitLabelFont(lblStatus, true, 14.5f, 9.5f);
				num10 += val2 + 26;
				SetOverviewBounds(overviewStatusDivider, num10, statusY + 11, 1, 20);
				num10 += 27;
				SetOverviewBounds(lblOverviewGuard, num10, statusY, num7, num3);
				num10 += num7 + 12;
				SetOverviewBounds(swGame, num10, statusY + 5, 72, 32);
				LayoutOverviewSecurityWarning(num6, statusY);
				if (overviewActivity != null)
				{
					SetOverviewBounds(overviewActivity, 16, statusY + num3 + 2, Math.Max(1, num6 - 32), 26);
					if (overviewActivityShown) num5 = Math.Max(num5, statusY + num3 + 36);
				}
				pageOverview.AutoScrollMinSize = ((num5 > num2) ? new Size(0, Theme.S(num5)) : Size.Empty);
				pageOverview.AutoScroll = num5 > num2;
				if (pageOverview.AutoScroll)
				{
					pageOverview.AutoScrollPosition = new Point(0, Math.Min(point.Y, DockScrollLimit(pageOverview)));
				}
				overviewViewport = pageOverview.ClientSize;
			}
			finally
			{
				try
				{
					pageOverview.ResumeLayout(true);
				}
				finally
				{
					overviewArranging = false;
				}
			}
		}

		public void NotifyLastSession(string brief)
		{
			try
			{
				if (!base.IsHandleCreated || base.IsDisposed || string.IsNullOrEmpty(brief))
				{
					return;
				}
				BeginInvoke((MethodInvoker)delegate
				{
					if (!base.IsDisposed)
					{
						RefreshOverviewAttention();
					}
				});
			}
			catch
			{
			}
		}

		private RogLinkButton AddOverviewLink(Control parent, int x, int y, int w, int h, string text, string code, string glyph, string url)
		{
			RogLinkButton rogLinkButton = new RogLinkButton(text, code, glyph);
			rogLinkButton.Bg = Theme.Nav;
			rogLinkButton.SetBounds(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(h));
			if (url != null)
			{
				rogLinkButton.Click += delegate
				{
					OpenExternal(url);
				};
			}
			parent.Controls.Add(rogLinkButton);
			return rogLinkButton;
		}

		public void NotifyUpdate(UpdateResult result)
		{
			try
			{
				PostUiStateResult(delegate
				{
					ApplyUpdateResult(result);
				});
			}
			catch
			{
			}
		}

		private void ApplyUpdateResult(UpdateResult result)
		{
			if (!base.IsDisposed && result != null && result.Ok && !string.IsNullOrEmpty(result.Latest))
			{
				if (latestUpdate == null || !UpdateChecker.IsNewer(latestUpdate.Latest, result.Latest))
				{
					latestUpdate = result;
				}
				RefreshUpdatePresentation();
			}
		}

		private void RefreshUpdatePresentation()
		{
			bool flag = latestUpdate != null && UpdateChecker.IsNewer(latestUpdate.Latest, App.Version);
			if (btnUpdateHint != null && !btnUpdateHint.IsDisposed)
			{
				btnUpdateHint.Text = (flag ? Lang.F("site.update.available", latestUpdate.Latest) : "");
				btnUpdateHint.AccessibleName = btnUpdateHint.Text;
				btnUpdateHint.Visible = flag;
			}
			if (updateBadge != null && !updateBadge.IsDisposed)
			{
				updateBadge.Visible = flag;
			}
			RefreshAboutUpdate();
		}

		public void NotifyDonate(DonateInfo d)
		{
			if (d != null)
			{
				DonateCache.Latest = d;
			}
		}

		private void RefreshDonateButton()
		{
			if (btnDonate != null && !btnDonate.IsDisposed)
			{
				btnDonate.Text = Lang.T(donateLoading ? "donate.loading" : "donate.entry");
				btnDonate.AccessibleName = btnDonate.Text;
				btnDonate.Enabled = !donateLoading;
				btnDonate.Cursor = (donateLoading ? Cursors.WaitCursor : Cursors.Hand);
				btnDonate.Invalidate();
			}
		}

		private void ShowDonate()
		{
			if (donateLoading || base.IsDisposed)
			{
				return;
			}
			DonateInfo effective = DonateCache.Effective;
			bool hasImage = DonateCache.HasImage;
			if (hasImage && !DonateCache.NeedsRefresh(DonateCache.CachedId, effective.Id, true))
			{
				OpenDonate(DonateCache.LoadCached(), false);
				return;
			}
			donateLoading = true;
			RefreshDonateButton();
			try
			{
				FetchDonateImage(delegate(byte[] bytes, DonateInfo info)
				{
					try
					{
						PostUiStateResult(delegate
						{
							CompleteDonate(bytes, info);
						});
					}
					catch
					{
						donateLoading = false;
					}
				});
			}
			catch
			{
				CompleteDonate(null, null);
				return;
			}
			OpenDonate(hasImage ? DonateCache.LoadCached() : null, true);
		}

		private static void FetchDonateImage(Action<byte[], DonateInfo> done)
		{
			Action<DonateInfo> pull = delegate(DonateInfo i)
			{
				byte[] arg = UpdateChecker.FetchBytes(i.Url, 2097152);
				done(arg, i);
			};
			DonateInfo known = DonateCache.Latest;
			if (known != null)
			{
				ThreadPool.QueueUserWorkItem(delegate
				{
					pull(known);
				});
				return;
			}
			UpdateChecker.CheckAsync(delegate(UpdateResult r)
			{
				DonateInfo donateInfo = ((r != null && r.Ok) ? r.Donate : null);
				if (donateInfo != null)
				{
					DonateCache.Latest = donateInfo;
				}
				pull(donateInfo ?? DonateCache.Default);
			});
		}

		private void CompleteDonate(byte[] bytes, DonateInfo info)
		{
			donateLoading = false;
			if (base.IsDisposed)
			{
				return;
			}
			Bitmap bitmap = null;
			if (bytes != null && info != null && DonateCache.Store(bytes, info.Id))
			{
				bitmap = DonateCache.Decode(bytes);
			}
			RefreshDonateButton();
			if (donateDialog != null && !donateDialog.IsDisposed)
			{
				if (bitmap != null)
				{
					donateDialog.SetImage(bitmap);
				}
				else
				{
					donateDialog.MarkFailed();
				}
			}
			else if (bitmap != null)
			{
				bitmap.Dispose();
			}
		}

		private void OpenDonate(Bitmap qr, bool loading)
		{
			using (DonateDialog donateDialog = new DonateDialog(qr, loading))
			{
				this.donateDialog = donateDialog;
				try
				{
					donateDialog.ShowDialog(this);
				}
				finally
				{
					this.donateDialog = null;
				}
			}
		}

		private void OpenExternal(string url)
		{
			if (string.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			try
			{
				using (Process.Start(url))
				{
				}
			}
			catch
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("v211.link.failed"));
			}
		}

		private void RefreshBoostPresentation()
		{
			if (lblOverviewBoost != null)
			{
				string boostStatusText = gameMode.BoostStatusText;
				if (lblOverviewBoost.Text != boostStatusText)
				{
					lblOverviewBoost.Text = boostStatusText;
				}
				lblOverviewBoost.ForeColor = (gameMode.BoostStateVerified ? Theme.Green : (gameMode.BoostHandleProtected ? Theme.Dim : Theme.Fg));
			}
		}

		private static string PerformanceFeatureKey(string name)
		{
			string text = name ?? "";
			string[] array = new string[4] { "manualPerformance_", "privacyToggle", "environment", "laboratory_" };
			foreach (string text2 in array)
			{
				if (text.StartsWith(text2, StringComparison.Ordinal))
				{
					text = text.Substring(text2.Length);
					break;
				}
			}
			switch (text)
			{
			case "laboratoryCpuIdle":
				return "LabCpuIdleStrategyV1";
            case "backgroundGovernanceMode":
                return "GmSuppress";
			case "WindowedOpt":
				return "WindowedOptimizations";
			case "VrrOpt":
				return "VariableRefreshRate";
			case "NvVrr":
				return "NvVrrWindowed";
			case "GameLowLatency":
				return "NvLowLat";
			default:
				return text;
			}
		}

		private static void ConfigurePerformanceConsole(PerformanceFeatureCard card, string key)
		{
			card.IconKey = key;
			switch (key)
			{
			case "GmBoost":
				card.Summary = AutoText("优先分配 CPU、GPU 与 I/O 资源", "Prioritize CPU, GPU and I/O resources");
				break;
			case "GmAwake":
				card.DisplayTitle = AutoText("对局保持唤醒", "Keep the match awake");
				card.Summary = AutoText("游戏前台时阻止息屏与睡眠", "Prevent display sleep while the game is foreground");
				break;
			case "GmThreadBoostStrategyV1":
				card.Summary = AutoText("按游戏自动提优渲染或最忙线程", "Boosts the render or busiest thread per game");
				break;
			case "AutoNtfsShortNamesV1":
				card.Summary = AutoText("停止生成新的 8.3 短文件名", "Stop creating new 8.3 short file names");
				break;
			case "GmCacheWarmV2":
				card.Summary = AutoText("空闲时预读，入局即取消", "Pre-read while idle; cancel on match entry");
				break;
			case "AutoGameMitigationsV1":
				card.Summary = AutoText("降低部分游戏防护，减少额外开销", "Reduce selected game protections and their overhead");
				break;
			case "GmMmcss":
				card.Summary = AutoText("调整游戏多媒体线程调度", "Adjust game multimedia thread scheduling");
				break;
			case LaptopPerfMode.PreferenceKey:
				card.Summary = AutoText("插电对局切换厂商性能档，退局恢复原档", "Use the vendor performance profile on AC; restore after matches");
				break;
			case "GmSuppress":
				card.Summary = Lang.T("background.governance.summary");
				break;
			case "GmPauseDl":
				card.Summary = AutoText("对局期间暂停支持的后台下载", "Pause supported background downloads during matches");
				break;
			case "GmPauseUpdate":
				card.Summary = AutoText("对局期间暂缓更新，退局恢复", "Pause updates during matches, restore on exit");
				break;
			case "GmPauseMaintenanceV1":
				card.Summary = AutoText("暂缓维护任务，减少资源竞争", "Defer maintenance to reduce resource contention");
				break;
			case "AutoBackgroundFpsV1":
				card.Summary = AutoText("限制合格后台渲染，不限制游戏前台", "Cap eligible background rendering, leave foreground uncapped");
				break;
			case "GmDwmBoost":
				card.Summary = AutoText("调整对局期间的桌面合成调度", "Adjust desktop compositor scheduling during matches");
				break;
			case "NvMaxPerf":
				card.Summary = AutoText("为游戏配置 NVIDIA 电源管理偏好", "Set NVIDIA power-management preferences for games");
				break;
			case "NvShaderCache":
				card.Summary = AutoText("为游戏配置 NVIDIA 着色器缓存", "Configure NVIDIA shader caching for games");
				break;
			case "GmIntelEnduranceV1":
				card.Summary = AutoText("关闭 Intel 图形续航限制，可能增加功耗", "Disable Intel graphics endurance limits; power use may rise");
				break;
			case "GmEnglishInputV1":
				card.Summary = AutoText("入局时为确认的游戏窗口切换英文", "Switch the confirmed game window to English on entry");
				break;
			case "GpuPrefStageOn":
				card.Summary = AutoText("为游戏登记高性能显卡偏好", "Prefer the high-performance GPU for games");
				break;
			case "AutoOpenGlThreadsV1":
				card.Summary = AutoText("为 NVIDIA OpenGL 游戏配置线程优化", "Configure threading for NVIDIA OpenGL games");
				break;
			case "Hags":
				card.Summary = AutoText("使用硬件加速 GPU 调度，视设备支持", "Use hardware-accelerated GPU scheduling where supported");
				break;
			case "WindowedOptimizations":
				card.Summary = AutoText("优化兼容的窗口与无边框游戏", "Optimize compatible windowed and borderless games");
				break;
			case "VariableRefreshRate":
				card.Summary = AutoText("为兼容的 DX11 全屏游戏补充 VRR", "Add VRR for compatible DX11 fullscreen games");
				break;
			case "NvVrrWindowed":
				card.Summary = AutoText("对局中补充窗口 VRR，退局还原", "Add windowed VRR during matches; restore on exit");
				break;
			case "AmdSam":
				card.Summary = AutoText("为受支持的设备启用 AMD SAM", "Enable AMD SAM on supported hardware");
				break;
			case "GameDvrOff":
				card.Summary = AutoText("守护期间关闭录制，停守护后还原", "Disable capture while guarding; restore when guarding stops");
				break;
			case "NvLowLat":
				card.Summary = AutoText("对局启用驱动低延迟，退局还原", "Use driver low latency during matches; restore on exit");
				break;
			case "EnvPerformanceBundleV1":
				card.Summary = AutoText("关闭 VBS 与 CPU 缓解，降低相应防护", "Disable VBS and CPU mitigations, reducing those protections");
				break;
			case "EnvCpuMitigationsV1":
				card.Summary = AutoText("关闭适用的软件缓解，降低相应防护", "Disable applicable software mitigations, reducing those protections");
				break;
			case "EnvStoragePowerGroupV1":
				card.Summary = AutoText("减少存储设备省电等待，视驱动支持", "Reduce storage power-saving delays where drivers support it");
				break;
			case "EnvQuantumProfileV1":
				card.Summary = AutoText("选择前台与后台任务的时间片策略", "Choose foreground and background scheduling quanta");
				break;
			case "AutoTimerTick":
				card.Summary = AutoText("调整启动时钟与节拍参数", "Adjust boot clock and tick parameters");
				break;
			case "AutoGlobalTimer":
				card.Summary = AutoText("仅对局请求 0.5 ms，实际精度由系统决定", "Request 0.5 ms in matches; Windows determines the result");
				break;
			case "LabEppYieldV1":
				card.Summary = AutoText("插电笔记本按功耗与瓶颈验证，失败即恢复", "Verify laptop power and bottlenecks on AC; revert on failure");
				break;
			case "LabGpuClockMaxV1":
				card.Summary = AutoText("NVIDIA 不保留原锁频，退局改回自动", "NVIDIA: exit restores auto clocks; existing locks are lost");
				break;
			case "LabGpuPowerMaxV1":
				card.Summary = AutoText("请求驱动最大功耗上限，可能增加发热；退局恢复", "Request the driver maximum power limit; heat may rise; restore on exit");
				break;
			case "LabAudioLowBufferV1":
				card.Summary = AutoText("申请较短共享音频周期，可能增加唤醒或爆音", "Request a shorter shared audio period; wakeups or crackles may increase");
				break;
			case "LabCpuIdleStrategyV1":
				card.Summary = AutoText("插电时作用当前计划，退局恢复；禁止空闲可能增热", "Current plan on AC; restore on exit; disabled idle may increase heat");
				break;
			case "EnvPrivacyBackgroundGlobalV1":
				card.Summary = AutoText("限制 Windows 应用后台活动，可能影响通知与同步", "Restrict Windows app background activity; notifications and sync may be affected");
				break;
			case "EnvPrivacyNtfsMemoryV1":
				card.Summary = AutoText("内存充足时扩大文件系统缓存", "Expand filesystem caching when memory allows");
				break;
			}
		}

		private void OrderPerformanceConsole()
		{
            string[] array = new string[6] { "GmBoost", "GmThreadBoostStrategyV1", "AutoNtfsShortNamesV1", "GmCacheWarmV2", "AutoGameMitigationsV1", "GmMmcss" };
			Control control = performanceTabPanels[0];
			List<PerformanceFeatureCard> list = new List<PerformanceFeatureCard>();
			foreach (Control control2 in control.Controls)
			{
				if (control2 is PerformanceFeatureCard)
				{
					list.Add((PerformanceFeatureCard)control2);
				}
			}
			control.SuspendLayout();
			try
			{
				for (int num = array.Length - 1; num >= 0; num--)
				{
					foreach (PerformanceFeatureCard item in list)
					{
						if (item.IconKey == array[num])
						{
							control.Controls.SetChildIndex(item, 0);
						}
					}
				}
			}
			finally
			{
				control.ResumeLayout(false);
			}
		}

		private void EnableCardCollapse(Control panel, params SettingCard[] keepOpen)
		{
			List<Control> list = new List<Control>();
			foreach (Control key in stackBase.Keys)
			{
				if (key.IsDisposed)
				{
					list.Add(key);
				}
			}
			foreach (Control item in list)
			{
				stackBase.Remove(item);
			}
			Dictionary<Control, int> dictionary = new Dictionary<Control, int>();
			foreach (Control control3 in panel.Controls)
			{
				dictionary[control3] = control3.Top;
			}
			stackBase[panel] = dictionary;
			List<SettingCard> list2 = new List<SettingCard>(keepOpen ?? new SettingCard[0]);
			foreach (Control control4 in panel.Controls)
			{
				SettingCard settingCard = control4 as SettingCard;
				if (settingCard == null || settingCard.Desc.Length == 0)
				{
					continue;
				}
				settingCard.ExpandedHeight = settingCard.Height;
				settingCard.CollapsedHeight = Theme.S(52) + (settingCard.HasStatus ? Theme.S(17) : 0);
				if (settingCard.CollapsedHeight < settingCard.ExpandedHeight)
				{
					settingCard.Collapsible = true;
					settingCard.SnapExpanded(list2.Contains(settingCard));
					Control owner = panel;
					settingCard.ExpandedChanged = delegate
					{
						RestackCards(owner);
					};
					settingCard.Height = (settingCard.Expanded ? settingCard.ExpandedHeight : settingCard.CollapsedHeight);
				}
			}
			RestackCards(panel);
		}

		private void RestackCards(Control panel)
		{
			Dictionary<Control, int> value;
			if (!stackBase.TryGetValue(panel, out value))
			{
				return;
			}
			List<int[]> list = new List<int[]>();
			Theme.S(8);
			foreach (Control control3 in panel.Controls)
			{
				SettingCard settingCard = control3 as SettingCard;
				int value2;
				if (settingCard != null && value.TryGetValue(settingCard, out value2) && settingCard.Collapsible)
				{
					int num = settingCard.Height - settingCard.ExpandedHeight;
					if (num != 0)
					{
						list.Add(new int[2] { value2, num });
					}
				}
			}
			ScrollableControl scrollableControl = panel as ScrollableControl;
			int num2 = ((scrollableControl != null) ? scrollableControl.AutoScrollPosition.Y : 0);
			panel.SuspendLayout();
			foreach (Control control4 in panel.Controls)
			{
				int value3;
				if (!value.TryGetValue(control4, out value3))
				{
					continue;
				}
				int num3 = 0;
				foreach (int[] item in list)
				{
					if (item[0] < value3)
					{
						num3 += item[1];
					}
				}
				int num4 = value3 + num3 + num2;
				if (control4.Top != num4)
				{
					control4.Top = num4;
				}
			}
			panel.ResumeLayout();
		}

		private void RefreshPolicyPresentation()
		{
			RefreshStandbyCleanerPresentation();
		}

		private void LayoutCompactSettingsAppearance(DBPanel panel)
		{
			Point autoScrollPosition = new Point(0, -panel.AutoScrollPosition.Y);
			panel.AutoScrollPosition = Point.Empty;
			int num = SettingsContentWidth(panel);
			int num2 = 0;
			SettingsAppearanceEditorPanel settingsAppearanceEditorPanel = panel.Controls["SettingsAppearanceEditor"] as SettingsAppearanceEditorPanel;
			if (settingsAppearanceEditorPanel != null)
			{
				int num3 = Theme.S(16);
				int num4 = Theme.S(24);
				int val = Math.Max(1, num - num3 * 2);
				bool flag = num >= Theme.S(940);
				int num5 = (flag ? Math.Min(Theme.S(490), (num - num4) * 44 / 100) : num);
				int num6 = (flag ? (num5 + num4) : 0);
				int num7 = (flag ? (num - num6) : num);
				int num8 = Theme.S(22);
				int num9 = Theme.S(12);
				int num10 = Theme.S(48);
				settingsAppearanceEditorPanel.BackgroundIconBounds = new Rectangle(num3, num9, num8, num8);
				Control control = settingsAppearanceEditorPanel.Controls["SettingsBackdropTitle"];
				if (control != null)
				{
					control.SetBounds(num3 + Theme.S(34), num9, Math.Max(1, num5 - num3 * 2 - Theme.S(34)), Theme.S(24));
				}
				backdropPreview.SetBounds(num3, num10, Theme.S(120), Theme.S(80));
				int num11 = num3 + Theme.S(136);
				int num12 = Math.Max(1, num5 - num11 - num3);
				int num13 = Math.Max(Theme.S(24), Math.Max(CompactAppearanceTextHeight(Lang.T("backdrop.status.off"), lblBackdropState.Font, num12), CompactAppearanceTextHeight(Lang.F("backdrop.status.on", Lang.T("backdrop.dim.3")), lblBackdropState.Font, num12)));
				lblBackdropState.SetBounds(num11, num10, num12, num13);
				int num14 = Math.Max(CompactAppearanceButtonWidth(btnBackdrop), TextRenderer.MeasureText(Lang.T("btn.backdrop.change"), btnBackdrop.Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + Theme.S(28));
				int num15 = CompactAppearanceButtonWidth(btnBackdropReset);
				int num16 = Theme.S(8);
				bool flag2 = num12 >= num14 + num15 + num16;
				int num17 = (flag2 ? num11 : num3);
				int num18 = (flag2 ? (num10 + num13 + Theme.S(8)) : (Math.Max(backdropPreview.Bottom, lblBackdropState.Bottom) + Theme.S(12)));
				int num19 = num5 - num17 - num3;
				bool flag3 = num19 < num14 + num15 + num16;
				btnBackdropReset.SetBounds(num17, num18, Math.Min(num15, num19), Theme.S(32));
				btnBackdrop.SetBounds(flag3 ? num17 : (btnBackdropReset.Right + num16), flag3 ? (btnBackdropReset.Bottom + num16) : num18, Math.Min(num14, num19), Theme.S(32));
				Control control2 = settingsAppearanceEditorPanel.Controls["SettingsBackdropDimTitle"];
				int num20 = Math.Max(Math.Max(backdropPreview.Bottom, lblBackdropState.Bottom), btnBackdrop.Bottom) + Theme.S(16);
				int num21 = ((control2 == null) ? Theme.S(100) : TextRenderer.MeasureText(control2.Text, control2.Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width);
				int num22 = CompactAppearancePickerWidth(pickBackdropDim);
				bool flag4 = num5 - num3 * 2 >= num21 + Theme.S(16) + num22;
				if (control2 != null)
				{
					control2.SetBounds(num3, num20, Math.Min(val, num21 + Theme.S(4)), Theme.S(32));
				}
				int num23 = (flag4 ? (num3 + num21 + Theme.S(16)) : num3);
				pickBackdropDim.SetBounds(num23, flag4 ? num20 : (num20 + Theme.S(34)), Math.Min(num22, Math.Max(1, num5 - num23 - num3)), Theme.S(32));
				int bottom = pickBackdropDim.Bottom;
				int num24 = (flag ? num9 : (bottom + Theme.S(28)));
				settingsAppearanceEditorPanel.AccentIconBounds = new Rectangle(num6 + num3, num24, num8, num8);
				Control control3 = settingsAppearanceEditorPanel.Controls["SettingsAccentTitle"];
				Control control4 = settingsAppearanceEditorPanel.Controls["SettingsAccentHint"];
				if (control3 != null)
				{
					control3.SetBounds(num6 + num3 + Theme.S(34), num24, Math.Max(1, num7 - num3 * 2 - Theme.S(34)), Theme.S(24));
				}
				if (control4 != null)
				{
					control4.Visible = false;
				}
				int num25 = num24 + Theme.S(36);
				int num26 = Theme.S(30);
				int num27 = Theme.S(24);
				int num28 = Theme.S(82);
				int num29 = num6 + num3 + num28 + Theme.S(8);
				int num30 = Math.Max(1, num7 - num3 * 2 - num28 - Theme.S(8));
				int num31 = Math.Max(1, (num30 + num26 - num27) / num26);
				PerformancePreset[] array = PresetValue.VisibleOrder();
				foreach (PerformancePreset performancePreset in array)
				{
					Label label = modeAccentLabels[(int)performancePreset];
					if (label != null)
					{
						label.SetBounds(num6 + num3, num25, num28, num27);
					}
					int num32 = 0;
					foreach (ColorSwatch item in modeSwatches[(int)performancePreset])
					{
						item.SetBounds(num29 + num32 % num31 * num26, num25 + num32 / num31 * num26, num27, num27);
						num32++;
					}
					num25 += Math.Max(Theme.S(32), (num32 + num31 - 1) / num31 * num26);
				}
				int num33 = Math.Max(bottom, num25) + Theme.S(16);
				Control control5 = settingsAppearanceEditorPanel.Controls["SettingsAppearanceSeparator"];
				if (control5 != null)
				{
					if (flag)
					{
						control5.SetBounds(num5 + num4 / 2, Theme.S(12), Math.Max(1, Theme.S(1)), Math.Max(1, num33 - Theme.S(24)));
					}
					else
					{
						control5.SetBounds(num3, bottom + Theme.S(14), val, Math.Max(1, Theme.S(1)));
					}
				}
				settingsAppearanceEditorPanel.SetBounds(0, 0, num, num33);
				settingsAppearanceEditorPanel.Invalidate();
				num2 = settingsAppearanceEditorPanel.Bottom;
			}
			if (motionAppearance != null && !motionAppearance.IsDisposed && motionAppearance.Parent == panel)
			{
				motionAppearance.SetBounds(0, num2 + Theme.S(12), num, motionAppearance.MeasureHeight(num));
				motionAppearance.PerformLayout();
				num2 = Math.Max(motionAppearance.Bottom, motionAppearance.Top + motionAppearance.Controls["MotionSaveStatus"].Bottom + Theme.S(12));
				if (motionAppearance.Bottom < num2) motionAppearance.Height = num2 - motionAppearance.Top;
			}
			foreach (Control control6 in panel.Controls)
			{
				if (control6 is SettingsWorkspaceTile)
				{
					SettingsWorkspaceTile settingsWorkspaceTile = (SettingsWorkspaceTile)control6;
					settingsWorkspaceTile.CompactRow = true;
					settingsWorkspaceTile.BottomSeparator = false;
					settingsWorkspaceTile.SetBounds(0, num2 + Theme.S(4), num, settingsWorkspaceTile.MeasureHeight(num));
					settingsWorkspaceTile.ArrangeContent();
					num2 = settingsWorkspaceTile.Bottom;
				}
			}
			panel.AutoScrollMinSize = new Size(0, num2 + Theme.S(4));
			panel.AutoScrollPosition = autoScrollPosition;
		}

		private static int CompactAppearanceTextHeight(string text, Font font, int width)
		{
			return TextRenderer.MeasureText(text, font, new Size(Math.Max(1, width), int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
		}

		private static int CompactAppearanceButtonWidth(PillButton button)
		{
			return Math.Max(Theme.S(72), TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + Theme.S(28));
		}

		private static int CompactAppearancePickerWidth(TierPicker picker)
		{
			int num = Theme.S(48);
			string[] labels = picker.Labels;
			foreach (string text in labels)
			{
				num = Math.Max(num, TextRenderer.MeasureText(text, DsType.Font(DsText.Label, true), Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + Theme.S(20));
			}
			return num * picker.Labels.Length + Theme.S(4) * (picker.Labels.Length - 1);
		}

		private bool QueryStartupTaskState()
		{
			return TaskHelper.IsStartupTaskEnabled();
		}

		private int ChangeStartupTask(bool enabled)
		{
			if (!enabled)
			{
				return TaskHelper.DeleteStartupTask();
			}
			return TaskHelper.CreateStartupTask();
		}

		private long MeasureSettingsShaderCache()
		{
			return ShaderCache.MeasureBytes();
		}

		private CacheSweep.Result CleanSettingsShaderCache()
		{
			return ShaderCache.Clean();
		}

		private bool ConfirmSettingsAction(string key, DlgKind kind)
		{
			return PaviseDialog.Confirm(this, "PAVISE AUTO", Lang.T(key), kind);
		}

		private void ShowShaderCleanupMessage(bool failed, string message)
		{
			if (failed)
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", message);
			}
			else
			{
				PaviseDialog.Info(this, "PAVISE AUTO", message);
			}
		}

		private void QueueUiStateWork(Action work)
		{
			if (!ThreadPool.QueueUserWorkItem(delegate
			{
				work();
			}))
			{
				throw new InvalidOperationException("UI state work could not be queued");
			}
		}

		private void PostUiStateResult(Action result)
		{
			BeginInvoke((MethodInvoker)delegate
			{
				result();
			});
		}

		private void WarnStartupTaskFailure(string reason)
		{
			string body = Lang.T("msg.taskfail") + (string.IsNullOrEmpty(reason) ? "" : ("\r\n\r\n" + reason));
			PaviseDialog.Warn(this, "PAVISE AUTO", body);
		}

		private void BuildSettingsPage()
		{
			Interlocked.Increment(ref slowVersion);
			InitializeSettingsWorkspace();
			Control scroll = settingsTabPanels[0];
			swAuto = MakeSwitch(TaskHelper.IsStartupTaskEnabledCached(), OnAutoToggle);
			AddSettingsTile(scroll, "SettingsStartup", Lang.T("set.autostart"), Lang.T("set.autostart.n"), "startup", swAuto);
			swAutoHide = MakeSwitch(Settings.Load("AutoHideOnGame", true), OnAutoHideToggle);
			AddSettingsTile(scroll, "SettingsAutoHide", Lang.T("set.autohide"), Lang.T("set.autohide.n"), "hide", swAutoHide);
			int[] exitGraceChoices = new int[6] { 0, 2, 5, 8, 15, 30 };
			TierPicker pickExitGrace = new TierPicker
			{
				Name = "GameExitGracePicker"
			};
			string[] array = new string[exitGraceChoices.Length];
			for (int i = 0; i < exitGraceChoices.Length; i++)
			{
				array[i] = Lang.F("set.exitgrace.seconds", exitGraceChoices[i]);
			}
			pickExitGrace.Labels = array;
			pickExitGrace.Index = Array.IndexOf(exitGraceChoices, gameMode.ExitGraceSeconds);
			pickExitGrace.Size = new Size(Theme.S(404), Theme.S(40));
			pickExitGrace.IndexChanged = delegate(int index)
			{
				if (index >= 0 && index < exitGraceChoices.Length && !gameMode.TrySetExitGraceSeconds(exitGraceChoices[index]))
				{
					pickExitGrace.Index = Array.IndexOf(exitGraceChoices, gameMode.ExitGraceSeconds);
					PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("set.exitgrace.fail"));
				}
			};
			AddSettingsTile(scroll, "SettingsExitGrace", Lang.T("set.exitgrace"), Lang.T("set.exitgrace.n"), "clock", pickExitGrace).StackHost = true;
			TierPicker tierPicker = new TierPicker();
			tierPicker.Name = "SettingsLanguagePicker";
			TierPicker tierPicker2 = tierPicker;
			tierPicker2.Labels = new string[2]
			{
				Lang.T("lang.zh"),
				Lang.T("lang.en")
			};
			tierPicker2.Index = Lang.Cur;
			tierPicker2.Size = new Size(Theme.S(232), Theme.S(40));
			tierPicker2.IndexChanged = delegate(int index)
			{
				Lang.Set(index);
				BeginInvoke(new MethodInvoker(RebuildUi));
			};
			AddSettingsTile(scroll, "SettingsLanguage", Lang.T("set.lang"), Lang.T("set.lang.n"), "language", tierPicker2);
			int sy = 0;
			BuildStandbyCleanerSettingsCard(scroll, ref sy);
			BuildAiTuningSettingsCard(scroll);
			RefreshStandbyCleanerPresentation();
			scroll = settingsTabPanels[1];
			PillButton pillButton = new PillButton(Lang.T("workflow.attention.action.logs"));
			pillButton.Name = "settingsOpenLogs";
			pillButton.Bg = Theme.Card;
			pillButton.Size = new Size(Theme.S(136), Theme.S(40));
			PillButton pillButton2 = pillButton;
			pillButton2.Click += delegate
			{
				nav.Select(7);
			};
			AddSettingsTile(scroll, "SettingsLogs", Lang.T("nav.log"), Lang.T("v20.log.sub"), "logs", pillButton2);
			btnWipeAll = new PillButton(Lang.T("btn.wipe"), BtnKind.Normal)
			{
				Bg = Theme.Card,
				Size = new Size(Theme.S(136), Theme.S(40))
			};
			btnWipeAll.Click += delegate
			{
				OnWipeAll();
			};
			AddSettingsTile(scroll, "SettingsReset", Lang.T("btn.wipe"), Lang.T("set.reset.desc"), "reset", btnWipeAll);
			btnUninstall = new PillButton(Lang.T("btn.uninstall"), BtnKind.Danger)
			{
				Bg = Theme.Card,
				Size = new Size(Theme.S(128), Theme.S(40))
			};
			btnUninstall.Click += delegate
			{
				OnUninstall();
			};
			AddSettingsTile(scroll, "SettingsUninstall", Lang.T("btn.uninstall"), Lang.T("set.uninstall.desc"), "uninstall", btnUninstall);
			btnShaderGo = new PillButton(Lang.T("btn.clean"))
			{
				Size = new Size(Theme.S(112), Theme.S(40))
			};
			btnShaderGo.Click += delegate(object s, EventArgs e)
			{
				OnShaderClean((PillButton)s);
			};
			cardShader = AddSettingsTile(scroll, "SettingsShader", Lang.T("btn.shader"), Lang.T("set.shader.n"), "shader", btnShaderGo);
			cardShader.Value = " ";
			SyncShaderCleanupPresentation();
			scroll = settingsTabPanels[2];
			sy = 2;
			BuildAppearanceSection(scroll, ref sy);
			LayoutSettingsWorkspace();
		}

		private void BuildAppearanceSection(Control scroll, ref int sy)
		{
			SettingsAppearanceEditorPanel settingsAppearanceEditorPanel = new SettingsAppearanceEditorPanel();
			settingsAppearanceEditorPanel.Name = "SettingsAppearanceEditor";
			settingsAppearanceEditorPanel.BackColor = Theme.Card;
			settingsAppearanceEditorPanel.AccessibleRole = AccessibleRole.Grouping;
			settingsAppearanceEditorPanel.AccessibleName = Lang.T("sec.appearance");
			SettingsAppearanceEditorPanel settingsAppearanceEditorPanel2 = settingsAppearanceEditorPanel;
			settingsAppearanceEditorPanel2.SetBounds(0, Theme.S(sy), Theme.S(ScrollContentW), Theme.S(200));
			scroll.Controls.Add(settingsAppearanceEditorPanel2);
			backdropPreview = new BackdropPreview();
			backdropPreview.Name = "SettingsBackdropPreview";
			backdropPreview.EmptyText = Lang.T("backdrop.preview.empty");
			settingsAppearanceEditorPanel2.Controls.Add(backdropPreview);
			CardLabel(settingsAppearanceEditorPanel2, Lang.T("backdrop.title"), 0, 0, 280, 24, 10.5f, true, Theme.Fg).Name = "SettingsBackdropTitle";
			lblBackdropState = CardLabel(settingsAppearanceEditorPanel2, "", 0, 0, 280, 40, 8.5f, false, Theme.Dim);
			lblBackdropState.Name = "SettingsBackdropState";
			btnBackdrop = new PillButton(Lang.T(Backdrop.Active ? "btn.backdrop.change" : "btn.backdrop.set"));
			btnBackdrop.Name = "SettingsBackdropChoose";
			btnBackdrop.Size = new Size(Theme.S(116), Theme.S(30));
			btnBackdrop.Click += delegate
			{
				OnBackdropPick();
			};
			settingsAppearanceEditorPanel2.Controls.Add(btnBackdrop);
			btnBackdropReset = new PillButton(Lang.T("btn.backdrop.reset"));
			btnBackdropReset.Name = "SettingsBackdropReset";
			btnBackdropReset.Size = new Size(Theme.S(104), Theme.S(30));
			btnBackdropReset.Click += delegate
			{
				OnBackdropReset();
			};
			settingsAppearanceEditorPanel2.Controls.Add(btnBackdropReset);
			CardLabel(settingsAppearanceEditorPanel2, Lang.T("backdrop.dim"), 0, 0, 160, 24, 8.6f, true, Theme.Fg).Name = "SettingsBackdropDimTitle";
			pickBackdropDim = new TierPicker
			{
				Name = "SettingsBackdropDimPicker"
			};
			pickBackdropDim.Labels = new string[4]
			{
				Lang.T("backdrop.dim.off"),
				Lang.T("backdrop.dim.1"),
				Lang.T("backdrop.dim.2"),
				Lang.T("backdrop.dim.3")
			};
			pickBackdropDim.Index = (Backdrop.Active ? (Backdrop.Dim + 1) : 0);
			pickBackdropDim.Size = new Size(Theme.S(276), Theme.S(30));
			pickBackdropDim.IndexChanged = OnBackdropDim;
			settingsAppearanceEditorPanel2.Controls.Add(pickBackdropDim);
			Panel panel = new Panel();
			panel.Name = "SettingsAppearanceSeparator";
			panel.BackColor = Theme.Stroke;
			settingsAppearanceEditorPanel2.Controls.Add(panel);
			CardLabel(settingsAppearanceEditorPanel2, Lang.T("sec.theme"), 0, 0, 280, 24, 10.5f, true, Theme.Fg).Name = "SettingsAccentTitle";
			CardLabel(settingsAppearanceEditorPanel2, Lang.T("theme.hint"), 0, 0, 280, 18, 8f, false, Theme.Dim).Name = "SettingsAccentHint";
			BuildThemeColorRows(settingsAppearanceEditorPanel2, 0);
			SyncBackdropAppearance();
			BuildMotionAppearance(scroll);
			sy += 208;
		}

		private void OnBackdropPick()
		{
			if (Backdrop.Choose(this))
			{
				SyncBackdropAppearance();
				RefreshBackdrop();
			}
		}

		private void OnBackdropReset()
		{
			if (Backdrop.Active)
			{
				Backdrop.Clear();
				SyncBackdropAppearance();
				RefreshBackdrop();
			}
		}

		private void OnBackdropDim(int index)
		{
			if (index <= 0)
			{
				Backdrop.Clear();
			}
			else
			{
				Backdrop.Dim = index - 1;
			}
			SyncBackdropAppearance();
			RefreshBackdrop();
		}

		private void SyncBackdropAppearance()
		{
			bool active = Backdrop.Active;
			if (btnBackdrop != null)
			{
				btnBackdrop.Text = Lang.T(active ? "btn.backdrop.change" : "btn.backdrop.set");
			}
			if (btnBackdropReset != null)
			{
				btnBackdropReset.Enabled = active;
			}
			if (pickBackdropDim != null)
			{
				pickBackdropDim.Enabled = active;
				pickBackdropDim.Index = (active ? (Backdrop.Dim + 1) : 0);
			}
			if (lblBackdropState != null)
			{
				string text = ((active && pickBackdropDim != null && pickBackdropDim.Labels != null) ? pickBackdropDim.Labels[Backdrop.Dim + 1] : "");
				lblBackdropState.Text = (active ? Lang.F("backdrop.status.on", text) : Lang.T("backdrop.status.off"));
				lblBackdropState.ForeColor = (active ? Theme.Accent : Theme.Dim);
			}
			if (backdropPreview != null)
			{
				backdropPreview.Invalidate();
			}
		}

		private void BuildThemeColorRows(Control parent, int startY)
		{
			PerformancePreset[] array = PresetValue.VisibleOrder();
			int num = 0;
			PerformancePreset[] array2 = array;
			foreach (PerformancePreset performancePreset in array2)
			{
				PerformancePreset capMode = performancePreset;
				modeSwatches[(int)performancePreset].Clear();
				int v = startY + num * 34;
				bool flag = performancePreset == gameMode.ActivePreset;
				Label label = new Label();
				label.Text = ModeName(performancePreset);
				label.ForeColor = (flag ? Theme.ModeColor(performancePreset) : Theme.Fg);
				label.BackColor = Color.Transparent;
				label.Font = Theme.UI(8.6f, flag);
				label.TextAlign = ContentAlignment.MiddleLeft;
				label.SetBounds(Theme.S(18), Theme.S(v), Theme.S(82), Theme.S(26));
				parent.Controls.Add(label);
				modeAccentLabels[(int)performancePreset] = label;
				int num2 = 108;
				bool flag2 = Theme.HasModeColorOverride(performancePreset);
				ColorSwatch colorSwatch = new ColorSwatch(Theme.ModeColorBuiltin(performancePreset));
				colorSwatch.IsDefault = true;
				colorSwatch.Selected = !flag2;
				colorSwatch.SetBounds(Theme.S(num2), Theme.S(v), Theme.S(26), Theme.S(26));
				colorSwatch.Picked = delegate
				{
					OnModeColorPick(capMode, Color.Empty);
				};
				parent.Controls.Add(colorSwatch);
				modeSwatches[(int)performancePreset].Add(colorSwatch);
				num2 += 32;
				Color a = Theme.ModeColor(performancePreset);
				Color[] accentPalette = AccentPalette;
				foreach (Color color in accentPalette)
				{
					Color cc = color;
					ColorSwatch colorSwatch2 = new ColorSwatch(cc);
					colorSwatch2.Selected = flag2 && SameRgb(a, cc);
					colorSwatch2.SetBounds(Theme.S(num2), Theme.S(v), Theme.S(26), Theme.S(26));
					colorSwatch2.Picked = delegate
					{
						OnModeColorPick(capMode, cc);
					};
					parent.Controls.Add(colorSwatch2);
					modeSwatches[(int)performancePreset].Add(colorSwatch2);
					num2 += 32;
				}
				num++;
			}
		}

		private void RefreshModeAccentLabels()
		{
			for (int i = 0; i < modeAccentLabels.Length; i++)
			{
				Label label = modeAccentLabels[i];
				if (label != null && !label.IsDisposed)
				{
					PerformancePreset performancePreset = (PerformancePreset)i;
					bool flag = performancePreset == visualMode;
					label.ForeColor = (flag ? Theme.ModeColor(performancePreset) : Theme.Fg);
					label.Font = Theme.UI(8.6f, flag);
				}
			}
		}

		private static string ModeName(PerformancePreset m)
		{
			return ModeButton.ModeName(m);
		}

		private static bool SameRgb(Color a, Color b)
		{
			if (a.R == b.R && a.G == b.G)
			{
				return a.B == b.B;
			}
			return false;
		}

		private void OnModeColorPick(PerformancePreset mode, Color color)
		{
			if (color.IsEmpty)
			{
				Theme.ClearModeColorOverride(mode);
				Settings.SaveStr("ModeAccent" + (int)mode, "");
			}
			else
			{
				Theme.SetModeColorOverride(mode, color);
				Settings.SaveStr("ModeAccent" + (int)mode, Col.ToHex(color));
			}
			bool flag = Theme.HasModeColorOverride(mode);
			Color b = Theme.ModeColor(mode);
			foreach (ColorSwatch item in modeSwatches[(int)mode])
			{
				item.Selected = (item.IsDefault ? (!flag) : (flag && SameRgb(item.Swatch, b)));
				item.Invalidate();
			}
			RefreshModeAccentLabels();
			if (motionAppearance != null && !motionAppearance.IsDisposed) motionAppearance.RefreshPalette();
			if (modeVisualInitialized && mode == visualMode)
			{
				using (Icon runtimeIcon = IconArt.MakeMultiIcon(visualMode, visualEnabled))
				{
					SetRuntimeIcon(runtimeIcon);
				}
				if (nav != null)
				{
					nav.RefreshLogo();
				}
				if (tuningNav != null)
				{
					tuningNav.RefreshLogo();
				}
				if (paviseCore != null)
				{
					paviseCore.RefreshVisual();
				}
			}
			if (UiActive)
			{
				UiClock.Wake();
			}
		}

		private void OnAutoToggle(object s, EventArgs e)
		{
			if (!base.IsDisposed && swAuto != null && !swAuto.IsDisposed)
			{
				Interlocked.Increment(ref slowVersion);
				if (ChangeStartupTask(swAuto.Checked) != 0)
				{
					string lastSchtasksError = TaskHelper.LastSchtasksError;
					swAuto.SetSilently(QueryStartupTaskState());
					WarnStartupTaskFailure(lastSchtasksError);
				}
			}
		}

		private void OnShaderClean(PillButton btn)
		{
			if (runtimeShutdownBusy || base.IsDisposed || btn == null || btn.IsDisposed)
			{
				return;
			}
			if (ShaderCleanupBusy)
			{
				if (cardShader != null)
				{
					cardShader.Value = Lang.T("shader.busy");
				}
			}
			else if (gameMode != null && gameMode.IsActive)
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("shader.ingame"));
			}
			else
			{
				if (!ConfirmSettingsAction("shader.confirm", DlgKind.Warn))
				{
					return;
				}
				if (gameMode != null && gameMode.IsActive)
				{
					PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("shader.ingame"));
				}
				else
				{
					if (runtimeShutdownBusy || base.IsDisposed || btn.IsDisposed || Interlocked.CompareExchange(ref shaderCleaning, 1, 0) != 0)
					{
						return;
					}
					int version = Interlocked.Increment(ref shaderCleanupVersion);
					btn.Enabled = false;
					SyncShaderCleanupPresentation();
					Action<string, CacheSweep.Result, long> complete = delegate(string failure, CacheSweep.Result cr, long left)
					{
						try
						{
							PostUiStateResult(delegate
							{
								if (!base.IsDisposed && !runtimeShutdownBusy && version == Volatile.Read(ref shaderCleanupVersion))
								{
									if (!btn.IsDisposed)
									{
										btn.Enabled = true;
									}
									SyncShaderCleanupPresentation();
									if (cardShader != null && !cardShader.IsDisposed && left >= 0)
									{
										cardShader.Value = CacheSweep.FmtBytes(left);
									}
									else if (cardShader != null && !cardShader.IsDisposed)
									{
										cardShader.Value = ((failure == null) ? "" : PerformancePolicy.Text("清理未完成", "Cleanup incomplete"));
									}
									string message = ((failure != null) ? (PerformancePolicy.Text("着色器缓存清理未完成", "Shader cache cleanup incomplete") + "\r\n\r\n" + failure) : (Lang.F("shader.freed", CacheSweep.FmtBytes(cr.FreedBytes)) + ((cr.FailedFiles > 0) ? ("\r\n" + Lang.F("shader.skip", cr.FailedFiles)) : "") + "\r\n\r\n" + Lang.T("shader.note")));
									ShowShaderCleanupMessage(failure != null, message);
								}
							});
						}
						catch
						{
						}
					};
					try
					{
						QueueUiStateWork(delegate
						{
							CacheSweep.Result result = null;
							long arg = -1L;
							string arg2 = null;
							try
							{
								result = CleanSettingsShaderCache();
								if (result == null)
								{
									throw new InvalidOperationException("Shader cleanup returned no result");
								}
								arg = MeasureSettingsShaderCache();
								Logger.Log(Lang.T("log.panelformsettingspage.1") + CacheSweep.FmtBytes(result.FreedBytes) + ((result.FailedFiles > 0) ? (" " + result.FailedFiles + Lang.T("log.panelformsettingspage.2")) : ""));
							}
							catch (Exception ex2)
							{
								arg2 = ex2.Message;
							}
							finally
							{
								Interlocked.Exchange(ref shaderCleaning, 0);
							}
							complete(arg2, result, arg);
						});
					}
					catch (Exception ex)
					{
						Interlocked.Exchange(ref shaderCleaning, 0);
						btn.Enabled = true;
						SyncShaderCleanupPresentation();
						ShowShaderCleanupMessage(true, PerformancePolicy.Text("着色器缓存清理未完成", "Shader cache cleanup incomplete") + "\r\n\r\n" + ex.Message);
					}
				}
			}
		}

		private void SyncShaderCleanupPresentation()
		{
			bool shaderCleanupBusy = ShaderCleanupBusy;
			if (btnShaderGo != null && !btnShaderGo.IsDisposed)
			{
				btnShaderGo.Enabled = !shaderCleanupBusy;
			}
			if (shaderCleanupBusy && cardShader != null && !cardShader.IsDisposed)
			{
				cardShader.Value = Lang.T("shader.busy");
			}
		}

		internal void BeginRuntimeShutdown(string message)
		{
			CancelConfigurationPresetWork();
			GameExtension.NotifySession(null, 0, 0, false);
			if (!base.IsDisposed)
			{
				if (!runtimeShutdownBusy)
				{
					runtimeShutdownBusy = true;
					base.FormClosing += PreventShutdownClose;
				}

				RealExit = true;
				auditCancelRequested = true;
				Interlocked.Increment(ref slowVersion);
				uiActive = false;
				uiActivityKnown = true;
				if (uiTimer != null)
				{
					uiTimer.Stop();
				}
				if (fitTimer != null)
				{
					fitTimer.Stop();
				}
				CancelAutoHide();
				StopPageReveal();
				if (paviseCore != null)
				{
					paviseCore.SetAnimationEnabled(false);
				}
				if (root != null)
				{
					root.Enabled = false;
				}
				Cursor = Cursors.WaitCursor;
				ShowDataOperationProgress(message);
				SetResetProgress(message);
			}
		}

		private void PreventShutdownClose(object sender, FormClosingEventArgs args)
		{
			if (runtimeShutdownBusy && args.CloseReason != CloseReason.WindowsShutDown)
			{
				args.Cancel = true;
			}
		}

		internal void FinishRuntimeShutdown()
		{
			runtimeShutdownBusy = false;
			base.FormClosing -= PreventShutdownClose;
		}

		internal void SetResetProgress(string message)
		{
			if (!base.IsDisposed)
			{
				if (dataOperationPanel != null && !dataOperationPanel.IsDisposed)
				{
					dataOperationPanel.Status.Text = message;
				}
				if (lblSub != null && !lblSub.IsDisposed)
				{
					lblSub.Text = message;
					lblSub.ForeColor = Theme.Warning;
				}
			}
		}

		private void SetResetActionsEnabled(bool enabled)
		{
			if (btnWipeAll != null && !btnWipeAll.IsDisposed)
			{
				btnWipeAll.Enabled = enabled;
			}
			if (btnUninstall != null && !btnUninstall.IsDisposed)
			{
				btnUninstall.Enabled = enabled;
			}
		}

		private bool CanBeginReset(string inGameMessage, string confirmation)
		{
			if (runtimeShutdownBusy || Interlocked.CompareExchange(ref wipeBusy, 1, 0) != 0)
			{
				return false;
			}
			bool flag = false;
			try
			{
				SetResetActionsEnabled(false);
				if (ShaderCleanupBusy)
				{
					ShowSettingsOperationWarning(PerformancePolicy.Text("着色器缓存正在清理，请完成后重试", "Shader cleanup is in progress; retry when it finishes"));
					return false;
				}
				if (gameMode.IsActive)
				{
					ShowSettingsOperationWarning(Lang.T(inGameMessage));
					return false;
				}
				if (!ConfirmSettingsAction(confirmation, DlgKind.Danger))
				{
					return false;
				}
				if (gameMode.IsActive)
				{
					ShowSettingsOperationWarning(Lang.T(inGameMessage));
					return false;
				}
				if (runtimeShutdownBusy || ShaderCleanupBusy)
				{
					return false;
				}
				Cursor = Cursors.WaitCursor;
				flag = true;
				return true;
			}
			finally
			{
				if (!flag)
				{
					Interlocked.Exchange(ref wipeBusy, 0);
					if (!runtimeShutdownBusy)
					{
						SetResetActionsEnabled(true);
					}
				}
			}
		}

		private void ShowSettingsOperationWarning(string message)
		{
			PaviseDialog.Warn(this, "PAVISE AUTO", message);
		}

		private void OnWipeAll()
		{
			Action resetApp = ResetApp;
			if (resetApp != null && CanBeginReset("wipe.ingame", "wipe.confirm"))
			{
				resetApp();
			}
		}

		private void OnUninstall()
		{
			Action uninstallApp = UninstallApp;
			if (uninstallApp != null && CanBeginReset("uninstall.ingame", "uninstall.confirm"))
			{
				uninstallApp();
			}
		}

		private void RefreshSlowStateAsync()
		{
			if (base.IsDisposed || !UiActive)
			{
				return;
			}

			if (Interlocked.CompareExchange(ref slowBusy, 1, 0) != 0)
			{
				Interlocked.Exchange(ref slowPending, 1);
				return;
			}
			Interlocked.Exchange(ref slowPending, 0);
			int version = Volatile.Read(ref slowVersion);
			Toggle auto = swAuto;
			SettingCard shader = cardShader;
			try
			{
				QueueUiStateWork(delegate
				{
					bool task = false;
					bool taskKnown = false;
					long shaderBytes = -1L;
					try
					{
						task = QueryStartupTaskState();
						taskKnown = true;
					}
					catch
					{
					}
					try
					{
						if (!ShaderCleanupBusy)
						{
							shaderBytes = MeasureSettingsShaderCache();
						}
					}
					catch
					{
					}
					try
					{
						PostUiStateResult(delegate
						{
							try
							{
								if (!base.IsDisposed && UiActive && object.ReferenceEquals(auto, swAuto) && object.ReferenceEquals(shader, cardShader))
								{
									if (version == Volatile.Read(ref slowVersion) && auto != null && !auto.IsDisposed && taskKnown)
									{
										auto.SetSilently(task);
									}
									if (shader != null && !shader.IsDisposed && !ShaderCleanupBusy && shaderBytes >= 0)
									{
										shader.Value = CacheSweep.FmtBytes(shaderBytes);
									}
								}
							}
							finally
							{
								FinishSlowStateRefresh();
							}
						});
					}
					catch
					{
						Interlocked.Exchange(ref slowBusy, 0);
					}
				});
			}
			catch
			{
				Interlocked.Exchange(ref slowBusy, 0);
			}
		}



		private void FinishSlowStateRefresh()
		{
			Interlocked.Exchange(ref slowBusy, 0);
			if (Interlocked.Exchange(ref slowPending, 0) != 0 && !base.IsDisposed && UiActive)
			{
				RefreshSlowStateAsync();
			}
		}

		private void InitializeSettingsWorkspace()
		{
			pageSettings.AutoScroll = false;
			settingsWorkspaceTabs.Clear();
			settingsWorkspaceViewport = new DBPanel
			{
				Name = "SettingsWorkspaceViewport",
				AutoScroll = false,
				BackColor = Theme.Bg
			};
			pageSettings.Controls.Add(settingsWorkspaceViewport);
			settingsWorkspaceSurface = new SettingsWorkspaceSurface
			{
				Name = "SettingsWorkspaceConsole",
				BackColor = Theme.Card
			};
			settingsWorkspaceViewport.Controls.Add(settingsWorkspaceSurface);
			settingsTabs = new TechTabs
			{
				Name = "SettingsTabState",
				Visible = false,
				TabStop = false
			};
			string[] labels = new string[3]
			{
				Lang.T("sec.app"),
				Lang.T("sec.maint"),
				Lang.T("sec.appearance")
			};
			settingsTabs.SetTabs(labels, null);
			pageSettings.Controls.Add(settingsTabs);
			settingsTabPanels = new DBPanel[3];
			for (int i = 0; i < settingsTabPanels.Length; i++)
			{
				settingsTabPanels[i] = new SettingsWorkspaceCategoryPanel
				{
					Visible = (i == 0)
				};
			}
			pageTabPanels[pageSettings] = settingsTabPanels;
			settingsTabs.IndexChanged = delegate(int index)
			{
				for (int l = 0; l < settingsTabPanels.Length; l++)
				{
					DBPanel dBPanel2 = settingsTabPanels[l];
					if (dBPanel2.Visible && l != index)
					{
						tabScrollPositions[dBPanel2] = new Point(0, -dBPanel2.AutoScrollPosition.Y);
					}
					dBPanel2.Visible = l == index;
				}
				Point value;
				if (tabScrollPositions.TryGetValue(settingsTabPanels[index], out value))
				{
					settingsTabPanels[index].AutoScrollPosition = new Point(0, value.Y);
				}
				SyncMotionAppearanceActivity();
			};
			for (int j = 0; j < settingsTabPanels.Length; j++)
			{
				DBPanel dBPanel = settingsTabPanels[j];
				dBPanel.Parent = settingsWorkspaceSurface;
				dBPanel.BackColor = Theme.Card;
				dBPanel.Name = "SettingsWorkspaceCategory" + j;
				dBPanel.AutoScroll = true;
				Native.Dark(dBPanel);
				int index2 = j;
				SettingsWorkspaceTab settingsWorkspaceTab = new SettingsWorkspaceTab(labels[j]);
				settingsWorkspaceTab.Name = "SettingsCategory" + j;
				settingsWorkspaceTab.Selected = j == 0;
				settingsWorkspaceTab.TabIndex = j;
				SettingsWorkspaceTab settingsWorkspaceTab2 = settingsWorkspaceTab;
				settingsWorkspaceTab2.AccessibleName = labels[j];
				settingsWorkspaceTab2.Click += delegate
				{
					settingsTabs.Index = index2;
				};
				settingsWorkspaceTab2.NavigationRequested = delegate(Keys key)
				{
					int num = DsKeys.NavigateHorizontal(key, index2, labels.Length);
					if (num >= 0)
					{
						settingsTabs.Index = num;
						settingsWorkspaceTabs[num].Focus();
					}
				};
				settingsWorkspaceSurface.Controls.Add(settingsWorkspaceTab2);
				settingsWorkspaceTabs.Add(settingsWorkspaceTab2);
			}
			TechTabs techTabs = settingsTabs;
			techTabs.IndexChanged = (Action<int>)Delegate.Combine(techTabs.IndexChanged, (Action<int>)delegate(int index)
			{
				for (int k = 0; k < settingsWorkspaceTabs.Count; k++)
				{
					settingsWorkspaceTabs[k].Selected = k == index;
					settingsWorkspaceTabs[k].AccessibleDescription = ((k == index) ? PerformancePolicy.Text("已选中", "Selected") : "");
					settingsWorkspaceTabs[k].Invalidate();
				}
				if (index == 0)
				{
					RefreshStandbyCleanerPresentation();
				}
				LayoutSettingsWorkspace();
			});
			pageSettings.SizeChanged += delegate
			{
				LayoutSettingsWorkspace();
			};
		}

		private SettingsWorkspaceTile AddSettingsTile(Control parent, string name, string title, string detail, string icon, Control host)
		{
			SettingsWorkspaceTile settingsWorkspaceTile = new SettingsWorkspaceTile();
			settingsWorkspaceTile.Name = name;
			settingsWorkspaceTile.Title = title;
			settingsWorkspaceTile.Desc = detail;
			settingsWorkspaceTile.IconKey = icon;
			settingsWorkspaceTile.CompactRow = true;
			SettingsWorkspaceTile settingsWorkspaceTile2 = settingsWorkspaceTile;
			if (host != null)
			{
				settingsWorkspaceTile2.Host(host);
				host.TabIndex = 0;
				host.AccessibleName = host.AccessibleName ?? title;
			}
			parent.Controls.Add(settingsWorkspaceTile2);
			return settingsWorkspaceTile2;
		}

		private static SettingsWorkspaceTile SettingsTile(Control panel, string name)
		{
			return panel.Controls[name] as SettingsWorkspaceTile;
		}

		private static int SettingsContentWidth(Control panel)
		{
			return Math.Max(1, panel.Width - SystemInformation.VerticalScrollBarWidth);
		}

		private void LayoutSettingsWorkspace()
		{
			if (arrangingSettingsWorkspace || settingsWorkspaceViewport == null || settingsWorkspaceSurface == null || settingsTabPanels == null || settingsWorkspaceSurface.IsDisposed || pageSettings == null || pageSettings.IsDisposed)
			{
				return;
			}
			arrangingSettingsWorkspace = true;
			try
			{
				pageSettings.AutoScroll = false;
				int num = Theme.S(24);
				int num2 = Theme.S(12);
				int num3 = Theme.S(12);
				settingsWorkspaceViewport.SetBounds(0, 0, pageSettings.ClientSize.Width, pageSettings.ClientSize.Height);
				int num4 = Math.Max(Theme.S(300), settingsWorkspaceViewport.ClientSize.Width - num * 2);
				int tabGap = Theme.S(6);
				int tabHeight = Theme.S(44);
				int tabX = 0;
				int tabY = Theme.S(4);
				foreach (SettingsWorkspaceTab settingsWorkspaceTab in settingsWorkspaceTabs)
				{
					int tabWidth = Math.Min(num4, Math.Max(Theme.S(124), TextRenderer.MeasureText(settingsWorkspaceTab.Text, Theme.UI(10f, true), Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + Theme.S(44)));
					if (tabX > 0 && tabX + tabWidth > num4)
					{
						tabX = 0;
						tabY += tabHeight + tabGap;
					}
					settingsWorkspaceTab.SetBounds(tabX, tabY, tabWidth, tabHeight);
					tabX += tabWidth + tabGap;
				}
				int num8 = Math.Max(1, settingsWorkspaceViewport.ClientSize.Height - num2 - num3);
				int num9 = tabY + tabHeight + Theme.S(12);
				int num10 = num9 + Theme.S(12);
				int num11 = Math.Min(Theme.S(540), num8);
				int num12 = Math.Max(1, num11 - num10 - Theme.S(12));
				settingsWorkspaceSurface.FrameTop = num9;
				settingsWorkspaceSurface.BackColor = Theme.Bg;
				settingsWorkspaceSurface.SetBounds(num, num2, num4, num11);
				int num13 = Theme.S(16);
				settingsWorkspaceSurface.HeaderLine = 0;
				for (int j = 0; j < settingsTabPanels.Length; j++)
				{
					DBPanel dBPanel = settingsTabPanels[j];
					dBPanel.SetBounds(num13, num10, num4 - num13 * 2, num12);
					dBPanel.AutoScroll = true;
					dBPanel.BackColor = Theme.Card;
				}
				LayoutSettingsGeneral(settingsTabPanels[0]);
				LayoutSettingsMaintenance(settingsTabPanels[1]);
				LayoutSettingsAppearance(settingsTabPanels[2]);

				DBPanel dBPanel2 = settingsTabPanels[settingsTabs.Index];
				num11 = Math.Min(num8, num10 + dBPanel2.AutoScrollMinSize.Height + Theme.S(12));
				settingsWorkspaceSurface.Height = num11;
				dBPanel2.Height = Math.Max(1, num11 - num10 - Theme.S(12));
				settingsWorkspaceSurface.DivisionX = 0;
				settingsWorkspaceSurface.DivisionTop = 0;
				SettingsWorkspaceTile settingsWorkspaceTile = SettingsTile(settingsTabPanels[0], "SettingsStandbyCleanerCard");
				settingsWorkspaceSurface.DivisionBottom = 0;
				SettingsWorkspaceCategoryPanel settingsWorkspaceCategoryPanel = settingsTabPanels[0] as SettingsWorkspaceCategoryPanel;
				if (settingsWorkspaceCategoryPanel != null)
				{
					int num18 = SettingsContentWidth(settingsWorkspaceCategoryPanel);
					settingsWorkspaceCategoryPanel.DivisionX = ((num18 >= Theme.S(806)) ? (num18 / 2) : 0);
					settingsWorkspaceCategoryPanel.DivisionBottom = ((settingsWorkspaceTile != null) ? (settingsWorkspaceTile.Top - Theme.S(8)) : 0);
					settingsWorkspaceCategoryPanel.Invalidate();
				}
				settingsWorkspaceSurface.Invalidate();
			}
			finally
			{
				arrangingSettingsWorkspace = false;
			}
		}

		private void LayoutSettingsGeneral(DBPanel panel)
		{
			SettingsWorkspaceTile settingsWorkspaceTile = SettingsTile(panel, "SettingsStartup");
			SettingsWorkspaceTile settingsWorkspaceTile2 = SettingsTile(panel, "SettingsAutoHide");
			SettingsWorkspaceTile settingsWorkspaceTile3 = SettingsTile(panel, "SettingsExitGrace");
			SettingsWorkspaceTile settingsWorkspaceTile4 = SettingsTile(panel, "SettingsLanguage");
			SettingsWorkspaceTile settingsWorkspaceTile5 = SettingsTile(panel, "SettingsStandbyCleanerCard");
			SettingsWorkspaceTile aiTuning = SettingsTile(panel, "SettingsAiTuning");
			if (settingsWorkspaceTile == null || settingsWorkspaceTile2 == null || settingsWorkspaceTile3 == null || settingsWorkspaceTile4 == null || settingsWorkspaceTile5 == null || aiTuning == null)
			{
				return;
			}
			Point autoScrollPosition = new Point(0, -panel.AutoScrollPosition.Y);
			panel.AutoScrollPosition = Point.Empty;
			int num = SettingsContentWidth(panel);
			int num2 = Theme.S(24);
			int num3 = 0;
			if (num >= Theme.S(806))
			{
				int num4 = (num - num2) / 2;
				int num5 = Math.Max(settingsWorkspaceTile.MeasureHeight(num4), settingsWorkspaceTile3.MeasureHeight(num4));
				int num6 = Math.Max(settingsWorkspaceTile2.MeasureHeight(num4), settingsWorkspaceTile4.MeasureHeight(num4));
				int num7 = settingsWorkspaceTile5.MeasureHeight(num);
				settingsWorkspaceTile.SetBounds(0, 0, num4, num5);
				settingsWorkspaceTile3.SetBounds(num4 + num2, 0, num - num4 - num2, num5);
				settingsWorkspaceTile2.SetBounds(0, num5, num4, num6);
				settingsWorkspaceTile4.SetBounds(num4 + num2, num5, num - num4 - num2, num6);
				settingsWorkspaceTile5.SetBounds(0, num5 + num6, num, num7);
				num3 = settingsWorkspaceTile5.Bottom;
			}
			else
			{
				SettingsWorkspaceTile[] array = new SettingsWorkspaceTile[5] { settingsWorkspaceTile, settingsWorkspaceTile2, settingsWorkspaceTile3, settingsWorkspaceTile4, settingsWorkspaceTile5 };
				foreach (SettingsWorkspaceTile settingsWorkspaceTile7 in array)
				{
					settingsWorkspaceTile7.SetBounds(0, num3, num, settingsWorkspaceTile7.MeasureHeight(num));
					num3 = settingsWorkspaceTile7.Bottom;
				}
			}
			aiTuning.SetBounds(0, num3, num, aiTuning.MeasureHeight(num));
			num3 = aiTuning.Bottom;
			settingsWorkspaceTile5.BottomSeparator = true;
			aiTuning.BottomSeparator = false;
			SettingsWorkspaceTile[] array2 = new SettingsWorkspaceTile[6] { settingsWorkspaceTile, settingsWorkspaceTile2, settingsWorkspaceTile3, settingsWorkspaceTile4, settingsWorkspaceTile5, aiTuning };
			foreach (SettingsWorkspaceTile settingsWorkspaceTile8 in array2)
			{
				settingsWorkspaceTile8.ArrangeContent();
			}
			panel.AutoScrollMinSize = new Size(0, num3);
			panel.AutoScrollPosition = autoScrollPosition;
		}

		private void LayoutSettingsMaintenance(DBPanel panel)
		{
			SettingsWorkspaceTile[] tiles = {
				SettingsTile(panel, "SettingsLogs"), SettingsTile(panel, "SettingsShader"),
				SettingsTile(panel, "SettingsReset"), SettingsTile(panel, "SettingsUninstall")
			};
			foreach (SettingsWorkspaceTile tile in tiles) if (tile == null) return;
			int width = SettingsContentWidth(panel), gap = Theme.S(24);
			bool twoColumns = width >= Theme.S(806);
			int column = twoColumns ? (width - gap) / 2 : width;
			Point scroll = new Point(0, -panel.AutoScrollPosition.Y);
			panel.AutoScrollPosition = Point.Empty;
			int top = 0;
			for (int i = 0; i < tiles.Length; i += twoColumns ? 2 : 1)
			{
				int height = tiles[i].MeasureHeight(column);
				if (twoColumns) height = Math.Max(height, tiles[i + 1].MeasureHeight(width - column - gap));
				tiles[i].SetBounds(0, top, column, height);
				if (twoColumns) tiles[i + 1].SetBounds(column + gap, top, width - column - gap, height);
				top += height;
			}
			for (int i = 0; i < tiles.Length; i++)
			{
				tiles[i].TabIndex = i;
				tiles[i].BottomSeparator = twoColumns ? i < 2 : i < tiles.Length - 1;
				tiles[i].ArrangeContent();
			}
			SettingsWorkspaceCategoryPanel category = panel as SettingsWorkspaceCategoryPanel;
			if (category != null)
			{
				category.DivisionX = twoColumns ? width / 2 : 0;
				category.DivisionBottom = twoColumns ? tiles[0].Bottom - Theme.S(8) : 0;
				category.Invalidate();
			}
			panel.AutoScrollMinSize = new Size(0, top);
			panel.AutoScrollPosition = scroll;
		}

		private void LayoutSettingsAppearance(DBPanel panel)
		{
			LayoutCompactSettingsAppearance(panel);
		}

		private static string SettingsStandbyCleanerDetail()
		{
			return AutoText("仅对局中达双阈值、间隔约一分钟才清理待机缓存。可能卡顿和重新读盘，不联动计时器或内存压缩。", "Purges standby cache during matches only at both thresholds, at least about a minute apart. May hitch and re-read disk; does not change timers or memory compression.");
		}

		private void BuildStandbyCleanerSettingsCard(Control parent, ref int y)
		{
			StandbyCleanerActionHost standbyCleanerActionHost = new StandbyCleanerActionHost();
			standbyCleanerActionHost.Size = new Size(Theme.S(188), Theme.S(30));
			PillButton pillButton = new PillButton(Lang.T("standbycleaner.options"), BtnKind.Normal);
			pillButton.Name = "StandbyCleanerOptions";
			pillButton.SetBounds(0, 0, Theme.S(128), Theme.S(30));
			pillButton.AccessibleName = Lang.T("standbycleaner.options.title");
			pillButton.Click += delegate
			{
				EditStandbyCleanerOptions();
			};
			swPolicyStandbyCleaner = MakeSwitch(gameMode.StandbyCleanerEnabled, null);
			swPolicyStandbyCleaner.Name = "environmentStandbyCleaner";
			swPolicyStandbyCleaner.Location = new Point(Theme.S(142), Theme.S(3));
			swPolicyStandbyCleaner.AccessibleName = Lang.T("gm.standbycleaner");
			swPolicyStandbyCleaner.CheckedChanged += delegate
			{
				OnStandbyCleanerToggle(swPolicyStandbyCleaner.Checked);
			};
			standbyCleanerActionHost.Controls.Add(pillButton);
			standbyCleanerActionHost.Controls.Add(swPolicyStandbyCleaner);
			SettingsWorkspaceTile settingsWorkspaceTile = AddSettingsTile(parent, "SettingsStandbyCleanerCard", Lang.T("gm.standbycleaner"), SettingsStandbyCleanerDetail(), "memory", standbyCleanerActionHost);
			settingsWorkspaceTile.Summary = PerformancePolicy.Text("对局中 · 双阈值触发", "During matches · both thresholds");
			cardPolicyStandbyCleaner = settingsWorkspaceTile;
			cardPolicyStandbyCleaner.Name = "SettingsStandbyCleanerCard";
			cardPolicyStandbyCleaner.TrackChildHover(pillButton);
			cardPolicyStandbyCleaner.TrackChildHover(swPolicyStandbyCleaner);
			y += 120;
		}

		private int StandbyCleanerCardHeight(int width, Control host, int minimum, bool perGame)
		{
			int val = FullTextCardHeight(perGame ? Lang.T("gm.standbycleaner.cfgsub") : SettingsStandbyCleanerDetail(), width, host, minimum);
			val = Math.Max(val, FullTextCardHeight(Lang.T("gm.standbycleaner.needadmin"), width, host, minimum));
			return Math.Max(val, FullTextCardHeight(Lang.T("standbycleaner.config.invalid"), width, host, minimum));
		}

		private void RefreshStandbyCleanerPresentation()
		{
			if (swPolicyStandbyCleaner != null)
			{
				swPolicyStandbyCleaner.SetSilently(gameMode.StandbyCleanerEnabled);
				swPolicyStandbyCleaner.Enabled = elevated || gameMode.StandbyCleanerEnabled;
			}
			if (cardPolicyStandbyCleaner != null)
			{
				cardPolicyStandbyCleaner.Desc = ((!gameMode.StandbyCleaningOptionsValid) ? Lang.T("standbycleaner.config.invalid") : (elevated ? SettingsStandbyCleanerDetail() : Lang.T("gm.standbycleaner.needadmin")));
				SettingsWorkspaceTile settingsWorkspaceTile = cardPolicyStandbyCleaner as SettingsWorkspaceTile;
				if (settingsWorkspaceTile != null)
				{
					settingsWorkspaceTile.Summary = ((!gameMode.StandbyCleaningOptionsValid) ? Lang.T("standbycleaner.config.invalid") : ((!elevated) ? Lang.T("gm.standbycleaner.needadmin") : PerformancePolicy.Text("对局中 · 双阈值触发", "During matches · both thresholds")));
				}
			}
		}

		private bool ConfirmStandbyCleanerEnable()
		{
			if (!elevated)
			{
				return false;
			}
			if (!gameMode.StandbyCleaningOptionsValid)
			{
				PaviseDialog.Warn(this, Lang.T("gm.standbycleaner"), Lang.T("standbycleaner.config.invalid"));
				return false;
			}
			return PaviseDialog.Confirm(this, Lang.T("gm.standbycleaner"), Lang.T("standbycleaner.warn"), DlgKind.Warn);
		}

		private void OnStandbyCleanerToggle(bool on)
		{
			if (on && !ConfirmStandbyCleanerEnable())
			{
				if (swPolicyStandbyCleaner != null)
				{
					swPolicyStandbyCleaner.SetSilently(gameMode.StandbyCleanerEnabled);
				}
				return;
			}
			bool flag = gameMode.TrySetStandbyCleanerEnabled(on);
			RefreshStandbyCleanerPresentation();
			if (!flag)
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("standbycleaner.switch.savefailed"));
			}
		}

		private bool EditStandbyCleanerOptions()
		{
			StandbyCleanerOptions standbyCleaningOptions = gameMode.StandbyCleaningOptions;
			using (StandbyCleanerOptionsDialog dlg = new StandbyCleanerOptionsDialog(standbyCleaningOptions, ApplyStandbyCleanerOptions, !gameMode.StandbyCleaningOptionsValid))
			{
				return ShowDim(dlg) == DialogResult.OK;
			}
		}

		private bool ApplyStandbyCleanerOptions(StandbyCleanerOptions value)
		{
			if (value == null || !value.IsValid || !gameMode.TrySetStandbyCleanerOptions(value))
			{
				return false;
			}
			RefreshStandbyCleanerPresentation();
			return true;
		}

		[DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
		private static extern IntPtr SendWhitelistCue(IntPtr window, int message, IntPtr parameter, string text);

		private List<WhitelistRuleView> QueryWhitelistState(bool deep)
		{
			if (!deep)
			{
				return gameMode.GetWhitelistRulesFast();
			}
			return gameMode.GetWhitelistRules();
		}

		private void BuildWhitelistPage()
		{
			Interlocked.Increment(ref whiteVersion);
			pageWhitelist.AutoScroll = false;
			Native.Dark(pageWhitelist);
			whiteSearchPanel = new WhitelistSearchSurface
			{
				Name = "WhitelistSearchSurface",
				Fill = Theme.Card,
				Border = Theme.StrokeHi,
				Radius = Theme.S(10),
				BackColor = Theme.Bg
			};
			whiteSearch = Theme.MakeTextBox(0, 0, Theme.S(200));
			whiteSearch.Name = "WhitelistSearch";
			whiteSearch.BorderStyle = BorderStyle.None;
			whiteSearch.BackColor = Theme.Card;
			whiteSearch.Text = whiteSearchText;
			whiteSearch.AccessibleName = AutoText("搜索应用或路径", "Search apps or paths");
			whiteSearch.HandleCreated += delegate
			{
				SendWhitelistCue(whiteSearch.Handle, 5377, new IntPtr(1), AutoText("搜索应用或路径", "Search apps or paths"));
			};
			whiteSearch.TextChanged += delegate
			{
				whiteSearchText = whiteSearch.Text;
				FilterWhitelistRows();
			};
			whiteSearchPanel.Controls.Add(whiteSearch);
			whiteFilters = new TierPicker
			{
				Name = "WhitelistFilter",
				StrongSelection = true,
				Labels = new string[4]
				{
					AutoText("全部", "All"),
					AutoText("手动", "Manual"),
					AutoText("平台", "Platforms"),
					AutoText("内置", "Built-in")
				},
				Index = whiteFilterIndex,
				AccessibleName = AutoText("白名单来源筛选", "Filter whitelist source")
			};
			whiteFilters.IndexChanged = delegate(int index)
			{
				whiteFilterIndex = index;
				FilterWhitelistRows();
			};
			whiteCount = new Label
			{
				Name = "WhitelistCount",
				BackColor = Theme.Bg,
				ForeColor = Theme.Dim,
				Font = Theme.Mono(8f),
				TextAlign = ContentAlignment.MiddleLeft,
				AutoEllipsis = true
			};
			whitePick = new PillButton(AutoText("添加进程", "Add process"), BtnKind.Primary)
			{
				Name = "whitelistPick"
			};
			whitePick.Click += delegate
			{
				PickRunningForWhitelist();
			};
			whiteBrowse = new PillButton(AutoText("添加 EXE", "Add EXE"))
			{
				Name = "whitelistBrowse"
			};
			whiteBrowse.Click += delegate
			{
				BrowseForWhitelist();
			};
			whitePanel = new EmptyStatePanel
			{
				Name = "WhitelistSurface",
				BackColor = Theme.Bg,
				Fill = Theme.Card,
				Border = Theme.Stroke,
				Radius = Theme.S(12),
				Padding = new Padding(Theme.S(8)),
				EmptyGlyph = "white"
			};
			whitePanel.EmptyTitle = AutoText("没有匹配的应用", "No matching apps");
			whitePanel.EmptyDetail = AutoText("调整筛选，或添加需要保护的程序。", "Change the filter or add an app to protect.");
			lstWhite = new TechListBox
			{
				Name = "WhitelistRules",
				Dock = DockStyle.Fill
			};
			Theme.StyleList(lstWhite, false);
			lstWhite.AccessibleName = Lang.T("nav.white");
			lstWhite.ItemHeight = Math.Min(255, Theme.S(56));
			lstWhite.DrawItem += DrawWhitelistItem;
			lstWhite.SelectedIndexChanged += delegate
			{
				SyncWhitelistRemoveAction();
			};
			lstWhite.KeyDown += delegate(object sender, KeyEventArgs e)
			{
				if (e.KeyCode == Keys.Delete && e.Modifiers == Keys.None)
				{
					e.Handled = true;
					e.SuppressKeyPress = true;
					RemoveSelectedWhitelist();
				}
				else if (e.KeyCode == Keys.Return && ShowSelectedAutomaticExemption())
				{
					e.Handled = true;
					e.SuppressKeyPress = true;
				}
			};
			lstWhite.MouseClick += delegate(object sender, MouseEventArgs e)
			{
				if (e.Button == MouseButtons.Left)
				{
					int num = lstWhite.IndexFromPoint(e.Location);
					if (num >= 0)
					{
						lstWhite.SelectedIndex = num;
						ShowSelectedAutomaticExemption();
					}
				}
			};
			lstWhite.MouseDown += delegate(object sender, MouseEventArgs e)
			{
				if (e.Button == MouseButtons.Right)
				{
					lstWhite.SelectedIndex = lstWhite.IndexFromPoint(e.Location);
				}
			};
			whiteMenu = new ContextMenuStrip
			{
				Font = Theme.UI(9f, false),
				BackColor = Theme.Card,
				ForeColor = Theme.Fg,
				ShowImageMargin = false,
				DropShadowEnabled = false,
				Renderer = new TechMenuRenderer()
			};
			whiteMenu.Opening += OnWhiteMenuOpening;
			lstWhite.ContextMenuStrip = whiteMenu;
			ContextMenuStrip ownedMenu = whiteMenu;
			whitePanel.Disposed += delegate
			{
				ownedMenu.Dispose();
			};
			whitePanel.Controls.Add(lstWhite);
			Control[] array = new Control[3] { pageWhitelist, whitePanel, lstWhite };
			foreach (Control control in array)
			{
				control.AllowDrop = true;
				control.DragEnter += OnWhiteDragEnter;
				control.DragDrop += OnWhiteDragDrop;
			}
			Native.Dark(whitePanel);
			btnRemoveWhitelist = new PillButton(Lang.T("btn.remove"))
			{
				Name = "whitelistRemove",
				Enabled = false
			};
			btnRemoveWhitelist.Click += delegate
			{
				RemoveSelectedWhitelist();
			};
			btnRestorePlatformProtection = new PillButton(Lang.T("white.platform.restore"))
			{
				Name = "restorePlatformProtection"
			};
			btnRestorePlatformProtection.AccessibleName = btnRestorePlatformProtection.Text;
			btnRestorePlatformProtection.AccessibleDescription = Lang.T("white.platform.hint");
			btnRestorePlatformProtection.Click += delegate
			{
				RunWhitelistOp(() => (!gameMode.RestorePlatformProtection()) ? gameMode.WhitelistLastError : null);
			};
			whiteReset = new PillButton(AutoText("恢复预设", "Restore preset"))
			{
				Name = "whitelistRestorePreset"
			};
			whiteReset.Click += delegate
			{
				if (PaviseDialog.Confirm(this, "PAVISE AUTO", Lang.T("white.reset.confirm"), DlgKind.Warn))
				{
					RunWhitelistOp(() => (!gameMode.ResetWhitelist()) ? gameMode.WhitelistLastError : null);
				}
			};
			lblWhiteHint = new Label
			{
				Name = "WhitelistSelectionHint",
				BackColor = Theme.Bg,
				ForeColor = Theme.Faint,
				Font = Theme.UI(8f, false),
				AutoEllipsis = true,
				TextAlign = ContentAlignment.MiddleLeft
			};
			pageWhitelist.Controls.AddRange(new Control[10] { whiteSearchPanel, whiteFilters, whiteCount, whitePick, whiteBrowse, whitePanel, btnRemoveWhitelist, btnRestorePlatformProtection, whiteReset, lblWhiteHint });
			pageWhitelist.SizeChanged += delegate
			{
				LayoutWhitelistViewport();
			};
			RefreshWhitelist(false);
			SyncWhitelistPlatformHint();
			LayoutWhitelistViewport();
		}

		private void WhiteBounds(Control control, int x, int y, int width, int height)
		{
			control.SetBounds(Theme.S(x), Theme.S(y) + pageWhitelist.AutoScrollPosition.Y, Theme.S(Math.Max(1, width)), Theme.S(Math.Max(1, height)));
		}

		private void LayoutWhitelistViewport()
		{
			if (whiteArranging || pageWhitelist == null || whitePanel == null || whitePanel.IsDisposed)
			{
				return;
			}
			whiteArranging = true;
			pageWhitelist.SuspendLayout();
			Point point = new Point(Math.Max(0, -pageWhitelist.AutoScrollPosition.X), Math.Max(0, -pageWhitelist.AutoScrollPosition.Y));
			try
			{
				for (int i = 0; i < 2; i++)
				{
					int num = pageWhitelist.ClientSize.Width;
					int num2 = Math.Max(280, Dpi.U(num));
					int num3 = Math.Max(1, Dpi.U(pageWhitelist.ClientSize.Height));
					int num4 = ((num2 < 600) ? 16 : 24);
					int num5 = num2 - num4 * 2;
					int num6 = 10;
					bool flag = num2 >= 920;
					bool flag2 = num2 >= 520;
					int num7 = 16;
					int num8;
					if (flag)
					{
						WhiteBounds(whiteSearchPanel, num4, num7, 280, 38);
						WhiteBounds(whiteFilters, num4 + 292, num7, 300, 38);
						WhiteBounds(whiteCount, num4 + 608, num7, Math.Max(1, num5 - 864), 38);
						WhiteBounds(whitePick, num2 - num4 - 248, num7, 126, 38);
						WhiteBounds(whiteBrowse, num2 - num4 - 112, num7, 112, 38);
						num8 = num7 + 38 + 12;
					}
					else if (flag2)
					{
						WhiteBounds(whiteSearchPanel, num4, num7, num5 - 248, 38);
						WhiteBounds(whitePick, num2 - num4 - 238, num7, 126, 38);
						WhiteBounds(whiteBrowse, num2 - num4 - 102, num7, 102, 38);
						WhiteBounds(whiteFilters, num4, num7 + 48, Math.Min(330, num5 - 100), 34);
						WhiteBounds(whiteCount, num4 + Math.Min(330, num5 - 100) + 12, num7 + 48, Math.Max(1, num5 - 342), 34);
						num8 = num7 + 94;
					}
					else
					{
						WhiteBounds(whiteSearchPanel, num4, num7, num5, 38);
						WhiteBounds(whiteFilters, num4, num7 + 48, num5, 34);
						int num9 = (num5 - num6) / 2;
						WhiteBounds(whitePick, num4, num7 + 92, num9, 34);
						WhiteBounds(whiteBrowse, num4 + num9 + num6, num7 + 92, num5 - num9 - num6, 34);
						num8 = num7 + 138;
					}
					whiteCount.Visible = (flag || flag2) && whiteCount.Width >= Theme.S(70);
					whiteSearch.SetBounds(Theme.S(40), Theme.S(10), Math.Max(1, whiteSearchPanel.Width - Theme.S(52)), Theme.S(24));
					bool flag3 = num5 < 620;
					int num10 = (flag3 ? 82 : 36);
					int num11 = Math.Max(num8 + 152, num3 - num4 - num10);
					WhiteBounds(whitePanel, num4, num8, num5, num11 - num8 - 12);
					if (flag3)
					{
						int num12 = (num5 - num6) / 2;
						WhiteBounds(btnRestorePlatformProtection, num4, num11, num12, 34);
						WhiteBounds(whiteReset, num4 + num12 + num6, num11, num5 - num12 - num6, 34);
						WhiteBounds(btnRemoveWhitelist, num2 - num4 - 88, num11 + 44, 88, 34);
						WhiteBounds(lblWhiteHint, num4 + 2, num11 + 44, Math.Max(1, num5 - 104), 34);
					}
					else
					{
						WhiteBounds(btnRemoveWhitelist, num2 - num4 - 88, num11, 88, 36);
						WhiteBounds(whiteReset, num2 - num4 - 206, num11, 108, 36);
						WhiteBounds(btnRestorePlatformProtection, num2 - num4 - 356, num11, 140, 36);
						WhiteBounds(lblWhiteHint, num4 + 2, num11, Math.Max(1, num5 - 374), 36);
					}
					int v = num11 + num10 + num4;
					bool flag4 = Theme.S(v) > pageWhitelist.ClientSize.Height;
					pageWhitelist.AutoScrollMargin = Size.Empty;
					pageWhitelist.AutoScrollMinSize = (flag4 ? new Size(0, Theme.S(v)) : Size.Empty);
					pageWhitelist.AutoScroll = flag4;
					if (num == pageWhitelist.ClientSize.Width)
					{
						break;
					}
				}
				pageWhitelist.AutoScrollPosition = new Point(point.X, Math.Min(point.Y, DockScrollLimit(pageWhitelist)));
			}
			finally
			{
				pageWhitelist.ResumeLayout(true);
				whiteArranging = false;
			}
		}

		internal void SyncWhitelistPlatformHint()
		{
			if (lblWhiteHint != null && !lblWhiteHint.IsDisposed)
			{
				if (whiteFilters != null)
				{
					whiteFilters.AccessibleDescription = Lang.T("white.platform.hint");
				}
				AppendDetectedPlatformHint();
				SyncWhitelistRemoveAction();
			}
		}

		private void AppendDetectedPlatformHint()
		{
			try
			{
				List<string> list = GamePlatformCatalog.DetectedPlatforms();
				if (list.Count != 0)
				{
					for (int i = 0; i < list.Count; i++)
					{
						list[i] = PlatformDisplayName(list[i]);
					}
					if (whiteFilters != null)
					{
						TierPicker tierPicker = whiteFilters;
						tierPicker.AccessibleDescription = tierPicker.AccessibleDescription + "\r\n" + Lang.F("white.platform.detected", string.Join(" · ", list.ToArray()));
					}
				}
			}
			catch
			{
			}
		}

		private void OnWhiteDragEnter(object sender, DragEventArgs e)
		{
			e.Effect = (e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None);
		}

		private void OnWhiteDragDrop(object sender, DragEventArgs e)
		{
			string[] array = e.Data.GetData(DataFormats.FileDrop) as string[];
			if (array != null && array.Length != 0)
			{
				AddWhitelistFiles(array);
			}
		}

		private void WarnWhitelistOperation(string error)
		{
			if (!string.IsNullOrEmpty(error) && !base.IsDisposed && !base.Disposing)
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", error);
			}
		}

		private void RunWhitelistOp(Func<string> op)
		{
			if (Interlocked.CompareExchange(ref whiteOpBusy, 1, 0) != 0)
			{
				return;
			}
			try
			{
				QueueUiStateWork(delegate
				{
					string error;
					try
					{
						error = op();
					}
					catch (Exception ex2)
					{
						error = ex2.Message;
					}
					try
					{
						PostUiStateResult(delegate
						{
							try
							{
								if (!base.IsDisposed && !base.Disposing)
								{
									WarnWhitelistOperation(error);
									RefreshWhitelist(true);
								}
							}
							finally
							{
								Interlocked.Exchange(ref whiteOpBusy, 0);
							}
						});
					}
					catch
					{
						Interlocked.Exchange(ref whiteOpBusy, 0);
					}
				});
			}
			catch (Exception ex)
			{
				Interlocked.Exchange(ref whiteOpBusy, 0);
				WarnWhitelistOperation(ex.Message);
			}
		}

		private void AddWhitelistFiles(IEnumerable<string> files)
		{
			List<string> list = new List<string>(files);
			RunWhitelistOp(delegate
			{
				int num = 0;
				string text = null;
				foreach (string item in list)
				{
					string text2 = ResolveWhitelistTarget(item);
					if (!string.IsNullOrEmpty(text2))
					{
						if (gameMode.AddWhitelistAuto(text2))
						{
							num++;
						}
						else if (text == null)
						{
							text = gameMode.WhitelistLastError;
						}
					}
				}
				return (num != 0) ? null : text;
			});
		}

		internal static string ResolveWhitelistTarget(string raw)
		{
			if (string.IsNullOrEmpty(raw))
			{
				return null;
			}
			string text = raw.Trim().Trim('"');
			try
			{
				if (text.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
				{
					string text2 = GameExecutableResolver.ResolveShortcut(text);
					if (!string.IsNullOrEmpty(text2))
					{
						text = text2;
					}
				}
			}
			catch
			{
			}
			if (!text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			return text;
		}

		private void BrowseForWhitelist()
		{
			using (OpenFileDialog openFileDialog = new OpenFileDialog())
			{
				openFileDialog.Filter = Lang.T("white.browse.filter");
				openFileDialog.Multiselect = true;
				if (openFileDialog.ShowDialog(this) == DialogResult.OK)
				{
					AddWhitelistFiles(openFileDialog.FileNames);
				}
			}
		}

		private void PickRunningForWhitelist()
		{
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (WhitelistRuleView item in gameMode.GetWhitelistRulesFast())
			{
				if (item.PlatformId == null && item.Rule.Kind != 0)
				{
					hashSet.Add(item.Rule.Value);
				}
			}
			using (RunningPickerDialog runningPickerDialog = new RunningPickerDialog(hashSet))
			{
				if (ShowDim(runningPickerDialog) == DialogResult.OK && runningPickerDialog.Selected.Count != 0)
				{
					AddWhitelistFiles(runningPickerDialog.Selected);
				}
			}
		}

		private bool CanRemoveSelectedWhitelist()
		{
			WhitelistItem whitelistItem = ((lstWhite == null) ? null : (lstWhite.SelectedItem as WhitelistItem));
			if (whitelistItem != null && !whitelistItem.Automatic && whitelistItem.View != null)
			{
				return !whitelistItem.View.Required;
			}
			return false;
		}

		private void SyncWhitelistRemoveAction()
		{
			if (btnRemoveWhitelist != null && !btnRemoveWhitelist.IsDisposed)
			{
				btnRemoveWhitelist.Enabled = CanRemoveSelectedWhitelist();
			}
			if (lblWhiteHint != null && !lblWhiteHint.IsDisposed)
			{
				WhitelistItem whitelistItem = ((lstWhite == null) ? null : (lstWhite.SelectedItem as WhitelistItem));
				lblWhiteHint.Text = ((whitelistItem == null) ? AutoText("拖入 EXE 或快捷方式即可添加", "Drop an EXE or shortcut to add it") : (whitelistItem.Automatic ? AutoText("点击查看匹配范围", "Select to view matching rules") : ((whitelistItem.View != null && whitelistItem.View.Required) ? AutoText("系统依赖，保持保护", "System dependency · protected") : ((whitelistItem.View != null && whitelistItem.View.PlatformId != null) ? AutoText("移除后按普通后台规则处理", "Removal applies normal background rules") : AutoText("保护此应用与已验证的子进程", "Protects this app and verified descendants")))));
			}
		}

		private void RemoveSelectedWhitelist()
		{
			WhitelistItem whitelistItem = lstWhite.SelectedItem as WhitelistItem;
			if (whitelistItem == null || whitelistItem.View == null)
			{
				return;
			}
			if (whitelistItem.View.Required)
			{
				PaviseDialog.Info(this, "PAVISE AUTO", Lang.T("white.required.locked"));
				return;
			}
			string key = whitelistItem.View.Key;
			RunWhitelistOp(() => (!gameMode.RemoveWhitelistRule(key)) ? gameMode.WhitelistLastError : null);
		}

		private bool ShowSelectedAutomaticExemption()
		{
			WhitelistItem whitelistItem = lstWhite.SelectedItem as WhitelistItem;
			if (whitelistItem == null || !whitelistItem.Automatic || string.IsNullOrEmpty(whitelistItem.Details))
			{
				return false;
			}
			TextBox textBox = new TextBox();
			textBox.Multiline = true;
			textBox.ReadOnly = true;
			textBox.WordWrap = true;
			textBox.ScrollBars = ScrollBars.Vertical;
			textBox.BackColor = Theme.Card;
			textBox.ForeColor = Theme.Fg;
			textBox.BorderStyle = BorderStyle.FixedSingle;
			textBox.Font = Theme.UI(9.2f, false);
			textBox.Text = whitelistItem.Details;
			textBox.Size = new Size(Theme.S(668), Theme.S(390));
			textBox.SelectionStart = 0;
			textBox.SelectionLength = 0;
			Native.Dark(textBox);
			PaviseDialog.Show(this, whitelistItem.Title, Lang.T("white.auto.details.body"), textBox, 720);
			return true;
		}

		private void OnWhiteMenuOpening(object sender, CancelEventArgs e)
		{
			whiteMenu.Items.Clear();
			if (!CanRemoveSelectedWhitelist())
			{
				e.Cancel = true;
				return;
			}
			whiteMenu.Items.Add(Lang.T("btn.remove"), null, delegate
			{
				RemoveSelectedWhitelist();
			});
			if (whiteMenu.Items.Count == 0)
			{
				e.Cancel = true;
			}
		}

		internal void RefreshWhitelist(bool deep)
		{
			if (base.IsDisposed || lstWhite == null || lstWhite.IsDisposed || whitePanel == null || whitePanel.IsDisposed)
			{
				return;
			}
			int version = Interlocked.Increment(ref whiteVersion);
			List<WhitelistRuleView> views = QueryWhitelistState(false);
			FillWhitelist(views);
			if (!deep || !UiActive)
			{
				return;
			}
			if (Interlocked.CompareExchange(ref whiteBusy, 1, 0) != 0)
			{
				Interlocked.Exchange(ref whitePending, 1);
				return;
			}
			Interlocked.Exchange(ref whitePending, 0);
			ListBox list = lstWhite;
			EmptyStatePanel panel = whitePanel;
			try
			{
				QueueUiStateWork(delegate
				{
					List<WhitelistRuleView> full = null;
					try
					{
						full = QueryWhitelistState(true);
					}
					catch
					{
					}
					try
					{
						PostUiStateResult(delegate
						{
							try
							{
								if (!base.IsDisposed && UiActive && full != null && version == Volatile.Read(ref whiteVersion) && object.ReferenceEquals(list, lstWhite) && object.ReferenceEquals(panel, whitePanel) && !list.IsDisposed && !panel.IsDisposed)
								{
									FillWhitelist(full);
								}
							}
							finally
							{
								FinishWhitelistRefresh();
							}
						});
					}
					catch
					{
						Interlocked.Exchange(ref whiteBusy, 0);
					}
				});
			}
			catch
			{
				Interlocked.Exchange(ref whiteBusy, 0);
			}
		}

		private void FinishWhitelistRefresh()
		{
			Interlocked.Exchange(ref whiteBusy, 0);
			if (Interlocked.Exchange(ref whitePending, 0) != 0 && !base.IsDisposed && UiActive)
			{
				RefreshWhitelist(true);
			}
		}

		private void FillWhitelist(List<WhitelistRuleView> views)
		{
			if (lstWhite == null || lstWhite.IsDisposed || whitePanel == null)
			{
				return;
			}
			whiteViews = ((views == null) ? new List<WhitelistRuleView>() : new List<WhitelistRuleView>(views));
			List<WhitelistItem> list = new List<WhitelistItem>();
			List<WhitelistItem> list2 = new List<WhitelistItem>();
			foreach (WhitelistRuleView whiteView in whiteViews)
			{
				WhitelistItem item = Decorate(new WhitelistItem(whiteView));
				if (whiteView.PlatformId != null)
				{
					list2.Add(item);
				}
				else if (whiteView.Required)
				{
					continue;
				}
				else
				{
					list.Add(item);
				}
			}
			list.AddRange(list2);
			list.AddRange(AutomaticExemptionItems());
			whiteRows = list;
			FilterWhitelistRows();
		}

		private void FilterWhitelistRows()
		{
			if (lstWhite == null || lstWhite.IsDisposed || whitePanel == null)
			{
				return;
			}
			WhitelistItem item = lstWhite.SelectedItem as WhitelistItem;
			string text = WhitelistSelectionKey(item);
			int topIndex = lstWhite.TopIndex;
			List<WhitelistItem> list = whiteRows;
			string text2 = (whiteSearchText ?? "").Trim();
			lstWhite.BeginUpdate();
			try
			{
				lstWhite.Items.Clear();
				foreach (WhitelistItem item2 in list)
				{
					int num = ((item2.View != null && item2.View.PlatformId != null) ? 2 : ((!item2.Automatic && (item2.View == null || !item2.View.Required)) ? 1 : 3));
					if ((whiteFilterIndex == 0 || whiteFilterIndex == num) && (text2.Length <= 0 || WhitelistContains(item2.Title, text2) || WhitelistContains(item2.Subtitle, text2) || WhitelistContains(item2.Details, text2) || (item2.View != null && WhitelistContains(item2.View.Rule.Value, text2))))
					{
						int selectedIndex = lstWhite.Items.Add(item2);
						if (text != null && string.Equals(WhitelistSelectionKey(item2), text, StringComparison.OrdinalIgnoreCase))
						{
							lstWhite.SelectedIndex = selectedIndex;
						}
					}
				}
				if (lstWhite.Items.Count > 0)
				{
					lstWhite.TopIndex = Math.Min(topIndex, lstWhite.Items.Count - 1);
				}
			}
			finally
			{
				lstWhite.EndUpdate();
			}
			if (btnRestorePlatformProtection != null)
			{
				btnRestorePlatformProtection.Enabled = PlatformRuntimePolicy.Mask != 0;
			}
			whitePanel.ShowEmpty = lstWhite.Items.Count == 0;
			lstWhite.Visible = !whitePanel.ShowEmpty;
			whitePanel.Invalidate();
			if (whiteCount != null)
			{
				whiteCount.Text = ((lstWhite.Items.Count == list.Count) ? AutoText(list.Count + " 项", list.Count + " entries") : AutoText(lstWhite.Items.Count + " / " + list.Count + " 项", lstWhite.Items.Count + " / " + list.Count + " entries"));
			}
			SyncWhitelistRemoveAction();
		}

		private static bool WhitelistContains(string source, string query)
		{
			if (!string.IsNullOrEmpty(source))
			{
				return source.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
			}
			return false;
		}

		private static string WhitelistSelectionKey(WhitelistItem item)
		{
			if (item != null)
			{
				if (item.View == null)
				{
					if (!item.Automatic)
					{
						return null;
					}
					return "auto:" + item.Title;
				}
				return item.View.Key;
			}
			return null;
		}

		private void DrawWhitelistItem(object sender, DrawItemEventArgs e)
		{
			if (e.Index < 0 || e.Index >= lstWhite.Items.Count)
			{
				return;
			}
			WhitelistItem whitelistItem = lstWhite.Items[e.Index] as WhitelistItem;
			if (whitelistItem == null || (whitelistItem.View == null && !whitelistItem.Automatic))
			{
				return;
			}
			Graphics graphics = e.Graphics;
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			Rectangle bounds = e.Bounds;
			bool flag = (e.State & DrawItemState.Selected) != 0;
			bool flag2 = !flag && e.Index == Theme.HoverIndex(lstWhite);
			bool flag3 = !whitelistItem.Automatic && whitelistItem.View.Required;
			bool flag4 = !whitelistItem.Automatic && whitelistItem.View.CurrentMatches > 0;
			using (SolidBrush brush = new SolidBrush(Theme.Card))
			{
				graphics.FillRectangle(brush, bounds);
			}
			Rectangle r = new Rectangle(bounds.Left + Theme.S(2), bounds.Top + Theme.S(2), Math.Max(1, bounds.Width - Theme.S(4)), Math.Max(1, bounds.Height - Theme.S(4)));
			using (GraphicsPath path = Theme.TechPath(r, Theme.S(6)))
			{
				if (flag || flag2)
				{
					using (SolidBrush brush2 = new SolidBrush(flag ? Theme.Sel : Theme.CardHover))
					{
						graphics.FillPath(brush2, path);
					}
					using (Pen pen = new Pen(flag ? Col.Alpha(Theme.Accent, 140) : Theme.StrokeHi))
					{
						graphics.DrawPath(pen, path);
					}
				}
			}
			if (flag)
			{
				using (Pen pen2 = new Pen(Theme.Accent, Math.Max(1f, Theme.S(2))))
				{
					graphics.DrawLine(pen2, r.Left, r.Top + Theme.S(8), r.Left, r.Bottom - Theme.S(8));
				}
			}
			int num = Theme.S(28);
			Rectangle rectangle = new Rectangle(bounds.Left + Theme.S(13), bounds.Top + (bounds.Height - num) / 2, num, num);
			if (whitelistItem.Automatic)
			{
				DrawWhitelistAutomaticIcon(graphics, rectangle, whitelistItem.Glyph);
			}
			else
			{
				Bitmap bitmap = WhitelistIcon(whitelistItem.View.Rule);
				if (bitmap == null)
				{
					DrawWhitelistDefaultIcon(graphics, rectangle, flag3, flag4);
				}
				else
				{
					graphics.DrawImage(bitmap, rectangle);
				}
			}
			int num2 = Dpi.U(bounds.Width);
			bool flag5 = num2 >= 420;
			bool flag6 = num2 >= 640;
			int num3 = (flag5 ? Theme.S(82) : 0);
			int num4 = (flag6 ? Theme.S(100) : 0);
			int num5 = rectangle.Right + Theme.S(12);
			int num6 = Math.Max(Theme.S(30), bounds.Right - num5 - num3 - num4 - Theme.S(22));
			TextRenderer.DrawText(graphics, whitelistItem.Title, Theme.UI(9.3f, true), new Rectangle(num5, bounds.Top + Theme.S(6), num6, Theme.S(22)), flag3 ? Theme.Dim : Theme.Fg, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			TextRenderer.DrawText(graphics, whitelistItem.Subtitle, Theme.UI(7.7f, false), new Rectangle(num5, bounds.Top + Theme.S(29), num6, Theme.S(18)), Theme.Faint, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			if (flag5)
			{
				Rectangle rectangle2 = new Rectangle(bounds.Right - num4 - num3 - Theme.S(11), bounds.Top + (bounds.Height - Theme.S(23)) / 2, num3 - Theme.S(10), Theme.S(23));
				Color color = ((flag3 || whitelistItem.Automatic || whitelistItem.View.PlatformId != null) ? Theme.Dim : Theme.Accent);
				using (GraphicsPath path2 = Theme.TechPath(rectangle2, Theme.S(4)))
				{
					using (SolidBrush brush3 = new SolidBrush(Col.Alpha(color, 15)))
					{
						graphics.FillPath(brush3, path2);
					}
					using (Pen pen3 = new Pen(Col.Alpha(color, 80)))
					{
						graphics.DrawPath(pen3, path2);
					}
				}
				TextRenderer.DrawText(graphics, whitelistItem.Badge, Theme.UI(7.3f, false), rectangle2, color, TextFormatFlags.EndEllipsis | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
			}
			if (flag6 && !flag3 && !whitelistItem.Automatic)
			{
				Rectangle rectangle3 = new Rectangle(bounds.Right - num4 - Theme.S(10), bounds.Top, num4, bounds.Height);
				int num7 = Theme.S(5);
				Color color2 = (flag4 ? Theme.Green : Theme.Faint);
				using (SolidBrush brush4 = new SolidBrush(color2))
				{
					graphics.FillEllipse(brush4, rectangle3.Left, rectangle3.Top + (rectangle3.Height - num7) / 2, num7, num7);
				}
				TextRenderer.DrawText(graphics, whitelistItem.StateText, Theme.UI(7.6f, false), new Rectangle(rectangle3.Left + Theme.S(11), rectangle3.Top, rectangle3.Width - Theme.S(11), rectangle3.Height), color2, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			}
			if (!flag && !flag2)
			{
				using (Pen pen4 = new Pen(Col.Alpha(Theme.Stroke, 100)))
				{
					graphics.DrawLine(pen4, bounds.Left + Theme.S(13), bounds.Bottom - 1, bounds.Right - Theme.S(12), bounds.Bottom - 1);
				}
			}
			if (flag && lstWhite.Focused && (e.State & DrawItemState.Focus) != 0)
			{
				e.DrawFocusRectangle();
			}
		}

		private static void DrawWhitelistAutomaticIcon(Graphics g, Rectangle box, string glyph)
		{
			Rectangle r = box;
			r.Width--;
			r.Height--;
			using (GraphicsPath path = Theme.TechPath(r, Theme.S(5)))
			{
				using (SolidBrush brush = new SolidBrush(Col.Alpha(Theme.Accent, 18)))
				{
					g.FillPath(brush, path);
				}
				using (Pen pen = new Pen(Col.Alpha(Theme.Accent, 108)))
				{
					g.DrawPath(pen, path);
				}
			}
			int num = Theme.S(19);
			Glyphs.Draw(g, string.IsNullOrEmpty(glyph) ? "shield" : glyph, new Rectangle(box.Left + (box.Width - num) / 2, box.Top + (box.Height - num) / 2, num, num), Col.Alpha(Theme.Accent, 210));
		}

		private static void DrawWhitelistDefaultIcon(Graphics g, Rectangle box, bool required, bool live)
		{
			Color c = (required ? Theme.Faint : (live ? Theme.Accent : Theme.Dim));
			Rectangle r = box;
			r.Width--;
			r.Height--;
			using (GraphicsPath path = Theme.TechPath(r, Theme.S(5)))
			{
				using (SolidBrush brush = new SolidBrush(Col.Alpha(c, required ? 12 : 20)))
				{
					g.FillPath(brush, path);
				}
				using (Pen pen = new Pen(Col.Alpha(c, required ? 76 : 118)))
				{
					g.DrawPath(pen, path);
				}
			}
			int num = Theme.S(19);
			Rectangle b = new Rectangle(box.Left + (box.Width - num) / 2, box.Top + (box.Height - num) / 2, num, num);
			Glyphs.Draw(g, "shield", b, Col.Alpha(c, required ? 145 : 205));
			int num2 = Math.Max(2, Theme.S(2));
			int num3 = box.Top + box.Height / 2;
			int num4 = box.Left + box.Width / 2;
			using (Pen pen2 = new Pen(Col.Alpha(c, required ? 112 : 172), Math.Max(1f, Theme.S(1))))
			{
				g.DrawLine(pen2, num4 - Theme.S(4), num3, num4 + Theme.S(4), num3);
			}
			using (SolidBrush brush2 = new SolidBrush(Col.Alpha(c, required ? 160 : 225)))
			{
				g.FillEllipse(brush2, num4 - Theme.S(5) - num2 / 2, num3 - num2 / 2, num2, num2);
				g.FillEllipse(brush2, num4 - num2 / 2, num3 - num2 / 2, num2, num2);
				g.FillEllipse(brush2, num4 + Theme.S(5) - num2 / 2, num3 - num2 / 2, num2, num2);
			}
		}

		private static void DrawWhitelistGroupHeader(Control owner, DrawItemEventArgs e, string text)
		{
			Graphics graphics = e.Graphics;
			Rectangle bounds = e.Bounds;
			using (SolidBrush brush = new SolidBrush(Backdrop.CardFill(owner, Theme.Card)))
			{
				graphics.FillRectangle(brush, bounds);
			}
			Size size = TextRenderer.MeasureText(graphics, text, Theme.UI(7.8f, false));
			int num = bounds.Left + Theme.S(14);
			int num2 = bounds.Top + bounds.Height / 2;
			TextRenderer.DrawText(graphics, text, Theme.UI(7.8f, false), new Rectangle(num, bounds.Top, size.Width + Theme.S(8), bounds.Height), Theme.Faint, TextFormatFlags.VerticalCenter);
			using (Pen pen = new Pen(Col.Alpha(Theme.Stroke, 150)))
			{
				graphics.DrawLine(pen, num + size.Width + Theme.S(12), num2, bounds.Right - Theme.S(14), num2);
			}
		}

		private Bitmap WhitelistIcon(WhitelistRule rule)
		{
			if (rule.Kind == WhitelistRuleKind.LegacyName)
			{
				return null;
			}
			string value = rule.Value;
			Bitmap value2;
			if (whiteIconCache.TryGetValue(value, out value2))
			{
				return value2;
			}
			try
			{
				using (Icon icon = Icon.ExtractAssociatedIcon(value))
				{
					value2 = icon.ToBitmap();
				}
			}
			catch
			{
				value2 = null;
			}
			whiteIconCache[value] = value2;
			return value2;
		}

		private string WhitelistTitle(WhitelistRule rule)
		{
			if (rule.Kind == WhitelistRuleKind.LegacyName)
			{
				return rule.Value;
			}
			string value = rule.Value;
			string value2;
			if (whiteNameCache.TryGetValue(value, out value2))
			{
				return value2;
			}
			string text = null;
			try
			{
				FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(value);
				text = (string.IsNullOrEmpty(versionInfo.FileDescription) ? versionInfo.ProductName : versionInfo.FileDescription);
				if (!string.IsNullOrEmpty(text))
				{
					text = text.Trim();
				}
			}
			catch
			{
			}
			if (string.IsNullOrEmpty(text))
			{
				try
				{
					text = Path.GetFileNameWithoutExtension(value);
				}
				catch
				{
					text = value;
				}
			}
			whiteNameCache[value] = text;
			return text;
		}

		private static string PlatformDisplayName(string id)
		{
			if (string.IsNullOrEmpty(id) || !id.StartsWith("t.", StringComparison.Ordinal))
			{
				return id;
			}
			return Lang.T(id);
		}

		private static List<WhitelistItem> AutomaticExemptionItems()
		{
			List<WhitelistItem> list = new List<WhitelistItem>();
			string systemNames = string.Join(" · ", SystemProcessCatalog.PresetWhitelist);
			list.Add(AutomaticExemption("white.auto.system", Lang.F("white.auto.system.list", systemNames), "shield", DetailSection("white.auto.details.processes", systemNames) + "\r\n\r\n" + Lang.T("white.auto.details.system.note")));
			string[] value = NetAcceleratorCatalog.ProcessNamesForDisplay();
			string[] value2 = NetAcceleratorCatalog.TokensForDisplay();
			list.Add(AutomaticExemption("white.auto.accel", Lang.F("white.auto.accel.list", string.Join(" · ", value)), "chart", DetailSection("white.auto.details.processes", string.Join(" · ", value)) + "\r\n\r\n" + DetailSection("white.auto.details.namekeywords", string.Join(" · ", value2))));
			string[] value3 = HardwareControlCatalog.ProcessNamesForDisplay();
			string[] value4 = HardwareControlCatalog.TokensForDisplay();
			list.Add(AutomaticExemption("white.auto.hardware", Lang.F("white.auto.hardware.list", string.Join(" · ", value3)), "chip", DetailSection("white.auto.details.processes", string.Join(" · ", value3)) + "\r\n\r\n" + DetailSection("white.auto.details.tokens", string.Join(" · ", value4)) + "\r\n\r\n" + Lang.T("white.auto.details.hardware.note")));
			List<string> list2 = new List<string>();
			List<string> list3 = new List<string>();
			AcGroup[] groups = AntiCheatCatalog.Groups;
			foreach (AcGroup acGroup in groups)
			{
				list3.Add(acGroup.Name);
				list2.Add(acGroup.Name + "\r\n" + string.Join(" · ", acGroup.Procs));
			}
			AcProtectionGroup[] protectionOnlyGroups = AntiCheatCatalog.ProtectionOnlyGroups;
			foreach (AcProtectionGroup acProtectionGroup in protectionOnlyGroups)
			{
				list3.Add(acProtectionGroup.Name);
				list2.Add(acProtectionGroup.Name + " · " + Lang.T("ac.protectiononly") + "\r\n" + string.Join(" · ", acProtectionGroup.PatternsForDisplay()));
			}
			list.Add(AutomaticExemption("white.auto.anticheat", Lang.F("white.auto.anticheat.list", string.Join(" · ", list3.ToArray())), "acshield", Lang.T("white.auto.details.groups") + "\r\n\r\n" + string.Join("\r\n\r\n", list2.ToArray()) + "\r\n\r\n" + DetailSection("white.auto.details.tokens", string.Join(" · ", AntiCheatCatalog.NameTokensForDisplay())) + "\r\n\r\n" + DetailSection("white.auto.details.anticheatdirs", string.Join(" · ", AntiCheatCatalog.DirectoryNamesForDisplay()))));
			string[] value5 = PeripheralCatalog.NameKeywordsForDisplay();
			string[] value6 = PeripheralCatalog.DescriptionWordsForDisplay();
			list.Add(AutomaticExemption("white.auto.peripheral", Lang.F("white.auto.peripheral.list", string.Join(" · ", value5)), "settings", DetailSection("white.auto.details.namekeywords", string.Join(" · ", value5)) + "\r\n\r\n" + DetailSection("white.auto.details.descriptionwords", string.Join(" · ", value6)) + "\r\n\r\n" + DetailSection("white.auto.details.vendors", string.Join(" · ", PeripheralCatalog.PresentVendorTokensForDisplay())) + "\r\n\r\n" + Lang.T("white.auto.details.peripheral.note")));
			string streamingProducts = string.Join(" · ", StreamingToolCatalog.ProductsForDisplay());
			list.Add(AutomaticExemption("white.auto.streaming", Lang.F("white.auto.streaming.list", streamingProducts), "chart", DetailSection("white.auto.details.products", streamingProducts) + "\r\n\r\n" + DetailSection("white.auto.details.processes", string.Join(" · ", StreamingToolCatalog.ProcessNamesForDisplay())) + "\r\n\r\n" + Lang.T("white.auto.details.streaming.note")));
			return list;
		}

		private static string DetailSection(string titleKey, string content)
		{
			return Lang.T(titleKey) + "\r\n" + content;
		}

		private static WhitelistItem AutomaticExemption(string titleKey, string subtitle, string glyph, string details)
		{
			WhitelistItem whitelistItem = new WhitelistItem(null);
			whitelistItem.Automatic = true;
			whitelistItem.Title = Lang.T(titleKey);
			whitelistItem.Subtitle = subtitle;
			whitelistItem.Badge = Lang.T("white.auto.badge");
			whitelistItem.StateText = Lang.T("white.auto.state");
			whitelistItem.Glyph = glyph;
			whitelistItem.Details = details;
			return whitelistItem;
		}

		private WhitelistItem Decorate(WhitelistItem item)
		{
			WhitelistRule rule = item.View.Rule;
			if (item.View.PlatformId != null)
			{
				item.Title = PlatformRuntimePolicy.DisplayName(item.View.PlatformId);
				item.Subtitle = PlatformRuntimePolicy.ProcessNames(item.View.PlatformId);
				item.Badge = Lang.T("white.platform.badge");
				item.StateText = ((item.View.CurrentMatches > 0) ? Lang.F("white.state.running", item.View.CurrentMatches) : ((item.View.CurrentMatches < 0) ? Lang.T("white.matches.pending") : Lang.T("white.state.idle")));
				return item;
			}
			item.Title = WhitelistTitle(rule);
			item.Subtitle = ((rule.Kind == WhitelistRuleKind.LegacyName) ? Lang.T("white.badge.name.sub") : rule.Value);
			item.Badge = (item.View.Required ? Lang.T("white.required.badge") : Lang.T("white.badge.family"));
			item.StateText = (item.View.Required ? "" : ((item.View.CurrentMatches < 0) ? Lang.T("white.matches.pending") : ((item.View.CurrentMatches > 0) ? Lang.F("white.state.running", item.View.CurrentMatches) : Lang.T("white.state.idle"))));
			return item;
		}

		private static int DockScrollLimit(ScrollableControl viewport)
		{
			if (viewport != null && viewport.VerticalScroll.Visible)
			{
				return Math.Max(0, viewport.VerticalScroll.Maximum - viewport.VerticalScroll.LargeChange + 1);
			}
			return 0;
		}

		public PanelForm(Tamer t, GameMode gm, Icon icon, bool isElevated)
		{
			tamer = t;
			gameMode = gm;
			elevated = isElevated;
			appIcon = (Icon)icon.Clone();
			lastAdvancedPage = LoadLastAdvancedPage();
			visualMode = gameMode.ActivePreset;
			visualEnabled = gameMode.Enabled;
			Theme.SetMode(visualMode, false);
			BuildUi(appIcon);
		}

		protected override void OnHandleCreated(EventArgs e)
		{
			base.OnHandleCreated(e);
			RegisterPowerSchemeNotifications();
			Native.EnableElevatedFileDrop(base.Handle);
			AttachFormFrame();
			Native.RoundCorners(base.Handle);
			uiActivityKnown = false;
			CenterRoot();
			ScheduleFit();
			SyncUiActivity();
			if (UiActive)
			{
				RefreshSlowStateAsync();
			}
		}

		private void BuildUi(Icon appIcon)
		{
			modeVisualInitialized = false;
			visualPolicySource = null;
			builtLang = Lang.Cur;
			Text = "PAVISE AUTO";
			base.Icon = appIcon;
			base.AutoScaleMode = AutoScaleMode.None;
			base.FormBorderStyle = FormBorderStyle.None;
			base.StartPosition = FormStartPosition.CenterScreen;
			base.MaximizeBox = false;
			if (!fitting)
			{
				if (Dpi.SetDesignSize(WinW, WinH)) Theme.DropFontCache();
				base.ClientSize = new Size(Theme.S(WinW), Theme.S(WinH));
			}
			BackColor = Theme.Bg;
			Font = Theme.UI(9.5f, false);
			AttachFormFrame();
			BuildNavigation();
			pageTabPanels.Clear();
			tabScrollPositions.Clear();
			DBPanel topBar = new DBPanel
			{
				Name = "WorkspaceTopBar"
			};
			topBar.SetBounds(Theme.S(RailW), 0, Theme.S(WinW - RailW), Theme.S(TopH));
			topBar.BackColor = Theme.Bg;
			topBar.MouseDown += DragMove;
			topBar.Paint += delegate(object sender, PaintEventArgs e)
			{
				using (Pen pen = new Pen(Theme.Stroke))
				{
					e.Graphics.DrawLine(pen, 0, topBar.Height - 1, topBar.Width, topBar.Height - 1);
				}
			};
			lblSub = new Label();
			lblSub.Text = (elevated ? (Lang.T("title.admin") + " " + Lang.T("v20.admin.ready")) : Lang.T("title.noelev"));
			lblSub.ForeColor = (elevated ? Theme.Faint : Theme.Danger);
			lblSub.BackColor = Color.Transparent;
			lblSub.Font = Theme.UI(9f, false);
			lblSub.UseCompatibleTextRendering = false;
			lblSub.AutoEllipsis = true;
			lblSub.Name = "PrivilegeStatus";
			lblSub.Padding = new Padding(Theme.S(24), 0, 0, 0);
			lblSub.TextAlign = ContentAlignment.MiddleLeft;
			lblSub.SetBounds(0, 0, 1, 1);
			lblSub.Visible = false;
			lblSub.Paint += delegate(object sender, PaintEventArgs e)
			{
				Glyphs.Draw(e.Graphics, "shield", new Rectangle(0, Theme.S(4), Theme.S(16), Theme.S(16)), lblSub.ForeColor);
			};
			WorkspaceBrand workspaceBrand = new WorkspaceBrand();
			workspaceBrand.Name = "WorkspaceBrand";
			WorkspaceBrand workspaceBrand2 = workspaceBrand;
			workspaceBrand2.SetState(gameMode.ActivePreset, gameMode.Enabled);
			workspaceBrand2.SetBounds(Theme.S(24), Theme.S(4), Theme.S(270), Theme.S(52));
			workspaceBrand2.MouseDown += DragMove;
			modeButton = new ModeButton();
			modeButton.Compact = true;
			modeButton.Name = "StrategyButton";
			modeButton.SetBounds(Theme.S(PageW - 342), Theme.S(12), Theme.S(232), Theme.S(46));
			modeButton.Clicked = ToggleModeFlyout;
			modeButton.SetMode(gameMode.ActivePreset);
			modeButton.SetSource(ModeSourceText(gameMode.SessionPolicySourceName, true));
			themeSwitch = new ThemeSwitch(Theme.LightMode);
			themeSwitch.Compact = false;
			themeSwitch.Name = "WorkspaceThemeSwitch";
			themeSwitch.SetBounds(Theme.S(PageW - 450), Theme.S(12), Theme.S(94), Theme.S(46));
			themeSwitch.Toggled = OnThemeToggled;
			BuildHeaderLinks();
			BuildStrategyControls();
			int num = Theme.S(WinW - RailW);
			CaptionButton captionButton = new CaptionButton(false);
			captionButton.Name = "WindowMinimize";
			captionButton.AccessibleName = AutoText("最小化", "Minimize");
			captionButton.SetBounds(num - Theme.S(92), 0, Theme.S(44), Theme.S(64));
			captionButton.Click += delegate
			{
				base.WindowState = FormWindowState.Minimized;
			};
			CaptionButton captionButton4 = new CaptionButton(true);
			captionButton4.Name = "WindowClose";
			captionButton4.AccessibleName = AutoText("关闭窗口", "Close window");
			captionButton4.SetBounds(num - Theme.S(48), 0, Theme.S(44), Theme.S(64));
			captionButton4.Click += delegate
			{
				BeginOutro();
			};
			topBar.Controls.AddRange(new Control[8] { workspaceBrand2, lblSub, headerWebsite, btnDonate, themeSwitch, modeButton, captionButton, captionButton4 });
			pages = new DBPanel[(int)PageId.Count];
			pages[0] = (pageOverview = MakePage());
			pages[1] = (pageLibrary = MakePage());
			pages[10] = (pageWhitelist = MakePage());
			pages[2] = (pagePolicy = MakePage());
			pages[12] = (pageCoreScheduling = MakePage());
			pages[3] = (pageAntiCheat = MakePage());
			pages[4] = (pageGraphics = MakePage());
			pages[5] = (pageEnvironment = MakePage());
			pages[6] = (pageAudit = MakePage());
			pages[11] = (pageIrq = MakePage());
			pages[7] = (pageLog = MakePage());
			pages[8] = (pageSettings = MakePage());
			pages[9] = (pageAbout = MakePage());
			BuildOverviewPage();
			BuildLibraryPage();
			BuildWhitelistPage();
			BuildAutomationPage();
			BuildCoreSchedulingPage();
			BuildAntiCheatPage();
			BuildAuditPage();
			BuildLogPage();
			BuildSettingsPage();
			BuildAboutPage();
			RegisterPages();
			root = new WorkspacePanel();
			root.SetBounds(0, 0, Theme.S(WinW), Theme.S(WinH));
			root.BackColor = Theme.Bg;
			root.Controls.Add(topBar);
			DBPanel[] array = pages;
			foreach (DBPanel value in array)
			{
				root.Controls.Add(value);
			}
			root.Controls.Add(nav);
			coreBackBar = new AdvancedBackBar(Lang.T("cfg.back"), "RETURN // LIBRARY");
			coreBackBar.Name = "coreBackToLibrary";
			coreBackBar.SetBounds(Theme.S(24), 0, Theme.S(230), Theme.S(TopH));
			coreBackBar.BackRequested = delegate
			{
				nav.Select(1);
			};
			coreBackBar.Visible = false;
			root.Controls.Add(coreBackBar);
			overviewBackBar = new AdvancedBackBar(AutoText("返回设置", "Back to settings"), "RETURN // SETTINGS");
			overviewBackBar.Name = "detailsBackToOverview";
			overviewBackBar.SetBounds(Theme.S(24), 0, Theme.S(230), Theme.S(TopH));
			overviewBackBar.BackRequested = delegate
			{
				nav.Select(8);
			};
			overviewBackBar.Visible = false;
			root.Controls.Add(overviewBackBar);
			root.Controls.Add(modeFlyout);
			modeFlyout.BringToFront();
			base.Controls.Add(root);
			LayoutWorkspaceViewport();
			CenterRoot();
			base.KeyPreview = true;
			base.KeyDown -= OnEscHide;
			base.KeyDown += OnEscHide;
			nav.Select(0);
			if (Backdrop.Active)
			{
				ApplyBackdropLabels(this);
			}
			uiTimer = new System.Windows.Forms.Timer();
			uiTimer.Interval = 1200;
			uiTimer.Tick += OnUiTick;
			uiActivityKnown = false;
			SyncUiActivity();
		}

		private void RefreshBackdrop()
		{
			StopPageReveal();
			ApplyBackdropLabels(this);
			Invalidate(true);
			Update();
		}

		private static void ApplyBackdropLabels(Control host)
		{
			if (!Backdrop.AppliesTo(host))
			{
				return;
			}
			foreach (Control control in host.Controls)
			{
				Label label = control as Label;
				if (label != null && label.BackColor != Color.Transparent)
				{
					label.BackColor = Color.Transparent;
				}
				if (control.HasChildren)
				{
					ApplyBackdropLabels(control);
				}
			}
		}

		private static void AssertNavMatchesPageIds(NavRail rail)
		{
			int num = (int)PageId.Count;
			if (rail.ItemCount != num)
			{
				throw new InvalidOperationException(Lang.T("t.panelform.1") + rail.ItemCount + Lang.T("t.panelform.2") + num + Lang.T("t.panelform.3"));
			}
			bool[] array = new bool[num];
			int num2 = 0;
			for (int i = 0; i < num; i++)
			{
				int num3 = rail.ItemAtSlot(i);
				if (num3 < 0)
				{
					break;
				}
				if (num3 < 0 || num3 >= num)
				{
					throw new InvalidOperationException(Lang.T("t.panelform.4") + num3);
				}
				if (array[num3])
				{
					throw new InvalidOperationException(Lang.T("t.panelform.5") + (PageId)num3);
				}
				array[num3] = true;
				num2++;
			}
			if (num2 == 0)
			{
				throw new InvalidOperationException(Lang.T("t.panelform.1") + "0");
			}
		}

		private DBPanel MakePage()
		{
			WorkspacePanel workspacePanel = new WorkspacePanel();
			workspacePanel.SetBounds(0, Theme.S(TopH), Theme.S(PageW), Theme.S(PageH));
			workspacePanel.BackColor = Theme.Bg;
			workspacePanel.Visible = false;
			return workspacePanel;
		}

		private void RegisterPages()
		{
			pageHooks = new PageHook[(int)PageId.Count];
			pageHooks[0] = new PageHook(pageOverview, delegate(bool active)
			{
				if (paviseCore != null)
				{
					paviseCore.SetAnimationEnabled(active);
				}
				SetOverviewTelemetryActive(active);
				if (active)
				{
					RefreshOverviewAttention();
				}
			}, delegate
			{
				QueueOverviewTelemetry(false);
			});
			pageHooks[1] = new PageHook(pageLibrary, delegate(bool active)
			{
				if (active)
				{
					RefreshGames();
				}
			}, delegate
			{
				RefreshGameRunningStates();
			});
			pageHooks[10] = new PageHook(pageWhitelist, delegate(bool active)
			{
				if (active)
				{
					RefreshWhitelist(true);
				}
			}, null);
			pageHooks[2] = new PageHook(pagePolicy, delegate(bool active)
			{
				if (active)
				{
					RefreshAutomationPage();
					RefreshEnvironmentStateAsync();
				}
			}, RefreshAutomationPage);
			pageHooks[12] = new PageHook(pageCoreScheduling, delegate(bool active)
			{
				if (active)
				{
					SyncCorePage();
				}
				else
				{
					coreDraftProfile = null;
				}
			}, SyncCorePage);
			pageHooks[3] = new PageHook(pageAntiCheat, delegate(bool active)
			{
				if (active)
				{
					SyncAcRoster();
				}
			}, RefreshAcGroupStates);
			pageHooks[4] = new PageHook(pageGraphics, null, null);
			pageHooks[5] = new PageHook(pageEnvironment, null, null);
			pageHooks[6] = new PageHook(pageAudit, null, null);
			pageHooks[11] = new PageHook(pageIrq, null, null);
			pageHooks[7] = new PageHook(pageLog, delegate(bool active)
			{
				if (active)
				{
					RefreshLog();
				}
			}, RefreshLog);
			pageHooks[8] = new PageHook(pageSettings, delegate(bool active)
			{
				SyncMotionAppearanceActivity();
				if (active)
				{
					RefreshSlowStateAsync();
					RefreshStandbyCleanerPresentation();
				}
			}, RefreshStandbyCleanerPresentation);
			pageHooks[9] = new PageHook(pageAbout, delegate(bool active)
			{
				if (active)
				{
					RefreshAboutIcon();
				}
			}, null);
		}

		private void NotifyPageActivation()
		{
			if (pageHooks == null)
			{
				return;
			}
			for (int i = 0; i < pageHooks.Length; i++)
			{
				PageHook pageHook = pageHooks[i];
				if (pageHook != null && pageHook.OnActiveChanged != null)
				{
					pageHook.OnActiveChanged(UiActive && pageHook.Panel == curPage);
				}
			}
		}

		internal void ShowPageForShot(PageId id)
		{
			nav.Select((int)id);
			StopPageReveal();
			nav.SnapToSelection();
			tuningNav.SnapToSelection();
			if (curPage != null)
			{
				curPage.Left = pageBaseLeft;
			}
		}

		private void ShowPage(int index)
		{
			bool flag = index == 5;
			if (flag)
			{
				index = 2;
				nav.SelectSilently(index);
			}
			if (index == 11)
			{
				index = 6;
			}
			if (index < 0 || index >= pages.Length || pages[index] == null)
			{
				return;
			}
			if (index != 8)
			{

			}
			SetModeFlyout(false);
			SetPowerFlyout(false);
			DBPanel dBPanel = pages[index];
			if (curPage == dBPanel && dBPanel.Visible)
			{
				if (flag)
				{
					ShowPerformanceSystemTab();
				}
				return;
			}
			SavePagePosition(curPage);
			bool flag2 = index == 12;
			bool flag3 = index == 7;
			pageBaseLeft = ((!flag2 && !flag3) ? Theme.S(RailW) : 0);
			dBPanel.Left = pageBaseLeft;
			bool flag4 = curPage != null && !curPage.IsDisposed && PreparePageReveal(dBPanel);
			bool flag5 = false;
			root.SuspendLayout();
			dBPanel.SuspendLayout();
			try
			{
				DBPanel[] array = pages;
				foreach (DBPanel dBPanel2 in array)
				{
					dBPanel2.Visible = dBPanel2 == dBPanel;
				}
				curPage = dBPanel;
				LayoutWorkspaceViewport();
				bool flag6 = IsAdvancedPage(index);
				nav.Visible = true;
				nav.BringToFront();
				tuningNav.Visible = false;
				coreBackBar.Visible = flag2;
				if (flag2)
				{
					coreBackBar.BringToFront();
				}
				overviewBackBar.Visible = flag3;
				if (flag3)
				{
					overviewBackBar.BringToFront();
				}
				if (flag6)
				{
					tuningNav.SelectSilently(index);
					nav.SelectSilently(2);
					if (lastAdvancedPage != index)
					{
						lastAdvancedPage = index;
						Settings.SaveStr("DeepTuningLastPage", index.ToString(CultureInfo.InvariantCulture));
					}
				}
				else
				{
					mainReturnPage = (flag2 ? 1 : (flag3 ? 8 : index));
					if (flag2)
					{
						nav.SelectSilently(1);
					}
					else if (flag3)
					{
						nav.SelectSilently(8);
					}
				}
				LayoutWorkspaceViewport();
				if (flag)
				{
					ShowPerformanceSystemTab();
				}
				if (UiActive)
				{
					UiClock.Wake();
				}
				NotifyPageActivation();
				flag5 = true;
			}
			finally
			{
				try
				{
					try
					{
						dBPanel.ResumeLayout(true);
					}
					finally
					{
						root.ResumeLayout(true);
					}
					if (flag5)
					{
						LayoutPerformanceViewport();
						RestorePagePosition(index);
					}
					if (flag4)
					{
						if (flag5)
						{
							if (nav.Visible)
							{
								nav.Update();
							}
							else if (tuningNav.Visible)
							{
								tuningNav.Update();
							}
							StartPageReveal();
						}
						else
						{
							StopPageReveal();
						}
					}
				}
				catch
				{
					StopPageReveal();
					throw;
				}
			}
		}

		private static bool WideWorkspacePage(Control page, Control core, Control whitelist, Control logs)
		{
			if (page != core && page != whitelist)
			{
				return page == logs;
			}
			return true;
		}

		private void LayoutWorkspaceViewport()
		{
			if (root == null || root.IsDisposed)
			{
				return;
			}
			int num = Theme.S(WinW);
			int num2 = Theme.S(WinH);
			root.Size = new Size(num, num2);
			CenterRoot();
			int num3 = Math.Min(num2, Theme.S(TopH));
			if (nav != null)
			{
				Size size = nav.MeasureFloatingDock(Math.Max(1, num - Theme.S(32)));
				nav.SetBounds((num - size.Width) / 2, Math.Max(num3, num2 - size.Height - Theme.S(16)), size.Width, size.Height);
			}
			if (tuningNav != null)
			{
				tuningNav.Visible = false;
			}
			if (lblSub != null)
			{
				lblSub.Visible = false;
			}
			Control control = root.Controls["WorkspaceTopBar"];
			if (control != null)
			{
				control.SetBounds(0, 0, num, num3);
				LayoutWorkspaceHeader(control);
			}
			int num4 = ((nav == null) ? num2 : (nav.Top - Theme.S(8)));
			if (pages != null)
			{
				DBPanel[] array = pages;
				foreach (DBPanel dBPanel in array)
				{
					dBPanel.SetBounds(0, num3, num, Math.Max(1, num4 - num3));
					if (dBPanel != pageOverview && dBPanel != pageLibrary && dBPanel != pageWhitelist && dBPanel != pagePolicy && dBPanel != pageAntiCheat && dBPanel != pageAbout && dBPanel != pageCoreScheduling && dBPanel != pageSettings && dBPanel != pageAudit)
					{
						dBPanel.AutoScroll = true;
					}
					DBPanel[] value;
					if (!pageTabPanels.TryGetValue(dBPanel, out value))
					{
						continue;
					}
					DBPanel[] array2 = value;
					foreach (DBPanel dBPanel2 in array2)
					{
						if (!(dBPanel2 is PerformanceFeatureGrid))
						{
							dBPanel2.SetBounds(dBPanel2.Left, dBPanel2.Top, Math.Max(1, dBPanel.ClientSize.Width - dBPanel2.Left - Theme.S(20)), Math.Max(1, dBPanel.ClientSize.Height - dBPanel2.Top - Theme.S(8)));
						}
					}
				}
			}
			int num5 = Math.Min(Theme.S(230), Math.Max(1, num - Theme.S(160)));
			if (coreBackBar != null)
			{
				coreBackBar.SetBounds(Theme.S(10), 0, num5, Math.Min(num3, Theme.S(60)));
			}
			if (overviewBackBar != null)
			{
				overviewBackBar.SetBounds(Theme.S(10), 0, num5, Math.Min(num3, Theme.S(60)));
			}
			LayoutLibraryViewport();
			LayoutWhitelistViewport();
			LayoutPerformanceViewport();
			LayoutAntiCheatViewport();
			LayoutAboutViewport();
			LayoutAuditViewport();
			LayoutCoreViewport();
			LayoutSettingsWorkspace();
			if (nav != null)
			{
				nav.BringToFront();
			}
			if (curPage != null)
			{
				pageBaseLeft = curPage.Left;
			}
		}

		private void LayoutWorkspaceHeader(Control bar)
		{
			int num = Theme.S(4);
			int num2 = Theme.S(40);
			int num3 = Math.Min(bar.Height, Theme.S(60));
			Control control = bar.Controls["WindowClose"];
			Control control3 = bar.Controls["WindowMinimize"];
			if (control != null)
			{
				control.SetBounds(bar.Width - num - num2, 0, num2, num3);
			}
			if (control3 != null)
			{
				control3.SetBounds(bar.Width - num - num2 * 2, 0, num2, num3);
			}
			bool flag = TopH > 60;
			int num4 = (flag ? (bar.Width - Theme.S(24)) : (bar.Width - num - num2 * 2 - Theme.S(14)));
			int rowY = Theme.S(flag ? 64 : 8);
			int num5 = Theme.S(94);
			int num6 = Theme.S(12);
			int num7 = Math.Min(Theme.S(188), Math.Max(Theme.S(100), num4 - num5 - num6 - Theme.S(24)));
			if (modeButton != null)
			{
				modeButton.SetBounds(Math.Max(0, num4 - num7), rowY, num7, Theme.S(44));
			}
			if (themeSwitch != null)
			{
				themeSwitch.SetBounds(Math.Max(0, num4 - num7 - num6 - num5), rowY, num5, Theme.S(44));
			}
			Control control4 = ((coreBackBar != null && coreBackBar.Visible) ? coreBackBar : ((overviewBackBar != null && overviewBackBar.Visible) ? overviewBackBar : null));
			int floor = (flag ? Theme.S(24) : ((control4 != null) ? (control4.Right + Theme.S(20)) : Theme.S(196)));
			int num8 = LayoutHeaderLinks(themeSwitch.Left, rowY, num6, floor);
			Control control5 = bar.Controls["WorkspaceBrand"];
			if (control5 != null)
			{
				int num9 = (flag ? (bar.Width - num - num2 * 2 - Theme.S(12)) : (num8 - Theme.S(20)));
				control5.SetBounds(Theme.S(24), Theme.S(4), Math.Max(1, Math.Min(Theme.S(270), num9 - Theme.S(24))), Theme.S(52));
				control5.Visible = control4 == null;
			}
		}

		private void BuildStrategyControls()
		{
			powerPlanChoices.Clear();
			modeFlyout = new StrategyFlyout
			{
				Visible = false,
				Trigger = modeButton
			};
			modeFlyout.Dismiss = delegate
			{
				SetModeFlyout(false);
			};
			powerButton = modeFlyout.Power;
			ConfigureLaptopPerformanceToggle();
			ConfigureBackgroundGovernanceControl();
			modeFlyout.Scene.AccessibleName = AutoText("游戏场景", "Game scene");
			modeFlyout.Scene.IndexChanged = delegate(int index)
			{
				ChooseGlobalMode((index == 1) ? PerformancePreset.Competitive : PerformancePreset.Standard);
			};
			powerButton.SelectionChanged += delegate(int index)
			{
				if (index >= 0 && index < powerPlanChoices.Count)
				{
					ChoosePowerPlan(powerPlanChoices[index]);
				}
			};
		}

		private void RefreshStrategyControls()
		{
			if (modeFlyout != null && !modeFlyout.IsDisposed)
			{
				modeFlyout.Scene.Index = ((gameMode.Preset == PerformancePreset.Competitive) ? 1 : 0);
				modeFlyout.Scene.Enabled = !gameMode.ModeSwitchLocked;
				RefreshBackgroundGovernanceControl();
				modeFlyout.SetLaptopPerformance(gameMode.LaptopPerformanceSupportKnown && gameMode.LaptopPerformanceSupported);
				modeFlyout.LaptopPerformance.SetSilently(gameMode.LaptopPerformanceEnabled);
				if (PowerPlan.HandheldBypass && (powerPlanChoices.Count != 1 || powerPlanChoices[0] != "off"))
					LoadStrategyPowerChoices();
				powerButton.Enabled = !PowerPlan.HandheldBypass && (!gameMode.Enabled || !gameMode.IsActive);
				if (!powerButton.Enabled)
				{
					powerButton.CloseDropDown();
				}
				int num = powerPlanChoices.FindIndex((string id) => string.Equals(id, PowerPlan.EffectivePlanId, StringComparison.OrdinalIgnoreCase));
				if (num >= 0)
				{
					powerButton.SetSilently(num);
				}
				if (modeFlyout.Visible) LayoutModeFlyout();
			}
		}

		private void LoadStrategyPowerChoices()
		{
			powerPlanChoices.Clear();
			if (PowerPlan.HandheldBypass)
			{
				powerPlanChoices.Add("off");
				powerButton.SetItems(new[] { AutoText("掌机：不切换", "Handheld: no switching") });
				powerButton.SetSilently(0);
				powerButton.Enabled = false;
				powerButton.AccessibleDescription = AutoText("已识别为掌机，电源计划由系统和厂商软件管理。", "Handheld detected; Windows and the vendor software manage power plans.");
				return;
			}
			powerPlanChoices.Add("managed");
			powerPlanChoices.Add("off");
			List<string> list = new List<string>();
			list.Add(Lang.T("plan.pick.auto"));
			list.Add(Lang.T("plan.pick.off"));
			List<string> list2 = list;
			foreach (PowerPlanEntry item in PowerPlan.ListUserPlans())
			{
				powerPlanChoices.Add(item.Id.ToString());
				list2.Add(item.Name);
			}
			string selected = PowerPlan.EffectivePlanId;
			if (!powerPlanChoices.Exists(id => string.Equals(id, selected, StringComparison.OrdinalIgnoreCase)))
			{
				powerPlanChoices.Add(selected);
				list2.Add(AutoText("不可用", "Unavailable") + " [" + selected + "]");
			}
			powerButton.SetItems(list2.ToArray());
		}

		private void ToggleModeFlyout()
		{
			SetModeFlyout(modeFlyout != null && !modeFlyout.Visible);
		}

		private static bool IsAdvancedPage(int index)
		{
			return false;
		}

		private void TogglePowerFlyout()
		{
			SetPowerFlyout(powerButton != null && !powerButton.DropDownVisible);
		}

		private void SetPowerFlyout(bool visible)
		{
			if (powerButton != null && !powerButton.IsDisposed)
			{
				if (visible && (PowerPlan.HandheldBypass || gameMode.Enabled && gameMode.IsActive))
				{
					visible = false;
				}
				if (visible)
				{
					SetModeFlyout(true);
					powerButton.Focus();
					powerButton.AccessibilityObject.DoDefaultAction();
				}
				else
				{
					powerButton.CloseDropDown();
				}
			}
		}

		private void ChoosePowerPlan(string id)
		{
			if (PowerPlan.HandheldBypass)
			{
				SetPowerFlyout(false);
				RefreshStrategyControls();
				return;
			}
			if (gameMode.IsActive && gameMode.Enabled)
			{
				SetPowerFlyout(false);
				PaviseDialog.Info(this, "PAVISE AUTO", Lang.T("mode.locked.ingame"));
				return;
			}
			if (id == null || !PowerPlan.SelectPlan(id))
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("lib.family.save.failed"));
				return;
			}
			gameMode.RefreshAutomaticPreferences();
			SetPowerFlyout(false);
			RefreshStrategyControls();
			for (int i = 0; i < policySync.Count; i++)
			{
				policySync[i]();
			}
			RefreshPolicyPresentation();
		}

		private void SetModeFlyout(bool visible)
		{
			if (visible)
			{
				StopPageReveal();
			}
			if (modeFlyout != null)
			{
				if (visible)
				{
					LoadStrategyPowerChoices();
					RefreshStrategyControls();
					LayoutModeFlyout();
					QueueLaptopPerformanceProbe();
				}
				else if (powerButton != null)
				{
					powerButton.CloseDropDown();
				}
				modeFlyout.Visible = visible;
				if (visible)
				{
					modeFlyout.BringToFront();
				}
			}
		}

		private void LayoutModeFlyout()
		{
			modeFlyout.FitViewport(new Size(Math.Max(1, root.ClientSize.Width - Theme.S(24)), Math.Max(1, root.ClientSize.Height - Theme.S(24))));
			Point point = root.PointToClient(modeButton.PointToScreen(Point.Empty));
			modeFlyout.Left = Math.Max(Theme.S(12), Math.Min(point.X + modeButton.Width - modeFlyout.Width, root.ClientSize.Width - modeFlyout.Width - Theme.S(12)));
			modeFlyout.Top = Math.Max(Theme.S(12), Math.Min(point.Y + modeButton.Height + Theme.S(8), root.ClientSize.Height - modeFlyout.Height - Theme.S(12)));
		}

		private void ChooseGlobalMode(PerformancePreset mode)
		{
			if (!gameMode.TrySetPreset(mode))
			{
				SetModeFlyout(false);
				PaviseDialog.Info(this, "PAVISE AUTO", Lang.T(gameMode.ModeSwitchLocked ? "mode.locked.ingame" : "lib.family.save.failed"));
			}
			else
			{
				UpdateModePresentation(true);
				SyncAllToggles();
			}
		}

		private void BuildHeaderLinks()
		{
			headerWebsite = new RogLinkButton(Lang.T("site.entry"), "", "globe")
			{
				Name = "WorkspaceWebsite"
			};
			headerWebsite.Click += delegate
			{
				OpenExternal("https://pavise.club/");
			};
			RogLinkButton donate = (btnDonate = new RogLinkButton(Lang.T("donate.entry"), "", "heart")
			{
				Name = "WorkspaceDonate",
				External = false
			});
			donate.Click += delegate
			{
				ShowDonate();
			};
			donate.TextChanged += delegate
			{
				if (donate.Parent != null)
				{
					LayoutWorkspaceHeader(donate.Parent);
				}
			};
			RefreshDonateButton();
		}

		private int LayoutHeaderLinks(int right, int rowY, int gap, int floor)
		{
			if (btnDonate == null || headerWebsite == null)
			{
				return right;
			}
			RogLinkButton[] array = new RogLinkButton[2] { btnDonate, headerWebsite };
			int[] array2 = new int[2]
			{
				HeaderLinkWidth(btnDonate),
				HeaderLinkWidth(headerWebsite)
			};
			int num = Theme.S(44);
			for (int num2 = array.Length; num2 >= 0; num2--)
			{
				for (int i = 0; i < 2; i++)
				{
					int num3 = right;
					for (int j = 0; j < num2; j++)
					{
						num3 -= gap + ((i == 0) ? array2[j] : num);
					}
					if (num2 > 0 && num3 < floor)
					{
						continue;
					}
					num3 = right;
					for (int k = 0; k < array.Length; k++)
					{
						array[k].Visible = k < num2;
						if (k < num2)
						{
							int num4 = ((i == 0) ? array2[k] : num);
							num3 -= gap + num4;
							array[k].SetBounds(num3, rowY, num4, Theme.S(44));
						}
					}
					return num3;
				}
			}
			return right;
		}

		private static int HeaderLinkWidth(RogLinkButton link)
		{
			Size size = TextRenderer.MeasureText(link.Text, DsType.Font(DsText.Body, true), Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
			return Theme.S(81) + size.Width;
		}

		protected override void DestroyHandle()
		{
			System.Windows.Forms.Timer timer = fitTimer;
			fitTimer = null;
			if (timer != null)
			{
				timer.Tick -= OnFitTick;
				timer.Stop();
				timer.Dispose();
			}
			System.Windows.Forms.Timer timer2 = outroTimer;
			outroTimer = null;
			if (timer2 != null)
			{
				timer2.Tick -= OnOutroTick;
				timer2.Stop();
				timer2.Dispose();
			}
			if (uiTimer != null)
			{
				uiTimer.Stop();
				if (base.Disposing)
				{
					uiTimer.Tick -= OnUiTick;
					uiTimer.Dispose();
					uiTimer = null;
				}
			}
			UiClock.Suspended = true;
			if (paviseCore != null)
			{
				paviseCore.SetAnimationEnabled(false);
			}
			DisposePageReveal();
			CancelAutoHide();
			base.DestroyHandle();
		}

		protected override void OnHandleDestroyed(EventArgs e)
		{
			SetOverviewTelemetryActive(false);

			UnregisterPowerSchemeNotifications();
			DisposePageReveal();
			CancelAutoHide();
			outroActive = false;
			outroWatch.Reset();
			if (outroTimer != null)
			{
				outroTimer.Stop();
				outroTimer.Dispose();
				outroTimer = null;
			}
			if (uiTimer != null)
			{
				uiTimer.Stop();
			}
			if (fitTimer != null)
			{
				fitTimer.Stop();
				fitTimer.Dispose();
				fitTimer = null;
			}
			uiActive = false;
			uiActivityKnown = true;
			UiClock.Suspended = true;
			if (paviseCore != null)
			{
				paviseCore.SetAnimationEnabled(false);
			}
			DetachFormFrame();
			foreach (Bitmap value in gameIconCache.Values)
			{
				try
				{
					value.Dispose();
				}
				catch
				{
				}
			}
			gameIconCache.Clear();
			foreach (Bitmap value2 in whiteIconCache.Values)
			{
				if (value2 != null)
				{
					try
					{
						value2.Dispose();
					}
					catch
					{
					}
				}
			}
			whiteIconCache.Clear();
			whiteNameCache.Clear();
			if (appIcon != null)
			{
				appIcon.Dispose();
				appIcon = null;
			}
			base.OnHandleDestroyed(e);
		}

		private void DragMove(object s, MouseEventArgs e)
		{
			if (e.Button == MouseButtons.Left)
			{
				Native.ReleaseCapture();
				Native.SendMessage(base.Handle, 161, (IntPtr)2, IntPtr.Zero);
			}
		}

		public void ShowPanel()
		{
			if (base.InvokeRequired)
			{
				BeginInvoke(new MethodInvoker(ShowPanel));
			}
			else if (!base.IsDisposed)
			{
				CancelOutro();
				ApplyPendingDpiRebuild();
				bool flag = base.Visible && base.WindowState != FormWindowState.Minimized;
				if (!flag)
				{
					BeginIntro();
				}
				Show();
				base.WindowState = FormWindowState.Normal;
				Activate();
				StartIntro();
				SyncUiActivity();
				if (flag)
				{
					SyncAllToggles();
				}
				releaseNotesAutoRequested = true;
				QueueUnseenReleaseNotes();
			}
		}

		public void SyncAllToggles()
		{
			if (base.InvokeRequired)
			{
				BeginInvoke(new MethodInvoker(SyncAllToggles));
			}
			else if (!base.IsDisposed && UiActive)
			{
				if (builtLang != Lang.Cur)
				{
					RebuildUi();
					return;
				}
				SyncToggleValues();
				RefreshSlowStateAsync();
				RefreshEnvironmentStateAsync();
			}
		}

		private void SyncToggleValues()
		{
			if (gameMode != null && tamer != null)
			{
				if (swGame != null)
				{
					swGame.SetSilently(gameMode.Enabled);
				}
				if (swAutoHide != null)
				{
					swAutoHide.SetSilently(Settings.Load("AutoHideOnGame", true));
				}
				if (swLogWrites != null)
				{
					swLogWrites.SetSilently(Settings.Load("LogWritesEnabled", true));
				}
				for (int i = 0; i < policySync.Count; i++)
				{
					policySync[i]();
				}
				RefreshStrategyControls();
				SyncEnvironmentToggles();
				UpdateModePresentation(false);
				for (int j = 0; j < acGroups.Count && j < acToggles.Count; j++)
				{
					acToggles[j].SetSilently(tamer.IsGroupEnabled(acGroups[j].Key));
				}
			}
		}

		private static int LoadLastAdvancedPage()
		{
			int result;
			if (!int.TryParse(Settings.LoadStr("DeepTuningLastPage", ""), out result))
			{
				return 2;
			}
			return NormalizeAdvancedPage(result);
		}

		private static int NormalizeAdvancedPage(int page)
		{
			if (!IsAdvancedPage(page))
			{
				return 2;
			}
			return page;
		}

		private void BuildNavigation()
		{
			navigationScale = Dpi.Scale;
			string[] array = new string[(int)PageId.Count]
			{
				Lang.T("nav.overview"),
				Lang.T("nav.library"),
				Lang.T("v20.advanced.nav.policy"),
				Lang.T("v20.advanced.nav.anticheat"),
				Lang.T("nav.graphics"),
				Lang.T("v20.advanced.nav.system"),
				Lang.T("v20.nav.report"),
				Lang.T("nav.log"),
				Lang.T("nav.set"),
				Lang.T("nav.about"),
				Lang.T("nav.white"),
				Lang.T("v20.advanced.nav.irq"),
				Lang.T("nav.corescheduling")
			};
			string[] icons = new string[(int)PageId.Count]
			{
				"game", "tiles", "settings", "acshield", "gpu", "chip", "chart", "log", "gear", "info",
				"white", "chip", "chip"
			};
			string[] array2 = (string[])array.Clone();
			array2[1] = PerformancePolicy.Text("游戏库", "Library");
			array2[2] = PerformancePolicy.Text("性能", "Performance");
			array2[10] = Lang.T("nav.white");
			nav = new NavRail(array2, icons, new int[8] { 0, 1, 10, 2, 3, 6, 8, 9 }, new int[1] { 7 }, new string[1] { "" }, 0);
			nav.Name = "MainNavigation";
			nav.ShowBranding = false;
			nav.SelectionChanged = ShowPage;
			nav.ItemInvoked = delegate(int page)
			{
				nav.Select(page);
			};
			tuningNav = new NavRail(array, icons, new int[4] { 2, 4, 5, 11 }, null, null, 0);
			tuningNav.Name = "TuningNavigation";
			tuningNav.ShowBranding = false;
			tuningNav.SectionTitle = Lang.T("v20.advanced.title");
			tuningNav.SelectionChanged = delegate(int page)
			{
				if (IsAdvancedPage(page))
				{
					nav.Select(page);
				}
			};
			NavRail[] array3 = new NavRail[2] { nav, tuningNav };
			foreach (NavRail navRail in array3)
			{
				AssertNavMatchesPageIds(navRail);
				navRail.SetBounds(Theme.S((WinW - 920) / 2), Theme.S(WinH - 88), Theme.S(920), Theme.S(72));
				navRail.SetMode(visualMode, visualEnabled);
			}
			tuningNav.Visible = false;
			advBackBar = new AdvancedBackBar();
			advBackBar.SetBounds(0, 0, Theme.S(230), Theme.S(TopH));
			advBackBar.BackRequested = ReturnFromDeepTuning;
			tuningNav.Controls.Add(advBackBar);
		}

		private void ReturnFromDeepTuning()
		{
			if (IsInDeepTuning)
			{
				int num = mainReturnPage;
				if (num < 0 || num >= (int)PageId.Count || IsAdvancedPage(num))
				{
					num = 0;
				}
				nav.Select(num);
				nav.Focus();
			}
		}

		private void SavePagePosition(Control page)
		{
			if (pages != null && page != null && !page.IsDisposed)
			{
				int num = Array.IndexOf(pages, page);
				if (num >= 0)
				{
					pagePositions[num] = PageViewPosition.Capture(page, navigationScale);
				}
			}
		}

		private void RestorePagePosition(int index)
		{
			PageViewPosition value;
			if (!pagePositions.TryGetValue(index, out value))
			{
				return;
			}
			value.Restore(pages[index], Dpi.Scale);
			DBPanel[] value2;
			if (pageTabPanels.TryGetValue(pages[index], out value2))
			{
				DBPanel[] array = value2;
				foreach (DBPanel dBPanel in array)
				{
					tabScrollPositions[dBPanel] = new Point(-dBPanel.AutoScrollPosition.X, -dBPanel.AutoScrollPosition.Y);
				}
			}
		}

		private bool PreparePageReveal(DBPanel page)
		{
			if (!base.IsHandleCreated || !base.Visible || UiClock.Frozen || UiClock.Suspended || introActive || introPending || outroActive || moveSizeLoop)
			{
				return false;
			}
			if (pageReveal == null || pageReveal.IsDisposed)
			{
				pageReveal = new PageReveal(this);
			}
			return pageReveal.Prepare(page as WorkspacePanel);
		}

		private void StartPageReveal()
		{
			if (pageReveal != null && !pageReveal.IsDisposed)
			{
				pageReveal.Reveal();
			}
		}

		private void StopPageReveal()
		{
			if (pageReveal != null && !pageReveal.IsDisposed)
			{
				pageReveal.Cancel();
			}
		}

		private void DisposePageReveal()
		{
			PageReveal pageReveal = this.pageReveal;
			this.pageReveal = null;
			if (pageReveal != null)
			{
				pageReveal.Dispose();
			}
		}

		[DllImport("user32.dll", SetLastError = true)]
		private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSettingGuid, uint flags);

		[DllImport("user32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

		private void RegisterPowerSchemeNotifications()
		{
			UnregisterPowerSchemeNotifications();
			if (!base.IsHandleCreated)
			{
				return;
			}
			Guid powerSettingGuid = ActivePowerSchemeGuid;
			try
			{
				powerSchemeNotification = RegisterPowerSettingNotification(base.Handle, ref powerSettingGuid, 0u);
			}
			catch
			{
				powerSchemeNotification = IntPtr.Zero;
			}
		}

		private void UnregisterPowerSchemeNotifications()
		{
			IntPtr intPtr = powerSchemeNotification;
			powerSchemeNotification = IntPtr.Zero;
			if (intPtr == IntPtr.Zero)
			{
				return;
			}
			try
			{
				UnregisterPowerSettingNotification(intPtr);
			}
			catch
			{
			}
		}

		private void HandlePowerSchemeNotification(Message message)
		{
			if (message.Msg != 536 || message.WParam.ToInt64() != 32787 || message.LParam == IntPtr.Zero)
			{
				return;
			}
			try
			{
				if (((PowerBroadcastSetting)Marshal.PtrToStructure(message.LParam, typeof(PowerBroadcastSetting))).PowerSetting == ActivePowerSchemeGuid)
				{
					gameMode.NotifyPowerSchemeChanged();
				}
			}
			catch
			{
			}
		}

		private void UpdateModePresentation(bool animate)
		{
			RefreshStrategyControls();
			PerformancePreset activePreset = gameMode.ActivePreset;
			bool enabled = gameMode.Enabled;
			bool isActive = gameMode.IsActive;
			string sessionPolicySourceName = gameMode.SessionPolicySourceName;
			bool flag = !modeVisualInitialized;
			bool flag2 = flag || activePreset != visualMode || enabled != visualEnabled;
			bool flag3 = flag || isActive != visualActive || !string.Equals(sessionPolicySourceName, visualPolicySource, StringComparison.Ordinal);
			if (flag2)
			{
				if (modeButton != null)
				{
					modeButton.SetMode(activePreset);
				}
				if (lblHeroMode != null)
				{
					lblHeroMode.Text = ModeButton.ModeName(activePreset);
				}
			}
			if (flag3)
			{
				if (modeButton != null)
				{
					modeButton.SetSource(ModeSourceText(sessionPolicySourceName, true));
				}
				if (lblHeroSource != null)
				{
					lblHeroSource.Text = ModeSourceText(sessionPolicySourceName, false);
				}
			}
			if (paviseCore != null)
			{
				paviseCore.SetState(activePreset, enabled, isActive);
			}
			SyncMotionAppearanceActivity();
			if (activePreset != visualMode)
			{
				visualMode = activePreset;
				Theme.SetMode(activePreset, animate);
			}
			visualEnabled = enabled;
			visualActive = isActive;
			visualPolicySource = sessionPolicySourceName;
			modeVisualInitialized = true;
			if (root != null)
			{
				Control control = root.Controls["WorkspaceTopBar"];
				WorkspaceBrand workspaceBrand = ((control == null) ? null : (control.Controls["WorkspaceBrand"] as WorkspaceBrand));
				if (workspaceBrand != null)
				{
					workspaceBrand.SetState(activePreset, enabled);
				}
			}
			if (flag2)
			{
				RefreshModeAccentLabels();
				if (nav != null)
				{
					nav.SetMode(activePreset, enabled);
				}
				if (tuningNav != null)
				{
					tuningNav.SetMode(activePreset, enabled);
				}
				using (Icon runtimeIcon = IconArt.MakeMultiIcon(activePreset, enabled))
				{
					SetRuntimeIcon(runtimeIcon);
				}
				if (curPage == pagePolicy)
				{
					RefreshAutomationPage();
				}
			}
		}

		internal static string ModeSourceText(string sourceName, bool compact)
		{
			if (!string.IsNullOrEmpty(sourceName))
			{
				return Lang.F(compact ? "mode.source.game.short" : "mode.source.game", sourceName);
			}
			return Lang.T("mode.source.global");
		}

		private void OnThemeToggled(bool light)
		{
			if (Theme.LightMode != light)
			{
				StopPageReveal();
				UiPalette previous = new UiPalette();
				Settings.Save("UiLight", light);
				Theme.SetLight(light);
				ApplyUiPalette(previous);
				Logger.Log(Lang.T("log.panelform.8") + (light ? Lang.T("log.panelform.9") : Lang.T("log.panelform.10")));
			}
		}

		public void SetRuntimeIcon(Icon value)
		{
			if (value == null)
			{
				return;
			}
			if (base.InvokeRequired)
			{
				Icon copy = (Icon)value.Clone();
				BeginInvoke((MethodInvoker)delegate
				{
					using (copy)
					{
						SetRuntimeIcon(copy);
					}
				});
				return;
			}
			Icon icon = (Icon)value.Clone();
			Icon icon2 = appIcon;
			appIcon = icon;
			base.Icon = icon;
			if (icon2 != null)
			{
				icon2.Dispose();
			}
		}

		private void RebuildUi()
		{
			CancelConfigurationPresetWork();
			SetOverviewTelemetryActive(false);
			if (libraryConfigurationDialog != null && !libraryConfigurationDialog.IsDisposed)
			{
				libraryConfigurationDialog.Close();
			}

			DisposePageReveal();
			if (tuningNav != null && tuningNav.Parent == null)
			{
				tuningNav.Dispose();
			}
			CancelOutro();
			if (uiTimer != null)
			{
				uiTimer.Stop();
				uiTimer.Dispose();
				uiTimer = null;
			}
			uiActive = false;
			uiActivityKnown = false;
			UiClock.Suspended = true;
			if (paviseCore != null)
			{
				paviseCore.SetAnimationEnabled(false);
			}
			DetachFormFrame();
			List<Control> list = new List<Control>();
			int num = ((pages != null && curPage != null) ? Array.IndexOf(pages, curPage) : ((nav != null) ? nav.Selected : 0));
			if (num < 0)
			{
				num = 0;
			}
			int num2 = mainReturnPage;
			AcViewState acViewState = CaptureAcViewState();
			CoreViewState coreViewState = CaptureCoreViewState();
			SavePagePosition(curPage);
			foreach (Control control in base.Controls)
			{
				list.Add(control);
			}
			base.Controls.Clear();
			foreach (Control item in list)
			{
				item.Dispose();
			}
			acGroups.Clear();
			acCards.Clear();
			acToggles.Clear();
			accentLabels.Clear();
			policySync.Clear();
			stackBase.Clear();
			for (int i = 0; i < modeAccentLabels.Length; i++)
			{
				modeAccentLabels[i] = null;
				modeSwatches[i].Clear();
			}
			BuildUi(appIcon);
			mainReturnPage = num2;
			nav.Select(num);
			RestoreAcViewState(acViewState);
			RestoreCoreViewState(coreViewState);
			if (UiActive)
			{
				RefreshSlowStateAsync();
			}
		}

		protected override void WndProc(ref Message m)
		{
			HandlePowerSchemeNotification(m);
			if (m.Msg == 10 && m.WParam == IntPtr.Zero) StopPageReveal();
			if (m.Msg == 563)
			{
				AddDroppedGames(Native.ReadDroppedFiles(m.WParam));
				m.Result = IntPtr.Zero;
				return;
			}
			if (m.Msg == 561) BeginMoveSizeLoop();
			else if (m.Msg == 562) EndMoveSizeLoop();
			base.WndProc(ref m);
		}

		private void ApplyPendingDpiRebuild()
		{
			if (base.IsDisposed) return;
			int dpi = Dpi.WindowDpi(base.Handle);
			if (dpi <= 0 || !Dpi.WouldChange(dpi)) return;
			Dpi.Update(dpi);
			Theme.DropFontCache();
			Logger.Log(Lang.T("log.panelform.11") + dpi);
			RebuildUi();
		}

		private PageHook CurrentPageHook()
		{
			if (pageHooks == null || curPage == null)
			{
				return null;
			}
			for (int i = 0; i < pageHooks.Length; i++)
			{
				if (pageHooks[i] != null && pageHooks[i].Panel == curPage)
				{
					return pageHooks[i];
				}
			}
			return null;
		}

		private void BeginMoveSizeLoop()
		{
			StopPageReveal();
			if (!moveSizeLoop)
			{
				moveSizeLoop = true;
				if (introActive)
				{
					introActive = false;
					introPending = false;
					DropLayeredStyle();
				}
				if (uiTimer != null)
				{
					uiTimer.Stop();
				}
				if (fitTimer != null)
				{
					fitTimer.Stop();
				}
			}
		}

		private void EndMoveSizeLoop()
		{
			if (!moveSizeLoop) return;
			moveSizeLoop = false;
			if (base.IsDisposed || !UiActive) return;
			if (uiTimer != null) uiTimer.Start();
			OnUiTick(null, EventArgs.Empty);
			if (fitDeferredByDrag) { fitDeferredByDrag = false; ScheduleFit(); }
			QueueUnseenReleaseNotes();
		}

		public void RenderTo(string path, int pageIndex, bool showAntiCheat = false, bool showModePicker = false, string previewMode = null)
		{
			shotMode = true;
			base.StartPosition = FormStartPosition.Manual;
			base.Location = new Point(-20000, -20000);
			Show();
			if (pageIndex >= 0 && pageIndex < pages.Length)
			{
				nav.Select(pageIndex);
				nav.SnapToSelection();
				tuningNav.SnapToSelection();
				if (curPage != null)
				{
					curPage.Left = pageBaseLeft;
				}
			}
			OnUiTick(null, EventArgs.Empty);
			if (showAntiCheat)
			{
				nav.Select(3);
				nav.SnapToSelection();
				if (curPage != null)
				{
					curPage.Left = pageBaseLeft;
				}
			}
			PerformancePreset? obj;
			switch (previewMode)
			{
			default:
				obj = null;
				break;
			case "standard":
				obj = PerformancePreset.Standard;
				break;
			case "handheld":
				obj = PerformancePreset.Handheld;
				break;
			case "competitive":
				obj = PerformancePreset.Competitive;
				break;
			case "extreme":
				obj = PerformancePreset.Extreme;
				break;
			}
			PerformancePreset? performancePreset = obj;
			if (performancePreset.HasValue)
			{
				Theme.SetMode(performancePreset.Value, false);
				modeButton.SetMode(performancePreset.Value);
				nav.SetMode(performancePreset.Value, true);
				tuningNav.SetMode(performancePreset.Value, true);
				if (lblHeroMode != null)
				{
					lblHeroMode.Text = ModeButton.ModeName(performancePreset.Value);
					lblHeroMode.ForeColor = Theme.Accent;
				}
				if (paviseCore != null)
				{
					paviseCore.SetState(performancePreset.Value, true, false);
				}
			}
			if (showModePicker && modeButton != null)
			{
				modeButton.PerformClick();
			}
			if (previewMode == "power" && powerButton != null)
			{
				SetPowerFlyout(true);
			}
			if (previewMode == "advanced")
			{
				nav.Select(lastAdvancedPage);
				tuningNav.SnapToSelection();
				if (curPage != null)
				{
					curPage.Left = pageBaseLeft;
				}
			}
			if (previewMode == "audit" && pageIndex == 6)
			{
				try
				{
					RenderAudit(SystemAudit.Collect(400));
				}
				catch
				{
				}
				if (lblAuditStatus != null)
				{
					lblAuditStatus.Text = "";
				}
			}
			if (previewMode == "settings-appearance" && pageIndex == 8 && settingsTabs != null)
			{
				settingsTabs.Index = 2;
			}
			if (previewMode == "settings-maint" && pageIndex == 8 && settingsTabs != null)
			{
				settingsTabs.Index = 1;
			}
			Application.DoEvents();
			StopPageReveal();
			if (lstGames != null)
			{
				bool flag = false;
				foreach (GameLibraryItem item in lstGames.Items)
				{
					if (item.Profile != null && item.Profile.ExecutablePath != null && item.Profile.ExecutablePath.IndexOf("PaviseShot_", StringComparison.OrdinalIgnoreCase) >= 0)
					{
						gameIconCache["D:\\Games\\NebulaStrike\\Binaries\\Win64\\NebulaStrike-Win64-Shipping.exe"] = GameIcon(item.Profile.ExecutablePath);
						item.Profile.ExecutablePath = "D:\\Games\\NebulaStrike\\Binaries\\Win64\\NebulaStrike-Win64-Shipping.exe";
						flag = true;
					}
				}
				if (flag)
				{
					lstGames.Invalidate(true);
					Application.DoEvents();
				}
			}
			using (Bitmap bitmap = new Bitmap(base.ClientSize.Width, base.ClientSize.Height))
			{
				DrawToBitmap(bitmap, new Rectangle(0, 0, base.ClientSize.Width, base.ClientSize.Height));
				if (showModePicker && modeFlyout != null && modeFlyout.Visible)
				{
					using (Bitmap bitmap2 = new Bitmap(modeFlyout.Width, modeFlyout.Height))
					{
						using (Graphics graphics = Graphics.FromImage(bitmap))
						{
							modeFlyout.DrawToBitmap(bitmap2, new Rectangle(0, 0, bitmap2.Width, bitmap2.Height));
							graphics.DrawImageUnscaled(bitmap2, modeFlyout.Left, modeFlyout.Top);
						}
					}
				}
				bitmap.Save(path, ImageFormat.Png);
			}
			Hide();
			shotMode = false;
		}

		protected override void OnFormClosing(FormClosingEventArgs e)
		{
			if (SelfUpdateGate.IsBusy && e.CloseReason != CloseReason.WindowsShutDown)
			{
				e.Cancel = true;
				UpdateDialog.ActivateCurrent();
				return;
			}
			if (!RealExit && e.CloseReason != CloseReason.WindowsShutDown && e.CloseReason != CloseReason.ApplicationExitCall)
			{
				e.Cancel = true;
				BeginOutro();
			}
			else
			{
				CancelConfigurationPresetWork();
				base.OnFormClosing(e);
			}
		}

		private static void CollectThemeControls(Control parent, List<Control> controls)
		{
			controls.Add(parent);
			foreach (Control control in parent.Controls)
			{
				CollectThemeControls(control, controls);
			}
		}

		private void ApplyUiPalette(UiPalette previous)
		{
			UiPalette next = new UiPalette();
			List<Control> list = new List<Control>();
			CollectThemeControls(this, list);
			if (libraryConfigurationDialog != null && !libraryConfigurationDialog.IsDisposed)
			{
				CollectThemeControls(libraryConfigurationDialog, list);
			}
			foreach (Control item in list)
			{
				item.SuspendLayout();
			}
			try
			{
				for (int num = list.Count - 1; num >= 0; num--)
				{
					Control control = list[num];
					Color color = previous.Remap(control.BackColor, next);
					Color color2 = previous.Remap(control.ForeColor, next);
					if (control.BackColor != color)
					{
						control.BackColor = color;
					}
					if (control.ForeColor != color2)
					{
						control.ForeColor = color2;
					}
					FxControl fxControl = control as FxControl;
					if (fxControl != null)
					{
						fxControl.Bg = previous.Remap(fxControl.Bg, next);
					}
					RoundPanel roundPanel = control as RoundPanel;
					if (roundPanel != null)
					{
						roundPanel.Fill = previous.Remap(roundPanel.Fill, next);
						roundPanel.Border = previous.Remap(roundPanel.Border, next);
					}
					SettingCard settingCard = control as SettingCard;
					if (settingCard != null)
					{
						settingCard.ValueColor = previous.Remap(settingCard.ValueColor, next);
						settingCard.SetStatus(settingCard.StatusText, previous.Remap(settingCard.StatusColor, next));
					}
					FamilySuppressionSwitch familySuppressionSwitch = control as FamilySuppressionSwitch;
					if (familySuppressionSwitch != null)
					{
						familySuppressionSwitch.Surface = previous.Remap(familySuppressionSwitch.Surface, next);
					}
					GameCardExtension gameCardExtension = control as GameCardExtension;
					if (gameCardExtension != null)
					{
						gameCardExtension.SetSurface(Theme.Card);
					}
					ModuleBanner moduleBanner = control as ModuleBanner;
					if (moduleBanner != null)
					{
						moduleBanner.StateColor = previous.Remap(moduleBanner.StateColor, next);
					}
					StatusDot statusDot = control as StatusDot;
					if (statusDot != null)
					{
						statusDot.Color = previous.Remap(statusDot.Color, next);
					}
					CoreMatrix coreMatrix = control as CoreMatrix;
					if (coreMatrix != null)
					{
						coreMatrix.SelectionColor = previous.Remap(coreMatrix.SelectionColor, next);
					}
					LinkLabel linkLabel = control as LinkLabel;
					if (linkLabel != null)
					{
						linkLabel.LinkColor = previous.Remap(linkLabel.LinkColor, next);
						linkLabel.ActiveLinkColor = previous.Remap(linkLabel.ActiveLinkColor, next);
						linkLabel.VisitedLinkColor = previous.Remap(linkLabel.VisitedLinkColor, next);
					}
					if (control.ContextMenuStrip != null)
					{
						RefreshMenuPalette(control.ContextMenuStrip, previous, next);
					}
					if (control is ScrollableControl || control is TextBoxBase || control is ListControl)
					{
						Native.RefreshDark(control);
					}
				}
				if (themeSwitch != null)
				{
					themeSwitch.SetSilently(Theme.LightMode);
				}
				for (int i = 0; i < modeSwatches.Length; i++)
				{
					foreach (ColorSwatch item2 in modeSwatches[i])
					{
						if (item2.IsDefault)
						{
							item2.Swatch = Theme.ModeColorBuiltin((PerformancePreset)i);
							item2.Invalidate();
						}
					}
				}
				RefreshAccentLabels();
				RefreshModeAccentLabels();
				if (pageAbout == curPage)
				{
					RefreshAboutIcon();
				}
			}
			finally
			{
				for (int num2 = list.Count - 1; num2 >= 0; num2--)
				{
					list[num2].ResumeLayout(false);
				}
			}
			InvalidateVisibleTheme();
			if (libraryConfigurationDialog != null && !libraryConfigurationDialog.IsDisposed)
			{
				libraryConfigurationDialog.Invalidate(true);
			}
		}

		private static void RefreshMenuPalette(ToolStrip menu, UiPalette previous, UiPalette next)
		{
			menu.BackColor = previous.Remap(menu.BackColor, next);
			menu.ForeColor = previous.Remap(menu.ForeColor, next);
			foreach (ToolStripItem item in menu.Items)
			{
				item.BackColor = previous.Remap(item.BackColor, next);
				item.ForeColor = previous.Remap(item.ForeColor, next);
				ToolStripDropDownItem toolStripDropDownItem = item as ToolStripDropDownItem;
				if (toolStripDropDownItem != null && toolStripDropDownItem.HasDropDownItems)
				{
					RefreshMenuPalette(toolStripDropDownItem.DropDown, previous, next);
				}
			}
		}

		private void InvalidateVisibleTheme()
		{
			Invalidate(false);
			if (root == null || root.IsDisposed)
			{
				return;
			}
			root.Invalidate(false);
			foreach (Control control in root.Controls)
			{
				if (control.Visible)
				{
					control.Invalidate(true);
				}
			}
		}

		private int PageHeader(DBPanel page, string title, string sub, int subLines, bool showModule = true, int topOffset = 0, int contentWidth = 0)
		{
			return 16 + topOffset;
		}

		private string PageModuleCode(DBPanel page)
		{
			if (page == pagePolicy)
			{
				return "POLICY // MODULE 01";
			}
			if (page == pageCoreScheduling)
			{
				return "CPU SCHEDULING // MODULE 10";
			}
			if (page == pageAntiCheat)
			{
				return "DEFENSE // MODULE 02";
			}
			if (page == pageWhitelist)
			{
				return "EXCLUSION // MODULE 03";
			}
			if (page == pageGraphics)
			{
				return "GRAPHICS // MODULE 04";
			}
			if (page == pageEnvironment)
			{
				return "SYSTEM // MODULE 05";
			}
			if (page == pageIrq)
			{
				return "INTERRUPT // MODULE 06";
			}
			if (page == pageAudit)
			{
				return "REPORT // MODULE 08";
			}
			if (page == pageLog)
			{
				return "EVENT BUS // MODULE 09";
			}
			return "PAVISE // CONTROL SURFACE";
		}

		private Label Section(Control parent, string text, int x, int y)
		{
			AccentLine accentLine = new AccentLine();
			accentLine.SetBounds(Theme.S(x + 4), Theme.S(y + 5), Theme.S(3), Theme.S(8));
			parent.Controls.Add(accentLine);
			Label label = new Label();
			label.Text = text;
			label.ForeColor = Theme.Faint;
			label.BackColor = Theme.Bg;
			label.Font = Theme.UI(8.25f, true);
			label.UseCompatibleTextRendering = false;
			label.SetBounds(Theme.S(x + 14), Theme.S(y), Theme.S(400), Theme.S(18));
			parent.Controls.Add(label);
			return label;
		}

		private Toggle MakeSwitch(bool on, EventHandler handler)
		{
			Toggle toggle = new Toggle();
			toggle.Size = new Size(Theme.S(46), Theme.S(24));
			toggle.Bg = Theme.Card;
			toggle.SetSilently(on);
			if (handler != null)
			{
				toggle.CheckedChanged += handler;
			}
			return toggle;
		}

		private int AutoCardHeight(string desc, int cardW, Control host, int minHeight)
		{
			return AutoCardHeight(desc, cardW, host, minHeight, 0);
		}

		private int AutoCardHeight(string desc, int cardW, Control host, int minHeight, int valueReserve)
		{
			if (string.IsNullOrEmpty(desc))
			{
				return minHeight;
			}
			int num = Theme.S(42);
			int num2 = num + ((host != null) ? (host.Width + Theme.S(14)) : 0);
			int num3 = Theme.S(20);
			int num4 = Theme.S(cardW) - num - num2 - valueReserve - num3;
			if (num4 <= 0)
			{
				return minHeight;
			}
			Font font = DsType.Font(DsText.Description);
			int num5 = TextRenderer.MeasureText("Ag", font).Height;
			if (num5 <= 0)
			{
				return minHeight;
			}
			int num6 = TextRenderer.MeasureText(desc, font, new Size(num4, int.MaxValue), TextFormatFlags.WordBreak).Height;
			int num7 = (num6 + num5 - 1) / num5;
			if (num7 < 1)
			{
				num7 = 1;
			}
			if (num7 > 3)
			{
				num7 = 3;
			}
			int num8 = Theme.S(100);
			if (num8 <= 0)
			{
				return minHeight;
			}
			int num9 = num7 * num5 + Theme.S(17) + Theme.S(44);
			int num10 = (num9 * 100 + num8 - 1) / num8;
			if (num10 <= minHeight)
			{
				return minHeight;
			}
			return num10;
		}

		private SettingCard MakeAutoCard(Control parent, int x, int y, int w, int minH, string title, string desc, Control host, out int used)
		{
			used = AutoCardHeight(desc, w, host, minH);
			return MakeCard(parent, x, y, w, used, title, desc, host);
		}

		private int FullTextCardHeight(string desc, int width, Control host, int minimum)
		{
			int num = Theme.S(width - 84 - 20) - ((host != null) ? (host.Width + Theme.S(14)) : 0);
			if (num <= 0)
			{
				return minimum;
			}
			Font font = DsType.Font(DsText.Description);
			int num2 = Math.Max(1, TextRenderer.MeasureText("Ag", font).Height);
			int num3 = TextRenderer.MeasureText(desc ?? "", font, new Size(num, int.MaxValue), TextFormatFlags.WordBreak).Height;
			num3 = (num3 + num2 - 1) / num2 * num2;
			int num4 = num3 + Theme.S(64);
			int num5 = Math.Max(1, Theme.S(100));
			return Math.Max(minimum, (num4 * 100 + num5 - 1) / num5);
		}

		private SettingCard MakeCard(Control parent, int x, int y, int w, int h, string title, string desc, Control host)
		{
			SettingCard settingCard = ((parent is PerformanceFeatureGrid) ? new PerformanceFeatureCard() : new SettingCard());
			int num = 1;
			foreach (Control control in parent.Controls)
			{
				if (control is SettingCard)
				{
					num++;
				}
			}
			settingCard.Channel = num;
			settingCard.SetBounds(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(h));
			settingCard.Title = title;
			settingCard.Desc = desc ?? "";
			if (host != null)
			{
				settingCard.Host(host);
			}
			parent.Controls.Add(settingCard);
			return settingCard;
		}

		private RoundPanel MakeConsolePanel(Control parent, int x, int y, int width, int height, bool accent)
		{
			RoundPanel roundPanel = new RoundPanel();
			roundPanel.SetBounds(Theme.S(x), Theme.S(y), Theme.S(width), Theme.S(height));
			roundPanel.BackColor = Theme.Bg;
			roundPanel.Fill = Theme.Card;
			roundPanel.Border = Theme.Stroke;
			roundPanel.Radius = Theme.S(14);
			roundPanel.AccentEdge = accent;
			parent.Controls.Add(roundPanel);
			return roundPanel;
		}

		private Label CardLabel(Control parent, string text, int x, int y, int w, int h, float size, bool bold, Color color)
		{
			Label label = new Label();
			label.Text = text;
			label.ForeColor = color;
			label.BackColor = Color.Transparent;
			label.Font = Theme.UI(size, bold);
			label.AutoEllipsis = true;
			label.UseCompatibleTextRendering = false;
			label.SetBounds(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(h));
			parent.Controls.Add(label);
			return label;
		}

		internal static float FitFontSize(string text, int widthPx, bool bold, float maxSize, float minSize)
		{
			if (string.IsNullOrEmpty(text) || widthPx <= 0)
			{
				return maxSize;
			}
			for (float num = maxSize; num > minSize; num -= 0.5f)
			{
				if (TextRenderer.MeasureText(text, Theme.UI(num, bold), new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width <= widthPx)
				{
					return num;
				}
			}
			return minSize;
		}

		private static void FitLabelFont(Label label, bool bold, float maxSize, float minSize)
		{
			if (label != null && !label.IsDisposed && label.Width > 0)
			{
				float size = FitFontSize(label.Text, label.Width, bold, maxSize, minSize);
				Font font = Theme.UI(size, bold);
				if (!object.ReferenceEquals(label.Font, font))
				{
					label.Font = font;
				}
			}
		}

		private Label AccentLabel(Control parent, string text, int x, int y, int w, int h, float size, bool bold)
		{
			Label label = CardLabel(parent, text, x, y, w, h, size, bold, Theme.Accent);
			accentLabels.Add(label);
			return label;
		}

		private void RefreshAccentLabels()
		{
			for (int num = accentLabels.Count - 1; num >= 0; num--)
			{
				Label label = accentLabels[num];
				if (label == null || label.IsDisposed)
				{
					accentLabels.RemoveAt(num);
				}
				else
				{
					label.ForeColor = Theme.Accent;
					LinkLabel linkLabel = label as LinkLabel;
					if (linkLabel != null)
					{
						linkLabel.LinkColor = Theme.Accent;
						linkLabel.VisitedLinkColor = Theme.Accent;
						linkLabel.ActiveLinkColor = Theme.Fg;
					}
				}
			}
		}

		private DialogResult ShowDim(Form dlg)
		{
			StopPageReveal();
			return dlg.ShowDialog(this);
		}

		private DBPanel[] MakeTabPanels(Control page, TechTabs tabs, int count, int y)
		{
			DBPanel[] panels = new DBPanel[count];
			for (int i = 0; i < panels.Length; i++)
			{
				WorkspacePanel workspacePanel = new WorkspacePanel();
				workspacePanel.SetBounds(Theme.S(20), Theme.S(y), Theme.S(PageW - 40), Theme.S(PageH - y - 8));
				workspacePanel.BackColor = Theme.Bg;
				workspacePanel.AutoScroll = true;
				Native.Dark(workspacePanel);
				workspacePanel.Visible = i == 0;
				page.Controls.Add(workspacePanel);
				panels[i] = workspacePanel;
			}
			pageTabPanels[page] = panels;
			tabs.IndexChanged = delegate(int index)
			{
				for (int j = 0; j < panels.Length; j++)
				{
					if (j != index)
					{
						if (panels[j].Visible)
						{
							tabScrollPositions[panels[j]] = new Point(-panels[j].AutoScrollPosition.X, -panels[j].AutoScrollPosition.Y);
						}
						panels[j].Visible = false;
					}
				}
				panels[index].Visible = true;
				Point value;
				if (tabScrollPositions.TryGetValue(panels[index], out value))
				{
					panels[index].AutoScrollPosition = value;
				}
			};
			return panels;
		}

		private void AttachFormFrame()
		{
			if (!formFrameAttached)
			{
				UiClock.Frame += OnFormFrame;
				formFrameAttached = true;
			}
		}

		private void DetachFormFrame()
		{
			if (formFrameAttached)
			{
				UiClock.Frame -= OnFormFrame;
				formFrameAttached = false;
			}
		}

		protected override void OnVisibleChanged(EventArgs e)
		{
			if (!base.Visible)
			{
				SetModeFlyout(false);
			}
			if (!base.Visible)
			{
			}
			base.OnVisibleChanged(e);
			if (base.Visible)
			{
				CenterRoot();
				ScheduleFit();
			}
			else if (introActive)
			{
				FinishIntro();
			}
			SyncUiActivity();
		}

		protected override void OnResize(EventArgs e)
		{
			AcViewState acViewState = CaptureAcViewState();
			CoreViewState coreViewState = CaptureCoreViewState();
			SetModeFlyout(false);
			if (base.WindowState == FormWindowState.Minimized)
			{
			}
			base.OnResize(e);
			CenterRoot();
			RestoreAcScrollPosition(acViewState);
			RestoreCoreViewState(coreViewState);
			ScheduleFit();
			SyncUiActivity();
		}

		private void CenterRoot()
		{
			if (root == null || root.IsDisposed) return;
			int x = (base.ClientSize.Width - root.Width) / 2;
			int y = (base.ClientSize.Height - root.Height) / 2;
			if (x < 0) x = 0;
			if (y < 0) y = 0;
			if (root.Left != x || root.Top != y) root.Location = new Point(x, y);
		}

		private void ScheduleFit()
		{
			if (fitting || base.IsDisposed || !base.IsHandleCreated) return;
			if (base.WindowState == FormWindowState.Minimized) return;
			if (!Dpi.FitDiffers(base.ClientSize.Width, base.ClientSize.Height)) return;
			if (moveSizeLoop) { fitDeferredByDrag = true; return; }
			if (fitTimer == null)
			{
				fitTimer = new System.Windows.Forms.Timer();
				fitTimer.Interval = 220;
				fitTimer.Tick += OnFitTick;
			}
			fitTimer.Stop();
			fitTimer.Start();
		}

		private void OnFitTick(object sender, EventArgs e)
		{
			if (fitTimer != null) fitTimer.Stop();
			if (fitting || base.IsDisposed || !base.IsHandleCreated) return;
			if (moveSizeLoop) { fitDeferredByDrag = true; return; }
			if (base.WindowState == FormWindowState.Minimized) return;
			int w = base.ClientSize.Width, h = base.ClientSize.Height;
			if (!Dpi.FitDiffers(w, h)) return;
			float target = Dpi.FitScale(w, h);
			fitting = true;
			try
			{
				Dpi.Scale = target;
				Theme.DropFontCache();
				Logger.Log(Lang.T("log.panelform.6") + w + "x" + h
					+ Lang.T("log.panelform.7") + target.ToString("F2"));
				RebuildUi();
			}
			finally { fitting = false; }
			CenterRoot();
		}

		internal static bool ShouldRunUi(bool visible, FormWindowState windowState)
		{
			if (visible)
			{
				return windowState != FormWindowState.Minimized;
			}
			return false;
		}

		protected override void OnActivated(EventArgs e)
		{
			base.OnActivated(e);
			SyncUiForeground(true);
			QueueUnseenReleaseNotes();
		}

		protected override void OnDeactivate(EventArgs e)
		{
			if (powerButton == null || !powerButton.DropDownVisible)
			{
				SetModeFlyout(false);
			}
			base.OnDeactivate(e);
			SyncUiForeground(false);
		}

		private void SyncUiForeground(bool foreground)
		{
			SyncMotionAppearanceActivity(foreground);
			if (paviseCore != null)
			{
				paviseCore.SetForeground(foreground);
			}
			if (foreground && UiActive)
			{
				UiClock.Wake();
				UiClock.WakeSlow();
			}
		}

		private void SyncUiActivity()
		{
			bool flag = ShouldRunUi(base.IsHandleCreated && !base.IsDisposed && base.Visible, base.WindowState);
			if (uiActivityKnown && uiActive == flag)
			{
				return;
			}
			uiActivityKnown = true;
			uiActive = flag;
			bool gameActive = gameMode != null && gameMode.Enabled && gameMode.IsActive;
			SyncAutoHideBaseline(gameActive, ref lastGameActive, ref autoHideArmed);
			if (!flag)
			{
				SetOverviewTelemetryActive(false);
				if (motionAppearance != null && !motionAppearance.IsDisposed) motionAppearance.SetActivity(false, motionAppearanceForeground, gameActive);

				if (uiTimer != null)
				{
					uiTimer.Stop();
				}
				CancelAutoHide();
				UiClock.Suspended = true;
				if (paviseCore != null)
				{
					paviseCore.SetAnimationEnabled(false);
				}
			}
			else if (builtLang != Lang.Cur)
			{
				RebuildUi();
			}
			else
			{
				RefreshLightweightUiState();
				SyncToggleValues();
				UiClock.Suspended = false;
				if (uiTimer != null)
				{
					uiTimer.Start();
				}
				UiClock.Wake();
				UiClock.WakeSlow();
				NotifyPageActivation();
			}
		}

		private void RefreshLightweightUiState()
		{
			if (gameMode == null)
			{
				return;
			}
			if (lblStatus != null)
			{
				string text = gameMode.ActiveGame ?? Lang.T("workflow.game.waiting");
				if (lblStatus.Text != text)
				{
					lblStatus.Text = text;
					FitLabelFont(lblStatus, true, 14.5f, 9.5f);
				}
			}
			bool flag = gameMode.Enabled && gameMode.IsActive;
			if (curPage == pageOverview)
			{
				RefreshOverviewAttention();
			}
			UiClock.Frozen = flag;
			SyncMotionAppearanceActivity();
			if (statusDot != null)
			{
				statusDot.Color = ((!gameMode.Enabled) ? Theme.Dim : (flag ? Theme.Green : Theme.Accent));
				statusDot.Pulse = flag;
			}
			if (paviseCore != null)
			{
				paviseCore.SetState(gameMode.ActivePreset, gameMode.Enabled, flag);
			}
			if (lblSub != null && elevated)
			{
				string activeGame = gameMode.ActiveGame;
				string text2 = Lang.T("title.admin") + " " + ((activeGame != null) ? Lang.F("title.guard", activeGame) : Lang.T("v20.admin.ready"));
				if (lblSub.Text != text2)
				{
					lblSub.Text = text2;
				}
				lblSub.ForeColor = ((activeGame != null) ? Theme.Green : Theme.Faint);
			}
			RefreshBoostPresentation();
		}

		private void OnFormFrame(object s, EventArgs e)
		{
			if (Theme.StepTheme())
			{
				RefreshAccentLabels();
				InvalidateVisibleTheme();
			}
			StepIntro();
			if (!introActive && !introPending && !outroActive && base.AllowTransparency)
			{
				DropLayeredStyle();
			}
		}

		private void StepIntro()
		{
			if (introActive)
			{
				if (introMotion.Step())
				{
					base.Opacity = 1.0 - (double)introMotion.Value;
				}
				else
				{
					FinishIntro();
				}
			}
		}

		private void FinishIntro()
		{
			introActive = false;
			if (base.Opacity < 1.0)
			{
				base.Opacity = 1.0;
			}
			DropLayeredStyle();
			QueueUnseenReleaseNotes();
		}

		private void DropLayeredStyle()
		{
			if (!base.AllowTransparency)
			{
				return;
			}
			try
			{
				base.Opacity = 1.0;
				base.AllowTransparency = false;
				Invalidate(true);
			}
			catch
			{
			}
		}

		private void BeginIntro()
		{
			StopPageReveal();
			introActive = false;
			introPending = true;
			base.Opacity = 0.0;
		}

		private void StartIntro()
		{
			if (!introPending)
			{
				if (!introActive)
				{
					DropLayeredStyle();
				}
				return;
			}
			introPending = false;
			introMotion.Speed = 0.24f;
			introMotion.Set(1f);
			introMotion.To(0f);
			introActive = true;
			PaintTree(this);
			UiClock.Wake(90);
			if (!UiClock.Running)
			{
				FinishIntro();
			}
		}

		private static void PaintTree(Control c)
		{
			if (c.IsHandleCreated && c.Visible)
			{
				c.Update();
				for (int i = 0; i < c.Controls.Count; i++)
				{
					PaintTree(c.Controls[i]);
				}
			}
		}

		internal static void SyncAutoHideBaseline(bool gameActive, ref bool lastActive, ref bool armed)
		{
			lastActive = gameActive;
			armed = gameActive;
		}

		private void OnUiTick(object s, EventArgs e)
		{
			if (UiActive && !moveSizeLoop)
			{
				if (overviewMemoryCleanup != null) overviewMemoryCleanup.SetGamePolicy(gameMode.IsActive);
				UpdateAutoHide(gameMode.Enabled && gameMode.IsActive);
				RefreshLightweightUiState();
				UpdateModePresentation(true);
				PageHook pageHook = CurrentPageHook();
				if (pageHook != null && pageHook.OnTick != null)
				{
					pageHook.OnTick();
				}
			}
		}

		internal static AutoHideAction NextAutoHide(bool gameActive, ref bool lastActive, ref bool armed, bool settingOn, bool visible)
		{
			if (gameActive == lastActive)
			{
				return AutoHideAction.None;
			}
			lastActive = gameActive;
			if (!gameActive)
			{
				armed = false;
				return AutoHideAction.Cancel;
			}
			if (armed)
			{
				return AutoHideAction.None;
			}
			armed = true;
			if (!settingOn || !visible)
			{
				return AutoHideAction.None;
			}
			return AutoHideAction.Schedule;
		}

		private void UpdateAutoHide(bool gameActive)
		{
			switch (NextAutoHide(gameActive, ref lastGameActive, ref autoHideArmed, Settings.LoadCached("AutoHideOnGame", true), UiActive))
			{
			case AutoHideAction.Cancel:
				CancelAutoHide();
				break;
			case AutoHideAction.Schedule:
				CancelAutoHide();
				autoHideTimer = new System.Windows.Forms.Timer();
				autoHideTimer.Interval = 10000;
				autoHideTimer.Tick += OnAutoHideTick;
				autoHideTimer.Start();
				break;
			}
		}

		private void OnAutoHideTick(object s, EventArgs e)
		{
			if (!moveSizeLoop)
			{
				CancelAutoHide();
				if (!base.IsDisposed && UiActive && !AnyDialogOpen())
				{
					BeginOutro();
				}
			}
		}

		private void BeginOutro()
		{
			if (SelfUpdateGate.IsBusy) { UpdateDialog.ActivateCurrent(); return; }
			StopPageReveal();
			if (outroActive)
			{
				return;
			}
			if (!base.IsHandleCreated || base.IsDisposed || !base.Visible || base.WindowState != 0)
			{
				Hide();
				return;
			}
			if (introActive)
			{
				FinishIntro();
			}
			introPending = false;
			outroActive = true;
			base.AllowTransparency = true;
			base.Opacity = 1.0;
			outroWatch.Reset();
			outroWatch.Start();
			if (outroTimer == null)
			{
				outroTimer = new System.Windows.Forms.Timer();
				outroTimer.Interval = 10;
				outroTimer.Tick += OnOutroTick;
			}
			outroTimer.Start();
		}

		private void OnOutroTick(object s, EventArgs e)
		{
			if (!outroActive || base.IsDisposed)
			{
				FinishOutro(false);
				return;
			}
			double num = outroWatch.Elapsed.TotalMilliseconds / 220.0;
			if (num >= 1.0)
			{
				FinishOutro(true);
				return;
			}
			double num2 = num * num;
			base.Opacity = 1.0 - num2;
		}

		private void FinishOutro(bool hide)
		{
			outroActive = false;
			outroWatch.Reset();
			if (outroTimer != null)
			{
				outroTimer.Stop();
			}
			if (!base.IsDisposed)
			{
				if (hide)
				{
					Hide();
				}
				base.Opacity = 1.0;
				DropLayeredStyle();
			}
		}

		private void CancelOutro()
		{
			if (outroActive)
			{
				FinishOutro(false);
			}
		}

		private void CancelAutoHide()
		{
			if (autoHideTimer != null)
			{
				autoHideTimer.Stop();
				autoHideTimer.Tick -= OnAutoHideTick;
				autoHideTimer.Dispose();
				autoHideTimer = null;
			}
		}

		[DllImport("user32.dll")]
		private static extern bool IsWindowEnabled(IntPtr hwnd);

		private bool AnyDialogOpen()
		{
			try
			{
				if (base.IsHandleCreated && !IsWindowEnabled(base.Handle))
				{
					return true;
				}
				foreach (Form openForm in Application.OpenForms)
				{
					if (!object.ReferenceEquals(openForm, this) && !(openForm is PageReveal) && openForm.Visible)
					{
						return true;
					}
				}
			}
			catch
			{
			}
			return false;
		}

		private void OnAutoHideToggle(object s, EventArgs e)
		{
			Settings.Save("AutoHideOnGame", swAutoHide.Checked);
			if (!swAutoHide.Checked)
			{
				CancelAutoHide();
			}
			swAutoHide.SetSilently(Settings.Load("AutoHideOnGame", true));
		}

		private void OnEscHide(object s, KeyEventArgs e)
		{
			if (e.KeyCode != Keys.Escape)
			{
				return;
			}
			if (powerButton != null && powerButton.DropDownVisible)
			{
				SetPowerFlyout(false);
			}
			else if (modeFlyout != null && modeFlyout.Visible)
			{
				SetModeFlyout(false);
			}
			else if ((curPage != pageAntiCheat || !CloseAcDrawer()) && (curPage != pageLibrary || lstGames == null || !lstGames.CollapseExpandedConfiguration()))
			{
				if (curPage == pageCoreScheduling)
				{
					nav.Select(1);
				}
				else if (IsInDeepTuning)
				{
					ReturnFromDeepTuning();
				}
				else
				{
					BeginOutro();
				}
			}
			e.Handled = true;
			e.SuppressKeyPress = true;
		}
	}
}
