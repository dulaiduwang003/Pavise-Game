#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <evntrace.h>
#include <evntcons.h>
#include <d3d9.h>
#include <objbase.h>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <fstream>
#include <map>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

static const GUID Dxgi = {0xca11c036,0x0102,0x4a2d,{0xa6,0xad,0xf0,0x3c,0xfe,0xd5,0xd3,0xc9}};
static const GUID D3d9 = {0x783aca0a,0x790e,0x4d7f,{0x84,0x51,0xaa,0x85,0x05,0x11,0xc6,0xb9}};
static void Check(bool value, const char* text) { if (!value) throw std::runtime_error(std::string(text)+" error="+std::to_string(GetLastError())); }
static double Seconds() { LARGE_INTEGER q,f; QueryPerformanceCounter(&q); QueryPerformanceFrequency(&f); return double(q.QuadPart)/f.QuadPart; }
static ULONGLONG Cpu(HANDLE h) { FILETIME c,e,k,u; Check(GetThreadTimes(h,&c,&e,&k,&u)!=0,"thread times"); return (ULONGLONG(k.dwHighDateTime)<<32|k.dwLowDateTime)+(ULONGLONG(u.dwHighDateTime)<<32|u.dwLowDateTime); }
static thread_local volatile ULONGLONG sink;
static void Work(unsigned n) { ULONGLONG v=123456789; for(unsigned i=0;i<n;++i) v=(v^(v>>11))*6364136223846793005ULL+1; sink=v; }

