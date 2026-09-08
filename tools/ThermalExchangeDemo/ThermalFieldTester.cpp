// 文件用途 Pavise Thermal Exchange 的一键现场测试器
// 这个编译单元复用隔离的控制器 加上 Win32 界面 一套抗漂移的固定协议
// 机器元数据和自动判定

#define wmain ThermalExchangeCommandMain
#include "ThermalExchangeDemo.cpp"
#undef wmain

#include <commctrl.h>
#include <psapi.h>
#include <shellapi.h>

#include <map>

#ifdef _MSC_VER
#pragma comment(lib, "comctl32.lib")
#pragma comment(lib, "psapi.lib")
#pragma comment(lib, "shell32.lib")
#endif

namespace field {

constexpr UINT kMessageLog = WM_APP + 1;
constexpr UINT kMessageProgress = WM_APP + 2;
constexpr UINT kMessageFinished = WM_APP + 3;
constexpr int kIdProcess = 1001;
constexpr int kIdRefresh = 1002;
constexpr int kIdStart = 1003;
constexpr int kIdCancel = 1004;
constexpr int kIdOpenResults = 1005;
constexpr int kIdProgress = 1006;
constexpr int kIdStatus = 1007;
constexpr int kIdLog = 1008;
constexpr int kPreheatSeconds = 30;
constexpr int kSettleSeconds = 5;
constexpr int kMeasureSeconds = 20;
const std::vector<int> kProtocol{100, 95, 100, 90, 100, 85, 100, 85, 100, 90, 100, 95, 100};

struct Candidate {
    HWND window = nullptr;
    DWORD pid = 0;
    std::wstring exe;
    std::wstring title;
    long long area = 0;
};

struct AppState {
    HWND window = nullptr;
    HWND process = nullptr;
    HWND refresh = nullptr;
    HWND start = nullptr;
    HWND cancel = nullptr;
    HWND openResults = nullptr;
    HWND progress = nullptr;
    HWND status = nullptr;
    HWND log = nullptr;
    HFONT titleFont = nullptr;
    HFONT bodyFont = nullptr;
    std::vector<Candidate> candidates;
    HANDLE worker = nullptr;
    bool running = false;
    bool closeWhenDone = false;
    std::wstring resultDirectory;
};

AppState g_app;

std::wstring BaseName(const std::wstring& path) {
    size_t slash = path.find_last_of(L"\\/");
    return slash == std::wstring::npos ? path : path.substr(slash + 1);
}

std::wstring ExeDirectory() {
    std::wstring path = ExePath();
    size_t slash = path.find_last_of(L"\\/");
    return slash == std::wstring::npos ? L"." : path.substr(0, slash);
}

std::wstring TimestampText() {
    SYSTEMTIME time = {};
    GetLocalTime(&time);
    wchar_t text[64] = {};
    swprintf_s(text, L"%04u%02u%02u-%02u%02u%02u", time.wYear, time.wMonth, time.wDay,
               time.wHour, time.wMinute, time.wSecond);
    return text;
}

std::wstring ReadRegistryString(HKEY root, const wchar_t* path, const wchar_t* name) {
    wchar_t value[512] = {};
    DWORD type = 0;
    DWORD bytes = sizeof(value);
    if (RegGetValueW(root, path, name, RRF_RT_REG_SZ, &type, value, &bytes) != ERROR_SUCCESS) return L"未知";
    return value;
}

std::wstring ProcessorName() {
    return ReadRegistryString(HKEY_LOCAL_MACHINE,
        L"HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0", L"ProcessorNameString");
}

std::wstring MachineModel() {
    std::wstring maker = ReadRegistryString(HKEY_LOCAL_MACHINE,
        L"HARDWARE\\DESCRIPTION\\System\\BIOS", L"SystemManufacturer");
    std::wstring model = ReadRegistryString(HKEY_LOCAL_MACHINE,
        L"HARDWARE\\DESCRIPTION\\System\\BIOS", L"SystemProductName");
    return maker + L" " + model;
}

std::wstring DisplayAdapters() {
    std::wstring result;
    DISPLAY_DEVICEW display = {};
    display.cb = sizeof(display);
    for (DWORD index = 0; EnumDisplayDevicesW(nullptr, index, &display, 0); ++index) {
        if (!(display.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) && display.DeviceString[0]) {
            if (!result.empty()) result += L" | ";
            result += display.DeviceString;
            const std::wstring prefix = L"\\Registry\\Machine\\";
            std::wstring key = display.DeviceKey;
            if (_wcsnicmp(key.c_str(), prefix.c_str(), prefix.size()) == 0) {
                key = key.substr(prefix.size());
                std::wstring version = ReadRegistryString(HKEY_LOCAL_MACHINE, key.c_str(), L"DriverVersion");
                std::wstring date = ReadRegistryString(HKEY_LOCAL_MACHINE, key.c_str(), L"DriverDate");
                if (version != L"未知") result += L" [驱动 " + version + L" / " + date + L"]";
            }
        }
        ZeroMemory(&display, sizeof(display));
        display.cb = sizeof(display);
    }
    return result.empty() ? L"未知" : result;
}

std::wstring WindowsVersion() {
    const wchar_t* key = L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion";
    std::wstring product = ReadRegistryString(HKEY_LOCAL_MACHINE, key, L"ProductName");
    std::wstring display = ReadRegistryString(HKEY_LOCAL_MACHINE, key, L"DisplayVersion");
    std::wstring build = ReadRegistryString(HKEY_LOCAL_MACHINE, key, L"CurrentBuildNumber");
    DWORD ubr = 0, bytes = sizeof(ubr);
    RegGetValueW(HKEY_LOCAL_MACHINE, key, L"UBR", RRF_RT_REG_DWORD, nullptr, &ubr, &bytes);
    return product + L" " + display + L" (" + build + L"." + std::to_wstring(ubr) + L")";
}

std::wstring ActivePowerScheme() {
    GUID* active = nullptr;
    if (PowerGetActiveScheme(nullptr, &active) != ERROR_SUCCESS || !active) return L"未知";
    std::wstring result = GuidText(*active);
    LocalFree(active);
    return result;
}

std::wstring CoreCounts() {
    DWORD bytes = 0;
    GetLogicalProcessorInformationEx(RelationProcessorCore, nullptr, &bytes);
    std::vector<BYTE> buffer(bytes);
    DWORD physical = 0;
    if (bytes && GetLogicalProcessorInformationEx(RelationProcessorCore,
            reinterpret_cast<PSYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(buffer.data()), &bytes)) {
        BYTE* cursor = buffer.data();
        BYTE* end = cursor + bytes;
        while (cursor < end) {
            auto* item = reinterpret_cast<PSYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(cursor);
            if (item->Relationship == RelationProcessorCore) ++physical;
            if (!item->Size) break;
            cursor += item->Size;
        }
    }
    SYSTEM_INFO info = {};
    GetNativeSystemInfo(&info);
    return std::to_wstring(physical) + L" 物理核 / " + std::to_wstring(info.dwNumberOfProcessors) + L" 线程";
}

std::wstring PowerSource() {
    SYSTEM_POWER_STATUS status = {};
    if (!GetSystemPowerStatus(&status)) return L"未知";
    if (status.ACLineStatus == 1) return L"交流电源";
    if (status.ACLineStatus == 0) return L"电池";
    return L"未知";
}

bool IsElevated() {
    BOOL elevated = FALSE;
    HANDLE token = nullptr;
    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) {
        TOKEN_ELEVATION elevation = {};
        DWORD bytes = 0;
        if (GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation), &bytes))
            elevated = elevation.TokenIsElevated;
        CloseHandle(token);
    }
    return elevated != FALSE;
}

