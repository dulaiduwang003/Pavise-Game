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

		// Only settings restored by session teardown belong to the in-match group.

		[DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
		private static extern IntPtr SendLibraryCue(IntPtr window, int message, IntPtr parameter, string text);

		[DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
		private static extern IntPtr SendWhitelistCue(IntPtr window, int message, IntPtr parameter, string text);

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

		[DllImport("user32.dll", SetLastError = true)]
		private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSettingGuid, uint flags);

		[DllImport("user32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

		[DllImport("user32.dll")]
		private static extern bool IsWindowEnabled(IntPtr hwnd);

	}
}
