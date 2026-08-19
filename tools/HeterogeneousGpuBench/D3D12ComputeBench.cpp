// Pavise no-window D3D12 heterogeneous GPU compute benchmark.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <d3dcompiler.h>
#include <wrl/client.h>

#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <numeric>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>

#ifdef _MSC_VER
#pragma comment(lib, "d3d12.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "d3dcompiler.lib")
#endif

using Microsoft::WRL::ComPtr;

namespace {

constexpr UINT kSlots = 3;
constexpr DWORD kTimeoutMs = 30000;

struct Failure : std::runtime_error {
    Failure(HRESULT hr, const char* op) : std::runtime_error([&] {
        std::ostringstream text;
        text << op << " failed, HRESULT=0x" << std::hex << std::uppercase
             << static_cast<unsigned long>(hr);
        return text.str();
    }()) {}
};

void Check(HRESULT hr, const char* op) { if (FAILED(hr)) throw Failure(hr, op); }

std::string Narrow(const std::wstring& value) {
    if (value.empty()) return {};
    int size = WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()),
                                   nullptr, 0, nullptr, nullptr);
    std::string out(static_cast<size_t>(size), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()),
                        out.data(), size, nullptr, nullptr);
    return out;
}

struct Options {
    bool run = false;
    UINT width = 1920;
    UINT height = 1080;
    UINT warmup = 120;
    UINT frames = 600;
    UINT repeats = 3;
    UINT sceneLoops = 12;
    UINT postLoops = 8;
    UINT primaryIndex = 0;
    UINT secondaryIndex = 1;
    std::filesystem::path output;
};

UINT Number(const wchar_t* text, UINT minimum, UINT maximum, const char* name) {
    wchar_t* end = nullptr;
    unsigned long value = wcstoul(text, &end, 10);
    if (!text[0] || !end || *end || value < minimum || value > maximum)
        throw std::runtime_error(std::string("invalid ") + name);
    return static_cast<UINT>(value);
}

Options Args(int argc, wchar_t** argv) {
    Options o;
    for (int i = 1; i < argc; ++i) {
        std::wstring arg = argv[i];
        auto next = [&]() -> const wchar_t* {
            if (++i >= argc) throw std::runtime_error("missing argument value");
            return argv[i];
        };
        if (arg == L"--run") o.run = true;
        else if (arg == L"--width") o.width = Number(next(), 64, 7680, "width");
        else if (arg == L"--height") o.height = Number(next(), 64, 4320, "height");
        else if (arg == L"--warmup-frames") o.warmup = Number(next(), 0, 100000, "warmup");
        else if (arg == L"--measure-frames") o.frames = Number(next(), 30, 100000, "frames");
        else if (arg == L"--repeats") o.repeats = Number(next(), 1, 20, "repeats");
        else if (arg == L"--scene-loops") o.sceneLoops = Number(next(), 1, 1000, "scene loops");
        else if (arg == L"--post-loops") o.postLoops = Number(next(), 1, 1000, "post loops");
        else if (arg == L"--primary-index") o.primaryIndex = Number(next(), 0, 64, "primary index");
        else if (arg == L"--secondary-index") o.secondaryIndex = Number(next(), 0, 64, "secondary index");
        else if (arg == L"--output") o.output = next();
        else throw std::runtime_error("unknown argument: " + Narrow(arg));
    }
    return o;
}

struct Adapter {
    UINT index = 0;
    DXGI_ADAPTER_DESC1 desc = {};
    ComPtr<IDXGIAdapter1> value;
};

std::vector<Adapter> Adapters() {
    ComPtr<IDXGIFactory1> factory;
    Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)), "CreateDXGIFactory1");
    std::vector<Adapter> out;
    for (UINT i = 0;; ++i) {
        ComPtr<IDXGIAdapter1> value;
        HRESULT hr = factory->EnumAdapters1(i, &value);
        if (hr == DXGI_ERROR_NOT_FOUND) break;
        Check(hr, "EnumAdapters1");
        Adapter a;
        a.index = i;
        a.value = value;
        Check(value->GetDesc1(&a.desc), "GetDesc1");
        if (!(a.desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE)) out.push_back(std::move(a));
    }
    return out;
}

Adapter Find(const std::vector<Adapter>& all, UINT index) {
    for (const auto& a : all) if (a.index == index) return a;
    throw std::runtime_error("adapter index not found");
}