bool RelaunchElevated() {
    SHELLEXECUTEINFOW execute = {};
    execute.cbSize = sizeof(execute);
    execute.lpVerb = L"runas";
    std::wstring path = ExePath();
    execute.lpFile = path.c_str();
    execute.lpParameters = L"--ui";
    execute.nShow = SW_SHOWNORMAL;
    return ShellExecuteExW(&execute) != FALSE;
}

std::wstring ProcessImage(DWORD pid) {
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!process) return L"";
    std::vector<wchar_t> path(32768);
    DWORD length = static_cast<DWORD>(path.size());
    bool ok = QueryFullProcessImageNameW(process, 0, path.data(), &length) != FALSE;
    CloseHandle(process);
    return ok ? std::wstring(path.data(), length) : L"";
}

BOOL CALLBACK EnumWindowCandidate(HWND window, LPARAM parameter) {
    auto* values = reinterpret_cast<std::map<DWORD, Candidate>*>(parameter);
    if (!IsWindowVisible(window) || GetWindow(window, GW_OWNER)) return TRUE;
    LONG_PTR style = GetWindowLongPtrW(window, GWL_EXSTYLE);
    if (style & WS_EX_TOOLWINDOW) return TRUE;
    wchar_t title[512] = {};
    if (!GetWindowTextW(window, title, _countof(title)) || !title[0]) return TRUE;
    DWORD pid = 0;
    GetWindowThreadProcessId(window, &pid);
    if (!pid || pid == GetCurrentProcessId()) return TRUE;
    RECT rect = {};
    GetClientRect(window, &rect);
    long long area = static_cast<long long>(std::max(0L, rect.right - rect.left)) *
                     std::max(0L, rect.bottom - rect.top);
    if (area < 320 * 200) return TRUE;
    std::wstring path = ProcessImage(pid);
    if (path.empty()) return TRUE;
    Candidate candidate{window, pid, BaseName(path), title, area};
    auto found = values->find(pid);
    if (found == values->end() || candidate.area > found->second.area) (*values)[pid] = candidate;
    return TRUE;
}

