// File purpose Pavise Interrupt Fabric read-only field feasibility probe
// No interrupt tuning, no driver install, no injection, no game state changes

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <evntrace.h>
#include <evntcons.h>
#include <commctrl.h>
#include <psapi.h>
#include <shellapi.h>
#include <tlhelp32.h>
#include <objbase.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <fstream>
#include <iomanip>
#include <map>
#include <memory>
#include <mutex>
#include <numeric>
#include <set>
#include <sstream>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#ifdef _MSC_VER
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "comctl32.lib")
#pragma comment(lib, "psapi.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "ole32.lib")
#endif

namespace pavise {

const GUID kDxgKrnl =
    {0x802ec45a, 0x1e99, 0x4b83, {0x99, 0x20, 0x87, 0xc9, 0x82, 0x77, 0xba, 0x9d}};
const GUID kDxgi =
    {0xca11c036, 0x0102, 0x4a2d, {0xa6, 0xad, 0xf0, 0x3c, 0xfe, 0xd5, 0xd3, 0xc9}};
const GUID kD3d9 =
    {0x783aca0a, 0x790e, 0x4d7f, {0x84, 0x51, 0xaa, 0x85, 0x05, 0x11, 0xc6, 0xb9}};
const GUID kPerfInfo =
    {0xce1dbfb4, 0x137e, 0x4da6, {0x87, 0xb0, 0x3f, 0x59, 0xaa, 0x10, 0x2c, 0xbc}};
const GUID kImageLoad =
    {0x2cb15d1d, 0x5fc1, 0x11d2, {0xab, 0xe1, 0x00, 0xa0, 0xc9, 0x11, 0xf5, 0x18}};
const GUID kThread =
    {0x3d6fa8d1, 0xfe05, 0x11d0, {0x9d, 0xda, 0x00, 0xc0, 0x4f, 0xd7, 0xba, 0x7c}};

constexpr int kWarmupSeconds = 30;
constexpr int kMeasureSeconds = 180;
constexpr size_t kMaxFrames = 3000000;
constexpr size_t kMaxEvents = 3000000;

std::atomic<bool> g_cancel(false);

std::wstring WinError(DWORD code) {
    wchar_t* message = nullptr;
    FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM |
                       FORMAT_MESSAGE_IGNORE_INSERTS,
                   nullptr, code, 0, reinterpret_cast<wchar_t*>(&message), 0, nullptr);
    std::wstring result = message ? message : L"未知错误";
    if (message) LocalFree(message);
    while (!result.empty() && (result.back() == L'\r' || result.back() == L'\n')) result.pop_back();
    return result;
}

std::wstring ExePath() {
    std::vector<wchar_t> path(32768);
    DWORD length = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    return std::wstring(path.data(), length);
}

std::wstring ExeDirectory() {
    std::wstring path = ExePath();
    size_t slash = path.find_last_of(L"\\/");
    return slash == std::wstring::npos ? L"." : path.substr(0, slash);
}

std::wstring BaseName(const std::wstring& path) {
    size_t slash = path.find_last_of(L"\\/");
    return slash == std::wstring::npos ? path : path.substr(slash + 1);
}

std::wstring TimestampText() {
    SYSTEMTIME time = {};
    GetLocalTime(&time);
    wchar_t text[64] = {};
    swprintf_s(text, L"%04u%02u%02u-%02u%02u%02u", time.wYear, time.wMonth, time.wDay,
               time.wHour, time.wMinute, time.wSecond);
    return text;
}

std::string Utf8(const std::wstring& value) {
    int count = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()),
                                    nullptr, 0, nullptr, nullptr);
    std::string result(static_cast<size_t>(std::max(0, count)), '\0');
    if (count) WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()),
                                   result.data(), count, nullptr, nullptr);
    return result;
}

bool WriteUtf8Bom(const std::wstring& path, const std::wstring& text) {
    std::ofstream file(path.c_str(), std::ios::binary | std::ios::trunc);
    if (!file) return false;
    const unsigned char bom[] = {0xef, 0xbb, 0xbf};
    const std::string bytes = Utf8(text);
    file.write(reinterpret_cast<const char*>(bom), sizeof(bom));
    file.write(bytes.data(), static_cast<std::streamsize>(bytes.size()));
    return file.good();
}

std::wstring ReadRegistryString(HKEY root, const wchar_t* key, const wchar_t* name) {
    wchar_t value[1024] = {};
    DWORD bytes = sizeof(value), type = 0;
    if (RegGetValueW(root, key, name, RRF_RT_REG_SZ, &type, value, &bytes) != ERROR_SUCCESS)
        return L"未知";
    return value;
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

std::wstring ProcessorName() {
    return ReadRegistryString(HKEY_LOCAL_MACHINE,
        L"HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0", L"ProcessorNameString");
}

std::wstring MachineModel() {
    return ReadRegistryString(HKEY_LOCAL_MACHINE, L"HARDWARE\\DESCRIPTION\\System\\BIOS",
                              L"SystemManufacturer") + L" " +
           ReadRegistryString(HKEY_LOCAL_MACHINE, L"HARDWARE\\DESCRIPTION\\System\\BIOS",
                              L"SystemProductName");
}

std::wstring CoreCounts() {
    DWORD bytes = 0;
    GetLogicalProcessorInformationEx(RelationProcessorCore, nullptr, &bytes);
    std::vector<BYTE> data(bytes);
    DWORD physical = 0;
    if (bytes && GetLogicalProcessorInformationEx(RelationProcessorCore,
            reinterpret_cast<PSYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(data.data()), &bytes)) {
        BYTE* cursor = data.data();
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
    return std::to_wstring(physical) + L" 物理核 / " +
           std::to_wstring(info.dwNumberOfProcessors) + L" 线程";
}

std::wstring DisplayAdapters() {
    std::set<std::wstring> unique;
    DISPLAY_DEVICEW value = {};
    value.cb = sizeof(value);
    for (DWORD index = 0; EnumDisplayDevicesW(nullptr, index, &value, 0); ++index) {
        if (!(value.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) && value.DeviceString[0])
            unique.insert(value.DeviceString);
        ZeroMemory(&value, sizeof(value)); value.cb = sizeof(value);
    }
    std::wstring result;
    for (const auto& name : unique) {
        if (!result.empty()) result += L" | ";
        result += name;
    }
    return result.empty() ? L"未知" : result;
}

std::wstring PowerSource() {
    SYSTEM_POWER_STATUS status = {};
    if (!GetSystemPowerStatus(&status)) return L"未知";
    return status.ACLineStatus == 1 ? L"交流电源" :
           status.ACLineStatus == 0 ? L"电池" : L"未知";
}

bool IsElevated() {
    HANDLE token = nullptr;
    TOKEN_ELEVATION elevation = {};
    DWORD bytes = 0;
    bool result = OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token) &&
                  GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation), &bytes) &&
                  elevation.TokenIsElevated;
    if (token) CloseHandle(token);
    return result;
}

bool RelaunchElevated() {
    SHELLEXECUTEINFOW info = {};
    info.cbSize = sizeof(info);
    info.lpVerb = L"runas";
    std::wstring path = ExePath();
    info.lpFile = path.c_str();
    info.lpParameters = L"--ui";
    info.nShow = SW_SHOWNORMAL;
    return ShellExecuteExW(&info) != FALSE;
}

std::wstring ProcessImage(DWORD pid) {
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!process) return L"";
    std::vector<wchar_t> path(32768);
    DWORD size = static_cast<DWORD>(path.size());
    bool ok = QueryFullProcessImageNameW(process, 0, path.data(), &size) != FALSE;
    CloseHandle(process);
    return ok ? std::wstring(path.data(), size) : L"";
}

bool ProcessAlive(DWORD pid) {
    HANDLE process = OpenProcess(SYNCHRONIZE, FALSE, pid);
    if (!process) return false;
    bool alive = WaitForSingleObject(process, 0) == WAIT_TIMEOUT;
    CloseHandle(process);
    return alive;
}

struct Candidate {
    HWND window = nullptr;
    DWORD pid = 0;
    std::wstring exe;
    std::wstring title;
    long long area = 0;
};

struct InterruptEvent {
    LONGLONG start = 0;
    LONGLONG end = 0;
    ULONGLONG routine = 0;
    DWORD cpu = 0;
    bool isr = false;
    bool target = false;
    std::wstring driver;
};

struct ImageRange {
    ULONGLONG base = 0;
    ULONGLONG size = 0;
    std::wstring name;
};

struct TraceData {
    std::vector<LONGLONG> frames;
    std::vector<InterruptEvent> interrupts;
    std::vector<ImageRange> images;
    ULONGLONG allDpcCount = 0;
    ULONGLONG allIsrCount = 0;
    ULONGLONG droppedDetail = 0;
    ULONG eventsLost = 0;
    ULONG buffersLost = 0;
};

