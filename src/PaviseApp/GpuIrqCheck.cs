using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Threading;

namespace PaviseApp
{
	internal static class GpuIrqCheck
	{
		internal const int CheckSeconds = 120;

		internal const long MinEvents = 300L;

		internal const double SlowP99Us = 500.0;

		internal const int SlowLimit = 5;

		internal const string StateKey = "GpuIrqCheckV1";

		internal const string SlowKey = "GpuIrqSlowV1";

		internal const string FuseKey = "GpuIrqFuseV1";

		private const string DisplayClass = "{4d36e968-e325-11ce-bfc1-08002be10318}";

		private static readonly object lk = new object();

		private static readonly IrqAffinityEngine engine = new IrqAffinityEngine("GpuIrqAutoOn", "GpuIrqAuto_", "GPU IRQ ");

		public static bool HasResidue
		{
			get
			{
				return engine.HasResidue;
			}
		}

		public static List<string> TouchedDevices()
		{
			return engine.TouchedDevices();
		}

		public static GpuIrqState LoadState()
		{
			return GpuIrqState.Parse(Settings.LoadStr("GpuIrqCheckV1", ""));
		}

		public static bool AwaitingReboot(GpuIrqState state)
		{
			if (state != null && state.Phase == GpuIrqPhase.Pending)
			{
				return RebootState(state.DeviceId) == IrqRebootState.AwaitingReboot;
			}
			return false;
		}

		internal static ulong WholeCores(ulong mask, ulong[] physical)
		{
			if (mask == 0 || physical == null)
			{
				return 0uL;
			}
			ulong wholeCoresResult = 0uL;
			foreach (ulong numericValueEntry in physical)
			{
				if ((numericValueEntry & mask) != 0)
				{
					wholeCoresResult |= numericValueEntry;
				}
			}
			return wholeCoresResult;
		}

		internal static ulong GameRegion(AutoCorePlan plan, ulong all, ulong[] physical)
		{
			if (plan == null || !plan.Available || all == 0)
			{
				return 0uL;
			}
			ulong exclusiveMask = plan.Exclusive & all;
			if (exclusiveMask == 0 || exclusiveMask == all)
			{
				return 0uL;
			}
			ulong wholeCoresMask = WholeCores(exclusiveMask, physical) & all;
			if (wholeCoresMask != all)
			{
				return wholeCoresMask;
			}
			return 0uL;
		}

		internal static ulong TargetFor(ulong region, ulong all, ulong perf, bool hybrid)
		{
			if (region == 0)
			{
				return 0uL;
			}
			ulong allMask = all & ~region;
			if (hybrid && perf != 0)
			{
				allMask &= perf;
			}
			return allMask;
		}

		internal static GpuIrqOutcome Evaluate(GpuIrqSample gpu, ulong region, ulong target, out string reason)
		{
			reason = "";
			if (gpu == null || gpu.Events < 300 || gpu.Seen == 0)
			{
				return GpuIrqOutcome.NoEvidence;
			}
			if (region == 0)
			{
				return GpuIrqOutcome.NoRegion;
			}
			if ((gpu.Seen & region) == 0)
			{
				return GpuIrqOutcome.Clear;
			}
			if (target == 0)
			{
				return GpuIrqOutcome.NoTarget;
			}
			if (!gpu.ConfigurationKnown)
			{
				reason = "irqcheck.reason.config";
				return GpuIrqOutcome.Skipped;
			}
			if (gpu.MultiMessage)
			{
				reason = "irqcheck.reason.multimessage";
				return GpuIrqOutcome.Skipped;
			}
			if (gpu.ExternalPin)
			{
				reason = "irqcheck.reason.externalpin";
				return GpuIrqOutcome.Skipped;
			}
			return GpuIrqOutcome.Scheduled;
		}

		internal static bool Verify(GpuIrqSample gpu, GpuIrqState state, bool displayReset, out string reason)
		{
			if (displayReset)
			{
				reason = "irqcheck.reason.reset";
				return false;
			}
			if ((gpu.Seen & ~state.Target) != 0)
			{
				reason = "irqcheck.reason.ignored";
				return false;
			}
			double lowerBound = Math.Max(state.BaselineP99 * 2.0, state.BaselineP99 + 100.0);
			if (state.BaselineP99 > 0.0 && gpu.P99Us > lowerBound)
			{
				reason = "irqcheck.reason.slower";
				return false;
			}
			reason = "";
			return true;
		}

