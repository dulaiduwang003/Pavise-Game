#pragma once
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <d3dcompiler.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <condition_variable>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iomanip>
#include <memory>
#include <mutex>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace bench {
template<class T> struct Com {
    T* p=nullptr;
    Com()=default; Com(const Com&)=delete; Com& operator=(const Com&)=delete;
    Com(Com&& o)noexcept:p(o.p){o.p=nullptr;}
    Com& operator=(Com&& o)noexcept{if(this!=&o){if(p)p->Release();p=o.p;o.p=nullptr;}return *this;}
    ~Com(){if(p)p->Release();}
    T* operator->()const{return p;} T* get()const{return p;}
    T** put(){if(p){p->Release();p=nullptr;}return &p;}
    template<class U> HRESULT as(Com<U>& o){return p->QueryInterface(__uuidof(U),reinterpret_cast<void**>(o.put()));}
};
struct HrError:std::runtime_error { HRESULT hr; HrError(HRESULT h,const char* op):std::runtime_error([&]{std::ostringstream s;s<<op<<" HRESULT=0x"<<std::hex<<uint32_t(h);return s.str();}()),hr(h){} };
inline void Check(HRESULT h,const char* op){if(FAILED(h))throw HrError(h,op);}
inline int64_t Qpc(){LARGE_INTEGER v;QueryPerformanceCounter(&v);return v.QuadPart;}
inline int64_t Frequency(){static int64_t f=[] {LARGE_INTEGER v;QueryPerformanceFrequency(&v);return v.QuadPart;}();return f;}
inline double Millis(int64_t ticks){return 1000.0*double(ticks)/double(Frequency());}
inline uint64_t Birth(HANDLE h){FILETIME a,b,c,d;if(!GetProcessTimes(h,&a,&b,&c,&d))return 0;return (uint64_t(a.dwHighDateTime)<<32)|a.dwLowDateTime;}
inline std::string Utf8(const std::wstring& w){if(w.empty())return {};int n=WideCharToMultiByte(CP_UTF8,0,w.data(),int(w.size()),nullptr,0,nullptr,nullptr);std::string s(n,'\0');WideCharToMultiByte(CP_UTF8,0,w.data(),int(w.size()),s.data(),n,nullptr,nullptr);return s;}
inline std::string Clean(std::string s){for(char& c:s)if(c=='\t'||c=='\r'||c=='\n')c=' ';return s;}
inline uint64_t Hash(const void* data,size_t n,uint64_t h=14695981039346656037ull){const auto* p=static_cast<const uint8_t*>(data);while(n--){h^=*p++;h*=1099511628211ull;}return h;}
inline std::string Hex(uint64_t h){std::ostringstream s;s<<std::hex<<std::setw(16)<<std::setfill('0')<<h;return s.str();}
inline void Write(const std::filesystem::path& path,const std::string& text){std::ofstream f(path,std::ios::binary|std::ios::trunc);f.exceptions(std::ios::badbit|std::ios::failbit);f<<text;f.flush();}
inline void AtomicWrite(const std::filesystem::path& path,const std::string& text){auto temporary=path;temporary+=L".tmp";Write(temporary,text);if(!MoveFileExW(temporary.c_str(),path.c_str(),MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH))throw std::runtime_error("atomic protocol write failed");}
struct Options {
    std::string api="dx11",scene="mixed",scenario="shared",nonce,background="none",worker;
    double seconds=60; uint32_t seed=1,width=1280,height=720,parent=0;
    bool hidden=false,warp=false,selfTest=false,waitStart=false,workerActive=false,uncapped=false;
    std::filesystem::path output;
};
struct Vec {float x=0,y=0,z=0;Vec operator+(Vec b)const{return{x+b.x,y+b.y,z+b.z};}Vec operator-(Vec b)const{return{x-b.x,y-b.y,z-b.z};}Vec operator*(float k)const{return{x*k,y*k,z*k};}};
inline float Dot(Vec a,Vec b){return a.x*b.x+a.y*b.y+a.z*b.z;}
inline Vec Cross(Vec a,Vec b){return{a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x};}
inline Vec Normalize(Vec a){return a*(1.0f/std::sqrt(std::max(1e-12f,Dot(a,a))));}
struct Matrix {float m[16]={};};
inline Matrix Mul(const Matrix& a,const Matrix& b){Matrix c;for(int i=0;i<4;i++)for(int j=0;j<4;j++)for(int k=0;k<4;k++)c.m[i*4+j]+=a.m[i*4+k]*b.m[k*4+j];return c;}
inline Matrix View(Vec eye,Vec target){Vec z=Normalize(target-eye),x=Normalize(Cross({0,1,0},z)),y=Cross(z,x);return{{x.x,y.x,z.x,0,x.y,y.y,z.y,0,x.z,y.z,z.z,0,-Dot(x,eye),-Dot(y,eye),-Dot(z,eye),1}};}
inline Matrix Projection(float aspect){float t=1/std::tan(.55f),n=.1f,f=220.f;return{{t/aspect,0,0,0,0,t,0,0,0,0,f/(f-n),1,0,0,-n*f/(f-n),0}};}
struct Vertex {Vec p,n;float u,v;Vec color;};
struct Constants {Matrix vp;float eye[4],settings[4];};
struct Range {UINT first,count;};
struct Frame {uint64_t sequence=0;int64_t qpc=0;double frameMs=0,cpuMs=0,gpuMs=-1,latencyMs=-1;bool valid=false,controlledBackground=false;};
// Reserve once, before sampling. Short high-FPS trials must not exhaust a buffer
// estimated at 2,000 FPS (the hardware switch smoke exceeded 4,800 FPS).
// Keep both a physical-memory budget and an absolute ceiling; exhaustion still
// fails explicitly rather than dropping samples, throttling or reallocating mid-trial.
inline size_t FrameCapacity(double seconds,uint64_t availableBytes){
    constexpr uint64_t minimum=10000,maximum=4000000;
    uint64_t budget=std::min<uint64_t>(maximum,availableBytes/16/sizeof(Frame));
    if(!std::isfinite(seconds)||seconds<.1||seconds>43200||budget<minimum)
        throw std::runtime_error("insufficient frame-sample memory or invalid duration");
    uint64_t target=std::max<uint64_t>(minimum,uint64_t(std::ceil(seconds*16000))+100);
    return size_t(std::min(budget,target));
}
inline uint64_t FrameStorageBytes(size_t capacity){
    if(capacity>4000000)throw std::runtime_error("invalid frame storage capacity");
    return uint64_t(capacity)*192+64ull*1024*1024;
}
inline bool FrameCapacityReached(size_t count,size_t capacity){return count>=capacity;}
inline size_t WriteFrameCsv(std::ostream& csv,const std::vector<Frame>& frames){
    size_t invalid=0;
    csv<<"sequence,qpc,frame_ms,cpu_ms,gpu_ms,latency_ms,valid,phase\n"<<std::setprecision(12);
    for(const auto& f:frames){if(!f.valid)++invalid;csv<<f.sequence<<','<<f.qpc<<','<<f.frameMs<<','<<f.cpuMs<<',';if(f.gpuMs>=0)csv<<f.gpuMs;csv<<',';if(f.latencyMs>=0)csv<<f.latencyMs;csv<<','<<(f.valid?1:0)<<','<<(f.controlledBackground?"controlled_background":"foreground")<<'\n';}
    return invalid;
}
struct InputEvent {uint64_t tag,sequence;int64_t posted,received,sampled,presented;bool helper;};
constexpr UINT MaxVertices=280000;
constexpr UINT TextureWidth=256;
constexpr UINT TextureCount=16;

