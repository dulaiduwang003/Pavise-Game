#include "BenchCommon.hpp"
namespace bench {
class Dx11 final:public Renderer {
    Com<ID3D11Device> device;Com<ID3D11DeviceContext> context;Com<IDXGISwapChain1> chain;
    Com<ID3D11RenderTargetView> target;Com<ID3D11Texture2D> depth;Com<ID3D11DepthStencilView> depthView;
    Com<ID3D11VertexShader> vs;Com<ID3D11PixelShader> ps;Com<ID3D11InputLayout> layout;
    Com<ID3D11Buffer> vertices,constants;Com<ID3D11SamplerState> sampler;Com<ID3D11RasterizerState> raster;
    struct Texture {Com<ID3D11Texture2D> value;Com<ID3D11ShaderResourceView> view;};
    std::vector<std::unique_ptr<Texture>> textures;
    struct Query {Com<ID3D11Query> disjoint,start,end;bool pending=false;size_t frame=0;};
    std::array<Query,16> queries;bool timing=true;uint64_t serial=0;Options options;
    bool collect(Query& q,std::vector<Frame>& frames){
        if(!q.pending)return true;D3D11_QUERY_DATA_TIMESTAMP_DISJOINT data{};UINT64 a=0,b=0;
        HRESULT h=context->GetData(q.disjoint.get(),&data,sizeof(data),D3D11_ASYNC_GETDATA_DONOTFLUSH);Check(h,"DX11 disjoint GetData");if(h==S_FALSE)return false;
        h=context->GetData(q.start.get(),&a,sizeof(a),D3D11_ASYNC_GETDATA_DONOTFLUSH);Check(h,"DX11 timestamp GetData");if(h==S_FALSE)return false;
        h=context->GetData(q.end.get(),&b,sizeof(b),D3D11_ASYNC_GETDATA_DONOTFLUSH);Check(h,"DX11 timestamp GetData");if(h==S_FALSE)return false;
        if(q.frame<frames.size()&&!data.Disjoint&&data.Frequency&&b>=a)frames[q.frame].gpuMs=1000.0*double(b-a)/double(data.Frequency);
        else if(q.frame<frames.size())++gpuMissing;q.pending=false;return true;
    }
public:
    Dx11(HWND window,const Options& o):options(o){
        Com<IDXGIFactory4> factory;Check(CreateDXGIFactory1(__uuidof(IDXGIFactory4),reinterpret_cast<void**>(factory.put())),"CreateDXGIFactory1");Com<IDXGIAdapter1> adapter;SelectAdapter(factory.get(),o.warp,adapter);AdapterMetadata(adapter.get(),*this);
        D3D_FEATURE_LEVEL level;Check(D3D11CreateDevice(adapter.get(),D3D_DRIVER_TYPE_UNKNOWN,nullptr,0,nullptr,0,D3D11_SDK_VERSION,device.put(),&level,context.put()),"D3D11CreateDevice");
        DXGI_SWAP_CHAIN_DESC1 sd{};sd.Width=o.width;sd.Height=o.height;sd.Format=DXGI_FORMAT_R8G8B8A8_UNORM;sd.SampleDesc.Count=1;sd.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;sd.BufferCount=2;sd.SwapEffect=DXGI_SWAP_EFFECT_FLIP_DISCARD;tearing=o.uncapped&&TearingSupported(factory.get());sd.Flags=tearing?DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING:0;
        Check(factory->CreateSwapChainForHwnd(device.get(),window,&sd,nullptr,nullptr,chain.put()),"DX11 CreateSwapChainForHwnd");Check(factory->MakeWindowAssociation(window,DXGI_MWA_NO_ALT_ENTER),"MakeWindowAssociation");
        Com<ID3D11Texture2D> back;Check(chain->GetBuffer(0,__uuidof(ID3D11Texture2D),reinterpret_cast<void**>(back.put())),"DX11 GetBuffer");Check(device->CreateRenderTargetView(back.get(),nullptr,target.put()),"CreateRenderTargetView");
        D3D11_TEXTURE2D_DESC dd{};dd.Width=o.width;dd.Height=o.height;dd.MipLevels=1;dd.ArraySize=1;dd.Format=DXGI_FORMAT_D32_FLOAT;dd.SampleDesc.Count=1;dd.BindFlags=D3D11_BIND_DEPTH_STENCIL;Check(device->CreateTexture2D(&dd,nullptr,depth.put()),"CreateDepthTexture");Check(device->CreateDepthStencilView(depth.get(),nullptr,depthView.put()),"CreateDepthStencilView");
        Com<ID3DBlob> v,p;Compile("VS","vs_5_0",v);Compile("PS","ps_5_0",p);Check(device->CreateVertexShader(v->GetBufferPointer(),v->GetBufferSize(),nullptr,vs.put()),"CreateVertexShader");Check(device->CreatePixelShader(p->GetBufferPointer(),p->GetBufferSize(),nullptr,ps.put()),"CreatePixelShader");
        D3D11_INPUT_ELEMENT_DESC elements[]={{"POSITION",0,DXGI_FORMAT_R32G32B32_FLOAT,0,0,D3D11_INPUT_PER_VERTEX_DATA,0},{"NORMAL",0,DXGI_FORMAT_R32G32B32_FLOAT,0,12,D3D11_INPUT_PER_VERTEX_DATA,0},{"TEXCOORD",0,DXGI_FORMAT_R32G32_FLOAT,0,24,D3D11_INPUT_PER_VERTEX_DATA,0},{"COLOR",0,DXGI_FORMAT_R32G32B32_FLOAT,0,32,D3D11_INPUT_PER_VERTEX_DATA,0}};
        Check(device->CreateInputLayout(elements,4,v->GetBufferPointer(),v->GetBufferSize(),layout.put()),"CreateInputLayout");
        D3D11_BUFFER_DESC bd{};bd.ByteWidth=MaxVertices*sizeof(Vertex);bd.Usage=D3D11_USAGE_DYNAMIC;bd.BindFlags=D3D11_BIND_VERTEX_BUFFER;bd.CPUAccessFlags=D3D11_CPU_ACCESS_WRITE;Check(device->CreateBuffer(&bd,nullptr,vertices.put()),"CreateVertexBuffer");bd.ByteWidth=sizeof(Constants);bd.BindFlags=D3D11_BIND_CONSTANT_BUFFER;Check(device->CreateBuffer(&bd,nullptr,constants.put()),"CreateConstantBuffer");
        D3D11_SAMPLER_DESC ss{};ss.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;ss.AddressU=ss.AddressV=ss.AddressW=D3D11_TEXTURE_ADDRESS_WRAP;ss.MaxLOD=D3D11_FLOAT32_MAX;ss.MaxAnisotropy=1;ss.ComparisonFunc=D3D11_COMPARISON_ALWAYS;Check(device->CreateSamplerState(&ss,sampler.put()),"CreateSamplerState");
        D3D11_RASTERIZER_DESC rd{};rd.FillMode=D3D11_FILL_SOLID;rd.CullMode=D3D11_CULL_NONE;rd.DepthClipEnable=TRUE;Check(device->CreateRasterizerState(&rd,raster.put()),"CreateRasterizerState");
        UINT count=o.scene=="memory"?UINT(std::max<uint64_t>(1,std::min<uint64_t>(128ull<<20,vramBudget/16)/(TextureWidth*TextureWidth*4))):1;
        count=std::min(512u,count);vramAllocated=uint64_t(count)*TextureWidth*TextureWidth*4;for(UINT i=0;i<count;i++){auto t=std::make_unique<Texture>();D3D11_TEXTURE2D_DESC td{};td.Width=td.Height=TextureWidth;td.MipLevels=1;td.ArraySize=1;td.Format=DXGI_FORMAT_R8G8B8A8_UNORM;td.SampleDesc.Count=1;td.Usage=D3D11_USAGE_DEFAULT;td.BindFlags=D3D11_BIND_SHADER_RESOURCE;std::vector<uint32_t> data(TextureWidth*TextureWidth,0xff808080u+i*17);D3D11_SUBRESOURCE_DATA init{data.data(),TextureWidth*4,0};Check(device->CreateTexture2D(&td,&init,t->value.put()),"CreateMaterialTexture");Check(device->CreateShaderResourceView(t->value.get(),nullptr,t->view.put()),"CreateMaterialSRV");textures.push_back(std::move(t));}
        for(auto& q:queries){D3D11_QUERY_DESC d{D3D11_QUERY_TIMESTAMP_DISJOINT,0};if(FAILED(device->CreateQuery(&d,q.disjoint.put()))){timing=false;break;}d.Query=D3D11_QUERY_TIMESTAMP;if(FAILED(device->CreateQuery(&d,q.start.put()))||FAILED(device->CreateQuery(&d,q.end.put()))){timing=false;break;}}
    }
    void draw(Scene& s,const Constants& c,std::vector<Frame>& frames,size_t index)override{
        Query& q=queries[serial++%queries.size()];bool measure=timing&&collect(q,frames);if(measure){context->Begin(q.disjoint.get());context->End(q.start.get());q.frame=index;}else if(index<frames.size())++gpuMissing;
        D3D11_MAPPED_SUBRESOURCE map{};Check(context->Map(vertices.get(),0,D3D11_MAP_WRITE_DISCARD,0,&map),"MapVertices");std::memcpy(map.pData,s.vertices.data(),s.vertices.size()*sizeof(Vertex));context->Unmap(vertices.get(),0);
        Check(context->Map(constants.get(),0,D3D11_MAP_WRITE_DISCARD,0,&map),"MapConstants");std::memcpy(map.pData,&c,sizeof(c));context->Unmap(constants.get(),0);
        UINT material=UINT(materialCursor++%textures.size());context->UpdateSubresource(textures[material]->value.get(),0,nullptr,s.texture.data(),TextureWidth*4,0);
        ID3D11RenderTargetView* rt=target.get();context->OMSetRenderTargets(1,&rt,depthView.get());float clear[]={.16f,.23f,.32f,1};context->ClearRenderTargetView(rt,clear);context->ClearDepthStencilView(depthView.get(),D3D11_CLEAR_DEPTH,1,0);
        D3D11_VIEWPORT vp{0,0,float(options.width),float(options.height),0,1};context->RSSetViewports(1,&vp);context->RSSetState(raster.get());context->IASetInputLayout(layout.get());UINT stride=sizeof(Vertex),offset=0;ID3D11Buffer* vb=vertices.get();context->IASetVertexBuffers(0,1,&vb,&stride,&offset);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);context->VSSetShader(vs.get(),nullptr,0);context->PSSetShader(ps.get(),nullptr,0);ID3D11Buffer* cb=constants.get();context->VSSetConstantBuffers(0,1,&cb);context->PSSetConstantBuffers(0,1,&cb);ID3D11ShaderResourceView* srv=textures[material]->view.get();context->PSSetShaderResources(0,1,&srv);ID3D11SamplerState* sp=sampler.get();context->PSSetSamplers(0,1,&sp);
        if(options.scene=="main"){for(const auto& r:s.visibleRanges)context->Draw(r.count,r.first);context->Draw(UINT(s.vertices.size()-s.staticCount),UINT(s.staticCount));}else context->Draw(UINT(s.vertices.size()),0);
        if(measure){context->End(q.end.get());context->End(q.disjoint.get());q.pending=true;}
        HRESULT h=chain->Present(0,tearing?DXGI_PRESENT_ALLOW_TEARING:0);if(h==DXGI_STATUS_OCCLUDED){++occluded;if(index<frames.size()&&!frames[index].controlledBackground)frames[index].valid=false;}Check(h,"DX11 Present");++presents;
    }
    void finish(std::vector<Frame>& frames)override{context->Flush();int64_t end=Qpc()+Frequency()*10;for(;;){bool pending=false;for(auto& q:queries)if(q.pending&&!collect(q,frames))pending=true;if(!pending||Qpc()>end)break;Sleep(1);}for(auto& q:queries)if(q.pending&&q.frame<frames.size()){++gpuMissing;q.pending=false;}}
};
std::unique_ptr<Renderer> MakeDx11(HWND w,const Options& o){return std::make_unique<Dx11>(w,o);}
} // namespace bench
