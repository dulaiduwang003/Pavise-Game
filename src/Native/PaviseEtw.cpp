// Pavise 的 ETW 前置处理
// 完整采集（线程切换 线程就绪 画面提交）时 系统每秒会送来几万个事件 逐个回调进 C# 的开销很大
// 这里在原生回调里完成计数 两种应急上限 时间窗口 每 CPU 事件顺序和切换链连续性检查
// 只把涉及游戏线程的切换与就绪事件 本进程的画面提交事件按到达顺序留下来
// 采集结束后由 C# 用同一套事件处理代码逐条回放 结论仍由 C# 那一份代码得出
// 规则必须和 FrameCoordinationSampler.Evidence 的事件处理逐条一致 改动任何一边都要同时改另一边
// 并跑 tools/Test-NativeEtw.ps1 的逐项比对
// 不链接 C 运行库 内存直接用进程堆 只依赖 kernel32
// 消费线程 CPU 与事件属性由 C# 以函数指针提供 原生与托管处理读的是同一个来源
// 构建与生成内嵌镜像 tools/Build-Native.ps1

#include <windows.h>
#include <stddef.h>
#include <stdint.h>

extern "C"
{
    // 编译器可能为结构清零和复制生成这两个调用 没有运行库时由这里提供
    void* memset(void* target, int value, size_t count)
    {
        unsigned char* bytes = static_cast<unsigned char*>(target);
        while (count-- != 0) *bytes++ = static_cast<unsigned char>(value);
        return target;
    }

    void* memcpy(void* target, const void* source, size_t count)
    {
        unsigned char* to = static_cast<unsigned char*>(target);
        const unsigned char* from = static_cast<const unsigned char*>(source);
        while (count-- != 0) *to++ = *from++;
        return target;
    }

    BOOL WINAPI DllMainCRTStartup(HINSTANCE, DWORD, LPVOID) { return TRUE; }
}

namespace
{
    // 以下是 evntcons.h 与 tdh.h 中的系统结构 布局由 static_assert 锁定
    struct EventDescriptor
    {
        uint16_t Id;
        uint8_t Version;
        uint8_t Channel;
        uint8_t Level;
        uint8_t Opcode;
        uint16_t Task;
        uint64_t Keyword;
    };

    struct Guid
    {
        uint64_t Low;
        uint64_t High;
    };

    struct EventHeader
    {
        uint16_t Size;
        uint16_t HeaderType;
        uint16_t Flags;
        uint16_t EventProperty;
        uint32_t ThreadId;
        uint32_t ProcessId;
        int64_t TimeStamp;
        Guid ProviderId;
        EventDescriptor Descriptor;
        uint64_t ProcessorTime;
        Guid ActivityId;
    };

    struct EventRecord
    {
        EventHeader Header;
        uint32_t BufferContext;
        uint16_t ExtendedDataCount;
        uint16_t UserDataLength;
        void* ExtendedData;
        void* UserData;
        void* UserContext;
    };

    struct PropertyDescriptor
    {
        uint64_t PropertyName;
        uint32_t ArrayIndex;
        uint32_t Reserved;
    };

    static_assert(sizeof(EventDescriptor) == 16, "EVENT_DESCRIPTOR");
    static_assert(sizeof(EventHeader) == 80, "EVENT_HEADER");
    static_assert(offsetof(EventHeader, ProviderId) == 24, "EVENT_HEADER.ProviderId");
    static_assert(offsetof(EventRecord, BufferContext) == 80, "EVENT_RECORD.BufferContext");
    static_assert(offsetof(EventRecord, UserDataLength) == 86, "EVENT_RECORD.UserDataLength");
    static_assert(offsetof(EventRecord, UserData) == 96, "EVENT_RECORD.UserData");
    static_assert(offsetof(EventRecord, UserContext) == 104, "EVENT_RECORD.UserContext");
    static_assert(sizeof(EventRecord) == 112, "EVENT_RECORD");
    static_assert(sizeof(PropertyDescriptor) == 16, "PROPERTY_DATA_DESCRIPTOR");

    // GUID 按内存布局读成两个 64 位数比较
    constexpr Guid MakeGuid(uint32_t a, uint16_t b, uint16_t c, const uint8_t (&d)[8])
    {
        return Guid {
            static_cast<uint64_t>(a) | (static_cast<uint64_t>(b) << 32) | (static_cast<uint64_t>(c) << 48),
            static_cast<uint64_t>(d[0]) | (static_cast<uint64_t>(d[1]) << 8) | (static_cast<uint64_t>(d[2]) << 16) | (static_cast<uint64_t>(d[3]) << 24)
                | (static_cast<uint64_t>(d[4]) << 32) | (static_cast<uint64_t>(d[5]) << 40) | (static_cast<uint64_t>(d[6]) << 48) | (static_cast<uint64_t>(d[7]) << 56) };
    }