enum class FrameEventKind { None, Runtime, Kernel };

FrameEventKind ClassifyFrameEvent(const GUID& provider, WORD id) {
    if ((IsEqualGUID(provider, kDxgi) && (id == 42 || id == 55)) ||
        (IsEqualGUID(provider, kD3d9) && id == 1)) return FrameEventKind::Runtime;
    if (IsEqualGUID(provider, kDxgKrnl) && id == 171) return FrameEventKind::Kernel;
    return FrameEventKind::None;
}

template <typename T>
bool ReadAt(const BYTE* data, ULONG size, size_t offset, T* value) {
    if (!data || !value || offset + sizeof(T) > size) return false;
    memcpy(value, data + offset, sizeof(T));
    return true;
}

class KernelTrace {
public:
    ~KernelTrace() { Stop(); }

    bool Start(DWORD targetPid, std::wstring* error) {
        targetPid_ = targetPid;
        SnapshotTargetThreads();
        name_ = L"PaviseInterruptKernel_" + std::to_wstring(GetCurrentProcessId());
        const size_t bytes = sizeof(EVENT_TRACE_PROPERTIES) + (name_.size() + 1) * sizeof(wchar_t);
        properties_.assign(bytes, 0);
        auto* props = reinterpret_cast<EVENT_TRACE_PROPERTIES*>(properties_.data());
        props->Wnode.BufferSize = static_cast<ULONG>(bytes);
        props->Wnode.Flags = WNODE_FLAG_TRACED_GUID;
        props->Wnode.ClientContext = 1;
        CoCreateGuid(&props->Wnode.Guid);
        props->BufferSize = 256;
        props->MinimumBuffers = 32;
        props->MaximumBuffers = 256;
        props->LogFileMode = EVENT_TRACE_REAL_TIME_MODE | EVENT_TRACE_SYSTEM_LOGGER_MODE;
        props->FlushTimer = 1;
        props->EnableFlags = EVENT_TRACE_FLAG_DPC | EVENT_TRACE_FLAG_INTERRUPT |
                             EVENT_TRACE_FLAG_IMAGE_LOAD | EVENT_TRACE_FLAG_CSWITCH |
                             EVENT_TRACE_FLAG_THREAD;
        props->LoggerNameOffset = sizeof(EVENT_TRACE_PROPERTIES);
        ULONG rc = StartTraceW(&session_, name_.c_str(), props);
        if (rc != ERROR_SUCCESS) return Fail(error, L"StartTrace(内核)", rc);

        EVENT_TRACE_LOGFILEW log = {};
        log.LoggerName = const_cast<LPWSTR>(name_.c_str());
        log.ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD;
        log.EventRecordCallback = &KernelTrace::OnEventStatic;
        log.BufferCallback = &KernelTrace::OnBufferStatic;
        log.Context = this;
        trace_ = OpenTraceW(&log);
        if (trace_ == INVALID_PROCESSTRACE_HANDLE) {
            rc = GetLastError(); Stop(); return Fail(error, L"OpenTrace(内核)", rc);
        }
        thread_ = CreateThread(nullptr, 0, &KernelTrace::ThreadProc, this, 0, nullptr);
        if (!thread_) {
            rc = GetLastError(); Stop(); return Fail(error, L"CreateThread(内核)", rc);
        }
        return true;
    }

    void Stop() {
        if (session_) {
            auto* props = reinterpret_cast<EVENT_TRACE_PROPERTIES*>(properties_.data());
            ControlTraceW(session_, name_.c_str(), props, EVENT_TRACE_CONTROL_STOP);
            data_.eventsLost = props->EventsLost;
            data_.buffersLost = props->RealTimeBuffersLost;
            session_ = 0;
        }
        if (thread_) {
            WaitForSingleObject(thread_, 5000);
            CloseHandle(thread_); thread_ = nullptr;
        }
        if (trace_ != INVALID_PROCESSTRACE_HANDLE) {
            CloseTrace(trace_); trace_ = INVALID_PROCESSTRACE_HANDLE;
        }
    }

    TraceData Take() {
        Stop();
        return std::move(data_);
    }

private:
    static bool Fail(std::wstring* error, const wchar_t* where, ULONG code) {
        if (error) *error = std::wstring(where) + L" 失败 (" + std::to_wstring(code) + L"): " + WinError(code);
        return false;
    }

    void SnapshotTargetThreads() {
        HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (snapshot == INVALID_HANDLE_VALUE) return;
        THREADENTRY32 item = {}; item.dwSize = sizeof(item);
        if (Thread32First(snapshot, &item)) {
            do {
                if (item.th32OwnerProcessID == targetPid_) targetThreads_.insert(item.th32ThreadID);
            } while (Thread32Next(snapshot, &item));
        }
        CloseHandle(snapshot);
    }

    static ULONG WINAPI OnBufferStatic(PEVENT_TRACE_LOGFILEW) { return TRUE; }
    static VOID WINAPI OnEventStatic(PEVENT_RECORD event) {
        auto* self = static_cast<KernelTrace*>(event->UserContext);
        if (self) self->OnEvent(event);
    }
    static DWORD WINAPI ThreadProc(void* context) {
        auto* self = static_cast<KernelTrace*>(context);
        TRACEHANDLE handle = self->trace_;
        ProcessTrace(&handle, 1, nullptr, nullptr);
        return 0;
    }

    void OnEvent(PEVENT_RECORD event) {
        const UCHAR opcode = event->EventHeader.EventDescriptor.Opcode;
        if (IsEqualGUID(event->EventHeader.ProviderId, kPerfInfo)) {
            if (opcode == 66 || opcode == 68 || opcode == 69 || opcode == 67) CaptureInterrupt(event, opcode == 67);
            return;
        }
        if (IsEqualGUID(event->EventHeader.ProviderId, kThread)) {
            if (opcode == 36) CaptureSwitch(event);
            else if (opcode == 1 || opcode == 3) CaptureThread(event);
            else if (opcode == 2 || opcode == 4) RemoveThread(event);
            return;
        }
        if (IsEqualGUID(event->EventHeader.ProviderId, kImageLoad) &&
            (opcode == 10 || opcode == 3)) CaptureImage(event);
    }

    void CaptureSwitch(PEVENT_RECORD event) {
        DWORD next = 0;
        if (!ReadAt(static_cast<const BYTE*>(event->UserData), event->UserDataLength, 0, &next)) return;
        DWORD cpu = event->BufferContext.ProcessorNumber;
        if (cpu >= runningThreads_.size()) runningThreads_.resize(cpu + 1, 0);
        runningThreads_[cpu] = next;
    }

    void CaptureThread(PEVENT_RECORD event) {
        DWORD pid = 0, tid = 0;
        const BYTE* bytes = static_cast<const BYTE*>(event->UserData);
        if (!ReadAt(bytes, event->UserDataLength, 0, &pid) ||
            !ReadAt(bytes, event->UserDataLength, 4, &tid)) return;
        if (pid == targetPid_) targetThreads_.insert(tid);
    }

    void RemoveThread(PEVENT_RECORD event) {
        DWORD pid = 0, tid = 0;
        const BYTE* bytes = static_cast<const BYTE*>(event->UserData);
        if (!ReadAt(bytes, event->UserDataLength, 0, &pid) ||
            !ReadAt(bytes, event->UserDataLength, 4, &tid)) return;
        if (pid == targetPid_) targetThreads_.erase(tid);
    }

    void CaptureInterrupt(PEVENT_RECORD event, bool isr) {
        const BYTE* bytes = static_cast<const BYTE*>(event->UserData);
        ULONGLONG initial = 0, routine = 0;
        if (!ReadAt(bytes, event->UserDataLength, 0, &initial)) return;
        const bool header32 = (event->EventHeader.Flags & EVENT_HEADER_FLAG_32_BIT_HEADER) != 0;
        if (header32) {
            DWORD value = 0;
            if (!ReadAt(bytes, event->UserDataLength, 8, &value)) return;
            routine = value;
        } else if (!ReadAt(bytes, event->UserDataLength, 8, &routine)) return;
        const LONGLONG end = event->EventHeader.TimeStamp.QuadPart;
        if (!initial || end <= static_cast<LONGLONG>(initial)) return;
        if (isr) ++data_.allIsrCount; else ++data_.allDpcCount;
        if (data_.interrupts.size() >= kMaxEvents) { ++data_.droppedDetail; return; }
        DWORD cpu = event->BufferContext.ProcessorNumber;
        DWORD current = cpu < runningThreads_.size() ? runningThreads_[cpu] : 0;
        InterruptEvent value;
        value.start = static_cast<LONGLONG>(initial);
        value.end = end;
        value.routine = routine;
        value.cpu = cpu;
        value.isr = isr;
        value.target = targetThreads_.find(current) != targetThreads_.end();
        data_.interrupts.push_back(value);
    }

