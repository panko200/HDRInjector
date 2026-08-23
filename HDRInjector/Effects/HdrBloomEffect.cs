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
using YukkuriMovieMaker.Player.Video.Effects;
using YukkuriMovieMaker.Plugin.Effects;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace HDRInjector.Effects;

public enum BloomFalloffType
{
    [Display(Name = "逆2乗型 (Inverse Square / 物理ベース)")]
    InverseSquare = 1,

    [Display(Name = "ガウス型 (Gaussian / やわらか)")]
    Gaussian = 0,

    [Display(Name = "指数型 (Exponential / ネオン)")]
    Exponential = 2
}

public enum ChromaStyleMode
{
    [Display(Name = "放射状")] Radial = 1,
    [Display(Name = "平行・指向性")] Directional = 2
}

public enum RayStyleMode
{
    [Display(Name = "放射状")] Radial = 1,
    [Display(Name = "平行・指向性")] Directional = 2
}

[VideoEffect("HDR ブルーム (物理逆2乗・超滑らか)", new string[] { "描画", "エフェクト", "HDR", "発光" }, new string[] { "hdr", "bloom", "glow", "発光", "ブルーム", "グロー", "逆2乗", "物理" })]
public class HdrBloomEffect : VideoEffectBase
{
    private Color glowColor = Colors.White;
    private bool colorize = false;
    private bool linearColor = true;
    private BloomFalloffType falloffType = BloomFalloffType.InverseSquare;

    public override string Label => $"HDR ブルーム ({FalloffType}, {BlurRadius.GetValue(0, 1, 30):F0}px, EV:{Exposure.GetValue(0, 1, 30):+0.0;-0.0;+0.0})";

    [Display(GroupName = "光の減衰カーブ", Name = "減衰タイプ (Falloff)", Description = "光が外側に向かって減衰する物理カーブの形式", Order = 5)]
    [EnumComboBox]
    public BloomFalloffType FalloffType
    {
        get => falloffType;
        set => Set(ref falloffType, value);
    }

    [Display(GroupName = "光の減衰カーブ", Name = "減衰の鋭さ (Falloff)", Description = "逆2乗/指数の減衰強度 (高いほど中心の芯が鋭く際立ちます)", Order = 6)]
    [AnimationSlider("F1", "", 0.1, 5.0)]
    public Animation FalloffPower { get; } = new Animation(1.0, 0.1, 5.0);

    [Display(GroupName = "発光特性", Name = "発光閾値 (Threshold)", Description = "発光を開始する明るさの基準値 (1.0 = 通常白、1.0以上でHDR高輝度部分のみ発光)", Order = 10)]
    [AnimationSlider("F2", "", 0.0, 5.0)]
    public Animation Threshold { get; } = new Animation(0.8, 0.0, 5.0);

    [Display(GroupName = "発光特性", Name = "ソフトニー幅 (%)", Description = "明暗境界の滑らかさ (高いほど境界の段差がなくなり自然に発光します)", Order = 20)]
    [AnimationSlider("F0", "%", 0.0, 100.0)]
    public Animation SoftKnee { get; } = new Animation(60.0, 0.0, 100.0);

    [Display(GroupName = "発光特性", Name = "光の広がり (Radius)", Description = "光が拡散する広さ (ピクセル)", Order = 30)]
    [AnimationSlider("F1", "px", 1.0, 500.0)]
    public Animation BlurRadius { get; } = new Animation(50.0, 1.0, 500.0);

    [Display(GroupName = "発光特性", Name = "発光強度 (%)", Description = "ブルーム光の強さ", Order = 40)]
    [AnimationSlider("F0", "%", 0.0, 500.0)]
    public Animation Intensity { get; } = new Animation(150.0, 0.0, 500.0);

    [Display(GroupName = "HDR 制御", Name = "露出ブースト (EV)", Description = "HDR 露出ブースト値 (-5.0 EV 〜 +10.0 EV: +1EVごとに光のエネルギーが2倍になります)", Order = 50)]
    [AnimationSlider("F1", "EV", -5.0, 10.0)]
    public Animation Exposure { get; } = new Animation(0.0, -5.0, 10.0);

