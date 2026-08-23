cbuffer Constants : register(b0)
{
    // Actual SDR reference white in nits used by the existing YMM4 HDR bridge.
    // The existing CPU/D2D path uses 80 * CurrentSdrWhiteScale.
    float sdrWhiteNits;
    // 0 = PQ, 1 = HLG. The current test material is HLG.
    float transferMode;
    float2 _padding;
};

Texture2D<float> YTexture : register(t0);
Texture2D<float2> UVTexture : register(t1);
RWTexture2D<float4> OutputTexture : register(u0);

static const float PQ_M1 = 0.1593017578125;
static const float PQ_M2 = 78.84375;
static const float PQ_C1 = 0.8359375;
static const float PQ_C2 = 18.8515625;
static const float PQ_C3 = 18.6875;

float HlgInverseOetf(float e)
{
    const float a = 0.17883277;
    const float b = 0.28466892;
    const float c = 0.55991073;
    e = saturate(e);
    if (e <= 0.5)
        return (e * e) / 3.0;
    return (exp((e - c) / a) + b) / 12.0;
}

float PqToLinearNits(float pq)
{
    float n = max(pq, 0.0);
    float n_m2 = pow(n, 1.0 / PQ_M2);
    float num = max(n_m2 - PQ_C1, 0.0);
    float den = max(PQ_C2 - PQ_C3 * n_m2, 0.000001);
    return pow(num / den, 1.0 / PQ_M1) * 10000.0;
}

float SrgbEncodeUnclamped(float x)
{
    x = max(x, 0.0);
    return (x <= 0.0031308)
        ? 12.92 * x
        : 1.055 * pow(x, 1.0 / 2.4) - 0.055;
}

float3 LinearToPipelineSrgb(float3 c)
{
    return float3(
        SrgbEncodeUnclamped(c.r),
        SrgbEncodeUnclamped(c.g),
        SrgbEncodeUnclamped(c.b));
}

float3 BT2020ToRec709(float3 c)
{
    float3x3 m = float3x3(
         1.6604910, -0.5876411, -0.0728499,
        -0.1245505,  1.1328999, -0.0083494,
        -0.0181508, -0.1005789,  1.1187297
    );
    return mul(m, c);
}

[numthreads(8,8,1)]
void main(uint3 id : SV_DispatchThreadID)
{
    uint w, h;
    YTexture.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;

    // P010 stores 10-bit video-range samples in the high bits of 16-bit words.
    // The R16/R16G16 UNORM views expose those words normalized to [0,1].
    float yStored = YTexture.Load(int3(id.xy, 0));
    uint2 uvPos = uint2(id.x >> 1, id.y >> 1);
    float2 uvStored = UVTexture.Load(int3(uvPos, 0));

    float y10 = yStored * 1023.0;
    float cb10 = uvStored.x * 1023.0;
    float cr10 = uvStored.y * 1023.0;

    // BT.2020 non-constant-luminance, 10-bit video range.
    float y = saturate((y10 - 64.0) / 876.0);
    float cb = (cb10 - 512.0) / 896.0;
    float cr = (cr10 - 512.0) / 896.0;

    float3 rgbSignal;
    rgbSignal.r = y + 1.678674 * cr;
    rgbSignal.g = y - 0.187326 * cb - 0.650424 * cr;
    rgbSignal.b = y + 2.141772 * cb;
    rgbSignal = saturate(rgbSignal);

    // Match the existing HdrColorConvertShader exactly at the transfer/gamut boundary:
    // HDR transfer -> linear scene/display-relative light -> BT.2020 to Rec.709 ->
    // pipeline-sRGB. YMM4's later global sRGB->Linear stage then recovers the HDR linear
    // value and applies its SDR-white scale, so the 80-nit reference cancels correctly.
    float3 linearRgb;
    if (transferMode < 0.5)
    {
        // PQ: 10,000-nit absolute linear luminance -> normalize by SDR reference white.
        linearRgb = float3(
            PqToLinearNits(rgbSignal.r),
            PqToLinearNits(rgbSignal.g),
            PqToLinearNits(rgbSignal.b)) / max(1.0, sdrWhiteNits);
    }
    else
    {
        // HLG: relative scene light -> 1,000-nit nominal display range, then SDR-white scale.
        linearRgb = float3(
            HlgInverseOetf(rgbSignal.r),
            HlgInverseOetf(rgbSignal.g),
            HlgInverseOetf(rgbSignal.b));
        linearRgb *= 1000.0 / max(1.0, sdrWhiteNits);
    }

    linearRgb = BT2020ToRec709(linearRgb);
    float3 pipelineRgb = LinearToPipelineSrgb(linearRgb);

    OutputTexture[id.xy] = float4(pipelineRgb, 1.0);
}
