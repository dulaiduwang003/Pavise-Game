// @author bdth 2074055628@qq.com
// 文件用途 呈现探针 长帧采集与 DPC 对齐
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal partial class GameMode
    {
        // present 采集按局起停 每局新建实例(PresentProbe.frames 不自清 复用会跨局累积)
        //   需要管理员 非管理员或 Start 失败(内部已记日志)就不留引用 优雅跳过不崩
        private void StartPresentProbe()
        {
            try
            {
                PresentProbe old = presentProbe;
                presentProbe = null;
                if (old != null) try { old.Stop(); } catch { }
            }
            catch { }
            try
            {
                var p = new PresentProbe();
                presentProbe = p.Start() ? p : null;
            }
            catch { presentProbe = null; }
        }

        // 停掉本局 present 会话 只取渲染进程的帧 算出 长帧区间 [上一帧qpc,本帧qpc] 供与 DPC 时间线求交
        //   present↔DPC 对齐只为增强设备中断判断 不产出对局报告摘要(帧数/p99/1%low 都不要)
        //   必须是完整排空 无截断/丢事件且渲染 pid 自身至少有 30 个有效间隔 否则返回 null
        //   少量或其它进程的 present 不足以建立有意义的时间对齐样本
        //   区间按 QPC 递增且首尾相接 与 DPC 会话同一根 QPC 尺子(都 RawTimestamp)可直接对齐
        private void CollectLongFrames(int rendererPid, TimeSpan sessionDuration,
            out List<long[]> longFrameIntervals)
        {
            longFrameIntervals = null;
            PresentProbe p = presentProbe;
            presentProbe = null;
            if (p == null) return;

            List<PresentFrame> frames;
            long freq;
            try
            {
                p.Stop();
                if (!PresentDpcAlignment.PresentCaptureComplete(p.DrainCompleted, p.ConsumerExitedEarly,
                    p.Truncated, p.EventsLost, p.BuffersLost)) return;
                frames = p.Frames;
                freq = p.QpcFrequency;
            }
            catch { return; }
            // 时间对齐样本至少要覆盖半局且不少于 2 秒 只抓到开局一小撮帧时不记线索
            double minCoverageSeconds = Math.Max(2.0, sessionDuration.TotalSeconds * 0.5);
            longFrameIntervals = PresentDpcAlignment.BuildLongFrameIntervals(
                frames, freq, rendererPid, minCoverageSeconds);
        }

        internal static int UpdateSessionRendererPid(string sessionProfileId, int currentPid,
            string detectedProfileId, int detectedPid)
        {
            return !string.IsNullOrEmpty(sessionProfileId) && detectedPid > 0
                && string.Equals(sessionProfileId, detectedProfileId, StringComparison.OrdinalIgnoreCase)
                ? detectedPid : currentPid;
        }

        internal static bool SameReportedProfile(string sessionProfileId, string detectedProfileId)
        {
            return !string.IsNullOrEmpty(sessionProfileId)
                && !string.IsNullOrEmpty(detectedProfileId)
                && string.Equals(sessionProfileId, detectedProfileId, StringComparison.OrdinalIgnoreCase);
        }

    }
}
