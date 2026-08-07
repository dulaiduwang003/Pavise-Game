// @author bdth 2074055628@qq.com
// 文件用途 构建显卡页 逐游戏的驱动项与全局呈现路径开关

using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private Toggle swGpu, swNvMax, swNvLowLat, swNvBg, swWindowedOpt;
        private Toggle swNvRebar, swNvAnsel, swNvBatt;
        private Toggle swAmdAlag, swAmdEsync, swAmdRis;
        private TierPicker frlPicker, dlssPicker, amdChillPicker;
        private PillButton btnAmdCache;

        private void BuildGraphicsPage()
        {
            int y = PageHeader(pageGraphics, Lang.T("nav.graphics"), Lang.T("v16.graphics.sub"), 2);

            var scroll = new DBPanel();
            scroll.SetBounds(Theme.S(20), Theme.S(y), Theme.S(PageW - 40), Theme.S(PageH - y - 8));
            scroll.BackColor = Theme.Bg;
            scroll.AutoScroll = true;
            Native.Dark(scroll);
            pageGraphics.Controls.Add(scroll);

            int sy = 2, cardH;
            Section(scroll, Lang.T("sec.pergame"), 6, sy); sy += 24;

            swGpu = MakeSwitch(gameMode.GpuHighPerf, null);
            swGpu.CheckedChanged += (s, e) => gameMode.GpuHighPerf = swGpu.Checked;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.gpu"), Lang.T("set.gpu.n"), swGpu, out cardH);
            sy += cardH + 8;

            bool nvOk = NvApi.Available;
            string nvNone = Lang.T("set.nv.none");

            swNvMax = MakeSwitch(gameMode.NvMaxPerf, null);
            swNvMax.CheckedChanged += (s, e) => gameMode.NvMaxPerf = swNvMax.Checked;
            swNvMax.Enabled = nvOk;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvmax"),
                nvOk ? Lang.T("set.nvmax.n") : nvNone, swNvMax, out cardH);
            sy += cardH + 8;

            swNvLowLat = MakeSwitch(gameMode.NvLowLatency, null);
            swNvLowLat.CheckedChanged += (s, e) => gameMode.NvLowLatency = swNvLowLat.Checked;
            swNvLowLat.Enabled = nvOk;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvll"),
                nvOk ? Lang.T("set.nvll.n") : nvNone, swNvLowLat, out cardH);
            sy += cardH + 8;

            frlPicker = new TierPicker();
            frlPicker.Size = new Size(Theme.S(270), Theme.S(28));
            frlPicker.Labels = new[] { Lang.T("frl.off"), "60", "120", "240", Lang.T("frl.screen") };
            frlPicker.Index = FrlIndexOf(gameMode.NvFrlMode);
            frlPicker.IndexChanged = delegate(int i) { gameMode.NvFrlMode = FrlModeOf(i); };
            frlPicker.Enabled = nvOk;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvfrl"),
                nvOk ? Lang.T("set.nvfrl.n") : nvNone, frlPicker, out cardH);
            sy += cardH + 8;

            bool dlssOk = nvOk && NvDrsTweaks.DlssOverrideSupported();
            dlssPicker = new TierPicker();
            dlssPicker.Size = new Size(Theme.S(270), Theme.S(28));
            dlssPicker.Labels = new[] { Lang.T("frl.off"), Lang.T("dlss.latest"), "J", "K" };
            dlssPicker.Index = DlssIndexOf(gameMode.NvDlssMode);
            dlssPicker.IndexChanged = delegate(int i) { gameMode.NvDlssMode = DlssModeOf(i); };
            dlssPicker.Enabled = dlssOk;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvdlss"),
                !nvOk ? nvNone : dlssOk ? Lang.T("set.nvdlss.n") : Lang.T("set.nvdlss.old"), dlssPicker, out cardH);
            sy += cardH + 8;

            string rebarDesc = Lang.T("set.nvrebar.n");
            if (nvOk)
            {
                bool rebarOn;
                ulong rebarWindow;
                string rebarGpu;
                if (RebarProbe.TryDetect(out rebarOn, out rebarWindow, out rebarGpu))
                    rebarDesc += Lang.F(rebarOn ? "set.nvrebar.det.on" : "set.nvrebar.det.off",
                        RebarProbe.WindowText(rebarWindow));
            }
            swNvRebar = MakeSwitch(gameMode.NvRebar, null);
            swNvRebar.CheckedChanged += (s, e) => gameMode.NvRebar = swNvRebar.Checked;
            swNvRebar.Enabled = nvOk;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvrebar"),
                nvOk ? rebarDesc : nvNone, swNvRebar, out cardH);
            sy += cardH + 8;

            swNvAnsel = MakeSwitch(gameMode.NvAnselOff, null);
            swNvAnsel.CheckedChanged += (s, e) => gameMode.NvAnselOff = swNvAnsel.Checked;
            swNvAnsel.Enabled = nvOk;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvansel"),
                nvOk ? Lang.T("set.nvansel.n") : nvNone, swNvAnsel, out cardH);
            sy += cardH + 8;

            swNvBatt = MakeSwitch(gameMode.NvBattFull, null);
            swNvBatt.CheckedChanged += (s, e) => gameMode.NvBattFull = swNvBatt.Checked;
            swNvBatt.Enabled = nvOk;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvbatt"),
                nvOk ? Lang.T("set.nvbatt.n") : nvNone, swNvBatt, out cardH);
            sy += cardH + 8;

            sy += 10;
            Section(scroll, Lang.T("sec.gfx.session"), 6, sy); sy += 24;

            swNvBg = MakeSwitch(gameMode.NvBgFrl, null);
            swNvBg.CheckedChanged += (s, e) => gameMode.NvBgFrl = swNvBg.Checked;
            swNvBg.Enabled = nvOk;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvbg"),
                nvOk ? Lang.T("set.nvbg.n") : nvNone, swNvBg, out cardH);
            sy += cardH + 8;

            sy += 10;
            Section(scroll, Lang.T("sec.graphics.present"), 6, sy); sy += 24;

            swWindowedOpt = MakeSwitch(WindowedOptTweak.EnabledByPavise || WindowedOptTweak.CurrentlyOn(), OnWindowedOptToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.winopt"), Lang.T("set.winopt.n"), swWindowedOpt, out cardH);
            sy += cardH + 8;

            sy += 10;
            Section(scroll, Lang.T("sec.amd"), 6, sy); sy += 24;

            bool amdOk = AdlxTweaks.Available;
            string amdNone = Lang.T("set.amd.none");

            swAmdAlag = MakeSwitch(gameMode.AmdAntiLag, null);
            swAmdAlag.CheckedChanged += (s, e) => gameMode.AmdAntiLag = swAmdAlag.Checked;
            swAmdAlag.Enabled = amdOk;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.alag"),
                amdOk ? Lang.T("set.alag.n") : amdNone, swAmdAlag, out cardH);
            sy += cardH + 8;

            amdChillPicker = new TierPicker();
            amdChillPicker.Size = new Size(Theme.S(270), Theme.S(28));
            amdChillPicker.Labels = new[] { Lang.T("frl.off"), "60", "120", "240", Lang.T("frl.screen") };
            amdChillPicker.Index = FrlIndexOf(gameMode.AmdChillMode);
            amdChillPicker.IndexChanged = delegate(int i) { gameMode.AmdChillMode = FrlModeOf(i); };
            amdChillPicker.Enabled = amdOk;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.achill"),
                amdOk ? Lang.T("set.achill.n") : amdNone, amdChillPicker, out cardH);
            sy += cardH + 8;

            swAmdEsync = MakeSwitch(gameMode.AmdEnhSync, null);
            swAmdEsync.CheckedChanged += (s, e) => gameMode.AmdEnhSync = swAmdEsync.Checked;
            swAmdEsync.Enabled = amdOk;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.aesync"),
                amdOk ? Lang.T("set.aesync.n") : amdNone, swAmdEsync, out cardH);
            sy += cardH + 8;

            swAmdRis = MakeSwitch(gameMode.AmdRis, null);
            swAmdRis.CheckedChanged += (s, e) => gameMode.AmdRis = swAmdRis.Checked;
            swAmdRis.Enabled = amdOk;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.aris"),
                amdOk ? Lang.T("set.aris.n") : amdNone, swAmdRis, out cardH);
            sy += cardH + 8;

            btnAmdCache = new PillButton(Lang.T("amd.cache.btn"));
            btnAmdCache.Size = new Size(Theme.S(150), Theme.S(32));
            btnAmdCache.Enabled = amdOk;
            btnAmdCache.Click += OnAmdCacheClick;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.acache"),
                amdOk ? Lang.T("set.acache.n") : amdNone, btnAmdCache, out cardH);
            sy += cardH + 8;
        }

        private void OnAmdCacheClick(object sender, EventArgs e)
        {
            btnAmdCache.Enabled = false;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                int done;
                bool ok = false;
                try { ok = AdlxTweaks.ResetShaderCacheAll(out done); }
                catch { done = 0; }
                try
                {
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        btnAmdCache.Enabled = AdlxTweaks.Available;
                        if (ok) PaviseDialog.Success(this, App.DisplayName, Lang.T("amd.cache.done"));
                        else PaviseDialog.Warn(this, App.DisplayName, Lang.T("amd.cache.fail"));
                    }));
                }
                catch { }
            });
        }

        internal static int DlssIndexOf(string mode)
        {
            return mode == "latest" ? 1 : mode == "j" ? 2 : mode == "k" ? 3 : 0;
        }

        internal static string DlssModeOf(int index)
        {
            return index == 1 ? "latest" : index == 2 ? "j" : index == 3 ? "k" : "off";
        }

        internal static int FrlIndexOf(string mode)
        {
            return mode == "60" ? 1 : mode == "120" ? 2 : mode == "240" ? 3
                : mode == "screen" ? 4 : 0;
        }

        internal static string FrlModeOf(int index)
        {
            return index == 1 ? "60" : index == 2 ? "120" : index == 3 ? "240"
                : index == 4 ? "screen" : "off";
        }

        private void OnWindowedOptToggle(object s, EventArgs e)
        {
            bool ok = swWindowedOpt.Checked ? WindowedOptTweak.Enable() : WindowedOptTweak.Restore();
            if (!ok) PaviseDialog.Warn(this, App.DisplayName, Lang.T("winopt.failed"));
            swWindowedOpt.SetSilently(WindowedOptTweak.EnabledByPavise || WindowedOptTweak.CurrentlyOn());
        }

        private void SyncGraphicsToggles()
        {
            if (swGpu != null) swGpu.SetSilently(gameMode.GpuHighPerf);
            if (swNvMax != null) swNvMax.SetSilently(gameMode.NvMaxPerf);
            if (swNvLowLat != null) swNvLowLat.SetSilently(gameMode.NvLowLatency);
            if (swNvBg != null) swNvBg.SetSilently(gameMode.NvBgFrl);
            if (frlPicker != null) frlPicker.Index = FrlIndexOf(gameMode.NvFrlMode);
            if (dlssPicker != null) dlssPicker.Index = DlssIndexOf(gameMode.NvDlssMode);
            if (swNvRebar != null) swNvRebar.SetSilently(gameMode.NvRebar);
            if (swNvAnsel != null) swNvAnsel.SetSilently(gameMode.NvAnselOff);
            if (swNvBatt != null) swNvBatt.SetSilently(gameMode.NvBattFull);
            if (swAmdAlag != null) swAmdAlag.SetSilently(gameMode.AmdAntiLag);
            if (amdChillPicker != null) amdChillPicker.Index = FrlIndexOf(gameMode.AmdChillMode);
            if (swAmdEsync != null) swAmdEsync.SetSilently(gameMode.AmdEnhSync);
            if (swAmdRis != null) swAmdRis.SetSilently(gameMode.AmdRis);
            if (swWindowedOpt != null)
                swWindowedOpt.SetSilently(WindowedOptTweak.EnabledByPavise || WindowedOptTweak.CurrentlyOn());
        }
    }
}
