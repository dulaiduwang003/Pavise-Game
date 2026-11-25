using System;
using System.Globalization;
using System.Linq;

namespace PaviseApp
{
    internal enum SchedulingComparisonArm { None, Original, PlusOne, PlusTwo }

    // 只保存逐游戏策略 线程身份由运行时确认
    // 新值只能追加在末尾 持久化使用策略文本 不使用枚举数值
    // Orchestration 和 RetiredGentle 只用于旧档读取 运行时关闭
    internal enum SchedulingStrategy { Orchestration, RenderBoost, RenderCore, DependencyBoost, RenderCoreTimeCritical, GamePriority, RetiredGentle }

    internal sealed class CriticalThreadRule
    {
        internal const string Key = "GameCriticalThreadHardRuleV1";
        internal const int MaximumIsolated = 4;

        internal const string AutomaticTopology = "auto";

        internal readonly string Topology;

        internal readonly bool WorkerAvoidance;

        internal readonly bool Automatic;

        internal readonly bool ManualTarget;

        internal readonly CriticalThreadLayout Layout;

        internal bool IsUnified { get { return Layout == CriticalThreadLayout.Unified; } }

        internal bool IsUnifiedPreference { get { return IsUnified && (Automatic || IsOff); } }

        internal readonly bool IsOff;

        internal readonly SchedulingComparisonArm ComparisonArm;

        internal bool IsComparison { get { return ComparisonArm != SchedulingComparisonArm.None; } }

        internal readonly SchedulingStrategy Strategy;

        // 旧布局和手动规则由各自的字段识别
        private bool FormalStrategy { get { return IsUnified && !IsOff && !IsComparison && Automatic && !ManualTarget && Strategy != SchedulingStrategy.RetiredGentle; } }

        // 退役方案的旧编码只供档案读取和界面提示 运行副本一律关闭
        internal bool IsRetiredStrategy { get { return IsUnified && !IsOff && !IsComparison && Automatic && !ManualTarget && Strategy == SchedulingStrategy.RetiredGentle; } }

        private bool IsOrchestration { get { return FormalStrategy && Strategy == SchedulingStrategy.Orchestration; } }

        internal bool IsRetiredAutomatic { get { return IsOrchestration || !IsUnified && Automatic && !ManualTarget; } }

        internal bool IsDependencyBoost { get { return FormalStrategy && Strategy == SchedulingStrategy.DependencyBoost; } }

        internal bool IsTimeCriticalRender { get { return FormalStrategy && Strategy == SchedulingStrategy.RenderCoreTimeCritical; } }

        // 旧存档里的逐游戏“游戏优先” 只保留读取兼容 运行时按关闭处理
        internal bool IsGamePriority { get { return FormalStrategy && Strategy == SchedulingStrategy.GamePriority; } }

        // 手动亲和性 把渲染线程固定到用户选的一到四个逻辑 CPU 上 按需加整核避让
        internal bool IsManualPin { get { return IsUnified && !IsOff && !IsComparison && Automatic && ManualTarget; } }

        internal ulong ManualMask { get { ulong mask = 0; foreach (ulong target in targets) mask |= target; return IsManualPin ? mask : 0; } }

        internal bool IsRenderStrategy { get { return FormalStrategy && (Strategy == SchedulingStrategy.RenderBoost || Strategy == SchedulingStrategy.RenderCore || Strategy == SchedulingStrategy.RenderCoreTimeCritical) || IsManualPin; } }

        internal bool SupportsClearCore { get { return FormalStrategy && !IsDependencyBoost && !IsGamePriority || IsManualPin; } }

        internal bool PinsRenderThread { get { return FormalStrategy && (Strategy == SchedulingStrategy.RenderCore || Strategy == SchedulingStrategy.RenderCoreTimeCritical) || IsManualPin; } }

        // 整核避让允许移动其他游戏线程
        internal bool WritesOtherThreadRanges { get { return !IsOrchestration && ClearCore && SupportsClearCore; } }

        private readonly ulong[] targets;

        internal ulong[] Targets
        {
            get
            {
                return (ulong[])targets.Clone();
            }
        }

        internal int CandidateCount
        {
            get
            {
                return targets.Length;
            }
        }

