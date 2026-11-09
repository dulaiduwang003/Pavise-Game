using System;

namespace PaviseApp
{
    internal interface IHardwareSchedulingNative : IDisposable
    {
        bool TryGetOsVersion(out uint major, out uint minor, out uint build);

        bool TryBindEntryPoints();

        int OpenAdapter(ref HwsOpenAdapter args);

        int QueryFeature(ref HwsFeatureQuery args);

        int CloseAdapter(ref HwsCloseAdapter args);
    }
}
