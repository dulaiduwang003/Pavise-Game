// File purpose Pavise Thermal Exchange demo, out-of-process experiment on CPU and GPU sharing a thermal budget
// Never opens game processes, no injection, no driver install, no GPU state changes

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <powrprof.h>
#include <pdh.h>
#include <evntrace.h>
#include <evntcons.h>
#include <shellapi.h>
#include <objbase.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cwchar>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <mutex>
#include <numeric>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

#ifdef _MSC_VER
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "pdh.lib")
#pragma comment(lib, "powrprof.lib")
#endif

namespace {

const GUID kProcessorSubgroup =
    {0x54533251, 0x82be, 0x4824, {0x96, 0xc1, 0x47, 0xb6, 0x0b, 0x74, 0x0d, 0x00}};
const GUID kProcessorMax =
    {0xbc5038f7, 0x23e0, 0x4960, {0x96, 0xda, 0x33, 0xab, 0xaf, 0x59, 0x35, 0xec}};
const GUID kProcessorMaxClass1 =
    {0xbc5038f7, 0x23e0, 0x4960, {0x96, 0xda, 0x33, 0xab, 0xaf, 0x59, 0x35, 0xed}};
const GUID kDxgKrnl =
    {0x802ec45a, 0x1e99, 0x4b83, {0x99, 0x20, 0x87, 0xc9, 0x82, 0x77, 0xba, 0x9d}};
constexpr PDH_STATUS kPdhMoreData = static_cast<PDH_STATUS>(0x800007D2L);
constexpr DWORD kPdhNewData = 1;

std::atomic<bool> g_stop(false);

std::wstring GuidText(const GUID& value) {
    wchar_t text[64] = {};
    StringFromGUID2(value, text, static_cast<int>(_countof(text)));
    return text;
}

bool ParseGuid(const std::wstring& text, GUID* value) {
    return value && SUCCEEDED(CLSIDFromString(text.c_str(), value));
}

std::wstring WinError(DWORD code) {
    wchar_t* message = nullptr;
    FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM |
                       FORMAT_MESSAGE_IGNORE_INSERTS,
                   nullptr, code, 0, reinterpret_cast<wchar_t*>(&message), 0, nullptr);
    std::wstring result = message ? message : L"unknown error";
    if (message) LocalFree(message);
    while (!result.empty() && (result.back() == L'\r' || result.back() == L'\n')) result.pop_back();
    return result;
}

std::wstring TempJournalPath() {
    wchar_t path[MAX_PATH] = {};
    DWORD count = GetTempPathW(_countof(path), path);
    if (!count || count >= _countof(path)) return L"PaviseThermalExchange.recovery";
    return std::wstring(path) + L"PaviseThermalExchange.recovery";
}

std::wstring ExePath() {
    std::vector<wchar_t> path(32768);
    DWORD count = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    return count ? std::wstring(path.data(), count) : L"Pavise.ThermalExchangeDemo.exe";
}

std::wstring Quote(const std::wstring& value) {
    std::wstring result = L"\"";
    for (wchar_t ch : value) {
        if (ch == L'\"') result += L'\\';
        result += ch;
    }
    result += L"\"";
    return result;
}

BOOL WINAPI ConsoleHandler(DWORD code) {
    if (code == CTRL_C_EVENT || code == CTRL_BREAK_EVENT || code == CTRL_CLOSE_EVENT ||
        code == CTRL_LOGOFF_EVENT || code == CTRL_SHUTDOWN_EVENT) {
        g_stop.store(true);
        return TRUE;
    }
    return FALSE;
}

struct Journal {
    GUID original = {};
    GUID temporary = {};
    DWORD ownerPid = 0;
};

bool WriteJournal(const Journal& journal) {
    const std::wstring path = TempJournalPath();
    std::wofstream out(path.c_str(), std::ios::trunc);
    if (!out) return false;
    out << GuidText(journal.original) << L"\n" << GuidText(journal.temporary) << L"\n"
        << journal.ownerPid << L"\n";
    out.flush();
    return out.good();
}

bool ReadJournal(Journal* journal) {
    if (!journal) return false;
    const std::wstring path = TempJournalPath();
    std::wifstream in(path.c_str());
    std::wstring original, temporary;
    DWORD owner = 0;
    if (!in || !std::getline(in, original) || !std::getline(in, temporary) || !(in >> owner)) return false;
    if (!ParseGuid(original, &journal->original) || !ParseGuid(temporary, &journal->temporary)) return false;
    journal->ownerPid = owner;
    return true;
}

bool ProcessAlive(DWORD pid) {
    if (!pid) return false;
    HANDLE process = OpenProcess(SYNCHRONIZE, FALSE, pid);
    if (!process) return false;
    bool alive = WaitForSingleObject(process, 0) == WAIT_TIMEOUT;
    CloseHandle(process);
    return alive;
}