    constexpr uint8_t KernelThreadTail[8] = { 0x9d, 0xda, 0x00, 0xc0, 0x4f, 0xd7, 0xba, 0x7c };
    constexpr uint8_t DxgiTail[8] = { 0xa6, 0xad, 0xf0, 0x3c, 0xfe, 0xd5, 0xd3, 0xc9 };
    constexpr uint8_t D3d9Tail[8] = { 0x84, 0x51, 0xaa, 0x85, 0x05, 0x11, 0xc6, 0xb9 };
    constexpr Guid KernelThread = MakeGuid(0x3d6fa8d1, 0xfe05, 0x11d0, KernelThreadTail);
    constexpr Guid Dxgi = MakeGuid(0xca11c036, 0x0102, 0x4a2d, DxgiTail);
    constexpr Guid D3d9 = MakeGuid(0x783aca0a, 0x790e, 0x4d7f, D3d9Tail);

    bool Same(const Guid& left, const Guid& right) { return left.Low == right.Low && left.High == right.High; }

    // 接口版本 结构布局或回放规则变了就加一 C# 端版本不符时不加载
    const int32_t AbiVersion = 1;

    enum Kind : uint8_t
    {
        KindSwitch = 1,         // 线程切换 至少一端是游戏线程 或者这颗 CPU 上原本在跑游戏线程
        KindReady = 2,          // 线程就绪 目标是游戏线程
        KindPresent = 3,        // 本进程的画面提交开始或结束 A 是画面提交表里的下标
        KindOrderReversed = 4,  // 同一 CPU 的事件时间倒序
        KindSwitchShort = 5,    // 线程切换数据不足
        KindReadyShort = 6,     // 线程就绪数据不足
        KindDiscontinuity = 7   // 第一次发现同一 CPU 前后两次切换衔接不上
    };

    enum Fault : int32_t
    {
        FaultNone = 0,
        FaultSystemEvents = 1,  // 事件送达超过应急上限
        FaultCpu = 2,           // 消费者与窗前准备的累计 CPU 超过应急上限
        FaultMemory = 3,        // 原生存储分配失败
        FaultReader = 4,        // C# 的 CPU 读取抛出异常 托管处理在同一个检查点按解码失败弃样
        FaultRecords = 5        // 留下的游戏相关记录到了应急上限 回放按状态块上限同一个原因弃样
    };

    typedef double (*CpuReading)();
    // 和 TdhGetProperty 相同的返回码 由 C# 转给 TDH
    typedef uint32_t (WINAPI* PropertyReading)(EventRecord* record, PropertyDescriptor* property, uint32_t bytes, void* data);

    // 以下四个结构与 C# 的 NativeEtw 一一对应 大小由 PvEtwLayout 报给 C# 核对
    struct Config
    {
        int32_t Size;
        int32_t Pid;
        int64_t Begin;
        int64_t End;
        const int32_t* Threads;     // 升序且不重复
        CpuReading ConsumerCpuMs;   // 消费线程自身 CPU
        PropertyReading ReadProperty;
        int32_t ThreadCount;
        int32_t EmergencySystemEvents;
        int32_t CpuCheckInterval;
        int32_t LayoutObservation;  // 非零时读取画面提交来源 和 C# 的 Sample.LayoutObservation 一致
        double EmergencyCaptureCpuMs;
        double CallerCpuBeforeEvents;
        // 留下的记录最多这么多条 和回放的关系状态块容量相同 事件上限放大后原生存储不能先涨到几百 MB 才在回放时作废
        int32_t EmergencyRecords;
        int32_t Reserved;
    };

    struct Record
    {
        int64_t Time;
        int32_t Count;      // 处理到这个事件时的已送达事件数
        int32_t Switches;   // 处理到这个事件时的线程切换数
        int32_t A;          // 切换时是切入线程 就绪时是目标线程 画面提交时是画面提交表下标
        int32_t B;          // 切换时是切出线程 就绪时是此刻这颗 CPU 上正在跑的游戏线程 没有就是 0
        uint16_t Cpu;
        uint8_t Kind;
        uint8_t State;      // 切换时是切出线程状态 就绪时是就绪标志
        int32_t Reserved;
    };

