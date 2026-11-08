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
		private void BuildCoreSchedulingPage()
		{
			BuildCoreWorkspace();
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
	}
}
