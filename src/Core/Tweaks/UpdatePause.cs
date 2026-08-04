// @author bdth 2074055628@qq.com
// 文件用途 对局期间暂停 Windows 更新相关服务并在结束后恢复

using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class UpdatePause
    {
        private static readonly string[] Names = { "wuauserv", "UsoSvc" };
        private const string Flag = "PrevUpdatePaused";
        private static readonly object lk = new object();
        private static bool active;

        public static bool Activate()
        {
            lock (lk)
            {
                if (active) return true;
                var owned = new List<string>();
                foreach (string s in Settings.LoadStr(Flag, "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
                    owned.Add(s);

                var justStopped = new List<string>();
                foreach (string n in Names)
                {
                    try
                    {
                        int before = SvcState.Query(n);
                        if (before != 4) continue;
                        bool confirmedStop;
                        bool issued = SvcCtl.StopIfRunning(n, out confirmedStop);
                        if (issued || confirmedStop) justStopped.Add(n);
                    }
                    catch { }
                }
                foreach (string n in justStopped) if (!owned.Contains(n)) owned.Add(n);
                if (owned.Count > 0)
                {
                    string joined = string.Join("|", owned.ToArray());
                    Settings.SaveStr(Flag, joined);
                    if (Settings.LoadStr(Flag, "") != joined)
                    {
                        foreach (string n in justStopped) SvcCtl.EnsureStarted(n);
                        Logger.Log("更新暂停状态无法持久化，已重新启动本轮停止的服务");
                        return false;
                    }
                    if (justStopped.Count > 0)
                        Logger.Log("已暂停 Windows 更新服务：" + string.Join(" + ", justStopped.ToArray()));
                }
                active = true;
                return true;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                string flag = Settings.LoadStr(Flag, "");
                if (flag.Length > 0)
                {
                    var remain = new List<string>();
                    foreach (string n in flag.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        bool ok = false;
                        try { ok = SvcCtl.EnsureStarted(n); } catch { }
                        if (!ok) remain.Add(n);
                    }
                    Settings.SaveStr(Flag, string.Join("|", remain.ToArray()));
                    if (remain.Count == 0) Logger.Log("Windows 更新服务已恢复");
                    else Logger.Log("部分更新服务未能拉起（" + string.Join(",", remain.ToArray()) + "），标志保留待重试");
                }
                active = false;
                return Settings.LoadStr(Flag, "").Length == 0;
            }
        }

        public static void HealFromCrash() { if (Settings.LoadStr(Flag, "").Length > 0) Restore(); }
    }
}
