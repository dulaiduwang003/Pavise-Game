// Pavise Heterogeneous GPU Bench
// Explicit multi-adapter feasibility harness. No injection, driver changes, or game access.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11_1.h>
#include <dxgi1_6.h>
#include <d3dcompiler.h>
#include <wrl/client.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstdint>
#include <cstring>
#include <cwchar>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <mutex>
#include <numeric>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

#ifdef _MSC_VER
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "d3dcompiler.lib")
#pragma comment(lib, "ole32.lib")
#endif

using Microsoft::WRL::ComPtr;

namespace {

constexpr UINT kPipelineSlots = 3;
constexpr DWORD kQueryTimeoutMs = 30000;
constexpr DWORD kMutexTimeoutMs = 30000;

struct HrError : std::runtime_error {
    HRESULT hr;
    HrError(HRESULT value, const std::string& operation)
        : std::runtime_error(operation + " failed, HRESULT=0x" + [&] {
              std::ostringstream text;
              text << std::hex << std::uppercase << static_cast<unsigned long>(value);
              return text.str();
          }()), hr(value) {}
};

void Check(HRESULT hr, const char* operation) {
    if (FAILED(hr)) throw HrError(hr, operation);
}

std::string Narrow(const std::wstring& value) {
    if (value.empty()) return {};
    int count = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()),
                                    nullptr, 0, nullptr, nullptr);
    std::string result(static_cast<size_t>(count), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()),
                        result.data(), count, nullptr, nullptr);
    return result;
}

std::string JsonEscape(const std::string& value) {
    std::ostringstream out;
    for (unsigned char ch : value) {
        switch (ch) {
            case '\\': out << "\\\\"; break;
            case '"': out << "\\\""; break;
            case '\n': out << "\\n"; break;
            case '\r': out << "\\r"; break;
            case '\t': out << "\\t"; break;
            default:
                if (ch < 0x20) out << "\\u" << std::hex << std::setw(4) << std::setfill('0') << int(ch);
                else out << ch;
        }
    }
    return out.str();
}

std::wstring TimestampName() {
    SYSTEMTIME value = {};
    GetLocalTime(&value);
    wchar_t text[64] = {};
    swprintf_s(text, L"%04u%02u%02u-%02u%02u%02u", value.wYear, value.wMonth, value.wDay,
               value.wHour, value.wMinute, value.wSecond);
    return text;
}

double Percentile(std::vector<double> values, double p) {
    if (values.empty()) return 0.0;
    std::sort(values.begin(), values.end());
    const double position = std::clamp(p, 0.0, 1.0) * static_cast<double>(values.size() - 1);
    const size_t low = static_cast<size_t>(std::floor(position));
    const size_t high = static_cast<size_t>(std::ceil(position));
    const double fraction = position - static_cast<double>(low);
    return values[low] * (1.0 - fraction) + values[high] * fraction;
}

double Mean(const std::vector<double>& values) {
    return values.empty() ? 0.0 : std::accumulate(values.begin(), values.end(), 0.0) /
                                      static_cast<double>(values.size());
}

double Median(const std::vector<double>& values) { return Percentile(values, 0.5); }

double OnePercentLowFps(std::vector<double> frameMs) {
    if (frameMs.empty()) return 0.0;
    std::sort(frameMs.begin(), frameMs.end(), std::greater<double>());
    const size_t count = std::max<size_t>(1, static_cast<size_t>(std::ceil(frameMs.size() * 0.01)));
    const double slowMean = std::accumulate(frameMs.begin(), frameMs.begin() + count, 0.0) /
                            static_cast<double>(count);
    return slowMean > 0.0 ? 1000.0 / slowMean : 0.0;
}

enum class Action { Plan, ListAdapters, Probe, SelfTest, Run };
enum class BenchMode { Single, Heterogeneous };

const char* ModeName(BenchMode mode) {
    return mode == BenchMode::Single ? "single" : "heterogeneous";
}

struct Options {
    Action action = Action::Plan;
    UINT width = 1920;
    UINT height = 1080;
    UINT warmupFrames = 120;
    UINT measureFrames = 600;
    UINT repeats = 3;
    UINT sceneLoops = 12;
    UINT postLoops = 8;
    int primaryIndex = -1;
    int secondaryIndex = -1;
    std::filesystem::path outputDirectory;
};

UINT ParseUInt(const wchar_t* value, const wchar_t* name, UINT minimum, UINT maximum) {
    wchar_t* end = nullptr;
    unsigned long parsed = wcstoul(value, &end, 10);
    if (!value[0] || !end || *end || parsed < minimum || parsed > maximum) {
        throw std::runtime_error("invalid numeric argument: " + Narrow(name));
    }
    return static_cast<UINT>(parsed);
}

int ParseInt(const wchar_t* value, const wchar_t* name, int minimum, int maximum) {
    wchar_t* end = nullptr;
    long parsed = wcstol(value, &end, 10);
    if (!value[0] || !end || *end || parsed < minimum || parsed > maximum) {
        throw std::runtime_error("invalid numeric argument: " + Narrow(name));
    }
    return static_cast<int>(parsed);
}

Options ParseArgs(int argc, wchar_t** argv) {
    Options options;
    for (int i = 1; i < argc; ++i) {
        const std::wstring arg = argv[i];
        auto require = [&](const wchar_t* name) -> const wchar_t* {
            if (++i >= argc) throw std::runtime_error("missing value for " + Narrow(name));
            return argv[i];
        };
        if (arg == L"--run") options.action = Action::Run;
        else if (arg == L"--probe") options.action = Action::Probe;
        else if (arg == L"--self-test") options.action = Action::SelfTest;
        else if (arg == L"--list-adapters") options.action = Action::ListAdapters;
        else if (arg == L"--width") options.width = ParseUInt(require(L"--width"), L"--width", 320, 7680);
        else if (arg == L"--height") options.height = ParseUInt(require(L"--height"), L"--height", 240, 4320);
        else if (arg == L"--warmup-frames") options.warmupFrames = ParseUInt(require(L"--warmup-frames"), L"--warmup-frames", 0, 100000);
        else if (arg == L"--measure-frames") options.measureFrames = ParseUInt(require(L"--measure-frames"), L"--measure-frames", 60, 1000000);
        else if (arg == L"--repeats") options.repeats = ParseUInt(require(L"--repeats"), L"--repeats", 1, 20);
        else if (arg == L"--scene-loops") options.sceneLoops = ParseUInt(require(L"--scene-loops"), L"--scene-loops", 1, 1000);
        else if (arg == L"--post-loops") options.postLoops = ParseUInt(require(L"--post-loops"), L"--post-loops", 1, 1000);
        else if (arg == L"--primary-index") options.primaryIndex = ParseInt(require(L"--primary-index"), L"--primary-index", 0, 64);
        else if (arg == L"--secondary-index") options.secondaryIndex = ParseInt(require(L"--secondary-index"), L"--secondary-index", 0, 64);
        else if (arg == L"--output") options.outputDirectory = require(L"--output");
        else if (arg == L"--help" || arg == L"-h" || arg == L"/?") options.action = Action::Plan;
        else throw std::runtime_error("unknown argument: " + Narrow(arg));
    }
    return options;
}

