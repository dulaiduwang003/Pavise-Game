using System;
using System.Globalization;
using System.IO;

namespace PaviseApp
{
	internal sealed class SpiOwnerStamp
	{
		internal readonly long Generation;

		internal readonly string Token;

		internal readonly SpiOwnerIdentity Identity;

		internal string ManualReceiptOwner
		{
			get
			{
				if (Identity.Kind != 0)
				{
					throw new InvalidOperationException("Only a new manual owner stamps visual receipts");
				}
				return Identity.UserSid + ":" + Generation.ToString(CultureInfo.InvariantCulture) + ":" + Token + ":" + Identity.OwnerNonce;
			}
		}

		internal SpiOwnerStamp(long generation, string token, SpiOwnerIdentity identity)
		{
			if (generation <= 0 || identity == null)
			{
				throw new InvalidDataException("Invalid SPI ownership generation");
			}
			SpiOwnerIdentity.Nonce(token);
			Generation = generation;
			Token = token;
			Identity = identity;
		}

		internal bool Same(SpiOwnerStamp other)
		{
			if (other != null && Generation == other.Generation && Token == other.Token)
			{
				return Identity.Same(other.Identity);
			}
			return false;
		}

		internal static SpiOwnerStamp FromManualReceiptOwner(string value)
		{
			string[] array = (value ?? "").Split(':');
			long result;
			if (array.Length != 4 || !long.TryParse(array[1], NumberStyles.None, CultureInfo.InvariantCulture, out result) || result.ToString(CultureInfo.InvariantCulture) != array[1])
			{
				throw new InvalidDataException("Invalid manual SPI receipt epoch");
			}
			return new SpiOwnerStamp(result, array[2], new SpiOwnerIdentity(SpiOwnerKind.Manual, array[0], array[3]));
		}
	}
}
