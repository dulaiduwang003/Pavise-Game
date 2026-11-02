using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PaviseApp
{
	internal static class FreezeNative
	{
		private sealed class StateLease : SafeHandleZeroOrMinusOneIsInvalid
		{
			internal StateLease(IntPtr value)
				: base(true)
			{
				SetHandle(value);
			}

			protected override bool ReleaseHandle()
			{
				return Native.CloseHandle(handle);
			}
		}

		internal static readonly bool Supported = Detect();

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
		private static extern IntPtr GetModuleHandle(string name);

		[DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
		private static extern IntPtr GetProcAddress(IntPtr module, string name);

		[DllImport("ntdll.dll")]
		private static extern int NtCreateProcessStateChange(out IntPtr state, uint access, IntPtr attributes, IntPtr process, uint reserved);

		[DllImport("ntdll.dll")]
		private static extern int NtChangeProcessState(IntPtr state, IntPtr process, int change, IntPtr info, UIntPtr size, uint reserved);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool IsProcessCritical(IntPtr process, out bool critical);

		internal static bool IsAlive(IntPtr process)
		{
			if (process != IntPtr.Zero)
			{
				return Native.WaitForSingleObject(process, 0u) == 258;
			}
			return false;
		}

		private static bool Detect()
		{
			try
			{
				IntPtr moduleHandle = GetModuleHandle("ntdll.dll");
				return GetProcAddress(moduleHandle, "NtCreateProcessStateChange") != IntPtr.Zero && GetProcAddress(moduleHandle, "NtChangeProcessState") != IntPtr.Zero;
			}
			catch
			{
				return false;
			}
		}

		internal static IDisposable Acquire(FreezeTarget target, int ownerSession, int ownerPid)
		{
			string detail;
			return Acquire(target, ownerSession, ownerPid, out detail);
		}

		internal static IDisposable Acquire(FreezeTarget target, int ownerSession, int ownerPid, out string detail)
		{
			detail = "unsupported-api";
			if (!Supported)
			{
				return null;
			}
			detail = "invalid-target";
			if (target == null || target.Pid <= 4 || target.Pid == ownerPid)
			{
				return null;
			}
			IntPtr intPtr = Native.OpenProcess(1055232, false, target.Pid);
			if (intPtr == IntPtr.Zero)
			{
				detail = "OpenProcess win32=" + Marshal.GetLastWin32Error();
				return null;
			}
			StateLease stateLease = null;
			try
			{
				detail = "creation-query";
				long creation;
				long cpu;
				ulong io;
				if (!Native.QueryProcessSample(intPtr, out creation, out cpu, out io))
				{
					return null;
				}
				detail = "creation-mismatch";
				if (creation != target.Creation)
				{
					return null;
				}
				detail = "path-mismatch-or-unavailable";
				if (!string.Equals(Native.ImagePath(intPtr), target.Path, StringComparison.OrdinalIgnoreCase))
				{
					return null;
				}
				string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(target.Path);
				detail = "protected-runtime";
				if (FamilyBoundary.CatalogProtected(target.Pid, creation, fileNameWithoutExtension, target.Path))
				{
					return null;
				}
				detail = "unapproved-background-role";
				if (0 == 0 && !StrongBackgroundPolicy.ApprovedImage(target.Path))
				{
					return null;
				}
				detail = "session-query";
				int sessionId;
				if (!Native.TryGetLiveProcessSessionId(intPtr, target.Pid, out sessionId))
				{
					return null;
				}
				detail = "session-mismatch";
				if (sessionId != ownerSession)
				{
					return null;
				}
				bool critical;
				if (!IsProcessCritical(intPtr, out critical))
				{
					detail = "IsProcessCritical win32=" + Marshal.GetLastWin32Error();
					return null;
				}
				detail = "critical-process";
				if (critical)
				{
					return null;
				}
				detail = "process-exited";
				if (!IsAlive(intPtr))
				{
					return null;
				}
				IntPtr state;
				int num = NtCreateProcessStateChange(out state, 1u, IntPtr.Zero, intPtr, 0u);
				detail = "NtCreateProcessStateChange ntstatus=0x" + num.ToString("X8");
				if (state != IntPtr.Zero)
				{
					stateLease = new StateLease(state);
				}
				if (num < 0 || stateLease == null)
				{
					return null;
				}
				num = NtChangeProcessState(state, intPtr, 0, IntPtr.Zero, UIntPtr.Zero, 0u);
				detail = "NtChangeProcessState ntstatus=0x" + num.ToString("X8");
				if (num < 0)
				{
					return null;
				}
				detail = "state-handle-owned ntstatus=0x" + num.ToString("X8");
				StateLease result = stateLease;
				stateLease = null;
				return result;
			}
			catch (Exception ex)
			{
				detail = detail + " exception=" + ex.GetType().Name;
				return null;
			}
			finally
			{
				if (stateLease != null)
				{
					stateLease.Dispose();
				}
				Native.CloseHandle(intPtr);
			}
		}
	}
}
