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
            toggle.AccessibleDescription = AutoText("联想使用 SmartFan；ASUS 需保持 G-Helper 运行，支持其前五个配置档。插电对局切性能档，退局恢复完整原配置；已有自定义 Turbo/全速档不覆盖，途中改档则让出。电源选“不切换”时不接管。可能增加发热和风扇噪声。",
                "Lenovo uses SmartFan. ASUS requires running G-Helper and supports its first five profiles. On AC, switches to performance and restores the complete original profile after matches. Preserves custom Turbo/Full Speed and yields to external changes. Skips Don't switch. Heat and fan noise may increase.");
            toggle.CheckedChanged += delegate
            {
                bool supported = gameMode.LaptopPerformanceSupportKnown && gameMode.LaptopPerformanceSupported;
                bool saved = !supported || gameMode.TrySetLaptopPerformanceEnabled(toggle.Checked);
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
                    try { gameMode.RefreshLaptopPerformanceSupport(); }
                    catch (Exception error) { Logger.Warn("笔记本性能档可用性： " + error.GetType().Name); }
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
