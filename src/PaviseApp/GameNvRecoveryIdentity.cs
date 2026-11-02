using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PaviseApp
{
	internal sealed class GameNvRecoveryIdentity
	{
		internal readonly string Executable;

		internal readonly string ImageHash;

		internal readonly string PlanHash;

		internal readonly string EvidenceHash;

		internal readonly string MachineHash;

		internal readonly string PhysicalToken;

		internal readonly long Generation;

		internal readonly string JournalDirectory;

		internal readonly string OriginalHash;

		internal readonly string ProfileName;

		internal readonly string ProfileScopeHash;

		internal readonly string LeaseToken;

		internal readonly uint ProfilePredefined;

		internal readonly bool OwnResources;

		internal bool ResourceBound
		{
			get
			{
				if (JournalDirectory != null)
				{
					return GameNvText.IsHash(OriginalHash);
				}
				return false;
			}
		}

		private GameNvRecoveryIdentity(GameNvRecoveryIdentity plan, string journal, string originalHash, string profile, uint predefined, string scope, string token, bool own)
		{
			if (!Path.IsPathRooted(journal) || Path.GetFullPath(journal) != journal || !GameNvText.IsHash(originalHash) || string.IsNullOrEmpty(profile) || predefined > 1 || Path.GetFileName(journal) != token)
			{
				throw new InvalidDataException("Recovery resource is not bound to its original journal");
			}
			Executable = plan.Executable;
			ImageHash = plan.ImageHash;
			PlanHash = plan.PlanHash;
			EvidenceHash = plan.EvidenceHash;
			MachineHash = plan.MachineHash;
			PhysicalToken = plan.PhysicalToken;
			Generation = plan.Generation;
			JournalDirectory = journal;
			OriginalHash = originalHash;
			ProfileName = profile;
			ProfilePredefined = predefined;
			ProfileScopeHash = scope;
			LeaseToken = token;
			OwnResources = own;
		}

		internal GameNvRecoveryIdentity WithJournal(string journal, string hash, string profile, uint predefined, string scope, string token, bool own)
		{
			return new GameNvRecoveryIdentity(this, journal, hash, profile, predefined, scope, token, own);
		}

		internal GameNvRecoveryIdentity(string planText, string planHash, string executable, string imageHash)
		{
			byte[] bytes = new UTF8Encoding(false, true).GetBytes(planText);
			Dictionary<string, string> dictionary = GameNvText.Decode(bytes);
			if (GameNvText.Hash(bytes) != planHash || dictionary.Count != 16 || dictionary["rule"] != "game-nv-prelaunch-intent-1" || dictionary["executable"] != executable || dictionary["image"] != imageHash || !GameNvText.IsHash(dictionary["evidence"]) || !GameNvText.IsHash(dictionary["machine"]) || !GameNvText.IsHash(dictionary["physical"]))
			{
				throw new InvalidDataException("Recovery lacks its complete original game plan identity");
			}
			Generation = long.Parse(dictionary["generation"], CultureInfo.InvariantCulture);
			if (Generation < 1)
			{
				throw new InvalidDataException("Invalid original game policy generation");
			}
			Executable = executable;
			ImageHash = imageHash;
			PlanHash = planHash;
			EvidenceHash = dictionary["evidence"];
			MachineHash = dictionary["machine"];
			PhysicalToken = dictionary["physical"];
		}
	}
}