        internal CriticalThreadRule(string topology, params ulong[] processors)
            : this(topology, false, processors)
        {
        }

        internal CriticalThreadRule(string topology, bool workerAvoidance, params ulong[] processors)
            : this(topology, workerAvoidance, CriticalThreadLayout.Compatible, processors)
        {
        }

        internal CriticalThreadRule(string topology, bool workerAvoidance, CriticalThreadLayout layout, params ulong[] processors)
        {
            RequireLayout(layout);
            if (string.IsNullOrEmpty(topology) || topology == "auto" || processors == null || processors.Length < 1 || processors.Length > MaximumIsolated || processors.Any((ulong x) => x == 0 || (x & (x - 1)) != 0) || processors.Distinct().Count() != processors.Length
                || (layout == CriticalThreadLayout.ElasticAuxiliary || layout == CriticalThreadLayout.Unified) && processors.Length != 1
                || layout == CriticalThreadLayout.Unified && !ValidTopologyText(topology))
            {
                throw new ArgumentException("Choose one to " + MaximumIsolated + " distinct logical processors");
            }
            Topology = topology;
            WorkerAvoidance = workerAvoidance;
            Layout = layout;
            targets = (ulong[])processors.Clone();
        }

        private CriticalThreadRule(bool workerAvoidance)
            : this(workerAvoidance, CriticalThreadLayout.Compatible)
        {
        }

        private CriticalThreadRule(bool workerAvoidance, CriticalThreadLayout layout)
        {
            RequireLayout(layout);
            if (layout == CriticalThreadLayout.Unified && workerAvoidance)
                throw new ArgumentException("Unified scheduling does not imply whole-core worker avoidance");
            Topology = "auto";
            WorkerAvoidance = workerAvoidance;
            Automatic = true;
            Layout = layout;
            targets = new ulong[0];
        }

        internal static CriticalThreadRule Auto(bool workerAvoidance)
        {
            return new CriticalThreadRule(workerAvoidance);
        }

        internal static CriticalThreadRule Auto(bool workerAvoidance, CriticalThreadLayout layout)
        {
            return new CriticalThreadRule(workerAvoidance, layout);
        }

        internal static CriticalThreadRule UnifiedAuto()
        {
            return new CriticalThreadRule(false, CriticalThreadLayout.Unified);
        }

        // 显式开启且没有可用规则时使用的模式
        internal static CriticalThreadRule UnifiedDefault()
        {
            return UnifiedStrategy(SchedulingStrategy.RenderCore, false);
        }

        private CriticalThreadRule(SchedulingStrategy strategy, bool clearCore)
            : this(false, CriticalThreadLayout.Unified)
        {
            if (!Enum.IsDefined(typeof(SchedulingStrategy), strategy)) throw new ArgumentException("Unknown scheduling strategy");
            Strategy = strategy;
            // 依赖协同和退役的温和提优不带清核 游戏优先旧存档里的“|x”只为原文能读回而保留 运行时不清核
            WorkerAvoidance = clearCore && strategy != SchedulingStrategy.DependencyBoost && strategy != SchedulingStrategy.RetiredGentle;
        }

        // 保留自动旧档编码
        internal static CriticalThreadRule UnifiedStrategy(SchedulingStrategy strategy, bool clearCore)
        {
            return strategy == SchedulingStrategy.Orchestration && !clearCore ? UnifiedAuto() : new CriticalThreadRule(strategy, clearCore);
        }

        private CriticalThreadRule(SchedulingComparisonArm arm)
            : this(false, CriticalThreadLayout.Unified)
        {
            if (arm != SchedulingComparisonArm.Original && arm != SchedulingComparisonArm.PlusOne && arm != SchedulingComparisonArm.PlusTwo)
                throw new ArgumentException("Choose an explicit priority comparison arm");
            ComparisonArm = arm;
        }

        internal static CriticalThreadRule UnifiedComparison(SchedulingComparisonArm arm)
        { return new CriticalThreadRule(arm); }

        private CriticalThreadRule()
        {
            Topology = AutomaticTopology;
            Layout = CriticalThreadLayout.Unified;
            IsOff = true;
            targets = new ulong[0];
        }

        internal static CriticalThreadRule UnifiedOff() { return new CriticalThreadRule(); }

