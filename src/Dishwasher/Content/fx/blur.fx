// ============================================================================
// blur.fx  --  radial blast blur (P0) + black-blood red overlay (P1)
// Blueprint: notes/shaders/decompiled/blur_t0p0_PS.hlsl (P0),
//            notes/shaders/decompiled/blur_t0p1_PS.hlsl (P1)
//            fx/blur technique "Blast"
// Original reflection parameters (names/types preserved exactly):
//   samplerState:sampler, levs:int, width:float, xcenter:float,
//   ycenter:float, blackblood:bool
// Technique: Blast  Passes: P0, P1
//
// The game uses Passes[0] for the comic-target radial blur (levs/width/
// xcenter/ycenter) and Passes[1] for drawBlood (blackblood).  The original
// P0 steps from the texel toward (xcenter,ycenter) with a "width"-scaled
// step and 8 unrolled taps; P1 is the red blood-mask overlay.
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
bool  blackblood;

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

// ---- Pass P0 : radial blast blur -------------------------------------------
float4 BlastPS(VSOutput input) : SV_TARGET
{
    float2 uv = input.TexCoord;

    if (levs == 0)
    {
        return SAMPLE(uv) * input.Color;
    }

    float2 dir = float2(xcenter - uv.x, ycenter - uv.y);
    float2 stp = dir * (width * 0.001);

    float4 sum = SAMPLE(uv);
    [unroll]
    for (int i = 1; i <= 8; ++i)
    {
        sum += SAMPLE(uv - stp * ((float)i * 0.125));
    }
    return sum * (1.0 / 9.0) * input.Color;
}

// ---- Pass P1 : black-blood red overlay -------------------------------------
float4 BloodPS(VSOutput input) : SV_TARGET
{
    float2 uv  = input.TexCoord;
    float4 c   = SAMPLE(uv);
    float  lum = max(max(c.r, c.g), c.b);

    float red;
    float a;
    if (blackblood)
    {
        // Strong black-blood mask: thin, high-contrast red.
        red = saturate((lum - 0.9) * 4.0);
        a   = saturate(lum * 3.0);
    }
    else
    {
        red = saturate(lum);
        a   = saturate(lum + 0.25);
    }
    return float4(red, 0.0, 0.0, a) * input.Color;
}

technique Blast
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL BlastPS();
    }
    pass P1
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL BloodPS();
    }
}