    struct PresentRecord
    {
        int32_t ThreadId;
        uint16_t Id;
        uint8_t Version;
        uint8_t IsDxgi;
        uint32_t FlagsStatus;
        uint32_t FlagsValue;
        uint32_t StreamStatus;
        uint32_t UserDataLength;
        uint32_t Properties;  // 已读取属性的位标志 1 是提交标志 2 是来源 只有游戏线程的版本 0 开始事件才会读
        uint64_t Stream;
        int32_t Result;
        int32_t Reserved;
    };

    struct Stats
    {
        int32_t Size;
        int32_t Count;
        int32_t Switches;
        int32_t Fault;
        int32_t Records;
        int32_t Presents;
        int32_t Blocks;
        int32_t BlockRecords;
    };

    static_assert(sizeof(Config) == 88, "Config");
    static_assert(sizeof(Record) == 32, "Record");
    static_assert(sizeof(PresentRecord) == 48, "PresentRecord");
    static_assert(sizeof(Stats) == 32, "Stats");

    struct CpuState
    {
        int64_t Last;
        int32_t Running;       // 这颗 CPU 上正在跑的游戏线程 不是游戏线程时为 0
        int32_t LastIncoming;  // 最近一次切入的线程 用于连续性检查
        int32_t IncomingKnown;
        int32_t Padding;
    };

    // 记录按固定大小分块存放 回调里不做大块重新分配和整体复制 免得消费变慢导致系统丢弃缓冲
    const int32_t BlockRecords = 65536;

    struct Context
    {
        Config Settings;
        int32_t* Threads;
        Record** Blocks;
        int32_t BlockCount, BlockCapacity;
        int32_t Records;
        PresentRecord* Presents;
        int32_t PresentCount, PresentCapacity;
        CpuState* Cpus;
        int32_t CpuCapacity;
        int32_t Count;
        int32_t Switches;
        bool Invalid;
        bool Continuous;
        volatile LONG FaultCode;
        volatile LONG Stopped;
    };

