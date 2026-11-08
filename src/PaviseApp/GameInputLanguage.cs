using System;

namespace PaviseApp
{
	internal sealed class GameInputLanguage : IGameInputLanguage
	{
		internal const uint RequestTimeoutMs = 500u;

		private readonly IGameInputLanguageApi api;

		internal GameInputLanguage()
			: this(new WindowsGameInputLanguageApi())
		{
		}

		internal GameInputLanguage(IGameInputLanguageApi value)
		{
			if (value == null)
			{
				throw new ArgumentNullException("value");
			}
			api = value;
		}

		private static bool Allowed(Func<bool> check)
		{
			if (check != null)
			{
				return check();
			}
			return false;
		}

		private bool IsIndependentEnglish(IntPtr layout)
		{
			if (layout != IntPtr.Zero && (layout.ToInt64() & 0x3FF) == 9)
			{
				return !api.IsIme(layout);
			}
			return false;
		}

		private bool SameTarget(GameInputProcess process, IGameInputProcessLease lease, GameInputWindow window, Func<bool> mayContinue)
		{
			GameInputWindow window2;
			if (Allowed(mayContinue) && lease.Creation == process.Creation && lease.IsAlive && api.TryGetForeground(out window2) && window.SameAs(window2))
			{
				return window2.Pid == process.Pid;
			}
			return false;
		}

		public EnglishInputOutcome TryOnce(GameInputProcess process, Func<bool> claim, Func<bool> mayContinue)
		{
			bool attempted = false;
			try
			{
				if (!Allowed(mayContinue))
				{
					return new EnglishInputOutcome(EnglishInputResult.Canceled);
				}
				if (!process.IsValid || process.Pid == api.CurrentProcessId)
				{
					return new EnglishInputOutcome(EnglishInputResult.Unavailable);
				}
				GameInputWindow window;
				if (!api.TryGetForeground(out window) || window == null || window.Pid != process.Pid)
				{
					return new EnglishInputOutcome(EnglishInputResult.WaitingForGameWindow);
				}
				bool knownGone;
				IGameInputProcessLease lease = api.OpenProcess(process.Pid, out knownGone);
				try
				{
					if (lease == null)
					{
						return new EnglishInputOutcome(knownGone ? EnglishInputResult.TargetChanged : EnglishInputResult.Unavailable);
					}
					if (!SameTarget(process, lease, window, mayContinue))
					{
						return new EnglishInputOutcome(EnglishInputResult.TargetChanged);
					}
					if (!Allowed(claim))
					{
						return new EnglishInputOutcome(EnglishInputResult.Canceled);
					}
					IntPtr initial = api.GetKeyboardLayout(window.ThreadId);
					if (initial == IntPtr.Zero)
					{
						return new EnglishInputOutcome(EnglishInputResult.Unavailable);
					}
					if (IsIndependentEnglish(initial))
					{
						return new EnglishInputOutcome(EnglishInputResult.AlreadyEnglish);
					}
					IntPtr[] keyboardLayouts = api.GetKeyboardLayouts();
					if (keyboardLayouts == null)
					{
						return new EnglishInputOutcome(EnglishInputResult.Unavailable);
					}
					IntPtr intPtr = IntPtr.Zero;
					IntPtr[] keyboardLayoutsSnapshot = keyboardLayouts;
					foreach (IntPtr intPtr2 in keyboardLayoutsSnapshot)
					{
						if (IsIndependentEnglish(intPtr2))
						{
							intPtr = intPtr2;
							break;
						}
					}
					if (intPtr == IntPtr.Zero)
					{
						return new EnglishInputOutcome(EnglishInputResult.NoEnglishLayout);
					}
					if (!SameTarget(process, lease, window, mayContinue))
					{
						return new EnglishInputOutcome(EnglishInputResult.TargetChanged);
					}
					IntPtr keyboardLayout = api.GetKeyboardLayout(window.ThreadId);
					if (keyboardLayout != initial)
					{
						return new EnglishInputOutcome(IsIndependentEnglish(keyboardLayout) ? EnglishInputResult.AlreadyEnglish : EnglishInputResult.TargetChanged);
					}
					Func<bool> mayContinue2 = () => SameTarget(process, lease, window, mayContinue) && api.GetKeyboardLayout(window.ThreadId) == initial;
					attempted = true;
					int error;
					switch (api.SendRequest(window, intPtr, 500u, mayContinue2, out error))
					{
					case GameInputSendResult.NotIssued:
						return new EnglishInputOutcome(EnglishInputResult.Canceled);
					case GameInputSendResult.Failed:
						return new EnglishInputOutcome((error == 5) ? EnglishInputResult.Denied : EnglishInputResult.Unavailable, true, error);
					default:
						if (!SameTarget(process, lease, window, mayContinue))
						{
							return new EnglishInputOutcome(EnglishInputResult.Unconfirmed, true, 0);
						}
						return new EnglishInputOutcome((api.GetKeyboardLayout(window.ThreadId) == intPtr) ? EnglishInputResult.Changed : EnglishInputResult.Unconfirmed, true, 0);
					}
				}
				finally
				{
					if (lease != null)
					{
						lease.Dispose();
					}
				}
			}
			catch
			{
				return new EnglishInputOutcome(EnglishInputResult.Unavailable, attempted, 0);
			}
		}

		public GameInputProcessState Probe(GameInputProcess process)
		{
			if (!process.IsValid)
			{
				return GameInputProcessState.Gone;
			}
			try
			{
				bool knownGone;
				using (IGameInputProcessLease gameInputProcessLease = api.OpenProcess(process.Pid, out knownGone))
				{
					if (gameInputProcessLease == null)
					{
						return knownGone ? GameInputProcessState.Gone : GameInputProcessState.Unknown;
					}
					return (gameInputProcessLease.Creation != process.Creation || !gameInputProcessLease.IsAlive) ? GameInputProcessState.Gone : GameInputProcessState.Alive;
				}
			}
			catch
			{
				return GameInputProcessState.Unknown;
			}
		}
	}
}
