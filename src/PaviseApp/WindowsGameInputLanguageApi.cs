using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
	internal sealed class WindowsGameInputLanguageApi : IGameInputLanguageApi
	{
		private sealed class ProcessLease : IGameInputProcessLease, IDisposable
		{
			private IntPtr handle;

			private readonly long creation;

			public long Creation
			{
				get
				{
					return creation;
				}
			}

			public bool IsAlive
			{
				get
				{
					if (handle == IntPtr.Zero)
					{
						throw new ObjectDisposedException("ProcessLease");
					}
					switch (WaitForSingleObject(handle, 0u))
					{
					case 258u:
						return true;
					case 0u:
						return false;
					default:
						throw new InvalidOperationException("Process lifetime could not be verified");
					}
				}
			}

			internal ProcessLease(IntPtr value, long birth)
			{
				handle = value;
				creation = birth;
			}

			public void Dispose()
			{
				IntPtr intPtr = handle;
				handle = IntPtr.Zero;
				if (intPtr != IntPtr.Zero)
				{
					CloseHandle(intPtr);
				}
			}
		}

		private struct NativeFileTime
		{
			internal uint Low;

			internal uint High;
		}

		private struct NativeRect
		{
			internal int Left;

			internal int Top;

			internal int Right;

			internal int Bottom;
		}

		private struct GuiThreadInfo
		{
			internal uint Size;

			internal uint Flags;

			internal IntPtr Active;

			internal IntPtr Focus;

			internal IntPtr Capture;

			internal IntPtr MenuOwner;

			internal IntPtr MoveSize;

			internal IntPtr Caret;

			internal NativeRect CaretRect;
		}

		private const uint QueryLimited = 4096u;

		private const uint Synchronize = 1048576u;

		private const uint WmInputLanguageChangeRequest = 80u;

		private const uint SendFlags = 35u;

		public int CurrentProcessId
		{
			get
			{
				RefuseNativeInTests();
				return (int)GetCurrentProcessId();
			}
		}

		public bool TryGetForeground(out GameInputWindow window)
		{
			RefuseNativeInTests();
			window = null;
			IntPtr foregroundWindow = GetForegroundWindow();
			if (foregroundWindow == IntPtr.Zero || !IsWindow(foregroundWindow) || !IsWindowVisible(foregroundWindow) || IsIconic(foregroundWindow))
			{
				return false;
			}
			uint pid;
			uint windowThreadProcessId = GetWindowThreadProcessId(foregroundWindow, out pid);
			if (windowThreadProcessId == 0 || pid == 0)
			{
				return false;
			}
			GuiThreadInfo info = default(GuiThreadInfo);
			info.Size = (uint)Marshal.SizeOf(typeof(GuiThreadInfo));
			if (!GetGUIThreadInfo(windowThreadProcessId, ref info) || info.Focus == IntPtr.Zero || !IsWindow(info.Focus) || GetAncestor(info.Focus, 2u) != foregroundWindow)
			{
				return false;
			}
			uint pid2;
			uint windowThreadProcessId2 = GetWindowThreadProcessId(info.Focus, out pid2);
			if (windowThreadProcessId2 != windowThreadProcessId || pid2 != pid || GetForegroundWindow() != foregroundWindow)
			{
				return false;
			}
			window = new GameInputWindow(foregroundWindow, info.Focus, windowThreadProcessId, (int)pid);
			return true;
		}

		public IGameInputProcessLease OpenProcess(int pid, out bool knownGone)
		{
			RefuseNativeInTests();
			knownGone = false;
			IntPtr intPtr = OpenProcessNative(1052672u, false, (uint)pid);
			if (intPtr == IntPtr.Zero)
			{
				knownGone = Marshal.GetLastWin32Error() == 87;
				return null;
			}
			try
			{
				NativeFileTime creation;
				NativeFileTime exit;
				NativeFileTime kernel;
				NativeFileTime user;
				if (!GetProcessTimes(intPtr, out creation, out exit, out kernel, out user))
				{
					return null;
				}
				long lowMask = (long)(((ulong)creation.High << 32) | creation.Low);
				if (lowMask <= 0)
				{
					return null;
				}
				ProcessLease result = new ProcessLease(intPtr, lowMask);
				intPtr = IntPtr.Zero;
				return result;
			}
			finally
			{
				if (intPtr != IntPtr.Zero)
				{
					CloseHandle(intPtr);
				}
			}
		}

		public IntPtr[] GetKeyboardLayouts()
		{
			RefuseNativeInTests();
			int keyboardLayoutList = GetKeyboardLayoutList(0, null);
			if (keyboardLayoutList <= 0)
			{
				if (keyboardLayoutList != 0)
				{
					return null;
				}
				return new IntPtr[0];
			}
			if (keyboardLayoutList > 512)
			{
				return null;
			}
			IntPtr[] nativeHandleItems = new IntPtr[keyboardLayoutList];
			int keyboardLayoutList2 = GetKeyboardLayoutList(nativeHandleItems.Length, nativeHandleItems);
			if (keyboardLayoutList2 <= 0 || keyboardLayoutList2 > nativeHandleItems.Length)
			{
				return null;
			}
			if (keyboardLayoutList2 != nativeHandleItems.Length)
			{
				Array.Resize(ref nativeHandleItems, keyboardLayoutList2);
			}
			return nativeHandleItems;
		}

		public IntPtr GetKeyboardLayout(uint threadId)
		{
			RefuseNativeInTests();
			return GetKeyboardLayoutNative(threadId);
		}

		public bool IsIme(IntPtr layout)
		{
			RefuseNativeInTests();
			return ImmIsIME(layout);
		}

		public GameInputSendResult SendRequest(GameInputWindow window, IntPtr layout, uint timeoutMs, Func<bool> mayContinue, out int error)
		{
			RefuseNativeInTests();
			error = 0;
			if (mayContinue == null || !mayContinue())
			{
				return GameInputSendResult.NotIssued;
			}
			SetLastError(0u);
			UIntPtr result;
			IntPtr intPtr = SendMessageTimeout(window.Focus, 80u, UIntPtr.Zero, layout, 35u, timeoutMs, out result);
			if (intPtr != IntPtr.Zero)
			{
				return GameInputSendResult.Processed;
			}
			error = Marshal.GetLastWin32Error();
			return GameInputSendResult.Failed;
		}

		private static void RefuseNativeInTests()
		{
		}

		[DllImport("user32.dll")]
		private static extern IntPtr GetForegroundWindow();

		[DllImport("user32.dll")]
		private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);

		[DllImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool IsWindow(IntPtr window);

		[DllImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool IsWindowVisible(IntPtr window);

		[DllImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool IsIconic(IntPtr window);

		[DllImport("user32.dll")]
		private static extern IntPtr GetAncestor(IntPtr window, uint flags);

		[DllImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

		[DllImport("user32.dll")]
		private static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[] layouts);

		[DllImport("user32.dll", EntryPoint = "GetKeyboardLayout")]
		private static extern IntPtr GetKeyboardLayoutNative(uint thread);

		[DllImport("imm32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool ImmIsIME(IntPtr layout);

		[DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
		private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);

		[DllImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
		private static extern IntPtr OpenProcessNative(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

		[DllImport("kernel32.dll")]
		private static extern uint GetCurrentProcessId();

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool GetProcessTimes(IntPtr handle, out NativeFileTime creation, out NativeFileTime exit, out NativeFileTime kernel, out NativeFileTime user);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern uint WaitForSingleObject(IntPtr handle, uint timeout);

		[DllImport("kernel32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool CloseHandle(IntPtr handle);

		[DllImport("kernel32.dll")]
		private static extern void SetLastError(uint error);
	}
}
