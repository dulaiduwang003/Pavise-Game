using System;

namespace PaviseApp
{
    internal enum SecurityProductState { Active, Inactive, Unknown }

    internal sealed class SecurityProductInfo
    {
        internal string Name { get; private set; }
        internal string VendorKey { get; private set; }
        internal SecurityProductState State { get; private set; }
        internal bool IsThirdParty { get; private set; }
        internal bool RuntimeKnown { get; private set; }
        internal bool IsRunning { get; private set; }

        internal SecurityProductInfo(string name, string vendorKey, SecurityProductState state,
            bool thirdParty, bool runtimeKnown, bool running)
        {
            Name = name ?? ""; VendorKey = vendorKey ?? ""; State = state;
            IsThirdParty = thirdParty; RuntimeKnown = runtimeKnown; IsRunning = running;
        }
    }

    internal sealed class SecurityProductSnapshot
    {
        private readonly SecurityProductInfo[] products;
        private readonly string[] roots;
        internal bool ScanCompleted { get; private set; }
        internal bool HasUnknownState { get; private set; }
        internal string FailureSummary { get; private set; }
        internal SecurityProductInfo[] Products { get { return (SecurityProductInfo[])products.Clone(); } }
        internal string[] ProtectedRoots { get { return (string[])roots.Clone(); } }

        internal SecurityProductSnapshot(bool completed, bool unknown, SecurityProductInfo[] found,
            string[] protectedRoots, string failure)
        {
            ScanCompleted = completed; HasUnknownState = unknown;
            products = found == null ? new SecurityProductInfo[0] : (SecurityProductInfo[])found.Clone();
            roots = protectedRoots == null ? new string[0] : (string[])protectedRoots.Clone();
            FailureSummary = failure ?? "";
        }

        internal static SecurityProductSnapshot Unknown(bool completed, string failure)
        { return new SecurityProductSnapshot(completed, true, null, null, failure); }
    }
}
