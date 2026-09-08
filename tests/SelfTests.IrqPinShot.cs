// @author bdth 2074055628@qq.com
// 文件用途 把设备中断挪核的选核弹窗连同每核负载热力叠加渲染成 PNG 供人工核对观感

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        // 造一台有代表性的真实对局建议设备 核对建议说明 负载热力 核类型和当前落核标记
        private static void RunIrqPinShot(string outPath)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            try { Settings.UseTransientStoreForCurrentProcess(); } catch { }
            Dpi.Init();
            Lang.Init();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Theme.SetLight(false);

            // 落核 0x3 也就是核 0 和 1 显示红点 意思是当前就压在这两个核上
            // 已写入核 0xF0 也就是核 4 到 7 预选现有设置 底部给一个合法的绿字选择
            const ulong seenMask = 0x3UL;
            const ulong selectedMask = 0xF0UL;

            var d = new IrqDevice
            {
                InstanceId = "PCI\\VEN_10DE&DEV_2504&SUBSYS_SELFTEST\\PAVISE_IRQPIN_SHOT",
                Name = "NVIDIA 网卡控制器",
                Service = "nvnet",
                Bus = "PCI",
                ClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}",
                Dpc = 48000,
                MaxUs = 1200,
                TotalUs = 320000,
                Over500Us = 640,
                Over1Ms = 210,
                SeenOnCpus = seenMask,
                MessageCount = 1,
                MultiMessageRisk = false,
                CompletionFollowsIssuer = false,
                // 保留一份已写入掩码 让矩阵有点亮的选中态
                Policy = 4,
                Mask = selectedMask,
                RebootedSincePin = true,
                Verdict = new IrqDriverVerdict { Worth = true, VersionVerified = true },
            };

            // 注入确定的每核负载 让弹窗铺满全负载区间
            //   一眼对比:繁忙核整格红色发光炸出来 空闲核冷暗
            int[] pat = { 88, 44, 8, 22, 92, 70, 55, 12, 6, 34, 61, 79, 48, 84, 27, 15 };
            var synth = new System.Collections.Generic.Dictionary<int, double>();
            for (int cpu = 0; cpu < 64; cpu++) synth[cpu] = pat[cpu % pat.Length];
            var session = new IrqPinSession { Available = true, GameName = "示例对局 / Fixture",
                StartUtcTicks = DateTime.UtcNow.Ticks, DurationSeconds = 300,
                GameMask = CpuTopology.StrictBoostMask, SeenMask = seenMask,
                MinimumCoverage = 100, MaximumCoverage = 100,
                Driver = new IrqDriverRecord { Driver = "nvnet.sys", Dpc = d.Dpc,
                    DpcMaxNs = (long)(d.MaxUs * 1000), Over500Us = d.Over500Us, Over1Ms = d.Over1Ms } };
            foreach (var load in synth) session.Loads[load.Key] = load.Value;

            var dlg = new IrqPinDialog(d, session);
            try
            {
                dlg.StartPosition = FormStartPosition.Manual;
                dlg.Location = new Point(-20000, -20000);
                dlg.Show();

                // 固定的合成会话不采实时 PDH 只等入场动画跑完
                for (int i = 0; i < 120; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(20);
                }

                using (var bmp = new Bitmap(dlg.ClientSize.Width, dlg.ClientSize.Height))
                {
                    dlg.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                    bmp.Save(outPath, ImageFormat.Png);
                    Console.Write(outPath + "  " + bmp.Width + "x" + bmp.Height
                        + "  落核 0x" + seenMask.ToString("X")
                        + "  已选核 0x" + selectedMask.ToString("X") + Environment.NewLine);
                }
                dlg.Hide();
            }
            finally { try { dlg.Dispose(); } catch { } }
            Environment.ExitCode = 0;
        }
    }
}
