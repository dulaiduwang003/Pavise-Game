using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PaviseApp
{
    // DSTS reports capabilities, not the active mode. Verify G-Helper's selection and a fresh driver acknowledgement.
    // Send only to its dedicated receiver; do not edit its files or inject global input.
    internal static class AsusPerformanceController
    {
        internal interface IRuntime
        {
            bool Read(out string configuration, out int processId, out long creation);
            bool Send(int processId, long creation, int key, int modifiers);
            long LogLength();
            string LogSince(long offset);
            void Pause();
        }

        private sealed class Selection
        {
            internal int Mode, Base, ProcessId, Modifiers;
            internal long Creation;
            internal int[] Keys;
        }

        private static readonly IRuntime native = new ControllerRuntime();
#if PAVISE_SELFTEST
        internal static IRuntime RuntimeForTest;
#endif
        private static Selection observed;
        private static string detail = "";
        internal static string Detail { get { return detail; } }
        private static IRuntime Runtime
        {
            get
            {
#if PAVISE_SELFTEST
                return RuntimeForTest;
#else
                return native;
#endif
            }
        }

        private static string Text(string zh, string en) { return Lang.Cur == 1 ? en : zh; }

        // Custom profile IDs are independent of firmware mode; parse by JSON keys.
        private static bool Parse(string json, out Selection value)
        {
            value = null;
            if (json == null || json.Length > 262144) return false;
            json = json.Trim().TrimStart('\uFEFF').Trim();
            if (json.Length < 2 || json[0] != '{') return false;
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            int cursor = 1;
            var pair = new Regex(@"\G\s*""(?<key>[^""\\]+)""\s*:\s*(?<value>""(?:\\.|[^""\\])*""|-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?|true|false|null)\s*(?<end>[,}])", RegexOptions.CultureInvariant);
            while (cursor < json.Length)
            {
                Match match = pair.Match(json, cursor);
                if (!match.Success || entries.ContainsKey(match.Groups["key"].Value)) return false;
                entries.Add(match.Groups["key"].Value, match.Groups["value"].Value);
                cursor += match.Length;
                if (match.Groups["end"].Value == "}") break;
            }
            if (cursor != json.Length) return false;
            int mode, baseMode, skip;
            if (!Number(entries, "performance_mode", -1, out mode) || mode < 0 || mode > 4
                || !Number(entries, "skip_hotkeys", 0, out skip) || skip != 0) return false;
            if (mode <= 2) baseMode = mode;
            else if (!Number(entries, "mode_base_" + mode, -1, out baseMode) || baseMode < 0 || baseMode > 3) return false;
            int[] keys = new int[5];
            int[] defaults = { 0x80, 0x81, 0x7F, 0x82, 0x83 }; // F17, F18, F16, F19, F20
            var distinct = new HashSet<int>();
            int cycle, overlay, xgm;
            if (!Number(entries, "keybind_profile", 0x74, out cycle)
                || !Number(entries, "keybind_overlay", 0x4F, out overlay)
                || !Number(entries, "keybind_xgm", 0x84, out xgm)) return false;
            for (int i = 0; i < keys.Length; i++)
            {
                if (!Number(entries, "keybind_profile_" + i, defaults[i], out keys[i])
                    || keys[i] < 0x7F || keys[i] > 0x87 || !distinct.Add(keys[i]) || keys[i] == cycle
                    || keys[i] == xgm || keys[i] == overlay) return false;
            }
            int modifiers = 7; // G-Helper's default Alt+Control+Shift profile modifier
            string modifier;
            if (entries.TryGetValue("modifier_keybind_alt", out modifier))
            {
                if (!Regex.IsMatch(modifier, "^\"(?:alt|control|shift|win)(?:-(?:alt|control|shift|win))*\"$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
                modifiers = 0;
                foreach (string part in modifier.Trim('"').ToLowerInvariant().Split('-'))
                    modifiers |= part == "alt" ? 1 : part == "control" ? 2 : part == "shift" ? 4 : 8;
            }
            int ordinaryModifiers = 6;
            if (entries.TryGetValue("modifier_keybind", out modifier))
            {
                if (!Regex.IsMatch(modifier, "^\"(?:alt|control|shift|win)(?:-(?:alt|control|shift|win))*\"$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
                ordinaryModifiers = 0;
                foreach (string part in modifier.Trim('"').ToLowerInvariant().Split('-'))
                    ordinaryModifiers |= part == "alt" ? 1 : part == "control" ? 2 : part == "shift" ? 4 : 8;
            }
            if (modifiers == ordinaryModifiers) return false;
            value = new Selection { Mode = mode, Base = baseMode, Keys = keys, Modifiers = modifiers };
            return true;
        }

        private static bool Number(Dictionary<string, string> values, string key, int fallback, out int value)
        {
            string raw;
            if (!values.TryGetValue(key, out raw)) { value = fallback; return true; }
            return int.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }

        private static bool ReadSelection(IRuntime runtime, out Selection value)
        {
            value = null;
            string json; int pid; long creation;
            if (runtime == null || !runtime.Read(out json, out pid, out creation) || !Parse(json, out value)) return false;
            value.ProcessId = pid; value.Creation = creation;
            return pid > 0 && creation > 0;
        }

        internal static bool Read(out int mode)
        {
            mode = -1;
            Selection value;
            try
            {
                if (!ReadSelection(Runtime, out value)) return false;
                observed = value; mode = value.Mode;
                detail = Text("ASUS · G-Helper 配置已核验；切档由控制器确认，退局恢复完整原配置", "ASUS · G-Helper selection verified; controller-confirmed switching and complete original-profile restoration");
                return true;
            }
            catch { return false; }
        }

        internal static bool IsPerformance(int mode)
        {
            Selection current = observed;
            // Preserve custom Turbo / Full Speed instead of replacing its fan curve.
            return current != null && current.Mode == mode && (current.Base == 1 || current.Base == 3);
        }

        internal static bool Write(int mode)
        {
            if (mode < 0 || mode > 4) return false;
            IRuntime runtime = Runtime;
            Selection expected = observed, current;
            try
            {
                if (expected == null || !ReadSelection(runtime, out current) || current.ProcessId != expected.ProcessId
                    || current.Creation != expected.Creation || current.Mode != expected.Mode) return false;
                string json; int pid; long creation;
                if (!runtime.Read(out json, out pid, out creation) || pid != current.ProcessId || creation != current.Creation) return false;
                Selection destination;
                string requested = Regex.Replace(json, "(\"performance_mode\"\\s*:\\s*)-?[0-9]+", "${1}" + mode);
                if (!Parse(requested, out destination)) return false;
                long offset = runtime.LogLength();
                if (offset < 0 || !runtime.Send(current.ProcessId, current.Creation, current.Keys[mode], current.Modifiers)) return false;
                int acknowledgedAt = -1;
                for (int attempt = 0; attempt < 40; attempt++)
                {
                    runtime.Pause();
                    Selection check;
                    if (!ReadSelection(runtime, out check) || check.ProcessId != current.ProcessId || check.Creation != current.Creation) return false;
                    string delta = runtime.LogSince(offset);
                    if (delta == null || delta.Contains("SetPerformanceMode failed") || delta.Contains("SetPerformanceMode cancelled") || delta.Contains(": Reset Mode =")) return false;
                    bool acknowledged = delta.Contains(": Mode = " + destination.Base + " : OK");
                    if (acknowledged && acknowledgedAt < 0) acknowledgedAt = attempt;
                    if (check.Mode != mode && check.Mode != current.Mode) return false;
                    // G-Helper applies fans and power after the base mode; wait for settlement while checking cancellation and user changes.
                    if (check.Mode == mode && acknowledged && attempt - acknowledgedAt >= 13)
                    {
                        observed = check;
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        internal static string UnavailableDetail()
        {
            uint raw;
            if (TryReadCapability(out raw))
                return Text("已识别 ASUS 控制接口（DSTS=0x" + raw.ToString("X8") + "）；该值不能核验当前档位。请保持 G-Helper 运行并使用可恢复的前五个配置档，未进行盲切。",
                    "ASUS interface detected (DSTS=0x" + raw.ToString("X8") + "); this value cannot verify the current profile. Keep G-Helper running with one of its first five restorable profiles; no blind switch was made.");
            return Text("未核验到可恢复的联想 SmartFan / ASUS G-Helper 控制路径；请检查厂商驱动与控制器。", "No restorable Lenovo SmartFan / ASUS G-Helper control path was verified; check the vendor driver and controller.");
        }

        internal static bool TryReadCapability(out uint raw)
        {
            raw = 0;
            using (SafeFileHandle handle = CreateFile(@"\\.\ATKACPI", 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero))
            {
                if (handle.IsInvalid) return false;
                byte[] input = new byte[16], output = new byte[16];
                BitConverter.GetBytes(0x53545344u).CopyTo(input, 0);
                BitConverter.GetBytes(8u).CopyTo(input, 4);
                BitConverter.GetBytes(0x00120075u).CopyTo(input, 8);
                uint count;
                if (!DeviceIoControl(handle, 0x0022240C, input, 16, output, 16, out count, IntPtr.Zero) || count < 4) return false;
                raw = BitConverter.ToUInt32(output, 0);
                return (raw & 0x10000) != 0 && (raw & 0x80000000) == 0;
            }
        }

        private sealed class ControllerRuntime : IRuntime
        {
            private static DateTime capabilityExpires;
            private static bool capabilityAvailable;
            private static string Folder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GHelper"); } }
            public bool Read(out string configuration, out int processId, out long creation)
            {
                configuration = null; processId = 0; creation = 0;
                if (DateTime.UtcNow >= capabilityExpires)
                {
                    uint raw;
                    capabilityAvailable = TryReadCapability(out raw);
                    capabilityExpires = DateTime.UtcNow.AddSeconds(30);
                }
                if (!capabilityAvailable) return false;
                Process[] processes = Process.GetProcessesByName("GHelper");
                try
                {
                    int session = Process.GetCurrentProcess().SessionId;
                    foreach (Process process in processes)
                    {
                        if (process.SessionId != session) continue;
                        if (processId != 0) return false;
                        processId = process.Id; creation = process.StartTime.ToUniversalTime().Ticks;
                    }
                    if (processId == 0 || Receivers(processId).Count == 0) return false;
                    string path = Path.Combine(Folder, "config.json");
                    if (new FileInfo(path).Length > 262144) return false;
                    configuration = File.ReadAllText(path, Encoding.UTF8);
                    return true;
                }
                finally { foreach (Process process in processes) process.Dispose(); }
            }

            internal static List<IntPtr> Receivers(int pid)
            {
                var found = new List<IntPtr>();
                EnumWindows(delegate(IntPtr window, IntPtr unused)
                {
                    uint owner; GetWindowThreadProcessId(window, out owner);
                    if (owner != pid || IsWindowVisible(window) || GetWindowTextLength(window) != 0) return true;
                    var name = new StringBuilder(256);
                    GetClassName(window, name, name.Capacity);
                    // NativeWindow minimum-frame sizing can make the receiver nonzero-sized; only KeyboardHook.Window handles WM_HOTKEY.
                    if (name.ToString().StartsWith("WindowsForms10.Window.0.app.", StringComparison.Ordinal)) found.Add(window);
                    return true;
                }, IntPtr.Zero);
                if (found.Count > 4) found.Clear();
                return found;
            }

            public bool Send(int processId, long creation, int key, int modifiers)
            {
                // Send WM_HOTKEY only to G-Helper's dedicated hidden receiver.
                using (Process process = Process.GetProcessById(processId))
                {
                    if (process.StartTime.ToUniversalTime().Ticks != creation || process.SessionId != Process.GetCurrentProcess().SessionId) return false;
                    List<IntPtr> receivers = Receivers(processId);
                    bool sent = false;
                    foreach (IntPtr receiver in receivers)
                    {
                        uint owner; GetWindowThreadProcessId(receiver, out owner);
                        if (owner != processId) return false;
                        IntPtr result;
                        sent |= SendMessageTimeout(receiver, 0x0312, IntPtr.Zero, new IntPtr((key << 16) | modifiers), 2, 1500, out result) != IntPtr.Zero;
                    }
                    return sent;
                }
            }
            public long LogLength() { return new FileInfo(Path.Combine(Folder, "log.txt")).Length; }
            public string LogSince(long offset)
            {
                using (var stream = new FileStream(Path.Combine(Folder, "log.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length < offset || stream.Length - offset > 65536) return null;
                    stream.Position = offset;
                    using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
                }
            }
            public void Pause() { Thread.Sleep(100); }
        }

        private delegate bool EnumWindow(IntPtr window, IntPtr value);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, IntPtr value);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wparam, IntPtr lparam, uint flags, uint timeout, out IntPtr result);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputLength, byte[] output, uint outputLength, out uint count, IntPtr overlapped);
    }
}
