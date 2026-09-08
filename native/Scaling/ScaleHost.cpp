// Pavise window scaler: an independent Windows Graphics Capture / D3D11 host.
// The target process is queried for identity only. No injection, game hooks,
// memory access, driver writes, downloads or third-party scaling runtime.
#define NOMINMAX
#include "CaptureAbi.h"
#include "ScaleMath.h"
#include "ScaleShader.h"
#include <shellapi.h>
#include <windowsx.h>
#include <cstdio>
#include <vector>
#include <cstring>
#include <memory>

struct Handle {
    HANDLE value=nullptr;
    explicit Handle(HANDLE h=nullptr):value(h){}
    ~Handle(){if(value && value!=INVALID_HANDLE_VALUE) CloseHandle(value);}
    Handle(const Handle&)=delete;
    Handle& operator=(const Handle&)=delete;
};
static void Status(const char* text) { printf("%s\n",text); fflush(stdout); }
static UINT64 Creation(HANDLE h) {
    FILETIME c{},e{},k{},u{};
    if(!GetProcessTimes(h,&c,&e,&k,&u)) return 0;
    return (UINT64(c.dwHighDateTime)<<32)|c.dwLowDateTime;
}
static bool OwnWindow(HWND hwnd,DWORD pid) {
    DWORD actual=0; GetWindowThreadProcessId(hwnd,&actual); return IsWindow(hwnd) && actual==pid;
}
static RECT ClientScreen(HWND hwnd) {
    RECT r{}; GetClientRect(hwnd,&r); MapWindowPoints(hwnd,nullptr,(POINT*)&r,2); return r;
}
static bool SameRect(const RECT& a,const RECT& b) {return EqualRect(&a,&b)!=FALSE;}

class Graphics {
public:
    Com<ID3D11Device> device;
    Com<ID3D11DeviceContext> context;
    Com<IDXGISwapChain2> swap;
    Com<ID3D11Texture2D> input;
    Com<ID3D11ShaderResourceView> inputView;
    Com<ID3D11RenderTargetView> target;
    Com<ID3D11VertexShader> vs;
    Com<ID3D11PixelShader> ps;
    Com<ID3D11SamplerState> sampler;
    Com<ID3D11Buffer> constants;
    HANDLE latency=nullptr;
    UINT inputW=0,inputH=0;
    ~Graphics(){if(context) {context->ClearState(); context->Flush();} if(latency) CloseHandle(latency);}