struct AdapterInfo {
    UINT index = 0;
    DXGI_ADAPTER_DESC1 desc = {};
    ComPtr<IDXGIAdapter1> adapter;
};

bool SameAdapter(const AdapterInfo& a, const AdapterInfo& b) {
    return a.desc.AdapterLuid.HighPart == b.desc.AdapterLuid.HighPart &&
           a.desc.AdapterLuid.LowPart == b.desc.AdapterLuid.LowPart;
}

bool SupportsD3D11(IDXGIAdapter1* adapter) {
    ComPtr<ID3D11Device> device;
    D3D_FEATURE_LEVEL level = D3D_FEATURE_LEVEL_11_0;
    return SUCCEEDED(D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0, &level, 1,
                                       D3D11_SDK_VERSION, &device, nullptr, nullptr));
}

std::vector<AdapterInfo> EnumerateAdapters() {
    ComPtr<IDXGIFactory1> factory;
    Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)), "CreateDXGIFactory1");
    std::vector<AdapterInfo> adapters;
    for (UINT index = 0;; ++index) {
        ComPtr<IDXGIAdapter1> adapter;
        HRESULT hr = factory->EnumAdapters1(index, &adapter);
        if (hr == DXGI_ERROR_NOT_FOUND) break;
        Check(hr, "EnumAdapters1");
        AdapterInfo info;
        info.index = index;
        info.adapter = adapter;
        Check(adapter->GetDesc1(&info.desc), "GetDesc1");
        if (!(info.desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE)) adapters.push_back(std::move(info));
    }
    return adapters;
}

std::string AdapterText(const AdapterInfo& info) {
    std::ostringstream out;
    out << "[" << info.index << "] " << Narrow(info.desc.Description)
        << " vendor=0x" << std::hex << std::uppercase << info.desc.VendorId
        << " device=0x" << info.desc.DeviceId << std::dec
        << " dedicated=" << (info.desc.DedicatedVideoMemory / (1024 * 1024)) << " MiB"
        << " shared=" << (info.desc.SharedSystemMemory / (1024 * 1024)) << " MiB"
        << " D3D11=" << (SupportsD3D11(info.adapter.Get()) ? "yes" : "no");
    return out.str();
}

std::pair<AdapterInfo, AdapterInfo> SelectAdapters(const std::vector<AdapterInfo>& adapters,
                                                   const Options& options) {
    if (adapters.size() < 2) throw std::runtime_error("fewer than two hardware adapters were found");
    auto byIndex = [&](int index) -> const AdapterInfo* {
        for (const auto& item : adapters) if (static_cast<int>(item.index) == index) return &item;
        return nullptr;
    };

    const AdapterInfo* primary = options.primaryIndex >= 0 ? byIndex(options.primaryIndex) : nullptr;
    if (options.primaryIndex >= 0 && !primary) throw std::runtime_error("primary adapter index was not found");
    if (!primary) {
        primary = &*std::max_element(adapters.begin(), adapters.end(), [](const auto& a, const auto& b) {
            return a.desc.DedicatedVideoMemory < b.desc.DedicatedVideoMemory;
        });
    }

    const AdapterInfo* secondary = options.secondaryIndex >= 0 ? byIndex(options.secondaryIndex) : nullptr;
    if (options.secondaryIndex >= 0 && !secondary) throw std::runtime_error("secondary adapter index was not found");
    if (!secondary) {
        for (const auto& item : adapters) {
            if (SameAdapter(*primary, item) || !SupportsD3D11(item.adapter.Get())) continue;
            if (!secondary || item.desc.DedicatedVideoMemory < secondary->desc.DedicatedVideoMemory) secondary = &item;
        }
    }
    if (!secondary || SameAdapter(*primary, *secondary)) {
        throw std::runtime_error("two distinct D3D11 hardware adapters could not be selected");
    }
    if (!SupportsD3D11(primary->adapter.Get()) || !SupportsD3D11(secondary->adapter.Get())) {
        throw std::runtime_error("a selected adapter does not support a D3D11 hardware device");
    }
    return {*primary, *secondary};
}

const char* kShaderSource = R"HLSL(
cbuffer Params : register(b0)
{
    float phase;
    float invWidth;
    float invHeight;
    uint loopCount;
};

struct VsOut
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

VsOut VSMain(uint id : SV_VertexID)
{
    VsOut result;
    float2 position = id == 0 ? float2(-1.0, -1.0) :
                      id == 1 ? float2(-1.0,  3.0) : float2(3.0, -1.0);
    result.position = float4(position, 0.0, 1.0);
    result.uv = float2(position.x * 0.5 + 0.5, 0.5 - position.y * 0.5);
    return result;
}

float4 PSScene(VsOut input) : SV_Target
{
    float2 p = input.uv * 2.0 - 1.0;
    float3 color = float3(input.uv, 0.35 + 0.25 * sin(phase));
    [loop]
    for (uint i = 0; i < loopCount; ++i)
    {
        float f = float(i + 1);
        float wave = sin(p.x * (7.0 + f * 0.17) + phase * 0.7 + f) *
                     cos(p.y * (9.0 + f * 0.13) - phase * 0.4);
        color = frac(color * 1.017 + float3(wave * 0.031, wave * 0.019, wave * 0.027) +
                     float3(0.0031, 0.0047, 0.0061) * f);
    }
    return float4(color, 1.0);
}

Texture2D sourceTexture : register(t0);
SamplerState linearSampler : register(s0);

float4 PSPost(VsOut input) : SV_Target
{
    float2 texel = float2(invWidth, invHeight);
    float3 accum = 0.0;
    float weight = 0.0;
    [loop]
    for (uint i = 0; i < loopCount; ++i)
    {
        float radius = float(i + 1);
        float2 offset = texel * radius * float2(1.0 + (i & 1), 1.0 + ((i >> 1) & 1));
        accum += sourceTexture.SampleLevel(linearSampler, input.uv + offset, 0).rgb;
        accum += sourceTexture.SampleLevel(linearSampler, input.uv - offset, 0).rgb;
        accum += sourceTexture.SampleLevel(linearSampler, input.uv + float2(offset.x, -offset.y), 0).rgb;
        accum += sourceTexture.SampleLevel(linearSampler, input.uv + float2(-offset.x, offset.y), 0).rgb;
        weight += 4.0;
    }
    float3 base = sourceTexture.SampleLevel(linearSampler, input.uv, 0).rgb;
    return float4(lerp(base, accum / max(weight, 1.0), 0.65), 1.0);
}
)HLSL";