void RemoveJournal() {
    DeleteFileW(TempJournalPath().c_str());
}

void RestoreJournal(const Journal& journal) {
    GUID original = journal.original;
    GUID temporary = journal.temporary;
    PowerSetActiveScheme(nullptr, &original);
    PowerDeleteScheme(nullptr, &temporary);
    RemoveJournal();
}

bool RecoverStaleRun(std::wstring* error) {
    Journal journal;
    if (!ReadJournal(&journal)) return true;
    if (ProcessAlive(journal.ownerPid)) {
        if (error) *error = L"another Thermal Exchange run is still active (pid " +
                            std::to_wstring(journal.ownerPid) + L")";
        return false;
    }
    RestoreJournal(journal);
    return true;
}

int WatchdogMain(DWORD ownerPid, const GUID& original, const GUID& temporary) {
    HANDLE process = OpenProcess(SYNCHRONIZE, FALSE, ownerPid);
    if (process) {
        WaitForSingleObject(process, INFINITE);
        CloseHandle(process);
    }
    Journal journal{original, temporary, ownerPid};
    RestoreJournal(journal);
    return 0;
}

bool SpawnWatchdog(const Journal& journal, std::wstring* error) {
    std::wstring command = Quote(ExePath()) + L" --watchdog " + std::to_wstring(journal.ownerPid) + L" " +
                           GuidText(journal.original) + L" " + GuidText(journal.temporary);
    std::vector<wchar_t> mutableCommand(command.begin(), command.end());
    mutableCommand.push_back(L'\0');
    STARTUPINFOW startup = {};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process = {};
    BOOL ok = CreateProcessW(nullptr, mutableCommand.data(), nullptr, nullptr, FALSE,
                             CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP, nullptr, nullptr,
                             &startup, &process);
    if (!ok) {
        if (error) *error = L"watchdog start failed: " + WinError(GetLastError());
        return false;
    }
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return true;
}

class PowerSandbox {
public:
    ~PowerSandbox() { Restore(); }

    bool Create(bool activate, std::wstring* error) {
        GUID* active = nullptr;
        DWORD rc = PowerGetActiveScheme(nullptr, &active);
        if (rc != ERROR_SUCCESS || !active) return Fail(error, L"PowerGetActiveScheme", rc);
        original_ = *active;
        LocalFree(active);

        GUID* duplicate = nullptr;
        rc = PowerDuplicateScheme(nullptr, &original_, &duplicate);
        if (rc != ERROR_SUCCESS || !duplicate) return Fail(error, L"PowerDuplicateScheme", rc);
        temporary_ = *duplicate;
        LocalFree(duplicate);
        created_ = true;

        const std::wstring name = L"Pavise Thermal Exchange (temporary)";
        PowerWriteFriendlyName(nullptr, &temporary_, nullptr, nullptr,
                               reinterpret_cast<UCHAR*>(const_cast<wchar_t*>(name.c_str())),
                               static_cast<DWORD>((name.size() + 1) * sizeof(wchar_t)));

        if (!activate) return true;
        Journal journal{original_, temporary_, GetCurrentProcessId()};
        if (!WriteJournal(journal)) {
            Restore();
            if (error) *error = L"could not write recovery journal";
            return false;
        }
        if (!SpawnWatchdog(journal, error)) {
            Restore();
            return false;
        }
        rc = PowerSetActiveScheme(nullptr, &temporary_);
        if (rc != ERROR_SUCCESS) {
            Restore();
            return Fail(error, L"PowerSetActiveScheme", rc);
        }
        active_ = true;
        return true;
    }

    bool SetCpuMaximum(DWORD percent, std::wstring* error) {
        if (!active_ || percent < 1 || percent > 100) {
            if (error) *error = L"invalid CPU ceiling or inactive sandbox";
            return false;
        }
        DWORD rc = WritePair(kProcessorMax, percent);
        if (rc != ERROR_SUCCESS) return Fail(error, L"write CPU maximum", rc);

        // Hybrid parts expose an extra processor maximum setting, fine if it is missing
        WritePair(kProcessorMaxClass1, percent);
        rc = PowerSetActiveScheme(nullptr, &temporary_);
        if (rc != ERROR_SUCCESS) return Fail(error, L"apply CPU maximum", rc);
        return true;
    }

    void Restore() {
        if (!created_) return;
        if (active_) PowerSetActiveScheme(nullptr, &original_);
        PowerDeleteScheme(nullptr, &temporary_);
        RemoveJournal();
        active_ = false;
        created_ = false;
    }

private:
    DWORD WritePair(const GUID& setting, DWORD value) {
        DWORD ac = PowerWriteACValueIndex(nullptr, &temporary_, &kProcessorSubgroup, &setting, value);
        DWORD dc = PowerWriteDCValueIndex(nullptr, &temporary_, &kProcessorSubgroup, &setting, value);
        return ac != ERROR_SUCCESS ? ac : dc;
    }

