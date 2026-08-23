using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using HDRInjector.Models;
using HDRInjector.Settings;

namespace HDRInjector.Controls;

/// <summary>
/// 32bit Float / HDR カラーホイール UI コントロール。
/// 高品質 HSV カラーホイール + Exposure (EV) スライダー + Float RGBA 直接入力 + 段階露出プレビュー + HDR プリセットを備えます。
/// </summary>
public class HdrColorWheel : Grid
{
    // ── Dependency Properties ──
    public static readonly DependencyProperty HProperty =
        DependencyProperty.Register(nameof(H), typeof(byte), typeof(HdrColorWheel),
            new FrameworkPropertyMetadata((byte)0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnHChanged));

    public static readonly DependencyProperty SProperty =
        DependencyProperty.Register(nameof(S), typeof(byte), typeof(HdrColorWheel),
            new FrameworkPropertyMetadata((byte)255, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSvChanged));

    public static readonly DependencyProperty VProperty =
        DependencyProperty.Register(nameof(V), typeof(byte), typeof(HdrColorWheel),
            new FrameworkPropertyMetadata((byte)255, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSvChanged));

    public static readonly DependencyProperty ExposureProperty =
        DependencyProperty.Register(nameof(Exposure), typeof(float), typeof(HdrColorWheel),
            new FrameworkPropertyMetadata(0.0f, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnExposureChanged));

    public byte H
    {
        get => (byte)GetValue(HProperty);
        set => SetValue(HProperty, value);
    }

    public byte S
    {
        get => (byte)GetValue(SProperty);
        set => SetValue(SProperty, value);
    }

    public byte V
    {
        get => (byte)GetValue(VProperty);
        set => SetValue(VProperty, value);
    }

    public float Exposure
    {
        get => (float)GetValue(ExposureProperty);
        set => SetValue(ExposureProperty, value);
    }

    public Action<FloatColor4>? OnHdrColorChanged { get; set; }

    // ── 内部要素 ──
    private readonly HsvColorWheel _hsvWheel;
    private Slider _exposureSlider = null!;
    private TextBox _exposureTextBox = null!;
    private TextBox _rFloatTextBox = null!;
    private TextBox _gFloatTextBox = null!;
    private TextBox _bFloatTextBox = null!;
    private TextBox _aFloatTextBox = null!;
    private readonly Border[] _previewBoxes = new Border[5]; // -2EV, -1EV, 0EV, +1EV, +2EV
    private readonly TextBlock[] _previewLabels = new TextBlock[5];

    // ── 状態 ──
    private double _hue;
    private double _sat = 1.0;
    private double _val = 1.0;
    private float _exposure = 0.0f;
    private float _alpha = 1.0f;
    private bool _updating;
    private bool _initialized;