void RefreshCandidates() {
    std::map<DWORD, Candidate> unique;
    EnumWindows(EnumWindowCandidate, reinterpret_cast<LPARAM>(&unique));
    g_app.candidates.clear();
    for (const auto& item : unique) g_app.candidates.push_back(item.second);
    std::sort(g_app.candidates.begin(), g_app.candidates.end(),
              [](const Candidate& a, const Candidate& b) { return a.area > b.area; });
    SendMessageW(g_app.process, CB_RESETCONTENT, 0, 0);
    for (const Candidate& candidate : g_app.candidates) {
        std::wstring label = candidate.exe + L"  (PID " + std::to_wstring(candidate.pid) + L")  —  " + candidate.title;
        SendMessageW(g_app.process, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(label.c_str()));
    }
    if (!g_app.candidates.empty()) SendMessageW(g_app.process, CB_SETCURSEL, 0, 0);
}

void PostLog(const std::wstring& text) {
    PostMessageW(g_app.window, kMessageLog, 0, reinterpret_cast<LPARAM>(new std::wstring(text)));
}

void PostProgress(int value, const std::wstring& text) {
    PostMessageW(g_app.window, kMessageProgress, static_cast<WPARAM>(value),
                 reinterpret_cast<LPARAM>(new std::wstring(text)));
}

void AppendLog(HWND edit, const std::wstring& text) {
    int length = GetWindowTextLengthW(edit);
    SendMessageW(edit, EM_SETSEL, length, length);
    std::wstring line = text + L"\r\n";
    SendMessageW(edit, EM_REPLACESEL, FALSE, reinterpret_cast<LPARAM>(line.c_str()));
    SendMessageW(edit, EM_SCROLLCARET, 0, 0);
}

double PairedDelta(const StageResult& treatment, const StageResult& left,
                   const StageResult& right, double StageResult::*field) {
    double value = treatment.*field;
    double baseline = ((left.*field) + (right.*field)) / 2.0;
    if (std::isnan(value) || std::isnan(baseline) || baseline == 0.0) return NAN;
    return (value / baseline - 1.0) * 100.0;
}

struct Verdict {
    bool valid = true;
    std::wstring title;
    std::vector<std::wstring> reasons;
};

Verdict Judge(const std::vector<StageResult>& stages, bool traceReady) {
    Verdict verdict;
    if (!traceReady) {
        verdict.valid = false;
        verdict.reasons.push_back(L"没有取得帧时间；本轮只能看遥测，不能判断性能收益。");
    }
    std::vector<double> baselineFps, gpuValues;
    for (const auto& stage : stages) {
        if (stage.cap == 100 && !std::isnan(stage.fps)) baselineFps.push_back(stage.fps);
        if (!std::isnan(stage.gpu)) gpuValues.push_back(stage.gpu);
    }
    double gpuMean = Mean(gpuValues);
    if (!std::isnan(gpuMean) && gpuMean < 80.0) {
        verdict.valid = false;
        verdict.reasons.push_back(L"GPU 3D 平均占用低于 80%，场景并非稳定的 GPU 瓶颈。");
    }
    if (baselineFps.size() >= 2) {
        double minimum = *std::min_element(baselineFps.begin(), baselineFps.end());
        double maximum = *std::max_element(baselineFps.begin(), baselineFps.end());
        double drift = minimum > 0.0 ? (maximum / minimum - 1.0) * 100.0 : 100.0;
        if (drift > 5.0) {
            verdict.valid = false;
            verdict.reasons.push_back(L"100% 基线最大漂移超过 5%，场景或温度不够稳定。");
        }
    }
    if (!verdict.valid) {
        verdict.title = L"数据无效，需要重测";
        return verdict;
    }

    int bestCap = 0;
    double bestFps = -1000.0;
    double bestLow = NAN;
    for (int cap : {95, 90, 85}) {
        std::vector<double> fpsDeltas, lowDeltas;
        for (size_t index = 1; index + 1 < stages.size(); ++index) {
            if (stages[index].cap != cap || stages[index - 1].cap != 100 || stages[index + 1].cap != 100) continue;
            double fps = PairedDelta(stages[index], stages[index - 1], stages[index + 1], &StageResult::fps);
            double low = PairedDelta(stages[index], stages[index - 1], stages[index + 1], &StageResult::oneLowFps);
            if (!std::isnan(fps)) fpsDeltas.push_back(fps);
            if (!std::isnan(low)) lowDeltas.push_back(low);
        }
        double fps = Mean(fpsDeltas);
        double low = Mean(lowDeltas);
        bool repeated = fpsDeltas.size() == 2 && lowDeltas.size() == 2 &&
                        *std::min_element(fpsDeltas.begin(), fpsDeltas.end()) >= 0.0 &&
                        *std::min_element(lowDeltas.begin(), lowDeltas.end()) >= -1.0;
        if (repeated && !std::isnan(fps) && fps > bestFps) {
            bestCap = cap; bestFps = fps; bestLow = low;
        }
    }
    if (bestCap && bestFps >= 1.0 && !std::isnan(bestLow) && bestLow >= 0.0) {
        verdict.title = L"发现候选收益，建议复验";
        verdict.reasons.push_back(std::to_wstring(bestCap) + L"% 档的配对平均 FPS 提升 " +
            std::to_wstring(bestFps).substr(0, 5) + L"%，且 1% Low 未下降。");
    } else {
        verdict.title = L"没有发现可重复收益";
        verdict.reasons.push_back(L"没有档位同时达到配对平均 FPS +1% 且 1% Low 不下降。");
    }
    return verdict;
}