        internal static CriticalThreadRule UnifiedManual(string topology, ulong processor)
        {
            return new CriticalThreadRule(topology, processor, CriticalThreadLayout.Unified);
        }

        internal static CriticalThreadRule UnifiedManual(string topology, ulong processor, bool clearCore)
        {
            return clearCore ? new CriticalThreadRule(topology, processor, new ClearCoreTag()) : UnifiedManual(topology, processor);
        }

        private struct ClearCoreTag { }

        private CriticalThreadRule(string topology, ulong processor, ClearCoreTag tag)
            : this(topology, true, CriticalThreadLayout.Unified, new ulong[] { processor })
        {
            if (topology != topology.ToUpperInvariant()) throw new ArgumentException("Noncanonical processor topology");
            Automatic = true;
            ManualTarget = true;
        }

        private struct ManualSetTag { }

        // 手动选了多个逻辑 CPU 渲染线程只在这几个 CPU 之间运行 编码仍是一个十六进制掩码 单个 CPU 时与旧编码相同
        private CriticalThreadRule(string topology, ulong[] processors, bool clearCore, ManualSetTag tag)
        {
            if (string.IsNullOrEmpty(topology) || topology == AutomaticTopology || topology != topology.ToUpperInvariant() || !ValidTopologyText(topology)
                || processors == null || processors.Length < 2 || processors.Length > MaximumIsolated
                || processors.Any(x => x == 0 || (x & (x - 1)) != 0) || processors.Distinct().Count() != processors.Length)
                throw new ArgumentException("Choose two to " + MaximumIsolated + " distinct logical processors");
            Topology = topology;
            WorkerAvoidance = clearCore;
            Layout = CriticalThreadLayout.Unified;
            Automatic = true;
            ManualTarget = true;
            targets = processors.OrderBy(x => x).ToArray();
        }

        // 按掩码建立手动规则 一个 CPU 走单核规则 多个走多核集合
        internal static CriticalThreadRule UnifiedManualSet(string topology, ulong mask, bool clearCore)
        {
            var bits = new System.Collections.Generic.List<ulong>();
            for (int index = 0; index < 64; index++) if ((mask & (1UL << index)) != 0) bits.Add(1UL << index);
            if (bits.Count == 1) return UnifiedManual(topology, bits[0], clearCore);
            return new CriticalThreadRule(topology, bits.ToArray(), clearCore, new ManualSetTag());
        }

        // 清核开关 只对统一规则有意义 旧布局的避让标志不算 旧手动规则的“|x”也按清核读取 游戏优先的旧“|x”不算
        internal bool ClearCore { get { return IsUnified && WorkerAvoidance && !IsOff && !IsComparison && !IsGamePriority; } }

        private CriticalThreadRule(string topology, ulong processor, bool manualTarget)
            : this(topology, processor, CriticalThreadLayout.Compatible)
        {
        }

        private CriticalThreadRule(string topology, ulong processor, CriticalThreadLayout layout)
            : this(topology, layout != CriticalThreadLayout.Unified, layout, new ulong[] { processor })
        {
            if (layout == CriticalThreadLayout.ElasticAuxiliary) throw new ArgumentException("Elastic auxiliary placement requires automatic primary selection");
            if (topology != topology.ToUpperInvariant()) throw new ArgumentException("Noncanonical processor topology");
            Automatic = true;
            ManualTarget = true;
        }

        internal static CriticalThreadRule Manual(string topology, ulong processor)
        {
            return new CriticalThreadRule(topology, processor, true);
        }

        internal static CriticalThreadRule Manual(string topology, ulong processor, CriticalThreadLayout layout)
        {
            return new CriticalThreadRule(topology, processor, layout);
        }

        private static void RequireLayout(CriticalThreadLayout layout)
        {
            if (!Enum.IsDefined(typeof(CriticalThreadLayout), layout)) throw new ArgumentException("Unknown thread layout");
        }

        internal static string ProcessorName(ulong processor)
        {
            if (processor == 0 || (processor & (processor - 1)) != 0) return "CPU —";
            int index = 0;
            while ((processor >>= 1) != 0) index++;
            return "CPU" + index.ToString(CultureInfo.InvariantCulture);
        }

