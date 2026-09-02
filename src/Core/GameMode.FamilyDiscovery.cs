// 文件用途 安装范围在库加载 添加或确认替换目标时确定
// 运行期发现只复用已捕获的进程身份 从不扫盘
// 也不会为某个具体游戏或启动器往渲染规则里加名字
using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private GameFamilyHistory gameFamilyHistory;
        private GameFamilyEvidence gameFamilyEvidence = GameFamilyEvidence.Empty;

        private GameFamilyHistory FamilyHistory
        {
            get
            {
                if (gameFamilyHistory == null)
                    Interlocked.CompareExchange(ref gameFamilyHistory, new GameFamilyHistory(), null);
                return gameFamilyHistory;
            }
        }

        private GameFamilyEvidence FamilyEvidence
        {
            get { return Volatile.Read(ref gameFamilyEvidence) ?? GameFamilyEvidence.Empty; }
        }

        private void ClearFamilyDiscovery()
        {
            lock (sync)
            {
                GameFamilyHistory history = gameFamilyHistory;
                if (history != null) history.Clear();
                Volatile.Write(ref gameFamilyEvidence, GameFamilyEvidence.Empty);
            }
        }

        // 事件源那 750 毫秒的批次送出来之前就需要已核实的身份
        // 包括库里还不认识的中转进程 这条路和 FastTrack 分开
        // 免得普通启动也被逼着走全量扫描
        public bool NeedsGameFamilyIdentity(int session)
        {
            if (session != selfSession || session < 0 || stopping || !enabled) return false;
            lock (sync) return profiles.Count > 0;
        }

        private void ObserveGameFamilyChanges(ProcessChangeBatch batch)
        {
            if (batch == null || stopping || !enabled) return;
            lock (sync)
            {
                if (stopping || !enabled || profiles.Count == 0) return;
                FamilyHistory.ObserveEvents(batch, selfSession, RendererNowMs());
            }
        }

        // 调用方持有 sync 并在同一把锁里取匹配的档案快照
        private void CaptureGameFamily(ProcessSnapshot snapshot, IList<GameProfile> library)
        {
            Volatile.Write(ref gameFamilyEvidence,
                FamilyHistory.Capture(snapshot, library, selfSession, RendererNowMs()));
        }

        internal static string ResolveLibraryInstallRoot(string executablePath, string fallbackRoot)
        {
            string fallback = NormalizeGameRoot(fallbackRoot);
            if (fallback == null) fallback = NormalizeGameRoot(GameScan.InferGameRoot(executablePath));
            fallback = GameInstallScope.RestrictFallback(executablePath, fallback);
            string resolved = NormalizeGameRoot(GameInstallScope.Resolve(executablePath, fallback));
            return resolved != null && FamilyBoundary.UnderRoot(executablePath, resolved) ? resolved : fallback;
        }

        // 已有条目的推断根目录可能太窄 加载时顺手把这份元数据修好
        // 选定的 EXE 选项和观测标签都不要动 没有安装记录就完全不放宽
        // 已知的平台或 common 容器目录 也不能当家族的兜底根
        internal static bool RefreshLibraryInstallRoots(IList<GameProfile> library)
        {
            bool changed = false;
            if (library == null) return false;
            foreach (GameProfile profile in library)
            {
                if (profile == null || string.IsNullOrEmpty(profile.ExecutablePath)) continue;
                string fallback = GameInstallScope.RestrictFallback(
                    profile.ExecutablePath, NormalizeGameRoot(profile.Root));
                bool rejectedOldRoot = !string.IsNullOrEmpty(profile.Root) && fallback == null;
                string resolved = NormalizeGameRoot(GameInstallScope.Resolve(
                    profile.ExecutablePath, fallback));
                if (resolved != null && !FamilyBoundary.UnderRoot(profile.ExecutablePath, resolved)) resolved = fallback;
                if (string.Equals(resolved, profile.Root, StringComparison.OrdinalIgnoreCase)) continue;
                // 不要收窄一个已经声明过的安装范围 也不要排除
                // 一个已确认的渲染进程 除非旧范围本身就是
                // 被证实不安全的容器目录 精确到 EXE 的身份一律保留
                if (!rejectedOldRoot)
                {
                    if (resolved == null) continue;
                    if (!string.IsNullOrEmpty(profile.Root) && !FamilyBoundary.UnderRoot(profile.Root, resolved)) continue;
                    if (!string.IsNullOrEmpty(profile.LearnedExecutablePath)
                        && !FamilyBoundary.UnderRoot(profile.LearnedExecutablePath, resolved)) continue;
                }
                profile.Root = resolved;
                changed = true;
            }
            return changed;
        }
    }
}
