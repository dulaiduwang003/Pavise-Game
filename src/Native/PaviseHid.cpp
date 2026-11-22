#include <windows.h>
#include <setupapi.h>
#include <stddef.h>
#include <stdint.h>

namespace
{
    enum Result : int32_t
    {
        Absent = 0,
        Holding = 1,
        NoInterface = 2,
        ProcessUnreadable = 3,
        InterfaceUnreadable = 5
    };

    constexpr ULONG ProcessHandleInformation = 51;
    constexpr ULONG ObjectNameInformation = 1;
    constexpr ULONG FileFsDeviceInformation = 4;
    constexpr LONG StatusInfoLengthMismatch = static_cast<LONG>(0xC0000004u);
    constexpr LONG StatusBufferTooSmall = static_cast<LONG>(0xC0000023u);
    constexpr LONG StatusPending = 0x103;
    constexpr ULONG MaxSnapshotBytes = 64u << 20;
    constexpr ULONG ScratchBytes = 16u << 10;
    constexpr size_t MaxDevices = 1024;
    constexpr size_t MaxDeviceName = 512;

    constexpr GUID HidInterface = {0x4d1e55b2, 0xf16f, 0x11cf, {0x88, 0xcb, 0x00, 0x11, 0x11, 0x00, 0x00, 0x30}};
    constexpr wchar_t DevicePrefix[] = L"\\Device\\";
    constexpr wchar_t GlobalRoot[] = L"\\\\?\\GLOBALROOT";

    using QueryProcess = LONG (NTAPI*)(HANDLE, ULONG, void*, ULONG, ULONG*);
    using QueryObject = LONG (NTAPI*)(HANDLE, ULONG, void*, ULONG, ULONG*);
    using QueryVolume = LONG (NTAPI*)(HANDLE, void*, void*, ULONG, ULONG);

    // 句柄表是当前进程的快照 结构大小与偏移按 Windows x64 布局核对
    struct HandleEntry
    {
        HANDLE Value;
        ULONG_PTR HandleCount;
        ULONG_PTR PointerCount;
        ULONG GrantedAccess;
        ULONG ObjectTypeIndex;
        ULONG Attributes;
        ULONG Reserved;
    };

    struct HandleTable
    {
        ULONG_PTR Count;
        ULONG_PTR Reserved;
        HandleEntry Entries[1];
    };

    static_assert(sizeof(HandleEntry) == 40, "ProcessHandleInformation entry size");
    static_assert(offsetof(HandleEntry, ObjectTypeIndex) == 28, "ProcessHandleInformation type offset");
    static_assert(offsetof(HandleTable, Entries) == 16, "ProcessHandleInformation entries offset");

    struct ObjectName
    {
        USHORT Length;
        USHORT Capacity;
        const wchar_t* Buffer;
    };

    struct VolumeDevice
    {
        ULONG Type;
        ULONG Characteristics;
    };

    struct IoStatus
    {
        volatile LONG Status;
        ULONG Reserved;
        ULONG_PTR Information;
    };

    static_assert(sizeof(IoStatus) == 16, "IO_STATUS_BLOCK size");

    struct Device
    {
        wchar_t Name[MaxDeviceName];
        int Length;
        ULONG Type;
        ULONG Traits;
    };

    struct Scanner
    {
        HANDLE Heap;
        QueryProcess ReadProcess;
        QueryObject ReadObject;
        QueryVolume ReadVolume;
        Device* Devices;
        size_t DeviceCount;
        size_t DeviceCapacity;
        bool Incomplete;
        ULONG FileTypeIndex;
        HandleTable* Table;
        ULONG TableSize;
        void* Scratch;
    };

    template<size_t Size>
    bool StartsWith(const wchar_t* text, size_t length, const wchar_t (&prefix)[Size])
    {
        return length >= Size - 1
            && CompareStringOrdinal(text, static_cast<int>(Size - 1), prefix, static_cast<int>(Size - 1), TRUE) == CSTR_EQUAL;
    }

