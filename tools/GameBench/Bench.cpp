#include "BenchCommon.hpp"
#include <bcrypt.h>
#include <cstdio>
#include <cwchar>
#include <limits>

namespace bench {
std::atomic<bool> cancelled{false};
struct WindowState {
    bool closed=false,ui=false,explicitPaint=false,paintWasExplicit=false;
    DWORD parent=0;
    int64_t posted=0,received=0,paintQpc=0;
    uint64_t marker=0,paints=0,paintTag=0,handled=0;
    HDC paintSurface=nullptr;HBITMAP paintBitmap=nullptr;HGDIOBJ originalBitmap=nullptr;
    ~WindowState(){if(paintSurface){if(originalBitmap)SelectObject(paintSurface,originalBitmap);if(paintBitmap)DeleteObject(paintBitmap);DeleteDC(paintSurface);}}
};
LRESULT CALLBACK WindowProc(HWND w,UINT message,WPARAM wp,LPARAM lp) {
    auto* s=reinterpret_cast<WindowState*>(GetWindowLongPtrW(w,GWLP_USERDATA));
    if(message==WM_NCCREATE) {
        auto* c=reinterpret_cast<CREATESTRUCTW*>(lp);
        SetWindowLongPtrW(w,GWLP_USERDATA,reinterpret_cast<LONG_PTR>(c->lpCreateParams));
    }
    if(message==WM_CLOSE) {if(s)s->closed=true;cancelled=true;return 0;}
    if(message==WM_KEYDOWN&&wp==VK_ESCAPE) {cancelled=true;return 0;}
    if(message==WM_APP+31) {
        if(s) {s->marker=uint64_t(wp);s->posted=int64_t(lp);s->received=Qpc();++s->handled;if(s->ui)InvalidateRect(w,nullptr,FALSE);}
        return 0;
    }
    if(message==WM_APP+32&&s&&s->ui) {
        if(wp) {ShowWindow(w,SW_SHOWNOACTIVATE);SetForegroundWindow(w);}
        else {if(s->parent)AllowSetForegroundWindow(s->parent);ShowWindow(w,SW_HIDE);}
        return 0;
    }
    if(message==WM_PAINT) {
        PAINTSTRUCT paint;HDC windowDc=BeginPaint(w,&paint),dc=s&&s->paintSurface?s->paintSurface:windowDc;RECT rect;GetClientRect(w,&rect);
        FillRect(dc,&rect,reinterpret_cast<HBRUSH>(COLOR_WINDOW+1));SetBkMode(dc,TRANSPARENT);
        DrawTextW(dc,L"Pavise owned desktop response fixture",-1,&rect,DT_CENTER|DT_VCENTER|DT_SINGLELINE);
        if(s&&s->ui) {for(int i=0;i<24;i++){RECT bar{16+i*20,16,30+i*20,28+LONG((s->marker+i*7)%64)};FillRect(dc,&bar,reinterpret_cast<HBRUSH>(COLOR_HIGHLIGHT+1));}}
        if(s&&s->paintSurface)BitBlt(windowDc,0,0,std::min<LONG>(640,rect.right),std::min<LONG>(360,rect.bottom),s->paintSurface,0,0,SRCCOPY);
        EndPaint(w,&paint);
        if(s) {++s->paints;s->paintQpc=Qpc();s->paintTag=s->marker;s->paintWasExplicit=s->explicitPaint;}
        return 0;
    }
    return DefWindowProcW(w,message,wp,lp);
}
void Pump(){MSG m;while(PeekMessageW(&m,nullptr,0,0,PM_REMOVE)){if(m.message==WM_QUIT)cancelled=true;TranslateMessage(&m);DispatchMessageW(&m);}}
struct Window {HWND value=nullptr;WindowState state;bool handoff=false;Window(const Options& o,const wchar_t* title,bool auxiliary=false){state.parent=o.parent;handoff=!o.hidden;WNDCLASSW wc{};wc.lpfnWndProc=WindowProc;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"PaviseCalibrationBenchOwnedWindow1";wc.hCursor=LoadCursorW(nullptr,IDC_ARROW);RegisterClassW(&wc);RECT r{0,0,LONG(auxiliary?640:o.width),LONG(auxiliary?360:o.height)};AdjustWindowRect(&r,WS_OVERLAPPEDWINDOW,FALSE);value=CreateWindowExW(o.hidden?WS_EX_TOOLWINDOW:0,wc.lpszClassName,title,WS_OVERLAPPEDWINDOW,o.hidden?-30000:CW_USEDEFAULT,o.hidden?-30000:CW_USEDEFAULT,r.right-r.left,r.bottom-r.top,nullptr,nullptr,wc.hInstance,&state);if(!value)throw std::runtime_error("CreateWindow failed");if(auxiliary){ShowWindow(value,SW_HIDE);ShowWindow(value,SW_HIDE);}else if(!o.hidden){ShowWindow(value,SW_SHOW);UpdateWindow(value);SetForegroundWindow(value);}}
    ~Window(){if(value){if(handoff&&state.parent)AllowSetForegroundWindow(state.parent);DestroyWindow(value);}}
};
uint64_t Number(const wchar_t* text,uint64_t low,uint64_t high){wchar_t* end=nullptr;if(!text[0]||text[0]=='-')throw std::runtime_error("invalid unsigned number");uint64_t v=std::wcstoull(text,&end,10);if(!end||*end||v<low||v>high)throw std::runtime_error("numeric argument outside permitted range");return v;}
Options Args(int argc,wchar_t** argv){Options o;for(int i=1;i<argc;i++){std::wstring key=argv[i];auto next=[&](){if(++i>=argc)throw std::runtime_error("missing argument");return argv[i];};if(key==L"--api")o.api=Utf8(next());else if(key==L"--scene")o.scene=Utf8(next());else if(key==L"--scenario")o.scenario=Utf8(next());else if(key==L"--nonce")o.nonce=Utf8(next());else if(key==L"--output")o.output=next();else if(key==L"--seed")o.seed=uint32_t(Number(next(),0,UINT32_MAX));else if(key==L"--width")o.width=UINT(Number(next(),160,7680));else if(key==L"--height")o.height=UINT(Number(next(),120,4320));else if(key==L"--parent-pid")o.parent=DWORD(Number(next(),1,UINT32_MAX));else if(key==L"--seconds"){wchar_t* end;const wchar_t* value=next();o.seconds=std::wcstod(value,&end);if(*end||!std::isfinite(o.seconds)||o.seconds<.1||o.seconds>43200)throw std::runtime_error("seconds must be 0.1..43200");}else if(key==L"--background-load")o.background=Utf8(next());else if(key==L"--worker")o.worker=Utf8(next());else if(key==L"--hidden")o.hidden=true;else if(key==L"--warp")o.warp=true;else if(key==L"--uncapped")o.uncapped=true;else if(key==L"--self-test")o.selfTest=true;else if(key==L"--wait-start")o.waitStart=true;else if(key==L"--worker-active")o.workerActive=true;else throw std::runtime_error("unknown option: "+Utf8(key));}
    if(o.api!="dx11"&&o.api!="dx12")throw std::runtime_error("api must be dx11 or dx12");const std::vector<std::string> scenes={"main","workers","gpu","mixed","stream","memory","startup","switch","wake","thermal"};if(std::find(scenes.begin(),scenes.end(),o.scene)==scenes.end())throw std::runtime_error("unknown scene");const std::vector<std::string> loads={"none","cpu","io","gpu"};if(std::find(loads.begin(),loads.end(),o.background)==loads.end()||(!o.worker.empty()&&o.worker!="cpu"&&o.worker!="io"&&o.worker!="gpu"&&o.worker!="ui"))throw std::runtime_error("unknown background load");if(o.scenario!="smart"&&o.scenario!="competitive"&&o.scenario!="shared")throw std::runtime_error("scenario must be smart, competitive or shared");if(o.output.empty()||o.nonce.empty()||o.nonce.size()>128||o.nonce.find_first_not_of("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.")!=std::string::npos)throw std::runtime_error("output and a safe 1..128 character nonce are required");o.output=std::filesystem::absolute(o.output);return o;
}
std::string Sha256(const std::filesystem::path& path){BCRYPT_ALG_HANDLE algorithm=nullptr;BCRYPT_HASH_HANDLE hash=nullptr;std::string result;Check(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0),"BCryptOpenAlgorithmProvider");DWORD size=0,n=0;Check(BCryptGetProperty(algorithm,BCRYPT_OBJECT_LENGTH,reinterpret_cast<PUCHAR>(&size),sizeof(size),&n,0),"BCryptGetProperty");std::vector<uint8_t> object(size);try{Check(BCryptCreateHash(algorithm,&hash,object.data(),size,nullptr,0,0),"BCryptCreateHash");std::ifstream in(path,std::ios::binary);if(!in)throw std::runtime_error("hash input open failed");char block[65536];while(in.read(block,sizeof(block))||in.gcount())Check(BCryptHashData(hash,reinterpret_cast<PUCHAR>(block),ULONG(in.gcount()),0),"BCryptHashData");uint8_t digest[32];Check(BCryptFinishHash(hash,digest,sizeof(digest),0),"BCryptFinishHash");std::ostringstream s;for(auto v:digest)s<<std::hex<<std::setw(2)<<std::setfill('0')<<unsigned(v);result=s.str();}catch(...){if(hash)BCryptDestroyHash(hash);BCryptCloseAlgorithmProvider(algorithm,0);throw;}BCryptDestroyHash(hash);BCryptCloseAlgorithmProvider(algorithm,0);return result;}
std::string Attributes(){std::ostringstream s;s<<"priority_class\t"<<GetPriorityClass(GetCurrentProcess())<<"\n";using NtQuery=LONG(NTAPI*)(HANDLE,ULONG,PVOID,ULONG,PULONG);auto query=reinterpret_cast<NtQuery>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"),"NtQueryInformationProcess"));ULONG io=0;if(query&&query(GetCurrentProcess(),33,&io,sizeof(io),nullptr)>=0)s<<"io_priority\t"<<io<<"\n";else s<<"io_priority\t\n";struct Throttling{ULONG version,control,state;} power{1,0,0};using GetInfo=BOOL(WINAPI*)(HANDLE,int,LPVOID,DWORD);auto get=reinterpret_cast<GetInfo>(GetProcAddress(GetModuleHandleW(L"kernel32.dll"),"GetProcessInformation"));if(get&&get(GetCurrentProcess(),4,&power,sizeof(power)))s<<"power_control\t"<<power.control<<"\npower_state\t"<<power.state<<"\n";else s<<"power_control\t\npower_state\t\n";return s.str();}
struct Watch {
    HANDLE parent=nullptr;std::thread thread;std::atomic<bool> done{false};std::string reason;std::mutex mutex;Options options;int64_t deadline;
    explicit Watch(const Options& o):options(o),deadline(Qpc()+int64_t((o.seconds+180)*Frequency())){if(o.parent){parent=OpenProcess(SYNCHRONIZE|PROCESS_QUERY_LIMITED_INFORMATION,FALSE,o.parent);if(!parent)throw std::runtime_error("parent process unavailable");}thread=std::thread([this]{while(!done){if(parent&&WaitForSingleObject(parent,0)==WAIT_OBJECT_0){set("parent_exited");cancelled=true;}if(GetFileAttributesW((options.output/L"cancel.flag").c_str())!=INVALID_FILE_ATTRIBUTES){set("cancel_flag");cancelled=true;}if(Qpc()>deadline){try{Write(options.output/L"watchdog-failure.tsv","nonce\t"+options.nonce+"\nreason\twall_timeout\n");}catch(...){}TerminateProcess(GetCurrentProcess(),124);}Sleep(50);}});}
    void set(const char* s){std::lock_guard<std::mutex> l(mutex);if(reason.empty())reason=s;}
    std::string why(){std::lock_guard<std::mutex> l(mutex);return reason.empty()?"cancelled":reason;}
    ~Watch(){done=true;if(thread.joinable())thread.join();if(parent)CloseHandle(parent);}
};
struct Child {
    struct Role {HANDLE process=nullptr;DWORD pid=0;uint64_t birth=0;std::string name;std::wstring image;HWND window=nullptr;~Role(){if(process){if(WaitForSingleObject(process,0)!=WAIT_OBJECT_0)TerminateProcess(process,126);CloseHandle(process);}}};
    std::array<Role,4> roles;Options options;std::wstring path;
    explicit Child(const Options& o):options(o){wchar_t exe[32768];GetModuleFileNameW(nullptr,exe,32768);path=exe;wchar_t helperPath[32768];DWORD helperLength=GetEnvironmentVariableW(L"BENCH_UI_HELPER",helperPath,32768);std::wstring helper=helperLength>0&&helperLength<32768&&GetFileAttributesW(helperPath)!=INVALID_FILE_ATTRIBUTES?std::wstring(helperPath):std::wstring();size_t index=0;for(const std::string name:{"cpu","io","gpu","ui"}){auto& role=roles[index++];role.name=name;bool reparent=name=="ui"&&!helper.empty();role.image=reparent?helper:path;std::wstring cmd=L"\""+role.image+L"\" --worker "+std::wstring(name.begin(),name.end())+L" --output \""+o.output.wstring()+L"\" --nonce "+std::wstring(o.nonce.begin(),o.nonce.end())+L" --parent-pid "+std::to_wstring(GetCurrentProcessId())+L" --seconds "+std::to_wstring(o.seconds)+(name!="ui"||o.hidden?L" --hidden":L"")+(o.warp?L" --warp":L"")+(name==o.background?L" --worker-active":L"");STARTUPINFOEXW six{};six.StartupInfo.cb=reparent?sizeof(six):sizeof(STARTUPINFOW);six.StartupInfo.dwFlags=STARTF_USESHOWWINDOW;six.StartupInfo.wShowWindow=SW_HIDE;PROCESS_INFORMATION pi{};DWORD flags=CREATE_NO_WINDOW;HANDLE shell=nullptr;LPPROC_THREAD_ATTRIBUTE_LIST attributes=nullptr;if(reparent){DWORD shellPid=0;GetWindowThreadProcessId(GetShellWindow(),&shellPid);shell=shellPid?OpenProcess(PROCESS_CREATE_PROCESS,FALSE,shellPid):nullptr;SIZE_T size=0;InitializeProcThreadAttributeList(nullptr,1,0,&size);attributes=static_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(HeapAlloc(GetProcessHeap(),0,size));if(!shell||!attributes||!InitializeProcThreadAttributeList(attributes,1,0,&size)||!UpdateProcThreadAttribute(attributes,0,PROC_THREAD_ATTRIBUTE_PARENT_PROCESS,&shell,sizeof(shell),nullptr,nullptr))throw std::runtime_error("helper reparenting failed");six.lpAttributeList=attributes;flags|=EXTENDED_STARTUPINFO_PRESENT;}BOOL created=CreateProcessW(role.image.c_str(),cmd.data(),nullptr,nullptr,FALSE,flags,nullptr,nullptr,&six.StartupInfo,&pi);if(attributes){DeleteProcThreadAttributeList(attributes);HeapFree(GetProcessHeap(),0,attributes);}if(shell)CloseHandle(shell);if(!created)throw std::runtime_error("background child creation failed");role.process=pi.hProcess;role.pid=pi.dwProcessId;role.birth=Birth(role.process);CloseHandle(pi.hThread);int64_t end=Qpc()+Frequency()*30;auto ready=o.output/(L"child-ready-"+std::wstring(name.begin(),name.end())+L".tsv");while(GetFileAttributesW(ready.c_str())==INVALID_FILE_ATTRIBUTES){Pump();if(cancelled||WaitForSingleObject(role.process,0)==WAIT_OBJECT_0||Qpc()>end)throw std::runtime_error("background initialization failed or cancelled");Sleep(10);}if(name=="ui"){std::ifstream input(ready);std::string line;while(std::getline(input,line))if(line.rfind("hwnd\t",0)==0)role.window=reinterpret_cast<HWND>(uintptr_t(std::stoull(line.substr(5))));DWORD owner=0;if(!role.window||!GetWindowThreadProcessId(role.window,&owner)||owner!=role.pid)throw std::runtime_error("UI helper window identity failed");}}}
    HWND uiWindow()const{for(const auto& role:roles)if(role.name=="ui")return role.window;return nullptr;}
    std::string metadata()const{std::ostringstream s;s<<"path\t"<<Utf8(path)<<"\n";for(const auto& role:roles){s<<role.name<<"_pid\t"<<role.pid<<"\n"<<role.name<<"_birth\t"<<role.birth<<"\n"<<role.name<<"_path\t"<<Utf8(role.image.empty()?path:role.image)<<"\n"<<role.name<<"_parent_pid\t"<<GetCurrentProcessId()<<"\n";if(role.name=="ui")s<<"ui_hwnd\t"<<uint64_t(reinterpret_cast<uintptr_t>(role.window))<<"\n";if(role.name==options.background)s<<"child_pid\t"<<role.pid<<"\nchild_birth\t"<<role.birth<<"\nchild_path\t"<<Utf8(path)<<"\nchild_parent_pid\t"<<GetCurrentProcessId()<<"\n";}return s.str();}
    void start(){Write(options.output/L"child-start.flag",options.nonce);}
    bool alive()const{for(const auto& role:roles)if(WaitForSingleObject(role.process,0)!=WAIT_TIMEOUT)return false;return true;}
    ~Child(){try{Write(options.output/L"child-stop.flag",options.nonce);}catch(...){}for(auto& role:roles)if(role.process)WaitForSingleObject(role.process,3000);}
};
double Percentile(std::vector<double> values,double fraction) {
    if(values.empty())return 0;
    std::sort(values.begin(),values.end());
    size_t index=size_t(std::ceil(double(values.size())*fraction));
    return values[std::min(values.size()-1,index?index-1:0)];
}
std::string ResultIdentity(const Options& o) {
    wchar_t path[32768];GetModuleFileNameW(nullptr,path,32768);
    std::ostringstream s;
    s<<"pid\t"<<GetCurrentProcessId()<<"\nbirth\t"<<Birth(GetCurrentProcess())
     <<"\npath\t"<<Utf8(path)<<"\nparent_pid\t"<<o.parent<<"\n";
    return s.str();
}
bool WaitForWorkerStart(const Options& o) {
    for(;;) {
        // Stop wins even if start and stop are both already present. A parent may
        // cancel after ready but before releasing any measured child work.
        if(cancelled||GetFileAttributesW((o.output/L"child-stop.flag").c_str())!=INVALID_FILE_ATTRIBUTES)return false;
        if(GetFileAttributesW((o.output/L"child-start.flag").c_str())!=INVALID_FILE_ATTRIBUTES)return true;
        Pump();Sleep(10);
    }
}
int WorkerStoppedBeforeStart(const Options& o) {
    AtomicWrite(o.output/(L"child-result-"+std::wstring(o.worker.begin(),o.worker.end())+L".tsv"),
        "schema\t1\nnonce\t"+o.nonce+"\nrole\t"+o.worker+"\n"+ResultIdentity(o)
        +"completed\t0\nstarted\t0\noperations\t0\nelapsed_seconds\t0\nreason\tstopped_before_start\n");
    return 3; // Gracefully stopped; never a completed workload or a usable measurement.
}
int UiWorker(const Options& o) {
    Watch watch(o);
    Window ui(o,L"Pavise owned desktop response — Esc cancels",true);
    ui.state.ui=true;ui.state.parent=o.parent;
    BITMAPINFO bitmap{};bitmap.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);bitmap.bmiHeader.biWidth=640;bitmap.bmiHeader.biHeight=-360;bitmap.bmiHeader.biPlanes=1;bitmap.bmiHeader.biBitCount=32;bitmap.bmiHeader.biCompression=BI_RGB;void* pixels=nullptr;
    ui.state.paintSurface=CreateCompatibleDC(nullptr);ui.state.paintBitmap=CreateDIBSection(nullptr,&bitmap,DIB_RGB_COLORS,&pixels,nullptr,0);
    if(!ui.state.paintSurface||!ui.state.paintBitmap||!pixels)throw std::runtime_error("UI helper offscreen bitmap creation failed");
    ui.state.originalBitmap=SelectObject(ui.state.paintSurface,ui.state.paintBitmap);if(!ui.state.originalBitmap||ui.state.originalBitmap==HGDI_ERROR)throw std::runtime_error("UI helper bitmap selection failed");
    std::ostringstream ready;
    ready<<"schema\t1\nnonce\t"<<o.nonce<<"\npid\t"<<GetCurrentProcessId()
         <<"\nbirth\t"<<Birth(GetCurrentProcess())<<"\nload\tui\nrole\tui\nactive\t1\nhwnd\t"
         <<uint64_t(reinterpret_cast<uintptr_t>(ui.value))<<"\n";
    AtomicWrite(o.output/L"child-ready-ui.tsv",ready.str());
    if(!WaitForWorkerStart(o))return WorkerStoppedBeforeStart(o);
    int64_t begin=Qpc(),lastPaint=0;
    uint64_t consumed=0,naturalPaintSamples=0,explicitHiddenPaintSamples=0;
    std::vector<double> handled,painted,intervals,foregroundPainted,backgroundPainted;
    size_t capacity=std::min<size_t>(1000000,size_t((o.seconds+180)*80+16));
    handled.reserve(capacity);painted.reserve(capacity);intervals.reserve(capacity);foregroundPainted.reserve(capacity/2);backgroundPainted.reserve(capacity/2);
    ui.state.marker=ui.state.paintTag=ui.state.paints=ui.state.handled=0;
    std::atomic<bool> senderDone{false};std::atomic<uint64_t> sent{0};
    std::thread sender([&]{
        int64_t next=Qpc();uint64_t tag=0;
        while(!senderDone&&!cancelled) {
            int64_t now=Qpc();
            if(now>=next) {
                if(PostMessageW(ui.value,WM_APP+31,WPARAM(++tag),LPARAM(now)))++sent;
                next+=Frequency()/60;
                if(next<now-Frequency())next=now+Frequency()/60;
            }
            Sleep(1);
        }
    });
    bool capacityReached=false;
    while(!cancelled&&GetFileAttributesW((o.output/L"child-stop.flag").c_str())==INVALID_FILE_ATTRIBUTES) {
        Pump();
        if(GetForegroundWindow()==ui.value&&o.parent)AllowSetForegroundWindow(o.parent);
        if(ui.state.marker&&ui.state.paintTag!=ui.state.marker)UpdateWindow(ui.value);
        // The auxiliary HWND also stays hidden during visible non-switch trials. Probe
        // its owned DIB explicitly whenever hidden, never as foreground UI evidence.
        if(!IsWindowVisible(ui.value)&&ui.state.marker&&ui.state.paintTag!=ui.state.marker) {
            ui.state.explicitPaint=true;SendMessageW(ui.value,WM_PAINT,0,0);ui.state.explicitPaint=false;
        }
        if(ui.state.paintTag&&ui.state.paintTag!=consumed&&ui.state.posted>0&&ui.state.received>=ui.state.posted&&ui.state.paintQpc>=ui.state.received) {
            if(painted.size()<capacity) {
                handled.push_back(Millis(ui.state.received-ui.state.posted));
                painted.push_back(Millis(ui.state.paintQpc-ui.state.posted));
                if(ui.state.paintWasExplicit)++explicitHiddenPaintSamples;else ++naturalPaintSamples;
                if(!ui.state.paintWasExplicit&&IsWindowVisible(ui.value)&&GetForegroundWindow()==ui.value)foregroundPainted.push_back(painted.back());else backgroundPainted.push_back(painted.back());
                if(lastPaint)intervals.push_back(Millis(ui.state.paintQpc-lastPaint));
            } else capacityReached=true;
            lastPaint=ui.state.paintQpc;consumed=ui.state.paintTag;
        }
        Sleep(1);
    }
    int64_t end=Qpc();senderDone=true;sender.join();
    bool normalStop=!cancelled&&!capacityReached&&GetFileAttributesW((o.output/L"child-stop.flag").c_str())!=INVALID_FILE_ATTRIBUTES;
    std::ostringstream result;result<<std::setprecision(12)
        <<"schema\t1\nnonce\t"<<o.nonce<<"\nrole\tui\nactive\t1\noperations\t"<<ui.state.paints
        <<"\nelapsed_seconds\t"<<Millis(end-begin)/1000<<"\ncompleted\t"<<(normalStop?1:0)
        <<"\nsamples\t"<<painted.size()<<"\nmedian_ms\t";
    if(!painted.empty())result<<Percentile(painted,.5);
    result<<"\np95_ms\t";if(!painted.empty())result<<Percentile(painted,.95);
    result<<"\nmax_ms\t";if(!painted.empty())result<<*std::max_element(painted.begin(),painted.end());
    result<<"\nhandled_median_ms\t";if(!handled.empty())result<<Percentile(handled,.5);
    result<<"\nhandled_p95_ms\t";if(!handled.empty())result<<Percentile(handled,.95);
    result<<"\npaint_interval_p95_ms\t";if(!intervals.empty())result<<Percentile(intervals,.95);
    result<<"\npaint_interval_max_ms\t";if(!intervals.empty())result<<*std::max_element(intervals.begin(),intervals.end());
    result<<"\nforeground_samples\t"<<foregroundPainted.size()<<"\nforeground_p95_ms\t";if(!foregroundPainted.empty())result<<Percentile(foregroundPainted,.95);
    result<<"\nforeground_max_ms\t";if(!foregroundPainted.empty())result<<*std::max_element(foregroundPainted.begin(),foregroundPainted.end());
    result<<"\nbackground_samples\t"<<backgroundPainted.size()<<"\nbackground_p95_ms\t";if(!backgroundPainted.empty())result<<Percentile(backgroundPainted,.95);
    result<<"\nprogress_count\t"<<ui.state.paints<<"\nsent_markers\t"<<sent.load()<<"\nhandled_markers\t"<<ui.state.handled
        <<"\nsource\tposted_to_WM_PAINT_return_not_display_latency\npaint_surface\towned_640x360_DIB\nhidden_paint_dispatch\t"<<(explicitHiddenPaintSamples?(naturalPaintSamples?"mixed_natural_and_explicit_hidden_probe":"explicit_WM_PAINT_non_certifying"):"natural_WM_PAINT")
        <<"\nnatural_paint_samples\t"<<naturalPaintSamples<<"\nexplicit_hidden_paint_samples\t"<<explicitHiddenPaintSamples<<"\nforeground_paint_source\tnatural_visible_foreground_only\ncapacity_exceeded\t"<<(capacityReached?1:0)
        <<"\nhwnd\t"<<uint64_t(reinterpret_cast<uintptr_t>(ui.value))
        <<"\naudio_supported\t0\naudio_reason\tnot_implemented\naudio_device\t\naudio_underruns\t\n"<<ResultIdentity(o)<<Attributes();
    AtomicWrite(o.output/L"child-result-ui.tsv",result.str());
    return normalStop?0:3;
}
int Worker(const Options& o){if(o.worker=="ui")return UiWorker(o);Watch watch(o);std::unique_ptr<Window> window;std::unique_ptr<Scene> scene;std::unique_ptr<Renderer> renderer;std::unique_ptr<TaskPool> cpuPool;std::vector<uint64_t> cpuSums;std::vector<Frame> discarded;HANDLE file=INVALID_HANDLE_VALUE;std::vector<uint8_t> data(4*1024*1024,17);uint64_t checksum=0,iterations=0,readBytes=0;void* directBuffer=nullptr;std::string ioMode;
    if(o.worker=="cpu"&&o.workerActive){cpuPool=std::make_unique<TaskPool>(std::min(8u,std::max(1u,std::thread::hardware_concurrency()/2)));cpuSums.resize(cpuPool->size());}
    if(o.worker=="gpu"){Options g=o;g.api="dx11";g.scene="gpu";g.width=640;g.height=360;window=std::make_unique<Window>(g,L"Pavise owned GPU background fixture");scene=std::make_unique<Scene>(g);renderer=MakeDx11(window->value,g);}
    if(o.worker=="io"){auto path=o.output/L"owned-background.bin";file=CreateFileW(path.c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);if(file==INVALID_HANDLE_VALUE)throw std::runtime_error("background fixture exists");DWORD n=0;if(!WriteFile(file,data.data(),DWORD(data.size()),&n,nullptr)||n!=data.size()||!FlushFileBuffers(file))throw std::runtime_error("background asset write failed");CloseHandle(file);directBuffer=VirtualAlloc(nullptr,data.size(),MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE);if(!directBuffer)throw std::runtime_error("aligned IO allocation failed");file=CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_RANDOM_ACCESS|FILE_FLAG_NO_BUFFERING,nullptr);if(file!=INVALID_HANDLE_VALUE)ioMode="unbuffered_owned_read_os_data_cache_bypass_not_device_cache";else{file=CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_RANDOM_ACCESS,nullptr);ioMode="buffered_fallback";}if(file==INVALID_HANDLE_VALUE)throw std::runtime_error("background asset read failed");}
    auto roleSuffix=std::wstring(o.worker.begin(),o.worker.end());AtomicWrite(o.output/(L"child-ready-"+roleSuffix+L".tsv"),"schema\t1\nnonce\t"+o.nonce+"\npid\t"+std::to_string(GetCurrentProcessId())+"\nbirth\t"+std::to_string(Birth(GetCurrentProcess()))+"\nload\t"+o.worker+"\nactive\t"+(o.workerActive?"1":"0")+"\n");
    if(!WaitForWorkerStart(o)){if(file!=INVALID_HANDLE_VALUE)CloseHandle(file);if(directBuffer)VirtualFree(directBuffer,0,MEM_RELEASE);return WorkerStoppedBeforeStart(o);}
    int64_t workerBegin=Qpc();while(!cancelled&&GetFileAttributesW((o.output/L"child-stop.flag").c_str())==INVALID_FILE_ATTRIBUTES){
        if(!o.workerActive){Pump();Sleep(50);continue;}
        if(o.worker=="cpu"){cpuPool->run([&](size_t worker){uint64_t sum=0;for(size_t i=worker*64;i<data.size();i+=cpuPool->size()*64){uint64_t v=data[i]+iterations;for(int j=0;j<96;j++)v=(v^(v>>13))*2862933555777941757ull+3037000493ull;data[i]=uint8_t(v);sum+=v;}cpuSums[worker]=sum;});for(auto sum:cpuSums)checksum^=sum;}
        else if(o.worker=="io"){DWORD n=0;if(!ReadFile(file,directBuffer,DWORD(data.size()),&n,nullptr))throw std::runtime_error("background read failed");if(!n)SetFilePointer(file,0,nullptr,FILE_BEGIN);else if(static_cast<uint8_t*>(directBuffer)[0]!=17)throw std::runtime_error("owned IO fixture validation failed");readBytes+=n;checksum+=n;}
        else {Pump();double elapsed=Millis(Qpc()-workerBegin)/1000;scene->update(iterations,elapsed);renderer->draw(*scene,scene->constants(iterations,elapsed),discarded,SIZE_MAX);}
        ++iterations;
    }if(renderer)renderer->finish(discarded);if(file!=INVALID_HANDLE_VALUE)CloseHandle(file);if(directBuffer)VirtualFree(directBuffer,0,MEM_RELEASE);Write(o.output/(L"child-result-"+roleSuffix+L".tsv"),"nonce\t"+o.nonce+"\nrole\t"+o.worker+"\n"+ResultIdentity(o)+"operations\t"+std::to_string(iterations)+"\nelapsed_seconds\t"+std::to_string(Millis(Qpc()-workerBegin)/1000)+"\ncompleted\t"+(!cancelled?"1":"0")+"\niterations\t"+std::to_string(iterations)+"\nchecksum\t"+std::to_string(checksum)+"\nread_bytes\t"+std::to_string(readBytes)+"\nio_mode\t"+ioMode+"\nactive\t"+(o.workerActive?"1":"0")+"\n"+Attributes());return 0;
}
struct CsvCountBuffer:std::streambuf {
    uint64_t bytes=0,rows=0,limit=UINT64_MAX;
    std::streamsize xsputn(const char* s,std::streamsize n)override{
        if(uint64_t(n)>limit-bytes)return 0;
        for(std::streamsize i=0;i<n;i++)if(s[i]=='\n')++rows;
        bytes+=uint64_t(n);return n;
    }
    int_type overflow(int_type c)override{
        if(traits_type::eq_int_type(c,traits_type::eof()))return traits_type::not_eof(c);
        char value=traits_type::to_char_type(c);return xsputn(&value,1)==1?c:traits_type::eof();
    }
};
bool SelfTest(const Options& base){
    {
        DXGI_ADAPTER_DESC1 integrated{},discrete{},software{};
        integrated.DedicatedVideoMemory=128ull*1024*1024;discrete.DedicatedVideoMemory=8ull*1024*1024*1024;
        software.Flags=DXGI_ADAPTER_FLAG_SOFTWARE;software.DedicatedVideoMemory=16ull*1024*1024*1024;
        if(HardwareAdapterChoice({software,integrated,discrete},false)!=2
            ||HardwareAdapterChoice({software,discrete,integrated},true)!=1
            ||HardwareAdapterChoice({integrated,discrete},true)!=0
            ||HardwareAdapterChoice({integrated,integrated},false)!=0
            ||HardwareAdapterChoice({software},true)!=SIZE_MAX||HardwareAdapterChoice({},false)!=SIZE_MAX)
            throw std::runtime_error("self-test adapter preference, fallback or software exclusion failed");
    }
    const uint64_t ample=uint64_t(16)*sizeof(Frame)*5000000;
    if(FrameCapacity(.1,ample)!=10000||FrameCapacity(4.5,ample)!=72100||FrameCapacity(20,ample)!=320100||FrameCapacity(600,ample)!=4000000)
        throw std::runtime_error("self-test high-FPS frame reservation failed");
    if(FrameCapacity(20,uint64_t(16)*sizeof(Frame)*15000)!=15000||FrameCapacity(43200,ample)!=4000000)
        throw std::runtime_error("self-test sample memory ceiling failed");
    for(double duration:{.0,43201.}){bool rejected=false;try{FrameCapacity(duration,ample);}catch(const std::runtime_error&){rejected=true;}if(!rejected)throw std::runtime_error("self-test sample duration guard failed");}
    bool lowMemoryRejected=false;try{FrameCapacity(20,uint64_t(16)*sizeof(Frame)*9999);}catch(const std::runtime_error&){lowMemoryRejected=true;}
    if(!lowMemoryRejected)throw std::runtime_error("self-test sample memory floor failed");
    {
        std::vector<Frame> maximum(4000000);
        for(size_t i=0;i<maximum.size();i++){auto& f=maximum[i];f.sequence=i;f.qpc=INT64_MAX-int64_t(i);f.frameMs=f.cpuMs=f.gpuMs=f.latencyMs=12345.123456789;f.valid=true;f.controlledBackground=true;}
        CsvCountBuffer buffer;std::ostream csv(&buffer);csv.exceptions(std::ios::badbit|std::ios::failbit);
        if(WriteFrameCsv(csv,maximum)!=0||buffer.rows!=4000001||buffer.bytes>4000000ull*192+128
            ||!FrameCapacityReached(maximum.size(),4000000)||FrameCapacityReached(3999999,4000000)
            ||FrameStorageBytes(4000000)!=4000000ull*192+64ull*1024*1024)
            throw std::runtime_error("self-test four-million-row CSV boundary failed");
        CsvCountBuffer full;full.limit=1024;std::ostream failing(&full);failing.exceptions(std::ios::badbit|std::ios::failbit);
        bool failed=false;try{WriteFrameCsv(failing,maximum);failing.flush();}catch(const std::ios_base::failure&){failed=true;}
        if(!failed||full.rows>=4000001)throw std::runtime_error("self-test full-disk failure was hidden");
    }
    Options o=base;o.scene="workers";Scene a(o),b(o);if(a.fixedChecksum!=b.fixedChecksum||!a.fixedChecksum)throw std::runtime_error("self-test deterministic scene failed");a.reset();b.reset();for(int i=0;i<5;i++){a.update(i);b.update(i);}if(a.rollingChecksum!=b.rollingChecksum)throw std::runtime_error("self-test task graph determinism failed");a.reset();b.reset();a.update(19,2.25);b.update(271,2.25);if(Hash(a.vertices.data(),a.vertices.size()*sizeof(Vertex))!=Hash(b.vertices.data(),b.vertices.size()*sizeof(Vertex)))throw std::runtime_error("self-test elapsed-time scene independence failed");o.seed++;Scene c(o);if(c.fixedChecksum==a.fixedChecksum)throw std::runtime_error("self-test seed isolation failed");Matrix p=Projection(16.f/9);if(!std::isfinite(p.m[0])||p.m[11]!=1)throw std::runtime_error("self-test matrix failed");uint64_t expected=Hash("hello",5);if(expected!=0xa430d84680aabd0bull)throw std::runtime_error("self-test FNV vector failed");bool rejected=false;try{Number(L"-1",0,10);}catch(...){rejected=true;}if(!rejected)throw std::runtime_error("self-test argument guard failed");return true;}

