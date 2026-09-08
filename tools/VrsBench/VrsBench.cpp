// 文件用途 Pavise 的 DX12 可变速率着色台架
// 只做离屏 没有窗口 交换链 输入钩子 不碰游戏进程 不注入

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <d3d12.h>
#include <dxgi1_6.h>

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <memory>
#include <numeric>
#include <sstream>
#include <string>
#include <vector>

#ifdef _MSC_VER
#pragma comment(lib, "d3d12.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "shell32.lib")
#endif

namespace {

template <typename T>
class ComPtr {
public:
    ComPtr() = default;
    ComPtr(const ComPtr& other) : value_(other.value_) { if (value_) value_->AddRef(); }
    ComPtr(ComPtr&& other) noexcept : value_(other.value_) { other.value_ = nullptr; }
    ~ComPtr() { Reset(); }
    ComPtr& operator=(const ComPtr& other) {
        if (this != std::addressof(other)) {
            Reset();
            value_ = other.value_;
            if (value_) value_->AddRef();
        }
        return *this;
    }
    ComPtr& operator=(ComPtr&& other) noexcept {
        if (this != std::addressof(other)) {
            Reset();
            value_ = other.value_;
            other.value_ = nullptr;
        }
        return *this;
    }
    T* Get() const { return value_; }
    T* operator->() const { return value_; }
    explicit operator bool() const { return value_ != nullptr; }
    T** operator&() { Reset(); return &value_; }
    void Reset() { if (value_) { value_->Release(); value_ = nullptr; } }
    template <typename U>
    HRESULT As(ComPtr<U>* output) const {
        if (!output) return E_POINTER;
        output->Reset();
        return value_ ? value_->QueryInterface(__uuidof(U), reinterpret_cast<void**>(&output->value_))
                      : E_NOINTERFACE;
    }
private:
    template <typename U> friend class ComPtr;
    T* value_ = nullptr;
};

struct Options {
    int width = 1600;
    int height = 900;
    int cycles = 14;
    int idleMs = 10;
    std::wstring outputDir;
};

struct Mode {
    const char* name;
    D3D12_SHADING_RATE rate;
    double theoreticalPixels;
    std::vector<double> milliseconds;
};

std::wstring ExeDirectory() {
    std::vector<wchar_t> path(32768);
    DWORD length = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (!length || length >= path.size()) return L".";
    std::wstring result(path.data(), length);
    size_t slash = result.find_last_of(L"\\/");
    return slash == std::wstring::npos ? L"." : result.substr(0, slash);
}

bool EnsureDirectory(const std::wstring& path) {
    if (path.empty()) return false;
    DWORD attrs = GetFileAttributesW(path.c_str());
    if (attrs != INVALID_FILE_ATTRIBUTES) return (attrs & FILE_ATTRIBUTE_DIRECTORY) != 0;

    size_t slash = path.find_last_of(L"\\/");
    if (slash != std::wstring::npos) {
        std::wstring parent = path.substr(0, slash);
        if (!parent.empty() && !EnsureDirectory(parent)) return false;
    }
    return CreateDirectoryW(path.c_str(), nullptr) != FALSE || GetLastError() == ERROR_ALREADY_EXISTS;
}

std::string Utf8(const std::wstring& text) {
    if (text.empty()) return {};
    int count = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()),
                                    nullptr, 0, nullptr, nullptr);
    std::string out(static_cast<size_t>(count), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()),
                        out.data(), count, nullptr, nullptr);
    return out;
}

bool WriteUtf8(const std::wstring& path, const std::string& text) {
    HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr,
                              CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    const unsigned char bom[] = {0xEF, 0xBB, 0xBF};
    DWORD written = 0;
    bool ok = WriteFile(file, bom, sizeof(bom), &written, nullptr) != FALSE;
    if (ok && !text.empty()) {
        ok = WriteFile(file, text.data(), static_cast<DWORD>(text.size()), &written, nullptr) != FALSE;
    }
    CloseHandle(file);
    return ok;
}

