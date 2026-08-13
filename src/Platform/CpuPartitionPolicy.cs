// @author bdth 2074055628@qq.com
// 文件用途 只负责 CPU 分区决策 不读取硬件也不调用 Windows API

using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class CpuPartitionPolicy
    {
        public static ulong StrictMask(ulong all, ulong background, ulong performance, ulong cache)
        {
            ulong preferred = cache != 0 ? cache
                : (performance != 0 ? performance : (all & ~background));
            preferred &= all;
            return preferred != 0 ? preferred : all;
        }

        public static int BackgroundCoreCount(int physicalCoreCount)
        {
            if (physicalCoreCount <= 6) return 0;
            if (physicalCoreCount <= 10) return 1;
            return Math.Min(4, Math.Max(2, physicalCoreCount / 8));
        }

        public const int SqueezeMinPhysical = 5;

        public static ulong SqueezeMask(ulong[] physicalCores, ulong allowedMask, ulong effMask, bool hybrid)
        {
            return SqueezeMask(physicalCores, allowedMask, effMask, hybrid, null, 0);
        }

        public static ulong SqueezeMask(ulong[] physicalCores, ulong allowedMask, ulong effMask,
            bool hybrid, ulong[] l3Domains, ulong gameMask)
        {
            if (physicalCores == null || allowedMask == 0) return 0;
            int physical = physicalCores.Length;
            if (physical < SqueezeMinPhysical) return 0;
            int reserve = physical <= 8 ? 1 : 2;

            var pool = new List<ulong>();
            if (hybrid && effMask != 0)
                foreach (ulong core in physicalCores)
                    if ((core & allowedMask) == core && (core & effMask) == core) pool.Add(core);
            if (pool.Count < reserve)
            {
                pool.Clear();
                foreach (ulong core in physicalCores)
                    if ((core & allowedMask) == core) pool.Add(core);
            }
            if (pool.Count < reserve) return 0;

            List<ulong> picked = PreferCleanCacheDomain(pool, l3Domains, gameMask, reserve);
            picked.Sort();

            ulong mask = 0;
            for (int i = picked.Count - reserve; i < picked.Count; i++) if (i >= 0) mask |= picked[i];
            mask &= allowedMask;
            if (mask == 0 || mask == allowedMask) return 0;
            if (BitCount(mask) * 2 >= BitCount(allowedMask)) return 0;
            return mask;
        }

        internal static List<ulong> PreferCleanCacheDomain(List<ulong> pool, ulong[] l3Domains,
            ulong gameMask, int reserve)
        {
            if (l3Domains == null || l3Domains.Length < 2 || pool.Count <= reserve) return pool;
            List<ulong> best = null;
            foreach (ulong domain in l3Domains)
            {
                if (domain == 0 || (domain & gameMask) != 0) continue;
                var inside = new List<ulong>();
                foreach (ulong core in pool) if ((core & domain) == core) inside.Add(core);
                if (inside.Count < reserve) continue;
                if (best == null || inside.Count < best.Count) best = inside;
            }
            return best ?? pool;
        }

        internal static int BitCount(ulong mask)
        {
            int n = 0;
            while (mask != 0) { mask &= mask - 1; n++; }
            return n;
        }

        public static double CoreInterruptRate(double[] rates, ulong coreMask)
        {
            if (rates == null || coreMask == 0) return 0;
            double peak = 0;
            for (int i = 0; i < rates.Length && i < 64; i++)
                if (((coreMask >> i) & 1UL) != 0 && rates[i] > peak) peak = rates[i];
            return peak;
        }

        public struct CoreDesc
        {
            public ulong Mask;
            public int L3;
            public int Die;
            public int Eff;
            public uint L3Size;
        }

        public struct CorePlan
        {
            public ulong Game;
            public ulong Background;
            public ulong Interrupt;
            public ulong AltGame;
            public ulong AltBackground;
            public ulong AltInterrupt;
            public int GameDomain;
            public int AltDomain;
            public bool Partitioned;
            public string Tag;
        }

        public const int IsolateCore0MinPhysical = 8;
        public const int MinGameDomainCores = 6;

        public static CorePlan Decide(CoreDesc[] cores, ulong allMask)
        {
            var plan = new CorePlan
            {
                Game = allMask, Background = 0, Interrupt = 0,
                GameDomain = -1, AltDomain = -1, Partitioned = false, Tag = "flat"
            };
            if (cores == null || cores.Length == 0 || allMask == 0) return plan;

            int minEff = int.MaxValue, maxEff = int.MinValue;
            foreach (CoreDesc c in cores) { if (c.Eff < minEff) minEff = c.Eff; if (c.Eff > maxEff) maxEff = c.Eff; }
            if (maxEff > minEff)
            {
                ulong perf = 0, eff = 0;
                foreach (CoreDesc c in cores) { if (c.Eff == maxEff) perf |= c.Mask; else eff |= c.Mask; }
                if (perf != 0 && eff != 0)
                {
                    ulong top = 0; int taken = 0;
                    for (int i = 63; i >= 0 && taken < 2; i--)
                    {
                        ulong bit = 1UL << i;
                        if ((perf & bit) != 0) { top |= bit; taken++; }
                    }
                    plan.Game = perf; plan.Background = eff; plan.Interrupt = top != 0 ? top : eff;
                    plan.Partitioned = true; plan.Tag = "hybrid-pe";
                    return plan;
                }
            }

            var cacheMask = new Dictionary<int, ulong>();
            var cacheSize = new Dictionary<int, uint>();
            foreach (CoreDesc c in cores)
                if (c.L3 >= 0 && c.L3Size > 0)
                {
                    ulong m; cacheMask.TryGetValue(c.L3, out m); cacheMask[c.L3] = m | c.Mask;
                    cacheSize[c.L3] = c.L3Size;
                }

            ulong core0 = 0;
            foreach (CoreDesc c in cores)
                if ((c.Mask & 1UL) != 0) { core0 = c.Mask; break; }

            if (cacheMask.Count >= 2)
            {
                uint maxSz = 0, minSz = uint.MaxValue;
                foreach (uint s in cacheSize.Values) { if (s > maxSz) maxSz = s; if (s < minSz) minSz = s; }
                if (maxSz > minSz && maxSz > 0)
                {
                    ulong big = 0, rest = 0;
                    foreach (var kv in cacheMask)
                    {
                        if (cacheSize[kv.Key] == maxSz) big |= kv.Value; else rest |= kv.Value;
                    }
                    if (big != 0 && rest != 0)
                    {
                        plan.Game = big; plan.Background = rest;
                        plan.Interrupt = TopCoreMask(cores, rest);
                        plan.AltGame = rest; plan.AltBackground = big;
                        plan.AltInterrupt = TopCoreMask(cores, big);
                        plan.Partitioned = true; plan.Tag = "x3d-cache";
                        return plan;
                    }
                }

            }

            var dieMask = new Dictionary<int, ulong>();
            var dieCores = new Dictionary<int, int>();
            foreach (CoreDesc c in cores)
                if (c.Die >= 0)
                {
                    ulong m; dieMask.TryGetValue(c.Die, out m); dieMask[c.Die] = m | c.Mask;
                    int n; dieCores.TryGetValue(c.Die, out n); dieCores[c.Die] = n + 1;
                }
            if (dieMask.Count >= 2)
            {
                int selected = -1, selectedCores = -1;
                foreach (var kv in dieCores)
                    if (kv.Value > selectedCores || kv.Value == selectedCores && (selected < 0 || kv.Key < selected))
                    {
                        selected = kv.Key;
                        selectedCores = kv.Value;
                    }
                ulong game = selected >= 0 ? dieMask[selected] & allMask : 0;
                ulong background = allMask & ~game;
                if (game != 0 && background != 0 && selectedCores >= MinGameDomainCores)
                {
                    plan.Game = game; plan.Background = background;
                    plan.Interrupt = TopCoreMask(cores, background);
                    plan.Partitioned = true; plan.Tag = "symmetric-ccd";
                    plan.GameDomain = selected;
                    int alt = -1, altCores = -1;
                    foreach (var kv in dieCores)
                        if (kv.Key != selected
                            && (kv.Value > altCores || kv.Value == altCores && (alt < 0 || kv.Key < alt)))
                        {
                            alt = kv.Key;
                            altCores = kv.Value;
                        }
                    if (alt >= 0 && altCores >= MinGameDomainCores)
                    {
                        ulong altGame = dieMask[alt] & allMask;
                        ulong altBackground = allMask & ~altGame;
                        if (altGame != 0 && altBackground != 0)
                        {
                            plan.AltGame = altGame; plan.AltBackground = altBackground;
                            plan.AltInterrupt = TopCoreMask(cores, altBackground);
                            plan.AltDomain = alt;
                        }
                    }
                    return plan;
                }
            }

            if (cores.Length >= IsolateCore0MinPhysical && core0 != 0)
            {
                int reserve = Math.Max(1, BackgroundCoreCount(cores.Length));
                var others = new List<ulong>();
                foreach (CoreDesc c in cores) if (c.Mask != core0) others.Add(c.Mask);
                others.Sort();
                ulong bg = core0;
                for (int i = 0; i < reserve - 1 && others.Count - i - 1 >= 0; i++)
                    bg |= others[others.Count - i - 1];
                ulong game = allMask & ~bg;
                if (game != 0)
                {
                    plan.Game = game; plan.Background = bg;
                    plan.Interrupt = TopCoreMask(cores, bg);
                    plan.Partitioned = true; plan.Tag = "pool-iso-core0";
                    return plan;
                }
            }

            int tail = BackgroundCoreCount(cores.Length);
            if (tail > 0)
            {
                var list = new List<ulong>();
                foreach (CoreDesc c in cores) list.Add(c.Mask);
                list.Sort();
                ulong bg = 0;
                for (int i = list.Count - tail; i < list.Count; i++) if (i >= 0) bg |= list[i];
                ulong game = allMask & ~bg;
                if (bg != 0 && game != 0)
                {
                    plan.Game = game; plan.Background = bg;
                    plan.Interrupt = TopCoreMask(cores, bg);
                    plan.Partitioned = true; plan.Tag = "tail-reserve";
                    return plan;
                }
            }

            return plan;
        }

        private static ulong TopCoreMask(CoreDesc[] cores, ulong within)
        {
            ulong best = 0;
            foreach (CoreDesc c in cores)
                if (c.Mask != 0 && (c.Mask & within) == c.Mask && c.Mask > best) best = c.Mask;
            return best != 0 ? best : within;
        }
    }
}
