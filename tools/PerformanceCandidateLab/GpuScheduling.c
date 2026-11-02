#define COBJMACROS
#define INITGUID
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <d3dcompiler.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef LONG (WINAPI *PriorityRead)(HANDLE,int*);
typedef LONG (WINAPI *PriorityWrite)(HANDLE,int);
typedef HRESULT (WINAPI *CompileShader)(LPCVOID,SIZE_T,LPCSTR,const D3D_SHADER_MACRO*,ID3DInclude*,LPCSTR,LPCSTR,UINT,UINT,ID3DBlob**,ID3DBlob**);
typedef struct Workload { ID3D11Device *device; ID3D11DeviceContext *context; ID3D11ComputeShader *shader; ID3D11Buffer *buffer; ID3D11UnorderedAccessView *view; ID3D11Query *query; char adapter[256]; } Workload;
static LARGE_INTEGER frequency;
static PriorityRead read_priority;
static PriorityWrite write_priority;
static CompileShader compile_shader;
static double now(void){LARGE_INTEGER t;QueryPerformanceCounter(&t);return (double)t.QuadPart/frequency.QuadPart;}
static DWORD WINAPI watchdog(LPVOID unused){Sleep(30000);ExitProcess(124);return 0;}
static int good(HRESULT hr,const char *operation){if(FAILED(hr)){fprintf(stderr,"%s failed 0x%08lx\n",operation,(unsigned long)hr);return 0;}return 1;}
static void release_work(Workload *w){if(w->context){ID3D11DeviceContext_ClearState(w->context);ID3D11DeviceContext_Flush(w->context);}if(w->query)ID3D11Query_Release(w->query);if(w->view)ID3D11UnorderedAccessView_Release(w->view);if(w->buffer)ID3D11Buffer_Release(w->buffer);if(w->shader)ID3D11ComputeShader_Release(w->shader);if(w->context)ID3D11DeviceContext_Release(w->context);if(w->device)ID3D11Device_Release(w->device);memset(w,0,sizeof(*w));}
static int prepare(Workload *w,int iterations)
{
    IDXGIFactory1 *factory=NULL;IDXGIAdapter1 *adapter=NULL,*best=NULL;SIZE_T best_size=0;int ok=0;
    if(!good(CreateDXGIFactory1(&IID_IDXGIFactory1,(void**)&factory),"Factory"))goto done;
    for(UINT i=0;IDXGIFactory1_EnumAdapters1(factory,i,&adapter)!=DXGI_ERROR_NOT_FOUND;i++)
    {DXGI_ADAPTER_DESC1 desc;IDXGIAdapter1_GetDesc1(adapter,&desc);if(!(desc.Flags&DXGI_ADAPTER_FLAG_SOFTWARE)&&(!best||desc.DedicatedVideoMemory>best_size)){if(best)IDXGIAdapter1_Release(best);best=adapter;best_size=desc.DedicatedVideoMemory;adapter=NULL;}if(adapter){IDXGIAdapter1_Release(adapter);adapter=NULL;}}
    if(!best)goto done;
    DXGI_ADAPTER_DESC1 desc;IDXGIAdapter1_GetDesc1(best,&desc);WideCharToMultiByte(CP_UTF8,0,desc.Description,-1,w->adapter,sizeof(w->adapter),NULL,NULL);
    D3D_FEATURE_LEVEL levels[]={D3D_FEATURE_LEVEL_11_0};D3D_FEATURE_LEVEL actual;
    if(!good(D3D11CreateDevice((IDXGIAdapter*)best,D3D_DRIVER_TYPE_UNKNOWN,NULL,0,levels,1,D3D11_SDK_VERSION,&w->device,&actual,&w->context),"Device"))goto done;
    char source[2048];sprintf(source,"RWStructuredBuffer<uint> output : register(u0); [numthreads(256,1,1)] void main(uint3 id:SV_DispatchThreadID) { uint x=id.x+output[id.x]; [loop] for(uint j=0;j<%d;j++) x=(x^(x>>9))*1664525u+1013904223u; output[id.x]=x; }",iterations);
    ID3DBlob *code=NULL,*errors=NULL;HRESULT hr=compile_shader(source,strlen(source),"PaviseOwnedCompute",NULL,NULL,"main","cs_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&code,&errors);
    if(errors){if(FAILED(hr))fprintf(stderr,"Shader: %s\n",(char*)ID3D10Blob_GetBufferPointer(errors));ID3D10Blob_Release(errors);}if(!good(hr,"Compile"))goto done;
    hr=ID3D11Device_CreateComputeShader(w->device,ID3D10Blob_GetBufferPointer(code),ID3D10Blob_GetBufferSize(code),NULL,&w->shader);ID3D10Blob_Release(code);if(!good(hr,"Shader"))goto done;
    D3D11_BUFFER_DESC buffer={0};buffer.ByteWidth=65536*sizeof(UINT);buffer.Usage=D3D11_USAGE_DEFAULT;buffer.BindFlags=D3D11_BIND_UNORDERED_ACCESS;buffer.MiscFlags=D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;buffer.StructureByteStride=sizeof(UINT);
    if(!good(ID3D11Device_CreateBuffer(w->device,&buffer,NULL,&w->buffer),"Buffer"))goto done;
    D3D11_UNORDERED_ACCESS_VIEW_DESC view={0};view.Format=DXGI_FORMAT_UNKNOWN;view.ViewDimension=D3D11_UAV_DIMENSION_BUFFER;view.Buffer.NumElements=65536;
    if(!good(ID3D11Device_CreateUnorderedAccessView(w->device,(ID3D11Resource*)w->buffer,&view,&w->view),"View"))goto done;
    UINT zero[4]={0};ID3D11DeviceContext_ClearUnorderedAccessViewUint(w->context,w->view,zero);
    D3D11_QUERY_DESC query={D3D11_QUERY_EVENT,0};if(!good(ID3D11Device_CreateQuery(w->device,&query,&w->query),"Query"))goto done;
    ID3D11DeviceContext_CSSetShader(w->context,w->shader,NULL,0);ID3D11DeviceContext_CSSetUnorderedAccessViews(w->context,0,1,&w->view,NULL);ok=1;
done:if(best)IDXGIAdapter1_Release(best);if(factory)IDXGIFactory1_Release(factory);if(!ok)release_work(w);return ok;
}
static int dispatch(Workload *w,double deadline,HANDLE stop)
{
    ID3D11DeviceContext_Dispatch(w->context,256,1,1);ID3D11DeviceContext_End(w->context,(ID3D11Asynchronous*)w->query);ID3D11DeviceContext_Flush(w->context);
    BOOL completed=FALSE;
    for(;;){HRESULT hr=ID3D11DeviceContext_GetData(w->context,(ID3D11Asynchronous*)w->query,&completed,sizeof(completed),D3D11_ASYNC_GETDATA_DONOTFLUSH);if(hr==S_OK&&completed)return 1;if(FAILED(hr)){good(hr,"GPU completion");return 0;}if(now()>deadline){fprintf(stderr,"GPU completion timeout\n");return 0;}if(stop&&WaitForSingleObject(stop,0)==0)return 2;Sleep(0);}
}
static int compare_double(const void *a,const void *b){double aa=*(const double*)a,bb=*(const double*)b;return (aa>bb)-(aa<bb);}
static double percentile(double *samples,int count,double p){return count?samples[(int)(p*(count-1))]:0;}
static int child(int argc,char **argv)
{
    if(argc!=7)return 2;int desired=atoi(argv[2]),original=0,actual=0,clean=0,ok=0,original_known=0;Workload w={0};HANDLE ready=OpenEventA(EVENT_MODIFY_STATE,FALSE,argv[3]),go=OpenEventA(SYNCHRONIZE,FALSE,argv[4]),stop=OpenEventA(SYNCHRONIZE,FALSE,argv[5]);
    if(!ready||!go||!stop||read_priority(GetCurrentProcess(),&original)!=0)goto done;
    original_known=1;
    if(!prepare(&w,8192)||write_priority(GetCurrentProcess(),desired)!=0||read_priority(GetCurrentProcess(),&actual)!=0||actual!=desired)goto done;
    SetEvent(ready);if(WaitForSingleObject(go,5000)!=WAIT_OBJECT_0)goto done;double begin=now();int loops=0;
    while(WaitForSingleObject(stop,0)!=0&&now()-begin<24){int status=dispatch(&w,begin+25,stop);if(status==2)break;if(status!=1)goto done;loops++;}
    FILE *file=fopen(argv[6],"wb");if(!file)goto done;fprintf(file,"{\"loops\":%d,\"seconds\":%.9f,\"gpuPriority\":%d,\"adapter\":\"%s\"}",loops,now()-begin,desired,w.adapter);fclose(file);ok=1;
done:if(original_known&&read_priority(GetCurrentProcess(),&actual)==0)clean=actual==original||(write_priority(GetCurrentProcess(),original)==0&&read_priority(GetCurrentProcess(),&actual)==0&&actual==original);release_work(&w);if(ready)CloseHandle(ready);if(go)CloseHandle(go);if(stop)CloseHandle(stop);return ok&&clean?0:1;
}
int main(int argc,char **argv)
{
    QueryPerformanceFrequency(&frequency);HANDLE watch=CreateThread(NULL,0,watchdog,NULL,0,NULL);if(watch)CloseHandle(watch);
    HMODULE gdi=LoadLibraryA("gdi32.dll");read_priority=(PriorityRead)GetProcAddress(gdi,"D3DKMTGetProcessSchedulingPriorityClass");write_priority=(PriorityWrite)GetProcAddress(gdi,"D3DKMTSetProcessSchedulingPriorityClass");if(!read_priority||!write_priority)return 2;
    HMODULE compiler=LoadLibraryA("d3dcompiler_47.dll");compile_shader=(CompileShader)GetProcAddress(compiler,"D3DCompile");if(!compile_shader)return 2;
    if(argc>1&&!strcmp(argv[1],"--child"))return child(argc,argv);
    if(argc!=6||strcmp(argv[1],"gpu"))return 2;int competitor=!strcmp(argv[2],"idle")?0:!strcmp(argv[2],"normal")?2:!strcmp(argv[2],"none")?-1:-2;
    int desired=!strcmp(argv[3],"baseline")?4:!strcmp(argv[3],"candidate")?5:-1;double seconds=atof(argv[4]);if(competitor==-2||desired<0||seconds<1||seconds>10)return 2;
    Workload w={0};PROCESS_INFORMATION pi={0};HANDLE ready=NULL,go=NULL,stop=NULL;int original=0,actual=0,original_thread=GetThreadPriority(GetCurrentThread()),gpu_clean=0,cpu_clean=0,ok=0,original_known=0;DWORD original_class=GetPriorityClass(GetCurrentProcess());double *samples=NULL;
    if(read_priority(GetCurrentProcess(),&original)!=0)goto done;
    original_known=1;
    if(!prepare(&w,512))goto done;
    char companion[32768];snprintf(companion,sizeof(companion),"%s.helper.json",argv[5]);
    if(competitor>=0)
    {
        char ready_name[256],go_name[256],stop_name[256];snprintf(ready_name,sizeof(ready_name),"Local\\PaviseGpuLab.%lu.%llu.Ready",GetCurrentProcessId(),(unsigned long long)GetTickCount64());snprintf(go_name,sizeof(go_name),"%s.Go",ready_name);snprintf(stop_name,sizeof(stop_name),"%s.Stop",ready_name);
        ready=CreateEventA(NULL,TRUE,FALSE,ready_name);go=CreateEventA(NULL,TRUE,FALSE,go_name);stop=CreateEventA(NULL,TRUE,FALSE,stop_name);if(!ready||!go||!stop)goto done;
        char exe[32768],command[32768];GetModuleFileNameA(NULL,exe,sizeof(exe));snprintf(command,sizeof(command),"\"%s\" --child %d %s %s %s \"%s\"",exe,competitor,ready_name,go_name,stop_name,companion);STARTUPINFOA start={0};start.cb=sizeof(start);start.dwFlags=STARTF_USESHOWWINDOW;start.wShowWindow=SW_HIDE;
        if(!CreateProcessA(exe,command,NULL,NULL,FALSE,CREATE_NO_WINDOW,NULL,NULL,&start,&pi))goto done;if(WaitForSingleObject(ready,5000)!=WAIT_OBJECT_0)goto done;
    }
    if(!SetPriorityClass(GetCurrentProcess(),HIGH_PRIORITY_CLASS)||!SetThreadPriority(GetCurrentThread(),THREAD_PRIORITY_ABOVE_NORMAL)||write_priority(GetCurrentProcess(),desired)!=0||read_priority(GetCurrentProcess(),&actual)!=0||actual!=desired)goto done;
    if(go)SetEvent(go);double warm=now()+.5;while(now()<warm)if(dispatch(&w,now()+2,NULL)!=1)goto done;
    samples=(double*)malloc(200000*sizeof(double));if(!samples)goto done;int count=0;double begin=now();
    while(now()-begin<seconds){double start=now();if(dispatch(&w,start+2,NULL)!=1)goto done;if(count>=200000)goto done;samples[count++]=(now()-start)*1000;}
    double elapsed=now()-begin;double loops_per_second=count/elapsed;qsort(samples,count,sizeof(double),compare_double);
    gpu_clean=write_priority(GetCurrentProcess(),original)==0&&read_priority(GetCurrentProcess(),&actual)==0&&actual==original;
    if(stop)SetEvent(stop);if(pi.hProcess){if(WaitForSingleObject(pi.hProcess,3000)!=WAIT_OBJECT_0)goto done;DWORD code;if(!GetExitCodeProcess(pi.hProcess,&code)||code!=0)goto done;}
    cpu_clean=SetThreadPriority(GetCurrentThread(),original_thread)&&SetPriorityClass(GetCurrentProcess(),original_class)&&GetThreadPriority(GetCurrentThread())==original_thread&&GetPriorityClass(GetCurrentProcess())==original_class;
    FILE *file=fopen(argv[5],"wb");if(!file)goto done;fprintf(file,"{\"scenario\":\"%s\",\"option\":\"%s\",\"adapter\":\"%s\",\"gpuPriority\":%d,\"competitorPriority\":%d,\"samples\":%d,\"seconds\":%.9f,\"loopsPerSecond\":%.9f,\"p50Ms\":%.9f,\"p95Ms\":%.9f,\"p99Ms\":%.9f,\"restored\":%s}",argv[2],argv[3],w.adapter,desired,competitor,count,elapsed,loops_per_second,percentile(samples,count,.5),percentile(samples,count,.95),percentile(samples,count,.99),gpu_clean&&cpu_clean?"true":"false");fclose(file);ok=gpu_clean&&cpu_clean;
done:
    if(stop)SetEvent(stop);if(original_known&&!gpu_clean&&read_priority(GetCurrentProcess(),&actual)==0){gpu_clean=actual==original||(write_priority(GetCurrentProcess(),original)==0&&read_priority(GetCurrentProcess(),&actual)==0&&actual==original);}if(!cpu_clean){SetThreadPriority(GetCurrentThread(),original_thread);SetPriorityClass(GetCurrentProcess(),original_class);}
    if(pi.hProcess){if(WaitForSingleObject(pi.hProcess,3000)!=WAIT_OBJECT_0){TerminateProcess(pi.hProcess,124);WaitForSingleObject(pi.hProcess,2000);}CloseHandle(pi.hProcess);CloseHandle(pi.hThread);}if(ready)CloseHandle(ready);if(go)CloseHandle(go);if(stop)CloseHandle(stop);if(samples)free(samples);release_work(&w);return ok?0:1;
}
