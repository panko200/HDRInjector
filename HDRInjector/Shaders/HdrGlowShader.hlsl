cbuffer Constants : register(b0)
{
    float4 innerColor;      // 16 bytes: インナーティント (RGBA)
    float4 outerColor;      // 16 bytes: アウターティント (RGBA)

    float exposure;         // 4 bytes: 露出ブースト
    float threshold;        // 4 bytes: 発光閾値
    float contrast;         // 4 bytes: コントラスト
    float sourceOpacity;    // 4 bytes: 原画の不透明度 -> 16 bytes

    float colorize;         // 4 bytes: 1.0 = 着色有効
    float linearColor;      // 4 bytes: 1.0 = リニア色空間演算
    float tintScale;        // 4 bytes: ティントスケール
    float tintGamma;        // 4 bytes: ティントガンマ -> 16 bytes

    float chromaR;          // 4 bytes: 色収差 R
    float chromaG;          // 4 bytes: 色収差 G
    float chromaB;          // 4 bytes: 色収差 B
    float rayLength;        // 4 bytes: 放射光の長さ -> 16 bytes

    float rayCenterX;       // 4 bytes: 放射光中心 X
    float rayCenterY;       // 4 bytes: 放射光中心 Y
    float raySamples;       // 4 bytes: 放射光サンプル数
    float texWidth;         // 4 bytes: テクスチャ幅 -> 16 bytes

    float texHeight;        // 4 bytes: テクスチャ高さ
    float rayFalloff;       // 4 bytes: 放射光減衰
    float rayStyle;         // 4 bytes: 0=Radial, 1=Directional
    float rayAngle;         // 4 bytes: 放射光角度 -> 16 bytes

    float chromaStyle;      // 4 bytes: 0=Radial, 1=Directional
    float chromaAngle;      // 4 bytes: 色収差角度
    float chromaCenterX;    // 4 bytes: 色収差中心 X
    float chromaCenterY;    // 4 bytes: 色収差中心 Y -> 16 bytes

    float falloffMode;      // 4 bytes: 0=Gaussian, 1=InverseSquare, 2=Exponential
    float falloffPower;     // 4 bytes: 減衰強度
    float ditherStrength;   // 4 bytes: ディザリング強度
    float clampAlpha;       // 4 bytes: 黒ずみ軽減 -> 16 bytes
};

Texture2D<float4> GlowTexture : register(t0);
Texture2D<float4> SourceTexture : register(t1);

SamplerState InputSampler : register(s0)
{
    Filter = MIN_MAG_MIP_LINEAR;
    AddressU = BORDER;
    AddressV = BORDER;
    BorderColor = float4(0, 0, 0, 0);
};

// 相対輝度算出 (Rec.709)
float GetLuma(float3 c)
{
    return dot(c, float3(0.2126, 0.7152, 0.0722));
}

float2 PixelToUVOffset(float2 pixelOffset, float2 duvdx, float2 duvdy)
{
    return pixelOffset.x * duvdx + pixelOffset.y * duvdy;
}

// 高品質インターリーブド・グラデーション・ノイズ (IGN)
float InterleavedGradientNoise(float2 pixelPos)
{
    float3 magic = float3(0.06711056, 0.00583715, 52.9829189);
    return frac(magic.z * frac(dot(pixelPos, magic.xy)));
}

// 三角確率密度関数 (TPDF) ディザノイズ [-1/255, +1/255]
float GetTPDFDither(float2 pixelPos)
{
    float n1 = InterleavedGradientNoise(pixelPos);
    float n2 = InterleavedGradientNoise(pixelPos + float2(47.0, 19.0));
    return (n1 + n2 - 1.0) / 255.0;
}

