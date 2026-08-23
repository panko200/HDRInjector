using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Numerics;
using SharpGen.Runtime;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Blend = Vortice.Direct2D1.Effects.Blend;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Effects;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace HDRInjector.Effects;

[VideoEffect("HDR Glow+ (ピラミッド合成・色収差・放射光)", new string[] { "加工", "エフェクト", "HDR", "発光" }, new string[] { "hdr", "bloom", "glow", "deep", "発光", "ブルーム", "グロー", "色収差", "放射光" })]
public class HdrGlowPlusEffect : VideoEffectBase
{
    private Color innerColor = Colors.White;
    private Color outerColor = Colors.White;
    private bool colorize = false;
    private ChromaStyleMode chromaStyle = ChromaStyleMode.Radial;


    public override string Label => $"HDR Glow+ (Steps:{(int)Steps.GetValue(0, 1, 30)}, EV:{Exposure.GetValue(0, 1, 30):+0.0;-0.0;+0.0})";

    [Display(GroupName = "基本", Name = "露出 / 強度 (EV)", Description = "光の強さを EV 単位で調整 (-5.0 EV 〜 +10.0 EV)", Order = 10)]
    [AnimationSlider("F2", "EV", -5.0, 10.0)]
    public Animation Exposure { get; } = new Animation(1.0, -5.0, 10.0);

    [Display(GroupName = "基本", Name = "発光閾値 (%)", Description = "発光を開始する明るさの基準値 (0〜100%)", Order = 20)]
    [AnimationSlider("F1", "%", 0.0, 100.0)]
    public Animation Threshold { get; } = new Animation(50.0, 0.0, 100.0);

    [Display(GroupName = "基本", Name = "コントラスト (%)", Description = "光の明暗コントラスト (0〜200%)", Order = 30)]
    [AnimationSlider("F0", "%", 0.0, 200.0)]
    public Animation Contrast { get; } = new Animation(100.0, 0.0, 200.0);

    [Display(GroupName = "基本", Name = "ピラミッド階層数 (Steps)", Description = "光の広がり階層数 (1〜8段階)", Order = 40)]
    [AnimationSlider("F0", "", 1.0, 8.0)]
    public Animation Steps { get; } = new Animation(6.0, 1.0, 8.0);

    [Display(GroupName = "基本", Name = "光の広がり (Radius)", Description = "基本ブラー半径 (ピクセル)", Order = 50)]
    [AnimationSlider("F1", "px", 1.0, 200.0)]
    public Animation BlurRadius { get; } = new Animation(30.0, 1.0, 200.0);

    [Display(GroupName = "色収差 (Chroma)", Name = "収差スタイル", Description = "色収差の広がり方", Order = 200)]
    [EnumComboBox]
    public ChromaStyleMode ChromaStyle
    {
        get => chromaStyle;
        set => Set(ref chromaStyle, value);
    }

    [Display(GroupName = "色収差 (Chroma)", Name = "赤のズレ (%)", Description = "Rチャンネルのズレ量", Order = 210)]
    [AnimationSlider("F1", "%", -50.0, 50.0)]
    public Animation ChromaR { get; } = new Animation(0.0, -50.0, 50.0);

    [Display(GroupName = "色収差 (Chroma)", Name = "青のズレ (%)", Description = "Bチャンネルのズレ量", Order = 220)]
    [AnimationSlider("F1", "%", -50.0, 50.0)]
    public Animation ChromaB { get; } = new Animation(0.0, -50.0, 50.0);

    [Display(GroupName = "放射光 (Rays)", Name = "光条の長さ", Description = "放射光の伸びる長さ (0〜100%)", Order = 300)]
    [AnimationSlider("F1", "%", 0.0, 100.0)]
    public Animation RayLength { get; } = new Animation(0.0, 0.0, 100.0);

    [Display(GroupName = "カラー設定", Name = "発光に着色する", Description = "光に指定した色を付けます", Order = 400)]
    [ToggleSlider]
    public bool Colorize
    {
        get => colorize;
        set => Set(ref colorize, value);
    }

    [Display(GroupName = "カラー設定", Name = "中心色 (Inner Tint)", Description = "光の中心付近の色", Order = 410)]
    [ColorPicker]
    public Color InnerColor
    {
        get => innerColor;
        set => Set(ref innerColor, value);
    }