int Run(const Options& o){
    std::filesystem::create_directories(o.output);if(!SetPriorityClass(GetCurrentProcess(),NORMAL_PRIORITY_CLASS))throw std::runtime_error("normal baseline priority request failed");if(!o.worker.empty())return Worker(o);
    for(const auto* name:{L"manifest.tsv",L"frames.csv",L"ready.tsv",L"child-ready.tsv",L"child-ready-cpu.tsv",L"child-ready-io.tsv",L"child-ready-gpu.tsv",L"child-ready-ui.tsv",L"start.flag",L"child-start.flag",L"child-stop.flag"})if(std::filesystem::exists(o.output/name))throw std::runtime_error("run directory already contains a run; refusing overwrite");
    int64_t entry=Qpc();FILETIME now;GetSystemTimePreciseAsFileTime(&now);uint64_t birth=Birth(GetCurrentProcess()),wall=(uint64_t(now.dwHighDateTime)<<32)|now.dwLowDateTime;int64_t creation=entry-int64_t(double(wall-birth)*double(Frequency())/1e7);
    std::string base="schema\t1\nnonce\t"+o.nonce+"\napi\t"+o.api+"\nscene\t"+o.scene+"\nscenario\t"+o.scenario+"\nseed\t"+std::to_string(o.seed)+"\nwidth\t"+std::to_string(o.width)+"\nheight\t"+std::to_string(o.height)+"\npid\t"+std::to_string(GetCurrentProcessId())+"\nbirth\t"+std::to_string(birth)+"\nqpc_frequency\t"+std::to_string(Frequency())+"\nworkload_version\t1\n";
    Write(o.output/L"manifest.tsv",base+"completed\t0\nsoftware\t1\nforeground_valid\t0\n");Watch watch(o);
    if(o.selfTest){SelfTest(o);Write(o.output/L"self-test.tsv","schema\t1\nnonce\t"+o.nonce+"\nstatus\tpass\nchecks\tdeterministic_scene,seed_isolation,task_graph,time_axis_independence,matrix,hash_vector,argument_guard,high_fps_reservation,memory_ceiling,four_million_csv,write_failure,adapter_preference\n");Write(o.output/L"frames.csv","sequence,qpc,frame_ms,cpu_ms,gpu_ms,latency_ms,valid,phase\n");Write(o.output/L"manifest.tsv",base+"completed\t1\nsoftware\t1\ndevice_lost\t0\nforeground_valid\t0\nself_test\t1\nwork_checksum\tselftest\nstartup_ms\t\nframes_sha256\t"+Sha256(o.output/L"frames.csv")+"\n");return 0;}
    bool completed=false,deviceLost=false;std::string error,reason="completed";size_t invalid=0,unexpectedFocus=0;uint64_t switches=0;int64_t measurementStart=0,measurementEnd=0,frameIntervalStart=0;double startup=-1,measurementSeconds=0,foregroundSeconds=0,controlledSeconds=0;std::vector<Frame> frames;std::vector<InputEvent> inputs;std::vector<std::string> focusEvents;std::unique_ptr<Renderer> renderer;std::unique_ptr<Scene> scene;
    try{
        MEMORYSTATUSEX sampleMemory{sizeof(sampleMemory)};if(!GlobalMemoryStatusEx(&sampleMemory))throw std::runtime_error("available sample memory query failed");size_t capacity=FrameCapacity(o.seconds,sampleMemory.ullAvailPhys);ULARGE_INTEGER freeSpace{};if(!GetDiskFreeSpaceExW(o.output.c_str(),&freeSpace,nullptr,nullptr)||freeSpace.QuadPart<FrameStorageBytes(capacity))throw std::runtime_error("insufficient evidence disk space for reserved frame capacity");Window window(o,L"Pavise 3D calibration — Esc cancels");scene=std::make_unique<Scene>(o);renderer=o.api=="dx11"?MakeDx11(window.value,o):MakeDx12(window.value,o);scene->fixedChecksum=Hash(&renderer->vramAllocated,sizeof(renderer->vramAllocated),scene->fixedChecksum);if(o.scene=="memory"){const char prepPlan[]="fixed_complete_ring_pretouch_v1";scene->fixedChecksum=Hash(prepPlan,sizeof(prepPlan),scene->fixedChecksum);}
        std::vector<Frame> warm;scene->reset();scene->update(0);int64_t startupMarker=Qpc();PostMessageW(window.value,WM_APP+31,1,0);Pump();renderer->draw(*scene,scene->constants(0),warm,SIZE_MAX);if(window.state.marker!=1||window.state.received<startupMarker)throw std::runtime_error("startup input marker was not dispatched");startup=Millis(Qpc()-creation);
        Child child(o);HWND helper=child.uiWindow();std::ostringstream ready;ready<<"schema\t1\nnonce\t"<<o.nonce<<"\npid\t"<<GetCurrentProcessId()<<"\nbirth\t"<<birth<<"\nqpc\t"<<Qpc()<<"\nbackground_load\t"<<o.background<<"\n"<<child.metadata();AtomicWrite(o.output/L"ready.tsv",ready.str());
        if(o.waitStart){int64_t limit=Qpc()+Frequency()*60;while(!cancelled&&!window.state.closed&&GetFileAttributesW((o.output/L"start.flag").c_str())==INVALID_FILE_ATTRIBUTES){Pump();if(Qpc()>limit)throw std::runtime_error("start.flag deadline exceeded 60 seconds");Sleep(10);}}
        if(!o.hidden) {
            ShowWindow(window.value,SW_SHOW);UpdateWindow(window.value);
            // The controller grants this owned PID before releasing start.flag. An initial
            // background launch may still need explicit operator activation. Wait before
            // starting any sampled work; never weaken the foreground checks below.
            if(GetForegroundWindow()!=window.value) {
                SetForegroundWindow(window.value);
                if(GetForegroundWindow()!=window.value) {
                    AtomicWrite(o.output/L"foreground-awaiting.tsv","nonce\t"+o.nonce+"\npid\t"+std::to_string(GetCurrentProcessId())+"\nhwnd\t"+std::to_string(uint64_t(reinterpret_cast<uintptr_t>(window.value)))+"\n");
                    int64_t activationEnd=Qpc()+Frequency()*30;
                    while(!cancelled&&!window.state.closed&&GetForegroundWindow()!=window.value&&Qpc()<activationEnd){Pump();Sleep(10);}
                }
            }
            if(GetForegroundWindow()!=window.value){watch.set("foreground_lost");cancelled=true;throw std::runtime_error("foreground activation was not acquired before warmup");}
            AtomicWrite(o.output/L"foreground-acquired.tsv","nonce\t"+o.nonce+"\npid\t"+std::to_string(GetCurrentProcessId())+"\nqpc\t"+std::to_string(Qpc())+"\n");
        }
        if(cancelled||window.state.closed)throw std::runtime_error("cancelled before warmup");child.start();
        int64_t warmEnd=Qpc()+Frequency()*2,warmLastStart=0;uint64_t warmFrame=0;while(Qpc()<warmEnd&&!cancelled){Pump();if(!o.hidden&&(GetForegroundWindow()!=window.value||IsIconic(window.value))){watch.set("foreground_lost");cancelled=true;break;}warmLastStart=Qpc();scene->update(warmFrame);renderer->draw(*scene,scene->constants(warmFrame),warm,SIZE_MAX);++warmFrame;}
        if(cancelled)throw std::runtime_error("warmup interrupted");if(!warmLastStart)throw std::runtime_error("warmup produced no frame start");
        renderer->finish(warm);scene->reset();scene->update(0,0);renderer->resetMaterialCycle();
        // A fixed complete sweep makes every memory-ring texture initialized and sampled,
        // independent of how many frames either arm rendered during the timed warmup.
        if(o.scene=="memory"){
            const Constants prepConstants=scene->constants(0,0);const uint64_t prepCount=renderer->vramAllocated/(uint64_t(TextureWidth)*TextureWidth*4);
            for(uint64_t i=0;i<prepCount;i++){
                Pump();if(cancelled||window.state.closed)throw std::runtime_error("memory preparation interrupted");
                if(!o.hidden&&(GetForegroundWindow()!=window.value||IsIconic(window.value))){watch.set("foreground_lost");cancelled=true;throw std::runtime_error("memory preparation lost foreground");}
                warmLastStart=Qpc();renderer->draw(*scene,prepConstants,warm,SIZE_MAX);++renderer->memoryPrepFrames;
            }
            renderer->finish(warm);
        }
        renderer->resetMaterialCycle();scene->reset();frames.reserve(capacity);inputs.reserve(std::min<size_t>(capacity/30+1,150000));
        std::string observedAttributes=Attributes();Write(o.output/L"measuring.tsv","nonce\t"+o.nonce+"\nqpc\t"+std::to_string(Qpc())+"\n"+observedAttributes);
        int64_t begin=Qpc(),previous=warmLastStart,nextWake=begin,phaseStart=begin;measurementStart=begin;frameIntervalStart=warmLastStart;uint64_t sequence=0;double nextMarker=0;bool helperForeground=false;
        focusEvents.push_back("initial,0,"+std::to_string(begin)+",renderer");
        while(Millis(Qpc()-begin)<o.seconds*1000&&!cancelled&&!window.state.closed){
            if(FrameCapacityReached(frames.size(),capacity)){reason="sample_capacity_reached";break;}if(!child.alive())throw std::runtime_error("background child exited unexpectedly");
            if(!o.hidden&&GetForegroundWindow()!=(helperForeground?helper:window.value)){++unexpectedFocus;reason="foreground_lost";break;}
            double phaseSeconds=Millis(Qpc()-begin)/1000;bool plannedHelper=o.scene=="switch"&&(uint64_t(phaseSeconds/2)%2==1);
            if(o.scene=="switch"&&plannedHelper!=helperForeground){
                if(!o.hidden){
                    if(plannedHelper){DWORD helperPid=0;GetWindowThreadProcessId(helper,&helperPid);AllowSetForegroundWindow(helperPid);PostMessageW(helper,WM_APP+32,1,0);}
                    else {SetForegroundWindow(window.value);PostMessageW(helper,WM_APP+32,0,0);}
                    int64_t focusLimit=Qpc()+Frequency()/2;
                    while(GetForegroundWindow()!=(plannedHelper?helper:window.value)&&Qpc()<focusLimit&&!cancelled){Pump();Sleep(1);}
                    if(GetForegroundWindow()!=(plannedHelper?helper:window.value)){++unexpectedFocus;reason="foreground_lost";break;}
                }
                int64_t at=Qpc();double phaseDuration=Millis(at-phaseStart)/1000;if(helperForeground)controlledSeconds+=phaseDuration;else foregroundSeconds+=phaseDuration;phaseStart=at;
                helperForeground=plannedHelper;++switches;
                focusEvents.push_back("transition,"+std::to_string(sequence)+","+std::to_string(at)+","+(plannedHelper?"helper":"renderer"));
            }
            int64_t posted=0;uint64_t tag=0;if(phaseSeconds>=nextMarker){tag=uint64_t(phaseSeconds*4)+1;nextMarker=double(tag)/4;posted=Qpc();PostMessageW(window.value,WM_APP+31,WPARAM(tag),LPARAM(posted));}Pump();int64_t start=Qpc();
            HWND foreground=GetForegroundWindow();bool focus=foreground==window.value&&IsWindowVisible(window.value)&&!IsIconic(window.value);bool intended=plannedHelper?foreground==helper:focus;
            if(!o.hidden&&!intended){++unexpectedFocus;reason="foreground_lost";break;}
            int64_t sampled=Qpc();uint64_t sampledTag=window.state.marker;double elapsed=Millis(start-begin)/1000;scene->update(sequence,elapsed);Constants c=scene->constants(sequence,elapsed);c.settings[1]=float(sampledTag&1);int64_t cpuEnd=Qpc();Frame f;f.sequence=sequence;f.qpc=start;f.frameMs=Millis(start-previous);f.cpuMs=Millis(cpuEnd-start);f.controlledBackground=plannedHelper;f.valid=previous&&intended&&!o.hidden&&!renderer->software;previous=start;frames.push_back(f);renderer->draw(*scene,c,frames,frames.size()-1);int64_t presented=Qpc();
            WindowState& inputState=window.state;if(posted&&inputState.marker==tag){inputs.push_back({tag,sequence,posted,inputState.received,sampled,presented,plannedHelper});if(sampledTag==tag&&inputState.received>=posted&&sampled>=inputState.received)frames.back().latencyMs=Millis(presented-posted);}
            if(!o.hidden&&GetForegroundWindow()!=(plannedHelper?helper:window.value)){++unexpectedFocus;frames.back().valid=false;reason="foreground_lost";break;}
            if(o.scene=="wake"){nextWake+=Frequency()/60;int64_t remaining=nextWake-Qpc();if(remaining>Frequency()/1000)Sleep(DWORD(std::max<int64_t>(1,remaining*1000/Frequency())));}
            ++sequence;
        }
        measurementEnd=Qpc();measurementSeconds=Millis(measurementEnd-begin)/1000;double finalPhase=Millis(measurementEnd-phaseStart)/1000;if(helperForeground)controlledSeconds+=finalPhase;else foregroundSeconds+=finalPhase;
        focusEvents.push_back("end,"+std::to_string(frames.size())+","+std::to_string(measurementEnd)+","+(helperForeground?"helper":"renderer"));
        renderer->finish(frames);if(cancelled||window.state.closed)reason=watch.why();completed=reason=="completed";
        if(o.scene=="switch"&&!o.hidden)PostMessageW(helper,WM_APP+32,0,0);
        Write(o.output/L"attributes.tsv",observedAttributes);Write(o.output/L"attributes-final.tsv",Attributes());
    }catch(const HrError& e){error=e.what();reason="graphics_failure";deviceLost=e.hr==DXGI_ERROR_DEVICE_REMOVED||e.hr==DXGI_ERROR_DEVICE_RESET||e.hr==DXGI_ERROR_DEVICE_HUNG;}catch(const std::exception& e){error=e.what();reason=cancelled?watch.why():"failed";}
    // Stream only after measurement. Do not hold a second, potentially 768 MB CSV
    // string beside the frame buffer. Full-disk/open/write/flush failures propagate;
    // the initial completed=0 manifest remains in force until every output succeeds.
    try {std::ofstream csv(o.output/L"frames.csv",std::ios::binary|std::ios::trunc);csv.exceptions(std::ios::badbit|std::ios::failbit);invalid=WriteFrameCsv(csv,frames);csv.flush();}
    catch(const std::ios_base::failure& e){throw std::runtime_error(std::string("frame evidence write/flush failed: ")+e.what());}
    std::ostringstream events;events<<"tag,sequence,posted_qpc,received_qpc,simulation_sample_qpc,present_return_qpc,marker_to_present_return_ms,target\n"<<std::setprecision(12);for(auto& e:inputs)events<<e.tag<<','<<e.sequence<<','<<e.posted<<','<<e.received<<','<<e.sampled<<','<<e.presented<<','<<Millis(e.presented-e.posted)<<','<<(e.helper?"renderer_controlled_background_marker":"renderer_material_marker")<<'\n';Write(o.output/L"input-events.csv",events.str());std::string focuses="kind,sequence,qpc,target\n";for(auto& e:focusEvents)focuses+=e+"\n";Write(o.output/L"focus-events.csv",focuses);
    std::ostringstream manifest;manifest<<base<<"adapter_luid\t"<<(renderer?renderer->adapterLuid:"")<<"\nadapter_name\t"<<(renderer?Clean(renderer->adapterName):"")<<"\nsoftware\t"<<(!renderer||renderer->software?1:0)<<"\ntearing\t"<<(renderer&&renderer->tearing?1:0)<<"\ncompleted\t"<<(completed?1:0)<<"\ndevice_lost\t"<<(deviceLost?1:0)<<"\nwork_checksum\t"<<(scene?Hex(scene->fixedChecksum):"")<<"\nwork_checksum_algorithm\tfnv1a64_plan_mesh_shader\nstartup_ms\t";if(startup>=0)manifest<<std::setprecision(12)<<startup;manifest<<"\nstartup_scope\tprocess_birth_to_first_present_return_before_controller_apply\nforeground_valid\t"<<(!o.hidden&&renderer&&!renderer->software&&unexpectedFocus==0&&frames.size()>1?1:0)<<"\nhidden\t"<<(o.hidden?1:0)<<"\nreason\t"<<reason<<"\nerror\t"<<Clean(error)<<"\nframe_count\t"<<frames.size()<<"\nmeasured_frame_count\t"<<(frames.empty()?0:frames.size()-1)<<"\nfirst_frame_role\twarmup_boundary_not_statistics"<<"\nframe_interval_start_qpc\t"<<frameIntervalStart<<"\nmeasurement_start_qpc\t"<<measurementStart<<"\nmeasurement_end_qpc\t"<<measurementEnd<<"\nmeasurement_seconds\t"<<measurementSeconds<<"\nforeground_seconds\t"<<foregroundSeconds<<"\ncontrolled_background_seconds\t"<<controlledSeconds<<"\ninvalid_frames\t"<<invalid<<"\nunexpected_focus_frames\t"<<unexpectedFocus<<"\ncontrolled_focus_transitions\t"<<switches<<"\nframes_sha256\t"<<Sha256(o.output/L"frames.csv")<<"\ninput_events_sha256\t"<<Sha256(o.output/L"input-events.csv")<<"\nfocus_events_sha256\t"<<Sha256(o.output/L"focus-events.csv")<<"\nlatency_source\tmarker_to_present_return_not_display_latency\nframe_interval_source\tcpu_frame_start_qpc_not_displayed_time\ncpu_ms_scope\tsimulation_tasks_geometry_material_before_submit\ngpu_ms_scope\tgpu_commands_upload_and_render_timestamp\nbackground_load\t"<<o.background<<"\nbackground_api\t"<<(o.background=="gpu"?"dx11":"")<<"\nworker_count\t"<<(scene?scene->workers():0)<<"\nstream_read_bytes\t"<<(scene?scene->ioBytes:0)<<"\nram_working_bytes\t"<<(scene?scene->ram.size():0)<<"\nvram_budget_bytes\t"<<(renderer?renderer->vramBudget:0)<<"\nvram_usage_before_bytes\t"<<(renderer?renderer->vramUsage:0)<<"\nvram_allocated_texture_bytes\t"<<(renderer?renderer->vramAllocated:0)<<"\nmemory_pretouch_frames\t"<<(renderer?renderer->memoryPrepFrames:0)<<"\nmaterial_cycle_start\t0\nmemory_preparation\t"<<(o.scene=="memory"?"fixed_complete_ring_upload_sample_drain_then_cursor_reset":"not_applicable")<<"\ngpu_missing_reported\t"<<(renderer?renderer->gpuMissing:0)<<"\ntexture_residency_strategy\tbounded_round_robin_no_forced_eviction\nshadow_method\tanalytic_soft_occlusion\nanimation_method\tprocedural_two_bone_limbs\nsimulation_clock\tmeasurement_qpc_seconds_with_stateless_fixed_work_prediction\nroute_duration_seconds\t60\nroute_seed\tfixed_v1_geometry_seed_above\nsimulation_tick_count\t"<<(scene?scene->simulationTick:0)<<"\nmarker_schedule\t250ms_measurement_time_frame_boundary_sampling\nthermal_sensor_source\tcontroller_required\naudio_supported\t0\naudio_reason\tnot_implemented\nui_response_source\tposted_to_WM_PAINT_return_not_display_latency\n"<<Attributes();for(const std::string role:{"cpu","io","gpu","ui"}){auto file=o.output/("child-result-"+role+".tsv");manifest<<role<<"_result_sha256\t"<<(std::filesystem::exists(file)?Sha256(file):"")<<"\n";}Write(o.output/L"manifest.final.tmp",manifest.str());if(!MoveFileExW((o.output/L"manifest.final.tmp").c_str(),(o.output/L"manifest.tsv").c_str(),MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH))throw std::runtime_error("final manifest atomic replacement failed");return completed?0:reason=="foreground_lost"?4:cancelled?3:1;
}
} // namespace bench
int wmain(int argc,wchar_t** argv){SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX|SEM_NOOPENFILEERRORBOX);try{return bench::Run(bench::Args(argc,argv));}catch(const std::exception& e){std::fprintf(stderr,"Pavise.Bench: %s\n",e.what());return 2;}}
