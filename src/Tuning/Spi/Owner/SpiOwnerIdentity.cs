using System;
using System.IO;
using System.Security.Principal;

namespace PaviseApp
{
    internal sealed class SpiOwnerIdentity
    {
        internal readonly SpiOwnerKind Kind;

        internal readonly string UserSid;

        internal readonly string OwnerNonce;

        internal SpiOwnerIdentity(SpiOwnerKind kind, string sid, string ownerNonce)
        {
            if (kind != 0 && kind != SpiOwnerKind.LegacyManual)
            {
                throw new InvalidDataException("Unknown SPI owner kind");
            }
            Sid(sid);
            Nonce(ownerNonce);
            Kind = kind;
            UserSid = sid;
            OwnerNonce = ownerNonce;
        }

        internal bool Same(SpiOwnerIdentity other)
        {
            if (other != null && Kind == other.Kind && UserSid == other.UserSid)
            {
                return OwnerNonce == other.OwnerNonce;
            }
            return false;
        }

        internal static void Sid(string value)
        {
            try
            {
                if (string.IsNullOrEmpty(value) || new SecurityIdentifier(value).Value != value)
                {
                    throw new InvalidDataException("Noncanonical SPI owner SID");
                }
            }
            catch (ArgumentException innerException)
            {
                throw new InvalidDataException("Invalid SPI owner SID", innerException);
            }
        }

        internal static void Nonce(string value)
        {
            Guid result;
            if (!Guid.TryParseExact(value, "N", out result) || result == Guid.Empty || result.ToString("N") != value)
            {
                throw new InvalidDataException("Invalid SPI owner nonce");
            }
        }

        internal static void Hash(string value)
        {
            if (value == null || value.Length != 64)
            {
                throw new InvalidDataException("Invalid SPI evidence digest");
            }
            foreach (char c in value)
            {
                if ((c < '0' || c > '9') && (c < 'a' || c > 'f'))
                {
                    throw new InvalidDataException("Noncanonical SPI evidence digest");
                }
            }
        }
    }
}
