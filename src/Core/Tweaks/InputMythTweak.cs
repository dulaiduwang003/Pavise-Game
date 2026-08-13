// @author bdth 2074055628@qq.com
// 文件用途 校正第三方优化工具在键鼠驱动上留下的队列长度改动 回到系统默认 可逆
// 依据 MouseDataQueueSize 与 KeyboardDataQueueSize 控制的是能缓存多少条输入数据 不是这些数据被处理的快慢
// 所以调高不降延迟 调低反而在高回报率鼠标上丢输入 这是全网流传最广也最没有实测支撑的一条键鼠偏方
// 本软件不提供把它调低的开关 只提供把别人调坏的值改回默认

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
            return value.HasValue && value.Value != SystemDefault;
        }

        public static bool NeedsRepair()
        {
            return IsTampered(MouseQueueSize()) || IsTampered(KeyboardQueueSize());
        }

        public static string Describe()
        {
            if (RepairedByPavise) return "已改回系统默认 " + SystemDefault + " 拨回开关即还原成原先那台工具写的值";

            var parts = new List<string>();
            int? mouse = MouseQueueSize();
            int? kbd = KeyboardQueueSize();
            if (IsTampered(mouse)) parts.Add("鼠标队列被改成 " + mouse.Value);
            if (IsTampered(kbd)) parts.Add("键盘队列被改成 " + kbd.Value);
            if (parts.Count == 0) return "键鼠队列长度都是系统默认 " + SystemDefault + " 没被动过 不用处理";
            return string.Join("  ", parts.ToArray())
                + "  这个值不影响延迟 只决定能缓存多少条输入 调低了会在高回报率鼠标上丢输入";
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
                    Logger.Log("键鼠队列长度 已改回系统默认 " + SystemDefault + " 重启生效");
                }
                else Logger.Log("键鼠队列长度 写入失败 多半是权限不足");
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
                    Logger.Log("键鼠队列长度 已还原原值");
                }
                else Logger.Log("键鼠队列长度 还原失败 快照保留待下次重试");
                return all;
            }
        }

        public static bool HasResidue()
        {
            return RepairedByPavise || MouseQueue.HasBackup || KbdQueue.HasBackup;
        }
    }
}
