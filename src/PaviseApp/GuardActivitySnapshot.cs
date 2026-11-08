using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class GuardActivitySnapshot
    {
        internal readonly long Session;
        internal readonly KeyValuePair<int, long>[] Processes;
        internal readonly string[] Services;
        internal readonly long CapturedTicks;
        internal readonly bool Pending;
        internal readonly long ForegroundVersion;
        internal readonly bool SmartSwitchedOut;
        internal readonly bool BackgroundReleasePending;

        internal GuardActivitySnapshot(long session, KeyValuePair<int, long>[] processes, string[] services, bool pending = false,
            long foregroundVersion = 0, bool smartSwitchedOut = false, bool backgroundReleasePending = false)
        {
            Session = session; Processes = processes ?? new KeyValuePair<int, long>[0];
            Services = services ?? new string[0]; Pending = pending; CapturedTicks = DateTime.UtcNow.Ticks;
            ForegroundVersion = foregroundVersion; SmartSwitchedOut = smartSwitchedOut; BackgroundReleasePending = backgroundReleasePending;
        }

        // Success comes from executor receipts; identity/liveness use the existing scan, never a new UI scan.
        internal static GuardActivitySnapshot Create(long session, ProcessSnapshot scan,
            IEnumerable<KeyValuePair<int, long>> receipts, IEnumerable<string> stoppedServices,
            long foregroundVersion = 0, bool smartSwitchedOut = false, bool backgroundReleasePending = false)
        {
            var services = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string service in stoppedServices) if (!string.IsNullOrWhiteSpace(service)) services.Add(service);
            var serviceNames = new string[services.Count]; services.CopyTo(serviceNames);
            if (scan == null || DateTime.UtcNow.Ticks - scan.TakenTicks > TimeSpan.FromSeconds(15).Ticks)
                return new GuardActivitySnapshot(session, null, serviceNames, true, foregroundVersion, smartSwitchedOut, backgroundReleasePending);
            var byPid = new Dictionary<int, KeyValuePair<int, long>>();
            foreach (KeyValuePair<int, long> receipt in receipts)
            {
                ProcEntry live = scan.Find(receipt.Key);
                if (live == null || receipt.Value <= 0 || live.Creation != receipt.Value) continue;
                byPid[receipt.Key] = receipt;
            }
            var processes = new List<KeyValuePair<int, long>>(byPid.Values);
            processes.Sort((a, b) => a.Key.CompareTo(b.Key));
            return new GuardActivitySnapshot(session, processes.ToArray(), serviceNames, false,
                foregroundVersion, smartSwitchedOut, backgroundReleasePending);
        }

        internal string Summary
        {
            get
            {
                if (SmartSwitchedOut)
                    return (BackgroundReleasePending
                        ? PerformancePolicy.Text("检测到切出游戏，正在释放后台压制…", "Game switched out; background release pending")
                        : PerformancePolicy.Text("检测到切出游戏，已释放后台压制", "Game switched out; background suppression released"))
                        + PerformancePolicy.Text(" · 已暂停 ", " · paused ") + Services.Length
                        + PerformancePolicy.Text(" 项服务", " services");
                if (Pending) return PerformancePolicy.Text("正在读取本局执行结果…", "Reading this session's actions…");
                return PerformancePolicy.Text("已压制 ", "Suppressed ") + Processes.Length
                    + PerformancePolicy.Text(" 个进程 · 已暂停 ", " processes · paused ") + Services.Length
                    + PerformancePolicy.Text(" 项服务", " services");
            }
        }

    }
}
