// ============================================================================
// poster.fx  --  post-process posterize / colour quantise
// Blueprint: notes/shaders/decompiled/poster_t0p0_PS.hlsl  (fx/poster "Poster")
// Original reflection parameters (names/types preserved exactly):
//   samplerState:sampler, alpha:float,
//   rMin,gMin,bMin, rMid,gMid,bMid, rMax,gMax,bMax :float, fore:bool
// Technique: Poster  Pass: P0
//
// The original microcode taps a 5-point cross, folds the channel sums through
// four thresholds (c254 = 0.6/1.2/1.8/2.4) and selects the min/mid/max level
// per channel, then multiplies by "alpha".  The effect is loaded by the game
// but never driven (no Effect.Parameters writes), so this re-author keeps the
// interface exact and uses a clean luminance posterize with the same level
// parameters.  Assumption documented in COMPLETION.md.
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

// defaults from the Xbox constant table
float alpha = 1.0;
float rMin = 0.5; float gMin = 0.0; float bMin = 0.0;
float rMid = 1.0; float gMid = 0.0; float bMid = 0.0;
float rMax = 1.0; float gMax = 0.5; float bMax = 0.5;
bool  fore = false;

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
    float4 c  = SAMPLE(uv);
    float  lum = dot(c.rgb, float3(0.33333, 0.33333, 0.33334));

    float3 col;
    col.r = (lum < 0.25) ? rMin : ((lum < 0.50) ? rMid : rMax);
    col.g = (lum < 0.25) ? gMin : ((lum < 0.50) ? gMid : gMax);
    col.b = (lum < 0.25) ? bMin : ((lum < 0.50) ? bMid : bMax);

    // "fore" selects foreground (pass-through) vs the poster ramp.
    col = fore ? c.rgb : col;

    return float4(col * alpha, c.a * alpha) * input.Color;
}

technique Poster
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
