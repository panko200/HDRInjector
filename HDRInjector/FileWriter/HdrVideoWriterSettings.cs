using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Settings;

namespace HDRInjector.FileWriter;

/// <summary>
/// HDR10 writer settings persisted through YMM4's SettingsBase system.
/// </summary>
public sealed class HdrVideoWriterSettings : SettingsBase<HdrVideoWriterSettings>
{
    public override SettingsCategory Category => SettingsCategory.None;

    public override string Name => "HDR10 出力設定";

    // The actual settings view is supplied by the video writer plugin because
    // YMM4 invokes it in the export dialog with VideoInfo/length context.
    public override bool HasSettingView => false;

    public override object? SettingView => null;

    public override void Initialize()
    {
        // No migration is required yet. SettingsBase calls this when loading
        // the persisted settings, so keeping this override explicit satisfies
        // the abstract contract and leaves current values unchanged.
    }

    private string nvencPreset = "p5";
    private int qp = 20;
    private double masteringMaxNits = 1000.0;
    private double masteringMinNits = 0.0001;

    /// <summary>
    /// Compatibility alias so the existing writer/plugin code can keep using Current.
    /// This points at YMM4's persisted Default instance rather than a transient object.
    /// </summary>
    public static HdrVideoWriterSettings Current => SettingsBase<HdrVideoWriterSettings>.Default;

    public string NvencPreset
    {
        get => nvencPreset;
        set => Set(ref nvencPreset, value, nameof(NvencPreset));
    }

    public int Qp
    {
        get => qp;
        set => Set(ref qp, Math.Clamp(value, 10, 40), nameof(Qp));
    }

    public double MasteringMaxNits
    {
        get => masteringMaxNits;
        set => Set(ref masteringMaxNits, Math.Clamp(value, 1.0, 10000.0), nameof(MasteringMaxNits));
    }

    public double MasteringMinNits
    {
        get => masteringMinNits;
        set => Set(ref masteringMinNits, Math.Clamp(value, 0.00001, 10.0), nameof(MasteringMinNits));
    }

    private static readonly IReadOnlyDictionary<string, (string NvencPreset, int Qp, string Description)> QualityPresets =
        new Dictionary<string, (string NvencPreset, int Qp, string Description)>
        {
            ["最高"] = ("p7", 16, "最高画質寄り。処理時間とファイルサイズが大きくなります。"),
            ["高"] = ("p6", 20, "高画質と速度のバランスを重視します。"),
            ["標準"] = ("p5", 24, "画質・速度・ファイルサイズのバランスを重視します。"),
            ["低"] = ("p4", 30, "ファイルサイズと速度を優先し、画質は低めです。"),
        };

    public static FrameworkElement CreateView()
    {
        var panel = new StackPanel { Margin = new Thickness(12) };
        AddHeader(panel, "HDR10 出力設定");

        AddDescription(panel,
            "HEVC Main10 / BT.2020 / PQ (ST 2084) で出力します。\n" +
            "HDR10メタデータには、実測したMaxCLL / MaxFALLを自動で埋め込みます。\n" +
            "品質設定のQPは数値が小さいほど高画質・高ビットレートになります。");

        var qualityPreset = new ComboBox
        {
            MinWidth = 220,
            Margin = new Thickness(0, 4, 0, 2),
        };
        foreach (var name in new[] { "最高", "高", "標準", "低", "カスタム" })
            qualityPreset.Items.Add(name);

        var presetDescription = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = SystemColors.GrayTextBrush,
            Margin = new Thickness(0, 0, 0, 10),
        };

        var initialPreset = FindMatchingPreset(Current.NvencPreset, Current.Qp) ?? "カスタム";
        qualityPreset.SelectedItem = initialPreset;
        presetDescription.Text = GetPresetDescription(initialPreset);

        var nvencPreset = new ComboBox { MinWidth = 160, Margin = new Thickness(0, 4, 0, 10) };
        foreach (var p in new[] { "p1", "p2", "p3", "p4", "p5", "p6", "p7" })
            nvencPreset.Items.Add(p);
        nvencPreset.SelectedItem = Current.NvencPreset;

        var qp = new Slider
        {
            Minimum = 10,
            Maximum = 40,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            Value = Current.Qp,
            Width = 220,
            Margin = new Thickness(0, 4, 0, 2)
        };
        var qpValue = new TextBlock { Text = $"QP {Current.Qp}", Margin = new Thickness(4, 0, 0, 10) };

        var qualityPresetChangeInProgress = false;