const char* kShader = R"HLSL(
cbuffer Params : register(b0) { uint width; uint height; uint loopCount; uint seed; };
StructuredBuffer<uint> inputPixels : register(t0);
RWStructuredBuffer<uint> outputPixels : register(u0);

uint Pack(float3 c) {
    c = saturate(c);
    uint3 p = (uint3)round(c * 255.0);
    return p.x | (p.y << 8) | (p.z << 16) | 0xff000000;
}
float3 Unpack(uint p) {
    return float3(p & 255, (p >> 8) & 255, (p >> 16) & 255) / 255.0;
}

[numthreads(256, 1, 1)]
void Scene(uint3 tid : SV_DispatchThreadID) {
    uint count = width * height;
    uint id = tid.x;
    if (id >= count) return;
    uint x = id % width;
    uint y = id / width;
    float2 uv = (float2(x, y) + 0.5) / float2(width, height);
    float3 c = float3(uv, 0.25 + (seed & 255) / 510.0);
    [loop] for (uint i = 0; i < loopCount; ++i) {
        float f = i + 1.0;
        float wave = sin((uv.x * 17.0 + f) * (1.0 + f * 0.013)) *
                     cos((uv.y * 23.0 - f) * (1.0 + f * 0.017));
        c = frac(c * 1.019 + float3(0.013, 0.021, 0.034) * wave + f * 0.00071);
    }
    outputPixels[id] = Pack(c);
}

[numthreads(256, 1, 1)]
void Post(uint3 tid : SV_DispatchThreadID) {
    uint count = width * height;
    uint id = tid.x;
    if (id >= count) return;
    uint x = id % width;
    uint y = id / width;
    float3 sum = Unpack(inputPixels[id]);
    float weight = 1.0;
    [loop] for (uint i = 0; i < loopCount; ++i) {
        uint r = i + 1;
        uint xl = x > r ? x - r : 0;
        uint xr = min(width - 1, x + r);
        uint yu = y > r ? y - r : 0;
        uint yd = min(height - 1, y + r);
        sum += Unpack(inputPixels[y * width + xl]);
        sum += Unpack(inputPixels[y * width + xr]);
        sum += Unpack(inputPixels[yu * width + x]);
        sum += Unpack(inputPixels[yd * width + x]);
        weight += 4.0;
    }
    outputPixels[id] = Pack(sum / weight);
}
)HLSL";

ComPtr<ID3DBlob> Compile(const char* entry) {
    ComPtr<ID3DBlob> code, errors;
    HRESULT hr = D3DCompile(kShader, strlen(kShader), "PaviseD3D12Compute.hlsl", nullptr, nullptr,
                            entry, "cs_5_1", D3DCOMPILE_ENABLE_STRICTNESS |
                            D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &errors);
    if (FAILED(hr)) {
        std::string detail = errors ? std::string(static_cast<char*>(errors->GetBufferPointer()),
                                                  errors->GetBufferSize()) : "";
        throw std::runtime_error(std::string("shader compile failed: ") + detail);
    }
    return code;
}

struct Device {
    ComPtr<ID3D12Device> value;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12RootSignature> root;
    ComPtr<ID3D12PipelineState> scene;
    ComPtr<ID3D12PipelineState> post;
    UINT64 timestampFrequency = 0;
};

