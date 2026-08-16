// @author bdth 2074055628@qq.com
// 文件用途 校正第三方优化工具在键鼠驱动上留下的队列长度改动 回到系统默认 可逆

using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class InputMythTweak
    {
        public const int SystemDefault = 100;

        private const string MouseParams = @"SYSTEM\CurrentControlSet\Services\mouclass\Parameters";
        private const string KbdParams = @"SYSTEM\CurrentControlSet\Services\kbdclass\Parameters";
        private const string MouseValue = "MouseDataQueueSize";
        private const string KbdValue = "KeyboardDataQueueSize";
        private const string KernelPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";
        private const string DpcValue = "ThreadDpcEnable";

        private static readonly ReversibleReg MouseQueue = new ReversibleReg(
            Registry.LocalMachine, MouseParams, MouseValue, RegistryValueKind.DWord, "PrevMouseQueue");
        private static readonly ReversibleReg KbdQueue = new ReversibleReg(
            Registry.LocalMachine, KbdParams, KbdValue, RegistryValueKind.DWord, "PrevKbdQueue");

        private static readonly object lk = new object();

        public static bool RepairedByPavise { get { return Settings.Load("InputMythRepaired", false); } }

        public static int? MouseQueueSize() { return Read(MouseParams, MouseValue); }
        public static int? KeyboardQueueSize() { return Read(KbdParams, KbdValue); }

        public static int? ThreadDpcOverride() { return Read(KernelPath, DpcValue); }

        private static int? Read(string path, string name)
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(path))
                {
                    if (key == null) return null;
                    object raw = key.GetValue(name);
                    return raw is int ? (int?)(int)raw : null;
                }
            }
            catch { return null; }
        }

        internal static bool IsTampered(int? value)
        {
            // 只有小于默认才算残留 队列截短会在高回报率下丢输入
            // 大于默认多为 8kHz 外设厂商软件刻意调大防丢包 属正常配置不碰
            return value.HasValue && value.Value < SystemDefault;
        }

        public static bool NeedsRepair()
        {
            return IsTampered(MouseQueueSize()) || IsTampered(KeyboardQueueSize());
        }

        public static string Describe()
        {
            if (RepairedByPavise) return Lang.T("t.inputmythtweak.1") + SystemDefault + Lang.T("t.inputmythtweak.2");

            var parts = new List<string>();
            int? mouse = MouseQueueSize();
            int? kbd = KeyboardQueueSize();
            if (IsTampered(mouse)) parts.Add(Lang.T("t.inputmythtweak.3") + mouse.Value);
            if (IsTampered(kbd)) parts.Add(Lang.T("t.inputmythtweak.4") + kbd.Value);
            if (parts.Count == 0) return Lang.T("t.inputmythtweak.5") + SystemDefault + Lang.T("t.inputmythtweak.6");
            return string.Join("  ", parts.ToArray())
                + Lang.T("t.inputmythtweak.7");
        }

        public static bool Repair()
        {
            lock (lk)
            {
                bool ok = true;
                if (IsTampered(MouseQueueSize())) ok &= MouseQueue.Apply(SystemDefault);
                if (IsTampered(KeyboardQueueSize())) ok &= KbdQueue.Apply(SystemDefault);
                if (ok)
                {
                    Settings.Save("InputMythRepaired", true);
                    Logger.Log(Lang.T("log.inputmythtweak.8") + SystemDefault + Lang.T("log.inputmythtweak.9"));
                }
                else Logger.Log(Lang.T("log.inputmythtweak.10"));
                return ok;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                bool all = MouseQueue.Restore();
                all &= KbdQueue.Restore();
                if (all)
                {
                    Settings.Save("InputMythRepaired", false);
                    Logger.Log(Lang.T("log.inputmythtweak.11"));
                }
                else Logger.Log(Lang.T("log.inputmythtweak.12"));
                return all;
            }
        }

        public static bool HasResidue()
        {
            return RepairedByPavise || MouseQueue.HasBackup || KbdQueue.HasBackup;
        }
    }
}