    static bool Fail(std::wstring* error, const std::wstring& where, DWORD code) {
        if (error) *error = where + L" failed (" + std::to_wstring(code) + L"): " + WinError(code);
        return false;
    }

    GUID original_ = {};
    GUID temporary_ = {};
    bool created_ = false;
    bool active_ = false;
};

class CpuMeter {
public:
    double Sample() {
        FILETIME idle = {}, kernel = {}, user = {};
        if (!GetSystemTimes(&idle, &kernel, &user)) return NAN;
        ULARGE_INTEGER i = {}, k = {}, u = {};
        i.LowPart = idle.dwLowDateTime; i.HighPart = idle.dwHighDateTime;
        k.LowPart = kernel.dwLowDateTime; k.HighPart = kernel.dwHighDateTime;
        u.LowPart = user.dwLowDateTime; u.HighPart = user.dwHighDateTime;
        if (!ready_) {
            idle_ = i.QuadPart; kernel_ = k.QuadPart; user_ = u.QuadPart; ready_ = true;
            return NAN;
        }
        uint64_t idleDelta = i.QuadPart - idle_;
        uint64_t totalDelta = (k.QuadPart - kernel_) + (u.QuadPart - user_);
        idle_ = i.QuadPart; kernel_ = k.QuadPart; user_ = u.QuadPart;
        if (!totalDelta) return NAN;
        return std::clamp(100.0 * (1.0 - static_cast<double>(idleDelta) / totalDelta), 0.0, 100.0);
    }
private:
    bool ready_ = false;
    uint64_t idle_ = 0, kernel_ = 0, user_ = 0;
};

class GpuPdhMeter {
public:
    ~GpuPdhMeter() { if (query_) PdhCloseQuery(query_); }

    bool Start() {
        if (PdhOpenQueryW(nullptr, 0, &query_) != ERROR_SUCCESS) return false;
        if (PdhAddEnglishCounterW(query_, L"\\GPU Engine(*engtype_3D)\\Utilization Percentage", 0,
                                  &counter_) != ERROR_SUCCESS) return false;
        return PdhCollectQueryData(query_) == ERROR_SUCCESS;
    }

    double Sample(DWORD targetPid) {
        if (!query_ || PdhCollectQueryData(query_) != ERROR_SUCCESS) return NAN;
        DWORD bytes = 0, count = 0;
        PDH_STATUS status = PdhGetFormattedCounterArrayW(counter_, PDH_FMT_DOUBLE, &bytes, &count, nullptr);
        if (status != kPdhMoreData || !bytes) return NAN;
        std::vector<BYTE> buffer(bytes);
        auto* items = reinterpret_cast<PDH_FMT_COUNTERVALUE_ITEM_W*>(buffer.data());
        status = PdhGetFormattedCounterArrayW(counter_, PDH_FMT_DOUBLE, &bytes, &count, items);
        if (status != ERROR_SUCCESS) return NAN;
        double total = 0.0;
        bool found = false;
        for (DWORD index = 0; index < count; ++index) {
            if (items[index].FmtValue.CStatus > kPdhNewData) continue;
            DWORD pid = ParsePid(items[index].szName);
            if (targetPid && pid != targetPid) continue;
            if (!targetPid && !pid) continue;
            total += std::max(0.0, items[index].FmtValue.doubleValue);
            found = true;
        }
        return found ? total : NAN;
    }

private:
    static DWORD ParsePid(const wchar_t* name) {
        if (!name || _wcsnicmp(name, L"pid_", 4) != 0) return 0;
        wchar_t* end = nullptr;
        unsigned long pid = wcstoul(name + 4, &end, 10);
        return end != name + 4 && end && *end == L'_' ? static_cast<DWORD>(pid) : 0;
    }
    PDH_HQUERY query_ = nullptr;
    PDH_HCOUNTER counter_ = nullptr;
};

class NvmlMeter {
public:
    struct Reading { double clockMHz = NAN, powerW = NAN, tempC = NAN; };
    ~NvmlMeter() { Stop(); }

