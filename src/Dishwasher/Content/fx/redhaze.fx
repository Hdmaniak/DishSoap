// ============================================================================
// redhaze.fx  --  red glare/haze streak
// Blueprint: notes/shaders/decompiled/redhaze_t0p0_PS.hlsl  (fx/redhaze "PostHaze")
// Original reflection parameters (names/types preserved exactly):
//   samplerState:sampler, horiz:bool, glare:bool
// Technique: PostHaze  Pass: P0
//
// The game drives horiz and glare.  The original walks 5 taps up and 5 taps
// down a single axis (step 0.007), accumulates the red channel above a 0.9
// threshold, and writes redhaze * (0.5, 0.125, 0.25).  This re-author follows
// that structure; when glare is set an extra centre tap is folded in.
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

bool horiz;
bool glare;

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
    float2 uv    = input.TexCoord;
    float2 step  = horiz ? float2(0.007, 0.0) : float2(0.0, 0.007);

    float acc = 0.0;
    [unroll]
    for (int i = 1; i <= 5; ++i)
    {
        float r = SAMPLE(uv + step * (float)i).r;
        acc += max(r - 0.9, 0.0);
    }
    [unroll]
    for (int j = 1; j <= 5; ++j)
    {
        float r = SAMPLE(uv - step * (float)j).r;
        acc += max(r - 0.9, 0.0);
    }

    if (glare)
    {
        acc += max(SAMPLE(uv).r - 0.9, 0.0);
    }

    acc *= 0.1;
    float3 col = acc * float3(0.5, 0.125, 0.25);
    return float4(col, 1.0) * input.Color;
}

technique PostHaze
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