    bool ValidHandle(HANDLE handle)
    {
        return handle != nullptr && handle != INVALID_HANDLE_VALUE;
    }

    template<class Procedure>
    Procedure Find(HMODULE module, const char* name)
    {
        return reinterpret_cast<Procedure>(reinterpret_cast<uintptr_t>(GetProcAddress(module, name)));
    }

    bool IsVendorCollection(const wchar_t* text, size_t length)
    {
        constexpr wchar_t gameCollection[] = L"HID_DEVICE_SYSTEM_GAME";
        return StartsWith(text, length, L"HID_DEVICE_UP:FF")
            || StartsWith(text, length, L"HID_DEVICE_UPR:FF")
            || (length == _countof(gameCollection) - 1 && StartsWith(text, length, gameCollection));
    }

    bool HasDedicatedCollection(const wchar_t* ids, size_t count)
    {
        size_t begin = 0;
        for (size_t end = 0; end < count; ++end)
        {
            if (ids[end] != L'\0') continue;
            if (end == begin) break;
            if (IsVendorCollection(ids + begin, end - begin)) return true;
            begin = end + 1;
        }
        return false;
    }

    bool AddDevice(Scanner& scanner, const wchar_t* name, size_t length)
    {
        if (!StartsWith(name, length, DevicePrefix) || length <= _countof(DevicePrefix) - 1 || length >= MaxDeviceName) return false;
        for (size_t i = 0; i < scanner.DeviceCount; ++i)
        {
            const Device& entry = scanner.Devices[i];
            if (CompareStringOrdinal(entry.Name, entry.Length, name, static_cast<int>(length), TRUE) == CSTR_EQUAL)
                return true;
        }
        if (scanner.DeviceCount == scanner.DeviceCapacity)
        {
            if (scanner.DeviceCapacity >= MaxDevices) return false;
            size_t capacity = scanner.DeviceCapacity == 0 ? 16 : scanner.DeviceCapacity * 2;
            size_t bytes = capacity * sizeof(Device);
            void* buffer = scanner.Devices == nullptr
                ? HeapAlloc(scanner.Heap, 0, bytes)
                : HeapReAlloc(scanner.Heap, 0, scanner.Devices, bytes);
            if (buffer == nullptr) return false;
            scanner.Devices = static_cast<Device*>(buffer);
            scanner.DeviceCapacity = capacity;
        }
        Device& entry = scanner.Devices[scanner.DeviceCount++];
        for (size_t i = 0; i < length; ++i) entry.Name[i] = name[i];
        entry.Name[length] = L'\0';
        entry.Length = static_cast<int>(length);
        entry.Type = 0;
        entry.Traits = 0;
        return true;
    }

