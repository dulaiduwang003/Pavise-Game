// @author bdth 2074055628@qq.com
// 文件用途 在进程快照里识别运行中的游戏与游戏库路径
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private string FindRunningGame(ProcessSnapshot all, out HashSet<int> gamePids)
        {
            List<GameProfile> copy;
            lock (sync)
            {
                copy = new List<GameProfile>();
                foreach (GameProfile p in profiles) copy.Add(p.Clone());
                CaptureGameFamily(all, copy);
            }
            gamePids = new HashSet<int>();
            ObserveRendererForegroundChange();
            string armedName = null;
            string armedVia = null;
            GameDetection raw = null;
            bool fullDetection = ShouldRunFullGameDetection();
            if (fullDetection)
            {
                raw = GameSessionDetector.Detect(
                    all, copy, selfSession, out armedName, out armedVia, FamilyEvidence);
            }
            int epoch;
            GameDetection proposal = ResolveRendererHandoff(all, copy, raw, out epoch);
            if (proposal != null && !FinalizeRendererSelection(proposal, epoch)) proposal = null;
            if (epoch != Volatile.Read(ref rendererHandoffEpoch)
                || stopping || !enabled || panicReq || ProfileStoreSaveFailed) return null;
            GameDetection hit = ApplyStickiness(proposal);
            if (hit != null && !stickyGraceOnly && !VerifyRendererCandidate(hit))
            {
                ClearSticky();
                RequestFullGameDetection();
                return null;
            }
            if (proposal != null && RendererHandoffTracker.SameIdentity(proposal, hit))
                CompleteRendererSelection(hit);
            if (fullDetection)
            {
                armedAwaitingElection = armedName != null && hit == null;
                UpdateArmedStatus(hit == null ? armedName : null, armedVia, hit != null);
                if (hit == null && armedName != null) PreStageDriverTuning(copy, armedName);
            }
            else if (hit != null && armedAwaitingElection)
            {
                armedAwaitingElection = false;
                UpdateArmedStatus(null, null, true);
            }
            if (hit == null) return null;
            // 渲染锚已证实不存在时 sticky 的一轮快速重扫只是
            // detector 内部缓冲 不能再当成一轮 running 去执行 Boost
            // 交给主循环的 8 秒宽限处理 它会先 Seal 而不是丢局
            if (stickyGraceOnly) return null;
            foreach (int pid in hit.FamilyPids) gamePids.Add(pid);
            if (irqProbe.HasSealedPending)
            {
                bool sameSealedProfile;
                string sealedGame;
                lock (sync)
                {
                    sameSealedProfile = SameReportedProfile(
                        repProfileId,
                        hit.Profile != null ? hit.Profile.Id : null);
                    sealedGame = repGame;
                }
                if (sameSealedProfile)
                {
                    // renderer 可能在上轮快照后 OpenProcess 前退出
                    // 因而已 Seal 但尚未进入 gameGone 宽限 同 profile
                    // 新 renderer 出现时仍要丢弃旧前缀并重武装
                    ArmIrqObservation(sealedGame ?? hit.Profile.Name);
                }
            }
            if (irqProbe.IsCapturing)
            {
                bool proofStrict;
                ulong proofMask = EffectiveGameMask(sessionPolicy, out proofStrict);
                if (irqProbe.IsSystemObservation) proofMask = 0;
                if (!irqProbe.ProofMatches(
                        proofMask, hit.RendererPid,
                        hit.RendererCreation))
                {
                    bool sameSessionProfile;
                    string sameSessionGame;
                    lock (sync)
                    {
                        sameSessionProfile = SameReportedProfile(
                            repProfileId,
                            hit.Profile != null ? hit.Profile.Id : null);
                        sameSessionGame = repGame;
                    }
                    irqProbe.InvalidateGameMask();
                    // 同一局从启动器换成真实 renderer 旧片段彻底丢弃 但允许新
                    // renderer 从零开始一个 epoch 不会把一局拆成两条台账记录
                    if (sameSessionProfile)
                        ArmIrqObservation(sameSessionGame ?? hit.Profile.Name);
                }
            }
            lock (sync)
            {
                if (!RendererEpochCurrent(epoch) || hit.Profile == null
                    || !RendererHandoffTracker.SameProfile(hit.Profile, FindProfileLocked(hit.Profile.Id))) return null;
                if (ShouldRearmLauncherTransition(
                        activeDetection, hit))
                {
                    transitionProbeRendererPid = 0;
                    transitionProbeRendererCreation = 0;
                }
                // 同一 profile 局内可从启动器更新为真实渲染器 换到另一个
                // profile 时不能在旧局 ReportFinish 前把它的 present 过滤 PID 覆盖掉
                repRendererPid = UpdateSessionRendererPid(
                    repProfileId, repRendererPid,
                    hit.Profile != null ? hit.Profile.Id : null, hit.RendererPid);
                activeDetection = hit;
            }
            // 启动器交接成真实渲染进程时扩展要拿到新身份 没有活动对局时这里等于一次空通知
            NotifyExtensionSession(true);
            MaybeObserveRendererActivity(hit);
            return hit.Profile.Name;
        }

        private volatile string armedGameName;
        private string lastArmedLogged;

        public string ArmedGame
        {
            get { lock (sync) return active ? null : armedGameName; }
        }

        private volatile bool gpuPrefStageOn;
        private readonly CpuLimitProbe cpuLimit = new CpuLimitProbe();
        private long sessionStartTicks;

        public List<string> LibraryExecutablePaths()
        {
            var paths = new List<string>();
            lock (sync)
                foreach (GameProfile p in profiles)
                {
                    string path = p.PreferredExecutablePath;
                    if (!string.IsNullOrEmpty(path)) paths.Add(path);
                }
            return paths;
        }
    }
}
