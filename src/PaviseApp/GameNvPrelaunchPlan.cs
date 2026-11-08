using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;

namespace PaviseApp
{
	internal sealed class GameNvPrelaunchPlan
	{
		internal const uint PStateId = 274197361u;

		internal const uint OpenGlId = 549528094u;

		internal readonly string LibraryId;

		internal readonly string Executable;

		internal readonly string ExecutableHash;

		internal readonly string EvidenceHash;

		internal readonly string MachineHash;

		internal readonly string PhysicalToken;

		internal readonly string Hash;

		internal readonly string Text;

		internal readonly long Generation;

		internal readonly ReadOnlyDictionary<GameNvMember, GameNvPending> Members;

		internal readonly ReadOnlyDictionary<uint, uint> Targets;

		private GameNvPrelaunchPlan(string libraryId, string executable, string imageHash, GameNvApprovalInput approval, IDictionary<GameNvMember, GameNvPending> members, IDictionary<uint, uint> targets, GameNvApiEvidence api)
		{
			LibraryId = libraryId;
			Executable = executable;
			ExecutableHash = imageHash;
			EvidenceHash = approval.EvidenceHash;
			MachineHash = approval.MachineHash;
			PhysicalToken = approval.PhysicalToken;
			Generation = approval.Generation;
			Members = new ReadOnlyDictionary<GameNvMember, GameNvPending>(new Dictionary<GameNvMember, GameNvPending>(members));
			Targets = new ReadOnlyDictionary<uint, uint>(new SortedDictionary<uint, uint>(targets));
			byte[] bytes = GameNvText.Encode(new Dictionary<string, string>
			{
				{ "rule", "game-nv-prelaunch-intent-1" },
				{ "library", LibraryId },
				{ "executable", Executable },
				{ "image", ExecutableHash },
				{
					"evidence",
					EvidenceHash ?? ""
				},
				{
					"machine",
					MachineHash ?? ""
				},
				{
					"physical",
					PhysicalToken ?? ""
				},
				{
					"generation",
					Generation.ToString(CultureInfo.InvariantCulture)
				},
				{
					"pstate",
					Members[GameNvMember.PState].ToString()
				},
				{
					"opengl",
					Members[GameNvMember.OpenGl].ToString()
				},
				{
					"api",
					(api == null) ? "unknown" : api.Api.ToString()
				},
				{
					"api_kind",
					(api == null) ? "None" : api.Kind.ToString()
				},
				{
					"api_image",
					(api == null) ? "" : (api.ExecutableHash ?? "")
				},
				{
					"api_source",
					(api == null) ? "" : (api.SourceVersion ?? "")
				},
				{
					"api_physical",
					(api == null) ? "" : (api.PhysicalToken ?? "")
				},
				{
					"api_nvidia",
					(api != null && api.Nvidia) ? "1" : "0"
				}
			});
			Text = new UTF8Encoding(false, true).GetString(bytes);
			Hash = GameNvText.Hash(bytes);
		}

		internal static GameNvPrelaunchPlan Build(string libraryId, string executable, string currentImageHash, GameNvApprovalInput approval, GameNvTargetPhase phase, IGameNvApiEvidenceProvider apiProvider = null)
		{
			if (approval == null)
			{
				throw new ArgumentNullException("approval");
			}
			string message = "";
			bool matchesIsHash = !string.IsNullOrWhiteSpace(libraryId) && libraryId.Length <= 256 && GameNvText.IsHash(currentImageHash);
			try
			{
				if (string.IsNullOrEmpty(executable) || !Path.IsPathRooted(executable) || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
				{
					matchesIsHash = false;
				}
				else
				{
					message = Path.GetFullPath(executable);
				}
			}
			catch (ArgumentException)
			{
				matchesIsHash = false;
			}
			if (matchesIsHash && !GamePerformanceTargetPolicy.AllowsGameTarget(message))
			{
				matchesIsHash = false;
			}
			bool matchesIsHash2 = approval.Current && approval.NvidiaScope && approval.Generation > 0 && GameNvText.IsHash(approval.EvidenceHash) && GameNvText.IsHash(approval.MachineHash) && GameNvText.IsHash(approval.PhysicalToken);
			GameNvApiEvidence gameNvApiEvidence = null;
			if (matchesIsHash && matchesIsHash2 && approval.OpenGlSelected && phase == GameNvTargetPhase.Closed && apiProvider != null)
			{
				try
				{
					gameNvApiEvidence = apiProvider.Observe(message, currentImageHash);
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch
				{
					gameNvApiEvidence = null;
				}
			}
			bool allConditionsMet = gameNvApiEvidence != null && gameNvApiEvidence.Api == GameNvApi.OpenGl && (gameNvApiEvidence.Kind == GameNvApiEvidenceKind.ObservedContext || gameNvApiEvidence.Kind == GameNvApiEvidenceKind.ValidatedLaunchMode) && gameNvApiEvidence.ExecutableHash == currentImageHash && !string.IsNullOrWhiteSpace(gameNvApiEvidence.SourceVersion) && gameNvApiEvidence.Nvidia && gameNvApiEvidence.PhysicalToken == approval.PhysicalToken;
			Dictionary<GameNvMember, GameNvPending> dictionary = new Dictionary<GameNvMember, GameNvPending>();
			Dictionary<uint, uint> dictionary2 = new Dictionary<uint, uint>();
			foreach (GameNvMember value in Enum.GetValues(typeof(GameNvMember)))
			{
				GameNvPending gameNvPending = (((value == GameNvMember.PState) ? approval.PStateSelected : approval.OpenGlSelected) ? ((!matchesIsHash2) ? GameNvPending.Approval : ((!matchesIsHash) ? GameNvPending.Target : ((phase != GameNvTargetPhase.Closed) ? GameNvPending.NextLaunch : ((value == GameNvMember.PState && !approval.OnAc) ? GameNvPending.AcPower : ((value != GameNvMember.OpenGl || allConditionsMet) ? GameNvPending.Ready : GameNvPending.ApiEvidence))))) : GameNvPending.Off);
				dictionary.Add(value, gameNvPending);
				if (gameNvPending == GameNvPending.Ready)
				{
					dictionary2.Add(Id(value), 1u);
				}
			}
			return new GameNvPrelaunchPlan(libraryId ?? "", message, currentImageHash ?? "", approval, dictionary, dictionary2, gameNvApiEvidence);
		}

		internal static uint Id(GameNvMember member)
		{
			switch (member)
			{
			case GameNvMember.PState:
				return 274197361u;
			case GameNvMember.OpenGl:
				return 549528094u;
			default:
				throw new ArgumentOutOfRangeException("member");
			}
		}

		internal static void RequireId(uint id)
		{
			if (id != 274197361 && id != 549528094)
			{
				throw new InvalidDataException("Game NVIDIA address is outside the two-key contract");
			}
		}
	}
}
