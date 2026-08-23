using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using SharpGen.Runtime;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Player.Video.Effects;
using YukkuriMovieMaker.Plugin.Effects;

namespace HDRInjector.Effects;

[VideoEffect("HDR トーンマッピング & 露出補正", new string[] { "描画", "エフェクト", "HDR", "色調補正" }, new string[] { "hdr", "tonemap", "exposure", "aces", "露出", "トーンマップ" })]
public class HdrToneMappingEffect : VideoEffectBase
{
    public override string Label => $"HDR トーンマップ ({Exposure.GetValue(0, 1, 30):+0.0;-0.0;+0.0} EV, ガンマ:{Gamma.GetValue(0, 1, 30):F2})";

    [Display(Name = "露出 (Exposure / EV)", Description = "全体の明るさを EV 単位で調整 (-5.0 EV 〜 +5.0 EV)", Order = 10)]
    [AnimationSlider("F1", "EV", -5.0, 5.0)]
    public Animation Exposure { get; } = new Animation(0.0, -5.0, 5.0);

    [Display(Name = "ガンマ (Gamma)", Description = "ガンマ補正カーブ (標準: 1.0)", Order = 20)]
    [AnimationSlider("F2", "", 0.2, 3.0)]
    public Animation Gamma { get; } = new Animation(1.0, 0.2, 3.0);

    [Display(Name = "コントラスト (%)", Description = "明暗の対比 (標準: 100%)", Order = 30)]
    [AnimationSlider("F0", "%", 0.0, 200.0)]
    public Animation Contrast { get; } = new Animation(100.0, 0.0, 200.0);

    [Display(Name = "彩度 (%)", Description = "色の鮮やかさ (標準: 100%)", Order = 40)]
    [AnimationSlider("F0", "%", 0.0, 200.0)]
    public Animation Saturation { get; } = new Animation(100.0, 0.0, 200.0);

    public override IEnumerable<string> CreateExoVideoFilters(int keyFrameIndex, ExoOutputDescription exoOutputDescription)
    {
        return Array.Empty<string>();
    }

    protected override IEnumerable<IAnimatable> GetAnimatables()
    {
        yield return Exposure;
        yield return Gamma;
        yield return Contrast;
        yield return Saturation;
    }

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
    {
        return new HdrToneMappingEffectProcessor(devices, this);
    }
}

public class HdrToneMappingEffectProcessor : VideoEffectProcessorBase
{
    private readonly HdrToneMappingEffect item;
    private HdrToneMappingCustomEffect? toneMapShader;
    private bool isFirstUpdate = true;
    private double lastExposure;
    private double lastGamma;
    private double lastContrast;
    private double lastSaturation;

    public HdrToneMappingEffectProcessor(IGraphicsDevicesAndContext devices, HdrToneMappingEffect item) : base(devices)
    {
        this.item = item;
    }

    protected override ID2D1Image? CreateEffect(IGraphicsDevicesAndContext devices)
    {
        // D2D組み込み ColorMatrix+GammaTransfer の代わりにカスタムシェーダーを使用。
        // D2D組み込み効果は内部的に8bit UNorm中間バッファを使用し、
        // HDR scRGB (>1.0) の値をクランプしてしまうため、
        // 16bit/32bit Float精度でHDR値を保持するカスタムシェーダーに置換する。
        toneMapShader = new HdrToneMappingCustomEffect(devices);
        disposer.Collect(toneMapShader);

        var output = toneMapShader.Output;
        disposer.Collect(output);
        return output;
    }

    protected override void setInput(ID2D1Image? input)
    {
        toneMapShader?.SetInput(0, input, (RawBool)true);
    }

    protected override void ClearEffectChain()
    {
        toneMapShader?.SetInput(0, null, (RawBool)true);
    }

    public override DrawDescription Update(EffectDescription effectDescription)
    {
        if (IsPassThroughEffect || toneMapShader == null)
            return effectDescription.DrawDescription;

        int frame = effectDescription.ItemPosition.Frame;
        int length = effectDescription.ItemDuration.Frame;
        int fps = effectDescription.FPS;

        double exposure = item.Exposure.GetValue(frame, length, fps);
        double gamma = item.Gamma.GetValue(frame, length, fps);
        double contrast = item.Contrast.GetValue(frame, length, fps) / 100.0;
        double sat = item.Saturation.GetValue(frame, length, fps) / 100.0;

        if (isFirstUpdate || Math.Abs(lastExposure - exposure) > 0.001 || Math.Abs(lastGamma - gamma) > 0.001 ||
            Math.Abs(lastContrast - contrast) > 0.001 || Math.Abs(lastSaturation - sat) > 0.001)
        {
            toneMapShader.Exposure = (float)exposure;
            toneMapShader.Gamma = (float)gamma;
            toneMapShader.Contrast = (float)contrast;
            toneMapShader.Saturation = (float)sat;

            lastExposure = exposure;
            lastGamma = gamma;
            lastContrast = contrast;
            lastSaturation = sat;
            isFirstUpdate = false;
        }

        return effectDescription.DrawDescription;
    }
}
