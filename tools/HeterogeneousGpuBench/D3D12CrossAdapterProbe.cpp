// File purpose Windowless D3D12 cross-adapter capability probe for the Pavise heterogeneous GPU bench

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>

#include <algorithm>
#include <cstdint>
#include <iostream>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>

#ifdef _MSC_VER
#pragma comment(lib, "d3d12.lib")
#pragma comment(lib, "dxgi.lib")
#endif

using Microsoft::WRL::ComPtr;

namespace {

struct HrError : std::runtime_error {
    HrError(HRESULT hr, const char* operation) : std::runtime_error([&] {
        std::ostringstream text;
        text << operation << " failed, HRESULT=0x" << std::hex << std::uppercase
             << static_cast<unsigned long>(hr);
        return text.str();
    }()) {}
};

void Check(HRESULT hr, const char* operation) {
    if (FAILED(hr)) throw HrError(hr, operation);
}

struct Adapter {
    UINT index = 0;
    DXGI_ADAPTER_DESC1 desc = {};
    ComPtr<IDXGIAdapter1> value;
};

std::string Narrow(const wchar_t* value) {
    int size = WideCharToMultiByte(CP_UTF8, 0, value, -1, nullptr, 0, nullptr, nullptr);
    std::string result(size > 0 ? static_cast<size_t>(size - 1) : 0, '\0');
    if (size > 1) WideCharToMultiByte(CP_UTF8, 0, value, -1, result.data(), size, nullptr, nullptr);
    return result;
}

std::vector<Adapter> Enumerate() {
    ComPtr<IDXGIFactory1> factory;
    Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)), "CreateDXGIFactory1");
    std::vector<Adapter> result;
    for (UINT index = 0;; ++index) {
        ComPtr<IDXGIAdapter1> value;
        HRESULT hr = factory->EnumAdapters1(index, &value);
        if (hr == DXGI_ERROR_NOT_FOUND) break;
        Check(hr, "EnumAdapters1");
        Adapter item;
        item.index = index;
        item.value = value;
        Check(value->GetDesc1(&item.desc), "GetDesc1");
        if (!(item.desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE)) result.push_back(std::move(item));
    }
    return result;
}

UINT ParseIndex(int argc, wchar_t** argv, const wchar_t* name, UINT fallback) {
    for (int i = 1; i + 1 < argc; ++i) {
        if (std::wstring(argv[i]) != name) continue;
        wchar_t* end = nullptr;
        unsigned long value = wcstoul(argv[i + 1], &end, 10);
        if (!end || *end || value > 64) throw std::runtime_error("invalid adapter index");
        return static_cast<UINT>(value);
    }
    return fallback;
}

Adapter Find(const std::vector<Adapter>& adapters, UINT index) {
    for (const auto& adapter : adapters) if (adapter.index == index) return adapter;
    throw std::runtime_error("adapter index was not found");
}

D3D12_RESOURCE_DESC BufferDesc(UINT64 bytes, D3D12_RESOURCE_FLAGS flags) {
    D3D12_RESOURCE_DESC desc = {};
    desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    desc.Alignment = 0;
    desc.Width = bytes;
    desc.Height = 1;
    desc.DepthOrArraySize = 1;
    desc.MipLevels = 1;
    desc.Format = DXGI_FORMAT_UNKNOWN;
    desc.SampleDesc.Count = 1;
    desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    desc.Flags = flags;
    return desc;
}

struct SharedBufferResult {
    UINT64 bytes = 0;
    UINT64 heapBytes = 0;
    D3D12_RESOURCE_FLAGS flags = D3D12_RESOURCE_FLAG_NONE;
};

SharedBufferResult ProbeSharedBuffer(ID3D12Device* primary, ID3D12Device* secondary,
                                     UINT64 bytes, bool unorderedAccess) {
    D3D12_RESOURCE_FLAGS flags = static_cast<D3D12_RESOURCE_FLAGS>(
        D3D12_RESOURCE_FLAG_ALLOW_CROSS_ADAPTER |
        (unorderedAccess ? D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS : 0));
    const auto resourceDesc = BufferDesc(bytes, flags);
    const auto primaryInfo = primary->GetResourceAllocationInfo(0, 1, &resourceDesc);
    const auto secondaryInfo = secondary->GetResourceAllocationInfo(0, 1, &resourceDesc);
    if (primaryInfo.SizeInBytes == UINT64_MAX || secondaryInfo.SizeInBytes == UINT64_MAX) {
        throw std::runtime_error("GetResourceAllocationInfo rejected the cross-adapter buffer");
    }

    D3D12_HEAP_DESC heapDesc = {};
    heapDesc.SizeInBytes = std::max(primaryInfo.SizeInBytes, secondaryInfo.SizeInBytes);
    heapDesc.Alignment = std::max(primaryInfo.Alignment, secondaryInfo.Alignment);
    heapDesc.Properties.Type = D3D12_HEAP_TYPE_DEFAULT;
    heapDesc.Properties.CreationNodeMask = 1;
    heapDesc.Properties.VisibleNodeMask = 1;
    heapDesc.Flags = static_cast<D3D12_HEAP_FLAGS>(D3D12_HEAP_FLAG_SHARED |
                                                   D3D12_HEAP_FLAG_SHARED_CROSS_ADAPTER);

    ComPtr<ID3D12Heap> primaryHeap;
    Check(primary->CreateHeap(&heapDesc, IID_PPV_ARGS(&primaryHeap)), "CreateHeap(primary)");
    ComPtr<ID3D12Resource> primaryResource;
    Check(primary->CreatePlacedResource(primaryHeap.Get(), 0, &resourceDesc,
                                        D3D12_RESOURCE_STATE_COMMON, nullptr,
                                        IID_PPV_ARGS(&primaryResource)),
          unorderedAccess ? "CreatePlacedResource(primary,UAV)" : "CreatePlacedResource(primary)");

    HANDLE heapHandle = nullptr;
    Check(primary->CreateSharedHandle(primaryHeap.Get(), nullptr, GENERIC_ALL, nullptr, &heapHandle),
          "CreateSharedHandle(heap)");
    ComPtr<ID3D12Heap> secondaryHeap;
    HRESULT openHr = secondary->OpenSharedHandle(heapHandle, IID_PPV_ARGS(&secondaryHeap));
    CloseHandle(heapHandle);
    Check(openHr, "OpenSharedHandle(heap,secondary)");
    ComPtr<ID3D12Resource> secondaryResource;
    Check(secondary->CreatePlacedResource(secondaryHeap.Get(), 0, &resourceDesc,
                                          D3D12_RESOURCE_STATE_COMMON, nullptr,
                                          IID_PPV_ARGS(&secondaryResource)),
          unorderedAccess ? "CreatePlacedResource(secondary,UAV)" : "CreatePlacedResource(secondary)");
    return {bytes, heapDesc.SizeInBytes, flags};
}