class PresentCapture {
    TRACEHANDLE session_=0, consumer_=INVALID_PROCESSTRACE_HANDLE;
    std::wstring name_;
    std::vector<BYTE> properties_;
    std::thread pump_;
    std::mutex mutex_;
    std::map<DWORD,unsigned> starts_, completed_;
    static void WINAPI Event(EVENT_RECORD* r) { static_cast<PresentCapture*>(r->UserContext)->Receive(*r); }
    void Receive(const EVENT_RECORD& r) {
        if(r.EventHeader.ProcessId!=GetCurrentProcessId()) return;
        bool dxgi=IsEqualGUID(r.EventHeader.ProviderId,Dxgi), d3d9=IsEqualGUID(r.EventHeader.ProviderId,D3d9);
        if(!dxgi&&!d3d9) return;
        USHORT id=r.EventHeader.EventDescriptor.Id;
        DWORD tid=r.EventHeader.ThreadId;
        if(!tid) return;
        std::lock_guard<std::mutex> l(mutex_);
        if((dxgi&&id==42)||(d3d9&&id==1)) {
            if(starts_.size()<256) starts_[tid]=id;
        } else if((dxgi&&id==43)||(d3d9&&id==2)) {
            auto it=starts_.find(tid);
            if(it==starts_.end()||it->second!=(dxgi?42u:1u)) return;
            starts_.erase(it);
            if(r.UserDataLength<sizeof(HRESULT)) return;
            HRESULT result; memcpy(&result,r.UserData,sizeof result);
            if(SUCCEEDED(result)) ++completed_[tid];
        }
    }
public:
    ULONG Start() {
        name_=L"Pavise.PresentLaneLab."+std::to_wstring(GetCurrentProcessId())+L"."+std::to_wstring(GetTickCount64());
        properties_.resize(sizeof(EVENT_TRACE_PROPERTIES)+(name_.size()+1)*sizeof(wchar_t));
        auto p=reinterpret_cast<EVENT_TRACE_PROPERTIES*>(properties_.data());
        p->Wnode.BufferSize=ULONG(properties_.size()); p->Wnode.Flags=WNODE_FLAG_TRACED_GUID; p->Wnode.ClientContext=1;
        p->LogFileMode=EVENT_TRACE_REAL_TIME_MODE; p->BufferSize=16; p->MinimumBuffers=4; p->MaximumBuffers=16; p->FlushTimer=1;
        p->LoggerNameOffset=sizeof(EVENT_TRACE_PROPERTIES);
        ULONG rc=StartTraceW(&session_,name_.c_str(),p); if(rc) {session_=0;return rc;}
        DWORD pid=GetCurrentProcessId(); EVENT_FILTER_DESCRIPTOR filter={};
        filter.Ptr=reinterpret_cast<ULONGLONG>(&pid);filter.Size=sizeof pid;filter.Type=0x80000004;
        ENABLE_TRACE_PARAMETERS params={};params.Version=ENABLE_TRACE_PARAMETERS_VERSION_2;
        params.EnableFilterDesc=&filter;params.FilterDescCount=1;
        rc=EnableTraceEx2(session_,&Dxgi,EVENT_CONTROL_CODE_ENABLE_PROVIDER,TRACE_LEVEL_VERBOSE,0x8000000000000002ULL,0,0,&params);
        if(!rc) rc=EnableTraceEx2(session_,&D3d9,EVENT_CONTROL_CODE_ENABLE_PROVIDER,TRACE_LEVEL_VERBOSE,0x8000000000000002ULL,0,0,&params);
        if(rc){Stop();return rc;}
        EVENT_TRACE_LOGFILEW log={};log.LoggerName=const_cast<LPWSTR>(name_.c_str());
        log.ProcessTraceMode=PROCESS_TRACE_MODE_REAL_TIME|PROCESS_TRACE_MODE_EVENT_RECORD;
        log.EventRecordCallback=Event;log.Context=this;
        consumer_=OpenTraceW(&log);if(consumer_==INVALID_PROCESSTRACE_HANDLE){rc=GetLastError();Stop();return rc;}
        TRACEHANDLE consumer=consumer_;
        pump_=std::thread([consumer]{TRACEHANDLE h=consumer;ProcessTrace(&h,1,nullptr,nullptr);});return 0;
    }
    std::map<DWORD,unsigned> Counts() { std::lock_guard<std::mutex> l(mutex_);return completed_; }
    DWORD Winner(unsigned* total) { auto c=Counts();DWORD result=0;unsigned largest=0;*total=0;for(auto x:c){*total+=x.second;if(x.second>largest){largest=x.second;result=x.first;}}return result; }
    ULONG Stop() {
        ULONG lost=0;
        if(session_){auto p=reinterpret_cast<EVENT_TRACE_PROPERTIES*>(properties_.data());ControlTraceW(session_,name_.c_str(),p,EVENT_TRACE_CONTROL_STOP);lost=p->EventsLost+p->RealTimeBuffersLost;session_=0;}
        if(consumer_!=INVALID_PROCESSTRACE_HANDLE){CloseTrace(consumer_);consumer_=INVALID_PROCESSTRACE_HANDLE;}
        if(pump_.joinable())pump_.join();return lost;
    }
    ~PresentCapture(){Stop();}
};

