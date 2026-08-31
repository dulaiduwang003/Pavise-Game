// 文件用途 输入语言的同意只在入场那一刻采集 中途改动会撤销待处理的工作
// 但不能在本次游戏运行期内再武装第二次请求
using System;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private volatile bool englishInputOn;
        private int englishInputGeneration;
        private long englishInputCreationFloor;
        private EnglishInputOnce englishInputOnce;

        private void InitializeEnglishInput()
        {
            // 重启 Pavise 不能把一个已经在跑的游戏的中文输入
            // 当成新入场 这是操作系统的 FILETIME 和 RendererCreation 一样
            Interlocked.Exchange(ref englishInputCreationFloor, DateTime.UtcNow.ToFileTimeUtc());
            englishInputOn = Settings.Load(PolicyCatalog.KeyEnglishInput, false);
            englishInputOnce = new EnglishInputOnce(new GameInputLanguage());
        }

        public bool EnglishInputEnabled
        {
            get { return englishInputOn; }
            set
            {
                lock (sync)
                {
                    if (stopping) return;
                    InvalidateEnglishInputWork();
                    // 落盘失败永远不给新的开启资格 关闭时
                    // 哪怕保存不了 也要把待处理的工作停掉
                    bool saved = Settings.Save(PolicyCatalog.KeyEnglishInput, value);
                    englishInputOn = value && saved;
                }
                RequestPolicyApply();
            }
        }

        private bool LiveEnglishInputPreference()
        {
            // 调用方持有 sync IsGlobal 也包含没有任何覆盖的档案
            return LiveBoolPreferenceLocked(PolicyCatalog.KeyEnglishInput, englishInputOn);
        }

        private Func<bool> CaptureEnglishInputAdmission(bool entry, out string profileId,
            out string profileName, out GameInputProcess process, out bool wanted)
        {
            int generation;
            long creationFloor;
            string id;
            bool preference;
            GameInputProcess renderer;
            lock (sync)
            {
                generation = Volatile.Read(ref englishInputGeneration);
                creationFloor = Volatile.Read(ref englishInputCreationFloor);
                GameDetection detection = activeDetection;
                profileId = id = detection != null && detection.Profile != null ? detection.Profile.Id : null;
                profileName = detection != null && detection.Profile != null ? detection.Profile.Name : "";
                bool selected = detection != null && detection.RendererCandidateSelected
                    && !detection.RendererSafetyOnly && !detection.RequiresGpuConfirm;
                process = renderer = selected ? new GameInputProcess(detection.RendererPid, detection.RendererCreation)
                    : new GameInputProcess();
                PolicySnapshot snapshot = sessionPolicy;
                wanted = preference = LiveEnglishInputPreference() && !string.IsNullOrEmpty(id)
                    && creationFloor > 0 && (!renderer.IsValid || renderer.Creation >= creationFloor)
                    && (snapshot == null || string.IsNullOrEmpty(snapshot.ProfileId)
                        || string.Equals(snapshot.ProfileId, id, StringComparison.OrdinalIgnoreCase));
            }
            return delegate
            {
                if (!preference || creationFloor != Volatile.Read(ref englishInputCreationFloor)
                    || generation != Volatile.Read(ref englishInputGeneration) || !Volatile.Read(ref active)
                    || !enabled || stopping || panicReq || Volatile.Read(ref standbyCleanerRestorePending) != 0
                    || ProfileStoreSaveFailed || Volatile.Read(ref stickyGraceOnly)
                    || Volatile.Read(ref gameGoneSinceTicks) != 0) return false;
                GameDetection detection = Volatile.Read(ref activeDetection);
                if (detection == null || detection.Profile == null
                    || !string.Equals(detection.Profile.Id, id, StringComparison.OrdinalIgnoreCase)) return false;
                bool selected = detection.RendererCandidateSelected && !detection.RendererSafetyOnly
                    && !detection.RequiresGpuConfirm;
                // 渲染进程只能在 Begin 之后才选得出来 请求准入时把启动下限
                // 再套一遍 免得迟到的发现绕过它
                if (selected && detection.RendererCreation > 0 && detection.RendererCreation < creationFloor)
                    return false;
                // 入场令牌跟随本局已核实的渲染进程交接
                // 单条请求还额外钉死它自己那个渲染进程
                return entry || (selected && renderer.IsValid && renderer.Creation >= creationFloor
                    && detection.RendererPid == renderer.Pid
                    && detection.RendererCreation == renderer.Creation);
            };
        }

        private void BeginEnglishInputSession()
        {
            EnglishInputOnce runner = englishInputOnce;
            if (runner == null) return;
            string profileId, profileName;
            GameInputProcess process;
            bool wanted;
            Func<bool> admission = CaptureEnglishInputAdmission(true, out profileId, out profileName, out process, out wanted);
            runner.Begin(profileId, process, wanted, admission);
        }

        private void StepEnglishInputSession()
        {
            EnglishInputOnce runner = englishInputOnce;
            if (runner == null) return;
            string profileId, profileName;
            GameInputProcess process;
            bool wanted;
            Func<bool> admission = CaptureEnglishInputAdmission(false, out profileId, out profileName, out process, out wanted);
            // 这里绝不能 Begin 中途开启表达的是下次入场的意图
            EnglishInputOutcome result = runner.Step(profileId, process, admission);
            if (result == null) return;
            string key;
            switch (result.Result)
            {
                case EnglishInputResult.Changed: key = "englishinput.changed"; break;
                case EnglishInputResult.AlreadyEnglish: key = "englishinput.already"; break;
                case EnglishInputResult.Unconfirmed: key = "englishinput.unconfirmed"; break;
                case EnglishInputResult.Denied: key = "englishinput.denied"; break;
                case EnglishInputResult.NoEnglishLayout: key = "englishinput.nolayout"; break;
                case EnglishInputResult.TargetChanged: key = "englishinput.targetchanged"; break;
                case EnglishInputResult.Expired: key = "englishinput.expired"; break;
                case EnglishInputResult.Canceled: key = "englishinput.canceled"; break;
                case EnglishInputResult.HistoryFull: key = "englishinput.historyfull"; break;
                default: key = "englishinput.unavailable"; break;
            }
            Logger.Log(Lang.F("log.englishinput.result", profileName, Lang.T(key), result.Error));
        }

        private void InvalidateEnglishInputWork()
        {
            Interlocked.Increment(ref englishInputGeneration);
            EnglishInputOnce runner = englishInputOnce;
            if (runner != null) runner.Cancel();
        }

        private void EndEnglishInputSession()
        {
            InvalidateEnglishInputWork();
            EnglishInputOnce runner = englishInputOnce;
            if (runner != null) runner.End();
        }

        private bool DrainEnglishInput(int timeoutMs)
        {
            EnglishInputOnce runner = englishInputOnce;
            return runner == null || runner.Drain(timeoutMs);
        }
    }
}