ComPtr<ID3DBlob> CompileShader(const char* entry, const char* target) {
    UINT flags = D3DCOMPILE_ENABLE_STRICTNESS | D3DCOMPILE_OPTIMIZATION_LEVEL3;
    ComPtr<ID3DBlob> bytecode;
    ComPtr<ID3DBlob> errors;
    HRESULT hr = D3DCompile(kShaderSource, strlen(kShaderSource), "Pavise.HeterogeneousGpuBench.hlsl",
                            nullptr, nullptr, entry, target, flags, 0, &bytecode, &errors);
    if (FAILED(hr)) {
        std::string detail;
        if (errors) detail.assign(static_cast<const char*>(errors->GetBufferPointer()), errors->GetBufferSize());
        throw std::runtime_error(std::string("D3DCompile ") + entry + " failed: " + detail);
    }
    return bytecode;
}

struct DeviceBundle {
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11Device1> device1;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11VertexShader> vertexShader;
    ComPtr<ID3D11PixelShader> sceneShader;
    ComPtr<ID3D11PixelShader> postShader;
    ComPtr<ID3D11SamplerState> sampler;
};

DeviceBundle CreateDevice(const AdapterInfo& adapter, bool createShaders) {
    DeviceBundle bundle;
    const D3D_FEATURE_LEVEL requested[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
    D3D_FEATURE_LEVEL actual = D3D_FEATURE_LEVEL_11_0;
    HRESULT hr = D3D11CreateDevice(adapter.adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                                   D3D11_CREATE_DEVICE_BGRA_SUPPORT, requested, 2, D3D11_SDK_VERSION,
                                   &bundle.device, &actual, &bundle.context);
    if (hr == E_INVALIDARG) {
        hr = D3D11CreateDevice(adapter.adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                               D3D11_CREATE_DEVICE_BGRA_SUPPORT, requested + 1, 1, D3D11_SDK_VERSION,
                               &bundle.device, &actual, &bundle.context);
    }
    Check(hr, "D3D11CreateDevice");
    Check(bundle.device.As(&bundle.device1), "Query ID3D11Device1");
    if (!createShaders) return bundle;

    const auto vs = CompileShader("VSMain", "vs_5_0");
    const auto scene = CompileShader("PSScene", "ps_5_0");
    const auto post = CompileShader("PSPost", "ps_5_0");
    Check(bundle.device->CreateVertexShader(vs->GetBufferPointer(), vs->GetBufferSize(), nullptr,
                                            &bundle.vertexShader), "CreateVertexShader");
    Check(bundle.device->CreatePixelShader(scene->GetBufferPointer(), scene->GetBufferSize(), nullptr,
                                           &bundle.sceneShader), "Create scene pixel shader");
    Check(bundle.device->CreatePixelShader(post->GetBufferPointer(), post->GetBufferSize(), nullptr,
                                           &bundle.postShader), "Create post pixel shader");
    D3D11_SAMPLER_DESC sampler = {};
    sampler.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sampler.AddressU = sampler.AddressV = sampler.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sampler.MaxLOD = D3D11_FLOAT32_MAX;
    Check(bundle.device->CreateSamplerState(&sampler, &bundle.sampler), "CreateSamplerState");
    return bundle;
}

struct alignas(16) ShaderParams {
    float phase = 0.0f;
    float invWidth = 0.0f;
    float invHeight = 0.0f;
    UINT loopCount = 0;
};

struct QuerySet {
    ComPtr<ID3D11Query> disjoint;
    ComPtr<ID3D11Query> begin;
    ComPtr<ID3D11Query> middle;
    ComPtr<ID3D11Query> end;
};

QuerySet CreateQueries(ID3D11Device* device) {
    QuerySet result;
    D3D11_QUERY_DESC desc = {D3D11_QUERY_TIMESTAMP_DISJOINT, 0};
    Check(device->CreateQuery(&desc, &result.disjoint), "Create disjoint query");
    desc.Query = D3D11_QUERY_TIMESTAMP;
    Check(device->CreateQuery(&desc, &result.begin), "Create begin timestamp");
    Check(device->CreateQuery(&desc, &result.middle), "Create middle timestamp");
    Check(device->CreateQuery(&desc, &result.end), "Create end timestamp");
    return result;
}

ComPtr<ID3D11Buffer> CreateConstantBuffer(ID3D11Device* device) {
    D3D11_BUFFER_DESC desc = {};
    desc.ByteWidth = sizeof(ShaderParams);
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    ComPtr<ID3D11Buffer> result;
    Check(device->CreateBuffer(&desc, nullptr, &result), "Create constant buffer");
    return result;
}

struct Slot {
    ComPtr<ID3D11Texture2D> localScene;
    ComPtr<ID3D11RenderTargetView> localSceneRtv;
    ComPtr<ID3D11ShaderResourceView> localSceneSrv;
    ComPtr<ID3D11Texture2D> localOutput;
    ComPtr<ID3D11RenderTargetView> localOutputRtv;
    ComPtr<ID3D11Buffer> localSceneConstants;
    ComPtr<ID3D11Buffer> localPostConstants;

    ComPtr<ID3D11Texture2D> sharedPrimary;
    ComPtr<ID3D11RenderTargetView> sharedPrimaryRtv;
    ComPtr<ID3D11ShaderResourceView> sharedPrimarySrv;
    ComPtr<IDXGIKeyedMutex> sharedPrimaryMutex;
    ComPtr<ID3D11Buffer> primaryConstants;

    ComPtr<ID3D11Texture2D> sharedSecondary;
    ComPtr<ID3D11ShaderResourceView> sharedSecondarySrv;
    ComPtr<IDXGIKeyedMutex> sharedSecondaryMutex;
    ComPtr<ID3D11Texture2D> secondaryOutput;
    ComPtr<ID3D11RenderTargetView> secondaryOutputRtv;
    ComPtr<ID3D11Buffer> secondaryConstants;

    QuerySet singleQueries;
    QuerySet primaryQueries;
    QuerySet secondaryQueries;
    int singlePreviousFrame = -1;
    int primaryPreviousFrame = -1;
    int secondaryPreviousFrame = -1;
    bool ready = false;
};

void CreateTextureViews(ID3D11Device* device, UINT width, UINT height,
                        ComPtr<ID3D11Texture2D>* texture,
                        ComPtr<ID3D11RenderTargetView>* rtv,
                        ComPtr<ID3D11ShaderResourceView>* srv,
                        UINT miscFlags = 0) {
    D3D11_TEXTURE2D_DESC desc = {};
    desc.Width = width;
    desc.Height = height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_RENDER_TARGET | (srv ? D3D11_BIND_SHADER_RESOURCE : 0);
    desc.MiscFlags = miscFlags;
    Check(device->CreateTexture2D(&desc, nullptr, texture->ReleaseAndGetAddressOf()), "CreateTexture2D");
    if (rtv) Check(device->CreateRenderTargetView(texture->Get(), nullptr, rtv->ReleaseAndGetAddressOf()),
                   "CreateRenderTargetView");
    if (srv) Check(device->CreateShaderResourceView(texture->Get(), nullptr, srv->ReleaseAndGetAddressOf()),
                   "CreateShaderResourceView");
}

void CreateSlot(Slot* slot, DeviceBundle& primary, DeviceBundle& secondary,
                UINT width, UINT height, bool local, bool shared) {
    if (local) {
        CreateTextureViews(primary.device.Get(), width, height, &slot->localScene,
                           &slot->localSceneRtv, &slot->localSceneSrv);
        CreateTextureViews(primary.device.Get(), width, height, &slot->localOutput,
                           &slot->localOutputRtv, nullptr);
        slot->localSceneConstants = CreateConstantBuffer(primary.device.Get());
        slot->localPostConstants = CreateConstantBuffer(primary.device.Get());
        slot->singleQueries = CreateQueries(primary.device.Get());
    }
    if (!shared) return;

    CreateTextureViews(primary.device.Get(), width, height, &slot->sharedPrimary,
                       &slot->sharedPrimaryRtv, &slot->sharedPrimarySrv,
                       D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX);
    Check(slot->sharedPrimary.As(&slot->sharedPrimaryMutex), "Query primary keyed mutex");
    ComPtr<IDXGIResource1> resource;
    Check(slot->sharedPrimary.As(&resource), "Query IDXGIResource1");
    HANDLE handle = nullptr;
    Check(resource->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE,
                                       nullptr, &handle), "CreateSharedHandle");
    HRESULT openHr = secondary.device1->OpenSharedResource1(handle, IID_PPV_ARGS(&slot->sharedSecondary));
    CloseHandle(handle);
    Check(openHr, "OpenSharedResource1 on secondary adapter");
    Check(slot->sharedSecondary.As(&slot->sharedSecondaryMutex), "Query secondary keyed mutex");
    Check(secondary.device->CreateShaderResourceView(slot->sharedSecondary.Get(), nullptr,
                                                     &slot->sharedSecondarySrv),
          "Create secondary shared SRV");
    CreateTextureViews(secondary.device.Get(), width, height, &slot->secondaryOutput,
                       &slot->secondaryOutputRtv, nullptr);
    slot->primaryConstants = CreateConstantBuffer(primary.device.Get());
    slot->secondaryConstants = CreateConstantBuffer(secondary.device.Get());
    slot->primaryQueries = CreateQueries(primary.device.Get());
    slot->secondaryQueries = CreateQueries(secondary.device.Get());
}

