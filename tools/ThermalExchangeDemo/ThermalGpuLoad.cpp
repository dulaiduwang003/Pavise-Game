// File purpose Off-screen D3D9 load, used only to validate the Thermal Exchange experiment

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d9.h>
#include <shellapi.h>

#include <atomic>
#include <cstdint>
#include <cwchar>
#include <string>
#include <thread>
#include <vector>

#ifdef _MSC_VER
#pragma comment(lib, "d3d9.lib")
#pragma comment(lib, "shell32.lib")
#endif

namespace {

std::atomic<bool> g_stop(false);

LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == WM_CLOSE || message == WM_DESTROY) {
        g_stop.store(true);
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(window, message, wparam, lparam);
}

BOOL WINAPI ConsoleHandler(DWORD code) {
    if (code == CTRL_C_EVENT || code == CTRL_BREAK_EVENT || code == CTRL_CLOSE_EVENT ||
        code == CTRL_LOGOFF_EVENT || code == CTRL_SHUTDOWN_EVENT) {
        g_stop.store(true);
        return TRUE;
    }
    return FALSE;
}

struct Vertex {
    float x, y, z, rhw;
    DWORD color;
};

constexpr DWORD kFvf = D3DFVF_XYZRHW | D3DFVF_DIFFUSE;
constexpr DWORD kSharedFrameMagic = 0x46585450; // PTXF
constexpr DWORD kSharedFrameVersion = 1;
constexpr DWORD kSharedFrameCapacity = 262144;

struct SharedFrameData {
    DWORD magic, version, capacity, reserved;
    volatile LONGLONG count;
    LONGLONG frequency;
    LONGLONG stamps[kSharedFrameCapacity];
};

int IntegerArg(int argc, wchar_t** argv, const wchar_t* name, int fallback, int low, int high) {
    for (int i = 1; i + 1 < argc; ++i) {
        if (_wcsicmp(argv[i], name) != 0) continue;
        wchar_t* end = nullptr;
        long value = wcstol(argv[i + 1], &end, 10);
        if (end != argv[i + 1] && end && !*end && value >= low && value <= high)
            return static_cast<int>(value);
    }
    return fallback;
}

