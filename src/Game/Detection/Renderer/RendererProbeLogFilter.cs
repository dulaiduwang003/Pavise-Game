namespace PaviseApp
{
    // 同一候选连续得到完全相同的探测结论（典型情形是探测通道次数耗尽后的复查）时不逐条重复记录
    // 结论变化立即记录 重复期间每五分钟补记一次 被省略的次数随下一条写出 返回 null 表示本条不写
    internal static class RendererProbeLogFilter
    {
        private static readonly object gate = new object();
        private static string last;
        private static long lastAtMs;
        private static int repeats;

        internal static string Filter(string line, long nowMs)
        {
            lock (gate)
            {
                bool same = line == last;
                if (same && nowMs - lastAtMs < 300000) { repeats++; return null; }
                string text = repeats == 0 ? line
                    : line + (same ? Logger.Text("；此前相同结论 ", "; same verdict repeated ") : Logger.Text("；上一结论另有 ", "; previous verdict repeated ")) + repeats + Logger.Text(" 次未重复记录", " more times without being logged");
                last = line; lastAtMs = nowMs; repeats = 0;
                return text;
            }
        }
    }
}
