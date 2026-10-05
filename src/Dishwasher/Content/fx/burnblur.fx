// ============================================================================
// burnblur.fx  --  blast blur + burn darkening
// Blueprint: notes/shaders/decompiled/burnblur_t0p0_PS.hlsl
//            fx/burnblur technique "Blast"
// Original reflection parameters (names/types preserved exactly):
//   samplerState:sampler, levs:int, width:float, xcenter:float,
//   ycenter:float, mag:float
// Technique: Blast  Pass: P0
//
// The game drives mag, xcenter/ycenter, levs, width.  The original blurs
// radially around the blast centre and subtracts a distance*mag burn term.
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

int   levs   = 1;
float width  = 200.0;
float xcenter = 0.5;
float ycenter = 0.5;
float mag    = 1.25;

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
    float4 col = SAMPLE(uv);

    if (levs > 0)
    {
        float2 dir = float2(xcenter - uv.x, ycenter - uv.y);
        float2 stp = dir * (width * 0.001);
        float4 sum = col;
        [unroll]
        for (int i = 1; i <= 8; ++i)
        {
            sum += SAMPLE(uv - stp * ((float)i * 0.125));
        }
        col = sum * (1.0 / 9.0);
    }

    // Burn: darken with distance from the blast centre.
    float d = length(uv - float2(xcenter, ycenter));
    col.rgb = max(col.rgb - d * mag * 0.4, 0.0);
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
