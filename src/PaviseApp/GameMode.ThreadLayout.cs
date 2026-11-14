using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private readonly Dictionary<string, ThreadLayoutCaptureRequest> threadLayoutCapturePhases = new Dictionary<string, ThreadLayoutCaptureRequest>(StringComparer.Ordinal);
        private long threadLayoutSerial = -1;
        private string threadLayoutIdentity;
        private bool threadLayoutDiagnosticsReady, threadLayoutPhaseLimitReported;
        private ThreadLayoutCaptureRequest threadLayoutCapture;

        private static string ThreadLayoutIdentity(CriticalRuntimeSource source)
        { return source.Serial + "|" + source.Profile + "|" + source.Pid + "|" + source.Birth + "|" + source.RuleText; }

        private void BeginThreadLayoutDiagnostics(CriticalRuntimeSource source)
        {
            string identity = ThreadLayoutIdentity(source);
            if (identity == threadLayoutIdentity) return;
            threadLayoutIdentity = identity;
            threadLayoutDiagnosticsReady = false;
            ResetElasticAuxiliary();
            if (threadLayoutSerial != source.Serial)
            {
                foreach (ThreadLayoutCaptureRequest phase in threadLayoutCapturePhases.Values) phase.Cancel();
                threadLayoutCapturePhases.Clear();
                threadLayoutPhaseLimitReported = false;
                threadLayoutSerial = source.Serial;
            }
            threadLayoutCapture = null;
            if (source.Rule.IsDependencyBoost)
            {
                StartThreadLayoutDiagnostics(source, 0);
                return;
            }
            ulong processMask;
            if (!ReadCriticalProcessMask(source.Pid, source.Birth, out processMask))
            {
                Logger.Warn("线程识别 原进程范围无法核验；游戏=" + source.Profile + "；进程编号=" + source.Pid
                    + "；进程创建=" + source.Birth + "；等待现有授权入口核验范围");
                return;
            }
            StartThreadLayoutDiagnostics(source, processMask);
        }

        private void StartThreadLayoutDiagnostics(CriticalRuntimeSource source, ulong processMask)
        {
            if (threadLayoutDiagnosticsReady || processMask == 0 && !source.Rule.IsDependencyBoost || threadLayoutIdentity != ThreadLayoutIdentity(source)) return;
            // 只复用已读回的范围建立当前识别身份，不为日志增加原生读取或重建采集预算。
            threadLayoutDiagnosticsReady = true;
            Logger.Info("线程识别 开始 会话=" + source.Serial + " 游戏=" + source.Profile + " PID=" + source.Pid
                + " 创建=" + source.Birth + " 规则=" + source.RuleText
                + (source.Rule.IsDependencyBoost ? " 仅优先级控制，无亲和性授权" : " 允许范围=0x" + processMask.ToString("X")));
        }

        private ThreadLayoutCaptureRequest EnsureThreadLayoutCapture(CriticalRuntimeSource source)
        {
            if (source.Rule.Layout == CriticalThreadLayout.Compatible) return null;
            string identity = ThreadLayoutIdentity(source);
            ThreadLayoutCaptureRequest request;
            if (!threadLayoutCapturePhases.TryGetValue(identity, out request))
            {
                if (threadLayoutCapturePhases.Count >= 16)
                {
                    if (!threadLayoutPhaseLimitReported)
                    {
                        threadLayoutPhaseLimitReported = true;
                        Logger.Warn("线程识别 本局规则身份数量达到上限；保留原采集记录，不再新增阶段");
                    }
                    return null;
                }
                request = new ThreadLayoutCaptureRequest(source.Pid, source.Birth, identity, AutoCores.NowTicks,
                    delegate(string stage, string detail, bool usable)
                    {
                        if (!string.Equals(Volatile.Read(ref threadLayoutIdentity), identity, StringComparison.Ordinal)) return;
                        string line = "线程识别 会话=" + source.Serial + " 游戏=" + source.Profile + " PID=" + source.Pid
                            + " 创建=" + source.Birth + " 阶段=" + stage + "；" + detail;
                        if (usable) Logger.Info(line); else Logger.Warn(line);
                    }, source.Rule.UsesWorkloadDiscovery);
                threadLayoutCapturePhases.Add(identity, request);
            }
            threadLayoutCapture = request;
            FrameCoordination.ConfigureLayoutObservation(request);
            return request;
        }

        private bool ThreadLayoutObservationWanted(CriticalRuntimeSource source)
        {
            return source.Rule.Layout != CriticalThreadLayout.Compatible && threadLayoutCapture != null
                && threadLayoutCapture.NeedsObserver(AutoCores.NowTicks);
        }

        private void EndThreadLayoutDiagnostics(string reason, bool restored)
        {
            if (threadLayoutIdentity == null) return;
            if (threadLayoutCapture != null) threadLayoutCapture.Cancel();
            FrameCoordination.ConfigureLayoutObservation(null);
            string line = "线程识别 结束 身份=" + threadLayoutIdentity + " 原因=" + reason
                + "；恢复=" + (restored ? "已确认现存设置归还或原对象退出" : "尚未结清，保留所有权及回执");
            if (restored) Logger.Info(line); else Logger.Warn(line);
            threadLayoutIdentity = null;
            threadLayoutDiagnosticsReady = false;
            ResetElasticAuxiliary();
        }
    }
}
