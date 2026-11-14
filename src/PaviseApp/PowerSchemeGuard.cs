using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PaviseApp
{
    // 监视电源方案存储变更，及时触发已有核对入口。
    // 写回由电源应用门禁串行执行，会话、失败预算和恢复仍由原控制器管理。
    internal sealed class PowerSchemeGuard : IDisposable
    {
        internal interface IWatch : IDisposable
        {
            // 登记下一次变更通知；登记之前已经发生的改动也会触发。
            bool Arm();
            WaitHandle Changed { get; }
        }

        // 先合并成批写入；持续冲突时退避至 15 秒，安静一分钟后恢复最快响应。
        internal const int QuietDelayMs = 250, MaxDelayMs = 15000, ContentionWindowMs = 60000;
        // 监视登记累计失败三次后停止，成功不退还次数；例行核对继续。
        internal const int WatchFailureLimit = 3;

        private readonly Func<IWatch> open;
        private readonly Action signal;
        private readonly Func<long> corrections;
        private readonly Func<long> clock;
        private readonly Func<WaitHandle, int, bool> pause;
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private readonly Thread thread;
        private volatile int delay = QuietDelayMs;
        private volatile int watchFailures;
        private volatile bool watching;

        internal int CurrentDelayMs { get { return delay; } }
        internal int WatchFailures { get { return watchFailures; } }
        internal bool Watching { get { return watching; } }

        internal static PowerSchemeGuard Start(Action signal)
        {
            return new PowerSchemeGuard(RegistryWatch.Open, signal, () => PowerPlan.ExternalDriftCorrectionTotal,
                () => Environment.TickCount & int.MaxValue);
        }

        // pause 供台架替换等待，返回 true 表示收到停止信号。
        internal PowerSchemeGuard(Func<IWatch> open, Action signal, Func<long> corrections, Func<long> clock,
            Func<WaitHandle, int, bool> pause = null)
        {
            this.open = open; this.signal = signal; this.corrections = corrections; this.clock = clock;
            this.pause = pause ?? ((handle, milliseconds) => handle.WaitOne(milliseconds));
            thread = new Thread(Run) { IsBackground = true, Name = "Pavise power guard" };
            thread.Start();
        }

        private void Run()
        {
            IWatch watch = null;
            long seen = corrections(), lastCorrection = long.MinValue;
            bool announced = false;
            try
            {
                while (!stop.WaitOne(0))
                {
                    bool armed = false;
                    try
                    {
                        if (watch == null) watch = open();
                        armed = watch != null && watch.Arm();
                    }
                    catch { }
                    if (!armed)
                    {
                        watching = false;
                        if (watch != null) { try { watch.Dispose(); } catch { } watch = null; }
                        int failures = ++watchFailures;
                        if (failures >= WatchFailureLimit)
                        {
                            Logger.Warn("电源守护：监视登记失败 " + failures + "/" + WatchFailureLimit + "，已停止即时监视；外部改动仍由 15 秒例行核对写回");
                            return;
                        }
                        if (pause(stop, 1000 * failures)) return;
                        continue;
                    }
                    watching = true;
                    if (!announced)
                    {
                        announced = true;
                        Logger.Info("电源守护：已开始监视电源方案存储；持有电源计划期间的外部改动会立即触发核对与写回");
                    }
                    if (WaitHandle.WaitAny(new[] { stop, watch.Changed }) == 0) return;
                    long now = clock(), total = corrections();
                    bool quiet = lastCorrection == long.MinValue || now < lastCorrection || now - lastCorrection > ContentionWindowMs;
                    if (total != seen)
                    {
                        // 一分钟内再次确认写回时，增加冲突退避。
                        delay = quiet ? QuietDelayMs : Math.Min(delay * 2, MaxDelayMs);
                        seen = total; lastCorrection = now;
                    }
                    else if (quiet) delay = QuietDelayMs;
                    if (pause(stop, delay)) return;
                    try { signal(); }
                    catch (Exception error) { Logger.RepeatedError("power-scheme-guard", "电源守护：触发核对未完成： " + error.Message); }
                }
            }
            finally
            {
                watching = false;
                if (watch != null) { try { watch.Dispose(); } catch { } }
            }
        }

        public void Dispose()
        {
            stop.Set();
            if (Thread.CurrentThread != thread && thread.Join(2000)) stop.Dispose();
        }

        private sealed class RegistryWatch : IWatch
        {
            [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
            private static extern int RegOpenKeyEx(UIntPtr root, string path, int options, int access, out IntPtr key);
            [DllImport("advapi32.dll")]
            private static extern int RegNotifyChangeKeyValue(IntPtr key, bool subtree, uint filter, SafeWaitHandle signal, bool asynchronous);
            [DllImport("advapi32.dll")]
            private static extern int RegCloseKey(IntPtr key);

            private readonly ManualResetEvent changed = new ManualResetEvent(false);
            private IntPtr key;

            internal static IWatch Open()
            {
                IntPtr key;
                // 此子树包含方案参数和活动方案，仅申请读取与通知权限。
                int code = RegOpenKeyEx(new UIntPtr(0x80000002u), @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes", 0, 0x20019, out key);
                if (code != 0) throw new InvalidOperationException("电源方案存储打开失败 code=" + code);
                return new RegistryWatch { key = key };
            }

            public WaitHandle Changed { get { return changed; } }

            public bool Arm()
            {
                changed.Reset();
                // 子项增删 | 数值写入 | 通知不随登记线程退出而失效。
                return RegNotifyChangeKeyValue(key, true, 0x10000005u, changed.SafeWaitHandle, true) == 0;
            }

            public void Dispose()
            {
                if (key != IntPtr.Zero) { RegCloseKey(key); key = IntPtr.Zero; }
                changed.Dispose();
            }
        }
    }
}