void WriteNumber(std::wostream& out, double value, const wchar_t* suffix = L"") {
    if (std::isnan(value)) out << L"n/a";
    else out << std::fixed << std::setprecision(2) << value << suffix;
}

bool WriteReport(const std::wstring& path, const Candidate& target,
                 const std::vector<Row>& rows, const std::vector<StageResult>& stages,
                 bool traceReady, bool completed, const std::wstring& csvName, Verdict* outVerdict) {
    Verdict verdict = Judge(stages, traceReady);
    if (!completed) {
        verdict.valid = false;
        verdict.title = L"测试未完成";
        verdict.reasons.insert(verdict.reasons.begin(), L"测试被取消、目标进程退出或中途发生错误。");
    }
    if (outVerdict) *outVerdict = verdict;
    std::wostringstream out;
    out << L"Pavise Thermal Exchange 外场实验报告\n"
        << L"====================================\n\n"
        << L"结论: " << verdict.title << L"\n";
    for (const auto& reason : verdict.reasons) out << L"- " << reason << L"\n";
    out << L"\n机器\n"
        << L"- 测试器版本: FieldTest 1.0 / 协议 1\n"
        << L"- Windows: " << WindowsVersion() << L"\n"
        << L"- 型号: " << MachineModel() << L"\n"
        << L"- CPU: " << ProcessorName() << L"\n"
        << L"- 核心: " << CoreCounts() << L"\n"
        << L"- 显卡: " << DisplayAdapters() << L"\n"
        << L"- 供电: " << PowerSource() << L"\n"
        << L"- 测试结束时活动电源方案: " << ActivePowerScheme() << L"\n"
        << L"- 游戏进程: " << target.exe << L" (PID " << target.pid << L")\n"
        << L"- 窗口: " << target.title << L"\n"
        << L"- 协议: 预热 " << kPreheatSeconds << L" 秒；每档稳定 " << kSettleSeconds
        << L" 秒、测量 " << kMeasureSeconds << L" 秒\n"
        << L"- 顺序: 100/95/100/90/100/85/100/85/100/90/100/95/100\n"
        << L"- 原始数据: " << csvName << L"\n\n"
        << L"阶段汇总\n"
        << L"阶段  CPU上限  FPS      1%Low    CPU%     GPU3D%   GPU频率  功耗W    温度C\n";
    for (const auto& stage : stages) {
        out << std::setw(4) << stage.stage << L"  " << std::setw(7) << stage.cap << L"  ";
        WriteNumber(out, stage.fps); out << L"  "; WriteNumber(out, stage.oneLowFps); out << L"  ";
        WriteNumber(out, stage.cpu); out << L"  "; WriteNumber(out, stage.gpu); out << L"  ";
        WriteNumber(out, stage.clock); out << L"  "; WriteNumber(out, stage.power); out << L"  ";
        WriteNumber(out, stage.temp); out << L"\n";
    }
    out << L"\n配对差值（每个实验档与左右相邻 100% 基线均值相比）\n";
    for (size_t index = 1; index + 1 < stages.size(); ++index) {
        if (stages[index].cap == 100 || stages[index - 1].cap != 100 || stages[index + 1].cap != 100) continue;
        out << L"- 阶段 " << stages[index].stage << L" / " << stages[index].cap << L"%: FPS ";
        WriteNumber(out, PairedDelta(stages[index], stages[index - 1], stages[index + 1], &StageResult::fps), L"%");
        out << L"，1% Low ";
        WriteNumber(out, PairedDelta(stages[index], stages[index - 1], stages[index + 1], &StageResult::oneLowFps), L"%");
        out << L"，GPU 频率 ";
        WriteNumber(out, PairedDelta(stages[index], stages[index - 1], stages[index + 1], &StageResult::clock), L"%");
        out << L"\n";
    }
    out << L"\n判定门槛\n"
        << L"- 必须取得游戏帧时间。\n"
        << L"- GPU 3D 平均占用至少 80%。\n"
        << L"- 100% 基线最大漂移不超过 5%。\n"
        << L"- 候选收益需两次同档 FPS 都不下降、配对平均至少 +1%，两次 1% Low 均不得低于 -1%；仍需第二轮复验。\n"
        << L"\n安全边界: 程序不打开游戏进程、不注入 DLL、不安装驱动、不修改 GPU。"
           L"它只轮转复制出来的临时电源方案，结束或崩溃后由看门狗恢复原方案。\n";
    std::wstring wide = out.str();
    int bytes = WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), static_cast<int>(wide.size()),
                                    nullptr, 0, nullptr, nullptr);
    if (bytes <= 0) return false;
    std::string utf8(static_cast<size_t>(bytes), '\0');
    WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), static_cast<int>(wide.size()),
                        utf8.data(), bytes, nullptr, nullptr);
    std::ofstream file(path.c_str(), std::ios::binary | std::ios::trunc);
    if (!file) return false;
    const unsigned char bom[] = {0xEF, 0xBB, 0xBF};
    file.write(reinterpret_cast<const char*>(bom), sizeof(bom));
    file.write(utf8.data(), static_cast<std::streamsize>(utf8.size()));
    return file.good();
}

