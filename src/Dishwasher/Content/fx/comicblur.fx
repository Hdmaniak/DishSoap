// ============================================================================
// comicblur.fx  --  comic speed-line directional blur
// Blueprint: notes/shaders/decompiled/comicblur_t0p0_PS.hlsl (uses a, speed)
//            fx/comicblur technique "ComicBlur"
// Original reflection parameters (names/types preserved exactly):
//   samplerState:sampler, left1, top1, right1, bottom1,
//   left2, top2, right2, bottom2, a, speed :float
// Technique: ComicBlur  Pass: P0
//
// The game drives only a and speed.  In the original the direction is fixed
// from "a" (frac(a*0.011+0.5)*2pi - pi) and the loop accumulates "speed" per
// tap; when speed <= 0 the pass is a straight copy.  The left/top/... group is
// present in the constant table but not referenced by the microcode body; it is
// declared here to preserve the interface.
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

float left1;   float top1;    float right1;  float bottom1;
float left2;   float top2;    float right2;  float bottom2;
float a;
float speed;

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

#define TWO_PI 6.2831853

float4 MainPS(VSOutput input) : SV_TARGET
{
    float2 uv  = input.TexCoord;
    float3 col = SAMPLE(uv).rgb;

    if (speed != 0.0)
    {
        float  ang  = frac(a * 0.011 + 0.5) * TWO_PI - 3.14159265;
        float2 step = float2(cos(ang), sin(ang)) * speed;
        float2 p    = uv;
        [unroll]
        for (int i = 0; i < 8; ++i)
        {
            p += step;
            col += SAMPLE(p).rgb;
        }
        col *= (1.0 / 9.0);
    }

    return float4(col, 1.0) * input.Color;
}

technique ComicBlur
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
