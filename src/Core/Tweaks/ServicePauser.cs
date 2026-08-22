// @author bdth 2074055628@qq.com
// 文件用途 会话期间暂停一组服务并在结束后恢复的通用引擎 供索引预取与 Windows 更新两组复用
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class ServicePauser
    {
        private readonly string[] names;
        private readonly string flag;
        private readonly bool onlyWhenRunning;
        private readonly object lk = new object();
        private bool active;

        public ServicePauser(string[] serviceNames, string flagKey, bool onlyWhenRunning)
        {
            names = serviceNames;
            flag = flagKey;
            this.onlyWhenRunning = onlyWhenRunning;
        }

        public bool HasResidue { get { return Settings.LoadStr(flag, "").Length > 0; } }

        private List<string> Owned()
        {
            var list = new List<string>();
            foreach (string s in Settings.LoadStr(flag, "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
                list.Add(s);
            return list;
        }

        public bool Activate(out List<string> justStopped, out List<string> confirmedStopped, out bool ledgerLost)
        {
            justStopped = new List<string>();
            confirmedStopped = new List<string>();
            ledgerLost = false;
            lock (lk)
            {
                if (active) return true;

                List<string> owned = Owned();
                var intent = new List<string>(owned);

                foreach (string n in names)
                {
                    try
                    {
                        int before = SvcState.Query(n);
                        if (onlyWhenRunning && before != 4) continue;
                        if (before == 4 && !intent.Contains(n))
                        {
                            intent.Add(n);
                            Settings.SaveStr(flag, string.Join("|", intent.ToArray()));
                        }
                        bool confirmedStop;
                        bool issued = SvcCtl.StopIfRunning(n, out confirmedStop);
                        if (!confirmedStop && before == 4 && SvcState.StopTaken(SvcState.Query(n)))
                            confirmedStop = true;
                        if (issued || confirmedStop) justStopped.Add(n);
                        if (confirmedStop) confirmedStopped.Add(n);
                    }
                    catch { }
                }

                foreach (string n in justStopped)
                    if (!owned.Contains(n)) owned.Add(n);

                if (owned.Count > 0 || intent.Count > 0)
                {
                    string joined = string.Join("|", owned.ToArray());
                    Settings.SaveStr(flag, joined);
                    if (Settings.LoadStr(flag, "") != joined)
                    {
                        foreach (string n in justStopped) SvcCtl.EnsureStarted(n);
                        ledgerLost = true;
                        active = false;
                        return false;
                    }
                }
                active = true;
                return true;
            }
        }

        public bool Restore(out List<string> remain)
        {
            remain = new List<string>();
            lock (lk)
            {
                string raw = Settings.LoadStr(flag, "");
                bool had = raw.Length > 0;
                if (had)
                {
                    foreach (string n in raw.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        bool ok = false;
                        try { ok = SvcCtl.EnsureStarted(n); } catch { }
                        if (!ok) remain.Add(n);
                    }
                    Settings.SaveStr(flag, string.Join("|", remain.ToArray()));
                }
                active = false;
                return Settings.LoadStr(flag, "").Length == 0;
            }
        }

        public bool HadLedger { get { return Settings.LoadStr(flag, "").Length > 0; } }
    }
}
