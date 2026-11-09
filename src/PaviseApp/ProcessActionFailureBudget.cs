using System;
using System.Collections.Generic;

namespace PaviseApp
{
    // 采样或写入失败与正常未命中分别计预算；缓存淘汰不能退还写入次数。
    internal sealed class ProcessActionFailureBudget
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, int> failures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static string Key(int pid, long birth, string path, string stage)
        { return pid + ":" + birth + "|" + path + "|" + stage; }
        internal bool Allowed(int pid, long birth, string path, string stage)
        {
            lock (gate) { int count; return !failures.TryGetValue(Key(pid, birth, path, stage), out count) || count < 3; }
        }
        internal int Failed(int pid, long birth, string path, string stage)
        {
            lock (gate)
            {
                string key = Key(pid, birth, path, stage); int count;
                failures.TryGetValue(key, out count);
                failures[key] = count = Math.Min(3, count + 1);
                return count;
            }
        }
        internal void Succeeded(int pid, long birth, string path, string stage)
        { lock (gate) failures.Remove(Key(pid, birth, path, stage)); }
    }
}
