// ============================================================================
// Sprite.fx  --  base 2D sprite drawing / tint (SpriteBatch-compatible)
// Re-authored for MonoGame / MGCB.  There is no *_sprite.xnb in the Xbox 360
// set: the game draws sprites with SpriteBatch's built-in effect and uses the
// fx/* effects only as post-process passes.  This is the reference sprite
// effect the post-process sketches are modelled on.
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
    return SAMPLE(input.TexCoord) * input.Color;
}

technique Technique1
{
    pass Pass1
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
