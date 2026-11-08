using System;
using System.Globalization;
using System.Linq;

namespace PaviseApp
{
	internal sealed class CriticalThreadRule
	{
		internal const string Key = "GameCriticalThreadHardRuleV1";

		internal const string AutomaticTopology = "auto";

		internal readonly string Topology;

		internal readonly bool WorkerAvoidance;

		internal readonly bool Automatic;

		internal readonly bool ManualTarget;

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
		{
			if (string.IsNullOrEmpty(topology) || topology == "auto" || processors == null || processors.Length < 1 || processors.Length > CriticalThreadAutoPolicy.MaximumIsolated || processors.Any((ulong x) => x == 0 || (x & (x - 1)) != 0) || processors.Distinct().Count() != processors.Length)
			{
				throw new ArgumentException("Choose one to " + CriticalThreadAutoPolicy.MaximumIsolated + " distinct logical processors");
			}
			Topology = topology;
			WorkerAvoidance = workerAvoidance;
			targets = (ulong[])processors.Clone();
		}

		private CriticalThreadRule(bool workerAvoidance)
		{
			Topology = "auto";
			WorkerAvoidance = workerAvoidance;
			Automatic = true;
			targets = new ulong[0];
		}

		internal static CriticalThreadRule Auto(bool workerAvoidance)
		{
			return new CriticalThreadRule(workerAvoidance);
		}

		private CriticalThreadRule(string topology, ulong processor, bool manualTarget)
			: this(topology, true, new ulong[] { processor })
		{
			if (topology != topology.ToUpperInvariant()) throw new ArgumentException("Noncanonical processor topology");
			Automatic = true;
			ManualTarget = true;
		}

		internal static CriticalThreadRule Manual(string topology, ulong processor)
		{
			return new CriticalThreadRule(topology, processor, true);
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
			return rule != null && rule.Automatic && !rule.ManualTarget ? Auto(true) : rule;
		}

		internal string Encode()
		{
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
			if (bound == null || bound.Automatic)
			{
				return false;
			}
			if (!Automatic)
			{
				return bound.Encode() == Encode();
			}
			if (ManualTarget)
			{
				return bound.Topology == Topology && bound.CandidateCount == 1 && bound.targets[0] == targets[0]
					&& bound.WorkerAvoidance == WorkerAvoidance;
			}
			return bound.WorkerAvoidance == WorkerAvoidance;
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

		private static ulong Hex(string text)
		{
			if (text.Length < 1 || text.Length > 16)
			{
				throw new FormatException("Invalid processor mask");
			}
			return ulong.Parse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
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
			// Either reserved SMT sibling holds the entire physical core for that owner.
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
	}
}