        internal static CriticalThreadRule ParseForRuntime(string text)
        {
            CriticalThreadRule rule = Parse(text);
            if (rule != null && !rule.IsUnified && rule.IsRetiredAutomatic) return UnifiedOff();
            // 运行只接收统一规则 旧格式解析仅供原有恢复收据核验使用
            if (rule == null || !rule.IsUnifiedPreference) return null;
            // 退役方案只保留读取兼容
            if (rule.IsComparison || rule.IsRetiredStrategy || rule.IsOrchestration) return UnifiedOff();
            // 只调优先级的旧方案（游戏优先 渲染优先 依赖协同）没有放置内容 运行副本关闭
            if (rule.IsGamePriority || rule.IsDependencyBoost || rule.IsRenderStrategy && !rule.PinsRenderThread) return UnifiedOff();
            // 旧的专核强化按渲染单核加整核避让运行
            if (rule.IsTimeCriticalRender) return UnifiedStrategy(SchedulingStrategy.RenderCore, true);
            // 手动规则原样运行 运行时再按当前拓扑和游戏范围核验
            return rule;
        }

        internal string Encode()
        {
            if (IsUnified)
            {
                if (IsOff) return "7|off";
                if (IsComparison) return "7|compare|" + (ComparisonArm == SchedulingComparisonArm.Original ? "original"
                    : ComparisonArm == SchedulingComparisonArm.PlusOne ? "1" : "2");
                // 清核作为末尾的可选字段 没有它的旧规则原样有效
                string clear = WorkerAvoidance ? "|x" : "";
                if (ManualTarget) return "7|manual|" + Topology + "|" + targets.Aggregate(0UL, (sum, item) => sum | item).ToString("X", CultureInfo.InvariantCulture) + clear;
                if (Automatic) return "7|" + StrategyToken(Strategy) + clear;
                return "8|" + Topology + "|" + targets[0].ToString("X", CultureInfo.InvariantCulture) + clear;
            }
            if (Layout != CriticalThreadLayout.Compatible)
            {
                string layout = ((int)Layout).ToString(CultureInfo.InvariantCulture);
                string workers = WorkerAvoidance ? "1" : "0";
                if (ManualTarget) return "5|" + Topology + "|" + targets[0].ToString("X", CultureInfo.InvariantCulture) + "|" + layout + "|" + workers;
                if (Automatic) return "4|auto|" + layout + "|" + workers;
                return "6|" + Topology + "|" + string.Join(",", targets.Select((ulong x) => x.ToString("X", CultureInfo.InvariantCulture))) + "|" + layout + "|" + workers;
            }
            if (ManualTarget)
            {
                return "3|" + Topology + "|" + targets[0].ToString("X", CultureInfo.InvariantCulture) + "|1";
            }
            if (Automatic)
            {
                return "2|auto|" + (WorkerAvoidance ? "1" : "0");
            }
            return "1|" + Topology + "|" + string.Join(",", targets.Select((ulong x) => x.ToString("X", CultureInfo.InvariantCulture))) + "|" + (WorkerAvoidance ? "1" : "0");
        }

        internal bool Admits(CriticalThreadRule bound)
        {
            if (IsOff || IsComparison || IsDependencyBoost || bound == null || bound.IsOff || bound.Automatic || bound.Layout != Layout)
            {
                return false;
            }
            if (!Automatic)
            {
                return bound.Encode() == Encode();
            }
            if (IsUnified)
                return bound.WorkerAvoidance == (ManualTarget && WorkerAvoidance) && bound.CandidateCount == 1
                    && (!ManualTarget || bound.Topology == Topology && bound.targets[0] == targets[0]);
            if (ManualTarget)
            {
                return bound.Topology == Topology && (bound.CandidateCount == 1 || Layout == CriticalThreadLayout.IsolatedHotspots)
                    && bound.targets[0] == targets[0]
                    && bound.WorkerAvoidance == WorkerAvoidance;
            }
            return bound.WorkerAvoidance == WorkerAvoidance
                && ((Layout != CriticalThreadLayout.CacheLocal && Layout != CriticalThreadLayout.ElasticAuxiliary) || bound.CandidateCount == 1);
        }