std::vector<unsigned char> ReadBinary(const std::wstring& path) {
    std::vector<unsigned char> data;
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr,
                              OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return data;
    LARGE_INTEGER size = {};
    if (!GetFileSizeEx(file, &size) || size.QuadPart <= 0 || size.QuadPart > 16 * 1024 * 1024) {
        CloseHandle(file);
        return data;
    }
    data.resize(static_cast<size_t>(size.QuadPart));
    DWORD read = 0;
    if (!ReadFile(file, data.data(), static_cast<DWORD>(data.size()), &read, nullptr) ||
        read != data.size()) data.clear();
    CloseHandle(file);
    return data;
}

std::wstring Timestamp() {
    SYSTEMTIME now = {};
    GetLocalTime(&now);
    wchar_t value[64] = {};
    swprintf(value, 64, L"%04u%02u%02u-%02u%02u%02u",
             now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond);
    return value;
}

int IntArg(int argc, wchar_t** argv, const wchar_t* name, int fallback, int low, int high) {
    for (int i = 1; i + 1 < argc; ++i) {
        if (_wcsicmp(argv[i], name) != 0) continue;
        wchar_t* end = nullptr;
        long value = wcstol(argv[i + 1], &end, 10);
        if (end != argv[i + 1] && end && !*end && value >= low && value <= high)
            return static_cast<int>(value);
    }
    return fallback;
}

std::wstring StringArg(int argc, wchar_t** argv, const wchar_t* name,
                       const std::wstring& fallback) {
    for (int i = 1; i + 1 < argc; ++i) {
        if (_wcsicmp(argv[i], name) == 0) return argv[i + 1];
    }
    return fallback;
}

std::string HrText(HRESULT hr) {
    char buffer[32] = {};
    snprintf(buffer, sizeof(buffer), "0x%08lX", static_cast<unsigned long>(hr));
    return buffer;
}

double Mean(const std::vector<double>& values) {
    if (values.empty()) return 0.0;
    return std::accumulate(values.begin(), values.end(), 0.0) / values.size();
}

double Percentile(std::vector<double> values, double p) {
    if (values.empty()) return 0.0;
    std::sort(values.begin(), values.end());
    double position = p * static_cast<double>(values.size() - 1);
    size_t lower = static_cast<size_t>(position);
    size_t upper = std::min(lower + 1, values.size() - 1);
    double fraction = position - static_cast<double>(lower);
    return values[lower] * (1.0 - fraction) + values[upper] * fraction;
}

const char* TierText(D3D12_VARIABLE_SHADING_RATE_TIER tier) {
    switch (tier) {
        case D3D12_VARIABLE_SHADING_RATE_TIER_1: return "Tier 1";
        case D3D12_VARIABLE_SHADING_RATE_TIER_2: return "Tier 2";
        default: return "Not supported";
    }
}

class Bench {
public:
    HRESULT Initialize(int width, int height) {
        width_ = width;
        height_ = height;

        UINT factoryFlags = 0;
        HRESULT hr = CreateDXGIFactory2(factoryFlags, IID_PPV_ARGS(&factory_));
        if (FAILED(hr)) return hr;

        ComPtr<IDXGIFactory6> factory6;
        factory_.As(std::addressof(factory6));
        ComPtr<IDXGIAdapter1> fallback;
        for (UINT index = 0;; ++index) {
            ComPtr<IDXGIAdapter1> candidate;
            if (factory6) {
                hr = factory6->EnumAdapterByGpuPreference(index, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE,
                                                           IID_PPV_ARGS(&candidate));
            } else {
                hr = factory_->EnumAdapters1(index, &candidate);
            }
            if (hr == DXGI_ERROR_NOT_FOUND) break;
            if (FAILED(hr)) return hr;

            DXGI_ADAPTER_DESC1 desc = {};
            candidate->GetDesc1(&desc);
            if (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) continue;
            if (!fallback) fallback = candidate;

            ComPtr<ID3D12Device> testDevice;
            if (FAILED(D3D12CreateDevice(candidate.Get(), D3D_FEATURE_LEVEL_11_0,
                                         IID_PPV_ARGS(&testDevice)))) continue;
            D3D12_FEATURE_DATA_D3D12_OPTIONS6 testOptions = {};
            if (SUCCEEDED(testDevice->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS6,
                                                          &testOptions, sizeof(testOptions))) &&
                testOptions.VariableShadingRateTier != D3D12_VARIABLE_SHADING_RATE_TIER_NOT_SUPPORTED) {
                adapter_ = candidate;
                device_ = testDevice;
                options6_ = testOptions;
                break;
            }
        }

