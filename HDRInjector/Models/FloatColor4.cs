using System;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Color = System.Windows.Media.Color;
using Vortice.Mathematics;

namespace HDRInjector.Models;

/// <summary>
/// 32bit Float (単精度浮動小数点) による HDR カラー表現構造体。
/// 各チャンネル (R, G, B, A) は 0.0f〜1.0f の SDR 範囲だけでなく、
/// 1.0f を超える高輝度（HDR）や負の値、露出 (Exposure / EV) 値を保持できます。
/// </summary>
public struct FloatColor4 : IEquatable<FloatColor4>
{
    public float R;
    public float G;
    public float B;
    public float A;
    public float Exposure; // EV値 (例: 0.0=1x, +1.0=2x, +2.0=4x, +3.0=8x, -1.0=0.5x)

    public FloatColor4(float r, float g, float b, float a = 1.0f, float exposure = 0.0f)
    {
        R = r;
        G = g;
        B = b;
        A = a;
        Exposure = exposure;
    }

    public static FloatColor4 FromColor(Color color, float exposure = 0.0f)
    {
        float multiplier = MathF.Pow(2.0f, exposure);
        return new FloatColor4(
            (color.R / 255.0f) * multiplier,
            (color.G / 255.0f) * multiplier,
            (color.B / 255.0f) * multiplier,
            color.A / 255.0f,
            exposure
        );
    }

    public static FloatColor4 FromBaseColorAndExposure(Color baseColor, float exposure)
    {
        return new FloatColor4(
            baseColor.R / 255.0f,
            baseColor.G / 255.0f,
            baseColor.B / 255.0f,
            baseColor.A / 255.0f,
            exposure
        );
    }

    /// <summary>
    /// 実効的な HDR RGB 値（ベースRGB × 2^Exposure）を取得します。
    /// </summary>
    public (float r, float g, float b) GetEffectiveHdrRgb()
    {
        float mult = MathF.Pow(2.0f, Exposure);
        return (R * mult, G * mult, B * mult);
    }

    /// <summary>
    /// 指定した露出オフセット (EV) を適用した SDR クランプ Color に変換します。
    /// </summary>
    public Color ToColorWithExposureOffset(float exposureOffset)
    {
        float mult = MathF.Pow(2.0f, Exposure + exposureOffset);
        byte r = (byte)Math.Clamp((int)MathF.Round(R * mult * 255.0f), 0, 255);
        byte g = (byte)Math.Clamp((int)MathF.Round(G * mult * 255.0f), 0, 255);
        byte b = (byte)Math.Clamp((int)MathF.Round(B * mult * 255.0f), 0, 255);
        byte a = (byte)Math.Clamp((int)MathF.Round(A * 255.0f), 0, 255);
        return Color.FromArgb(a, r, g, b);
    }

    /// <summary>
    /// SDR (0〜255) の System.Windows.Media.Color に変換（クランプ）します。
    /// </summary>
    public Color ToColor()
    {
        return ToColorWithExposureOffset(0.0f);
    }

    /// <summary>
    /// Direct2D 用の Color4 に変換します（実効 HDR 輝度を反映）。
    /// </summary>
    public Color4 ToColor4()
    {
        var (r, g, b) = GetEffectiveHdrRgb();
        return new Color4(r, g, b, A);
    }

    /// <summary>
    /// System.Numerics.Vector4 に変換します（実効 HDR 輝度を反映）。
    /// </summary>
    public Vector4 ToVector4()
    {
        var (r, g, b) = GetEffectiveHdrRgb();
        return new Vector4(r, g, b, A);
    }

    /// <summary>
    /// 相対輝度 (Rec.709 Luminance) を算出します。
    /// </summary>
    public float Luminance
    {
        get
        {
            var (r, g, b) = GetEffectiveHdrRgb();
            return 0.2126f * r + 0.7152f * g + 0.0722f * b;
        }
    }

    /// <summary>
    /// HSV + Exposure から FloatColor4 を作成します。
    /// </summary>
    /// <param name="h">色相 (0.0〜360.0)</param>
    /// <param name="s">彩度 (0.0〜1.0)</param>
    /// <param name="v">明度 (0.0〜1.0)</param>
    /// <param name="a">不透明度 (0.0〜1.0)</param>
    /// <param name="exposure">露出 (EV)</param>
    public static FloatColor4 FromHsv(float h, float s, float v, float a = 1.0f, float exposure = 0.0f)
    {
        h = (h % 360.0f + 360.0f) % 360.0f;
        s = Math.Clamp(s, 0.0f, 1.0f);
        v = Math.Clamp(v, 0.0f, 1.0f);

        float c = v * s;
        float x = c * (1.0f - MathF.Abs((h / 60.0f) % 2.0f - 1.0f));
        float m = v - c;

        float r1 = 0, g1 = 0, b1 = 0;
        int sextant = (int)(h / 60.0f) % 6;
        switch (sextant)
        {
            case 0: r1 = c; g1 = x; b1 = 0; break;
            case 1: r1 = x; g1 = c; b1 = 0; break;
            case 2: r1 = 0; g1 = c; b1 = x; break;
            case 3: r1 = 0; g1 = x; b1 = c; break;
            case 4: r1 = x; g1 = 0; b1 = c; break;
            case 5: r1 = c; g1 = 0; b1 = x; break;
        }

        return new FloatColor4(r1 + m, g1 + m, b1 + m, a, exposure);
    }

