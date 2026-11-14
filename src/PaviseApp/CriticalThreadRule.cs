using System;
using System.Globalization;
using System.Linq;

namespace PaviseApp
{
    internal enum SchedulingComparisonArm { None, Original, PlusOne, PlusTwo }

    // 只保存逐游戏策略，线程身份由运行时确认。
    // 新值只能追加在末尾；持久化使用策略文本，不使用枚举数值。
    internal enum SchedulingStrategy { Orchestration, RenderBoost, RenderCore, DependencyBoost, RenderCoreTimeCritical, GamePriority }

    internal sealed class CriticalThreadRule
    {
        internal const string Key = "GameCriticalThreadHardRuleV1";

        internal const string AutomaticTopology = "auto";

        internal readonly string Topology;

        internal readonly bool WorkerAvoidance;

        internal readonly bool Automatic;

        internal readonly bool ManualTarget;

        internal readonly CriticalThreadLayout Layout;

        internal bool IsUnified { get { return Layout == CriticalThreadLayout.Unified; } }

        internal readonly bool IsOff;

        internal readonly SchedulingComparisonArm ComparisonArm;

        internal bool IsComparison { get { return ComparisonArm != SchedulingComparisonArm.None; } }

        internal readonly SchedulingStrategy Strategy;

        // ParseForRuntime 已将旧手动规则转换为自动选核策略。
        private bool FormalStrategy { get { return IsUnified && !IsOff && !IsComparison && Automatic && !ManualTarget; } }

        internal bool IsOrchestration { get { return FormalStrategy && Strategy == SchedulingStrategy.Orchestration; } }

        internal bool IsDependencyBoost { get { return FormalStrategy && Strategy == SchedulingStrategy.DependencyBoost; } }

        internal bool IsTimeCriticalRender { get { return FormalStrategy && Strategy == SchedulingStrategy.RenderCoreTimeCritical; } }

        // 游戏优先：进程提优、渲染线程提优与渲染线程固定核心三项配合；进程类别由资源通道单独记账和恢复。
        internal bool IsGamePriority { get { return FormalStrategy && Strategy == SchedulingStrategy.GamePriority; } }

        internal bool RaisesGameProcess { get { return IsGamePriority; } }

        internal bool UsesWorkloadDiscovery { get { return IsOrchestration || IsDependencyBoost; } }

        internal bool IsRenderStrategy { get { return FormalStrategy && (Strategy == SchedulingStrategy.RenderBoost || Strategy == SchedulingStrategy.RenderCore || Strategy == SchedulingStrategy.RenderCoreTimeCritical || Strategy == SchedulingStrategy.GamePriority); } }

        internal bool SupportsClearCore { get { return FormalStrategy && !IsDependencyBoost; } }

        internal bool PinsRenderThread { get { return FormalStrategy && (Strategy == SchedulingStrategy.RenderCore || Strategy == SchedulingStrategy.RenderCoreTimeCritical || Strategy == SchedulingStrategy.GamePriority); } }