class WakeCapture {
    TRACEHANDLE session_=0,consumer_=INVALID_PROCESSTRACE_HANDLE;
    std::wstring name_;std::vector<BYTE> properties_;std::thread pump_;
    std::mutex mutex_;std::map<DWORD,unsigned> edges_;std::map<DWORD,bool> own_;
    std::map<unsigned,DWORD> running_;
    DWORD presenter_=0;
    std::map<unsigned,unsigned> opcodes_;unsigned targetEvents_=0,invalidSender_=0;
    static void WINAPI Event(EVENT_RECORD* r){static_cast<WakeCapture*>(r->UserContext)->Receive(*r);}
    void Receive(const EVENT_RECORD& r){
        ++opcodes_[r.EventHeader.EventDescriptor.Opcode];
        static const GUID threadGuid={0x3d6fa8d1,0xfe05,0x11d0,{0x9d,0xda,0,0xc0,0x4f,0xd7,0xba,0x7c}};
        if(!IsEqualGUID(r.EventHeader.ProviderId,threadGuid))return;
        unsigned cpu=r.BufferContext.ProcessorNumber;
        if(r.EventHeader.EventDescriptor.Opcode==36&&r.UserDataLength>=24){DWORD next;memcpy(&next,r.UserData,sizeof next);running_[cpu]=next;return;}
        if(r.EventHeader.EventDescriptor.Opcode!=50||r.UserDataLength<8)return;
        DWORD target;memcpy(&target,r.UserData,sizeof target);
        auto bytes=static_cast<const BYTE*>(r.UserData);
        if(target!=presenter_||(bytes[6]&1))return;
        ++targetEvents_;
        // ReadyThread 的头线程 ID 为 -1，需用同 CPU 的最近切换关联唤醒者。
        auto current=running_.find(cpu);
        DWORD sender=current==running_.end()?0:current->second;
        if(!sender||sender==presenter_||sender==MAXDWORD){++invalidSender_;return;}
        std::lock_guard<std::mutex> l(mutex_);
        auto found=own_.find(sender);
        if(found==own_.end()){
            if(own_.size()>=64)return;
            HANDLE h=OpenThread(THREAD_QUERY_LIMITED_INFORMATION,FALSE,sender);
            bool ours=h&&GetProcessIdOfThread(h)==GetCurrentProcessId();if(h)CloseHandle(h);
            own_[sender]=ours;if(!ours)return;
        }else if(!found->second)return;
        ++edges_[sender];
    }
public:
    ULONG Start(DWORD presenter){
        presenter_=presenter;name_=L"Pavise.WakeLaneLab."+std::to_wstring(GetCurrentProcessId())+L"."+std::to_wstring(GetTickCount64());
        properties_.resize(sizeof(EVENT_TRACE_PROPERTIES)+(name_.size()+1)*sizeof(wchar_t));
        auto p=reinterpret_cast<EVENT_TRACE_PROPERTIES*>(properties_.data());
        p->Wnode.BufferSize=ULONG(properties_.size());p->Wnode.Flags=WNODE_FLAG_TRACED_GUID;p->Wnode.ClientContext=1;CoCreateGuid(&p->Wnode.Guid);
        p->LogFileMode=EVENT_TRACE_REAL_TIME_MODE|EVENT_TRACE_SYSTEM_LOGGER_MODE;
        p->BufferSize=64;p->MinimumBuffers=8;p->MaximumBuffers=32;p->FlushTimer=1;
        p->EnableFlags=EVENT_TRACE_FLAG_CSWITCH|EVENT_TRACE_FLAG_THREAD|0x800;
        p->LoggerNameOffset=sizeof(EVENT_TRACE_PROPERTIES);
        ULONG rc=StartTraceW(&session_,name_.c_str(),p);if(rc){session_=0;return rc;}
        EVENT_TRACE_LOGFILEW log={};log.LoggerName=const_cast<LPWSTR>(name_.c_str());
        log.ProcessTraceMode=PROCESS_TRACE_MODE_REAL_TIME|PROCESS_TRACE_MODE_EVENT_RECORD;log.EventRecordCallback=Event;log.Context=this;
        consumer_=OpenTraceW(&log);if(consumer_==INVALID_PROCESSTRACE_HANDLE){rc=GetLastError();Stop();return rc;}
        TRACEHANDLE consumer=consumer_;
        pump_=std::thread([consumer]{TRACEHANDLE h=consumer;ProcessTrace(&h,1,nullptr,nullptr);});return 0;
    }
    ULONG Stop(){ULONG lost=0;if(session_){auto p=reinterpret_cast<EVENT_TRACE_PROPERTIES*>(properties_.data());ControlTraceW(session_,name_.c_str(),p,EVENT_TRACE_CONTROL_STOP);lost=p->EventsLost+p->RealTimeBuffersLost;session_=0;}
        if(consumer_!=INVALID_PROCESSTRACE_HANDLE){CloseTrace(consumer_);consumer_=INVALID_PROCESSTRACE_HANDLE;}if(pump_.joinable()){pump_.join();fprintf(stderr,"kernel cswitch=%u ready=%u own_target=%u invalid_sender=%u\n",opcodes_[36],opcodes_[50],targetEvents_,invalidSender_);}return lost;}
    DWORD Winner(unsigned* total){std::lock_guard<std::mutex> l(mutex_);DWORD best=0;unsigned count=0;*total=0;for(auto e:edges_){*total+=e.second;if(e.second>count){count=e.second;best=e.first;}}
        return count>=16&&count>=*total*.75?best:0;}
    ~WakeCapture(){Stop();}
};

