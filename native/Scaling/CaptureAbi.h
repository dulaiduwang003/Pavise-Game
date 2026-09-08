// Windows Graphics Capture ABI declarations (Windows SDK contracts).
// No code from Magpie or other scaling applications is used by this component.
#pragma once
#include <windows.h>
#include <inspectable.h>
#include <d3d11.h>
#include <dxgi1_3.h>
#include <d3dcompiler.h>
#include <dwmapi.h>
#include <string>
#include <stdexcept>
#include <utility>

inline GUID Guid(const wchar_t* text) { GUID id{}; CLSIDFromString(text, &id); return id; }
inline void Check(HRESULT hr, const char* stage) {
    if (FAILED(hr)) { char message[160]; snprintf(message, sizeof(message), "%s:0x%08lX", stage, (unsigned long)hr); throw std::runtime_error(message); }
}
template<class T> class Com {
    T* p = nullptr;
public:
    Com() = default;
    ~Com() { reset(); }
    Com(const Com&) = delete;
    Com& operator=(const Com&) = delete;
    Com(Com&& x) noexcept : p(x.p) { x.p = nullptr; }
    Com& operator=(Com&& x) noexcept { if (this != &x) { reset(); p=x.p; x.p=nullptr; } return *this; }
    T* get() const { return p; }
    T* operator->() const { return p; }
    explicit operator bool() const { return p != nullptr; }
    T** put() { reset(); return &p; }
    void reset() { if (p) { p->Release(); p=nullptr; } }
};
struct CaptureSize { INT32 Width, Height; };
struct Closable : IInspectable { virtual HRESULT STDMETHODCALLTYPE Close() = 0; };
inline void CloseObject(IUnknown* value) {
    if (!value) return;
    Com<Closable> close;
    if (SUCCEEDED(value->QueryInterface(Guid(L"{30D5A829-7FA4-4026-83BB-D75BAE4EA99E}"), (void**)close.put()))) close->Close();
}
struct CaptureItem : IInspectable {
    virtual HRESULT STDMETHODCALLTYPE get_DisplayName(HSTRING*) = 0;
    virtual HRESULT STDMETHODCALLTYPE get_Size(CaptureSize*) = 0;
    virtual HRESULT STDMETHODCALLTYPE add_Closed(IUnknown*, INT64*) = 0;
    virtual HRESULT STDMETHODCALLTYPE remove_Closed(INT64) = 0;
};
struct CaptureItemInterop : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE CreateForWindow(HWND, REFIID, void**) = 0;
    virtual HRESULT STDMETHODCALLTYPE CreateForMonitor(HMONITOR, REFIID, void**) = 0;
};
struct CaptureFrame : IInspectable {
    virtual HRESULT STDMETHODCALLTYPE get_Surface(IInspectable**) = 0;
    virtual HRESULT STDMETHODCALLTYPE get_SystemRelativeTime(INT64*) = 0;
    virtual HRESULT STDMETHODCALLTYPE get_ContentSize(CaptureSize*) = 0;
};
struct CaptureSession : IInspectable { virtual HRESULT STDMETHODCALLTYPE StartCapture() = 0; };
struct CaptureSessionCursor : IInspectable {
    virtual HRESULT STDMETHODCALLTYPE get_IsCursorCaptureEnabled(BYTE*) = 0;
    virtual HRESULT STDMETHODCALLTYPE put_IsCursorCaptureEnabled(BYTE) = 0;
};
struct CaptureSessionInterval : IInspectable {
    virtual HRESULT STDMETHODCALLTYPE get_MinUpdateInterval(INT64*) = 0;
    virtual HRESULT STDMETHODCALLTYPE put_MinUpdateInterval(INT64) = 0;
};
struct CaptureFramePool : IInspectable {
    virtual HRESULT STDMETHODCALLTYPE Recreate(IInspectable*, INT32, INT32, CaptureSize) = 0;
    virtual HRESULT STDMETHODCALLTYPE TryGetNextFrame(CaptureFrame**) = 0;
    virtual HRESULT STDMETHODCALLTYPE add_FrameArrived(IUnknown*, INT64*) = 0;
    virtual HRESULT STDMETHODCALLTYPE remove_FrameArrived(INT64) = 0;
    virtual HRESULT STDMETHODCALLTYPE CreateCaptureSession(CaptureItem*, CaptureSession**) = 0;
    virtual HRESULT STDMETHODCALLTYPE get_DispatcherQueue(IInspectable**) = 0;
};
struct CapturePoolFactory : IInspectable {
    virtual HRESULT STDMETHODCALLTYPE CreateFreeThreaded(IInspectable*, INT32, INT32, CaptureSize, CaptureFramePool**) = 0;
};
struct DxgiAccess : IUnknown { virtual HRESULT STDMETHODCALLTYPE GetInterface(REFIID, void**) = 0; };
struct FrameHandler : IUnknown { virtual HRESULT STDMETHODCALLTYPE Invoke(IInspectable*, IInspectable*) = 0; };

