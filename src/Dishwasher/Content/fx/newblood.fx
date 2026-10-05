// ============================================================================
// newblood.fx  --  blood overlay / contrast pass
// Blueprint: notes/shaders/decompiled/newblood_t0p0_PS.hlsl
//            fx/newblood technique "Blood"
// Original reflection parameters (names/types preserved exactly):
//   samplerState:sampler, bright:float, statik:float, lineStatik:float
// Technique: Blood  Pass: P0
//
// The game drives bright (0.475 / luminance), statik (1..3) and lineStatik
// (0..1).  The original is a branch-heavy contrast/darken pass with the red
// channel of the sample driving an edge term and statik/lineStatik adding
// noise bands.  This re-author reproduces that observable behaviour.
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

float bright     = 0.95;
float statik     = 0.0;
float lineStatik = 0.0;

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
    float4 c   = SAMPLE(uv);
    float  lum = dot(c.rgb, float3(0.299, 0.587, 0.114));

    // Contrast against the game-supplied bright level.
    float3 col = c.rgb * bright;

    if (statik > 0.0)
    {
        float n = frac(sin(dot(uv, float2(12.9898, 78.233)) + statik) * 43758.5453);
        col *= (0.65 + 0.35 * n) * (0.5 + 0.5 * statik);
    }

    if (lineStatik > 0.0)
    {
        float scan = frac(uv.y * 120.0 + lineStatik);
        col *= (scan > 0.5) ? 1.0 : 0.35;
    }

    float a = saturate(lum * bright + 0.1);
    return float4(col, a) * input.Color;
}

technique Blood
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