    /// <summary>
    /// 現在の FloatColor4 を HSV に変換します。
    /// </summary>
    public void ToHsv(out float h, out float s, out float v, out float a, out float exposure)
    {
        float r = Math.Clamp(R, 0.0f, 1.0f);
        float g = Math.Clamp(G, 0.0f, 1.0f);
        float b = Math.Clamp(B, 0.0f, 1.0f);

        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        float delta = max - min;

        v = max;
        s = max <= 0.00001f ? 0.0f : delta / max;

        if (delta <= 0.00001f)
        {
            h = 0.0f;
        }
        else if (r >= max)
        {
            h = 60.0f * (((g - b) / delta) % 6.0f);
        }
        else if (g >= max)
        {
            h = 60.0f * (((b - r) / delta) + 2.0f);
        }
        else
        {
            h = 60.0f * (((r - g) / delta) + 4.0f);
        }

        if (h < 0.0f) h += 360.0f;
        a = A;
        exposure = Exposure;
    }

    public override string ToString()
    {
        var (er, eg, eb) = GetEffectiveHdrRgb();
        if (MathF.Abs(Exposure) > 0.001f)
        {
            return $"hdr({er:F3}, {eg:F3}, {eb:F3}, {A:F3} | EV:{Exposure:+0.0;-0.0;+0.0})";
        }
        return $"float4({er:F3}, {eg:F3}, {eb:F3}, {A:F3})";
    }

    /// <summary>
    /// 文字列（Hex、float4、hdr 等）からパースします。
    /// </summary>
    public static bool TryParse(string? text, out FloatColor4 result)
    {
        result = new FloatColor4(1, 1, 1, 1, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;

        text = text.Trim();

        // 1. hdr(r, g, b, a) または rgba(r, g, b, a)
        var match = Regex.Match(text, @"(?:hdr|rgba|float4)\s*\(\s*([0-9.-]+)\s*,\s*([0-9.-]+)\s*,\s*([0-9.-]+)(?:\s*,\s*([0-9.-]+))?(?:\s*\|\s*EV:\s*([0-9.+-]+))?\s*\)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            if (float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float r) &&
                float.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float g) &&
                float.TryParse(match.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float b))
            {
                float a = 1.0f;
                if (match.Groups[4].Success)
                {
                    float.TryParse(match.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out a);
                }
                float ev = 0.0f;
                if (match.Groups[5].Success)
                {
                    float.TryParse(match.Groups[5].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out ev);
                }
                result = new FloatColor4(r, g, b, a, ev);
                return true;
            }
        }

        // 2. #RRGGBB (+2.0EV) 形式
        var hexEvMatch = Regex.Match(text, @"^#?([0-9a-fA-F]{6}|[0-9a-fA-F]{8})\s*(?:\(\s*([+-]?[0-9.]+)\s*EV\s*\))?$", RegexOptions.IgnoreCase);
        if (hexEvMatch.Success)
        {
            string hex = hexEvMatch.Groups[1].Value;
            byte r = Convert.ToByte(hex.Substring(0, 2), 16);
            byte g = Convert.ToByte(hex.Substring(2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(4, 2), 16);
            byte a = hex.Length == 8 ? Convert.ToByte(hex.Substring(6, 2), 16) : (byte)255;
            float ev = 0.0f;
            if (hexEvMatch.Groups[2].Success)
            {
                float.TryParse(hexEvMatch.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out ev);
            }
            result = FromBaseColorAndExposure(Color.FromArgb(a, r, g, b), ev);
            return true;
        }

        return false;
    }

    public bool Equals(FloatColor4 other)
    {
        return MathF.Abs(R - other.R) < 0.0001f &&
               MathF.Abs(G - other.G) < 0.0001f &&
               MathF.Abs(B - other.B) < 0.0001f &&
               MathF.Abs(A - other.A) < 0.0001f &&
               MathF.Abs(Exposure - other.Exposure) < 0.0001f;
    }

    public override bool Equals(object? obj) => obj is FloatColor4 other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(R, G, B, A, Exposure);

    public static bool operator ==(FloatColor4 left, FloatColor4 right) => left.Equals(right);
    public static bool operator !=(FloatColor4 left, FloatColor4 right) => !left.Equals(right);
}
