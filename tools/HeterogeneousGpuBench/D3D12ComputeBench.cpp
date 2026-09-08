// 文件用途 Pavise 无窗口 D3D12 异构 GPU 计算台架

#define WIN32_LEAN_AND_MEAN
#ifndef NOMINMAX
#define NOMINMAX
#endif
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
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <limits>
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
constexpr UINT kValidationTolerance = 1;
constexpr double kMaximumGpuWallGapPct = 5.0;
constexpr double kMaximumBaselineDriftPct = 5.0;
constexpr double kMinimumAverageGainPct = 2.0;
constexpr double kMinimumLowGainPct = 1.0;
constexpr double kMaximumP99RegressionPct = 5.0;
constexpr const char* kScope = "owned_offscreen_compute_only";

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

enum class Action { Plan, Run, SelfTest, ListAdapters, ValidateOnly };
enum class TransferPath { Direct, Copy };
enum class SecondaryInputMode { Shared, Local };

const char* PathName(TransferPath path) { return path == TransferPath::Copy ? "copy" : "direct"; }
const char* SecondaryInputName(SecondaryInputMode mode) { return mode == SecondaryInputMode::Local ? "local" : "shared"; }
const char* ModeName(bool multi) { return multi ? "heterogeneous" : "single"; }

struct Options {
    Action action = Action::Plan;
    TransferPath transferPath = TransferPath::Direct;
    SecondaryInputMode secondaryInput = SecondaryInputMode::Shared;
    UINT width = 1920;
    UINT height = 1080;
    UINT warmup = 120;
    UINT frames = 600;
    UINT repeats = 3;
    UINT sceneLoops = 12;
    UINT postLoops = 8;
    UINT primaryIndex = 0;
    UINT secondaryIndex = 1;
    UINT preconditionSeconds = 0;
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
        if (arg == L"--run") o.action = Action::Run;
        else if (arg == L"--self-test") o.action = Action::SelfTest;
        else if (arg == L"--list-adapters") o.action = Action::ListAdapters;
        else if (arg == L"--validate-only") o.action = Action::ValidateOnly;
        else if (arg == L"--help" || arg == L"-h" || arg == L"/?") o.action = Action::Plan;
        else if (arg == L"--width") o.width = Number(next(), 64, 7680, "width");
        else if (arg == L"--height") o.height = Number(next(), 64, 4320, "height");
        else if (arg == L"--warmup-frames") o.warmup = Number(next(), 0, 100000, "warmup");
        else if (arg == L"--measure-frames") o.frames = Number(next(), 30, 100000, "frames");
        else if (arg == L"--repeats") o.repeats = Number(next(), 1, 20, "repeats");
        else if (arg == L"--scene-loops") o.sceneLoops = Number(next(), 1, 1000, "scene loops");
        else if (arg == L"--post-loops") o.postLoops = Number(next(), 1, 1000, "post loops");
        else if (arg == L"--primary-index") o.primaryIndex = Number(next(), 0, 64, "primary index");
        else if (arg == L"--secondary-index") o.secondaryIndex = Number(next(), 0, 64, "secondary index");
        else if (arg == L"--precondition-seconds") o.preconditionSeconds = Number(next(), 0, 600, "precondition seconds");
        else if (arg == L"--transfer-path") {
            const std::wstring path = next();
            if (path == L"direct") o.transferPath = TransferPath::Direct;
            else if (path == L"copy") o.transferPath = TransferPath::Copy;
            else throw std::runtime_error("transfer path must be direct or copy");
        }
        else if (arg == L"--secondary-input") {
            const std::wstring input = next();
            if (input == L"shared") o.secondaryInput = SecondaryInputMode::Shared;
            else if (input == L"local") o.secondaryInput = SecondaryInputMode::Local;
            else throw std::runtime_error("secondary input must be shared or local");
        }
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

bool SameAdapter(const Adapter& a, const Adapter& b) {
    return a.desc.AdapterLuid.HighPart == b.desc.AdapterLuid.HighPart &&
           a.desc.AdapterLuid.LowPart == b.desc.AdapterLuid.LowPart;
}

std::string Hex64(UINT64 value) {
    std::ostringstream text;
    text << std::hex << std::setw(16) << std::setfill('0') << value;
    return text.str();
}

std::string AdapterLuid(const Adapter& adapter) {
    return Hex64((static_cast<UINT64>(static_cast<UINT32>(adapter.desc.AdapterLuid.HighPart)) << 32) |
                 adapter.desc.AdapterLuid.LowPart);
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

[numthreads(16, 16, 1)]
void Scene(uint3 tid : SV_DispatchThreadID) {
    uint x = tid.x;
    uint y = tid.y;
    if (x >= width || y >= height) return;
    uint id = y * width + x;
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

[numthreads(16, 16, 1)]
void Post(uint3 tid : SV_DispatchThreadID) {
    uint x = tid.x;
    uint y = tid.y;
    if (x >= width || y >= height) return;
    uint id = y * width + x;
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
    if (!d.timestampFrequency) throw std::runtime_error("GPU timestamp frequency is zero");

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
    ComPtr<ID3D12Resource> secondaryLocalInput;
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
    UINT frameCount = 0;
    UINT intervalCount = 0;
    UINT64 timestampFrequency = 0;
    std::vector<Frame> frames;
    double wallSeconds = 0.0;
    double gpuSpanMs = 0.0;
    double wallFps = 0.0;
    double avgFps = 0.0;
    double lowFps = 0.0;
    double p99Ms = 0.0;
    double sceneMs = 0.0;
    double postMs = 0.0;
    double gpuWallGapPct = 0.0;
    bool valid = false;
    std::vector<std::string> reasons;
};

double Mean(const std::vector<double>& values) {
    return values.empty() ? 0.0 : std::accumulate(values.begin(), values.end(), 0.0) / values.size();
}

double Percentile(std::vector<double> values, double p) {
    if (values.empty()) return 0.0;
    std::sort(values.begin(), values.end());
    const double position = std::clamp(p, 0.0, 1.0) * (values.size() - 1);
    const size_t low = static_cast<size_t>(position), high = static_cast<size_t>(std::ceil(position));
    return values[low] + (values[high] - values[low]) * (position - low);
}

double Median(const std::vector<double>& values) { return Percentile(values, 0.5); }

double ChangePct(double current, double baseline) {
    if (!(baseline > 0.0) || !std::isfinite(baseline) || !std::isfinite(current))
        return std::numeric_limits<double>::quiet_NaN();
    return (current / baseline - 1.0) * 100.0;
}

double RangePct(const std::vector<double>& values) {
    if (values.empty()) return std::numeric_limits<double>::quiet_NaN();
    const auto range = std::minmax_element(values.begin(), values.end());
    return ChangePct(*range.second, *range.first);
}

bool PositiveFinite(double value) { return std::isfinite(value) && value > 0.0; }

bool Invalidate(Phase* phase, const std::string& reason) {
    phase->valid = false;
    phase->reasons.push_back(reason);
    return false;
}

bool SummarizePhase(Phase* phase, UINT64 frequency) {
    phase->valid = false;
    phase->timestampFrequency = frequency;
    phase->frameCount = static_cast<UINT>(phase->frames.size());
    phase->intervalCount = phase->frameCount ? phase->frameCount - 1 : 0;
    if (!frequency) return Invalidate(phase, "zero_timestamp_frequency");
    if (phase->frameCount < 2) return Invalidate(phase, "insufficient_completion_intervals");
    if (!PositiveFinite(phase->wallSeconds)) return Invalidate(phase, "invalid_wall_duration");
    std::vector<double> intervals, scene, post;
    intervals.reserve(phase->intervalCount);
    for (size_t i = 0; i < phase->frames.size(); ++i) {
        Frame& frame = phase->frames[i];
        if (frame.index != i || !frame.endTick || !PositiveFinite(frame.sceneMs) ||
            !PositiveFinite(frame.postMs)) return Invalidate(phase, "invalid_frame_timestamp_or_duration");
        scene.push_back(frame.sceneMs);
        post.push_back(frame.postMs);
        if (!i) continue;
        if (frame.endTick <= phase->frames[i - 1].endTick)
            return Invalidate(phase, "nonmonotonic_completion_timestamp");
        frame.intervalMs = static_cast<double>(frame.endTick - phase->frames[i - 1].endTick) *
                           1000.0 / frequency;
        if (!PositiveFinite(frame.intervalMs)) return Invalidate(phase, "invalid_completion_interval");
        intervals.push_back(frame.intervalMs);
    }
    phase->gpuSpanMs = static_cast<double>(phase->frames.back().endTick - phase->frames.front().endTick) *
                       1000.0 / frequency;
    phase->avgFps = 1000.0 / Mean(intervals);
    phase->wallFps = phase->frameCount / phase->wallSeconds;
    phase->p99Ms = Percentile(intervals, .99);
    std::sort(intervals.begin(), intervals.end(), std::greater<double>());
    const size_t slowCount = std::max<size_t>(1, static_cast<size_t>(std::ceil(intervals.size() * .01)));
    const double slowMean = std::accumulate(intervals.begin(), intervals.begin() + slowCount, 0.0) /
                            slowCount;
    phase->lowFps = 1000.0 / slowMean;
    phase->sceneMs = Mean(scene);
    phase->postMs = Mean(post);
    phase->gpuWallGapPct = std::abs(ChangePct(phase->avgFps, phase->wallFps));
    if (!PositiveFinite(phase->avgFps) || !PositiveFinite(phase->wallFps) ||
        !PositiveFinite(phase->lowFps) || !PositiveFinite(phase->p99Ms) ||
        !std::isfinite(phase->gpuWallGapPct)) return Invalidate(phase, "nonfinite_phase_metrics");
    if (phase->gpuWallGapPct > kMaximumGpuWallGapPct)
        return Invalidate(phase, "gpu_wall_rate_gap_exceeds_5_percent");
    phase->valid = true;
    return true;
}

struct ValidationSeed {
    UINT seed = 0;
    UINT64 expectedPixels = 0;
    UINT64 sourcePixelsSingle = 0, sourcePixelsMulti = 0;
    UINT64 outputPixelsSingle = 0, outputPixelsMulti = 0;
    UINT64 sourceMismatchPixels = 0, outputMismatchPixels = 0;
    UINT64 sourceAlphaErrors = 0, singleAlphaErrors = 0, multiAlphaErrors = 0;
    UINT64 referenceSamples = 0, referenceMismatchSingle = 0, referenceMismatchMulti = 0;
    UINT outputMaxRgbError = 0, referenceMaxRgbErrorSingle = 0, referenceMaxRgbErrorMulti = 0;
    UINT64 sourceHashSingle = 0, sourceHashMulti = 0, outputHashSingle = 0, outputHashMulti = 0;
    bool staleSource = false, staleOutput = false, passed = false;
    std::vector<std::string> reasons;
};

struct ValidationReport {
    bool attempted = false;
    bool passed = false;
    UINT width = 0, height = 0;
    std::vector<ValidationSeed> seeds;
    std::vector<std::string> reasons;
};

UINT64 PixelHash(const std::vector<UINT>& values) {
    UINT64 hash = 14695981039346656037ull;
    for (UINT pixel : values) for (UINT shift = 0; shift < 32; shift += 8) {
        hash ^= (pixel >> shift) & 255u;
        hash *= 1099511628211ull;
    }
    return hash;
}

UINT RgbError(UINT a, UINT b) {
    UINT error = 0;
    for (UINT shift = 0; shift < 24; shift += 8) {
        const int delta = static_cast<int>((a >> shift) & 255u) - static_cast<int>((b >> shift) & 255u);
        error = std::max(error, static_cast<UINT>(std::abs(delta)));
    }
    return error;
}

// 着色器算的是 8 位通道值的加权平均 整数运算给一份独立参考
// 固定的 1 个码位容差用来吃掉 GPU 的舍入
UINT ReferencePost(const std::vector<UINT>& source, UINT width, UINT height, UINT loops, size_t id) {
    UINT64 sums[3] = {};
    auto add = [&](size_t index) {
        for (UINT c = 0; c < 3; ++c) sums[c] += (source[index] >> (c * 8)) & 255u;
    };
    const UINT x = static_cast<UINT>(id % width), y = static_cast<UINT>(id / width);
    add(id);
    for (UINT i = 0; i < loops; ++i) {
        const UINT radius = i + 1;
        const UINT left = x > radius ? x - radius : 0;
        const UINT right = std::min(width - 1, x + radius);
        const UINT up = y > radius ? y - radius : 0;
        const UINT down = std::min(height - 1, y + radius);
        add(static_cast<size_t>(y) * width + left);
        add(static_cast<size_t>(y) * width + right);
        add(static_cast<size_t>(up) * width + x);
        add(static_cast<size_t>(down) * width + x);
    }
    const UINT64 weight = 1ull + 4ull * loops;
    UINT pixel = 0xff000000u;
    for (UINT c = 0; c < 3; ++c) pixel |= static_cast<UINT>((sums[c] + weight / 2) / weight) << (c * 8);
    return pixel;
}

std::vector<size_t> ReferenceIndices(UINT width, UINT height) {
    const size_t count = static_cast<size_t>(width) * height;
    std::vector<size_t> indices;
    const size_t samples = std::min<size_t>(4096, count);
    for (size_t i = 0; i < samples; ++i) indices.push_back(i * count / samples);
    for (size_t i = 0; i < 16; ++i) {
        const size_t x = i * (width - 1) / 15, y = i * (height - 1) / 15;
        indices.push_back(x);
        indices.push_back(static_cast<size_t>(height - 1) * width + x);
        indices.push_back(y * width);
        indices.push_back(y * width + width - 1);
    }
    std::sort(indices.begin(), indices.end());
    indices.erase(std::unique(indices.begin(), indices.end()), indices.end());
    return indices;
}

void ComparePixels(ValidationSeed* result, const Options& options,
                   const std::vector<UINT>& sourceSingle, const std::vector<UINT>& outputSingle,
                   const std::vector<UINT>& sourceMulti, const std::vector<UINT>& outputMulti,
                   const ValidationSeed* previous) {
    result->expectedPixels = static_cast<UINT64>(options.width) * options.height;
    result->sourcePixelsSingle = sourceSingle.size();
    result->sourcePixelsMulti = sourceMulti.size();
    result->outputPixelsSingle = outputSingle.size();
    result->outputPixelsMulti = outputMulti.size();
    if (sourceSingle.size() != result->expectedPixels || sourceMulti.size() != result->expectedPixels ||
        outputSingle.size() != result->expectedPixels || outputMulti.size() != result->expectedPixels) {
        result->reasons.push_back("pixel_count_mismatch");
        return;
    }
    for (size_t i = 0; i < sourceSingle.size(); ++i) {
        result->sourceMismatchPixels += sourceSingle[i] != sourceMulti[i];
        result->sourceAlphaErrors += ((sourceSingle[i] >> 24) != 255u || (sourceMulti[i] >> 24) != 255u);
        result->singleAlphaErrors += (outputSingle[i] >> 24) != 255u;
        result->multiAlphaErrors += (outputMulti[i] >> 24) != 255u;
        const UINT error = RgbError(outputSingle[i], outputMulti[i]);
        result->outputMaxRgbError = std::max(result->outputMaxRgbError, error);
        result->outputMismatchPixels += error > kValidationTolerance;
    }
    const auto indices = ReferenceIndices(options.width, options.height);
    result->referenceSamples = indices.size();
    for (size_t index : indices) {
        const UINT expected = ReferencePost(sourceSingle, options.width, options.height, options.postLoops, index);
        const UINT a = RgbError(expected, outputSingle[index]);
        const UINT b = RgbError(expected, outputMulti[index]);
        result->referenceMaxRgbErrorSingle = std::max(result->referenceMaxRgbErrorSingle, a);
        result->referenceMaxRgbErrorMulti = std::max(result->referenceMaxRgbErrorMulti, b);
        result->referenceMismatchSingle += a > kValidationTolerance;
        result->referenceMismatchMulti += b > kValidationTolerance;
    }
    result->sourceHashSingle = PixelHash(sourceSingle);
    result->sourceHashMulti = PixelHash(sourceMulti);
    result->outputHashSingle = PixelHash(outputSingle);
    result->outputHashMulti = PixelHash(outputMulti);
    if (previous) {
        result->staleSource = result->sourceHashSingle == previous->sourceHashSingle ||
                              result->sourceHashMulti == previous->sourceHashMulti;
        result->staleOutput = result->outputHashSingle == previous->outputHashSingle ||
                              result->outputHashMulti == previous->outputHashMulti;
    }
    if (result->sourceMismatchPixels) result->reasons.push_back("source_pixels_differ_between_paths");
    if (result->outputMismatchPixels) result->reasons.push_back("output_rgb_error_exceeds_fixed_tolerance");
    if (result->sourceAlphaErrors || result->singleAlphaErrors || result->multiAlphaErrors)
        result->reasons.push_back("alpha_or_unwritten_pixel_error");
    if (result->referenceMismatchSingle || result->referenceMismatchMulti)
        result->reasons.push_back("cpu_reference_error_exceeds_fixed_tolerance");
    if (result->staleSource || result->staleOutput) result->reasons.push_back("unchanged_image_across_distinct_seeds");
    result->passed = result->reasons.empty();
}

struct ModeMetrics {
    double avg = 0.0, wall = 0.0, low = 0.0, p99 = 0.0;
};

struct Evaluation {
    std::string verdict = "INVALID";
    bool scored = true;
    ModeMetrics single, multi;
    double deltaAvg = 0.0, deltaWall = 0.0, deltaLow = 0.0, p99Change = 0.0;
    double baselineDrift = 0.0, maximumGpuWallGap = 0.0;
    UINT pairWins = 0, pairCount = 0;
    double pairWinRatio = 0.0;
    bool identity = false, validation = false, complete = false, measurements = false;
    bool order = false, clock = false, drift = false;
    bool averageGain = false, wallGain = false, lowGain = false, p99Gate = false, pairs = false;
    std::vector<std::string> reasons;
};

Evaluation Evaluate(const std::vector<Phase>& phases, const Options& options,
                    bool validationPassed, bool distinctAdapters) {
    Evaluation result;
    result.identity = distinctAdapters;
    result.validation = validationPassed;
    result.complete = phases.size() == static_cast<size_t>(options.repeats) * 4;
    result.measurements = !phases.empty();
    result.order = !phases.empty();
    result.clock = !phases.empty();
    std::vector<double> aAvg, aWall, aLow, aP99, bAvg, bWall, bLow, bP99;
    const std::array<bool, 4> abba = {false, true, true, false};
    const std::array<bool, 4> baab = {true, false, false, true};
    for (size_t i = 0; i < phases.size(); ++i) {
        const Phase& phase = phases[i];
        const auto& block = ((i / 4) % 2) ? baab : abba;
        if (phase.index != i + 1 || phase.heterogeneous != block[i % 4]) result.order = false;
        if (!phase.valid || phase.frameCount != options.frames || phase.intervalCount != options.frames - 1 ||
            !PositiveFinite(phase.avgFps) || !PositiveFinite(phase.wallFps) ||
            !PositiveFinite(phase.lowFps) || !PositiveFinite(phase.p99Ms)) result.measurements = false;
        if (!std::isfinite(phase.gpuWallGapPct) || phase.gpuWallGapPct > kMaximumGpuWallGapPct) result.clock = false;
        result.maximumGpuWallGap = std::max(result.maximumGpuWallGap, phase.gpuWallGapPct);
        (phase.heterogeneous ? bAvg : aAvg).push_back(phase.avgFps);
        (phase.heterogeneous ? bWall : aWall).push_back(phase.wallFps);
        (phase.heterogeneous ? bLow : aLow).push_back(phase.lowFps);
        (phase.heterogeneous ? bP99 : aP99).push_back(phase.p99Ms);
    }
    result.single = {Median(aAvg), Median(aWall), Median(aLow), Median(aP99)};
    result.multi = {Median(bAvg), Median(bWall), Median(bLow), Median(bP99)};
    result.deltaAvg = ChangePct(result.multi.avg, result.single.avg);
    result.deltaWall = ChangePct(result.multi.wall, result.single.wall);
    result.deltaLow = ChangePct(result.multi.low, result.single.low);
    result.p99Change = ChangePct(result.multi.p99, result.single.p99);
    result.baselineDrift = std::max(RangePct(aAvg), RangePct(aWall));
    result.drift = std::isfinite(result.baselineDrift) && result.baselineDrift <= kMaximumBaselineDriftPct;
    for (size_t i = 0; i + 1 < phases.size(); i += 2) {
        if (phases[i].heterogeneous == phases[i + 1].heterogeneous) continue;
        const Phase& a = phases[i].heterogeneous ? phases[i + 1] : phases[i];
        const Phase& b = phases[i].heterogeneous ? phases[i] : phases[i + 1];
        ++result.pairCount;
        if (b.avgFps >= a.avgFps && b.wallFps >= a.wallFps) ++result.pairWins;
    }
    result.pairWinRatio = result.pairCount ? static_cast<double>(result.pairWins) / result.pairCount : 0.0;
    result.pairs = result.pairCount > 0 && result.pairWins * 3 >= result.pairCount * 2;
    result.averageGain = std::isfinite(result.deltaAvg) && result.deltaAvg >= kMinimumAverageGainPct;
    result.wallGain = std::isfinite(result.deltaWall) && result.deltaWall >= kMinimumAverageGainPct;
    result.lowGain = std::isfinite(result.deltaLow) && result.deltaLow >= kMinimumLowGainPct;
    result.p99Gate = std::isfinite(result.p99Change) && result.p99Change <= kMaximumP99RegressionPct;
    if (!result.identity) result.reasons.push_back("adapters_are_not_distinct_hardware_luids");
    if (!result.validation) result.reasons.push_back("correctness_preflight_not_passed");
    if (!result.complete) result.reasons.push_back("incomplete_protocol");
    if (!result.measurements) result.reasons.push_back("invalid_phase_measurements");
    if (!result.order) result.reasons.push_back("unexpected_phase_order");
    if (!result.clock) result.reasons.push_back("gpu_wall_rate_gap_exceeds_5_percent");
    if (!result.drift) result.reasons.push_back("baseline_range_drift_exceeds_5_percent_or_unavailable");
    if (!result.reasons.empty()) return result;
    result.verdict = "NO_BENEFIT";
    if (!result.averageGain) result.reasons.push_back("average_completion_rate_gain_below_2_percent");
    if (!result.wallGain) result.reasons.push_back("wall_completion_rate_gain_below_2_percent");
    if (!result.lowGain) result.reasons.push_back("one_percent_low_gain_below_1_percent");
    if (!result.p99Gate) result.reasons.push_back("p99_regression_exceeds_5_percent");
    if (!result.pairs) result.reasons.push_back("fewer_than_two_thirds_paired_wins");
    if (result.reasons.empty()) result.verdict = "CANDIDATE";
    return result;
}

class Runner {
public:
    Runner(const Adapter& primaryAdapter, const Adapter& secondaryAdapter, const Options& o)
        : options_(o) {
        if (SameAdapter(primaryAdapter, secondaryAdapter))
            throw std::runtime_error("primary and secondary adapters must have different hardware LUIDs");
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
        SummarizePhase(&phase, heterogeneous ? secondary_.timestampFrequency : primary_.timestampFrequency);
        return phase;
    }

    void Validate(ValidationReport* report) {
        report->attempted = true;
        report->width = options_.width;
        report->height = options_.height;
        const std::array<UINT, 3> seeds = {0, 41, 193};
        for (UINT seed : seeds) {
            report->seeds.push_back(ValidationSeed{});
            ValidationSeed& result = report->seeds.back();
            result.seed = seed;
            try {
                Slot& slot = slots_[0];
                const UINT64 singleDone = ++singleValue_;
                SubmitSingle(slot, seed, singleDone);
                Wait(singleFence_.Get(), singleDone);
                std::vector<UINT> sourceSingle, outputSingle, sourceMulti, outputMulti;
                ReadPixelPair(primary_, slot.primaryAllocator.Get(), slot.primaryList.Get(),
                              slot.singleInput.Get(), slot.singleOutput.Get(), singleFence_.Get(),
                              &singleValue_, &sourceSingle, &outputSingle);
                const UINT64 render = ++renderValue_, complete = ++completionValue_;
                SubmitMulti(slot, seed, render, complete);
                Wait(completion_.Get(), complete);
                ReadPixelPair(secondary_, slot.secondaryAllocator.Get(), slot.secondaryList.Get(),
                              SecondarySource(slot), slot.multiOutput.Get(), completion_.Get(),
                              &completionValue_, &sourceMulti, &outputMulti);
                const ValidationSeed* previous = report->seeds.size() > 1
                    ? &report->seeds[report->seeds.size() - 2] : nullptr;
                ComparePixels(&result, options_, sourceSingle, outputSingle, sourceMulti, outputMulti, previous);
                if (!result.passed) throw std::runtime_error("pixel correctness preflight failed at seed " +
                                                            std::to_string(seed));
            } catch (const std::exception& error) {
                result.passed = false;
                result.reasons.push_back(error.what());
                report->reasons.push_back(error.what());
                report->passed = false;
                throw;
            }
        }
        report->passed = true;
    }

    double Precondition(UINT seconds) {
        if (!seconds) return 0.0;
        const auto begin = std::chrono::steady_clock::now();
        // 镜像等长批次要跑完整 ABBA 跑一半不许停
        do {
            Run(0, false, 8, false);
            Run(0, true, 8, false);
            Run(0, true, 8, false);
            Run(0, false, 8, false);
        } while (std::chrono::duration<double>(std::chrono::steady_clock::now() - begin).count() < seconds);
        return std::chrono::duration<double>(std::chrono::steady_clock::now() - begin).count();
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
        if (options_.secondaryInput == SecondaryInputMode::Local)
            s->secondaryLocalInput = LocalBuffer(secondary_.value.Get(), bytes_, D3D12_HEAP_TYPE_DEFAULT,
                                                  D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_COMMON);

        const auto flags = static_cast<D3D12_RESOURCE_FLAGS>(D3D12_RESOURCE_FLAG_ALLOW_CROSS_ADAPTER |
                                                              D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
        const auto desc = Buffer(bytes_, flags);
        const auto a = primary_.value->GetResourceAllocationInfo(0, 1, &desc);
        const auto b = secondary_.value->GetResourceAllocationInfo(0, 1, &desc);
        if (!a.SizeInBytes || !b.SizeInBytes || a.SizeInBytes == std::numeric_limits<UINT64>::max() ||
            b.SizeInBytes == std::numeric_limits<UINT64>::max() || !a.Alignment || !b.Alignment)
            throw std::runtime_error("invalid cross-adapter resource allocation information");
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

    void CheckDevices() {
        Check(primary_.value->GetDeviceRemovedReason(), "primary device removed");
        Check(secondary_.value->GetDeviceRemovedReason(), "secondary device removed");
    }

    void Wait(ID3D12Fence* fence, UINT64 value) {
        if (!value) return;
        CheckDevices();
        UINT64 done = fence->GetCompletedValue();
        if (done == std::numeric_limits<UINT64>::max())
            throw std::runtime_error("fence returned UINT64_MAX: device removed");
        if (done >= value) return;
        Check(fence->SetEventOnCompletion(value, event_), "SetEventOnCompletion");
        const DWORD waited = WaitForSingleObject(event_, kTimeoutMs);
        CheckDevices();
        if (waited != WAIT_OBJECT_0)
            throw std::runtime_error("GPU fence timeout");
        done = fence->GetCompletedValue();
        if (done == std::numeric_limits<UINT64>::max() || done < value)
            throw std::runtime_error("fence completion is invalid after wakeup");
    }

    void ReadPixelPair(Device& device, ID3D12CommandAllocator* allocator,
                       ID3D12GraphicsCommandList* list, ID3D12Resource* source, ID3D12Resource* output,
                       ID3D12Fence* fence, UINT64* value,
                       std::vector<UINT>* sourcePixels, std::vector<UINT>* outputPixels) {
        for (ID3D12Resource* resource : {source, output}) {
            const auto desc = resource->GetDesc();
            if (desc.Dimension != D3D12_RESOURCE_DIMENSION_BUFFER || desc.Width != bytes_ ||
                bytes_ % sizeof(UINT)) throw std::runtime_error("validation resource dimensions do not match");
        }
        auto readback = LocalBuffer(device.value.Get(), bytes_ * 2, D3D12_HEAP_TYPE_READBACK,
                                    D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_COPY_DEST);
        Check(allocator->Reset(), "Reset validation allocator");
        Check(list->Reset(allocator, nullptr), "Reset validation list");
        D3D12_RESOURCE_BARRIER begin[2] = {
            Transition(source, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_SOURCE),
            Transition(output, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_SOURCE)};
        list->ResourceBarrier(2, begin);
        list->CopyBufferRegion(readback.Get(), 0, source, 0, bytes_);
        list->CopyBufferRegion(readback.Get(), bytes_, output, 0, bytes_);
        D3D12_RESOURCE_BARRIER finish[2] = {
            Transition(source, D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_COMMON),
            Transition(output, D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_COMMON)};
        list->ResourceBarrier(2, finish);
        Check(list->Close(), "Close validation copy list");
        ID3D12CommandList* lists[] = {list};
        device.queue->ExecuteCommandLists(1, lists);
        Check(device.queue->Signal(fence, ++*value), "Signal validation copy fence");
        Wait(fence, *value);
        UINT* pixels = nullptr;
        const size_t count = static_cast<size_t>(bytes_ / sizeof(UINT));
        D3D12_RANGE range = {0, static_cast<SIZE_T>(bytes_ * 2)};
        Check(readback->Map(0, &range, reinterpret_cast<void**>(&pixels)), "Map validation pixels");
        try {
            sourcePixels->assign(pixels, pixels + count);
            outputPixels->assign(pixels + count, pixels + count * 2);
        } catch (...) {
            D3D12_RANGE noWrite = {0, 0};
            readback->Unmap(0, &noWrite);
            throw;
        }
        D3D12_RANGE noWrite = {0, 0};
        readback->Unmap(0, &noWrite);
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

    void DispatchWork(ID3D12GraphicsCommandList* list) {
        list->Dispatch((options_.width + 15) / 16, (options_.height + 15) / 16, 1);
    }

    ID3D12Resource* SecondarySource(const Slot& slot) const {
        return options_.secondaryInput == SecondaryInputMode::Local
            ? slot.secondaryLocalInput.Get() : slot.sharedSecondary.Get();
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
        DispatchWork(list);
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
        DispatchWork(list);
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
        ID3D12Resource* sceneTarget = options_.transferPath == TransferPath::Copy
            ? s.singleInput.Get() : s.sharedPrimary.Get();
        auto b = Transition(sceneTarget, D3D12_RESOURCE_STATE_COMMON,
                            D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        p->ResourceBarrier(1, &b);
        p->SetPipelineState(primary_.scene.Get());
        Constants(p, options_.sceneLoops, frame);
        p->SetComputeRootUnorderedAccessView(2, sceneTarget->GetGPUVirtualAddress());
        DispatchWork(p);
        if (options_.transferPath == TransferPath::Copy) {
            D3D12_RESOURCE_BARRIER copyBegin[2] = {
                Transition(sceneTarget, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COPY_SOURCE),
                Transition(s.sharedPrimary.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_DEST)};
            p->ResourceBarrier(2, copyBegin);
            p->CopyBufferRegion(s.sharedPrimary.Get(), 0, sceneTarget, 0, bytes_);
            D3D12_RESOURCE_BARRIER copyEnd[2] = {
                Transition(sceneTarget, D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_COMMON),
                Transition(s.sharedPrimary.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_COMMON)};
            p->ResourceBarrier(2, copyEnd);
        } else {
            b = Transition(sceneTarget, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COMMON);
            p->ResourceBarrier(1, &b);
        }
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
        // 两种模式都从这里开始计时 本地输入的暂存 包括它的拷贝和屏障
        // 都算进 post_gpu_ms 走同一条完成和栅栏链
        q->EndQuery(s.secondaryQueries.heap.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 0);
        ID3D12Resource* postInput = SecondarySource(s);
        if (options_.secondaryInput == SecondaryInputMode::Local) {
            D3D12_RESOURCE_BARRIER copyBegin[2] = {
                Transition(s.sharedSecondary.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_SOURCE),
                Transition(postInput, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_DEST)};
            q->ResourceBarrier(2, copyBegin);
            q->CopyBufferRegion(postInput, 0, s.sharedSecondary.Get(), 0, bytes_);
            D3D12_RESOURCE_BARRIER copied[3] = {
                Transition(s.sharedSecondary.Get(), D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_COMMON),
                Transition(postInput, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE),
                Transition(s.multiOutput.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_UNORDERED_ACCESS)};
            q->ResourceBarrier(3, copied);
        } else {
            D3D12_RESOURCE_BARRIER begin[2] = {
                Transition(postInput, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE),
                Transition(s.multiOutput.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_UNORDERED_ACCESS)};
            q->ResourceBarrier(2, begin);
        }
        q->SetPipelineState(secondary_.post.Get());
        Constants(q, options_.postLoops, frame);
        q->SetComputeRootShaderResourceView(1, postInput->GetGPUVirtualAddress());
        q->SetComputeRootUnorderedAccessView(2, s.multiOutput->GetGPUVirtualAddress());
        DispatchWork(q);
        D3D12_RESOURCE_BARRIER finish[2] = {
            Transition(postInput, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
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
            if (!a[0] || a[1] <= a[0] || !b[0] || b[1] <= b[0])
                throw std::runtime_error("nonmonotonic multi-adapter timestamp query");
            f.sceneMs = static_cast<double>(a[1] - a[0]) * 1000.0 / primary_.timestampFrequency;
            f.postMs = static_cast<double>(b[1] - b[0]) * 1000.0 / secondary_.timestampFrequency;
            f.endTick = b[1];
        } else {
            const auto a = Read(s.singleQueries);
            if (!a[0] || a[1] <= a[0] || a[2] <= a[1])
                throw std::runtime_error("nonmonotonic single-adapter timestamp query");
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
        phase.frameCount = count;
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
        phase.wallSeconds = std::chrono::duration<double>(end - start).count();
        phase.wallFps = count / phase.wallSeconds;
        if (!retain) phase.frames.clear();
        return phase;
    }

};

std::wstring Stamp() {
    SYSTEMTIME t = {};
    GetLocalTime(&t);
    wchar_t value[64] = {};
    swprintf_s(value, L"%04u%02u%02u-%02u%02u%02u", t.wYear, t.wMonth, t.wDay,
               t.wHour, t.wMinute, t.wSecond);
    return value;
}

std::string JsonString(const std::string& value) {
    std::ostringstream out;
    out << '"';
    for (unsigned char ch : value) {
        if (ch == '"') out << "\\\"";
        else if (ch == '\\') out << "\\\\";
        else if (ch == '\n') out << "\\n";
        else if (ch == '\r') out << "\\r";
        else if (ch == '\t') out << "\\t";
        else if (ch < 32) out << "\\u" << std::hex << std::setw(4) << std::setfill('0') << static_cast<UINT>(ch) << std::dec;
        else out << static_cast<char>(ch);
    }
    out << '"';
    return out.str();
}

std::string JsonNumber(double value) {
    if (!std::isfinite(value)) return "null";
    std::ostringstream out;
    out << std::setprecision(12) << value;
    return out.str();
}

const char* Bool(bool value) { return value ? "true" : "false"; }

void JsonStrings(std::ostream& out, const std::vector<std::string>& values) {
    out << '[';
    for (size_t i = 0; i < values.size(); ++i) {
        if (i) out << ',';
        out << JsonString(values[i]);
    }
    out << ']';
}

std::ofstream OpenResult(const std::filesystem::path& directory, const char* name) {
    const auto path = directory / name;
    if (std::filesystem::exists(path)) throw std::runtime_error("refusing to overwrite result file: " + Narrow(path.wstring()));
    std::ofstream out;
    out.exceptions(std::ios::failbit | std::ios::badbit);
    out.open(path, std::ios::binary | std::ios::out);
    return out;
}

std::filesystem::path ReserveOutput(const Options& options) {
    auto output = options.output;
    if (output.empty()) output = std::filesystem::current_path() /
        (L"Pavise-D3D12Gpu-Results-" + Stamp() + L"-" + std::to_wstring(GetCurrentProcessId()));
    output = std::filesystem::absolute(output).lexically_normal();
    if (std::filesystem::exists(output)) {
        if (!std::filesystem::is_directory(output) || !std::filesystem::is_empty(output))
            throw std::runtime_error("result directory must be empty; refusing overwrite: " + Narrow(output.wstring()));
    } else {
        std::filesystem::create_directories(output);
    }
    return output;
}

void WriteValidation(const std::filesystem::path& directory, const ValidationReport& report, const Options& options) {
    auto out = OpenResult(directory, "validation.json");
    out << "{\n  \"schema_version\":2,\n  \"status\":"
        << JsonString(report.attempted ? (report.passed ? "PASS" : "FAIL") : "NOT_RUN")
        << ",\n  \"passed\":" << Bool(report.passed)
        << ",\n  \"transfer_path\":" << JsonString(PathName(options.transferPath))
        << ",\n  \"secondary_input\":" << JsonString(SecondaryInputName(options.secondaryInput))
        << ",\n  \"multi_source_readback\":" << JsonString(options.secondaryInput == SecondaryInputMode::Local
                ? "secondary_local_input" : "cross_adapter_shared_buffer")
        << ",\n  \"width\":" << options.width << ",\n  \"height\":" << options.height
        << ",\n  \"scene_loops\":" << options.sceneLoops << ",\n  \"post_loops\":" << options.postLoops
        << ",\n  \"tolerance_8bit\":" << kValidationTolerance
        << ",\n  \"source_comparison\":\"full_image_exact\""
        << ",\n  \"output_comparison\":\"full_image_rgb_tolerance_alpha_255\""
        << ",\n  \"reference_kind\":\"sampled_integer_channel_average_from_primary_source_readback\""
        << ",\n  \"fixed_seeds\":[0,41,193],\n  \"timed\":false,\n  \"seeds\":[";
    for (size_t i = 0; i < report.seeds.size(); ++i) {
        const auto& seed = report.seeds[i];
        if (i) out << ',';
        out << "\n    {\"seed\":" << seed.seed << ",\"passed\":" << Bool(seed.passed)
            << ",\"expected_pixels\":" << seed.expectedPixels
            << ",\"source_pixels_single\":" << seed.sourcePixelsSingle
            << ",\"source_pixels_multi\":" << seed.sourcePixelsMulti
            << ",\"output_pixels_single\":" << seed.outputPixelsSingle
            << ",\"output_pixels_multi\":" << seed.outputPixelsMulti
            << ",\"source_mismatch_pixels\":" << seed.sourceMismatchPixels
            << ",\"output_mismatch_pixels\":" << seed.outputMismatchPixels
            << ",\"output_max_rgb_error\":" << seed.outputMaxRgbError
            << ",\"source_alpha_errors\":" << seed.sourceAlphaErrors
            << ",\"single_alpha_errors\":" << seed.singleAlphaErrors
            << ",\"multi_alpha_errors\":" << seed.multiAlphaErrors
            << ",\"reference_samples\":" << seed.referenceSamples
            << ",\"reference_mismatch_single\":" << seed.referenceMismatchSingle
            << ",\"reference_mismatch_multi\":" << seed.referenceMismatchMulti
            << ",\"reference_max_rgb_error_single\":" << seed.referenceMaxRgbErrorSingle
            << ",\"reference_max_rgb_error_multi\":" << seed.referenceMaxRgbErrorMulti
            << ",\"source_hash_single\":" << JsonString(Hex64(seed.sourceHashSingle))
            << ",\"source_hash_multi\":" << JsonString(Hex64(seed.sourceHashMulti))
            << ",\"output_hash_single\":" << JsonString(Hex64(seed.outputHashSingle))
            << ",\"output_hash_multi\":" << JsonString(Hex64(seed.outputHashMulti))
            << ",\"stale_source\":" << Bool(seed.staleSource)
            << ",\"stale_output\":" << Bool(seed.staleOutput) << ",\"reasons\":";
        JsonStrings(out, seed.reasons);
        out << '}';
    }
    out << "\n  ],\n  \"reasons\":";
    JsonStrings(out, report.reasons);
    out << "\n}\n";
    out.close();
}

void JsonModeMetrics(std::ostream& out, const ModeMetrics& metrics) {
    out << "{\"avg_completion_rate\":" << JsonNumber(metrics.avg)
        << ",\"wall_completion_rate\":" << JsonNumber(metrics.wall)
        << ",\"one_percent_low\":" << JsonNumber(metrics.low)
        << ",\"p99_ms\":" << JsonNumber(metrics.p99) << '}';
}

void WriteResults(const std::filesystem::path& directory, const std::vector<Phase>& phases,
                  const Adapter& primary, const Adapter& secondary, const Options& options,
                  const ValidationReport& validation, const Evaluation& evaluation,
                  double preconditionActual) {
    WriteValidation(directory, validation, options);
    auto csv = OpenResult(directory, "frames.csv");
    csv << "phase,mode,frame,completion_interval_ms,scene_gpu_ms,post_gpu_ms,transfer_path,end_tick,timestamp_frequency_hz,secondary_input\n"
        << std::fixed << std::setprecision(9);
    for (const auto& phase : phases) for (const auto& frame : phase.frames) {
        csv << phase.index << ',' << ModeName(phase.heterogeneous) << ',' << frame.index << ',';
        if (frame.index && std::isfinite(frame.intervalMs)) csv << frame.intervalMs;
        csv << ',' << frame.sceneMs << ',' << frame.postMs << ',' << PathName(options.transferPath)
            << ',' << frame.endTick << ',' << phase.timestampFrequency
            << ',' << SecondaryInputName(options.secondaryInput) << '\n';
    }
    csv.close();
    auto phaseCsv = OpenResult(directory, "phases.csv");
    phaseCsv << "phase,mode,transfer_path,frame_count,interval_count,wall_seconds,gpu_span_ms,wall_completion_rate,avg_completion_rate,one_percent_low,p99_ms,scene_gpu_ms,post_gpu_ms,gpu_wall_gap_pct,valid,timestamp_frequency_hz,secondary_input\n"
             << std::fixed << std::setprecision(9);
    for (const auto& phase : phases) {
        phaseCsv << phase.index << ',' << ModeName(phase.heterogeneous) << ',' << PathName(options.transferPath)
                 << ',' << phase.frameCount << ',' << phase.intervalCount << ',' << phase.wallSeconds
                 << ',' << phase.gpuSpanMs << ',' << phase.wallFps << ',' << phase.avgFps
                 << ',' << phase.lowFps << ',' << phase.p99Ms << ',' << phase.sceneMs << ',' << phase.postMs
                 << ',' << phase.gpuWallGapPct << ',' << Bool(phase.valid) << ',' << phase.timestampFrequency
                 << ',' << SecondaryInputName(options.secondaryInput) << '\n';
    }
    phaseCsv.close();

    auto summary = OpenResult(directory, "summary.json");
    summary << "{\n  \"schema_version\":2,\n  \"mode\":"
            << JsonString(options.action == Action::ValidateOnly ? "validate-only" : "run")
            << ",\n  \"verdict\":" << (evaluation.scored ? JsonString(evaluation.verdict) : "null")
            << ",\n  \"scope\":" << JsonString(kScope) << ",\n  \"product_ready\":false"
            << ",\n  \"rate_unit\":\"completed_offscreen_tasks_per_second\""
            << ",\n  \"transfer_path\":" << JsonString(PathName(options.transferPath))
            << ",\n  \"secondary_input\":" << JsonString(SecondaryInputName(options.secondaryInput))
            << ",\n  \"validation_passed\":" << Bool(validation.passed)
            << ",\n  \"requested_phases\":" << (options.action == Action::ValidateOnly ? 0 : options.repeats * 4)
            << ",\n  \"completed_phases\":" << phases.size()
            << ",\n  \"config\":{\"width\":" << options.width << ",\"height\":" << options.height
            << ",\"scene_loops\":" << options.sceneLoops << ",\"post_loops\":" << options.postLoops
            << ",\"warmup_frames\":" << options.warmup << ",\"measure_frames\":" << options.frames
            << ",\"repeats\":" << options.repeats
            << ",\"secondary_input\":" << JsonString(SecondaryInputName(options.secondaryInput))
            << ",\"precondition_seconds\":" << options.preconditionSeconds
            << ",\"precondition_actual_seconds\":" << JsonNumber(preconditionActual) << '}'
            << ",\n  \"hardware\":{\"primary\":{\"index\":" << primary.index
            << ",\"name\":" << JsonString(Narrow(primary.desc.Description))
            << ",\"luid\":" << JsonString(AdapterLuid(primary)) << ",\"vendor_id\":" << primary.desc.VendorId
            << ",\"device_id\":" << primary.desc.DeviceId << "},\"secondary\":{\"index\":" << secondary.index
            << ",\"name\":" << JsonString(Narrow(secondary.desc.Description))
            << ",\"luid\":" << JsonString(AdapterLuid(secondary)) << ",\"vendor_id\":" << secondary.desc.VendorId
            << ",\"device_id\":" << secondary.desc.DeviceId << "}}"
            << ",\n  \"metrics\":{\"single\":";
    JsonModeMetrics(summary, evaluation.single);
    summary << ",\"multi\":";
    JsonModeMetrics(summary, evaluation.multi);
    summary << ",\"delta_avg_pct\":" << JsonNumber(evaluation.deltaAvg)
            << ",\"delta_wall_pct\":" << JsonNumber(evaluation.deltaWall)
            << ",\"delta_low_pct\":" << JsonNumber(evaluation.deltaLow)
            << ",\"p99_change_pct\":" << JsonNumber(evaluation.p99Change)
            << ",\"pair_wins\":" << evaluation.pairWins << ",\"pair_count\":" << evaluation.pairCount
            << ",\"pair_win_ratio\":" << JsonNumber(evaluation.pairWinRatio)
            << ",\"baseline_drift_pct\":" << JsonNumber(evaluation.baselineDrift)
            << ",\"max_gpu_wall_gap_pct\":" << JsonNumber(evaluation.maximumGpuWallGap) << '}'
            << ",\n  \"gates\":{\"distinct_adapters\":" << Bool(evaluation.identity)
            << ",\"correctness_preflight\":" << Bool(evaluation.validation)
            << ",\"complete_protocol\":" << Bool(evaluation.complete)
            << ",\"phase_measurements_valid\":" << Bool(evaluation.measurements)
            << ",\"protocol_order_valid\":" << Bool(evaluation.order)
            << ",\"gpu_wall_consistent\":" << Bool(evaluation.clock)
            << ",\"baseline_drift_within_5_pct\":" << Bool(evaluation.drift)
            << ",\"average_gain_at_least_2_pct\":" << Bool(evaluation.averageGain)
            << ",\"wall_gain_at_least_2_pct\":" << Bool(evaluation.wallGain)
            << ",\"low_gain_at_least_1_pct\":" << Bool(evaluation.lowGain)
            << ",\"p99_regression_at_most_5_pct\":" << Bool(evaluation.p99Gate)
            << ",\"pair_wins_at_least_two_thirds\":" << Bool(evaluation.pairs) << '}'
            << ",\n  \"thresholds\":{\"average_gain_pct\":2,\"wall_gain_pct\":2,\"low_gain_pct\":1,"
               "\"p99_max_regression_pct\":5,\"baseline_max_range_pct\":5,\"gpu_wall_max_gap_pct\":5,"
               "\"pair_win_fraction\":0.6666666666666666,\"rgb_tolerance_8bit\":1}"
            << ",\n  \"reasons\":";
    JsonStrings(summary, evaluation.reasons);
    summary << ",\n  \"phase_errors\":[";
    bool firstError = true;
    for (const auto& phase : phases) if (!phase.reasons.empty()) {
        if (!firstError) summary << ',';
        firstError = false;
        summary << "{\"phase\":" << phase.index << ",\"reasons\":";
        JsonStrings(summary, phase.reasons);
        summary << '}';
    }
    summary << "],\n  \"limitations\":["
               "\"No game capture, injection, frame generation, output return transfer, or presentation.\","
               "\"Rates describe synthetic owned compute completions, not game FPS or end-to-end display latency.\","
               "\"No GPU clock calibration; GPU/wall mismatch is rejected rather than corrected.\","
               "\"Single-GPU baseline uses one compute queue; an optimized asynchronous single-GPU baseline remains untested.\","
               "\"CPU reference uses fixed sampled pixels; paired GPU source/output comparisons cover the full image.\","
               "\"No statistical confidence interval or thermal/idle telemetry; short phases have few tail samples.\","
               "\"CANDIDATE is not authorization or evidence to ship a universal game optimization.\"]\n}\n";
    summary.close();

    auto report = OpenResult(directory, "report.txt");
    report << "Pavise D3D12 Heterogeneous Compute Bench / protocol 2\n"
           << "Scope: " << kScope << " (NOT game FPS; NOT product-ready)\n"
           << "Mode: " << (options.action == Action::ValidateOnly ? "validate-only" : "run")
           << "\nVerdict: " << (evaluation.scored ? evaluation.verdict : "VALIDATION_ONLY_PASS")
           << "\nCorrectness preflight: " << (validation.passed ? "PASS" : "FAIL/NOT_RUN")
           << "\nPrimary: " << Narrow(primary.desc.Description) << " LUID=" << AdapterLuid(primary)
           << "\nSecondary: " << Narrow(secondary.desc.Description) << " LUID=" << AdapterLuid(secondary)
           << "\nResolution: " << options.width << 'x' << options.height
           << " loops=" << options.sceneLoops << '/' << options.postLoops
           << " transfer=" << PathName(options.transferPath)
           << " secondaryInput=" << SecondaryInputName(options.secondaryInput)
           << "\nPrecondition seconds (requested/actual): " << options.preconditionSeconds << '/' << preconditionActual
           << "\nRates below are completed offscreen tasks/second; durations are milliseconds.\n\n"
           << std::fixed << std::setprecision(6);
    for (const auto& phase : phases) {
        report << "Phase " << phase.index << ' ' << ModeName(phase.heterogeneous)
               << " avgRate=" << phase.avgFps << " wallRate=" << phase.wallFps
               << " slowest1pctRate=" << phase.lowFps << " p99Ms=" << phase.p99Ms
               << " sceneGpuMs=" << phase.sceneMs << " postGpuMs=" << phase.postMs
               << " gpuWallGapPct=" << phase.gpuWallGapPct << " valid=" << Bool(phase.valid) << '\n';
        for (const auto& reason : phase.reasons) report << "  - " << reason << '\n';
    }
    report << "\nMedian completion-rate change: " << evaluation.deltaAvg << "%"
           << "\nMedian wall-rate change: " << evaluation.deltaWall << "%"
           << "\nMedian slowest-1%-rate change: " << evaluation.deltaLow << "%"
           << "\nMedian p99 change: " << evaluation.p99Change << "%"
           << "\nPaired wins: " << evaluation.pairWins << '/' << evaluation.pairCount
           << "\nSingle baseline max/min drift: " << evaluation.baselineDrift << "%\n";
    for (const auto& reason : evaluation.reasons) report << "- " << reason << '\n';
    report << "\nNo capture/display cost, no per-frame end-to-end latency, no optimized async single-GPU baseline.\n"
              "When secondaryInput=local, postGpuMs includes shared-to-local input copying and all associated barriers.\n"
              "Short phases cannot establish statistical confidence; a candidate requires independent real-workload confirmation.\n"
              "Pixel validation and CPU reference work are outside the measured phases. No system settings are changed.\n";
    report.close();
}

std::vector<Phase> SyntheticPhases(const Options& options) {
    std::vector<Phase> phases;
    const std::array<bool, 4> abba = {false, true, true, false};
    const std::array<bool, 4> baab = {true, false, false, true};
    for (UINT repeat = 0; repeat < options.repeats; ++repeat) {
        const auto& block = repeat % 2 ? baab : abba;
        for (bool multi : block) {
            Phase phase;
            phase.index = static_cast<UINT>(phases.size() + 1);
            phase.heterogeneous = multi;
            phase.frameCount = options.frames;
            phase.intervalCount = options.frames - 1;
            phase.avgFps = multi ? 105.0 : 100.0;
            phase.wallFps = multi ? 104.0 : 99.0;
            phase.lowFps = multi ? 75.0 : 70.0;
            phase.p99Ms = multi ? 14.0 : 15.0;
            phase.gpuWallGapPct = std::abs(ChangePct(phase.avgFps, phase.wallFps));
            phase.valid = true;
            phases.push_back(phase);
        }
    }
    return phases;
}

int RunSelfTests() {
    UINT passed = 0, failed = 0;
    auto test = [&](bool condition, const char* name) {
        std::cout << (condition ? "PASS " : "FAIL ") << name << '\n';
        condition ? ++passed : ++failed;
    };
    auto parseTestOptions = [](std::initializer_list<const wchar_t*> arguments) {
        std::vector<std::wstring> owned;
        for (const wchar_t* argument : arguments) owned.emplace_back(argument);
        std::vector<wchar_t*> pointers;
        for (auto& argument : owned) pointers.push_back(argument.data());
        return Args(static_cast<int>(pointers.size()), pointers.data());
    };
    test(Options{}.secondaryInput == SecondaryInputMode::Shared, "secondary_input_defaults_to_shared");
    test(parseTestOptions({L"bench"}).secondaryInput == SecondaryInputMode::Shared,
         "old_cli_omission_preserves_shared_input");
    bool orthogonal = true;
    for (UINT primaryPath = 0; primaryPath < 2; ++primaryPath) {
        for (UINT secondaryPath = 0; secondaryPath < 2; ++secondaryPath) {
            const Options parsed = parseTestOptions({L"bench", L"--transfer-path", primaryPath ? L"copy" : L"direct",
                                                     L"--secondary-input", secondaryPath ? L"local" : L"shared"});
            orthogonal &= parsed.transferPath == (primaryPath ? TransferPath::Copy : TransferPath::Direct) &&
                          parsed.secondaryInput == (secondaryPath ? SecondaryInputMode::Local : SecondaryInputMode::Shared);
        }
    }
    test(orthogonal, "primary_transfer_and_secondary_input_are_orthogonal");
    bool badSecondaryRejected = false, missingSecondaryRejected = false;
    try { parseTestOptions({L"bench", L"--secondary-input", L"invalid"}); }
    catch (const std::exception&) { badSecondaryRejected = true; }
    try { parseTestOptions({L"bench", L"--secondary-input"}); }
    catch (const std::exception&) { missingSecondaryRejected = true; }
    test(badSecondaryRejected, "unknown_secondary_input_rejected");
    test(missingSecondaryRejected, "missing_secondary_input_rejected");
    test(Median({2, 4, 10, 20}) == 7.0, "even_median_averages_middle_values");
    test(Median({1, 3, 9}) == 3.0, "odd_median");
    test(std::abs(Percentile({1, 2, 3, 4}, .5) - 2.5) < 1e-9, "interpolated_percentile");
    test(JsonString("a\"\\\n") == "\"a\\\"\\\\\\n\"", "json_string_escaping");
    test(JsonNumber(std::numeric_limits<double>::quiet_NaN()) == "null", "json_rejects_nonfinite_numbers");
    Options options;
    auto phases = SyntheticPhases(options);
    test(Evaluate(phases, options, true, true).verdict == "CANDIDATE", "candidate_requires_all_gates");
    test(Evaluate(phases, options, false, true).verdict == "INVALID", "correctness_gate");
    test(Evaluate(phases, options, true, false).verdict == "INVALID", "distinct_adapter_gate");
    auto incomplete = phases;
    incomplete.pop_back();
    test(Evaluate(incomplete, options, true, true).verdict == "INVALID", "incomplete_protocol_gate");
    auto corrupt = phases;
    corrupt[1].gpuWallGapPct = 5.01;
    test(Evaluate(corrupt, options, true, true).verdict == "INVALID", "wall_clock_consistency_gate");
    corrupt = phases;
    corrupt[0].avgFps = 110.0;
    test(Evaluate(corrupt, options, true, true).verdict == "INVALID", "baseline_drift_gate");
    corrupt = phases;
    corrupt[0].heterogeneous = true;
    test(Evaluate(corrupt, options, true, true).verdict == "INVALID", "mirror_protocol_order_gate");
    corrupt = phases;
    for (auto& phase : corrupt) if (phase.heterogeneous) phase.lowFps = 69.0;
    test(Evaluate(corrupt, options, true, true).verdict == "NO_BENEFIT", "tail_regression_rejects_candidate");
    corrupt = phases;
    for (auto& phase : corrupt) if (phase.heterogeneous) phase.p99Ms = 16.0;
    test(Evaluate(corrupt, options, true, true).verdict == "NO_BENEFIT", "p99_regression_gate");
    corrupt = phases;
    for (auto& phase : corrupt) if (phase.heterogeneous) { phase.avgFps = 99.0; phase.wallFps = 98.0; }
    test(!Evaluate(corrupt, options, true, true).pairs, "two_thirds_paired_win_gate");
    corrupt = phases;
    for (auto& phase : corrupt) if (phase.heterogeneous) phase.wallFps = 100.0;
    test(!Evaluate(corrupt, options, true, true).wallGain, "wall_gain_required_independently");
    Phase sample;
    sample.wallSeconds = .003;
    for (UINT i = 0; i < 3; ++i) { Frame frame; frame.index = i; frame.sceneMs = .6; frame.postMs = .4;
        frame.endTick = (i + 1) * 1000; sample.frames.push_back(frame); }
    test(SummarizePhase(&sample, 1000000) && std::abs(sample.avgFps - 1000.0) < 1e-9,
         "same_clock_completion_statistics");
    sample.frames[2].endTick = sample.frames[1].endTick;
    test(!SummarizePhase(&sample, 1000000), "rejects_nonmonotonic_completion_ticks");
    test(!SummarizePhase(&sample, 0), "rejects_zero_frequency");
    const std::vector<UINT> source = {0xff0a0a0a, 0xff141414, 0xff1e1e1e, 0xff282828};
    test(ReferencePost(source, 2, 2, 1, 0) == 0xff101010, "cpu_reference_clamped_edges");
    test(RgbError(0xff000102, 0xff010203) == 1 && RgbError(0xff000102, 0xff020304) == 2,
         "fixed_rgb_tolerance");
    Options small;
    small.width = small.height = 2;
    small.postLoops = 1;
    std::vector<UINT> expected;
    for (size_t i = 0; i < source.size(); ++i) expected.push_back(ReferencePost(source, 2, 2, 1, i));
    ValidationSeed correct;
    ComparePixels(&correct, small, source, expected, source, expected, nullptr);
    test(correct.passed, "full_pixel_comparison_and_reference_pass");
    ValidationSeed stale;
    ComparePixels(&stale, small, source, expected, source, expected, &correct);
    test(!stale.passed && stale.staleSource, "stale_seed_images_are_rejected");
    auto badAlpha = expected;
    badAlpha[0] &= 0x00ffffffu;
    ValidationSeed alpha;
    ComparePixels(&alpha, small, source, expected, source, badAlpha, nullptr);
    test(!alpha.passed && alpha.multiAlphaErrors == 1, "unwritten_or_bad_alpha_rejected");
    ValidationSeed dimensions;
    ComparePixels(&dimensions, small, source, expected, {}, expected, nullptr);
    test(!dimensions.passed, "dimension_mismatch_rejected");
    test((7680 + 15) / 16 <= 65535 && (4320 + 15) / 16 <= 65535, "8k_2d_dispatch_within_limits");
    std::cout << "Self-test: " << passed << " PASS / " << failed
              << " FAIL (pure CPU; no D3D device, GPU work, or result files)\n";
    return failed ? 1 : 0;
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
    Options options;
    Adapter primary, secondary;
    bool identityKnown = false, outputReserved = false, archiveStarted = false;
    std::filesystem::path output;
    std::vector<Phase> phases;
    ValidationReport validation;
    double preconditionActual = 0.0;
    try {
        options = Args(argc, argv);
        if (options.action == Action::SelfTest) return RunSelfTests();
        if (options.action == Action::Plan) {
            std::cout << "Safe plan mode: no D3D device, queue, shader, or window was created.\n"
                         "--self-test: pure CPU statistics/correctness-gate tests.\n"
                         "--list-adapters: enumerate hardware adapters without GPU workloads.\n"
                         "--validate-only: create devices and run pixel preflight, no performance phases.\n"
                         "--run: pixel preflight, optional balanced precondition, then ABBA/BAAB compute-only test.\n"
                         "Options: --primary-index N --secondary-index N --width N --height N\n"
                         "         --scene-loops N --post-loops N --warmup-frames N --measure-frames N\n"
                         "         --repeats N --transfer-path direct|copy --precondition-seconds N --output DIR\n"
                         "         --secondary-input shared|local (default shared; independent of primary transfer path)\n"
                         "The output directory must be empty. CANDIDATE never means game-FPS improvement or product-ready.\n";
            return 0;
        }
        if (options.action == Action::ListAdapters) {
            for (const auto& adapter : Adapters())
                std::cout << '[' << adapter.index << "] " << Narrow(adapter.desc.Description)
                          << " LUID=" << AdapterLuid(adapter) << " vendor=" << adapter.desc.VendorId
                          << " device=" << adapter.desc.DeviceId
                          << " dedicatedMiB=" << adapter.desc.DedicatedVideoMemory / (1024 * 1024)
                          << " sharedMiB=" << adapter.desc.SharedSystemMemory / (1024 * 1024) << '\n';
            return 0;
        }
        output = ReserveOutput(options);
        outputReserved = true;
        const auto all = Adapters();
        primary = Find(all, options.primaryIndex);
        secondary = Find(all, options.secondaryIndex);
        identityKnown = true;
        if (SameAdapter(primary, secondary)) throw std::runtime_error("same hardware LUID selected twice");
        std::cout << "primary=" << Narrow(primary.desc.Description) << " secondary="
                  << Narrow(secondary.desc.Description) << " transfer=" << PathName(options.transferPath)
                  << " secondary_input=" << SecondaryInputName(options.secondaryInput)
                  << " (owned offscreen compute only; no capture/display/injection)\n";
        Runner runner(primary, secondary, options);
        std::cout << "correctness_preflight=running (outside performance timing)\n" << std::flush;
        runner.Validate(&validation);
        std::cout << "correctness_preflight=PASS\n" << std::flush;
        if (options.action == Action::ValidateOnly) {
            Evaluation evaluation;
            evaluation.scored = false;
            evaluation.identity = true;
            evaluation.validation = true;
            archiveStarted = true;
            WriteResults(output, phases, primary, secondary, options, validation, evaluation, 0.0);
            std::cout << "validation_only=PASS results=" << Narrow(output.wstring()) << '\n';
            return 0;
        }
        if (options.preconditionSeconds) {
            std::cout << "balanced_precondition_seconds=" << options.preconditionSeconds << '\n' << std::flush;
            preconditionActual = runner.Precondition(options.preconditionSeconds);
        }
        const std::array<bool, 4> abba = {false, true, true, false};
        const std::array<bool, 4> baab = {true, false, false, true};
        for (UINT repeat = 0; repeat < options.repeats; ++repeat) {
            const auto& block = repeat % 2 ? baab : abba;
            for (bool mode : block) {
                const UINT index = static_cast<UINT>(phases.size() + 1);
                std::cout << "phase " << index << '/' << (options.repeats * 4) << ' '
                          << ModeName(mode) << "..." << std::flush;
                phases.push_back(runner.RunPhase(index, mode, options.warmup, options.frames));
                std::cout << std::fixed << std::setprecision(3) << phases.back().avgFps
                          << " compute completions/s wall=" << phases.back().wallFps << '\n';
                if (!phases.back().valid)
                    throw std::runtime_error("invalid measured phase " + std::to_string(index) + "; stopping additional GPU work");
            }
        }
        const Evaluation evaluation = Evaluate(phases, options, validation.passed, true);
        archiveStarted = true;
        WriteResults(output, phases, primary, secondary, options, validation, evaluation, preconditionActual);
        std::cout << "verdict=" << evaluation.verdict << " scope=" << kScope
                  << " product_ready=false results=" << Narrow(output.wstring()) << '\n';
        return evaluation.verdict == "INVALID" ? 2 : 0;
    } catch (const std::exception& e) {
        std::cerr << "error: " << e.what() << "\n";
        if (outputReserved && !archiveStarted) {
            try {
                Evaluation evaluation = Evaluate(phases, options, validation.passed,
                                                 identityKnown && !SameAdapter(primary, secondary));
                evaluation.verdict = "INVALID";
                evaluation.reasons.push_back(std::string("runtime_failure: ") + e.what());
                archiveStarted = true;
                WriteResults(output, phases, primary, secondary, options, validation, evaluation, preconditionActual);
                std::cerr << "invalid_results=" << Narrow(output.wstring()) << '\n';
            } catch (const std::exception& writeError) {
                std::cerr << "result_archive_failed: " << writeError.what() << '\n';
            }
        }
        return 2;
    }
}
