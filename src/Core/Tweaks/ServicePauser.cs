// @author bdth 2074055628@qq.com
// 文件用途 会话期间暂停一组服务并在结束后恢复的通用引擎 供索引预取与 Windows 更新两组复用
//
// 为什么抽出来
//   SvcPause 与 UpdatePause 的账目逻辑逐行同构：先把"打算停"的名字落盘(intent)
//   再停 再把"真停下了"的名字落盘(owned) 落盘失败就立刻把停掉的重新拉起来。
//   这套先记账后动手的次序是崩溃续还原的前提 不能有两份各自演化的实现。
//   两边真正的差异只有三处：服务名、落盘键、以及要不要跳过本来就没在跑的服务。
//   日志文案留在各自文件里 因为两组面向用户的说法不同。
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

        // onlyWhenRunning 为真时只碰当前正在运行的服务 停着的一概不动
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
                        // 先把意图落盘 万一停完就崩 下次启动照样知道该把谁拉回来
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
                        // 账记不下就别留下改动 立刻把刚停的拉回来
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