        if (!adapter_) {
            if (!fallback) return DXGI_ERROR_NOT_FOUND;
            adapter_ = fallback;
            hr = D3D12CreateDevice(adapter_.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device_));
            if (FAILED(hr)) return hr;
            options6_ = {};
            device_->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS6, &options6_, sizeof(options6_));
        }

        DXGI_ADAPTER_DESC1 desc = {};
        adapter_->GetDesc1(&desc);
        adapterName_ = desc.Description;
        vendorId_ = desc.VendorId;
        deviceId_ = desc.DeviceId;
        dedicatedVideoMemory_ = desc.DedicatedVideoMemory;

        if (options6_.VariableShadingRateTier == D3D12_VARIABLE_SHADING_RATE_TIER_NOT_SUPPORTED)
            return S_FALSE;

        D3D12_COMMAND_QUEUE_DESC queueDesc = {};
        queueDesc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        queueDesc.Priority = D3D12_COMMAND_QUEUE_PRIORITY_NORMAL;
        hr = device_->CreateCommandQueue(&queueDesc, IID_PPV_ARGS(&queue_));
        if (FAILED(hr)) return hr;
        hr = queue_->GetTimestampFrequency(&timestampFrequency_);
        if (FAILED(hr) || !timestampFrequency_) return FAILED(hr) ? hr : E_FAIL;

        hr = device_->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT,
                                             IID_PPV_ARGS(&allocator_));
        if (FAILED(hr)) return hr;
        hr = device_->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator_.Get(),
                                        nullptr, IID_PPV_ARGS(&commandList_));
        if (FAILED(hr)) return hr;
        hr = commandList_.As(std::addressof(commandList5_));
        if (FAILED(hr)) return hr;
        commandList_->Close();

        hr = CreatePipeline();
        if (FAILED(hr)) return hr;
        hr = CreateTargetsAndQueries();
        if (FAILED(hr)) return hr;

        hr = device_->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence_));
        if (FAILED(hr)) return hr;
        fenceEvent_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        return fenceEvent_ ? S_OK : HRESULT_FROM_WIN32(GetLastError());
    }

    ~Bench() {
        if (fenceEvent_) CloseHandle(fenceEvent_);
    }

    HRESULT Measure(D3D12_SHADING_RATE rate, int draws, double* milliseconds) {
        if (!milliseconds) return E_POINTER;
        HRESULT hr = allocator_->Reset();
        if (FAILED(hr)) return hr;
        hr = commandList_->Reset(allocator_.Get(), pipeline_.Get());
        if (FAILED(hr)) return hr;

        D3D12_CPU_DESCRIPTOR_HANDLE rtv = rtvHeap_->GetCPUDescriptorHandleForHeapStart();
        commandList_->OMSetRenderTargets(1, &rtv, FALSE, nullptr);
        commandList_->RSSetViewports(1, &viewport_);
        commandList_->RSSetScissorRects(1, &scissor_);
        commandList_->SetGraphicsRootSignature(rootSignature_.Get());
        commandList_->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        commandList5_->RSSetShadingRate(rate, nullptr);

        commandList_->EndQuery(queryHeap_.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 0);
        for (int draw = 0; draw < draws; ++draw) {
            commandList_->SetGraphicsRoot32BitConstant(0, seed_++, 0);
            commandList_->DrawInstanced(3, 1, 0, 0);
        }
        commandList_->EndQuery(queryHeap_.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 1);
        commandList_->ResolveQueryData(queryHeap_.Get(), D3D12_QUERY_TYPE_TIMESTAMP,
                                       0, 2, queryReadback_.Get(), 0);
        hr = commandList_->Close();
        if (FAILED(hr)) return hr;

        ID3D12CommandList* lists[] = {commandList_.Get()};
        queue_->ExecuteCommandLists(1, lists);
        hr = WaitForGpu();
        if (FAILED(hr)) return hr;

        uint64_t* stamps = nullptr;
        D3D12_RANGE readRange = {0, sizeof(uint64_t) * 2};
        hr = queryReadback_->Map(0, &readRange, reinterpret_cast<void**>(&stamps));
        if (FAILED(hr)) return hr;
        uint64_t delta = stamps[1] >= stamps[0] ? stamps[1] - stamps[0] : 0;
        D3D12_RANGE writeRange = {0, 0};
        queryReadback_->Unmap(0, &writeRange);
        *milliseconds = static_cast<double>(delta) * 1000.0 /
                        static_cast<double>(timestampFrequency_);
        return S_OK;
    }

    int CalibrateDraws() {
        int draws = 1;
        for (int attempt = 0; attempt < 8; ++attempt) {
            double ms = 0.0;
            if (FAILED(Measure(D3D12_SHADING_RATE_1X1, draws, &ms)) || ms <= 0.0) break;
            if (ms >= 2.5 || draws >= 128) break;
            int next = static_cast<int>(std::ceil(draws * (3.5 / ms)));
            draws = std::max(draws + 1, std::min(128, next));
        }
        return draws;
    }

    const std::wstring& AdapterName() const { return adapterName_; }
    UINT VendorId() const { return vendorId_; }
    UINT DeviceId() const { return deviceId_; }
    size_t DedicatedVideoMemory() const { return dedicatedVideoMemory_; }
    const D3D12_FEATURE_DATA_D3D12_OPTIONS6& VrsOptions() const { return options6_; }

