// @author bdth 2074055628@qq.com
// 文件用途 检出系统偷偷给游戏挂上的容错堆 shim 并提供可还原的解除 只摘注册表指针 不删 sdb 文件
using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Win32;

namespace PaviseApp
{
    // 容错堆 FTH 在进程以堆损坏 0xC0000374 或 ntdll 访问违例 0xC0000005 反复崩溃后
    // 由系统自动给该 exe 挂一层堆兼容垫片 用户全程无感知
    // 触发条件写在 FTH 键的 CrashVelocity 与 CrashWindowInMinutes 上 本机默认 60 分钟内 3 次
    internal static class FthTweak
    {
        private const string FthKey = @"SOFTWARE\Microsoft\FTH";
        private const string FthStateKey = @"SOFTWARE\Microsoft\FTH\State";
        private const string CustomKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Custom";
        private const string InstalledSdbKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\InstalledSDB";

        private const string ExclusionValue = "ExclusionList";
        private const string ExclusionBakKey = "FthExclusionBak";
        private const string CustomBakKey = "FthCustomBak";
        private const string Absent = "__pavise_absent__";

        private const char RecordSep = (char)0x1E;
        private const char FieldSep = (char)0x1F;

        private static readonly object lk = new object();

        internal sealed class Shim
        {
            public string Exe;        // 镜像名 FTH 与 AppCompat 都按镜像名匹配 不带路径
            public string ValueName;  // Custom\<exe> 下那个 {GUID}.sdb 值名
            public string SdbPath;    // 仅用于展示 该文件永不删除 留作还原源
        }

        // ---------- 只读检测 ----------

