// ============================================================================
// lense.fx  --  lens refraction over the back buffer
// Blueprint: notes/shaders/decompiled/lense_t0p0_PS.hlsl  (fx/lense "PostLense")
// Original reflection parameters (names/types preserved exactly):
//   refractSampler:sampler, backBuffer:sampler, edgeBlur:float, offsets(float4[6])
// Technique: PostLense  Pass: P0
//
// The game binds its sprite texture (refractTarg) to sampler 0 and sets
// GraphicsDevice.Textures[1] = rTarg (the scene) for backBuffer; it drives
// only "edgeBlur".  The original samples the red channel of refractSampler at
// +/-1 texel to form a refraction gradient, displaces the backBuffer lookup,
// then (when edgeBlur > 0) averages 8 edge-weighted taps.  This re-author
// keeps the two-sampler interface and that structure.
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
sampler2D refractSampler : register(s0);
sampler2D backBuffer     : register(s1);
#define SAMPLE_REFRACT(uv) tex2D(refractSampler, uv)
#define SAMPLE_BACK(uv)    tex2D(backBuffer, uv)
#else
Texture2D refractTexture    : register(t0);
Texture2D backBufferTexture : register(t1);
SamplerState lenseSampler   : register(s0);
#define SAMPLE_REFRACT(uv) refractTexture.Sample(lenseSampler, uv)
#define SAMPLE_BACK(uv)    backBufferTexture.Sample(lenseSampler, uv)
#endif

float  edgeBlur = 0.0;
// defaults from the Xbox constant table
float2 offsets[6] =
{
    float2( 1.0,  0.0),
    float2( 0.5,  0.866),
    float2(-0.5,  0.866),
    float2(-1.0,  0.0),
    float2(-0.5, -0.866),
    float2( 0.5, -0.866)
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

    // Refraction gradient from the red channel of the refract texture.
    float rC = SAMPLE_REFRACT(uv).r;
    float rU = SAMPLE_REFRACT(uv + float2( 0.002,  0.000)).r;
    float rD = SAMPLE_REFRACT(uv + float2(-0.002,  0.000)).r;
    float rL = SAMPLE_REFRACT(uv + float2( 0.000, -0.002)).r;
    float rR = SAMPLE_REFRACT(uv + float2( 0.000,  0.002)).r;

    float2 grad = float2(rD - rU, rL - rR);
    float2 suv  = saturate(uv + grad * 0.6);

    float4 col = SAMPLE_BACK(suv);

    if (edgeBlur > 0.0)
    {
        // Edge-weighted 9-tap blur of the displaced lookup.
        float dist = length(suv - 0.5);
        float amt  = saturate((dist - 0.01) * edgeBlur);
        float4 sum = col;
        [unroll]
        for (int i = 0; i < 6; ++i)
        {
            sum += SAMPLE_BACK(suv + offsets[i].xy * amt);
        }
        col = sum / 7.0;
    }

    return col * input.Color;
}

technique PostLense
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
