// @author bdth 2074055628@qq.com
// 文件用途 统一管理界面动画时钟

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
        internal const int ForegroundFrameMs = 10;
        internal const int BackgroundFrameMs = 200;
        internal const int ForegroundSlowMs = 200;
        internal const int BackgroundSlowMs = 1000;

        internal const float BaselineFrameMs = 16.667f;
        internal const float MinDeltaScale = 0.25f;
        internal const float MaxDeltaScale = 4f;

        private static System.Windows.Forms.Timer timer;
        private static System.Windows.Forms.Timer slowTimer;
        private static int framesLeft;
        private static int slowFramesLeft;
        private static bool suspended;
        private static bool background;
        private static readonly Stopwatch frameWatch = new Stopwatch();
        private static float deltaScale = 1f;
        private static bool precisionHeld;

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

        internal static int DesiredFrameMs(bool isBackground)
        {
            return isBackground ? BackgroundFrameMs : ForegroundFrameMs;
        }

        internal static int DesiredSlowMs(bool isBackground)
        {
            return isBackground ? BackgroundSlowMs : ForegroundSlowMs;
        }

        public static bool Background
        {
            get { return background; }
            set
            {
                if (background == value) return;
                background = value;
                if (timer == null) return;
                int frameMs = DesiredFrameMs(value);
                if (timer.Interval != frameMs) timer.Interval = frameMs;
                int slowMs = DesiredSlowMs(value);
                if (slowTimer.Interval != slowMs) slowTimer.Interval = slowMs;
            }
        }

        private static void Ensure()
        {
            if (timer != null) return;
            timer = new System.Windows.Forms.Timer();
            timer.Interval = DesiredFrameMs(background);
            timer.Tick += (s, e) =>
            {
                if (suspended) { framesLeft = 0; StopFrames(); return; }
                AdvanceDelta();
                if (Frame != null) Frame(null, EventArgs.Empty);
                if (--framesLeft <= 0) StopFrames();
            };
            slowTimer = new System.Windows.Forms.Timer();
            slowTimer.Interval = DesiredSlowMs(background);
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
            HoldPrecision(false);
        }

        private static void HoldPrecision(bool hold)
        {
            if (hold == precisionHeld) return;
            try
            {
                if (hold) Native.timeBeginPeriod(1);
                else Native.timeEndPeriod(1);
                precisionHeld = hold;
            }
            catch { }
        }

        public static void Wake(int frames = 48)
        {
            Ensure();
            if (suspended) return;
            if (frames > framesLeft) framesLeft = frames;
            if (!timer.Enabled) { HoldPrecision(true); timer.Start(); }
        }

        public static void WakeSlow(int frames = 12)
        {
            Ensure();
            if (suspended) return;
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
