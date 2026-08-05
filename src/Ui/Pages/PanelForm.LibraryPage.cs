// @author bdth 2074055628@qq.com
// 文件用途 构建游戏库页 并维护条目的运行状态与网络策略同步

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private ListBox lstGames;
        private EmptyStatePanel gameListPanel;
        private readonly Dictionary<string, Bitmap> gameIconCache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        private int runningBusy;
        private long nextRunningProbeTicks;
        private int netQosBusy;
        private string netQosSignature;
        private bool netQosDeferred;
        private static readonly long RunningProbeIntervalTicks = TimeSpan.FromSeconds(5).Ticks;

        private void BuildLibraryPage()
        {
            int y = PageHeader(pageLibrary, Lang.T("nav.library"), Lang.T("v15.library.sub"), 2);
            int listH = PageH - y - 16;
            int listW = ContentW - 238;
            var listWrap = new EmptyStatePanel();
            gameListPanel = listWrap;
            listWrap.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(listW), Theme.S(listH));
            listWrap.BackColor = Theme.Bg; listWrap.Fill = Theme.Card; listWrap.Border = Theme.Stroke; listWrap.Radius = Theme.S(14);
            listWrap.EmptyTitle = "PAVISE LIBRARY";
            listWrap.EmptyDetail = Lang.T("v15.library.empty");
            listWrap.Padding = new Padding(Theme.S(8));
            lstGames = new ListBox(); lstGames.Dock = DockStyle.Fill; Theme.StyleList(lstGames);
            lstGames.ItemHeight = Math.Min(255, Theme.S(68));
            lstGames.DrawItem += DrawGameLibraryItem;
            lstGames.KeyDown += delegate(object s, KeyEventArgs e)
            {
                GameLibraryItem item = lstGames.SelectedItem as GameLibraryItem;
                if (e.KeyCode == Keys.Delete && item != null) { gameMode.RemoveProfile(item.Profile.Id); RefreshGames(); }
            };
            listWrap.Controls.Add(lstGames);
            int bx = ContentX + listW + 16, bw = ContentW - listW - 16, bh = 40;
            var scan = new PillButton(Lang.T("v17.library.scan"), BtnKind.Primary);
            scan.SetBounds(Theme.S(bx), Theme.S(y), Theme.S(bw), Theme.S(bh));
            scan.Click += delegate { ScanInstalledGames(); };
            var browse = new PillButton(Lang.T("v15.library.add")); browse.SetBounds(Theme.S(bx), Theme.S(y + 50), Theme.S(bw), Theme.S(bh)); browse.Click += delegate { BrowseGameExecutable(); };
            var fromProc = new PillButton(Lang.T("v16.library.fromproc"));
            fromProc.SetBounds(Theme.S(bx), Theme.S(y + 100), Theme.S(bw), Theme.S(bh));
            fromProc.Click += delegate { AddGameFromRunningProcess(); };
            var remove = new PillButton(Lang.T("btn.remove")); remove.SetBounds(Theme.S(bx), Theme.S(y + 150), Theme.S(bw), Theme.S(bh));
            remove.Click += delegate
            {
                GameLibraryItem item = lstGames.SelectedItem as GameLibraryItem;
                if (item != null) { gameMode.RemoveProfile(item.Profile.Id); RefreshGames(); }
            };
            Label hint = new Label(); hint.Text = Lang.T("v15.library.drop"); hint.ForeColor = Theme.Dim; hint.BackColor = Theme.Bg;
            hint.Font = Theme.UI(8.2f, false); hint.AutoEllipsis = true; hint.SetBounds(Theme.S(bx + 4), Theme.S(y + 208), Theme.S(bw - 8), Theme.S(64));
            pageLibrary.Controls.AddRange(new Control[] { listWrap, scan, browse, fromProc, remove, hint });
            RefreshGames();
        }

        private void DrawGameLibraryItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            GameLibraryItem item = lstGames.Items[e.Index] as GameLibraryItem;
            if (item == null || item.Profile == null) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            int hover = lstGames.Tag is int ? (int)lstGames.Tag : -1;
            Rectangle row = Rectangle.Inflate(e.Bounds, -Theme.S(4), -Theme.S(3));
            using (var back = new SolidBrush(Theme.Card)) e.Graphics.FillRectangle(back, e.Bounds);
            Theme.FillRound(e.Graphics, row, Theme.S(10), selected ? Theme.Sel : (e.Index == hover ? Theme.CardHover : Theme.Card));
            int iconSize = Theme.S(38), ix = e.Bounds.X + Theme.S(14), iy = e.Bounds.Y + (e.Bounds.Height - iconSize) / 2;
            e.Graphics.DrawImage(GameIcon(item.Profile.ExecutablePath), new Rectangle(ix, iy, iconSize, iconSize));
            int tx = ix + iconSize + Theme.S(14), right = Theme.S(92);
            TextRenderer.DrawText(e.Graphics, item.Profile.Name, Theme.UI(10.2f, true),
                    new Rectangle(tx, e.Bounds.Y + Theme.S(11), e.Bounds.Width - tx - right, Theme.S(22)),
                    Theme.Fg, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            string path = item.Profile.ExecutablePath ?? item.Profile.Root ?? "";
            TextRenderer.DrawText(e.Graphics, path, Theme.UI(7.8f, false),
                    new Rectangle(tx, e.Bounds.Y + Theme.S(37), e.Bounds.Width - tx - Theme.S(18), Theme.S(18)),
                    Theme.Dim, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(e.Graphics, Lang.T(item.Running ? "v15.library.running" : "v15.library.ready"), Theme.UI(7.6f, true),
                    new Rectangle(e.Bounds.Right - right, e.Bounds.Y + Theme.S(12), right - Theme.S(16), Theme.S(20)),
                    item.Running ? Theme.Green : Theme.Faint, TextFormatFlags.Right | TextFormatFlags.NoPadding);
        }

        private void ScanInstalledGames()
        {
            var known = new List<string>();
            foreach (GameProfile p in gameMode.GetProfiles())
            {
                if (!string.IsNullOrEmpty(p.ExecutablePath)) known.Add(p.ExecutablePath);
                if (!string.IsNullOrEmpty(p.LearnedExecutablePath)) known.Add(p.LearnedExecutablePath);
            }

            using (var dlg = new GameScanDialog(known))
            {
                if (ShowDim(dlg) != DialogResult.OK || dlg.Selected.Count == 0) return;
                string lastError;
                int added = gameMode.AddScannedGames(dlg.Selected, out lastError);
                RefreshGames();
                if (added > 0) Logger.Log(Lang.F("scan.added", added));
                else if (lastError != null)
                    MessageBox.Show(this, lastError, "Pavise", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void AddGameFromRunningProcess()
        {
            using (var dlg = new ProcessPickerDialog())
            {
                if (ShowDim(dlg) != DialogResult.OK || string.IsNullOrEmpty(dlg.SelectedPath)) return;
                string error;
                if (!gameMode.AddGameFile(dlg.SelectedPath, out error) && !string.IsNullOrEmpty(error))
                    MessageBox.Show(this, error, "Pavise", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                RefreshGames();
            }
        }

        private void BrowseGameExecutable()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = Lang.T("ofd.game");
                dlg.Filter = Lang.T("ofd.filter");
                dlg.CheckFileExists = false;
                dlg.DereferenceLinks = false;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    string error;
                    if (!gameMode.AddGameFile(dlg.FileName, out error))
                        MessageBox.Show(this, error, "Pavise", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    RefreshGames();
                }
            }
        }

        private void RefreshGames()
        {
            if (lstGames == null) return;
            List<GameProfile> profiles = gameMode.GetProfiles();
            var paths = new List<string>();
            foreach (GameProfile profile in profiles) paths.Add(profile.ExecutablePath);
            Dictionary<string, bool> states = ProbeRunning(paths);
            lstGames.BeginUpdate();
            lstGames.Items.Clear();
            foreach (GameProfile profile in profiles)
                lstGames.Items.Add(new GameLibraryItem(profile, RunningIn(states, profile.ExecutablePath)));
            lstGames.EndUpdate();
            bool empty = lstGames.Items.Count == 0;
            lstGames.Visible = !empty;
            if (gameListPanel != null) { gameListPanel.ShowEmpty = empty; gameListPanel.Invalidate(); }
            SyncNetQosPolicies(profiles);
        }

        private void RefreshGameRunningStates(bool force = false)
        {
            if (!UiActive || lstGames == null) return;
            if (netQosDeferred && gameMode.ActiveGame == null)
                SyncNetQosPolicies(gameMode.GetProfiles());
            long now = DateTime.UtcNow.Ticks;
            if (!force && now < Interlocked.Read(ref nextRunningProbeTicks)) return;
            if (Interlocked.Exchange(ref runningBusy, 1) == 1) return;
            Interlocked.Exchange(ref nextRunningProbeTicks, now + RunningProbeIntervalTicks);
            var paths = new List<string>();
            foreach (object value in lstGames.Items)
            {
                GameLibraryItem item = value as GameLibraryItem;
                if (item != null && item.Profile != null) paths.Add(item.Profile.ExecutablePath);
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Dictionary<string, bool> states = null;
                try { states = ProbeRunning(paths); }
                catch { }
                Interlocked.Exchange(ref runningBusy, 0);
                if (states == null || !UiActive) return;
                try
                {
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        if (IsDisposed || !UiActive || lstGames == null) return;
                        ApplyRunningStates(states);
                    }));
                }
                catch { }
            });
        }

        private void ApplyRunningStates(Dictionary<string, bool> states)
        {
            bool changed = false;
            foreach (object value in lstGames.Items)
            {
                GameLibraryItem item = value as GameLibraryItem;
                if (item == null || item.Profile == null) continue;
                string path = item.Profile.ExecutablePath;
                bool running;
                if (string.IsNullOrEmpty(path) || !states.TryGetValue(path, out running)) continue;
                if (running != item.Running) { item.Running = running; changed = true; }
            }
            if (changed) lstGames.Invalidate();
        }

        private void SyncNetQosPolicies(List<GameProfile> profiles)
        {
            var sb = new System.Text.StringBuilder();
            foreach (GameProfile profile in profiles)
            {
                if (string.IsNullOrEmpty(profile.ExecutablePath)) continue;
                sb.Append(profile.Name).Append('>').Append(profile.ExecutablePath).Append('|');
            }
            string signature = sb.ToString();
            if (netQosSignature == null) { netQosSignature = signature; return; }
            if (netQosSignature == signature && !netQosDeferred) return;
            netQosSignature = signature;
            if (!NetworkAffinityTweak.EnabledByPavise) { netQosDeferred = false; return; }
            if (gameMode.ActiveGame != null)
            {
                if (!netQosDeferred) Logger.Log("网络优先级：游戏会话进行中，游戏库变更的策略同步推迟到退出游戏后执行");
                netQosDeferred = true;
                return;
            }
            netQosDeferred = false;
            if (Interlocked.Exchange(ref netQosBusy, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                lock (netQosSync)
                {
                    Interlocked.Exchange(ref netQosBusy, 0);
                    try { NetworkAffinityTweak.Enable(gameMode.GetProfiles()); }
                    catch { }
                }
            });
        }

        private void AddDroppedGames(string[] files)
        {
            if (files == null) return;
            string error = null;
            foreach (string file in files)
                if (!gameMode.AddGameFile(file, out error) && error != "该游戏已经在列表中") break;
            if (!string.IsNullOrEmpty(error) && error != "该游戏已经在列表中")
                MessageBox.Show(this, error, "Pavise", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RefreshGames();
        }

        private static bool RunningIn(Dictionary<string, bool> states, string executablePath)
        {
            bool running;
            return !string.IsNullOrEmpty(executablePath)
                && states.TryGetValue(executablePath, out running) && running;
        }

        private static Dictionary<string, bool> ProbeRunning(List<string> paths)
        {
            var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var wanted = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path) || result.ContainsKey(path)) continue;
                result[path] = false;
                string name;
                try { name = Path.GetFileNameWithoutExtension(path); }
                catch { continue; }
                if (string.IsNullOrEmpty(name)) continue;
                List<string> bucket;
                if (!wanted.TryGetValue(name, out bucket)) { bucket = new List<string>(); wanted[name] = bucket; }
                bucket.Add(path);
            }
            if (wanted.Count == 0) return result;
            Process[] all = null;
            try
            {
                all = Process.GetProcesses();
                foreach (Process process in all)
                {
                    List<string> bucket = null;
                    int pid = 0;
                    try { wanted.TryGetValue(process.ProcessName, out bucket); pid = process.Id; }
                    catch { continue; }
                    if (bucket == null) continue;
                    IntPtr handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (handle == IntPtr.Zero) continue;
                    string image;
                    try { image = Native.ImagePath(handle); }
                    finally { Native.CloseHandle(handle); }
                    if (string.IsNullOrEmpty(image)) continue;
                    foreach (string path in bucket)
                        if (string.Equals(path, image, StringComparison.OrdinalIgnoreCase)) result[path] = true;
                }
            }
            catch { }
            finally { if (all != null) foreach (Process process in all) process.Dispose(); }
            return result;
        }

        private Bitmap GameIcon(string executablePath)
        {
            string key = executablePath ?? "";
            Bitmap bitmap;
            if (gameIconCache.TryGetValue(key, out bitmap)) return bitmap;
            try
            {
                using (Icon icon = Icon.ExtractAssociatedIcon(executablePath)) bitmap = icon.ToBitmap();
            }
            catch { bitmap = appIcon == null ? new Bitmap(32, 32) : appIcon.ToBitmap(); }
            gameIconCache[key] = bitmap;
            return bitmap;
        }

        private sealed class GameLibraryItem
        {
            public readonly GameProfile Profile;
            public bool Running;
            public GameLibraryItem(GameProfile profile, bool running) { Profile = profile; Running = running; }
            public override string ToString() { return Profile == null ? "" : Profile.Name; }
        }
    }
}