    bool Start(std::wstring* status) {
        module_ = LoadLibraryW(L"nvml.dll");
        if (!module_) {
            if (status) *status = L"NVML unavailable; clock/power/temperature columns will be empty";
            return false;
        }
        init_ = Load<InitFn>("nvmlInit_v2");
        shutdown_ = Load<ShutdownFn>("nvmlShutdown");
        getHandle_ = Load<GetHandleFn>("nvmlDeviceGetHandleByIndex_v2");
        getClock_ = Load<GetClockFn>("nvmlDeviceGetClockInfo");
        getPower_ = Load<GetPowerFn>("nvmlDeviceGetPowerUsage");
        getTemp_ = Load<GetTempFn>("nvmlDeviceGetTemperature");
        if (!init_ || !shutdown_ || !getHandle_ || !getClock_ || !getPower_ || !getTemp_ || init_() != 0 ||
            getHandle_(0, &device_) != 0) {
            if (status) *status = L"NVML initialization failed; vendor telemetry disabled";
            Stop();
            return false;
        }
        initialized_ = true;
        if (status) *status = L"NVML telemetry enabled (GPU index 0)";
        return true;
    }

    Reading Sample() const {
        Reading reading;
        if (!initialized_) return reading;
        unsigned int value = 0;
        if (getClock_(device_, 0, &value) == 0) reading.clockMHz = value; // NVML_CLOCK_GRAPHICS
        if (getPower_(device_, &value) == 0) reading.powerW = value / 1000.0;
        if (getTemp_(device_, 0, &value) == 0) reading.tempC = value; // NVML_TEMPERATURE_GPU
        return reading;
    }

private:
    using Device = void*;
    using InitFn = int (*)();
    using ShutdownFn = int (*)();
    using GetHandleFn = int (*)(unsigned int, Device*);
    using GetClockFn = int (*)(Device, unsigned int, unsigned int*);
    using GetPowerFn = int (*)(Device, unsigned int*);
    using GetTempFn = int (*)(Device, unsigned int, unsigned int*);
    template <typename T> T Load(const char* name) { return reinterpret_cast<T>(GetProcAddress(module_, name)); }
    void Stop() {
        if (initialized_ && shutdown_) shutdown_();
        initialized_ = false;
        device_ = nullptr;
        if (module_) FreeLibrary(module_);
        module_ = nullptr;
    }
    HMODULE module_ = nullptr;
    Device device_ = nullptr;
    InitFn init_ = nullptr;
    ShutdownFn shutdown_ = nullptr;
    GetHandleFn getHandle_ = nullptr;
    GetClockFn getClock_ = nullptr;
    GetPowerFn getPower_ = nullptr;
    GetTempFn getTemp_ = nullptr;
    bool initialized_ = false;
};

constexpr DWORD kSharedFrameMagic = 0x46585450; // PTXF
constexpr DWORD kSharedFrameVersion = 1;
constexpr DWORD kSharedFrameCapacity = 262144;

struct SharedFrameData {
    DWORD magic, version, capacity, reserved;
    volatile LONGLONG count;
    LONGLONG frequency;
    LONGLONG stamps[kSharedFrameCapacity];
};

class SharedFrameTrace {
public:
    ~SharedFrameTrace() {
        if (data_) UnmapViewOfFile(data_);
        if (mapping_) CloseHandle(mapping_);
    }

    bool Start(DWORD pid) {
        std::wstring name = L"Local\\PaviseThermalGpuLoad_" + std::to_wstring(pid);
        mapping_ = OpenFileMappingW(FILE_MAP_READ, FALSE, name.c_str());
        if (!mapping_) return false;
        data_ = static_cast<const SharedFrameData*>(MapViewOfFile(mapping_, FILE_MAP_READ, 0, 0,
                                                                  sizeof(SharedFrameData)));
        if (!data_ || data_->magic != kSharedFrameMagic || data_->version != kSharedFrameVersion ||
            data_->capacity != kSharedFrameCapacity) {
            if (data_) { UnmapViewOfFile(data_); data_ = nullptr; }
            CloseHandle(mapping_); mapping_ = nullptr;
            return false;
        }
        return true;
    }

    std::vector<LONGLONG> Snapshot() const {
        std::vector<LONGLONG> result;
        if (!data_) return result;
        MemoryBarrier();
        LONGLONG count = data_->count;
        if (count <= 0) return result;
        size_t available = static_cast<size_t>(std::min<LONGLONG>(count, kSharedFrameCapacity));
        result.assign(data_->stamps, data_->stamps + available);
        return result;
    }

private:
    HANDLE mapping_ = nullptr;
    const SharedFrameData* data_ = nullptr;
};

class PresentTrace {
public:
    ~PresentTrace() { Stop(); }

