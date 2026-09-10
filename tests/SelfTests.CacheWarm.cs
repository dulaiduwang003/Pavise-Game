#if PAVISE_SELFTEST
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.Serialization;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static void RunCacheWarmRegressionTests()
        {
            const ulong gib = 1024UL * 1024 * 1024;
            Eq(0L, new CacheWarmEnvironment { Ac = false, Total = 16 * gib, Available = 8 * gib }.Budget);
            Eq(0L, new CacheWarmEnvironment { Ac = true, Total = 16 * gib, Available = 3 * gib }.Budget);
            Eq(0L, new CacheWarmEnvironment { Ac = true, Total = 64 * gib, Available = 8 * gib }.Budget);
            Eq((long)gib, new CacheWarmEnvironment { Ac = true, Total = 16 * gib, Available = 8 * gib }.Budget);
            Eq((long)(2 * gib), new CacheWarmEnvironment { Ac = true, Total = 64 * gib, Available = 32 * gib }.Budget);
            Eq(false, PolicyResolver.Global().CacheWarm);
            Eq("0", PolicyCatalog.ItemOf(PolicyCatalog.KeyCacheWarm).Fallback);
            Eq(false, GameProfile.IsRetiredOverrideKey(PolicyCatalog.KeyCacheWarm));
            Eq(true, GameProfile.IsRetiredOverrideKey("GmCacheWarm"));
            Eq(false, CacheWarmEngine.Asset("game.exe"));
            Eq(false, CacheWarmEngine.Asset("anticheat.dll"));
            Eq(true, CacheWarmEngine.Asset("map.wad.client"));

            string folder = Path.Combine(Path.GetTempPath(), "Pavise-CacheWarm-Test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string assets = Path.Combine(folder, "assets"); Directory.CreateDirectory(assets);
                byte[] original = Enumerable.Range(0, 2 * CacheWarmEngine.ChunkBytes).Select(i => (byte)(i % 251)).ToArray();
                string archive = Path.Combine(assets, "map.pak"); File.WriteAllBytes(archive, original);
                string executable = Path.Combine(assets, "game.exe"); File.WriteAllBytes(executable, original);
                string ignored = Path.Combine(assets, "EasyAntiCheat"); Directory.CreateDirectory(ignored);
                File.WriteAllBytes(Path.Combine(ignored, "ignored.pak"), original);
                Eq(1, CacheWarmEngine.Candidates(assets, delegate { return true; }).Count);
                Eq(0, CacheWarmEngine.Candidates(assets, delegate { return false; }).Count);
                long progress = 0; int waits = 0;
                Eq(1500000L, CacheWarmEngine.Warm(assets, 1500000, delegate { return true; },
                    delegate(int ms) { Eq(32, ms); waits++; return false; }, delegate(long n) { progress = n; }));
                Eq(1500000L, progress); Eq(2, waits);
                bool admitted = true;
                Eq((long)CacheWarmEngine.ChunkBytes, CacheWarmEngine.Warm(assets, original.Length,
                    delegate { return admitted; }, delegate { return false; }, delegate { admitted = false; }));
                using (var locked = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.None))
                    Eq(0L, CacheWarmEngine.Warm(assets, original.Length, delegate { return true; }, delegate { return false; }, delegate { }));
                Eq(true, File.ReadAllBytes(archive).SequenceEqual(original));
                Eq(true, File.ReadAllBytes(executable).SequenceEqual(original));
                using (var outside = new FileStream(Path.Combine(ignored, "ignored.pak"), FileMode.Open, FileAccess.Read))
                {
                    Eq(true, CacheWarmPlatform.HandleUnder(outside, assets));
                    Eq(false, CacheWarmPlatform.HandleUnder(outside, Path.Combine(folder, "assets-sibling")));
                }

                CacheWarmSurvivesFirstVerifiedBoost(folder);
                CacheWarmPathHandoffRetargets(folder);
                CacheWarmRunnerCancelsSupersededPaths(executable, assets);
                using (var f = new FamilyPolicyFixture(folder, "cachewarm-policy"))
                {
                    var mode = f.Mode;
                    var profile = f.Current("first");
                    mode.ProbeSessionPolicyApply(profile);
                    FamilyPolicySetField(mode, "activeDetection", FamilyObservationTarget(profile));
                    FamilyPolicySetField(mode, "active", true); FamilyPolicySetField(mode, "enabled", true);
                    FamilyPolicyInvoke(mode, "BeginCacheWarmSession");
                    Eq(false, mode.CacheWarmAdmissionForTest()());
                    Eq(true, mode.SetProfileOverride("first", PolicyCatalog.KeyCacheWarm, "1"));
                    Eq(true, mode.CacheWarmAdmissionForTest()()); // Per-game On wins over global Off.
                    var old = mode.CacheWarmAdmissionForTest();
                    Eq(true, mode.SetProfileOverride("first", PolicyCatalog.KeyCacheWarm, "0"));
                    Eq(false, old());
                    mode.CacheWarmOn = true;
                    Eq(false, mode.CacheWarmAdmissionForTest()()); // Per-game Off wins over global On.
                    Eq(true, mode.ClearProfileOverride("first", PolicyCatalog.KeyCacheWarm));
                    Eq(true, mode.CacheWarmAdmissionForTest()());
                    Eq(true, mode.SetProfileOverride("first", PolicyCatalog.KeyStandbyCleaner, "1"));
                    Eq(false, mode.CacheWarmAdmissionForTest()());
                    Eq(true, mode.ClearProfileOverride("first", PolicyCatalog.KeyStandbyCleaner));
                    foreach (string boundary in new[] { "active", "enabled", "stopping", "panicReq", "stickyGraceOnly", "standbyCleanerRestorePending", "profileSaveFailureSignaled", "gameGoneSinceTicks" })
                    {
                        object before = FamilyPolicyGetField(mode, boundary);
                        var admission = mode.CacheWarmAdmissionForTest(); Eq(true, admission());
                        FamilyPolicySetField(mode, boundary, boundary == "gameGoneSinceTicks" ? (object)1L
                            : boundary == "standbyCleanerRestorePending" || boundary == "profileSaveFailureSignaled" ? (object)1 : (object)(boundary != "active" && boundary != "enabled"));
                        Eq(false, admission()); FamilyPolicySetField(mode, boundary, before);
                    }
                    var stale = mode.CacheWarmAdmissionForTest();
                    FamilyPolicySetField(mode, "activeDetection", FamilyObservationTarget(f.Current("second")));
                    Eq(false, stale());
                    Settings.SuspendWritesForReset(); mode.CacheWarmOn = false; mode.CacheWarmOn = true;
                    Eq(false, mode.CacheWarmOn);
                    Settings.UseTransientStoreForCurrentProcess();
                    foreach (int language in new[] { 0, 1 })
                    {
                        Lang.Cur = language;
                        var form = (PanelForm)FormatterServices.GetUninitializedObject(typeof(PanelForm));
                        GC.SuppressFinalize(form);
                        UiConfigSetField(form, "gameMode", mode);
                        UiConfigSetField(form, "policySync", new List<Action>());
                        using (var panel = new Panel())
                        {
                            object[] args = { panel, 2 };
                            var card = (SettingCard)UiConfigCall(form, "BuildCacheWarmPolicyCard", args);
                            Eq(Lang.T("gm.cachewarm"), card.Title);
                            var toggle = card.Controls.OfType<Toggle>().Single();
                            toggle.Checked = true; Eq(true, mode.CacheWarmOn);
                            toggle.Checked = false; Eq(false, mode.CacheWarmOn);
                            using (var image = new Bitmap(card.Width, card.Height))
                            {
                                card.DrawToBitmap(image, card.ClientRectangle);
                                Eq(true, card.DescLinesShown >= card.DescLinesWanted);
                                string shots = Environment.GetEnvironmentVariable("PAVISE_CACHEWARM_SHOTS");
                                if (!string.IsNullOrEmpty(shots)) { Directory.CreateDirectory(shots); image.Save(Path.Combine(shots, "cache-warm-" + language + ".png")); }
                            }
                            Eq<Form>(null, card.FindForm());
                        }
                    }
                }
                var runner = new CacheWarmRunner();
                int admissions = 0;
                // Cancellation during the 90-second delay must drain without any
                // disk/power probe or native background-priority call.
                try
                {
                    runner.Update("one", executable, assets, delegate { Interlocked.Increment(ref admissions); return true; });
                    runner.Cancel(true); Eq(true, runner.Drain(2000)); Eq(0, admissions);
                    runner.Update("two", executable, assets, delegate { Interlocked.Increment(ref admissions); return true; });
                    runner.Cancel(false); Eq(true, runner.Drain(2000)); Eq(0, admissions);
                }
                finally { runner.Cancel(true); Eq(true, runner.Drain(2000)); }
            }
            finally
            {
                string resolved = Path.GetFullPath(folder), temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\";
                if (!resolved.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(resolved).StartsWith("Pavise-CacheWarm-Test-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Unexpected test cleanup path");
                Directory.Delete(resolved, true);
            }
        }

        private static void CacheWarmSurvivesFirstVerifiedBoost(string folder)
        {
            using (var f = new FamilyPolicyFixture(folder, "cachewarm-session"))
            {
                var mode = f.Mode;
                // 只走会话登记与提优后的记账；禁用 lane，不执行 Boost/原生写入/预热 worker。
                Eq(true, mode.SetProfileOverride("first", PolicyCatalog.KeyRenderLane, "0"));
                var profile = f.Current("first");
                mode.ProbeSessionPolicyApply(profile);
                Eq(false, PolicyResolver.For(profile).EffLane);
                var target = FamilyObservationTarget(profile);
                FamilyPolicySetField(mode, "activeDetection", target);
                FamilyPolicySetField(mode, "active", true);
                FamilyPolicySetField(mode, "enabled", true);
                mode.CacheWarmOn = true;
                Eq(false, IrqSessionProbe.EnabledSetting);
                Eq(false, mode.CacheWarmAdmissionForTest()()); // 没有真实开局不能入场。
                try
                {
                    FamilyPolicySetField(mode, "boostFirstStampTicks", DateTime.UtcNow.Ticks);
                    mode.ProbeSessionBegin("first");
                    long session = (long)FamilyPolicyGetField(mode, "cacheWarmSessionId");
                    var admitted = mode.CacheWarmAdmissionForTest();
                    Eq(true, admitted());

                    Type passType = typeof(GameMode).GetNestedType("BoostPass", System.Reflection.BindingFlags.NonPublic);
                    object pass = Activator.CreateInstance(passType, true);
                    passType.GetField("RendererName").SetValue(pass, target.RendererName);
                    passType.GetField("PriorityTarget").SetValue(pass, Native.HIGH_PRIORITY_CLASS);
                    FamilyPolicyInvoke(mode, "EngageLaneAndReport", IntPtr.Zero, null,
                        target.RendererPid, target.RendererCreation, pass, true, true, false, true, "");
                    Eq(0L, (long)FamilyPolicyGetField(mode, "boostFirstStampTicks"));
                    Eq(session, (long)FamilyPolicyGetField(mode, "cacheWarmSessionId"));
                    Eq(true, admitted());
                    Eq(true, mode.CacheWarmAdmissionForTest()());

                    // 同一游戏把 renderer 交给新进程仍属于本局，不重启/取消预热。
                    var replacement = FamilyObservationTarget(profile);
                    replacement.RendererPid++; replacement.RendererCreation++;
                    FamilyPolicySetField(mode, "activeDetection", replacement);
                    Eq(true, admitted());
                    Eq(session, (long)FamilyPolicyGetField(mode, "cacheWarmSessionId"));

                    // 同一游戏、同一 renderer 的下一局也必须使旧回调失效。
                    mode.ProbeSessionFinish(); mode.ProbeSessionBegin("first");
                    Eq(false, admitted());
                    Eq(true, (long)FamilyPolicyGetField(mode, "cacheWarmSessionId") > session);
                    var next = mode.CacheWarmAdmissionForTest();
                    Eq(true, next());

                    // 直接换游戏不经过 Deactivate，ReportBegin 仍建立新会话。
                    mode.ProbeSessionFinish();
                    profile = f.Current("second");
                    FamilyPolicySetField(mode, "activeDetection", FamilyObservationTarget(profile));
                    mode.ProbeSessionPolicyApply(profile);
                    mode.ProbeSessionBegin("second");
                    Eq(false, next());
                    var current = mode.CacheWarmAdmissionForTest();
                    Eq(true, current());

                    FamilyPolicyInvoke(mode, "InvalidateCacheWarm");
                    Eq(false, current());
                    var resumed = mode.CacheWarmAdmissionForTest();
                    Eq(true, resumed());
                    FamilyPolicySetField(mode, "active", false);
                    FamilyPolicyInvoke(mode, "InvalidateCacheWarm");
                    Eq(false, resumed());
                    Eq(false, mode.CacheWarmAdmissionForTest()());
                }
                finally
                {
                    FamilyPolicyInvoke(mode, "InvalidateCacheWarm");
                    mode.ProbeSessionFinish();
                }
            }
        }

        private static Func<bool> CacheWarmCapture(GameMode mode, out string key)
        {
            object[] paths = { null, null, null };
            var admitted = (Func<bool>)FamilyPolicyInvoke(mode, "CaptureCacheWarmAdmission", paths);
            key = (string)paths[0];
            return admitted;
        }

        private static void CacheWarmPathHandoffRetargets(string folder)
        {
            using (var f = new FamilyPolicyFixture(folder, "cachewarm-paths"))
            {
                var mode = f.Mode;
                var profile = f.Current("first");
                mode.ProbeSessionPolicyApply(profile);
                FamilyPolicySetField(mode, "activeDetection", FamilyObservationTarget(profile));
                FamilyPolicySetField(mode, "active", true); FamilyPolicySetField(mode, "enabled", true);
                mode.CacheWarmOn = true;
                FamilyPolicyInvoke(mode, "BeginCacheWarmSession");
                try
                {
                    string originalKey, nextKey;
                    var original = CacheWarmCapture(mode, out originalKey); Eq(true, original());
                    // 学习提交先更新档案，activeDetection 下一步才发布；这段间隙也要撤销旧路径。
                    var live = (GameProfile)FamilyPolicyInvoke(mode, "FindProfileLocked", "first");
                    live.Root = Path.Combine(f.DirectoryPath, "new-install");
                    live.ExecutablePath = Path.Combine(live.Root, "renderer.exe");
                    Eq(false, original());
                    Eq(false, CacheWarmCapture(mode, out nextKey)());
                    var replacement = FamilyObservationTarget(live.Clone());
                    FamilyPolicySetField(mode, "activeDetection", replacement);
                    var next = CacheWarmCapture(mode, out nextKey); Eq(true, next());
                    Eq(false, string.Equals(originalKey, nextKey, StringComparison.OrdinalIgnoreCase));

                    // 同根目录换 renderer 路径也需要新任务；仅 PID/创建时间变化则继续本局。
                    replacement.RendererPath = Path.Combine(live.Root, "alternate.exe");
                    Eq(false, next());
                    string rendererKey;
                    var renderer = CacheWarmCapture(mode, out rendererKey); Eq(true, renderer());
                    Eq(false, string.Equals(nextKey, rendererKey, StringComparison.OrdinalIgnoreCase));
                    replacement.RendererPid++; replacement.RendererCreation++;
                    Eq(true, renderer());

                    // Windows 路径大小写变化既不撤销准入，也不产生新的任务身份。
                    live.Root = live.Root.ToUpperInvariant();
                    live.ExecutablePath = live.ExecutablePath.ToUpperInvariant();
                    replacement.Profile = live.Clone();
                    replacement.RendererPath = replacement.RendererPath.ToUpperInvariant();
                    Eq(true, renderer());
                    string caseKey;
                    Eq(true, CacheWarmCapture(mode, out caseKey)());
                    Eq(true, string.Equals(rendererKey, caseKey, StringComparison.OrdinalIgnoreCase));
                }
                finally { FamilyPolicyInvoke(mode, "InvalidateCacheWarm"); }
            }
        }

        private static T CacheWarmRunnerField<T>(CacheWarmRunner runner, string field)
        {
            return (T)typeof(CacheWarmRunner).GetField(field,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(runner);
        }

        private static void CacheWarmRunnerCancelsSupersededPaths(string executable, string root)
        {
            var runner = new CacheWarmRunner();
            int admissions = 0;
            Func<bool> allowed = delegate { Interlocked.Increment(ref admissions); return true; };
            try
            {
                // 持有状态锁让被取消的 worker 暂停在 SetStatus，确定覆盖旧 worker 仍活着的 G→H→G。
                lock (CacheWarmRunnerField<object>(runner, "gate"))
                {
                    runner.Update("G", executable, root, allowed);
                    var token = CacheWarmRunnerField<ManualResetEvent>(runner, "cancel");
                    runner.Update("g", executable.ToUpperInvariant(), root.ToUpperInvariant(), allowed);
                    Eq(false, token.WaitOne(0));
                    runner.Update("H", executable, root, allowed);
                    Eq(true, token.WaitOne(0));
                    runner.Update("G", executable, root, allowed);
                    Eq<string>(null, CacheWarmRunnerField<string>(runner, "attempted"));
                }
                Eq(true, runner.Drain(2000));
                runner.Update("G", executable, root, allowed);
                Eq("cachewarm.waiting", runner.Status);
                Eq("G", CacheWarmRunnerField<string>(runner, "attempted"));
                Eq(false, CacheWarmRunnerField<ManualResetEvent>(runner, "cancel").WaitOne(0));
                runner.Update("H", executable, root, allowed);
                Eq(true, runner.Drain(2000));
                runner.Update("H", executable, root, allowed);
                Eq("cachewarm.waiting", runner.Status);
                Eq("H", CacheWarmRunnerField<string>(runner, "attempted"));
                // 即使没有观察到中间路径，新准入暂时失效所触发的 Cancel(false) 也不能挡住恢复。
                runner.Cancel(false); Eq(true, runner.Drain(2000));
                runner.Update("H", executable, root, allowed);
                Eq("cachewarm.waiting", runner.Status);
                Eq("H", CacheWarmRunnerField<string>(runner, "attempted"));
                Eq(0, admissions); // 全部取消发生在 90 秒等待内，不调用任何后台原生功能。
            }
            finally { runner.Cancel(true); Eq(true, runner.Drain(2000)); }
        }
    }
}
#endif
