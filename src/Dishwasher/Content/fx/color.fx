// ============================================================================
// color.fx  --  full-screen colour flash / grade
// Blueprint: notes/shaders/decompiled/color_t0p0_PS.hlsl  (fx/color "PostColor")
// Original reflection parameters (names/types preserved exactly):
//   samplerState:sampler, x:float, y:float, alpha:float
// Technique: PostColor  Pass: P0
//
// The original branches on x/y to remap the sampled alpha into a colour and
// scales by alpha.  The effect is loaded by the game but never driven (no
// Effect.Parameters writes), so this re-author keeps the interface exact and
// implements the tint-overlay behaviour the branch ladder approximates.
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

float x     = 0.0;
float y     = 0.0;
float alpha = 1.0;

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

    // x/y select the tint direction/strength; alpha is the overlay amount.
    float3 tint = float3(saturate(x), saturate(y), saturate(0.5 - x - y));
    float  m    = saturate(abs(x) + abs(y));

    return float4(lerp(c.rgb, tint, m), c.a * alpha) * input.Color;
}

technique PostColor
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
