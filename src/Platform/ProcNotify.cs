// @author bdth 2074055628@qq.com
// 文件用途 监听系统进程启动事件并唤醒调度线程

using System;
using System.Management;
using System.Threading;

namespace AegisApp
{

    internal sealed class ProcNotify
    {
        public event Action Changed;

        private ManagementEventWatcher startW, stopW;
        private Timer coalesce;
        private int pending;
        private const int WindowMs = 200;

        public void Start()
        {
            coalesce = new Timer(OnWindow, null, Timeout.Infinite, Timeout.Infinite);
            try
            {
                startW = Watch("Win32_ProcessStartTrace");
                stopW = Watch("Win32_ProcessStopTrace");
                Logger.Log("进程事件驱动已启用（WMI ETW，" + WindowMs + "ms 合并突发），轮询降为安全兜底");
            }
            catch (Exception ex)
            {
                Kill(ref startW);
                Kill(ref stopW);
                Logger.Log("进程事件订阅失败，退回轮询：" + ex.Message);
            }
        }

        private ManagementEventWatcher Watch(string cls)
        {
            var w = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM " + cls));
            w.Scope.Options.EnablePrivileges = true;
            w.EventArrived += OnEvent;
            try { w.Start(); }
            catch { try { w.Dispose(); } catch { } throw; }
            return w;
        }

        private void OnEvent(object sender, EventArrivedEventArgs e)
        {
            try { ManagementBaseObject ne = e.NewEvent; if (ne != null) ne.Dispose(); } catch { }
            if (Interlocked.Exchange(ref pending, 1) == 0)
            {
                try { if (coalesce != null) coalesce.Change(WindowMs, Timeout.Infinite); } catch { }
            }
        }

        private void OnWindow(object state)
        {
            Interlocked.Exchange(ref pending, 0);
            Action h = Changed;
            if (h != null) { try { h(); } catch { } }
        }

        public void Stop()
        {
            Kill(ref startW);
            Kill(ref stopW);
            if (coalesce != null) { try { coalesce.Dispose(); } catch { } coalesce = null; }
        }

        private static void Kill(ref ManagementEventWatcher w)
        {
            if (w == null) return;
            try { w.Stop(); } catch { }
            try { w.Dispose(); } catch { }
            w = null;
        }
    }
}