        internal static CriticalThreadRule Parse(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 1250)
            {
                return null;
            }
            try
            {
                string[] parts = text.Split('|');
                if (parts[0] == "7" || parts[0] == "8")
                {
                    CriticalThreadRule unified;
                    SchedulingStrategy strategy;
                    if (parts.Length == 2 && parts[0] == "7" && parts[1] == "off") unified = UnifiedOff();
                    else if ((parts.Length == 2 || parts.Length == 3 && parts[2] == "x") && parts[0] == "7" && TryStrategy(parts[1], out strategy))
                        unified = UnifiedStrategy(strategy, parts.Length == 3);
                    else if (parts.Length == 3 && parts[0] == "7" && parts[1] == "compare"
                        && (parts[2] == "original" || parts[2] == "1" || parts[2] == "2"))
                        unified = UnifiedComparison(parts[2] == "original" ? SchedulingComparisonArm.Original
                            : parts[2] == "1" ? SchedulingComparisonArm.PlusOne : SchedulingComparisonArm.PlusTwo);
                    else if ((parts.Length == 4 || parts.Length == 5 && parts[4] == "x") && parts[0] == "7" && parts[1] == "manual" && ValidTopologyText(parts[2]))
                        unified = UnifiedManualSet(parts[2], Hex(parts[3]), parts.Length == 5);
                    else if ((parts.Length == 3 || parts.Length == 4 && parts[3] == "x") && parts[0] == "8" && ValidTopologyText(parts[1]))
                        unified = new CriticalThreadRule(parts[1], parts.Length == 4, CriticalThreadLayout.Unified, Hex(parts[2]));
                    else return null;
                    return unified.Encode() == text ? unified : null;
                }
                if ((parts.Length == 4 && parts[0] == "4") || (parts.Length == 5 && (parts[0] == "5" || parts[0] == "6")))
                {
                    int layoutIndex = parts[0] == "4" ? 2 : 3;
                    string layoutText = parts[layoutIndex];
                    string workerText = parts[layoutIndex + 1];
                    if ((layoutText != "1" && layoutText != "2" && layoutText != "3") || (workerText != "0" && workerText != "1")) return null;
                    CriticalThreadLayout layout = (CriticalThreadLayout)(layoutText[0] - '0');
                    CriticalThreadRule current;
                    if (parts[0] == "4")
                    {
                        if (parts[1] != AutomaticTopology) return null;
                        current = Auto(workerText == "1", layout);
                    }
                    else
                    {
                        if (!ValidTopologyText(parts[1])) return null;
                        current = parts[0] == "5" ? Manual(parts[1], Hex(parts[2]), layout)
                            : new CriticalThreadRule(parts[1], workerText == "1", layout, parts[2].Split(',').Select(Hex).ToArray());
                    }
                    return current.Encode() == text ? current : null;
                }
                if (parts.Length == 3 && parts[0] == "2")
                {
                    return (parts[1] == "auto" && (parts[2] == "0" || parts[2] == "1")) ? new CriticalThreadRule(parts[2] == "1") : null;
                }
                if (parts.Length != 4 || (parts[0] != "1" && parts[0] != "3") || (parts[3] != "0" && parts[3] != "1"))
                {
                    return null;
                }
                string[] parts2 = parts[1].Split(',');
                if (parts2.Length < 2 || parts2.Length > 65)
                {
                    return null;
                }
                string[] textEntries = parts2;
                foreach (string entryText in textEntries)
                {
                    if (Hex(entryText) == 0)
                    {
                        return null;
                    }
                }
                CriticalThreadRule criticalThreadRule = parts[0] == "3"
                    ? Manual(parts[1], Hex(parts[2]))
                    : new CriticalThreadRule(parts[1], parts[3] == "1", parts[2].Split(',').Select(Hex).ToArray());
                return (criticalThreadRule.Encode() == text) ? criticalThreadRule : null;
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (FormatException)
            {
                return null;
            }
            catch (OverflowException)
            {
                return null;
            }
        }

        private static string StrategyToken(SchedulingStrategy strategy)
        {
            return strategy == SchedulingStrategy.RenderBoost ? "render" : strategy == SchedulingStrategy.RenderCore ? "core"
                : strategy == SchedulingStrategy.DependencyBoost ? "dependency" : strategy == SchedulingStrategy.RenderCoreTimeCritical ? "core-critical"
                : strategy == SchedulingStrategy.GamePriority ? "game" : strategy == SchedulingStrategy.RetiredGentle ? "gentle" : "auto";
        }