private:
    HRESULT CreatePipeline() {
        std::vector<unsigned char> vertex = ReadBinary(ExeDirectory() + L"\\VrsBench.vs.cso");
        std::vector<unsigned char> pixel = ReadBinary(ExeDirectory() + L"\\VrsBench.ps.cso");
        if (vertex.empty() || pixel.empty()) return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);

        D3D12_ROOT_PARAMETER parameter = {};
        parameter.ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        parameter.Constants.ShaderRegister = 0;
        parameter.Constants.RegisterSpace = 0;
        parameter.Constants.Num32BitValues = 1;
        parameter.ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

        D3D12_ROOT_SIGNATURE_DESC rootDesc = {};
        rootDesc.NumParameters = 1;
        rootDesc.pParameters = &parameter;
        rootDesc.Flags = D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT;
        ComPtr<ID3DBlob> serialized;
        ComPtr<ID3DBlob> errors;
        HRESULT hr = D3D12SerializeRootSignature(&rootDesc, D3D_ROOT_SIGNATURE_VERSION_1,
                                                 &serialized, &errors);
        if (FAILED(hr)) return hr;
        hr = device_->CreateRootSignature(0, serialized->GetBufferPointer(),
                                          serialized->GetBufferSize(),
                                          IID_PPV_ARGS(&rootSignature_));
        if (FAILED(hr)) return hr;

        D3D12_GRAPHICS_PIPELINE_STATE_DESC pso = {};
        pso.pRootSignature = rootSignature_.Get();
        pso.VS = {vertex.data(), vertex.size()};
        pso.PS = {pixel.data(), pixel.size()};
        pso.BlendState.AlphaToCoverageEnable = FALSE;
        pso.BlendState.IndependentBlendEnable = FALSE;
        D3D12_RENDER_TARGET_BLEND_DESC targetBlend = {};
        targetBlend.BlendEnable = FALSE;
        targetBlend.LogicOpEnable = FALSE;
        targetBlend.SrcBlend = D3D12_BLEND_ONE;
        targetBlend.DestBlend = D3D12_BLEND_ZERO;
        targetBlend.BlendOp = D3D12_BLEND_OP_ADD;
        targetBlend.SrcBlendAlpha = D3D12_BLEND_ONE;
        targetBlend.DestBlendAlpha = D3D12_BLEND_ZERO;
        targetBlend.BlendOpAlpha = D3D12_BLEND_OP_ADD;
        targetBlend.LogicOp = D3D12_LOGIC_OP_NOOP;
        targetBlend.RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;
        pso.BlendState.RenderTarget[0] = targetBlend;
        pso.SampleMask = UINT_MAX;
        pso.RasterizerState.FillMode = D3D12_FILL_MODE_SOLID;
        pso.RasterizerState.CullMode = D3D12_CULL_MODE_NONE;
        pso.RasterizerState.FrontCounterClockwise = FALSE;
        pso.RasterizerState.DepthBias = D3D12_DEFAULT_DEPTH_BIAS;
        pso.RasterizerState.DepthBiasClamp = D3D12_DEFAULT_DEPTH_BIAS_CLAMP;
        pso.RasterizerState.SlopeScaledDepthBias = D3D12_DEFAULT_SLOPE_SCALED_DEPTH_BIAS;
        pso.RasterizerState.DepthClipEnable = TRUE;
        pso.RasterizerState.MultisampleEnable = FALSE;
        pso.RasterizerState.AntialiasedLineEnable = FALSE;
        pso.RasterizerState.ForcedSampleCount = 0;
        pso.RasterizerState.ConservativeRaster = D3D12_CONSERVATIVE_RASTERIZATION_MODE_OFF;
        pso.DepthStencilState.DepthEnable = FALSE;
        pso.DepthStencilState.StencilEnable = FALSE;
        pso.InputLayout = {nullptr, 0};
        pso.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
        pso.NumRenderTargets = 1;
        pso.RTVFormats[0] = DXGI_FORMAT_R8G8B8A8_UNORM;
        pso.SampleDesc.Count = 1;
        return device_->CreateGraphicsPipelineState(&pso, IID_PPV_ARGS(&pipeline_));
    }

    HRESULT CreateTargetsAndQueries() {
        D3D12_DESCRIPTOR_HEAP_DESC heapDesc = {};
        heapDesc.NumDescriptors = 1;
        heapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
        HRESULT hr = device_->CreateDescriptorHeap(&heapDesc, IID_PPV_ARGS(&rtvHeap_));
        if (FAILED(hr)) return hr;

        D3D12_HEAP_PROPERTIES defaultHeap = {};
        defaultHeap.Type = D3D12_HEAP_TYPE_DEFAULT;
        D3D12_RESOURCE_DESC targetDesc = {};
        targetDesc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        targetDesc.Width = static_cast<UINT64>(width_);
        targetDesc.Height = static_cast<UINT>(height_);
        targetDesc.DepthOrArraySize = 1;
        targetDesc.MipLevels = 1;
        targetDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        targetDesc.SampleDesc.Count = 1;
        targetDesc.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
        targetDesc.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
        D3D12_CLEAR_VALUE clearValue = {};
        clearValue.Format = targetDesc.Format;
        clearValue.Color[3] = 1.0f;
        hr = device_->CreateCommittedResource(&defaultHeap, D3D12_HEAP_FLAG_NONE, &targetDesc,
                                              D3D12_RESOURCE_STATE_RENDER_TARGET, &clearValue,
                                              IID_PPV_ARGS(&renderTarget_));
        if (FAILED(hr)) return hr;
        device_->CreateRenderTargetView(renderTarget_.Get(), nullptr,
                                        rtvHeap_->GetCPUDescriptorHandleForHeapStart());

        D3D12_QUERY_HEAP_DESC queryDesc = {};
        queryDesc.Count = 2;
        queryDesc.Type = D3D12_QUERY_HEAP_TYPE_TIMESTAMP;
        hr = device_->CreateQueryHeap(&queryDesc, IID_PPV_ARGS(&queryHeap_));
        if (FAILED(hr)) return hr;

        D3D12_HEAP_PROPERTIES readbackHeap = {};
        readbackHeap.Type = D3D12_HEAP_TYPE_READBACK;
        D3D12_RESOURCE_DESC bufferDesc = {};
        bufferDesc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        bufferDesc.Width = sizeof(uint64_t) * 2;
        bufferDesc.Height = 1;
        bufferDesc.DepthOrArraySize = 1;
        bufferDesc.MipLevels = 1;
        bufferDesc.SampleDesc.Count = 1;
        bufferDesc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        hr = device_->CreateCommittedResource(&readbackHeap, D3D12_HEAP_FLAG_NONE, &bufferDesc,
                                              D3D12_RESOURCE_STATE_COPY_DEST, nullptr,
                                              IID_PPV_ARGS(&queryReadback_));
        if (FAILED(hr)) return hr;

        viewport_.TopLeftX = 0.0f;
        viewport_.TopLeftY = 0.0f;
        viewport_.Width = static_cast<float>(width_);
        viewport_.Height = static_cast<float>(height_);
        viewport_.MinDepth = 0.0f;
        viewport_.MaxDepth = 1.0f;
        scissor_ = {0, 0, width_, height_};
        return S_OK;
    }

    HRESULT WaitForGpu() {
        const uint64_t value = ++fenceValue_;
        HRESULT hr = queue_->Signal(fence_.Get(), value);
        if (FAILED(hr)) return hr;
        if (fence_->GetCompletedValue() < value) {
            hr = fence_->SetEventOnCompletion(value, fenceEvent_);
            if (FAILED(hr)) return hr;
            WaitForSingleObject(fenceEvent_, INFINITE);
        }
        return device_->GetDeviceRemovedReason();
    }

    int width_ = 0;
    int height_ = 0;
    uint32_t seed_ = 1;
    uint64_t timestampFrequency_ = 0;
    uint64_t fenceValue_ = 0;
    HANDLE fenceEvent_ = nullptr;
    UINT vendorId_ = 0;
    UINT deviceId_ = 0;
    size_t dedicatedVideoMemory_ = 0;
    std::wstring adapterName_;
    D3D12_FEATURE_DATA_D3D12_OPTIONS6 options6_ = {};
    D3D12_VIEWPORT viewport_ = {};
    D3D12_RECT scissor_ = {};

    ComPtr<IDXGIFactory4> factory_;
    ComPtr<IDXGIAdapter1> adapter_;
    ComPtr<ID3D12Device> device_;
    ComPtr<ID3D12CommandQueue> queue_;
    ComPtr<ID3D12CommandAllocator> allocator_;
    ComPtr<ID3D12GraphicsCommandList> commandList_;
    ComPtr<ID3D12GraphicsCommandList5> commandList5_;
    ComPtr<ID3D12RootSignature> rootSignature_;
    ComPtr<ID3D12PipelineState> pipeline_;
    ComPtr<ID3D12DescriptorHeap> rtvHeap_;
    ComPtr<ID3D12Resource> renderTarget_;
    ComPtr<ID3D12QueryHeap> queryHeap_;
    ComPtr<ID3D12Resource> queryReadback_;
    ComPtr<ID3D12Fence> fence_;
};