static std::vector<ULONG_PTR> PhysicalCores() {
    Check(GetActiveProcessorGroupCount()==1,"prototype supports one processor group only");
    DWORD bytes=0;GetLogicalProcessorInformationEx(RelationProcessorCore,nullptr,&bytes);
    std::vector<BYTE> data(bytes);Check(GetLogicalProcessorInformationEx(RelationProcessorCore,reinterpret_cast<PSYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(data.data()),&bytes)!=0,"topology");
    DWORD_PTR allowed,system;Check(GetProcessAffinityMask(GetCurrentProcess(),&allowed,&system)!=0,"allowed CPUs");
    std::vector<ULONG_PTR> result;
    for(DWORD off=0;off<bytes;){auto p=reinterpret_cast<PSYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(data.data()+off);
        Check(p->Size>0&&off+p->Size<=bytes,"topology length");
        if(p->Processor.GroupCount==1&&p->Processor.GroupMask[0].Group==0){auto mask=p->Processor.GroupMask[0].Mask&allowed;if(mask)result.push_back(mask);}off+=p->Size;}
    Check(result.size()>=2,"at least two cores");return result;
}
static ULONG_PTR First(ULONG_PTR mask){return mask&(~mask+1);}
static double Percentile(std::vector<double> v,double q){if(v.empty())return NAN;std::sort(v.begin(),v.end());return v[size_t(std::ceil(q*v.size()))-1];}

struct Load {
    std::atomic<bool> stop{false}, measure{false}, failed{false};
    std::atomic<DWORD> frameTid{0},hogTid{0},workerTid{0};
    HANDLE job=CreateEventW(nullptr,FALSE,FALSE,nullptr),done=CreateEventW(nullptr,FALSE,FALSE,nullptr);
    std::vector<double> frames;
    unsigned presents=0,workerJobs=0;
    bool dependent=false;
    ~Load(){CloseHandle(job);CloseHandle(done);}
};

