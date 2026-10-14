using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;

namespace PaviseApp
{
    internal sealed class MonitoringRtssHardwareSensors : IDisposable
    {
        internal const int SharedMemorySize = 1048576, MutexWaitMilliseconds = 20, TransientGraceMilliseconds = 2000;
        internal const string SharedMemoryName = "LHMDPSharedMemory";
        internal const string SharedMutexName = "Global\\Access_LHMDPSharedMemory";
        internal const MonitoringMetrics HardwareMetrics = MonitoringMetrics.CpuPower | MonitoringMetrics.CpuTemperature
            | MonitoringMetrics.GpuUsage | MonitoringMetrics.GpuTemperature | MonitoringMetrics.GpuClock
            | MonitoringMetrics.GpuMemoryClock | MonitoringMetrics.GpuPower | MonitoringMetrics.GpuFan
            | MonitoringMetrics.Vram | MonitoringMetrics.VramUsage | MonitoringMetrics.Ram | MonitoringMetrics.RamUsage;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenFileMapping(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);
        [DllImport("kernel32.dll")]
        private static extern bool UnmapViewOfFile(IntPtr view);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateMutex(IntPtr attributes, bool owner, string name);
        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll")]
        private static extern bool ReleaseMutex(IntPtr mutex);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        private readonly Func<bool> providerAlive;
        private readonly Func<long> providerGeneration;
        private readonly string mappingName, mutexName;
        private string verifiedGpuName;
        private readonly byte[] buffer = new byte[SharedMemorySize];
        private static readonly MonitoringSensorReading EmptyReading = new MonitoringSensorReading();
        private IntPtr mapping, view, mutex;
        private bool disposed;
        private MonitoringSensorReading lastReading;
        private MonitoringMetrics lastMetrics;
        private long lastSuccessfulRead, sourceGeneration;
        private bool hasSourceGeneration;
        internal string LastReadStatus { get; private set; }
        internal void SetGpuRoute(string name)
        {
            if (verifiedGpuName == name) return;
            verifiedGpuName = name;
            ClearRecent();
        }
#if PAVISE_RTSS_HARDWARE_CHECKS
        internal Action<string> ExportCopyForTest;
        internal Func<long> ClockForTest;
#endif

        internal MonitoringRtssHardwareSensors(Func<bool> alive)
            : this(alive, null, SharedMemoryName, SharedMutexName, null) { }
        internal MonitoringRtssHardwareSensors(Func<bool> alive, string renderGpuName)
            : this(alive, renderGpuName, SharedMemoryName, SharedMutexName, null) { }
        internal MonitoringRtssHardwareSensors(Func<bool> alive, string renderGpuName, Func<long> generation)
            : this(alive, renderGpuName, SharedMemoryName, SharedMutexName, generation) { }
        internal MonitoringRtssHardwareSensors(Func<bool> alive, string memoryName, string synchronizationName)
            : this(alive, null, memoryName, synchronizationName, null) { }
        internal MonitoringRtssHardwareSensors(Func<bool> alive, string memoryName, string synchronizationName, Func<long> generation)
            : this(alive, null, memoryName, synchronizationName, generation) { }
        private MonitoringRtssHardwareSensors(Func<bool> alive, string renderGpuName, string memoryName, string synchronizationName, Func<long> generation)
        {
            providerAlive = alive; providerGeneration = generation;
            mappingName = memoryName; mutexName = synchronizationName;
            verifiedGpuName = renderGpuName == null ? null : renderGpuName.Trim();
            LastReadStatus = "idle";
        }

