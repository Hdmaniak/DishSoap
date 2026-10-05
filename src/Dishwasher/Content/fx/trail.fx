// ============================================================================
// trail.fx  --  directional smear / motion trail
// Blueprint: notes/shaders/decompiled/trail_t0p0_PS.hlsl  (fx/trail "PostTrail")
// Original reflection parameters (names/types preserved exactly):
//   samplerState:sampler, tx:float, ty:float, alpha:float, r:float,
//   g:float, b:float
// Technique: PostTrail  Pass: P0
//
// The game loads trail.fx and passes it into Character.Draw as the trail
// effect but never writes its parameters, so the Xbox defaults apply
// (tx=ty=0, alpha=0, r=g=b=1).  The original averages 12 taps stepping from
// uv - 5.5*(tx,ty) to uv + 5.5*(tx,ty) and multiplies the channels by
// r/g/b and alpha.  Reproduced faithfully here.
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

float tx    = 0.0;
float ty    = 0.0;
float alpha = 0.0;
float r     = 1.0;
float g     = 1.0;
float b     = 1.0;

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
    float2 uv   = input.TexCoord;
    float2 step = float2(tx, ty) * 5.5;
    float2 p    = uv - step;

    float4 sum = 0.0;
    [unroll]
    for (int i = 0; i < 12; ++i)
    {
        sum += SAMPLE(p);
        p   += step;
    }
    sum *= (1.0 / 12.0);

    return float4(sum.r * r, sum.g * g, sum.b * b, sum.a * alpha) * input.Color;
}

technique PostTrail
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