std::string BuildReport(const Bench& bench, const Options& options, int draws,
                        const std::vector<Mode>& modes) {
    const double baseline = Percentile(modes[0].milliseconds, 0.5);
    std::ostringstream out;
    out.setf(std::ios::fixed);
    out.precision(3);
    out << "Pavise DX12 VRS 离屏台架报告\r\n"
        << "================================\r\n\r\n"
        << "结论: ";
    double best = baseline > 0.0 ? baseline / Percentile(modes[3].milliseconds, 0.5) : 0.0;
    if (best >= 1.10)
        out << "本机像素着色负载对 VRS 有明确响应。\r\n";
    else if (best >= 1.03)
        out << "本机像素着色负载对 VRS 有小幅响应。\r\n";
    else
        out << "本机未测到有意义的 VRS 加速。\r\n";
    out << "- 注意: 这是合成像素着色台架，不代表任何具体游戏的实际收益。\r\n"
        << "- 程序纯离屏运行，没有窗口、捕获、注入或游戏进程访问。\r\n\r\n"
        << "设备\r\n"
        << "- GPU: " << Utf8(bench.AdapterName()) << "\r\n"
        << "- PCI: VEN_" << std::hex << bench.VendorId() << " DEV_" << bench.DeviceId()
        << std::dec << "\r\n"
        << "- 专用显存: " << (bench.DedicatedVideoMemory() / (1024 * 1024)) << " MB\r\n"
        << "- VRS: " << TierText(bench.VrsOptions().VariableShadingRateTier) << "\r\n"
        << "- AdditionalShadingRates: "
        << (bench.VrsOptions().AdditionalShadingRatesSupported ? "yes" : "no") << "\r\n"
        << "- 渲染尺寸: " << options.width << "x" << options.height << "\r\n"
        << "- 每样本全屏绘制: " << draws << " 次\r\n"
        << "- 每档样本: " << modes[0].milliseconds.size() << "\r\n\r\n"
        << "结果（GPU时间）\r\n"
        << "模式    中位ms    平均ms    P95ms    相对1x1加速    理论像素着色量\r\n";
    for (const Mode& mode : modes) {
        double median = Percentile(mode.milliseconds, 0.5);
        double speedup = median > 0.0 ? baseline / median : 0.0;
        out << mode.name << "    " << median << "    " << Mean(mode.milliseconds)
            << "    " << Percentile(mode.milliseconds, 0.95) << "    " << speedup
            << "x    " << mode.theoreticalPixels * 100.0 << "%\r\n";
    }
    out << "\r\n判读边界\r\n"
        << "- 台架刻意使用重像素着色器，因此给出的是该类负载的硬件上限证据。\r\n"
        << "- 游戏若受 CPU、几何、光追、带宽、Shader 编译或资源加载限制，收益会显著更低。\r\n"
        << "- Tier 1 只能整次 draw 设置着色率；真正保护 HUD/准星并分区需要 Tier 2 或游戏语义。\r\n"
        << "- 该结果不能证明 DLL 注入对反作弊游戏安全。\r\n";
    return out.str();
}

