using System;

namespace PaviseApp
{
	internal interface IGameInputLanguageApi
	{
		int CurrentProcessId { get; }

		bool TryGetForeground(out GameInputWindow window);

		IGameInputProcessLease OpenProcess(int pid, out bool knownGone);

		IntPtr[] GetKeyboardLayouts();

		IntPtr GetKeyboardLayout(uint threadId);

		bool IsIme(IntPtr layout);

		GameInputSendResult SendRequest(GameInputWindow window, IntPtr layout, uint timeoutMs, Func<bool> mayContinue, out int error);
	}
}
