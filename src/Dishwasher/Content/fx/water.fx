// ============================================================================
// water.fx  --  water-surface wave refraction
// Blueprint: notes/shaders/decompiled/water_t0p0_PS.hlsl  (fx/water "PostWater")
// Original reflection parameters (names/types preserved exactly):
//   samplerState:sampler, horizon:float, delta:float, theta:float,
//   rnd:float, puddle:bool
// Technique: PostWater  Pass: P0
//
// The game drives horizon, delta, theta, rnd and puddle.  The original builds
// a wave phase from delta/theta, offsets the lookup horizontally below the
// horizon and writes the sampled colour; "puddle" attenuates the alpha.
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

float horizon;
float delta;
float theta;
float rnd;
bool  puddle;

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

    // Wave phase driven by the game's delta/theta/rnd.
    float phase = uv.x * 12.0 + delta + frac(rnd * 0.0001);
    float wave  = sin(phase) * 0.0025 + cos(phase * 1.7 + theta) * 0.004;

    float below = uv.y - horizon;
    float4 col  = float4(0.0, 0.0, 0.0, 0.0);

    if (below >= 0.0)
    {
        float2 duv = uv + float2(wave, wave * 0.5);
        col = SAMPLE(duv);
        col.a = saturate(1.0 - below * 0.2);
    }

    if (puddle)
    {
        col.a *= saturate(1.0 - horizon) * 0.8;
    }

    return col * input.Color;
}

technique PostWater
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