float4 main(
    float4 pos : SV_POSITION,
    float4 posScene : SCENE_POSITION,
    float4 uv0 : TEXCOORD0,
    float4 uv1 : TEXCOORD1
) : SV_Target
{
    float2 rayCenterPixel = float2(rayCenterX, rayCenterY);
    float2 chromaCenterPixel = float2(chromaCenterX, chromaCenterY);

    float2 duvdx = ddx(uv0.xy);
    float2 duvdy = ddy(uv0.xy);

    float safeRayLength = min(max(rayLength, 0.0), 0.99);
    float maxDim = max(texWidth, texHeight);

    float4 sumGlow = float4(0, 0, 0, 0);
    int samples = max(1, (int)raySamples);
    float totalWeight = 0.0;

    // 1. 放射光 (Light Rays) ＆ 色収差 (Chromatic Aberration) の多重サンプリング
    for (int i = 0; i < samples; i++)
    {
        float2 offsetPixel;
        float weight;

        if (rayStyle > 0.5)
        {
            // Directional
            float ratio = (float)i / max(1.0, (float)(samples - 1));
            float offset = (ratio * 2.0 - 1.0);
            float pixelDist = maxDim * safeRayLength * offset * 0.5;
            offsetPixel = float2(cos(rayAngle), sin(rayAngle)) * pixelDist;
            weight = pow(max(1.0 - abs(offset), 0.0), rayFalloff);
        }
        else
        {
            // Radial
            float ratio = (float)i / max(1.0, (float)samples);
            float2 dirPixel = posScene.xy - rayCenterPixel;
            offsetPixel = dirPixel * (safeRayLength * ratio);
            weight = pow(max(1.0 - ratio, 0.0), rayFalloff);
        }

        float2 currentRayOffsetUV = PixelToUVOffset(offsetPixel, duvdx, duvdy);
        float2 baseUV = uv0.xy - currentRayOffsetUV;

        // 色収差のオフセット計算
        float2 chromaOffsetR, chromaOffsetG, chromaOffsetB;
        if (chromaStyle > 0.5)
        {
            // Directional
            float chromaDist = maxDim * 0.5;
            float2 cDirPixel = float2(cos(chromaAngle), sin(chromaAngle)) * chromaDist;
            float2 cDirUV = PixelToUVOffset(cDirPixel, duvdx, duvdy);
            chromaOffsetR = cDirUV * chromaR;
            chromaOffsetG = cDirUV * chromaG;
            chromaOffsetB = cDirUV * chromaB;
        }
        else
        {
            // Radial
            float2 currentPixelPos = posScene.xy - offsetPixel;
            float2 cDirPixel = currentPixelPos - chromaCenterPixel;
            float2 cDirUV = PixelToUVOffset(cDirPixel, duvdx, duvdy);
            chromaOffsetR = cDirUV * chromaR;
            chromaOffsetG = cDirUV * chromaG;
            chromaOffsetB = cDirUV * chromaB;
        }

        float2 uvR = baseUV - chromaOffsetR;
        float2 uvG = baseUV - chromaOffsetG;
        float2 uvB = baseUV - chromaOffsetB;

        float r = GlowTexture.Sample(InputSampler, uvR).r;
        float g = GlowTexture.Sample(InputSampler, uvG).g;
        float b = GlowTexture.Sample(InputSampler, uvB).b;
        float a = GlowTexture.Sample(InputSampler, baseUV).a;
        a = max(a, max(r, max(g, b)));

        sumGlow += float4(r, g, b, a) * weight;
        totalWeight += weight;
    }

    float4 glow = sumGlow / max(totalWeight, 0.0001);

    // 2. 減衰カーブ補正 (Falloff: 逆2乗型 / 指数型 / ガウス型)
    if (falloffMode > 0.5 && falloffMode < 1.5)
    {
        // 逆2乗型 (Inverse Square Falloff: 物理法則に基づく自然な減衰)
        float p = max(0.1, falloffPower);
        // HDR: 1.0超えの輝度を保持したまま、強い光だけを穏やかに減衰させる。
        glow.rgb = glow.rgb / (1.0 + max(glow.rgb, 0.0) * p);
    }
    else if (falloffMode > 1.5)
    {
        // 指数型 (Exponential Falloff)
        float p = max(0.1, falloffPower);
        glow.rgb = pow(max(glow.rgb, 0.0), p);
    }

    // 3. カラーティント
    // このシェーダーはHDR scRGBのLinear RGBを直接処理する。
    // 入力はすでにLinearなので、ここでsRGB→Linearを再適用しない。
    float3 mixInner = pow(max(innerColor.rgb, 0.0), 2.2);
    float3 mixOuter = pow(max(outerColor.rgb, 0.0), 2.2);

    float luma = GetLuma(glow.rgb);
    luma *= tintScale;
    float t_color = saturate(pow(max(luma, 0.0001), tintGamma));

    float3 finalGlowRGB;
    float finalAlpha;

    if (colorize > 0.5)
    {
        // HDR値を0～1へclampしない。1.0超えをそのまま保持する。
        finalGlowRGB = lerp(mixOuter, mixInner, t_color) * (luma * 2.0);
        finalAlpha = saturate(glow.a * tintScale);
    }
    else
    {
        finalGlowRGB = glow.rgb * mixOuter;
        finalAlpha = glow.a * outerColor.a;
    }

    // 露出ブースト
    finalGlowRGB *= pow(2.0, exposure);

    if (clampAlpha > 0.5)
    {
        float maxGlowRGB = max(finalGlowRGB.r, max(finalGlowRGB.g, finalGlowRGB.b));
        finalAlpha = min(finalAlpha, maxGlowRGB);
    }

    // 4. 原画サンプリング ＆ 加算合成
    float4 source = SourceTexture.Sample(InputSampler, uv1.xy);
    source.a *= sourceOpacity;
    source.rgb *= sourceOpacity;

    // SourceTextureもHDR scRGB Linear RGB。二重の色空間変換は行わない。
    float3 finalRGB = source.rgb + finalGlowRGB;
    float finalA = saturate(source.a + finalAlpha);

    // 5. HDR scRGB / FP16 出力では8bit量子化用ディザは不要。
    // 1.0を超えるHDR値や極小の暗部値を保持するため、ディザリングは無効化する。

    // RGBはHDRの1.0超えを保持する。Alphaのみ0～1。
    return float4(max(finalRGB, 0.0), finalA);
}