    [Display(GroupName = "カラー設定", Name = "外側色 (Outer Tint)", Description = "光の外側付近の色", Order = 420)]
    [ColorPicker]
    public Color OuterColor
    {
        get => outerColor;
        set => Set(ref outerColor, value);
    }

    public override IEnumerable<string> CreateExoVideoFilters(int keyFrameIndex, ExoOutputDescription exoOutputDescription) => Array.Empty<string>();

    protected override IEnumerable<IAnimatable> GetAnimatables()
    {
        yield return Exposure;
        yield return Threshold;
        yield return Contrast;
        yield return Steps;
        yield return BlurRadius;
        yield return ChromaR;
        yield return ChromaB;
        yield return RayLength;
    }

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
    {
        return new HdrGlowPlusEffectProcessor(devices, this);
    }
}

public class HdrGlowPlusEffectProcessor : IVideoEffectProcessor, IDisposable
{
    private readonly HdrGlowPlusEffect item;
    private readonly IGraphicsDevicesAndContext devices;
    private ID2D1Image? input;

    private class PyramidLevel : IDisposable
    {
        public Scale Downscaler { get; }
        public GaussianBlur BlurX { get; }
        public GaussianBlur BlurY { get; }
        public Crop Cropper { get; }
        public Scale Upscaler { get; }
        public Blend Blender { get; }

        public PyramidLevel(ID2D1DeviceContext dc)
        {
            Downscaler = new Scale(dc);
            // D2D1_SCALE_PROP_CLAMP_OUTPUT = 4
            try { Downscaler.SetValue(4, 0.0f); } catch { }
            BlurX = new GaussianBlur(dc) { Optimization = GaussianBlurOptimization.Speed, BorderMode = BorderMode.Soft };
            // D2D1_GAUSSIANBLUR_PROP_CLAMP_OUTPUT = 3
            try { BlurX.SetValue(3, 0.0f); } catch { }
            BlurY = new GaussianBlur(dc) { Optimization = GaussianBlurOptimization.Speed, BorderMode = BorderMode.Soft };
            // D2D1_GAUSSIANBLUR_PROP_CLAMP_OUTPUT = 3
            try { BlurY.SetValue(3, 0.0f); } catch { }
            Cropper = new Crop(dc);
            // D2D1_CROP_PROP_CLAMP_OUTPUT = 2
            try { Cropper.SetValue(2, 0.0f); } catch { }
            Upscaler = new Scale(dc);
            // D2D1_SCALE_PROP_CLAMP_OUTPUT = 4
            try { Upscaler.SetValue(4, 0.0f); } catch { }
            Blender = new Blend(dc) { Mode = BlendMode.LinearDodge };
        }

        public void Dispose()
        {
            Downscaler.Dispose();
            BlurX.Dispose();
            BlurY.Dispose();
            Cropper.Dispose();
            Upscaler.Dispose();
            Blender.Dispose();
        }
    }

    private readonly List<PyramidLevel> pyramidLevels = new();
    private ColorMatrix? thresholdEffect;
    private Crop? initialCrop;
    private Crop? finalSourceCrop;
    private Crop? finalGlowCrop;
    private Flood? padFlood;
    private Composite? padComposite;
    private HdrGlowCustomEffect? glowCustomEffect;

    public ID2D1Image Output => glowCustomEffect?.Output ?? input!;

    public HdrGlowPlusEffectProcessor(IGraphicsDevicesAndContext devices, HdrGlowPlusEffect item)
    {
        this.devices = devices;
        this.item = item;
    }

    public void SetInput(ID2D1Image? input) => this.input = input;
    public void ClearInput() => this.input = null;

    private void EnsurePyramidLevels(ID2D1DeviceContext dc, int count)
    {
        while (pyramidLevels.Count < count) pyramidLevels.Add(new PyramidLevel(dc));
    }

