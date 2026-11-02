using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PaviseApp
{
	internal static class SpiManualClosureManifest
	{
		private static string Header(SpiOwnerStamp stamp)
		{
			return "pavise-manual-spi-close-1\nowner\t" + stamp.ManualReceiptOwner + "\n";
		}

		internal static byte[] Bytes(SpiOwnerStamp stamp, IList<string> rows)
		{
			return new UTF8Encoding(false, true).GetBytes(Header(stamp) + ((rows.Count == 0) ? "" : (string.Join("\n", new List<string>(rows).ToArray()) + "\n")));
		}

		internal static IDictionary<string, string> Read(SpiOwnership.SpiOwnerAccess closedAccess)
		{
			string @string = new UTF8Encoding(false, true).GetString(closedAccess.ReadClosedManifest());
			string text = Header(closedAccess.Stamp);
			if (!@string.StartsWith(text, StringComparison.Ordinal) || !@string.EndsWith("\n", StringComparison.Ordinal))
			{
				throw new InvalidDataException("Closed manual SPI evidence has another owner");
			}
			HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
			MatchVisualEffects.Effect[] effects = MatchVisualEffects.Effects;
			foreach (MatchVisualEffects.Effect effect in effects)
			{
				hashSet.Add(PerformanceActionStore.Slot("MatchVisualEffectsV1", effect.Name));
			}
			Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
			string text2 = @string.Substring(text.Length);
			string text3 = null;
			if (text2.Length != 0)
			{
				string[] array = text2.TrimEnd('\n').Split('\n');
				foreach (string text4 in array)
				{
					string[] array2 = text4.Split('\t');
					if (array2.Length != 2 || !hashSet.Contains(array2[0]) || dictionary.ContainsKey(array2[0]) || (text3 != null && string.CompareOrdinal(text3, array2[0]) >= 0))
					{
						throw new InvalidDataException("Invalid closed manual SPI receipt coordinate");
					}
					SpiOwnerIdentity.Hash(array2[1]);
					dictionary.Add(array2[0], array2[1]);
					text3 = array2[0];
				}
			}
			return dictionary;
		}
	}
}
