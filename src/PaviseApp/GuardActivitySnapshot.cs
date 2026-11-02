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

        internal GuardActivitySnapshot(long session, KeyValuePair<int, long>[] processes, string[] services, bool pending = false)
        {
            Session = session; Processes = processes ?? new KeyValuePair<int, long>[0];
            Services = services ?? new string[0]; Pending = pending; CapturedTicks = DateTime.UtcNow.Ticks;
        }

        // The executor receipts establish success. The existing guard scan establishes
        // identity and liveness; presenting this data must never start a process scan.
        internal static GuardActivitySnapshot Create(long session, ProcessSnapshot scan,
            IEnumerable<KeyValuePair<int, long>> receipts, IEnumerable<string> stoppedServices)
        {
            if (scan == null || DateTime.UtcNow.Ticks - scan.TakenTicks > TimeSpan.FromSeconds(15).Ticks)
                return new GuardActivitySnapshot(session, null, null, true);
            var byPid = new Dictionary<int, KeyValuePair<int, long>>();
            foreach (KeyValuePair<int, long> receipt in receipts)
            {
                ProcEntry live = scan.Find(receipt.Key);
                if (live == null || receipt.Value <= 0 || live.Creation != receipt.Value) continue;
                byPid[receipt.Key] = receipt;
            }
            var processes = new List<KeyValuePair<int, long>>(byPid.Values);
            processes.Sort((a, b) => a.Key.CompareTo(b.Key));
            var services = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string service in stoppedServices) if (!string.IsNullOrWhiteSpace(service)) services.Add(service);
            var serviceNames = new string[services.Count]; services.CopyTo(serviceNames);
            return new GuardActivitySnapshot(session, processes.ToArray(), serviceNames);
        }

        internal string Summary
        {
            get
            {
                if (Pending) return PerformancePolicy.Text("正在读取本局执行结果…", "Reading this session's actions…");
                return PerformancePolicy.Text("已压制 ", "Suppressed ") + Processes.Length
                    + PerformancePolicy.Text(" 个进程 · 已暂停 ", " processes · paused ") + Services.Length
                    + PerformancePolicy.Text(" 项服务", " services");
            }
        }

    }
}