    public HdrColorWheel()
    {
        Width = 420;
        Height = 260;
        Background = Brushes.Transparent;

        // メインレイアウト: 左右 2 カラム + 下部プリセット行
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(156) }); // 左: ホイール
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 右: HDR パラメータ

        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 下: プリセットバー

        // ── 左: HsvColorWheel ──
        _hsvWheel = new HsvColorWheel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _hsvWheel.SetBinding(HsvColorWheel.HProperty, new Binding(nameof(H)) { Source = this, Mode = BindingMode.TwoWay });
        _hsvWheel.SetBinding(HsvColorWheel.SProperty, new Binding(nameof(S)) { Source = this, Mode = BindingMode.TwoWay });
        _hsvWheel.SetBinding(HsvColorWheel.VProperty, new Binding(nameof(V)) { Source = this, Mode = BindingMode.TwoWay });
        _hsvWheel.OnColorChanged = (h, s, v) =>
        {
            if (_updating) return;
            _hue = h / 255.0 * 360.0;
            _sat = s / 255.0;
            _val = v / 255.0;
            UpdateFloatBoxes();
            UpdatePreviews();
            NotifyColorChanged();
        };

        Grid.SetColumn(_hsvWheel, 0);
        Grid.SetRow(_hsvWheel, 0);
        Children.Add(_hsvWheel);

        // ── 右: HDR パネル ──
        var rightPanel = BuildRightHdrPanel();
        Grid.SetColumn(rightPanel, 1);
        Grid.SetRow(rightPanel, 0);
        Children.Add(rightPanel);

        // ── 下: プリセットバー ──
        var presetBar = BuildPresetBar();
        Grid.SetColumn(presetBar, 0);
        Grid.SetColumnSpan(presetBar, 2);
        Grid.SetRow(presetBar, 1);
        Children.Add(presetBar);

        Loaded += (s, e) => EnsureInitialized();
    }

    private FrameworkElement BuildRightHdrPanel()
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(6, 4, 6, 4),
            VerticalAlignment = VerticalAlignment.Stretch
        };

        // 1. 露出 (EV / Intensity) ヘッダー & スライダー
        var evHeaderGrid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        evHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        evHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        evHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var evLabel = new TextBlock
        {
            Text = "露出 / 輝度 (Exposure / EV):",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = SystemColors.ControlTextBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(evLabel, 0);
        evHeaderGrid.Children.Add(evLabel);

        _exposureTextBox = new TextBox
        {
            Width = 54,
            Height = 19,
            FontSize = 11,
            Text = "+0.0 EV",
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(4, 0, 4, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _exposureTextBox.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ApplyExposureFromText();
                e.Handled = true;
            }
        };
        _exposureTextBox.LostFocus += (s, e) => ApplyExposureFromText();
        Grid.SetColumn(_exposureTextBox, 1);
        evHeaderGrid.Children.Add(_exposureTextBox);

        var evResetBtn = new Button
        {
            Content = "0 EV",
            FontSize = 10,
            Padding = new Thickness(4, 1, 4, 1),
            Height = 19,
            ToolTip = "露出をリセット (SDR 1.0x)"
        };
        evResetBtn.Click += (s, e) =>
        {
            Exposure = 0.0f;
            _exposure = 0.0f;
            _exposureSlider.Value = 0.0;
            UpdateUiFromHdr();
            NotifyColorChanged();
        };
        Grid.SetColumn(evResetBtn, 2);
        evHeaderGrid.Children.Add(evResetBtn);
        panel.Children.Add(evHeaderGrid);

        _exposureSlider = new Slider
        {
            Minimum = -5.0,
            Maximum = 10.0,
            Value = 0.0,
            SmallChange = 0.1,
            LargeChange = 1.0,
            Margin = new Thickness(0, 0, 0, 6)
        };
        _exposureSlider.ValueChanged += (s, e) =>
        {
            if (_updating) return;
            _exposure = (float)e.NewValue;
            Exposure = _exposure;
            _exposureTextBox.Text = $"{_exposure:+0.0;-0.0;+0.0} EV";
            UpdatePreviews();
            UpdateFloatBoxes();
            NotifyColorChanged();
        };
        panel.Children.Add(_exposureSlider);

        // 2. 32bit Float RGBA 入力欄
        var floatGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        for (int i = 0; i < 4; i++)
        {
            floatGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        (_rFloatTextBox, var rBox) = CreateFloatChannelInput("R (float)", Brushes.IndianRed);
        (_gFloatTextBox, var gBox) = CreateFloatChannelInput("G (float)", Brushes.SeaGreen);
        (_bFloatTextBox, var bBox) = CreateFloatChannelInput("B (float)", Brushes.DodgerBlue);
        (_aFloatTextBox, var aBox) = CreateFloatChannelInput("A", Brushes.Gray);

        Grid.SetColumn(rBox, 0);
        Grid.SetColumn(gBox, 1);
        Grid.SetColumn(bBox, 2);
        Grid.SetColumn(aBox, 3);

        floatGrid.Children.Add(rBox);
        floatGrid.Children.Add(gBox);
        floatGrid.Children.Add(bBox);
        floatGrid.Children.Add(aBox);
        panel.Children.Add(floatGrid);

        // 3. 段階露出プレビュー
        var previewTitle = new TextBlock
        {
            Text = "HDR 段階露出プレビュー (-2EV 〜 +2EV):",
            FontSize = 10,
            Foreground = SystemColors.GrayTextBrush,
            Margin = new Thickness(0, 0, 0, 2)
        };
        panel.Children.Add(previewTitle);

        var previewGrid = new Grid { Height = 28, Margin = new Thickness(0, 0, 0, 4) };
        for (int i = 0; i < 5; i++)
        {
            previewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var border = new Border
            {
                BorderBrush = SystemColors.ActiveBorderBrush,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Background = Brushes.Black,
                Cursor = Cursors.Hand
            };

            int offsetEv = i - 2; // -2, -1, 0, +1, +2
            border.ToolTip = $"{offsetEv:+0;-0;0} EV での SDR 表示（クリックで露出を設定）";
            border.MouseLeftButtonDown += (s, e) =>
            {
                _exposure += offsetEv;
                _exposure = Math.Clamp(_exposure, -5.0f, 10.0f);
                Exposure = _exposure;
                _exposureSlider.Value = _exposure;
                _exposureTextBox.Text = $"{_exposure:+0.0;-0.0;+0.0} EV";
                UpdateUiFromHdr();
                NotifyColorChanged();
            };

            var tb = new TextBlock
            {
                Text = $"{offsetEv:+0;-0;0}EV",
                FontSize = 9,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 1),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 2, ShadowDepth = 1, Color = Colors.Black }
            };

            border.Child = tb;
            _previewBoxes[i] = border;
            _previewLabels[i] = tb;

            Grid.SetColumn(border, i);
            previewGrid.Children.Add(border);
        }
        panel.Children.Add(previewGrid);

        return panel;
    }

    private (TextBox, FrameworkElement) CreateFloatChannelInput(string label, WpfBrush accent)
    {
        var stack = new StackPanel { Margin = new Thickness(2, 0, 2, 0) };
        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 10,
            Foreground = accent,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var tb = new TextBox
        {
            Height = 20,
            FontSize = 11,
            Text = "1.000",
            TextAlignment = TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        tb.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ApplyFloatRgbFromInputs();
                e.Handled = true;
            }
        };
        tb.LostFocus += (s, e) => ApplyFloatRgbFromInputs();

        stack.Children.Add(lbl);
        stack.Children.Add(tb);
        return (tb, stack);
    }

    private FrameworkElement BuildPresetBar()
    {
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(4, 2, 4, 4),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var label = new TextBlock
        {
            Text = "HDRプリセット:",
            FontSize = 10,
            Foreground = SystemColors.GrayTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        };
        bar.Children.Add(label);

        foreach (var preset in HdrColorRegistry.Presets)
        {
            var btn = new Button
            {
                Content = preset.Name,
                FontSize = 10,
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(2, 0, 2, 0),
                ToolTip = preset.Description
            };
            float ev = preset.Exposure;
            btn.Click += (s, e) =>
            {
                _exposure = ev;
                Exposure = ev;
                _exposureSlider.Value = ev;
                _exposureTextBox.Text = $"{_exposure:+0.0;-0.0;+0.0} EV";
                UpdateUiFromHdr();
                NotifyColorChanged();
            };
            bar.Children.Add(btn);
        }

        return bar;
    }

    private void ApplyExposureFromText()
    {
        string text = _exposureTextBox.Text.Replace("EV", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float ev))
        {
            ev = Math.Clamp(ev, -5.0f, 10.0f);
            _exposure = ev;
            Exposure = ev;
            _exposureSlider.Value = ev;
            _exposureTextBox.Text = $"{_exposure:+0.0;-0.0;+0.0} EV";
            UpdateUiFromHdr();
            NotifyColorChanged();
        }
    }

    private void ApplyFloatRgbFromInputs()
    {
        if (float.TryParse(_rFloatTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float r) &&
            float.TryParse(_gFloatTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float g) &&
            float.TryParse(_bFloatTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float b) &&
            float.TryParse(_aFloatTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float a))
        {
            var hdr = new FloatColor4(r, g, b, a, 0.0f);
            hdr.ToHsv(out float h, out float s, out float v, out _, out float ev);

            _hue = h;
            _sat = s;
            _val = v;
            _exposure = ev;
            _alpha = a;

            _updating = true;
            H = (byte)Math.Clamp((int)MathF.Round(h / 360.0f * 255.0f), 0, 255);
            S = (byte)Math.Clamp((int)MathF.Round(s * 255.0f), 0, 255);
            V = (byte)Math.Clamp((int)MathF.Round(v * 255.0f), 0, 255);
            Exposure = ev;
            _updating = false;

            UpdateUiFromHdr();
            NotifyColorChanged();
        }
    }

    public void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;
        _hsvWheel.EnsureInitialized();
        _hue = H / 255.0 * 360.0;
        _sat = S / 255.0;
        _val = V / 255.0;
        _exposure = Exposure;
        UpdateUiFromHdr();
    }

    public void SetMode(WheelMode mode)
    {
        _hsvWheel.SetMode(mode);
    }

    public void ApplySettings(HdrColorPickerSettings settings)
    {
        _hsvWheel.ApplySettings(settings);
    }

    private void UpdateUiFromHdr()
    {
        _updating = true;
        _exposureSlider.Value = _exposure;
        _exposureTextBox.Text = $"{_exposure:+0.0;-0.0;+0.0} EV";
        _updating = false;

        UpdateFloatBoxes();
        UpdatePreviews();
    }

    private void UpdateFloatBoxes()
    {
        var hdr = GetCurrentFloatColor();
        var (er, eg, eb) = hdr.GetEffectiveHdrRgb();

        _rFloatTextBox.Text = $"{er:F3}";
        _gFloatTextBox.Text = $"{eg:F3}";
        _bFloatTextBox.Text = $"{eb:F3}";
        _aFloatTextBox.Text = $"{hdr.A:F3}";
    }

    private void UpdatePreviews()
    {
        var hdr = GetCurrentFloatColor();
        for (int i = 0; i < 5; i++)
        {
            int offsetEv = i - 2;
            Color c = hdr.ToColorWithExposureOffset(offsetEv);
            _previewBoxes[i].Background = new SolidColorBrush(c);
        }
    }

    public FloatColor4 GetCurrentFloatColor()
    {
        return FloatColor4.FromHsv((float)_hue, (float)_sat, (float)_val, _alpha, _exposure);
    }

    private void NotifyColorChanged()
    {
        var hdr = GetCurrentFloatColor();
        HdrColorRegistry.CurrentHdrColor = hdr;
        OnHdrColorChanged?.Invoke(hdr);
    }

    private static void OnHChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not HdrColorWheel w) return;
        w._hue = (byte)e.NewValue / 255.0 * 360.0;
        if (!w._updating && w._initialized)
        {
            w.UpdateFloatBoxes();
            w.UpdatePreviews();
        }
    }

    private static void OnSvChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not HdrColorWheel w) return;
        w._sat = w.S / 255.0;
        w._val = w.V / 255.0;
        if (!w._updating && w._initialized)
        {
            w.UpdateFloatBoxes();
            w.UpdatePreviews();
        }
    }

    private static void OnExposureChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not HdrColorWheel w) return;
        w._exposure = (float)e.NewValue;
        if (!w._updating && w._initialized)
        {
            w.UpdateUiFromHdr();
        }
    }
}
