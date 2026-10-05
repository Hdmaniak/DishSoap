// ============================================================================
// trainblur.fx  --  train horizontal motion blur + blast blur
// Blueprint: notes/shaders/decompiled/trainblur_t0p0_PS.hlsl
//            fx/trainblur technique "Blast"
// Original reflection parameters (names/types preserved exactly):
//   samplerState:sampler, levs:int, width:float, xcenter:float,
//   ycenter:float, facta:float, horizon:float
// Technique: Blast  Pass: P0
//
// The game drives xcenter/ycenter/levs/width (radial blast, levs>0) and
// facta/horizon (train smear).  The original is blur P0 plus a horizon-gated
// second sample offset in x by facta*luminance.  This re-author keeps both.
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

int   levs;
float width;
float xcenter;
float ycenter;
float facta;
float horizon;

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
    float4 cen = SAMPLE(uv);
    float4 col = cen;

    if (levs > 0)
    {
        float2 dir = float2(xcenter - uv.x, ycenter - uv.y);
        float2 stp = dir * (width * 0.001);
        float4 sum = cen;
        [unroll]
        for (int i = 1; i <= 8; ++i)
        {
            sum += SAMPLE(uv - stp * ((float)i * 0.125));
        }
        col = sum * (1.0 / 9.0);
    }

    // Train smear below the horizon.
    if (uv.y > horizon)
    {
        float2 suv = uv;
        suv.x += cen.r * facta;
        col = (col + SAMPLE(suv)) * 0.5;
    }

    return col * input.Color;
}

technique Blast
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
