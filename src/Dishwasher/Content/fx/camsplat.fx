// ============================================================================
// camsplat.fx  --  camera blood-splat mask
// Blueprint: notes/shaders/decompiled/camsplat_t0p0_PS.hlsl
//            fx/camsplat technique "CamSplat"
// Original reflection parameters (names/types preserved exactly):
//   samplerState:sampler, red:float
// Technique: CamSplat  Pass: P0
//
// The game toggles "red" between 0 (blood setting 2) and 1.  The original
// samples the red channel, compares it against neighbouring taps to build a
// splat mask and writes (red * mask, 0, 0, mask * 0.7).
// ============================================================================
#if OPENGL
#define SV_POSITION POSITION
#define SV_TARGET COLOR
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define SV_TARGET SV_Target0
#define VS_SHADERMODEL vs_6_0
#define PS_SHADERMODEL ps_6_0
#endif

#if OPENGL
sampler2D samplerState : register(s0);
#define SAMPLE(uv) tex2D(samplerState, uv)
#else
Texture2D samplerStateTexture : register(t0);
SamplerState samplerStateSampler : register(s0);
#define SAMPLE(uv) samplerStateTexture.Sample(samplerStateSampler, uv)
#endif

float red = 1.0;

// MonoGame SpriteBatch supplies pixel-space vertices; like its built-in
// SpriteEffect, we must transform them to clip space via the ortho projection.
float4x4 MatrixTransform;

struct VSInput
{
    float4 Position : POSITION0;
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

struct VSOutput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

VSOutput MainVS(VSInput input)
{
    VSOutput o;
    o.Position = mul(input.Position, MatrixTransform);
    o.Color    = input.Color;
    o.TexCoord = input.TexCoord;
    return o;
}

float4 MainPS(VSOutput input) : SV_TARGET
{
    float2 uv = input.TexCoord;

    float r  = SAMPLE(uv).r;
    float rl = SAMPLE(uv + float2(-0.02, 0.0)).r;
    float rr = SAMPLE(uv + float2( 0.02, 0.0)).r;
    float ru = SAMPLE(uv + float2(0.0, -0.02)).r;
    float rd = SAMPLE(uv + float2(0.0,  0.02)).r;

    float edge = saturate(r - min(min(rl, rr), min(ru, rd)));
    float m    = smoothstep(0.25, 0.5, edge);

    return float4(red * m, 0.0, 0.0, m * 0.7) * input.Color;
}

technique CamSplat
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