		private static string ServiceOf(string driver)
		{
			string driverOffset = driver ?? "";
			int matchIndex = driverOffset.LastIndexOf('.');
			if (matchIndex <= 0)
			{
				return driverOffset;
			}
			return driverOffset.Substring(0, matchIndex);
		}

		internal static GpuIrqSample PickGpu(List<IrqDevice> devices, List<DriverInterrupt> drivers, string ownedDevice)
		{
			GpuIrqSample gpuIrqSample = null;
			if (devices == null || drivers == null)
			{
				return null;
			}
			foreach (IrqDevice device in devices)
			{
				if (device == null || !string.Equals(device.ClassGuid, "{4d36e968-e325-11ce-bfc1-08002be10318}", StringComparison.OrdinalIgnoreCase) || !string.Equals(device.Bus, "PCI", StringComparison.OrdinalIgnoreCase) || device.Service.Length == 0)
				{
					continue;
				}
				DriverInterrupt driverInterrupt = null;
				foreach (DriverInterrupt driver in drivers)
				{
					if (driver != null && string.Equals(ServiceOf(driver.Driver), device.Service, StringComparison.OrdinalIgnoreCase))
					{
						driverInterrupt = driver;
						break;
					}
				}
				if (driverInterrupt != null)
				{
					GpuIrqSample gpuIrqSample2 = new GpuIrqSample();
					gpuIrqSample2.DeviceId = device.InstanceId;
					gpuIrqSample2.Name = device.Name;
					gpuIrqSample2.Driver = driverInterrupt.Driver ?? "";
					gpuIrqSample2.DriverVersion = device.DriverVersion ?? "";
					gpuIrqSample2.Seen = (driverInterrupt.CpuMaskTruncated ? 0 : driverInterrupt.CpuMask);
					gpuIrqSample2.Events = driverInterrupt.Dpc;
					gpuIrqSample2.P99Us = IrqPercentile.ApproxUs(driverInterrupt.DpcBuckets, driverInterrupt.DpcMaxUs, 0.99);
					gpuIrqSample2.MaxUs = driverInterrupt.DpcMaxUs;
					gpuIrqSample2.MultiMessage = device.MultiMessageRisk;
					gpuIrqSample2.ConfigurationKnown = device.ConfigurationKnown;
					gpuIrqSample2.ExternalPin = device.IsPinned && !string.Equals(device.InstanceId, ownedDevice ?? "", StringComparison.OrdinalIgnoreCase);
					GpuIrqSample gpuIrqSample3 = gpuIrqSample2;
					if (gpuIrqSample == null || gpuIrqSample3.Events > gpuIrqSample.Events)
					{
						gpuIrqSample = gpuIrqSample3;
					}
				}
			}
			return gpuIrqSample;
		}

		internal static List<SlowDriver> SlowDrivers(List<DriverInterrupt> drivers, List<IrqDevice> devices)
		{
			List<SlowDriver> list = new List<SlowDriver>();
			if (drivers == null)
			{
				return list;
			}
			foreach (DriverInterrupt driver in drivers)
			{
				if (driver == null || string.IsNullOrEmpty(driver.Driver) || DriverDeviceResolver.IsFramework(driver.Driver))
				{
					continue;
				}
				double approxUsResult = IrqPercentile.ApproxUs(driver.DpcBuckets, driver.DpcMaxUs, 0.99);
				if (driver.DpcOver1Ms <= 0 && (driver.Dpc < 100 || approxUsResult < 500.0))
				{
					continue;
				}
				string device = "";
				if (devices != null)
				{
					foreach (IrqDevice device2 in devices)
					{
						if (device2 != null && string.Equals(device2.Service, ServiceOf(driver.Driver), StringComparison.OrdinalIgnoreCase))
						{
							device = device2.Name;
							break;
						}
					}
				}
				list.Add(new SlowDriver
				{
					Driver = driver.Driver,
					Device = device,
					MaxUs = driver.DpcMaxUs,
					P99Us = approxUsResult,
					Over1Ms = driver.DpcOver1Ms,
					Events = driver.Dpc
				});
			}
			list.Sort((SlowDriver a, SlowDriver b) => b.MaxUs.CompareTo(a.MaxUs));
			if (list.Count > 5)
			{
				list.RemoveRange(5, list.Count - 5);
			}
			return list;
		}

