using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.FileSource;
using HDRInjector.FileSource;

namespace HDRInjector.Patches;

/// <summary>
/// VideoFileSourceFactory.Create に HDR 動画検出パッチを適用する。
/// 標準プラグインが動画を読み込んでも、FFprobe で HDR メタデータを検出し、
/// HDR動画の場合は HdrVideoFileSource に置き換える。
/// </summary>
internal static class HdrVideoPluginPatch
{
    public static void Apply(Harmony harmony)
    {
        try
        {
            var createMethod = typeof(VideoFileSourceFactory).GetMethod(
                "Create",
                BindingFlags.Static | BindingFlags.Public,
                null,
                new[] { typeof(IGraphicsDevices), typeof(string) },
                null
            );

            if (createMethod == null)
            {
                Debug.WriteLine("[HDRInjector][HdrPlugin] VideoFileSourceFactory.Create not found.");
                return;
            }

            var postfix = typeof(HdrVideoPluginPatch).GetMethod(
                nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic);

            harmony.Patch(createMethod, postfix: new HarmonyMethod(postfix));
            Debug.WriteLine("[HDRInjector][HdrPlugin] Patch installed on VideoFileSourceFactory.Create.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HDRInjector][HdrPlugin] Apply error: {ex}");
        }
    }

    /// <summary>
    /// VideoFileSourceFactory.Create の postfix。
    /// 常に FFprobe で HDR メタデータを検出し、HDR動画なら標準プラグインの結果を置き換える。
    /// </summary>
    private static void Postfix(ref IVideoFileSource? __result, IGraphicsDevices devices, string filePath)
    {
        try
        {
            // HDR メタデータを検出
            var hdrInfo = HdrVideoFileSourcePlugin.DetectHdrMetadata(filePath);

            if (hdrInfo == null)
            {
                // SDR 動画
                Debug.WriteLine($"[HDRInjector][HdrPlugin] SDR video (no HDR metadata): {System.IO.Path.GetFileName(filePath)}");
                return;
            }

            Debug.WriteLine($"[HDRInjector][HdrPlugin] *** HDR video detected ***: {System.IO.Path.GetFileName(filePath)}" +
                            $" | Transfer={hdrInfo.Value.TransferFunction}, ColorSpace={hdrInfo.Value.ColorSpace}");

            // 標準プラグインの結果を破棄
            if (__result != null)
            {
                Debug.WriteLine($"[HDRInjector][HdrPlugin] Disposing standard SDR source, replacing with HDR source");
                try { __result.Dispose(); } catch { }
                __result = null;
            }

            // HDR 動画ソースを作成
            var context = devices.CreateContext();
            try
            {
                var hdrSource = new HdrVideoFileSource(context, filePath, hdrInfo.Value, ownsDevicesContext: true);
                __result = hdrSource;
                Debug.WriteLine($"[HDRInjector][HdrPlugin] HDR video source created: {System.IO.Path.GetFileName(filePath)}" +
                                $" | Mode={hdrInfo.Value.TransferFunction} | ownsContext=true");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HDRInjector][HdrPlugin] HDR source creation failed: {ex.Message}");
                context.Dispose();
                // 作成失敗時は __result が null のままになる（動画なし）
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HDRInjector][HdrPlugin] Postfix error: {ex.Message}");
        }
    }
}