struct QueryResult {
    double firstMs = 0.0;
    double secondMs = 0.0;
    UINT64 endTick = 0;
    UINT64 frequency = 0;
    bool disjoint = false;
};

template <typename T>
T WaitQueryData(ID3D11DeviceContext* context, ID3D11Query* query, const char* name) {
    const ULONGLONG deadline = GetTickCount64() + kQueryTimeoutMs;
    T value = {};
    for (;;) {
        HRESULT hr = context->GetData(query, &value, sizeof(value), 0);
        if (hr == S_OK) return value;
        if (FAILED(hr)) Check(hr, name);
        if (GetTickCount64() >= deadline) throw std::runtime_error(std::string(name) + " timed out");
        SwitchToThread();
    }
}

QueryResult ReadQueries(ID3D11DeviceContext* context, QuerySet& queries, bool hasMiddle) {
    const auto disjoint = WaitQueryData<D3D11_QUERY_DATA_TIMESTAMP_DISJOINT>(
        context, queries.disjoint.Get(), "timestamp disjoint query");
    const UINT64 begin = WaitQueryData<UINT64>(context, queries.begin.Get(), "begin timestamp query");
    const UINT64 middle = hasMiddle
        ? WaitQueryData<UINT64>(context, queries.middle.Get(), "middle timestamp query") : begin;
    const UINT64 end = WaitQueryData<UINT64>(context, queries.end.Get(), "end timestamp query");
    QueryResult result;
    result.frequency = disjoint.Frequency;
    result.endTick = end;
    result.disjoint = disjoint.Disjoint != FALSE || !disjoint.Frequency;
    if (!result.disjoint) {
        result.firstMs = static_cast<double>(middle - begin) * 1000.0 /
                         static_cast<double>(disjoint.Frequency);
        result.secondMs = static_cast<double>(end - middle) * 1000.0 /
                          static_cast<double>(disjoint.Frequency);
    }
    return result;
}

void BeginQueries(ID3D11DeviceContext* context, QuerySet& queries) {
    context->Begin(queries.disjoint.Get());
    context->End(queries.begin.Get());
}

void EndQueries(ID3D11DeviceContext* context, QuerySet& queries) {
    context->End(queries.end.Get());
    context->End(queries.disjoint.Get());
}

void SetCommonState(DeviceBundle& device, ID3D11Buffer* constants, UINT width, UINT height) {
    D3D11_VIEWPORT viewport = {};
    viewport.Width = static_cast<float>(width);
    viewport.Height = static_cast<float>(height);
    viewport.MaxDepth = 1.0f;
    device.context->RSSetViewports(1, &viewport);
    device.context->IASetInputLayout(nullptr);
    device.context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    device.context->VSSetShader(device.vertexShader.Get(), nullptr, 0);
    device.context->VSSetConstantBuffers(0, 1, &constants);
    device.context->PSSetConstantBuffers(0, 1, &constants);
}

void RenderScene(DeviceBundle& device, ID3D11RenderTargetView* target, ID3D11Buffer* constants,
                 QuerySet& queries, const ShaderParams& params, UINT width, UINT height,
                 bool finishQueries) {
    device.context->UpdateSubresource(constants, 0, nullptr, &params, 0, 0);
    BeginQueries(device.context.Get(), queries);
    SetCommonState(device, constants, width, height);
    device.context->OMSetRenderTargets(1, &target, nullptr);
    device.context->PSSetShader(device.sceneShader.Get(), nullptr, 0);
    device.context->Draw(3, 0);
    device.context->End(queries.middle.Get());
    if (finishQueries) EndQueries(device.context.Get(), queries);
}

void RenderPost(DeviceBundle& device, ID3D11ShaderResourceView* source,
                ID3D11RenderTargetView* target, ID3D11Buffer* constants,
                QuerySet& queries, const ShaderParams& params, UINT width, UINT height,
                bool queriesAlreadyBegun) {
    device.context->UpdateSubresource(constants, 0, nullptr, &params, 0, 0);
    if (!queriesAlreadyBegun) BeginQueries(device.context.Get(), queries);
    SetCommonState(device, constants, width, height);
    device.context->OMSetRenderTargets(1, &target, nullptr);
    device.context->PSSetShader(device.postShader.Get(), nullptr, 0);
    device.context->PSSetShaderResources(0, 1, &source);
    ID3D11SamplerState* sampler = device.sampler.Get();
    device.context->PSSetSamplers(0, 1, &sampler);
    device.context->Draw(3, 0);
    ID3D11ShaderResourceView* none = nullptr;
    device.context->PSSetShaderResources(0, 1, &none);
    EndQueries(device.context.Get(), queries);
}

struct FrameSample {
    UINT frame = 0;
    double sceneGpuMs = 0.0;
    double postGpuMs = 0.0;
    UINT64 completionTick = 0;
    UINT64 completionFrequency = 0;
    double completionIntervalMs = 0.0;
    bool disjoint = false;
    bool primaryDisjoint = false;
    bool secondaryDisjoint = false;
};