// The callback only signals an event. All capture consumption / D3D work stays
// on the owner thread; closing a pool never waits for that thread in a callback.
class FrameSignal final : public FrameHandler {
    LONG refs = 1;
    HANDLE event;
public:
    explicit FrameSignal(HANDLE e) {
        if (!DuplicateHandle(GetCurrentProcess(), e, GetCurrentProcess(), &event, 0, FALSE, DUPLICATE_SAME_ACCESS))
            throw std::runtime_error("frame-event");
    }
    ~FrameSignal() { CloseHandle(event); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override {
        if (!value) return E_POINTER;
        *value = nullptr;
        if (iid == IID_IUnknown || iid == Guid(L"{51A947F7-79CF-5A3E-A3A5-1289CFA6DFE8}") || iid == Guid(L"{94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90}")) {
            *value=static_cast<FrameHandler*>(this); AddRef(); return S_OK;
        }
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&refs); }
    ULONG STDMETHODCALLTYPE Release() override { LONG n=InterlockedDecrement(&refs); if (!n) delete this; return n; }
    HRESULT STDMETHODCALLTYPE Invoke(IInspectable*, IInspectable*) override { SetEvent(event); return S_OK; }
};

class WinRt {
    HMODULE module = nullptr;
    using Init = HRESULT(WINAPI*)(UINT32);
    using Uninit = void(WINAPI*)();
    using CreateString = HRESULT(WINAPI*)(PCNZWCH, UINT32, HSTRING*);
    using DeleteString = HRESULT(WINAPI*)(HSTRING);
    using Factory = HRESULT(WINAPI*)(HSTRING, REFIID, void**);
    Uninit uninit = nullptr;
    CreateString createString = nullptr;
    DeleteString deleteString = nullptr;
    Factory factory = nullptr;
public:
    WinRt() {
        module = LoadLibraryExW(L"combase.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!module) throw std::runtime_error("windows-capture-unavailable");
        auto init=(Init)GetProcAddress(module,"RoInitialize");
        uninit=(Uninit)GetProcAddress(module,"RoUninitialize");
        createString=(CreateString)GetProcAddress(module,"WindowsCreateString");
        deleteString=(DeleteString)GetProcAddress(module,"WindowsDeleteString");
        factory=(Factory)GetProcAddress(module,"RoGetActivationFactory");
        if (!init || !uninit || !createString || !deleteString || !factory) throw std::runtime_error("winrt-api");
        Check(init(1), "winrt-init");
    }
    ~WinRt() { if (uninit) uninit(); if (module) FreeLibrary(module); }
    template<class T> void Get(const wchar_t* name, const wchar_t* iid, Com<T>& result) {
        HSTRING text{}; Check(createString(name, (UINT32)wcslen(name), &text), "winrt-string");
        HRESULT hr=factory(text, Guid(iid), (void**)result.put()); deleteString(text); Check(hr,"capture-factory");
    }
};