    void CaptureImage(PEVENT_RECORD event) {
        const BYTE* bytes = static_cast<const BYTE*>(event->UserData);
        const bool header32 = (event->EventHeader.Flags & EVENT_HEADER_FLAG_32_BIT_HEADER) != 0;
        ULONGLONG base = 0, size = 0;
        size_t nameOffset = 0;
        if (header32) {
            DWORD b = 0, s = 0;
            if (!ReadAt(bytes, event->UserDataLength, 0, &b) ||
                !ReadAt(bytes, event->UserDataLength, 4, &s)) return;
            base = b; size = s; nameOffset = 44;
        } else {
            if (!ReadAt(bytes, event->UserDataLength, 0, &base) ||
                !ReadAt(bytes, event->UserDataLength, 8, &size)) return;
            nameOffset = 56;
        }
        if (!base || !size || nameOffset >= event->UserDataLength) return;
        size_t chars = (event->UserDataLength - nameOffset) / sizeof(wchar_t);
        const wchar_t* name = reinterpret_cast<const wchar_t*>(bytes + nameOffset);
        size_t length = 0;
        while (length < chars && name[length]) ++length;
        if (!length) return;
        data_.images.push_back({base, size, BaseName(std::wstring(name, length))});
    }

    DWORD targetPid_ = 0;
    std::wstring name_;
    TRACEHANDLE session_ = 0;
    TRACEHANDLE trace_ = INVALID_PROCESSTRACE_HANDLE;
    HANDLE thread_ = nullptr;
    std::vector<BYTE> properties_;
    std::vector<DWORD> runningThreads_;
    std::unordered_set<DWORD> targetThreads_;
    TraceData data_;
};

struct PresentCapture {
    std::vector<LONGLONG> runtimeFrames;
    std::vector<LONGLONG> kernelFrames;
};

class PresentTrace {
public:
    ~PresentTrace() { Stop(); }
    bool Start(DWORD pid, std::wstring* error) {
        pid_ = pid;
        name_ = L"PaviseInterruptPresent_" + std::to_wstring(GetCurrentProcessId());
        const size_t bytes = sizeof(EVENT_TRACE_PROPERTIES) + (name_.size() + 1) * sizeof(wchar_t);
        properties_.assign(bytes, 0);
        auto* props = reinterpret_cast<EVENT_TRACE_PROPERTIES*>(properties_.data());
        props->Wnode.BufferSize = static_cast<ULONG>(bytes);
        props->Wnode.Flags = WNODE_FLAG_TRACED_GUID;
        props->Wnode.ClientContext = 1;
        CoCreateGuid(&props->Wnode.Guid);
        props->BufferSize = 256;
        props->MinimumBuffers = 16;
        props->MaximumBuffers = 128;
        props->LogFileMode = EVENT_TRACE_REAL_TIME_MODE;
        props->FlushTimer = 1;
        props->LoggerNameOffset = sizeof(EVENT_TRACE_PROPERTIES);
        ULONG rc = StartTraceW(&session_, name_.c_str(), props);
        if (rc != ERROR_SUCCESS) return Fail(error, L"StartTrace(Present)", rc);
        rc = EnableTraceEx2(session_, &kDxgKrnl, EVENT_CONTROL_CODE_ENABLE_PROVIDER,
                            TRACE_LEVEL_VERBOSE, ~0ULL, 0, 0, nullptr);
        if (rc != ERROR_SUCCESS) { Stop(); return Fail(error, L"EnableTrace(DxgKrnl)", rc); }
        rc = EnableTraceEx2(session_, &kDxgi, EVENT_CONTROL_CODE_ENABLE_PROVIDER,
                            TRACE_LEVEL_VERBOSE, ~0ULL, 0, 0, nullptr);
        if (rc != ERROR_SUCCESS) { Stop(); return Fail(error, L"EnableTrace(DXGI)", rc); }
        rc = EnableTraceEx2(session_, &kD3d9, EVENT_CONTROL_CODE_ENABLE_PROVIDER,
                            TRACE_LEVEL_VERBOSE, ~0ULL, 0, 0, nullptr);
        if (rc != ERROR_SUCCESS) { Stop(); return Fail(error, L"EnableTrace(D3D9)", rc); }
        EVENT_TRACE_LOGFILEW log = {};
        log.LoggerName = const_cast<LPWSTR>(name_.c_str());
        log.ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD;
        log.EventRecordCallback = &PresentTrace::OnEventStatic;
        log.Context = this;
        trace_ = OpenTraceW(&log);
        if (trace_ == INVALID_PROCESSTRACE_HANDLE) {
            rc = GetLastError(); Stop(); return Fail(error, L"OpenTrace(Present)", rc);
        }
        thread_ = CreateThread(nullptr, 0, &PresentTrace::ThreadProc, this, 0, nullptr);
        if (!thread_) { rc = GetLastError(); Stop(); return Fail(error, L"CreateThread(Present)", rc); }
        return true;
    }
    void Stop() {
        if (session_) {
            ControlTraceW(session_, name_.c_str(),
                reinterpret_cast<EVENT_TRACE_PROPERTIES*>(properties_.data()), EVENT_TRACE_CONTROL_STOP);
            session_ = 0;
        }
        if (thread_) { WaitForSingleObject(thread_, 5000); CloseHandle(thread_); thread_ = nullptr; }
        if (trace_ != INVALID_PROCESSTRACE_HANDLE) { CloseTrace(trace_); trace_ = INVALID_PROCESSTRACE_HANDLE; }
    }
    size_t Count() const { return std::max(runtimeCount_.load(), kernelCount_.load()); }
    std::vector<std::pair<DWORD, size_t>> TopProducers(size_t limit) const {
        std::lock_guard<std::mutex> lock(countMutex_);
        std::unordered_map<DWORD, size_t> merged = runtimePidCounts_;
        for (const auto& item : kernelPidCounts_) merged[item.first] = std::max(merged[item.first], item.second);
        std::vector<std::pair<DWORD, size_t>> values(merged.begin(), merged.end());
        std::sort(values.begin(), values.end(), [](const auto& a, const auto& b) { return a.second > b.second; });
        if (values.size() > limit) values.resize(limit);
        return values;
    }
    PresentCapture Take() {
        Stop();
        return {std::move(runtimeFrames_), std::move(kernelFrames_)};
    }
private:
    static bool Fail(std::wstring* error, const wchar_t* where, ULONG code) {
        if (error) *error = std::wstring(where) + L" 失败 (" + std::to_wstring(code) + L"): " + WinError(code);
        return false;
    }
    static VOID WINAPI OnEventStatic(PEVENT_RECORD event) {
        auto* self = static_cast<PresentTrace*>(event->UserContext);
        if (self) self->OnEvent(event);
    }
    void OnEvent(PEVENT_RECORD event) {
        const WORD id = event->EventHeader.EventDescriptor.Id;
        const FrameEventKind kind = ClassifyFrameEvent(event->EventHeader.ProviderId, id);
        const bool runtime = kind == FrameEventKind::Runtime;
        // For APIs without DXGI or D3D9 runtime events the correct kernel-side start is PresentHistory_Start 171
        // Present_Info 184 is a completion and correlation event, not a process-owned frame source
        const bool kernel = kind == FrameEventKind::Kernel;
        if (!runtime && !kernel) return;
        const DWORD pid = event->EventHeader.ProcessId;
        {
            std::lock_guard<std::mutex> lock(countMutex_);
            if (runtime) ++runtimePidCounts_[pid];
            else ++kernelPidCounts_[pid];
        }
        if (pid != pid_) return;
        if (runtime && runtimeFrames_.size() < kMaxFrames) {
            runtimeFrames_.push_back(event->EventHeader.TimeStamp.QuadPart);
            runtimeCount_.fetch_add(1, std::memory_order_relaxed);
        } else if (kernel && kernelFrames_.size() < kMaxFrames) {
            kernelFrames_.push_back(event->EventHeader.TimeStamp.QuadPart);
            kernelCount_.fetch_add(1, std::memory_order_relaxed);
        }
    }
    static DWORD WINAPI ThreadProc(void* context) {
        auto* self = static_cast<PresentTrace*>(context);
        TRACEHANDLE handle = self->trace_;
        ProcessTrace(&handle, 1, nullptr, nullptr);
        return 0;
    }
    DWORD pid_ = 0;
    std::wstring name_;
    TRACEHANDLE session_ = 0;
    TRACEHANDLE trace_ = INVALID_PROCESSTRACE_HANDLE;
    HANDLE thread_ = nullptr;
    std::vector<BYTE> properties_;
    std::vector<LONGLONG> runtimeFrames_;
    std::vector<LONGLONG> kernelFrames_;
    std::atomic<size_t> runtimeCount_{0};
    std::atomic<size_t> kernelCount_{0};
    mutable std::mutex countMutex_;
    std::unordered_map<DWORD, size_t> runtimePidCounts_;
    std::unordered_map<DWORD, size_t> kernelPidCounts_;
};

