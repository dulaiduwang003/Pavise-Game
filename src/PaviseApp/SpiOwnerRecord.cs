using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PaviseApp
{
	internal sealed class SpiOwnerRecord
	{
		private const string Schema = "pavise-shared-spi-owner-1";

		internal readonly string UserSid;

		internal readonly string ClosureDigest;

		internal readonly long Generation;

		internal readonly SpiOwnerPhase Phase;

		internal readonly SpiOwnerStamp Owner;

		internal SpiOwnerRecord(string sid, long generation, SpiOwnerPhase phase, SpiOwnerStamp owner, string closureDigest)
		{
			SpiOwnerIdentity.Sid(sid);
			if (generation < 0 || (phase != 0 && phase != SpiOwnerPhase.RestoreOnly && phase != SpiOwnerPhase.Closed))
			{
				throw new InvalidDataException("Invalid SPI slot phase");
			}
			if ((generation == 0 && (phase != SpiOwnerPhase.Closed || owner != null)) || (generation > 0 && (owner == null || owner.Generation != generation || owner.Identity.UserSid != sid)))
			{
				throw new InvalidDataException("SPI owner generation binding differs");
			}
			if (phase == SpiOwnerPhase.Closed)
			{
				SpiOwnerIdentity.Hash(closureDigest);
			}
			else if (!string.IsNullOrEmpty(closureDigest))
			{
				throw new InvalidDataException("A held SPI link cannot claim closure");
			}
			UserSid = sid;
			Generation = generation;
			Phase = phase;
			Owner = owner;
			ClosureDigest = closureDigest ?? "";
		}

		internal byte[] Bytes()
		{
			SpiOwnerIdentity spiOwnerIdentity = ((Owner == null) ? null : Owner.Identity);
			string s = "pavise-shared-spi-owner-1\nuser_sid\t" + UserSid + "\ngeneration\t" + Generation.ToString(CultureInfo.InvariantCulture) + "\nphase\t" + Phase.ToString() + "\nkind\t" + ((spiOwnerIdentity == null) ? "none" : spiOwnerIdentity.Kind.ToString()) + "\nowner_nonce\t" + ((spiOwnerIdentity == null) ? "" : spiOwnerIdentity.OwnerNonce) + "\ntoken\t" + ((Owner == null) ? "" : Owner.Token) + "\nroot64\t\nrun\t\ntrial\t\nreceipt64\t\narming\t\nclosure\t" + ClosureDigest + "\n";
			return new UTF8Encoding(false, true).GetBytes(s);
		}

		internal static SpiOwnerRecord Parse(byte[] bytes)
		{
			if (bytes == null || bytes.Length == 0 || bytes.Length > 32768)
			{
				throw new InvalidDataException("SPI owner link missing or oversized");
			}
			string[] getStringParts = new UTF8Encoding(false, true).GetString(bytes).Split('\n');
			string[] textEntries = new string[12]
			{
				"user_sid", "generation", "phase", "kind", "owner_nonce", "token", "root64", "run", "trial", "receipt64",
				"arming", "closure"
			};
			if (getStringParts.Length != textEntries.Length + 2 || getStringParts[0] != "pavise-shared-spi-owner-1" || getStringParts[getStringParts.Length - 1] != "")
			{
				throw new InvalidDataException("Incomplete SPI owner link");
			}
			string[] textEntries2 = new string[textEntries.Length];
			for (int i = 0; i < textEntries.Length; i++)
			{
				string message = textEntries[i] + "\t";
				if (!getStringParts[i + 1].StartsWith(message, StringComparison.Ordinal) || getStringParts[i + 1].IndexOf('\t', message.Length) >= 0)
				{
					throw new InvalidDataException("Invalid SPI owner link field order");
				}
				textEntries2[i] = getStringParts[i + 1].Substring(message.Length);
			}
			long result;
			SpiOwnerPhase result2;
			if (!long.TryParse(textEntries2[1], NumberStyles.None, CultureInfo.InvariantCulture, out result) || result.ToString(CultureInfo.InvariantCulture) != textEntries2[1] || !Enum.TryParse<SpiOwnerPhase>(textEntries2[2], out result2) || result2.ToString() != textEntries2[2])
			{
				throw new InvalidDataException("Noncanonical SPI owner generation/phase");
			}
			for (int j = 6; j <= 10; j++)
			{
				if (textEntries2[j].Length != 0)
				{
					throw new InvalidDataException("Retired SPI owner fields must stay empty");
				}
			}
			SpiOwnerStamp owner = null;
			if (textEntries2[3] != "none")
			{
				SpiOwnerKind result3;
				if (!Enum.TryParse<SpiOwnerKind>(textEntries2[3], out result3) || result3.ToString() != textEntries2[3])
				{
					throw new InvalidDataException("Unknown SPI owner kind");
				}
				owner = new SpiOwnerStamp(result, textEntries2[5], new SpiOwnerIdentity(result3, textEntries2[0], textEntries2[4]));
			}
			else
			{
				for (int k = 4; k <= 5; k++)
				{
					if (textEntries2[k].Length != 0)
					{
						throw new InvalidDataException("Initial SPI slot carries an owner");
					}
				}
			}
			SpiOwnerRecord spiOwnerRecord = new SpiOwnerRecord(textEntries2[0], result, result2, owner, textEntries2[11]);
			if (!Equal(spiOwnerRecord.Bytes(), bytes))
			{
				throw new InvalidDataException("Noncanonical SPI owner bytes");
			}
			return spiOwnerRecord;
		}

		internal static bool Equal(byte[] left, byte[] right)
		{
			if (left == null || right == null)
			{
				return left == right;
			}
			if (left.Length != right.Length)
			{
				return false;
			}
			for (int i = 0; i < left.Length; i++)
			{
				if (left[i] != right[i])
				{
					return false;
				}
			}
			return true;
		}

		internal static string Digest(byte[] bytes)
		{
			using (SHA256 sHA = SHA256.Create())
			{
				return BitConverter.ToString(sHA.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
			}
		}
	}
}
