// ============================================================================
// trainover.fx  --  train motion streak overlay
// Blueprint: notes/shaders/decompiled/trainover_t0p0_PS.hlsl
//            fx/trainover technique "Train"
// Original reflection parameters (names/types preserved exactly):
//   samplerState:sampler, facta:float
// Technique: Train  Pass: P0
//
// The game drives only facta.  The original averages a fixed 8-tap x-ward
// smear whose step is driven by facta and the centre luminance, then derives
// alpha from the vertical position.  This re-author follows that structure.
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

float facta;

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
    float2 uv  = input.TexCoord;
    float4 cen = SAMPLE(uv);

    // Step driven by facta and the centre intensity.
    float2 step = float2(cen.r * facta * 12.0, 0.0);
    float4 sum  = cen;
    float2 p    = uv;
    [unroll]
    for (int i = 0; i < 8; ++i)
    {
        p += step;
        sum += SAMPLE(p);
    }
    sum *= (1.0 / 9.0);

    float a = saturate((sum.r + 0.1) * 5.0) * saturate(1.0 - uv.y);
    return float4(sum.rgb, a) * input.Color;
}

technique Train
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