struct PhaseResult {
    UINT phase = 0;
    BenchMode mode = BenchMode::Single;
    std::vector<FrameSample> frames;
    double wallSeconds = 0.0;
    double wallFps = 0.0;
    double averageFps = 0.0;
    double onePercentLowFps = 0.0;
    double p50Ms = 0.0;
    double p95Ms = 0.0;
    double p99Ms = 0.0;
    double sceneGpuMs = 0.0;
    double postGpuMs = 0.0;
    bool valid = false;
    std::string invalidReason;
};

class BenchPipeline {
public:
    BenchPipeline(const AdapterInfo& primaryInfo, const AdapterInfo& secondaryInfo,
                  UINT width, UINT height)
        : primary_(CreateDevice(primaryInfo, true)), secondary_(CreateDevice(secondaryInfo, true)),
          width_(width), height_(height) {
        for (auto& slot : slots_) CreateSlot(&slot, primary_, secondary_, width_, height_, true, true);
    }

    static void Probe(const AdapterInfo& primaryInfo, const AdapterInfo& secondaryInfo,
                      UINT width, UINT height) {
        auto primary = CreateDevice(primaryInfo, false);
        auto secondary = CreateDevice(secondaryInfo, false);
        Slot slot;
        CreateSlot(&slot, primary, secondary, width, height, false, true);
    }

    PhaseResult RunPhase(UINT phase, BenchMode mode, UINT warmupFrames, UINT measureFrames,
                         UINT sceneLoops, UINT postLoops) {
        if (warmupFrames) {
            if (mode == BenchMode::Single) RunSingle(0, warmupFrames, sceneLoops, postLoops, false);
            else RunHeterogeneous(0, warmupFrames, sceneLoops, postLoops, false);
        }
        PhaseResult result = mode == BenchMode::Single
            ? RunSingle(phase, measureFrames, sceneLoops, postLoops, true)
            : RunHeterogeneous(phase, measureFrames, sceneLoops, postLoops, true);
        Summarize(&result);
        return result;
    }

private:
    std::array<Slot, kPipelineSlots> slots_;
    DeviceBundle primary_;
    DeviceBundle secondary_;
    UINT width_;
    UINT height_;

    ShaderParams ParamsFor(UINT frame, UINT loops) const {
        ShaderParams value;
        value.phase = static_cast<float>(frame) * 0.013f;
        value.invWidth = 1.0f / static_cast<float>(width_);
        value.invHeight = 1.0f / static_cast<float>(height_);
        value.loopCount = loops;
        return value;
    }

    PhaseResult RunSingle(UINT phase, UINT count, UINT sceneLoops, UINT postLoops, bool retain) {
        PhaseResult result;
        result.phase = phase;
        result.mode = BenchMode::Single;
        result.frames.resize(count);
        for (auto& slot : slots_) slot.singlePreviousFrame = -1;
        const auto start = std::chrono::steady_clock::now();
        for (UINT frame = 0; frame < count; ++frame) {
            Slot& slot = slots_[frame % kPipelineSlots];
            if (slot.singlePreviousFrame >= 0) {
                const QueryResult timing = ReadQueries(primary_.context.Get(), slot.singleQueries, true);
                StoreSingle(&result.frames[static_cast<size_t>(slot.singlePreviousFrame)], timing);
            }
            RenderScene(primary_, slot.localSceneRtv.Get(), slot.localSceneConstants.Get(),
                        slot.singleQueries, ParamsFor(frame, sceneLoops), width_, height_, false);
            RenderPost(primary_, slot.localSceneSrv.Get(), slot.localOutputRtv.Get(),
                       slot.localPostConstants.Get(), slot.singleQueries, ParamsFor(frame, postLoops),
                       width_, height_, true);
            primary_.context->Flush();
            result.frames[frame].frame = frame;
            slot.singlePreviousFrame = static_cast<int>(frame);
        }
        for (auto& slot : slots_) {
            if (slot.singlePreviousFrame >= 0) {
                const QueryResult timing = ReadQueries(primary_.context.Get(), slot.singleQueries, true);
                StoreSingle(&result.frames[static_cast<size_t>(slot.singlePreviousFrame)], timing);
            }
        }
        const auto end = std::chrono::steady_clock::now();
        result.wallSeconds = std::chrono::duration<double>(end - start).count();
        if (!retain) result.frames.clear();
        return result;
    }

    PhaseResult RunHeterogeneous(UINT phase, UINT count, UINT sceneLoops, UINT postLoops, bool retain) {
        PhaseResult result;
        result.phase = phase;
        result.mode = BenchMode::Heterogeneous;
        result.frames.resize(count);
        for (auto& slot : slots_) {
            slot.primaryPreviousFrame = -1;
            slot.secondaryPreviousFrame = -1;
            slot.ready = false;
        }
        std::mutex mutex;
        std::condition_variable changed;
        std::atomic<bool> abort{false};
        std::exception_ptr workerError;
        const auto start = std::chrono::steady_clock::now();

        std::thread secondaryWorker([&] {
            try {
                for (UINT frame = 0; frame < count; ++frame) {
                    Slot& slot = slots_[frame % kPipelineSlots];
                    {
                        std::unique_lock<std::mutex> lock(mutex);
                        changed.wait(lock, [&] { return slot.ready || abort.load(); });
                        if (abort.load()) return;
                        slot.ready = false;
                    }
                    if (slot.secondaryPreviousFrame >= 0) {
                        const QueryResult old = ReadQueries(secondary_.context.Get(), slot.secondaryQueries, false);
                        StoreSecondary(&result.frames[static_cast<size_t>(slot.secondaryPreviousFrame)], old);
                    }
                    Check(slot.sharedSecondaryMutex->AcquireSync(1, kMutexTimeoutMs),
                          "secondary keyed mutex AcquireSync");
                    RenderPost(secondary_, slot.sharedSecondarySrv.Get(), slot.secondaryOutputRtv.Get(),
                               slot.secondaryConstants.Get(), slot.secondaryQueries,
                               ParamsFor(frame, postLoops), width_, height_, false);
                    secondary_.context->Flush();
                    Check(slot.sharedSecondaryMutex->ReleaseSync(0), "secondary keyed mutex ReleaseSync");
                    slot.secondaryPreviousFrame = static_cast<int>(frame);
                    changed.notify_all();
                }
                for (auto& slot : slots_) {
                    if (slot.secondaryPreviousFrame >= 0) {
                        const QueryResult old = ReadQueries(secondary_.context.Get(), slot.secondaryQueries, false);
                        StoreSecondary(&result.frames[static_cast<size_t>(slot.secondaryPreviousFrame)], old);
                    }
                }
            } catch (...) {
                workerError = std::current_exception();
                abort.store(true);
                changed.notify_all();
            }
        });

        try {
            for (UINT frame = 0; frame < count; ++frame) {
                if (abort.load()) break;
                Slot& slot = slots_[frame % kPipelineSlots];
                Check(slot.sharedPrimaryMutex->AcquireSync(0, kMutexTimeoutMs),
                      "primary keyed mutex AcquireSync");
                if (slot.primaryPreviousFrame >= 0) {
                    const QueryResult old = ReadQueries(primary_.context.Get(), slot.primaryQueries, false);
                    StorePrimary(&result.frames[static_cast<size_t>(slot.primaryPreviousFrame)], old);
                }
                RenderScene(primary_, slot.sharedPrimaryRtv.Get(), slot.primaryConstants.Get(),
                            slot.primaryQueries, ParamsFor(frame, sceneLoops), width_, height_, true);
                primary_.context->Flush();
                Check(slot.sharedPrimaryMutex->ReleaseSync(1), "primary keyed mutex ReleaseSync");
                result.frames[frame].frame = frame;
                slot.primaryPreviousFrame = static_cast<int>(frame);
                {
                    std::lock_guard<std::mutex> lock(mutex);
                    slot.ready = true;
                }
                changed.notify_all();
            }
        } catch (...) {
            abort.store(true);
            changed.notify_all();
            if (secondaryWorker.joinable()) secondaryWorker.join();
            throw;
        }
        secondaryWorker.join();
        if (workerError) std::rethrow_exception(workerError);
        for (auto& slot : slots_) {
            if (slot.primaryPreviousFrame >= 0) {
                const QueryResult old = ReadQueries(primary_.context.Get(), slot.primaryQueries, false);
                StorePrimary(&result.frames[static_cast<size_t>(slot.primaryPreviousFrame)], old);
            }
        }
        const auto end = std::chrono::steady_clock::now();
        result.wallSeconds = std::chrono::duration<double>(end - start).count();
        if (!retain) result.frames.clear();
        return result;
    }

