// @author bdth 2074055628@qq.com
// 文件用途 统一管理界面动画时钟 只有对局期间整体冻结为静态 其余一律全速
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PaviseApp
{
    internal struct Motion
    {
        public float Value;
        public float Target;
        public float Speed;
        public void Set(float v) { Value = v; Target = v; }
        public void To(float t) { Target = t; }
        public bool Step()
        {
            if (Speed <= 0f) Speed = 0.25f;
            if (UiClock.Frozen)
            {
                if (Value == Target) return false;
                Value = Target;
                return true;
            }
            float d = Target - Value;
            if (d < 0.0015f && d > -0.0015f) { if (Value != Target) { Value = Target; return true; } return false; }
            Value += d * StepFraction(Speed, UiClock.DeltaScale);
            return true;
        }

        internal static float StepFraction(float speed, float deltaScale)
        {
            if (speed >= 1f) return 1f;
            if (deltaScale <= 0f) return 0f;
            if (deltaScale == 1f) return speed;
            double kept = Math.Pow(1.0 - speed, deltaScale);
            float f = (float)(1.0 - kept);
            return f > 1f ? 1f : f;
        }
    }

    internal static class UiClock
    {
        // WinForms Timer 走 WM_TIMER 粒度就是系统时钟跳 15.6ms 请求再小也拿不到更快
        //   本机实测每档 200 次投递 请求 10ms 和 15ms 的均值都是 15.6ms 都是 64 FPS
        //   timeBeginPeriod(1) 对 WM_TIMER 无效 实测提不提精度均值一样
        //   老写法请求 10ms 并在动画期间常驻提精度 一帧都没多拿到 却把全局系统时钟拉到 1ms
        //   15 卡在一跳之内 每跳必发 投递也最规律 中位数 15.58ms 而 10ms 档中位数 12.51ms 抖动明显
        //   不要改成 16 一跳 15.6ms 不够 16 会等到下一跳 实测掉到 40 FPS 中位数 27.67ms
        //   动画快慢由 Motion.StepFraction 按 DeltaScale 归一 换帧率不改观感
        internal const int FrameMs = 15;
        internal const int SlowMs = 200;

        internal const float BaselineFrameMs = 16.667f;
        internal const float MinDeltaScale = 0.25f;
        internal const float MaxDeltaScale = 4f;

        private static System.Windows.Forms.Timer timer;
        private static System.Windows.Forms.Timer slowTimer;
        private static int framesLeft;
        private static int slowFramesLeft;
        private static bool suspended;
        private static bool frozen;
        private static bool settling;
        private static readonly Stopwatch frameWatch = new Stopwatch();
        private static float deltaScale = 1f;

        public static float DeltaScale { get { return deltaScale; } }

        internal static float ScaleFor(double elapsedMs)
        {
            float s = (float)(elapsedMs / BaselineFrameMs);
            if (s < MinDeltaScale) return MinDeltaScale;
            if (s > MaxDeltaScale) return MaxDeltaScale;
            return s;
        }

        private static void AdvanceDelta()
        {
            if (!frameWatch.IsRunning) { frameWatch.Reset(); frameWatch.Start(); deltaScale = 1f; return; }
            double ms = frameWatch.Elapsed.TotalMilliseconds;
            frameWatch.Reset();
            frameWatch.Start();
            deltaScale = ScaleFor(ms);
        }
        public static event EventHandler Frame;
        public static event EventHandler SlowFrame;

        private static void Ensure()
        {
            if (timer != null) return;
            timer = new System.Windows.Forms.Timer();
            timer.Interval = FrameMs;
            timer.Tick += (s, e) =>
            {
                if (suspended) { framesLeft = 0; StopFrames(); return; }
                AdvanceDelta();
                if (Frame != null) Frame(null, EventArgs.Empty);
                if (--framesLeft <= 0) StopFrames();
            };
            slowTimer = new System.Windows.Forms.Timer();
            slowTimer.Interval = SlowMs;
            slowTimer.Tick += (s, e) =>
            {
                if (suspended) { slowFramesLeft = 0; slowTimer.Stop(); return; }
                if (SlowFrame != null) SlowFrame(null, EventArgs.Empty);
                if (--slowFramesLeft <= 0) slowTimer.Stop();
            };
        }

        private static void StopFrames()
        {
            timer.Stop();
            frameWatch.Reset();
        }

        public static bool Frozen
        {
            get { return frozen; }
            set
            {
                if (frozen == value) return;
                frozen = value;
                if (!value) return;
                Ensure();
                framesLeft = 0;
                slowFramesLeft = 0;
                StopFrames();
                slowTimer.Stop();
                if (!suspended) Settle();
            }
        }

        private static void Settle()
        {
            if (settling) return;
            settling = true;
            try
            {
                deltaScale = 1f;
                if (Frame != null) Frame(null, EventArgs.Empty);
            }
            finally { settling = false; }
        }

        public static bool HasFrameListeners { get { return Frame != null; } }

        public static void Wake(int frames = 48)
        {
            if (Frame == null) return;
            Ensure();
            if (suspended) return;
            if (frozen) { Settle(); return; }
            if (frames > framesLeft) framesLeft = frames;
            if (!timer.Enabled) timer.Start();
        }

        public static void WakeSlow(int frames = 12)
        {
            if (SlowFrame == null) return;
            Ensure();
            if (suspended || frozen) return;
            if (frames > slowFramesLeft) slowFramesLeft = frames;
            if (!slowTimer.Enabled) slowTimer.Start();
        }

        public static bool Suspended
        {
            get { return suspended; }
            set
            {
                Ensure();
                if (suspended == value) return;
                suspended = value;
                if (value)
                {
                    framesLeft = 0;
                    slowFramesLeft = 0;
                    StopFrames();
                    slowTimer.Stop();
                }
            }
        }

        public static bool Borrow()
        {
            bool was = suspended;
            if (was) Suspended = false;
            Wake();
            return was;
        }

        public static void Return(bool was)
        {
            if (was) Suspended = true;
        }

        public static bool Running
        {
            get { return timer != null && timer.Enabled; }
            set
            {
                Ensure();
                if (value) Wake();
                else { framesLeft = 0; StopFrames(); }
            }
        }
    }

}