        private static bool TryStrategy(string token, out SchedulingStrategy strategy)
        {
            strategy = token == "render" ? SchedulingStrategy.RenderBoost : token == "core" ? SchedulingStrategy.RenderCore
                : token == "dependency" ? SchedulingStrategy.DependencyBoost : token == "core-critical" ? SchedulingStrategy.RenderCoreTimeCritical
                : token == "game" ? SchedulingStrategy.GamePriority : token == "gentle" ? SchedulingStrategy.RetiredGentle : SchedulingStrategy.Orchestration;
            return token == "auto" || token == "render" || token == "core" || token == "dependency" || token == "core-critical" || token == "game" || token == "gentle";
        }

        private static ulong Hex(string text)
        {
            if (text.Length < 1 || text.Length > 16)
            {
                throw new FormatException("Invalid processor mask");
            }
            return ulong.Parse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        }

        private static bool ValidTopologyText(string topology)
        {
            string[] fields = topology.Split(',');
            if (fields.Length < 2 || fields.Length > 65 || topology != topology.ToUpperInvariant()) return false;
            return fields.All((string field) => Hex(field) != 0 && Hex(field).ToString("X", CultureInfo.InvariantCulture) == field);
        }

        internal static string Raw(GameProfile profile)
        {
            string value;
            if (profile == null || !profile.Overrides.TryGetValue("GameCriticalThreadHardRuleV1", out value))
            {
                return null;
            }
            return value;
        }

        internal string Validate(ulong all, ulong[] physical, ulong allowed, ulong reserved, bool topologyTrusted)
        {
            if (IsUnified) return ValidateUnified(all, physical, allowed, reserved, topologyTrusted);
            if (Automatic && (!topologyTrusted || physical == null))
            {
                return PerformancePolicy.Text("处理器拓扑不可验证", "CPU topology could not be verified");
            }
            if (!topologyTrusted || physical == null || ((!Automatic || ManualTarget) && Topology != ManualCorePlan.Stamp(all, physical)))
            {
                return PerformancePolicy.Text("处理器拓扑已变化，请重新选择", "CPU topology changed; choose processors again");
            }
            if (physical.Length < 3)
            {
                return PerformancePolicy.Text("至少需要三个不同物理核心：一个重点核心和两个工作核心", "At least three distinct physical cores are required: one candidate core and two worker cores");
            }
            ulong numericValue = 0uL;
            foreach (ulong numericValueEntry in physical)
            {
                if (numericValueEntry == 0 || (numericValueEntry & ~all) != 0 || (numericValue & numericValueEntry) != 0)
                {
                    return PerformancePolicy.Text("处理器拓扑不可验证", "CPU topology could not be verified");
                }
                numericValue |= numericValueEntry;
            }
            if (numericValue != all || allowed == 0 || (allowed & ~all) != 0)
            {
                return PerformancePolicy.Text("游戏允许的处理器范围不可验证", "The game's allowed processor range could not be verified");
            }
            // 任一 SMT 同核线程被预留时 其所有者占用整个物理核
            reserved = AutoCores.WholeCores(reserved, physical);
            if (Automatic && !ManualTarget)
            {
                if (physical.Count(core => (core & allowed) != 0 && (core & reserved) == 0) < 3)
                {
                    return PerformancePolicy.Text("可用物理核心不足：至少选中三个不同物理核，每核可只选一个逻辑线程", "Not enough available physical cores: select at least three distinct physical cores; one logical processor per core is enough");
                }
                return null;
            }
            ulong numericValue2 = 0uL;
            ulong[] targetsSnapshot = targets;
            foreach (ulong target in targetsSnapshot)
            {
                if ((target & allowed) != target)
                {
                    return PerformancePolicy.Text("重点核心超出游戏允许范围", "A selected processor is outside the game's allowed range");
                }
                ulong singleResult = physical.Single((ulong x) => (x & target) != 0);
                if ((singleResult & (numericValue2 | reserved)) != 0)
                {
                    return PerformancePolicy.Text("重点线程需要不同且未被占用的物理核心", "Candidates require separate, unreserved physical cores");
                }
                numericValue2 |= singleResult;
            }
            ulong workers = allowed & ~(numericValue2 | reserved);
            if (physical.Count((ulong x) => (x & workers) != 0) < 2)
            {
                return PerformancePolicy.Text("至少为其他线程保留两个不同物理核心上的逻辑线程", "Reserve logical processors on at least two other physical cores for other threads");
            }
            return null;
        }