DWORD WINAPI RunExperiment(void* parameter) {
    Candidate target = *static_cast<Candidate*>(parameter);
    delete static_cast<Candidate*>(parameter);
    g_stop.store(false);
    std::wstring error;
    std::vector<Row> rows;
    std::vector<StageResult> stages;
    bool completed = false;
    bool traceReady = false;

    PostLog(L"目标: " + target.exe + L" (PID " + std::to_wstring(target.pid) + L")");
    PostLog(L"请不要切换场景、打开菜单或操作游戏。测试结束前保持相同画面。 ");
    if (PowerSource() == L"电池") {
        PostLog(L"错误: 当前使用电池供电。请接通电源后重试。");
        PostMessageW(g_app.window, kMessageFinished, 2, 0);
        return 2;
    }
    if (!RecoverStaleRun(&error)) {
        PostLog(L"恢复检查失败: " + error);
        PostMessageW(g_app.window, kMessageFinished, 2, 0);
        return 2;
    }
    PowerSandbox sandbox;
    if (!sandbox.Create(true, &error)) {
        PostLog(L"无法创建临时电源方案: " + error);
        PostMessageW(g_app.window, kMessageFinished, 3, 0);
        return 3;
    }
    SharedFrameTrace sharedTrace;
    bool sharedReady = sharedTrace.Start(target.pid);
    PresentTrace trace;
    traceReady = sharedReady || trace.Start(target.pid, &error);
    if (!traceReady) PostLog(L"警告: 无法取得帧时间: " + error);
    GpuPdhMeter gpu;
    bool gpuReady = gpu.Start();
    NvmlMeter nvml;
    std::wstring nvmlStatus;
    nvml.Start(&nvmlStatus);
    PostLog(nvmlStatus);
    CpuMeter cpu;
    cpu.Sample();
    LARGE_INTEGER frequency = {};
    QueryPerformanceFrequency(&frequency);

    if (!sandbox.SetCpuMaximum(100, &error)) {
        PostLog(L"设置 100% 基线失败: " + error);
    } else {
        PostLog(L"预热 30 秒，让温度和频率进入稳定区间……");
        for (int second = 0; second < kPreheatSeconds && !g_stop.load(); ++second) {
            if (!ProcessAlive(target.pid)) { g_stop.store(true); PostLog(L"游戏进程已退出。"); break; }
            Sleep(1000);
            PostProgress((second + 1) * 8 / kPreheatSeconds, L"预热中 " + std::to_wstring(second + 1) + L"/30 秒");
        }
    }

    for (size_t stageIndex = 0; stageIndex < kProtocol.size() && !g_stop.load(); ++stageIndex) {
        if (!ProcessAlive(target.pid)) { PostLog(L"游戏进程已退出，停止测试。"); break; }
        int cap = kProtocol[stageIndex];
        if (!sandbox.SetCpuMaximum(cap, &error)) { PostLog(L"设置 CPU 上限失败: " + error); break; }
        PostLog(L"阶段 " + std::to_wstring(stageIndex + 1) + L"/" + std::to_wstring(kProtocol.size()) +
                L"：CPU 上限 " + std::to_wstring(cap) + L"%");
        for (int second = 0; second < kSettleSeconds && !g_stop.load(); ++second) Sleep(1000);
        std::vector<LONGLONG> before = sharedReady ? sharedTrace.Snapshot() :
            (traceReady ? trace.Snapshot() : std::vector<LONGLONG>());
        size_t beginStamp = before.size();
        for (int second = 1; second <= kMeasureSeconds && !g_stop.load(); ++second) {
            Sleep(1000);
            if (!ProcessAlive(target.pid)) { g_stop.store(true); PostLog(L"游戏进程已退出。"); break; }
            NvmlMeter::Reading n = nvml.Sample();
            Row row;
            row.stage = static_cast<int>(stageIndex + 1); row.cap = cap; row.second = second;
            row.cpu = cpu.Sample(); row.gpu = gpuReady ? gpu.Sample(target.pid) : NAN;
            row.clock = n.clockMHz; row.power = n.powerW; row.temp = n.tempC;
            rows.push_back(row);
            int elapsed = static_cast<int>(stageIndex) * (kSettleSeconds + kMeasureSeconds) +
                          kSettleSeconds + second;
            int total = static_cast<int>(kProtocol.size()) * (kSettleSeconds + kMeasureSeconds);
            PostProgress(8 + elapsed * 90 / total,
                L"阶段 " + std::to_wstring(stageIndex + 1) + L"/" + std::to_wstring(kProtocol.size()) +
                L" · " + std::to_wstring(cap) + L"% · 测量 " + std::to_wstring(second) + L"/20 秒");
        }
        std::vector<LONGLONG> after = sharedReady ? sharedTrace.Snapshot() :
            (traceReady ? trace.Snapshot() : std::vector<LONGLONG>());
        StageResult result = SummarizeStage(static_cast<int>(stageIndex + 1), cap, rows, after,
                                            beginStamp, frequency);
        stages.push_back(result);
        std::wstringstream summary;
        summary << L"  FPS "; WriteNumber(summary, result.fps);
        summary << L" | 1% Low "; WriteNumber(summary, result.oneLowFps);
        summary << L" | GPU "; WriteNumber(summary, result.gpu, L"%");
        summary << L" | " ; WriteNumber(summary, result.clock, L" MHz");
        PostLog(summary.str());
    }
    completed = !g_stop.load() && stages.size() == kProtocol.size();
    trace.Stop();
    sandbox.Restore();
    PostLog(L"原电源计划已恢复，临时方案已删除。");

    std::wstring stamp = TimestampText();
    std::wstring resultDir = ExeDirectory() + L"\\Pavise-ThermalExchange-Results";
    if (!CreateDirectoryW(resultDir.c_str(), nullptr) && GetLastError() != ERROR_ALREADY_EXISTS) {
        wchar_t temporary[MAX_PATH] = {};
        GetTempPathW(_countof(temporary), temporary);
        resultDir = std::wstring(temporary) + L"Pavise-ThermalExchange-Results";
        CreateDirectoryW(resultDir.c_str(), nullptr);
    }
    std::wstring csvName = L"Pavise-ThermalExchange-" + stamp + L".csv";
    std::wstring reportName = L"Pavise-ThermalExchange-" + stamp + L"-报告.txt";
    std::wstring csvPath = resultDir + L"\\" + csvName;
    std::wstring reportPath = resultDir + L"\\" + reportName;
    WriteCsv(csvPath, rows, stages);
    Verdict verdict;
    WriteReport(reportPath, target, rows, stages, traceReady, completed, csvName, &verdict);
    g_app.resultDirectory = resultDir;
    PostProgress(100, completed ? verdict.title : L"测试未完成");
    PostLog(L"报告: " + reportPath);
    PostLog(L"原始数据: " + csvPath);
    PostMessageW(g_app.window, kMessageFinished, completed ? 0 : 1,
                 reinterpret_cast<LPARAM>(new std::wstring(verdict.title)));
    return completed ? 0 : 1;
}