    public DrawDescription Update(EffectDescription effectDescription)
    {
        var dc = devices.DeviceContext;
        if (input == null) return effectDescription.DrawDescription;

        int frame = effectDescription.ItemPosition.Frame;
        int length = effectDescription.ItemDuration.Frame;
        int fps = effectDescription.FPS;

        float exposure = (float)item.Exposure.GetValue(frame, length, fps);
        float threshold = (float)item.Threshold.GetValue(frame, length, fps) / 100.0f;
        float contrast = (float)item.Contrast.GetValue(frame, length, fps) / 100.0f;
        int steps = Math.Clamp((int)item.Steps.GetValue(frame, length, fps), 1, 8);
        float blurRadius = (float)item.BlurRadius.GetValue(frame, length, fps);
        float chromaR = (float)item.ChromaR.GetValue(frame, length, fps) / 100.0f;
        float chromaB = (float)item.ChromaB.GetValue(frame, length, fps) / 100.0f;
        float rayLength = (float)item.RayLength.GetValue(frame, length, fps) / 100.0f;

        thresholdEffect ??= new ColorMatrix(dc);
        initialCrop ??= new Crop(dc);
        finalSourceCrop ??= new Crop(dc);
        finalGlowCrop ??= new Crop(dc);
        // D2D1_CROP_PROP_CLAMP_OUTPUT = 2
        try { initialCrop.SetValue(2, 0.0f); } catch { }
        try { finalSourceCrop.SetValue(2, 0.0f); } catch { }
        try { finalGlowCrop.SetValue(2, 0.0f); } catch { }
        padFlood ??= new Flood(dc) { Color = new Vector4(0f, 0f, 0f, 0f) };
        padComposite ??= new Composite(dc);
        glowCustomEffect ??= new HdrGlowCustomEffect(devices);

        var bounds = dc.GetImageLocalBounds(input);
        if (float.IsInfinity(bounds.Left) || float.IsInfinity(bounds.Right) || Math.Abs(bounds.Right - bounds.Left) > 50000)
            bounds = new Vortice.RawRectF(-960, -540, 960, 540);

        float baseBlurSigma = Math.Clamp(blurRadius, 0.1f, 200.0f);
        float maxScaleFactor = (float)Math.Pow(2, steps - 1);
        float maxSpread = baseBlurSigma * 4.0f * maxScaleFactor;
        float safePadding = maxSpread + 30.0f;
        if (rayLength > 0) safePadding += 1920.0f * rayLength * 0.5f;

        float centerX = (bounds.Left + bounds.Right) / 2.0f;
        float centerY = (bounds.Top + bounds.Bottom) / 2.0f;
        float inputHalfW = (bounds.Right - bounds.Left) / 2.0f;
        float inputHalfH = (bounds.Bottom - bounds.Top) / 2.0f;

        float finalHalfW = Math.Min(inputHalfW + safePadding, 1920.0f);
        float finalHalfH = Math.Min(inputHalfH + safePadding, 1920.0f);
        var safeRect = new Vector4(centerX - finalHalfW, centerY - finalHalfH, centerX + finalHalfW, centerY + finalHalfH);

        using (var floodOut = padFlood.Output)
        {
            padComposite.SetInput(0, floodOut, (RawBool)true);
        }
        padComposite.SetInput(1, input, (RawBool)true);
        var paddedInput = padComposite.Output;

        initialCrop.Rectangle = safeRect;
        initialCrop.SetInput(0, paddedInput, (RawBool)true);

        float slope = 1.0f + (contrast * 1.5f);
        float offset = -threshold * slope;
        thresholdEffect.Matrix = new Matrix5x4
        {
            M11 = slope, M22 = slope, M33 = slope, M44 = 1f,
            M51 = offset, M52 = offset, M53 = offset
        };
        using (var initCropOut = initialCrop.Output)
        {
            thresholdEffect.SetInput(0, initCropOut, (RawBool)true);
        }

        ID2D1Image processingImage = thresholdEffect.Output;
        EnsurePyramidLevels(dc, steps);

        for (int i = 0; i < steps; i++)
        {
            var level = pyramidLevels[i];
            if (i > 0)
            {
                level.Downscaler.SetValue((int)ScaleProperties.Scale, new Vector2(0.5f, 0.5f));
                level.Downscaler.SetInput(0, processingImage, (RawBool)true);
                processingImage = level.Downscaler.Output;
            }
            level.BlurX.StandardDeviation = baseBlurSigma;
            level.BlurX.SetInput(0, processingImage, (RawBool)true);
            processingImage = level.BlurX.Output;

            level.BlurY.StandardDeviation = baseBlurSigma;
            level.BlurY.SetInput(0, processingImage, (RawBool)true);
            processingImage = level.BlurY.Output;

            float currentLevelScale = (float)Math.Pow(0.5, i);
            var levelRect = new Vector4(safeRect.X * currentLevelScale, safeRect.Y * currentLevelScale, safeRect.Z * currentLevelScale, safeRect.W * currentLevelScale);
            level.Cropper.Rectangle = levelRect;
            level.Cropper.SetInput(0, processingImage, (RawBool)true);
            processingImage = level.Cropper.Output;
        }

        ID2D1Image accumulated = pyramidLevels[steps - 1].Cropper.Output;
        for (int i = steps - 2; i >= 0; i--)
        {
            var level = pyramidLevels[i];
            level.Upscaler.SetValue((int)ScaleProperties.Scale, new Vector2(2.0f, 2.0f));
            level.Upscaler.SetInput(0, accumulated, (RawBool)true);

            using (var upOut = level.Upscaler.Output)
            using (var cropOut = level.Cropper.Output)
            {
                level.Blender.SetInput(0, cropOut, (RawBool)true);
                level.Blender.SetInput(1, upOut, (RawBool)true);
            }
            accumulated = level.Blender.Output;
        }

        finalGlowCrop.Rectangle = safeRect;
        finalGlowCrop.SetInput(0, accumulated, (RawBool)true);

        finalSourceCrop.Rectangle = safeRect;
        finalSourceCrop.SetInput(0, paddedInput, (RawBool)true);

        using (var glowCropped = finalGlowCrop.Output)
        using (var sourceCropped = finalSourceCrop.Output)
        {
            glowCustomEffect.SetInput(0, glowCropped, (RawBool)true);
            glowCustomEffect.SetInput(1, sourceCropped, (RawBool)true);
        }

        glowCustomEffect.Exposure = exposure;
        glowCustomEffect.Threshold = 0f;
        glowCustomEffect.Contrast = 0f;
        glowCustomEffect.Colorize = item.Colorize;
        Color inC = item.InnerColor;
        Color outC = item.OuterColor;
        glowCustomEffect.InnerColor = new Vector4(inC.R / 255f, inC.G / 255f, inC.B / 255f, inC.A / 255f);
        glowCustomEffect.OuterColor = new Vector4(outC.R / 255f, outC.G / 255f, outC.B / 255f, outC.A / 255f);
        glowCustomEffect.LinearColor = true;
        glowCustomEffect.ClampAlpha = true;
        glowCustomEffect.DitherStrength = 1.0f;
        glowCustomEffect.FalloffMode = 1.0f; // 逆2乗
        glowCustomEffect.FalloffPower = 1.0f;
        glowCustomEffect.ChromaR = chromaR;
        glowCustomEffect.ChromaG = 0f;
        glowCustomEffect.ChromaB = chromaB;
        glowCustomEffect.ChromaStyle = 0f;
        glowCustomEffect.ChromaAngle = 0f;
        glowCustomEffect.ChromaCenterX = centerX;
        glowCustomEffect.ChromaCenterY = centerY;
        glowCustomEffect.RayLength = rayLength;
        glowCustomEffect.RayAngle = 0f;
        glowCustomEffect.RaySamples = 8f;
        glowCustomEffect.RayStyle = 0f;
        glowCustomEffect.RayCenterX = centerX;
        glowCustomEffect.RayCenterY = centerY;
        glowCustomEffect.RayFalloff = 1.0f;
        glowCustomEffect.TexWidth = Math.Max(1.0f, safeRect.Z - safeRect.X);
        glowCustomEffect.TexHeight = Math.Max(1.0f, safeRect.W - safeRect.Y);

        return effectDescription.DrawDescription;
    }

    public void Dispose()
    {
        foreach (var lvl in pyramidLevels) lvl.Dispose();
        pyramidLevels.Clear();
        thresholdEffect?.Dispose();
        initialCrop?.Dispose();
        finalSourceCrop?.Dispose();
        finalGlowCrop?.Dispose();
        padFlood?.Dispose();
        padComposite?.Dispose();
        glowCustomEffect?.Dispose();
    }
}
