using System;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SharpGen.Runtime;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Project;
using HDRInjector.Effects;

namespace HDRInjector.Patches;

/// <summary>
/// YMM4の通常SDR描画をFP16中間ターゲットへ取り込み、
/// sRGB/Rec.709 -> Linear に変換して、FP16/scRGB SwapChainへ出力する。
/// Windows Advanced Color の一般用途推奨経路を使い、PQ/HDR10エンコードは行わない。
/// </summary>
public static class HdrPlayerDrawPatch
{
    private static readonly ConditionalWeakTable<object, HdrPlayerState> States = new();

    private sealed class HdrPlayerState : IDisposable
    {
        public ID2D1Bitmap1? HdrRenderTarget;
        public SrgbToLinearCustomEffect? SrgbToLinearEffect;
        public IGraphicsDevicesAndContext? Devices;
        public float LastSdrWhiteScale = float.NaN;
        public int Width;
        public int Height;

        public void Ensure(IGraphicsDevicesAndContext devices, int width, int height)
        {
            // A YMM4 device/context can be recreated after device loss or other GPU-resource
            // lifecycle events. Do not reuse D2D effects/bitmaps that belong to the old device.
            if (Devices != null && !ReferenceEquals(Devices, devices))
            {
                HdrRenderTarget?.Dispose();
                HdrRenderTarget = null;
                SrgbToLinearEffect?.Dispose();
                SrgbToLinearEffect = null;
                Width = 0;
                Height = 0;
                LastSdrWhiteScale = float.NaN;
            }

            Devices = devices;
            SrgbToLinearEffect ??= new SrgbToLinearCustomEffect(devices);
            if (HdrRenderTarget != null && Width == width && Height == height)
                return;

            HdrRenderTarget?.Dispose();
            HdrRenderTarget = null;

            Width = Math.Max(1, width);
            Height = Math.Max(1, height);

            var dc = devices.DeviceContext;
            var dpi = dc.Dpi;
            var prop = new BitmapProperties1(
                new PixelFormat(Format.R16G16B16A16_Float, Vortice.DCommon.AlphaMode.Premultiplied),
                dpi.Width,
                dpi.Height,
                BitmapOptions.Target
            );

            HdrRenderTarget = dc.CreateBitmap(
                new SizeI(Width, Height),
                IntPtr.Zero,
                Width * 8,
                prop
            );
        }

        public void Dispose()
        {
            HdrRenderTarget?.Dispose();
            HdrRenderTarget = null;
            SrgbToLinearEffect?.Dispose();
            SrgbToLinearEffect = null;
            Devices = null;
            LastSdrWhiteScale = float.NaN;
        }
    }

    private static readonly Type? PlayerType =
        AccessTools.TypeByName("YukkuriMovieMaker.Player.TimelineVideoPlayer, YukkuriMovieMaker");
    private static readonly Type? RenderTargetType =
        AccessTools.TypeByName("YukkuriMovieMaker.Player.TimelineVideoPlayerRenderTargetResources, YukkuriMovieMaker");
    private static readonly Type? TimelineSourceType =
        AccessTools.TypeByName("YukkuriMovieMaker.Player.Video.TimelineSource, YukkuriMovieMaker");

