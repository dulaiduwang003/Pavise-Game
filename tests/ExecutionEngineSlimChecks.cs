#if PAVISE_SELFTEST && PAVISE_EXECUTION_ENGINE_CHECKS
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;

namespace PaviseApp
{
    internal static partial class ExecutionEngineChecks
    {
        private static GameMode SlimMode(PerformanceActionCoordinator coordinator, string game, bool match)
        {
            var mode = (GameMode)FormatterServices.GetUninitializedObject(typeof(GameMode)); GC.SuppressFinalize(mode);
            typeof(GameMode).GetField("sync", Hidden).SetValue(mode, new object());
            typeof(GameMode).GetField("profileStore", Hidden).SetValue(mode, new GameProfileStore(Path.GetDirectoryName(game)));
            typeof(GameMode).GetField("performanceActions", Hidden).SetValue(mode, coordinator);
            typeof(GameMode).GetField("enabled", Hidden).SetValue(mode, true);
            typeof(GameMode).GetField("active", Hidden).SetValue(mode, match);
            typeof(GameMode).GetField("bgSuppressOn", Hidden).SetValue(mode, true);
            return mode;
        }
        private static void SelectPersistentActions(bool selected)
        {
            foreach (string key in new[] { PerformanceActionCatalog.Mitigations, PerformanceActionCatalog.OpenGl, PerformanceActionCatalog.ShortNames })
                Settings.Save(key, selected);
        }
        private static void SyncSlim(GameMode mode)
        {
            try { typeof(GameMode).GetMethod("SyncPerformanceAutomation", Hidden).Invoke(mode, new object[0]); }
            catch (TargetInvocationException error) {
                if (error.InnerException is NullReferenceException) Check(false, "all disabled persistent actions still enumerated the library");
                throw;
            }
        }
        private static void DisabledPersistentDiscoveryKeepsRecovery()
        {
            WithCoordinator(delegate(PerformanceActionCoordinator coordinator, string game, Dictionary<string, string> values) {
                SelectPersistentActions(false);
                var mode = SlimMode(coordinator, game, false);
                SyncSlim(mode);
                Check(coordinator.DrainForTest(3000) && Latest(coordinator) != null,
                    "an all disabled request skipped the original recovery coordinator");
                Check(RequestTargets(Latest(coordinator), "Games").Length == 0 && RequestTargets(Latest(coordinator), "Background").Length == 0 && values.Count == 0,
                    "all disabled discovery retained unused targets or reached a mocked write");
                foreach (string key in new[] { PerformanceActionCatalog.Mitigations, PerformanceActionCatalog.OpenGl, PerformanceActionCatalog.ShortNames }) {
                    Settings.Save(key, true); bool reached = false;
                    try { typeof(GameMode).GetMethod("SyncPerformanceAutomation", Hidden).Invoke(mode, new object[] { null }); }
                    catch (TargetInvocationException error) { if (!(error.InnerException is NullReferenceException)) throw; reached = true; }
                    Check(reached, "a selected persistent action stopped collecting its current library");
                    Settings.Save(key, false);
                }
                typeof(GameMode).GetField("profiles", Hidden).SetValue(mode, new List<GameProfile> { new GameProfile { ExecutablePath = game } });
                Settings.Save(PerformanceActionCatalog.OpenGl, true); SyncSlim(mode);
                Check(coordinator.DrainForTest(3000) && values.ContainsKey(game) && values[game] == "1" && PerformanceActionStore.All().Count == 1,
                    "an enabled persistent action lost its original apply and receipt path");
                Settings.Save(PerformanceActionCatalog.OpenGl, false);
                typeof(GameMode).GetField("profiles", Hidden).SetValue(mode, null); SyncSlim(mode);
                Check(coordinator.DrainForTest(3000) && values[game] == "absent" && PerformanceActionStore.All().Count == 0,
                    "all disabled discovery abandoned a previously owned persistent receipt");
            });
            Console.WriteLine("PASS actual idle discovery skips disabled library and keeps selected discovery and owned recovery");
        }
        private static void AutomaticOpenGlDoesNotFeedLegacyGames()
        {
            WithCoordinator(delegate(PerformanceActionCoordinator coordinator, string game, Dictionary<string, string> values) {
                var mode = SlimMode(coordinator, game, false);
                typeof(GameMode).GetField("profiles", Hidden).SetValue(mode, new List<GameProfile> { new GameProfile { ExecutablePath = game } });
                SyncSlim(mode);
                Check(coordinator.DrainForTest(3000) && values[game] == "1" && PerformanceActionStore.All().Count == 1,
                    "automatic legacy retirement fixture did not establish its manual owned receipt");
                SelectPersistentActions(false);
                var saved = new Dictionary<FieldInfo, object>();
                BindingFlags runtimeFlags = BindingFlags.Static | BindingFlags.NonPublic;
                foreach (string name in new[] { "certificate", "activated", "busy", "powerSuspended", "optionsFingerprint", "observedGeneration",
                    "activeFingerprint", "gameNvRuntimeGrant", "recoveryStatus" }) {
                    var field = typeof(CalibrationRuntime).GetField(name, runtimeFlags); saved[field] = field.GetValue(null);
                }
                bool requested = CalibrationRuntime.Requested; var priorAc = CalibrationRuntime.AcForTest;
                try {
                    Settings.Save(CalibrationRuntime.RequestedKey, true); CalibrationRuntime.AcForTest = delegate { return true; };
                    var certificate = new CalibrationCertificate { Fingerprint = "synthetic-policy-domain", EvidenceHash = new string('a', 64) };
                    certificate.Approved[PerformanceActionCatalog.OpenGl] = "1";
                    var grant = (CalibrationRuntime.GameNvRuntimeGrant)FormatterServices.GetUninitializedObject(typeof(CalibrationRuntime.GameNvRuntimeGrant));
                    GC.SuppressFinalize(grant);
                    string options = CalibrationRuntime.OptionFingerprintForTest();
                    foreach (var value in new Dictionary<string, object> {
                        { "original", certificate }, { "generation", typeof(CalibrationRuntime).GetField("sessionGeneration", runtimeFlags).GetValue(null) },
                        { "options", options }, { "approvedText", PerformanceActionCatalog.OpenGl + "=1" },
                        { "EvidenceHash", certificate.EvidenceHash }, { "MachineHash", certificate.Fingerprint } })
                        typeof(CalibrationRuntime.GameNvRuntimeGrant).GetField(value.Key, Hidden).SetValue(grant, value.Value);
                    foreach (var value in new Dictionary<string, object> {
                        { "certificate", certificate }, { "activated", true }, { "busy", false }, { "powerSuspended", false },
                        { "optionsFingerprint", options }, { "observedGeneration", Settings.MutationGeneration }, { "activeFingerprint", certificate.Fingerprint },
                        { "gameNvRuntimeGrant", grant }, { "recoveryStatus", new CalibrationRecoveryStatus(true, 0, 0, 0, "synthetic read only policy fixture") } })
                        typeof(CalibrationRuntime).GetField(value.Key, runtimeFlags).SetValue(null, value.Value);
                    Check(PerformanceTuning.ActionSelected(PerformanceActionCatalog.OpenGl) && !PerformanceTuning.LegacyActionSelected(PerformanceActionCatalog.OpenGl),
                        "automatic policy fixture did not separate NV approval from the retired legacy consumer");
                    Check(typeof(CalibrationRuntime.GameNvRuntimeGrant).GetField("sourceGate", Hidden).GetValue(grant) == null
                        && typeof(CalibrationRuntime.GameNvRuntimeGrant).GetField("pins", Hidden).GetValue(grant) == null,
                        "synthetic read only policy fixture unexpectedly retained original source resources");
                    bool nativeRefused = false;
                    try { grant.RequireCurrent(); } catch (ArgumentNullException) { nativeRefused = true; }
                    Check(nativeRefused, "synthetic policy fixture was not refused at its missing managed source gate");
                    typeof(GameMode).GetField("profiles", Hidden).SetValue(mode, null);
                    try { typeof(GameMode).GetMethod("SyncPerformanceAutomation", Hidden).Invoke(mode, new object[] { null }); }
                    catch (TargetInvocationException error) {
                        if (error.InnerException is NullReferenceException) Check(false, "automatic OpenGL approval enumerated unused legacy library");
                        throw;
                    }
                    Check(coordinator.DrainForTest(3000) && RequestTargets(Latest(coordinator), "Games").Length == 0
                        && values[game] == "absent" && PerformanceActionStore.All().Count == 0,
                        "automatic legacy suppression skipped its original owned receipt recovery");
                } finally {
                    foreach (var field in saved) field.Key.SetValue(null, field.Value);
                    CalibrationRuntime.AcForTest = priorAc; Settings.Save(CalibrationRuntime.RequestedKey, requested);
                }
            });
            Console.WriteLine("PASS actual automatic approval skips retired legacy discovery and restores its manual receipt policy fixture only");
        }
    }
}
#endif
