using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm : Form
    {
        private static void AssertNavMatchesPageIds(NavRail rail)
        {
            int pageIdCount = (int)PageId.Count;
            if (rail.ItemCount != pageIdCount)
            {
                throw new InvalidOperationException(Lang.T("t.panelform.1") + rail.ItemCount + Lang.T("t.panelform.2") + pageIdCount + Lang.T("t.panelform.3"));
            }
            bool[] conditionMetItems = new bool[pageIdCount];
            int processedCount = 0;
            for (int i = 0; i < pageIdCount; i++)
            {
                int itemAtSlotResult = rail.ItemAtSlot(i);
                if (itemAtSlotResult < 0)
                {
                    break;
                }
                if (itemAtSlotResult < 0 || itemAtSlotResult >= pageIdCount)
                {
                    throw new InvalidOperationException(Lang.T("t.panelform.4") + itemAtSlotResult);
                }
                if (conditionMetItems[itemAtSlotResult])
                {
                    throw new InvalidOperationException(Lang.T("t.panelform.5") + (PageId)itemAtSlotResult);
                }
                conditionMetItems[itemAtSlotResult] = true;
                processedCount++;
            }
            if (processedCount == 0)
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
            List<string> ts = new List<string>();
            ts.Add(Lang.T("plan.pick.auto"));
            ts.Add(Lang.T("plan.pick.off"));
            List<string> names = ts;
            foreach (PowerPlanEntry listUserPlan in PowerPlan.ListUserPlans())
            {
                powerPlanChoices.Add(listUserPlan.Id.ToString());
                names.Add(listUserPlan.Name);
            }
            string selected = PowerPlan.EffectivePlanId;
            if (!powerPlanChoices.Exists(id => string.Equals(id, selected, StringComparison.OrdinalIgnoreCase)))
            {
                powerPlanChoices.Add(selected);
                names.Add(AutoText("不可用", "Unavailable") + " [" + selected + "]");
            }
            powerButton.SetItems(names.ToArray());
        }
        private void ToggleModeFlyout()
        {
            SetModeFlyout(modeFlyout != null && !modeFlyout.Visible);
        }
        private static bool IsAdvancedPage(int index)
        {
            return false;
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
            System.Windows.Forms.Timer currentOutroTimer = outroTimer;
            outroTimer = null;
            if (currentOutroTimer != null)
            {
                currentOutroTimer.Tick -= OnOutroTick;
                currentOutroTimer.Stop();
                currentOutroTimer.Dispose();
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
        private void DragMove(object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                Native.ReleaseCapture();
                Native.SendMessage(base.Handle, 161, (IntPtr)2, IntPtr.Zero);
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
        private void SavePagePosition(Control page)
        {
            if (pages != null && page != null && !page.IsDisposed)
            {
                int matchIndex = Array.IndexOf(pages, page);
                if (matchIndex >= 0)
                {
                    pagePositions[matchIndex] = PageViewPosition.Capture(page, navigationScale);
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
            DBPanel[] pageTabPanel;
            if (pageTabPanels.TryGetValue(pages[index], out pageTabPanel))
            {
                foreach (DBPanel dBPanel in pageTabPanel)
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
        private int PageHeader(DBPanel page, string title, string sub, int subLines, bool showModule = true, int topOffset = 0, int contentWidth = 0)
        {
            return 16 + topOffset;
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
    }
}
