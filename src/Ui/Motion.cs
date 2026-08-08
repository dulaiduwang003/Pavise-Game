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
            Value += d * Speed;
            return true;
        }
    }

    internal static class UiClock
    {
        internal const int ForegroundFrameMs = 16;
        internal const int BackgroundFrameMs = 200;
        internal const int ForegroundSlowMs = 200;
        internal const int BackgroundSlowMs = 1000;

        private static System.Windows.Forms.Timer timer;
        private static System.Windows.Forms.Timer slowTimer;
        private static int framesLeft;
        private static int slowFramesLeft;
        private static bool suspended;
        private static bool background;
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
                if (suspended) { framesLeft = 0; timer.Stop(); return; }
                if (Frame != null) Frame(null, EventArgs.Empty);
                if (--framesLeft <= 0) timer.Stop();
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

        public static void Wake(int frames = 48)
        {
            Ensure();
            if (suspended) return;
            if (frames > framesLeft) framesLeft = frames;
            if (!timer.Enabled) timer.Start();
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
                    timer.Stop();
                    slowTimer.Stop();
                }
            }
        }

        public static bool Running
        {
            get { return timer != null && timer.Enabled; }
            set
            {
                Ensure();
                if (value) Wake();
                else { framesLeft = 0; timer.Stop(); }
            }
        }
    }

}
