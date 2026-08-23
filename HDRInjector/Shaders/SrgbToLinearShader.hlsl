// SDR (sRGB / Rec.709 transfer curve) -> linear RGB.
// Input is assumed to be premultiplied alpha, so un-premultiply before
// decoding RGB and premultiply again afterwards.
cbuffer Constants : register(b0)
{
    float SdrWhiteScale;
    float3 _padding;
};

Texture2D<float4> InputTexture : register(t0);

SamplerState InputSampler : register(s0)
{
    Filter = MIN_MAG_MIP_LINEAR;
    AddressU = CLAMP;
    AddressV = CLAMP;
};

float SrgbToLinear(float x)
{
    x = max(x, 0.0);
    return (x <= 0.04045)
        ? x / 12.92
        : pow((x + 0.055) / 1.055, 2.4);
}

float4 main(
    float4 pos : SV_POSITION,
    float4 posScene : SCENE_POSITION,
    float4 uv0 : TEXCOORD0
) : SV_Target
{
    float4 src = InputTexture.Sample(InputSampler, uv0.xy);
    float a = saturate(src.a);

    // D2D/YMM4 images are normally premultiplied-alpha.
    // Decode the color in straight-alpha space, then premultiply again.
    float3 srgb = (a > 0.000001) ? src.rgb / a : 0.0.xxx;
    float3 linearRgb = float3(
        SrgbToLinear(srgb.r),
        SrgbToLinear(srgb.g),
        SrgbToLinear(srgb.b)
    );

    return float4(linearRgb * SdrWhiteScale * a, a);
}
