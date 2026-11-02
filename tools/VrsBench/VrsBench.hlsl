cbuffer Params : register(b0) { uint seed; };

struct VOut {
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

VOut VSMain(uint id : SV_VertexID) {
    float2 p = float2((id << 1) & 2, id & 2);
    VOut output;
    output.position = float4(p * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    output.uv = p;
    return output;
}

float4 PSMain(VOut input) : SV_Target {
    float2 p = input.uv * 1.731 + float2((seed & 255) * 0.00031, (seed >> 8) * 0.00017);
    float4 value = float4(p, p.x + p.y, p.x - p.y) + 0.1234;
    [unroll(72)]
    for (int index = 0; index < 72; ++index) {
        float4 mixed = sin(value.wxyz * float4(1.137, 1.271, 1.419, 1.613) +
                           float4(p.x, p.y, p.x + p.y, p.x - p.y) + index * 0.0137);
        value = frac(abs(mixed * 1.6180339 + value * 0.381966 + 0.071 * index));
    }
    return float4(value.xyz, 1.0);
}