inline const char* Shader=R"HLSL(
cbuffer Frame : register(b0) { row_major float4x4 vp; float4 eye; float4 settings; };
Texture2D<float4> material : register(t0); SamplerState materialSampler : register(s0);
struct In { float3 p:POSITION;float3 n:NORMAL;float2 uv:TEXCOORD;float3 color:COLOR; };
struct Out { float4 p:SV_Position;float3 world:POSITION;float3 n:NORMAL;float2 uv:TEXCOORD;float3 color:COLOR; };
Out VS(In v) { Out o;o.p=mul(float4(v.p,1),vp);o.world=v.p;o.n=v.n;o.uv=v.uv;o.color=v.color;return o; }
float4 PS(Out p):SV_Target {
 float3 n=normalize(p.n),sun=normalize(float3(-.6,1,-.35));
 float3 tex=material.Sample(materialSampler,p.uv*.15).rgb;
 float shadow=1;
 // Analytic soft occlusion from the courtyard's round monuments (no shadow-map claim).
 [unroll]for(int k=0;k<4;k++){float3 c=float3((k-1.5)*9,2.3,0);float3 d=p.world-c;float along=dot(-d,sun);float dist=length(d+sun*max(0,along));shadow*=lerp(.45,1,smoothstep(1.8,2.4,dist));}
 float wave=0;[loop]for(int i=0;i<(int)settings.x;i++){float f=i+1;wave+=sin(p.world.x*.13*f+sin(p.world.z*.11+f))*cos(p.uv.y*f*.2)*.007;}
 float light=.22+max(0,dot(n,sun))*.78*shadow;
 float spec=pow(saturate(dot(reflect(-sun,n),normalize(eye.xyz-p.world))),24)*.16;
 float3 c=p.color*(.55+tex*.6)*light+spec+wave+settings.y*.035;
 float fog=saturate(length(eye.xyz-p.world)/180);return float4(lerp(c,float3(.25,.37,.48),fog),1);
})HLSL";
inline void Compile(const char* entry,const char* target,Com<ID3DBlob>& blob){Com<ID3DBlob> err;HRESULT h=D3DCompile(Shader,std::strlen(Shader),"CalibrationBench",nullptr,nullptr,entry,target,D3DCOMPILE_OPTIMIZATION_LEVEL3|D3DCOMPILE_ENABLE_STRICTNESS,0,blob.put(),err.put());if(FAILED(h))throw std::runtime_error(err.get()?std::string(static_cast<char*>(err->GetBufferPointer()),err->GetBufferSize()):"shader compilation failed");}
inline uint32_t Random(uint32_t& s){s^=s<<13;s^=s>>17;s^=s<<5;return s;}

