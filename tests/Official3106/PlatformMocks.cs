using System;
using System.Collections.Generic;
namespace PaviseApp
{
    internal static class OfficialPlatformMocks
    {
        internal static readonly Guid Plan = new Guid("10000000-0000-0000-0000-000000000001");
        internal static bool ForbiddenPowerWrite() { throw new InvalidOperationException("Power mutation is forbidden in migration tests"); }
        internal static PrivacyPlatform Privacy;
        internal static string Machine="11111111111111111111111111111111", User="S-1-5-21-1-2-3-1001";
        internal static readonly Dictionary<string,string> Registry = new Dictionary<string,string>();
        internal static readonly List<string> Writes = new List<string>();
        internal static string ReadRegistry(string target) { string value; return Registry.TryGetValue(target,out value)?value:"absent"; }
        internal static bool WriteRegistry(string target,string value) { Writes.Add(target); Registry[target]=value; return true; }
    }
}
