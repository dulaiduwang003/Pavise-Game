using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace PaviseApp
{
	internal sealed class GameNvProfileBackend : IGameNvProfileReader, IDisposable
	{
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate int Init();

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate int Open(out IntPtr session);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate int SessionCall(IntPtr session);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate int Find(IntPtr session, [MarshalAs(UnmanagedType.LPWStr)] string name, out IntPtr profile);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate int FindApp(IntPtr session, [MarshalAs(UnmanagedType.LPWStr)] string name, out IntPtr profile, ref Application app);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate int GetInfo(IntPtr session, IntPtr profile, ref Profile info);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate int EnumApps(IntPtr session, IntPtr profile, uint start, ref uint count, ref Application app);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate int GetSetting(IntPtr session, IntPtr profile, uint id, ref Setting setting);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate int EnumSettings(IntPtr session, IntPtr profile, uint start, ref uint count, ref Setting setting);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate int Global(IntPtr session, out IntPtr profile);

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
		private struct Profile
		{
			internal uint Version;

			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)]
			internal string Name;

			internal uint Support;

			internal uint Predefined;

			internal uint Apps;

			internal uint Settings;
		}

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
		private struct Application
		{
			internal uint Version;

			internal uint Predefined;

			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)]
			internal string Name;

			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)]
			internal string Friendly;

			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)]
			internal string Launcher;

			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)]
			internal string Folder;

			internal uint Flags;

			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)]
			internal string Command;
		}

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
		private struct Setting
		{
			internal uint Version;

			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)]
			internal string Name;

			internal uint Id;

			internal uint Type;

			internal uint Location;

			internal uint CurrentPredefined;

			internal uint PredefinedValid;

			internal uint Predefined;

			[MarshalAs(UnmanagedType.ByValArray, SizeConst = 4096)]
			internal byte[] PredefinedPad;

			internal uint Value;

			[MarshalAs(UnmanagedType.ByValArray, SizeConst = 4096)]
			internal byte[] ValuePad;
		}

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate int CreateProfileCall(IntPtr session, ref Profile profile, out IntPtr handle);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate int CreateAppCall(IntPtr session, IntPtr profile, ref Application application);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate int SetCall(IntPtr session, IntPtr profile, ref Setting setting);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate int DeleteSettingCall(IntPtr session, IntPtr profile, uint id);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate int DeleteAppCall(IntPtr session, IntPtr profile, [MarshalAs(UnmanagedType.LPWStr)] string exactPath);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate int DeleteProfileCall(IntPtr session, IntPtr profile);

		private sealed class NativeSession : IGameNvProfileWriter, IGameNvProfileReader, IDisposable
		{
			private readonly GameNvProfileBackend reader;

			private readonly CreateProfileCall create;

			private readonly CreateAppCall bind;

			private readonly SetCall set;

			private readonly DeleteSettingCall remove;

			private readonly DeleteAppCall unbind;

			private readonly DeleteProfileCall delete;

			private readonly SessionCall save;

			internal NativeSession()
			{
				reader = new GameNvProfileBackend();
				try
				{
					create = Resolve<CreateProfileCall>(3424084072u);
					bind = Resolve<CreateAppCall>(1128770014u);
					set = Resolve<SetCall>(1467863554u);
					remove = Resolve<DeleteSettingCall>(3835847522u);
					unbind = Resolve<DeleteAppCall>(745098182u);
					delete = Resolve<DeleteProfileCall>(386478598u);
					save = Resolve<SessionCall>(4240211476u);
				}
				catch (Exception originalFailure)
				{
					CleanupSequence.Complete(originalFailure, reader.Dispose);
					throw;
				}
			}

			public void Reload()
			{
				reader.Reload();
			}

			public GameNvProfile FindApplication(string path)
			{
				return reader.FindApplication(path);
			}

			public GameNvProfile FindProfile(string name)
			{
				return reader.FindProfile(name);
			}

			public GameNvSetting Read(string name, uint id)
			{
				return reader.Read(name, id);
			}

			public GameNvSetting Reference(uint id)
			{
				return reader.Reference(id);
			}

			public void CreateProfile(string name)
			{
				Text(name);
				Profile profile = default(Profile);
				profile.Version = Version<Profile>();
				profile.Name = name;
				Profile profile2 = profile;
				IntPtr handle;
				Ok(create(reader.session, ref profile2, out handle), "create game profile");
				if (handle == IntPtr.Zero)
				{
					throw new InvalidDataException("Created NVIDIA profile has no handle");
				}
			}

			public void Bind(string name, string path)
			{
				Text(path);
				Application application = default(Application);
				application.Version = Version<Application>(4u);
				application.Name = path;
				application.Friendly = "";
				application.Launcher = "";
				application.Folder = "";
				application.Flags = 0u;
				application.Command = "";
				Application application2 = application;
				Ok(bind(reader.session, reader.Handle(name), ref application2), "bind exact game application");
			}

			public void SetUserOverride(string name, uint id, uint? value)
			{
				GameNvPrelaunchPlan.RequireId(id);
				IntPtr profile = reader.Handle(name);
				if (!value.HasValue)
				{
					Ok(remove(reader.session, profile, id), "delete owned game override");
					return;
				}
				Setting setting = NewSetting(id);
				setting.Value = value.Value;
				Ok(set(reader.session, profile, ref setting), "write game DWORD override");
			}

			public void Unbind(string name, string path)
			{
				Text(path);
				Ok(unbind(reader.session, reader.Handle(name), path), "unbind owned exact application");
			}

			public void DeleteEmptyProfile(string name)
			{
				Ok(delete(reader.session, reader.Handle(name)), "delete owned empty profile");
			}

			public void Save()
			{
				reader.Active();
				Ok(save(reader.session), "save game profile");
			}

			public void Dispose()
			{
				reader.Dispose();
			}
		}

		private sealed class GameNvFreshProfileWriter : IGameNvProfileWriter, IGameNvProfileReader, IDisposable, IGameNvProfileRecoveryContext
		{
			private readonly Func<IGameNvProfileWriter> open;

			private readonly Func<IDisposable> gate;

			private readonly Action forward;

			private readonly Action recover;

			private readonly GameNvRecoveryIdentity original;

			private readonly string name;

			private readonly string path;

			private readonly string scope;

			private readonly bool own;

			private readonly uint predefined;

			private readonly Dictionary<uint, GameNvSetting> originals;

			private readonly Dictionary<uint, uint> targets;

			private bool recovery;

			private bool disposed;

			private Action<IGameNvProfileWriter> pending;

			private Action<IGameNvProfileWriter> verify;

			private string pendingScope;

			private string pendingValue;

			private string pendingOperation;

			private uint? pendingId;

			private FileStream originalPin;

			internal GameNvFreshProfileWriter(Func<IGameNvProfileWriter> sessionFactory, Func<IDisposable> enter, Action requireForward, Action requireRecovery, GameNvRecoveryIdentity identity, string profile, string executable, bool owned, uint profilePredefined, string scopeHash, IDictionary<uint, GameNvSetting> oldValues, IDictionary<uint, uint> desired, bool recoveryMode)
			{
				if (sessionFactory == null || enter == null || requireForward == null || requireRecovery == null || identity == null)
				{
					throw new ArgumentNullException("Original NVIDIA transaction dependencies");
				}
				if (string.IsNullOrEmpty(profile) || profile.Length >= 2048 || profile.IndexOf('\0') >= 0 || !Path.IsPathRooted(executable) || Path.GetFullPath(executable) != executable || executable.Length >= 2048 || executable.IndexOf('\0') >= 0 || profilePredefined > 1 || (owned && profilePredefined != 0))
				{
					throw new InvalidDataException("Invalid exact NVIDIA write scope");
				}
				open = sessionFactory;
				gate = enter;
				forward = requireForward;
				recover = requireRecovery;
				original = identity;
				name = profile;
				path = executable;
				own = owned;
				predefined = profilePredefined;
				scope = scopeHash;
				originals = new Dictionary<uint, GameNvSetting>(oldValues);
				targets = new Dictionary<uint, uint>(desired);
				recovery = recoveryMode;
				foreach (KeyValuePair<uint, uint> target in targets)
				{
					GameNvPrelaunchPlan.RequireId(target.Key);
					if (target.Value != 1 || !originals.ContainsKey(target.Key))
					{
						throw new InvalidDataException("Unsupported native game target");
					}
				}
				if (!original.ResourceBound || original.ProfileName != name || original.ProfilePredefined != predefined || original.ProfileScopeHash != scope || original.OwnResources != own || original.Executable != path || originals.Count != 2 || targets.Count == 0)
				{
					throw new InvalidDataException("Native transaction lacks exact original resource binding");
				}
				string text = Path.Combine(original.JournalDirectory, "original.tsv");
				NoReparse.CheckPath(text);
				originalPin = new FileStream(text, FileMode.Open, FileAccess.Read, FileShare.Read);
				try
				{
					RequireOriginal();
				}
				catch
				{
					originalPin.Dispose();
					originalPin = null;
					throw;
				}
			}

			private void RequireOriginal()
			{
				if (originalPin == null || originalPin.Length < 1 || originalPin.Length > 1048576)
				{
					throw new InvalidDataException("No pinned native original");
				}
				byte[] array = new byte[(int)originalPin.Length];
				originalPin.Position = 0L;
				int num;
				for (int i = 0; i < array.Length; i += num)
				{
					num = originalPin.Read(array, i, array.Length - i);
					if (num <= 0)
					{
						throw new EndOfStreamException();
					}
				}
				originalPin.Position = 0L;
				if (GameNvText.Hash(array) != original.OriginalHash)
				{
					throw new InvalidDataException("Native original bytes changed");
				}
				Dictionary<string, string> dictionary = GameNvText.Decode(array);
				if (dictionary.Count != 14 || dictionary["schema"] != "game-nv-original-1" || dictionary["lease"] != original.LeaseToken || dictionary["profile"] != name || dictionary["predefined"] != predefined.ToString(CultureInfo.InvariantCulture) || dictionary["scope"] != scope || dictionary["own"] != (own ? "1" : "0") || dictionary["target"] != path || dictionary["image"] != original.ImageHash || dictionary["plan"] != original.PlanHash)
				{
					throw new InvalidDataException("Native original resource fields differ");
				}
				new GameNvRecoveryIdentity(dictionary["plan_text"], original.PlanHash, path, original.ImageHash);
				uint[] array2 = new uint[2] { 274197361u, 549528094u };
				for (int j = 0; j < array2.Length; j++)
				{
					uint key = array2[j];
					string text = key.ToString("X8");
					if (dictionary["original." + text] != originals[key].Text || dictionary["target." + text] != (targets.ContainsKey(key) ? "1" : "leave"))
					{
						throw new InvalidDataException("Native original key vector differs");
					}
				}
			}

			private void RequireIssued(string operation)
			{
				RequireOriginal();
				string text = Path.Combine(original.JournalDirectory, "issued-" + operation + ".tsv");
				NoReparse.CheckPath(text);
				Dictionary<string, string> dictionary = new Dictionary<string, string>();
				dictionary.Add("schema", "game-nv-phase-1");
				dictionary.Add("lease", original.LeaseToken);
				dictionary.Add("original", original.OriginalHash);
				dictionary.Add("operation", "issued-" + operation);
				byte[] array = GameNvText.Encode(dictionary);
				using (FileStream fileStream = new FileStream(text, FileMode.Open, FileAccess.Read, FileShare.Read))
				{
					if (fileStream.Length != array.Length)
					{
						throw new InvalidDataException("Native issued operation changed");
					}
					for (int i = 0; i < array.Length; i++)
					{
						if (fileStream.ReadByte() != array[i])
						{
							throw new InvalidDataException("Native issued operation is not bound to original");
						}
					}
				}
			}

			private void Active()
			{
				if (disposed)
				{
					throw new ObjectDisposedException("Game NVIDIA fresh writer");
				}
			}

			private void Require()
			{
				Active();
				if (recovery)
				{
					recover();
				}
				else
				{
					forward();
				}
			}

			public void BeginRecovery(GameNvRecoveryIdentity identity)
			{
				Active();
				if (identity == null || identity.PlanHash != original.PlanHash || identity.Executable != path || identity.ImageHash != original.ImageHash || identity.OriginalHash != original.OriginalHash || identity.JournalDirectory != original.JournalDirectory || identity.ProfileName != name)
				{
					throw new InvalidDataException("Recovery changed original game identity");
				}
				Clear();
				recover();
				recovery = true;
			}

			private T ReadFresh<T>(Func<IGameNvProfileWriter, T> read)
			{
				Require();
				using (gate())
				{
					Require();
					using (IGameNvProfileWriter gameNvProfileWriter = open())
					{
						Require();
						gameNvProfileWriter.Reload();
						Require();
						T result = read(gameNvProfileWriter);
						Require();
						return result;
					}
				}
			}

			public void Reload()
			{
				Active();
				if (pending != null)
				{
					throw new InvalidOperationException("Cannot discard an issued staged NVIDIA operation");
				}
			}

			public GameNvProfile FindApplication(string query)
			{
				return ReadFresh((IGameNvProfileWriter api) => api.FindApplication(query));
			}

			public GameNvProfile FindProfile(string profile)
			{
				return ReadFresh((IGameNvProfileWriter api) => api.FindProfile(profile));
			}

			public GameNvSetting Read(string profile, uint id)
			{
				GameNvPrelaunchPlan.RequireId(id);
				return ReadFresh((IGameNvProfileWriter api) => api.Read(profile, id));
			}

			public GameNvSetting Reference(uint id)
			{
				GameNvPrelaunchPlan.RequireId(id);
				return ReadFresh((IGameNvProfileWriter api) => api.Reference(id));
			}

			private GameNvProfile Scope(IGameNvProfileWriter api, bool allowAbsent)
			{
				GameNvProfile gameNvProfile = api.FindProfile(name);
				if (gameNvProfile == null)
				{
					if (allowAbsent && own)
					{
						return null;
					}
					throw new IOException("Game profile disappeared before native commit");
				}
				if (gameNvProfile.Name != name || gameNvProfile.Predefined != predefined || !gameNvProfile.CompleteApplications || !gameNvProfile.CompleteLocalSettings || (!own && gameNvProfile.ScopeHash != scope) || (own && (gameNvProfile.Applications.Count > 1 || (gameNvProfile.Applications.Count == 1 && (!gameNvProfile.Applications[0].IsPlainExactPath(path) || gameNvProfile.Applications[0].Predefined != 0)))))
				{
					throw new IOException("Native game profile scope changed");
				}
				return gameNvProfile;
			}

			private static string Stamp(GameNvProfile profile)
			{
				if (profile != null)
				{
					return profile.ScopeHash + ":" + profile.LocalSettingCount;
				}
				return "absent";
			}

			private void Stage(string profile, uint? id, string operation, Action<IGameNvProfileWriter> stage, Action<IGameNvProfileWriter> readback)
			{
				Require();
				if (profile != name || pending != null)
				{
					throw new InvalidOperationException("NVIDIA operation escaped its original scope or transaction");
				}
				RequireIssued(operation);
				using (gate())
				{
					Require();
					using (IGameNvProfileWriter gameNvProfileWriter = open())
					{
						Require();
						gameNvProfileWriter.Reload();
						Require();
						GameNvProfile profile2 = Scope(gameNvProfileWriter, true);
						pendingScope = Stamp(profile2);
						pendingValue = (id.HasValue ? gameNvProfileWriter.Read(name, id.Value).Text : null);
						Require();
					}
				}
				pendingId = id;
				pending = stage;
				verify = readback;
				pendingOperation = operation;
			}

			public void CreateProfile(string profile)
			{
				if (!own || recovery)
				{
					throw new InvalidOperationException("No original profile creation authority");
				}
				Stage(profile, null, "create", delegate(IGameNvProfileWriter api)
				{
					if (Scope(api, true) != null || api.FindApplication(path) != null || api.FindApplication(Path.GetFileName(path)) != null)
					{
						throw new IOException("Existing application/profile cannot be claimed");
					}
					api.CreateProfile(name);
				}, delegate(IGameNvProfileWriter api)
				{
					GameNvProfile gameNvProfile = Scope(api, false);
					if (gameNvProfile.Applications.Count != 0 || gameNvProfile.LocalSettingCount != 0)
					{
						throw new IOException("Created profile is not locally empty");
					}
				});
			}

			public void Bind(string profile, string exactPath)
			{
				if (!own || recovery || exactPath != path)
				{
					throw new InvalidOperationException("No exact owned binding authority");
				}
				Stage(profile, null, "bind", delegate(IGameNvProfileWriter api)
				{
					GameNvProfile gameNvProfile2 = Scope(api, false);
					if (gameNvProfile2.Applications.Count != 0 || gameNvProfile2.LocalSettingCount != 0 || api.FindApplication(path) != null || api.FindApplication(Path.GetFileName(path)) != null)
					{
						throw new IOException("Owned application binding changed");
					}
					api.Bind(name, path);
				}, delegate(IGameNvProfileWriter api)
				{
					GameNvProfile gameNvProfile = Scope(api, false);
					if (gameNvProfile.Applications.Count != 1 || !gameNvProfile.Applications[0].IsPlainExactPath(path))
					{
						throw new IOException("Exact binding did not persist");
					}
				});
			}

			public void SetUserOverride(string profile, uint id, uint? value)
			{
				GameNvPrelaunchPlan.RequireId(id);
				uint expected;
				GameNvSetting baseline;
				if (!targets.TryGetValue(id, out expected) || !originals.TryGetValue(id, out baseline))
				{
					throw new InvalidOperationException("Unissued game setting");
				}
				if ((!recovery && (!value.HasValue || value.Value != 1 || baseline.UserOverride || GameNvProfilePolicy.Evaluate(id, 1u, baseline).State != GameNvPending.Ready)) || (recovery && value != (baseline.UserOverride ? new uint?(baseline.Value) : null)))
				{
					throw new InvalidOperationException("Value/provenance escaped game policy");
				}
				Stage(profile, id, (recovery ? "restore-" : "set-") + id.ToString("X8"), delegate(IGameNvProfileWriter api)
				{
					GameNvProfile gameNvProfile = Scope(api, false);
					if (gameNvProfile.Applications.Count != 1 || !gameNvProfile.Applications[0].IsPlainExactPath(path))
					{
						throw new IOException("No exact target binding");
					}
					GameNvSetting gameNvSetting2 = api.Read(name, id);
					bool num;
					if (!recovery)
					{
						if (!own)
						{
							num = gameNvSetting2.Text != baseline.Text;
						}
						else
						{
							if (gameNvSetting2.UserOverride || gameNvSetting2.Resolved != baseline.Resolved)
							{
								goto RejectOverrideOwnershipChange;
							}
							if (!gameNvSetting2.Resolved)
							{
								goto ApplyOwnedOverride;
							}
							num = gameNvSetting2.Value != baseline.Value;
						}
					}
					else
					{
						if (!gameNvSetting2.UserOverride)
						{
							goto RejectOverrideOwnershipChange;
						}
						num = gameNvSetting2.Value != expected;
					}
					if (num)
					{
						goto RejectOverrideOwnershipChange;
					}
					goto ApplyOwnedOverride;
					ApplyOwnedOverride:
					api.SetUserOverride(name, id, value);
					return;
					RejectOverrideOwnershipChange:
					throw new IOException("Current native override is not the owned expected value");
				}, delegate(IGameNvProfileWriter api)
				{
					Scope(api, false);
					GameNvSetting gameNvSetting = api.Read(name, id);
					if (gameNvSetting.UserOverride != value.HasValue || (value.HasValue && gameNvSetting.Value != value.Value))
					{
						throw new IOException("Native override persistence/provenance differs");
					}
				});
			}

			public void Unbind(string profile, string exactPath)
			{
				if (!own || !recovery || exactPath != path)
				{
					throw new InvalidOperationException("No original application cleanup authority");
				}
				Stage(profile, null, "unbind", delegate(IGameNvProfileWriter api)
				{
					GameNvProfile gameNvProfile = Scope(api, false);
					if (gameNvProfile.LocalSettingCount != 0 || gameNvProfile.Applications.Count != 1 || !gameNvProfile.Applications[0].IsPlainExactPath(path))
					{
						throw new IOException("Owned application acquired foreign data");
					}
					api.Unbind(name, path);
				}, delegate(IGameNvProfileWriter api)
				{
					if (Scope(api, false).Applications.Count != 0)
					{
						throw new IOException("Owned binding did not disappear");
					}
				});
			}

			public void DeleteEmptyProfile(string profile)
			{
				if (!own || !recovery)
				{
					throw new InvalidOperationException("No original profile deletion authority");
				}
				Stage(profile, null, "delete", delegate(IGameNvProfileWriter api)
				{
					GameNvProfile gameNvProfile = Scope(api, false);
					if (gameNvProfile.Applications.Count != 0 || gameNvProfile.LocalSettingCount != 0)
					{
						throw new IOException("Only locally empty owned profile may be deleted");
					}
					api.DeleteEmptyProfile(name);
				}, delegate(IGameNvProfileWriter api)
				{
					if (api.FindProfile(name) != null)
					{
						throw new IOException("Owned profile deletion did not persist");
					}
				});
			}

			public void Save()
			{
				Active();
				if (pending == null)
				{
					throw new InvalidOperationException("No issued native operation to save");
				}
				Action<IGameNvProfileWriter> action = pending;
				Action<IGameNvProfileWriter> action2 = verify;
				try
				{
					Require();
					using (gate())
					{
						Require();
						using (IGameNvProfileWriter gameNvProfileWriter = open())
						{
							Require();
							gameNvProfileWriter.Reload();
							Require();
							if (Stamp(Scope(gameNvProfileWriter, true)) != pendingScope || (pendingId.HasValue && gameNvProfileWriter.Read(name, pendingId.Value).Text != pendingValue))
							{
								throw new IOException("Native scope/value changed between preparation and commit");
							}
							RequireIssued(pendingOperation);
							action(gameNvProfileWriter);
							Require();
							RequireIssued(pendingOperation);
							gameNvProfileWriter.Save();
						}
						Require();
						using (IGameNvProfileWriter gameNvProfileWriter2 = open())
						{
							Require();
							gameNvProfileWriter2.Reload();
							Require();
							action2(gameNvProfileWriter2);
							Require();
						}
					}
				}
				finally
				{
					Clear();
				}
			}

			private void Clear()
			{
				pending = (verify = null);
				pendingId = null;
				pendingScope = (pendingValue = (pendingOperation = null));
			}

			public void Dispose()
			{
				Clear();
				disposed = true;
				if (originalPin != null)
				{
					originalPin.Dispose();
					originalPin = null;
				}
			}
		}

		private readonly SessionCall load;

		private readonly SessionCall close;

		private readonly Find find;

		private readonly FindApp findApp;

		private readonly GetInfo info;

		private readonly EnumApps enumApps;

		private readonly GetSetting get;

		private readonly EnumSettings enumSettings;

		private readonly Global global;

		private IntPtr session;

		[DllImport("nvapi64.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "nvapi_QueryInterface")]
		private static extern IntPtr Query(uint id);

		private static T Resolve<T>(uint id) where T : class
		{
			IntPtr intPtr = Query(id);
			if (intPtr == IntPtr.Zero)
			{
				throw new NotSupportedException("NVIDIA readonly entry unavailable: " + id.ToString("X8"));
			}
			return (T)(object)Marshal.GetDelegateForFunctionPointer(intPtr, typeof(T));
		}

		private static void Ok(int status, string operation)
		{
			if (status != 0)
			{
				throw new IOException("NVIDIA readonly " + operation + " failed: " + status);
			}
		}

		internal static bool FoundStatus(int status, int exactNotFound, string operation)
		{
			if (status == exactNotFound)
			{
				return false;
			}
			Ok(status, operation);
			return true;
		}

		private static uint Version<T>(uint revision = 1u)
		{
			return (uint)Marshal.SizeOf(typeof(T)) | (revision << 16);
		}

		internal static void ValidateAbi()
		{
			if (IntPtr.Size != 8 || Marshal.SizeOf(typeof(Profile)) != 4116 || Marshal.SizeOf(typeof(Application)) != 20492 || Marshal.SizeOf(typeof(Setting)) != 12320 || Marshal.OffsetOf(typeof(Application), "Version").ToInt32() != 0 || Marshal.OffsetOf(typeof(Application), "Predefined").ToInt32() != 4 || Marshal.OffsetOf(typeof(Application), "Name").ToInt32() != 8 || Marshal.OffsetOf(typeof(Application), "Friendly").ToInt32() != 4104 || Marshal.OffsetOf(typeof(Application), "Launcher").ToInt32() != 8200 || Marshal.OffsetOf(typeof(Application), "Folder").ToInt32() != 12296 || Marshal.OffsetOf(typeof(Application), "Flags").ToInt32() != 16392 || Marshal.OffsetOf(typeof(Application), "Command").ToInt32() != 16396 || Marshal.OffsetOf(typeof(Profile), "Apps").ToInt32() != 4108 || Marshal.OffsetOf(typeof(Profile), "Settings").ToInt32() != 4112 || Marshal.OffsetOf(typeof(Setting), "Location").ToInt32() != 4108 || Marshal.OffsetOf(typeof(Setting), "Value").ToInt32() != 8220 || Version<Application>(4u) != 282636 || Version<Profile>() != 69652 || Version<Setting>() != 77856)
			{
				throw new NotSupportedException("NVIDIA readonly V4 application ABI mismatch");
			}
		}

		internal GameNvProfileBackend()
		{
			try
			{
				ValidateAbi();
				Ok(Resolve<Init>(22079528u)(), "initialize");
				close = Resolve<SessionCall>(3671707640u);
				load = Resolve<SessionCall>(928890219u);
				find = Resolve<Find>(2118818315u);
				findApp = Resolve<FindApp>(4008011442u);
				info = Resolve<GetInfo>(1640853462u);
				enumApps = Resolve<EnumApps>(2141329210u);
				get = Resolve<GetSetting>(1941930808u);
				global = Resolve<Global>(1635516319u);
				enumSettings = Resolve<EnumSettings>(2922396122u);
				Ok(Resolve<Open>(110417198u)(out session), "open session");
				Reload();
			}
			catch (Exception originalFailure)
			{
				CleanupSequence.Complete(originalFailure, Dispose);
				throw;
			}
		}

		private void Active()
		{
			if (session == IntPtr.Zero)
			{
				throw new ObjectDisposedException("Readonly game NVIDIA session");
			}
		}

		public void Reload()
		{
			Active();
			Ok(load(session), "reload");
		}

		public GameNvProfile FindApplication(string fullPathOrName)
		{
			Active();
			Application application = default(Application);
			application.Version = Version<Application>(4u);
			Application app = application;
			IntPtr profile;
			int status = findApp(session, fullPathOrName, out profile, ref app);
			if (!FoundStatus(status, -166, "find application"))
			{
				return null;
			}
			return Snapshot(profile);
		}

		public GameNvProfile FindProfile(string name)
		{
			Active();
			IntPtr profile;
			int status = find(session, name, out profile);
			if (!FoundStatus(status, -163, "find profile"))
			{
				return null;
			}
			return Snapshot(profile);
		}

		private IntPtr Handle(string name)
		{
			Active();
			IntPtr profile;
			Ok(find(session, name, out profile), "find existing profile");
			if (profile == IntPtr.Zero)
			{
				throw new InvalidDataException("Missing existing profile handle");
			}
			return profile;
		}

		private GameNvProfile Snapshot(IntPtr profile)
		{
			if (profile == IntPtr.Zero)
			{
				throw new InvalidDataException("Missing readonly profile handle");
			}
			Profile profile2 = default(Profile);
			profile2.Version = Version<Profile>();
			Profile profile3 = profile2;
			Ok(info(session, profile, ref profile3), "profile metadata");
			if (profile3.Version != Version<Profile>())
			{
				throw new InvalidDataException("Profile metadata version changed");
			}
			List<GameNvApplication> list = new List<GameNvApplication>();
			if (profile3.Apps == 1)
			{
				uint count = 1u;
				Application application = default(Application);
				application.Version = Version<Application>(4u);
				Application app = application;
				Ok(enumApps(session, profile, 0u, ref count, ref app), "application metadata");
				if (count != 1 || app.Version != Version<Application>(4u))
				{
					throw new InvalidDataException("Application membership/version changed");
				}
				list.Add(new GameNvApplication(app.Name, app.Friendly, app.Launcher, app.Folder, app.Flags, app.Command, app.Predefined));
			}
			uint num = 0u;
			bool completeLocalSettings = false;
			if (profile3.Settings <= 4096)
			{
				HashSet<uint> hashSet = new HashSet<uint>();
				for (uint num2 = 0u; num2 <= 4096; num2++)
				{
					uint count2 = 1u;
					Setting setting = NewSetting(0u);
					int num3 = enumSettings(session, profile, num2, ref count2, ref setting);
					if (num3 == -7 || (num3 == 0 && count2 == 0))
					{
						completeLocalSettings = true;
						break;
					}
					Ok(num3, "enumerate profile settings");
					if (count2 != 1 || setting.Version != Version<Setting>() || setting.Location > 3 || !hashSet.Add(setting.Id))
					{
						throw new InvalidDataException("Incomplete or inconsistent NVIDIA local setting enumeration");
					}
					if (num2 == 4096)
					{
						break;
					}
					if (setting.Location == 0)
					{
						num++;
					}
				}
			}
			Profile profile4 = default(Profile);
			profile4.Version = Version<Profile>();
			Profile profile5 = profile4;
			Ok(info(session, profile, ref profile5), "verify profile membership");
			if (profile5.Version != profile3.Version || profile5.Name != profile3.Name || profile5.Predefined != profile3.Predefined || profile5.Apps != profile3.Apps || profile5.Settings != profile3.Settings)
			{
				throw new IOException("NVIDIA application/settings membership changed during metadata read");
			}
			return new GameNvProfile(profile3.Name, profile3.Predefined, profile3.Settings, list, profile3.Apps <= 1, num, completeLocalSettings);
		}

		private static Setting NewSetting(uint id)
		{
			Setting result = default(Setting);
			result.Version = Version<Setting>();
			result.Id = id;
			result.Type = 0u;
			result.PredefinedPad = new byte[4096];
			result.ValuePad = new byte[4096];
			return result;
		}

		private GameNvSetting ReadHandle(IntPtr profile, uint id)
		{
			GameNvPrelaunchPlan.RequireId(id);
			Setting setting = NewSetting(id);
			int status = get(session, profile, id, ref setting);
			if (!FoundStatus(status, -160, "read setting"))
			{
				return new GameNvSetting(false, 0u, 0u, 0u, 0u, 0u);
			}
			if (setting.Id != id || setting.Type != 0 || setting.Version != Version<Setting>())
			{
				throw new InvalidDataException("NVIDIA setting ID/type/version mismatch");
			}
			return new GameNvSetting(true, setting.Value, setting.Location, setting.CurrentPredefined, setting.PredefinedValid, setting.Predefined);
		}

		public GameNvSetting Read(string profileName, uint id)
		{
			return ReadHandle(Handle(profileName), id);
		}

		public GameNvSetting Reference(uint id)
		{
			Active();
			IntPtr profile;
			Ok(global(session, out profile), "selected global profile");
			if (profile == IntPtr.Zero)
			{
				throw new InvalidDataException("Missing selected global profile");
			}
			return ReadHandle(profile, id);
		}

		public void Dispose()
		{
			IntPtr intPtr = session;
			session = IntPtr.Zero;
			if (intPtr != IntPtr.Zero)
			{
				Ok(close(intPtr), "close session");
			}
		}

		private static void Text(string value)
		{
			if (string.IsNullOrEmpty(value) || value.Length >= 2048 || value.IndexOf('\0') >= 0)
			{
				throw new InvalidDataException("NVIDIA string is not an exact bounded value");
			}
		}

		internal static IGameNvProfileWriter OpenWritable(GameNvNativeAuthority authority, GameNvPrelaunchPlan plan, GameNvRecoveryIdentity original, string name, string path, bool own, uint predefined, string scopeHash, IDictionary<uint, GameNvSetting> originals, IDictionary<uint, uint> targets, bool recovery)
		{
			if (authority == null || original == null)
			{
				throw new ArgumentNullException("Original NVIDIA authority");
			}
			if (!original.ResourceBound || original.ProfileName != name || original.ProfileScopeHash != scopeHash || original.OwnResources != own || original.ProfilePredefined != predefined || original.Executable != path)
			{
				throw new InvalidDataException("Native provider lacks its bound original resource receipt");
			}
			if (recovery)
			{
				authority.RequireRecovery(original);
			}
			else
			{
				authority.RequireForward(plan);
			}
			return new GameNvFreshProfileWriter(() => new NativeSession(), GameNvNativeGate.Enter, delegate
			{
				authority.RequireForward(plan);
			}, delegate
			{
				authority.RequireRecovery(original);
			}, original, name, path, own, predefined, scopeHash, originals, targets, recovery);
		}
	}
}
