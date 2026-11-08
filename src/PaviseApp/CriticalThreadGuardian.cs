using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PaviseApp
{
	internal static class CriticalThreadGuardian
	{
		private struct DeleteDisposition
		{
			internal byte Delete;
		}

		private sealed class DirectoryPin : IDisposable
		{
			private IntPtr handle;

			private bool closeFault;

			internal readonly string Identity;

			internal bool Valid
			{
				get
				{
					FileIdentity info;
					if (handle != IntPtr.Zero && GetFileInformationByHandle(handle, out info) && FileKey(info) == Identity)
					{
						return (info.Attributes & 0x400) == 0;
					}
					return false;
				}
			}

			internal DirectoryPin(string path, string sid)
			{
				RequireAcl(path, sid, false);
				handle = CreateFile(path, 128u, 3u, IntPtr.Zero, 3u, 35651584u, IntPtr.Zero);
				if (handle == IntPtr.Zero || handle == new IntPtr(-1))
				{
					handle = IntPtr.Zero;
					throw new IOException("original session directory pin unavailable");
				}
				try
				{
					FileIdentity info;
					if (!GetFileInformationByHandle(handle, out info) || (info.Attributes & 0x400u) != 0 || (info.Attributes & 0x10) == 0)
					{
						throw new IOException("original directory identity unavailable");
					}
					Identity = FileKey(info);
				}
				catch (Exception cause)
				{
					HardThreadConstructionCleanup.CloseFailed(this, cause);
					throw;
				}
			}

			public void Dispose()
			{
				HardThreadRawHandleClosure.Close(ref handle, ref closeFault, CloseHandle);
			}
		}

		private sealed class ParentGuardian : IHardThreadGuardian, IDisposable
		{
			private sealed class CleanupFiles : IHardThreadCleanupFiles
			{
				private readonly ParentGuardian owner;

				private readonly HardThreadFileStore store;

				private readonly int[] openErrors = new int[6];

				private Scope Original
				{
					get
					{
						return owner.OriginalScope;
					}
				}

				private string DirectoryPath
				{
					get
					{
						return Path.Combine(Original.Root, "guards", Original.Target.SessionId.ToString("N"));
					}
				}

				private string ExecutablePath
				{
					get
					{
						return Path.Combine(DirectoryPath, "Pavise.HardThreadGuardian.exe");
					}
				}

				public bool OriginalSourceReady
				{
					get
					{
						try
						{
							if (!owner.childGone || !HardThreadCleanupPolicy.ChildSettled(owner.SpawnSucceeded, owner.ChildState) || owner.Parent == null || owner.Parent.State != 0 || !owner.Parent.Actor.Same(Original.Parent) || !owner.Parent.VerifyImage() || owner.Root == null || !owner.Root.Valid || owner.Root.Identity != Original.RootIdentity || owner.GuardsDirectory == null || !owner.GuardsDirectory.Valid)
							{
								return false;
							}
							RequireAcl(Original.Root, Original.Sid);
							if (!store.MatchesSettled(owner.settledSource, owner.cleanupState.Removed(HardThreadCleanupPart.Ledger)))
							{
								return false;
							}
							foreach (HardThreadCleanupPart value in Enum.GetValues(typeof(HardThreadCleanupPart)))
							{
								string path = PartPath(value);
								if (owner.cleanupState.Removed(value))
								{
									if (!Missing(path))
									{
										return false;
									}
								}
								else
								{
									if (owner.cleanupState.Deferred(value))
									{
										continue;
									}
									if (value == HardThreadCleanupPart.Directory)
									{
										if (owner.SessionDirectory != null)
										{
											if (!owner.SessionDirectory.Valid)
											{
												return false;
											}
											owner.directoryIdentity = owner.SessionDirectory.Identity;
										}
										if (string.IsNullOrEmpty(owner.directoryIdentity))
										{
											return false;
										}
									}
									else if (!VerifyFile(path, ExpectedHash(value)))
									{
										return false;
									}
								}
							}
							return true;
						}
						catch (Exception error)
						{
							owner.RememberClosureFailure(error);
							return false;
						}
					}
				}

				public bool ClosureCertain
				{
					get
					{
						return !owner.closeFault;
					}
				}

				internal CleanupFiles(ParentGuardian original, HardThreadFileStore store)
				{
					owner = original;
					this.store = store;
				}

				private string PartPath(HardThreadCleanupPart part)
				{
					string sessionIdText = Original.Target.SessionId.ToString("N");
					switch (part)
					{
					case HardThreadCleanupPart.Ledger:
						return Path.Combine(Original.Root, "hard-thread-" + sessionIdText + ".xml");
					case HardThreadCleanupPart.SessionLock:
						return Path.Combine(Original.Root, "hard-thread-" + sessionIdText + ".lock");
					case HardThreadCleanupPart.Configuration:
						return ExecutablePath + ".config";
					case HardThreadCleanupPart.Executable:
						return ExecutablePath;
					case HardThreadCleanupPart.Registration:
						return Path.Combine(DirectoryPath, "scope.xml");
					default:
						return DirectoryPath;
					}
				}

				private string ExpectedHash(HardThreadCleanupPart part)
				{
					switch (part)
					{
					case HardThreadCleanupPart.Ledger:
						return owner.settledSource.JournalHash;
					case HardThreadCleanupPart.SessionLock:
						return Hash(new byte[0]);
					case HardThreadCleanupPart.Configuration:
						if (!owner.ConfigurationCreated)
						{
							return null;
						}
						return Hash(Text("<configuration><startup><supportedRuntime version=\"v4.0\" sku=\".NETFramework,Version=v4.7.2\"/></startup></configuration>"));
					case HardThreadCleanupPart.Executable:
						if (!owner.ExecutableCreated)
						{
							return null;
						}
						return Original.Parent.ImageHash;
					case HardThreadCleanupPart.Registration:
						if (!owner.RegistrationCreated || owner.Scope == null)
						{
							return null;
						}
						return owner.Scope.Digest;
					default:
						return null;
					}
				}

				private static bool Missing(string path)
				{
					try
					{
						File.GetAttributes(path);
						return false;
					}
					catch (FileNotFoundException)
					{
						return true;
					}
					catch (DirectoryNotFoundException)
					{
						return true;
					}
				}

				private static bool VerifyFile(string path, string expected)
				{
					if (expected == null)
					{
						return Missing(path);
					}
					if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
					{
						return false;
					}
					FileStream source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
					bool valid = false;
					HardThreadConstructionCleanup.AcquireAndRelease(delegate
					{
						if (source.Length <= 67108864)
						{
							valid = Hash(source) == expected;
						}
					}, delegate
					{
						HardThreadConstructionCleanup.CloseOwned(source);
					});
					return valid;
				}

				public bool DeleteExact(HardThreadCleanupPart part)
				{
					try
					{
						if (part == HardThreadCleanupPart.Executable && owner.Child != null)
						{
							owner.Child.CloseImage();
						}
						if (part == HardThreadCleanupPart.Registration && owner.Source != null)
						{
							owner.Source.Dispose();
							owner.Source = null;
						}
						if (part == HardThreadCleanupPart.Directory && owner.SessionDirectory != null)
						{
							owner.SessionDirectory.Dispose();
							owner.SessionDirectory = null;
						}
						string path = PartPath(part);
						string expected = ExpectedHash(part);
						if (part != HardThreadCleanupPart.Directory && expected == null)
						{
							return Missing(path);
						}
						NoReparse(Path.GetDirectoryName(path));
						IntPtr handle = CreateFile(path, 2147549184u, 5u, IntPtr.Zero, 3u, (part == HardThreadCleanupPart.Directory) ? 35651584u : 2097152u, IntPtr.Zero);
						if (handle == IntPtr.Zero || handle == new IntPtr(-1))
						{
							openErrors[(int)part] = Marshal.GetLastWin32Error();
							return false;
						}
						bool marked = false;
						HardThreadConstructionCleanup.AcquireAndRelease(delegate
						{
							FileIdentity info;
							if (GetFileInformationByHandle(handle, out info) && (info.Attributes & 0x400) == 0)
							{
								if (part == HardThreadCleanupPart.Directory)
								{
									if ((info.Attributes & 0x10) == 0 || FileKey(info) != owner.directoryIdentity)
									{
										return;
									}
								}
								else
								{
									if ((info.Attributes & 0x10u) != 0)
									{
										return;
									}
									using (SafeFileHandle handle2 = new SafeFileHandle(handle, false))
									{
										using (FileStream fileStream = new FileStream(handle2, FileAccess.Read))
										{
											if (fileStream.Length > 67108864 || Hash(fileStream) != expected)
											{
												return;
											}
										}
									}
								}
								DeleteDisposition deleteDisposition = default(DeleteDisposition);
								deleteDisposition.Delete = 1;
								DeleteDisposition information = deleteDisposition;
								marked = SetFileInformationByHandle(handle, 4, ref information, 1u);
							}
						}, delegate
						{
							bool uncertain = false;
							HardThreadRawHandleClosure.Close(ref handle, ref uncertain, CloseHandle);
						});
						return marked;
					}
					catch (Exception error)
					{
						owner.RememberClosureFailure(error);
						return false;
					}
				}

				public bool HeldElsewhere(HardThreadCleanupPart part)
				{
					if (part == HardThreadCleanupPart.Directory || openErrors[(int)part] != 32)
					{
						return false;
					}
					try
					{
						string expectedHashResult = ExpectedHash(part);
						if (expectedHashResult == null || !VerifyFile(PartPath(part), expectedHashResult))
						{
							return false;
						}
					}
					catch (Exception error)
					{
						owner.RememberClosureFailure(error);
						return false;
					}
					NoteSettledLeftovers();
					return true;
				}
			}

			internal readonly string ControlledRoot;

			internal Scope Setup;

			internal bool SpawnSucceeded;

			internal bool DirectoryCreated;

			internal volatile bool controllerReleased;

			internal ActorPin Parent;

			internal DirectoryPin SessionDirectory;

			internal DirectoryPin GuardsDirectory;

			internal IntPtr UnverifiedSpawn;

			internal IntPtr SpawnThread;

			internal bool ExecutableCreated;

			internal bool ConfigurationCreated;

			internal bool RegistrationCreated;

			internal Func<HardThreadGuardianActor, bool> Unregister;

			private readonly HardThreadCleanupState cleanupState = new HardThreadCleanupState();

			private bool cleanupComplete;

			private bool protectionReleased;

			private bool childGone;

			private bool closeFault;

			private bool spawnThreadCloseFault;

			private bool spawnProcessCloseFault;

			private Exception unassignedCloseFailure;

			private string directoryIdentity;

			private Timer cleanupTimer;

			private int cleanupDelay = 1000;
			private int cleanupAttempts;
			private bool cleanupExitPhase;

			internal Scope Scope;

			internal ActorPin Child;

			internal RootPin Root;

			internal FileStream Source;

			internal NamedPipeServerStream Pipe;

			private RegisteredWaitHandle wait;

			private ActorWait waitObject;

			private Action lost;

			private readonly object lifetime = new object();

			private volatile bool failed;

			private volatile bool disposed;

			private HardThreadSettledSource settledSource;

			internal Scope OriginalScope
			{
				get
				{
					return Scope ?? Setup;
				}
			}

			private bool ControllerClosed
			{
				get
				{
					if (Child == null && Parent == null && Root == null && Source == null && Pipe == null && SessionDirectory == null && GuardsDirectory == null && wait == null && waitObject == null && UnverifiedSpawn == IntPtr.Zero)
					{
						return SpawnThread == IntPtr.Zero;
					}
					return false;
				}
			}

			private HardThreadRead ChildState
			{
				get
				{
					if (Child != null)
					{
						return Child.State;
					}
					if (UnverifiedSpawn == IntPtr.Zero)
					{
						return HardThreadRead.Unknown;
					}
					switch (WaitForSingleObject(UnverifiedSpawn, 0u))
					{
					default:
						return HardThreadRead.Unknown;
					case 258u:
						return HardThreadRead.Known;
					case 0u:
						return HardThreadRead.Gone;
					}
				}
			}

			public HardThreadGuardianActor Actor
			{
				get
				{
					lock (lifetime)
					{
						return (Scope == null) ? null : Scope.Child;
					}
				}
			}

			public bool ResourcesClosed
			{
				get
				{
					lock (lifetime)
					{
						return cleanupComplete && protectionReleased && ControllerClosed && !closeFault;
					}
				}
			}

			internal ParentGuardian(string root)
			{
				ControlledRoot = Canonical(root);
			}

			internal void RememberClosureFailure(Exception error)
			{
				if (!HardThreadClosureFailure.Contains(error))
				{
					return;
				}
				lock (lifetime)
				{
					closeFault = true;
					if (unassignedCloseFailure == null)
					{
						unassignedCloseFailure = error;
					}
				}
			}

			internal void ReleaseSpawnHandles(bool releaseProcess)
			{
				lock (lifetime)
				{
					try
					{
						HardThreadSpawnHandleClosure.Close(ref SpawnThread, ref spawnThreadCloseFault, ref UnverifiedSpawn, ref spawnProcessCloseFault, releaseProcess, CloseHandle);
					}
					catch (Exception error)
					{
						RememberClosureFailure(error);
						throw;
					}
				}
			}

			private void ScheduleCleanupLocked()
			{
				if (cleanupAttempts >= 3) return;
				if (cleanupTimer == null)
				{
					cleanupTimer = new Timer(delegate
					{
						try
						{
							Dispose();
						}
						catch
						{
							lock (lifetime)
							{
								ScheduleCleanupLocked();
							}
						}
					}, null, cleanupDelay, -1);
				}
				else
				{
					cleanupTimer.Change(cleanupDelay, -1);
				}
				cleanupDelay = Math.Min(30000, cleanupDelay * 2);
			}

			private void FinishProtectionRelease()
			{
				Func<HardThreadGuardianActor, bool> unregister;
				HardThreadGuardianActor arg;
				lock (lifetime)
				{
					if (!cleanupComplete || !ControllerClosed || closeFault || protectionReleased)
					{
						return;
					}
					unregister = Unregister;
					arg = ((Scope == null) ? null : Scope.Child);
				}
				bool matchesUnregister = unregister == null;
				if (unregister != null)
				{
					try
					{
						matchesUnregister = unregister(arg);
					}
					catch
					{
					}
				}
				lock (lifetime)
				{
					protectionReleased = matchesUnregister;
					if (matchesUnregister)
					{
						controllerReleased = true;
						RemoveController(this);
					}
					if (matchesUnregister && cleanupTimer != null)
					{
						cleanupTimer.Dispose();
						cleanupTimer = null;
					}
					else if (!matchesUnregister)
					{
						ScheduleCleanupLocked();
					}
				}
			}

			private void CloseControllerLocked()
			{
				if (wait != null)
				{
					try
					{
						wait.Unregister(null);
						wait = null;
					}
					catch
					{
					}
				}
				if (waitObject != null)
				{
					try
					{
						waitObject.Dispose();
						waitObject = null;
					}
					catch
					{
					}
				}
				if (Child != null)
				{
					try
					{
						Child.Dispose();
						Child = null;
					}
					catch
					{
						if (!Child.ClosureCertain)
						{
							closeFault = true;
						}
					}
				}
				try
				{
					ReleaseSpawnHandles(true);
				}
				catch
				{
					closeFault = true;
				}
				if (Parent != null)
				{
					try
					{
						Parent.Dispose();
						Parent = null;
					}
					catch
					{
						if (!Parent.ClosureCertain)
						{
							closeFault = true;
						}
					}
				}
				if (Source != null)
				{
					try
					{
						Source.Dispose();
						Source = null;
					}
					catch
					{
					}
				}
				if (SessionDirectory != null)
				{
					try
					{
						SessionDirectory.Dispose();
						SessionDirectory = null;
					}
					catch
					{
						closeFault = true;
					}
				}
				if (GuardsDirectory != null)
				{
					try
					{
						GuardsDirectory.Dispose();
						GuardsDirectory = null;
					}
					catch
					{
						closeFault = true;
					}
				}
				if (Root != null)
				{
					try
					{
						Root.Dispose();
						Root = null;
					}
					catch
					{
						closeFault = true;
					}
				}
			}

			private void TryCleanupLocked()
			{
				if (!childGone || OriginalScope == null || settledSource == null)
				{
					return;
				}
				try
				{
					HardThreadFileStore store = new HardThreadFileStore(OriginalScope.Root);
					HardThreadConstructionCleanup.AcquireAndRelease(delegate
					{
						CleanupFiles original = new CleanupFiles(this, store);
						cleanupComplete = cleanupState.Step(original);
					}, delegate
					{
						HardThreadConstructionCleanup.CloseOwned(store);
					});
				}
				catch (Exception error)
				{
					RememberClosureFailure(error);
				}
			}

			public void NoteSettled(HardThreadSettledSource original)
			{
				lock (lifetime)
				{
					if (OriginalScope == null || original == null || !OriginalScope.Target.Same(original.Target) || !string.Equals(OriginalScope.Root, Canonical(original.Root), StringComparison.OrdinalIgnoreCase))
					{
						throw new InvalidOperationException("settled source belongs to another original guardian");
					}
					settledSource = original;
				}
			}

			internal void Watch()
			{
				lock (lifetime)
				{
					WatchLocked();
				}
			}

			private void WatchLocked()
			{
				if (wait != null || (Child == null && UnverifiedSpawn == IntPtr.Zero))
				{
					return;
				}
				waitObject = new ActorWait((Child == null) ? UnverifiedSpawn : Child.Handle);
				wait = ThreadPool.RegisterWaitForSingleObject(waitObject, delegate
				{
					Action action;
					lock (lifetime)
					{
						failed = true;
						action = (disposed ? null : lost);
					}
					if (action != null)
					{
						try
						{
							action();
						}
						catch
						{
						}
					}
					if (disposed)
					{
						try
						{
							Dispose();
						}
						catch
						{
						}
					}
				}, null, -1, true);
			}

			public void OnLost(Action callback)
			{
				bool allConditionsMet;
				lock (lifetime)
				{
					lost = callback;
					allConditionsMet = failed && !disposed && callback != null;
				}
				if (!allConditionsMet)
				{
					return;
				}
				ThreadPool.QueueUserWorkItem(delegate
				{
					try
					{
						callback();
					}
					catch
					{
					}
				});
			}

			public void RequireForward()
			{
				lock (lifetime)
				{
					if (disposed || failed || Child == null || Child.State != 0)
					{
						throw new InvalidOperationException("original guardian is not alive");
					}
				}
			}

			public void Dispose()
			{
				lock (lifetime)
				{
					bool exited = childGone || ChildState == HardThreadRead.Gone;
					if (exited && !cleanupExitPhase)
					{
						cleanupExitPhase = true;
						cleanupAttempts = 0;
					}
					if (cleanupAttempts >= 3) return;
					cleanupAttempts++;
					// The first attempt of each phase is the ordinary stop/cleanup, not a failure.
					string cleanupLine = "Critical guardian cleanup: phase=" + (cleanupExitPhase ? "child-exit" : "live") + " attempt=" + cleanupAttempts + "/3";
					if (cleanupAttempts == 1) Logger.Info(cleanupLine); else Logger.Warn(cleanupLine);
					disposed = true;
					try
					{
						bool settled = false;
						if (Scope != null && settledSource != null)
						{
							HardThreadFileStore store = new HardThreadFileStore(Scope.Root);
							HardThreadConstructionCleanup.AcquireAndRelease(delegate
							{
								settled = store.MatchesSettled(settledSource, cleanupState.Removed(HardThreadCleanupPart.Ledger));
							}, delegate
							{
								HardThreadConstructionCleanup.CloseOwned(store);
							});
						}
						if (settled && !failed && Child != null && Child.State == HardThreadRead.Known && Pipe != null)
						{
							WritePacket(Pipe, Text("stop:" + Scope.Digest));
						}
					}
					catch (Exception error)
					{
						RememberClosureFailure(error);
					}
					if (Pipe != null)
					{
						try
						{
							Pipe.Dispose();
							Pipe = null;
						}
						catch
						{
						}
					}
					HardThreadRead childState = ChildState;
					if (childGone || childState == HardThreadRead.Gone || (Child == null && UnverifiedSpawn == IntPtr.Zero))
					{
						childGone = true;
						if (OriginalScope == null || (!DirectoryCreated && !SpawnSucceeded))
						{
							cleanupComplete = true;
						}
						else if (!cleanupComplete)
						{
							TryCleanupLocked();
						}
						if (cleanupComplete)
						{
							ReleaseLocked();
						}
						if (!cleanupComplete || !ControllerClosed)
						{
							ScheduleCleanupLocked();
						}
					}
					else
					{
						WatchLocked();
					}
				}
				FinishProtectionRelease();
			}

			private void ReleaseLocked()
			{
				CloseControllerLocked();
			}
		}

		private struct FileIdentity
		{
			internal uint Attributes;

			internal uint CreatedLow;

			internal uint CreatedHigh;

			internal uint AccessLow;

			internal uint AccessHigh;

			internal uint WrittenLow;

			internal uint WrittenHigh;

			internal uint Volume;

			internal uint SizeHigh;

			internal uint SizeLow;

			internal uint Links;

			internal uint IndexHigh;

			internal uint IndexLow;
		}

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		private struct StartupInfo
		{
			internal int Size;

			internal IntPtr Reserved;

			internal IntPtr Desktop;

			internal IntPtr Title;

			internal uint X;

			internal uint Y;

			internal uint Width;

			internal uint Height;

			internal uint XChars;

			internal uint YChars;

			internal uint Fill;

			internal uint Flags;

			internal ushort Show;

			internal ushort ReservedSize;

			internal IntPtr ReservedBytes;

			internal IntPtr Input;

			internal IntPtr Output;

			internal IntPtr Error;
		}

		private struct ProcessInfo
		{
			internal IntPtr Process;

			internal IntPtr Thread;

			internal uint Pid;

			internal uint Tid;
		}

		private sealed class RootPin : IDisposable
		{
			private IntPtr handle;

			private bool closeFault;

			internal readonly string Identity;

			internal bool Valid
			{
				get
				{
					FileIdentity info;
					if (handle != IntPtr.Zero && GetFileInformationByHandle(handle, out info) && (info.Attributes & 0x400) == 0)
					{
						return info.Volume.ToString("x8") + info.IndexHigh.ToString("x8") + info.IndexLow.ToString("x8") == Identity;
					}
					return false;
				}
			}

			internal RootPin(string root, string sid)
			{
				RequireAcl(root, sid);
				handle = CreateFile(root, 128u, 3u, IntPtr.Zero, 3u, 35651584u, IntPtr.Zero);
				if (handle == new IntPtr(-1))
				{
					handle = IntPtr.Zero;
					throw new IOException("original root pin unavailable");
				}
				try
				{
					FileIdentity info;
					if (!GetFileInformationByHandle(handle, out info) || (info.Attributes & 0x400u) != 0 || (info.Attributes & 0x10) == 0)
					{
						throw new IOException("original root pin invalid");
					}
					Identity = info.Volume.ToString("x8") + info.IndexHigh.ToString("x8") + info.IndexLow.ToString("x8");
				}
				catch (Exception cause)
				{
					HardThreadConstructionCleanup.CloseFailed(this, cause);
					throw;
				}
			}

			public void Dispose()
			{
				HardThreadRawHandleClosure.Close(ref handle, ref closeFault, CloseHandle);
			}
		}

		private sealed class ActorPin : IDisposable
		{
			internal IntPtr Handle;

			internal readonly HardThreadGuardianActor Actor;

			private FileStream image;

			private bool closeFault;

			internal HardThreadRead State
			{
				get
				{
					switch ((uint)((Handle == IntPtr.Zero) ? (-1) : ((int)WaitForSingleObject(Handle, 0u))))
					{
					default:
						return HardThreadRead.Unknown;
					case 258u:
						return HardThreadRead.Known;
					case 0u:
						return HardThreadRead.Gone;
					}
				}
			}

			internal bool ClosureCertain
			{
				get
				{
					return !closeFault;
				}
			}

			internal ActorPin(int pid, HardThreadGuardianActor expected, IntPtr originalSpawn = default(IntPtr))
			{
				if (originalSpawn == IntPtr.Zero)
				{
					Handle = OpenProcess(1053696u, false, pid);
				}
				else if (!DuplicateHandle(GetCurrentProcess(), originalSpawn, GetCurrentProcess(), out Handle, 1053696u, false, 0u))
				{
					throw new IOException("original spawn handle could not be retained");
				}
				if (Handle == IntPtr.Zero)
				{
					throw new IOException("original actor query denied");
				}
				try
				{
					StringBuilder stringBuilder = new StringBuilder(32768);
					int capacity = stringBuilder.Capacity;
					long born;
					long exit;
					long kernel;
					long user;
					if (GetProcessId(Handle) != (uint)pid || !GetProcessTimes(Handle, out born, out exit, out kernel, out user) || !QueryFullProcessImageName(Handle, 0u, stringBuilder, ref capacity))
					{
						throw new IOException("actor identity unknown");
					}
					image = new FileStream(stringBuilder.ToString(), FileMode.Open, FileAccess.Read, FileShare.Read);
					Actor = new HardThreadGuardianActor(pid, born, stringBuilder.ToString(), Hash(image));
					if (expected != null && !expected.Same(Actor))
					{
						throw new IOException("actor creation or image differs");
					}
				}
				catch (Exception cause)
				{
					HardThreadConstructionCleanup.CloseFailed(this, cause);
					throw;
				}
			}

			internal IntPtr Duplicate()
			{
				IntPtr duplicate;
				if (!DuplicateHandle(GetCurrentProcess(), Handle, GetCurrentProcess(), out duplicate, 0u, false, 2u))
				{
					throw new IOException("original target handle could not be retained");
				}
				return duplicate;
			}

			internal void CloseImage()
			{
				if (image != null)
				{
					image.Dispose();
					image = null;
				}
			}

			internal bool VerifyImage()
			{
				if (image != null)
				{
					return Hash(image) == Actor.ImageHash;
				}
				return false;
			}

			public void Dispose()
			{
				HardThreadPinClosure.Close(CloseImage, ref Handle, ref closeFault, CloseHandle);
			}
		}

		private sealed class TargetPin : IDisposable
		{
			internal IntPtr Handle;

			private bool closeFault;

			internal TargetPin(HardThreadTarget expected)
			{
				Handle = OpenProcess(1053696u, false, expected.ProcessId);
				if (Handle == IntPtr.Zero)
				{
					throw new IOException("original target handle unavailable");
				}
				try
				{
					StringBuilder stringBuilder = new StringBuilder(32768);
					int capacity = stringBuilder.Capacity;
					long born;
					long exit;
					long kernel;
					long user;
					if (!GetProcessTimes(Handle, out born, out exit, out kernel, out user) || born != expected.ProcessCreation || !QueryFullProcessImageName(Handle, 0u, stringBuilder, ref capacity) || !string.Equals(Canonical(stringBuilder.ToString()), expected.ExecutablePath, StringComparison.OrdinalIgnoreCase))
					{
						throw new IOException("original target creation differs");
					}
				}
				catch (Exception cause)
				{
					HardThreadConstructionCleanup.CloseFailed(this, cause);
					throw;
				}
			}

			internal IntPtr Duplicate()
			{
				IntPtr duplicate;
				if (!DuplicateHandle(GetCurrentProcess(), Handle, GetCurrentProcess(), out duplicate, 0u, false, 2u))
				{
					throw new IOException("retained target could not be duplicated");
				}
				return duplicate;
			}

			public void Dispose()
			{
				HardThreadRawHandleClosure.Close(ref Handle, ref closeFault, CloseHandle);
			}
		}

		private sealed class Scope
		{
			internal HardThreadTarget Target;

			internal HardThreadGuardianActor Parent;

			internal HardThreadGuardianActor Child;

			internal string Root;

			internal string RootIdentity;

			internal string Sid;

			internal string Nonce;

			internal byte[] Bytes;

			internal HardThreadOriginContext Origin;

			internal byte[] OriginSeal;

			internal string Digest
			{
				get
				{
					return Hash(Bytes);
				}
			}

			private byte[] Binding()
			{
				using (MemoryStream memoryStream = new MemoryStream())
				{
					using (BinaryWriter binaryWriter = new BinaryWriter(memoryStream, new UTF8Encoding(false), true))
					{
						binaryWriter.Write("Pavise.HardThread.Scope.V2");
						binaryWriter.Write(Target.SessionId.ToByteArray());
						binaryWriter.Write(Target.ProcessId);
						binaryWriter.Write(Target.ProcessCreation);
						binaryWriter.Write(Target.ExecutablePath);
						binaryWriter.Write(Target.AllowedMask);
						binaryWriter.Write(Root);
						binaryWriter.Write(RootIdentity);
						binaryWriter.Write(Sid);
						binaryWriter.Write(Nonce);
						HardThreadGuardianActor[] hardThreadGuardianActorItems = new HardThreadGuardianActor[2] { Parent, Child };
						foreach (HardThreadGuardianActor hardThreadGuardianActor in hardThreadGuardianActorItems)
						{
							binaryWriter.Write(hardThreadGuardianActor.Pid);
							binaryWriter.Write(hardThreadGuardianActor.Creation);
							binaryWriter.Write(hardThreadGuardianActor.Path);
							binaryWriter.Write(hardThreadGuardianActor.ImageHash);
						}
						binaryWriter.Write(Origin.Machine);
						binaryWriter.Write(Origin.Name);
						binaryWriter.Write(Origin.Boot);
					}
					return memoryStream.ToArray();
				}
			}

			internal byte[] Encode()
			{
				return Encode(new NativeOrigin());
			}

			internal byte[] Encode(IHardThreadOriginPlatform platform)
			{
				Origin = platform.Current();
				OriginSeal = platform.Seal(Binding());
				if (OriginSeal == null || OriginSeal.Length < 1 || OriginSeal.Length > 16384)
				{
					throw new InvalidDataException("bounded machine origin seal required");
				}
				using (MemoryStream memoryStream = new MemoryStream())
				{
					using (XmlWriter xmlWriter = XmlWriter.Create(memoryStream, new XmlWriterSettings
					{
						Encoding = new UTF8Encoding(false),
						CloseOutput = false
					}))
					{
						xmlWriter.WriteStartElement("originalHardThreadScope");
						Write(xmlWriter, "version", "2");
						Write(xmlWriter, "session", Target.SessionId);
						Write(xmlWriter, "pid", Target.ProcessId);
						Write(xmlWriter, "birth", Target.ProcessCreation);
						Write(xmlWriter, "path", Target.ExecutablePath);
						Write(xmlWriter, "allowed", Target.AllowedMask);
						Write(xmlWriter, "root", Root);
						Write(xmlWriter, "rootId", RootIdentity);
						Write(xmlWriter, "sid", Sid);
						Write(xmlWriter, "nonce", Nonce);
						WriteActor(xmlWriter, "parent", Parent);
						WriteActor(xmlWriter, "child", Child);
						Write(xmlWriter, "machine", Origin.Machine);
						Write(xmlWriter, "machineName", Origin.Name);
						Write(xmlWriter, "kernel", Origin.Boot);
						Write(xmlWriter, "originSeal", Convert.ToBase64String(OriginSeal));
						xmlWriter.WriteEndElement();
					}
					return Bytes = memoryStream.ToArray();
				}
			}

			private static void Write(XmlWriter writer, string key, object value)
			{
				writer.WriteAttributeString(key, Convert.ToString(value, CultureInfo.InvariantCulture));
			}

			private static void WriteActor(XmlWriter writer, string prefix, HardThreadGuardianActor actor)
			{
				Write(writer, prefix + "Pid", actor.Pid);
				Write(writer, prefix + "Birth", actor.Creation);
				Write(writer, prefix + "Path", actor.Path);
				Write(writer, prefix + "Hash", actor.ImageHash);
			}

			private static HardThreadGuardianActor ReadActor(XmlElement xml, string prefix)
			{
				return new HardThreadGuardianActor(int.Parse(xml.GetAttribute(prefix + "Pid")), long.Parse(xml.GetAttribute(prefix + "Birth")), xml.GetAttribute(prefix + "Path"), xml.GetAttribute(prefix + "Hash"));
			}

			internal static Scope Parse(byte[] bytes, string root, Guid session, string nonce, string actualSid, IHardThreadOriginPlatform platform, bool currentKernel)
			{
				if (bytes.Length < 32 || bytes.Length > 65536)
				{
					throw new InvalidDataException("guardian scope size");
				}
				XmlDocument xmlDocument = new XmlDocument();
				xmlDocument.XmlResolver = null;
				XmlDocument xmlDocument2 = xmlDocument;
				using (XmlReader reader = XmlReader.Create(new MemoryStream(bytes), new XmlReaderSettings
				{
					XmlResolver = null,
					DtdProcessing = DtdProcessing.Prohibit,
					MaxCharactersInDocument = 65536L
				}))
				{
					xmlDocument2.Load(reader);
				}
				XmlElement documentElement = xmlDocument2.DocumentElement;
				if (documentElement == null || documentElement.Name != "originalHardThreadScope" || documentElement.GetAttribute("version") != "2" || documentElement.HasChildNodes)
				{
					throw new InvalidDataException("guardian scope format or original machine domain is unavailable");
				}
				Scope scope = new Scope();
				scope.Target = new HardThreadTarget(Guid.Parse(documentElement.GetAttribute("session")), int.Parse(documentElement.GetAttribute("pid")), long.Parse(documentElement.GetAttribute("birth")), documentElement.GetAttribute("path"), ulong.Parse(documentElement.GetAttribute("allowed")));
				scope.Parent = ReadActor(documentElement, "parent");
				scope.Child = ReadActor(documentElement, "child");
				scope.Root = Canonical(documentElement.GetAttribute("root"));
				scope.RootIdentity = documentElement.GetAttribute("rootId");
				scope.Sid = documentElement.GetAttribute("sid");
				scope.Nonce = documentElement.GetAttribute("nonce");
				scope.Bytes = (byte[])bytes.Clone();
				scope.Origin = new HardThreadOriginContext(documentElement.GetAttribute("machine"), documentElement.GetAttribute("machineName"), documentElement.GetAttribute("kernel"));
				scope.OriginSeal = Convert.FromBase64String(documentElement.GetAttribute("originSeal"));
				Scope scope2 = scope;
				if (scope2.Target.SessionId != session || scope2.Nonce != nonce || !IsHash(nonce) || !string.Equals(scope2.Root, Canonical(root), StringComparison.OrdinalIgnoreCase) || scope2.Sid != actualSid || scope2.Parent.ImageHash != scope2.Child.ImageHash || !IsHash(scope2.Parent.ImageHash))
				{
					throw new InvalidDataException("original guardian scope mismatch");
				}
				HardThreadOriginContext hardThreadOriginContext = platform.Current();
				if (!scope2.Origin.SameMachine(hardThreadOriginContext) || (currentKernel && scope2.Origin.Boot != hardThreadOriginContext.Boot) || scope2.OriginSeal.Length < 1 || scope2.OriginSeal.Length > 16384 || Hash(platform.Unseal(scope2.OriginSeal)) != Hash(scope2.Binding()))
				{
					throw new InvalidDataException("original Windows protection or kernel domain differs");
				}
				string path = Path.Combine(scope2.Root, "guards", session.ToString("N"), "Pavise.HardThreadGuardian.exe");
				if (!string.Equals(Canonical(path), scope2.Child.Path, StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidDataException("guardian is outside the original runtime");
				}
				return scope2;
			}
		}

		private sealed class ActorWait : WaitHandle
		{
			internal ActorWait(IntPtr handle)
			{
				base.SafeWaitHandle = new SafeWaitHandle(handle, false);
			}
		}

		private sealed class NativeRegistration : IDisposable
		{
			internal Scope Scope;

			internal ActorPin Parent;

			internal TargetPin Target;

			internal RootPin Root;

			internal FileStream Source;

			internal NamedPipeClientStream Pipe;

			internal HardThreadGuardianHandshake Handshake;

			internal readonly object Coordination = new object();

			private HardThreadResourceClosure closure;

			private Exception unassignedCloseFailure;

			private volatile bool closingStarted;

			internal bool ResourcesClosed
			{
				get
				{
					lock (Coordination)
					{
						return unassignedCloseFailure == null && closure != null && closure.ResourcesClosed;
					}
				}
			}

			internal void RememberClosureFailure(Exception error)
			{
				if (HardThreadClosureFailure.Contains(error))
				{
					lock (Coordination)
					{
						unassignedCloseFailure = error;
					}
				}
			}

			internal void RequireStopped()
			{
				if (closingStarted || Parent == null || Root == null || Source == null || Handshake == null || !Handshake.RecoveryEligible || Parent.State != HardThreadRead.Gone)
				{
					throw new InvalidOperationException("original live registration has no stopped-parent proof");
				}
			}

			public void Dispose()
			{
				lock (Coordination)
				{
					closingStarted = true;
					if (closure == null)
					{
						closure = new HardThreadResourceClosure(Pipe, Source, Target, Parent, Root);
					}
					if (unassignedCloseFailure == null)
					{
						closure.Dispose();
						return;
					}
					try
					{
						closure.Dispose();
					}
					catch (Exception ex)
					{
						throw new AggregateException("Original registration construction and cleanup remain unconfirmed", unassignedCloseFailure, ex);
					}
					throw new HardThreadClosureFailure("Unassigned registration source closure is unconfirmed", unassignedCloseFailure);
				}
			}
		}

		internal sealed class StopProof : IDisposable
		{
			private NativeRegistration original;

			internal HardThreadTarget Target
			{
				get
				{
					RequireStopped();
					return original.Scope.Target;
				}
			}

			internal string CanonicalStore
			{
				get
				{
					RequireStopped();
					return original.Scope.Root;
				}
			}

			internal string SourceNonce
			{
				get
				{
					RequireStopped();
					return original.Scope.Nonce;
				}
			}

			internal string SourceScopeHash
			{
				get
				{
					RequireStopped();
					return original.Scope.Digest;
				}
			}

			private StopProof(NativeRegistration original)
			{
				this.original = original;
			}

			internal static StopProof Issue(object original)
			{
				NativeRegistration nativeRegistration = original as NativeRegistration;
				if (nativeRegistration == null || !object.ReferenceEquals(issuingProof, nativeRegistration))
				{
					throw new InvalidOperationException("only the original live guardian can issue recovery authority");
				}
				nativeRegistration.RequireStopped();
				return new StopProof(nativeRegistration);
			}

			internal IDisposable EnterUse()
			{
				RequireStopped();
				return new RecoveryUse(original.Coordination);
			}

			internal bool WaitForTargetExit()
			{
				RequireStopped();
				return WaitForSingleObject(original.Target.Handle, uint.MaxValue) == 0;
			}

			internal bool TargetExited()
			{
				RequireStopped();
				return WaitForSingleObject(original.Target.Handle, 0) == 0;
			}

			internal void RequireStopped()
			{
				if (original == null)
				{
					throw new ObjectDisposedException("guardian proof");
				}
				original.RequireStopped();
			}

			internal IntPtr DuplicateTarget()
			{
				RequireStopped();
				return original.Target.Duplicate();
			}

			public void Dispose()
			{
				if (original != null)
				{
					original.Dispose();
					original = null;
				}
			}
		}

		private sealed class RecoveryUse : IDisposable
		{
			private object held;

			internal RecoveryUse(object value)
			{
				held = value;
				Monitor.Enter(held);
			}

			public void Dispose()
			{
				object heldSnapshot = held;
				held = null;
				if (heldSnapshot != null)
				{
					Monitor.Exit(heldSnapshot);
				}
			}
		}

		private sealed class NativeOrigin : IHardThreadOriginPlatform
		{
			private static readonly byte[] entropy = Text("Pavise.HardThread.Origin.V2.LocalMachine");

			public HardThreadOriginContext Current()
			{
				string input;
				using (RegistryKey registryKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
				{
					using (RegistryKey registryKey2 = registryKey.OpenSubKey("SOFTWARE\\Microsoft\\Cryptography", false))
					{
						input = ((registryKey2 == null) ? null : (registryKey2.GetValue("MachineGuid") as string));
					}
				}
				Guid result;
				if (!Guid.TryParse(input, out result) || result == Guid.Empty)
				{
					throw new IOException("Windows machine protection domain unavailable");
				}
				return new HardThreadOriginContext(result.ToString("N"), Environment.MachineName, BootSessionIdentity.Read());
			}

			public byte[] Seal(byte[] original)
			{
				return ProtectedData.Protect(original, entropy, DataProtectionScope.LocalMachine);
			}

			public byte[] Unseal(byte[] original)
			{
				return ProtectedData.Unprotect(original, entropy, DataProtectionScope.LocalMachine);
			}
		}

		private sealed class HistoricalRegistration : IDisposable
		{
			internal Scope Scope;

			internal RootPin Root;

			internal DirectoryPin Guards;

			internal DirectoryPin Directory;

			internal FileStream Source;

			internal FileStream Image;

			internal HardThreadFileStore Store;

			internal HardThreadJournal Journal;

			internal string JournalHash;

			internal HardThreadHistoricalBudget Budget;

			private bool imageVerified;

			internal void RequireOriginal(bool verifyImage = true)
			{
				if (Root == null || !Root.Valid || Guards == null || !Guards.Valid || Directory == null || !Directory.Valid || Source == null || Image == null || Store == null || !Store.HoldsRootWriter || Scope == null || Journal == null || Scope.RootIdentity != Root.Identity || !Scope.Target.Same(Journal.Target) || Scope.Nonce != Journal.SourceNonce || Scope.Digest != Journal.SourceScopeHash || Hash(Source) != Scope.Digest)
				{
					throw new InvalidOperationException("original protected historical source is unavailable");
				}
				RequireAcl(Scope.Root, Scope.Sid);
				if (verifyImage && !imageVerified)
				{
					if (Budget == null || !Budget.ReadImage(Image.Length) || Hash(Image) != Scope.Child.ImageHash)
					{
						throw new InvalidDataException("original image differs or the historical read budget is exhausted");
					}
					imageVerified = true;
				}
			}

			public void Dispose()
			{
				try
				{
					if (Source != null)
					{
						Source.Dispose();
					}
				}
				finally
				{
					try
					{
						if (Image != null)
						{
							Image.Dispose();
						}
					}
					finally
					{
						try
						{
							if (Directory != null)
							{
								Directory.Dispose();
							}
						}
						finally
						{
							if (Guards != null)
							{
								Guards.Dispose();
							}
						}
					}
				}
			}
		}

		private sealed class HistoricalProcessPin : IDisposable
		{
			internal readonly HardThreadProcessFact Fact;

			private IntPtr handle;

			internal HistoricalProcessPin(int pid)
			{
				handle = OpenProcess(1052672u, false, pid);
				if (handle == IntPtr.Zero)
				{
					Fact = new HardThreadProcessFact(false, pid, 0L, false, uint.MaxValue, Marshal.GetLastWin32Error());
					return;
				}
				long born;
				long exit;
				long kernel;
				long user;
				bool processTimes = GetProcessTimes(handle, out born, out exit, out kernel, out user);
				Fact = new HardThreadProcessFact(true, (int)GetProcessId(handle), born, processTimes, WaitForSingleObject(handle, 0u), 0);
			}

			public void Dispose()
			{
				if (handle != IntPtr.Zero)
				{
					IntPtr intPtr = handle;
					handle = IntPtr.Zero;
					if (!CloseHandle(intPtr))
					{
						throw new IOException("historical query handle closure is unconfirmed");
					}
				}
			}
		}

		internal sealed class NativeGoneProof : IDisposable
		{
			private readonly HistoricalRegistration original;

			private HistoricalProcessPin first;

			private HistoricalProcessPin second;

			internal readonly HardThreadGoneReason Reason;

			private bool disposed;

			internal HardThreadTarget Target
			{
				get
				{
					if (disposed)
					{
						throw new ObjectDisposedException("native gone proof");
					}
					return original.Scope.Target;
				}
			}

			private NativeGoneProof(HistoricalRegistration original, HistoricalProcessPin first, HistoricalProcessPin second, HardThreadGoneReason reason)
			{
				this.original = original;
				this.first = first;
				this.second = second;
				Reason = reason;
			}

			internal static NativeGoneProof Issue(object original)
			{
				HistoricalRegistration historicalRegistration = original as HistoricalRegistration;
				if (historicalRegistration == null || !object.ReferenceEquals(issuingHistorical, historicalRegistration))
				{
					throw new InvalidOperationException("only the original protected historical probe can issue a gone fact");
				}
				historicalRegistration.RequireOriginal(false);
				HistoricalProcessPin historicalProcessPin = null;
				HistoricalProcessPin historicalProcessPin2 = null;
				try
				{
					historicalProcessPin = new HistoricalProcessPin(historicalRegistration.Scope.Target.ProcessId);
					if (!historicalProcessPin.Fact.Opened && historicalProcessPin.Fact.Error == 87)
					{
						historicalProcessPin2 = new HistoricalProcessPin(historicalRegistration.Scope.Target.ProcessId);
					}
					HardThreadGoneReason hardThreadGoneReason = HardThreadHistoricalPolicy.Gone(historicalRegistration.Scope.Target, historicalProcessPin.Fact, (historicalProcessPin2 == null) ? null : historicalProcessPin2.Fact);
					if (hardThreadGoneReason == HardThreadGoneReason.None)
					{
						historicalProcessPin.Dispose();
						if (historicalProcessPin2 != null)
						{
							historicalProcessPin2.Dispose();
						}
						return null;
					}
					historicalRegistration.RequireOriginal();
					return new NativeGoneProof(historicalRegistration, historicalProcessPin, historicalProcessPin2, hardThreadGoneReason);
				}
				catch
				{
					try
					{
						if (historicalProcessPin != null)
						{
							historicalProcessPin.Dispose();
						}
					}
					finally
					{
						if (historicalProcessPin2 != null)
						{
							historicalProcessPin2.Dispose();
						}
					}
					throw;
				}
			}

			internal void RequireCommit(HardThreadFileStore store, string root, HardThreadJournal current, string hash)
			{
				if (disposed || !object.ReferenceEquals(store, original.Store) || !string.Equals(Canonical(root), original.Scope.Root, StringComparison.OrdinalIgnoreCase) || current == null || !current.Target.Same(original.Journal.Target) || current.Revision != original.Journal.Revision || current.SourceNonce != original.Scope.Nonce || current.SourceScopeHash != original.Scope.Digest || hash != original.JournalHash || Reason == HardThreadGoneReason.None)
				{
					throw new InvalidOperationException("gone metadata source changed");
				}
				original.RequireOriginal();
			}

			public void Dispose()
			{
				if (disposed)
				{
					return;
				}
				disposed = true;
				try
				{
					if (first != null)
					{
						first.Dispose();
						first = null;
					}
				}
				finally
				{
					if (second != null)
					{
						second.Dispose();
						second = null;
					}
				}
			}
		}

		internal const string Command = "--hard-thread-guardian";

		internal const string StoreDirectory = "CriticalThreadHardAffinityV1";

		private const string GuardianConfiguration = "<configuration><startup><supportedRuntime version=\"v4.0\" sku=\".NETFramework,Version=v4.7.2\"/></startup></configuration>";

		private static readonly object controllersGate = new object();

		private static readonly List<ParentGuardian> controllers = new List<ParentGuardian>();

		private static readonly object proofGate = new object();

		private static object issuingProof;

		private static readonly string[] SettledLeftoverNames = new string[3] { "Pavise.HardThreadGuardian.exe.config", "Pavise.HardThreadGuardian.exe", "scope.xml" };

		private static volatile bool settledLeftovers = true;

		private static readonly object historicalGate = new object();

		private static HistoricalRegistration issuingHistorical;
		private static readonly Dictionary<string, int> historicalFailures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		private static readonly HashSet<string> historicalInFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		private sealed class HistoricalTargetObservation { internal bool Exited; internal int FailedReads; internal long SessionSerial; }
		private static readonly Dictionary<string, HistoricalTargetObservation> historicalTargets = new Dictionary<string, HistoricalTargetObservation>(StringComparer.OrdinalIgnoreCase);

		private static string HistoricalTargetPhase(string key, HardThreadTarget target)
		{
			lock (historicalGate)
			{
				HistoricalTargetObservation observation;
				if (!historicalTargets.TryGetValue(key, out observation)) historicalTargets[key] = observation = new HistoricalTargetObservation();
				long serial = PerformanceTuning.RecoveryCycleSerial;
				if (serial > observation.SessionSerial) { observation.SessionSerial = serial; observation.FailedReads = 0; }
				if (!observation.Exited && observation.FailedReads < 3)
				{
					try
					{
						using (HistoricalProcessPin first = new HistoricalProcessPin(target.ProcessId))
						using (HistoricalProcessPin second = !first.Fact.Opened && first.Fact.Error == 87 ? new HistoricalProcessPin(target.ProcessId) : null)
						{
							observation.Exited = HardThreadHistoricalPolicy.Gone(target, first.Fact, second == null ? null : second.Fact) != HardThreadGoneReason.None;
							if (observation.Exited || (first.Fact.Opened && first.Fact.TimesKnown && first.Fact.Pid == target.ProcessId && first.Fact.Birth == target.ProcessCreation && first.Fact.Wait == 258)) observation.FailedReads = 0;
							else observation.FailedReads++;
						}
					}
					catch { observation.FailedReads++; }
				}
				return key + (observation.Exited ? "/target-exit" : "/live-or-unknown");
			}
		}

		private static bool HistoricalAttemptAllowed(string key)
		{
			lock (historicalGate) { int count; return !historicalFailures.TryGetValue(key, out count) || count < 3; }
		}

		private static bool BeginHistoricalAttempt(string key)
		{
			lock (historicalGate) { return HistoricalAttemptAllowed(key) && historicalInFlight.Add(key); }
		}

		private static bool HistoricalTargetReadable(string key)
		{
			lock (historicalGate)
			{
				HistoricalTargetObservation observation;
				return !historicalTargets.TryGetValue(key, out observation) || observation.Exited || observation.FailedReads < 3;
			}
		}

		private static void HistoricalAttemptEnded(string key, bool completed)
		{
			lock (historicalGate)
			{
				historicalInFlight.Remove(key);
				if (completed) { historicalFailures.Remove(key); return; }
				int count; historicalFailures.TryGetValue(key, out count);
				historicalFailures[key] = Math.Min(3, count + 1);
				Logger.Warn("Critical historical recovery: scope=" + key + " attempt=" + historicalFailures[key] + "/3; original files retained");
			}
		}

		internal static bool SettledLeftoversPending
		{
			get
			{
				return settledLeftovers;
			}
		}

		internal static bool RetainRecoveryUntilSettled(Func<bool> restore, Action<int> wait)
		{
			if (restore == null || wait == null)
			{
				throw new ArgumentNullException("original recovery operation");
			}
			int retainRecoveryUntilSettledValue = 250;
			for (int attempt = 1; attempt <= 3; attempt++)
			{
				bool retainRecoveryUntilSettledReady = false;
				try
				{
					retainRecoveryUntilSettledReady = restore();
				}
				catch
				{
				}
				if (retainRecoveryUntilSettledReady)
				{
					return true;
				}
				Logger.Warn("Critical thread guardian recovery: attempt=" + attempt + "/3; original authority retained");
				if (attempt < 3)
				{
					try { wait(retainRecoveryUntilSettledValue); } catch { Thread.Sleep(retainRecoveryUntilSettledValue); }
					retainRecoveryUntilSettledValue *= 2;
				}
			}
			return false;
		}

internal static bool RetainRecoveryAcrossTargetExit(Func<bool> settle, Action<int> wait, Func<bool> targetGone, Func<bool> waitForTargetExit, int initialDelay)
        {
            bool exitPhase = false;
            try { exitPhase = targetGone(); } catch { }
            int attempts = 0, delay = initialDelay;
            while (true)
            {
                bool gone = exitPhase;
                if (!gone) { try { gone = targetGone(); } catch { } }
                if (!exitPhase && gone)
                {
                    exitPhase = true; attempts = 0; delay = initialDelay;
                }
                bool settled = false;
                attempts++;
                try { settled = settle(); } catch { }
                if (settled) return true;
                Logger.Warn("Guardian recovery: phase=" + (exitPhase ? "target-exit" : "live") + " attempt=" + attempts + "/3; original ownership retained");
                if (attempts >= 3)
                {
                    if (exitPhase) return false;
                    bool exited = false;
                    try { exited = waitForTargetExit(); } catch { }
                    if (!exited) return false;
                    exitPhase = true; attempts = 0; delay = initialDelay;
                    continue;
                }
                try { wait(delay); } catch { Thread.Sleep(delay); }
                delay *= 2;
            }
        }

		internal static bool TryRun(string[] args, string originalStartupData, out int exitCode)
		{
			exitCode = 0;
			bool runReady = false;
			if (args != null)
			{
				foreach (string a in args)
				{
					if (string.Equals(a, "--hard-thread-guardian", StringComparison.OrdinalIgnoreCase))
					{
						runReady = true;
					}
				}
			}
			if (!runReady)
			{
				return false;
			}
			exitCode = 13;
			return RunNative(args, originalStartupData, out exitCode);
		}

		internal static bool HasControllerDebt(string canonicalRoot)
		{
			string b = Canonical(canonicalRoot);
			lock (controllersGate)
			{
				foreach (ParentGuardian controller in controllers)
				{
					if (!controller.controllerReleased && string.Equals(controller.ControlledRoot, b, StringComparison.OrdinalIgnoreCase))
					{
						return true;
					}
				}
			}
			return false;
		}

		private static void RegisterController(ParentGuardian controller)
		{
			lock (controllersGate)
			{
				foreach (ParentGuardian controller2 in controllers)
				{
					if (!controller2.controllerReleased && string.Equals(controller2.ControlledRoot, controller.ControlledRoot, StringComparison.OrdinalIgnoreCase))
					{
						throw new HardThreadOperationException(HardThreadFailureKind.RecoveryPending, "an original controller still owns this root");
					}
				}
				controllers.Add(controller);
			}
		}

		private static void RemoveController(ParentGuardian controller)
		{
			lock (controllersGate)
			{
				controllers.Remove(controller);
			}
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool SetFileInformationByHandle(IntPtr handle, int informationClass, ref DeleteDisposition information, uint bytes);

		private static string FileKey(FileIdentity value)
		{
			return value.Volume + ":" + value.IndexHigh + ":" + value.IndexLow;
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool CloseHandle(IntPtr handle);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GetProcessTimes(IntPtr handle, out long born, out long exit, out long kernel, out long user);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern uint GetProcessId(IntPtr handle);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder name, ref int capacity);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint pid);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern IntPtr CreateFile(string path, uint access, uint share, IntPtr attributes, uint creation, uint flags, IntPtr template);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern bool CreateDirectory(string path, IntPtr attributes);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GetFileInformationByHandle(IntPtr handle, out FileIdentity info);

		[DllImport("kernel32.dll")]
		private static extern IntPtr GetCurrentProcess();

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool inJob);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr duplicate, uint access, bool inherit, uint options);

		private static string Hex(byte[] bytes)
		{
			return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
		}

		private static string Hash(byte[] bytes)
		{
			using (SHA256 sHA = SHA256.Create())
			{
				return Hex(sHA.ComputeHash(bytes));
			}
		}

		private static string Hash(Stream stream)
		{
			stream.Position = 0L;
			using (SHA256 sHA = SHA256.Create())
			{
				return Hex(sHA.ComputeHash(stream));
			}
		}

		private static string Nonce()
		{
			byte[] rawBytes = new byte[32];
			using (RandomNumberGenerator randomNumberGenerator = RandomNumberGenerator.Create())
			{
				randomNumberGenerator.GetBytes(rawBytes);
			}
			return Hex(rawBytes);
		}

		private static bool IsHash(string value)
		{
			if (value == null || value.Length != 64)
			{
				return false;
			}
			foreach (char c in value)
			{
				if (c < '0' || (c > '9' && c < 'a') || c > 'f')
				{
					return false;
				}
			}
			return true;
		}

		private static string Canonical(string path)
		{
			return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		}

		private static void NoReparse(string path)
		{
			for (DirectoryInfo directoryInfo = new DirectoryInfo(path); directoryInfo != null; directoryInfo = directoryInfo.Parent)
			{
				if ((directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0)
				{
					throw new IOException("guardian reparse directory refused");
				}
			}
		}

		private static string UserSid()
		{
			using (WindowsIdentity windowsIdentity = WindowsIdentity.GetCurrent())
			{
				return windowsIdentity.User.Value;
			}
		}

		private static bool TrustedSid(string value, string user)
		{
			if (!(value == user) && !(value == "S-1-5-18"))
			{
				return value == "S-1-5-32-544";
			}
			return true;
		}

		private static void RequireAcl(string root, string sid, bool protectedRules = true)
		{
			NoReparse(root);
			DirectorySecurity accessControl = Directory.GetAccessControl(root, AccessControlSections.Access | AccessControlSections.Owner);
			if ((protectedRules && !accessControl.AreAccessRulesProtected) || !TrustedSid(accessControl.GetOwner(typeof(SecurityIdentifier)).Value, sid))
			{
				throw new IOException("original guardian store owner is not trusted");
			}
			bool requireAclReady = false;
			foreach (FileSystemAccessRule accessRule in accessControl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
			{
				if (accessRule.AccessControlType == AccessControlType.Allow)
				{
					string value = accessRule.IdentityReference.Value;
					if (!TrustedSid(value, sid))
					{
						throw new IOException("guardian source has a foreign allow rule");
					}
					if (value == sid && (accessRule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl)
					{
						requireAclReady = true;
					}
				}
			}
			if (!requireAclReady)
			{
				throw new IOException("original user lacks full source ownership");
			}
		}

		private static void ProvisionRoot(string root, string sid)
		{
			if (!Directory.Exists(root))
			{
				Directory.CreateDirectory(root);
				NoReparse(root);
				DirectorySecurity directorySecurity = new DirectorySecurity();
				directorySecurity.SetOwner(new SecurityIdentifier(sid));
				directorySecurity.SetAccessRuleProtection(true, false);
				string[] textEntries = new string[3] { sid, "S-1-5-18", "S-1-5-32-544" };
				foreach (string sddlForm in textEntries)
				{
					directorySecurity.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sddlForm), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
				}
				Directory.SetAccessControl(root, directorySecurity);
			}
			RequireAcl(root, sid);
		}

		private static string PipeName(Guid session, string nonce)
		{
			return "PaviseHardThread-" + session.ToString("N") + "-" + nonce;
		}

		private static byte[] AwaitRead(Stream stream, int count)
		{
			byte[] rawBytes = new byte[count];
			Task<int> task;
			for (int i = 0; i < count; i += task.Result)
			{
				task = stream.ReadAsync(rawBytes, i, count - i);
				if (!task.Wait(5000))
				{
					stream.Dispose();
					throw new TimeoutException("guardian live read timed out");
				}
				if (task.Result <= 0)
				{
					throw new EndOfStreamException("guardian peer disconnected");
				}
			}
			return rawBytes;
		}

		private static byte[] ReadPacket(Stream stream)
		{
			int toInt32Result = BitConverter.ToInt32(AwaitRead(stream, 4), 0);
			if (toInt32Result < 1 || toInt32Result > 65536)
			{
				throw new InvalidDataException("guardian packet bound");
			}
			return AwaitRead(stream, toInt32Result);
		}

		private static void WritePacket(Stream stream, byte[] bytes)
		{
			if (bytes == null || bytes.Length < 1 || bytes.Length > 65536)
			{
				throw new InvalidDataException("guardian packet bound");
			}
			byte[] bytes2 = BitConverter.GetBytes(bytes.Length);
			Task task = stream.WriteAsync(bytes2, 0, 4);
			if (!task.Wait(5000))
			{
				stream.Dispose();
				throw new TimeoutException("guardian write timed out");
			}
			Task task2 = stream.WriteAsync(bytes, 0, bytes.Length);
			if (!task2.Wait(5000))
			{
				stream.Dispose();
				throw new TimeoutException("guardian write timed out");
			}
			stream.Flush();
		}

		private static async Task<byte[]> ReadStop(Stream stream)
		{
			try
			{
				byte[] prefix = new byte[4];
				int read2;
				for (int position = 0; position < 4; position += read2)
				{
					read2 = await stream.ReadAsync(prefix, position, 4 - position).ConfigureAwait(false);
					if (read2 <= 0)
					{
						return null;
					}
				}
				int length = BitConverter.ToInt32(prefix, 0);
				if (length < 1 || length > 256)
				{
					return null;
				}
				byte[] bytes = new byte[length];
				int read;
				for (int position = 0; position < length; position += read)
				{
					read = await stream.ReadAsync(bytes, position, length - position).ConfigureAwait(false);
					if (read <= 0)
					{
						return null;
					}
				}
				return bytes;
			}
			catch
			{
				return null;
			}
		}

		private static string Text(byte[] bytes)
		{
			return new UTF8Encoding(false, true).GetString(bytes);
		}

		private static byte[] Text(string value)
		{
			return new UTF8Encoding(false, true).GetBytes(value);
		}

		private static void WriteNew(string path, byte[] bytes)
		{
			FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
			HardThreadConstructionCleanup.AcquireAndRelease(delegate
			{
				stream.Write(bytes, 0, bytes.Length);
				stream.Flush(true);
			}, delegate
			{
				HardThreadConstructionCleanup.CloseOwned(stream);
			});
		}

		internal static IHardThreadGuardian Arm(GameMode.CriticalThreadTargetLease original, out HardThreadFileStore originalStore)
		{
			originalStore = null;
			original.RequireActive();
			string canonicalResult = Canonical(original.CanonicalStore);
			HardThreadStorageDomain.RequireLocal(canonicalResult, (string drive) => new DriveInfo(drive).DriveType);
			string userSidResult = UserSid();
			if (!string.Equals(Path.GetFileName(canonicalResult), "CriticalThreadHardAffinityV1", StringComparison.Ordinal))
			{
				throw new InvalidDataException("original startup store required");
			}
			ProvisionRoot(canonicalResult, userSidResult);
			ParentGuardian result = new ParentGuardian(canonicalResult);
			RegisterController(result);
			try
			{
				result.Root = new RootPin(canonicalResult, userSidResult);
				try
				{
					originalStore = new HardThreadFileStore(canonicalResult);
					originalStore.RequireForwardAdmission(new HardThreadTarget(original.SessionId, original.ProcessId, original.ProcessCreation, original.ExecutablePath, original.AllowedMask));
				}
				catch (HardThreadOperationException)
				{
					throw;
				}
				catch (Exception ex2)
				{
					throw new HardThreadOperationException(HardThreadFailureKind.RecoveryPending, "original root writer or historical ledger is unavailable: " + ex2.Message);
				}
				result.Parent = new ActorPin(Process.GetCurrentProcess().Id, null);
				ActorPin parent = result.Parent;
				if (parent.State != 0)
				{
					throw new IOException("original parent is not alive");
				}
				string nonceResult = Nonce();
				result.Setup = new Scope
				{
					Target = new HardThreadTarget(original.SessionId, original.ProcessId, original.ProcessCreation, original.ExecutablePath, original.AllowedMask),
					Parent = parent.Actor,
					Root = canonicalResult,
					RootIdentity = result.Root.Identity,
					Sid = userSidResult,
					Nonce = nonceResult
				};
				string guardsPath = Path.Combine(canonicalResult, "guards", original.SessionId.ToString("N"));
				if (Directory.Exists(guardsPath))
				{
					throw new IOException("original guardian session already registered");
				}
				Directory.CreateDirectory(Path.GetDirectoryName(guardsPath));
				if (!CreateDirectory(guardsPath, IntPtr.Zero))
				{
					throw new IOException("original session directory could not be created exclusively: " + Marshal.GetLastWin32Error());
				}
				result.DirectoryCreated = true;
				NoReparse(guardsPath);
				result.GuardsDirectory = new DirectoryPin(Path.GetDirectoryName(guardsPath), userSidResult);
				result.SessionDirectory = new DirectoryPin(guardsPath, userSidResult);
				string executable = Path.Combine(guardsPath, "Pavise.HardThreadGuardian.exe");
				FileStream source = null;
				FileStream copy = null;
				HardThreadConstructionCleanup.AcquireAndRelease(delegate
				{
					source = new FileStream(parent.Actor.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
					copy = new FileStream(executable, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough);
					source.CopyTo(copy);
					copy.Flush(true);
				}, delegate
				{
					HardThreadConstructionCleanup.CloseOwned(new HardThreadResourceClosure(copy, source));
				});
				result.ExecutableCreated = true;
				WriteNew(executable + ".config", Text("<configuration><startup><supportedRuntime version=\"v4.0\" sku=\".NETFramework,Version=v4.7.2\"/></startup></configuration>"));
				result.ConfigurationCreated = true;
				PipeSecurity pipeSecurity = new PipeSecurity();
				pipeSecurity.AddAccessRule(new PipeAccessRule(new SecurityIdentifier("S-1-5-2"), PipeAccessRights.FullControl, AccessControlType.Deny));
				string[] textEntries = new string[3] { userSidResult, "S-1-5-18", "S-1-5-32-544" };
				foreach (string sddlForm in textEntries)
				{
					pipeSecurity.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sddlForm), PipeAccessRights.FullControl, AccessControlType.Allow));
				}
				result.Pipe = new NamedPipeServerStream(PipeName(original.SessionId, nonceResult), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, pipeSecurity);
				StartupInfo startupInfo = default(StartupInfo);
				startupInfo.Size = Marshal.SizeOf(typeof(StartupInfo));
				startupInfo.Flags = 1u;
				startupInfo.Show = 0;
				StartupInfo startup = startupInfo;
				string directoryName = Path.GetDirectoryName(canonicalResult);
				string value = "\"" + executable + "\" --hard-thread-guardian " + original.SessionId.ToString("N") + " " + nonceResult + " --autostart-data \"" + directoryName + "\"";
				ProcessInfo child;
				if (!CreateProcess(executable, new StringBuilder(value), IntPtr.Zero, IntPtr.Zero, false, 150994944u, IntPtr.Zero, guardsPath, ref startup, out child))
				{
					int lastWin32Error = Marshal.GetLastWin32Error();
					bool inJob = false;
					bool membershipKnown = lastWin32Error == 5 && IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out inJob);
					throw HardThreadGuardianLaunchPolicy.Failed(lastWin32Error, membershipKnown, inJob);
				}
				result.SpawnSucceeded = true;
				result.SpawnThread = child.Thread;
				result.UnverifiedSpawn = child.Process;
				HardThreadConstructionCleanup.AcquireAndRelease(delegate
				{
					result.Child = new ActorPin((int)child.Pid, null, child.Process);
				}, delegate
				{
					result.ReleaseSpawnHandles(result.Child != null);
				});
				if (result.Child.Actor.ImageHash != parent.Actor.ImageHash || !string.Equals(result.Child.Actor.Path, executable, StringComparison.OrdinalIgnoreCase))
				{
					throw new IOException("spawned guardian bytes differ");
				}
				bool inJob2;
				bool membershipKnown2 = IsProcessInJob(result.Child.Handle, IntPtr.Zero, out inJob2);
				HardThreadGuardianLaunchPolicy.RequireIndependent(membershipKnown2, inJob2);
				result.Scope = new Scope
				{
					Target = new HardThreadTarget(original.SessionId, original.ProcessId, original.ProcessCreation, original.ExecutablePath, original.AllowedMask),
					Parent = parent.Actor,
					Child = result.Child.Actor,
					Root = canonicalResult,
					RootIdentity = result.Root.Identity,
					Sid = userSidResult,
					Nonce = nonceResult
				};
				byte[] bytes = result.Scope.Encode();
				originalStore.BindOriginalScope(result.Scope.Target, nonceResult, result.Scope.Digest);
				result.Unregister = original.UnregisterGuardianActor;
				if (!original.RegisterGuardianActor(result.Child.Actor))
				{
					throw new IOException("original guardian background restoration is unconfirmed");
				}
				string path = Path.Combine(guardsPath, "scope.xml");
				WriteNew(path, bytes);
				result.RegistrationCreated = true;
				result.Source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
				IAsyncResult asyncResult = result.Pipe.BeginWaitForConnection(null, null);
				if (!asyncResult.AsyncWaitHandle.WaitOne(5000))
				{
					throw new TimeoutException("original guardian did not connect");
				}
				result.Pipe.EndWaitForConnection(asyncResult);
				asyncResult.AsyncWaitHandle.Close();
				uint pid;
				if (!GetNamedPipeClientProcessId(result.Pipe.SafePipeHandle.DangerousGetHandle(), out pid) || pid != result.Child.Actor.Pid || result.Child.State != 0)
				{
					throw new IOException("pipe client is not the original spawned guardian");
				}
				WritePacket(result.Pipe, bytes);
				if (Text(ReadPacket(result.Pipe)) != "ready:" + result.Scope.Digest)
				{
					throw new IOException("guardian did not retain the original scope");
				}
				original.RequireActive();
				WritePacket(result.Pipe, Text("armed:" + result.Scope.Digest));
				result.Watch();
				return result;
			}
			catch (Exception ex3)
			{
				result.RememberClosureFailure(ex3);
				try
				{
					if (originalStore != null && result.OriginalScope != null)
					{
						try
						{
							HardThreadJournal hardThreadJournal = originalStore.Load(result.OriginalScope.Target);
							result.NoteSettled(originalStore.CaptureSettled(result.OriginalScope.Target, hardThreadJournal.Revision));
						}
						catch
						{
						}
					}
					if (originalStore != null)
					{
						try
						{
							HardThreadConstructionCleanup.CloseOwned(originalStore);
							originalStore = null;
						}
						catch (Exception ex4)
						{
							result.RememberClosureFailure(ex4);
							throw new AggregateException("Original preparation and store cleanup both failed", ex3, ex4);
						}
					}
				}
				finally
				{
					result.Dispose();
				}
				throw;
			}
		}

		private static StopProof OriginalStopProof(NativeRegistration original)
		{
			lock (proofGate)
			{
				original.RequireStopped();
				issuingProof = original;
				try
				{
					return StopProof.Issue(original);
				}
				finally
				{
					issuingProof = null;
				}
			}
		}

		private static bool RunNative(string[] args, string originalStartupData, out int exitCode)
		{
			exitCode = 13;
			NativeRegistration nativeRegistration = null;
			try
			{
				Guid result;
				if (args == null || args.Length < 3 || args[0] != "--hard-thread-guardian" || !Guid.TryParseExact(args[1], "N", out result) || result == Guid.Empty || !IsHash(args[2]) || string.IsNullOrEmpty(originalStartupData) || !Path.IsPathRooted(originalStartupData))
				{
					return true;
				}
				string canonicalResult = Canonical(Path.Combine(originalStartupData, "CriticalThreadHardAffinityV1"));
				RequireAcl(canonicalResult, UserSid());
				NamedPipeClientStream namedPipeClientStream = new NamedPipeClientStream(".", PipeName(result, args[2]), PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
				NativeRegistration nativeRegistration2 = new NativeRegistration();
				nativeRegistration2.Pipe = namedPipeClientStream;
				nativeRegistration = nativeRegistration2;
				namedPipeClientStream.Connect(5000);
				uint pid;
				if (!GetNamedPipeServerProcessId(namedPipeClientStream.SafePipeHandle.DangerousGetHandle(), out pid))
				{
					throw new IOException("original server PID unavailable");
				}
				byte[] bytes = ReadPacket(namedPipeClientStream);
				nativeRegistration.Scope = Scope.Parse(bytes, canonicalResult, result, args[2], UserSid(), new NativeOrigin(), true);
				if (pid != nativeRegistration.Scope.Parent.Pid || nativeRegistration.Scope.Child.Pid != Process.GetCurrentProcess().Id)
				{
					throw new IOException("actual guardian pipe peers differ");
				}
				nativeRegistration.Root = new RootPin(canonicalResult, nativeRegistration.Scope.Sid);
				if (nativeRegistration.Root.Identity != nativeRegistration.Scope.RootIdentity)
				{
					throw new IOException("original source root was replaced");
				}
				nativeRegistration.Parent = new ActorPin((int)pid, nativeRegistration.Scope.Parent);
				using (ActorPin actorPin = new ActorPin(Process.GetCurrentProcess().Id, nativeRegistration.Scope.Child))
				{
					if (nativeRegistration.Parent.State != 0 || actorPin.State != 0)
					{
						throw new IOException("live original peers required");
					}
					nativeRegistration.Target = new TargetPin(nativeRegistration.Scope.Target);
					string path = Path.Combine(canonicalResult, "guards", result.ToString("N"), "scope.xml");
					nativeRegistration.Source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
					if (Hash(nativeRegistration.Source) != nativeRegistration.Scope.Digest)
					{
						throw new IOException("original persisted scope differs from live scope");
					}
					nativeRegistration.Handshake = new HardThreadGuardianHandshake(nativeRegistration.Parent.Actor, actorPin.Actor, args[2], nativeRegistration.Scope.Digest);
					nativeRegistration.Handshake.Accept((int)pid, actorPin.Actor.Pid, nativeRegistration.Parent.Actor, actorPin.Actor, args[2], nativeRegistration.Scope.Digest);
					WritePacket(namedPipeClientStream, Text("ready:" + nativeRegistration.Scope.Digest));
					if (Text(ReadPacket(namedPipeClientStream)) != "armed:" + nativeRegistration.Scope.Digest)
					{
						throw new IOException("original parent did not acknowledge registration");
					}
				}
				Task<byte[]> task = ReadStop(namedPipeClientStream);
				bool runNativeReady = false;
				long numericValue = 0L;
				int terminalReadFailures = 0;
				while (true)
				{
					switch (nativeRegistration.Parent.State)
					{
					case HardThreadRead.Unknown:
						exitCode = 14;
						return true;
					default:
					{
						if (task != null && task.IsCompleted)
						{
							byte[] result2 = task.Result;
							task = null;
							if (result2 == null || Text(result2) == "stop:" + nativeRegistration.Scope.Digest)
							{
								runNativeReady = true;
							}
						}
						if (!runNativeReady || terminalReadFailures >= 3 || DateTime.UtcNow.Ticks < numericValue)
						{
							break;
						}
						numericValue = DateTime.UtcNow.AddSeconds(1.0).Ticks;
						bool runNativeReady2 = false;
						try
						{
							using (HardThreadFileStore hardThreadFileStore = new HardThreadFileStore(canonicalResult))
							{
								runNativeReady2 = true;
								foreach (HardThreadReceipt row in hardThreadFileStore.Load(nativeRegistration.Scope.Target).Rows)
								{
									if (!row.Terminal)
									{
										runNativeReady2 = false;
									}
								}
							}
						}
						catch
						{
							terminalReadFailures++;
							Logger.Warn("Critical guardian terminal journal read: attempt=" + terminalReadFailures + "/3; retained until parent exit");
						}
						if (runNativeReady2)
						{
							exitCode = 0;
							return true;
						}
						break;
					}
					case HardThreadRead.Gone:
					{
						nativeRegistration.Handshake.ObserveParent(HardThreadRead.Gone);
						StopProof proof = OriginalStopProof(nativeRegistration);
						try
						{
							nativeRegistration = null;
							Func<bool> restore = delegate
							{
								using (CriticalThreadHardAffinitySession criticalThreadHardAffinitySession = CriticalThreadHardAffinitySession.OpenRecovery(proof))
								{
									return criticalThreadHardAffinitySession.Restore();
								}
							};
							if (!RetainRecoveryAcrossTargetExit(restore, Thread.Sleep, proof.TargetExited, proof.WaitForTargetExit, 250))
							{
								Logger.Warn("Critical thread guardian recovery: exhausted; authority retained without active retries");
								Thread.Sleep(Timeout.Infinite);
							}
							exitCode = 0;
						}
						finally
						{
							if (proof != null)
							{
								((IDisposable)proof).Dispose();
							}
						}
						return true;
					}
					}
					WaitForSingleObject(nativeRegistration.Parent.Handle, 250u);
				}
			}
			catch (Exception error)
			{
				if (nativeRegistration != null)
				{
					nativeRegistration.RememberClosureFailure(error);
				}
				exitCode = 14;
				return true;
			}
			finally
			{
				HardThreadGuardianExit.CloseForExit(nativeRegistration, ref exitCode);
			}
		}

		private static void NoteSettledLeftovers()
		{
			settledLeftovers = true;
		}

		internal static bool SweepSettledGuards(string originalStartupData)
		{
			string enumerationKey = "cleanup-enumeration:" + originalStartupData + "/" + PerformanceTuning.RecoveryCycleSerial;
			if (!HistoricalAttemptAllowed(enumerationKey)) return false;
			settledLeftovers = false;
			bool sweepSettledGuardsSucceeded = false;
			try
			{
				if (string.IsNullOrEmpty(originalStartupData) || !Path.IsPathRooted(originalStartupData))
				{
					return false;
				}
				string canonicalResult = Canonical(Path.Combine(originalStartupData, "CriticalThreadHardAffinityV1"));
				string guardsPath = Path.Combine(canonicalResult, "guards");
				if (HasControllerDebt(canonicalResult))
				{
					return false;
				}
				if (!Directory.Exists(guardsPath))
				{
					return sweepSettledGuardsSucceeded = true;
				}
				string sid = UserSid();
				RequireAcl(canonicalResult, sid);
				RequireAcl(guardsPath, sid, false);
				NoReparse(guardsPath);
				sweepSettledGuardsSucceeded = true;
				string[] directories = Directory.GetDirectories(guardsPath);
				HistoricalAttemptEnded(enumerationKey, true);
				foreach (string entryText in directories)
				{
					Guid result;
					if (Guid.TryParseExact(Path.GetFileName(entryText), "N", out result))
					{
						string resultText = result.ToString("N");
						if (!File.Exists(Path.Combine(canonicalResult, "hard-thread-" + resultText + ".xml")) && !File.Exists(Path.Combine(canonicalResult, "hard-thread-" + resultText + ".lock")) && !SweepSettledGuard(entryText))
						{
							sweepSettledGuardsSucceeded = false;
						}
					}
				}
				return sweepSettledGuardsSucceeded;
			}
			catch
			{
				HistoricalAttemptEnded(enumerationKey, false);
				sweepSettledGuardsSucceeded = false;
				return false;
			}
			finally
			{
				if (!sweepSettledGuardsSucceeded)
				{
					settledLeftovers = true;
				}
			}
		}

		private static bool SweepSettledGuard(string directory)
		{
			string key = "cleanup:" + directory + "/" + PerformanceTuning.RecoveryCycleSerial;
			if (!BeginHistoricalAttempt(key)) return false;
			bool completed = false;
			try { completed = SweepSettledGuardCore(directory); return completed; }
			finally { HistoricalAttemptEnded(key, completed); }
		}

		private static bool SweepSettledGuardCore(string directory)
		{
			if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
			{
				return false;
			}
			string[] fileSystemEntries = Directory.GetFileSystemEntries(directory);
			foreach (string path in fileSystemEntries)
			{
				if (Array.IndexOf(SettledLeftoverNames, Path.GetFileName(path)) < 0)
				{
					return false;
				}
			}
			string[] settledLeftoverNames = SettledLeftoverNames;
			foreach (string path2 in settledLeftoverNames)
			{
				string path3 = Path.Combine(directory, path2);
				if (File.Exists(path3) && !DeleteSettledEntry(path3, false))
				{
					return false;
				}
			}
			return DeleteSettledEntry(directory, true);
		}

		private static bool DeleteSettledEntry(string path, bool directory)
		{
			IntPtr intPtr = CreateFile(path, 65664u, 7u, IntPtr.Zero, 3u, directory ? 35651584u : 2097152u, IntPtr.Zero);
			if (intPtr == IntPtr.Zero || intPtr == new IntPtr(-1))
			{
				return false;
			}
			try
			{
				FileIdentity info;
				if (!GetFileInformationByHandle(intPtr, out info) || (info.Attributes & 0x400u) != 0 || (info.Attributes & 0x10) != 0 != directory)
				{
					return false;
				}
				DeleteDisposition deleteDisposition = default(DeleteDisposition);
				deleteDisposition.Delete = 1;
				DeleteDisposition information = deleteDisposition;
				return SetFileInformationByHandle(intPtr, 4, ref information, 1u);
			}
			finally
			{
				CloseHandle(intPtr);
			}
		}

		private static byte[] ScopeBytes(FileStream source)
		{
			if (source.Length < 32 || source.Length > 65536)
			{
				throw new InvalidDataException("bounded original historical scope required");
			}
			byte[] rawBytes = new byte[(int)source.Length];
			int scopeBytesValue;
			for (int i = 0; i < rawBytes.Length; i += scopeBytesValue)
			{
				scopeBytesValue = source.Read(rawBytes, i, rawBytes.Length - i);
				if (scopeBytesValue <= 0)
				{
					throw new EndOfStreamException();
				}
			}
			if (source.ReadByte() != -1)
			{
				throw new InvalidDataException("historical source grew during capture");
			}
			return rawBytes;
		}

		internal static bool TrySettleHistoricalGone(string originalStartupData)
		{
			string enumerationKey = "journal-enumeration:" + originalStartupData + "/" + PerformanceTuning.RecoveryCycleSerial;
			if (!HistoricalAttemptAllowed(enumerationKey)) return false;
			try
			{
				if (string.IsNullOrEmpty(originalStartupData) || !Path.IsPathRooted(originalStartupData))
				{
					return false;
				}
				string canonicalResult = Canonical(Path.Combine(originalStartupData, "CriticalThreadHardAffinityV1"));
				string userSidResult = UserSid();
				if (HasControllerDebt(canonicalResult))
				{
					return false;
				}
				if (!Directory.Exists(canonicalResult))
				{
					return !HardThreadFileStore.CanonicalRootHasRecoveryDebt(canonicalResult);
				}
				RequireAcl(canonicalResult, userSidResult);
				using (RootPin root = new RootPin(canonicalResult, userSidResult))
				{
					HardThreadHistoricalBudget hardThreadHistoricalBudget = new HardThreadHistoricalBudget();
					Stopwatch stopwatch = Stopwatch.StartNew();
					HardThreadJournal[] historicalJournalsResult;
					using (HardThreadFileStore hardThreadFileStore = new HardThreadFileStore(canonicalResult))
					{
						historicalJournalsResult = hardThreadFileStore.HistoricalJournals();
					}
					HistoricalAttemptEnded(enumerationKey, true);
					bool settleHistoricalGoneReady = false;
					HardThreadJournal[] hardThreadJournalItems = historicalJournalsResult;
					foreach (HardThreadJournal hardThreadJournal in hardThreadJournalItems)
					{
						bool settleHistoricalGoneReady2 = false;
						foreach (HardThreadReceipt row in hardThreadJournal.Rows)
						{
							if (!row.Terminal)
							{
								settleHistoricalGoneReady2 = true;
							}
						}
						if (!settleHistoricalGoneReady2)
						{
							continue;
						}
						string observationKey = "journal:" + canonicalResult + "/" + hardThreadJournal.Target.SessionId + "/" + hardThreadJournal.Target.ProcessCreation;
						string retryKey = HistoricalTargetPhase(observationKey, hardThreadJournal.Target);
						if (!HistoricalTargetReadable(observationKey)) { settleHistoricalGoneReady = true; continue; }
						if (!HistoricalAttemptAllowed(retryKey)) { settleHistoricalGoneReady = true; continue; }
						if (!hardThreadHistoricalBudget.BeginSession(stopwatch.ElapsedMilliseconds))
						{
							settleHistoricalGoneReady = true;
							break;
						}
						bool completed = false;
						if (!BeginHistoricalAttempt(retryKey)) { settleHistoricalGoneReady = true; continue; }
						try
						{
							using (HardThreadFileStore hardThreadFileStore2 = new HardThreadFileStore(canonicalResult))
							{
								using (HistoricalRegistration historicalRegistration = new HistoricalRegistration())
								{
									historicalRegistration.Store = hardThreadFileStore2;
									historicalRegistration.Root = root;
									historicalRegistration.Budget = hardThreadHistoricalBudget;
									historicalRegistration.Journal = hardThreadFileStore2.ReadHistorical(hardThreadJournal.Target);
									if (string.IsNullOrEmpty(historicalRegistration.Journal.SourceNonce) || string.IsNullOrEmpty(historicalRegistration.Journal.SourceScopeHash))
									{
										throw new InvalidDataException("legacy historical source has no original host binding");
									}
									string guardsPath = Path.Combine(canonicalResult, "guards", hardThreadJournal.Target.SessionId.ToString("N"));
									historicalRegistration.Guards = new DirectoryPin(Path.GetDirectoryName(guardsPath), userSidResult);
									historicalRegistration.Directory = new DirectoryPin(guardsPath, userSidResult);
									string path = Path.Combine(guardsPath, "scope.xml");
									if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
									{
										throw new InvalidDataException("original scope reparse source refused");
									}
									historicalRegistration.Source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
									byte[] bytes = ScopeBytes(historicalRegistration.Source);
									historicalRegistration.Scope = Scope.Parse(bytes, canonicalResult, hardThreadJournal.Target.SessionId, historicalRegistration.Journal.SourceNonce, userSidResult, new NativeOrigin(), false);
									if ((File.GetAttributes(historicalRegistration.Scope.Child.Path) & FileAttributes.ReparsePoint) != 0)
									{
										throw new InvalidDataException("original guardian image reparse source refused");
									}
									historicalRegistration.Image = new FileStream(historicalRegistration.Scope.Child.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
									historicalRegistration.JournalHash = hardThreadFileStore2.HistoricalHash(hardThreadJournal.Target);
									historicalRegistration.RequireOriginal(false);
									NativeGoneProof nativeGoneProof;
									lock (historicalGate)
									{
										issuingHistorical = historicalRegistration;
										try
										{
											nativeGoneProof = NativeGoneProof.Issue(historicalRegistration);
										}
										finally
										{
											issuingHistorical = null;
										}
									}
									if (nativeGoneProof == null)
									{
										completed = true; // A valid live target is a successful observation, not an execution failure.
										settleHistoricalGoneReady = true;
										continue;
									}
									using (nativeGoneProof)
									{
										hardThreadFileStore2.CommitHistoricalGone(nativeGoneProof);
										completed = true;
									}
								}
							}
						}
						catch
						{
							completed = false;
							settleHistoricalGoneReady = true;
						}
						finally { HistoricalAttemptEnded(retryKey, completed); }
					}
					return !settleHistoricalGoneReady && !HardThreadFileStore.CanonicalRootHasRecoveryDebt(canonicalResult);
				}
			}
			catch
			{
				HistoricalAttemptEnded(enumerationKey, false);
				return false;
			}
		}
	}
}
