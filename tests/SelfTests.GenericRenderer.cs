// @author bdth 2074055628@qq.com
// 文件用途 名称无关的候选与静态入口回归 只用快照/内存PE/自有临时目录 不运行程序或更改系统
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static int RunGenericRendererRegressionTests()
        {
            Action[] tests =
            {
                TestGenericRendererNames, TestGenericRendererGpuCompetition,
                TestGenericRendererUnknownHighGpu, TestGenericRendererOwnership,
                TestGenericRendererWindowIsNotGpu, TestGenericRendererForce,
                TestGenericRendererSafety, TestGenericRendererArmed,
                TestGenericExecutableNormalImports, TestGenericExecutableDelayedImports,
                TestGenericExecutableNoGraphics, TestGenericExecutableInvalidHeaders,
                TestGenericExecutableInvalidImports, TestGenericExecutableOverlappingSections,
                TestGenericExecutableUnique, TestGenericExecutableAmbiguous,
                TestGenericExecutableIncomplete, TestGenericRendererRootInference,
                TestGenericExecutableDeepDataTree, TestGenericExecutableDepthBoundary,
                TestGenericExecutableLazyBudgets, TestGenericExecutableBreadthFirst,
                TestGenericExecutableSearchFaults
            };
            foreach (Action test in tests) test();
            return tests.Length;
        }

        private static GameProfile GenericProfile()
        {
            return new GameProfile
            {
                Id = "generic-evidence", Name = "Unlisted title",
                Root = @"C:\PaviseGenericTests\Title",
                ExecutablePath = @"C:\PaviseGenericTests\Title\Entry.exe"
            };
        }

        private static GameProcessSnapshot GenericProcess(int pid, string path, bool foreground)
        {
            return new GameProcessSnapshot
            {
                Pid = pid, Creation = 1000 + pid, ParentPid = 1,
                Name = Path.GetFileNameWithoutExtension(path), Path = path,
                Foreground = foreground, Visible = true
            };
        }

        private static GameDetection GenericCandidate(GameProfile profile,
            params GameProcessSnapshot[] snapshot)
        {
            GameDetection candidate = GameSessionDetector.FindForegroundCandidateSnapshot(snapshot, profile, null);
            if (candidate == null) throw new Exception("generic candidate missing");
            return candidate;
        }

        private static void TestGenericRendererNames()
        {
            GameProfile profile = GenericProfile();
            string[] names =
            {
                "UnknownFutureClient", "LeagueClient", "LeagueClientUxRender", "RiotClientServices",
                "steam", "EpicGamesLauncher", "launcher", "UnrealCEFSubProcess",
                "CrashReportClient", "telemetry", "helper", "chrome", "notepad"
            };
            foreach (string name in names)
            {
                var process = GenericProcess(100, Path.Combine(profile.Root, name + ".exe"), true);
                Eq(false, GameSessionDetector.ElectionVetoed(process.Name, process.Path));
                GameDetection candidate = GenericCandidate(profile, process);
                Eq(100, candidate.RendererPid);
                Eq(true, candidate.RequiresGpuConfirm);
                Eq(false, candidate.RendererCandidateSelected);
                process.FullscreenLike = true;
                candidate = GenericCandidate(profile, process);
                Eq(true, candidate.RendererCandidateSelected);
                Eq(0L, candidate.RendererGpuProofExpiresMs);
            }
        }

        private static void TestGenericRendererGpuCompetition()
        {
            GameProfile profile = GenericProfile();
            var shell = GenericProcess(100, profile.ExecutablePath, false);
            var render = GenericProcess(101, Path.Combine(profile.Root, "Anything.exe"), true);
            GameDetection candidate = GenericCandidate(profile, shell, render);
            var counters = new Dictionary<int, double> { { 100, 70 }, { 101, 25 } };
            double value;
            Eq(false, RendererHandoffTracker.HasGpuEvidence(candidate, counters, out value));
            // 不因另一个程序GPU更高而把它偷换成前台候选。
            Eq(101, candidate.RendererPid);
            counters[100] = 5;
            Eq(true, RendererHandoffTracker.HasGpuEvidence(candidate, counters, out value));
            Eq(25.0, value);
            counters[101] = double.NaN;
            Eq(false, RendererHandoffTracker.HasGpuEvidence(candidate, counters, out value));
        }

        private static void TestGenericRendererUnknownHighGpu()
        {
            GameProfile profile = GenericProfile();
            var shell = GenericProcess(100, Path.Combine(profile.Root, "NeverSeenClient.exe"), true);
            var render = GenericProcess(101, Path.Combine(profile.Root, "FrameOwner.exe"), false);
            GameDetection candidate = GenericCandidate(profile, shell, render);
            var tracker = new RendererHandoffTracker();
            tracker.Offer(candidate, 0);
            Eq<RendererProbeTicket>(null, tracker.BeginProbe(0));
            Eq(true, tracker.Protects(100, shell.Creation, shell.Path, 0));
            tracker.Recovery(candidate, true);
            RendererProbeTicket ticket = tracker.BeginProbe(0);
            if (ticket == null) throw new Exception("released generic candidate was not sampled");
            tracker.Complete(ticket, new Dictionary<int, double> { { 100, 35 }, { 101, 60 } }, true, 100);
            Eq<GameDetection>(null, tracker.Confirmed(100));
            // 切到真正画面窗口后，新身份单独取证，旧客户端无需命名名单。
            shell.Foreground = false; render.Foreground = true;
            candidate = GenericCandidate(profile, shell, render);
            tracker.Offer(candidate, 200);
            tracker.Recovery(candidate, true);
            ticket = tracker.BeginProbe(RendererHandoffTracker.ProbeCooldownMs);
            if (ticket == null) throw new Exception("new foreground candidate lost cooldown handoff");
            tracker.Complete(ticket, new Dictionary<int, double> { { 100, 3 }, { 101, 60 } }, true,
                RendererHandoffTracker.ProbeCooldownMs + 100);
            Eq(101, tracker.Confirmed(RendererHandoffTracker.ProbeCooldownMs + 100).RendererPid);
        }

        private static void TestGenericRendererOwnership()
        {
            GameProfile profile = GenericProfile();
            var parent = GenericProcess(100, profile.ExecutablePath, false);
            var external = GenericProcess(101, @"D:\UnrelatedFolder\InvisibleName.exe", true);
            external.ParentPid = 100;
            GenericCandidate(profile, parent, external);
            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(
                new[] { external }, profile, null));
            var nested = GenericProcess(102, Path.Combine(profile.Root, @"Arbitrary\Folders\client.exe"), true);
            GenericCandidate(profile, nested);
            var second = profile.Clone(); second.Id = "ambiguous-owner";
            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(
                new[] { nested }, new[] { second, profile }, null));
            var contradictory = GenericProcess(103, Path.Combine(profile.Root, "Other.exe"), true);
            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(
                new[] { nested, contradictory }, profile, null));
        }

        private static void TestGenericRendererWindowIsNotGpu()
        {
            GameProfile profile = GenericProfile();
            var selected = GenericProcess(100, profile.ExecutablePath, true);
            GameDetection exact = GenericCandidate(profile, selected);
            Eq(true, exact.RendererCandidateSelected);
            Eq(0L, exact.RendererGpuProofExpiresMs);
            selected.Path = Path.Combine(profile.Root, "UnknownFullscreen.exe");
            selected.Name = "UnknownFullscreen"; selected.FullscreenLike = true;
            GameDetection fullscreen = GenericCandidate(profile, selected);
            Eq(true, fullscreen.RendererCandidateSelected);
            Eq(0L, fullscreen.RendererGpuProofExpiresMs);
            double value;
            Eq(false, RendererHandoffTracker.HasGpuEvidence(fullscreen,
                new Dictionary<int, double>(), out value));
        }

        private static void TestGenericRendererForce()
        {
            GameProfile profile = GenericProfile(); profile.ForceTrigger = true;
            var process = GenericProcess(100, Path.Combine(profile.Root, "UnlistedClient.exe"), true);
            process.FullscreenLike = true;
            GameDetection safety = GenericCandidate(profile, process);
            Eq(true, safety.RendererSafetyOnly);
            Eq(false, safety.RendererCandidateSelected);
            Eq(false, safety.RendererLearnable);
            profile.ExecutablePath = process.Path;
            GameDetection exact = GenericCandidate(profile, process);
            Eq(true, exact.RendererCandidateSelected);
            Eq(false, exact.RendererLearnable);
            Eq(0L, exact.RendererGpuProofExpiresMs);
        }

        private static void TestGenericRendererSafety()
        {
            GameProfile profile = GenericProfile();
            var safety = GenericProcess(100, Path.Combine(profile.Root, "EasyAntiCheat.exe"), true);
            safety.FullscreenLike = true;
            foreach (bool force in new[] { false, true })
            {
                profile.ForceTrigger = force; profile.ExecutablePath = safety.Path;
                Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(new[] { safety }, profile, null));
                Eq<GameDetection>(null, GameSessionDetector.DetectSnapshot(new[] { safety }, new[] { profile }));
            }
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrEmpty(windows)) throw new Exception("Windows directory unavailable for safety fixture");
            foreach (string name in new[] { "explorer", "dwm", "ShellExperienceHost" })
            {
                string path = Path.Combine(windows, name + ".exe");
                Eq(true, GameSessionDetector.ElectionVetoed(name, path));
                Eq(false, GameSessionDetector.ElectionVetoed(name, @"D:\Games\Unknown\" + name + ".exe"));
                Eq(false, GameSessionDetector.ElectionVetoed(name, windows + "Backup\\" + name + ".exe"));
                var system = GenericProcess(101, path, true);
                profile.ExecutablePath = path;
                foreach (bool force in new[] { false, true })
                {
                    profile.ForceTrigger = force;
                    Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(new[] { system }, profile, null));
                    Eq<GameDetection>(null, GameSessionDetector.DetectSnapshot(new[] { system }, new[] { profile }));
                }
            }
        }

        private static void TestGenericRendererArmed()
        {
            GameProfile profile = GenericProfile();
            var process = GenericProcess(100, profile.ExecutablePath, false);
            process.Visible = false;
            string armed;
            Eq<GameDetection>(null, GameSessionDetector.DetectSnapshot(new[] { process }, new[] { profile }, out armed));
            Eq(profile.Name, armed);
            Eq(process.Name, GameSessionDetector.ArmedVia(profile, new[] { process }));
            process.Name = "FalseName";
            Eq<string>(null, GameSessionDetector.ArmedVia(profile, new[] { process }));
        }

        private static byte[] GenericPe(bool x64, string dependency, bool delayed)
        {
            byte[] bytes = new byte[0x600];
            Put16(bytes, 0, 0x5A4D); Put32(bytes, 60, 0x80);
            Put32(bytes, 0x80, 0x00004550); Put16(bytes, 0x84, x64 ? 0x8664 : 0x014C);
            Put16(bytes, 0x86, 1); Put16(bytes, 0x94, x64 ? 240 : 224); Put16(bytes, 0x96, 0x0002);
            int optional = 0x98, directories = optional + (x64 ? 112 : 96);
            Put16(bytes, optional, x64 ? 0x020B : 0x010B);
            if (x64) Put64(bytes, optional + 24, 0x140000000);
            else Put32(bytes, optional + 28, 0x00400000);
            Put32(bytes, optional + 60, 0x200); Put16(bytes, optional + 68, 2);
            Put32(bytes, directories - 4, 16);
            int section = optional + (x64 ? 240 : 224);
            Put32(bytes, section + 8, 0x400); Put32(bytes, section + 12, 0x1000);
            Put32(bytes, section + 16, 0x400); Put32(bytes, section + 20, 0x200);
            if (dependency != null)
            {
                int entry = directories + (delayed ? 13 : 1) * 8;
                Put32(bytes, entry, 0x1000); Put32(bytes, entry + 4, delayed ? 64U : 40U);
                if (delayed) Put32(bytes, 0x200, 1);
                Put32(bytes, 0x200 + (delayed ? 4 : 12), 0x1080);
                byte[] ascii = Encoding.ASCII.GetBytes(dependency);
                Buffer.BlockCopy(ascii, 0, bytes, 0x280, ascii.Length);
            }
            return bytes;
        }

        private static void Put16(byte[] bytes, int at, int value)
        { Buffer.BlockCopy(BitConverter.GetBytes((ushort)value), 0, bytes, at, 2); }
        private static void Put32(byte[] bytes, int at, uint value)
        { Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, at, 4); }
        private static void Put64(byte[] bytes, int at, ulong value)
        { Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, at, 8); }
        private static ExecutableCandidateFacts GenericFacts(byte[] bytes)
        { using (var stream = new MemoryStream(bytes, false)) return ExecutableCandidateProbe.ReadFacts(stream); }

        private static void TestGenericExecutableNormalImports()
        {
            foreach (bool x64 in new[] { false, true })
                foreach (string api in new[] { "d3d9.dll", "D3D11.dll", "d3d12.dll", "opengl32.dll", "vulkan-1.dll" })
                {
                    ExecutableCandidateFacts facts = GenericFacts(GenericPe(x64, api, false));
                    Eq(true, facts.Executable); Eq(true, facts.Gui); Eq(true, facts.GraphicsImports);
                }
        }

        private static void TestGenericExecutableDelayedImports()
        {
            foreach (bool x64 in new[] { false, true })
            {
                ExecutableCandidateFacts facts = GenericFacts(GenericPe(x64, "d3d12.dll", true));
                Eq(true, facts.Executable); Eq(true, facts.GraphicsImports);
            }
            byte[] legacy = GenericPe(false, "d3d9.dll", true);
            Put32(legacy, 0x200, 0); Put32(legacy, 0x204, 0x401080);
            Eq(true, GenericFacts(legacy).GraphicsImports);
        }

        private static void TestGenericExecutableNoGraphics()
        {
            foreach (string dependency in new[] { null, "kernel32.dll", "d3d11.dll.bak", "cef.dll", "steam_api64.dll" })
            {
                ExecutableCandidateFacts facts = GenericFacts(GenericPe(true, dependency, false));
                Eq(true, facts.Executable); Eq(true, facts.Gui); Eq(false, facts.GraphicsImports);
            }
            byte[] decoy = GenericPe(true, "kernel32.dll", false);
            Buffer.BlockCopy(Encoding.ASCII.GetBytes("d3d12.dll"), 0, decoy, 0x400, 9);
            Eq(false, GenericFacts(decoy).GraphicsImports);
        }

        private static void TestGenericExecutableInvalidHeaders()
        {
            Action<byte[]>[] mutations =
            {
                delegate(byte[] b) { Put16(b, 0, 0); },
                delegate(byte[] b) { Put32(b, 60, uint.MaxValue); },
                delegate(byte[] b) { Put32(b, 0x80, 0); },
                delegate(byte[] b) { Put16(b, 0x86, ExecutableCandidateProbe.MaxSections + 1); },
                delegate(byte[] b) { Put16(b, 0x94, 5000); },
                delegate(byte[] b) { Put16(b, 0x96, 0x2002); },
                delegate(byte[] b) { Put32(b, 0x98 + 108, 100); }
            };
            foreach (Action<byte[]> mutation in mutations)
            {
                byte[] bytes = GenericPe(true, "d3d11.dll", false); mutation(bytes);
                Eq(false, GenericFacts(bytes).Executable);
            }
            Eq(false, GenericFacts(new byte[63]).Executable);
            Eq(false, ExecutableCandidateProbe.ReadFacts(null).Executable);
        }

        private static void TestGenericExecutableInvalidImports()
        {
            Action<byte[]>[] mutations =
            {
                delegate(byte[] b) { Put32(b, 0x98 + 112 + 8, uint.MaxValue); },
                delegate(byte[] b) { Put32(b, 0x98 + 112 + 12, 20); },
                delegate(byte[] b) { Put32(b, 0x98 + 112 + 12, 10000); },
                delegate(byte[] b) { Put32(b, 0x20C, 0x1700); },
                delegate(byte[] b) { for (int i = 0; i < 260; i++) b[0x280 + i] = (byte)'x'; }
            };
            foreach (Action<byte[]> mutation in mutations)
            {
                byte[] bytes = GenericPe(true, "d3d11.dll", false); mutation(bytes);
                Eq(false, GenericFacts(bytes).Executable);
            }
        }

        private static void TestGenericExecutableOverlappingSections()
        {
            byte[] bytes = GenericPe(true, "d3d11.dll", false);
            Put16(bytes, 0x86, 2);
            Buffer.BlockCopy(bytes, 0x98 + 240, bytes, 0x98 + 240 + 40, 40);
            Eq(false, GenericFacts(bytes).Executable);
        }

        private static ExecutableCandidateFacts GenericStatic(string path, bool graphics, bool paired)
        {
            return new ExecutableCandidateFacts
            { Path = path, Executable = true, Gui = true, GraphicsImports = graphics, EngineDataPair = paired };
        }

        private static void TestGenericExecutableUnique()
        {
            var client = GenericStatic(@"C:\Scan\LargeUnknown.exe", false, false);
            var renderer = GenericStatic(@"C:\Scan\launcher.exe", true, false);
            Eq(renderer.Path, ExecutableCandidateProbe.PickUnique(new[] { client, renderer }, true));
            Eq(renderer.Path, ExecutableCandidateProbe.PickUnique(new[] { renderer, client }, true));
            renderer.GraphicsImports = false; renderer.EngineDataPair = true;
            Eq(renderer.Path, ExecutableCandidateProbe.PickUnique(new[] { client, renderer }, true));
            Eq(client.Path, ExecutableCandidateProbe.PickUnique(new[] { client }, true));
        }

        private static void TestGenericExecutableAmbiguous()
        {
            var a = GenericStatic(@"C:\Scan\Game.exe", true, false);
            var b = GenericStatic(@"C:\Scan\client.exe", true, false);
            Eq<string>(null, ExecutableCandidateProbe.PickUnique(new[] { a, b }, true));
            Eq<string>(null, ExecutableCandidateProbe.PickUnique(new[] { b, a }, true));
            a.GraphicsImports = false; b.GraphicsImports = false;
            Eq<string>(null, ExecutableCandidateProbe.PickUnique(new[] { a, b }, true));
            b.Path = a.Path.ToUpperInvariant();
            Eq(a.Path, ExecutableCandidateProbe.PickUnique(new[] { a, b }, true));
            b.GraphicsImports = true;
            Eq<string>(null, ExecutableCandidateProbe.PickUnique(new[] { a, b }, true));
            Eq<string>(null, ExecutableCandidateProbe.PickUnique(new[] { b, a }, true));
            b.Path = @"C:\Scan\Other.exe"; b.GraphicsImports = false; b.Gui = false;
            Eq<string>(null, ExecutableCandidateProbe.PickUnique(new[] { a, b }, true));
        }

        private static void TestGenericExecutableIncomplete()
        {
            var candidate = GenericStatic(@"C:\Scan\Game.exe", true, false);
            Eq(candidate.Path, ExecutableCandidateProbe.PickUnique(new[] { candidate }, false));
            candidate.GraphicsImports = false;
            Eq<string>(null, ExecutableCandidateProbe.PickUnique(new[] { candidate }, false));
            candidate.EngineDataPair = true;
            Eq(candidate.Path, ExecutableCandidateProbe.PickUnique(new[] { candidate }, false));
            Eq<string>(null, ExecutableCandidateProbe.PickUnique(null, true));
            Eq<string>(null, ExecutableCandidateProbe.PickUnique(new ExecutableCandidateFacts[0], true));
            candidate.Executable = false;
            Eq<string>(null, ExecutableCandidateProbe.PickUnique(new[] { candidate }, true));
        }

        private static void TestGenericRendererRootInference()
        {
            Eq(@"C:\Games\Unlisted", GameScan.InferGameRoot(@"C:\Games\Unlisted\Binaries\Win64\Anything.exe"));
            Eq(@"C:\Games\Unlisted\FutureClient", GameScan.InferGameRoot(@"C:\Games\Unlisted\FutureClient\Anything.exe"));
            Eq(@"C:\Games\Unlisted\LeagueClient", GameScan.InferGameRoot(@"C:\Games\Unlisted\LeagueClient\Anything.exe"));
        }

        private sealed class GenericExecutableFixture : IDisposable
        {
            internal readonly string Root;

            internal GenericExecutableFixture()
            {
                Root = Path.Combine(Path.GetTempPath(), "PaviseExecutableScope-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root);
            }

            internal string WriteExecutable(string relativePath, string dependency)
            {
                string path = Path.GetFullPath(Path.Combine(Root, relativePath));
                if (!path.StartsWith(Root + "\\", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("fixture target escaped its owned directory");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, GenericPe(true, dependency, false));
                return path;
            }

            public void Dispose()
            {
                string root = Path.GetFullPath(Root);
                string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\";
                if (!root.StartsWith(temporary + "PaviseExecutableScope-", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(Path.GetDirectoryName(root).TrimEnd('\\'), temporary.TrimEnd('\\'),
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("refusing to remove a non-fixture directory");
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private static void TestGenericExecutableDeepDataTree()
        {
            using (var fixture = new GenericExecutableFixture())
            {
                string executable = fixture.WriteExecutable("Anything.exe", "d3d12.dll");
                Directory.CreateDirectory(Path.Combine(fixture.Root, @"Data\One\Two\Three\Four"));
                Eq(executable, ExecutableCandidateProbe.PickMainExecutable(fixture.Root));
                fixture.WriteExecutable("client.exe", "d3d11.dll");
                Eq<string>(null, ExecutableCandidateProbe.PickMainExecutable(fixture.Root));
            }
            using (var fixture = new GenericExecutableFixture())
            {
                fixture.WriteExecutable("OnlyGui.exe", "kernel32.dll");
                Directory.CreateDirectory(Path.Combine(fixture.Root, @"Data\One\Two\Three\Four"));
                Eq<string>(null, ExecutableCandidateProbe.PickMainExecutable(fixture.Root));
            }
        }

        private static void TestGenericExecutableDepthBoundary()
        {
            string relative = "";
            for (int i = 1; i <= ExecutableCandidateProbe.MaxDirectoryDepth; i++)
                relative = Path.Combine(relative, "Level" + i);
            using (var fixture = new GenericExecutableFixture())
            {
                string executable = fixture.WriteExecutable(Path.Combine(relative, "Anything.exe"), "d3d11.dll");
                Eq(executable, ExecutableCandidateProbe.PickMainExecutable(fixture.Root));
                // Depth four is included; a child at depth five must not discard it.
                Directory.CreateDirectory(Path.Combine(fixture.Root, relative, "OutsideScope"));
                Eq(executable, ExecutableCandidateProbe.PickMainExecutable(fixture.Root));
            }
            using (var fixture = new GenericExecutableFixture())
            {
                fixture.WriteExecutable(Path.Combine(relative, @"OutsideScope\Anything.exe"), "d3d11.dll");
                Eq<string>(null, ExecutableCandidateProbe.PickMainExecutable(fixture.Root));
            }
        }

        private static IEnumerable<string> GenericLazyChildren(string root, int count, Action onYield, bool executable)
        {
            for (int i = 0; i < count; i++)
            {
                onYield();
                yield return Path.Combine(root, "Item" + i + (executable ? ".exe" : ""));
            }
        }

        private static void TestGenericExecutableLazyBudgets()
        {
            const string root = @"C:\VirtualScan";
            string executable = Path.Combine(root, "Anything.exe");
            int yielded = 0, visited = 0;
            List<string> paths;
            bool complete;
            bool valid = ExecutableCandidateProbe.CollectPaths(root,
                delegate(string directory)
                {
                    visited++;
                    return directory == root ? new[] { executable } : new string[0];
                },
                delegate(string directory)
                {
                    return directory == root
                        ? GenericLazyChildren(root, 100000, delegate { yielded++; }, false)
                        : new string[0];
                }, delegate { return FileAttributes.Normal; }, out paths, out complete);
            Eq(true, valid); Eq(false, complete);
            Eq(ExecutableCandidateProbe.MaxDirectories, visited);
            Eq(ExecutableCandidateProbe.MaxDirectories, yielded);
            Eq(1, paths.Count);
            Eq(executable, ExecutableCandidateProbe.PickUnique(new[] { GenericStatic(paths[0], true, false) }, complete));

            yielded = 0;
            valid = ExecutableCandidateProbe.CollectPaths(root,
                delegate { return GenericLazyChildren(root, 100000, delegate { yielded++; }, true); },
                delegate { return new string[0]; }, delegate { return FileAttributes.Normal; }, out paths, out complete);
            Eq(true, valid); Eq(false, complete);
            Eq(ExecutableCandidateProbe.MaxExecutables, paths.Count);
            Eq(ExecutableCandidateProbe.MaxExecutables + 1, yielded);
            var facts = new List<ExecutableCandidateFacts>();
            for (int i = 0; i < paths.Count; i++) facts.Add(GenericStatic(paths[i], i == 0, false));
            Eq(paths[0], ExecutableCandidateProbe.PickUnique(facts, complete));
            facts[facts.Count - 1].GraphicsImports = true;
            Eq<string>(null, ExecutableCandidateProbe.PickUnique(facts, complete));
        }

        private static void TestGenericExecutableBreadthFirst()
        {
            const string root = @"C:\VirtualScan";
            string deep = Path.Combine(root, "DeepData"), sibling = Path.Combine(root, "OtherFolder");
            var tree = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            tree[root] = new[] { deep, sibling };
            string current = deep;
            for (int i = 1; i <= ExecutableCandidateProbe.MaxDirectoryDepth + 1; i++)
            {
                string child = Path.Combine(current, "Level" + i);
                tree[current] = new[] { child };
                current = child;
            }
            var visits = new List<string>();
            List<string> paths;
            bool complete;
            Eq(true, ExecutableCandidateProbe.CollectPaths(root,
                delegate(string directory)
                {
                    visits.Add(directory);
                    return directory == sibling ? new[] { Path.Combine(sibling, "Anything.exe") } : new string[0];
                }, delegate(string directory)
                {
                    string[] children;
                    return tree.TryGetValue(directory, out children) ? children : new string[0];
                }, delegate { return FileAttributes.Normal; }, out paths, out complete));
            Eq(false, complete); Eq(1, paths.Count);
            Eq(root, visits[0]); Eq(deep, visits[1]); Eq(sibling, visits[2]);
            Eq(Path.Combine(sibling, "Anything.exe"), paths[0]);
        }

        private static void TestGenericExecutableSearchFaults()
        {
            const string root = @"C:\VirtualScan";
            string executable = Path.Combine(root, "Anything.exe");
            List<string> paths;
            bool complete;
            // A filesystem fault is not a budget limit, even if a strong-looking path was found first.
            Eq(false, ExecutableCandidateProbe.CollectPaths(root,
                delegate { return new[] { executable }; },
                delegate { throw new IOException("fixture read fault"); },
                delegate { return FileAttributes.Normal; }, out paths, out complete));
            Eq(false, complete); Eq(1, paths.Count);
            Eq(false, ExecutableCandidateProbe.CollectPaths(root,
                delegate { return new[] { executable }; }, delegate { return new string[0]; },
                delegate(string path) { return path == executable ? FileAttributes.ReparsePoint : FileAttributes.Normal; },
                out paths, out complete));
            Eq(false, complete);
            Eq(false, ExecutableCandidateProbe.CollectPaths(root,
                delegate { return new string[0]; }, delegate { return new string[0]; },
                delegate { return FileAttributes.ReparsePoint; }, out paths, out complete));
            Eq(false, complete);
        }
    }
}
