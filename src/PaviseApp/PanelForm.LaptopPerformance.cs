using System;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private int laptopPerformanceProbeBusy;

        private void AddLaptopPerformanceToggle(Control parent, ref int y)
        {
            AddManualPerformanceToggle(parent, ref y, LaptopPerfMode.PreferenceKey,
                AutoText("笔记本厂商性能模式", "Laptop vendor performance mode"),
                AutoText("兼容的联想/华硕笔记本插电对局时切厂商性能档，退局恢复原档；用户或厂商中途改档则让出。电源选“不切换”时不接管。可能增加发热和风扇噪声。",
                    "On supported Lenovo/ASUS laptops, switches the vendor profile during AC-powered matches and restores it afterwards. Yields to user/vendor changes. Skips Don't switch. Heat and fan noise may increase."),
                () => gameMode.LaptopPerformanceEnabled,
                value => gameMode.TrySetLaptopPerformanceEnabled(value),
                () => gameMode.LaptopPerformanceSupportKnown && gameMode.LaptopPerformanceSupported,
                null, () => gameMode.LaptopPerformanceStatus);
            QueueLaptopPerformanceProbe();
        }

        private void QueueLaptopPerformanceProbe()
        {
            if (IsDisposed || Disposing || runtimeShutdownBusy || Interlocked.CompareExchange(ref laptopPerformanceProbeBusy, 1, 0) != 0) return;
            try
            {
                QueueUiStateWork(delegate
                {
                    try { gameMode.RefreshLaptopPerformanceSupport(); }
                    catch (Exception error) { Logger.Warn("Laptop profile availability: " + error.GetType().Name); }
                    finally { Interlocked.Exchange(ref laptopPerformanceProbeBusy, 0); }
                    if (IsDisposed || Disposing || runtimeShutdownBusy || !IsHandleCreated) return;
                    try { PostUiStateResult(delegate { if (!IsDisposed && !Disposing && !runtimeShutdownBusy) RefreshAutomationPage(); }); }
                    catch (InvalidOperationException) { }
                });
            }
            catch { Interlocked.Exchange(ref laptopPerformanceProbeBusy, 0); }
        }
    }
}