		internal static string EncodeSlow(List<SlowDriver> slow, long ticks)
		{
			List<string> list = new List<string>();
			list.Add("1");
			list.Add(ticks.ToString(CultureInfo.InvariantCulture));
			List<string> list2 = list;
			foreach (SlowDriver item in slow)
			{
				list2.Add(Uri.EscapeDataString(item.Driver) + "," + Uri.EscapeDataString(item.Device) + "," + item.MaxUs.ToString("F0", CultureInfo.InvariantCulture) + "," + item.P99Us.ToString("F0", CultureInfo.InvariantCulture) + "," + item.Over1Ms.ToString(CultureInfo.InvariantCulture) + "," + item.Events.ToString(CultureInfo.InvariantCulture));
			}
			return string.Join("|", list2.ToArray());
		}

		internal static List<SlowDriver> DecodeSlow(string raw, out long ticks)
		{
			ticks = 0L;
			List<SlowDriver> list = new List<SlowDriver>();
			if (string.IsNullOrEmpty(raw))
			{
				return list;
			}
			string[] rawParts = raw.Split('|');
			if (rawParts.Length < 2 || rawParts[0] != "1" || !long.TryParse(rawParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out ticks))
			{
				ticks = 0L;
				return list;
			}
			for (int i = 2; i < rawParts.Length; i++)
			{
				string[] parts = rawParts[i].Split(',');
				double result;
				double result2;
				long result3;
				long result4;
				if (parts.Length == 6 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out result) && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out result2) && long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out result3) && long.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out result4))
				{
					try
					{
						list.Add(new SlowDriver
						{
							Driver = Uri.UnescapeDataString(parts[0]),
							Device = Uri.UnescapeDataString(parts[1]),
							MaxUs = result,
							P99Us = result2,
							Over1Ms = result3,
							Events = result4
						});
					}
					catch
					{
					}
				}
			}
			return list;
		}

		public static List<SlowDriver> LoadSlow(out long ticks)
		{
			return DecodeSlow(Settings.LoadStr("GpuIrqSlowV1", ""), out ticks);
		}

		private static string FuseToken(string deviceId, string version)
		{
			return (deviceId ?? "").ToUpperInvariant() + "#" + (version ?? "");
		}

		internal static bool IsFused(string deviceId, string version)
		{
			string b = FuseToken(deviceId, version);
			string[] loadStrParts = Settings.LoadStr("GpuIrqFuseV1", "").Split('\n');
			foreach (string entryText in loadStrParts)
			{
				if (string.Equals(entryText.Trim(), b, StringComparison.Ordinal))
				{
					return true;
				}
			}
			return false;
		}

		private static void AddFuse(string deviceId, string version)
		{
			if (!IsFused(deviceId, version))
			{
				string gpuIrqFuseV1Value = Settings.LoadStr("GpuIrqFuseV1", "");
				Settings.SaveStr("GpuIrqFuseV1", (gpuIrqFuseV1Value.Length == 0) ? FuseToken(deviceId, version) : (gpuIrqFuseV1Value + "\n" + FuseToken(deviceId, version)));
			}
		}

		private static bool SameDevice(GpuIrqSample gpu, GpuIrqState state)
		{
			if (gpu != null)
			{
				return string.Equals(gpu.DeviceId, state.DeviceId, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}

		public static GpuIrqResult Inspect(int windowMs, Func<bool> cancelled, Action<float> progress = null)
		{
			lock (lk)
			{
				GpuIrqResult gpuIrqResult = new GpuIrqResult();
				if (cancelled != null && cancelled())
				{
					gpuIrqResult.Outcome = GpuIrqOutcome.Cancelled;
					return gpuIrqResult;
				}
				if (!Elevated())
				{
					gpuIrqResult.Detail = Lang.T("irqcheck.needadmin");
					return gpuIrqResult;
				}
				if (CpuTopology.MultiGroup)
				{
					gpuIrqResult.Detail = Lang.T("irq.exact.unsupported");
					return gpuIrqResult;
				}
				GpuIrqState gpuIrqState = LoadState();
				if (gpuIrqState.Phase == GpuIrqPhase.Pending)
				{
					gpuIrqResult.Outcome = (AwaitingReboot(gpuIrqState) ? GpuIrqOutcome.AwaitingReboot : GpuIrqOutcome.NeedsVerification);
					return gpuIrqResult;
				}
				string error;
				InterruptAttributionResult interruptAttributionResult = Capture(cancelled, progress, out error, Math.Max(1000, Math.Min(120000, windowMs)));
				if (cancelled != null && cancelled())
				{
					gpuIrqResult.Outcome = GpuIrqOutcome.Cancelled;
					return gpuIrqResult;
				}
				if (interruptAttributionResult == null || !interruptAttributionResult.Ok || interruptAttributionResult.Lossy || interruptAttributionResult.Incomplete)
				{
					gpuIrqResult.Outcome = ((cancelled != null && cancelled()) ? GpuIrqOutcome.Cancelled : GpuIrqOutcome.Failed);
					gpuIrqResult.Detail = ((interruptAttributionResult == null) ? error : Lang.T("irqcheck.lossy"));
					return gpuIrqResult;
				}
				List<IrqDevice> devices = Devices();
				gpuIrqResult.Slow = SlowDrivers(interruptAttributionResult.Drivers, devices);
				gpuIrqResult.Gpu = PickGpu(devices, interruptAttributionResult.Drivers, (gpuIrqState.Phase == GpuIrqPhase.Applied) ? gpuIrqState.DeviceId : null);
				ulong allMask = CpuTopology.AllMask;
				gpuIrqResult.Region = GameRegion(Plan(), allMask, CpuTopology.PhysicalCoreMasks());
				gpuIrqResult.Target = TargetFor(gpuIrqResult.Region, allMask, CpuTopology.PerfMask, CpuTopology.Hybrid);
				string reason;
				gpuIrqResult.Outcome = Evaluate(gpuIrqResult.Gpu, gpuIrqResult.Region, gpuIrqResult.Target, out reason);
				if (gpuIrqResult.Outcome == GpuIrqOutcome.Scheduled)
				{
					if (IsFused(gpuIrqResult.Gpu.DeviceId, gpuIrqResult.Gpu.DriverVersion))
					{
						gpuIrqResult.Outcome = GpuIrqOutcome.Skipped;
						reason = "irqcheck.reason.fused";
					}
					else
					{
						gpuIrqResult.Outcome = GpuIrqOutcome.NeedsPlacement;
					}
				}
				gpuIrqResult.Detail = (string.IsNullOrEmpty(reason) ? "" : Lang.T(reason));
				return gpuIrqResult;
			}
		}

		internal static GpuIrqResult AfterFix(GpuIrqResult previous, GpuIrqResult fix)
		{
			if (fix == null || fix.Outcome == GpuIrqOutcome.Cancelled || fix.Outcome == GpuIrqOutcome.Failed)
			{
				return previous;
			}
			GpuIrqState gpuIrqState = LoadState();
			if (gpuIrqState.Phase == GpuIrqPhase.Pending)
			{
				GpuIrqResult gpuIrqResult = new GpuIrqResult();
				gpuIrqResult.Outcome = (AwaitingReboot(gpuIrqState) ? GpuIrqOutcome.AwaitingReboot : GpuIrqOutcome.NeedsVerification);
				return gpuIrqResult;
			}
			GpuIrqResult gpuIrqResult2 = new GpuIrqResult();
			gpuIrqResult2.Outcome = fix.Outcome;
			gpuIrqResult2.Detail = fix.Detail ?? "";
			gpuIrqResult2.Gpu = fix.Gpu;
			gpuIrqResult2.Region = fix.Region;
			gpuIrqResult2.Target = fix.Target;
			gpuIrqResult2.Slow = fix.Slow ?? new List<SlowDriver>();
			GpuIrqResult gpuIrqResult3 = gpuIrqResult2;
			switch (fix.Outcome)
			{
			case GpuIrqOutcome.Verified:
				gpuIrqResult3.Outcome = GpuIrqOutcome.Clear;
				gpuIrqResult3.Detail = "";
				break;
			case GpuIrqOutcome.Restored:
				gpuIrqResult3.Outcome = GpuIrqOutcome.NoRegion;
				gpuIrqResult3.Detail = "";
				break;
			case GpuIrqOutcome.RolledBack:
				gpuIrqResult3.Outcome = GpuIrqOutcome.Skipped;
				gpuIrqResult3.Detail = Lang.T("irqcheck.reason.fused");
				break;
			}
			return gpuIrqResult3;
		}

		public static GpuIrqResult Run(Func<bool> cancelled, Action<float> progress)
		{
			lock (lk)
			{
				GpuIrqResult gpuIrqResult = new GpuIrqResult();
				if (cancelled != null && cancelled())
				{
					gpuIrqResult.Outcome = GpuIrqOutcome.Cancelled;
					return gpuIrqResult;
				}
				if (!Elevated())
				{
					gpuIrqResult.Outcome = GpuIrqOutcome.Failed;
					gpuIrqResult.Detail = Lang.T("irqcheck.needadmin");
					return gpuIrqResult;
				}
				if (CpuTopology.MultiGroup)
				{
					gpuIrqResult.Outcome = GpuIrqOutcome.Failed;
					gpuIrqResult.Detail = Lang.T("irq.exact.unsupported");
					return gpuIrqResult;
				}
				GpuIrqState gpuIrqState = LoadState();
				if (AwaitingReboot(gpuIrqState))
				{
					gpuIrqResult.Outcome = GpuIrqOutcome.AwaitingReboot;
					return gpuIrqResult;
				}
				string error;
				InterruptAttributionResult interruptAttributionResult = Capture(cancelled, progress, out error);
				if (cancelled != null && cancelled())
				{
					gpuIrqResult.Outcome = GpuIrqOutcome.Cancelled;
					return gpuIrqResult;
				}
				if (interruptAttributionResult == null)
				{
					bool matchesCancelled = cancelled != null && cancelled();
					gpuIrqResult.Outcome = (matchesCancelled ? GpuIrqOutcome.Cancelled : GpuIrqOutcome.Failed);
					gpuIrqResult.Detail = error;
					return gpuIrqResult;
				}
				if (!interruptAttributionResult.Ok || interruptAttributionResult.Lossy || interruptAttributionResult.Incomplete)
				{
					gpuIrqResult.Outcome = GpuIrqOutcome.Failed;
					gpuIrqResult.Detail = ((interruptAttributionResult.Lossy || interruptAttributionResult.Incomplete) ? Lang.T("irqcheck.lossy") : interruptAttributionResult.Error);
					return gpuIrqResult;
				}
				List<IrqDevice> devices = Devices();
				long ticks = DateTime.UtcNow.Ticks;
				gpuIrqResult.Slow = SlowDrivers(interruptAttributionResult.Drivers, devices);
				Settings.SaveStr("GpuIrqSlowV1", EncodeSlow(gpuIrqResult.Slow, ticks));
				bool anyConditionMet = gpuIrqState.Phase == GpuIrqPhase.Pending || gpuIrqState.Phase == GpuIrqPhase.Applied;
				GpuIrqSample gpuIrqSample = (gpuIrqResult.Gpu = PickGpu(devices, interruptAttributionResult.Drivers, anyConditionMet ? gpuIrqState.DeviceId : null));
				ulong allMask = CpuTopology.AllMask;
				gpuIrqResult.Region = GameRegion(Plan(), allMask, CpuTopology.PhysicalCoreMasks());
				gpuIrqResult.Target = TargetFor(gpuIrqResult.Region, allMask, CpuTopology.PerfMask, CpuTopology.Hybrid);
				gpuIrqState.CheckedTicks = ticks;
				if (gpuIrqState.Phase == GpuIrqPhase.Pending)
				{
					if (!SameDevice(gpuIrqSample, gpuIrqState) || gpuIrqSample.Events < 300 || gpuIrqSample.Seen == 0)
					{
						Save(gpuIrqState);
						gpuIrqResult.Outcome = GpuIrqOutcome.NoEvidence;
						return gpuIrqResult;
					}
					gpuIrqState.Seen = gpuIrqSample.Seen;
					string reason;
					if (Verify(gpuIrqSample, gpuIrqState, DisplayResetSinceBoot(), out reason))
					{
						gpuIrqState.Phase = GpuIrqPhase.Applied;
						gpuIrqState.Reason = "";
						gpuIrqResult.Outcome = GpuIrqOutcome.Verified;
						Logger.Log(Lang.F("log.irqcheck.verified", gpuIrqState.DeviceName, IrqRelocate.MaskText(gpuIrqState.Target)));
					}
					else
					{
						bool restoreSucceeded = Restore(gpuIrqState.DeviceId);
						AddFuse(gpuIrqState.DeviceId, gpuIrqState.DriverVersion);
						gpuIrqState.Phase = GpuIrqPhase.RolledBack;
						gpuIrqState.Reason = reason;
						gpuIrqResult.Outcome = GpuIrqOutcome.RolledBack;
						gpuIrqResult.Detail = Lang.T(reason) + (restoreSucceeded ? "" : (" · " + Lang.T("irqcheck.restorefailed")));
						Logger.Warn(Lang.F("log.irqcheck.rolledback", gpuIrqState.DeviceName, Lang.T(reason)));
					}
					Save(gpuIrqState);
					return gpuIrqResult;
				}
				string reason2;
				GpuIrqOutcome gpuIrqOutcome = Evaluate(gpuIrqSample, gpuIrqResult.Region, gpuIrqResult.Target, out reason2);
				if (gpuIrqSample != null)
				{
					gpuIrqState.Seen = gpuIrqSample.Seen;
					if (!anyConditionMet)
					{
						gpuIrqState.DeviceId = gpuIrqSample.DeviceId;
						gpuIrqState.DriverVersion = gpuIrqSample.DriverVersion;
					}
					gpuIrqState.DeviceName = gpuIrqSample.Name;
				}
				gpuIrqState.Region = gpuIrqResult.Region;
				switch (gpuIrqOutcome)
				{
				case GpuIrqOutcome.Scheduled:
				{
					if (IsFused(gpuIrqSample.DeviceId, gpuIrqSample.DriverVersion))
					{
						gpuIrqOutcome = GpuIrqOutcome.Skipped;
						gpuIrqState.Phase = GpuIrqPhase.Skipped;
						gpuIrqState.Reason = "irqcheck.reason.fused";
						break;
					}
					string failure;
					if (!Write(gpuIrqSample.DeviceId, gpuIrqResult.Target, out failure))
					{
						gpuIrqOutcome = GpuIrqOutcome.Failed;
						gpuIrqResult.Detail = failure;
						break;
					}
					gpuIrqState.Phase = GpuIrqPhase.Pending;
					gpuIrqState.DeviceId = gpuIrqSample.DeviceId;
					gpuIrqState.DriverVersion = gpuIrqSample.DriverVersion;
					gpuIrqState.Target = gpuIrqResult.Target;
					gpuIrqState.BaselineP99 = gpuIrqSample.P99Us;
					gpuIrqState.Reason = "";
					Logger.Log(Lang.F("log.irqcheck.scheduled", gpuIrqSample.Name, IrqRelocate.MaskText(gpuIrqSample.Seen), IrqRelocate.MaskText(gpuIrqResult.Target)));
					break;
				}
				case GpuIrqOutcome.NoRegion:
					if (anyConditionMet)
					{
						bool restoreSucceeded2 = Restore(gpuIrqState.DeviceId);
						gpuIrqOutcome = GpuIrqOutcome.Restored;
						if (!restoreSucceeded2)
						{
							gpuIrqResult.Detail = Lang.T("irqcheck.restorefailed");
						}
					}
					gpuIrqState.Phase = GpuIrqPhase.NoRegion;
					gpuIrqState.Reason = "";
					break;
				case GpuIrqOutcome.Clear:
					gpuIrqState.Phase = ((anyConditionMet && gpuIrqState.Phase == GpuIrqPhase.Applied) ? GpuIrqPhase.Applied : GpuIrqPhase.Clear);
					gpuIrqState.Reason = "";
					break;
				case GpuIrqOutcome.Skipped:
					gpuIrqState.Phase = GpuIrqPhase.Skipped;
					gpuIrqState.Reason = reason2;
					break;
				case GpuIrqOutcome.NoTarget:
					gpuIrqState.Phase = GpuIrqPhase.Skipped;
					gpuIrqState.Reason = "irqcheck.reason.notarget";
					break;
				}
				gpuIrqResult.Outcome = gpuIrqOutcome;
				if (gpuIrqOutcome != 0)
				{
					Save(gpuIrqState);
				}
				return gpuIrqResult;
			}
		}

		public static bool RestoreAll()
		{
			lock (lk)
			{
				bool hooksRestoreAllSucceeded = HooksRestoreAll();
				if (hooksRestoreAllSucceeded)
				{
					Settings.SaveStr("GpuIrqCheckV1", "");
				}
				return hooksRestoreAllSucceeded;
			}
		}

		private static void Save(GpuIrqState state)
		{
			Settings.SaveStr("GpuIrqCheckV1", state.Encode());
		}

		private static InterruptAttributionResult Capture(Func<bool> cancelled, Action<float> progress, out string error, int windowMs = 120000)
		{
			error = "";
			InterruptAttribution interruptAttribution = new InterruptAttribution();
			try
			{
				if (!interruptAttribution.Start())
				{
					error = InterruptAttribution.StartFailureText(interruptAttribution);
					return null;
				}
				Stopwatch stopwatch = Stopwatch.StartNew();
				while (true)
				{
					long elapsedMilliseconds = stopwatch.ElapsedMilliseconds;
					if (progress != null)
					{
						progress(Math.Min(1f, (float)elapsedMilliseconds / (float)windowMs));
					}
					if (cancelled != null && cancelled())
					{
						interruptAttribution.Stop();
						return null;
					}
					if (elapsedMilliseconds >= windowMs)
					{
						break;
					}
					Thread.Sleep(200);
				}
				InterruptAttributionResult interruptAttributionResult = interruptAttribution.Stop();
				if (!string.IsNullOrEmpty(interruptAttributionResult.Error))
				{
					error = interruptAttributionResult.Error;
					return null;
				}
				return interruptAttributionResult;
			}
			catch (Exception ex)
			{
				error = ex.GetType().Name;
				try
				{
					interruptAttribution.Stop();
				}
				catch
				{
				}
				return null;
			}
		}

		internal static bool DisplayResetSinceBoot()
		{
			try
			{
				string query = "*[System[Provider[@Name='Display'] and (EventID=4101) and TimeCreated[@SystemTime>='" + (DateTime.UtcNow - TimeSpan.FromMilliseconds(Native.GetTickCount64())).ToString("yyyy-MM-ddTHH:mm:ss.000Z", CultureInfo.InvariantCulture) + "']]]";
				using (EventLogReader eventLogReader = new EventLogReader(new EventLogQuery("System", PathType.LogName, query)))
				{
					using (EventRecord eventRecord = eventLogReader.ReadEvent())
					{
						return eventRecord != null;
					}
				}
			}
			catch
			{
				return false;
			}
		}

		private static bool Elevated()
		{
			return Native.IsElevated();
		}

		private static List<IrqDevice> Devices()
		{
			try
			{
				return IrqDeviceInventory.Enumerate();
			}
			catch
			{
				return new List<IrqDevice>();
			}
		}

		private static AutoCorePlan Plan()
		{
			AutoCoreStatus status = AutoCores.Status;
			if (!status.Manual || !status.Engaged || status.Pending)
			{
				return null;
			}
			return status.Plan;
		}

		private static IrqRebootState RebootState(string deviceId)
		{
			return engine.GetRebootState(deviceId);
		}

		private static bool Write(string deviceId, ulong mask, out string failure)
		{
			failure = "";
			bool enableSucceeded = engine.Enable(new List<string> { deviceId }, mask);
			if (!enableSucceeded)
			{
				failure = engine.LastResult.Describe();
			}
			return enableSucceeded;
		}

		private static bool Restore(string deviceId)
		{
			return engine.RestoreOnly(new List<string> { deviceId });
		}

		private static bool HooksRestoreAll()
		{
			return engine.Disable(null);
		}
	}
}
