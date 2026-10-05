// ============================================================================
// grad.fx  --  full-screen gradient tint + radial "burn" blur
// Blueprint: notes/shaders/decompiled/grad_t0p0_PS.hlsl  (fx/grad "PostGrad")
// Original reflection parameters (names/types preserved exactly):
//   samplerState:sampler, burnmag:float, gradFlip:bool, rgrad:float,
//   ggrad:float, bgrad:float, bright:float, levs:int, width:float,
//   xcenter:float, ycenter:float
// Technique: PostGrad  Pass: P0
//
// The game drives gradFlip, rgrad/ggrad/bgrad, bright, burnmag, levs,
// xcenter, ycenter and width (always as a float).  The original blurs
// radially around (xcenter,ycenter) with step width when levs>0, then adds a
// vertical (or flipped) gradient and scales by bright, subtracting the burn.
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
float burnmag = 0.0;
bool  gradFlip = false;
float rgrad = 1.0;
float ggrad = 1.0;
float bgrad = 1.0;
float bright = 1.1;
int   levs = 0;
float width = 200.0;
float xcenter = 0.0;
float ycenter = 0.0;

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
    float4 acc = SAMPLE(uv);

    // Faithful transcription of grad_t0p0_PS.hlsl: when levs>0 walk away from
    // (xcenter,ycenter) by dir/width seven times, accumulating 8 samples / 8.
    if (levs > 0)
    {
        float2 p = uv;
        [unroll]
        for (int i = 0; i < 7; ++i)
        {
            float2 dir = float2(p.x - xcenter, p.y - ycenter);
            p -= dir * (1.0 / max(width, 0.000001));
            acc += SAMPLE(p);
        }
        acc *= 0.125;
    }

    // Vertical (or flipped) gradient divided by the theme's per-channel ramps.
    float  t   = gradFlip ? (1.0 - uv.y) : uv.y;
    float3 grd = t / float3(max(rgrad, 0.000001),
                            max(ggrad, 0.000001),
                            max(bgrad, 0.000001));

    float3 col = (acc.rgb + grd) * bright;

    // Burn: burnmag * distance from the screen centre (microcode uses 0.5,0.5).
    if (burnmag > 0.0)
    {
        float d = length(uv - 0.5);
        col -= burnmag * d;
    }

    return float4(max(col, 0.0), acc.a) * input.Color;
}

technique PostGrad
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
