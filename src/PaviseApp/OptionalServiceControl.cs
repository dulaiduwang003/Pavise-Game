using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
	internal sealed class OptionalServiceControl : IOptionalServiceControl, IOptionalServiceStopClassification
	{
		private sealed class ServiceConfiguration
		{
			public uint Type;

			public uint StartType;

			public string[] Dependencies;

			public string Identity;
		}

		private struct SERVICE_STATUS
		{
			public uint Type;

			public uint State;

			public uint ControlsAccepted;

			public uint Win32ExitCode;

			public uint SpecificExitCode;

			public uint CheckPoint;

			public uint WaitHint;
		}

#pragma warning disable 0649
		private struct QUERY_SERVICE_CONFIG
		{
			public uint Type;

			public uint StartType;

			public uint ErrorControl;

			public IntPtr BinaryPath;

			public IntPtr LoadOrderGroup;

			public uint TagId;

			public IntPtr Dependencies;

			public IntPtr Account;

			public IntPtr DisplayName;
		}

		private struct PRINTER_INFO_2
		{
			public IntPtr ServerName;

			public IntPtr PrinterName;

			public IntPtr ShareName;

			public IntPtr PortName;

			public IntPtr DriverName;

			public IntPtr Comment;

			public IntPtr Location;

			public IntPtr DevMode;

			public IntPtr SeparatorFile;

			public IntPtr PrintProcessor;

			public IntPtr DataType;

			public IntPtr Parameters;

			public IntPtr SecurityDescriptor;

			public uint Attributes;

			public uint Priority;

			public uint DefaultPriority;

			public uint StartTime;

			public uint UntilTime;

			public uint Status;

			public uint Jobs;

			public uint AveragePpm;
		}

		private struct SYSTEM_BOOT_ENVIRONMENT_INFORMATION
		{
			public Guid BootIdentifier;

			public uint FirmwareType;

			public ulong BootFlags;
		}
#pragma warning restore 0649

		private const uint ScManagerConnect = 1u;

		private const uint ServiceQueryConfig = 1u;

		private const uint ServiceQueryStatus = 4u;

		private const uint ServiceEnumerateDependents = 8u;

		private const uint ServiceStart = 16u;

		private const uint ServiceStop = 32u;

		private const uint QueryAccess = 13u;

		private const uint ServiceActive = 1u;

		private const uint ServiceAcceptStop = 1u;

		private const int ServiceStopped = 1;

		private const int ServiceRunning = 4;

		private const int ServiceDisabled = 4;

		private const int ErrorPathNotFound = 3;

		private const int ErrorAccessDenied = 5;

		private const int ErrorInvalidHandle = 6;

		private const int ErrorInsufficientBuffer = 122;

		private const int ErrorMoreData = 234;

		private const int ErrorServiceRequestTimeout = 1053;

		private const int ErrorServiceDatabaseLocked = 1055;

		private const int ErrorServiceAlreadyRunning = 1056;

		private const int ErrorServiceDisabled = 1058;

		private const int ErrorServiceDoesNotExist = 1060;

		private const int ErrorServiceDependencyFail = 1068;

		private const int ErrorServiceLogonFailed = 1069;

		private const int ErrorServiceMarkedForDelete = 1072;

		private const int ErrorServiceDependencyDeleted = 1075;

		private const int ErrorInvalidServiceControl = 1052;

		private const int ErrorServiceCannotAcceptControl = 1061;

		private const int ErrorServiceNotActive = 1062;

		private const int MaxConfigurationBytes = 65536;

		private const int MaxPrinterBytes = 16777216;

		private const uint PrinterEnumLocal = 2u;

		private const uint PrinterEnumConnections = 4u;

		private const uint PrinterEnumCategoryAll = 33554432u;

		private const uint PrinterAttributeShared = 8u;

		private const uint PrinterAttributeNetwork = 16u;

		private const uint PrinterBusyStates = 18176u;

		public bool LastStopWasNormalSkip { get; private set; }

		public bool TryGetBootIdentity(out string identity)
		{
			RefuseNativeInTests();
			identity = "";
			try
			{
				identity = BootSessionIdentity.Read();
				return BootSessionIdentity.Valid(identity);
			}
			catch
			{
				return false;
			}
		}

		public bool TryQuery(string name, out OptionalServiceSnapshot snapshot)
		{
			RefuseNativeInTests();
			snapshot = new OptionalServiceSnapshot
			{
				Configuration = ""
			};
			if (string.IsNullOrWhiteSpace(name))
			{
				return false;
			}
			IntPtr intPtr = IntPtr.Zero;
			IntPtr intPtr2 = IntPtr.Zero;
			try
			{
				intPtr = OpenSCManagerW(null, null, 1u);
				if (intPtr == IntPtr.Zero)
				{
					return false;
				}
				intPtr2 = OpenServiceW(intPtr, name, 13u);
				if (intPtr2 == IntPtr.Zero)
				{
					return Marshal.GetLastWin32Error() == 1060;
				}
				ServiceConfiguration configuration;
				return TryReadSnapshot(intPtr2, out snapshot, out configuration);
			}
			catch
			{
				return false;
			}
			finally
			{
				if (intPtr2 != IntPtr.Zero)
				{
					CloseServiceHandle(intPtr2);
				}
				if (intPtr != IntPtr.Zero)
				{
					CloseServiceHandle(intPtr);
				}
			}
		}

		internal static bool TryQueryHandleForPrivacy(IntPtr service, out OptionalServiceSnapshot snapshot)
		{
			RefuseNativeInTests();
			snapshot = null;
			if (service == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				ServiceConfiguration configuration;
				return TryReadSnapshot(service, out snapshot, out configuration);
			}
			catch
			{
				return false;
			}
		}

		public bool TryStop(string name, OptionalServiceSnapshot expected, Func<bool> mayContinue)
		{
			RefuseNativeInTests();
			LastStopWasNormalSkip = true;
			if (!OptionalServicePauseEngine.IsAllowed(name) || object.ReferenceEquals(expected, null) || !CanStop(expected) || string.IsNullOrEmpty(expected.Configuration) || !MayContinue(mayContinue))
			{
				return false;
			}
			if (IsPrintingService(name) && !IsPrintingIdle(mayContinue))
			{
				return false;
			}
			IntPtr intPtr = IntPtr.Zero;
			IntPtr intPtr2 = IntPtr.Zero;
			bool stopReady = false;
			try
			{
				intPtr = OpenSCManagerW(null, null, 1u);
				if (intPtr == IntPtr.Zero)
				{
					LastStopWasNormalSkip = false;
					return false;
				}
				if (!MayContinue(mayContinue))
				{
					return false;
				}
				intPtr2 = OpenServiceW(intPtr, name, 45u);
				if (intPtr2 == IntPtr.Zero)
				{
					int lastWin32Error = Marshal.GetLastWin32Error();
					LastStopWasNormalSkip = lastWin32Error == 1060 || lastWin32Error == 1072;
					return false;
				}
				if (!MayContinue(mayContinue))
				{
					return false;
				}
				OptionalServiceSnapshot snapshot;
				ServiceConfiguration configuration;
				if (!TryReadSnapshot(intPtr2, out snapshot, out configuration, mayContinue))
				{
					LastStopWasNormalSkip = !MayContinue(mayContinue);
					return false;
				}
				if (!CanStop(snapshot) || !IsWin32Service(configuration.Type) || !string.Equals(snapshot.Configuration, expected.Configuration, StringComparison.Ordinal))
				{
					return false;
				}
				if (!MayContinue(mayContinue))
				{
					return false;
				}
				stopReady = true;
				SERVICE_STATUS status;
				bool controlServiceSucceeded = ControlService(intPtr2, 1u, out status);
				int lastWin32Error2 = ((!controlServiceSucceeded) ? Marshal.GetLastWin32Error() : 0);
				LastStopWasNormalSkip = !controlServiceSucceeded && (lastWin32Error2 == 1062 || lastWin32Error2 == 1052 || lastWin32Error2 == 1061);
				if (!controlServiceSucceeded && lastWin32Error2 == 1053)
				{
					throw new TimeoutException("The service stop request timed out; its ownership is unknown.");
				}
				if (!controlServiceSucceeded && lastWin32Error2 != 1062 && lastWin32Error2 != 1052 && lastWin32Error2 != 1061)
				{
					throw new Win32Exception(lastWin32Error2, "The service stop request failed; its outcome is unknown.");
				}
				return controlServiceSucceeded;
			}
			catch
			{
				if (stopReady)
				{
					throw;
				}
				return false;
			}
			finally
			{
				if (intPtr2 != IntPtr.Zero)
				{
					CloseServiceHandle(intPtr2);
				}
				if (intPtr != IntPtr.Zero)
				{
					CloseServiceHandle(intPtr);
				}
			}
		}

		public OptionalServiceStartResult TryStart(string name, string expectedConfiguration)
		{
			RefuseNativeInTests();
			if (!OptionalServicePauseEngine.IsAllowed(name) || string.IsNullOrEmpty(expectedConfiguration))
			{
				return OptionalServiceStartResult.OwnershipChanged;
			}
			IntPtr intPtr = IntPtr.Zero;
			IntPtr intPtr2 = IntPtr.Zero;
			bool startReady = false;
			try
			{
				intPtr = OpenSCManagerW(null, null, 1u);
				if (intPtr == IntPtr.Zero)
				{
					return OptionalServiceStartResult.NotIssued;
				}
				intPtr2 = OpenServiceW(intPtr, name, 21u);
				if (intPtr2 == IntPtr.Zero)
				{
					return IsStartOwnershipError(Marshal.GetLastWin32Error()) ? OptionalServiceStartResult.OwnershipChanged : OptionalServiceStartResult.NotIssued;
				}
				ServiceConfiguration configuration;
				bool ownershipChanged;
				if (!TryReadStartState(intPtr2, expectedConfiguration, out configuration, out ownershipChanged))
				{
					return ownershipChanged ? OptionalServiceStartResult.OwnershipChanged : OptionalServiceStartResult.NotIssued;
				}
				HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				hashSet.Add(name);
				if (!DependenciesRunning(intPtr, configuration.Dependencies, hashSet, 0))
				{
					return OptionalServiceStartResult.NotIssued;
				}
				if (!TryReadStartState(intPtr2, expectedConfiguration, out configuration, out ownershipChanged))
				{
					return ownershipChanged ? OptionalServiceStartResult.OwnershipChanged : OptionalServiceStartResult.NotIssued;
				}
				startReady = true;
				bool startServiceWSucceeded = StartServiceW(intPtr2, 0u, IntPtr.Zero);
				int error = ((!startServiceWSucceeded) ? Marshal.GetLastWin32Error() : 0);
				return startServiceWSucceeded ? OptionalServiceStartResult.Accepted : ClassifyStartFailure(error);
			}
			catch
			{
				if (startReady)
				{
					throw;
				}
				return OptionalServiceStartResult.NotIssued;
			}
			finally
			{
				if (intPtr2 != IntPtr.Zero)
				{
					CloseServiceHandle(intPtr2);
				}
				if (intPtr != IntPtr.Zero)
				{
					CloseServiceHandle(intPtr);
				}
			}
		}

		public bool IsPrintingIdle()
		{
			RefuseNativeInTests();
			return IsPrintingIdle(null);
		}

		private bool IsPrintingIdle(Func<bool> mayContinue)
		{
			if (!MayContinue(mayContinue) || !SpoolerReady() || !MayContinue(mayContinue))
			{
				return false;
			}
			try
			{
				uint needed;
				uint count;
				bool enumPrintersWSucceeded = EnumPrintersW(4u, null, 4u, IntPtr.Zero, 0u, out needed, out count);
				if (!MayContinue(mayContinue) || !enumPrintersWSucceeded || count != 0)
				{
					return false;
				}
				uint flags = 33554434u;
				bool enumPrintersWSucceeded2 = EnumPrintersW(flags, null, 2u, IntPtr.Zero, 0u, out needed, out count);
				int lastWin32Error = ((!enumPrintersWSucceeded2) ? Marshal.GetLastWin32Error() : 0);
				if (!MayContinue(mayContinue))
				{
					return false;
				}
				if (enumPrintersWSucceeded2)
				{
					return count == 0 && SpoolerReady() && MayContinue(mayContinue);
				}
				if (lastWin32Error != 122)
				{
					return false;
				}
				for (int i = 0; i < 3; i++)
				{
					if (needed == 0 || needed > 16777216)
					{
						return false;
					}
					uint neededSnapshot = needed;
					IntPtr intPtr = Marshal.AllocHGlobal((int)neededSnapshot);
					try
					{
						enumPrintersWSucceeded2 = EnumPrintersW(flags, null, 2u, intPtr, neededSnapshot, out needed, out count);
						lastWin32Error = ((!enumPrintersWSucceeded2) ? Marshal.GetLastWin32Error() : 0);
						if (!MayContinue(mayContinue))
						{
							return false;
						}
						if (enumPrintersWSucceeded2)
						{
							int sizeOfResult = Marshal.SizeOf(typeof(PRINTER_INFO_2));
							if (count > neededSnapshot / (uint)sizeOfResult)
							{
								return false;
							}
							for (uint iterationIndex = 0u; iterationIndex < count; iterationIndex++)
							{
								PRINTER_INFO_2 pRINTER_INFO_ = (PRINTER_INFO_2)Marshal.PtrToStructure(IntPtr.Add(intPtr, (int)iterationIndex * sizeOfResult), typeof(PRINTER_INFO_2));
								if (pRINTER_INFO_.Jobs != 0 || (pRINTER_INFO_.Attributes & 0x18u) != 0 || (pRINTER_INFO_.Status & 0x4700u) != 0)
								{
									return false;
								}
							}
							return SpoolerReady() && MayContinue(mayContinue);
						}
						if (lastWin32Error != 122 || needed <= neededSnapshot)
						{
							return false;
						}
					}
					finally
					{
						Marshal.FreeHGlobal(intPtr);
					}
				}
				return false;
			}
			catch
			{
				return false;
			}
		}

		private bool SpoolerReady()
		{
			OptionalServiceSnapshot snapshot;
			if (TryQuery("Spooler", out snapshot) && snapshot.Exists && snapshot.State == 4)
			{
				return snapshot.StartType != 4;
			}
			return false;
		}

		private static bool IsPrintingService(string name)
		{
			if (!string.Equals(name, "Spooler", StringComparison.OrdinalIgnoreCase))
			{
				return string.Equals(name, "PrintNotify", StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}

		private static bool CanStop(OptionalServiceSnapshot snapshot)
		{
			if (snapshot.Exists && snapshot.State == 4 && snapshot.StartType != 4 && snapshot.AcceptsStop)
			{
				return !snapshot.HasActiveDependents;
			}
			return false;
		}

		private static bool TryReadStartState(IntPtr service, string expectedConfiguration, out ServiceConfiguration configuration, out bool ownershipChanged)
		{
			configuration = null;
			ownershipChanged = false;
			SERVICE_STATUS status;
			if (!QueryServiceStatus(service, out status))
			{
				ownershipChanged = IsStartOwnershipError(Marshal.GetLastWin32Error());
				return false;
			}
			if (status.State != 1)
			{
				ownershipChanged = true;
				return false;
			}
			int error;
			if (!TryReadConfiguration(service, out configuration, out error))
			{
				ownershipChanged = IsStartOwnershipError(error);
				return false;
			}
			ownershipChanged = configuration.StartType == 4 || !IsWin32Service(configuration.Type) || !string.Equals(configuration.Identity, expectedConfiguration, StringComparison.Ordinal);
			return !ownershipChanged;
		}

		private static bool IsStartOwnershipError(int error)
		{
			if (error != 1056 && error != 1058 && error != 1060)
			{
				return error == 1072;
			}
			return true;
		}

		private static OptionalServiceStartResult ClassifyStartFailure(int error)
		{
			if (IsStartOwnershipError(error))
			{
				return OptionalServiceStartResult.OwnershipChanged;
			}
			switch (error)
			{
			case 3:
			case 5:
			case 6:
			case 1055:
			case 1068:
			case 1069:
			case 1075:
				return OptionalServiceStartResult.NotIssued;
			case 1053:
				throw new TimeoutException("The service start request timed out; its outcome is unknown.");
			default:
				throw new Win32Exception(error, "The service start request failed; its outcome is unknown.");
			}
		}

		private static bool IsWin32Service(uint type)
		{
			if ((type & 0x30u) != 0)
			{
				return (type & 3) == 0;
			}
			return false;
		}

		private static bool DependenciesRunning(IntPtr scm, string[] dependencies, HashSet<string> checkedServices, int depth)
		{
			if (depth > 32)
			{
				return false;
			}
			foreach (string entryText in dependencies)
			{
				if (string.IsNullOrEmpty(entryText) || entryText[0] == '+')
				{
					return false;
				}
				if (checkedServices.Contains(entryText))
				{
					continue;
				}
				if (checkedServices.Count >= 128)
				{
					return false;
				}
				IntPtr intPtr = OpenServiceW(scm, entryText, 5u);
				if (intPtr == IntPtr.Zero)
				{
					return false;
				}
				try
				{
					SERVICE_STATUS status;
					ServiceConfiguration configuration;
					if (!QueryServiceStatus(intPtr, out status) || status.State != 4 || !TryReadConfiguration(intPtr, out configuration))
					{
						return false;
					}
					checkedServices.Add(entryText);
					if (!DependenciesRunning(scm, configuration.Dependencies, checkedServices, depth + 1))
					{
						return false;
					}
				}
				finally
				{
					CloseServiceHandle(intPtr);
				}
			}
			return true;
		}

		private static bool TryReadSnapshot(IntPtr service, out OptionalServiceSnapshot snapshot, out ServiceConfiguration configuration, Func<bool> mayContinue = null)
		{
			snapshot = new OptionalServiceSnapshot
			{
				Configuration = ""
			};
			configuration = null;
			SERVICE_STATUS status;
			bool active;
			if (!TryReadConfiguration(service, out configuration, mayContinue) || !MayContinue(mayContinue) || !QueryServiceStatus(service, out status) || !MayContinue(mayContinue) || status.State < 1 || status.State > 7 || !TryHasActiveDependents(service, out active) || !MayContinue(mayContinue))
			{
				return false;
			}
			snapshot = new OptionalServiceSnapshot
			{
				Exists = true,
				State = (int)status.State,
				StartType = (int)configuration.StartType,
				AcceptsStop = ((status.ControlsAccepted & 1) != 0),
				HasActiveDependents = active,
				Configuration = configuration.Identity
			};
			return true;
		}

		private static bool TryHasActiveDependents(IntPtr service, out bool active)
		{
			active = false;
			uint needed;
			uint count;
			if (EnumDependentServicesW(service, 1u, IntPtr.Zero, 0u, out needed, out count))
			{
				active = count != 0;
				return true;
			}
			if (Marshal.GetLastWin32Error() != 234 || needed == 0)
			{
				return false;
			}
			active = true;
			return true;
		}

		private static bool TryReadConfiguration(IntPtr service, out ServiceConfiguration configuration, Func<bool> mayContinue = null)
		{
			int error;
			return TryReadConfiguration(service, out configuration, out error, mayContinue);
		}

		private static bool TryReadConfiguration(IntPtr service, out ServiceConfiguration configuration, out int error, Func<bool> mayContinue = null)
		{
			configuration = null;
			uint needed;
			bool serviceConfigW = QueryServiceConfigW(service, IntPtr.Zero, 0u, out needed);
			error = ((!serviceConfigW) ? Marshal.GetLastWin32Error() : 0);
			if (serviceConfigW || error != 122 || !MayContinue(mayContinue))
			{
				return false;
			}
			for (int i = 0; i < 3; i++)
			{
				if (needed < Marshal.SizeOf(typeof(QUERY_SERVICE_CONFIG)) || needed > 65536)
				{
					return false;
				}
				uint neededSnapshot = needed;
				IntPtr intPtr = Marshal.AllocHGlobal((int)neededSnapshot);
				try
				{
					serviceConfigW = QueryServiceConfigW(service, intPtr, neededSnapshot, out needed);
					error = ((!serviceConfigW) ? Marshal.GetLastWin32Error() : 0);
					if (!MayContinue(mayContinue))
					{
						return false;
					}
					if (serviceConfigW)
					{
						QUERY_SERVICE_CONFIG qUERY_SERVICE_CONFIG = (QUERY_SERVICE_CONFIG)Marshal.PtrToStructure(intPtr, typeof(QUERY_SERVICE_CONFIG));
						if (qUERY_SERVICE_CONFIG.StartType > 4)
						{
							return false;
						}
						string value;
						string value2;
						string value3;
						string value4;
						string[] names;
						if (!TryReadString(intPtr, (int)neededSnapshot, qUERY_SERVICE_CONFIG.BinaryPath, out value) || !TryReadString(intPtr, (int)neededSnapshot, qUERY_SERVICE_CONFIG.LoadOrderGroup, out value2) || !TryReadString(intPtr, (int)neededSnapshot, qUERY_SERVICE_CONFIG.Account, out value3) || !TryReadString(intPtr, (int)neededSnapshot, qUERY_SERVICE_CONFIG.DisplayName, out value4) || !TryReadDependencies(intPtr, (int)neededSnapshot, qUERY_SERVICE_CONFIG.Dependencies, out names))
						{
							return false;
						}
						StringBuilder stringBuilder = new StringBuilder("svc1|");
						AppendPart(stringBuilder, qUERY_SERVICE_CONFIG.Type.ToString(CultureInfo.InvariantCulture));
						AppendPart(stringBuilder, qUERY_SERVICE_CONFIG.StartType.ToString(CultureInfo.InvariantCulture));
						AppendPart(stringBuilder, qUERY_SERVICE_CONFIG.ErrorControl.ToString(CultureInfo.InvariantCulture));
						AppendPart(stringBuilder, qUERY_SERVICE_CONFIG.TagId.ToString(CultureInfo.InvariantCulture));
						AppendPart(stringBuilder, value);
						AppendPart(stringBuilder, value2);
						AppendPart(stringBuilder, value3);
						AppendPart(stringBuilder, value4);
						AppendPart(stringBuilder, names.Length.ToString(CultureInfo.InvariantCulture));
						string[] namesSnapshot = names;
						foreach (string value5 in namesSnapshot)
						{
							AppendPart(stringBuilder, value5);
						}
						configuration = new ServiceConfiguration
						{
							Type = qUERY_SERVICE_CONFIG.Type,
							StartType = qUERY_SERVICE_CONFIG.StartType,
							Dependencies = names,
							Identity = stringBuilder.ToString()
						};
						return true;
					}
					if (error != 122 || needed <= neededSnapshot)
					{
						return false;
					}
				}
				finally
				{
					Marshal.FreeHGlobal(intPtr);
				}
			}
			return false;
		}

		private static void AppendPart(StringBuilder target, string value)
		{
			target.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value)
				.Append('|');
		}

		private static bool MayContinue(Func<bool> mayContinue)
		{
			if (mayContinue == null)
			{
				return true;
			}
			try
			{
				return mayContinue();
			}
			catch
			{
				return false;
			}
		}

		private static bool TryReadString(IntPtr buffer, int capacity, IntPtr pointer, out string value)
		{
			value = "";
			if (pointer == IntPtr.Zero)
			{
				return true;
			}
			long remainingToInt64 = pointer.ToInt64() - buffer.ToInt64();
			if (remainingToInt64 < 0 || remainingToInt64 > capacity - 2 || (remainingToInt64 & 1) != 0)
			{
				return false;
			}
			int ratio = (capacity - (int)remainingToInt64) / 2;
			for (int i = 0; i < ratio; i++)
			{
				if (Marshal.ReadInt16(pointer, i * 2) == 0)
				{
					value = Marshal.PtrToStringUni(pointer, i);
					return true;
				}
			}
			return false;
		}

		private static bool TryReadDependencies(IntPtr buffer, int capacity, IntPtr pointer, out string[] names)
		{
			names = new string[0];
			if (pointer == IntPtr.Zero)
			{
				return true;
			}
			List<string> list = new List<string>();
			for (int i = 0; i < 128; i++)
			{
				string value;
				if (!TryReadString(buffer, capacity, pointer, out value))
				{
					return false;
				}
				if (value.Length == 0)
				{
					names = list.ToArray();
					return true;
				}
				list.Add(value);
				pointer = IntPtr.Add(pointer, (value.Length + 1) * 2);
			}
			return false;
		}

		private static void RefuseNativeInTests()
		{
		}

		[DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
		private static extern IntPtr OpenSCManagerW(string machine, string database, uint access);

		[DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
		private static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);

		[DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
		private static extern bool QueryServiceStatus(IntPtr service, out SERVICE_STATUS status);

		[DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
		private static extern bool QueryServiceConfigW(IntPtr service, IntPtr buffer, uint size, out uint needed);

		[DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
		private static extern bool EnumDependentServicesW(IntPtr service, uint state, IntPtr buffer, uint size, out uint needed, out uint count);

		[DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
		private static extern bool ControlService(IntPtr service, uint control, out SERVICE_STATUS status);

		[DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
		private static extern bool StartServiceW(IntPtr service, uint count, IntPtr arguments);

		[DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
		private static extern bool CloseServiceHandle(IntPtr service);

		[DllImport("winspool.drv", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
		private static extern bool EnumPrintersW(uint flags, string name, uint level, IntPtr buffer, uint size, out uint needed, out uint count);

		[DllImport("ntdll.dll", ExactSpelling = true)]
		private static extern int NtQuerySystemInformation(int informationClass, out SYSTEM_BOOT_ENVIRONMENT_INFORMATION information, uint size, out uint returned);
	}
}