    [Display(GroupName = "カラー設定", Name = "発光に着色する", Description = "光に指定した色を付けます", Order = 60)]
    [ToggleSlider]
    public bool Colorize
    {
        get => colorize;
        set => Set(ref colorize, value);
    }

    [Display(GroupName = "カラー設定", Name = "発光カラー", Description = "発光の色", Order = 70)]
    [ColorPicker]
    public Color GlowColor
    {
        get => glowColor;
        set => Set(ref glowColor, value);
    }

    [Display(GroupName = "品質設定", Name = "リニア色空間処理", Description = "物理的に正確な光の合成（白飛びを抑え、鮮やかで自然な発光を実現）", Order = 80)]
    [ToggleSlider]
    public bool LinearColor
    {
        get => linearColor;
        set => Set(ref linearColor, value);
    }

    [Display(GroupName = "品質設定", Name = "ディザリング強度 (%)", Description = "8bit画面でのカラーバンディング（等高線状の縞模様）を完全消去するディザ強度", Order = 90)]
    [AnimationSlider("F0", "%", 0.0, 200.0)]
    public Animation DitherStrength { get; } = new Animation(100.0, 0.0, 200.0);

    public override IEnumerable<string> CreateExoVideoFilters(int keyFrameIndex, ExoOutputDescription exoOutputDescription)
    {
        return Array.Empty<string>();
    }

    protected override IEnumerable<IAnimatable> GetAnimatables()
    {
        yield return FalloffPower;
        yield return Threshold;
        yield return SoftKnee;
        yield return BlurRadius;
        yield return Intensity;
        yield return Exposure;
        yield return DitherStrength;
    }

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
    {
        return new HdrBloomEffectProcessor(devices, this);
    }
}

public class HdrBloomEffectProcessor : VideoEffectProcessorBase
{
    private readonly HdrBloomEffect item;

    // マルチスケール・ピラミッド・ブラー (Level 0〜4: 5段階スケール)
    private readonly GaussianBlur[] blurLevels = new GaussianBlur[5];
    private Composite? multiBlurComposite;
    private HdrGlowCustomEffect? customGlowShader;

    private bool isFirstUpdate = true;
    private double lastThreshold;
    private double lastSoftKnee;
    private double lastRadius;
    private double lastIntensity;
    private double lastExposure;
    private double lastDither;
    private double lastFalloffPower;
    private BloomFalloffType lastFalloffType;
    private bool lastColorize;
    private Color lastColor;
    private bool lastLinear;

    public HdrBloomEffectProcessor(IGraphicsDevicesAndContext devices, HdrBloomEffect item) : base(devices)
    {
        this.item = item;
    }

    protected override ID2D1Image? CreateEffect(IGraphicsDevicesAndContext devices)
    {
        var dc = devices.DeviceContext;

        for (int i = 0; i < blurLevels.Length; i++)
        {
            blurLevels[i] = new GaussianBlur(dc)
            {
                Optimization = GaussianBlurOptimization.Quality,
                BorderMode = BorderMode.Soft
            };
            // ClampOutput = false を直接設定（Vorticeプロパティ未公開のため、プロパティIDで設定）
            // D2D1_GAUSSIANBLUR_PROP_CLAMP_OUTPUT = 3
            try { blurLevels[i].SetValue(3, 0.0f); } catch { }
            disposer.Collect(blurLevels[i]);
        }

        multiBlurComposite = new Composite(dc);
        multiBlurComposite.Mode = CompositeMode.SourceOver;
        for (int i = 0; i < blurLevels.Length; i++)
        {
            multiBlurComposite.SetInput(i, blurLevels[i].Output, (RawBool)true);
        }
        disposer.Collect(multiBlurComposite);

        customGlowShader = new HdrGlowCustomEffect(devices);
        customGlowShader.SetInput(0, multiBlurComposite.Output, (RawBool)true);
        disposer.Collect(customGlowShader);

        var output = customGlowShader.Output;
        disposer.Collect(output);
        return output;
    }

