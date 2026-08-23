using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using HDRInjector.Models;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.Brush;

namespace HDRInjector.Brush;

[PluginOrder(10)]
public class HdrSolidColorBrushPlugin : IBrushPlugin, IPlugin
{
    public string Name => "HDR 単色 (32bit Float)";
    public string DefaultGroupName => "HDR / 高輝度";
    public int DefaultOrder => 10;
    public IBrushParameter CreateBrushParameter() => new HdrSolidColorBrushParameter();
}

public class HdrSolidColorBrushParameter : BrushParameterBase
{
    private Color color = Colors.White;

    [Display(Name = "ベースカラー", Description = "基準となる色を指定します", Order = 10)]
    [ColorPicker]
    public Color Color
    {
        get => color;
        set => Set(ref color, value);
    }

    [Display(Name = "露出 (EV)", Description = "HDR 露出ブースト値 (-5.0 EV 〜 +10.0 EV: +1EVごとに輝度が2倍になります)", Order = 20)]
    [AnimationSlider("F1", "EV", -5.0, 10.0)]
    public Animation Exposure { get; } = new Animation(0.0, -5.0, 10.0);

    [Display(Name = "輝度倍率 (%)", Description = "リニアな輝度倍率 (100% = 1.0x)", Order = 30)]
    [AnimationSlider("F0", "%", 0.0, 1000.0)]
    public Animation Intensity { get; } = new Animation(100.0, 0.0, 1000.0);

    public override IBrushSource CreateBrush(IGraphicsDevicesAndContext devices)
    {
        return new HdrSolidColorBrushSource(devices, this);
    }

    protected override IEnumerable<IAnimatable> GetAnimatables()
    {
        yield return Exposure;
        yield return Intensity;
    }
}

public class HdrSolidColorBrushSource : IBrushSource, IDisposable
{
    private readonly DisposeCollector disposer = new();
    private readonly HdrSolidColorBrushParameter parameter;
    private readonly ID2D1SolidColorBrush brush;
    private bool isFirst = true;
    private Color lastColor;
    private double lastEv;
    private double lastIntensity;
    private bool disposedValue;

    public ID2D1Brush Brush => brush;

    public HdrSolidColorBrushSource(IGraphicsDevicesAndContext devices, HdrSolidColorBrushParameter parameter)
    {
        this.parameter = parameter;
        brush = devices.DeviceContext.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f));
        disposer.Collect(brush);
    }

    public bool Update(TimelineItemSourceDescription desc)
    {
        int frame = desc.ItemPosition.Frame;
        int length = desc.ItemDuration.Frame;
        int fps = desc.FPS;

        Color c = parameter.Color;
        double ev = parameter.Exposure.GetValue(frame, length, fps);
        double intensity = parameter.Intensity.GetValue(frame, length, fps) / 100.0;

        if (isFirst || lastColor != c || Math.Abs(lastEv - ev) > 0.001 || Math.Abs(lastIntensity - intensity) > 0.001)
        {
            // レジストリから取得、またはパラメータの EV/Intensity を掛け合わせ
            var hdr = HdrColorRegistry.GetHdrColor(parameter, c);
            float totalEv = hdr.Exposure + (float)ev;
            float multiplier = MathF.Pow(2.0f, totalEv) * (float)intensity;

            float r = (c.R / 255.0f) * multiplier;
            float g = (c.G / 255.0f) * multiplier;
            float b = (c.B / 255.0f) * multiplier;
            float a = (c.A / 255.0f);

            brush.Color = new Color4(r, g, b, a);

            lastColor = c;
            lastEv = ev;
            lastIntensity = intensity;
            isFirst = false;
            return true;
        }

        return false;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                disposer.Dispose();
            }
            disposedValue = true;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
