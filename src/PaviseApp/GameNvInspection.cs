using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace PaviseApp
{
	internal sealed class GameNvInspection
	{
		internal readonly GameNvPending State;

		internal readonly GameNvProfile Profile;

		internal readonly string ProfileName;

		internal readonly bool OwnResources;

		internal readonly ReadOnlyDictionary<uint, uint> EligibleTargets;

		internal readonly ReadOnlyDictionary<uint, GameNvSetting> Originals;

		internal readonly ReadOnlyDictionary<uint, GameNvProfileDecision> Decisions;

		internal readonly bool ProductPolicyEvaluated;

		internal readonly string OriginalPlanHash;

		internal bool NativeMutationAvailable
		{
			get
			{
				return false;
			}
		}

		internal string PolicyRule
		{
			get
			{
				if (!ProductPolicyEvaluated)
				{
					return "not-evaluated";
				}
				return "game-nv-preserve-profile-rules-and-user-overrides-1";
			}
		}

		internal GameNvInspection(GameNvPending state, GameNvProfile profile, string name, bool own, IDictionary<uint, uint> targets, IDictionary<uint, GameNvSetting> originals, IDictionary<uint, GameNvProfileDecision> decisions, bool productPolicyEvaluated, string originalPlanHash)
		{
			State = state;
			Profile = profile;
			ProfileName = name;
			OwnResources = own;
			EligibleTargets = new ReadOnlyDictionary<uint, uint>(new Dictionary<uint, uint>(targets));
			Originals = new ReadOnlyDictionary<uint, GameNvSetting>(new Dictionary<uint, GameNvSetting>(originals));
			Decisions = new ReadOnlyDictionary<uint, GameNvProfileDecision>(new Dictionary<uint, GameNvProfileDecision>(decisions));
			ProductPolicyEvaluated = productPolicyEvaluated;
			OriginalPlanHash = originalPlanHash;
		}

		internal void RequireCurrentProductPolicy(GameNvPrelaunchPlan originalPlan)
		{
			if (!ProductPolicyEvaluated || originalPlan == null || originalPlan.Hash != OriginalPlanHash)
			{
				throw new InvalidOperationException("No matching product profile-policy evaluation");
			}
			if (Originals.Count != 2 || Decisions.Count != 2 || EligibleTargets.Keys.Any((uint id) => id != 274197361 && id != 549528094))
			{
				throw new InvalidDataException("Product policy vector is outside the two-key contract");
			}
			uint[] numericValueItems = new uint[2] { 274197361u, 549528094u };
			foreach (uint numericValueEntry in numericValueItems)
			{
				uint value;
				GameNvProfileDecision gameNvProfileDecision = (originalPlan.Targets.TryGetValue(numericValueEntry, out value) ? GameNvProfilePolicy.Evaluate(numericValueEntry, value, Originals[numericValueEntry]) : new GameNvProfileDecision(originalPlan.Members[(numericValueEntry != 274197361) ? GameNvMember.OpenGl : GameNvMember.PState], GameNvProfilePolicy.Source(Originals[numericValueEntry])));
				if (Decisions[numericValueEntry].State != gameNvProfileDecision.State || Decisions[numericValueEntry].Source != gameNvProfileDecision.Source || EligibleTargets.ContainsKey(numericValueEntry) != (gameNvProfileDecision.State == GameNvPending.Ready) || (EligibleTargets.ContainsKey(numericValueEntry) && EligibleTargets[numericValueEntry] != value))
				{
					throw new InvalidDataException("Product game NVIDIA policy result changed");
				}
			}
		}
	}
}