    void Initialize(bool software=false) {
        D3D_FEATURE_LEVEL requested[]={D3D_FEATURE_LEVEL_11_0}, actual{};
        Check(D3D11CreateDevice(nullptr,software?D3D_DRIVER_TYPE_WARP:D3D_DRIVER_TYPE_HARDWARE,nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,requested,1,D3D11_SDK_VERSION,device.put(),&actual,context.put()),"d3d11-device");
        Com<ID3DBlob> vb,pb,errors;
        Check(D3DCompile(ScaleShader,strlen(ScaleShader),nullptr,nullptr,nullptr,"VS","vs_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,vb.put(),errors.put()),"vertex-compile");
        Check(D3DCompile(ScaleShader,strlen(ScaleShader),nullptr,nullptr,nullptr,"PS","ps_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,pb.put(),errors.put()),"pixel-compile");
        Check(device->CreateVertexShader(vb->GetBufferPointer(),vb->GetBufferSize(),nullptr,vs.put()),"vertex-shader");
        Check(device->CreatePixelShader(pb->GetBufferPointer(),pb->GetBufferSize(),nullptr,ps.put()),"pixel-shader");
        D3D11_SAMPLER_DESC sd{}; sd.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sd.AddressU=sd.AddressV=sd.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP; sd.MaxLOD=D3D11_FLOAT32_MAX;
        Check(device->CreateSamplerState(&sd,sampler.put()),"sampler");
        D3D11_BUFFER_DESC bd{}; bd.ByteWidth=32; bd.Usage=D3D11_USAGE_DEFAULT; bd.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
        Check(device->CreateBuffer(&bd,nullptr,constants.put()),"constants");
    }
    void CreateSwap(HWND hwnd,UINT width,UINT height) {
        Com<IDXGIDevice> dxgi; Com<IDXGIAdapter> adapter; Com<IDXGIFactory2> factory;
        Check(device->QueryInterface(__uuidof(IDXGIDevice),(void**)dxgi.put()),"dxgi-device");
        Check(dxgi->GetAdapter(adapter.put()),"dxgi-adapter");
        Check(adapter->GetParent(__uuidof(IDXGIFactory2),(void**)factory.put()),"dxgi-factory");
        DXGI_SWAP_CHAIN_DESC1 d{}; d.Width=width; d.Height=height; d.Format=DXGI_FORMAT_B8G8R8A8_UNORM;
        d.SampleDesc.Count=1; d.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT; d.BufferCount=2;
        d.SwapEffect=DXGI_SWAP_EFFECT_FLIP_DISCARD; d.Scaling=DXGI_SCALING_STRETCH;
        d.Flags=DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
        Com<IDXGISwapChain1> base;
        Check(factory->CreateSwapChainForHwnd(device.get(),hwnd,&d,nullptr,nullptr,base.put()),"swapchain");
        Check(base->QueryInterface(__uuidof(IDXGISwapChain2),(void**)swap.put()),"swapchain2");
        Check(swap->SetMaximumFrameLatency(1),"frame-latency");
        latency=swap->GetFrameLatencyWaitableObject();
        if(!latency) throw std::runtime_error("frame-latency-handle");
        factory->MakeWindowAssociation(hwnd,DXGI_MWA_NO_ALT_ENTER);
        CreateTarget();
    }
    void CreateTarget() {
        Com<ID3D11Texture2D> back;
        Check(swap->GetBuffer(0,__uuidof(ID3D11Texture2D),(void**)back.put()),"swap-buffer");
        Check(device->CreateRenderTargetView(back.get(),nullptr,target.put()),"swap-target");
    }
    void Resize(UINT width,UINT height) {
        context->OMSetRenderTargets(0,nullptr,nullptr); target.reset();
        Check(swap->ResizeBuffers(0,width,height,DXGI_FORMAT_UNKNOWN,DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT),"swap-resize");
        CreateTarget();
    }
    void CopyInput(ID3D11Texture2D* texture,const D3D11_BOX& box) {
        UINT width=box.right-box.left,height=box.bottom-box.top;
        if(!width || !height || width>16384 || height>16384) throw std::runtime_error("source-size");
        if(!input || width!=inputW || height!=inputH) {
            inputView.reset(); input.reset();
            D3D11_TEXTURE2D_DESC td{}; td.Width=width; td.Height=height; td.MipLevels=td.ArraySize=1;
            td.Format=DXGI_FORMAT_B8G8R8A8_UNORM; td.SampleDesc.Count=1;
            td.Usage=D3D11_USAGE_DEFAULT; td.BindFlags=D3D11_BIND_SHADER_RESOURCE;
            Check(device->CreateTexture2D(&td,nullptr,input.put()),"input-texture");
            Check(device->CreateShaderResourceView(input.get(),nullptr,inputView.put()),"input-view");
            inputW=width;inputH=height;
        }
        context->CopySubresourceRegion(input.get(),0,0,0,0,texture,0,&box);
    }
    ScaleRect Draw(UINT width,UINT height,float sharp) {
        ScaleRect fit=Fit(inputW,inputH,width,height);
        const FLOAT black[4]={0,0,0,1}; context->ClearRenderTargetView(target.get(),black);
        ID3D11RenderTargetView* rt=target.get(); context->OMSetRenderTargets(1,&rt,nullptr);
        D3D11_VIEWPORT vp{float(fit.x),float(fit.y),float(fit.width),float(fit.height),0,1};
        context->RSSetViewports(1,&vp);
        float params[8]={0,0,1,1,1.f/inputW,1.f/inputH,sharp,0};
        context->UpdateSubresource(constants.get(),0,nullptr,params,0,0);
        ID3D11Buffer* cb=constants.get(); ID3D11ShaderResourceView* srv=inputView.get(); ID3D11SamplerState* sam=sampler.get();
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(vs.get(),nullptr,0); context->PSSetShader(ps.get(),nullptr,0);
        context->PSSetConstantBuffers(0,1,&cb); context->PSSetSamplers(0,1,&sam); context->PSSetShaderResources(0,1,&srv);
        context->Draw(3,0);
        srv=nullptr; context->PSSetShaderResources(0,1,&srv);
        return fit;
    }
};

class Capture {
    Com<IInspectable> wrapped;
    Com<CaptureItem> item;
    Com<CaptureFramePool> pool;
    Com<CaptureSession> session;
    INT64 token=0;
    bool subscribed=false;
public:
    CaptureSize size{};
    ~Capture(){ CloseObject(session.get()); if(pool && subscribed) pool->remove_FrameArrived(token); CloseObject(pool.get()); }
    void Start(WinRt& rt,ID3D11Device* device,HWND hwnd,HANDLE frameEvent,bool cursor) {
        Com<IDXGIDevice> dxgi; Check(device->QueryInterface(__uuidof(IDXGIDevice),(void**)dxgi.put()),"capture-dxgi");
        using Wrap=HRESULT(WINAPI*)(IDXGIDevice*,IInspectable**);
        auto wrap=(Wrap)GetProcAddress(GetModuleHandleW(L"d3d11.dll"),"CreateDirect3D11DeviceFromDXGIDevice");
        if(!wrap) throw std::runtime_error("capture-device-api");
        Check(wrap(dxgi.get(),wrapped.put()),"capture-device");
        Com<CaptureItemInterop> interop;
        rt.Get(L"Windows.Graphics.Capture.GraphicsCaptureItem",L"{3628E81B-3CAC-4C60-B7F4-23CE0E0C3356}",interop);
        Check(interop->CreateForWindow(hwnd,Guid(L"{79C3F95B-31F7-4EC2-A464-632EF5D30760}"),(void**)item.put()),"capture-window");
        Check(item->get_Size(&size),"capture-size");
        if(size.Width<=0 || size.Height<=0) throw std::runtime_error("capture-empty");
        Com<CapturePoolFactory> factory;
        rt.Get(L"Windows.Graphics.Capture.Direct3D11CaptureFramePool",L"{589B103F-6BBC-5DF5-A991-02E28B3B66D5}",factory);
        Check(factory->CreateFreeThreaded(wrapped.get(),87,2,size,pool.put()),"capture-pool");
        FrameSignal* signal=new FrameSignal(frameEvent);
        HRESULT hr=pool->add_FrameArrived(signal,&token); signal->Release(); Check(hr,"capture-event"); subscribed=true;
        Check(pool->CreateCaptureSession(item.get(),session.put()),"capture-session");
        Com<CaptureSessionCursor> cursorOptions;
        if(SUCCEEDED(session->QueryInterface(Guid(L"{2C39AE40-7D2E-5044-804E-8B6799D4CF9E}"),(void**)cursorOptions.put())))
            Check(cursorOptions->put_IsCursorCaptureEnabled(cursor?1:0),"capture-cursor");
        Com<CaptureSessionInterval> interval;
        if(SUCCEEDED(session->QueryInterface(Guid(L"{67C0EA62-1F85-5061-925A-239BE0AC09CB}"),(void**)interval.put())))
            interval->put_MinUpdateInterval(10000); // 1 ms; actual work is gated by new frames and presentation.
        Check(session->StartCapture(),"capture-start");
    }
    bool CopyLatest(Graphics& graphics,HWND hwnd) {
        Com<CaptureFrame> latest;
        // Pool has two buffers; bounded draining also handles a concurrent producer.
        for(int i=0;i<4;++i) {
            Com<CaptureFrame> next; Check(pool->TryGetNextFrame(next.put()),"capture-frame");
            if(!next) break;
            CloseObject(latest.get()); latest=std::move(next);
        }
        if(!latest) return false;
        struct FrameClose { CaptureFrame* p; ~FrameClose(){CloseObject(p);} } close{latest.get()};
        CaptureSize content{}; Check(latest->get_ContentSize(&content),"frame-size");
        if(content.Width<=0 || content.Height<=0) return false;
        if(content.Width!=size.Width || content.Height!=size.Height) {
            CloseObject(latest.get()); latest.reset(); close.p=nullptr;
            size=content; Check(pool->Recreate(wrapped.get(),87,2,size),"capture-resize"); return false;
        }
        Com<IInspectable> surface; Com<DxgiAccess> access; Com<ID3D11Texture2D> texture;
        Check(latest->get_Surface(surface.put()),"frame-surface");
        Check(surface->QueryInterface(Guid(L"{A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1}"),(void**)access.put()),"surface-access");
        Check(access->GetInterface(__uuidof(ID3D11Texture2D),(void**)texture.put()),"surface-texture");
        RECT client=ClientScreen(hwnd), frame{};
        if(FAILED(DwmGetWindowAttribute(hwnd,DWMWA_EXTENDED_FRAME_BOUNDS,&frame,sizeof(frame)))) GetWindowRect(hwnd,&frame);
        int left=std::max(0,int(client.left-frame.left)),top=std::max(0,int(client.top-frame.top));
        int right=std::min(content.Width,left+int(client.right-client.left));
        int bottom=std::min(content.Height,top+int(client.bottom-client.top));
        D3D11_TEXTURE2D_DESC td{}; texture->GetDesc(&td);
        right=std::min(right,int(td.Width)); bottom=std::min(bottom,int(td.Height));
        if(right<=left || bottom<=top) return false;
        D3D11_BOX box{UINT(left),UINT(top),0,UINT(right),UINT(bottom),1};
        graphics.CopyInput(texture.get(),box); return true;
    }
};

struct WindowState {
    HWND source=nullptr;
    DWORD sourcePid=0;
    bool mapped=false;
    ScaleRect fit{};
    int sourceW=0,sourceH=0;
    WORD buttons=0;
    POINT last{};
    void ReleaseButtons() {
        if(OwnWindow(source,sourcePid)) {
            if(buttons&MK_LBUTTON) PostMessageW(source,WM_LBUTTONUP,0,MAKELPARAM(last.x,last.y));
            if(buttons&MK_RBUTTON) PostMessageW(source,WM_RBUTTONUP,0,MAKELPARAM(last.x,last.y));
            if(buttons&MK_MBUTTON) PostMessageW(source,WM_MBUTTONUP,0,MAKELPARAM(last.x,last.y));
            if(buttons&MK_XBUTTON1) PostMessageW(source,WM_XBUTTONUP,MAKEWPARAM(0,XBUTTON1),MAKELPARAM(last.x,last.y));
            if(buttons&MK_XBUTTON2) PostMessageW(source,WM_XBUTTONUP,MAKEWPARAM(0,XBUTTON2),MAKELPARAM(last.x,last.y));
        }
        buttons=0; if(GetCapture()) ReleaseCapture();
    }
};
static LRESULT CALLBACK OutputProc(HWND hwnd,UINT msg,WPARAM wp,LPARAM lp) {
    WindowState* state=(WindowState*)GetWindowLongPtrW(hwnd,GWLP_USERDATA);
    if(msg==WM_NCCREATE) {state=(WindowState*)((CREATESTRUCTW*)lp)->lpCreateParams; SetWindowLongPtrW(hwnd,GWLP_USERDATA,(LONG_PTR)state);}
    if(msg==WM_MOUSEACTIVATE) return MA_NOACTIVATE;
    if(msg==WM_ERASEBKGND) return 1;
    if(msg==WM_CLOSE) {PostQuitMessage(0); return 0;}
    if(msg==WM_HOTKEY) {PostQuitMessage(0); return 0;}
    if(state && (msg==WM_CANCELMODE || msg==WM_CAPTURECHANGED)) state->ReleaseButtons();
    if(state && state->mapped && msg>=WM_MOUSEFIRST && msg<=WM_MOUSELAST && OwnWindow(state->source,state->sourcePid)) {
        POINT point{GET_X_LPARAM(lp),GET_Y_LPARAM(lp)};
        if(msg==WM_MOUSEWHEEL || msg==WM_MOUSEHWHEEL) ScreenToClient(hwnd,&point);
        auto f=state->fit;
        if(f.width<=0 || f.height<=0) return 0;
        if(!state->buttons && (point.x<f.x || point.y<f.y || point.x>=f.x+f.width || point.y>=f.y+f.height)) return 0;
        POINT mapped{MapCoordinate(point.x,f.x,f.width,state->sourceW),MapCoordinate(point.y,f.y,f.height,state->sourceH)};
        state->last=mapped;
        if(msg==WM_LBUTTONDOWN || msg==WM_RBUTTONDOWN || msg==WM_MBUTTONDOWN || msg==WM_XBUTTONDOWN) SetCapture(hwnd);
        state->buttons=LOWORD(wp)&(MK_LBUTTON|MK_RBUTTON|MK_MBUTTON|MK_XBUTTON1|MK_XBUTTON2);
        if(msg==WM_LBUTTONUP || msg==WM_RBUTTONUP || msg==WM_MBUTTONUP || msg==WM_XBUTTONUP) if(!state->buttons && GetCapture()==hwnd) ReleaseCapture();
        if(msg==WM_MOUSEWHEEL || msg==WM_MOUSEHWHEEL) ClientToScreen(state->source,&mapped);
        PostMessageW(state->source,msg,wp,MAKELPARAM(mapped.x,mapped.y)); return msg==WM_XBUTTONDOWN || msg==WM_XBUTTONUP ? TRUE : 0;
    }
    return DefWindowProcW(hwnd,msg,wp,lp);
}
static HWND CreateOutput(WindowState* state) {
    WNDCLASSW cls{}; cls.lpfnWndProc=OutputProc; cls.hInstance=GetModuleHandleW(nullptr);
    cls.hCursor=LoadCursorW(nullptr,IDC_ARROW); cls.lpszClassName=L"Pavise.Scaling.Output.V1";
    RegisterClassW(&cls);
    DWORD ex=WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE;
    if(!state->mapped) ex|=WS_EX_LAYERED|WS_EX_TRANSPARENT;
    HWND hwnd=CreateWindowExW(ex,cls.lpszClassName,L"Pavise Scaling",WS_POPUP,0,0,64,64,nullptr,nullptr,cls.hInstance,state);
    if(!hwnd) throw std::runtime_error("output-window");
    if(!state->mapped) SetLayeredWindowAttributes(hwnd,0,255,LWA_ALPHA);
    return hwnd;
}
struct OutputWindow {
    HWND hwnd;
    WindowState& state;
    ~OutputWindow(){state.ReleaseButtons();UnregisterHotKey(hwnd,1);DestroyWindow(hwnd);}
};

static int RunWindow(HWND source,DWORD pid,UINT64 creation,DWORD parent,UINT64 parentCreation,const wchar_t* stopName,bool mapped,float sharp,bool hiddenTest=false) {
    Handle sourceProcess(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION|SYNCHRONIZE,FALSE,pid));
    Handle parentProcess(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION|SYNCHRONIZE,FALSE,parent));
    Handle stop(OpenEventW(SYNCHRONIZE,FALSE,stopName));
    if(!sourceProcess.value || !parentProcess.value || !stop.value || !creation || !parentCreation || Creation(sourceProcess.value)!=creation || Creation(parentProcess.value)!=parentCreation || !OwnWindow(source,pid))
        throw std::runtime_error("identity-changed");
    WinRt rt; Graphics graphics; graphics.Initialize();
    WindowState state;state.source=source;state.sourcePid=pid;state.mapped=mapped;
    OutputWindow output{CreateOutput(&state),state};
    // Required before showing anything: emergency exit must always be available.
    if(!hiddenTest && !RegisterHotKey(output.hwnd,1,MOD_CONTROL|MOD_ALT|MOD_NOREPEAT,'U')) throw std::runtime_error("exit-hotkey-busy");
    Handle frameEvent(CreateEventW(nullptr,FALSE,FALSE,nullptr));
    if(!frameEvent.value) throw std::runtime_error("frame-event");
    std::unique_ptr<Capture> capture;
    bool visible=false,wasActive=false,waitingWindowed=false;
    RECT previousMonitor{};
    UINT64 captureAt=0;
    std::string lastReady;
    HANDLE waits[]={stop.value,parentProcess.value,sourceProcess.value,frameEvent.value};
    Status("STARTING");
    for(;;) {
        DWORD result=MsgWaitForMultipleObjectsEx(4,waits,50,QS_ALLINPUT,MWMO_INPUTAVAILABLE);
        if(result<=WAIT_OBJECT_0+2) break;
        MSG msg; while(PeekMessageW(&msg,nullptr,0,0,PM_REMOVE)) {
            if(msg.message==WM_QUIT) {Status("STOPPED user"); return 0;}
            TranslateMessage(&msg);DispatchMessageW(&msg);
        }
        if(!OwnWindow(source,pid)) break;
        bool active=(hiddenTest || GetForegroundWindow()==source) && IsWindowVisible(source) && !IsIconic(source);
        if(!active) {
            if(visible) {ShowWindow(output.hwnd,SW_HIDE);state.ReleaseButtons();visible=false;}
            if(wasActive) {capture.reset(); Status("PAUSED");}
            wasActive=false;waitingWindowed=false;continue;
        }
        RECT client=ClientScreen(source);
        MONITORINFO monitor{};monitor.cbSize=sizeof(monitor);
        if(!GetMonitorInfoW(MonitorFromWindow(source,MONITOR_DEFAULTTONEAREST),&monitor)) throw std::runtime_error("monitor");
        UINT width=monitor.rcMonitor.right-monitor.rcMonitor.left,height=monitor.rcMonitor.bottom-monitor.rcMonitor.top;
        // The application controls its own resolution. Never resize/reconfigure a game.
        if(!hiddenTest && client.right-client.left>=LONG(width) && client.bottom-client.top>=LONG(height)) {
            if(visible) {ShowWindow(output.hwnd,SW_HIDE);state.ReleaseButtons();visible=false;}
            capture.reset(); if(!waitingWindowed) Status("WAITING windowed"); waitingWindowed=true;wasActive=true;continue;
        }
        wasActive=true;waitingWindowed=false;
        if(!SameRect(previousMonitor,monitor.rcMonitor)) {
            SetWindowPos(output.hwnd,HWND_TOPMOST,monitor.rcMonitor.left,monitor.rcMonitor.top,width,height,SWP_NOACTIVATE);
            if(graphics.swap) graphics.Resize(width,height); else graphics.CreateSwap(output.hwnd,width,height);
            previousMonitor=monitor.rcMonitor;
        }
        if(!capture) {
            capture.reset(new Capture()); capture->Start(rt,graphics.device.get(),source,frameEvent.value,!mapped);
            captureAt=GetTickCount64();lastReady.clear();Status("STARTING");
        }
        // WGC may stop producing frames for a static window. Only fail if the
        // initial frame never arrives; keep the last good image while idle.
        if(captureAt && GetTickCount64()-captureAt>8000) throw std::runtime_error("capture-timeout");
        if(result!=WAIT_OBJECT_0+3) continue;
        if(!capture->CopyLatest(graphics,source)) continue;
        // Bound GPU queuing and still notice parent/stop before submitting a frame.
        HANDLE presentWaits[]={stop.value,parentProcess.value,sourceProcess.value,graphics.latency};
        DWORD p=WaitForMultipleObjects(4,presentWaits,FALSE,250);
        if(p<=WAIT_OBJECT_0+2) break;
        if(p!=WAIT_OBJECT_0+3) continue;
        state.fit=graphics.Draw(width,height,sharp);state.sourceW=graphics.inputW;state.sourceH=graphics.inputH;
        HRESULT hr=graphics.swap->Present(1,0);
        Check(hr,"present");
        if(!visible) {
            // ShowWindow's first call can be overridden by STARTUPINFO's hidden
            // launch flag. Only explicitly reveal the output after a real frame.
            if(!hiddenTest) SetWindowPos(output.hwnd,HWND_TOPMOST,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE|SWP_SHOWWINDOW);
            visible=true;
        }
        char status[160];snprintf(status,sizeof(status),"READY %u %u %u %u",graphics.inputW,graphics.inputH,width,height);
        if(lastReady!=status) {Status(status);lastReady=status;}
        captureAt=0;
    }
    Status("STOPPED session");return 0;
}

static int SelfTest(bool software) {
    ScaleRect r=Fit(1280,720,1920,1200);
    if(r.x!=0 || r.y!=60 || r.width!=1920 || r.height!=1080 || MapCoordinate(1919,0,1920,1280)!=1279 || MapCoordinate(-10,0,1920,1280)!=0)
        throw std::runtime_error("geometry-test");
    Graphics graphics;graphics.Initialize(software);
    D3D11_TEXTURE2D_DESC td{};td.Width=16;td.Height=8;td.MipLevels=td.ArraySize=1;
    td.Format=DXGI_FORMAT_B8G8R8A8_UNORM;td.SampleDesc.Count=1;td.BindFlags=D3D11_BIND_SHADER_RESOURCE;
    std::vector<UINT32> pixels(128,0xFF40A0E0);D3D11_SUBRESOURCE_DATA data{pixels.data(),64,0};
    Com<ID3D11Texture2D> input;Check(graphics.device->CreateTexture2D(&td,&data,input.put()),"test-input");
    D3D11_BOX box{0,0,0,16,8,1};graphics.CopyInput(input.get(),box);
    td.Width=64;td.Height=64;td.BindFlags=D3D11_BIND_RENDER_TARGET;
    Com<ID3D11Texture2D> target;Check(graphics.device->CreateTexture2D(&td,nullptr,target.put()),"test-target");
    Check(graphics.device->CreateRenderTargetView(target.get(),nullptr,graphics.target.put()),"test-view");
    graphics.Draw(64,64,.4f);
    td.BindFlags=0;td.Usage=D3D11_USAGE_STAGING;td.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
    Com<ID3D11Texture2D> read;Check(graphics.device->CreateTexture2D(&td,nullptr,read.put()),"test-read");
    graphics.context->CopyResource(read.get(),target.get());
    D3D11_MAPPED_SUBRESOURCE mapped{};Check(graphics.context->Map(read.get(),0,D3D11_MAP_READ,0,&mapped),"test-map");
    bool good=true;
    for(int y=0;y<64;++y) for(int x=0;x<64;++x) {
        UINT32 actual=((UINT32*)((BYTE*)mapped.pData+y*mapped.RowPitch))[x];
        UINT32 expected=(y>=16 && y<48)?0xFF40A0E0:0xFF000000;
        for(int channel=0;channel<4;++channel) if(abs(int((actual>>(channel*8))&255)-int((expected>>(channel*8))&255))>1) good=false;
    }
    graphics.context->Unmap(read.get(),0); if(!good) throw std::runtime_error("shader-pixel-test");
    WindowState state; OutputWindow output{CreateOutput(&state),state};
    graphics.CreateSwap(output.hwnd,64,64); graphics.Draw(64,64,.35f);
    Check(graphics.swap->Present(0,0),"hidden-present");
    Status(software?"PASS warp geometry aspect sharpen pixels hidden-present cleanup":"PASS hardware geometry aspect sharpen pixels hidden-present cleanup"); return 0;
}

static int CaptureFactoryProbe() {
    // Only validates handle interop against an already existing window. No
    // capture session is created, no pixels are acquired and no border appears.
    WinRt rt;Com<CaptureItemInterop> interop;Com<CaptureItem> item;
    rt.Get(L"Windows.Graphics.Capture.GraphicsCaptureItem",L"{3628E81B-3CAC-4C60-B7F4-23CE0E0C3356}",interop);
    HWND hwnd=GetForegroundWindow();if(!hwnd) throw std::runtime_error("no-existing-window");
    Check(interop->CreateForWindow(hwnd,Guid(L"{79C3F95B-31F7-4EC2-A464-632EF5D30760}"),(void**)item.put()),"existing-window-factory");
    CaptureSize size{};Check(item->get_Size(&size),"existing-window-size");
    if(size.Width<=0 || size.Height<=0) throw std::runtime_error("existing-window-empty");
    Status("PASS capture-item-interop session-not-started pixels-not-captured");return 0;
}

static int ExistingCaptureProbe(HWND requested=nullptr) {
    // A capture-only smoke test against the task's already open Codex window.
    // It will not target a game or an arbitrary foreground application; pixels
    // stay in GPU resources and are neither displayed nor written to disk.
    HWND hwnd=requested?requested:GetForegroundWindow();DWORD pid=0;GetWindowThreadProcessId(hwnd,&pid);
    Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid));
    wchar_t path[32768]{};DWORD length=32768;
    if(!process.value || !QueryFullProcessImageNameW(process.value,0,path,&length)) throw std::runtime_error("probe-process");
    const wchar_t* leaf=wcsrchr(path,L'\\');leaf=leaf?leaf+1:path;
    if(_wcsicmp(leaf,L"Codex.exe")!=0 && _wcsicmp(leaf,L"ChatGPT.exe")!=0) {
        char name[256]{};WideCharToMultiByte(CP_UTF8,0,leaf,-1,name,256,nullptr,nullptr);printf("PROBE foreground-name=%s\n",name);fflush(stdout);
        throw std::runtime_error("probe-requires-task-window");
    }
    WinRt rt;Graphics graphics;graphics.Initialize();Handle event(CreateEventW(nullptr,FALSE,FALSE,nullptr));
    Capture capture;capture.Start(rt,graphics.device.get(),hwnd,event.value,false);
    UINT64 until=GetTickCount64()+4000;int frames=0;
    while(GetTickCount64()<until && frames<3) {
        WaitForSingleObject(event.value,100);
        if(capture.CopyLatest(graphics,hwnd)) ++frames;
    }
    if(!frames) throw std::runtime_error("existing-capture-timeout");
    WindowState state;OutputWindow output{CreateOutput(&state),state};
    graphics.CreateSwap(output.hwnd,640,360);graphics.Draw(640,360,.35f);Check(graphics.swap->Present(0,0),"existing-hidden-present");
    Status("PASS wgc-existing-task-window frame-event gpu-copy scaling hidden-present no-image-saved cleanup");return 0;
}