    bool Start(DWORD targetPid, std::wstring* error) {
        targetPid_ = targetPid;
        sessionName_ = L"PaviseThermalPresent_" + std::to_wstring(GetCurrentProcessId());
        const size_t bytes = sizeof(EVENT_TRACE_PROPERTIES) +
                             (sessionName_.size() + 1) * sizeof(wchar_t);
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

        ULONG rc = StartTraceW(&session_, sessionName_.c_str(), props);
        if (rc != ERROR_SUCCESS) return Fail(error, L"StartTrace", rc);
        rc = EnableTraceEx2(session_, &kDxgKrnl, EVENT_CONTROL_CODE_ENABLE_PROVIDER,
                            TRACE_LEVEL_VERBOSE, ~0ULL, 0, 0, nullptr);
        if (rc != ERROR_SUCCESS) {
            Stop();
            return Fail(error, L"EnableTraceEx2(DxgKrnl)", rc);
        }

        EVENT_TRACE_LOGFILEW log = {};
        log.LoggerName = const_cast<LPWSTR>(sessionName_.c_str());
        log.ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD;
        log.EventRecordCallback = &PresentTrace::OnEventStatic;
        log.Context = this;
        trace_ = OpenTraceW(&log);
        if (trace_ == INVALID_PROCESSTRACE_HANDLE) {
            rc = GetLastError();
            Stop();
            return Fail(error, L"OpenTrace", rc);
        }
        thread_ = CreateThread(nullptr, 0, &PresentTrace::ThreadProc, this, 0, nullptr);
        if (!thread_) {
            rc = GetLastError();
            Stop();
            return Fail(error, L"CreateThread", rc);
        }
        return true;
    }

    void Stop() {
        if (session_) {
            ControlTraceW(session_, sessionName_.c_str(),
                          properties_.empty() ? nullptr : reinterpret_cast<EVENT_TRACE_PROPERTIES*>(properties_.data()),
                          EVENT_TRACE_CONTROL_STOP);
            session_ = 0;
        }
        if (trace_ != INVALID_PROCESSTRACE_HANDLE) {
            CloseTrace(trace_);
            trace_ = INVALID_PROCESSTRACE_HANDLE;
        }
        if (thread_) {
            WaitForSingleObject(thread_, 3000);
            CloseHandle(thread_);
            thread_ = nullptr;
        }
    }

    std::vector<LONGLONG> Snapshot() const {
        std::lock_guard<std::mutex> lock(mutex_);
        return stamps_;
    }

private:
    static bool Fail(std::wstring* error, const std::wstring& where, ULONG code) {
        if (error) *error = where + L" failed (" + std::to_wstring(code) + L"): " + WinError(code);
        return false;
    }
    static VOID WINAPI OnEventStatic(PEVENT_RECORD event) {
        auto* self = static_cast<PresentTrace*>(event->UserContext);
        if (self) self->OnEvent(event);
    }
    void OnEvent(PEVENT_RECORD event) {
        if (!IsEqualGUID(event->EventHeader.ProviderId, kDxgKrnl) ||
            event->EventHeader.ProcessId != targetPid_ || event->EventHeader.EventDescriptor.Id != 184) return;
        std::lock_guard<std::mutex> lock(mutex_);
        if (stamps_.size() < 2000000) stamps_.push_back(event->EventHeader.TimeStamp.QuadPart);
    }
    static DWORD WINAPI ThreadProc(void* context) {
        auto* self = static_cast<PresentTrace*>(context);
        TRACEHANDLE handle = self->trace_;
        ProcessTrace(&handle, 1, nullptr, nullptr);
        return 0;
    }
    DWORD targetPid_ = 0;
    std::wstring sessionName_;
    TRACEHANDLE session_ = 0;
    TRACEHANDLE trace_ = INVALID_PROCESSTRACE_HANDLE;
    HANDLE thread_ = nullptr;
    std::vector<BYTE> properties_;
    mutable std::mutex mutex_;
    std::vector<LONGLONG> stamps_;
};

struct Args {
    enum class Mode { Help, Probe, SelfTest, Run, Watchdog } mode = Mode::Help;
    DWORD pid = 0;
    int stageSeconds = 30;
    int settleSeconds = 8;
    int probeSeconds = 10;
    std::vector<int> caps{100, 95, 90, 85, 90, 95, 100};
    DWORD watchdogPid = 0;
    GUID watchdogOriginal = {};
    GUID watchdogTemporary = {};
};

bool ParseInteger(const wchar_t* text, int* value) {
    if (!text || !value) return false;
    wchar_t* end = nullptr;
    long parsed = wcstol(text, &end, 10);
    if (end == text || *end) return false;
    *value = static_cast<int>(parsed);
    return true;
}

bool ParseCaps(const std::wstring& text, std::vector<int>* caps) {
    std::vector<int> parsed;
    std::wstringstream stream(text);
    std::wstring part;
    while (std::getline(stream, part, L',')) {
        int value = 0;
        if (!ParseInteger(part.c_str(), &value) || value < 50 || value > 100) return false;
        parsed.push_back(value);
    }
    if (parsed.empty()) return false;
    *caps = parsed;
    return true;
}

