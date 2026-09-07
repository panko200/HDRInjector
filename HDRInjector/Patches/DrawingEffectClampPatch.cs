using System;
using System.Reflection;
using HarmonyLib;
using SharpGen.Runtime;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video.Effects;
using HDRInjector.Settings;

namespace HDRInjector.Patches;

/// <summary>
/// YMM4 の DrawingEffect が持つ D2D 組み込みエフェクトの ClampOutput を false に設定するパッチ。
/// ClampOutput は BOOL 型プロパティのため、PropertyType.Bool で直接設定する。
/// 対象のエフェクトが ClampOutput をサポートしていない場合は静かにスキップする。
/// </summary>
internal static class DrawingEffectClampPatch
{
    public static void Apply(Harmony harmony)
    {
        try
        {
            var createEffectMethod = typeof(DrawingEffect).GetMethod(
                "CreateEffect",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null,
                new[] { typeof(IGraphicsDevicesAndContext) },
                null
            );

            if (createEffectMethod == null)
            {
                System.Diagnostics.Debug.WriteLine("[HDRInjector][DrawClamp] DrawingEffect.CreateEffect not found.");
                return;
            }

            var postfix = typeof(DrawingEffectClampPatch).GetMethod(
                nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic);

            harmony.Patch(createEffectMethod, postfix: new HarmonyMethod(postfix));
            System.Diagnostics.Debug.WriteLine("[HDRInjector][DrawClamp] Patch installed on DrawingEffect.CreateEffect.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HDRInjector][DrawClamp] Apply error: {ex}");
        }
    }

    private static void Postfix(DrawingEffect __instance)
    {
        if (!HdrInjectorSettings.Default.EnableHdr) return;
        try
        {
            var type = __instance.GetType();
            TrySetClampOutput(type, __instance, "zoomEffect", "AffineTransform2D");
            TrySetClampOutput(type, __instance, "cropEffect", "Crop");
            TrySetClampOutput(type, __instance, "renderEffect", "Transform3D");
            TrySetClampOutput(type, __instance, "opacityEffect", "ColorMatrix");
        }
        catch
        {
            // DrawingEffect の内部構造が予期と異なる場合は無視
        }
    }

    private static void TrySetClampOutput(Type type, object instance, string fieldName, string effectName)
    {
        try
        {
            var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field == null) return;

            var effect = field.GetValue(instance);
            if (effect == null) return;

            // Method 1: Try managed ClampOutput property
            var clampProp = effect.GetType().GetProperty("ClampOutput", BindingFlags.Instance | BindingFlags.Public);
            if (clampProp != null && clampProp.CanWrite)
            {
                clampProp.SetValue(effect, false);
                System.Diagnostics.Debug.WriteLine($"[HDRInjector][DrawClamp] {effectName} ClampOutput=false (property).");
                return;
            }

            // Method 2: Use raw COM via ID2D1Properties.SetValue(int, PropertyType.Bool, void*, int)
            if (effect is ID2D1Properties props)
            {
                int id = GetClampOutputPropertyId(effectName);
                if (id >= 0)
                {
                    unsafe
                    {
                        int falseVal = 0;
                        props.SetValue(id, PropertyType.Bool, &falseVal, sizeof(int));
                    }
                    System.Diagnostics.Debug.WriteLine($"[HDRInjector][DrawClamp] {effectName} ClampOutput=false (raw COM, index={id}).");
                    return;
                }
            }

            // ClampOutput が存在しないエフェクトは正常（スキップ）
        }
        catch
        {
            // このエフェクトは ClampOutput をサポートしていない
        }
    }

    private static int GetClampOutputPropertyId(string effectName)
    {
        return effectName switch
        {
            // これらのエフェクトは ClampOutput を持たない場合がある
            "AffineTransform2D" => 5,
            "Crop" => 2,
            "Transform3D" => 3,
            "ColorMatrix" => 4,
            _ => -1
        };
    }
}
