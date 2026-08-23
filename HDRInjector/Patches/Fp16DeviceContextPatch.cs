using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SharpGen.Runtime;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using YukkuriMovieMaker.Commons;

namespace HDRInjector.Patches;

/// <summary>
/// GraphicsDevicesAndContext のデバイスコンテキストを高精度形式に差し替えるパッチ。
/// デフォルトでは B8G8R8A8_UNorm (8bit) で作成されるため、HDR 値 (>1.0) が中間バッファでクランプされる。
/// FP16/FP32 コンテキストに差し替えることで、エフェクトチェーン全体が高精度で処理される。
/// </summary>
internal static class Fp16DeviceContextPatch
{
    private static readonly FieldInfo? DeviceContextField =
        typeof(GraphicsDevicesAndContext)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(f => f.FieldType == typeof(ID2D1DeviceContext6));

    // チャイネル: 試行するピクセル形式（高精度→低精度の順）
    private static readonly Format[] FallbackFormats = new[]
    {
        Format.R16G16B16A16_Float,  // 最も高精度 (16bit float)
        Format.R32G32B32A32_Float,  // 32bit float (広い範囲)
        Format.R16G16B16A16_UNorm,  // 16bit UNorm
        Format.R10G10B10A2_UNorm,   // 10bit (HDR10 と互換)
    };

    public static void Apply(Harmony harmony)
    {
        try
        {
            if (DeviceContextField == null)
            {
                System.Diagnostics.Debug.WriteLine("[HDRInjector][Fp16DC] DeviceContext backing field not found by type. FP16 patch disabled.");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[HDRInjector][Fp16DC] Found backing field: {DeviceContextField.Name} ({DeviceContextField.FieldType.Name})");

            var ctor = typeof(GraphicsDevicesAndContext).GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(IGraphicsDevices) },
                null
            );
            if (ctor == null)
            {
                System.Diagnostics.Debug.WriteLine("[HDRInjector][Fp16DC] Constructor not found.");
                return;
            }

            var postfix = typeof(Fp16DeviceContextPatch).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic);
            harmony.Patch(ctor, postfix: new HarmonyMethod(postfix));
            System.Diagnostics.Debug.WriteLine("[HDRInjector][Fp16DC] Patch installed.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HDRInjector][Fp16DC] Apply error: {ex}");
        }
    }

    private static void Postfix(GraphicsDevicesAndContext __instance)
    {
        try
        {
            if (DeviceContextField == null) return;

            var originalDC = __instance.DeviceContext;
            if (originalDC == null) return;

            var d3dDevice = __instance.D3D.Device;
            if (d3dDevice == null)
            {
                System.Diagnostics.Debug.WriteLine("[HDRInjector][Fp16DC] D3D Device is null, skipping.");
                return;
            }

            var d2dFactory = __instance.D2D.Factory;

            // 複数のフォーマットを試行
            foreach (var format in FallbackFormats)
            {
                try
                {
                    var textureDesc = new Texture2DDescription
                    {
                        Width = 1,
                        Height = 1,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = format,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                        CPUAccessFlags = CpuAccessFlags.None,
                        MiscFlags = ResourceOptionFlags.None,
                    };

                    using var texture = d3dDevice.CreateTexture2D(textureDesc);
                    using var surface = texture.QueryInterface<IDXGISurface>();

                    var rtProps = new RenderTargetProperties();
                    using var renderTarget = d2dFactory.CreateDxgiSurfaceRenderTarget(surface, rtProps);
                    if (renderTarget == null) continue;

                    var fp16DC = renderTarget.QueryInterface<ID2D1DeviceContext6>();
                    if (fp16DC == null) continue;

                    // 成功: バッキングフィールドを書き換え
                    DeviceContextField.SetValue(__instance, fp16DC);
                    originalDC.Dispose();

                    System.Diagnostics.Debug.WriteLine($"[HDRInjector][Fp16DC] Device context replaced with {format}.");
                    return;
                }
                catch (Exception)
                {
                    // このフォーマットは非対応、次のフォーマットを試行
                    continue;
                }
            }

            // すべてのフォーマットが失敗
            System.Diagnostics.Debug.WriteLine("[HDRInjector][Fp16DC] All fallback formats failed. Using default UNorm context.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HDRInjector][Fp16DC] Postfix error: {ex.Message}");
        }
    }
}