    void DiscoverInterfaces(Scanner& scanner, HMODULE setup)
    {
        const auto getSet = Find<decltype(&SetupDiGetClassDevsW)>(setup, "SetupDiGetClassDevsW");
        const auto enumerate = Find<decltype(&SetupDiEnumDeviceInfo)>(setup, "SetupDiEnumDeviceInfo");
        const auto property = Find<decltype(&SetupDiGetDeviceRegistryPropertyW)>(setup, "SetupDiGetDeviceRegistryPropertyW");
        const auto closeSet = Find<decltype(&SetupDiDestroyDeviceInfoList)>(setup, "SetupDiDestroyDeviceInfoList");
        if (!getSet || !enumerate || !property || !closeSet)
        {
            scanner.Incomplete = true;
            return;
        }
        HDEVINFO set = getSet(&HidInterface, nullptr, nullptr, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (!ValidHandle(set))
        {
            scanner.Incomplete = true;
            return;
        }
        auto* bytes = static_cast<BYTE*>(scanner.Scratch);
        auto* text = static_cast<wchar_t*>(scanner.Scratch);
        SP_DEVINFO_DATA data;
        for (DWORD index = 0; ; ++index)
        {
            data.cbSize = sizeof(data);
            if (!enumerate(set, index, &data))
            {
                if (GetLastError() != ERROR_NO_MORE_ITEMS) scanner.Incomplete = true;
                break;
            }
            DWORD type, used;
            if (!property(set, &data, SPDRP_HARDWAREID, &type, bytes, ScratchBytes, &used))
            {
                if (GetLastError() != ERROR_INVALID_DATA) scanner.Incomplete = true;
                continue;
            }
            if (type != REG_MULTI_SZ || used > ScratchBytes || used % sizeof(wchar_t) != 0)
            {
                scanner.Incomplete = true;
                continue;
            }
            if (!HasDedicatedCollection(text, used / sizeof(wchar_t))) continue;
            if (!property(set, &data, SPDRP_PHYSICAL_DEVICE_OBJECT_NAME, &type, bytes, ScratchBytes, &used)
                || type != REG_SZ || used > ScratchBytes || used % sizeof(wchar_t) != 0)
            {
                scanner.Incomplete = true;
                continue;
            }
            size_t length = 0;
            size_t capacity = used / sizeof(wchar_t);
            while (length < capacity && text[length] != L'\0') ++length;
            if (length == capacity || !AddDevice(scanner, text, length)) scanner.Incomplete = true;
        }
        closeSet(set);
    }

    bool ReadHandleTable(Scanner& scanner, HANDLE process)
    {
        while (scanner.Table != nullptr)
        {
            ULONG used = 0;
            LONG status = scanner.ReadProcess(process, ProcessHandleInformation, scanner.Table, scanner.TableSize, &used);
            if (status == 0)
            {
                const ULONG header = offsetof(HandleTable, Entries);
                return used <= scanner.TableSize && used >= header
                    && scanner.Table->Count <= (used - header) / sizeof(HandleEntry);
            }
            if (status != StatusInfoLengthMismatch && status != StatusBufferTooSmall) return false;
            uint64_t next = static_cast<uint64_t>(scanner.TableSize) * 2;
            uint64_t required = static_cast<uint64_t>(used) + (64u << 10);
            if (next < required) next = required;
            if (next > MaxSnapshotBytes) return false;
            void* table = HeapReAlloc(scanner.Heap, 0, scanner.Table, static_cast<SIZE_T>(next));
            if (!table) return false;
            scanner.Table = static_cast<HandleTable*>(table);
            scanner.TableSize = static_cast<ULONG>(next);
        }
        return false;
    }

    ULONG ReadFileTypeIndex(Scanner& scanner, HANDLE sample)
    {
        if (!ReadHandleTable(scanner, GetCurrentProcess())) return 0;
        for (ULONG_PTR i = 0; i < scanner.Table->Count; ++i)
        {
            const HandleEntry& entry = scanner.Table->Entries[i];
            if (entry.Value == sample) return entry.ObjectTypeIndex;
        }
        return 0;
    }

    ULONG ReadDeviceType(Scanner& scanner, HANDLE file, ULONG* traits = nullptr)
    {
        IoStatus ioStatus = {StatusPending, 0, 0};
        VolumeDevice device = {};
        LONG status = scanner.ReadVolume(file, &ioStatus, &device, sizeof(device), FileFsDeviceInformation);
        // 异步设备完成查询前必须保留输出缓冲 等待上限由探测进程的调用方管控
        if (status == StatusPending)
        {
            while ((status = ioStatus.Status) == StatusPending) Sleep(1);
        }
        if (status != 0) return 0;
        if (traits) *traits = device.Characteristics;
        return device.Type;
    }

    bool ReadDeviceTypes(Scanner& scanner)
    {
        wchar_t path[_countof(GlobalRoot) + MaxDeviceName];
        for (size_t i = 0; i < _countof(GlobalRoot) - 1; ++i) path[i] = GlobalRoot[i];
        bool any = false;
        for (size_t i = 0; i < scanner.DeviceCount; ++i)
        {
            Device& entry = scanner.Devices[i];
            for (int j = 0; j <= entry.Length; ++j) path[_countof(GlobalRoot) - 1 + j] = entry.Name[j];
            HANDLE file = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr);
            if (ValidHandle(file))
            {
                entry.Type = ReadDeviceType(scanner, file, &entry.Traits);
                if (scanner.FileTypeIndex == 0) scanner.FileTypeIndex = ReadFileTypeIndex(scanner, file);
                CloseHandle(file);
            }
            if (entry.Type == 0) scanner.Incomplete = true;
            else any = true;
        }
        return any && scanner.FileTypeIndex != 0;
    }

