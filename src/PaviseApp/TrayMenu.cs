using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class TrayMenu
    {
        private readonly GameMode gameMode;

        private readonly Action exitApp;

        private readonly Action afterChange;

        private readonly ContextMenuStrip strip;

        private static string reserve;

        private static int reserveScale;

        public ContextMenuStrip Strip
        {
            get
            {
                return strip;
            }
        }

        public TrayMenu(GameMode gameMode, Action exitApp, Action afterChange)
        {
            this.gameMode = gameMode;
            this.exitApp = exitApp;
            this.afterChange = afterChange;
            ToolStripManager.Renderer = new DarkMenuRenderer();
            strip = new ContextMenuStrip();
            StyleDropDown(strip);
            ContextMenuStrip contextMenuStrip = strip;
            CancelEventHandler value = delegate
            {
                Rebuild();
            };
            contextMenuStrip.Opening += value;
            Rebuild();
        }

        private void Changed()
        {
            Action action = afterChange;
            if (action != null)
            {
                try
                {
                    action();
                }
                catch
                {
                }
            }
        }

        private static void StyleDropDown(ToolStripDropDown dd)
        {
            dd.Font = Theme.UI(9.5f, false);
            ToolStripDropDownMenu toolStripDropDownMenu = dd as ToolStripDropDownMenu;
            if (toolStripDropDownMenu != null)
            {
                toolStripDropDownMenu.ShowImageMargin = true;
                toolStripDropDownMenu.ShowCheckMargin = false;
                toolStripDropDownMenu.ImageScalingSize = new Size(Theme.S(6), Theme.S(6));
            }
            dd.Opened += delegate
            {
                try
                {
                    Native.RoundCorners(dd.Handle);
                }
                catch
                {
                }
            };
        }

        private ToolStripMenuItem Item(string text, EventHandler onClick)
        {
            ToolStripMenuItem toolStripMenuItem = new ToolStripMenuItem(text);
            toolStripMenuItem.ForeColor = Theme.Fg;
            toolStripMenuItem.Padding = new Padding(0, Theme.S(7), 0, Theme.S(7));
            if (onClick != null)
            {
                toolStripMenuItem.Click += onClick;
            }
            return toolStripMenuItem;
        }

        private ToolStripMenuItem Check(string text, bool on, EventHandler onClick)
        {
            ToolStripMenuItem toolStripMenuItem = Item(text + Reserve(), onClick);
            toolStripMenuItem.Checked = on;
            return toolStripMenuItem;
        }

        private static string Reserve()
        {
            if (reserve == null || reserveScale != Theme.S(30))
            {
                reserveScale = Theme.S(30);
                Font font = Theme.UI(9.5f, false);
                int width = TextRenderer.MeasureText("\u00a0", font, Size.Empty, TextFormatFlags.NoPadding).Width;
                int wheelScale = ((width > 0) ? ((int)Math.Ceiling((double)Theme.S(30) / (double)width)) : 6);
                reserve = new string('\u00a0', Math.Max(7, wheelScale));
            }
            return reserve;
        }

        private void Rebuild()
        {
            while (strip.Items.Count > 0)
            {
                ToolStripItem toolStripItem = strip.Items[0];
                strip.Items.RemoveAt(0);
                toolStripItem.Dispose();
            }
            strip.Items.Add(Check(Lang.T("v14.master"), gameMode.Enabled, delegate
            {
                gameMode.Enabled = !gameMode.Enabled;
                Settings.Save("GameModeOn", gameMode.Enabled);
                Changed();
            }));
            strip.Items.Add(Check(Lang.T("tm.autostart"), TaskHelper.IsStartupTaskEnabled(), delegate
            {
                ToggleAutostart();
                Changed();
            }));
            strip.Items.Add(new ToolStripSeparator());
            strip.Items.Add(Item(Lang.T("tray.exit"), delegate
            {
                exitApp();
            }));
        }

        private void ToggleAutostart()
        {
            if ((TaskHelper.IsStartupTaskEnabled() ? TaskHelper.DeleteStartupTask() : TaskHelper.CreateStartupTask()) != 0)
            {
                string lastSchtasksError = TaskHelper.LastSchtasksError;
                TaskHelper.IsStartupTaskEnabled();
                string body = Lang.T("msg.taskfail") + (string.IsNullOrEmpty(lastSchtasksError) ? "" : ("\r\n\r\n" + lastSchtasksError));
                PaviseDialog.Warn(null, "PAVISE AUTO", body);
            }
        }
    }
}