    static void StoreSingle(FrameSample* frame, const QueryResult& timing) {
        frame->sceneGpuMs = timing.firstMs;
        frame->postGpuMs = timing.secondMs;
        frame->completionTick = timing.endTick;
        frame->completionFrequency = timing.frequency;
        frame->disjoint = timing.disjoint;
    }

    static void StorePrimary(FrameSample* frame, const QueryResult& timing) {
        frame->sceneGpuMs = timing.firstMs + timing.secondMs;
        frame->primaryDisjoint = timing.disjoint;
    }

    static void StoreSecondary(FrameSample* frame, const QueryResult& timing) {
        frame->postGpuMs = timing.firstMs + timing.secondMs;
        frame->completionTick = timing.endTick;
        frame->completionFrequency = timing.frequency;
        frame->secondaryDisjoint = timing.disjoint;
    }

    static void Summarize(PhaseResult* result) {
        if (result->frames.size() < 2 || result->wallSeconds <= 0.0) {
            result->invalidReason = "insufficient samples";
            return;
        }
        std::vector<double> intervals;
        std::vector<double> scene;
        std::vector<double> post;
        for (size_t i = 0; i < result->frames.size(); ++i) {
            auto& frame = result->frames[i];
            frame.disjoint = frame.disjoint || frame.primaryDisjoint || frame.secondaryDisjoint;
            if (frame.disjoint || !frame.completionFrequency) {
                result->invalidReason = "GPU timestamp query was disjoint";
                return;
            }
            scene.push_back(frame.sceneGpuMs);
            post.push_back(frame.postGpuMs);
            if (i == 0) continue;
            const auto& previous = result->frames[i - 1];
            if (previous.completionFrequency != frame.completionFrequency ||
                frame.completionTick <= previous.completionTick) {
                result->invalidReason = "GPU completion timestamps were not monotonic";
                return;
            }
            frame.completionIntervalMs = static_cast<double>(frame.completionTick - previous.completionTick) *
                                         1000.0 / static_cast<double>(frame.completionFrequency);
            intervals.push_back(frame.completionIntervalMs);
        }
        result->wallFps = static_cast<double>(result->frames.size()) / result->wallSeconds;
        const double meanMs = Mean(intervals);
        result->averageFps = meanMs > 0.0 ? 1000.0 / meanMs : 0.0;
        result->onePercentLowFps = OnePercentLowFps(intervals);
        result->p50Ms = Percentile(intervals, 0.50);
        result->p95Ms = Percentile(intervals, 0.95);
        result->p99Ms = Percentile(intervals, 0.99);
        result->sceneGpuMs = Mean(scene);
        result->postGpuMs = Mean(post);
        result->valid = true;
    }
};

struct Evaluation {
    bool dataValid = false;
    bool candidateBenefit = false;
    double singleFps = 0.0;
    double heterogeneousFps = 0.0;
    double fpsDeltaPercent = 0.0;
    double singleOnePercentLow = 0.0;
    double heterogeneousOnePercentLow = 0.0;
    double lowDeltaPercent = 0.0;
    double singleP99Ms = 0.0;
    double heterogeneousP99Ms = 0.0;
    double maxBaselineDriftPercent = 0.0;
    UINT pairedWins = 0;
    UINT pairedComparisons = 0;
    std::string verdict;
};

double RelativeDeltaPercent(double candidate, double baseline) {
    return baseline != 0.0 ? (candidate / baseline - 1.0) * 100.0 : 0.0;
}

Evaluation Evaluate(const std::vector<PhaseResult>& phases) {
    Evaluation value;
    if (phases.empty()) {
        value.verdict = "invalid: no phases";
        return value;
    }
    std::vector<double> singleFps, heterogeneousFps, singleLow, heterogeneousLow;
    std::vector<double> singleP99, heterogeneousP99;
    for (const auto& phase : phases) {
        if (!phase.valid) {
            value.verdict = "invalid: phase " + std::to_string(phase.phase) + " " + phase.invalidReason;
            return value;
        }
        auto& fps = phase.mode == BenchMode::Single ? singleFps : heterogeneousFps;
        auto& low = phase.mode == BenchMode::Single ? singleLow : heterogeneousLow;
        auto& p99 = phase.mode == BenchMode::Single ? singleP99 : heterogeneousP99;
        fps.push_back(phase.averageFps);
        low.push_back(phase.onePercentLowFps);
        p99.push_back(phase.p99Ms);
    }
    if (singleFps.empty() || heterogeneousFps.empty()) {
        value.verdict = "invalid: both modes were not measured";
        return value;
    }
    value.singleFps = Median(singleFps);
    value.heterogeneousFps = Median(heterogeneousFps);
    value.fpsDeltaPercent = RelativeDeltaPercent(value.heterogeneousFps, value.singleFps);
    value.singleOnePercentLow = Median(singleLow);
    value.heterogeneousOnePercentLow = Median(heterogeneousLow);
    value.lowDeltaPercent = RelativeDeltaPercent(value.heterogeneousOnePercentLow,
                                                 value.singleOnePercentLow);
    value.singleP99Ms = Median(singleP99);
    value.heterogeneousP99Ms = Median(heterogeneousP99);

    auto drift = [](const std::vector<double>& values) {
        if (values.size() < 2) return 0.0;
        const double center = (values.front() + values.back()) * 0.5;
        return center > 0.0 ? std::abs(values.back() - values.front()) / center * 100.0 : 0.0;
    };
    value.maxBaselineDriftPercent = std::max(drift(singleFps), drift(heterogeneousFps));

    for (size_t base = 0; base + 3 < phases.size(); base += 4) {
        std::vector<double> a, b;
        for (size_t i = base; i < base + 4; ++i) {
            (phases[i].mode == BenchMode::Single ? a : b).push_back(phases[i].averageFps);
        }
        if (!a.empty() && !b.empty()) {
            ++value.pairedComparisons;
            if (Median(b) >= Median(a)) ++value.pairedWins;
        }
    }

    value.dataValid = value.maxBaselineDriftPercent <= 5.0;
    if (!value.dataValid) {
        value.verdict = "invalid: first/last baseline drift exceeded 5%";
        return value;
    }
    const UINT requiredWins = value.pairedComparisons ? (value.pairedComparisons * 2 + 2) / 3 : 1;
    value.candidateBenefit = value.fpsDeltaPercent >= 2.0 && value.lowDeltaPercent >= 1.0 &&
                             value.pairedWins >= requiredWins &&
                             value.heterogeneousP99Ms <= value.singleP99Ms * 1.05;
    value.verdict = value.candidateBenefit
        ? "candidate benefit; requires an engine/game prototype replication"
        : "benefit not demonstrated";
    return value;
}