        // 保存时要求清核后至少剩两颗物理核 运行期容量不足仅停用清核
        internal string ClearCoreCapacityError(ulong all, ulong[] physical, ulong allowed, ulong reserved, bool topologyTrusted)
        {
            if (!ClearCore || !FormalStrategy) return null;
            if (!topologyTrusted || physical == null || physical.Length == 0 || all == 0)
                return PerformancePolicy.Text("处理器拓扑不可验证，暂不能打开清核", "CPU topology could not be verified; clear core is unavailable");
            ulong usable = allowed & ~AutoCores.WholeCores(reserved, physical);
            if (physical.Count(core => (core & usable) != 0) < 3)
                return PerformancePolicy.Text("清核后其他线程至少需要两颗物理核，请扩大游戏核心范围或关闭清核",
                    "Clearing a core leaves fewer than two physical cores for other threads; widen the game's CPU range or turn clear core off");
            return null;
        }

        private string ValidateUnified(ulong all, ulong[] physical, ulong allowed, ulong reserved, bool topologyTrusted)
        {
            // 优先级授权不依赖选核拓扑 运行时仍须逐属性核验身份 权限和范围
            if (IsOff || Automatic && !ManualTarget) return null;
            if (!topologyTrusted || physical == null || physical.Length == 0 || all == 0
                || Topology != ManualCorePlan.Stamp(all, physical))
                return ThreadLayoutDescriptions.Text("处理器拓扑不可验证，请重新选择主核心", "CPU topology could not be verified; choose the primary CPU again", "CPU構成を確認できません。主CPUを選択し直してください");
            ulong known = 0;
            foreach (ulong core in physical)
            {
                if (core == 0 || (core & ~all) != 0 || (known & core) != 0)
                    return ThreadLayoutDescriptions.Text("处理器拓扑不可验证", "CPU topology could not be verified", "CPU構成を確認できません");
                known |= core;
            }
            if (known != all || allowed == 0 || (allowed & ~all) != 0)
                return ThreadLayoutDescriptions.Text("游戏允许的处理器范围不可验证", "The game's allowed CPU range could not be verified", "ゲームの使用可能なCPU範囲を確認できません");
            ulong selected = targets.Aggregate(0UL, (sum, item) => sum | item);
            if ((selected & allowed) != selected)
                return ThreadLayoutDescriptions.Text("所选核心超出游戏允许范围", "A selected CPU is outside the game's allowed range", "選択したCPUがゲームの使用可能範囲外です");
            ulong whole = physical.Where(core => (core & selected) != 0).Aggregate(0UL, (sum, core) => sum | core);
            if ((whole & AutoCores.WholeCores(reserved, physical)) != 0)
                return ThreadLayoutDescriptions.Text("所选核心已被其他调度归属预留", "A selected core is reserved by another scheduling owner", "選択したコアは他のスケジュール所有者に予約されています");
            ulong rest = allowed & ~(whole | AutoCores.WholeCores(reserved, physical));
            // 渲染线程之外的线程至少还要两颗物理核 整核避让时所选核心整颗都不给它们用
            if (WorkerAvoidance && physical.Count(core => (core & rest) != 0) < 2)
                return ThreadLayoutDescriptions.Text("整核避让后其他线程至少需要两颗物理核，请少选几个核心、扩大游戏核心范围或关闭整核避让", "Whole-core avoidance leaves fewer than two physical cores for other threads; select fewer CPUs, widen the game's CPU range, or turn it off", "コアを空けると他のスレッド用の物理コアが2つ未満になります。選択を減らすか、CPU範囲を広げるか、オフにしてください");
            // 不避让时 手动指定逻辑 CPU 不等于整核独占 运行时另行核验负载容量和可用范围
            return null;
        }
    }
}