        internal bool Read(MonitoringMetrics selected, MonitoringSensorReading destination)
        {
            if (disposed || destination == null) { LastReadStatus = "closed"; return false; }
            if ((selected & HardwareMetrics) == 0) { Reset(); LastReadStatus = "idle"; return true; }
            Copy(selected, EmptyReading, destination);
            try
            {
                if (providerAlive == null || !providerAlive()) { Reset(); LastReadStatus = "provider-unresponsive"; return false; }
                long generation = SourceGeneration();
                if (hasSourceGeneration && generation != sourceGeneration) Reset();
                sourceGeneration = generation; hasSourceGeneration = true;
                if (!Open()) { LastReadStatus = "mapping-unavailable"; return CopyRecent(selected, destination); }
                string xml; bool transient;
                if (!ReadXml(out xml, out transient))
                {
                    LastReadStatus = "export-unavailable";
                    if (transient) return CopyRecent(selected, destination);
                    Reset(); return false;
                }
#if PAVISE_RTSS_HARDWARE_CHECKS
                if (ExportCopyForTest != null) ExportCopyForTest(xml);
#endif
                MonitoringSensorReading values;
                if (!TryParse(xml, selected, verifiedGpuName, out values)) { Reset(); LastReadStatus = "xml-invalid"; return false; }
                if (!providerAlive()) { Reset(); LastReadStatus = "provider-unresponsive"; return false; }
                if (generation != SourceGeneration()) { Reset(); LastReadStatus = "provider-changed"; return false; }
                Copy(selected, values, destination);
                lastReading = values; lastMetrics = selected & HardwareMetrics;
                lastSuccessfulRead = ClockMilliseconds();
                LastReadStatus = "ready";
                return true;
            }
            catch { Reset(); LastReadStatus = "read-error"; return false; }
        }

        private long ClockMilliseconds()
        {
#if PAVISE_RTSS_HARDWARE_CHECKS
            if (ClockForTest != null) return ClockForTest();
#endif
            return (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency));
        }
        private long SourceGeneration()
        { return providerGeneration == null ? 0 : providerGeneration(); }
        private bool CopyRecent(MonitoringMetrics selected, MonitoringSensorReading destination)
        {
            if (lastReading == null) return false;
            long elapsed = ClockMilliseconds() - lastSuccessfulRead;
            if (elapsed < 0 || elapsed >= TransientGraceMilliseconds)
            { ClearRecent(); return false; }
            MonitoringMetrics available = selected & lastMetrics;
            if (available == 0) return false;
            // 仅短暂传输失败可复用读数；成功导出中的缺失值不补旧数据。
            if (providerAlive == null || !providerAlive())
            { Reset(); LastReadStatus = "provider-unresponsive"; return false; }
            if (sourceGeneration != SourceGeneration())
            { Reset(); LastReadStatus = "provider-changed"; return false; }
            Copy(available, lastReading, destination);
            LastReadStatus = "refresh-pending";
            return true;
        }
        private void ClearRecent()
        { lastReading = null; lastMetrics = 0; lastSuccessfulRead = 0; }
        private bool Open()
        {
            if (view != IntPtr.Zero && mutex != IntPtr.Zero) return true;
            CloseConnection();
            mapping = OpenFileMapping(4, false, mappingName);
            if (mapping == IntPtr.Zero) return false;
            view = MapViewOfFile(mapping, 4, 0, 0, new UIntPtr(SharedMemorySize));
            // 保留同名互斥体，使每次导出写入都参与同步。
            if (view != IntPtr.Zero) mutex = CreateMutex(IntPtr.Zero, false, mutexName);
            if (view != IntPtr.Zero && mutex != IntPtr.Zero) return true;
            CloseConnection(); return false;
        }

        private unsafe bool ReadXml(out string xml, out bool transient)
        {
            xml = null; transient = false;
            uint wait = WaitForSingleObject(mutex, MutexWaitMilliseconds);
            if (wait != 0 && wait != 0x80) { transient = wait == 0x102; return false; }
            try
            {
                if (wait == 0x80) return false; // 写入者异常退出可能留下不完整导出。
                byte* data = (byte*)view.ToPointer();
                int length = 0;
                while (length < SharedMemorySize && data[length] != 0) length++;
                if (length == 0 || length == SharedMemorySize) return false;
                Marshal.Copy(view, buffer, 0, length);
                // Importer.dll 按 Windows ANSI 编码导出文本。
                xml = Encoding.Default.GetString(buffer, 0, length);
                return true;
            }
            finally { ReleaseMutex(mutex); }
        }

