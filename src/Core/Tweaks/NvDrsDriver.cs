using System;

namespace PaviseApp
{
    internal interface INvDrsDriver
    {
        bool Available { get; }
        bool TryOpenSession(out IntPtr session);
        void CloseSession(IntPtr session);
        bool SaveSession(IntPtr session);
        bool FindOrCreateAppProfile(IntPtr session, string exe, out IntPtr profile);
        int TryGetDword(IntPtr session, IntPtr profile, uint id, out uint value);
        bool SetDword(IntPtr session, IntPtr profile, uint id, uint value, out int status);
        bool DeleteSetting(IntPtr session, IntPtr profile, uint id);
    }

    internal sealed class NativeNvDrsDriver : INvDrsDriver
    {
        public bool Available { get { return NvApi.Available; } }
        public bool TryOpenSession(out IntPtr session) { return NvApi.TryOpenSession(out session); }
        public void CloseSession(IntPtr session) { NvApi.CloseSession(session); }
        public bool SaveSession(IntPtr session) { return NvApi.SaveSession(session); }
        public bool FindOrCreateAppProfile(IntPtr session, string exe, out IntPtr profile)
        { return NvApi.FindOrCreateAppProfile(session, exe, out profile); }
        public int TryGetDword(IntPtr session, IntPtr profile, uint id, out uint value)
        { return NvApi.TryGetDword(session, profile, id, out value); }
        public bool SetDword(IntPtr session, IntPtr profile, uint id, uint value, out int status)
        { return NvApi.SetDword(session, profile, id, value, out status); }
        public bool DeleteSetting(IntPtr session, IntPtr profile, uint id)
        { return NvApi.DeleteSetting(session, profile, id); }
    }
}
