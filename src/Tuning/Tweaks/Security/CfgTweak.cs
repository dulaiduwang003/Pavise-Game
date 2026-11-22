using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace PaviseApp
{
    // 映像劫持选项的读写 测试换成内存实现 正式版走 64 位注册表视图
    internal interface IIfeoStore
    {
        // 映像键不存在时返回 null
        string[] SubKeyNames(string image);

        // subKey 为 null 表示映像键本身
        object Read(string image, string subKey, string name);

        void Write(string image, string subKey, string name, object value, RegistryValueKind kind);

        void Delete(string image, string subKey, string name);

        void DeleteSubKey(string image, string subKey);

        // 映像键下既没有值也没有子键时把它删掉
        void PruneImage(string image);
    }

    // 逐游戏关闭控制流防护 CFG
    // 写在映像劫持选项里并按完整路径过滤 只影响这一个 exe 下次启动游戏生效
    // 只增删 Pavise 自己的过滤子键 别人按路径做的程序设置一律不改
    internal static class CfgTweak
    {
        internal const string ListKey = "CfgExeList";

        internal const string FilterPrefix = "PaviseCfg-";

        private const char ListSep = '\u001f';

        // MitigationOptions 第一个 QWORD 的第 40 和 41 位 1 是强制开 2 是强制关 0 跟随系统
        internal const ulong CfgMask = 3UL << 40;

        internal const ulong CfgAlwaysOff = 2UL << 40;

        // 新系统按 16 字节读取 名字级已有更长的值时保留原长度
        private const int MinimumOptionsLength = 16;

        private static readonly object sync = new object();

#if PAVISE_SELFTEST
        internal static IIfeoStore StoreForTest;

        private static IIfeoStore Store
        {
            get
            {
                if (StoreForTest == null) throw new InvalidOperationException("Missing in-memory IFEO store");
                return StoreForTest;
            }
        }
#else
        private static readonly IIfeoStore Store = new RegistryIfeoStore();
#endif

        internal static string[] TrackedExes()
        {
            lock (sync)
            {
                string value;
                if (!Settings.TryLoadStr(ListKey, out value))
                {
                    throw new InvalidOperationException("Cannot read the CFG recovery ledger");
                }
                return value.Split(new char[1] { ListSep }, StringSplitOptions.RemoveEmptyEntries);
            }
        }

        public static bool HasResidue()
        {
            try
            {
                return TrackedExes().Length > 0;
            }
            catch
            {
                return true;
            }
        }

        public static bool RestoreAll()
        {
            lock (sync)
            {
                try
                {
                    bool allRestored = true;
                    foreach (string exePath in TrackedExes())
                    {
                        allRestored &= SetForExe(exePath, false);
                    }
                    return allRestored && TrackedExes().Length == 0;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static bool SetForExe(string exePath, bool disableCfg)
        {
            string image = ImageName(exePath);
            if (image == null)
            {
                return false;
            }
            lock (sync)
            {
                bool applying = false;
                try
                {
                    IIfeoStore store = Store;
                    bool tracked = IsTracked(exePath);
                    string ours = FindFilter(store, image, exePath, true);
                    string foreign = FindFilter(store, image, exePath, false);
                    if (!disableCfg)
                    {
                        if (ours != null)
                        {
                            RemoveFilter(store, image, ours);
                            Logger.Log(() => Lang.T("log.cfg.restored") + " " + exePath);
                        }
                        return !tracked || Track(exePath, false);
                    }
                    if (foreign != null)
                    {
                        // 这个 exe 已经有别人按路径做的程序设置 两条过滤谁先生效说不准 不叠加
                        if (ours != null)
                        {
                            RemoveFilter(store, image, ours);
                        }
                        if (tracked)
                        {
                            Track(exePath, false);
                        }
                        bool alreadyOff = CfgOff(store.Read(image, foreign, "MitigationOptions"));
                        if (!alreadyOff)
                        {
                            Logger.Log(() => Lang.T("log.cfg.foreign") + " " + exePath);
                        }
                        return alreadyOff;
                    }
                    if (ours != null && FilterReady(store, image, ours))
                    {
                        return tracked || Track(exePath, true);
                    }
                    // 先记账再写 写到一半退出 下次启动也能按记录撤销
                    if (!tracked && !Track(exePath, true))
                    {
                        return false;
                    }
                    applying = true;
                    string subKey = ours ?? FilterName(exePath);
                    store.Write(image, subKey, "FilterFullPath", exePath, RegistryValueKind.String);
                    store.Write(image, subKey, "MitigationOptions", MergeOptions(store.Read(image, null, "MitigationOptions")), RegistryValueKind.Binary);
                    store.Write(image, null, "UseFilter", 1, RegistryValueKind.DWord);
                    if (!FilterReady(store, image, subKey) || FindFilter(store, image, exePath, false) != null)
                    {
                        throw new InvalidOperationException("CFG program setting verification failed");
                    }
                    if (!IsTracked(exePath))
                    {
                        throw new InvalidOperationException("CFG recovery ledger changed during apply");
                    }
                    Logger.Log(() => Lang.T("log.cfg.disabled") + " " + exePath);
                    return true;
                }
                catch (Exception ex)
                {
                    if (applying)
                    {
                        Rollback(image, exePath);
                    }
                    Logger.Log(() => Lang.T("log.cfg.failed") + " " + exePath + " " + ex.Message);
                    return false;
                }
            }
        }

        internal static string ImageName(string exePath)
        {
            if (string.IsNullOrEmpty(exePath) || !Path.IsPathRooted(exePath))
            {
                return null;
            }
            string name = Path.GetFileName(exePath);
            if (string.IsNullOrEmpty(name) || !string.Equals(Path.GetExtension(name), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return name;
        }

        // 子键名带路径摘要 同名 exe 装在不同目录时各占一个子键
        internal static string FilterName(string exePath)
        {
            uint hash = 2166136261;
            foreach (char c in exePath.ToUpperInvariant())
            {
                hash = (hash ^ c) * 16777619;
            }
            return FilterPrefix + hash.ToString("x8");
        }

        internal static bool CfgOff(object options)
        {
            byte[] bytes = options as byte[];
            return bytes != null && bytes.Length >= 8 && (BitConverter.ToUInt64(bytes, 0) & CfgMask) == CfgAlwaysOff;
        }

        // 照抄名字级已有的缓解设置 只改 CFG 两位 过滤生效后名字级的值对这个路径不再起作用
        internal static byte[] MergeOptions(object nameLevel)
        {
            byte[] source = null;
            if (nameLevel != null)
            {
                source = nameLevel as byte[];
                if (source == null)
                {
                    throw new InvalidOperationException("Unexpected MitigationOptions value type");
                }
            }
            int length = Math.Max(MinimumOptionsLength, source == null ? 0 : (source.Length + 7) / 8 * 8);
            byte[] result = new byte[length];
            if (source != null)
            {
                Buffer.BlockCopy(source, 0, result, 0, source.Length);
            }
            ulong first = (BitConverter.ToUInt64(result, 0) & ~CfgMask) | CfgAlwaysOff;
            Buffer.BlockCopy(BitConverter.GetBytes(first), 0, result, 0, 8);
            return result;
        }

        private static bool FilterReady(IIfeoStore store, string image, string subKey)
        {
            object useFilter = store.Read(image, null, "UseFilter");
            return useFilter is int && (int)useFilter != 0 && CfgOff(store.Read(image, subKey, "MitigationOptions"));
        }

        // ours 为 true 找 Pavise 的过滤子键 为 false 找别人指向同一路径的过滤子键
        private static string FindFilter(IIfeoStore store, string image, string exePath, bool ours)
        {
            string[] names = store.SubKeyNames(image);
            if (names == null)
            {
                return null;
            }
            foreach (string name in names)
            {
                if (name.StartsWith(FilterPrefix, StringComparison.OrdinalIgnoreCase) != ours)
                {
                    continue;
                }
                string path = store.Read(image, name, "FilterFullPath") as string;
                if (path != null && string.Equals(path, exePath, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }
            return null;
        }

        // 删掉自己的子键 没有别的路径过滤了就收回 UseFilter 映像键空了一并删除
        private static void RemoveFilter(IIfeoStore store, string image, string subKey)
        {
            store.DeleteSubKey(image, subKey);
            bool otherFilters = false;
            string[] names = store.SubKeyNames(image);
            if (names != null)
            {
                foreach (string name in names)
                {
                    if (store.Read(image, name, "FilterFullPath") is string)
                    {
                        otherFilters = true;
                        break;
                    }
                }
            }
            if (!otherFilters)
            {
                store.Delete(image, null, "UseFilter");
            }
            store.PruneImage(image);
            string[] after = store.SubKeyNames(image);
            if (after != null && Array.IndexOf(after, subKey) >= 0)
            {
                throw new InvalidOperationException("CFG program setting was not removed");
            }
        }

        private static void Rollback(string image, string exePath)
        {
            try
            {
                IIfeoStore store = Store;
                string ours = FindFilter(store, image, exePath, true);
                if (ours == null)
                {
                    string[] names = store.SubKeyNames(image);
                    string planned = FilterName(exePath);
                    if (names != null && Array.IndexOf(names, planned) >= 0)
                    {
                        ours = planned;
                    }
                }
                if (ours != null)
                {
                    RemoveFilter(store, image, ours);
                }
                if (FindFilter(store, image, exePath, true) == null)
                {
                    Track(exePath, false);
                }
            }
            catch
            {
            }
        }

        private static bool IsTracked(string exePath)
        {
            foreach (string entry in TrackedExes())
            {
                if (string.Equals(entry, exePath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool Track(string exePath, bool disabled)
        {
            List<string> list = new List<string>();
            foreach (string entry in TrackedExes())
            {
                if (!string.Equals(entry, exePath, StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(entry);
                }
            }
            if (disabled)
            {
                list.Add(exePath);
            }
            string joined = string.Join(ListSep.ToString(), list.ToArray());
            string confirmed;
            return Settings.SaveStr(ListKey, joined) && Settings.TryLoadStr(ListKey, out confirmed) && confirmed == joined;
        }

        private sealed class RegistryIfeoStore : IIfeoStore
        {
            private const string IfeoKey = "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options";

            private static RegistryKey Machine()
            {
                return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            }

            private static string PathOf(string image, string subKey)
            {
                return IfeoKey + "\\" + image + (subKey == null ? "" : "\\" + subKey);
            }

            public string[] SubKeyNames(string image)
            {
                using (RegistryKey machine = Machine())
                using (RegistryKey key = machine.OpenSubKey(PathOf(image, null)))
                {
                    return key == null ? null : key.GetSubKeyNames();
                }
            }

            public object Read(string image, string subKey, string name)
            {
                using (RegistryKey machine = Machine())
                using (RegistryKey key = machine.OpenSubKey(PathOf(image, subKey)))
                {
                    return key == null ? null : key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                }
            }

            public void Write(string image, string subKey, string name, object value, RegistryValueKind kind)
            {
                using (RegistryKey machine = Machine())
                using (RegistryKey key = machine.CreateSubKey(PathOf(image, subKey)))
                {
                    if (key == null)
                    {
                        throw new InvalidOperationException("Cannot open image file execution options");
                    }
                    key.SetValue(name, value, kind);
                }
            }

            public void Delete(string image, string subKey, string name)
            {
                using (RegistryKey machine = Machine())
                using (RegistryKey key = machine.OpenSubKey(PathOf(image, subKey), true))
                {
                    if (key != null)
                    {
                        key.DeleteValue(name, false);
                    }
                }
            }

            public void DeleteSubKey(string image, string subKey)
            {
                using (RegistryKey machine = Machine())
                using (RegistryKey key = machine.OpenSubKey(PathOf(image, null), true))
                {
                    if (key != null)
                    {
                        key.DeleteSubKeyTree(subKey, false);
                    }
                }
            }

            public void PruneImage(string image)
            {
                using (RegistryKey machine = Machine())
                using (RegistryKey parent = machine.OpenSubKey(IfeoKey, true))
                {
                    if (parent == null)
                    {
                        return;
                    }
                    using (RegistryKey key = parent.OpenSubKey(image))
                    {
                        if (key == null || key.ValueCount > 0 || key.SubKeyCount > 0)
                        {
                            return;
                        }
                    }
                    parent.DeleteSubKey(image, false);
                }
            }
        }
    }
}
