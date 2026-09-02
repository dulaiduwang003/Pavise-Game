// @author bdth 2074055628@qq.com
// 文件用途 游戏档案存取 保存失败信号与提优状态文案
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        public List<GameProfile> GetProfiles()
        {
            lock (sync)
            {
                var copy = new List<GameProfile>();
                foreach (GameProfile p in profiles) copy.Add(p.Clone());
                return copy;
            }
        }

        public bool ProfileStoreSaveFailed
        {
            get
            {
                return profileStore.SaveFailed
                    || Interlocked.CompareExchange(ref profileSaveFailureSignaled, 0, 0) != 0;
            }
        }

        public event Action ProfileStoreSaveFailure;

        private bool SaveProfilesLocked()
        {
            // 首次落盘失败就熔断 强制清空的 UI 回调是异步的
            // 回调执行前不得再尝试写入任何游戏库数据
            if (stopping || ProfileStoreSaveFailed) return false;
            if (profileStore.Save(profiles)) return true;
            SignalProfileStoreSaveFailure();
            return false;
        }

        private void SignalProfileStoreSaveFailure()
        {
            if (Interlocked.Exchange(ref profileSaveFailureSignaled, 1) != 0) return;
            InvalidateStandbyCleanerWork();
            InvalidateEnglishInputWork();
            InvalidateIntelGraphicsWork();
            InvalidateRendererHandoff();
            Action handler = ProfileStoreSaveFailure;
            if (handler != null) { try { handler(); } catch { } }
        }

        private const string CoreMaskKey = "GmCoreMask";

        public string StatusText
        {
            get
            {
                if (!enabled) return Lang.T("st.off");
                lock (sync)
                {
                    if (!active)
                    {
                        string armedName = armedGameName;
                        return armedName != null
                            ? Lang.F("st.armed", armedName) : Lang.T("st.mon");
                    }
                    long gone = gameGoneSinceTicks;
                    if (gone != 0)
                    {
                        int remain = ExitGraceSeconds
                            - (int)((DateTime.UtcNow.Ticks - gone) / TimeSpan.TicksPerSecond);
                        if (remain < 0) remain = 0;
                        return Lang.F("st.grace", activeGame, remain);
                    }
                    int n = core.ThrottledCountCached();
                    int b = boostStateVerified.Count;
                    string s = Lang.F("st.active", activeGame, n);
                    // 两段以前是直接拼的 出来是"已压制 28 个进程已提优 1" 中间没有断句
                    s += Lang.T("st.sep") + Lang.F("st.boost", b, Lang.T(planSwitch ? "st.hp" : "st.pr"));
                    return s;
                }
            }
        }

        public bool BoostStateVerified
        {
            get
            {
                lock (sync) return active && activeDetection != null
                    && boostStateVerified.Contains(activeDetection.RendererPid);
            }
        }

        public bool BoostHandleProtected
        {
            get
            {
                lock (sync) return active && activeDetection != null
                    && (boostHandleStripped.Contains(activeDetection.RendererPid)
                        || boostStateWarned.Contains(activeDetection.RendererPid));
            }
        }

        public string BoostStatusText
        {
            get
            {
                if (!enabled || !EffBoost) return Lang.T("v14.boost.disabled");
                lock (sync)
                {
                    if (!active) return Lang.T("v14.boost.wait");
                    if (activeDetection == null || activeDetection.RendererPid <= 0)
                        return Lang.T("v14.boost.no.renderer");
                    string name = activeDetection.RendererName ?? activeGame ?? "Game";
                    if (ProtectedGameRoster.Contains(activeDetection.RendererName))
                        return Lang.F("v14.boost.protected", name);
                    if (boostStateVerified.Contains(activeDetection.RendererPid))
                        return Lang.F("v14.boost.verified", name);
                    if (boostHandleStripped.Contains(activeDetection.RendererPid)
                        || boostStateWarned.Contains(activeDetection.RendererPid))
                        return Lang.F("v14.boost.protected", name);
                    return Lang.F("v14.boost.applying", name);
                }
            }
        }
    }
}
