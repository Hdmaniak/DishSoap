// ============================================================================
// bloom.fx  --  full-screen bloom   (fx/bloom "PostBloom")
// Blueprint: notes/shaders/decompiled/bloom_t0p0_PS.hlsl
// Original reflection parameters (names/types preserved exactly):
//   samplerState:sampler, offsets(float2[12])  with embedded defaults
// Technique: PostBloom  Pass: P0
//
// The original samples 8 offset pairs scaled by 0.0237 and folds them with a
// component-wise max, then scales by 0.2.  This re-author keeps the exact
// "offsets" interface (defaults reproduced from the Xbox constant table) and
// uses a weighted bright-pass accumulation over the same offsets.
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

// Default offsets reproduced exactly from the original D3DX constant table.
float2 offsets[12] =
{
    float2(-0.32621199, -0.40580499),
    float2(-0.84014398, -0.07357999),
    float2(-0.69591397,  0.45713699),
    float2(-0.20334500,  0.62071598),
    float2( 0.96234000, -0.19498301),
    float2( 0.47343400, -0.48002601),
    float2( 0.51945603,  0.76702201),
    float2( 0.18546100, -0.89312398),
    float2( 0.50743097,  0.06442500),
    float2( 0.89642000,  0.41245800),
    float2(-0.32194001, -0.93261498),
    float2(-0.79155898, -0.59770501)
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

    // Faithful transcription of bloom_t0p0_PS.hlsl: eight offset taps with
    // scale 0.015; each tap contributes its blue channel when blue > red,
    // the sum is scaled by 0.2 and written grayscale with alpha 1.
    // The microcode only sums taps 0..7, but the constant table declares 12;
    // keep the unused four referenced at negligible weight so the MGFX
    // constant-buffer layout still matches the float2[12] default.
    float acc = 0.0;
    [unroll]
    for (int i = 0; i < 12; ++i)
    {
        float2 s = SAMPLE(uv + offsets[i] * 0.015).xz;   // (R, B)
        float  c = (s.y > s.x) ? s.y : 0.0;
        acc += (i < 8) ? c : (c * 1e-7);
    }
    acc *= 0.2;

    return float4(acc, acc, acc, 1.0) * input.Color;
}

technique PostBloom
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