    bool SameLiveProcess(HANDLE process, int64_t creation)
    {
        FILETIME born, exited, kernel, user;
        if (!GetProcessTimes(process, &born, &exited, &kernel, &user) || WaitForSingleObject(process, 0) != WAIT_TIMEOUT) return false;
        uint64_t stamp = (static_cast<uint64_t>(born.dwHighDateTime) << 32) | born.dwLowDateTime;
        return stamp == static_cast<uint64_t>(creation);
    }

    Result MatchDeviceName(Scanner& scanner, HANDLE file)
    {
        ULONG used = 0;
        if (scanner.ReadObject(file, ObjectNameInformation, scanner.Scratch, ScratchBytes, &used) != 0
            || used < sizeof(ObjectName) || used > ScratchBytes) return ProcessUnreadable;
        const auto& name = *static_cast<const ObjectName*>(scanner.Scratch);
        if (name.Length == 0) return Absent;
        uintptr_t start = reinterpret_cast<uintptr_t>(scanner.Scratch);
        uintptr_t text = reinterpret_cast<uintptr_t>(name.Buffer);
        if (name.Length % sizeof(wchar_t) != 0 || text % alignof(wchar_t) != 0 || text < start
            || text - start > used || name.Length > used - (text - start)) return ProcessUnreadable;
        size_t size = name.Length / sizeof(wchar_t);
        if (!StartsWith(name.Buffer, size, DevicePrefix)) return Absent;
        size_t end = _countof(DevicePrefix) - 1;
        while (end < size && name.Buffer[end] != L'\\') ++end;
        for (size_t i = 0; i < scanner.DeviceCount; ++i)
        {
            const Device& entry = scanner.Devices[i];
            if (CompareStringOrdinal(entry.Name, entry.Length, name.Buffer, static_cast<int>(end), TRUE) == CSTR_EQUAL)
                return Holding;
        }
        return Absent;
    }

    // 不少驱动软件用同步句柄一直阻塞着读设备 这种句柄在读完之前查不出名字 直接查会一直等下去
    // 所以从同一个设备另开一个不带读写权限的句柄 用新句柄查名字 原句柄和它正在进行的读写都不受影响
    // 只对和私有接口同一类设备节点的句柄这样做 别的设备不去新开
    Result MatchDevice(Scanner& scanner, HANDLE file, bool sameNodeKind)
    {
        if (sameNodeKind)
        {
            HANDLE fresh = ReOpenFile(file, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, FILE_FLAG_OVERLAPPED);
            if (ValidHandle(fresh))
            {
                Result match = MatchDeviceName(scanner, fresh);
                CloseHandle(fresh);
                return match;
            }
        }
        return MatchDeviceName(scanner, file);
    }

