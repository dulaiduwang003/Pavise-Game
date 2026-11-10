using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PaviseApp
{
    internal sealed class GameNvProfileLease : IDisposable
    {
        private sealed class NativeAdmission : IGameNvRuntimeAdmission
        {
            private readonly GameNvNativeAuthority original;

            internal NativeAdmission(GameNvNativeAuthority authority)
            {
                original = authority;
            }

            public GameNvTargetPhase Recheck(GameNvPrelaunchPlan plan)
            {
                original.RequireForward(plan);
                return GameNvTargetPhase.Closed;
            }

            public void RequireRecovery(GameNvRecoveryIdentity receipt)
            {
                original.RequireRecovery(receipt);
            }
        }

        private static readonly uint[] ids = new uint[2] { 274197361u, 549528094u };

        private readonly IGameNvProfileWriter backend;

        private readonly IGameNvRuntimeAdmission admission;

        private readonly GameNvNativeAuthority nativeAuthority;

        private readonly GameNvPrelaunchPlan plan;

        private readonly GameNvRecoveryIdentity recoveryIdentity;

        private readonly string directory;

        private readonly string token;

        private readonly string name;

        private readonly string path;

        private readonly string imageHash;

        private readonly string planHash;

        private readonly string scopeHash;

        private readonly bool own;

        private readonly uint predefined;

        private readonly Dictionary<uint, GameNvSetting> originals;

        private readonly Dictionary<uint, uint> targets;

        private FileStream storeLock;

        private FileStream profileLock;

        private FileStream originalPin;

        private string originalHash;

        private bool disposed;

        private bool restored;

        private bool nativeBackendClosed;

        internal bool ConfiguredForNextLaunch { get; private set; }

        internal bool ResolvedBaselineChanged { get; private set; }

        internal bool ActivationObserved
        {
            get
            {
                return false;
            }
        }

        internal bool BenefitEvaluated
        {
            get
            {
                return false;
            }
        }

        internal bool HasNativeMutationAuthority
        {
            get
            {
                if (disposed || restored || nativeAuthority == null || plan == null || backend == null)
                {
                    return false;
                }
                try
                {
                    nativeAuthority.RequireForward(plan);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        internal string JournalDirectory
        {
            get
            {
                return directory;
            }
        }

        internal bool DisposalStarted
        {
            get
            {
                return disposed;
            }
        }

        internal bool RestorationConfirmed
        {
            get
            {
                return restored;
            }
        }

        internal bool ResourcesClosed
        {
            get
            {
                if (originalPin == null && profileLock == null && storeLock == null)
                {
                    if (nativeAuthority != null && backend != null)
                    {
                        return nativeBackendClosed;
                    }
                    return true;
                }
                return false;
            }
        }

        private static string OwnName(string executable)
        {
            return "Pavise.Game." + GameNvText.Hash(Encoding.UTF8.GetBytes(executable.ToUpperInvariant())).Substring(0, 32);
        }

        private static GameNvInspection Result(GameNvPending state, GameNvProfile profile = null, string name = "", bool own = false, IDictionary<uint, uint> targets = null, IDictionary<uint, GameNvSetting> originals = null, IDictionary<uint, GameNvProfileDecision> decisions = null, bool productPolicyEvaluated = false, string originalPlanHash = null)
        {
            return new GameNvInspection(state, profile, name, own, targets ?? new Dictionary<uint, uint>(), originals ?? new Dictionary<uint, GameNvSetting>(), decisions ?? new Dictionary<uint, GameNvProfileDecision>(), productPolicyEvaluated, originalPlanHash);
        }

        internal static GameNvInspection Inspect(GameNvPrelaunchPlan plan, IGameNvProfileReader explicitlySuppliedReader = null)
        {
            return InspectCore(plan, explicitlySuppliedReader, false);
        }

        private static GameNvInspection InspectCore(GameNvPrelaunchPlan plan, IGameNvProfileReader explicitlySuppliedReader, bool mechanismOnly)
        {
            if (plan == null)
            {
                throw new ArgumentNullException("plan");
            }
            if (plan.Targets.Count == 0)
            {
                return Result(plan.Members.Values.FirstOrDefault((GameNvPending x) => x != GameNvPending.Off));
            }
            IGameNvProfileReader gameNvProfileReader = explicitlySuppliedReader;
            try
            {
                if (gameNvProfileReader == null)
                {
                    gameNvProfileReader = new GameNvProfileBackend();
                }
                gameNvProfileReader.Reload();
                GameNvProfile gameNvProfile = gameNvProfileReader.FindApplication(plan.Executable);
                bool createOwnedProfile = gameNvProfile == null;
                string profileName = (createOwnedProfile ? (OwnName(plan.Executable) + "." + Guid.NewGuid().ToString("N")) : gameNvProfile.Name);
                if (createOwnedProfile)
                {
                    if (gameNvProfileReader.FindApplication(Path.GetFileName(plan.Executable)) != null || gameNvProfileReader.FindProfile(profileName) != null)
                    {
                        return Result(GameNvPending.Scope);
                    }
                }
                else if (!gameNvProfile.CompleteApplications || gameNvProfile.Applications.Count != 1 || !gameNvProfile.Applications[0].IsPlainExactPath(plan.Executable))
                {
                    return Result(GameNvPending.Scope, gameNvProfile, profileName);
                }
                Dictionary<uint, GameNvSetting> gameNvSettingsById = new Dictionary<uint, GameNvSetting>();
                Dictionary<uint, uint> targetValuesById = new Dictionary<uint, uint>();
                Dictionary<uint, GameNvProfileDecision> gameNvProfileDecisionsById = new Dictionary<uint, GameNvProfileDecision>();
                foreach (uint settingId in ids)
                {
                    GameNvSetting gameNvSetting = (createOwnedProfile ? gameNvProfileReader.Reference(settingId) : gameNvProfileReader.Read(profileName, settingId));
                    if (gameNvSetting == null)
                    {
                        throw new InvalidDataException("Missing original setting provenance");
                    }
                    if (createOwnedProfile)
                    {
                        gameNvSetting = InheritedOriginal(gameNvSetting);
                    }
                    gameNvSettingsById.Add(settingId, gameNvSetting);
                    uint target;
                    GameNvProfileDecision gameNvProfileDecision = ((!plan.Targets.TryGetValue(settingId, out target)) ? new GameNvProfileDecision(plan.Members[(settingId != 274197361) ? GameNvMember.OpenGl : GameNvMember.PState], GameNvProfilePolicy.Source(gameNvSetting)) : (mechanismOnly ? new GameNvProfileDecision((!gameNvSetting.Resolved || gameNvSetting.Value != target) ? GameNvPending.Ready : GameNvPending.AlreadyMatches, GameNvProfilePolicy.Source(gameNvSetting)) : GameNvProfilePolicy.Evaluate(settingId, target, gameNvSetting)));
                    gameNvProfileDecisionsById.Add(settingId, gameNvProfileDecision);
                    if (gameNvProfileDecision.State == GameNvPending.Ready)
                    {
                        targetValuesById.Add(settingId, target);
                    }
                }
                gameNvProfileReader.Reload();
                GameNvProfile application = gameNvProfileReader.FindApplication(plan.Executable);
                bool scopeChanged = createOwnedProfile ? application != null
                    : application == null || application.ScopeHash != gameNvProfile.ScopeHash;
                if (!scopeChanged)
                {
                    foreach (uint currentSettingId in ids)
                    {
                        if ((createOwnedProfile ? InheritedOriginal(gameNvProfileReader.Reference(currentSettingId)) : gameNvProfileReader.Read(profileName, currentSettingId)).Text != gameNvSettingsById[currentSettingId].Text)
                        {
                            throw new IOException("Game NVIDIA originals changed during inspection");
                        }
                    }
                    GameNvPending state = ((targetValuesById.Count > 0) ? GameNvPending.Integration : (gameNvProfileDecisionsById.Values.Any((GameNvProfileDecision x) => x.State == GameNvPending.ProfileRule) ? GameNvPending.ProfileRule : (gameNvProfileDecisionsById.Values.Any((GameNvProfileDecision x) => x.State == GameNvPending.UserPreference) ? GameNvPending.UserPreference : (gameNvProfileDecisionsById.Values.Any((GameNvProfileDecision x) => x.State == GameNvPending.Observation) ? GameNvPending.Observation : (gameNvProfileDecisionsById.Values.Any((GameNvProfileDecision x) => x.State == GameNvPending.ApiEvidence) ? GameNvPending.ApiEvidence : GameNvPending.AlreadyMatches)))));
                    return Result(state, gameNvProfile, profileName, createOwnedProfile, targetValuesById, gameNvSettingsById, gameNvProfileDecisionsById, !mechanismOnly, plan.Hash);
                }
                throw new IOException("Game NVIDIA profile scope changed during inspection");
            }
            finally
            {
                if (explicitlySuppliedReader == null && gameNvProfileReader != null)
                {
                    gameNvProfileReader.Dispose();
                }
            }
        }

        private static GameNvSetting InheritedOriginal(GameNvSetting value)
        {
            return new GameNvSetting(value.Resolved, value.Value, (value.Location == 0) ? 1u : value.Location, value.CurrentPredefined, value.PredefinedValid, value.Predefined);
        }

        private bool OriginalMatches(GameNvSetting observed, uint id)
        {
            GameNvSetting gameNvSetting = originals[id];
            if (!own)
            {
                return observed.Text == gameNvSetting.Text;
            }
            if (!observed.UserOverride && observed.Resolved == gameNvSetting.Resolved)
            {
                if (gameNvSetting.Resolved)
                {
                    return observed.Value == gameNvSetting.Value;
                }
                return true;
            }
            return false;
        }

        internal static GameNvProfileLease PrepareNative(GameNvPrelaunchPlan originalPlan, GameNvNativeAuthority authority)
        {
            if (authority == null || originalPlan == null)
            {
                throw new ArgumentNullException("Original native game authority");
            }
            authority.RequireForward(originalPlan);
            string fullPath = Path.GetFullPath(authority.CanonicalStore);
            NoReparse.CheckPath(fullPath);
            Directory.CreateDirectory(fullPath);
            FileStream fileStream = LockStore(fullPath);
            try
            {
                RequireStoreNoDebt(fullPath);
                authority.RequireForward(originalPlan);
                GameNvInspection gameNvInspection = Inspect(originalPlan);
                authority.RequireForward(originalPlan);
                if (gameNvInspection.State != GameNvPending.Integration)
                {
                    throw new GameNvPreparationPending(gameNvInspection.State);
                }
                gameNvInspection.RequireCurrentProductPolicy(originalPlan);
                GameNvProfileLease gameNvProfileLease = new GameNvProfileLease(originalPlan, fullPath, new NativeAdmission(authority), null, gameNvInspection, authority, fileStream);
                fileStream = null;
                try
                {
                    gameNvProfileLease.Apply();
                    return gameNvProfileLease;
                }
                catch (Exception originalFailure)
                {
                    CleanupSequence.Complete(originalFailure, gameNvProfileLease.Restore, gameNvProfileLease.Release);
                    throw;
                }
            }
            finally
            {
                if (fileStream != null)
                {
                    fileStream.Dispose();
                }
            }
        }

        internal static void RecoverNative(string journalDirectory, GameNvNativeAuthority authority)
        {
            if (authority == null)
            {
                throw new ArgumentNullException("Original recovery authority");
            }
            string fullPath = Path.GetFullPath(journalDirectory);
            string fullPath2 = Path.GetFullPath(authority.CanonicalStore);
            if (!string.Equals(Path.GetDirectoryName(Path.GetDirectoryName(fullPath)), fullPath2, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Recovery journal is outside canonical NVIDIA store");
            }
            using (GameNvProfileLease gameNvProfileLease = new GameNvProfileLease(fullPath, new NativeAdmission(authority), null, authority))
            {
                gameNvProfileLease.Restore();
            }
        }

        private GameNvProfileLease(GameNvPrelaunchPlan originalPlan, string store, IGameNvRuntimeAdmission originalAdmission, IGameNvProfileWriter writer, GameNvInspection inspection, GameNvNativeAuthority native = null, FileStream heldStore = null)
        {
            nativeAuthority = native;
            plan = originalPlan;
            backend = writer;
            admission = originalAdmission;
            token = (inspection.OwnResources ? inspection.ProfileName.Substring(inspection.ProfileName.Length - 32) : Guid.NewGuid().ToString("N"));
            name = inspection.ProfileName;
            path = plan.Executable;
            imageHash = plan.ExecutableHash;
            planHash = plan.Hash;
            own = inspection.OwnResources;
            recoveryIdentity = new GameNvRecoveryIdentity(plan.Text, planHash, path, imageHash);
            predefined = ((inspection.Profile != null) ? inspection.Profile.Predefined : 0u);
            scopeHash = ((inspection.Profile == null) ? "absent" : inspection.Profile.ScopeHash);
            originals = new Dictionary<uint, GameNvSetting>(inspection.Originals);
            targets = new Dictionary<uint, uint>(inspection.EligibleTargets);
            string profileRootResult = ProfileRoot(store, name, predefined);
            directory = Path.Combine(profileRootResult, token);
            try
            {
                storeLock = heldStore ?? LockStore(Path.GetDirectoryName(profileRootResult));
                profileLock = Lock(profileRootResult);
                RequireStoreNoDebt(Path.GetDirectoryName(profileRootResult));
                Directory.CreateDirectory(directory);
                Dictionary<string, string> textsByName = new Dictionary<string, string>
                {
                    { "schema", "game-nv-original-1" },
                    { "lease", token },
                    { "profile", name },
                    {
                        "predefined",
                        predefined.ToString(CultureInfo.InvariantCulture)
                    },
                    { "scope", scopeHash },
                    {
                        "own",
                        own ? "1" : "0"
                    },
                    { "target", path },
                    { "image", imageHash },
                    { "plan", planHash },
                    { "plan_text", plan.Text }
                };
                uint[] idsSnapshot = ids;
                for (int i = 0; i < idsSnapshot.Length; i++)
                {
                    uint key = idsSnapshot[i];
                    string keyText = key.ToString("X8");
                    textsByName.Add("original." + keyText, originals[key].Text);
                    textsByName.Add("target." + keyText, targets.ContainsKey(key) ? "1" : "leave");
                }
                byte[] bytes = GameNvText.Encode(textsByName);
                GameNvText.Create(Path.Combine(directory, "original.tsv"), bytes);
                originalHash = GameNvText.Hash(bytes);
                originalPin = PinOriginal();
                recoveryIdentity = recoveryIdentity.WithJournal(directory, originalHash, name, predefined, scopeHash, token, own);
                if (nativeAuthority != null)
                {
                    backend = GameNvProfileBackend.OpenWritable(nativeAuthority, plan, recoveryIdentity, name, path, own, predefined, scopeHash, originals, targets, false);
                }
            }
            catch (Exception originalFailure)
            {
                CleanupSequence.Complete(originalFailure, Release);
                throw;
            }
        }

        private GameNvProfileLease(string journalDirectory, IGameNvRuntimeAdmission originalAdmission, IGameNvProfileWriter writer, GameNvNativeAuthority native = null)
        {
            nativeAuthority = native;
            backend = writer;
            admission = originalAdmission;
            directory = Path.GetFullPath(journalDirectory);
            token = Path.GetFileName(directory);
            Guid result;
            if (!Guid.TryParseExact(token, "N", out result) || result == Guid.Empty)
            {
                throw new InvalidDataException("Invalid game NVIDIA recovery lease token");
            }
            try
            {
                string directoryName = Path.GetDirectoryName(directory);
                storeLock = LockStore(Path.GetDirectoryName(directoryName));
                profileLock = Lock(directoryName);
                originalPin = PinOriginal();
                byte[] bytes = ReadPinned(originalPin);
                originalHash = GameNvText.Hash(bytes);
                Dictionary<string, string> textsByName = GameNvText.Decode(bytes);
                if (textsByName.Count != 14 || textsByName["schema"] != "game-nv-original-1" || textsByName["lease"] != token || (textsByName["own"] != "0" && textsByName["own"] != "1"))
                {
                    throw new InvalidDataException("Invalid original game NVIDIA receipt");
                }
                name = textsByName["profile"];
                predefined = uint.Parse(textsByName["predefined"], CultureInfo.InvariantCulture);
                own = textsByName["own"] == "1";
                scopeHash = textsByName["scope"];
                path = textsByName["target"];
                imageHash = textsByName["image"];
                planHash = textsByName["plan"];
                recoveryIdentity = new GameNvRecoveryIdentity(textsByName["plan_text"], planHash, path, imageHash);
                if (!GameNvText.IsHash(imageHash) || !GameNvText.IsHash(planHash) || !Path.IsPathRooted(path) || Path.GetFullPath(path) != path || predefined > 1 || (own && (name != OwnName(path) + "." + token || predefined != 0 || scopeHash != "absent")) || (!own && !GameNvText.IsHash(scopeHash)) || Path.GetFileName(directoryName) != ProfileKey(name, predefined))
                {
                    throw new InvalidDataException("Original game NVIDIA profile identity changed");
                }
                originals = new Dictionary<uint, GameNvSetting>();
                targets = new Dictionary<uint, uint>();
                uint[] idsSnapshot = ids;
                for (int i = 0; i < idsSnapshot.Length; i++)
                {
                    uint key = idsSnapshot[i];
                    string keyText = key.ToString("X8");
                    originals.Add(key, GameNvSetting.Parse(textsByName["original." + keyText]));
                    string message = textsByName["target." + keyText];
                    if (message == "1")
                    {
                        targets.Add(key, 1u);
                    }
                    else if (message != "leave")
                    {
                        throw new InvalidDataException("Unknown original game NVIDIA target");
                    }
                }
                if (targets.Count == 0)
                {
                    throw new InvalidDataException("Empty original game NVIDIA transaction");
                }
                ValidateDirectory();
                recoveryIdentity = recoveryIdentity.WithJournal(directory, originalHash, name, predefined, scopeHash, token, own);
                if (nativeAuthority != null)
                {
                    backend = GameNvProfileBackend.OpenWritable(nativeAuthority, null, recoveryIdentity, name, path, own, predefined, scopeHash, originals, targets, true);
                }
            }
            catch (Exception originalFailure)
            {
                CleanupSequence.Complete(originalFailure, Release);
                throw;
            }
        }

        private static string ProfileKey(string name, uint predefined)
        {
            return GameNvText.Hash(Encoding.UTF8.GetBytes(name.ToUpperInvariant() + "\n" + predefined.ToString(CultureInfo.InvariantCulture)));
        }

        private static string ProfileRoot(string store, string name, uint predefined)
        {
            string fullPath = Path.GetFullPath(store);
            NoReparse.CheckPath(fullPath);
            Directory.CreateDirectory(fullPath);
            string result = Path.Combine(fullPath, ProfileKey(name, predefined));
            NoReparse.CheckPath(result);
            Directory.CreateDirectory(result);
            return result;
        }

        private static FileStream Lock(string root)
        {
            NoReparse.CheckPath(root);
            return new FileStream(Path.Combine(root, "profile.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        private static FileStream LockStore(string root)
        {
            NoReparse.CheckPath(root);
            return new FileStream(Path.Combine(root, "store.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        private static void RequireStoreNoDebt(string root)
        {
            string[] directories = Directory.GetDirectories(root);
            if (directories.Length > 256)
            {
                throw new InvalidDataException("Game NVIDIA profile history exceeds bound");
            }
            foreach (string root2 in directories)
            {
                if (!GameNvText.IsHash(Path.GetFileName(root2)))
                {
                    throw new InvalidDataException("Unexpected game NVIDIA profile directory");
                }
                RequireNoDebt(root2);
            }
        }

        private static void RequireNoDebt(string root)
        {
            string[] directories = Directory.GetDirectories(root);
            if (directories.Length > 256)
            {
                throw new InvalidDataException("Game NVIDIA journal history exceeds bound");
            }
            foreach (string path in directories)
            {
                NoReparse.CheckPath(path);
                string originalPath = Path.Combine(path, "original.tsv");
                if (File.Exists(originalPath))
                {
                    string terminalPath = Path.Combine(path, "terminal.tsv");
                    if (!File.Exists(terminalPath))
                    {
                        throw new GameNvPreparationPending(GameNvPending.Recovery);
                    }
                    byte[] bytes = ReadFile(originalPath, 1048576);
                    Dictionary<string, string> textsByName = GameNvText.Decode(bytes);
                    Dictionary<string, string> currentTextsByName = new Dictionary<string, string>();
                    currentTextsByName.Add("schema", "game-nv-phase-1");
                    currentTextsByName.Add("lease", Path.GetFileName(path));
                    currentTextsByName.Add("original", GameNvText.Hash(bytes));
                    currentTextsByName.Add("operation", "terminal");
                    byte[] second = GameNvText.Encode(currentTextsByName);
                    if (!ReadFile(terminalPath, 4096).SequenceEqual(second) || textsByName.Count != 14 || textsByName["schema"] != "game-nv-original-1" || textsByName["lease"] != Path.GetFileName(path))
                    {
                        throw new InvalidDataException("Invalid terminal game NVIDIA journal");
                    }
                }
                if (!File.Exists(originalPath) && Directory.GetFileSystemEntries(path).Length != 0)
                {
                    throw new InvalidDataException("Unrecognized partial game NVIDIA journal");
                }
            }
        }

        private FileStream PinOriginal()
        {
            string originalPath = Path.Combine(directory, "original.tsv");
            NoReparse.CheckPath(originalPath);
            FileStream fileStream = new FileStream(originalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                byte[] bytes = ReadPinned(fileStream);
                if (originalHash != null && GameNvText.Hash(bytes) != originalHash)
                {
                    throw new InvalidDataException("Original game NVIDIA journal changed before pin");
                }
                return fileStream;
            }
            catch
            {
                fileStream.Dispose();
                throw;
            }
        }

        private static byte[] ReadPinned(FileStream stream)
        {
            if (stream.Length < 1 || stream.Length > 1048576)
            {
                throw new InvalidDataException("Invalid game NVIDIA original bytes");
            }
            stream.Position = 0L;
            byte[] rawBytes = new byte[(int)stream.Length];
            int pinnedValue;
            for (int i = 0; i < rawBytes.Length; i += pinnedValue)
            {
                pinnedValue = stream.Read(rawBytes, i, rawBytes.Length - i);
                if (pinnedValue <= 0)
                {
                    throw new EndOfStreamException();
                }
            }
            stream.Position = 0L;
            return rawBytes;
        }

        private static byte[] ReadFile(string path, int maximum)
        {
            NoReparse.CheckPath(path);
            using (FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (fileStream.Length > maximum)
                {
                    throw new InvalidDataException("Game NVIDIA phase exceeds bound");
                }
                return ReadPinned(fileStream);
            }
        }

        private byte[] Event(string op)
        {
            Dictionary<string, string> textsByName = new Dictionary<string, string>();
            textsByName.Add("schema", "game-nv-phase-1");
            textsByName.Add("lease", token);
            textsByName.Add("original", originalHash);
            textsByName.Add("operation", op);
            return GameNvText.Encode(textsByName);
        }

        private bool Has(string op)
        {
            string combinedPath = Path.Combine(directory, op + ".tsv");
            if (!File.Exists(combinedPath))
            {
                return false;
            }
            if (!ReadFile(combinedPath, 4096).SequenceEqual(Event(op)))
            {
                throw new InvalidDataException("Game NVIDIA phase does not bind original receipt");
            }
            return true;
        }

        private void Mark(string op)
        {
            if (!Has(op))
            {
                GameNvText.Create(Path.Combine(directory, op + ".tsv"), Event(op));
            }
        }

        private void ValidateDirectory()
        {
            HashSet<string> textsSet = new HashSet<string>(new string[6] { "original.tsv", "terminal.tsv", "issued-create.tsv", "issued-bind.tsv", "issued-unbind.tsv", "issued-delete.tsv" }, StringComparer.Ordinal);
            uint[] idsSnapshot = ids;
            for (int i = 0; i < idsSnapshot.Length; i++)
            {
                uint settingId = idsSnapshot[i];
                textsSet.Add("issued-set-" + settingId.ToString("X8") + ".tsv");
                textsSet.Add("issued-restore-" + settingId.ToString("X8") + ".tsv");
            }
            foreach (string filePath in Directory.GetFiles(directory))
            {
                string fileName = Path.GetFileName(filePath);
                if (!textsSet.Contains(fileName))
                {
                    throw new InvalidDataException("Unknown game NVIDIA phase file");
                }
                if (fileName != "original.tsv")
                {
                    Has(Path.GetFileNameWithoutExtension(filePath));
                }
            }
            if (Directory.GetDirectories(directory).Length != 0)
            {
                throw new InvalidDataException("Unexpected nested game NVIDIA phase directory");
            }
        }

        private void BeforeWrite()
        {
            if (disposed || profileLock == null || originalPin == null || plan == null)
            {
                throw new InvalidOperationException("No active original game NVIDIA admission");
            }
            if (admission.Recheck(plan) != GameNvTargetPhase.Closed)
            {
                throw new GameNvPreparationPending(GameNvPending.NextLaunch);
            }
        }

        private GameNvProfile Current(bool allowEmpty)
        {
            backend.Reload();
            GameNvProfile gameNvProfile = backend.FindProfile(name);
            if (gameNvProfile == null)
            {
                return null;
            }
            if (gameNvProfile.Predefined != predefined || !gameNvProfile.CompleteApplications || !gameNvProfile.CompleteLocalSettings)
            {
                throw new IOException("Game NVIDIA profile identity/membership changed");
            }
            if (!own)
            {
                if (gameNvProfile.ScopeHash != scopeHash)
                {
                    throw new IOException("Borrowed game NVIDIA application scope changed");
                }
                return gameNvProfile;
            }
            bool expectedContents = gameNvProfile.Applications.Count == 0
                ? allowEmpty && gameNvProfile.LocalSettingCount == 0
                : gameNvProfile.Applications.Count == 1 && gameNvProfile.Applications[0].IsPlainExactPath(path);
            if (!expectedContents)
            {
                throw new IOException("Owned game NVIDIA profile gained another application or unbound settings");
            }
            return gameNvProfile;
        }

        private void Apply()
        {
            BeforeWrite();
            GameNvProfile gameNvProfile = Current(true);
            if (own)
            {
                if (gameNvProfile != null || backend.FindApplication(path) != null || backend.FindApplication(Path.GetFileName(path)) != null)
                {
                    throw new IOException("Game NVIDIA application/profile appeared before staging");
                }
                Mark("issued-create");
                BeforeWrite();
                backend.CreateProfile(name);
                BeforeWrite();
                backend.Save();
                gameNvProfile = Current(true);
                if (gameNvProfile == null || gameNvProfile.Applications.Count != 0)
                {
                    throw new IOException("Created game NVIDIA profile is not empty");
                }
                Mark("issued-bind");
                BeforeWrite();
                backend.Bind(name, path);
                BeforeWrite();
                backend.Save();
                gameNvProfile = Current(false);
                if (gameNvProfile == null)
                {
                    throw new IOException("Owned application binding did not persist");
                }
            }
            else if (gameNvProfile == null)
            {
                throw new IOException("Original borrowed game NVIDIA profile disappeared");
            }
            uint[] idsSnapshot = ids;
            for (int i = 0; i < idsSnapshot.Length; i++)
            {
                uint settingId = idsSnapshot[i];
                uint target;
                if (targets.TryGetValue(settingId, out target))
                {
                    BeforeWrite();
                    Current(false);
                    GameNvSetting observed = backend.Read(name, settingId);
                    if (!OriginalMatches(observed, settingId))
                    {
                        throw new IOException("Original game NVIDIA setting changed before staging");
                    }
                    Mark("issued-set-" + settingId.ToString("X8"));
                    BeforeWrite();
                    backend.SetUserOverride(name, settingId, target);
                    BeforeWrite();
                    backend.Save();
                    Current(false);
                    GameNvSetting gameNvSetting = backend.Read(name, settingId);
                    if (!gameNvSetting.UserOverride || gameNvSetting.Value != target)
                    {
                        throw new IOException("Game NVIDIA override did not persist after reload");
                    }
                }
            }
            foreach (uint currentSettingId in ids)
            {
                if (!targets.ContainsKey(currentSettingId) && !OriginalMatches(backend.Read(name, currentSettingId), currentSettingId))
                {
                    throw new IOException("Unselected game NVIDIA setting changed");
                }
            }
            BeforeWrite();
            ConfiguredForNextLaunch = true;
        }

        internal void Restore()
        {
            if (restored)
            {
                return;
            }
            if (disposed || originalPin == null)
            {
                throw new ObjectDisposedException("Game NVIDIA original lease");
            }
            admission.RequireRecovery(recoveryIdentity);
            ValidateDirectory();
            IGameNvProfileRecoveryContext gameNvProfileRecoveryContext = backend as IGameNvProfileRecoveryContext;
            if (gameNvProfileRecoveryContext != null)
            {
                gameNvProfileRecoveryContext.BeginRecovery(recoveryIdentity);
            }
            if (Has("terminal"))
            {
                restored = true;
                ConfiguredForNextLaunch = false;
                return;
            }
            GameNvProfile gameNvProfile = Current(own && (!ids.Any((uint id) => Has("issued-set-" + id.ToString("X8"))) || Has("issued-unbind")));
            if (gameNvProfile == null)
            {
                if (!own)
                {
                    throw new IOException("Original borrowed game NVIDIA profile is missing");
                }
                BeforeRecovery();
                Mark("terminal");
                restored = true;
                ConfiguredForNextLaunch = false;
                return;
            }
            if (own && !Has("issued-create"))
            {
                throw new IOException("A profile appeared without an original issued creation phase");
            }
            CleanupSequence.Complete(null, ((IEnumerable<uint>)ids).Select((Func<uint, Action>)((uint id) => delegate
            {
                RestoreMember(id);
            })).ToArray());
            if (own)
            {
                gameNvProfile = Current(Has("issued-unbind") || !Has("issued-bind"));
                if (!gameNvProfile.CompleteLocalSettings || gameNvProfile.LocalSettingCount != 0)
                {
                    throw new IOException("Owned game NVIDIA profile retains other local settings");
                }
                if (gameNvProfile.Applications.Count != 0)
                {
                    if (!Has("issued-bind"))
                    {
                        throw new IOException("Application appeared without an original binding phase");
                    }
                    Mark("issued-unbind");
                    BeforeRecovery();
                    backend.Unbind(name, path);
                    BeforeRecovery();
                    backend.Save();
                    gameNvProfile = Current(true);
                    if (gameNvProfile == null || gameNvProfile.Applications.Count != 0)
                    {
                        throw new IOException("Owned game NVIDIA unbind did not persist");
                    }
                }
                Mark("issued-delete");
                BeforeRecovery();
                backend.DeleteEmptyProfile(name);
                BeforeRecovery();
                backend.Save();
                backend.Reload();
                if (backend.FindProfile(name) != null)
                {
                    throw new IOException("Owned game NVIDIA profile deletion did not persist");
                }
            }
            BeforeRecovery();
            Mark("terminal");
            restored = true;
            ConfiguredForNextLaunch = false;
        }

        private void BeforeRecovery()
        {
            if (disposed || originalPin == null)
            {
                throw new ObjectDisposedException("Game NVIDIA recovery lease");
            }
            admission.RequireRecovery(recoveryIdentity);
        }

        private void RestoreMember(uint id)
        {
            if (!Has("issued-set-" + id.ToString("X8")))
            {
                return;
            }
            Current(false);
            GameNvSetting gameNvSetting = originals[id];
            GameNvSetting currentGameNvSetting = backend.Read(name, id);
            if (currentGameNvSetting.UserOverride == gameNvSetting.UserOverride && (!gameNvSetting.UserOverride || currentGameNvSetting.Value == gameNvSetting.Value))
            {
                ResolvedBaselineChanged |= currentGameNvSetting.Text != gameNvSetting.Text;
                return;
            }
            uint target;
            if (!targets.TryGetValue(id, out target) || !currentGameNvSetting.UserOverride || currentGameNvSetting.Value != target)
            {
                throw new IOException("External game NVIDIA override conflicts with original ownership");
            }
            Mark("issued-restore-" + id.ToString("X8"));
            BeforeRecovery();
            backend.SetUserOverride(name, id, gameNvSetting.UserOverride ? new uint?(gameNvSetting.Value) : null);
            BeforeRecovery();
            backend.Save();
            Current(false);
            currentGameNvSetting = backend.Read(name, id);
            if (currentGameNvSetting.UserOverride != gameNvSetting.UserOverride || (gameNvSetting.UserOverride && currentGameNvSetting.Value != gameNvSetting.Value))
            {
                throw new IOException("Game NVIDIA original override provenance was not restored");
            }
            ResolvedBaselineChanged |= currentGameNvSetting.Text != gameNvSetting.Text;
        }

        private void Release()
        {
            disposed = true;
            CleanupSequence.Complete(null, delegate
            {
                if (nativeAuthority != null && backend != null && !nativeBackendClosed)
                {
                    backend.Dispose();
                    nativeBackendClosed = true;
                }
            }, delegate
            {
                if (originalPin != null)
                {
                    originalPin.Dispose();
                    originalPin = null;
                }
            }, delegate
            {
                if (profileLock != null)
                {
                    profileLock.Dispose();
                    profileLock = null;
                }
            }, delegate
            {
                if (storeLock != null)
                {
                    storeLock.Dispose();
                    storeLock = null;
                }
            });
        }

        public void Dispose()
        {
            if (disposed)
            {
                Release();
                return;
            }
            Exception originalFailure = null;
            try
            {
                Restore();
            }
            catch (Exception ex)
            {
                originalFailure = ex;
                throw;
            }
            finally
            {
                CleanupSequence.Complete(originalFailure, Release);
            }
        }
    }
}