struct SelectedPresentFrames {
    std::vector<LONGLONG> frames;
    std::wstring source;
    size_t runtimeTimestamps = 0;
    size_t kernelTimestamps = 0;
    size_t runtimeValidIntervals = 0;
    size_t kernelValidIntervals = 0;
};

std::pair<size_t, size_t> CountPresentQuality(std::vector<LONGLONG>& frames,
                                              LONGLONG begin, LONGLONG end,
                                              LONGLONG frequency) {
    std::sort(frames.begin(), frames.end());
    frames.erase(std::unique(frames.begin(), frames.end()), frames.end());
    auto first = std::lower_bound(frames.begin(), frames.end(), begin);
    auto last = std::upper_bound(frames.begin(), frames.end(), end);
    const size_t timestamps = static_cast<size_t>(last - first);
    size_t valid = 0;
    if (frequency > 0 && first != last) {
        for (auto current = first + 1; current != last; ++current) {
            double ms = static_cast<double>(*current - *(current - 1)) * 1000.0 / frequency;
            if (ms >= 0.20 && ms <= 1000.0) ++valid;
        }
    }
    return {timestamps, valid};
}

SelectedPresentFrames SelectPresentFrames(PresentCapture capture, LONGLONG begin,
                                           LONGLONG end, LONGLONG frequency) {
    SelectedPresentFrames selected;
    auto runtime = CountPresentQuality(capture.runtimeFrames, begin, end, frequency);
    auto kernel = CountPresentQuality(capture.kernelFrames, begin, end, frequency);
    selected.runtimeTimestamps = runtime.first;
    selected.runtimeValidIntervals = runtime.second;
    selected.kernelTimestamps = kernel.first;
    selected.kernelValidIntervals = kernel.second;
    if (runtime.second >= kernel.second) {
        selected.frames = std::move(capture.runtimeFrames);
        selected.source = runtime.second ? L"DXGI/D3D9 Runtime PresentStart" : L"未取得可靠帧源";
    } else {
        selected.frames = std::move(capture.kernelFrames);
        selected.source = L"DxgKrnl PresentHistoryStart（Vulkan/非标准运行时后备）";
    }
    return selected;
}

struct FrameInterval {
    LONGLONG start = 0;
    LONGLONG end = 0;
    double ms = 0;
    bool spike = false;
};

struct DriverStats {
    ULONGLONG count = 0;
    ULONGLONG longCount = 0;
    ULONGLONG targetCount = 0;
    ULONGLONG targetLongCount = 0;
    double totalMs = 0;
    double longMs = 0;
    double maxUs = 0;
};

struct SecondStats {
    int frames = 0;
    int spikes = 0;
    std::vector<double> frameMs;
    ULONGLONG dpcCount = 0;
    ULONGLONG isrCount = 0;
    ULONGLONG over100 = 0;
    ULONGLONG targetCount = 0;
    double dpcMs = 0;
    double isrMs = 0;
    double targetMs = 0;
};

struct Analysis {
    bool valid = false;
    std::wstring verdict;
    std::wstring frameSource;
    size_t runtimeFrameTimestamps = 0;
    size_t kernelFrameTimestamps = 0;
    size_t runtimeValidIntervals = 0;
    size_t kernelValidIntervals = 0;
    std::vector<std::wstring> reasons;
    double qpcFrequency = 0;
    double medianMs = NAN;
    double p99Ms = NAN;
    double spikeThresholdMs = NAN;
    double meanFps = NAN;
    size_t frameCount = 0;
    size_t spikeCount = 0;
    size_t spikeWith100us = 0;
    size_t directSpikeEvents = 0;
    double spikeInterruptMeanMs = NAN;
    double normalInterruptMeanMs = NAN;
    std::vector<FrameInterval> intervals;
    std::vector<InterruptEvent> events;
    std::map<std::wstring, DriverStats> drivers;
    std::vector<SecondStats> seconds;
};

double Percentile(std::vector<double> values, double p) {
    if (values.empty()) return NAN;
    std::sort(values.begin(), values.end());
    double position = p * static_cast<double>(values.size() - 1);
    size_t left = static_cast<size_t>(position);
    size_t right = std::min(left + 1, values.size() - 1);
    double weight = position - static_cast<double>(left);
    return values[left] * (1.0 - weight) + values[right] * weight;
}

std::wstring ResolveDriver(ULONGLONG routine, const std::vector<ImageRange>& images) {
    const ImageRange* best = nullptr;
    for (const auto& image : images) {
        if (routine >= image.base && routine - image.base < image.size) {
            if (!best || image.base > best->base) best = &image;
        }
    }
    return best ? best->name : L"<未解析内核模块>";
}

Analysis Analyze(std::vector<LONGLONG> frames, TraceData& trace,
                 LONGLONG begin, LONGLONG end, LONGLONG frequency) {
    Analysis result;
    result.qpcFrequency = static_cast<double>(frequency);
    if (frequency <= 0 || end <= begin) {
        result.verdict = L"数据无效";
        result.reasons.push_back(L"测量时间轴无效。");
        return result;
    }
    std::sort(frames.begin(), frames.end());
    frames.erase(std::remove_if(frames.begin(), frames.end(), [=](LONGLONG value) {
        return value < begin || value > end;
    }), frames.end());
    frames.erase(std::unique(frames.begin(), frames.end()), frames.end());
    result.frameCount = frames.size();

    std::vector<double> frameDurations;
    for (size_t index = 1; index < frames.size(); ++index) {
        const double ms = static_cast<double>(frames[index] - frames[index - 1]) * 1000.0 / frequency;
        if (ms >= 0.20 && ms <= 1000.0) frameDurations.push_back(ms);
    }
    if (frameDurations.size() < 300) {
        result.verdict = L"数据无效：没有捕获到足够游戏帧";
        result.reasons.push_back(L"有效帧间隔少于 300。这只说明当前进程没有提供足够的兼容 Present 事件，不能据此断言选错进程。");
        result.reasons.push_back(L"本轮中断数据不得用于判断 Interrupt Fabric 是否可行。");
        return result;
    }

    result.medianMs = Percentile(frameDurations, 0.50);
    result.p99Ms = Percentile(frameDurations, 0.99);
    result.spikeThresholdMs = std::max(result.medianMs * 2.0, result.medianMs + 4.0);
    result.meanFps = static_cast<double>(frameDurations.size()) /
                     (std::accumulate(frameDurations.begin(), frameDurations.end(), 0.0) / 1000.0);
    for (size_t index = 1; index < frames.size(); ++index) {
        double ms = static_cast<double>(frames[index] - frames[index - 1]) * 1000.0 / frequency;
        if (ms < 0.20 || ms > 1000.0) continue;
        result.intervals.push_back({frames[index - 1], frames[index], ms, ms >= result.spikeThresholdMs});
    }
    result.spikeCount = static_cast<size_t>(std::count_if(result.intervals.begin(), result.intervals.end(),
        [](const FrameInterval& value) { return value.spike; }));

    const int durationSeconds = std::max(1, static_cast<int>((end - begin) / frequency) + 1);
    result.seconds.resize(static_cast<size_t>(durationSeconds));
    for (const auto& frame : result.intervals) {
        int second = static_cast<int>((frame.end - begin) / frequency);
        if (second < 0 || second >= durationSeconds) continue;
        auto& value = result.seconds[static_cast<size_t>(second)];
        ++value.frames;
        value.frameMs.push_back(frame.ms);
        if (frame.spike) ++value.spikes;
    }

    std::sort(trace.images.begin(), trace.images.end(),
              [](const ImageRange& a, const ImageRange& b) { return a.base < b.base; });
    std::sort(trace.interrupts.begin(), trace.interrupts.end(),
              [](const InterruptEvent& a, const InterruptEvent& b) { return a.end < b.end; });
    std::vector<double> frameIrqMs(result.intervals.size(), 0.0);
    std::vector<bool> frameHas100(result.intervals.size(), false);

    for (auto event : trace.interrupts) {
        if (event.end < begin || event.start > end) continue;
        LONGLONG ticks = event.end - event.start;
        if (ticks <= 0 || ticks > frequency) continue;
        event.driver = ResolveDriver(event.routine, trace.images);
        const double durationMs = static_cast<double>(ticks) * 1000.0 / frequency;
        auto position = std::lower_bound(result.intervals.begin(), result.intervals.end(), event.end,
            [](const FrameInterval& frame, LONGLONG stamp) { return frame.end < stamp; });
        bool inFrame = position != result.intervals.end() && event.end >= position->start && event.start <= position->end;
        bool inSpike = inFrame && position->spike;
        size_t frameIndex = inFrame ? static_cast<size_t>(position - result.intervals.begin()) : 0;
        if (inFrame) {
            frameIrqMs[frameIndex] += durationMs;
            if (durationMs >= 0.10) frameHas100[frameIndex] = true;
        }
        if (event.target && inSpike) ++result.directSpikeEvents;
        DriverStats& driver = result.drivers[event.driver];
        ++driver.count; driver.totalMs += durationMs;
        driver.maxUs = std::max(driver.maxUs, durationMs * 1000.0);
        if (inSpike) { ++driver.longCount; driver.longMs += durationMs; }
        if (event.target) ++driver.targetCount;
        if (event.target && inSpike) ++driver.targetLongCount;

        int second = static_cast<int>((event.end - begin) / frequency);
        if (second >= 0 && second < durationSeconds) {
            auto& value = result.seconds[static_cast<size_t>(second)];
            if (event.isr) { ++value.isrCount; value.isrMs += durationMs; }
            else { ++value.dpcCount; value.dpcMs += durationMs; }
            if (durationMs >= 0.10) ++value.over100;
            if (event.target) { ++value.targetCount; value.targetMs += durationMs; }
        }
        if (durationMs >= 0.05 || event.target) result.events.push_back(std::move(event));
    }

    double spikeIrq = 0, normalIrq = 0;
    size_t spikes = 0, normal = 0;
    for (size_t index = 0; index < result.intervals.size(); ++index) {
        if (result.intervals[index].spike) {
            ++spikes; spikeIrq += frameIrqMs[index];
            if (frameHas100[index]) ++result.spikeWith100us;
        } else {
            ++normal; normalIrq += frameIrqMs[index];
        }
    }
    result.spikeInterruptMeanMs = spikes ? spikeIrq / spikes : 0.0;
    result.normalInterruptMeanMs = normal ? normalIrq / normal : 0.0;
    result.valid = true;

    std::wstring candidateDriver;
    DriverStats candidateStats;
    for (const auto& item : result.drivers) {
        if (item.second.targetLongCount > candidateStats.targetLongCount ||
            (item.second.targetLongCount == candidateStats.targetLongCount &&
             item.second.longMs > candidateStats.longMs)) {
            candidateDriver = item.first; candidateStats = item.second;
        }
    }
    const double spikeCoverage = result.spikeCount ?
        static_cast<double>(result.spikeWith100us) / result.spikeCount : 0.0;
    const bool directCandidate = result.spikeCount >= 5 && candidateStats.targetLongCount >= 3 &&
                                 candidateStats.maxUs >= 100.0;
    const bool pressure = result.spikeCount >= 5 && spikeCoverage >= 0.25 &&
                          result.spikeInterruptMeanMs >= 0.10 &&
                          result.spikeInterruptMeanMs >= result.normalInterruptMeanMs * 1.5;
    if (directCandidate) {
        result.verdict = L"发现可复验的中断关联候选";
        result.reasons.push_back(candidateDriver + L" 至少 3 次在长帧内直接打断游戏线程，且存在 ≥100us 事件。");
        result.reasons.push_back(L"这证明存在值得做第二轮 A/B 的信号，但尚不能证明控制该设备中断一定会提升性能。");
    } else if (pressure) {
        result.verdict = L"发现系统级中断压力，尚未定位为游戏直接命中";
        result.reasons.push_back(L"长帧中的 ISR/DPC 时间明显高于普通帧，但直接打断游戏线程的重复证据不足。");
        result.reasons.push_back(L"可继续细分设备与 CPU 拓扑；当前证据不足以开发控制驱动。");
    } else {
        result.verdict = L"未发现可重复的中断长帧关联";
        result.reasons.push_back(L"本场景没有达到直接命中或系统级压力门槛，Interrupt Fabric 在这台机器/这个场景上暂不值得进入控制阶段。");
    }
    if (trace.eventsLost || trace.buffersLost)
        result.reasons.push_back(L"ETW 有丢失事件，结论可信度下降；请关闭其他跟踪工具后重测。");
    if (trace.droppedDetail)
        result.reasons.push_back(L"详细中断事件超过内存上限，后段事件被截断。");
    return result;
}

