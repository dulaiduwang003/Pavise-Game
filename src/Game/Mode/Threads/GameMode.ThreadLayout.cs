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
            if (threadLayoutSerial != source.Serial)
            {
                foreach (ThreadLayoutCaptureRequest phase in threadLayoutCapturePhases.Values) phase.Cancel();
                threadLayoutCapturePhases.Clear();
                threadLayoutPhaseLimitReported = false;
                threadLayoutSerial = source.Serial;
            }
            threadLayoutCapture = null;
            ulong processMask;
            if (!ReadCriticalProcessMask(source.Pid, source.Birth, out processMask))
            {
                Logger.Warn(Logger.Format("线程识别 原进程范围无法核验；游戏={0}；进程编号={1}；进程创建={2}；等待现有授权入口核验范围", "Thread identification: original process range cannot be verified; game={0}; pid={1}; processBirth={2}; waiting for the existing authorized entry to verify the range", source.Profile, source.Pid, source.Birth));
                return;
            }
            StartThreadLayoutDiagnostics(source, processMask);
        }

        private void StartThreadLayoutDiagnostics(CriticalRuntimeSource source, ulong processMask)
        {
            if (threadLayoutDiagnosticsReady || processMask == 0 || threadLayoutIdentity != ThreadLayoutIdentity(source)) return;
            // 只复用已读回的范围建立当前识别身份 不为日志增加原生读取或重建采集预算
            threadLayoutDiagnosticsReady = true;
            Logger.Info(Logger.Format("线程识别 开始 会话={0} 游戏={1} PID={2} 创建={3} 规则={4}{5}", "Thread identification start session={0} game={1} PID={2} birth={3} rule={4}{5}", source.Serial, source.Profile, source.Pid, source.Birth, source.RuleText, Logger.Text(" 允许范围=0x", " allowedMask=0x") + processMask.ToString("X")));
        }

        private ThreadLayoutCaptureRequest EnsureThreadLayoutCapture(CriticalRuntimeSource source)
        {
            string identity = ThreadLayoutIdentity(source);
            if (threadLayoutCapture != null && threadLayoutCapture.Identity == identity) return threadLayoutCapture;
            ThreadLayoutCaptureRequest request;
            if (!threadLayoutCapturePhases.TryGetValue(identity, out request))
            {
                if (threadLayoutCapturePhases.Count >= 16)
                {
                    if (!threadLayoutPhaseLimitReported)
                    {
                        threadLayoutPhaseLimitReported = true;
                        Logger.Warn(Logger.Text("线程识别 本局规则身份数量达到上限；保留原采集记录，不再新增阶段", "Thread identification: rule identity count for this match reached its limit; original capture records kept, no new phases"));
                    }
                    return null;
                }
                request = new ThreadLayoutCaptureRequest(source.Pid, source.Birth, identity, AutoCores.NowTicks,
                    delegate(string stage, string detail, bool usable)
                    {
                        if (!string.Equals(Volatile.Read(ref threadLayoutIdentity), identity, StringComparison.Ordinal)) return;
                        string line = Logger.Text("线程识别 会话=", "Thread identification session=") + source.Serial + Logger.Text(" 游戏=", " game=") + source.Profile + " PID=" + source.Pid
                            + Logger.Text(" 创建=", " birth=") + source.Birth + Logger.Text(" 阶段=", " phase=") + stage + Logger.Text("；", "; ") + detail;
                        if (usable) Logger.Info(line); else Logger.Warn(line);
                    });
                threadLayoutCapturePhases.Add(identity, request);
            }
            threadLayoutCapture = request;
            FrameCoordination.ConfigureLayoutObservation(request);
            return request;
        }

        private void EndThreadLayoutDiagnostics(string reason, bool restored)
        {
            if (threadLayoutIdentity == null) return;
            if (threadLayoutCapture != null) threadLayoutCapture.Cancel();
            FrameCoordination.ConfigureLayoutObservation(null);
            string line = Logger.Text("线程识别 结束 身份=", "Thread identification end identity=") + threadLayoutIdentity + Logger.Text(" 原因=", " reason=") + reason
                + Logger.Text("；恢复=", "; restore=") + (restored ? Logger.Text("已确认现存设置归还或原对象退出", "existing settings confirmed returned or the original object exited") : Logger.Text("尚未结清，保留所有权及回执", "not yet settled; ownership and receipts retained"));
            if (restored) Logger.Info(line); else Logger.Warn(line);
            threadLayoutIdentity = null;
            threadLayoutDiagnosticsReady = false;
        }
    }
}