    Result ProbeHandles(Scanner& scanner, HANDLE process)
    {
        if (!ReadHandleTable(scanner, process)) return ProcessUnreadable;
        bool unreadable = false;
        HANDLE self = GetCurrentProcess();
        for (ULONG_PTR i = 0; i < scanner.Table->Count; ++i)
        {
            const HandleEntry& entry = scanner.Table->Entries[i];
            if (entry.ObjectTypeIndex != scanner.FileTypeIndex) continue;
            HANDLE file;
            if (!DuplicateHandle(process, entry.Value, self, &file, 0, FALSE, DUPLICATE_SAME_ACCESS))
            {
                // 枚举后关闭的句柄会从下一次快照消失
                if (GetLastError() != ERROR_INVALID_HANDLE) unreadable = true;
                continue;
            }
            ULONG traits = 0;
            ULONG type = ReadDeviceType(scanner, file, &traits);
            bool sameType = false;
            bool sameNodeKind = false;
            for (size_t j = 0; type != 0 && j < scanner.DeviceCount && !sameNodeKind; ++j)
            {
                if (scanner.Devices[j].Type != type) continue;
                sameType = true;
                sameNodeKind = scanner.Devices[j].Traits == traits;
            }
            Result match = sameType ? MatchDevice(scanner, file, sameNodeKind) : Absent;
            CloseHandle(file);
            if (match == Holding) return Holding;
            if (match == ProcessUnreadable) unreadable = true;
        }
        if (unreadable) return ProcessUnreadable;
        return scanner.Incomplete ? InterfaceUnreadable : Absent;
    }

    void FreeScanner(Scanner* scanner)
    {
        if (!scanner) return;
        HANDLE heap = scanner->Heap;
        if (scanner->Devices) HeapFree(heap, 0, scanner->Devices);
        if (scanner->Table) HeapFree(heap, 0, scanner->Table);
        if (scanner->Scratch) HeapFree(heap, 0, scanner->Scratch);
        HeapFree(heap, 0, scanner);
    }
}

extern "C"
{
    BOOL WINAPI PaviseHidDllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }

    __declspec(dllexport) int32_t PaviseHidAbi() { return 1; }

    __declspec(dllexport) void* PaviseHidOpen(int32_t* result)
    {
        if (!result) return nullptr;
        *result = InterfaceUnreadable;
        HANDLE heap = GetProcessHeap();
        if (!heap) return nullptr;
        auto* scanner = static_cast<Scanner*>(HeapAlloc(heap, HEAP_ZERO_MEMORY, sizeof(Scanner)));
        if (!scanner) return nullptr;
        scanner->Heap = heap;
        scanner->TableSize = 256u << 10;
        scanner->Table = static_cast<HandleTable*>(HeapAlloc(heap, 0, scanner->TableSize));
        scanner->Scratch = HeapAlloc(heap, 0, ScratchBytes);
        HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
        HMODULE setup = LoadLibraryExW(L"setupapi.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (setup && ntdll && scanner->Table && scanner->Scratch)
        {
            scanner->ReadProcess = Find<QueryProcess>(ntdll, "NtQueryInformationProcess");
            scanner->ReadObject = Find<QueryObject>(ntdll, "NtQueryObject");
            scanner->ReadVolume = Find<QueryVolume>(ntdll, "NtQueryVolumeInformationFile");
            if (scanner->ReadProcess && scanner->ReadObject && scanner->ReadVolume)
            {
                DiscoverInterfaces(*scanner, setup);
                if (scanner->DeviceCount == 0 && !scanner->Incomplete) *result = NoInterface;
                else if (ReadDeviceTypes(*scanner)) *result = Absent;
            }
        }
        if (setup) FreeLibrary(setup);
        if (*result == Absent) return scanner;
        FreeScanner(scanner);
        return nullptr;
    }

    __declspec(dllexport) int32_t PaviseHidProbe(void* context, uint32_t pid, int64_t creation)
    {
        if (!context) return InterfaceUnreadable;
        if (pid <= 4 || pid == GetCurrentProcessId() || creation <= 0) return ProcessUnreadable;
        HANDLE process = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_DUP_HANDLE | SYNCHRONIZE, FALSE, pid);
        if (!process) return ProcessUnreadable;
        Result result = ProcessUnreadable;
        if (SameLiveProcess(process, creation))
        {
            result = ProbeHandles(*static_cast<Scanner*>(context), process);
            if (!SameLiveProcess(process, creation)) result = ProcessUnreadable;
        }
        CloseHandle(process);
        return result;
    }

    __declspec(dllexport) void PaviseHidClose(void* context) { FreeScanner(static_cast<Scanner*>(context)); }
}
