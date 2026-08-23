cbuffer Constants : register(b0)
{
    float mode;             // 0: PQ(ST2084)->Linear, 1: HLG->Linear, 2: Linear->PQ(ST2084), 3: Linear->HLG
    float targetNits;       // ディスプレイ輝度 (標準: 1000.0)
    float sdrNits;          // SDR 基準白輝度 (標準: 80.0 〜 100.0)
    float convertGamut;     // 1: BT.2020 <-> Rec.709 ガマット変換を行う
};

Texture2D<float4> InputTexture : register(t0);

SamplerState InputSampler : register(s0)
{
    Filter = MIN_MAG_MIP_LINEAR;
    AddressU = CLAMP;
    AddressV = CLAMP;
};

// SMPTE ST 2084 (PQ) 定数
static const float m1 = 0.1593017578125;
static const float m2 = 78.84375;
static const float c1 = 0.8359375;
static const float c2 = 18.8515625;
static const float c3 = 18.6875;

// PQ (ST 2084) -> リニア輝度 [0.0, 10000.0 nit] -> SDR 基準倍率
float3 PQToLinear(float3 pq, float sdrWhite)
{
    float3 N = max(pq, 0.0);
    float3 N_m2 = pow(N, 1.0 / m2);
    float3 num = max(N_m2 - c1, 0.0);
    float3 den = max(c2 - c3 * N_m2, 0.000001);
    float3 linearNits = pow(num / den, 1.0 / m1) * 10000.0;
    return linearNits / max(1.0, sdrWhite);
}

// リニア輝度 -> PQ (ST 2084)
float3 LinearToPQ(float3 linearCol, float sdrWhite)
{
    float3 nits = max(linearCol, 0.0) * max(1.0, sdrWhite);
    float3 Y = saturate(nits / 10000.0);
    float3 Y_m1 = pow(Y, m1);
    float3 num = c1 + c2 * Y_m1;
    float3 den = 1.0 + c3 * Y_m1;
    return pow(num / den, m2);
}

// HLG (ARIB STD-B67) -> リニア輝度
float3 HLGToLinear(float3 hlg, float sdrWhite)
{
    static const float a = 0.17883277;
    static const float b = 0.28466892;
    static const float c = 0.55991073;

    float3 e = saturate(hlg);
    float3 low = (e * e) / 3.0;
    float3 high = (exp((e - c) / a) + b) / 12.0;
    float3 linearCol = (e <= 0.5) ? low : high;
    return linearCol * (1000.0 / max(1.0, sdrWhite));
}

// BT.2020 -> Rec.709 ガマット変換行列
float3 BT2020ToRec709(float3 c)
{
    float3x3 m = float3x3(
         1.6604910, -0.5876411, -0.0728499,
        -0.1245505,  1.1328999, -0.0083494,
        -0.0181508, -0.1005789,  1.1187297
    );
    return mul(m, c);
}

// Rec.709 -> BT.2020 ガマット変換行列

// Linear RGB -> the exact inverse of the bridge shader's sRGB transfer.
// Values above 1.0 are intentionally allowed for HDR; this is a mathematical
// encoding used only so the later global sRGB->Linear pass recovers the value.
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
        SrgbEncodeUnclamped(c.b)
    );
}

float3 Rec709ToBT2020(float3 c)
{
    float3x3 m = float3x3(
        0.6274040, 0.3292820, 0.0433136,
        0.0690970, 0.9195400, 0.0113612,
        0.0163916, 0.0880132, 0.8955952
    );
    return mul(m, c);
}

float4 main(
    float4 pos : SV_POSITION,
    float4 posScene : SCENE_POSITION,
    float4 uv0 : TEXCOORD0
) : SV_Target
{
    float4 src = InputTexture.Sample(InputSampler, uv0.xy);
    float3 rgb = src.rgb;
    float a = src.a;

    if (mode < 0.5)
    {
        // PQ (ST 2084) -> Linear Float (HDR 展開)
        rgb = PQToLinear(rgb, sdrNits);
        if (convertGamut > 0.5)
        {
            rgb = BT2020ToRec709(rgb);
        }
    }
    else if (mode < 1.5)
    {
        // HLG -> Linear Float (HDR 展開)
        rgb = HLGToLinear(rgb, sdrNits);
        if (convertGamut > 0.5)
        {
            rgb = BT2020ToRec709(rgb);
        }
    }
    else if (mode < 2.5)
    {
        // Linear Float -> PQ (ST 2084) エンコード用
        if (convertGamut > 0.5)
        {
            rgb = Rec709ToBT2020(rgb);
        }
        rgb = LinearToPQ(rgb, sdrNits);
    }
    else if (mode < 4.5)
    {
        // HDR10 PQ -> Linear -> Rec.709, then pre-encode for the YMM4
        // global sRGB->Linear bridge. Because SdrNits is set to
        // 80 * current SDR scale, the following global scale cancels out.
        rgb = PQToLinear(rgb, sdrNits);
        if (convertGamut > 0.5)
        {
            rgb = BT2020ToRec709(rgb);
        }
        rgb = LinearToPipelineSrgb(rgb);
    }
    else
    {
        // HLG -> Linear -> Rec.709 -> pipeline-sRGB.
        rgb = HLGToLinear(rgb, sdrNits);
        if (convertGamut > 0.5)
        {
            rgb = BT2020ToRec709(rgb);
        }
        rgb = LinearToPipelineSrgb(rgb);
    }

    return float4(rgb, a);
}