void CpuWorker(int slot) {
    uint64_t state = 0x9E3779B97F4A7C15ULL ^ static_cast<uint64_t>(slot + 1);
    while (!g_stop.load(std::memory_order_relaxed)) {
        for (int i = 0; i < 200000; ++i) {
            state ^= state >> 12;
            state ^= state << 25;
            state ^= state >> 27;
            state *= 0x2545F4914F6CDD1DULL;
        }
        Sleep(1);
    }
    if (state == 1) OutputDebugStringW(L"unreachable");
}

} // namespace

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int) {
    SetConsoleCtrlHandler(ConsoleHandler, TRUE);
    int argc = 0;
    wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    int seconds = IntegerArg(argc, argv, L"--seconds", 150, 20, 3600);
    int passes = IntegerArg(argc, argv, L"--passes", 192, 8, 1024);
    int cpuThreads = IntegerArg(argc, argv, L"--cpu-threads", 2, 0, 32);
    if (argv) LocalFree(argv);

    const wchar_t* className = L"PaviseThermalGpuLoad";
    WNDCLASSW cls = {};
    cls.lpfnWndProc = WindowProc;
    cls.hInstance = instance;
    cls.lpszClassName = className;
    if (!RegisterClassW(&cls) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return 2;

    constexpr int width = 2560;
    constexpr int height = 1440;
    HWND window = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, className,
                                  L"Pavise Thermal GPU Load", WS_POPUP,
                                  -32000, -32000, width, height, nullptr, nullptr, instance, nullptr);
    if (!window) return 3;
    ShowWindow(window, SW_SHOWNOACTIVATE);
    ShowWindow(window, SW_SHOWNOACTIVATE); // Override a hidden STARTUPINFO while keeping the window off-screen
    SetWindowPos(window, HWND_BOTTOM, -32000, -32000, width, height,
                 SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING);

    IDirect3D9* d3d = Direct3DCreate9(D3D_SDK_VERSION);
    if (!d3d) { DestroyWindow(window); return 4; }
    D3DPRESENT_PARAMETERS pp = {};
    pp.BackBufferWidth = width;
    pp.BackBufferHeight = height;
    pp.BackBufferFormat = D3DFMT_X8R8G8B8;
    pp.BackBufferCount = 1;
    pp.MultiSampleType = D3DMULTISAMPLE_NONE;
    pp.SwapEffect = D3DSWAPEFFECT_DISCARD;
    pp.hDeviceWindow = window;
    pp.Windowed = TRUE;
    pp.PresentationInterval = D3DPRESENT_INTERVAL_IMMEDIATE;

    IDirect3DDevice9* device = nullptr;
    HRESULT hr = d3d->CreateDevice(D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL, window,
                                   D3DCREATE_HARDWARE_VERTEXPROCESSING | D3DCREATE_FPU_PRESERVE,
                                   &pp, &device);
    if (FAILED(hr)) {
        hr = d3d->CreateDevice(D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL, window,
                               D3DCREATE_SOFTWARE_VERTEXPROCESSING | D3DCREATE_FPU_PRESERVE,
                               &pp, &device);
    }
    if (FAILED(hr) || !device) { d3d->Release(); DestroyWindow(window); return 5; }

    std::wstring mappingName = L"Local\\PaviseThermalGpuLoad_" + std::to_wstring(GetCurrentProcessId());
    HANDLE mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0,
                                        sizeof(SharedFrameData), mappingName.c_str());
    auto* shared = mapping ? static_cast<SharedFrameData*>(MapViewOfFile(
                                mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(SharedFrameData))) : nullptr;
    if (!shared) {
        if (mapping) CloseHandle(mapping);
        device->Release(); d3d->Release(); DestroyWindow(window);
        return 7;
    }
    ZeroMemory(shared, sizeof(SharedFrameData));
    shared->magic = kSharedFrameMagic;
    shared->version = kSharedFrameVersion;
    shared->capacity = kSharedFrameCapacity;
    LARGE_INTEGER sharedFrequency = {};
    QueryPerformanceFrequency(&sharedFrequency);
    shared->frequency = sharedFrequency.QuadPart;

    device->SetRenderState(D3DRS_ZENABLE, FALSE);
    device->SetRenderState(D3DRS_CULLMODE, D3DCULL_NONE);
    device->SetRenderState(D3DRS_ALPHABLENDENABLE, TRUE);
    device->SetRenderState(D3DRS_SRCBLEND, D3DBLEND_SRCALPHA);
    device->SetRenderState(D3DRS_DESTBLEND, D3DBLEND_INVSRCALPHA);
    device->SetFVF(kFvf);

    std::vector<std::thread> workers;
    for (int i = 0; i < cpuThreads; ++i) workers.emplace_back(CpuWorker, i);

    LARGE_INTEGER frequency = {}, start = {}, now = {};
    QueryPerformanceFrequency(&frequency);
    QueryPerformanceCounter(&start);
    uint64_t frames = 0;
    MSG message = {};
    while (!g_stop.load()) {
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        QueryPerformanceCounter(&now);
        if ((now.QuadPart - start.QuadPart) / frequency.QuadPart >= seconds) break;

        device->Clear(0, nullptr, D3DCLEAR_TARGET, 0xFF05070B, 1.0f, 0);
        if (SUCCEEDED(device->BeginScene())) {
            for (int pass = 0; pass < passes; ++pass) {
                DWORD red = static_cast<DWORD>((pass * 17 + frames) & 0xFF);
                DWORD green = static_cast<DWORD>((pass * 31 + frames * 3) & 0xFF);
                DWORD blue = static_cast<DWORD>((pass * 47 + frames * 7) & 0xFF);
                DWORD color = 0x18000000 | (red << 16) | (green << 8) | blue;
                float inset = static_cast<float>(pass & 15);
                Vertex quad[4] = {
                    {-0.5f + inset, -0.5f + inset, 0.0f, 1.0f, color},
                    {width - 0.5f - inset, -0.5f + inset, 0.0f, 1.0f, color},
                    {-0.5f + inset, height - 0.5f - inset, 0.0f, 1.0f, color},
                    {width - 0.5f - inset, height - 0.5f - inset, 0.0f, 1.0f, color}
                };
                device->DrawPrimitiveUP(D3DPT_TRIANGLESTRIP, 2, quad, sizeof(Vertex));
            }
            device->EndScene();
        }
        hr = device->Present(nullptr, nullptr, nullptr, nullptr);
        if (hr == D3DERR_DEVICELOST) {
            Sleep(50);
            if (device->TestCooperativeLevel() == D3DERR_DEVICENOTRESET) device->Reset(&pp);
        }
        LARGE_INTEGER stamp = {};
        QueryPerformanceCounter(&stamp);
        LONGLONG index = shared->count;
        if (index >= 0 && index < kSharedFrameCapacity) {
            shared->stamps[index] = stamp.QuadPart;
            MemoryBarrier();
            InterlockedExchange64(&shared->count, index + 1);
        }
        ++frames;
    }

    g_stop.store(true);
    for (auto& worker : workers) worker.join();
    UnmapViewOfFile(shared);
    CloseHandle(mapping);
    device->Release();
    d3d->Release();
    DestroyWindow(window);
    return frames ? 0 : 6;
}