void ProbeFence(ID3D12Device* primary, ID3D12Device* secondary) {
    ComPtr<ID3D12Fence> primaryFence;
    const auto flags = static_cast<D3D12_FENCE_FLAGS>(D3D12_FENCE_FLAG_SHARED |
                                                      D3D12_FENCE_FLAG_SHARED_CROSS_ADAPTER);
    Check(primary->CreateFence(0, flags, IID_PPV_ARGS(&primaryFence)), "CreateFence(cross-adapter)");
    HANDLE handle = nullptr;
    Check(primary->CreateSharedHandle(primaryFence.Get(), nullptr, GENERIC_ALL, nullptr, &handle),
          "CreateSharedHandle(fence)");
    ComPtr<ID3D12Fence> secondaryFence;
    HRESULT hr = secondary->OpenSharedHandle(handle, IID_PPV_ARGS(&secondaryFence));
    CloseHandle(handle);
    Check(hr, "OpenSharedHandle(fence,secondary)");
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
    try {
        const UINT primaryIndex = ParseIndex(argc, argv, L"--primary-index", 0);
        const UINT secondaryIndex = ParseIndex(argc, argv, L"--secondary-index", 1);
        const auto adapters = Enumerate();
        const auto primaryAdapter = Find(adapters, primaryIndex);
        const auto secondaryAdapter = Find(adapters, secondaryIndex);
        std::cout << "primary=[" << primaryIndex << "] " << Narrow(primaryAdapter.desc.Description) << "\n"
                  << "secondary=[" << secondaryIndex << "] " << Narrow(secondaryAdapter.desc.Description) << "\n";

        ComPtr<ID3D12Device> primary;
        ComPtr<ID3D12Device> secondary;
        Check(D3D12CreateDevice(primaryAdapter.value.Get(), D3D_FEATURE_LEVEL_11_0,
                                IID_PPV_ARGS(&primary)), "D3D12CreateDevice(primary)");
        Check(D3D12CreateDevice(secondaryAdapter.value.Get(), D3D_FEATURE_LEVEL_11_0,
                                IID_PPV_ARGS(&secondary)), "D3D12CreateDevice(secondary)");

        D3D12_FEATURE_DATA_D3D12_OPTIONS primaryOptions = {};
        D3D12_FEATURE_DATA_D3D12_OPTIONS secondaryOptions = {};
        Check(primary->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS, &primaryOptions,
                                           sizeof(primaryOptions)), "CheckFeatureSupport(primary)");
        Check(secondary->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS, &secondaryOptions,
                                             sizeof(secondaryOptions)), "CheckFeatureSupport(secondary)");
        std::cout << "primary_cross_node_tier=" << static_cast<int>(primaryOptions.CrossNodeSharingTier)
                  << "\nsecondary_cross_node_tier=" << static_cast<int>(secondaryOptions.CrossNodeSharingTier)
                  << "\n";

        ProbeFence(primary.Get(), secondary.Get());
        const auto plain = ProbeSharedBuffer(primary.Get(), secondary.Get(), 4ull * 1024 * 1024, false);
        bool uavSupported = false;
        std::string uavFailure;
        try {
            ProbeSharedBuffer(primary.Get(), secondary.Get(), 4ull * 1024 * 1024, true);
            uavSupported = true;
        } catch (const std::exception& error) {
            uavFailure = error.what();
        }
        std::cout << "cross_adapter_fence=pass\n"
                  << "cross_adapter_buffer=pass bytes=" << plain.bytes << " heap_bytes=" << plain.heapBytes << "\n"
                  << "cross_adapter_uav_buffer=" << (uavSupported ? "pass" : "unsupported")
                  << (uavFailure.empty() ? "" : " reason=\"" + uavFailure + "\"") << "\n"
                  << "probe=pass (no command queue, draw, dispatch, or window was created)\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "probe=fail " << error.what() << "\n";
        return 1;
    }
}