    private static readonly FieldInfo? DevicesAndContextField =
        PlayerType?.GetField("devicesAndContext", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? RenderTargetField =
        PlayerType?.GetField("renderTarget", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? TimelineVideoField =
        PlayerType?.GetField("timelineVideo", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? TimelineField =
        PlayerType?.GetField("timeline", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? TargetPtrField =
        PlayerType?.GetField("targetPtr", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? PreviewDisplayZoomField =
        PlayerType?.GetField("previewDisplayZoom", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? PreviewViewCenterField =
        PlayerType?.GetField("previewViewCenter", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly PropertyInfo? PreviewAreaZoomProperty =
        PlayerType?.GetProperty("PreviewAreaZoom", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly MethodInfo? CreateControllerLayerMethod =
        PlayerType?.GetMethod("CreateControllerLayer", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo? DrawPreviewScrollBarsMethod =
        PlayerType?.GetMethod("DrawPreviewScrollBars", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo? GetVisibleVideoSizeMethod =
        PlayerType?.GetMethod("GetVisibleVideoSize", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo? CreatePreviewViewTransformMethod =
        PlayerType?.GetMethod("CreatePreviewViewTransform", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
    private static readonly PropertyInfo? TimelineOutputProperty =
        TimelineSourceType?.GetProperty("Output", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly PropertyInfo? BackBufferProperty =
        RenderTargetType?.GetProperty("BackBuffer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly PropertyInfo? ShadowEffectProperty =
        RenderTargetType?.GetProperty("ShadowEffect", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly PropertyInfo? ShadowOutputProperty =
        RenderTargetType?.GetProperty("ShadowOutput", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    public static void Apply(Harmony harmony)
    {
        try
        {
            if (PlayerType == null) return;
            var drawMethod = PlayerType.GetMethod("Draw", BindingFlags.Instance | BindingFlags.NonPublic);
            if (drawMethod == null) return;

            var prefix = typeof(HdrPlayerDrawPatch).GetMethod(nameof(DrawPrefix), BindingFlags.Static | BindingFlags.NonPublic);
            harmony.Patch(drawMethod, prefix: new HarmonyMethod(prefix));

            var disposeMethod = FindDisposeMethod(PlayerType);
            if (disposeMethod != null)
            {
                var disposePostfix = typeof(HdrPlayerDrawPatch).GetMethod(nameof(DisposePostfix), BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(disposeMethod, postfix: new HarmonyMethod(disposePostfix));
                System.Diagnostics.Debug.WriteLine($"[HDRInjector][HdrPlayerDrawPatch] HDR pipeline Dispose patch installed: {disposeMethod.DeclaringType?.FullName}.{disposeMethod.Name}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[HDRInjector][HdrPlayerDrawPatch] WARNING: TimelineVideoPlayer Dispose method was not found; HDR state cleanup is not automatically hooked.");
            }

            System.Diagnostics.Debug.WriteLine("[HDRInjector][HdrPlayerDrawPatch] HDR pipeline Draw patch installed.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HDRInjector][HdrPlayerDrawPatch] Apply error: {ex}");
        }
    }

    private static MethodInfo? FindDisposeMethod(Type playerType)
    {
        // Harmony must patch the method declaration that actually implements Dispose(),
        // not a MethodInfo merely inherited by TimelineVideoPlayer. Walk the hierarchy
        // and only accept a method declared by that specific type (DeclaredOnly).
        for (Type? t = playerType; t != null; t = t.BaseType)
        {
            var declared = t.GetMethod(
                "Dispose",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            if (declared != null)
                return declared;
        }

        // Explicit IDisposable implementation fallback. Again, return the method that
        // is actually declared/implemented by the mapped target type.
        if (typeof(IDisposable).IsAssignableFrom(playerType))
        {
            try
            {
                var map = playerType.GetInterfaceMap(typeof(IDisposable));
                for (int i = 0; i < map.InterfaceMethods.Length; i++)
                {
                    if (map.InterfaceMethods[i].Name != nameof(IDisposable.Dispose))
                        continue;

                    var target = map.TargetMethods[i];
                    if (target != null)
                        return target;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HDRInjector][HdrPlayerDrawPatch] IDisposable map lookup failed: {ex}");
            }
        }

        return null;
    }

    private static void DisposePostfix(object __instance)
    {
        try
        {
            if (States.TryGetValue(__instance, out var state))
            {
                state.Dispose();
                System.Diagnostics.Debug.WriteLine("[HDRInjector][HdrPlayerDrawPatch] HdrPlayerState disposed with TimelineVideoPlayer.");
            }
        }
        catch (Exception ex)
        {
            // Cleanup must never interfere with YMM4's own shutdown path.
            System.Diagnostics.Debug.WriteLine($"[HDRInjector][HdrPlayerDrawPatch] HdrPlayerState dispose error: {ex}");
        }
    }

    private static bool DrawPrefix(object __instance)
    {
        try
        {
            var devicesAndContext = DevicesAndContextField?.GetValue(__instance) as IGraphicsDevicesAndContext;
            var renderTarget = RenderTargetField?.GetValue(__instance);
            var timelineVideo = TimelineVideoField?.GetValue(__instance);
            var timeline = TimelineField?.GetValue(__instance) as Timeline;

            if (devicesAndContext == null || renderTarget == null || timelineVideo == null || timeline == null)
                return true;

            var timelineOutput = TimelineOutputProperty?.GetValue(timelineVideo) as ID2D1Image;
            var backBuffer = BackBufferProperty?.GetValue(renderTarget) as ID2D1Bitmap1;
            var shadowEffect = ShadowEffectProperty?.GetValue(renderTarget) as Shadow;
            var shadowOutput = ShadowOutputProperty?.GetValue(renderTarget) as ID2D1Image;
            if (timelineOutput == null || backBuffer == null) return true;

            var state = States.GetValue(__instance, _ => new HdrPlayerState());
            state.Ensure(devicesAndContext, timeline.VideoInfo.Width, timeline.VideoInfo.Height);
            if (state.HdrRenderTarget == null || state.SrgbToLinearEffect == null)
                return true;

            // Windows Advanced Color defines SDR reference white in nits.
            // For FP16/scRGB, SDR content must be scaled by SDRWhite / 80.
            // TimelineVideoPlayer already stores the preview HWND in targetPtr, so use
            // that directly instead of depending on the DXGI description wrapper.
            float sdrWhiteScale = 1.0f;
            try
            {
                var hwnd = TargetPtrField?.GetValue(__instance) is IntPtr ptr ? ptr : IntPtr.Zero;
                System.Diagnostics.Debug.WriteLine($"[HDRInjector][SDRWhite] Query start hwnd=0x{hwnd.ToInt64():X}");
                sdrWhiteScale = SdrWhiteLevelHelper.GetScaleForWindow(hwnd);
                HdrPreviewManager.SetSdrWhiteScale(sdrWhiteScale);
                System.Diagnostics.Debug.WriteLine($"[HDRInjector][SDRWhite] Scale applied={sdrWhiteScale:0.####}");
            }
            catch (Exception whiteEx)
            {
                System.Diagnostics.Debug.WriteLine($"[HDRInjector][SDRWhite] Query exception; using scale=1.0. {whiteEx}");
            }
            // Avoid hammering the native ID2D1Properties.SetValue every frame. This also
            // prevents a late Draw() from touching an already-invalid native effect when
            // YMM4 recreates its graphics device.
            if (float.IsNaN(state.LastSdrWhiteScale) || MathF.Abs(state.LastSdrWhiteScale - sdrWhiteScale) > 0.0001f)
            {
                state.SrgbToLinearEffect.SdrWhiteScale = sdrWhiteScale;
                state.LastSdrWhiteScale = sdrWhiteScale;
            }

            float previewDisplayZoom = PreviewDisplayZoomField != null ? (float)PreviewDisplayZoomField.GetValue(__instance)! : 1f;
            Vector2 previewViewCenter = PreviewViewCenterField != null ? (Vector2)PreviewViewCenterField.GetValue(__instance)! : Vector2.Zero;
            float previewAreaZoom = PreviewAreaZoomProperty != null ? (float)PreviewAreaZoomProperty.GetValue(__instance)! : 1f;
            int videoWidth = timeline.VideoInfo.Width;
            int videoHeight = timeline.VideoInfo.Height;

            using var controllerLayer = CreateControllerLayerMethod?.Invoke(__instance, null) as ID2D1CommandList;

            if (controllerLayer != null && shadowEffect != null)
            {
                shadowEffect.SetInput(0, controllerLayer, (RawBool)true);
                shadowEffect.BlurStandardDeviation =
                    Math.Max(0.5f, (float)(1.0 / previewAreaZoom / 2.0)) / previewDisplayZoom;
            }

            var dc = devicesAndContext.DeviceContext;
            Vector2 targetOffset = new(videoWidth / 2f, videoHeight / 2f);
            Matrix3x2 visibleTransform = CreatePreviewTransform(__instance, previewDisplayZoom, previewViewCenter, videoWidth, videoHeight, dc.Transform);

            // Pass 1: 元のYMM4 SDR画像をsRGB -> LinearでFP16中間ターゲットへ。
            state.SrgbToLinearEffect.SetInput(0, timelineOutput, (RawBool)true);
            dc.Target = state.HdrRenderTarget;
            dc.BeginDraw();
            try
            {
                dc.Clear(new Color4(0, 0, 0, 1));
                var originalTransform = dc.Transform;
                dc.Transform = visibleTransform;
                try
                {
                    dc.DrawImage(state.SrgbToLinearEffect.Output, in targetOffset);
                    if (controllerLayer != null && shadowOutput != null)
                    {
                        dc.BlendImage(shadowOutput, BlendMode.Multiply, new Vector2?(targetOffset), null, InterpolationMode.HighQualityCubic);
                        dc.DrawImage(controllerLayer, in targetOffset);
                    }
                }
                finally
                {
                    dc.Transform = originalTransform;
                }
            }
            finally
            {
                dc.EndDraw();
                dc.Target = null;
            }

            // Pass 2: FP16/scRGB output. The swap-chain is R16G16B16A16_FLOAT + scRGB,
            // so the data must remain linear sRGB/Rec.709 values. Do NOT PQ-encode or
            // convert the gamut to BT.2020 here.
            dc.Target = backBuffer;
            dc.BeginDraw();
            try
            {
                dc.Clear(new Color4(0, 0, 0, 1));
                var originalTransform = dc.Transform;
                dc.Transform = Matrix3x2.Identity;
                try
                {
                    var outputOffset = Vector2.Zero;
                    dc.DrawImage(state.HdrRenderTarget, in outputOffset);
                }
                finally
                {
                    dc.Transform = originalTransform;
                }
            }
            finally
            {
                dc.EndDraw();
                dc.Target = null;
            }

            DrawPreviewScrollBarsMethod?.Invoke(__instance, new object[] { previewDisplayZoom, previewViewCenter });
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HDRInjector][HdrPlayerDrawPatch] Draw exception: {ex}");
            return true;
        }
    }

    private static Matrix3x2 CreatePreviewTransform(object instance, float displayZoom, Vector2 viewCenter, int width, int height, Matrix3x2 original)
    {
        var visibleVideoSize = GetVisibleVideoSizeMethod != null
            ? (Vector2)GetVisibleVideoSizeMethod.Invoke(instance, new object[] { displayZoom })!
            : new Vector2(width, height);
        var view = CreatePreviewViewTransformMethod != null
            ? (Matrix3x2)CreatePreviewViewTransformMethod.Invoke(null, new object[] { visibleVideoSize, viewCenter, (float)width, (float)height })!
            : Matrix3x2.Identity;
        return view * original;
    }
}