std::vector<BenchMode> MakeSchedule(UINT repeats) {
    std::vector<BenchMode> result;
    for (UINT repeat = 0; repeat < repeats; ++repeat) {
        const std::array<BenchMode, 4> abba = {BenchMode::Single, BenchMode::Heterogeneous,
                                               BenchMode::Heterogeneous, BenchMode::Single};
        const std::array<BenchMode, 4> baab = {BenchMode::Heterogeneous, BenchMode::Single,
                                               BenchMode::Single, BenchMode::Heterogeneous};
        const auto& block = (repeat % 2 == 0) ? abba : baab;
        result.insert(result.end(), block.begin(), block.end());
    }
    return result;
}

std::string DriverVersion(const AdapterInfo& adapter) {
    LARGE_INTEGER version = {};
    if (FAILED(adapter.adapter->CheckInterfaceSupport(IID_IDXGIDevice, &version))) return "unavailable";
    const UINT64 raw = static_cast<UINT64>(version.QuadPart);
    std::ostringstream out;
    out << ((raw >> 48) & 0xffff) << "." << ((raw >> 32) & 0xffff) << "."
        << ((raw >> 16) & 0xffff) << "." << (raw & 0xffff);
    return out.str();
}

std::string OsVersion() {
    using RtlGetVersionFn = LONG(WINAPI*)(PRTL_OSVERSIONINFOW);
    HMODULE module = GetModuleHandleW(L"ntdll.dll");
    auto fn = reinterpret_cast<RtlGetVersionFn>(GetProcAddress(module, "RtlGetVersion"));
    RTL_OSVERSIONINFOW info = {};
    info.dwOSVersionInfoSize = sizeof(info);
    if (!fn || fn(&info) != 0) return "unknown";
    return std::to_string(info.dwMajorVersion) + "." + std::to_string(info.dwMinorVersion) +
           "." + std::to_string(info.dwBuildNumber);
}

void WriteFramesCsv(const std::filesystem::path& path, const std::vector<PhaseResult>& phases) {
    std::ofstream out(path, std::ios::binary);
    if (!out) throw std::runtime_error("could not create frames.csv");
    out << "phase,mode,frame,completion_interval_ms,scene_gpu_ms,post_gpu_ms,disjoint\n";
    out << std::fixed << std::setprecision(6);
    for (const auto& phase : phases) {
        for (const auto& frame : phase.frames) {
            out << phase.phase << ',' << ModeName(phase.mode) << ',' << frame.frame << ',';
            if (frame.frame != 0) out << frame.completionIntervalMs;
            out << ',' << frame.sceneGpuMs << ',' << frame.postGpuMs << ','
                << (frame.disjoint ? 1 : 0) << '\n';
        }
    }
}

void WriteReport(const std::filesystem::path& path, const AdapterInfo& primary,
                 const AdapterInfo& secondary, const Options& options,
                 const std::vector<PhaseResult>& phases, const Evaluation& evaluation) {
    std::ofstream out(path, std::ios::binary);
    if (!out) throw std::runtime_error("could not create report.txt");
    out << "Pavise Heterogeneous GPU Bench\n\n"
        << "OS: " << OsVersion() << "\n"
        << "Primary: " << AdapterText(primary) << " driver=" << DriverVersion(primary) << "\n"
        << "Secondary: " << AdapterText(secondary) << " driver=" << DriverVersion(secondary) << "\n"
        << "Resolution: " << options.width << "x" << options.height << "\n"
        << "Warmup/measure frames: " << options.warmupFrames << "/" << options.measureFrames << "\n"
        << "Scene/post loops: " << options.sceneLoops << "/" << options.postLoops << "\n\n";
    out << std::fixed << std::setprecision(3);
    for (const auto& phase : phases) {
        out << "Phase " << phase.phase << " " << ModeName(phase.mode)
            << " avg=" << phase.averageFps << " fps, 1%low=" << phase.onePercentLowFps
            << " fps, p99=" << phase.p99Ms << " ms, wall=" << phase.wallFps
            << " fps, sceneGPU=" << phase.sceneGpuMs << " ms, postGPU=" << phase.postGpuMs
            << " ms, valid=" << (phase.valid ? "yes" : "no") << "\n";
    }
    out << "\nMedian single/heterogeneous FPS: " << evaluation.singleFps << " / "
        << evaluation.heterogeneousFps << " (" << evaluation.fpsDeltaPercent << "%)\n"
        << "Median single/heterogeneous 1% low: " << evaluation.singleOnePercentLow << " / "
        << evaluation.heterogeneousOnePercentLow << " (" << evaluation.lowDeltaPercent << "%)\n"
        << "Paired wins: " << evaluation.pairedWins << "/" << evaluation.pairedComparisons << "\n"
        << "Max baseline drift: " << evaluation.maxBaselineDriftPercent << "%\n"
        << "Verdict: " << evaluation.verdict << "\n";
}

