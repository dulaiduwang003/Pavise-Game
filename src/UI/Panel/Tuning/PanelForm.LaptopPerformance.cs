using System;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private int laptopPerformanceProbeBusy;

        private void ConfigureLaptopPerformanceToggle()
        {
            Toggle toggle = modeFlyout.LaptopPerformance;
            toggle.AccessibleDescription = AutoText("插电对局切换性能档，退局恢复；联想使用 SmartFan，ASUS 需运行 G-Helper（前五档）。保留自定义 Turbo/全速档和手动改档；电源选“不切换”时停用。可能增加发热与风扇噪声。",
                "Uses performance mode during AC-powered matches and restores afterwards. Lenovo uses SmartFan; ASUS needs G-Helper running (first five profiles). Preserves custom Turbo/Full Speed and manual changes. Disabled with Don't switch. Heat and fan noise may increase.");
            toggle.CheckedChanged += delegate
            {
                bool supported = gameMode.LaptopPerformance.SupportKnown && gameMode.LaptopPerformance.Supported;
                bool saved = !supported || gameMode.LaptopPerformance.TrySetEnabled(toggle.Checked);
                RefreshStrategyControls();
                if (!saved) ShowManualPerformanceSaveFailure();
            };
        }

        private void QueueLaptopPerformanceProbe()
        {
            if (IsDisposed || Disposing || runtimeShutdownBusy || Interlocked.CompareExchange(ref laptopPerformanceProbeBusy, 1, 0) != 0) return;
            try
            {
                QueueUiStateWork(delegate
                {
                    try { gameMode.LaptopPerformance.RefreshSupport(); }
                    catch (Exception error) { Logger.Warn(Logger.Format("笔记本性能档可用性： {0}", "Laptop profile availability: {0}", error.GetType().Name)); }
                    finally { Interlocked.Exchange(ref laptopPerformanceProbeBusy, 0); }
                    if (IsDisposed || Disposing || runtimeShutdownBusy || !IsHandleCreated) return;
                    try { PostUiStateResult(delegate { if (!IsDisposed && !Disposing && !runtimeShutdownBusy) RefreshStrategyControls(); }); }
                    catch (InvalidOperationException) { }
                });
            }
            catch { Interlocked.Exchange(ref laptopPerformanceProbeBusy, 0); }
        }
    }
}
