using System;
using System.IO;

namespace PaviseApp
{
    internal static class HardThreadStorageDomain
    {
        internal static void RequireLocal(string canonicalRoot, Func<string, DriveType> readDriveType)
        {
            if (string.IsNullOrEmpty(canonicalRoot) || canonicalRoot.Length < 3 || ((canonicalRoot[0] < 'A' || canonicalRoot[0] > 'Z') && (canonicalRoot[0] < 'a' || canonicalRoot[0] > 'z')) || canonicalRoot[1] != ':' || canonicalRoot[2] != '\\')
            {
                throw new HardThreadStorageDomainException("Critical-thread placement requires a local Pavise data directory");
            }
            if (readDriveType == null)
            {
                throw new ArgumentNullException("readDriveType");
            }
            DriveType driveType;
            try
            {
                driveType = readDriveType(canonicalRoot.Substring(0, 3));
            }
            catch (Exception inner)
            {
                throw new HardThreadStorageDomainException("The local Pavise data directory could not be confirmed", inner);
            }
            if (driveType != DriveType.Fixed && driveType != DriveType.Removable && driveType != DriveType.Ram)
            {
                throw new HardThreadStorageDomainException("Critical-thread placement requires a local Pavise data directory");
            }
        }
    }
}
