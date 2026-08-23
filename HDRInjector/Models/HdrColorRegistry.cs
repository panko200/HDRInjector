using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace HDRInjector.Models;

/// <summary>
/// YMM4 の各オブジェクト（ColorPicker、ブラシ、エフェクト、設定等）に
/// 32bit Float / HDR カラー情報を関連付けて保持・管理するレジストリ。
/// </summary>
public static class HdrColorRegistry
{
    // オブジェクト参照ごとの HDR カラー保持
    private static readonly ConditionalWeakTable<object, HdrColorBox> _objectColors = new();

    // Color (8bit) のハッシュ/値に基づく最新の HDR カラーキャッシュ（フォールバック用）
    private static readonly ConcurrentDictionary<int, FloatColor4> _colorValueCache = new();

    // 最後に操作・選択された HDR カラー（セッション共通）
    public static FloatColor4 CurrentHdrColor { get; set; } = new FloatColor4(1.0f, 1.0f, 1.0f, 1.0f, 0.0f);

    public class HdrColorBox
    {
        public FloatColor4 Color;
        public HdrColorBox(FloatColor4 color) => Color = color;
    }

    /// <summary>
    /// 指定したオブジェクトに 32bit Float / HDR カラーを設定します。
    /// </summary>
    public static void SetHdrColor(object target, FloatColor4 hdrColor)
    {
        if (target == null) return;
        var box = _objectColors.GetOrCreateValue(target);
        box.Color = hdrColor;

        // Color (8bit) 値ベースのキャッシュにも記録
        Color sdr = hdrColor.ToColor();
        int key = (sdr.A << 24) | (sdr.R << 16) | (sdr.G << 8) | sdr.B;
        _colorValueCache[key] = hdrColor;

        CurrentHdrColor = hdrColor;
    }

    /// <summary>
    /// オブジェクトに関連付けられた 32bit Float / HDR カラーを取得します。
    /// 存在しない場合はフォールバックとして Color (8bit) から生成します。
    /// </summary>
    public static bool TryGetHdrColor(object? target, out FloatColor4 color)
    {
        if (target != null && _objectColors.TryGetValue(target, out var box))
        {
            color = box.Color;
            return true;
        }

        color = default;
        return false;
    }

    public static FloatColor4 GetHdrColor(object target, Color fallbackColor)
    {
        return TryGetHdrColor(target, out var color)
            ? color
            : FloatColor4.FromColor(fallbackColor, 0.0f);
    }

    /// <summary>
    /// 指定の 8bit Color から関連づいた HDR Color を取得します。
    /// </summary>
    public static FloatColor4 GetHdrColorFromSdr(Color color)
    {
        int key = (color.A << 24) | (color.R << 16) | (color.G << 8) | color.B;
        if (_colorValueCache.TryGetValue(key, out var cached))
        {
            return cached;
        }
        return FloatColor4.FromColor(color, 0.0f);
    }

    /// <summary>
    /// 定義済みの HDR 発光プリセット一覧を取得します。
    /// </summary>
    public static IReadOnlyList<(string Name, float Exposure, string Description)> Presets { get; } = new List<(string, float, string)>
    {
        ("SDR (標準)", 0.0f, "通常の標準輝度 (1.0x)"),
        ("ソフト発光", 1.0f, "ほのかな発光感 (+1.0 EV / 2.0x)"),
        ("ネオンサイン", 2.0f, "鮮やかなネオン管の光 (+2.0 EV / 4.0x)"),
        ("白熱電球", 3.0f, "明るい光源 (+3.0 EV / 8.0x)"),
        ("高出力レーザー", 4.0f, "強いレーザー・稲妻光 (+4.0 EV / 16.0x)"),
        ("超高輝度", 5.0f, "眩しいエネルギー (+5.0 EV / 32.0x)"),
        ("太陽光", 6.0f, "直射日光レベル (+6.0 EV / 64.0x)")
    };
}