    void* Allocate(size_t bytes) { return HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, bytes); }

    void* Grow(void* block, size_t bytes)
    {
        return block == nullptr ? Allocate(bytes) : HeapReAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, block, bytes);
    }

    void Free(void* block) { if (block != nullptr) HeapFree(GetProcessHeap(), 0, block); }

    void Destroy(Context* context)
    {
        if (context == nullptr) return;
        for (int32_t i = 0; i < context->BlockCount; i++) Free(context->Blocks[i]);
        Free(context->Blocks);
        Free(context->Presents);
        Free(context->Cpus);
        Free(context->Threads);
        Free(context);
    }

    wchar_t FlagsName[] = L"Flags";
    wchar_t DxgiStreamName[] = L"pIDXGISwapChain";
    wchar_t D3d9StreamName[] = L"pSwapchain";

    // NaN 与正负无穷都不算已知读数
    bool KnownCpu(double value) { return value == value && value >= 0 && value <= 1.7976931348623157e308; }

    // 与 C# 的 CpuEmergencyExceeded 相同 任一已知分量超界即停 两者都已知时看总和
    bool CpuEmergencyExceeded(double consumer, double caller, double limit)
    {
        bool consumerKnown = KnownCpu(consumer), callerKnown = KnownCpu(caller);
        return (consumerKnown && consumer > limit) || (callerKnown && caller > limit)
            || (consumerKnown && callerKnown && consumer + caller > limit);
    }

    bool Tracked(const Context* context, int32_t tid)
    {
        int32_t low = 0, high = context->Settings.ThreadCount - 1;
        while (low <= high)
        {
            int32_t middle = low + (high - low) / 2;
            int32_t value = context->Threads[middle];
            if (value == tid) return true;
            if (value < tid) low = middle + 1;
            else high = middle - 1;
        }
        return false;
    }

    void Fail(Context* context, int32_t fault)
    {
        context->Invalid = true;
        InterlockedCompareExchange(&context->FaultCode, fault, FaultNone);
    }

    CpuState* State(Context* context, int32_t cpu)
    {
        if (cpu >= context->CpuCapacity)
        {
            int32_t capacity = context->CpuCapacity < 64 ? 64 : context->CpuCapacity;
            while (capacity <= cpu) capacity *= 2;
            void* grown = Grow(context->Cpus, static_cast<size_t>(capacity) * sizeof(CpuState));
            if (grown == nullptr) { Fail(context, FaultMemory); return nullptr; }
            context->Cpus = static_cast<CpuState*>(grown);
            context->CpuCapacity = capacity;
        }
        return &context->Cpus[cpu];
    }

    void Emit(Context* context, int64_t time, uint8_t kind, int32_t cpu, int32_t a, int32_t b, uint8_t state)
    {
        if (context->Records == 0x7fffffff) { Fail(context, FaultMemory); return; }
        if (context->Records >= context->Settings.EmergencyRecords) { Fail(context, FaultRecords); return; }
        int32_t offset = context->Records % BlockRecords;
        if (offset == 0)
        {
            if (context->BlockCount == context->BlockCapacity)
            {
                int32_t capacity = context->BlockCapacity == 0 ? 16 : context->BlockCapacity * 2;
                void* grown = Grow(context->Blocks, static_cast<size_t>(capacity) * sizeof(Record*));
                if (grown == nullptr) { Fail(context, FaultMemory); return; }
                context->Blocks = static_cast<Record**>(grown);
                context->BlockCapacity = capacity;
            }
            void* block = HeapAlloc(GetProcessHeap(), 0, static_cast<size_t>(BlockRecords) * sizeof(Record));
            if (block == nullptr) { Fail(context, FaultMemory); return; }
            context->Blocks[context->BlockCount++] = static_cast<Record*>(block);
        }
        Record& record = context->Blocks[context->BlockCount - 1][offset];
        record.Time = time;
        record.Count = context->Count;
        record.Switches = context->Switches;
        record.A = a;
        record.B = b;
        record.Cpu = static_cast<uint16_t>(cpu);
        record.Kind = kind;
        record.State = state;
        record.Reserved = 0;
        context->Records++;
    }

    int32_t ReadInt32(const EventRecord* record, size_t offset)
    {
        const uint8_t* bytes = static_cast<const uint8_t*>(record->UserData) + offset;
        return static_cast<int32_t>(static_cast<uint32_t>(bytes[0]) | (static_cast<uint32_t>(bytes[1]) << 8)
            | (static_cast<uint32_t>(bytes[2]) << 16) | (static_cast<uint32_t>(bytes[3]) << 24));
    }

    uint8_t ReadByte(const EventRecord* record, size_t offset)
    {
        return static_cast<const uint8_t*>(record->UserData)[offset];
    }

    uint32_t ReadProperty(Context* context, EventRecord* record, wchar_t* name, void* buffer, uint32_t bytes)
    {
        PropertyDescriptor descriptor;
        descriptor.PropertyName = reinterpret_cast<uint64_t>(name);
        descriptor.ArrayIndex = 0xffffffffu;
        descriptor.Reserved = 0;
        return context->Settings.ReadProperty(record, &descriptor, bytes, buffer);
    }

    void OnKernelThread(Context* context, EventRecord* record, int64_t time)
    {
        // 大处理器编号必须用 ProcessorIndex 不能截断后归到另一颗 CPU
        int32_t cpu = static_cast<int32_t>(record->BufferContext & ((record->Header.Flags & 0x0200) != 0 ? 0xFFFFu : 0xFFu));
        CpuState* state = State(context, cpu);
        if (state == nullptr) return;
        if (time < state->Last)
        {
            state->Running = 0;
            Emit(context, time, KindOrderReversed, cpu, 0, 0, 0);
            return;
        }
        state->Last = time;
        uint8_t opcode = record->Header.Descriptor.Opcode;
        if (opcode == 36)
        {
            if (context->Switches < 0x7fffffff) context->Switches++;
            if (record->UserDataLength < 24)
            {
                state->Running = 0;
                Emit(context, time, KindSwitchShort, cpu, 0, 0, 0);
                return;
            }
            int32_t incoming = ReadInt32(record, 0), outgoing = ReadInt32(record, 4);
            uint8_t oldState = ReadByte(record, 14);
            // 连续性要看全部切换 同一 CPU 上一次切入的线程应当就是这一次切出的线程
            if (state->IncomingKnown != 0 && state->LastIncoming != outgoing && context->Continuous)
            {
                context->Continuous = false;
                Emit(context, time, KindDiscontinuity, cpu, incoming, outgoing, 0);
            }
            state->LastIncoming = incoming;
            state->IncomingKnown = 1;
            bool trackedIncoming = Tracked(context, incoming);
            // 切换链断开时 这颗 CPU 上的游戏线程也要随这次切换清掉 回放才能得到同样的运行状态
            if (trackedIncoming || Tracked(context, outgoing) || state->Running != 0)
                Emit(context, time, KindSwitch, cpu, incoming, outgoing, oldState);
            state->Running = trackedIncoming ? incoming : 0;
        }
        else if (opcode == 50)
        {
            if (record->UserDataLength < 8)
            {
                Emit(context, time, KindReadyShort, cpu, 0, 0, 0);
                return;
            }
            int32_t target = ReadInt32(record, 0);
            if (Tracked(context, target)) Emit(context, time, KindReady, cpu, target, state->Running, ReadByte(record, 6));
        }
    }

    void OnPresent(Context* context, EventRecord* record, int64_t time, bool dxgi)
    {
        uint16_t id = record->Header.Descriptor.Id;
        uint16_t start = dxgi ? 42 : 1, stop = dxgi ? 43 : 2;
        if (id != start && id != stop) return;
        if (context->PresentCount == context->PresentCapacity)
        {
            if (context->PresentCapacity >= 0x40000000) { Fail(context, FaultMemory); return; }
            int32_t capacity = context->PresentCapacity == 0 ? 1024 : context->PresentCapacity * 2;
            void* grown = Grow(context->Presents, static_cast<size_t>(capacity) * sizeof(PresentRecord));
            if (grown == nullptr) { Fail(context, FaultMemory); return; }
            context->Presents = static_cast<PresentRecord*>(grown);
            context->PresentCapacity = capacity;
        }
        int32_t index = context->PresentCount;
        PresentRecord& present = context->Presents[index];
        present.ThreadId = static_cast<int32_t>(record->Header.ThreadId);
        present.Id = id;
        present.Version = record->Header.Descriptor.Version;
        present.IsDxgi = dxgi ? 1 : 0;
        present.UserDataLength = record->UserDataLength;
        present.FlagsStatus = present.FlagsValue = present.StreamStatus = present.Properties = 0;
        present.Stream = 0;
        present.Result = id == stop && record->UserDataLength >= 4 ? ReadInt32(record, 0) : 0;
        present.Reserved = 0;
        if (id == start && present.Version == 0 && Tracked(context, present.ThreadId))
        {
            // 属性在回调里按 C# 规则读取的条件读好 DXGI 先读提交标志 读失败或是 TEST 提交就不再读来源
            // 来源只在布局观察时读取 回放在同样的位置取回同样的结果
            bool submitted = true;
            if (dxgi)
            {
                present.Properties |= 1;
                present.FlagsStatus = ReadProperty(context, record, FlagsName, &present.FlagsValue, 4);
                submitted = present.FlagsStatus == 0 && (present.FlagsValue & 1u) == 0;
            }
            if (submitted && context->Settings.LayoutObservation != 0)
            {
                present.Properties |= 2;
                present.StreamStatus = ReadProperty(context, record, dxgi ? DxgiStreamName : D3d9StreamName, &present.Stream, 8);
            }
        }
        context->PresentCount++;
        Emit(context, time, KindPresent, 0, index, 0, 0);
    }

    void WINAPI OnEvent(EventRecord* record)
    {
        Context* context = static_cast<Context*>(record->UserContext);
        if (context == nullptr || context->Invalid || context->Stopped != 0) return;
        if (context->Count < 0x7fffffff) context->Count++;
        if (context->Count > context->Settings.EmergencySystemEvents) { Fail(context, FaultSystemEvents); return; }
        if ((context->Count & (context->Settings.CpuCheckInterval - 1)) == 0)
        {
            double consumer = context->Settings.ConsumerCpuMs();
            // C# 包装在读取抛出异常时返回负无穷 正常读数不会是这个值
            if (consumer == -__builtin_inf()) { Fail(context, FaultReader); return; }
            if (CpuEmergencyExceeded(consumer, context->Settings.CallerCpuBeforeEvents, context->Settings.EmergencyCaptureCpuMs))
            { Fail(context, FaultCpu); return; }
        }
        int64_t time = record->Header.TimeStamp;
        if (time < context->Settings.Begin || time > context->Settings.End) return;
        const Guid& provider = record->Header.ProviderId;
        if (Same(provider, KernelThread)) { OnKernelThread(context, record, time); return; }
        bool dxgi = Same(provider, Dxgi);
        if (!dxgi && !Same(provider, D3d9)) return;
        if (record->Header.ProcessId != static_cast<uint32_t>(context->Settings.Pid)) return;
        OnPresent(context, record, time, dxgi);
    }
}