        internal void Reset()
        { ClearRecent(); hasSourceGeneration = false; CloseConnection(); }
        private void CloseConnection()
        {
            if (view != IntPtr.Zero) { UnmapViewOfFile(view); view = IntPtr.Zero; }
            if (mapping != IntPtr.Zero) { CloseHandle(mapping); mapping = IntPtr.Zero; }
            if (mutex != IntPtr.Zero) { CloseHandle(mutex); mutex = IntPtr.Zero; }
        }
        public void Dispose() { if (!disposed) { disposed = true; Reset(); } }

        private sealed class Hardware
        {
            internal string Id, Name, Type;
            internal readonly List<Sensor> Sensors = new List<Sensor>();
        }
        private sealed class Sensor
        {
            internal string Id, Name, Type;
            internal double? Value;
        }

        // 导出没有时间戳，必须同时验证采集器存活。
        internal static bool TryParse(string xml, MonitoringMetrics selected, out MonitoringSensorReading reading)
        { return TryParse(xml, selected, null, out reading); }
        internal static bool TryParse(string xml, MonitoringMetrics selected, string renderGpuName,
            out MonitoringSensorReading reading)
        {
            reading = new MonitoringSensorReading();
            if (String.IsNullOrWhiteSpace(xml) || xml.Length >= SharedMemorySize) return false;
            try
            {
                var settings = new XmlReaderSettings {
                    ConformanceLevel = ConformanceLevel.Fragment,
                    DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                    MaxCharactersInDocument = SharedMemorySize, MaxCharactersFromEntities = 0,
                    IgnoreComments = true, IgnoreWhitespace = true, IgnoreProcessingInstructions = true
                };
                var devices = new List<Hardware>();
                using (var input = new StringReader(xml))
                using (var reader = XmlReader.Create(input, settings))
                {
                    while (reader.Read())
                    {
                        if (reader.NodeType != XmlNodeType.Element) continue;
                        if (reader.Depth != 0 || reader.Name != "hardware" || reader.NamespaceURI.Length != 0 || reader.HasAttributes) return false;
                        var document = new XmlDocument { XmlResolver = null };
                        using (var subtree = reader.ReadSubtree()) document.Load(subtree);
                        Hardware device;
                        if (!ReadHardware(document.DocumentElement, out device)) return false;
                        devices.Add(device);
                        if (devices.Count > 128) return false;
                    }
                }
                if (devices.Count == 0) return false;
                Hardware cpu = Unique(devices, 0), gpu = SelectGpu(devices, renderGpuName), memory = Unique(devices, 2);
                if ((selected & MonitoringMetrics.CpuPower) != 0 && cpu != null)
                {
                    string name = cpu.Id.StartsWith("/intelcpu/", StringComparison.Ordinal) ? "CPU Package" : "Package";
                    // 整包零功耗可能表示 MSR 读取失败。
                    reading.CpuPower = Value(cpu, "Power", name, 0.001, 5000);
                }
                if ((selected & MonitoringMetrics.CpuTemperature) != 0 && cpu != null)
                    reading.CpuTemperature = CpuTemperature(cpu);
                reading.GpuSourceMatched = gpu != null;
                if (gpu != null) ReadGpu(gpu, selected, reading);
                if (memory != null)
                {
                    if ((selected & MonitoringMetrics.Ram) != 0)
                    {
                        double? used = Value(memory, "Data", "Memory Used", 0, 1048576);
                        reading.RamMiB = used.HasValue ? (double?)(used.Value * 1024) : null;
                    }
                    if ((selected & MonitoringMetrics.RamUsage) != 0) reading.RamUsage = Value(memory, "Load", "Memory", 0, 100);
                }
                return true;
            }
            catch (XmlException) { return false; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        private static bool ReadHardware(XmlElement element, out Hardware hardware)
        {
            hardware = null;
            if (element == null) return false;
            string id = Field(element, "id"), name = Field(element, "name"), type = Field(element, "type");
            if (id == null || name == null || type == null || id.Length > 256 || name.Length > 256 || type.Length > 32) return false;
            var device = new Hardware { Id = id, Name = name, Type = type };
            var identifiers = new Dictionary<string, Sensor>(StringComparer.Ordinal);
            foreach (XmlNode node in element.ChildNodes)
            {
                var sensor = node as XmlElement;
                if (sensor == null || sensor.Name != "sensor") continue;
                if (sensor.NamespaceURI.Length != 0 || sensor.HasAttributes) return false;
                string sensorId = Field(sensor, "id"), sensorName = Field(sensor, "name"), sensorType = Field(sensor, "type"), text = Field(sensor, "value");
                if (sensorId == null || sensorName == null || sensorType == null || text == null
                    || sensorId.Length > 512 || sensorName.Length > 128 || sensorType.Length > 32 || text.Length > 64) return false;
                if (!SensorId(id, sensorId, sensorType)) continue;
                double number;
                double? value = null;
                if (!(text.IndexOf(',') >= 0 && text.IndexOf('.') >= 0)
                    && Double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.AllowLeadingSign
                        | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out number)
                    && MonitoringFrameWindow.Finite(number)) value = number;
                var parsed = new Sensor { Id = sensorId, Name = sensorName, Type = sensorType, Value = value };
                Sensor duplicate;
                if (identifiers.TryGetValue(sensorId, out duplicate))
                {
                    // 重复传感器 ID 只使该通道失效。
                    duplicate.Value = null; parsed.Value = null;
                }
                else identifiers.Add(sensorId, parsed);
                device.Sensors.Add(parsed);
                if (device.Sensors.Count > 4096) return false;
            }
            hardware = device; return true;
        }
        private static string Field(XmlElement owner, string name)
        {
            string result = null;
            foreach (XmlNode child in owner.ChildNodes)
            {
                var element = child as XmlElement;
                if (element == null || element.Name != name) continue;
                if (result != null || element.NamespaceURI.Length != 0 || element.HasAttributes) return null;
                foreach (XmlNode value in element.ChildNodes)
                    if (value.NodeType != XmlNodeType.Text && value.NodeType != XmlNodeType.Whitespace && value.NodeType != XmlNodeType.SignificantWhitespace) return null;
                result = element.InnerText;
            }
            return result;
        }
        private static bool SensorId(string hardwareId, string sensorId, string type)
        {
            string prefix = hardwareId + "/" + type.ToLowerInvariant() + "/";
            return sensorId.StartsWith(prefix, StringComparison.Ordinal) && Index(sensorId.Substring(prefix.Length));
        }
        private static bool Index(string value)
        {
            if (value.Length == 0 || value.Length > 9) return false;
            for (int i = 0; i < value.Length; i++) if (value[i] < '0' || value[i] > '9') return false;
            return true;
        }
        private static bool DeviceId(string id, string kind)
        { return id.StartsWith(kind, StringComparison.Ordinal) && Index(id.Substring(kind.Length)); }
        private static Hardware Unique(List<Hardware> devices, int kind)
        {
            Hardware result = null; int count = 0;
            foreach (Hardware device in devices)
            {
                bool matches = kind == 0 ? device.Type == "Cpu" && (DeviceId(device.Id, "/intelcpu/") || DeviceId(device.Id, "/amdcpu/"))
                    : kind == 1 ? device.Type.StartsWith("Gpu", StringComparison.Ordinal)
                    : device.Type == "Memory" && device.Id == "/ram";
                if (!matches) continue;
                result = device; count++;
            }
            if (count != 1) return null;
            if (kind == 1 && !SupportedGpu(result)) return null;
            return result;
        }
        private static bool SupportedGpu(Hardware device)
        {
            return device.Type == "GpuNvidia" && DeviceId(device.Id, "/gpu-nvidia/")
                || device.Type == "GpuAmd" && DeviceId(device.Id, "/gpu-amd/")
                || device.Type == "GpuIntel" && (EncodedDeviceId(device.Id, "/gpu-intel-integrated/")
                    || EncodedDeviceId(device.Id, "/gpu-intel/"));
        }
        private static bool EncodedDeviceId(string id, string prefix)
        {
            // Intel 传感器标识使用转义设备路径，不能按数字索引解析。
            if (!id.StartsWith(prefix, StringComparison.Ordinal) || id.Length == prefix.Length) return false;
            for (int i = prefix.Length; i < id.Length; i++)
            {
                char c = id[i];
                if (c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9'
                    || c == '-' || c == '_' || c == '.' || c == '~') continue;
                if (c != '%' || i + 2 >= id.Length || !Uri.IsHexDigit(id[i + 1]) || !Uri.IsHexDigit(id[i + 2])) return false;
                i += 2;
            }
            return true;
        }
        private static Hardware SelectGpu(List<Hardware> devices, string renderGpuName)
        {
            if (renderGpuName == null) return Unique(devices, 1);
            Hardware result = null; int count = 0;
            string expected = renderGpuName.Trim();
            if (expected.Length == 0) return null;
            foreach (Hardware device in devices)
            {
                if (!SupportedGpu(device)) continue;
                if (!String.Equals(device.Name.Trim(), expected, StringComparison.OrdinalIgnoreCase)) continue;
                result = device; count++;
            }
            // 传感器名称需唯一匹配游戏实际渲染显卡。
            return count == 1 ? result : null;
        }
        private static double? Value(Hardware device, string type, string name, double minimum, double maximum)
        {
            double? result = null; int count = 0;
            foreach (Sensor sensor in device.Sensors)
                if (sensor.Type == type && sensor.Name == name)
                { count++; result = MonitoringFrameWindow.Valid(sensor.Value, minimum, maximum); }
            return count == 1 ? result : null;
        }
        private static double? CpuTemperature(Hardware cpu)
        {
            if (cpu.Id.StartsWith("/intelcpu/", StringComparison.Ordinal))
                return Value(cpu, "Temperature", "CPU Package", 1, 150);
            // 优先 Tdie，避免将带偏移的 Tctl 当作实际温度。
            foreach (Sensor sensor in cpu.Sensors)
                if (sensor.Type == "Temperature" && sensor.Name == "Core (Tdie)")
                    return Value(cpu, "Temperature", "Core (Tdie)", 1, 150);
            // 传感器索引随版本变化，按所属设备和通道名称匹配。
            return Value(cpu, "Temperature", "Core (Tctl/Tdie)", 1, 150);
        }
        private static void ReadGpu(Hardware gpu, MonitoringMetrics selected, MonitoringSensorReading reading)
        {
            if ((selected & MonitoringMetrics.GpuUsage) != 0) reading.GpuUsage = GpuUsage(gpu);
            if ((selected & MonitoringMetrics.GpuTemperature) != 0) reading.GpuTemperature = Value(gpu, "Temperature", "GPU Core", 1, 150);
            if ((selected & MonitoringMetrics.GpuClock) != 0) reading.GpuClock = Value(gpu, "Clock", "GPU Core", 0, 100000);
            if ((selected & MonitoringMetrics.GpuMemoryClock) != 0) reading.GpuMemoryClock = Value(gpu, "Clock", "GPU Memory", 0, 100000);
            if ((selected & MonitoringMetrics.GpuPower) != 0)
            {
                reading.GpuPower = Value(gpu, "Power", "GPU Package", 0.001, 5000);
                // 核显功耗仅取图形域，不能用 CPU 整包或 SoC 功耗代替。
                if (!reading.GpuPower.HasValue && gpu.Type == "GpuIntel"
                    && gpu.Id.StartsWith("/gpu-intel-integrated/", StringComparison.Ordinal))
                    reading.GpuPower = Value(gpu, "Power", "GPU Power", 0, 5000);
                if (!reading.GpuPower.HasValue && gpu.Type == "GpuAmd")
                    reading.GpuPower = Value(gpu, "Power", "GPU Core", 0, 5000);
            }
            if ((selected & MonitoringMetrics.GpuFan) != 0)
            {
                reading.GpuFanRpm = Fan(gpu, "Fan", 100000);
                if (!reading.GpuFanRpm.HasValue) reading.GpuFanPercent = Fan(gpu, "Control", 500);
            }
            if ((selected & (MonitoringMetrics.Vram | MonitoringMetrics.VramUsage)) != 0)
            {
                double? used = Value(gpu, "SmallData", "GPU Memory Used", 0, 1048576);
                double? total = Value(gpu, "SmallData", "GPU Memory Total", 1, 1048576);
                if (!used.HasValue)
                {
                    used = Value(gpu, "SmallData", "D3D Dedicated Memory Used", 0, 1048576);
                    total = Value(gpu, "SmallData", "D3D Dedicated Memory Total", 1, 1048576);
                }
                if (used.HasValue && (!total.HasValue || used.Value <= total.Value))
                {
                    if ((selected & MonitoringMetrics.Vram) != 0) reading.VramMiB = used;
                    if ((selected & MonitoringMetrics.VramUsage) != 0 && total.HasValue)
                        reading.VramUsage = used.Value * 100 / total.Value;
                }
            }
        }
        private static double? GpuUsage(Hardware gpu)
        {
            double? result = Value(gpu, "Load", "GPU Core", 0, 100);
            if (result.HasValue) return result;
            // 缺少核心占用率时取最忙的 3D 引擎，不累加多个引擎。
            foreach (Sensor sensor in gpu.Sensors)
            {
                if (sensor.Type != "Load" || sensor.Name != "D3D 3D") continue;
                double? value = MonitoringFrameWindow.Valid(sensor.Value, 0, 100);
                if (value.HasValue && (!result.HasValue || value.Value > result.Value)) result = value;
            }
            return result;
        }
        private static double? Fan(Hardware gpu, string type, double maximum)
        {
            string[] names = { "GPU Fan 1", "GPU Fan", "GPU" };
            foreach (string name in names)
            {
                double? value = Value(gpu, type, name, 0, maximum);
                if (value.HasValue) return value;
            }
            return null;
        }
        private static void Copy(MonitoringMetrics selected, MonitoringSensorReading source, MonitoringSensorReading target)
        {
            if ((selected & MonitoringNativeSensors.GpuMetrics) != 0) target.GpuSourceMatched = source.GpuSourceMatched;
            if ((selected & MonitoringMetrics.CpuPower) != 0) target.CpuPower = source.CpuPower;
            if ((selected & MonitoringMetrics.CpuTemperature) != 0) target.CpuTemperature = source.CpuTemperature;
            if ((selected & MonitoringMetrics.GpuUsage) != 0) target.GpuUsage = source.GpuUsage;
            if ((selected & MonitoringMetrics.GpuTemperature) != 0) target.GpuTemperature = source.GpuTemperature;
            if ((selected & MonitoringMetrics.GpuClock) != 0) target.GpuClock = source.GpuClock;
            if ((selected & MonitoringMetrics.GpuMemoryClock) != 0) target.GpuMemoryClock = source.GpuMemoryClock;
            if ((selected & MonitoringMetrics.GpuPower) != 0) target.GpuPower = source.GpuPower;
            if ((selected & MonitoringMetrics.GpuFan) != 0) { target.GpuFanRpm = source.GpuFanRpm; target.GpuFanPercent = source.GpuFanPercent; }
            if ((selected & MonitoringMetrics.Vram) != 0) target.VramMiB = source.VramMiB;
            if ((selected & MonitoringMetrics.VramUsage) != 0) target.VramUsage = source.VramUsage;
            if ((selected & MonitoringMetrics.Ram) != 0) target.RamMiB = source.RamMiB;
            if ((selected & MonitoringMetrics.RamUsage) != 0) target.RamUsage = source.RamUsage;
        }
    }
}
