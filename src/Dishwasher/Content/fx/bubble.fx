// ============================================================================
// bubble.fx  --  refractive bubble lens
// Blueprint: notes/shaders/decompiled/bubble_t0p0_PS.hlsl  (fx/bubble "Bubble")
// Original reflection parameters (names/types preserved exactly):
//   samplerState:sampler, width:float, xcenter:float, ycenter:float,
//   mag:float, reddish:bool
// Technique: Bubble  Pass: P0
//
// The game drives width/xcenter/ycenter/mag/reddish.  The original computes a
// distance field around (xcenter,ycenter), pulls the lookup toward or past the
// centre by mag, and (reddish) shifts the tint.  This re-author keeps that.
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
float width   = 0.5;
float xcenter = 0.5;
float ycenter = 0.5;
float mag     = 0.5;
bool  reddish = false;

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
    float2 d  = uv - float2(xcenter, ycenter);
    float  dist = length(d);

    float4 col = SAMPLE(uv);

    if (dist < width)
    {
        // Magnify (negative displacement toward the rim).
        float  k   = (width - dist) * 0.5;
        float2 duv = uv - d * k * mag;
        col = SAMPLE(duv);

        if (reddish)
        {
            col.r = saturate(col.r * 1.5);
        }
        else
        {
            col.r *= 0.8;
            col.b *= 1.1;
        }
        col.a = saturate((width - dist) * 4.0);
    }

    return col * input.Color;
}

technique Bubble
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