extern "C"
{
    __declspec(dllexport) int32_t PvEtwAbi() { return AbiVersion; }

    // 结构大小按 Config Record PresentRecord Stats 的顺序写入 供 C# 核对布局
    __declspec(dllexport) int32_t PvEtwLayout(int32_t* sizes, int32_t count)
    {
        if (sizes == nullptr || count < 4) return 0;
        sizes[0] = sizeof(Config);
        sizes[1] = sizeof(Record);
        sizes[2] = sizeof(PresentRecord);
        sizes[3] = sizeof(Stats);
        return 4;
    }

    __declspec(dllexport) void* PvEtwCreate(const Config* settings)
    {
        if (settings == nullptr || settings->Size != static_cast<int32_t>(sizeof(Config)) || settings->ThreadCount < 0
            || (settings->ThreadCount > 0 && settings->Threads == nullptr) || settings->CpuCheckInterval <= 0
            || (settings->CpuCheckInterval & (settings->CpuCheckInterval - 1)) != 0 || settings->ConsumerCpuMs == nullptr
            || settings->ReadProperty == nullptr || settings->EmergencyRecords <= 0)
            return nullptr;
        for (int32_t i = 1; i < settings->ThreadCount; i++)
            if (settings->Threads[i - 1] >= settings->Threads[i]) return nullptr;
        Context* context = static_cast<Context*>(Allocate(sizeof(Context)));
        if (context == nullptr) return nullptr;
        context->Settings = *settings;
        context->Settings.Threads = nullptr;
        context->Continuous = true;
        if (settings->ThreadCount > 0)
        {
            context->Threads = static_cast<int32_t*>(Allocate(static_cast<size_t>(settings->ThreadCount) * sizeof(int32_t)));
            if (context->Threads == nullptr) { Destroy(context); return nullptr; }
            for (int32_t i = 0; i < settings->ThreadCount; i++) context->Threads[i] = settings->Threads[i];
        }
        return context;
    }

    __declspec(dllexport) void* PvEtwCallback() { return reinterpret_cast<void*>(&OnEvent); }

    // 采集线程随时可读 返回已发生的应急停止原因 没有为 0
    __declspec(dllexport) int32_t PvEtwFault(void* handle)
    {
        Context* context = static_cast<Context*>(handle);
        return context == nullptr ? FaultNone : InterlockedCompareExchange(&context->FaultCode, 0, 0);
    }

    // 测试台架在首个事件前预置已送达数 用来触发应急上限与 CPU 检查点 产品代码不调用
    __declspec(dllexport) int32_t PvEtwSeedCount(void* handle, int32_t count)
    {
        Context* context = static_cast<Context*>(handle);
        if (context == nullptr || count < 0 || context->Count != 0 || context->Records != 0) return 0;
        context->Count = count;
        return 1;
    }

    // 调用之后送达的事件一律忽略 对应 C# 采集收尾时置 Invalid
    __declspec(dllexport) void PvEtwStop(void* handle)
    {
        Context* context = static_cast<Context*>(handle);
        if (context != nullptr) InterlockedExchange(&context->Stopped, 1);
    }

    // 以下读取只能在消费线程退出之后调用
    __declspec(dllexport) int32_t PvEtwStats(void* handle, Stats* stats)
    {
        Context* context = static_cast<Context*>(handle);
        if (context == nullptr || stats == nullptr || stats->Size != static_cast<int32_t>(sizeof(Stats))) return 0;
        stats->Count = context->Count;
        stats->Switches = context->Switches;
        stats->Fault = InterlockedCompareExchange(&context->FaultCode, 0, 0);
        stats->Records = context->Records;
        stats->Presents = context->PresentCount;
        stats->Blocks = context->BlockCount;
        stats->BlockRecords = BlockRecords;
        return 1;
    }

    __declspec(dllexport) const void* PvEtwBlock(void* handle, int32_t index)
    {
        Context* context = static_cast<Context*>(handle);
        if (context == nullptr || index < 0 || index >= context->BlockCount) return nullptr;
        return context->Blocks[index];
    }

    __declspec(dllexport) const void* PvEtwPresents(void* handle)
    {
        Context* context = static_cast<Context*>(handle);
        return context == nullptr || context->PresentCount == 0 ? nullptr : context->Presents;
    }

    __declspec(dllexport) void PvEtwDestroy(void* handle)
    {
        Destroy(static_cast<Context*>(handle));
    }
}
