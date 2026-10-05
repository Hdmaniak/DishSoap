// ============================================================================
// fade.fx  --  circular fade / vignette
// Blueprint: notes/shaders/decompiled/fade_t0p0_PS.hlsl  (fx/fade "PostFade")
// Original reflection parameters (names/types preserved exactly):
//   samplerState:sampler, rad:float, fader:int, offsets(float2[12])
// Technique: PostFade  Pass: P0
//
// The game drives rad and fader.  The original averages 12 taps offset by
// offsets[i]*rad, then applies one of two radial masks depending on the value
// of fader (1 => scale -0.6, bias +1.5; otherwise => scale +2.0, bias -0.25).
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

float rad = 0.015;
int   fader = 0;
// defaults from the Xbox constant table
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

    // Faithful transcription of fade_t0p0_PS.hlsl: 12 offset taps plus the
    // centre tap (13 total).  The microcode keeps BOTH the raw 13-tap sum and
    // the /13 average; the three fader paths use different combinations.
    float3 sum = SAMPLE(uv).rgb;
    [unroll]
    for (int i = 0; i < 12; ++i)
    {
        sum += SAMPLE(uv + offsets[i] * rad).rgb;
    }
    float3 blur = sum * (1.0 / 13.0);

    // Faithful branch decode of fade_t0p0_PS.hlsl.  The microcode tests
    // `fader == 1`; BOTH other values (0 and 2) take the same path:
    //   fader == 1 : rgb = sum * 0.1154,  a = max(1.5*dist - 0.1, 0) * 0.15
    //   fader != 1 : rgb = sum / 13,      a = max(2.0*dist - 0.25, 0)
    // (c253=(-0.1,2.0), c254=(0,1,1/13,-0.5), c255=(0.1154,0.15,1.5,-0.25).)
    // Gameplay calls drawFade(...,fader=0): the correct output is therefore the
    // AVERAGED blurred scene with a centre-transparent radial alpha mask, NOT
    // the opaque full-screen blur the previous re-author emitted.
    float4 outc;
    if (fader == 1)
    {
        float dist = length(uv - 0.5);
        outc = float4(sum * 0.1154, max(1.5 * dist - 0.1, 0.0) * 0.15);
    }
    else
    {
        float dist = length(uv - 0.5);
        outc = float4(blur, max(2.0 * dist - 0.25, 0.0));
    }

    // MonoGame SpriteBatch passes the per-draw tint as the vertex colour; the
    // original Xbox sprite pipeline applied it, so honour it here.
    return outc * input.Color;
}

technique PostFade
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