bool ParseArgs(int argc, wchar_t** argv, Args* args, std::wstring* error) {
    if (!args) return false;
    for (int index = 1; index < argc; ++index) {
        std::wstring arg = argv[index];
        if (arg == L"--probe") args->mode = Args::Mode::Probe;
        else if (arg == L"--self-test") args->mode = Args::Mode::SelfTest;
        else if (arg == L"--run") args->mode = Args::Mode::Run;
        else if (arg == L"--pid" && index + 1 < argc) {
            int value = 0;
            if (!ParseInteger(argv[++index], &value) || value <= 0) return false;
            args->pid = static_cast<DWORD>(value);
        } else if (arg == L"--stage-seconds" && index + 1 < argc) {
            if (!ParseInteger(argv[++index], &args->stageSeconds) || args->stageSeconds < 5) return false;
        } else if (arg == L"--settle-seconds" && index + 1 < argc) {
            if (!ParseInteger(argv[++index], &args->settleSeconds) || args->settleSeconds < 0) return false;
        } else if (arg == L"--seconds" && index + 1 < argc) {
            if (!ParseInteger(argv[++index], &args->probeSeconds) || args->probeSeconds < 2) return false;
        } else if (arg == L"--caps" && index + 1 < argc) {
            if (!ParseCaps(argv[++index], &args->caps)) return false;
        } else if (arg == L"--watchdog" && index + 3 < argc) {
            int pid = 0;
            if (!ParseInteger(argv[++index], &pid) || pid <= 0 ||
                !ParseGuid(argv[++index], &args->watchdogOriginal) ||
                !ParseGuid(argv[++index], &args->watchdogTemporary)) return false;
            args->watchdogPid = static_cast<DWORD>(pid);
            args->mode = Args::Mode::Watchdog;
        } else if (arg == L"--help" || arg == L"-h" || arg == L"/?") {
            args->mode = Args::Mode::Help;
        } else {
            if (error) *error = L"unknown or invalid argument: " + arg;
            return false;
        }
    }
    if (args->mode == Args::Mode::Run && !args->pid) {
        if (error) *error = L"--run requires --pid <game process id>";
        return false;
    }
    return true;
}

struct Row {
    int stage = 0, cap = 100, second = 0;
    double cpu = NAN, gpu = NAN, clock = NAN, power = NAN, temp = NAN;
};

double Mean(const std::vector<double>& values) {
    double sum = 0.0;
    size_t count = 0;
    for (double value : values) if (!std::isnan(value)) { sum += value; ++count; }
    return count ? sum / count : NAN;
}

double Percentile(std::vector<double> values, double p) {
    if (values.empty()) return NAN;
    std::sort(values.begin(), values.end());
    double position = p * (values.size() - 1);
    size_t low = static_cast<size_t>(position);
    size_t high = std::min(low + 1, values.size() - 1);
    double fraction = position - low;
    return values[low] * (1.0 - fraction) + values[high] * fraction;
}

struct StageResult {
    int stage = 0, cap = 100;
    size_t frames = 0;
    double fps = NAN, p99ms = NAN, oneLowFps = NAN;
    double cpu = NAN, gpu = NAN, clock = NAN, power = NAN, temp = NAN;
};

StageResult SummarizeStage(int stage, int cap, const std::vector<Row>& rows,
                           const std::vector<LONGLONG>& stamps, size_t beginStamp,
                           LARGE_INTEGER frequency) {
    StageResult result;
    result.stage = stage; result.cap = cap;
    std::vector<double> cpu, gpu, clock, power, temp, intervals;
    for (const Row& row : rows) {
        if (row.stage != stage) continue;
        cpu.push_back(row.cpu); gpu.push_back(row.gpu); clock.push_back(row.clock);
        power.push_back(row.power); temp.push_back(row.temp);
    }
    for (size_t i = std::max<size_t>(beginStamp + 1, 1); i < stamps.size(); ++i) {
        double ms = (stamps[i] - stamps[i - 1]) * 1000.0 / frequency.QuadPart;
        if (ms > 0.05 && ms < 1000.0) intervals.push_back(ms);
    }
    result.frames = intervals.size();
    if (!intervals.empty()) {
        double meanMs = std::accumulate(intervals.begin(), intervals.end(), 0.0) / intervals.size();
        result.fps = 1000.0 / meanMs;
        result.p99ms = Percentile(intervals, 0.99);
        result.oneLowFps = result.p99ms > 0 ? 1000.0 / result.p99ms : NAN;
    }
    result.cpu = Mean(cpu); result.gpu = Mean(gpu); result.clock = Mean(clock);
    result.power = Mean(power); result.temp = Mean(temp);
    return result;
}

