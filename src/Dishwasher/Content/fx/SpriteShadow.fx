// ============================================================================
// SpriteShadow.fx  --  offset drop shadow for sprites (the "shadow" pass)
// The original game does not ship a dedicated shadow effect; shadows are
// silhouettes drawn through the sprite path.  This sketch provides the missing
// primitive explicitly: sample the sprite alpha at an offset and emit a dark
// colour, which is what the tinted-shadow draws approximate.
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
sampler2D SpriteTexture : register(s0);
#define SAMPLE(uv) tex2D(SpriteTexture, uv)
#else
Texture2D SpriteTexture : register(t0);
SamplerState SpriteTextureSampler : register(s0);
#define SAMPLE(uv) SpriteTexture.Sample(SpriteTextureSampler, uv)
#endif

// original defaults: offset (2,2) texels, colour (0,0,0,0.5)
float2 ShadowOffset;             // in texels
float2 TextureSize;
float4 ShadowColor;

struct VertexShaderInput
{
    float4 Position : POSITION0;
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

struct VertexShaderOutput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

VertexShaderOutput MainVS(VertexShaderInput input)
{
    VertexShaderOutput output;
    output.Position = input.Position;
    output.Color    = input.Color;
    output.TexCoord = input.TexCoord;
    return output;
}

float4 MainPS(VertexShaderOutput input) : SV_TARGET
{
    float2 uv = input.TexCoord - ShadowOffset / TextureSize;
    float  a  = SAMPLE(uv).a;
    return float4(ShadowColor.rgb, a * ShadowColor.a);
}

technique Technique1
{
    pass Pass1
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
