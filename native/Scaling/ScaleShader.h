#pragma once
// Original spatial reconstruction: bilinear sampling with a bounded unsharp
// mask. The limiter avoids halos outside the immediate source neighbourhood.
static const char* ScaleShader = R"HLSL(
Texture2D source : register(t0);
SamplerState linearClamp : register(s0);
cbuffer Params : register(b0) { float4 region; float4 tuning; };
struct Vertex { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
Vertex VS(uint id : SV_VertexID) {
    Vertex v;
    v.uv=float2((id<<1)&2,id&2);
    v.pos=float4(v.uv*float2(2,-2)+float2(-1,1),0,1);
    return v;
}
float3 sampleAt(float2 uv) { return source.SampleLevel(linearClamp, clamp(uv,region.xy+tuning.xy*.5,region.xy+region.zw-tuning.xy*.5),0).rgb; }
float4 PS(Vertex v) : SV_TARGET {
    float2 uv=region.xy+v.uv*region.zw;
    float3 c=sampleAt(uv);
    float3 n=sampleAt(uv-float2(0,tuning.y));
    float3 s=sampleAt(uv+float2(0,tuning.y));
    float3 w=sampleAt(uv-float2(tuning.x,0));
    float3 e=sampleAt(uv+float2(tuning.x,0));
    float3 lo=min(c,min(min(n,s),min(w,e))), hi=max(c,max(max(n,s),max(w,e)));
    return float4(clamp(c+(c-(n+s+w+e)*.25)*tuning.z,lo,hi),1);
}
)HLSL";