std::vector<std::pair<std::wstring, DriverStats>> SortedDrivers(const Analysis& analysis) {
    std::vector<std::pair<std::wstring, DriverStats>> result(analysis.drivers.begin(), analysis.drivers.end());
    std::sort(result.begin(), result.end(), [](const auto& a, const auto& b) {
        if (a.second.targetLongCount != b.second.targetLongCount)
            return a.second.targetLongCount > b.second.targetLongCount;
        if (a.second.longMs != b.second.longMs) return a.second.longMs > b.second.longMs;
        return a.second.totalMs > b.second.totalMs;
    });
    return result;
}

bool EventInsideSpike(const InterruptEvent& event, const std::vector<FrameInterval>& intervals) {
    auto position = std::lower_bound(intervals.begin(), intervals.end(), event.end,
        [](const FrameInterval& frame, LONGLONG stamp) { return frame.end < stamp; });
    return position != intervals.end() && position->spike &&
           event.end >= position->start && event.start <= position->end;
}

bool WriteOutputs(const std::wstring& directory, const Candidate& target, const Analysis& analysis,
                  const TraceData& raw, LONGLONG begin, LONGLONG frequency,
                  const std::wstring& kernelError, const std::wstring& presentError) {
    CreateDirectoryW(directory.c_str(), nullptr);
    const std::wstring reportPath = directory + L"\\Pavise-InterruptFabric-报告.txt";
    const std::wstring secondsPath = directory + L"\\Pavise-InterruptFabric-逐秒.csv";
    const std::wstring eventsPath = directory + L"\\Pavise-InterruptFabric-关键事件.csv";

    std::wostringstream report;
    report << L"Pavise Interrupt Fabric 外场可行性报告\n"
           << L"========================================\n\n"
           << L"结论: " << analysis.verdict << L"\n";
    for (const auto& reason : analysis.reasons) report << L"- " << reason << L"\n";
    report << L"\n测试对象\n"
           << L"- 测试器: FieldTest 1.2 / 协议 3\n"
           << L"- Windows: " << WindowsVersion() << L"\n"
           << L"- 型号: " << MachineModel() << L"\n"
           << L"- CPU: " << ProcessorName() << L"\n"
           << L"- 核心: " << CoreCounts() << L"\n"
           << L"- 显卡: " << DisplayAdapters() << L"\n"
           << L"- 供电: " << PowerSource() << L"\n"
           << L"- 游戏进程: " << target.exe << L" (PID " << target.pid << L")\n"
           << L"- 游戏窗口: " << target.title << L"\n"
           << L"- 帧源: " << analysis.frameSource << L"\n"
           << L"- 正式区间 Runtime 时间戳/有效间隔: " << analysis.runtimeFrameTimestamps
           << L" / " << analysis.runtimeValidIntervals << L"\n"
           << L"- 正式区间 PresentHistory 时间戳/有效间隔: " << analysis.kernelFrameTimestamps
           << L" / " << analysis.kernelValidIntervals << L"\n"
           << L"- 协议: 预热 " << kWarmupSeconds << L" 秒 + 连续测量 " << kMeasureSeconds << L" 秒\n";
    if (!kernelError.empty()) report << L"- 内核跟踪错误: " << kernelError << L"\n";
    if (!presentError.empty()) report << L"- 帧跟踪错误: " << presentError << L"\n";
    report << L"\n帧与中断摘要\n"
           << L"- 捕获帧时间戳: " << analysis.frameCount << L"\n"
           << L"- 平均 FPS: " << std::fixed << std::setprecision(2) << analysis.meanFps << L"\n"
           << L"- 帧时间中位数: " << analysis.medianMs << L" ms\n"
           << L"- 帧时间 P99: " << analysis.p99Ms << L" ms\n"
           << L"- 本报告长帧阈值: " << analysis.spikeThresholdMs << L" ms\n"
           << L"- 长帧: " << analysis.spikeCount << L"\n"
           << L"- 含 ≥100us ISR/DPC 的长帧: " << analysis.spikeWith100us << L"\n"
           << L"- 长帧内直接命中游戏线程的事件: " << analysis.directSpikeEvents << L"\n"
           << L"- 长帧平均 ISR/DPC 时间: " << analysis.spikeInterruptMeanMs << L" ms\n"
           << L"- 普通帧平均 ISR/DPC 时间: " << analysis.normalInterruptMeanMs << L" ms\n"
           << L"- 全量 DPC / ISR 计数: " << raw.allDpcCount << L" / " << raw.allIsrCount << L"\n"
           << L"- ETW 丢事件 / 丢实时缓冲: " << raw.eventsLost << L" / " << raw.buffersLost << L"\n";
    report << L"\n驱动模块排行（按长帧直接命中、长帧耗时排序）\n"
           << L"模块                          总次数   总耗时ms  长帧次数  长帧耗时ms  直击游戏  长帧直击  最大us\n";
    auto drivers = SortedDrivers(analysis);
    for (size_t i = 0; i < std::min<size_t>(20, drivers.size()); ++i) {
        const auto& item = drivers[i];
        report << std::left << std::setw(30) << item.first << std::right
               << std::setw(9) << item.second.count << std::setw(11) << item.second.totalMs
               << std::setw(10) << item.second.longCount << std::setw(13) << item.second.longMs
               << std::setw(10) << item.second.targetCount << std::setw(10) << item.second.targetLongCount
               << std::setw(10) << item.second.maxUs << L"\n";
    }
    report << L"\n判定边界\n"
           << L"- 长帧阈值 = max(帧时间中位数×2, 中位数+4ms)。\n"
           << L"- ‘直接命中候选’要求同一驱动至少 3 次在长帧中打断游戏线程，并出现 ≥100us 事件。\n"
           << L"- ‘系统级压力’要求至少 25% 长帧含 ≥100us 事件，且长帧中断时间至少是普通帧的 1.5 倍并达到 0.10ms。\n"
           << L"- 关联不是因果；只有下一阶段针对候选设备做严格 ABBA，才能判断是否可转化为性能收益。\n"
           << L"\n安全边界: 本程序只读 ETW，不打开游戏进程做读写、不注入 DLL、不安装驱动、"
              L"不修改 GPU/电源/中断设置。\n";
    if (!WriteUtf8Bom(reportPath, report.str())) return false;

    std::wostringstream seconds;
    seconds << L"second,frames,fps,frame_p50_ms,frame_p99_ms,long_frames,dpc_count,dpc_ms,isr_count,isr_ms,over_100us,target_hit_count,target_hit_ms\n";
    for (size_t index = 0; index < analysis.seconds.size(); ++index) {
        const auto& value = analysis.seconds[index];
        seconds << index << L"," << value.frames << L"," << value.frames << L",";
        if (value.frameMs.empty()) seconds << L"n/a,n/a,";
        else seconds << Percentile(value.frameMs, 0.50) << L"," << Percentile(value.frameMs, 0.99) << L",";
        seconds << value.spikes << L"," << value.dpcCount << L"," << value.dpcMs << L"," << value.isrCount
                << L"," << value.isrMs << L"," << value.over100 << L"," << value.targetCount << L"," << value.targetMs << L"\n";
    }
    if (!WriteUtf8Bom(secondsPath, seconds.str())) return false;

    std::wostringstream events;
    events << L"time_s,type,cpu,duration_us,routine,driver,on_target_thread,inside_long_frame\n";
    for (const auto& event : analysis.events) {
        double time = static_cast<double>(event.end - begin) / frequency;
        double us = static_cast<double>(event.end - event.start) * 1000000.0 / frequency;
        events << std::fixed << std::setprecision(6) << time << L"," << (event.isr ? L"ISR" : L"DPC")
               << L"," << event.cpu << L"," << std::setprecision(2) << us << L",0x" << std::hex
               << event.routine << std::dec << L"," << event.driver << L"," << (event.target ? 1 : 0)
               << L"," << (EventInsideSpike(event, analysis.intervals) ? 1 : 0) << L"\n";
    }
    return WriteUtf8Bom(eventsPath, events.str());
}

