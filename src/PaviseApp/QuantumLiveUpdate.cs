using System;

namespace PaviseApp
{
	internal static class QuantumLiveUpdate
	{
		[ThreadStatic] private static QuantumRuntimeResult last;
		[ThreadStatic] private static string lastValue;

		internal static void Begin() { last = null; lastValue = null; }

		internal static bool PrepareWrite()
		{
			string boot = PerformanceTuning.Boot();
			return BootSessionIdentity.Valid(boot)
				&& Settings.SaveStrDurable(PerformanceActionCatalog.Quantum + "Boot", boot);
		}

		internal static void AfterWrite(string value)
		{
			last = null; lastValue = null;
			try
			{
				// Missing originals provide no restorable runtime baseline; never guess client/server defaults.
				bool knownBaseline = false;
				foreach (PerformanceActionStore.Receipt receipt in PerformanceActionStore.All())
					if (receipt.Id == PerformanceActionCatalog.Quantum
						&& string.Equals(receipt.Target, QuantumRecovery.Target, StringComparison.OrdinalIgnoreCase)
						&& receipt.Original != "absent")
					{
						PerformanceActionBackend.Dword(receipt.Original);
						knownBaseline = true;
					}
				if (!knownBaseline || value == "absent") return;
				uint raw = PerformanceActionBackend.Dword(value);
				lastValue = value;
				last = QuantumRuntime.TryApply(raw);
				if (!last.Applied) Logger.Info("Online quantum update deferred to restart | " + last.Detail);
			}
			catch (Exception error)
			{
				last = null;
				Logger.Info("Online quantum update deferred to restart | " + error.Message);
			}
		}

		internal static string Complete(bool on, string fallback)
		{
			try
			{
				if (last != null && last.Applied && lastValue == PerformanceActionBackend.Read(
					PerformanceActionCatalog.Quantum, QuantumRecovery.Target)
					&& BootSessionIdentity.Compare(Settings.LoadStr(PerformanceActionCatalog.Quantum + "Boot", ""),
						PerformanceTuning.Boot()) == IrqRebootState.AwaitingReboot)
					return on ? "live-applied" : "live-restored";
			}
			catch { }
			return fallback;
		}
	}
}
