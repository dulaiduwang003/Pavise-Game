using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;

namespace PaviseApp
{
    // Production methods run unchanged. Settings is the existing in-memory substitute;
    // GameMode is never constructed or started, and no process/thread handle is opened.
    internal static class HighPriorityChecks
    {
        private const int Pid = 515151;
        private const long Birth = 133100000000000500;
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static int checks;
        private static void Check(bool condition, string detail)
        { checks++; if (!condition) throw new InvalidOperationException(detail); }
        private static void Set(object target, string name, object value)
        { target.GetType().GetField(name, Hidden).SetValue(target, value); }
        private static object Get(object target, string name)
        { return target.GetType().GetField(name, Hidden).GetValue(target); }
        private static object Call(object target, string name, params object[] args)
        {
            try { return target.GetType().GetMethod(name, Hidden).Invoke(target, args); }
            catch (TargetInvocationException error) { throw error.InnerException; }
        }
        private static GameMode Mode()
        {
            var mode = (GameMode)FormatterServices.GetUninitializedObject(typeof(GameMode));
            Set(mode, "sync", new object());
            foreach (string name in new[] { "gameBoostNextAudit", "boostStateVerified", "boostResourceSelections",
                "boostActionRestorePending", "automationEvidence", "gameBoost" })
            {
                FieldInfo field = typeof(GameMode).GetField(name, Hidden);
                field.SetValue(mode, Activator.CreateInstance(field.FieldType));
            }
            Set(mode, "boostIdentityPid", Pid);
            Set(mode, "boostIdentityCreation", Birth);
            // No previous candidate-thread receipt exists in this fixture. Mark its clean
            // restoration as cached so this priority-policy test cannot open native handles.
            Set(mode, "boostLaneReleased", true);
            return mode;
        }
        private static object Pass()
        {
            Type type = typeof(GameMode).GetNestedType("BoostPass", BindingFlags.NonPublic);
            object pass = Activator.CreateInstance(type, true);
            Set(pass, "RendererPid", Pid); Set(pass, "RendererCreation", Birth);
            Set(pass, "RendererName", "owned-policy-fixture");
            Set(pass, "PriorityTarget", Native.NORMAL_PRIORITY_CLASS);
            return pass;
        }
        private static void AdmissionMatrix()
        {
            foreach (PerformancePreset preset in new[] { PerformancePreset.Standard, PerformancePreset.Competitive })
            foreach (bool boost in new[] { false, true })
            foreach (bool thread in new[] { false, true })
            foreach (bool fallback in new[] { false, true })
            foreach (bool denied in new[] { false, true })
            {
                Settings.UseTransientStoreForCurrentProcess();
                Settings.SaveStr("PerformancePreset", ((int)preset).ToString());
                Settings.Save(PolicyCatalog.KeyBoost, boost);
                Settings.SaveStr(ThreadBoostSelection.Key, thread ? ThreadBoostSelection.On : ThreadBoostSelection.Off);
                GameMode mode = Mode();
                PolicySnapshot policy = PolicyResolver.Global();
                Set(mode, "sessionPolicy", policy);
                if (fallback)
                {
                    Type type = typeof(GameMode).GetNestedType("LaneFallbackTarget", BindingFlags.NonPublic);
                    Set(mode, "laneFallback", Activator.CreateInstance(type, Hidden, null, new object[] { Pid, Birth }, null));
                }
                object pass = Pass(); Set(pass, "WriteDenied", denied);
                Call(mode, "PrepareResourcePriorities", pass, policy);
                Check((bool)Get(pass, "CpuBoost") == boost, "the fixed High target bypassed the game-priority master switch");
                Check((bool)Call(mode, "RefreshBoostPriority", pass), "a current renderer identity was rejected");
                Check((uint)Get(pass, "PriorityTarget") == Native.HIGH_PRIORITY_CLASS,
                    "the process target depended on mode or thread-boost admission");
                Check((bool)Get(pass, "LaneAllowed") == (thread && fallback && !denied),
                    "the fixed High target changed candidate-thread admission");
                if (!boost)
                {
                    bool wrote = false;
                    Check((bool)Call(mode, "ApplySelectedBoostAction", IntPtr.Zero, Pid, pass, "GamePriority", false,
                        new Func<long, bool>(delegate { throw new InvalidOperationException("disabled CPU priority was queried"); }),
                        new Func<int>(delegate { wrote = true; return 0; })) && !wrote,
                        "disabled CPU priority wrote an untouched process");
                }
                if (denied)
                {
                    object[] stage = { IntPtr.Zero, Pid, pass, false, false, false };
                    Check((bool)Call(mode, "ApplyBoostStateStage", stage) && !(bool)stage[4],
                        "a write-protected game was reported as successfully boosted");
                }
                Set(pass, "PriorityTarget", Native.NORMAL_PRIORITY_CLASS);
                Set(pass, "RendererCreation", Birth + 1);
                Check(!(bool)Call(mode, "RefreshBoostPriority", pass)
                    && (uint)Get(pass, "PriorityTarget") == Native.NORMAL_PRIORITY_CLASS,
                    "a reused PID received a priority decision");
                Set(pass, "RendererCreation", Birth);
                Set(pass, "IdentityGeneration", 1L);
                Check(!(bool)Call(mode, "RefreshBoostPriority", pass), "a stale match generation was accepted");
            }
        }
        private static void OriginalPriority()
        {
            foreach (uint original in new[] { Native.NORMAL_PRIORITY_CLASS, 32768u, Native.HIGH_PRIORITY_CLASS })
            {
                GameMode mode = Mode();
                Type type = typeof(GameMode).GetNestedType("Snap", BindingFlags.NonPublic);
                object snapshot = Activator.CreateInstance(type, true);
                Set(snapshot, "Pri", original);
                ((IDictionary)Get(mode, "gameBoost"))[Pid] = snapshot;
                object[] args = { Pid, "GamePriority", 0L };
                Check((bool)Call(mode, "TryOriginalBoostAction", args) && (long)args[2] == original,
                    "the fixed High target replaced an original recovery priority");
            }
        }
        private static int Main()
        {
            try
            {
                Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false;
                AdmissionMatrix(); OriginalPriority();
                Console.WriteLine("PASS fixed High priority checks=" + checks
                    + "; memory settings, production policy methods; no game/Pavise startup or native priority writes");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
