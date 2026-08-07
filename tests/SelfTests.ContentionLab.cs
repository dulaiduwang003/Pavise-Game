// @author bdth 2074055628@qq.com
// 文件用途 用受控争抢负载量化后台压制的真实收益 每轮首尾各测一次放任段
// 增益以两次放任的均值为基线 首尾之差即本轮热漂移 移动平台上必须看这个数

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private sealed class FrameVictim
        {
            private readonly List<double> frames = new List<double>();
            private readonly object gate = new object();
            private volatile bool stop;
            private volatile bool collect;
            private Thread worker;
            private readonly List<Thread> helpers = new List<Thread>();

            private const int WorkPerFrame = 260000;

            public void Start() { Start(0); }

            public void Start(int helperThreads)
            {
                worker = new Thread(delegate ()
                {
                    var sw = new Stopwatch();
                    double sink = 0;
                    while (!stop)
                    {
                        sw.Restart();
                        for (int i = 1; i <= WorkPerFrame; i++) sink += 1.0 / i;
                        sw.Stop();
                        if (collect)
                        {
                            double ms = sw.Elapsed.TotalMilliseconds;
                            lock (gate) frames.Add(ms);
                        }
                    }
                    if (sink < 0) Console.Write("");
                });
                worker.IsBackground = true;
                worker.Priority = ThreadPriority.Normal;
                worker.Start();

                for (int t = 0; t < helperThreads; t++)
                {
                    var h = new Thread(delegate ()
                    {
                        double sink = 0;
                        while (!stop) for (int i = 1; i <= WorkPerFrame; i++) sink += 1.0 / i;
                        if (sink < 0) Console.Write("");
                    });
                    h.IsBackground = true;
                    h.Priority = ThreadPriority.Normal;
                    h.Start();
                    helpers.Add(h);
                }
            }

            public void BeginPhase() { lock (gate) frames.Clear(); collect = true; }

            public double[] EndPhase()
            {
                collect = false;
                lock (gate) return frames.ToArray();
            }

            public void Stop()
            {
                stop = true;
                if (worker != null) worker.Join(3000);
                foreach (Thread h in helpers) { try { h.Join(3000); } catch { } }
            }
        }

        private const int MinFramesForTail = 2000;

        private struct PhaseStat
        {
            public int Count;
            public double Median;
            public double P99;
            public double OnePercentLow;
        }

        private static PhaseStat Summarize(double[] samples)
        {
            var s = new PhaseStat();
            if (samples == null || samples.Length == 0) return s;
            var sorted = (double[])samples.Clone();
            Array.Sort(sorted);
            s.Count = sorted.Length;
            s.Median = sorted[sorted.Length / 2];
            s.P99 = sorted[(int)Math.Min(sorted.Length - 1, Math.Floor(sorted.Length * 0.99))];
            int worst = Math.Max(1, sorted.Length / 100);
            double sum = 0;
            for (int i = sorted.Length - worst; i < sorted.Length; i++) sum += sorted[i];
            s.OnePercentLow = sum / worst;
            return s;
        }

        private static void RunContentionLab(string output, string secondsArg, string hogsArg,
            string roundsArg, string victimThreadsArg)
        {
            int seconds, hogs, rounds, victimThreads;
            if (!int.TryParse(secondsArg ?? "", out seconds) || seconds < 5) seconds = 15;
            if (!int.TryParse(hogsArg ?? "", out hogs) || hogs < 1) hogs = Environment.ProcessorCount;
            if (!int.TryParse(roundsArg ?? "", out rounds) || rounds < 1) rounds = 5;
            if (!int.TryParse(victimThreadsArg ?? "", out victimThreads) || victimThreads < 1) victimThreads = 1;

            var sb = new StringBuilder();
            string self = Process.GetCurrentProcess().MainModule.FileName;
            var spawned = new List<Process>();
            var core = new SuppressionCore();
            var victim = new FrameVictim();

            sb.AppendLine("=== 后台压制收益实测（多轮 A/B 配对）===");
            sb.AppendLine("逻辑处理器: " + Environment.ProcessorCount
                + " | 抢占进程: " + hogs + " | 每段: " + seconds + "s | 轮数: " + rounds);
            sb.AppendLine("受害者: " + victimThreads + " 线程（1 条计帧 + "
                + (victimThreads - 1) + " 条陪跑），帧时间直接反映所得 CPU 时间片");
            double demand = (hogs + victimThreads) / (double)Environment.ProcessorCount;
            sb.AppendLine("可运行线程/逻辑处理器 = " + (hogs + victimThreads) + "/"
                + Environment.ProcessorCount + " = " + demand.ToString("F2") + "x"
                + (demand < 1.5 ? "  ← 超额不足，竞争可能不真实" : ""));
            sb.AppendLine();

            var lowGains = new List<double>();
            var medGains = new List<double>();
            var partGains = new List<double>();
            var freezeGains = new List<double>();
            var freezeMedGains = new List<double>();
            var quotaMedGains = new List<double>();
            var quotaLowGains = new List<double>();
            var quotaVsFreezeMed = new List<double>();
            var quotaVsPriMed = new List<double>();
            var comboLowVsIso = new List<double>();
            var comboMedVsPri = new List<double>();
            var comboLowVsQuota = new List<double>();
            var q1MedVsPri = new List<double>();
            var q1LowVsPri = new List<double>();
            var pinMedVsPri = new List<double>();
            var pinLowVsIso = new List<double>();
            var pinLowVsQuota = new List<double>();
            var pinOnlyLow = new List<double>();
            var pinOnlyMed = new List<double>();
            var quotaNetLow = new List<double>();
            var quotaNetMed = new List<double>();
            var pinNetLow = new List<double>();
            var driftMed = new List<double>();
            var driftLow = new List<double>();
            Process selfProc = Process.GetCurrentProcess();
            IntPtr origAffinity = selfProc.ProcessorAffinity;
            ulong pinMask = CpuTopology.StrictBoostMask;
            bool canPin = pinMask != 0;
            var quota = new JobQuota();
            try
            {
                for (int i = 0; i < hogs; i++)
                {
                    var psi = new ProcessStartInfo(self, "--cpu-burn")
                    { UseShellExecute = false, CreateNoWindow = true };
                    spawned.Add(Process.Start(psi));
                }
                Thread.Sleep(1500);
                victim.Start(victimThreads - 1);
                Thread.Sleep(2000);

                int joined = 0;
                if (quota.Open())
                    foreach (Process p in spawned) if (quota.Add(p.Id)) joined++;
                sb.AppendLine("硬配额作业: 已纳入 " + joined + "/" + spawned.Count
                    + (joined == spawned.Count ? "" : " —— " + quota.LastError));

                sb.AppendLine("本机分区: 后台核 " + MaskText(CpuTopology.ThrottleMask)
                    + " | 竞技游戏核 " + MaskText(CpuTopology.StrictBoostMask)
                    + " | 分区可用=" + CpuTopology.HasSafeBackgroundPartition());
                sb.AppendLine();
                sb.AppendLine("轮次  段            帧数     中位ms   p99ms    1%最差ms");
                for (int r = 1; r <= rounds; r++)
                {
                    PhaseStat a = RunPhase(victim, seconds);
                    sb.AppendLine(Row(r, "A 放任      ", a));

                    int nPri = Apply(core, spawned, SuppressionLevel.Restrained);
                    Thread.Sleep(1200);
                    PhaseStat b = RunPhase(victim, seconds);
                    sb.AppendLine(Row(r, "B 仅降优先级", b));
                    core.ReleaseReason(SuppressReason.Background);
                    Thread.Sleep(1200);

                    int nIso = Apply(core, spawned, SuppressionLevel.Isolated);
                    Thread.Sleep(1200);
                    PhaseStat c = RunPhase(victim, seconds);
                    sb.AppendLine(Row(r, "C 降级+分区 ", c));
                    core.ReleaseReason(SuppressReason.Background);
                    Thread.Sleep(1200);

                    int nFrz = Apply(core, spawned, SuppressionLevel.Frozen);
                    Thread.Sleep(1200);
                    bool reallyFrozen = VerifyFrozen(spawned);
                    PhaseStat d = RunPhase(victim, seconds);
                    sb.AppendLine(Row(r, "D 冻结" + (reallyFrozen ? "(已验证)" : "(未生效!)"), d));
                    core.ReleaseReason(SuppressReason.Background);
                    Thread.Sleep(1500);

                    bool q20ok = joined == hogs && quota.SetCap(20);
                    Thread.Sleep(1200);
                    PhaseStat e = RunPhase(victim, seconds);
                    sb.AppendLine(Row(r, "E 硬配额20%" + (q20ok ? "" : "(未生效!)"), e));
                    quota.Clear();
                    Thread.Sleep(1200);

                    bool q5ok = joined == hogs && quota.SetCap(5);
                    Thread.Sleep(1200);
                    PhaseStat f = RunPhase(victim, seconds);
                    sb.AppendLine(Row(r, "F 硬配额 5%" + (q5ok ? "" : "(未生效!)"), f));
                    quota.Clear();
                    Thread.Sleep(1200);

                    int nCombo = Apply(core, spawned, SuppressionLevel.Isolated);
                    bool comboOk = joined == hogs && nCombo == hogs && quota.SetCap(5);
                    Thread.Sleep(1200);
                    PhaseStat g = RunPhase(victim, seconds);
                    sb.AppendLine(Row(r, "G 分区+配额5" + (comboOk ? "" : "(未生效!)"), g));
                    quota.Clear();
                    core.ReleaseReason(SuppressReason.Background);
                    Thread.Sleep(1200);

                    bool q1ok = joined == hogs && quota.SetCap(1);
                    Thread.Sleep(1200);
                    PhaseStat h = RunPhase(victim, seconds);
                    sb.AppendLine(Row(r, "H 硬配额 1%" + (q1ok ? "" : "(未生效!)"), h));
                    quota.Clear();
                    Thread.Sleep(1200);

                    PhaseStat iSeg = new PhaseStat(), jSeg = new PhaseStat(), kSeg = new PhaseStat();
                    bool pinOk = false, pinIsoOk = false;
                    if (canPin)
                    {
                        try
                        {
                            selfProc.ProcessorAffinity = (IntPtr)(long)pinMask;
                            pinOk = true;
                        }
                        catch { pinOk = false; }
                    }
                    if (pinOk)
                    {
                        bool qpok = joined == hogs && quota.SetCap(1);
                        Thread.Sleep(1200);
                        iSeg = RunPhase(victim, seconds);
                        sb.AppendLine(Row(r, "I 绑P核+配额" + (qpok ? "" : "(未生效!)"), iSeg));
                        quota.Clear();
                        Thread.Sleep(1200);

                        jSeg = RunPhase(victim, seconds);
                        sb.AppendLine(Row(r, "J 绑P核 放任", jSeg));
                        Thread.Sleep(1200);

                        int nPinIso = Apply(core, spawned, SuppressionLevel.Isolated);
                        Thread.Sleep(1200);
                        kSeg = RunPhase(victim, seconds);
                        sb.AppendLine(Row(r, "K 绑P核+分区" + (nPinIso == hogs ? "" : "(部分失败)"), kSeg));
                        core.ReleaseReason(SuppressReason.Background);
                        pinIsoOk = nPinIso == hogs;

                        try { selfProc.ProcessorAffinity = origAffinity; }
                        catch { }
                    }
                    else sb.AppendLine("      绑核段跳过：StrictBoostMask 不可用");
                    Thread.Sleep(1500);

                    PhaseStat a2 = RunPhase(victim, seconds);
                    sb.AppendLine(Row(r, "A'放任(轮末)", a2));
                    double baseMed = (a.Median + a2.Median) / 2.0;
                    double baseLow = (a.OnePercentLow + a2.OnePercentLow) / 2.0;
                    if (a.Median > 0 && a2.Median > 0 && a.OnePercentLow > 0 && a2.OnePercentLow > 0)
                    {
                        driftMed.Add((a2.Median - a.Median) / a.Median * 100.0);
                        driftLow.Add((a2.OnePercentLow - a.OnePercentLow) / a.OnePercentLow * 100.0);
                    }
                    if (a.Count < MinFramesForTail || a2.Count < MinFramesForTail)
                        sb.AppendLine("      ! 轮 " + r + " 放任段帧数不足（A=" + a.Count + " A'=" + a2.Count
                            + "），1% 最差帧仅由 " + Math.Max(1, Math.Min(a.Count, a2.Count) / 100)
                            + " 个样本决定，本轮百分比不可信");
                    Thread.Sleep(1500);

                    if (pinOk && iSeg.OnePercentLow > 0 && iSeg.Median > 0 && jSeg.OnePercentLow > 0
                        && b.Median > 0 && c.OnePercentLow > 0 && h.OnePercentLow > 0 && a.OnePercentLow > 0)
                    {
                        pinMedVsPri.Add((b.Median - iSeg.Median) / b.Median * 100.0);
                        pinLowVsIso.Add((c.OnePercentLow - iSeg.OnePercentLow) / c.OnePercentLow * 100.0);
                        pinLowVsQuota.Add((h.OnePercentLow - iSeg.OnePercentLow) / h.OnePercentLow * 100.0);
                        pinOnlyLow.Add((baseLow - jSeg.OnePercentLow) / baseLow * 100.0);
                        pinOnlyMed.Add((baseMed - jSeg.Median) / baseMed * 100.0);
                    }
                    if (pinIsoOk && kSeg.OnePercentLow > 0 && iSeg.OnePercentLow > 0
                        && c.OnePercentLow > 0 && kSeg.Median > 0 && iSeg.Median > 0)
                    {
                        quotaNetLow.Add((kSeg.OnePercentLow - iSeg.OnePercentLow) / kSeg.OnePercentLow * 100.0);
                        quotaNetMed.Add((kSeg.Median - iSeg.Median) / kSeg.Median * 100.0);
                        pinNetLow.Add((c.OnePercentLow - kSeg.OnePercentLow) / c.OnePercentLow * 100.0);
                    }

                    if (comboOk && c.OnePercentLow > 0 && g.OnePercentLow > 0 && g.Median > 0
                        && f.OnePercentLow > 0 && b.Median > 0)
                    {
                        comboLowVsIso.Add((c.OnePercentLow - g.OnePercentLow) / c.OnePercentLow * 100.0);
                        comboMedVsPri.Add((b.Median - g.Median) / b.Median * 100.0);
                        comboLowVsQuota.Add((f.OnePercentLow - g.OnePercentLow) / f.OnePercentLow * 100.0);
                    }
                    if (q1ok && h.OnePercentLow > 0 && h.Median > 0 && b.Median > 0 && b.OnePercentLow > 0)
                    {
                        q1MedVsPri.Add((b.Median - h.Median) / b.Median * 100.0);
                        q1LowVsPri.Add((b.OnePercentLow - h.OnePercentLow) / b.OnePercentLow * 100.0);
                    }

                    if (baseLow > 0 && baseMed > 0 && b.OnePercentLow > 0 && c.OnePercentLow > 0
                        && d.OnePercentLow > 0 && nPri == hogs && nIso == hogs && nFrz == hogs
                        && reallyFrozen && a.Count >= MinFramesForTail && a2.Count >= MinFramesForTail)
                    {
                        lowGains.Add((baseLow - b.OnePercentLow) / baseLow * 100.0);
                        medGains.Add((baseLow - c.OnePercentLow) / baseLow * 100.0);
                        partGains.Add((b.OnePercentLow - c.OnePercentLow) / b.OnePercentLow * 100.0);
                        freezeGains.Add((c.OnePercentLow - d.OnePercentLow) / c.OnePercentLow * 100.0);
                        freezeMedGains.Add((c.Median - d.Median) / c.Median * 100.0);

                        if (q5ok && f.OnePercentLow > 0 && f.Median > 0 && d.Median > 0 && b.Median > 0)
                        {
                            quotaMedGains.Add((baseMed - f.Median) / baseMed * 100.0);
                            quotaLowGains.Add((baseLow - f.OnePercentLow) / baseLow * 100.0);
                            quotaVsFreezeMed.Add((d.Median - f.Median) / d.Median * 100.0);
                            quotaVsPriMed.Add((b.Median - f.Median) / b.Median * 100.0);
                        }
                    }
                }
                sb.AppendLine();
                sb.AppendLine("=== 结论 ===");
                sb.AppendLine("--- 轮内热漂移（A' 轮末放任 相对 A 轮首放任）---");
                if (driftMed.Count < 2)
                {
                    sb.AppendLine("  样本不足，无法评估漂移。");
                }
                else
                {
                    double dMedM = Med(driftMed), dLowM = Med(driftLow);
                    sb.AppendLine("  中位帧漂移: " + dMedM.ToString("F1") + "%   1%最差帧漂移: "
                        + dLowM.ToString("F1") + "%（正数=轮末更差）");
                    sb.AppendLine("  各轮中位漂移: " + Join(driftMed));
                    if (Math.Abs(dMedM) < 3)
                        sb.AppendLine("  判定: 漂移可忽略 —— 本机在一轮内性能稳定，段序不构成系统性偏差。");
                    else if (dMedM > 3)
                        sb.AppendLine("  判定: 存在热漂移 —— 轮末比轮首慢 " + dMedM.ToString("F1")
                            + "%，越晚测的档位系统性吃亏，压制档收益被低估。"
                            + "跨机比较时必须与对方机器的漂移量一并考虑。");
                    else
                        sb.AppendLine("  判定: 反向漂移 —— 轮末反而更快 " + Math.Abs(dMedM).ToString("F1")
                            + "%，可能是预热未完成，前几轮数据需谨慎。");
                }
                sb.AppendLine();
                if (lowGains.Count < 2)
                {
                    sb.AppendLine("有效配对不足（" + lowGains.Count + "），无法判定。");
                }
                else
                {
                    sb.AppendLine("以 1% 最差帧为准，各轮改善的中位值：");
                    sb.AppendLine("  仅降优先级 vs 放任 : " + Med(lowGains).ToString("F1") + "%");
                    sb.AppendLine("  降级+分区 vs 放任  : " + Med(medGains).ToString("F1") + "%");
                    sb.AppendLine("  分区的额外贡献     : " + Med(partGains).ToString("F1") + "%");
                    sb.AppendLine();
                    sb.AppendLine("  仅降优先级各轮: " + Join(lowGains));
                    sb.AppendLine("  降级+分区各轮 : " + Join(medGains));
                    sb.AppendLine("  分区增量各轮  : " + Join(partGains));
                    sb.AppendLine();
                    int partPositive = 0;
                    foreach (double g in partGains) if (g > 0) partPositive++;
                    double partMed = Med(partGains);
                    if (partPositive == partGains.Count && partMed > 10)
                        sb.AppendLine("判定: 分区有额外收益 —— 每一轮都优于仅降优先级，中位再改善 "
                            + partMed.ToString("F0") + "%。");
                    else if (partMed < -10)
                        sb.AppendLine("判定: 分区有害 —— 把后台挤进少数核心后，受害者反而更差，"
                            + "说明本机核心数下分区的代价超过收益。");
                    else if (partPositive * 2 < partGains.Count)
                        sb.AppendLine("判定: 分区无额外收益 —— 多数轮次不优于仅降优先级，"
                            + "收益基本来自降优先级本身。");
                    else
                        sb.AppendLine("判定: 分区增量方向不稳定 —— " + partPositive + "/" + partGains.Count
                            + " 轮为正，需要更多轮次才能定论。");

                    sb.AppendLine();
                    sb.AppendLine("--- 冻结档增量（相对已隔离压制）---");
                    sb.AppendLine("  1% 最差帧再改善: " + Med(freezeGains).ToString("F1") + "%");
                    sb.AppendLine("  中位帧再改善   : " + Med(freezeMedGains).ToString("F1") + "%");
                    sb.AppendLine("  各轮: " + Join(freezeGains));
                    double fz = Med(freezeGains);
                    int fzPos = 0;
                    foreach (double g in freezeGains) if (g > 0) fzPos++;
                    double fzMed = Med(freezeMedGains);
                    int fzMedPos = 0;
                    foreach (double g in freezeMedGains) if (g > 1) fzMedPos++;
                    if (fzMedPos == freezeMedGains.Count && fzMed > 3)
                        sb.AppendLine("  判定: 冻结是唯一能改善中位帧的档位 —— 中位帧再降 "
                            + fzMed.ToString("F1") + "%（每轮一致），说明它真正释放了 CPU 周期"
                            + "而非仅调整排队顺序；尾部帧的额外收益则不稳定（" + fzPos + "/"
                            + freezeGains.Count + " 轮为正），因为隔离档已把尾部压到接近噪声底。");
                    else if (fzPos == freezeGains.Count && fz > 20)
                        sb.AppendLine("  判定: 冻结有显著额外收益，尾部中位再改善 " + fz.ToString("F0") + "%。");
                    else if (Math.Abs(fz) < 10 && Math.Abs(fzMed) < 3)
                        sb.AppendLine("  判定: 冻结相对隔离压制无显著增量 —— "
                            + "隔离档已经把争抢压到接近极限，不可逆的挂起换不到多少额外收益。");
                    else
                        sb.AppendLine("  判定: 冻结增量不稳定（尾部 " + fzPos + "/" + freezeGains.Count
                            + " 轮为正，中位 " + fzMedPos + "/" + freezeMedGains.Count + " 轮为正）。");

                    sb.AppendLine();
                    sb.AppendLine("--- 硬配额档（作业对象 CPU 上限 5%）---");
                    if (quotaMedGains.Count < 2)
                    {
                        sb.AppendLine("  有效配对不足（" + quotaMedGains.Count + "），无法判定。");
                    }
                    else
                    {
                        sb.AppendLine("  vs 放任  中位帧改善: " + Med(quotaMedGains).ToString("F1")
                            + "%   1%最差帧改善: " + Med(quotaLowGains).ToString("F1") + "%");
                        sb.AppendLine("  vs 降优先级 中位帧增量: " + Med(quotaVsPriMed).ToString("F1") + "%");
                        sb.AppendLine("  vs 冻结     中位帧差距: " + Med(quotaVsFreezeMed).ToString("F1")
                            + "%（正数表示配额更好）");
                        sb.AppendLine("  各轮中位帧改善: " + Join(quotaMedGains));
                        double qPri = Med(quotaVsPriMed);
                        double qFrz = Med(quotaVsFreezeMed);
                        int qPriPos = 0;
                        foreach (double g in quotaVsPriMed) if (g > 1) qPriPos++;
                        if (qPriPos == quotaVsPriMed.Count && qPri > 3 && Math.Abs(qFrz) < 5)
                            sb.AppendLine("  判定: 硬配额拿到了冻结那一档的中位帧收益 —— 每轮都优于仅降优先级（中位再降 "
                                + qPri.ToString("F1") + "%），与冻结的差距在 "
                                + Math.Abs(qFrz).ToString("F1") + "% 以内；它释放的是真实 CPU 周期，"
                                + "但被限进程仍在运行，没有挂起的副作用。");
                        else if (qPriPos == quotaVsPriMed.Count && qPri > 3)
                            sb.AppendLine("  判定: 硬配额优于仅降优先级（中位再降 " + qPri.ToString("F1")
                                + "%），但与冻结仍有 " + qFrz.ToString("F1") + "% 差距。");
                        else if (Math.Abs(qPri) < 3)
                            sb.AppendLine("  判定: 硬配额相对降优先级无显著增量 —— 与预期不符，"
                                + "说明配额并未真正释放周期，需检查配额是否生效。");
                        else
                            sb.AppendLine("  判定: 硬配额增量不稳定（" + qPriPos + "/"
                                + quotaVsPriMed.Count + " 轮优于降优先级）。");
                    }

                    sb.AppendLine();
                    sb.AppendLine("--- 更低配额（1%）---");
                    if (q1MedVsPri.Count < 2)
                        sb.AppendLine("  有效配对不足（" + q1MedVsPri.Count + "）。");
                    else
                    {
                        sb.AppendLine("  vs 降优先级  中位帧增量: " + Med(q1MedVsPri).ToString("F1")
                            + "%   1%最差帧增量: " + Med(q1LowVsPri).ToString("F1") + "%");
                        sb.AppendLine("  各轮尾部增量: " + Join(q1LowVsPri));
                    }

                    sb.AppendLine();
                    sb.AppendLine("--- 组合档：分区 + 配额5%（能否同时拿到中位与尾部）---");
                    if (comboLowVsIso.Count < 2)
                    {
                        sb.AppendLine("  有效配对不足（" + comboLowVsIso.Count + "），无法判定。");
                    }
                    else
                    {
                        double cMed = Med(comboMedVsPri);
                        double cLowIso = Med(comboLowVsIso);
                        double cLowQ = Med(comboLowVsQuota);
                        sb.AppendLine("  中位帧 vs 降优先级 : " + cMed.ToString("F1") + "%（配额单独为 "
                            + Med(quotaVsPriMed).ToString("F1") + "%）");
                        sb.AppendLine("  尾部帧 vs 纯分区   : " + cLowIso.ToString("F1")
                            + "%（正数表示组合更好）");
                        sb.AppendLine("  尾部帧 vs 纯配额   : " + cLowQ.ToString("F1")
                            + "%（正数表示分区救回了抖动）");
                        sb.AppendLine("  各轮尾部 vs 纯分区 : " + Join(comboLowVsIso));
                        if (cMed > 8 && cLowIso > -10)
                            sb.AppendLine("  判定: 组合成立 —— 中位帧拿到了配额那一档的收益（"
                                + cMed.ToString("F1") + "%），尾部相对纯分区只差 "
                                + Math.Abs(cLowIso).ToString("F1") + "%；分区吸收了配额的调度抖动。");
                        else if (cMed > 8 && cLowQ > 20)
                            sb.AppendLine("  判定: 分区显著缓解了配额的尾部恶化（相对纯配额改善 "
                                + cLowQ.ToString("F1") + "%），但仍不及纯分区（差 "
                                + Math.Abs(cLowIso).ToString("F1") + "%），是否划算取决于取舍。");
                        else if (cLowIso < -20)
                            sb.AppendLine("  判定: 组合不成立 —— 尾部仍比纯分区差 "
                                + Math.Abs(cLowIso).ToString("F1") + "%，分区救不回配额的抖动。");
                        else
                            sb.AppendLine("  判定: 组合效果不明确，中位增量 " + cMed.ToString("F1")
                                + "%，尾部相对纯分区 " + cLowIso.ToString("F1") + "%。");
                    }

                    sb.AppendLine();
                    sb.AppendLine("--- 受害者绑 P 核 " + MaskText(pinMask) + " ---");
                    if (pinMedVsPri.Count < 2)
                    {
                        sb.AppendLine("  有效配对不足（" + pinMedVsPri.Count + "），无法判定。");
                    }
                    else
                    {
                        double pMed = Med(pinMedVsPri);
                        double pLowIso = Med(pinLowVsIso);
                        double pLowQ = Med(pinLowVsQuota);
                        sb.AppendLine("  绑核+配额1% 中位帧 vs 降优先级: " + pMed.ToString("F1") + "%");
                        sb.AppendLine("  绑核+配额1% 尾部帧 vs 纯分区  : " + pLowIso.ToString("F1")
                            + "%（正数表示更好）");
                        sb.AppendLine("  绑核+配额1% 尾部帧 vs 纯配额  : " + pLowQ.ToString("F1")
                            + "%（正数表示绑核救回了尾部）");
                        sb.AppendLine("  仅绑核不压制 vs 放任: 中位 " + Med(pinOnlyMed).ToString("F1")
                            + "%  尾部 " + Med(pinOnlyLow).ToString("F1") + "%");
                        sb.AppendLine("  各轮尾部 vs 纯分区: " + Join(pinLowVsIso));
                        if (pMed > 8 && pLowIso > -10)
                            sb.AppendLine("  判定: E 核假设成立且组合可行 —— 把受害者钉在 P 核后，"
                                + "中位帧拿到释放周期的收益（" + pMed.ToString("F1")
                                + "%），尾部同时回到纯分区的水平（差 "
                                + Math.Abs(pLowIso).ToString("F1") + "%）。压制得狠 + 游戏钉 P 核，两者兼得。");
                        else if (pLowQ > 20)
                            sb.AppendLine("  判定: 绑核确实救回了部分尾部（相对纯配额改善 "
                                + pLowQ.ToString("F1") + "%），但仍不及纯分区（差 "
                                + Math.Abs(pLowIso).ToString("F1") + "%），E 核只解释了一部分。");
                        else
                            sb.AppendLine("  判定: E 核假设不成立 —— 绑 P 核后尾部相对纯配额只变化 "
                                + pLowQ.ToString("F1") + "%，尾部 0.75 的来源另有原因。");
                    }

                    sb.AppendLine();
                    sb.AppendLine("--- 变量隔离：配额的净贡献（I 绑核+配额 vs K 绑核+分区，只差配额）---");
                    if (quotaNetLow.Count < 2)
                    {
                        sb.AppendLine("  有效配对不足（" + quotaNetLow.Count + "），无法判定。");
                    }
                    else
                    {
                        double qnLow = Med(quotaNetLow);
                        double qnMed = Med(quotaNetMed);
                        double pnLow = Med(pinNetLow);
                        sb.AppendLine("  配额净贡献 尾部: " + qnLow.ToString("F1")
                            + "%   中位: " + qnMed.ToString("F1") + "%");
                        sb.AppendLine("  各轮尾部净贡献: " + Join(quotaNetLow));
                        sb.AppendLine("  绑核净贡献 尾部（K vs C 纯分区）: " + pnLow.ToString("F1") + "%");
                        int qnPos = 0;
                        foreach (double g in quotaNetLow) if (g > 2) qnPos++;
                        if (qnPos == quotaNetLow.Count && qnLow > 5)
                            sb.AppendLine("  判定: 硬配额有独立净贡献 —— 在同样绑核同样压制的前提下，"
                                + "加配额尾部再改善 " + qnLow.ToString("F1") + "%（每轮一致），值得落地。");
                        else if (Math.Abs(qnLow) < 3)
                            sb.AppendLine("  判定: 硬配额没有独立贡献 —— 净效果 " + qnLow.ToString("F1")
                                + "%，落在噪声内。此前 I 相对 C 的改善全部来自绑核，配额可以删掉。");
                        else if (qnLow < -5)
                            sb.AppendLine("  判定: 硬配额有害 —— 在绑核压制的基础上再加配额，尾部反而恶化 "
                                + Math.Abs(qnLow).ToString("F1") + "%。");
                        else
                            sb.AppendLine("  判定: 配额净贡献不稳定（" + qnPos + "/" + quotaNetLow.Count
                                + " 轮为正，中位 " + qnLow.ToString("F1") + "%）。");
                    }
                }
            }
            catch (Exception ex) { sb.AppendLine("异常: " + ex); }
            finally
            {
                try { core.ReleaseReason(SuppressReason.Background); } catch { }
                try { quota.Dispose(); } catch { }
                victim.Stop();
                foreach (Process p in spawned)
                {
                    try { if (!p.HasExited) p.Kill(); } catch { }
                    try { p.Dispose(); } catch { }
                }
            }

            File.WriteAllText(output, sb.ToString(), Encoding.UTF8);
            Console.Write(sb.ToString());
        }

        private static int Apply(SuppressionCore core, List<Process> targets, SuppressionLevel level)
        {
            int ok = 0;
            foreach (Process p in targets)
            {
                try
                {
                    if (core.Acquire(p.Id, p.ProcessName, SuppressReason.Background, null, level)
                        != AcquireResult.ApplyFailed) ok++;
                }
                catch { }
            }
            return ok;
        }

        private static bool VerifyFrozen(List<Process> targets)
        {
            var before = new List<TimeSpan>();
            foreach (Process p in targets)
            {
                try { p.Refresh(); before.Add(p.TotalProcessorTime); }
                catch { before.Add(TimeSpan.Zero); }
            }
            Thread.Sleep(1200);
            for (int i = 0; i < targets.Count; i++)
            {
                try
                {
                    targets[i].Refresh();
                    if ((targets[i].TotalProcessorTime - before[i]).TotalMilliseconds > 30) return false;
                }
                catch { return false; }
            }
            return true;
        }

        private static double Med(List<double> v)
        {
            var a = v.ToArray(); Array.Sort(a);
            return a.Length == 0 ? 0 : a[a.Length / 2];
        }

        private static string Join(List<double> v)
        {
            return string.Join(", ", Array.ConvertAll(v.ToArray(), g => g.ToString("F0") + "%"));
        }

        private static string MaskText(ulong mask)
        {
            var cores = new List<string>();
            for (int i = 0; i < 64; i++) if (((mask >> i) & 1UL) != 0) cores.Add(i.ToString());
            return "[" + string.Join(",", cores.ToArray()) + "]";
        }

        private static string Row(int round, string phase, PhaseStat s)
        {
            return round.ToString().PadLeft(3) + "   " + phase + "  "
                + s.Count.ToString().PadLeft(6)
                + s.Median.ToString("F2").PadLeft(9)
                + s.P99.ToString("F2").PadLeft(9)
                + s.OnePercentLow.ToString("F2").PadLeft(11);
        }

        private static PhaseStat RunPhase(FrameVictim victim, int seconds)
        {
            victim.BeginPhase();
            Thread.Sleep(seconds * 1000);
            return Summarize(victim.EndPhase());
        }

    }
}
