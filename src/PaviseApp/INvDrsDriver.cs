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
}
