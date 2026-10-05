// ============================================================================
// wallblood.fx  --  wall blood-splat mask
// Blueprint: notes/shaders/decompiled/wallblood_t0p0_PS.hlsl
//            fx/wallblood technique "WallBlood"
// Original reflection parameters (names/types preserved exactly):
//   samplerState:sampler, red:float, offsets(float2[6]) with embedded defaults
// Technique: WallBlood  Pass: P0
//
// The game drives only red.  The original samples 6 taps offset by
// offsets[i]*0.007 and folds the products of the channel pairs into a mask,
// writing (red*0.4, 0, 0, mask).  Defaults reproduced from the constant table.
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

float red = 0.0;
float2 offsets[6] =
{
    float2( 1.0,  0.0),
    float2( 0.5,  0.86),
    float2(-0.5,  0.86),
    float2(-1.0,  0.0),
    float2(-0.5, -0.86),
    float2( 0.5, -0.86)
};

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

    float m = 0.0;
    [unroll]
    for (int i = 0; i < 6; ++i)
    {
        float2 s = SAMPLE(uv + offsets[i] * 0.007).xy;
        m += s.x * s.y;
    }
    m = saturate(m * 0.3);

    return float4(red * 0.4, 0.0, 0.0, m) * input.Color;
}

technique WallBlood
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
