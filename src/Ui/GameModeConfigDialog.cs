// @author bdth 2074055628@qq.com
// 文件用途 编辑游戏模式的细项配置

using System;
using System.Drawing;
using System.Windows.Forms;

namespace AegisApp
{
    internal class GameModeConfigDialog : Form
    {
        private readonly GameMode gameMode;

        public GameModeConfigDialog(GameMode gm)
        {
            gameMode = gm;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Theme.S(460), Theme.S(686));
            BackColor = Theme.Bg;
            Font = Theme.UI(9.5f, false);

            var title = new Label();
            title.Text = Lang.T("gm.cfg");
            title.ForeColor = Theme.Fg;
            title.Font = Theme.UI(10.5f, true);
            title.SetBounds(Theme.S(16), Theme.S(12), Theme.S(300), Theme.S(24));
            title.MouseDown += DragMove;

            var lblClose = new Label();
            lblClose.Text = "✕";
            lblClose.ForeColor = Theme.Dim;
            lblClose.SetBounds(Theme.S(424), Theme.S(10), Theme.S(26), Theme.S(26));
            lblClose.TextAlign = ContentAlignment.MiddleCenter;
            lblClose.Cursor = Cursors.Hand;
            lblClose.Click += (s, e) => DialogResult = DialogResult.OK;

            int y = Theme.S(48);
            AddRow(ref y, Lang.T("gm.suppress"), gameMode.SuppressBackground, v => gameMode.SuppressBackground = v);
            AddRow(ref y, Lang.T("gm.gpudemote"), gameMode.GpuDemote, v => gameMode.GpuDemote = v);
            AddRow(ref y, Lang.T("set.trim"), gameMode.TrimWorkingSet, v => gameMode.TrimWorkingSet = v);
            AddRow(ref y, Lang.T("gm.boost"), gameMode.BoostGame, v => gameMode.BoostGame = v);
            AddRow(ref y, Lang.T("gm.strict"), gameMode.StrictCoreIsolation, v => gameMode.StrictCoreIsolation = v);
            AddRow(ref y, Lang.T("set.plan"), gameMode.PowerPlanSwitch, v => gameMode.PowerPlanSwitch = v);
            AddRow(ref y, Lang.T("gm.net"), gameMode.NetOptimize, v => gameMode.NetOptimize = v);
            AddRow(ref y, Lang.T("gm.fgboost"), gameMode.FgSchedBoost, v => gameMode.FgSchedBoost = v);
            AddRow(ref y, Lang.T("gm.mmcss"), gameMode.MmcssPriority, v => gameMode.MmcssPriority = v);
            AddRow(ref y, Lang.T("gm.pausedl"), gameMode.PauseDownloads, v => gameMode.PauseDownloads = v);
            AddRow(ref y, Lang.T("gm.pausesvc"), gameMode.PauseSvcIndex, v => gameMode.PauseSvcIndex = v);
            AddRow(ref y, Lang.T("set.notif"), gameMode.NotifQuiet, v => gameMode.NotifQuiet = v);
            AddRow(ref y, Lang.T("set.hz"), gameMode.HzGuard, v => gameMode.HzGuard = v);
            AddRow(ref y, Lang.T("set.dvr"), gameMode.KillGameDvr, v => gameMode.KillGameDvr = v);

            var hint = new Label();
            hint.Text = Lang.T("gm.hint");
            hint.ForeColor = Theme.Faint;
            hint.BackColor = Theme.Bg;
            hint.Font = Theme.UI(8.25f, false);
            hint.SetBounds(Theme.S(16), y + Theme.S(6), Theme.S(428), Theme.S(32));

            var btnOk = new PillButton(Lang.T("btn.done"), BtnKind.Primary);
            btnOk.SetBounds(Theme.S(344), y + Theme.S(44), Theme.S(100), Theme.S(32));
            btnOk.Click += (s, e) => DialogResult = DialogResult.OK;

            Controls.Add(title);
            Controls.Add(lblClose);
            Controls.Add(hint);
            Controls.Add(btnOk);
            MouseDown += DragMove;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Enter) DialogResult = DialogResult.OK; };
        }

        private void AddRow(ref int y, string text, bool value, Action<bool> apply)
        {
            var sw = new Toggle();
            sw.Text = text;
            sw.SetBounds(Theme.S(20), y, Theme.S(424), Theme.S(30));
            sw.SetSilently(value);
            sw.CheckedChanged += (s, e) => apply(sw.Checked);
            Controls.Add(sw);
            y += Theme.S(36);
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000 ; return cp; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.StrokeHi))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.RoundCorners(Handle);
        }

        private void DragMove(object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                Native.ReleaseCapture();
                Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HT_CAPTION, IntPtr.Zero);
            }
        }
    }
}
