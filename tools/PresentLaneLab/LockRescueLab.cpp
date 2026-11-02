#define main PresentLaneUnusedMain
#include "PresentLaneLab.cpp"
#undef main
#include <wct.h>

static void DebugPrivilege() {
    HANDLE token=nullptr;Check(OpenProcessToken(GetCurrentProcess(),TOKEN_ADJUST_PRIVILEGES|TOKEN_QUERY,&token)!=0,"own token");
    TOKEN_PRIVILEGES privileges={};privileges.PrivilegeCount=1;
    Check(LookupPrivilegeValueW(nullptr,L"SeDebugPrivilege",&privileges.Privileges[0].Luid)!=0,"debug privilege lookup");
    privileges.Privileges[0].Attributes=SE_PRIVILEGE_ENABLED;SetLastError(0);
    BOOL ok=AdjustTokenPrivileges(token,FALSE,&privileges,0,nullptr,nullptr);DWORD error=GetLastError();CloseHandle(token);
    Check(ok&&error==0,"enable debug privilege in owned lab process");
}
static DWORD SameProcessOwner(HWCT session,DWORD root,unsigned* nodesSeen) {
    WAITCHAIN_NODE_INFO nodes[16]={};DWORD count=16;BOOL cycle=FALSE;
    if(!GetThreadWaitChain(session,0,0,root,&count,nodes,&cycle)||cycle||count<3||count>16)return 0;
    *nodesSeen=count;
    if(nodes[0].ObjectType!=WctThreadType||nodes[0].ThreadObject.ThreadId!=root||nodes[0].ThreadObject.ProcessId!=GetCurrentProcessId())return 0;
    if(nodes[1].ObjectType!=WctMutexType&&nodes[1].ObjectType!=WctCriticalSectionType)return 0;
    if(nodes[1].ObjectStatus==WctStatusNoAccess||nodes[1].ObjectStatus==WctStatusError||nodes[1].ObjectStatus==WctStatusAbandoned)return 0;
    if(nodes[2].ObjectType!=WctThreadType||nodes[2].ThreadObject.ProcessId!=GetCurrentProcessId()||nodes[2].ObjectStatus==WctStatusNoAccess)return 0;
    return nodes[2].ThreadObject.ThreadId;
}
static int LockRun(const std::string& arm,const std::string& layout,bool sleeps,const std::string& output) {
    DebugPrivilege();auto cores=PhysicalCores();ULONG_PTR mask=First(cores.back());
    std::atomic<bool> stop{false},measuring{false},ready{false};std::atomic<DWORD> root{0},owner{0};
    HANDLE mutex=CreateMutexW(nullptr,FALSE,nullptr),request=CreateEventW(nullptr,FALSE,FALSE,nullptr),locked=CreateEventW(nullptr,FALSE,FALSE,nullptr);
    Check(mutex&&request&&locked,"own synchronization");std::vector<double> frames;
    std::atomic<bool> watchdogDone{false};std::thread watchdog([&]{for(int i=0;i<300&&!watchdogDone;++i)Sleep(100);if(!watchdogDone)TerminateProcess(GetCurrentProcess(),124);});
    std::thread worker([&]{if(layout=="same")SetThreadAffinityMask(GetCurrentThread(),mask);SetThreadPriority(GetCurrentThread(),THREAD_PRIORITY_BELOW_NORMAL);owner=GetCurrentThreadId();
        while(!stop){if(WaitForSingleObject(request,200)!=WAIT_OBJECT_0)continue;WaitForSingleObject(mutex,INFINITE);SetEvent(locked);if(sleeps)Sleep(10);else Work(900000);ReleaseMutex(mutex);}
        SetThreadPriority(GetCurrentThread(),THREAD_PRIORITY_NORMAL);});
    std::thread renderer([&]{if(layout=="same")SetThreadAffinityMask(GetCurrentThread(),mask);SetThreadPriority(GetCurrentThread(),THREAD_PRIORITY_ABOVE_NORMAL);root=GetCurrentThreadId();double before=Seconds();bool previous=false;
        while(!stop){SetEvent(request);if(WaitForSingleObject(locked,200)!=WAIT_OBJECT_0)continue;WaitForSingleObject(mutex,INFINITE);Work(60000);ReleaseMutex(mutex);Sleep(1);double now=Seconds();bool m=measuring;if(m&&previous)frames.push_back((now-before)*1000);previous=m;before=now;}
        SetThreadPriority(GetCurrentThread(),THREAD_PRIORITY_NORMAL);});
    std::thread hog([&]{if(layout=="same")SetThreadAffinityMask(GetCurrentThread(),mask);while(!stop)Work(60000);});
    HWCT session=nullptr;unsigned queries=0,hits=0,promotions=0,nodes=0;double maxQuery=0,totalQuery=0;bool promoted=false;DWORD lastOwner=0;int confirmations=0;
    double started=0,elapsed=0;int result=0;bool restored=false;
    try {
        Check(arm=="baseline"||arm=="rescue","known lock arm");
        double deadline=Seconds()+5;while(!root||!owner){Check(Seconds()<deadline,"owned workers ready");Sleep(10);}
        session=OpenThreadWaitChainSession(0,nullptr);Check(session!=nullptr,"WCT session");Sleep(800);measuring=true;started=Seconds();
        while(Seconds()-started<6){double q0=Seconds();DWORD found=SameProcessOwner(session,root,&nodes);double cost=(Seconds()-q0)*1000;maxQuery=std::max(maxQuery,cost);totalQuery+=cost;++queries;
            if(found){++hits;Check(found==owner,"WCT returned owned lock worker");}
            if(found==lastOwner&&found)++confirmations;else{lastOwner=found;confirmations=found?1:0;}
            if(arm=="rescue"){
                if(found&&confirmations>=2&&!promoted){Check(SetThreadPriority(worker.native_handle(),THREAD_PRIORITY_ABOVE_NORMAL)!=0,"rescue verified owner");promoted=true;++promotions;}
                if(!found&&promoted){Check(SetThreadPriority(worker.native_handle(),THREAD_PRIORITY_BELOW_NORMAL)!=0,"release owner rescue");promoted=false;}
            }
            Sleep(10);
        }
        measuring=false;elapsed=Seconds()-started;Check(SetThreadPriority(worker.native_handle(),THREAD_PRIORITY_BELOW_NORMAL)!=0,"restore owner priority");
        restored=GetThreadPriority(worker.native_handle())==THREAD_PRIORITY_BELOW_NORMAL;
    }catch(const std::exception& e){fprintf(stderr,"%s\n",e.what());result=1;}
    measuring=false;stop=true;SetEvent(request);SetEvent(locked);SetThreadPriority(worker.native_handle(),THREAD_PRIORITY_ABOVE_NORMAL);
    renderer.join();worker.join();hog.join();if(session)CloseThreadWaitChainSession(session);CloseHandle(mutex);CloseHandle(request);CloseHandle(locked);watchdogDone=true;watchdog.join();
    std::ofstream file(output);file<<"arm,layout,sleeping_owner,pid,frame_tid,owner_tid,queries,owner_hits,promotions,max_nodes,mean_query_ms,max_query_ms,samples,seconds,loops_per_second,p50_ms,p99_ms,max_ms,restored\n";
    file<<arm<<","<<layout<<","<<sleeps<<","<<GetCurrentProcessId()<<","<<root<<","<<owner<<","<<queries<<","<<hits<<","<<promotions<<","<<nodes<<","<<(queries?totalQuery/queries:0)<<","<<maxQuery<<","<<frames.size()<<","<<elapsed<<","<<(elapsed?frames.size()/elapsed:0)<<","<<Percentile(frames,.5)<<","<<Percentile(frames,.99)<<","<<Percentile(frames,1)<<","<<restored<<"\n";
    if(!restored)result=1;return result;
}
int main(int argc,char** argv){try{Check(argc==5,"usage: baseline/rescue same/free sleeping output");return LockRun(argv[1],argv[2],std::string(argv[3])=="true",argv[4]);}catch(const std::exception& e){fprintf(stderr,"%s\n",e.what());return 1;}}