static void DpiAware() {
    using Set=BOOL(WINAPI*)(HANDLE);
    auto set=(Set)GetProcAddress(GetModuleHandleW(L"user32.dll"),"SetProcessDpiAwarenessContext");
    if(set) set((HANDLE)-4); else SetProcessDPIAware();
}
int WINAPI wWinMain(HINSTANCE,HINSTANCE,LPWSTR,int) {
    SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX);DpiAware();
    int argc=0;LPWSTR* argv=CommandLineToArgvW(GetCommandLineW(),&argc);
    int result=2;
    try {
        if(argc==2 && wcscmp(argv[1],L"--self-test")==0) result=SelfTest(false);
        else if(argc==2 && wcscmp(argv[1],L"--self-test-warp")==0) result=SelfTest(true);
        else if(argc==2 && wcscmp(argv[1],L"--capture-factory-test")==0) result=CaptureFactoryProbe();
        else if((argc==2 || argc==3) && wcscmp(argv[1],L"--capture-existing-test")==0)
            result=ExistingCaptureProbe(argc==3?(HWND)(UINT_PTR)_wcstoui64(argv[2],nullptr,10):nullptr);
        else if(argc==10 && (wcscmp(argv[1],L"--window")==0 || wcscmp(argv[1],L"--window-hidden-test")==0)) {
            auto number=[](const wchar_t* p)->UINT64 { wchar_t* end=nullptr;UINT64 n=_wcstoui64(p,&end,10);if(!*p || *end || *p==L'-') throw std::runtime_error("invalid-argument");return n; };
            UINT64 hwnd=number(argv[2]),pid=number(argv[3]),creation=number(argv[4]),parent=number(argv[5]),parentCreation=number(argv[6]);
            UINT64 mode=number(argv[8]),sharp=number(argv[9]);
            if(!hwnd || pid>MAXDWORD || parent>MAXDWORD || mode>1 || sharp>100 || wcsncmp(argv[7],L"Local\\Pavise.Scale.",19)!=0) throw std::runtime_error("invalid-argument");
            result=RunWindow((HWND)(UINT_PTR)hwnd,DWORD(pid),creation,DWORD(parent),parentCreation,argv[7],mode==1,float(sharp)/100.f,wcscmp(argv[1],L"--window-hidden-test")==0);
        } else Status("ERROR arguments");
    } catch(const std::exception& e) { std::string line="ERROR ";line+=e.what();Status(line.c_str());result=1; }
    if(argv) LocalFree(argv);return result;
}
