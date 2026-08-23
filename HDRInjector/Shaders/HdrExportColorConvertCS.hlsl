cbuffer Constants : register(b0)
{
    float sdrWhiteScale;
    float3 _padding;
};

Texture2D<float4> InputTexture : register(t0);
RWTexture2D<float4> OutputTexture : register(u0);

static const float PqM1 = 2610.0 / 16384.0;
static const float PqM2 = 2523.0 / 32.0;
static const float PqC1 = 3424.0 / 4096.0;
static const float PqC2 = 2413.0 / 128.0;
static const float PqC3 = 2392.0 / 128.0;

static const float3x3 Rec709ToBt2020 = float3x3(
    0.6274040, 0.3292820, 0.0433136,
    0.0690970, 0.9195400, 0.0113623,
    0.0163910, 0.0880130, 0.8955950
);

float SrgbToLinear(float x)
{
    x = max(x, 0.0);
    return x <= 0.04045
        ? x / 12.92
        : pow((x + 0.055) / 1.055, 2.4);
}

float PqEncode(float nits)
{
    float x = saturate(nits / 10000.0);
    if (x <= 0.0)
        return 0.0;

    float p = pow(x, PqM1);
    return saturate(pow((PqC1 + PqC2 * p) / (1.0 + PqC3 * p), PqM2));
}

[numthreads(8, 8, 1)]
void main(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint width, height;
    InputTexture.GetDimensions(width, height);

    uint2 p = dispatchThreadId.xy;
    if (p.x >= width || p.y >= height)
        return;

    float4 src = InputTexture.Load(int3(p, 0));
    float a = saturate(src.a);

    float3 rgb;
    if (a > 0.000001)
        rgb = src.rgb / a;
    else
        rgb = 0.0.xxx;

    rgb = float3(
        SrgbToLinear(rgb.r),
        SrgbToLinear(rgb.g),
        SrgbToLinear(rgb.b)
    );

    // Match the existing CPU export path exactly:
    // pseudo-sRGB -> linear -> SDR White scale -> scRGB reference white (80 nit).
    rgb *= max(0.01, sdrWhiteScale);
    rgb *= 80.0;
    rgb = max(rgb, 0.0.xxx);

    // Linear Rec.709 -> linear BT.2020, in nits.
    rgb = mul(Rec709ToBt2020, rgb);
    rgb = max(rgb, 0.0.xxx);

    float luminanceNits = dot(rgb, float3(0.2627, 0.6780, 0.0593));

    float3 pq = float3(
        PqEncode(rgb.r),
        PqEncode(rgb.g),
        PqEncode(rgb.b)
    );

    // BT.2020 non-constant luminance Y'CbCr.
    float yPrime = dot(pq, float3(0.2627, 0.6780, 0.0593));
    float cb = -0.13963 * pq.r - 0.36037 * pq.g + 0.5 * pq.b;
    float cr = 0.5 * pq.r - 0.459786 * pq.g - 0.040214 * pq.b;

    // RGBA = Y', Cb, Cr, luminance/10000 for MaxCLL/MaxFALL measurement.
    OutputTexture[p] = float4(yPrime, cb, cr, saturate(luminanceNits / 10000.0));
}