void CsvNumber(std::wofstream& out, double value) {
    if (!std::isnan(value)) out << std::fixed << std::setprecision(2) << value;
}

std::wstring TimestampedCsvPath() {
    SYSTEMTIME time = {};
    GetLocalTime(&time);
    wchar_t name[128] = {};
    swprintf_s(name, L"Pavise-ThermalExchange-%04u%02u%02u-%02u%02u%02u.csv",
               time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute, time.wSecond);
    return name;
}

bool WriteCsv(const std::wstring& path, const std::vector<Row>& rows,
              const std::vector<StageResult>& stages) {
    std::wofstream out(path.c_str(), std::ios::trunc);
    if (!out) return false;
    out << L"kind,stage,cpu_max_percent,second,cpu_percent,gpu_3d_percent,gpu_clock_mhz,gpu_power_w,gpu_temp_c,frames,fps,p99_ms,one_percent_low_fps\n";
    for (const Row& row : rows) {
        out << L"sample," << row.stage << L',' << row.cap << L',' << row.second << L',';
        CsvNumber(out, row.cpu); out << L','; CsvNumber(out, row.gpu); out << L',';
        CsvNumber(out, row.clock); out << L','; CsvNumber(out, row.power); out << L',';
        CsvNumber(out, row.temp); out << L",,,,\n";
    }
    for (const StageResult& stage : stages) {
        out << L"summary," << stage.stage << L',' << stage.cap << L",,";
        CsvNumber(out, stage.cpu); out << L','; CsvNumber(out, stage.gpu); out << L',';
        CsvNumber(out, stage.clock); out << L','; CsvNumber(out, stage.power); out << L',';
        CsvNumber(out, stage.temp); out << L',' << stage.frames << L',';
        CsvNumber(out, stage.fps); out << L','; CsvNumber(out, stage.p99ms); out << L',';
        CsvNumber(out, stage.oneLowFps); out << L'\n';
    }
    return out.good();
}

void PrintValue(double value, const wchar_t* suffix = L"") {
    if (std::isnan(value)) std::wcout << L"n/a";
    else std::wcout << std::fixed << std::setprecision(1) << value << suffix;
}

int ProbeMain(const Args& args) {
    CpuMeter cpu;
    GpuPdhMeter gpu;
    NvmlMeter nvml;
    std::wstring nvmlStatus;
    bool gpuReady = gpu.Start();
    nvml.Start(&nvmlStatus);
    std::wcout << L"Read-only probe: " << args.probeSeconds << L" seconds\n" << nvmlStatus << L"\n";
    cpu.Sample();
    for (int second = 1; second <= args.probeSeconds && !g_stop.load(); ++second) {
        Sleep(1000);
        NvmlMeter::Reading n = nvml.Sample();
        std::wcout << L"[" << second << L"] CPU "; PrintValue(cpu.Sample(), L"%");
        std::wcout << L" | GPU3D "; PrintValue(gpuReady ? gpu.Sample(args.pid) : NAN, L"%");
        std::wcout << L" | clock "; PrintValue(n.clockMHz, L" MHz");
        std::wcout << L" | power "; PrintValue(n.powerW, L" W");
        std::wcout << L" | temp "; PrintValue(n.tempC, L" C"); std::wcout << L"\n";
    }
    return 0;
}

int SelfTestMain() {
    std::wstring error;
    if (!RecoverStaleRun(&error)) {
        std::wcerr << L"Recovery check failed: " << error << L"\n";
        return 2;
    }
    PowerSandbox sandbox;
    if (!sandbox.Create(false, &error)) {
        std::wcerr << L"Power sandbox create/delete test failed: " << error << L"\n";
        return 3;
    }
    sandbox.Restore();
    GpuPdhMeter gpu;
    bool pdh = gpu.Start();
    NvmlMeter nvml;
    std::wstring nvmlStatus;
    bool vendor = nvml.Start(&nvmlStatus);
    std::wcout << L"SELFTEST PASS\n"
               << L"- temporary power scheme duplicated and deleted without activation\n"
               << L"- GPU Engine PDH: " << (pdh ? L"available" : L"unavailable") << L"\n"
               << L"- NVML: " << (vendor ? L"available" : L"optional/unavailable") << L"\n";
    return 0;
}