        qualityPreset.SelectionChanged += (_, _) =>
        {
            if (qualityPresetChangeInProgress || qualityPreset.SelectedItem is not string value)
                return;

            qualityPresetChangeInProgress = true;
            try
            {
                if (QualityPresets.TryGetValue(value, out var preset))
                {
                    Current.NvencPreset = preset.NvencPreset;
                    Current.Qp = preset.Qp;
                    nvencPreset.SelectedItem = preset.NvencPreset;
                    qp.Value = preset.Qp;
                    qpValue.Text = $"QP {Current.Qp}";
                }

                presetDescription.Text = GetPresetDescription(value);
                nvencPreset.IsEnabled = value == "カスタム";
                qp.IsEnabled = value == "カスタム";
            }
            finally
            {
                qualityPresetChangeInProgress = false;
            }
        };

        nvencPreset.SelectionChanged += (_, _) =>
        {
            if (nvencPreset.SelectedItem is not string value)
                return;
            Current.NvencPreset = value;
            UpdatePresetSelection(qualityPreset, presetDescription, value, Current.Qp);
        };

        qp.ValueChanged += (_, _) =>
        {
            Current.Qp = (int)Math.Round(qp.Value);
            qpValue.Text = $"QP {Current.Qp}";
            if (!qualityPresetChangeInProgress)
                UpdatePresetSelection(qualityPreset, presetDescription, Current.NvencPreset, Current.Qp);
        };

        AddLabeled(panel, "画質プリセット", qualityPreset);
        panel.Children.Add(presetDescription);

        AddLabeled(panel, "ハードウェアエンコーダー品質", nvencPreset);
        AddLabeled(panel, "画質 (QP / CQ)", qp);

        var qpGuide = new Grid { Width = 220, Margin = new Thickness(0, 0, 0, 2) };
        qpGuide.ColumnDefinitions.Add(new ColumnDefinition());
        qpGuide.ColumnDefinitions.Add(new ColumnDefinition());
        var best = new TextBlock { Text = "高画質", HorizontalAlignment = HorizontalAlignment.Left };
        var compact = new TextBlock { Text = "高圧縮", HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(best, 0);
        Grid.SetColumn(compact, 1);
        qpGuide.Children.Add(best);
        qpGuide.Children.Add(compact);
        panel.Children.Add(qpGuide);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { new TextBlock { Text = "現在の設定: ", VerticalAlignment = VerticalAlignment.Center }, qpValue }
        });

        var maxNits = new TextBox { Text = Current.MasteringMaxNits.ToString("0.####"), Margin = new Thickness(0, 4, 0, 10) };
        maxNits.LostFocus += (_, _) =>
        {
            if (double.TryParse(maxNits.Text, out var value) && value > 0)
                Current.MasteringMaxNits = value;
            maxNits.Text = Current.MasteringMaxNits.ToString("0.####");
        };
        AddLabeled(panel, "Mastering Display 最大輝度 (nit)", maxNits);

        var minNits = new TextBox { Text = Current.MasteringMinNits.ToString("0.####"), Margin = new Thickness(0, 4, 0, 10) };
        minNits.LostFocus += (_, _) =>
        {
            if (double.TryParse(minNits.Text, out var value) && value >= 0)
                Current.MasteringMinNits = value;
            minNits.Text = Current.MasteringMinNits.ToString("0.####");
        };
        AddLabeled(panel, "Mastering Display 最小輝度 (nit)", minNits);

        var info = new TextBlock
        {
            Text = "MaxCLL / MaxFALL: 書き出し中にフレームから自動計測\n色域: BT.2020 / 白色点: D65",
            TextWrapping = TextWrapping.Wrap,
            Foreground = SystemColors.GrayTextBrush,
            Margin = new Thickness(0, 2, 0, 0)
        };
        panel.Children.Add(info);

        nvencPreset.IsEnabled = initialPreset == "カスタム";
        qp.IsEnabled = initialPreset == "カスタム";

        return panel;
    }

    private static void UpdatePresetSelection(ComboBox qualityPreset, TextBlock description, string nvencPreset, int qp)
    {
        var match = FindMatchingPreset(nvencPreset, qp);
        qualityPreset.SelectedItem = match ?? "カスタム";
        description.Text = GetPresetDescription(qualityPreset.SelectedItem as string ?? "カスタム");
    }

    private static string? FindMatchingPreset(string nvencPreset, int qp)
    {
        foreach (var pair in QualityPresets)
        {
            if (pair.Value.NvencPreset == nvencPreset && pair.Value.Qp == qp)
                return pair.Key;
        }
        return null;
    }

    private static string GetPresetDescription(string name)
    {
        if (QualityPresets.TryGetValue(name, out var preset))
            return $"{name}: {preset.Description} (NVENC {preset.NvencPreset}, QP {preset.Qp})";
        return "カスタム: NVENCプリセットとQPを個別に設定します。";
    }

    private static void AddHeader(Panel panel, string text) =>
        panel.Children.Add(new TextBlock { Text = text, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });

    private static void AddDescription(Panel panel, string text) =>
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });

    private static void AddLabeled(Panel panel, string label, UIElement control)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 2, 0, 0) });
        panel.Children.Add(control);
    }
}