        public static bool FeatureEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(FthKey))
                {
                    if (k == null) return false;
                    object v = k.GetValue("Enabled");
                    return v == null || Convert.ToInt64(v) != 0;
                }
            }
            catch { return false; }
        }

        // 系统判定该 exe 崩得够频繁所需的次数与窗口 用于把结论说清楚而不是甩个结果
        public static bool TryReadTrigger(out int velocity, out int windowMinutes)
        {
            velocity = 0; windowMinutes = 0;
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(FthKey))
                {
                    if (k == null) return false;
                    object v = k.GetValue("CrashVelocity");
                    object w = k.GetValue("CrashWindowInMinutes");
                    if (v == null || w == null) return false;
                    velocity = Convert.ToInt32(v);
                    windowMinutes = Convert.ToInt32(w);
                    return velocity > 0 && windowMinutes > 0;
                }
            }
            catch { return false; }
        }

        public static string[] Exclusions()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(FthKey))
                {
                    if (k == null) return new string[0];
                    return k.GetValue(ExclusionValue) as string[] ?? new string[0];
                }
            }
            catch { return new string[0]; }
        }

        public static bool IsExcluded(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return false;
            foreach (string s in Exclusions())
                if (string.Equals(s, exe, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // FTH 自己记账的那批 exe 格式未公开 值名和子键名都当候选镜像名收着
        private static HashSet<string> StateNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(FthStateKey))
                {
                    if (k == null) return names;
                    foreach (string n in k.GetValueNames()) if (n.Length > 0) names.Add(n);
                    foreach (string n in k.GetSubKeyNames()) if (n.Length > 0) names.Add(n);
                }
            }
            catch { }
            return names;
        }

        internal enum SdbVerdict
        {
            NotFth = 0,   // 描述读得到 且明说不是 FTH 装的 装机程序自带的兼容修复走这条
            Fth = 1,      // 描述正面确认
            Unknown = 2   // 描述读不到 只能靠 FTH 自己记的账兜底
        }

        // 同一个 exe 底下可能还挂着别人装的兼容修复 描述明说不是 FTH 就绝不认领
        // 只有描述读不出来时 才允许拿 FTH\State 的记账兜底 免得旧记录把别人的垫片带走
        internal static SdbVerdict ClassifySdb(string valueName, out string sdbPath)
        {
            sdbPath = null;
            string guid = valueName;
            if (guid.EndsWith(".sdb", StringComparison.OrdinalIgnoreCase))
                guid = guid.Substring(0, guid.Length - 4);
            if (guid.Length == 0) return SdbVerdict.Unknown;
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(InstalledSdbKey + "\\" + guid))
                {
                    if (k == null) return SdbVerdict.Unknown;
                    string desc = k.GetValue("DatabaseDescription") as string;
                    sdbPath = k.GetValue("DatabasePath") as string;
                    if (string.IsNullOrEmpty(desc)) return SdbVerdict.Unknown;
                    bool fth = desc.IndexOf("fault tolerant heap", StringComparison.OrdinalIgnoreCase) >= 0
                        || desc.IndexOf("faulttolerantheap", StringComparison.OrdinalIgnoreCase) >= 0;
                    return fth ? SdbVerdict.Fth : SdbVerdict.NotFth;
                }
            }
            catch { return SdbVerdict.Unknown; }
        }

        // 体检页每渲染一行都会问一次判据 这里要遍历 Custom 下全部子键再逐个查 InstalledSDB
        // 短时缓存一下 避免一次渲染重复扫十几遍注册表 改动路径会主动作废
        private static List<Shim> cached;
        private static long cachedTicks;
        private const int CacheMs = 2000;

        private static void InvalidateCache()
        {
            lock (lk) { cached = null; cachedTicks = 0; }
        }

        public static List<Shim> Detect()
        {
            lock (lk)
            {
                if (cached != null
                    && DateTime.UtcNow.Ticks - cachedTicks < CacheMs * TimeSpan.TicksPerMillisecond)
                    return new List<Shim>(cached);
            }
            List<Shim> scanned = Scan();
            lock (lk) { cached = scanned; cachedTicks = DateTime.UtcNow.Ticks; }
            return new List<Shim>(scanned);
        }

        private static List<Shim> Scan()
        {
            var found = new List<Shim>();
            HashSet<string> state = StateNames();
            try
            {
                using (RegistryKey root = Registry.LocalMachine.OpenSubKey(CustomKey))
                {
                    if (root == null) return found;
                    foreach (string exe in root.GetSubKeyNames())
                    {
                        using (RegistryKey k = root.OpenSubKey(exe))
                        {
                            if (k == null) continue;
                            foreach (string valueName in k.GetValueNames())
                            {
                                if (!valueName.EndsWith(".sdb", StringComparison.OrdinalIgnoreCase)) continue;
                                string sdbPath;
                                SdbVerdict verdict = ClassifySdb(valueName, out sdbPath);
                                // 明说不是 FTH 的直接放过 认不出来的才看 FTH 有没有记过这个 exe
                                if (verdict == SdbVerdict.NotFth) continue;
                                if (verdict == SdbVerdict.Unknown && !state.Contains(exe)) continue;
                                found.Add(new Shim { Exe = exe, ValueName = valueName, SdbPath = sdbPath });
                            }
                        }
                    }
                }
            }
            catch { }
            return found;
        }

        public static bool NeedsRepair()
        {
            return Detect().Count > 0;
        }

        public static bool RepairedByPavise
        {
            get { return Settings.LoadStr(CustomBakKey, "").Length > 0 || Settings.LoadStr(ExclusionBakKey, "").Length > 0; }
        }

        public static string Describe()
        {
            if (!FeatureEnabled()) return Lang.T("fth.off");
            List<Shim> shims = Detect();
            if (shims.Count == 0)
            {
                return RepairedByPavise ? Lang.T("fth.cleared") : Lang.T("fth.clean");
            }
            var names = new List<string>();
            foreach (Shim s in shims) if (!names.Contains(s.Exe)) names.Add(s.Exe);
            int velocity, window;
            if (TryReadTrigger(out velocity, out window))
                return Lang.F("fth.hit.why", string.Join(" ", names.ToArray()), window, velocity);
            return Lang.F("fth.hit", string.Join(" ", names.ToArray()));
        }

        // ---------- 可还原的解除 ----------

        public static bool Repair()
        {
            lock (lk)
            {
                List<Shim> shims = Detect();
                if (shims.Count == 0) return true;

                var exes = new List<string>();
                foreach (Shim s in shims)
                    if (!HasName(exes, s.Exe)) exes.Add(s.Exe);

                // 先排除 再摘指针 顺序反了的话摘完到下次崩溃之前还会被重新挂上
                if (!AddExclusions(exes))
                {
                    Logger.Log(Lang.T("log.fthtweak.1"));
                    return false;
                }
                int done = 0;
                foreach (Shim s in shims)
                {
                    if (DropPointer(s)) done++;
                    else Logger.Log(Lang.T("log.fthtweak.2") + s.Exe + " " + s.ValueName);
                }
                InvalidateCache();
                Logger.Log(Lang.F("log.fthtweak.3", done, string.Join(" ", exes.ToArray())));
                return done == shims.Count;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                bool ok = RestorePointers();
                ok &= RestoreExclusions();
                InvalidateCache();
                if (ok) Logger.Log(Lang.T("log.fthtweak.4"));
                else Logger.Log(Lang.T("log.fthtweak.5"));
                return ok;
            }
        }

        // ---------- ExclusionList 是 REG_MULTI_SZ 自带备份与回读 ----------

        private static bool AddExclusions(List<string> exes)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(FthKey, true))
                {
                    if (k == null) return false;
                    // 值在但类型不是 MULTI_SZ 说明被别的工具改过 覆盖会丢原值 一律不碰
                    object raw = k.GetValue(ExclusionValue);
                    if (raw != null)
                    {
                        RegistryValueKind curKind = RegistryValueKind.Unknown;
                        try { curKind = k.GetValueKind(ExclusionValue); } catch { }
                        if (curKind != RegistryValueKind.MultiString)
                        {
                            Logger.Log(Lang.T("log.fthtweak.6") + curKind);
                            return false;
                        }
                    }
                    string[] current = raw as string[];
                    if (Settings.LoadStr(ExclusionBakKey, "").Length == 0)
                    {
                        string encoded = current == null ? Absent : EncodeList(current);
                        Settings.SaveStr(ExclusionBakKey, encoded);
                        if (Settings.LoadStr(ExclusionBakKey, "") != encoded) return false;
                    }

                    var merged = new List<string>();
                    if (current != null) merged.AddRange(current);
                    foreach (string exe in exes)
                        if (!HasName(merged, exe)) merged.Add(exe);

                    k.SetValue(ExclusionValue, merged.ToArray(), RegistryValueKind.MultiString);
                    string[] readBack = k.GetValue(ExclusionValue) as string[];
                    if (readBack == null) return false;
                    foreach (string exe in exes)
                        if (!HasName(readBack, exe)) return false;
                    return true;
                }
            }
            catch { return false; }
        }

        private static bool RestoreExclusions()
        {
            string stored = Settings.LoadStr(ExclusionBakKey, "");
            if (stored.Length == 0) return true;
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(FthKey, true))
                {
                    if (k == null) return false;
                    if (stored == Absent)
                    {
                        k.DeleteValue(ExclusionValue, false);
                        if (k.GetValue(ExclusionValue) != null) return false;
                    }
                    else
                    {
                        string[] original = DecodeList(stored);
                        if (original == null) return false;
                        k.SetValue(ExclusionValue, original, RegistryValueKind.MultiString);
                        string[] readBack = k.GetValue(ExclusionValue) as string[];
                        if (!ListsEqual(readBack, original)) return false;
                    }
                }
                Settings.Remove(ExclusionBakKey);
                return true;
            }
            catch { return false; }
        }

        // ---------- Custom\<exe> 下的 sdb 指针 只删注册表值 文件原地不动 ----------

        private static bool DropPointer(Shim shim)
        {
            try
            {
                string path = CustomKey + "\\" + shim.Exe;
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(path, true))
                {
                    if (k == null) return false;
                    object value = k.GetValue(shim.ValueName);
                    if (value == null) return true;
                    // 读不出类型就还原不回原样 宁可不动 这一条和 ReversibleReg 的守卫同理
                    RegistryValueKind kind;
                    try { kind = k.GetValueKind(shim.ValueName); }
                    catch { return false; }
                    if (kind == RegistryValueKind.Unknown) return false;

                    string record = shim.Exe + FieldSep + shim.ValueName + FieldSep
                        + ((int)kind).ToString() + FieldSep + EncodeValue(value);
                    string stored = Settings.LoadStr(CustomBakKey, "");
                    // 逐条比对 不能用子串查找 game.exe 会命中 mygame.exe 那条 备份被跳过就还原不回来
                    if (!HasBackupRecord(stored, shim.Exe, shim.ValueName))
                    {
                        string merged = stored.Length == 0 ? record : stored + RecordSep + record;
                        Settings.SaveStr(CustomBakKey, merged);
                        if (Settings.LoadStr(CustomBakKey, "") != merged) return false;
                    }

                    k.DeleteValue(shim.ValueName, false);
                    return k.GetValue(shim.ValueName) == null;
                }
            }
            catch { return false; }
        }

        private static bool RestorePointers()
        {
            string stored = Settings.LoadStr(CustomBakKey, "");
            if (stored.Length == 0) return true;
            bool all = true;
            foreach (string record in stored.Split(RecordSep))
            {
                if (record.Length == 0) continue;
                string[] parts = record.Split(FieldSep);
                if (parts.Length < 4) { all = false; continue; }
                int kindRaw;
                if (!int.TryParse(parts[2], out kindRaw)) { all = false; continue; }
                if (!Enum.IsDefined(typeof(RegistryValueKind), kindRaw)
                    || kindRaw == (int)RegistryValueKind.Unknown) { all = false; continue; }
                object value = DecodeValue(parts[3], (RegistryValueKind)kindRaw);
                if (value == null) { all = false; continue; }
                try
                {
                    using (RegistryKey k = Registry.LocalMachine.CreateSubKey(CustomKey + "\\" + parts[0]))
                    {
                        if (k == null) { all = false; continue; }
                        k.SetValue(parts[1], value, (RegistryValueKind)kindRaw);
                        if (k.GetValue(parts[1]) == null) all = false;
                    }
                }
                catch { all = false; }
            }
            if (all) Settings.Remove(CustomBakKey);
            return all;
        }

        // ---------- 编解码 值可能是 QWORD DWORD 二进制或字符串 一律走 base64 不猜类型 ----------

        // 三种载荷一律 base64 编码 值里带 0x1E 0x1F 也不会把记录切错
        internal static string EncodeValue(object value)
        {
            if (value is byte[]) return "b" + Convert.ToBase64String((byte[])value);
            if (value is string[]) return "m" + EncodeList((string[])value);
            return "s" + Convert.ToBase64String(Encoding.UTF8.GetBytes(
                Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)));
        }

        // 解码按备份里存的原类型走 不靠载荷长相猜 猜错会把 REG_SZ 的 "123" 还原成数字
        internal static object DecodeValue(string repr, RegistryValueKind kind)
        {
            try
            {
                if (repr.Length == 0) return null;
                if (repr[0] == 'b') return kind == RegistryValueKind.Binary
                    ? Convert.FromBase64String(repr.Substring(1)) : null;
                if (repr[0] == 'm') return kind == RegistryValueKind.MultiString
                    ? DecodeList(repr.Substring(1)) : null;
                if (repr[0] != 's') return null;
                // 载荷是文本 而备份里存的却是二进制或多字符串 说明对不上 不能猜着还原
                if (kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString
                    && kind != RegistryValueKind.DWord && kind != RegistryValueKind.QWord) return null;
                string body = Encoding.UTF8.GetString(Convert.FromBase64String(repr.Substring(1)));
                if (kind == RegistryValueKind.DWord)
                {
                    int i;
                    return int.TryParse(body, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out i) ? (object)i : null;
                }
                if (kind == RegistryValueKind.QWord)
                {
                    long n;
                    return long.TryParse(body, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out n) ? (object)n : null;
                }
                return body;
            }
            catch { return null; }
        }

        internal static string EncodeList(string[] list)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < list.Length; i++)
            {
                if (i > 0) sb.Append('\0');
                sb.Append(list[i]);
            }
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        internal static string[] DecodeList(string encoded)
        {
            try
            {
                string joined = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                return joined.Length == 0 ? new string[0] : joined.Split('\0');
            }
            catch { return null; }
        }

        private static bool ListsEqual(string[] a, string[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }

        // 备份串按记录切开逐条比对镜像名与值名 避免子串误命中
        internal static bool HasBackupRecord(string stored, string exe, string valueName)
        {
            if (string.IsNullOrEmpty(stored)) return false;
            foreach (string record in stored.Split(RecordSep))
            {
                if (record.Length == 0) continue;
                string[] parts = record.Split(FieldSep);
                if (parts.Length < 2) continue;
                if (string.Equals(parts[0], exe, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(parts[1], valueName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool HasName(IEnumerable<string> list, string value)
        {
            if (list == null) return false;
            foreach (string s in list)
                if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