int RunMain(const Args& args) {
    std::wstring error;
    if (!RecoverStaleRun(&error)) {
        std::wcerr << error << L"\n";
        return 3;
    }
    PowerSandbox sandbox;
    if (!sandbox.Create(true, &error)) {
        std::wcerr << L"Power sandbox failed: " << error << L"\n";
        return 4;
    }

    SharedFrameTrace sharedTrace;
    bool sharedReady = sharedTrace.Start(args.pid);
    PresentTrace trace;
    bool traceReady = sharedReady ? false : trace.Start(args.pid, &error);
    if (sharedReady) std::wcout << L"Pavise workload shared frame telemetry enabled\n";
    else if (!traceReady) std::wcerr << L"Present ETW unavailable: " << error << L" (telemetry continues)\n";
    GpuPdhMeter gpu;
    bool gpuReady = gpu.Start();
    NvmlMeter nvml;
    std::wstring nvmlStatus;
    nvml.Start(&nvmlStatus);
    std::wcout << nvmlStatus << L"\n";

    CpuMeter cpu;
    cpu.Sample();
    LARGE_INTEGER frequency = {};
    QueryPerformanceFrequency(&frequency);
    std::vector<Row> rows;
    std::vector<StageResult> results;

    for (size_t stageIndex = 0; stageIndex < args.caps.size() && !g_stop.load(); ++stageIndex) {
        int cap = args.caps[stageIndex];
        if (!sandbox.SetCpuMaximum(cap, &error)) {
            std::wcerr << L"Could not set cap " << cap << L"%: " << error << L"\n";
            break;
        }
        std::wcout << L"Stage " << (stageIndex + 1) << L"/" << args.caps.size()
                   << L": CPU max " << cap << L"%, settling " << args.settleSeconds << L"s\n";
        for (int second = 0; second < args.settleSeconds && !g_stop.load(); ++second) Sleep(1000);
        std::vector<LONGLONG> before = sharedReady ? sharedTrace.Snapshot() :
                                             (traceReady ? trace.Snapshot() : std::vector<LONGLONG>());
        size_t beginStamp = before.size();
        for (int second = 1; second <= args.stageSeconds && !g_stop.load(); ++second) {
            Sleep(1000);
            NvmlMeter::Reading n = nvml.Sample();
            Row row;
            row.stage = static_cast<int>(stageIndex + 1); row.cap = cap; row.second = second;
            row.cpu = cpu.Sample(); row.gpu = gpuReady ? gpu.Sample(args.pid) : NAN;
            row.clock = n.clockMHz; row.power = n.powerW; row.temp = n.tempC;
            rows.push_back(row);
        }
        std::vector<LONGLONG> after = sharedReady ? sharedTrace.Snapshot() :
                                            (traceReady ? trace.Snapshot() : std::vector<LONGLONG>());
        StageResult result = SummarizeStage(static_cast<int>(stageIndex + 1), cap, rows, after,
                                            beginStamp, frequency);
        results.push_back(result);
        std::wcout << L"  FPS "; PrintValue(result.fps); std::wcout << L" | 1% low ";
        PrintValue(result.oneLowFps); std::wcout << L" | GPU clock "; PrintValue(result.clock, L" MHz");
        std::wcout << L" | GPU power "; PrintValue(result.power, L" W"); std::wcout << L"\n";
    }

    trace.Stop();
    sandbox.Restore();
    std::wstring csv = TimestampedCsvPath();
    if (WriteCsv(csv, rows, results)) std::wcout << L"CSV: " << csv << L"\n";
    else std::wcerr << L"Could not write CSV output.\n";
    std::wcout << (g_stop.load() ? L"Stopped; original power plan restored.\n" :
                                      L"Complete; original power plan restored.\n");
    return results.empty() ? 5 : 0;
}

void PrintHelp() {
    std::wcout <<
        L"Pavise Thermal Exchange Demo\n\n"
        L"Safe/read-only:\n"
        L"  Pavise.ThermalExchangeDemo.exe --probe [--seconds 10] [--pid N]\n"
        L"  Pavise.ThermalExchangeDemo.exe --self-test\n\n"
        L"Experiment (changes only a temporary duplicated power plan):\n"
        L"  Pavise.ThermalExchangeDemo.exe --run --pid N [--stage-seconds 30]\n"
        L"      [--settle-seconds 8] [--caps 100,95,90,85,90,95,100]\n\n"
        L"The demo does not open the target process, inject code, install a driver, or alter GPU state.\n";
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    SetConsoleCtrlHandler(ConsoleHandler, TRUE);
    Args args;
    std::wstring error;
    if (!ParseArgs(argc, argv, &args, &error)) {
        if (!error.empty()) std::wcerr << error << L"\n";
        PrintHelp();
        return 1;
    }
    if (args.mode == Args::Mode::Watchdog)
        return WatchdogMain(args.watchdogPid, args.watchdogOriginal, args.watchdogTemporary);
    if (args.mode == Args::Mode::Help) { PrintHelp(); return 0; }
    if (args.mode == Args::Mode::Probe) return ProbeMain(args);
    if (args.mode == Args::Mode::SelfTest) return SelfTestMain();
    return RunMain(args);
}
