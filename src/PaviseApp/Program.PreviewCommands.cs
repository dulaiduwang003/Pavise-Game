using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class Program
    {
        private static bool TryRunPreviewCommand(string[] args)
        {
            if (args.Length > 0 && args[0] == "--genicon")
            {
                try
                {
                    IcoWriter.Save(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "Pavise.ico"), new int[8] { 16, 20, 24, 32, 48, 64, 128, 256 });
                    return true;
                }
                catch
                {
                    return true;
                }
            }
            if (args.Length >= 2 && args[0] == "--geniconpng")
            {
                PerformancePreset mode = ((args.Length >= 3 && args[2] == "extreme") ? PerformancePreset.Extreme : ((args.Length >= 3 && args[2] == "competitive") ? PerformancePreset.Competitive : ((args.Length >= 3 && args[2] == "handheld") ? PerformancePreset.Handheld : PerformancePreset.Standard)));
                try
                {
                    using (Bitmap bitmap = IconArt.Render(256, mode, true))
                    {
                        bitmap.Save(args[1], ImageFormat.Png);
                        return true;
                    }
                }
                catch
                {
                    Environment.ExitCode = 1;
                    return true;
                }
            }
            if (args.Length >= 2 && args[0] == "--screenshot")
            {
                Dpi.NoFit = true;
                Dpi.Init();
                float result;
                if (args.Length >= 6 && float.TryParse(args[5], NumberStyles.Float, CultureInfo.InvariantCulture, out result) && result >= 1f)
                {
                    Dpi.Scale = result;
                }
                Paths.Init();
                Lang.Init();
                if (args.Length >= 4)
                {
                    Lang.Cur = ((args[3] == "en") ? 1 : ((args[3] == "ja") ? 2 : 0));
                }
                if (args.Length >= 5 && (args[4] == "light" || args[4] == "backdrop-light"))
                {
                    Theme.SetLight(true);
                }
                if (args.Length >= 5 && (args[4] == "backdrop-light" || args[4] == "backdrop-dark"))
                {
                    try
                    {
                        Backdrop.Init();
                    }
                    catch
                    {
                    }
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                string dataValue = Path.Combine(Path.GetTempPath(), "PaviseShot_" + Process.GetCurrentProcess().Id);
                Directory.CreateDirectory(dataValue);
                Paths.Data = dataValue;
                Logger.LogPath = Path.Combine(dataValue, "Pavise.log");
                int pageIndex = ((args.Length >= 3) ? int.Parse(args[2]) : 0);
                SuppressionCore core = new SuppressionCore();
                Tamer t = new Tamer(core);
                try
                {
                    GameMode gameMode2 = new GameMode(dataValue, core);
                    if (pageIndex == 7)
                    {
                        Logger.Log("CORE 守护服务已开启，等待游戏进程");
                        Logger.Log("GAME 已识别 NebulaStrike-Win64-Shipping.exe");
                        Logger.Log("POWER 电源计划已生效：PG 专注 5EFC");
                        Logger.Log("SUPPRESS 后台资源边界已完成部署");
                        Logger.Log("IRQ 警告：检测到慢驱动 rtwlane.sys，单次 DPC 最长 2.1ms");
                        Logger.Log("GPU 策略写入失败：驱动接口拒绝访问");
                        Logger.Log("SESSION 游戏退出，系统状态已还原");
                    }
                    if (pageIndex == 1)
                    {
                        string nebulaStrikePath = Path.Combine(dataValue, "NebulaStrike", "Binaries", "Win64");
                        Directory.CreateDirectory(nebulaStrikePath);
                        string nebulaStrikeWin64ShippingPath = Path.Combine(nebulaStrikePath, "NebulaStrike-Win64-Shipping.exe");
                        File.Copy(Application.ExecutablePath, nebulaStrikeWin64ShippingPath, true);
                        gameMode2.AddGameExecutable("NEBULA STRIKE", nebulaStrikeWin64ShippingPath);
                    }
                    using (PanelForm panelForm = new PanelForm(t, gameMode2, IconArt.MakeIcon(Dpi.S(24)), true))
                    {
                        IntPtr handle = panelForm.Handle;
                        GC.KeepAlive(handle);
                        panelForm.RenderTo(args[1], pageIndex, args.Length >= 5 && args[4] == "anticheat", args.Length >= 5 && args[4] == "mode", (args.Length >= 5) ? args[4] : null);
                        return true;
                    }
                }
                finally
                {
                    try
                    {
                        Directory.Delete(dataValue, true);
                    }
                    catch
                    {
                    }
                }
            }
            if (args.Length >= 2 && args[0] == "--shot-contact")
            {
                Dpi.Init();
                Paths.Init();
                Lang.Init();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (ContactDialog contactDialog = new ContactDialog())
                {
                    contactDialog.StartPosition = FormStartPosition.Manual;
                    contactDialog.ShowInTaskbar = false;
                    contactDialog.Location = new Point(-20000, -20000);
                    contactDialog.Show();
                    Application.DoEvents();
                    using (Bitmap bitmap2 = new Bitmap(contactDialog.ClientSize.Width, contactDialog.ClientSize.Height))
                    {
                        contactDialog.DrawToBitmap(bitmap2, new Rectangle(0, 0, bitmap2.Width, bitmap2.Height));
                        bitmap2.Save(args[1], ImageFormat.Png);
                        return true;
                    }
                }
            }
            if (args.Length > 0 && args[0] == "--ui-preview")
            {
                Dpi.Init();
                Paths.Init();
                Lang.Init();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Logger.LogPath = Path.Combine(Paths.Data, "Pavise.preview.log");
                SuppressionCore core2 = new SuppressionCore();
                Tamer t2 = new Tamer(core2);
                GameMode gameMode3 = new GameMode(Paths.Data, core2);
                gameMode3.Enabled = Settings.Load("GameModeOn", true);
                using (Icon icon2 = IconArt.MakeMultiIcon(gameMode3.ActivePreset, gameMode3.Enabled))
                {
                    using (PanelForm panelForm2 = new PanelForm(t2, gameMode3, icon2, true))
                    {
                        panelForm2.RealExit = true;
                        panelForm2.ShowPanel();
                        Application.Run(panelForm2);
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
