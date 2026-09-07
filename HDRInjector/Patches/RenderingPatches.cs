using System;
using System.Numerics;
using System.Reflection;
using HarmonyLib;
using HDRInjector.Models;
using HDRInjector.Settings;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Brush;

namespace HDRInjector.Patches;

/// <summary>
/// YMM4 の Direct2D レンダリング処理に対して 32bit Float / HDR カラーを注入する Harmony パッチ集。
/// </summary>
public static class RenderingPatches
{
    private static readonly FieldInfo? _brushField =
        typeof(SolidColorBrushSource).GetField("brush", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? _paramField =
        typeof(SolidColorBrushSource).GetField("solidColorBrushParameter", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    /// SolidColorBrushSource.Update() に対する Postfix パッチ。
    /// 単色ブラシの描画色に 1.0f を超える 32bit Float / HDR カラーを適用します。
    /// </summary>
    [HarmonyPatch(typeof(SolidColorBrushSource), "Update")]
    public static class SolidColorBrushSourcePatch
    {
        private static void Postfix(SolidColorBrushSource __instance, TimelineItemSourceDescription desc)
        {
            if (!HdrInjectorSettings.Default.EnableHdr) return;
            try
            {
                if (_paramField?.GetValue(__instance) is SolidColorBrushParameter param &&
                    _brushField?.GetValue(__instance) is ID2D1SolidColorBrush brush)
                {
                    // HDR情報が明示的に紐付いている場合だけ上書きする。
                    // HDR情報がない通常カラーは、YMM4本来の色変換を絶対に壊さない。
                    if (!HdrColorRegistry.TryGetHdrColor(param, out var hdr)) return;

                    var (r, g, b) = hdr.GetEffectiveHdrRgb();
                    brush.Color = new Color4(r, g, b, hdr.A);
                }
            }
            catch
            {
                // 無視
            }
        }
    }

    /// <summary>
    /// MonocolorizationEffect (単色化エフェクト) の Update に対する Postfix パッチ。
    /// </summary>
    [HarmonyPatch(typeof(YukkuriMovieMaker.Player.Video.Effects.MonocolorizationEffect), "Update")]
    public static class MonocolorizationEffectPatch
    {
        private static readonly FieldInfo? _itemField =
            typeof(YukkuriMovieMaker.Player.Video.Effects.MonocolorizationEffect).GetField("item", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo? _solidColorizeField =
            typeof(YukkuriMovieMaker.Player.Video.Effects.MonocolorizationEffect).GetField("solidColorize", BindingFlags.Instance | BindingFlags.NonPublic);

        private static void Postfix(YukkuriMovieMaker.Player.Video.Effects.MonocolorizationEffect __instance, EffectDescription effectDescription)
        {
            if (!HdrInjectorSettings.Default.EnableHdr) return;
            try
            {
                if (_itemField?.GetValue(__instance) is YukkuriMovieMaker.Project.Effects.MonocolorizationEffect item &&
                    _solidColorizeField?.GetValue(__instance) is object solidColorize)
                {
                    var prop = solidColorize.GetType().GetProperty("Color");
                    if (prop != null)
                    {
                        if (!HdrColorRegistry.TryGetHdrColor(item, out var hdr)) return;
                        var (r, g, b) = hdr.GetEffectiveHdrRgb();
                        prop.SetValue(solidColorize, new Vector4(r * hdr.A, g * hdr.A, b * hdr.A, hdr.A));
                    }
                }
            }
            catch
            {
                // 無視
            }
        }
    }
}
