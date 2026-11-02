#define main UnusedPresentLabMain
#include "PresentLaneLab.cpp"
#undef main
#include <d3d11.h>

int main(int argc,char** argv) {
    if(argc!=2)return 2;
    std::atomic<bool> stop{false},failed{false};std::atomic<DWORD> frameTid{0},workerTid{0};
    HANDLE job=CreateEventW(nullptr,FALSE,FALSE,nullptr),done=CreateEventW(nullptr,FALSE,FALSE,nullptr);
    std::thread worker([&]{workerTid=GetCurrentThreadId();while(!stop)if(WaitForSingleObject(job,100)==WAIT_OBJECT_0){Work(600000);SetEvent(done);}});
    std::thread frame([&]{
        HWND w=CreateWindowExW(0,L"STATIC",L"Pavise owned DXGI fixture",WS_POPUP,0,0,32,32,nullptr,nullptr,GetModuleHandleW(nullptr),nullptr);
        ID3D11Device* d=nullptr;ID3D11DeviceContext* c=nullptr;IDXGISwapChain* s=nullptr;
        DXGI_SWAP_CHAIN_DESC desc={};desc.BufferDesc.Width=32;desc.BufferDesc.Height=32;desc.BufferDesc.Format=DXGI_FORMAT_R8G8B8A8_UNORM;
        desc.SampleDesc.Count=1;desc.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;desc.BufferCount=1;desc.OutputWindow=w;desc.Windowed=TRUE;desc.SwapEffect=DXGI_SWAP_EFFECT_DISCARD;
        HRESULT hr=D3D11CreateDeviceAndSwapChain(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&desc,&s,&d,nullptr,&c);
        if(FAILED(hr)){failed=true;stop=true;}else{
            frameTid=GetCurrentThreadId();
            while(!stop){SetEvent(job);if(WaitForSingleObject(done,100)!=WAIT_OBJECT_0)continue;Work(60000);if(FAILED(s->Present(0,0))){failed=true;break;}Sleep(1);}
        }
        if(s)s->Release();if(c)c->Release();if(d)d->Release();if(w)DestroyWindow(w);
    });
    double deadline=Seconds()+8;while(!frameTid&&!failed&&Seconds()<deadline)Sleep(20);
    if(frameTid){std::ofstream f(argv[1]);f<<"pid,frame_tid,worker_tid\n"<<GetCurrentProcessId()<<","<<frameTid<<","<<workerTid<<"\n";}
    double start=Seconds();while(!failed&&Seconds()-start<25)Sleep(100);
    stop=true;SetEvent(job);SetEvent(done);frame.join();worker.join();CloseHandle(job);CloseHandle(done);return failed?1:0;
}
