// ============================================================================
// ink.fx  --  black ink / outline threshold
// Blueprint: notes/shaders/decompiled/ink_t0p0_PS.hlsl  (fx/ink "Ink")
// Original reflection parameters (names/types preserved exactly):
//   samplerState:sampler, bright:float
// Technique: Ink  Pass: P0
//
// The game drives bright (0.0 normally, 0.4 in the menu-switch overlay).  The
// original samples the red channel, shifts it by uv.y*0.25 + bright, runs it
// through three constant thresholds (0.1/0.4/0.5) and writes an ink level in
// x, a glow in y and an alpha.  This re-author keeps the interface and the
// observable threshold/glow split.
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

float bright = 0.95;

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
    float  v  = SAMPLE(uv).r;          // sampled red drives the ink level
    float  t  = 1.0 - v;

    // Faithful transcription of ink_t0p0_PS.hlsl: a piecewise-linear "level"
    // over t = 1 - red, with knots at 0.1 / 0.4 / 0.5.
    float level;
    if (t < 0.1)        level = t;
    else if (t < 0.4)   level = (t - 0.1) / 3.0 + 0.1;
    else if (t < 0.5)   level = 8.0 * (t - 0.4) + 0.2;
    else                level = 1.0;

    float glow = (level < 0.5) ? (0.45 * (0.5 - level)) : 0.0;
    float red  = uv.y * 0.25 + bright + glow;

    // colour = (red, glow, 0, 0.85*level); tint by the sprite vertex colour.
    return float4(red, glow, 0.0, 0.85 * level) * input.Color;
}

technique Ink
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
