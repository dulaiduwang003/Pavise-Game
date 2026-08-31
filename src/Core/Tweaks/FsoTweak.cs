// @author bdth 2074055628@qq.com
// 文件用途 逐游戏禁用 Windows 全屏优化 向 HKCU 兼容层 Layers 写入 token 可逆 写后回读核验
using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    // 逐游戏的持久偏好 直接读写 HKCU 兼容层字符串 不走 ReversibleReg 也不参与对局快照
    //   兼容层是 exe 启动时读的 下次启动该游戏才生效 对局临时下发这一局无效 所以不挂载不还原
    internal static class FsoTweak
    {
        private const string LayersKey =
            @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        // 禁用全屏优化的兼容层 token 首元素 ~ 是用户级兼容层标记
        private const string Token = "DISABLEDXMAXIMIZEDWINDOWEDMODE";
        private const string Marker = "~";
        // 台账只记 Pavise 自己写过的 exe 用户自己在系统属性里关的全屏优化不进这里也不还原
        //   分隔符用不可能出现在路径里的单元分隔符 免得跟路径里的分号空格打架
        private const string ListKey = "FsoExeList";
        private const char ListSep = '\u001F';
        private static readonly object sync = new object();

#if PAVISE_SELFTEST
        internal static Func<string, string> ReadLayerForTest;
        internal static Action<string, string> WriteLayerForTest;
        internal static Func<string, bool> SaveTrackedForTest;
#endif

        internal static string[] TrackedExes()
        {
            lock (sync)
            {
                string stored;
                if (!Settings.TryLoadStr(ListKey, out stored))
                    throw new InvalidOperationException("Cannot read the FSO recovery ledger");
                return stored.Split(new[] { ListSep }, StringSplitOptions.RemoveEmptyEntries);
            }
        }

        public static bool HasResidue()
        {
            try { return TrackedExes().Length > 0; }
            catch { return true; } // Unreadable ownership is not an empty ledger.
        }

        // 一键清除配置时按台账逐个撤销 撤销走的是和界面同一条 RemoveToken 路径 不碰别人的 token
        public static bool RestoreAll()
        {
            lock (sync)
            {
                try
                {
                    bool all = true;
                    foreach (string exe in TrackedExes())
                        if (!SetForExe(exe, false)) all = false;
                    return all && TrackedExes().Length == 0;
                }
                catch { return false; }
            }
        }

        private static bool Track(string exePath, bool disabled)
        {
            var kept = new List<string>();
            foreach (string exe in TrackedExes())
                if (!string.Equals(exe, exePath, StringComparison.OrdinalIgnoreCase)) kept.Add(exe);
            if (disabled) kept.Add(exePath);
            string next = string.Join(ListSep.ToString(), kept.ToArray());
            bool saved;
#if PAVISE_SELFTEST
            if (SaveTrackedForTest != null) saved = SaveTrackedForTest(next);
            else
#endif
                saved = Settings.SaveStr(ListKey, next);
            string confirmed;
            return saved && Settings.TryLoadStr(ListKey, out confirmed) && confirmed == next;
        }

        private static bool IsTracked(string exePath)
        {
            foreach (string exe in TrackedExes())
                if (string.Equals(exe, exePath, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // 读失败和令牌不存在要分开看 特别是移除之后一次被拒绝的读
        // 不能当成还原已确认 也不能清它的台账
        private static string ReadLayer(string exePath)
        {
#if PAVISE_SELFTEST
            if (ReadLayerForTest != null) return ReadLayerForTest(exePath);
            if (WriteLayerForTest != null) throw new InvalidOperationException("Missing in-memory FSO reader");
#endif
            using (var k = Registry.CurrentUser.OpenSubKey(LayersKey))
            {
                object value = k == null ? null : k.GetValue(exePath);
                if (value != null && !(value is string))
                    throw new InvalidOperationException("Unexpected FSO compatibility-layer value type");
                return value as string;
            }
        }

        private static void WriteLayer(string exePath, string value)
        {
#if PAVISE_SELFTEST
            if (WriteLayerForTest != null) { WriteLayerForTest(exePath, value); return; }
            if (ReadLayerForTest != null) throw new InvalidOperationException("Missing in-memory FSO writer");
#endif
            using (var k = Registry.CurrentUser.CreateSubKey(LayersKey))
            {
                if (k == null) throw new InvalidOperationException("Cannot open FSO compatibility layers");
                if (value.Length == 0) k.DeleteValue(exePath, false);
                else k.SetValue(exePath, value, RegistryValueKind.String);
            }
        }

        private static void RollbackToken(string exePath)
        {
            try
            {
                // 拿最新的图层字符串去合并 而不是把一个过期值
                // 重放到别人写过的兼容性设置上面
                string current = ReadLayer(exePath);
                if (HasToken(current)) WriteLayer(exePath, RemoveToken(current));
                if (!HasToken(ReadLayer(exePath))) Track(exePath, false);
            }
            catch { } // A failed rollback deliberately retains the recovery ledger.
        }

        public static bool IsDisabledForExe(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return false;
            lock (sync)
            {
                try { return HasToken(ReadLayer(exePath)); }
                catch (Exception ex)
                {
                    Logger.Log(Lang.T("log.fso.3") + " " + ex.Message);
                    return false;
                }
            }
        }

        public static bool SetForExe(string exePath, bool disableFso)
        {
            if (string.IsNullOrEmpty(exePath)) return false;
            lock (sync)
            {
                bool rollback = false;
                try
                {
                    string current = ReadLayer(exePath);
                    bool tracked = IsTracked(exePath);
                    if (HasToken(current) == disableFso)
                    {
                        // 用户本来就有的偏好 不算 Pavise 拥有的改动
                        // 一条之前还原过的跟踪条目 仍然需要清理
                        return disableFso || !tracked || Track(exePath, false);
                    }
                    // 动 Windows 之前 先确认那条持久的恢复记录已经落地
                    // 不要先改了再指望所有权后面能保存成功
                    if (disableFso && !tracked && !Track(exePath, true)) return false;
                    rollback = disableFso;
                    WriteLayer(exePath, disableFso ? AddToken(current) : RemoveToken(current));
                    if (HasToken(ReadLayer(exePath)) != disableFso)
                        throw new InvalidOperationException("FSO compatibility-layer verification failed");
                    if (disableFso && !IsTracked(exePath))
                        throw new InvalidOperationException("FSO recovery ledger changed during apply");
                    if (!disableFso && tracked && !Track(exePath, false)) return false;
                    Logger.Log(Lang.T(disableFso ? "log.fso.1" : "log.fso.2") + " " + exePath);
                    return true;
                }
                catch (Exception ex)
                {
                    if (rollback) RollbackToken(exePath);
                    Logger.Log(Lang.T("log.fso.3") + " " + ex.Message);
                    return false;
                }
            }
        }

        private static string[] Split(string s)
        {
            if (string.IsNullOrEmpty(s)) return new string[0];
            return s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static bool HasToken(string s)
        {
            foreach (string t in Split(s))
                if (string.Equals(t, Token, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // 收集除标记与目标 token 外的其它 token 顺带去掉重复的 ~ 保留别人的兼容层设置
        //   已有值可能畸形 ~ 不在首位或出现多次 这里统一丢弃所有 ~ 由调用方补一个在首位
        private static List<string> OtherTokens(string cur)
        {
            var others = new List<string>();
            foreach (string t in Split(cur))
            {
                if (t == Marker) continue;
                if (string.Equals(t, Token, StringComparison.OrdinalIgnoreCase)) continue;
                others.Add(t);
            }
            return others;
        }

        // 追加 token 保留其它 token 输出恒为 ~ 唯一且在首位 目标 token 在末尾
        private static string AddToken(string cur)
        {
            var parts = new List<string> { Marker };
            parts.AddRange(OtherTokens(cur));
            parts.Add(Token);
            return string.Join(" ", parts.ToArray());
        }

        // 只移除自己这个 token 不动别人的 还有其它 token 就补回唯一的 ~ 只剩标记则清空让整个 value 被删
        private static string RemoveToken(string cur)
        {
            var others = OtherTokens(cur);
            if (others.Count == 0) return "";
            var parts = new List<string> { Marker };
            parts.AddRange(others);
            return string.Join(" ", parts.ToArray());
        }
    }
}