    protected override void setInput(ID2D1Image? input)
    {
        for (int i = 0; i < blurLevels.Length; i++)
        {
            blurLevels[i]?.SetInput(0, input, (RawBool)true);
        }
        customGlowShader?.SetInput(1, input, (RawBool)true);
    }

    protected override void ClearEffectChain()
    {
        for (int i = 0; i < blurLevels.Length; i++)
        {
            blurLevels[i]?.SetInput(0, null, (RawBool)true);
        }
        customGlowShader?.SetInput(1, null, (RawBool)true);
    }

    public override DrawDescription Update(EffectDescription effectDescription)
    {
        if (IsPassThroughEffect || customGlowShader == null || multiBlurComposite == null)
            return effectDescription.DrawDescription;

        int frame = effectDescription.ItemPosition.Frame;
        int length = effectDescription.ItemDuration.Frame;
        int fps = effectDescription.FPS;

        double threshold = item.Threshold.GetValue(frame, length, fps);
        double softKnee = item.SoftKnee.GetValue(frame, length, fps) / 100.0;
        double radius = item.BlurRadius.GetValue(frame, length, fps);
        double intensity = item.Intensity.GetValue(frame, length, fps) / 100.0;
        double exposure = item.Exposure.GetValue(frame, length, fps);
        double dither = item.DitherStrength.GetValue(frame, length, fps) / 100.0;
        double falloffPower = item.FalloffPower.GetValue(frame, length, fps);
        var falloffType = item.FalloffType;
        bool colorize = item.Colorize;
        Color c = item.GlowColor;
        bool linear = item.LinearColor;

        if (isFirstUpdate ||
            Math.Abs(lastThreshold - threshold) > 0.001 ||
            Math.Abs(lastSoftKnee - softKnee) > 0.001 ||
            Math.Abs(lastRadius - radius) > 0.001 ||
            Math.Abs(lastIntensity - intensity) > 0.001 ||
            Math.Abs(lastExposure - exposure) > 0.001 ||
            Math.Abs(lastDither - dither) > 0.001 ||
            Math.Abs(lastFalloffPower - falloffPower) > 0.001 ||
            lastFalloffType != falloffType ||
            lastColorize != colorize ||
            lastColor != c ||
            lastLinear != linear)
        {
            float baseSigma = (float)Math.Max(0.1, radius);
            blurLevels[0].StandardDeviation = baseSigma * 0.25f;
            blurLevels[1].StandardDeviation = baseSigma * 0.75f;
            blurLevels[2].StandardDeviation = baseSigma * 2.0f;
            blurLevels[3].StandardDeviation = baseSigma * 5.0f;
            blurLevels[4].StandardDeviation = baseSigma * 12.0f;

            customGlowShader.Threshold = (float)threshold;
            customGlowShader.Contrast = (float)softKnee;
            customGlowShader.Exposure = (float)exposure + (float)Math.Log2(Math.Max(0.01, intensity));
            customGlowShader.DitherStrength = (float)dither;
            customGlowShader.FalloffMode = (float)falloffType;
            customGlowShader.FalloffPower = (float)falloffPower;
            customGlowShader.Colorize = colorize;
            customGlowShader.LinearColor = linear;
            customGlowShader.InnerColor = new Vector4(c.R / 255.0f, c.G / 255.0f, c.B / 255.0f, c.A / 255.0f);
            customGlowShader.OuterColor = new Vector4(c.R / 255.0f, c.G / 255.0f, c.B / 255.0f, c.A / 255.0f);
            customGlowShader.SourceOpacity = 1.0f;
            customGlowShader.RayLength = 0f;
            customGlowShader.ChromaR = 0f;
            customGlowShader.ChromaG = 0f;
            customGlowShader.ChromaB = 0f;

            lastThreshold = threshold;
            lastSoftKnee = softKnee;
            lastRadius = radius;
            lastIntensity = intensity;
            lastExposure = exposure;
            lastDither = dither;
            lastFalloffPower = falloffPower;
            lastFalloffType = falloffType;
            lastColorize = colorize;
            lastColor = c;
            lastLinear = linear;
            isFirstUpdate = false;
        }

        return effectDescription.DrawDescription;
    }
}