Device MakeDevice(const Adapter& adapter, ID3DBlob* scene, ID3DBlob* post) {
    Device d;
    Check(D3D12CreateDevice(adapter.value.Get(), D3D_FEATURE_LEVEL_11_0,
                            IID_PPV_ARGS(&d.value)), "D3D12CreateDevice");
    D3D12_COMMAND_QUEUE_DESC q = {};
    q.Type = D3D12_COMMAND_LIST_TYPE_COMPUTE;
    Check(d.value->CreateCommandQueue(&q, IID_PPV_ARGS(&d.queue)), "CreateCommandQueue");
    Check(d.queue->GetTimestampFrequency(&d.timestampFrequency), "GetTimestampFrequency");

    D3D12_ROOT_PARAMETER params[3] = {};
    params[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
    params[0].Constants.Num32BitValues = 4;
    params[0].Constants.ShaderRegister = 0;
    params[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
    params[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV;
    params[1].Descriptor.ShaderRegister = 0;
    params[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
    params[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE_UAV;
    params[2].Descriptor.ShaderRegister = 0;
    params[2].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
    D3D12_ROOT_SIGNATURE_DESC rootDesc = {};
    rootDesc.NumParameters = 3;
    rootDesc.pParameters = params;
    ComPtr<ID3DBlob> serialized, errors;
    Check(D3D12SerializeRootSignature(&rootDesc, D3D_ROOT_SIGNATURE_VERSION_1,
                                      &serialized, &errors), "D3D12SerializeRootSignature");
    Check(d.value->CreateRootSignature(0, serialized->GetBufferPointer(), serialized->GetBufferSize(),
                                       IID_PPV_ARGS(&d.root)), "CreateRootSignature");
    D3D12_COMPUTE_PIPELINE_STATE_DESC pso = {};
    pso.pRootSignature = d.root.Get();
    pso.CS = {scene->GetBufferPointer(), scene->GetBufferSize()};
    Check(d.value->CreateComputePipelineState(&pso, IID_PPV_ARGS(&d.scene)), "Create scene PSO");
    pso.CS = {post->GetBufferPointer(), post->GetBufferSize()};
    Check(d.value->CreateComputePipelineState(&pso, IID_PPV_ARGS(&d.post)), "Create post PSO");
    return d;
}

D3D12_RESOURCE_DESC Buffer(UINT64 bytes, D3D12_RESOURCE_FLAGS flags) {
    D3D12_RESOURCE_DESC d = {};
    d.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    d.Width = bytes;
    d.Height = 1;
    d.DepthOrArraySize = 1;
    d.MipLevels = 1;
    d.SampleDesc.Count = 1;
    d.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    d.Flags = flags;
    return d;
}

ComPtr<ID3D12Resource> LocalBuffer(ID3D12Device* device, UINT64 bytes,
                                   D3D12_HEAP_TYPE type, D3D12_RESOURCE_FLAGS flags,
                                   D3D12_RESOURCE_STATES state) {
    D3D12_HEAP_PROPERTIES heap = {};
    heap.Type = type;
    heap.CreationNodeMask = heap.VisibleNodeMask = 1;
    const auto desc = Buffer(bytes, flags);
    ComPtr<ID3D12Resource> out;
    Check(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc, state, nullptr,
                                          IID_PPV_ARGS(&out)), "CreateCommittedResource");
    return out;
}

struct Queries {
    ComPtr<ID3D12QueryHeap> heap;
    ComPtr<ID3D12Resource> readback;
    UINT count = 0;
};

Queries MakeQueries(ID3D12Device* device, UINT count) {
    Queries q;
    q.count = count;
    D3D12_QUERY_HEAP_DESC desc = {};
    desc.Type = D3D12_QUERY_HEAP_TYPE_TIMESTAMP;
    desc.Count = count;
    Check(device->CreateQueryHeap(&desc, IID_PPV_ARGS(&q.heap)), "CreateQueryHeap");
    q.readback = LocalBuffer(device, count * sizeof(UINT64), D3D12_HEAP_TYPE_READBACK,
                             D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_COPY_DEST);
    return q;
}

struct Slot {
    ComPtr<ID3D12Resource> singleInput, singleOutput;
    ComPtr<ID3D12Heap> sharedPrimaryHeap, sharedSecondaryHeap;
    ComPtr<ID3D12Resource> sharedPrimary, sharedSecondary, multiOutput;
    ComPtr<ID3D12CommandAllocator> primaryAllocator, secondaryAllocator;
    ComPtr<ID3D12GraphicsCommandList> primaryList, secondaryList;
    Queries singleQueries, primaryQueries, secondaryQueries;
    int previousFrame = -1;
    UINT64 completionFence = 0;
};

struct Frame {
    UINT index = 0;
    double sceneMs = 0.0;
    double postMs = 0.0;
    UINT64 endTick = 0;
    double intervalMs = 0.0;
};

struct Phase {
    UINT index = 0;
    bool heterogeneous = false;
    std::vector<Frame> frames;
    double wallFps = 0.0;
    double avgFps = 0.0;
    double lowFps = 0.0;
    double p99Ms = 0.0;
    double sceneMs = 0.0;
    double postMs = 0.0;
};

class Runner {
public:
    Runner(const Adapter& primaryAdapter, const Adapter& secondaryAdapter, const Options& o)
        : options_(o) {
        auto sceneCode = Compile("Scene");
        auto postCode = Compile("Post");
        primary_ = MakeDevice(primaryAdapter, sceneCode.Get(), postCode.Get());
        secondary_ = MakeDevice(secondaryAdapter, sceneCode.Get(), postCode.Get());
        bytes_ = static_cast<UINT64>(o.width) * o.height * sizeof(UINT);
        CreateFences();
        for (auto& slot : slots_) CreateSlot(&slot);
    }

    ~Runner() { if (event_) CloseHandle(event_); }

    Phase RunPhase(UINT index, bool heterogeneous, UINT warmup, UINT frames) {
        if (warmup) Run(0, heterogeneous, warmup, false);
        Phase phase = Run(index, heterogeneous, frames, true);
        Summarize(&phase, heterogeneous ? secondary_.timestampFrequency : primary_.timestampFrequency);
        return phase;
    }

private:
    Options options_;
    Device primary_, secondary_;
    std::array<Slot, kSlots> slots_;
    UINT64 bytes_ = 0;
    ComPtr<ID3D12Fence> renderPrimary_, renderSecondary_, completion_, singleFence_;
    UINT64 renderValue_ = 0, completionValue_ = 0, singleValue_ = 0;
    HANDLE event_ = nullptr;

    void CreateFences() {
        event_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (!event_) throw std::runtime_error("CreateEvent failed");
        const auto sharedFlags = static_cast<D3D12_FENCE_FLAGS>(D3D12_FENCE_FLAG_SHARED |
                                                                 D3D12_FENCE_FLAG_SHARED_CROSS_ADAPTER);
        Check(primary_.value->CreateFence(0, sharedFlags, IID_PPV_ARGS(&renderPrimary_)),
              "Create cross-adapter render fence");
        HANDLE handle = nullptr;
        Check(primary_.value->CreateSharedHandle(renderPrimary_.Get(), nullptr, GENERIC_ALL, nullptr,
                                                 &handle), "CreateSharedHandle(render fence)");
        HRESULT hr = secondary_.value->OpenSharedHandle(handle, IID_PPV_ARGS(&renderSecondary_));
        CloseHandle(handle);
        Check(hr, "OpenSharedHandle(render fence)");
        Check(secondary_.value->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&completion_)),
              "Create completion fence");
        Check(primary_.value->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&singleFence_)),
              "Create single fence");
    }

    void CreateSlot(Slot* s) {
        const auto uav = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
        s->singleInput = LocalBuffer(primary_.value.Get(), bytes_, D3D12_HEAP_TYPE_DEFAULT, uav,
                                     D3D12_RESOURCE_STATE_COMMON);
        s->singleOutput = LocalBuffer(primary_.value.Get(), bytes_, D3D12_HEAP_TYPE_DEFAULT, uav,
                                      D3D12_RESOURCE_STATE_COMMON);
        s->multiOutput = LocalBuffer(secondary_.value.Get(), bytes_, D3D12_HEAP_TYPE_DEFAULT, uav,
                                     D3D12_RESOURCE_STATE_COMMON);

        const auto flags = static_cast<D3D12_RESOURCE_FLAGS>(D3D12_RESOURCE_FLAG_ALLOW_CROSS_ADAPTER |
                                                              D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
        const auto desc = Buffer(bytes_, flags);
        const auto a = primary_.value->GetResourceAllocationInfo(0, 1, &desc);
        const auto b = secondary_.value->GetResourceAllocationInfo(0, 1, &desc);
        D3D12_HEAP_DESC heap = {};
        heap.SizeInBytes = std::max(a.SizeInBytes, b.SizeInBytes);
        heap.Alignment = std::max(a.Alignment, b.Alignment);
        heap.Properties.Type = D3D12_HEAP_TYPE_DEFAULT;
        heap.Properties.CreationNodeMask = heap.Properties.VisibleNodeMask = 1;
        heap.Flags = static_cast<D3D12_HEAP_FLAGS>(D3D12_HEAP_FLAG_SHARED |
                                                   D3D12_HEAP_FLAG_SHARED_CROSS_ADAPTER);
        Check(primary_.value->CreateHeap(&heap, IID_PPV_ARGS(&s->sharedPrimaryHeap)),
              "Create shared heap");
        HANDLE handle = nullptr;
        Check(primary_.value->CreateSharedHandle(s->sharedPrimaryHeap.Get(), nullptr, GENERIC_ALL,
                                                 nullptr, &handle), "CreateSharedHandle(heap)");
        HRESULT hr = secondary_.value->OpenSharedHandle(handle, IID_PPV_ARGS(&s->sharedSecondaryHeap));
        CloseHandle(handle);
        Check(hr, "OpenSharedHandle(heap)");
        Check(primary_.value->CreatePlacedResource(s->sharedPrimaryHeap.Get(), 0, &desc,
                                                   D3D12_RESOURCE_STATE_COMMON, nullptr,
                                                   IID_PPV_ARGS(&s->sharedPrimary)),
              "CreatePlacedResource(primary)");
        Check(secondary_.value->CreatePlacedResource(s->sharedSecondaryHeap.Get(), 0, &desc,
                                                     D3D12_RESOURCE_STATE_COMMON, nullptr,
                                                     IID_PPV_ARGS(&s->sharedSecondary)),
              "CreatePlacedResource(secondary)");

        Check(primary_.value->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_COMPUTE,
                                                     IID_PPV_ARGS(&s->primaryAllocator)),
              "Create primary allocator");
        Check(secondary_.value->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_COMPUTE,
                                                       IID_PPV_ARGS(&s->secondaryAllocator)),
              "Create secondary allocator");
        Check(primary_.value->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_COMPUTE,
                                               s->primaryAllocator.Get(), nullptr,
                                               IID_PPV_ARGS(&s->primaryList)),
              "Create primary list");
        Check(secondary_.value->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_COMPUTE,
                                                 s->secondaryAllocator.Get(), nullptr,
                                                 IID_PPV_ARGS(&s->secondaryList)),
              "Create secondary list");
        Check(s->primaryList->Close(), "Close primary list");
        Check(s->secondaryList->Close(), "Close secondary list");
        s->singleQueries = MakeQueries(primary_.value.Get(), 3);
        s->primaryQueries = MakeQueries(primary_.value.Get(), 2);
        s->secondaryQueries = MakeQueries(secondary_.value.Get(), 2);
    }

    void Wait(ID3D12Fence* fence, UINT64 value) {
        if (!value || fence->GetCompletedValue() >= value) return;
        Check(fence->SetEventOnCompletion(value, event_), "SetEventOnCompletion");
        if (WaitForSingleObject(event_, kTimeoutMs) != WAIT_OBJECT_0)
            throw std::runtime_error("GPU fence timeout");
    }

    std::vector<UINT64> Read(const Queries& q) {
        D3D12_RANGE range = {0, q.count * sizeof(UINT64)};
        UINT64* data = nullptr;
        Check(q.readback->Map(0, &range, reinterpret_cast<void**>(&data)), "Map query readback");
        std::vector<UINT64> out(data, data + q.count);
        D3D12_RANGE empty = {0, 0};
        q.readback->Unmap(0, &empty);
        return out;
    }

    static D3D12_RESOURCE_BARRIER Transition(ID3D12Resource* r, D3D12_RESOURCE_STATES before,
                                             D3D12_RESOURCE_STATES after) {
        D3D12_RESOURCE_BARRIER b = {};
        b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Transition.pResource = r;
        b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        b.Transition.StateBefore = before;
        b.Transition.StateAfter = after;
        return b;
    }

    void Constants(ID3D12GraphicsCommandList* list, UINT loops, UINT frame) {
        UINT values[4] = {options_.width, options_.height, loops, frame};
        list->SetComputeRoot32BitConstants(0, 4, values, 0);
    }

    void SubmitSingle(Slot& s, UINT frame, UINT64 fenceValue) {
        Check(s.primaryAllocator->Reset(), "Reset primary allocator");
        Check(s.primaryList->Reset(s.primaryAllocator.Get(), nullptr), "Reset primary list");
        auto* list = s.primaryList.Get();
        list->SetComputeRootSignature(primary_.root.Get());
        list->EndQuery(s.singleQueries.heap.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 0);
        auto b = Transition(s.singleInput.Get(), D3D12_RESOURCE_STATE_COMMON,
                            D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        list->ResourceBarrier(1, &b);
        list->SetPipelineState(primary_.scene.Get());
        Constants(list, options_.sceneLoops, frame);
        list->SetComputeRootUnorderedAccessView(2, s.singleInput->GetGPUVirtualAddress());
        list->Dispatch((options_.width * options_.height + 255) / 256, 1, 1);
        b = Transition(s.singleInput.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                       D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        list->ResourceBarrier(1, &b);
        list->EndQuery(s.singleQueries.heap.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 1);
        b = Transition(s.singleOutput.Get(), D3D12_RESOURCE_STATE_COMMON,
                       D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        list->ResourceBarrier(1, &b);
        list->SetPipelineState(primary_.post.Get());
        Constants(list, options_.postLoops, frame);
        list->SetComputeRootShaderResourceView(1, s.singleInput->GetGPUVirtualAddress());
        list->SetComputeRootUnorderedAccessView(2, s.singleOutput->GetGPUVirtualAddress());
        list->Dispatch((options_.width * options_.height + 255) / 256, 1, 1);
        D3D12_RESOURCE_BARRIER finish[2] = {
            Transition(s.singleInput.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
                       D3D12_RESOURCE_STATE_COMMON),
            Transition(s.singleOutput.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                       D3D12_RESOURCE_STATE_COMMON)};
        list->ResourceBarrier(2, finish);
        list->EndQuery(s.singleQueries.heap.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 2);
        list->ResolveQueryData(s.singleQueries.heap.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 0, 3,
                               s.singleQueries.readback.Get(), 0);
        Check(list->Close(), "Close single list");
        ID3D12CommandList* lists[] = {list};
        primary_.queue->ExecuteCommandLists(1, lists);
        Check(primary_.queue->Signal(singleFence_.Get(), fenceValue), "Signal single fence");
    }

    void SubmitMulti(Slot& s, UINT frame, UINT64 renderValue, UINT64 completeValue) {
        Check(s.primaryAllocator->Reset(), "Reset primary allocator");
        Check(s.primaryList->Reset(s.primaryAllocator.Get(), nullptr), "Reset primary list");
        auto* p = s.primaryList.Get();
        p->SetComputeRootSignature(primary_.root.Get());
        p->EndQuery(s.primaryQueries.heap.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 0);
        auto b = Transition(s.sharedPrimary.Get(), D3D12_RESOURCE_STATE_COMMON,
                            D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        p->ResourceBarrier(1, &b);
        p->SetPipelineState(primary_.scene.Get());
        Constants(p, options_.sceneLoops, frame);
        p->SetComputeRootUnorderedAccessView(2, s.sharedPrimary->GetGPUVirtualAddress());
        p->Dispatch((options_.width * options_.height + 255) / 256, 1, 1);
        b = Transition(s.sharedPrimary.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                       D3D12_RESOURCE_STATE_COMMON);
        p->ResourceBarrier(1, &b);
        p->EndQuery(s.primaryQueries.heap.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 1);
        p->ResolveQueryData(s.primaryQueries.heap.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 0, 2,
                            s.primaryQueries.readback.Get(), 0);
        Check(p->Close(), "Close primary multi list");
        ID3D12CommandList* primaryLists[] = {p};
        primary_.queue->ExecuteCommandLists(1, primaryLists);
        Check(primary_.queue->Signal(renderPrimary_.Get(), renderValue), "Signal render fence");

        Check(s.secondaryAllocator->Reset(), "Reset secondary allocator");
        Check(s.secondaryList->Reset(s.secondaryAllocator.Get(), nullptr), "Reset secondary list");
        auto* q = s.secondaryList.Get();
        q->SetComputeRootSignature(secondary_.root.Get());
        q->EndQuery(s.secondaryQueries.heap.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 0);
        D3D12_RESOURCE_BARRIER begin[2] = {
            Transition(s.sharedSecondary.Get(), D3D12_RESOURCE_STATE_COMMON,
                       D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE),
            Transition(s.multiOutput.Get(), D3D12_RESOURCE_STATE_COMMON,
                       D3D12_RESOURCE_STATE_UNORDERED_ACCESS)};
        q->ResourceBarrier(2, begin);
        q->SetPipelineState(secondary_.post.Get());
        Constants(q, options_.postLoops, frame);
        q->SetComputeRootShaderResourceView(1, s.sharedSecondary->GetGPUVirtualAddress());
        q->SetComputeRootUnorderedAccessView(2, s.multiOutput->GetGPUVirtualAddress());
        q->Dispatch((options_.width * options_.height + 255) / 256, 1, 1);
        D3D12_RESOURCE_BARRIER finish[2] = {
            Transition(s.sharedSecondary.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
                       D3D12_RESOURCE_STATE_COMMON),
            Transition(s.multiOutput.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                       D3D12_RESOURCE_STATE_COMMON)};
        q->ResourceBarrier(2, finish);
        q->EndQuery(s.secondaryQueries.heap.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 1);
        q->ResolveQueryData(s.secondaryQueries.heap.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 0, 2,
                            s.secondaryQueries.readback.Get(), 0);
        Check(q->Close(), "Close secondary multi list");
        Check(secondary_.queue->Wait(renderSecondary_.Get(), renderValue), "Queue wait render fence");
        ID3D12CommandList* secondaryLists[] = {q};
        secondary_.queue->ExecuteCommandLists(1, secondaryLists);
        Check(secondary_.queue->Signal(completion_.Get(), completeValue), "Signal completion fence");
    }

    void Collect(Slot& s, Phase* phase) {
        if (s.previousFrame < 0) return;
        Wait(phase->heterogeneous ? completion_.Get() : singleFence_.Get(), s.completionFence);
        Frame& f = phase->frames[static_cast<size_t>(s.previousFrame)];
        if (phase->heterogeneous) {
            const auto a = Read(s.primaryQueries);
            const auto b = Read(s.secondaryQueries);
            f.sceneMs = static_cast<double>(a[1] - a[0]) * 1000.0 / primary_.timestampFrequency;
            f.postMs = static_cast<double>(b[1] - b[0]) * 1000.0 / secondary_.timestampFrequency;
            f.endTick = b[1];
        } else {
            const auto a = Read(s.singleQueries);
            f.sceneMs = static_cast<double>(a[1] - a[0]) * 1000.0 / primary_.timestampFrequency;
            f.postMs = static_cast<double>(a[2] - a[1]) * 1000.0 / primary_.timestampFrequency;
            f.endTick = a[2];
        }
        s.previousFrame = -1;
    }

    Phase Run(UINT index, bool heterogeneous, UINT count, bool retain) {
        Phase phase;
        phase.index = index;
        phase.heterogeneous = heterogeneous;
        phase.frames.resize(count);
        for (auto& s : slots_) { s.previousFrame = -1; s.completionFence = 0; }
        const auto start = std::chrono::steady_clock::now();
        for (UINT frame = 0; frame < count; ++frame) {
            Slot& s = slots_[frame % kSlots];
            Collect(s, &phase);
            phase.frames[frame].index = frame;
            if (heterogeneous) {
                const UINT64 render = ++renderValue_;
                const UINT64 complete = ++completionValue_;
                SubmitMulti(s, frame, render, complete);
                s.completionFence = complete;
            } else {
                const UINT64 complete = ++singleValue_;
                SubmitSingle(s, frame, complete);
                s.completionFence = complete;
            }
            s.previousFrame = static_cast<int>(frame);
        }
        for (auto& s : slots_) Collect(s, &phase);
        const auto end = std::chrono::steady_clock::now();
        phase.wallFps = count / std::chrono::duration<double>(end - start).count();
        if (!retain) phase.frames.clear();
        return phase;
    }

    static double Mean(const std::vector<double>& v) {
        return v.empty() ? 0.0 : std::accumulate(v.begin(), v.end(), 0.0) / v.size();
    }

    static double Percentile(std::vector<double> v, double p) {
        if (v.empty()) return 0.0;
        std::sort(v.begin(), v.end());
        const double x = p * (v.size() - 1);
        const size_t lo = static_cast<size_t>(x), hi = static_cast<size_t>(std::ceil(x));
        return v[lo] + (v[hi] - v[lo]) * (x - lo);
    }

    static void Summarize(Phase* phase, UINT64 frequency) {
        std::vector<double> intervals, scene, post;
        for (size_t i = 0; i < phase->frames.size(); ++i) {
            scene.push_back(phase->frames[i].sceneMs);
            post.push_back(phase->frames[i].postMs);
            if (!i) continue;
            phase->frames[i].intervalMs = static_cast<double>(phase->frames[i].endTick -
                                               phase->frames[i - 1].endTick) * 1000.0 / frequency;
            intervals.push_back(phase->frames[i].intervalMs);
        }
        const double mean = Mean(intervals);
        phase->avgFps = mean > 0 ? 1000.0 / mean : 0.0;
        std::sort(intervals.begin(), intervals.end(), std::greater<double>());
        const size_t slow = std::max<size_t>(1, static_cast<size_t>(std::ceil(intervals.size() * .01)));
        const double slowMean = std::accumulate(intervals.begin(), intervals.begin() + slow, 0.0) / slow;
        phase->lowFps = slowMean > 0 ? 1000.0 / slowMean : 0.0;
        phase->p99Ms = Percentile(intervals, .99);
        phase->sceneMs = Mean(scene);
        phase->postMs = Mean(post);
    }
};

double Median(std::vector<double> v) {
    if (v.empty()) return 0.0;
    std::sort(v.begin(), v.end());
    return v[v.size() / 2];
}

std::wstring Stamp() {
    SYSTEMTIME t = {};
    GetLocalTime(&t);
    wchar_t value[64] = {};
    swprintf_s(value, L"%04u%02u%02u-%02u%02u%02u", t.wYear, t.wMonth, t.wDay,
               t.wHour, t.wMinute, t.wSecond);
    return value;
}

void WriteResults(const std::filesystem::path& dir, const std::vector<Phase>& phases,
                  const Adapter& primary, const Adapter& secondary, const Options& o) {
    std::filesystem::create_directories(dir);
    std::ofstream csv(dir / "frames.csv", std::ios::binary);
    csv << "phase,mode,frame,completion_interval_ms,scene_gpu_ms,post_gpu_ms\n" << std::fixed
        << std::setprecision(6);
    for (const auto& p : phases) for (const auto& f : p.frames) {
        csv << p.index << ',' << (p.heterogeneous ? "heterogeneous" : "single") << ',' << f.index << ',';
        if (f.index) csv << f.intervalMs;
        csv << ',' << f.sceneMs << ',' << f.postMs << '\n';
    }
    std::vector<double> aFps, bFps, aLow, bLow;
    for (const auto& p : phases) {
        (p.heterogeneous ? bFps : aFps).push_back(p.avgFps);
        (p.heterogeneous ? bLow : aLow).push_back(p.lowFps);
    }
    const double single = Median(aFps), multi = Median(bFps);
    const double singleLow = Median(aLow), multiLow = Median(bLow);
    std::ofstream report(dir / "report.txt", std::ios::binary);
    report << "Pavise D3D12 Heterogeneous Compute Bench\n"
           << "Primary: " << Narrow(primary.desc.Description) << "\nSecondary: "
           << Narrow(secondary.desc.Description) << "\nResolution: " << o.width << 'x' << o.height
           << " loops=" << o.sceneLoops << '/' << o.postLoops << "\n\n" << std::fixed
           << std::setprecision(3);
    for (const auto& p : phases)
        report << "Phase " << p.index << ' ' << (p.heterogeneous ? "heterogeneous" : "single")
               << " avg=" << p.avgFps << " 1%low=" << p.lowFps << " p99=" << p.p99Ms
               << " wall=" << p.wallFps << " sceneGPU=" << p.sceneMs << " postGPU=" << p.postMs << "\n";
    report << "\nMedian FPS single/heterogeneous: " << single << " / " << multi
           << " delta=" << (multi / single - 1.0) * 100.0 << "%\n"
           << "Median 1%low single/heterogeneous: " << singleLow << " / " << multiLow
           << " delta=" << (multiLow / singleLow - 1.0) * 100.0 << "%\n";
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
    try {
        const Options o = Args(argc, argv);
        if (!o.run) {
            std::cout << "Safe plan mode: no D3D device, queue, shader, or window was created.\n"
                         "Pass --run explicitly to start the no-window GPU benchmark.\n";
            return 0;
        }
        const auto all = Adapters();
        const auto primary = Find(all, o.primaryIndex);
        const auto secondary = Find(all, o.secondaryIndex);
        std::cout << "primary=" << Narrow(primary.desc.Description) << " secondary="
                  << Narrow(secondary.desc.Description) << " (no window/offscreen buffers only)\n";
        Runner runner(primary, secondary, o);
        const std::array<bool, 4> abba = {false, true, true, false};
        const std::array<bool, 4> baab = {true, false, false, true};
        std::vector<Phase> phases;
        for (UINT repeat = 0; repeat < o.repeats; ++repeat) {
            const auto& block = repeat % 2 ? baab : abba;
            for (bool mode : block) {
                const UINT index = static_cast<UINT>(phases.size() + 1);
                std::cout << "phase " << index << '/' << (o.repeats * 4) << ' '
                          << (mode ? "heterogeneous" : "single") << "..." << std::flush;
                phases.push_back(runner.RunPhase(index, mode, o.warmup, o.frames));
                std::cout << std::fixed << std::setprecision(2) << phases.back().avgFps << " fps\n";
            }
        }
        std::filesystem::path out = o.output;
        if (out.empty()) out = std::filesystem::current_path() / (L"Pavise-D3D12Gpu-Results-" + Stamp());
        WriteResults(out, phases, primary, secondary, o);
        std::cout << "results=" << Narrow(out.wstring()) << "\n";
        return 0;
    } catch (const std::exception& e) {
        std::cerr << "error: " << e.what() << "\n";
        return 1;
    }
}
