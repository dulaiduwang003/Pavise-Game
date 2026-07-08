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

namespace AegisApp
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
        private static System.Windows.Forms.Timer timer;
        private static int framesLeft;
        public static event EventHandler Frame;

        private static void Ensure()
        {
            if (timer != null) return;
            timer = new System.Windows.Forms.Timer();
            timer.Interval = 16;
            timer.Tick += (s, e) =>
            {
                if (Frame != null) Frame(null, EventArgs.Empty);
                if (--framesLeft <= 0) timer.Stop();
            };
        }

        public static void Wake(int frames = 48)
        {
            Ensure();
            if (frames > framesLeft) framesLeft = frames;
            if (!timer.Enabled) timer.Start();
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
