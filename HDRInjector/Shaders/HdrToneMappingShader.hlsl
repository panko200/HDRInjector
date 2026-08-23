cbuffer Constants : register(b0)
{
    float exposure;
    float gamma;
    float contrast;
    float saturation;
};

Texture2D<float4> InputTexture : register(t0);

SamplerState InputSampler : register(s0)
{
    Filter = MIN_MAG_MIP_LINEAR;
    AddressU = CLAMP;
    AddressV = CLAMP;
};

float GetLuma(float3 c)
{
    return dot(c, float3(0.2126, 0.7152, 0.0722));
}

float4 main(
    float4 pos : SV_POSITION,
    float4 posScene : SCENE_POSITION,
    float4 uv0 : TEXCOORD0
) : SV_Target
{
    float4 src = InputTexture.Sample(InputSampler, uv0.xy);
    float3 rgb = max(src.rgb, 0.0);
    float a = saturate(src.a);

    // Exposure: 2^EV. 1.0超えのHDR値をそのまま維持する。
    rgb *= exp2(exposure);

    // Saturation around Rec.709 luminance.
    float luma = GetLuma(rgb);
    rgb = lerp(float3(luma, luma, luma), rgb, saturation);

    // Match the previous ColorMatrix implementation's contrast pivot.
    float contrastOffset = (1.0 - contrast) * 0.5;
    rgb = rgb * contrast + contrastOffset;
    rgb = max(rgb, 0.0);

    // Match the previous GammaTransfer behavior: exponent = 1 / gamma.
    float safeGamma = max(gamma, 0.1);
    rgb = pow(rgb, 1.0 / safeGamma);

    // Never clamp HDR RGB to [0,1].
    return float4(rgb, a);
}
