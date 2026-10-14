// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static partial class Program
    {
        internal static bool IsAutostart(string[] args)
        {
            if (args != null)
                foreach (string arg in args)
                    if (string.Equals(arg, TaskHelper.AutostartArgument, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static bool ShouldSignalExistingPanel(bool autoStarted) { return !autoStarted; }

        internal static bool RecreateSingleInstanceMutex(ref Mutex mutex, string name)
        {
            // 先关闭旧单实例句柄，再创建替代实例。
            if (mutex != null) { mutex.Dispose(); mutex = null; }
            bool created = false;
            try { mutex = new Mutex(true, name, out created); }
            catch { created = false; }
            return created;
        }

        internal static bool ShouldShowStartupPanel(bool autoStarted, bool requested, long requestTicks, long nowTicks)
        {
            if (!autoStarted) return true;
            return requested && requestTicks > 0 && nowTicks >= requestTicks
                && nowTicks - requestTicks <= 30L * TimeSpan.TicksPerSecond;
        }

        internal static bool ShouldShowPanelForMissingTray(bool autoStarted, bool showingPanel)
        { return !autoStarted || showingPanel; }

        private static void SetPendingPanelRequest(bool requested)
        {
            Settings.SaveStr(PendingPanelTicksKey, requested
                ? DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
            Settings.Save(PendingPanelKey, requested);
        }

        private static void SignalShowPanel()
        {
            try
            {
                using (var show = EventWaitHandle.OpenExisting("Global\\Pavise_ShowPanel"))
                {
                    show.Set();
                    return;
                }
            }
            catch { }
            if (IsElevated()) return;
            try
            {
                if (!TaskHelper.TaskExists()) return;
                SetPendingPanelRequest(true);
                if (TaskHelper.Run("/Run /TN " + TaskHelper.TaskName) != 0)
                    SetPendingPanelRequest(false);
            }
            catch { }
        }

        private static EventWaitHandle CreateSignalEvent(string name)
        {
            try
            {
                var sec = new EventWaitHandleSecurity();
                sec.AddAccessRule(new EventWaitHandleAccessRule(
                    WindowsIdentity.GetCurrent().User,
                    EventWaitHandleRights.FullControl,
                    AccessControlType.Allow));
                bool createdNew;
                return new EventWaitHandle(false, EventResetMode.AutoReset, name, out createdNew, sec);
            }
            catch { }
            try { return new EventWaitHandle(false, EventResetMode.AutoReset, name); }
            catch { return null; }
        }

        internal static int CompareVersions(string left, string right)
        {
            Version a, b;
            if (!Version.TryParse(NormalizeVersion(left), out a)) a = new Version(0, 0);
            if (!Version.TryParse(NormalizeVersion(right), out b)) b = new Version(0, 0);
            return a.CompareTo(b);
        }

        private static string NormalizeVersion(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "0.0.0.0";
            string text = raw.Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);
            int parts = text.Split('.').Length;
            for (int i = parts; i < 4; i++) text += ".0";
            return text;
        }

        private static bool TryReplaceOlderInstance()
        {
            Process older = null;
            try
            {
                int self, session;
                using (Process current = Process.GetCurrentProcess()) { self = current.Id; session = current.SessionId; }
                foreach (Process p in Process.GetProcesses())
                {
                    if (p.Id == self) { p.Dispose(); continue; }
                    string version = null;
                    try
                    {
                        if (p.SessionId == session)
                        {
                            FileVersionInfo info = p.MainModule.FileVersionInfo;
                            if (IsOlderPaviseInstance(info.ProductName, info.CompanyName, info.FileVersion)) version = info.FileVersion;
                        }
                    }
                    catch { }
                    if (version != null && CompareVersions(App.Version, version) > 0 && older == null) older = p;
                    else p.Dispose();
                }
                if (older == null) return false;

                try { using (var exit = EventWaitHandle.OpenExisting("Global\\Pavise_Exit")) exit.Set(); }
                catch { return false; }
                if (!older.WaitForExit(20000)) return false;
                return true;
            }
            catch { return false; }
            finally { if (older != null) older.Dispose(); }
        }

        internal static bool IsOlderPaviseInstance(string product, string company, string version)
        {
            Version parsed;
            return string.Equals(product, "Pavise Auto", StringComparison.OrdinalIgnoreCase)
                && string.Equals(company, App.Author, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(version) && Version.TryParse(NormalizeVersion(version), out parsed)
                && CompareVersions(App.Version, version) > 0;
        }

        // 最低系统构建为 19041，旧版缺少所需调度接口。
        internal const int OsBuildBaseline = 19041;
        internal const int OsBuildBest = 26100;

        internal static bool OsBelowBaseline()
        {
            int build = Native.OsBuild();
            return build > 0 && build < OsBuildBaseline;
        }

        // 系统版本检查放在旧改动恢复之后，避免阻断恢复入口。
        private static bool BlockIfOsTooOld(string dataDir)
        {
            int build = Native.OsBuild();
            if (!OsBelowBaseline()) return false;
            Logger.Log(Lang.T("log.program.11") + build);
            int restored = 0;
            try
            {
                List<string> failed = LegacyPurge.RestorePersistent(dataDir);
                restored = failed == null ? 1 : (failed.Count == 0 ? 1 : 2);
            }
            catch { restored = 2; }
            string body = Lang.F("os.old.body", build, OsBuildBaseline, OsBuildBest);
            if (restored == 2) body += Lang.T("os.old.restorefail");
            PaviseDialog.Warn(null, App.DisplayName, body);
            return true;
        }

        private static bool IsElevated()
        {
            try
            {
                using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        internal static bool TryResetUserData(string dir, Func<bool> stopRuntime,
            out int files, out string failure, Action<string> progress = null)
        {
            files = 0;
            failure = null;
            if (string.IsNullOrWhiteSpace(dir))
            {
                failure = Lang.T("wipe.pathfail");
                return false;
            }
            try
            {
                if (stopRuntime == null || !stopRuntime())
                {
                    failure = Lang.T("wipe.stopfail");
                    return false;
                }
            }
            catch (Exception ex)
            {
                failure = Lang.T("wipe.stopfail") + " (" + ex.GetType().Name + ")";
                return false;
            }
            try
            {
                return LegacyPurge.WipeAll(dir, true, Lang.T("t.panelformsettingspage.3"),
                    out files, out failure, progress);
            }
            catch (Exception ex)
            {
                failure = Lang.T("wipe.cleanupfail") + " (" + ex.GetType().Name + ")";
                return false;
            }
        }

        internal sealed class RuntimeUiDispatcher : IDisposable
        {
            private readonly Control control = new Control();
            private readonly int ownerThread = Thread.CurrentThread.ManagedThreadId;
            private volatile bool closed;
            internal RuntimeUiDispatcher() { GC.KeepAlive(control.Handle); }
            internal bool IsOwnerThread { get { return Thread.CurrentThread.ManagedThreadId == ownerThread; } }
            internal bool TrySend(Action action, int timeoutMs = 5000)
            {
                if (closed) return false;
                if (IsOwnerThread) { action(); return true; }
                object pending = new object();
                bool done = false, expired = false;
                Exception error = null;
                if (!Post(delegate
                {
                    lock (pending) if (expired) return;
                    try { action(); }
                    catch (Exception failure) { error = failure; }
                    finally { lock (pending) { done = true; Monitor.PulseAll(pending); } }
                })) return false;
                var watch = Stopwatch.StartNew();
                lock (pending)
                {
                    while (!done)
                    {
                        int remaining = (int)Math.Max(0L, timeoutMs - watch.ElapsedMilliseconds);
                        if (remaining == 0) { expired = true; return false; }
                        Monitor.Wait(pending, remaining);
                    }
                }
                if (error != null) throw new InvalidOperationException("UI shutdown dispatch failed", error);
                return true;
            }
            internal bool Post(Action action)
            {
                try
                {
                    if (closed || control.IsDisposed || !control.IsHandleCreated) return false;
                    control.BeginInvoke((MethodInvoker)delegate { if (!closed && !control.IsDisposed) action(); });
                    return true;
                }
                catch { return false; }
            }
            public void Dispose() { closed = true; control.Dispose(); }
        }

#if PAVISE_SELFTEST || PAVISE_PERFLAB
        internal static Action<Thread> ShutdownThreadStartForTest;
#endif
        internal static bool StartShutdownWorker(Action work, Action<Exception> failed)
        {
            Action<Exception> report = delegate(Exception error) { try { if (failed != null) failed(error); } catch { } };
            try
            {
                var thread = new Thread(delegate() { try { work(); } catch (Exception error) { report(error); } });
                thread.IsBackground = true;
                thread.SetApartmentState(ApartmentState.STA);
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                if (ShutdownThreadStartForTest != null) ShutdownThreadStartForTest(thread);
                else thread.Start();
#else
                thread.Start();
#endif
                return true;
            }
            catch (Exception error) { report(error); return false; }
        }

    }
}