        // 写入其他游戏线程范围的资格：编排要放置工作池成员，支持清核的策略可额外让其他线程避让。
        internal bool WritesOtherThreadRanges { get { return IsOrchestration || ClearCore && SupportsClearCore; } }

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
            if (string.IsNullOrEmpty(topology) || topology == "auto" || processors == null || processors.Length < 1 || processors.Length > CriticalThreadAutoPolicy.MaximumIsolated || processors.Any((ulong x) => x == 0 || (x & (x - 1)) != 0) || processors.Distinct().Count() != processors.Length
                || (layout == CriticalThreadLayout.ElasticAuxiliary || layout == CriticalThreadLayout.Unified) && processors.Length != 1
                || layout == CriticalThreadLayout.Unified && !ValidTopologyText(topology))
            {
                throw new ArgumentException("Choose one to " + CriticalThreadAutoPolicy.MaximumIsolated + " distinct logical processors");
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

        // 用户打开开关而没有选过方案时使用的缺省方案。已保存的 7|auto 仍按多核协同读取。
        internal static CriticalThreadRule UnifiedDefault()
        {
            return UnifiedStrategy(SchedulingStrategy.GamePriority, false);
        }

        private CriticalThreadRule(SchedulingStrategy strategy, bool clearCore)
            : this(false, CriticalThreadLayout.Unified)
        {
            if (!Enum.IsDefined(typeof(SchedulingStrategy), strategy)) throw new ArgumentException("Unknown scheduling strategy");
            Strategy = strategy;
            WorkerAvoidance = clearCore && strategy != SchedulingStrategy.DependencyBoost;
        }

        // 无清核的编排沿用 7|auto 编码。
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

        // 清核开关。只对统一规则有意义，旧布局的避让标志不算；旧手动规则的“|x”也按清核读取。
        internal bool ClearCore { get { return IsUnified && WorkerAvoidance && !IsOff && !IsComparison; } }

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

        internal CriticalThreadRule WithLayout(CriticalThreadLayout layout)
        {
            if (IsOff) return this;
            if (IsComparison)
            {
                if (layout != CriticalThreadLayout.Unified) throw new ArgumentException("Priority comparison requires unified scheduling");
                return this;
            }
            if (layout == CriticalThreadLayout.Unified)
                return ManualTarget ? UnifiedManual(Topology, targets[0], ClearCore) : Automatic ? UnifiedStrategy(Strategy, ClearCore)
                    : new CriticalThreadRule(Topology, false, layout, targets[0]);
            if (ManualTarget) return Manual(Topology, targets[0], layout);
            if (Automatic) return Auto(WorkerAvoidance, layout);
            return new CriticalThreadRule(Topology, WorkerAvoidance, layout, targets);
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
            // 运行只接收统一规则；旧格式解析仅供原有恢复收据核验使用。
            if (rule == null || !rule.IsUnified || !rule.Automatic && !rule.IsOff) return null;
            // 退役对照规则仅保留读取兼容，运行副本关闭。
            if (rule.IsComparison) return UnifiedOff();
            // 旧手动规则转换为自动选核，保留清核选择。
            return rule.ManualTarget ? UnifiedStrategy(SchedulingStrategy.RenderCore, rule.ClearCore) : rule;
        }

        internal static bool IsLegacyManual(string text)
        {
            CriticalThreadRule rule = Parse(text);
            return rule != null && rule.IsUnified && rule.ManualTarget;
        }

        internal string Encode()
        {
            if (IsUnified)
            {
                if (IsOff) return "7|off";
                if (IsComparison) return "7|compare|" + (ComparisonArm == SchedulingComparisonArm.Original ? "original"
                    : ComparisonArm == SchedulingComparisonArm.PlusOne ? "1" : "2");
                // 清核作为末尾的可选字段，没有它的旧规则原样有效。
                string clear = WorkerAvoidance ? "|x" : "";
                if (ManualTarget) return "7|manual|" + Topology + "|" + targets[0].ToString("X", CultureInfo.InvariantCulture) + clear;
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
                        unified = UnifiedManual(parts[2], Hex(parts[3]), parts.Length == 5);
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
                : strategy == SchedulingStrategy.GamePriority ? "game" : "auto";
        }

        private static bool TryStrategy(string token, out SchedulingStrategy strategy)
        {
            strategy = token == "render" ? SchedulingStrategy.RenderBoost : token == "core" ? SchedulingStrategy.RenderCore
                : token == "dependency" ? SchedulingStrategy.DependencyBoost : token == "core-critical" ? SchedulingStrategy.RenderCoreTimeCritical
                : token == "game" ? SchedulingStrategy.GamePriority : SchedulingStrategy.Orchestration;
            return token == "auto" || token == "render" || token == "core" || token == "dependency" || token == "core-critical" || token == "game";
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

        private static string CanonicalTopology(string topology)
        {
            // 最早的固定规则允许小写和前导零；只规范运行副本，保留旧原文供身份与恢复核对。
            return string.Join(",", topology.Split(',').Select(field => Hex(field).ToString("X", CultureInfo.InvariantCulture)).ToArray());
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
            // 任一 SMT 同核线程被预留时，其所有者占用整个物理核。
            reserved = AutoCores.WholeCores(reserved, physical);
            if (Automatic && !ManualTarget)
            {
                if (CriticalThreadAutoPolicy.Targets(1, physical, allowed, reserved, 0uL, 0uL, null, 0uL, 0uL) == null)
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

        // 保存时要求清核后至少剩两颗物理核；运行期容量不足仅停用清核。
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
            // 优先级授权不依赖选核拓扑；运行时仍须逐属性核验身份、权限和范围。
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
            ulong target = targets[0];
            if ((target & allowed) != target)
                return ThreadLayoutDescriptions.Text("主核心超出游戏允许范围", "The primary CPU is outside the game's allowed range", "主CPUがゲームの使用可能範囲外です");
            ulong whole = physical.Single(core => (core & target) != 0);
            if ((whole & AutoCores.WholeCores(reserved, physical)) != 0)
                return ThreadLayoutDescriptions.Text("主核心已被其他调度归属预留", "The primary core is reserved by another scheduling owner", "主コアは他のスケジュール所有者に予約されています");
            if (WorkerAvoidance)
            {
                // 清核把整颗物理核留给主角色，其余线程至少还要有两颗物理核可用。
                ulong others = allowed & ~(whole | AutoCores.WholeCores(reserved, physical));
                if (physical.Count(core => (core & others) != 0) < 2)
                    return ThreadLayoutDescriptions.Text("清核后其他线程至少需要两颗物理核，请扩大游戏核心范围或关闭清核", "Clearing the core leaves fewer than two physical cores for other threads; widen the game's CPU range or turn it off", "コアを空けると他のスレッド用の物理コアが2つ未満になります。CPU範囲を広げるかオフにしてください");
            }
            // 不清核时，手动指定一个逻辑 CPU 不等于整核独占；运行时另行核验负载容量和可用范围。
            return null;
        }
    }
}
