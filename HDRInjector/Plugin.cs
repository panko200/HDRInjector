using System;
using System.Reflection;
using HarmonyLib;
using YukkuriMovieMaker.Plugin;

namespace HDRInjector;

/// <summary>
/// HDRInjector プラグインのエントリポイント。
/// YMM4 起動時に Harmony パッチを適用し、32bit Float / HDR カラーシステムを有効化します。
/// </summary>
public class HdrColorStudioPlugin : IPlugin
{
    public const string HarmonyId = "com.panko200.HDRInjector";

    public string Name => "HDRInjector";

    static HdrColorStudioPlugin()
    {
        try
        {
            var harmony = new Harmony(HarmonyId);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            Patches.Fp16DeviceContextPatch.Apply(harmony);
            Patches.HdrExportRenderTargetPatch.Apply(harmony);
            Patches.DrawingEffectClampPatch.Apply(harmony);
            Patches.HdrVideoPluginPatch.Apply(harmony);
            Patches.HdrPreviewPatch.Apply(harmony);
            Patches.HdrPlayerDrawPatch.Apply(harmony);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HDRInjector] Harmony Patch Error: {ex}");
        }
    }
}