class TaskPool {
    std::vector<std::thread> threads; std::mutex mutex;std::condition_variable work,done;
    std::function<void(size_t)> task;size_t epoch=0,remaining=0;bool stop=false;
public:
    explicit TaskPool(size_t n){for(size_t i=0;i<n;i++)threads.emplace_back([&,i]{size_t observed=0;for(;;){std::unique_lock<std::mutex> l(mutex);work.wait(l,[&]{return stop||epoch!=observed;});if(stop)return;observed=epoch;auto job=task;l.unlock();job(i);l.lock();if(!--remaining)done.notify_one();}});}
    ~TaskPool(){{std::lock_guard<std::mutex> l(mutex);stop=true;}work.notify_all();for(auto& t:threads)t.join();}
    size_t size()const{return threads.size();}
    void run(std::function<void(size_t)> job){std::unique_lock<std::mutex> l(mutex);task=std::move(job);remaining=threads.size();++epoch;work.notify_all();done.wait(l,[&]{return !remaining;});}
};
class Scene {
    void tri(Vec a,Vec b,Vec c,Vec col){Vec n=Normalize(Cross(b-a,c-a));vertices.push_back({a,n,a.x,a.z,col});vertices.push_back({b,n,b.x,b.z,col});vertices.push_back({c,n,c.x,c.z,col});}
    void quad(Vec a,Vec b,Vec c,Vec d,Vec col){tri(a,b,c,col);tri(a,c,d,col);}
    void box(Vec p,Vec s,Vec col){float x=p.x,y=p.y,z=p.z,a=x+s.x,b=y+s.y,c=z+s.z;quad({x,y,z},{a,y,z},{a,b,z},{x,b,z},col);quad({a,y,c},{x,y,c},{x,b,c},{a,b,c},col);quad({x,y,c},{x,y,z},{x,b,z},{x,b,c},col);quad({a,y,z},{a,y,c},{a,b,c},{a,b,z},col);quad({x,b,z},{a,b,z},{a,b,c},{x,b,c},col);quad({x,y,c},{a,y,c},{a,y,z},{x,y,z},col);}
    void sphere(Vec p,float radius,Vec col,int rings=6,int sides=10){for(int r=0;r<rings;r++)for(int s=0;s<sides;s++){auto at=[&](int a,int b){float u=float(b)*6.2831853f/sides,v=float(a)*3.14159265f/rings;return p+Vec{std::sin(v)*std::cos(u),std::cos(v),std::sin(v)*std::sin(u)}*radius;};quad(at(r,s),at(r+1,s),at(r+1,s+1),at(r,s+1),col);}}
    void limb(Vec a,Vec b,float radius,Vec col){Vec n=Normalize(b-a),u=Normalize(Cross(n,{.01f,0,1})),v=Cross(n,u);for(int i=0;i<8;i++){float t=i*6.2831853f/8,q=(i+1)*6.2831853f/8;Vec p=(u*std::cos(t)+v*std::sin(t))*radius,r=(u*std::cos(q)+v*std::sin(q))*radius;quad(a+p,a+r,b+r,b+p,col);}}
    struct Body {Vec p,v;};
    std::vector<Body> bodies,initialBodies;size_t fixedVertices=0;uint32_t rng;
    std::unique_ptr<TaskPool> pool;std::vector<uint64_t> jobs;
public:
    Options options;std::vector<Vertex> vertices;std::vector<Range> ranges;
    struct Bounds{Vec center;float radius;};std::vector<Bounds> bounds;std::vector<Range> visibleRanges;
    std::vector<uint8_t> texture;std::vector<uint8_t> ram;
    uint64_t fixedChecksum=0,rollingChecksum=0,ioBytes=0,simulationTick=0;UINT textureIndex=0;
    std::filesystem::path streamPath;HANDLE stream=INVALID_HANDLE_VALUE;
    size_t staticCount=0;UINT shaderLoops=4;
    explicit Scene(const Options& o):rng(o.seed?o.seed:1),options(o){
        vertices.reserve(MaxVertices);ranges.reserve(1024);texture.resize(TextureWidth*TextureWidth*4);
        for(int z=-48;z<48;z+=3)for(int x=-48;x<48;x+=3){auto h=[](float a,float b){return -.7f+std::sin(a*.13f)*std::cos(b*.12f)*.3f;};quad({float(x),h(float(x),float(z)),float(z)},{float(x+3),h(float(x+3),float(z)),float(z)},{float(x+3),h(float(x+3),float(z+3)),float(z+3)},{float(x),h(float(x),float(z+3)),float(z+3)},{.4f,.48f,.31f});}
        ranges.push_back({0,UINT(vertices.size())});
        // Deterministic city blocks, pitched roofs, courtyard and an open-sided interior hall.
        for(int z=-4;z<=4;z++)for(int x=-4;x<=4;x++){if(std::abs(x)<2&&std::abs(z)<2)continue;UINT start=UINT(vertices.size());float h=3+Random(rng)%80*.1f;Vec p={float(x)*9,-.5f,float(z)*9};box(p,{4,h,5},{.32f+float(x+4)*.025f,.3f+float(z+4)*.02f,.38f});Vec a=p+Vec{0,h,0},b=a+Vec{4,0,0},c=a+Vec{4,0,5},d=a+Vec{0,0,5},e=a+Vec{2,2.1f,0},f=a+Vec{2,2.1f,5};tri(a,b,e,{.5f,.24f,.16f});tri(d,f,c,{.5f,.24f,.16f});quad(a,e,f,d,{.5f,.24f,.16f});quad(e,b,c,f,{.5f,.24f,.16f});ranges.push_back({start,UINT(vertices.size())-start});}
        for(int i=0;i<4;i++){UINT start=UINT(vertices.size());sphere({(i-1.5f)*9,2.3f,0},2.2f,{.6f,.52f,.3f},10,16);ranges.push_back({start,UINT(vertices.size())-start});}
        for(int x=-3;x<=3;x++){UINT start=UINT(vertices.size());limb({float(x)*3,0,16},{float(x)*3,6,16},.4f,{.65f,.62f,.54f});box({float(x)*3-1.5f,6,14},{3,.4f,7},{.52f,.45f,.36f});ranges.push_back({start,UINT(vertices.size())-start});}
        fixedVertices=vertices.size();staticCount=fixedVertices;
        for(const auto& range:ranges){Vec lo={1e9f,1e9f,1e9f},hi={-1e9f,-1e9f,-1e9f};for(UINT j=0;j<range.count;j++){Vec p=vertices[range.first+j].p;lo={std::min(lo.x,p.x),std::min(lo.y,p.y),std::min(lo.z,p.z)};hi={std::max(hi.x,p.x),std::max(hi.y,p.y),std::max(hi.z,p.z)};}Vec center=(lo+hi)*.5f;Vec delta=hi-center;bounds.push_back({center,std::sqrt(Dot(delta,delta))});}visibleRanges.reserve(ranges.size());
        size_t n=(o.scene=="workers"||o.scene=="mixed"||o.scene=="thermal")?8192:1024;
        bodies.resize(n);for(auto& b:bodies){b.p={float(int(Random(rng)%600)-300)*.01f,1+float(Random(rng)%500)*.01f,float(int(Random(rng)%600)-300)*.01f};b.v={.1f,0,.05f};}
        initialBodies=bodies;
        if(o.scene=="workers"||o.scene=="mixed"||o.scene=="thermal"){size_t count=std::min<size_t>(8,std::max(1u,std::thread::hardware_concurrency()>1?std::thread::hardware_concurrency()-1:1));pool=std::make_unique<TaskPool>(count);jobs.resize(count);}
        shaderLoops=(o.scene=="gpu"||o.scene=="thermal")?32:o.scene=="mixed"?12:4;
        if(o.scene=="memory") {MEMORYSTATUSEX m={sizeof(m)};GlobalMemoryStatusEx(&m);size_t bytes=size_t(std::min<uint64_t>(128ull<<20,m.ullAvailPhys/16));if(bytes<4*1024*1024)throw std::runtime_error("insufficient available RAM");ram.resize(bytes);for(size_t i=0;i<ram.size();i+=4096)ram[i]=uint8_t(i>>12);}
        if(o.scene=="stream"||o.scene=="startup"||o.scene=="thermal"){streamPath=o.output/L"owned-assets.rle";std::vector<uint8_t> data(4*1024*1024);for(size_t i=0;i<data.size();i+=2){data[i]=3;data[i+1]=uint8_t(Random(rng));}HANDLE f=CreateFileW(streamPath.c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);if(f==INVALID_HANDLE_VALUE)throw std::runtime_error("owned asset file must be new");DWORD written=0;bool ok=WriteFile(f,data.data(),DWORD(data.size()),&written,nullptr)&&written==data.size();CloseHandle(f);if(!ok)throw std::runtime_error("owned asset generation failed");stream=CreateFileW(streamPath.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL|FILE_FLAG_SEQUENTIAL_SCAN,nullptr);if(stream==INVALID_HANDLE_VALUE)throw std::runtime_error("owned asset read open failed");}
        fixedChecksum=Hash(vertices.data(),vertices.size()*sizeof(Vertex));fixedChecksum=Hash(o.scene.data(),o.scene.size(),fixedChecksum);fixedChecksum=Hash(o.background.data(),o.background.size(),fixedChecksum);auto count=bodies.size();fixedChecksum=Hash(&count,sizeof(count),fixedChecksum);count=workers();fixedChecksum=Hash(&count,sizeof(count),fixedChecksum);count=ram.size();fixedChecksum=Hash(&count,sizeof(count),fixedChecksum);fixedChecksum=Hash(&o.seed,sizeof(o.seed),fixedChecksum);fixedChecksum=Hash(&o.width,sizeof(o.width),fixedChecksum);fixedChecksum=Hash(&o.height,sizeof(o.height),fixedChecksum);fixedChecksum=Hash(Shader,std::strlen(Shader),fixedChecksum);
        update(0);
    }
    ~Scene(){if(stream!=INVALID_HANDLE_VALUE)CloseHandle(stream);}
    size_t workers()const{return pool?pool->size():0;}
    void reset(){bodies=initialBodies;rollingChecksum=ioBytes=0;textureIndex=0;if(stream!=INVALID_HANDLE_VALUE)SetFilePointer(stream,0,nullptr,FILE_BEGIN);}
    void update(uint64_t frame,double elapsedSeconds=-1){
        double time=elapsedSeconds>=0?elapsedSeconds:double(frame)/60;float t=float(std::fmod(time,120));simulationTick=uint64_t(time*60);vertices.resize(fixedVertices);
        // Stateless time-derived snapshots keep visible motion independent of rendered FPS.
        // Each frame still executes exactly the same number of local collision/force prediction steps.
        auto physics=[&](size_t worker,size_t count){uint64_t h=14695981039346656037ull;int steps=options.scene=="workers"?12:2;for(size_t i=worker;i<bodies.size();i+=count){auto& b=bodies[i];b=initialBodies[i];float height=b.p.y,halfPeriod=std::sqrt(2*height/9.81f),phase=std::fmod(t+float(i)*.003f,2*halfPeriod)-halfPeriod;b.p.y=height-4.905f*phase*phase;b.v.y=-9.81f*phase;auto reflect=[](float p,float v,float time){float x=std::fmod(p+6+v*time,24);if(x<0)x+=24;return x<12?x-6:18-x;};b.p.x=reflect(b.p.x,b.v.x,t);b.p.z=reflect(b.p.z,b.v.z,t);for(int s=0;s<steps;s++){b.v.y-=.008175f;b.p=b.p+b.v*(1.f/120);if(b.p.y<0){b.p.y=-b.p.y;b.v.y=std::abs(b.v.y)*.92f;}if(std::abs(b.p.x)>6)b.v.x=-b.v.x;if(std::abs(b.p.z)>6)b.v.z=-b.v.z;Vec force=Normalize(b.p-Vec{0,2,0})*.00003f;b.v=b.v+force;}h=Hash(&b.p,sizeof(b.p),h);}return h;};
        if(pool){pool->run([&](size_t w){jobs[w]=physics(w,pool->size());});for(auto h:jobs)rollingChecksum^=h;}else rollingChecksum^=physics(0,1);
        // Procedural two-bone limbs: the elbow/knee joint moves independently of the root.
        for(int i=0;i<8;i++){Vec root={-10.f+i*2.5f,1.2f,7+std::sin(t+i)*1.5f};Vec shoulder=root+Vec{0,.9f,0};limb(root,shoulder,.22f,{.2f,.4f,.65f});sphere(shoulder+Vec{0,.42f,0},.3f,{.75f,.57f,.4f},5,8);for(int side:{-1,1}){float swing=std::sin(t*3+i)*side;Vec hip=root+Vec{side*.18f,0,0},knee=hip+Vec{0,-.5f,swing*.25f},foot=knee+Vec{0,-.5f,-swing*.4f};limb(hip,knee,.12f,{.2f,.26f,.4f});limb(knee,foot,.1f,{.17f,.2f,.3f});Vec elbow=shoulder+Vec{side*.4f,-.35f,swing*.3f},hand=elbow+Vec{side*.1f,-.35f,-swing*.3f};limb(shoulder,elbow,.1f,{.2f,.4f,.65f});limb(elbow,hand,.08f,{.75f,.57f,.4f});}}
        for(size_t i=0;i<std::min<size_t>(bodies.size(),384);i++){Vec p=bodies[i].p+Vec{0,.1f,-7};tri(p+Vec{-.045f,0,0},p+Vec{.045f,0,0},p+Vec{0,.12f,0},{.95f,.55f,.15f});}
        if(vertices.size()>MaxVertices)throw std::runtime_error("vertex capacity exceeded");
        // Material stream performs actual bounded file reads and RLE decompression into upload data.
        if(stream!=INVALID_HANDLE_VALUE) {std::array<uint8_t,32768> packed{};DWORD read=0;LONG offset=LONG(((simulationTick/8)%128)*packed.size());if(SetFilePointer(stream,offset,nullptr,FILE_BEGIN)==INVALID_SET_FILE_POINTER&&GetLastError()!=NO_ERROR)throw std::runtime_error("asset seek failed");if(!ReadFile(stream,packed.data(),DWORD(packed.size()),&read,nullptr)||read!=packed.size())throw std::runtime_error("asset read failed");ioBytes+=read;size_t p=0;for(size_t i=0;p<texture.size();i=(i+2)%read){size_t n=size_t(packed[i])+1;while(n--&&p<texture.size())texture[p++]=packed[i+1];}textureIndex=UINT(simulationTick/8);}
        else for(UINT y=0;y<TextureWidth;y++)for(UINT x=0;x<TextureWidth;x++){size_t p=(y*TextureWidth+x)*4;uint8_t c=uint8_t(((x/16+y/16+(simulationTick/30))&1)?185:100);texture[p]=c;texture[p+1]=uint8_t(c*.9f);texture[p+2]=uint8_t(c*.75f);texture[p+3]=255;}
        if(!ram.empty()){size_t pages=ram.size()/4096;for(size_t j=0;j<pages;j++){size_t p=((j*4051+simulationTick*17)%pages)*4096;ram[p]=uint8_t(j+simulationTick);rollingChecksum+=ram[p];}textureIndex=UINT(simulationTick%TextureCount);}
    }
    Constants constants(uint64_t frame,double elapsedSeconds=-1){double time=elapsedSeconds>=0?elapsedSeconds:double(frame)/60;float t=float(std::fmod(time,60)/10);const Vec path[]={{30,7,-32},{12,4,-10},{0,3,18},{-20,5,30},{-30,7,0},{0,5,-28}};int section=int(t)%6;float u=t-std::floor(t);u=u*u*(3-2*u);Vec eye=path[section]*(1-u)+path[(section+1)%6]*u;Constants c{};c.vp=Mul(View(eye,{0,1.5f,3}),Projection(float(options.width)/options.height));c.eye[0]=eye.x;c.eye[1]=eye.y;c.eye[2]=eye.z;c.settings[0]=float(shaderLoops);visibleRanges.clear();for(size_t i=0;i<ranges.size();i++){bool visible=true;const auto& b=bounds[i];for(int plane=0;plane<6;plane++){int axis=plane/2;float sign=plane%2?-1.f:1.f;float p[4];for(int k=0;k<4;k++)p[k]=(plane==4?0:c.vp.m[k*4+3])+sign*c.vp.m[k*4+axis];float distance=p[0]*b.center.x+p[1]*b.center.y+p[2]*b.center.z+p[3];float radius=b.radius*std::sqrt(p[0]*p[0]+p[1]*p[1]+p[2]*p[2]);if(distance < -radius){visible=false;break;}}if(visible)visibleRanges.push_back(ranges[i]);}return c;}
};
class Renderer {
public:
    virtual ~Renderer()=default;
    std::string adapterName,adapterLuid;bool software=false,tearing=false;uint64_t vramBudget=0,vramUsage=0,vramAllocated=0;uint64_t presents=0,occluded=0,gpuMissing=0,materialCursor=0,memoryPrepFrames=0;
    void resetMaterialCycle(){materialCursor=0;}
    virtual void draw(Scene&,const Constants&,std::vector<Frame>&,size_t)=0;
    virtual void finish(std::vector<Frame>&)=0;
};
inline void AdapterMetadata(IDXGIAdapter1* a,Renderer& r){DXGI_ADAPTER_DESC1 d{};Check(a->GetDesc1(&d),"GetDesc1");r.adapterName=Utf8(d.Description);r.adapterLuid=Hex((uint64_t(uint32_t(d.AdapterLuid.HighPart))<<32)|d.AdapterLuid.LowPart);r.software=(d.Flags&DXGI_ADAPTER_FLAG_SOFTWARE)!=0;Com<IDXGIAdapter3> v;if(SUCCEEDED(a->QueryInterface(__uuidof(IDXGIAdapter3),reinterpret_cast<void**>(v.put())))){DXGI_QUERY_VIDEO_MEMORY_INFO m{};if(SUCCEEDED(v->QueryVideoMemoryInfo(0,DXGI_MEMORY_SEGMENT_GROUP_LOCAL,&m))){r.vramBudget=m.Budget;r.vramUsage=m.CurrentUsage;}}}
inline size_t HardwareAdapterChoice(const std::vector<DXGI_ADAPTER_DESC1>& adapters,bool performanceOrdered){
    size_t best=SIZE_MAX;
    for(size_t i=0;i<adapters.size();i++){
        if(adapters[i].Flags&DXGI_ADAPTER_FLAG_SOFTWARE)continue;
        if(best==SIZE_MAX||(!performanceOrdered&&adapters[i].DedicatedVideoMemory>adapters[best].DedicatedVideoMemory))best=i;
    }
    return best;
}
// --uncapped: tearing presents bypass the refresh-rate cap of windowed flip presentation when the system supports them.
inline bool TearingSupported(IDXGIFactory4* f){Com<IDXGIFactory5> f5;BOOL allow=FALSE;if(FAILED(f->QueryInterface(__uuidof(IDXGIFactory5),reinterpret_cast<void**>(f5.put()))))return false;return SUCCEEDED(f5->CheckFeatureSupport(DXGI_FEATURE_PRESENT_ALLOW_TEARING,&allow,sizeof(allow)))&&allow!=FALSE;}
inline void SelectAdapter(IDXGIFactory4* f,bool warp,Com<IDXGIAdapter1>& a){
    if(warp){Check(f->EnumWarpAdapter(__uuidof(IDXGIAdapter1),reinterpret_cast<void**>(a.put())),"EnumWarpAdapter");return;}
    Com<IDXGIFactory6> preferred;bool ordered=SUCCEEDED(f->QueryInterface(__uuidof(IDXGIFactory6),reinterpret_cast<void**>(preferred.put())));
    std::vector<Com<IDXGIAdapter1>> adapters;std::vector<DXGI_ADAPTER_DESC1> descriptions;
    for(UINT i=0;;i++){
        Com<IDXGIAdapter1> next;
        HRESULT h=ordered?preferred->EnumAdapterByGpuPreference(i,DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE,__uuidof(IDXGIAdapter1),reinterpret_cast<void**>(next.put())):f->EnumAdapters1(i,next.put());
        if(h==DXGI_ERROR_NOT_FOUND)break;Check(h,"Enumerate preferred hardware adapter");
        DXGI_ADAPTER_DESC1 d{};Check(next->GetDesc1(&d),"GetDesc1");descriptions.push_back(d);adapters.push_back(std::move(next));
    }
    size_t selected=HardwareAdapterChoice(descriptions,ordered);
    if(selected==SIZE_MAX)throw std::runtime_error("no hardware adapter; use --warp for non-certifying validation");
    a=std::move(adapters[selected]);
}
std::unique_ptr<Renderer> MakeDx11(HWND,const Options&);
std::unique_ptr<Renderer> MakeDx12(HWND,const Options&);
} // namespace bench