static int Run(const std::string& arm,const std::string& layout,bool dependent,double duration,const std::string& output) {
    auto cores=PhysicalCores();ULONG_PTR frameMask=First(cores.back()),hogMask=frameMask;
    if(layout=="spread")hogMask=First(cores[cores.size()-2]);
    if(layout=="smt"){hogMask=First(cores.back()&~frameMask);Check(hogMask!=0,"SMT sibling");}
    Load load;load.dependent=dependent;
    std::atomic<bool> watchdogDone{false};
    std::thread watchdog([&]{for(int i=0;i<300&&!watchdogDone;++i)Sleep(100);if(!watchdogDone)TerminateProcess(GetCurrentProcess(),124);});
    std::thread frame([&]{
        IDirect3D9* d3d=nullptr;IDirect3DDevice9* device=nullptr;HWND window=nullptr;
        int original=GetThreadPriority(GetCurrentThread());ULONG_PTR affinity=0;
        try {
            if(layout!="free"){affinity=SetThreadAffinityMask(GetCurrentThread(),frameMask);Check(affinity!=0,"frame affinity");}
            window=CreateWindowExW(0,L"STATIC",L"Pavise owned hidden test surface",WS_POPUP,0,0,32,32,nullptr,nullptr,GetModuleHandleW(nullptr),nullptr);Check(window!=nullptr,"hidden window");
            d3d=Direct3DCreate9(D3D_SDK_VERSION);Check(d3d!=nullptr,"D3D9");
            D3DPRESENT_PARAMETERS pp={};pp.BackBufferWidth=32;pp.BackBufferHeight=32;pp.BackBufferFormat=D3DFMT_UNKNOWN;
            pp.BackBufferCount=1;pp.SwapEffect=D3DSWAPEFFECT_DISCARD;pp.hDeviceWindow=window;pp.Windowed=TRUE;pp.PresentationInterval=D3DPRESENT_INTERVAL_IMMEDIATE;
            Check(SUCCEEDED(d3d->CreateDevice(D3DADAPTER_DEFAULT,D3DDEVTYPE_HAL,window,D3DCREATE_SOFTWARE_VERTEXPROCESSING,&pp,&device)),"owned graphics device");
            load.frameTid=GetCurrentThreadId();double previous=Seconds();bool wasMeasuring=false;
            while(!load.stop){
                if(dependent){SetEvent(load.job);DWORD wait=WaitForSingleObject(load.done,1000);if(wait!=WAIT_OBJECT_0)continue;}
                Work(60000);device->Clear(0,nullptr,D3DCLEAR_TARGET,D3DCOLOR_XRGB(0,0,0),1,0);
                HRESULT hr=device->Present(nullptr,nullptr,nullptr,nullptr);if(FAILED(hr)){load.failed=true;break;}
                ++load.presents;Sleep(1);double now=Seconds();bool measuring=load.measure;
                if(measuring&&wasMeasuring)load.frames.push_back((now-previous)*1000);previous=now;wasMeasuring=measuring;
            }
        }catch(...){load.failed=true;load.stop=true;}
        if(device)device->Release();if(d3d)d3d->Release();if(window)DestroyWindow(window);
        SetThreadPriority(GetCurrentThread(),original);if(affinity)SetThreadAffinityMask(GetCurrentThread(),affinity);
    });
    std::thread hog([&]{int original=GetThreadPriority(GetCurrentThread());ULONG_PTR affinity=0;
        if(layout!="free")affinity=SetThreadAffinityMask(GetCurrentThread(),hogMask);
        load.hogTid=GetCurrentThreadId();while(!load.stop)Work(60000);
        SetThreadPriority(GetCurrentThread(),original);if(affinity)SetThreadAffinityMask(GetCurrentThread(),affinity);});
    std::thread worker;
    if(dependent)worker=std::thread([&]{int original=GetThreadPriority(GetCurrentThread());ULONG_PTR affinity=0;
        if(layout!="free")affinity=SetThreadAffinityMask(GetCurrentThread(),hogMask);
        load.workerTid=GetCurrentThreadId();while(!load.stop){if(WaitForSingleObject(load.job,200)==WAIT_OBJECT_0){Work(600000);++load.workerJobs;SetEvent(load.done);}}
        SetThreadPriority(GetCurrentThread(),original);if(affinity)SetThreadAffinityMask(GetCurrentThread(),affinity);});
    int result=0;PresentCapture capture;WakeCapture wake;ULONG traceRc=0,lost=0,wakeRc=0,wakeLost=0;DWORD cpuWinner=0,eventWinner=0,wakeWinner=0;unsigned eventTotal=0,wakeTotal=0;double cpuShare=0;
    int originalFrame=0,originalHog=0,originalWorker=0;bool restored=false;double windowSeconds=0;
    try {
        double ready=Seconds();while(!load.frameTid||!load.hogTid||(dependent&&!load.workerTid)){Check(!load.failed&&Seconds()-ready<5,"worker initialization");Sleep(10);}
        originalFrame=GetThreadPriority(frame.native_handle());originalHog=GetThreadPriority(hog.native_handle());
        if(dependent)originalWorker=GetThreadPriority(worker.native_handle());
        traceRc=capture.Start();
        if(arm=="wake-group")wakeRc=wake.Start(load.frameTid);
        auto f0=Cpu(frame.native_handle()),h0=Cpu(hog.native_handle()),w0=dependent?Cpu(worker.native_handle()):0;
        Sleep(1600);auto fd=Cpu(frame.native_handle())-f0,hd=Cpu(hog.native_handle())-h0,wd=dependent?Cpu(worker.native_handle())-w0:0;
        auto total=fd+hd+wd;cpuWinner=hd>fd?DWORD(load.hogTid):DWORD(load.frameTid);auto largest=std::max(fd,hd);
        if(wd>largest){largest=wd;cpuWinner=load.workerTid;}cpuShare=total?double(largest)/total:0;
        lost=capture.Stop();eventWinner=capture.Winner(&eventTotal);
        if(arm=="wake-group"){wakeLost=wake.Stop();wakeWinner=wake.Winner(&wakeTotal);Check(!wakeRc&&!wakeLost&&wakeWinner!=0,"complete wake edge capture");Check(wakeWinner==load.workerTid,"discovered sender is the owned worker");}
        if(!traceRc)Check(!lost&&eventTotal>=8&&eventWinner==load.frameTid,"real runtime Present attribution");
        if(arm=="cpu-hot"){Check(cpuShare>=0.35,"CPU candidate share");HANDLE h=cpuWinner==load.hogTid?hog.native_handle():cpuWinner==load.frameTid?frame.native_handle():worker.native_handle();Check(SetThreadPriority(h,THREAD_PRIORITY_HIGHEST)!=0,"CPU-hot boost");}
        else if(arm=="present")Check(SetThreadPriority(frame.native_handle(),THREAD_PRIORITY_ABOVE_NORMAL)!=0,"present boost");
        else if(arm=="group"||arm=="wake-group"){Check(SetThreadPriority(frame.native_handle(),THREAD_PRIORITY_ABOVE_NORMAL)!=0,"present group boost");if(dependent)Check(SetThreadPriority(worker.native_handle(),THREAD_PRIORITY_ABOVE_NORMAL)!=0,"dependency boost");}
        else Check(arm=="baseline","known arm");
        Sleep(350);load.measure=true;double start=Seconds();Sleep(DWORD(duration*1000));load.measure=false;windowSeconds=Seconds()-start;
        Check(SetThreadPriority(frame.native_handle(),originalFrame)!=0&&SetThreadPriority(hog.native_handle(),originalHog)!=0,"restore priority");
        if(dependent)Check(SetThreadPriority(worker.native_handle(),originalWorker)!=0,"restore worker priority");
        restored=GetThreadPriority(frame.native_handle())==originalFrame&&GetThreadPriority(hog.native_handle())==originalHog&&(!dependent||GetThreadPriority(worker.native_handle())==originalWorker);
    }catch(const std::exception& e){fprintf(stderr,"%s\n",e.what());result=1;}
    load.measure=false;load.stop=true;SetEvent(load.job);SetEvent(load.done);
    SetThreadPriority(frame.native_handle(),THREAD_PRIORITY_NORMAL);SetThreadPriority(hog.native_handle(),THREAD_PRIORITY_NORMAL);if(dependent)SetThreadPriority(worker.native_handle(),THREAD_PRIORITY_NORMAL);
    frame.join();hog.join();if(worker.joinable())worker.join();capture.Stop();wake.Stop();watchdogDone=true;watchdog.join();
    std::ofstream file(output);file<<"arm,layout,dependent,pid,frame_tid,hog_tid,worker_tid,cpu_winner,cpu_share,present_winner,present_events,trace_rc,events_lost,wake_winner,wake_events,wake_rc,wake_lost,process_priority,samples,seconds,loops_per_second,p50_ms,p95_ms,p99_ms,max_ms,restored,failed\n";
    file<<arm<<","<<layout<<","<<dependent<<","<<GetCurrentProcessId()<<","<<load.frameTid<<","<<load.hogTid<<","<<load.workerTid<<","<<cpuWinner<<","<<cpuShare<<","<<eventWinner<<","<<eventTotal<<","<<traceRc<<","<<lost<<","<<wakeWinner<<","<<wakeTotal<<","<<wakeRc<<","<<wakeLost<<","<<GetPriorityClass(GetCurrentProcess())<<","<<load.frames.size()<<","<<windowSeconds<<","<<(windowSeconds?load.frames.size()/windowSeconds:0)<<","<<Percentile(load.frames,.5)<<","<<Percentile(load.frames,.95)<<","<<Percentile(load.frames,.99)<<","<<Percentile(load.frames,1)<<","<<restored<<","<<load.failed<<"\n";
    if(load.failed||!restored)result=1;return result;
}
int main(int argc,char** argv){try{Check(argc==6,"usage: arm layout dependent seconds output");Check(SetPriorityClass(GetCurrentProcess(),HIGH_PRIORITY_CLASS)!=0,"production-like process priority");return Run(argv[1],argv[2],std::string(argv[3])=="true",std::stod(argv[4]),argv[5]);}catch(const std::exception& e){fprintf(stderr,"%s\n",e.what());return 1;}}