std::string BuildCsv(const std::vector<Mode>& modes) {
    std::ostringstream out;
    out << "mode,sample,gpu_ms\r\n";
    for (const Mode& mode : modes) {
        for (size_t i = 0; i < mode.milliseconds.size(); ++i)
            out << mode.name << ',' << (i + 1) << ',' << mode.milliseconds[i] << "\r\n";
    }
    return out.str();
}

} // namespace

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    SetPriorityClass(GetCurrentProcess(), BELOW_NORMAL_PRIORITY_CLASS);
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);

    int argc = 0;
    wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    Options options;
    options.width = IntArg(argc, argv, L"--width", options.width, 640, 3840);
    options.height = IntArg(argc, argv, L"--height", options.height, 360, 2160);
    options.cycles = IntArg(argc, argv, L"--cycles", options.cycles, 4, 60);
    options.idleMs = IntArg(argc, argv, L"--idle-ms", options.idleMs, 0, 100);
    options.outputDir = StringArg(argc, argv, L"--output-dir", ExeDirectory() + L"\\results");
    if (argv) LocalFree(argv);
    EnsureDirectory(options.outputDir);

    std::wstring stamp = Timestamp();
    std::wstring prefix = options.outputDir + L"\\Pavise-VrsBench-" + stamp;
    std::wstring errorPath = prefix + L"-error.txt";

    Bench bench;
    HRESULT hr = bench.Initialize(options.width, options.height);
    if (hr == S_FALSE) {
        WriteUtf8(errorPath, "VRS_UNSUPPORTED\r\nThe selected physical GPU reports no DX12 Variable Rate Shading support.\r\n");
        return 3;
    }
    if (FAILED(hr)) {
        WriteUtf8(errorPath, "INITIALIZE_FAILED " + HrText(hr) + "\r\n");
        return 2;
    }

    int draws = bench.CalibrateDraws();
    std::vector<Mode> modes = {
        {"1x1", D3D12_SHADING_RATE_1X1, 1.00, {}},
        {"1x2", D3D12_SHADING_RATE_1X2, 0.50, {}},
        {"2x1", D3D12_SHADING_RATE_2X1, 0.50, {}},
        {"2x2", D3D12_SHADING_RATE_2X2, 0.25, {}},
    };

    // 采配对样本之前 先把着色器和频率状态都热起来
    for (int warmup = 0; warmup < 8; ++warmup) {
        double ignored = 0.0;
        hr = bench.Measure(modes[warmup % 4].rate, draws, &ignored);
        if (FAILED(hr)) {
            WriteUtf8(errorPath, "WARMUP_FAILED " + HrText(hr) + "\r\n");
            return 4;
        }
        if (options.idleMs) Sleep(static_cast<DWORD>(options.idleMs));
    }

    // 镜像顺序能减少温度和频率漂移带来的偏差 又不至于把桌面 GPU 跑满
    const int order[] = {0, 1, 3, 2, 2, 3, 1, 0};
    for (int cycle = 0; cycle < options.cycles; ++cycle) {
        for (int index : order) {
            double ms = 0.0;
            hr = bench.Measure(modes[index].rate, draws, &ms);
            if (FAILED(hr) || !std::isfinite(ms) || ms <= 0.0) {
                WriteUtf8(errorPath, "MEASURE_FAILED " + HrText(hr) + "\r\n");
                return 5;
            }
            modes[index].milliseconds.push_back(ms);
            if (options.idleMs) Sleep(static_cast<DWORD>(options.idleMs));
        }
    }

    WriteUtf8(prefix + L".csv", BuildCsv(modes));
    WriteUtf8(prefix + L".txt", BuildReport(bench, options, draws, modes));
    return 0;
}