void WriteSummaryJson(const std::filesystem::path& path, const AdapterInfo& primary,
                      const AdapterInfo& secondary, const Options& options,
                      const std::vector<PhaseResult>& phases, const Evaluation& evaluation) {
    std::ofstream out(path, std::ios::binary);
    if (!out) throw std::runtime_error("could not create summary.json");
    auto adapterJson = [&](const AdapterInfo& info) {
        out << "{\"index\":" << info.index << ",\"name\":\"" << JsonEscape(Narrow(info.desc.Description))
            << "\",\"vendor_id\":" << info.desc.VendorId << ",\"device_id\":" << info.desc.DeviceId
            << ",\"dedicated_video_memory\":" << info.desc.DedicatedVideoMemory
            << ",\"shared_system_memory\":" << info.desc.SharedSystemMemory
            << ",\"driver_version\":\"" << DriverVersion(info) << "\"}";
    };
    out << std::fixed << std::setprecision(6);
    out << "{\n  \"schema\":1,\n  \"os\":\"" << OsVersion() << "\",\n  \"primary\":";
    adapterJson(primary);
    out << ",\n  \"secondary\":";
    adapterJson(secondary);
    out << ",\n  \"protocol\":{\"width\":" << options.width << ",\"height\":" << options.height
        << ",\"warmup_frames\":" << options.warmupFrames << ",\"measure_frames\":"
        << options.measureFrames << ",\"repeats\":" << options.repeats << ",\"scene_loops\":"
        << options.sceneLoops << ",\"post_loops\":" << options.postLoops << "},\n  \"phases\":[\n";
    for (size_t i = 0; i < phases.size(); ++i) {
        const auto& phase = phases[i];
        out << "    {\"phase\":" << phase.phase << ",\"mode\":\"" << ModeName(phase.mode)
            << "\",\"valid\":" << (phase.valid ? "true" : "false")
            << ",\"average_fps\":" << phase.averageFps << ",\"one_percent_low_fps\":"
            << phase.onePercentLowFps << ",\"p50_ms\":" << phase.p50Ms << ",\"p95_ms\":"
            << phase.p95Ms << ",\"p99_ms\":" << phase.p99Ms << ",\"wall_fps\":" << phase.wallFps
            << ",\"scene_gpu_ms\":" << phase.sceneGpuMs << ",\"post_gpu_ms\":"
            << phase.postGpuMs << ",\"invalid_reason\":\"" << JsonEscape(phase.invalidReason) << "\"}"
            << (i + 1 == phases.size() ? "\n" : ",\n");
    }
    out << "  ],\n  \"evaluation\":{\"data_valid\":" << (evaluation.dataValid ? "true" : "false")
        << ",\"candidate_benefit\":" << (evaluation.candidateBenefit ? "true" : "false")
        << ",\"single_fps\":" << evaluation.singleFps << ",\"heterogeneous_fps\":"
        << evaluation.heterogeneousFps << ",\"fps_delta_percent\":" << evaluation.fpsDeltaPercent
        << ",\"single_one_percent_low\":" << evaluation.singleOnePercentLow
        << ",\"heterogeneous_one_percent_low\":" << evaluation.heterogeneousOnePercentLow
        << ",\"low_delta_percent\":" << evaluation.lowDeltaPercent << ",\"paired_wins\":"
        << evaluation.pairedWins << ",\"paired_comparisons\":" << evaluation.pairedComparisons
        << ",\"max_baseline_drift_percent\":" << evaluation.maxBaselineDriftPercent
        << ",\"verdict\":\"" << JsonEscape(evaluation.verdict) << "\"}\n}\n";
}

void PrintPlan(const Options& options) {
    const auto schedule = MakeSchedule(options.repeats);
    std::cout << "Pavise Heterogeneous GPU Bench (safe plan mode)\n"
              << "No D3D device was created and no GPU workload was run.\n\n"
              << "Explicit actions:\n"
              << "  --list-adapters   enumerate hardware adapters only\n"
              << "  --probe           create both devices and test shared-resource setup; no draw\n"
              << "  --self-test       synthetic statistics only; no D3D device\n"
              << "  --run             run the GPU benchmark\n\n"
              << "Planned resolution: " << options.width << "x" << options.height << '\n'
              << "Warmup/measure frames per phase: " << options.warmupFrames << "/"
              << options.measureFrames << "\nSchedule: ";
    for (size_t i = 0; i < schedule.size(); ++i) {
        if (i) std::cout << ',';
        std::cout << ModeName(schedule[i]);
    }
    std::cout << "\n";
}

int RunSelfTest() {
    const std::vector<double> values = {10.0, 11.0, 12.0, 13.0, 14.0};
    if (std::abs(Mean(values) - 12.0) > 1e-9 || std::abs(Median(values) - 12.0) > 1e-9 ||
        std::abs(Percentile(values, 0.0) - 10.0) > 1e-9 ||
        std::abs(Percentile(values, 1.0) - 14.0) > 1e-9 || OnePercentLowFps(values) <= 0.0) {
        std::cerr << "statistics self-test failed\n";
        return 2;
    }
    const auto schedule = MakeSchedule(3);
    if (schedule.size() != 12 || schedule[0] != BenchMode::Single ||
        schedule[4] != BenchMode::Heterogeneous) {
        std::cerr << "schedule self-test failed\n";
        return 2;
    }
    std::cout << "self-test passed (synthetic data only; no D3D device created)\n";
    return 0;
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
    try {
        const Options options = ParseArgs(argc, argv);
        if (options.action == Action::Plan) {
            PrintPlan(options);
            return 0;
        }
        if (options.action == Action::SelfTest) return RunSelfTest();

        const auto adapters = EnumerateAdapters();
        if (options.action == Action::ListAdapters) {
            for (const auto& adapter : adapters) std::cout << AdapterText(adapter) << '\n';
            return adapters.empty() ? 2 : 0;
        }
        const auto selected = SelectAdapters(adapters, options);
        std::cout << "Primary: " << AdapterText(selected.first) << "\nSecondary: "
                  << AdapterText(selected.second) << "\n";

        if (options.action == Action::Probe) {
            BenchPipeline::Probe(selected.first, selected.second, options.width, options.height);
            std::cout << "probe passed: two devices and cross-adapter shared resources were created; "
                         "no draw or benchmark was run\n";
            return 0;
        }

        if (options.action != Action::Run) throw std::runtime_error("internal action error");
        BenchPipeline pipeline(selected.first, selected.second, options.width, options.height);
        const auto schedule = MakeSchedule(options.repeats);
        std::vector<PhaseResult> phases;
        phases.reserve(schedule.size());
        for (size_t index = 0; index < schedule.size(); ++index) {
            std::cout << "phase " << (index + 1) << "/" << schedule.size() << " "
                      << ModeName(schedule[index]) << "..." << std::flush;
            phases.push_back(pipeline.RunPhase(static_cast<UINT>(index + 1), schedule[index],
                                               options.warmupFrames, options.measureFrames,
                                               options.sceneLoops, options.postLoops));
            std::cout << " " << std::fixed << std::setprecision(2) << phases.back().averageFps
                      << " fps\n";
        }
        const Evaluation evaluation = Evaluate(phases);
        std::filesystem::path output = options.outputDirectory;
        if (output.empty()) output = std::filesystem::current_path() /
            (L"Pavise-HeterogeneousGpu-Results-" + TimestampName());
        std::filesystem::create_directories(output);
        WriteFramesCsv(output / "frames.csv", phases);
        WriteSummaryJson(output / "summary.json", selected.first, selected.second, options,
                         phases, evaluation);
        WriteReport(output / "report.txt", selected.first, selected.second, options,
                    phases, evaluation);
        std::cout << "Verdict: " << evaluation.verdict << "\nResults: "
                  << Narrow(output.wstring()) << "\n";
        return evaluation.dataValid ? 0 : 3;
    } catch (const std::exception& error) {
        std::cerr << "error: " << error.what() << "\n";
        return 1;
    }
}