constexpr UINT kMessageLog = WM_APP + 1;
constexpr UINT kMessageProgress = WM_APP + 2;
constexpr UINT kMessageFinished = WM_APP + 3;
constexpr int kIdProcess = 1001;
constexpr int kIdRefresh = 1002;
constexpr int kIdStart = 1003;
constexpr int kIdCancel = 1004;
constexpr int kIdOpen = 1005;
constexpr int kIdProgress = 1006;
constexpr int kIdStatus = 1007;
constexpr int kIdLog = 1008;

struct AppState {
    HWND window = nullptr;
    HWND process = nullptr;
    HWND refresh = nullptr;
    HWND start = nullptr;
    HWND cancel = nullptr;
    HWND open = nullptr;
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

BOOL CALLBACK EnumCandidate(HWND window, LPARAM parameter) {
    auto* values = reinterpret_cast<std::map<DWORD, Candidate>*>(parameter);
    if (!IsWindowVisible(window) || GetWindow(window, GW_OWNER)) return TRUE;
    if (GetWindowLongPtrW(window, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) return TRUE;
    wchar_t title[512] = {};
    if (!GetWindowTextW(window, title, _countof(title)) || !title[0]) return TRUE;
    DWORD pid = 0;
    GetWindowThreadProcessId(window, &pid);
    if (!pid || pid == GetCurrentProcessId()) return TRUE;
    RECT rect = {};
    GetClientRect(window, &rect);
    long long area = static_cast<long long>(std::max(0L, rect.right - rect.left)) *
                     std::max(0L, rect.bottom - rect.top);
    if (area < 320LL * 200LL) return TRUE;
    std::wstring path = ProcessImage(pid);
    if (path.empty()) return TRUE;
    Candidate candidate{window, pid, BaseName(path), title, area};
    auto found = values->find(pid);
    if (found == values->end() || candidate.area > found->second.area) (*values)[pid] = candidate;
    return TRUE;
}

void RefreshCandidates() {
    std::map<DWORD, Candidate> unique;
    EnumWindows(EnumCandidate, reinterpret_cast<LPARAM>(&unique));
    g_app.candidates.clear();
    for (const auto& item : unique) g_app.candidates.push_back(item.second);
    std::sort(g_app.candidates.begin(), g_app.candidates.end(),
              [](const Candidate& a, const Candidate& b) { return a.area > b.area; });
    SendMessageW(g_app.process, CB_RESETCONTENT, 0, 0);
    for (const auto& candidate : g_app.candidates) {
        std::wstring label = candidate.exe + L"  (PID " + std::to_wstring(candidate.pid) +
                             L")  —  " + candidate.title;
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

bool WaitPhase(int seconds, int base, int span, const std::wstring& label, DWORD pid) {
    for (int tenth = 0; tenth < seconds * 10; ++tenth) {
        if (g_cancel.load() || !ProcessAlive(pid)) return false;
        if (tenth % 10 == 0) {
            int progress = base + (tenth * span) / (seconds * 10);
            PostProgress(progress, label + L"，剩余 " + std::to_wstring(seconds - tenth / 10) + L" 秒");
        }
        Sleep(100);
    }
    return true;
}

DWORD WINAPI RunExperiment(void* parameter) {
    Candidate target = *static_cast<Candidate*>(parameter);
    delete static_cast<Candidate*>(parameter);
    g_cancel.store(false);
    KernelTrace kernel;
    PresentTrace presents;
    std::wstring kernelError, presentError;
    bool kernelReady = kernel.Start(target.pid, &kernelError);
    bool presentReady = presents.Start(target.pid, &presentError);
    if (kernelReady) PostLog(L"内核 ISR/DPC 跟踪已启动。");
    else PostLog(L"内核跟踪失败：" + kernelError);
    if (presentReady) PostLog(L"游戏 Present 跟踪已启动。");
    else PostLog(L"帧跟踪失败：" + presentError);

    if (target.window && IsWindow(target.window)) {
        ShowWindow(g_app.window, SW_MINIMIZE);
        SetForegroundWindow(target.window);
    }
    bool completed = kernelReady && presentReady;
    if (completed) completed = WaitPhase(kWarmupSeconds, 0, 15, L"预热中", target.pid);
    if (completed && presents.Count() < 30) {
        presentError = L"预热 30 秒内没有取得足够的 DXGI/D3D9/PresentHistory 帧事件。"
                       L"这可能是当前呈现路径不兼容，不能自动判定为选错进程。";
        auto producers = presents.TopProducers(12);
        int suggestions = 0;
        for (const auto& producer : producers) {
            if (producer.first == target.pid || producer.first <= 4 || producer.second < 30) continue;
            std::wstring image = BaseName(ProcessImage(producer.first));
            if (image.empty() || _wcsicmp(image.c_str(), L"dwm.exe") == 0 ||
                _wcsicmp(image.c_str(), L"csrss.exe") == 0 ||
                _wcsicmp(image.c_str(), L"explorer.exe") == 0) continue;
            if (!suggestions) presentError += L" 本轮检测到的活跃渲染候选：";
            if (suggestions) presentError += L"、";
            presentError += image + L" (PID " + std::to_wstring(producer.first) + L")";
            if (++suggestions >= 3) break;
        }
        PostLog(L"硬校验失败：" + presentError);
        completed = false;
    }
    LARGE_INTEGER begin = {}, end = {}, frequency = {};
    QueryPerformanceFrequency(&frequency);
    QueryPerformanceCounter(&begin);
    if (completed) {
        PostLog(L"渲染进程校验通过，开始 180 秒正式采集。请保持相同游戏场景。 ");
        completed = WaitPhase(kMeasureSeconds, 15, 85, L"正式采集中", target.pid);
    }
    QueryPerformanceCounter(&end);
    PresentCapture capture = presents.Take();
    TraceData raw = kernel.Take();
    SelectedPresentFrames selectedFrames = SelectPresentFrames(
        std::move(capture), begin.QuadPart, end.QuadPart, frequency.QuadPart);

    Analysis analysis = Analyze(std::move(selectedFrames.frames), raw,
                                begin.QuadPart, end.QuadPart, frequency.QuadPart);
    analysis.frameSource = selectedFrames.source;
    analysis.runtimeFrameTimestamps = selectedFrames.runtimeTimestamps;
    analysis.kernelFrameTimestamps = selectedFrames.kernelTimestamps;
    analysis.runtimeValidIntervals = selectedFrames.runtimeValidIntervals;
    analysis.kernelValidIntervals = selectedFrames.kernelValidIntervals;
    if (!kernelReady) {
        analysis.valid = false;
        analysis.verdict = L"数据无效：内核跟踪未启动";
        analysis.reasons.insert(analysis.reasons.begin(), kernelError);
    }
    if (!presentReady) {
        analysis.valid = false;
        analysis.verdict = L"数据无效：帧跟踪未启动";
        analysis.reasons.insert(analysis.reasons.begin(), presentError);
    }
    if (!completed && g_cancel.load()) {
        analysis.valid = false;
        analysis.verdict = L"测试未完成";
        analysis.reasons.insert(analysis.reasons.begin(), L"测试被用户取消。");
    } else if (!completed && !presentError.empty()) {
        analysis.valid = false;
        analysis.verdict = L"数据无效：未获得兼容帧事件";
        analysis.reasons.insert(analysis.reasons.begin(), presentError);
    } else if (!completed) {
        analysis.valid = false;
        analysis.verdict = L"测试未完成";
        analysis.reasons.insert(analysis.reasons.begin(), L"目标进程退出或采集初始化失败。");
    }

    std::wstring folder = ExeDirectory() + L"\\Pavise-InterruptFabric-结果-" + TimestampText();
    bool written = WriteOutputs(folder, target, analysis, raw, begin.QuadPart, frequency.QuadPart,
                                kernelError, presentError);
    g_app.resultDirectory = folder;
    if (written) PostLog(L"报告已生成：" + folder);
    else PostLog(L"报告写入失败，请把错误截图发回。");
    PostProgress(100, analysis.verdict);
    PostMessageW(g_app.window, kMessageFinished, written ? 1 : 0,
                 reinterpret_cast<LPARAM>(new std::wstring(analysis.verdict)));
    return 0;
}

void SetRunning(bool running) {
    g_app.running = running;
    EnableWindow(g_app.process, !running);
    EnableWindow(g_app.refresh, !running);
    EnableWindow(g_app.start, !running);
    EnableWindow(g_app.cancel, running);
}

void StartSelected() {
    int selected = static_cast<int>(SendMessageW(g_app.process, CB_GETCURSEL, 0, 0));
    if (selected < 0 || static_cast<size_t>(selected) >= g_app.candidates.size()) {
        MessageBoxW(g_app.window, L"请先启动游戏、进入实际画面，然后刷新并选择真正渲染进程。",
                    L"未选择游戏", MB_OK | MB_ICONWARNING);
        return;
    }
    const Candidate target = g_app.candidates[static_cast<size_t>(selected)];
    std::wstring prompt = L"将测试：" + target.exe + L" (PID " + std::to_wstring(target.pid) +
        L")\n\n请确认它是正在渲染游戏画面的进程，不是启动器、登录器、网页助手或反作弊进程。"
        L"\n\n协议：30 秒预热 + 180 秒正式采集。测试期间正常玩，不要切出游戏。";
    if (MessageBoxW(g_app.window, prompt.c_str(), L"确认渲染进程", MB_OKCANCEL | MB_ICONINFORMATION) != IDOK)
        return;
    SetRunning(true);
    SendMessageW(g_app.progress, PBM_SETPOS, 0, 0);
    SetWindowTextW(g_app.status, L"正在初始化 ETW…");
    SetWindowTextW(g_app.log, L"");
    AppendLog(g_app.log, L"目标：" + target.exe + L" / PID " + std::to_wstring(target.pid));
    g_app.worker = CreateThread(nullptr, 0, RunExperiment, new Candidate(target), 0, nullptr);
    if (!g_app.worker) {
        SetRunning(false);
        MessageBoxW(g_app.window, L"无法创建测试线程。", L"错误", MB_OK | MB_ICONERROR);
    }
}

LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
    switch (message) {
    case WM_CREATE: {
        g_app.window = window;
        g_app.bodyFont = CreateFontW(-18, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                    OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                    DEFAULT_PITCH, L"Microsoft YaHei UI");
        g_app.titleFont = CreateFontW(-26, 0, 0, 0, FW_BOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                     OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                     DEFAULT_PITCH, L"Microsoft YaHei UI");
        HWND title = CreateWindowW(L"STATIC", L"Pavise Interrupt Fabric · 外场可行性探测器",
            WS_CHILD | WS_VISIBLE, 24, 18, 750, 36, window, nullptr, nullptr, nullptr);
        HWND description = CreateWindowW(L"STATIC",
            L"只读采集，不实施优化。它要回答：长帧是否被某个设备驱动的 ISR/DPC 重复命中。",
            WS_CHILD | WS_VISIBLE, 25, 58, 820, 28, window, nullptr, nullptr, nullptr);
        HWND warning = CreateWindowW(L"STATIC",
            L"先进入游戏实际画面，再选择真正渲染进程；选错进程会被报告判为数据无效。",
            WS_CHILD | WS_VISIBLE, 25, 91, 820, 28, window, nullptr, nullptr, nullptr);
        g_app.process = CreateWindowW(WC_COMBOBOXW, L"", WS_CHILD | WS_VISIBLE | CBS_DROPDOWNLIST | WS_VSCROLL,
            25, 128, 675, 300, window, reinterpret_cast<HMENU>(kIdProcess), nullptr, nullptr);
        g_app.refresh = CreateWindowW(L"BUTTON", L"刷新进程", WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON,
            715, 127, 120, 34, window, reinterpret_cast<HMENU>(kIdRefresh), nullptr, nullptr);
        g_app.start = CreateWindowW(L"BUTTON", L"开始 3 分钟测试", WS_CHILD | WS_VISIBLE | BS_DEFPUSHBUTTON,
            25, 177, 190, 42, window, reinterpret_cast<HMENU>(kIdStart), nullptr, nullptr);
        g_app.cancel = CreateWindowW(L"BUTTON", L"取消", WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON,
            228, 177, 100, 42, window, reinterpret_cast<HMENU>(kIdCancel), nullptr, nullptr);
        g_app.open = CreateWindowW(L"BUTTON", L"打开结果文件夹", WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON,
            645, 177, 190, 42, window, reinterpret_cast<HMENU>(kIdOpen), nullptr, nullptr);
        g_app.progress = CreateWindowW(PROGRESS_CLASSW, L"", WS_CHILD | WS_VISIBLE | PBS_SMOOTH,
            25, 238, 810, 20, window, reinterpret_cast<HMENU>(kIdProgress), nullptr, nullptr);
        g_app.status = CreateWindowW(L"STATIC", L"准备就绪", WS_CHILD | WS_VISIBLE,
            25, 268, 810, 28, window, reinterpret_cast<HMENU>(kIdStatus), nullptr, nullptr);
        g_app.log = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"",
            WS_CHILD | WS_VISIBLE | ES_MULTILINE | ES_AUTOVSCROLL | ES_READONLY | WS_VSCROLL,
            25, 302, 810, 220, window, reinterpret_cast<HMENU>(kIdLog), nullptr, nullptr);
        SendMessageW(title, WM_SETFONT, reinterpret_cast<WPARAM>(g_app.titleFont), TRUE);
        for (HWND child : {description, warning, g_app.process, g_app.refresh, g_app.start, g_app.cancel,
                           g_app.open, g_app.status, g_app.log})
            SendMessageW(child, WM_SETFONT, reinterpret_cast<WPARAM>(g_app.bodyFont), TRUE);
        EnableWindow(g_app.cancel, FALSE);
        EnableWindow(g_app.open, FALSE);
        RefreshCandidates();
        AppendLog(g_app.log, L"提示：CS2 通常选 cs2.exe；英雄联盟要选 League of Legends.exe，而不是客户端。 ");
        AppendLog(g_app.log, L"请在训练场/回放等无排名风险场景测试，并关闭 WPR、xperf、LatencyMon。 ");
        return 0;
    }
    case WM_COMMAND:
        switch (LOWORD(wParam)) {
        case kIdRefresh: RefreshCandidates(); break;
        case kIdStart: StartSelected(); break;
        case kIdCancel: g_cancel.store(true); SetWindowTextW(g_app.status, L"正在停止并生成未完成报告…"); break;
        case kIdOpen:
            if (!g_app.resultDirectory.empty()) ShellExecuteW(window, L"open", g_app.resultDirectory.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
            break;
        }
        return 0;
    case kMessageLog: {
        std::unique_ptr<std::wstring> text(reinterpret_cast<std::wstring*>(lParam));
        AppendLog(g_app.log, *text);
        return 0;
    }
    case kMessageProgress: {
        std::unique_ptr<std::wstring> text(reinterpret_cast<std::wstring*>(lParam));
        SendMessageW(g_app.progress, PBM_SETPOS, wParam, 0);
        SetWindowTextW(g_app.status, text->c_str());
        return 0;
    }
    case kMessageFinished: {
        std::unique_ptr<std::wstring> verdict(reinterpret_cast<std::wstring*>(lParam));
        if (g_app.worker) { CloseHandle(g_app.worker); g_app.worker = nullptr; }
        SetRunning(false);
        EnableWindow(g_app.open, wParam != 0);
        ShowWindow(window, SW_RESTORE);
        SetForegroundWindow(window);
        if (g_app.closeWhenDone) DestroyWindow(window);
        else MessageBoxW(window, (L"测试结束：" + *verdict + L"\n\n请把结果文件夹内 3 个文件全部发回。 ").c_str(),
                         L"Pavise Interrupt Fabric", MB_OK | (wParam ? MB_ICONINFORMATION : MB_ICONERROR));
        return 0;
    }
    case WM_CLOSE:
        if (g_app.running) {
            if (MessageBoxW(window, L"测试正在进行。是否取消测试并在停止后退出？", L"确认退出",
                            MB_YESNO | MB_ICONWARNING) != IDYES) return 0;
            g_cancel.store(true); g_app.closeWhenDone = true; ShowWindow(window, SW_MINIMIZE); return 0;
        }
        DestroyWindow(window); return 0;
    case WM_DESTROY:
        if (g_app.titleFont) DeleteObject(g_app.titleFont);
        if (g_app.bodyFont) DeleteObject(g_app.bodyFont);
        PostQuitMessage(0); return 0;
    default: return DefWindowProcW(window, message, wParam, lParam);
    }
}

int RunSelfTest() {
    if (ClassifyFrameEvent(kDxgi, 42) != FrameEventKind::Runtime ||
        ClassifyFrameEvent(kDxgi, 55) != FrameEventKind::Runtime ||
        ClassifyFrameEvent(kD3d9, 1) != FrameEventKind::Runtime ||
        ClassifyFrameEvent(kDxgKrnl, 171) != FrameEventKind::Kernel ||
        ClassifyFrameEvent(kDxgKrnl, 184) != FrameEventKind::None) return 16;
    const LONGLONG frequency = 10000000;
    const LONGLONG begin = frequency;
    LONGLONG now = begin;
    std::vector<LONGLONG> frames{now};
    TraceData raw;
    raw.images.push_back({0xfffff80000000000ULL, 0x100000, L"synthetic-net.sys"});
    for (int index = 1; index <= 18000; ++index) {
        LONGLONG interval = (index % 500 == 0) ? 300000 : 100000;
        now += interval;
        frames.push_back(now);
        if (index % 500 == 0) {
            InterruptEvent event;
            event.end = now - 1000;
            event.start = event.end - 2500;
            event.routine = 0xfffff80000001234ULL;
            event.cpu = 2;
            event.target = true;
            raw.interrupts.push_back(event);
            ++raw.allDpcCount;
        }
    }
    PresentCapture sparseRuntime;
    for (int index = 0; index < 50; ++index)
        sparseRuntime.runtimeFrames.push_back(begin + index * 100000);
    sparseRuntime.kernelFrames = frames;
    SelectedPresentFrames kernelSelected = SelectPresentFrames(
        std::move(sparseRuntime), begin, now, frequency);
    if (kernelSelected.source.find(L"PresentHistoryStart") == std::wstring::npos ||
        kernelSelected.kernelValidIntervals <= kernelSelected.runtimeValidIntervals) return 17;

    PresentCapture runtimePreferred;
    runtimePreferred.runtimeFrames = frames;
    for (int index = 0; index < 50; ++index)
        runtimePreferred.kernelFrames.push_back(begin + index * 100000);
    SelectedPresentFrames runtimeSelected = SelectPresentFrames(
        std::move(runtimePreferred), begin, now, frequency);
    if (runtimeSelected.source.find(L"Runtime PresentStart") == std::wstring::npos ||
        runtimeSelected.runtimeValidIntervals <= runtimeSelected.kernelValidIntervals) return 18;

    Analysis analysis = Analyze(frames, raw, begin, now, frequency);
    analysis.frameSource = L"合成 DXGI 帧源";
    if (!analysis.valid || analysis.verdict != L"发现可复验的中断关联候选") return 10;
    TraceData quietRaw;
    Analysis quiet = Analyze(frames, quietRaw, begin, now, frequency);
    quiet.frameSource = L"合成 DXGI 帧源";
    if (!quiet.valid || quiet.verdict != L"未发现可重复的中断长帧关联") return 14;
    std::vector<LONGLONG> wrongProcess{begin, begin + 100000};
    Analysis invalid = Analyze(wrongProcess, quietRaw, begin, now, frequency);
    invalid.frameSource = L"未取得可靠帧源";
    if (invalid.valid || invalid.verdict.find(L"数据无效") == std::wstring::npos) return 15;
    wchar_t temp[MAX_PATH] = {};
    if (!GetTempPathW(_countof(temp), temp)) return 11;
    std::wstring directory = std::wstring(temp) + L"PaviseInterruptFabricSelfTest-" + std::to_wstring(GetCurrentProcessId());
    Candidate target{nullptr, 4242, L"synthetic-game.exe", L"合成测试", 0};
    if (!WriteOutputs(directory, target, analysis, raw, begin, frequency, L"", L"")) return 12;
    std::wstring report = directory + L"\\Pavise-InterruptFabric-报告.txt";
    std::ifstream file(report.c_str(), std::ios::binary);
    unsigned char bom[3] = {};
    file.read(reinterpret_cast<char*>(bom), 3);
    bool validBom = file.gcount() == 3 && bom[0] == 0xef && bom[1] == 0xbb && bom[2] == 0xbf;
    file.close();
    DeleteFileW(report.c_str());
    DeleteFileW((directory + L"\\Pavise-InterruptFabric-逐秒.csv").c_str());
    DeleteFileW((directory + L"\\Pavise-InterruptFabric-关键事件.csv").c_str());
    RemoveDirectoryW(directory.c_str());
    return validBom ? 0 : 13;
}

int RunTraceSmoke() {
    if (!IsElevated()) return 77;
    KernelTrace trace;
    std::wstring error;
    if (!trace.Start(GetCurrentProcessId(), &error)) return 78;
    Sleep(2000);
    TraceData data = trace.Take();
    if (data.allDpcCount + data.allIsrCount == 0) return 79;
    if (data.images.empty()) return 80;
    return 0;
}

int RunUi(HINSTANCE instance) {
    INITCOMMONCONTROLSEX controls = {sizeof(controls), ICC_PROGRESS_CLASS | ICC_STANDARD_CLASSES};
    InitCommonControlsEx(&controls);
    WNDCLASSEXW type = {};
    type.cbSize = sizeof(type);
    type.lpfnWndProc = WindowProc;
    type.hInstance = instance;
    type.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    type.hIcon = LoadIconW(nullptr, IDI_APPLICATION);
    type.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
    type.lpszClassName = L"PaviseInterruptFabricFieldTest";
    if (!RegisterClassExW(&type)) return 20;
    HWND window = CreateWindowExW(0, type.lpszClassName, L"Pavise Interrupt Fabric 外场测试",
        WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX,
        CW_USEDEFAULT, CW_USEDEFAULT, 880, 585, nullptr, nullptr, instance, nullptr);
    if (!window) return 21;
    ShowWindow(window, SW_SHOW);
    UpdateWindow(window);
    MSG message = {};
    while (GetMessageW(&message, nullptr, 0, 0) > 0) {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    return static_cast<int>(message.wParam);
}

} // namespace pavise

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int) {
    const std::wstring command = GetCommandLineW();
    if (command.find(L"--self-test") != std::wstring::npos) return pavise::RunSelfTest();
    if (command.find(L"--trace-smoke") != std::wstring::npos) return pavise::RunTraceSmoke();
    if (!pavise::IsElevated()) {
        if (!pavise::RelaunchElevated())
            MessageBoxW(nullptr, L"内核 ETW 采集需要管理员权限；提权被取消。", L"Pavise Interrupt Fabric",
                        MB_OK | MB_ICONWARNING);
        return 0;
    }
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    int result = pavise::RunUi(instance);
    CoUninitialize();
    return result;
}
