using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace PaviseApp
{
    internal static partial class CriticalThreadGuardian
    {

        private static string Hex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        private static string Hash(byte[] bytes)
        {
            using (SHA256 sHA = SHA256.Create())
            {
                return Hex(sHA.ComputeHash(bytes));
            }
        }

        private static string Hash(Stream stream)
        {
            stream.Position = 0L;
            using (SHA256 sHA = SHA256.Create())
            {
                return Hex(sHA.ComputeHash(stream));
            }
        }

        private static string Nonce()
        {
            byte[] rawBytes = new byte[32];
            using (RandomNumberGenerator randomNumberGenerator = RandomNumberGenerator.Create())
            {
                randomNumberGenerator.GetBytes(rawBytes);
            }
            return Hex(rawBytes);
        }

        private static bool IsHash(string value)
        {
            if (value == null || value.Length != 64)
            {
                return false;
            }
            foreach (char c in value)
            {
                if (c < '0' || (c > '9' && c < 'a') || c > 'f')
                {
                    return false;
                }
            }
            return true;
        }

        private static string Canonical(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static void NoReparse(string path)
        {
            for (DirectoryInfo directoryInfo = new DirectoryInfo(path); directoryInfo != null; directoryInfo = directoryInfo.Parent)
            {
                if ((directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("guardian reparse directory refused");
                }
            }
        }

        private static string UserSid()
        {
            using (WindowsIdentity windowsIdentity = WindowsIdentity.GetCurrent())
            {
                return windowsIdentity.User.Value;
            }
        }

        private static bool TrustedSid(string value, string user)
        {
            if (value != user && value != "S-1-5-18")
            {
                return value == "S-1-5-32-544";
            }
            return true;
        }

        private static void RequireAcl(string root, string sid, bool protectedRules = true)
        {
            NoReparse(root);
            DirectorySecurity accessControl = Directory.GetAccessControl(root, AccessControlSections.Access | AccessControlSections.Owner);
            if ((protectedRules && !accessControl.AreAccessRulesProtected) || !TrustedSid(accessControl.GetOwner(typeof(SecurityIdentifier)).Value, sid))
            {
                throw new IOException("original guardian store owner is not trusted");
            }
            bool ownerHasFullControl = false;
            foreach (FileSystemAccessRule accessRule in accessControl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (accessRule.AccessControlType == AccessControlType.Allow)
                {
                    string identityReferenceValue = accessRule.IdentityReference.Value;
                    if (!TrustedSid(identityReferenceValue, sid))
                    {
                        throw new IOException("guardian source has a foreign allow rule");
                    }
                    if (identityReferenceValue == sid && (accessRule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl)
                    {
                        ownerHasFullControl = true;
                    }
                }
            }
            if (!ownerHasFullControl)
            {
                throw new IOException("original user lacks full source ownership");
            }
        }

        private static void ProvisionRoot(string root, string sid)
        {
            if (!Directory.Exists(root))
            {
                Directory.CreateDirectory(root);
                NoReparse(root);
                DirectorySecurity directorySecurity = new DirectorySecurity();
                directorySecurity.SetOwner(new SecurityIdentifier(sid));
                directorySecurity.SetAccessRuleProtection(true, false);
                foreach (string sddlForm in new string[3] { sid, "S-1-5-18", "S-1-5-32-544" })
                {
                    directorySecurity.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sddlForm), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                }
                Directory.SetAccessControl(root, directorySecurity);
            }
            RequireAcl(root, sid);
        }

    }
}