void SetRunning(bool running) {
    g_app.running = running;
    EnableWindow(g_app.process, !running);
    EnableWindow(g_app.refresh, !running);
    EnableWindow(g_app.start, !running);
    EnableWindow(g_app.cancel, running);
    EnableWindow(g_app.openResults, !running && !g_app.resultDirectory.empty());
}

void Layout(HWND window) {
    RECT client = {};
    GetClientRect(window, &client);
    int width = client.right - client.left;
    MoveWindow(g_app.process, 24, 102, width - 168, 320, TRUE);
    MoveWindow(g_app.refresh, width - 132, 101, 108, 30, TRUE);
    MoveWindow(g_app.progress, 24, 174, width - 48, 20, TRUE);
    MoveWindow(g_app.status, 24, 200, width - 48, 24, TRUE);
    MoveWindow(g_app.log, 24, 232, width - 48, client.bottom - 308, TRUE);
    MoveWindow(g_app.start, 24, client.bottom - 58, 210, 34, TRUE);
    MoveWindow(g_app.cancel, 246, client.bottom - 58, 100, 34, TRUE);
    MoveWindow(g_app.openResults, width - 164, client.bottom - 58, 140, 34, TRUE);
}

LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
    switch (message) {
    case WM_CREATE: {
        g_app.window = window;
        g_app.bodyFont = CreateFontW(-18, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
            OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Microsoft YaHei UI");
        g_app.titleFont = CreateFontW(-26, 0, 0, 0, FW_BOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
            OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Microsoft YaHei UI");
        HWND title = CreateWindowW(L"STATIC", L"Pavise 跨芯片热预算外场测试", WS_CHILD | WS_VISIBLE,
            24, 18, 700, 34, window, nullptr, nullptr, nullptr);
        HWND help = CreateWindowW(L"STATIC", L"先启动游戏并进入可重复的 GPU 高负载场景，再选择真正负责渲染的进程。全程不要切换场景。",
            WS_CHILD | WS_VISIBLE, 24, 58, 850, 26, window, nullptr, nullptr, nullptr);
        g_app.process = CreateWindowW(WC_COMBOBOXW, L"", WS_CHILD | WS_VISIBLE | CBS_DROPDOWNLIST | WS_VSCROLL,
            24, 102, 650, 300, window, reinterpret_cast<HMENU>(kIdProcess), nullptr, nullptr);
        g_app.refresh = CreateWindowW(L"BUTTON", L"刷新进程", WS_CHILD | WS_VISIBLE,
            690, 101, 108, 30, window, reinterpret_cast<HMENU>(kIdRefresh), nullptr, nullptr);
        g_app.progress = CreateWindowW(PROGRESS_CLASSW, L"", WS_CHILD | WS_VISIBLE | PBS_SMOOTH,
            24, 174, 774, 20, window, reinterpret_cast<HMENU>(kIdProgress), nullptr, nullptr);
        SendMessageW(g_app.progress, PBM_SETRANGE, 0, MAKELPARAM(0, 100));
        g_app.status = CreateWindowW(L"STATIC", L"准备就绪 · 标准测试约 6 分钟", WS_CHILD | WS_VISIBLE,
            24, 200, 774, 24, window, reinterpret_cast<HMENU>(kIdStatus), nullptr, nullptr);
        g_app.log = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"", WS_CHILD | WS_VISIBLE | WS_VSCROLL |
            ES_MULTILINE | ES_AUTOVSCROLL | ES_READONLY, 24, 232, 774, 310, window,
            reinterpret_cast<HMENU>(kIdLog), nullptr, nullptr);
        g_app.start = CreateWindowW(L"BUTTON", L"开始标准测试", WS_CHILD | WS_VISIBLE | BS_DEFPUSHBUTTON,
            24, 560, 210, 34, window, reinterpret_cast<HMENU>(kIdStart), nullptr, nullptr);
        g_app.cancel = CreateWindowW(L"BUTTON", L"停止并恢复", WS_CHILD | WS_VISIBLE,
            246, 560, 100, 34, window, reinterpret_cast<HMENU>(kIdCancel), nullptr, nullptr);
        g_app.openResults = CreateWindowW(L"BUTTON", L"打开结果文件夹", WS_CHILD | WS_VISIBLE,
            658, 560, 140, 34, window, reinterpret_cast<HMENU>(kIdOpenResults), nullptr, nullptr);
        for (HWND child : {help, g_app.process, g_app.refresh, g_app.start, g_app.cancel,
                           g_app.openResults, g_app.status, g_app.log})
            SendMessageW(child, WM_SETFONT, reinterpret_cast<WPARAM>(g_app.bodyFont), TRUE);
        SendMessageW(title, WM_SETFONT, reinterpret_cast<WPARAM>(g_app.titleFont), TRUE);
        SetRunning(false);
        RefreshCandidates();
        AppendLog(g_app.log, L"程序只使用系统级遥测和临时电源方案，不接触游戏进程。测试前请关闭帧率限制和垂直同步。");
        return 0;
    }
    case WM_SIZE: Layout(window); return 0;
    case WM_COMMAND:
        switch (LOWORD(wparam)) {
        case kIdRefresh: RefreshCandidates(); return 0;
        case kIdStart: {
            int index = static_cast<int>(SendMessageW(g_app.process, CB_GETCURSEL, 0, 0));
            if (index < 0 || index >= static_cast<int>(g_app.candidates.size())) {
                MessageBoxW(window, L"没有可测试进程。请先启动游戏并进入游戏画面，然后点击“刷新进程”。",
                            L"Pavise", MB_OK | MB_ICONINFORMATION);
                return 0;
            }
            SetWindowTextW(g_app.log, L"");
            SendMessageW(g_app.progress, PBM_SETPOS, 0, 0);
            SetRunning(true);
            auto* target = new Candidate(g_app.candidates[index]);
            ShowWindow(window, SW_MINIMIZE);
            if (IsWindow(target->window)) {
                ShowWindow(target->window, SW_RESTORE);
                SetForegroundWindow(target->window);
            }
            g_app.worker = CreateThread(nullptr, 0, RunExperiment, target, 0, nullptr);
            if (!g_app.worker) { delete target; SetRunning(false); }
            return 0;
        }
        case kIdCancel:
            g_stop.store(true);
            EnableWindow(g_app.cancel, FALSE);
            SetWindowTextW(g_app.status, L"正在停止并恢复原电源计划……");
            return 0;
        case kIdOpenResults:
            if (!g_app.resultDirectory.empty()) ShellExecuteW(window, L"open", g_app.resultDirectory.c_str(),
                                                              nullptr, nullptr, SW_SHOWNORMAL);
            return 0;
        }
        break;
    case kMessageLog: {
        std::unique_ptr<std::wstring> value(reinterpret_cast<std::wstring*>(lparam));
        AppendLog(g_app.log, *value);
        return 0;
    }
    case kMessageProgress: {
        std::unique_ptr<std::wstring> value(reinterpret_cast<std::wstring*>(lparam));
        SendMessageW(g_app.progress, PBM_SETPOS, wparam, 0);
        SetWindowTextW(g_app.status, value->c_str());
        return 0;
    }
    case kMessageFinished: {
        std::unique_ptr<std::wstring> verdict(reinterpret_cast<std::wstring*>(lparam));
        if (g_app.worker) { CloseHandle(g_app.worker); g_app.worker = nullptr; }
        SetRunning(false);
        if (verdict) SetWindowTextW(g_app.status, verdict->c_str());
        if (g_app.closeWhenDone) DestroyWindow(window);
        else {
            ShowWindow(window, SW_RESTORE);
            SetForegroundWindow(window);
            MessageBoxW(window,
            wparam == 0 ? L"测试完成。请把结果文件夹中的报告和 CSV 一起发给 Pavise 开发者。"
                        : L"测试没有完整完成。原电源计划已经恢复；请查看日志后重试。",
            L"Pavise 测试", MB_OK | (wparam == 0 ? MB_ICONINFORMATION : MB_ICONWARNING));
        }
        return 0;
    }
    case WM_CLOSE:
        if (g_app.running) {
            if (MessageBoxW(window, L"测试仍在运行。确定停止测试并恢复原电源计划吗？",
                            L"Pavise", MB_YESNO | MB_ICONWARNING) != IDYES) return 0;
            g_app.closeWhenDone = true;
            g_stop.store(true);
            ShowWindow(window, SW_HIDE);
            return 0;
        }
        DestroyWindow(window);
        return 0;
    case WM_DESTROY:
        if (g_app.titleFont) DeleteObject(g_app.titleFont);
        if (g_app.bodyFont) DeleteObject(g_app.bodyFont);
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(window, message, wparam, lparam);
}

int RunUi(HINSTANCE instance, int show) {
    INITCOMMONCONTROLSEX controls{sizeof(controls), ICC_PROGRESS_CLASS | ICC_STANDARD_CLASSES};
    InitCommonControlsEx(&controls);
    const wchar_t* className = L"PaviseThermalFieldTester";
    WNDCLASSEXW cls = {};
    cls.cbSize = sizeof(cls);
    cls.lpfnWndProc = WindowProc;
    cls.hInstance = instance;
    cls.hIcon = LoadIconW(nullptr, IDI_APPLICATION);
    cls.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    cls.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
    cls.lpszClassName = className;
    RegisterClassExW(&cls);
    HWND window = CreateWindowExW(0, className, L"Pavise Thermal Exchange Field Tester",
        WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT, 880, 680, nullptr, nullptr, instance, nullptr);
    if (!window) return 2;
    ShowWindow(window, show);
    UpdateWindow(window);
    MSG message = {};
    while (GetMessageW(&message, nullptr, 0, 0) > 0) {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    return static_cast<int>(message.wParam);
}

int FieldSelfTest() {
    std::vector<Row> rows;
    std::vector<StageResult> stages;
    const double fps[] = {100.0, 102.0, 100.0, 101.5, 100.0, 103.0, 100.0,
                          102.5, 100.0, 101.4, 100.0, 102.1, 100.0};
    for (size_t index = 0; index < kProtocol.size(); ++index) {
        StageResult result;
        result.stage = static_cast<int>(index + 1);
        result.cap = kProtocol[index];
        result.frames = 2000;
        result.fps = fps[index];
        result.oneLowFps = result.cap == 100 ? 80.0 : 81.0;
        result.cpu = 35.0; result.gpu = 98.0; result.clock = result.cap == 100 ? 1900.0 : 1935.0;
        result.power = 125.0; result.temp = 70.0;
        stages.push_back(result);
    }
    wchar_t temporary[MAX_PATH] = {};
    if (!GetTempPathW(_countof(temporary), temporary)) return 20;
    std::wstring path = std::wstring(temporary) + L"Pavise-ThermalExchange-field-selftest.txt";
    Candidate target{nullptr, 1234, L"SyntheticGame.exe", L"Synthetic validation", 1920LL * 1080LL};
    Verdict verdict;
    if (!WriteReport(path, target, rows, stages, true, true, L"synthetic.csv", &verdict)) return 21;
    if (verdict.title != L"发现候选收益，建议复验") return 22;
    std::ifstream file(path.c_str(), std::ios::binary);
    unsigned char bom[3] = {};
    file.read(reinterpret_cast<char*>(bom), sizeof(bom));
    return file && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF ? 0 : 23;
}

} // namespace field

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int show) {
    int argc = 0;
    wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (argc > 1 && _wcsicmp(argv[1], L"--watchdog") == 0) {
        int result = ThermalExchangeCommandMain(argc, argv);
        LocalFree(argv);
        return result;
    }
    if (argc > 1 && _wcsicmp(argv[1], L"--field-self-test") == 0) {
        LocalFree(argv);
        return field::FieldSelfTest();
    }
    if (argc > 1 && (_wcsicmp(argv[1], L"--self-test") == 0 ||
                     _wcsicmp(argv[1], L"--probe") == 0 || _wcsicmp(argv[1], L"--run") == 0)) {
        int result = ThermalExchangeCommandMain(argc, argv);
        LocalFree(argv);
        return result;
    }
    LocalFree(argv);
    if (!field::IsElevated()) {
        if (field::RelaunchElevated()) return 0;
        MessageBoxW(nullptr, L"测试需要管理员权限来读取系统帧时间并安全轮转临时电源方案。",
                    L"Pavise", MB_OK | MB_ICONERROR);
        return 5;
    }
    return field::RunUi(instance, show);
}
