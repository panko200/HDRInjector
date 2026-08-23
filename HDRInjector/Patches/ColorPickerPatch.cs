using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using HarmonyLib;
using HDRInjector.Controls;
using HDRInjector.Models;
using HDRInjector.Settings;
using YukkuriMovieMaker.Controls;

namespace HDRInjector.Patches;

/// <summary>
/// ColorPicker.OnApplyTemplate() に対する Harmony パッチ。
/// ポップアップ内に「■ 標準」「△ 三角形」「□ 四角形」の切り替えタブと、
/// 高品質 32bit Float / HDR カラーホイール UI を注入します。
/// </summary>
[HarmonyPatch(typeof(ColorPicker), "OnApplyTemplate")]
public static class ColorPickerPatch
{
    private const string InjectedTag = "HdrColorStudioTabBar";

    private static readonly ConditionalWeakTable<ColorPicker, WheelState> States = new();
    private static readonly List<WeakReference<WheelState>> LiveStates = new();
    private static bool settingsHooked;

    private static readonly MethodInfo? SliderValueChangedMethod =
        typeof(ColorPicker).GetMethod("Slider_ValueChanged", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? MainHSliderField =
        typeof(ColorPicker).GetField("mainHSlider", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? PopupField =
        typeof(ColorPicker).GetField("popup", BindingFlags.Instance | BindingFlags.NonPublic);

    private sealed class WheelState
    {
        public HdrColorWheel? TriangleWheel;
        public Border? TriangleContainer;
        public HdrColorWheel? SquareWheel;
        public Border? SquareContainer;
        public int ActiveTab;
    }

    private static void Postfix(ColorPicker __instance)
    {
        try
        {
            HookSettings();
            InjectWheelUi(__instance);
        }
        catch
        {
            // 安全のため例外は無視
        }
    }

    private static void HookSettings()
    {
        if (settingsHooked) return;
        settingsHooked = true;
        HdrColorPickerSettings.Default.PropertyChanged += OnSettingsPropertyChanged;
    }

    private static void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var app = Application.Current;
        if (app == null) return;

        void ApplyAll()
        {
            var settings = HdrColorPickerSettings.Default;
            for (var i = LiveStates.Count - 1; i >= 0; i--)
            {
                if (!LiveStates[i].TryGetTarget(out var state))
                {
                    LiveStates.RemoveAt(i);
                    continue;
                }
                ApplySettings(state, settings);
            }
        }

        if (app.Dispatcher.CheckAccess())
            ApplyAll();
        else
            app.Dispatcher.BeginInvoke((Action)ApplyAll);
    }

    private static void InjectWheelUi(ColorPicker picker)
    {
        if (PopupField?.GetValue(picker) is not Popup { Child: Grid shadowGrid })
            return;

        var innerGrid = FindInnerGrid(shadowGrid);
        if (innerGrid == null || IsAlreadyInjected(innerGrid))
            return;

        // 1. タブ行を最上部に挿入
        innerGrid.RowDefinitions.Insert(0, new RowDefinition { Height = GridLength.Auto });
        if (!double.IsNaN(innerGrid.Height))
        {
            innerGrid.Height += 24;
        }

        foreach (UIElement child in innerGrid.Children)
        {
            Grid.SetRow(child, Grid.GetRow(child) + 1);
        }

        // 2. HDR ホイール作成（三角形 & 四角形）
        var triangleWheel = CreateHdrWheel(picker, WheelMode.Triangle);
        var squareWheel = CreateHdrWheel(picker, WheelMode.Square);

        var triangleContainer = CreateWheelContainer(triangleWheel);
        var squareContainer = CreateWheelContainer(squareWheel);
        innerGrid.Children.Add(triangleContainer);
        innerGrid.Children.Add(squareContainer);

        var state = States.GetOrCreateValue(picker);
        state.TriangleWheel = triangleWheel;
        state.TriangleContainer = triangleContainer;
        state.SquareWheel = squareWheel;
        state.SquareContainer = squareContainer;
        state.ActiveTab = 0;

        LiveStates.Add(new WeakReference<WheelState>(state));
        ApplySettings(state, HdrColorPickerSettings.Default);

        // 3. タブボタン作成
        var tab0 = CreateTabButton("■ 標準", true);
        var tab1 = CreateTabButton("△ 三角形", false);
        var tab2 = CreateTabButton("□ 四角形", false);
        var tabs = new[] { tab0, tab1, tab2 };

        // 同期アクション
        Action syncHdrState = () =>
        {
            try
            {
                var currentHdr =
                    picker.DataContext is object dataContext && HdrColorRegistry.TryGetHdrColor(dataContext, out var dataContextHdr)
                        ? dataContextHdr
                        : HdrColorRegistry.GetHdrColor(picker, picker.Value);

                triangleWheel.Exposure = currentHdr.Exposure;
                squareWheel.Exposure = currentHdr.Exposure;
                triangleWheel.EnsureInitialized();
                squareWheel.EnsureInitialized();
            }
            catch { }
        };

        var popup = PopupField?.GetValue(picker) as Popup;
        if (popup != null)
        {
            popup.Opened += (s, e) => syncHdrState();
        }

        tab0.MouseLeftButtonDown += (s, e) =>
        {
            if (state.ActiveTab == 0) return;
            state.ActiveTab = 0;
            SetTabActive(tabs, 0);
            triangleContainer.Visibility = Visibility.Collapsed;
            squareContainer.Visibility = Visibility.Collapsed;
            e.Handled = true;
        };

        tab1.MouseLeftButtonDown += (s, e) =>
        {
            if (state.ActiveTab == 1) return;
            state.ActiveTab = 1;
            SetTabActive(tabs, 1);
            squareContainer.Visibility = Visibility.Collapsed;
            triangleContainer.Visibility = Visibility.Visible;
            syncHdrState();
            e.Handled = true;
        };

        tab2.MouseLeftButtonDown += (s, e) =>
        {
            if (state.ActiveTab == 2) return;
            state.ActiveTab = 2;
            SetTabActive(tabs, 2);
            triangleContainer.Visibility = Visibility.Collapsed;
            squareContainer.Visibility = Visibility.Visible;
            syncHdrState();
            e.Handled = true;
        };

        var tabBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Height = 22,
            Tag = InjectedTag,
            Margin = new Thickness(0, 0, 0, 2)
        };
        tabBar.Children.Add(tab0);
        tabBar.Children.Add(tab1);
        tabBar.Children.Add(tab2);

        Grid.SetRow(tabBar, 0);
        Grid.SetColumn(tabBar, 0);
        Grid.SetColumnSpan(tabBar, 3);
        innerGrid.Children.Add(tabBar);
    }

    private static Grid? FindInnerGrid(Grid shadowGrid)
    {
        foreach (UIElement child in shadowGrid.Children)
        {
            if (child is Grid grid && grid.ColumnDefinitions.Count >= 3)
                return grid;
        }
        return null;
    }

    private static bool IsAlreadyInjected(Grid innerGrid)
    {
        foreach (UIElement child in innerGrid.Children)
        {
            if (child is FrameworkElement { Tag: string tag } && tag == InjectedTag)
                return true;
        }
        return false;
    }

    private static HdrColorWheel CreateHdrWheel(ColorPicker picker, WheelMode mode)
    {
        var wheel = new HdrColorWheel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        wheel.SetMode(mode);

        wheel.SetBinding(HdrColorWheel.HProperty,
            new Binding(nameof(ColorPicker.H)) { Source = picker, Mode = BindingMode.TwoWay });
        wheel.SetBinding(HdrColorWheel.SProperty,
            new Binding(nameof(ColorPicker.S)) { Source = picker, Mode = BindingMode.TwoWay });
        wheel.SetBinding(HdrColorWheel.VProperty,
            new Binding(nameof(ColorPicker.V)) { Source = picker, Mode = BindingMode.TwoWay });

        wheel.OnHdrColorChanged = (hdrColor) =>
        {
            try
            {
                HdrColorRegistry.SetHdrColor(picker, hdrColor);
                if (picker.DataContext is object dataContext && !ReferenceEquals(dataContext, picker))
                {
                    HdrColorRegistry.SetHdrColor(dataContext, hdrColor);
                }

                if ((bool)picker.GetValue(ColorPicker.IsValueChangingProperty)) return;

                Color sdrColor = hdrColor.ToColor();
                if (picker.Value != sdrColor)
                {
                    picker.Value = sdrColor;
                }

                var mainHSlider = MainHSliderField?.GetValue(picker);
                if (mainHSlider != null && SliderValueChangedMethod != null)
                {
                    SliderValueChangedMethod.Invoke(picker, new object[] { mainHSlider, EventArgs.Empty });
                }
            }
            catch { }
        };

        return wheel;
    }

    private static Border CreateWheelContainer(UIElement wheel)
    {
        var container = new Border
        {
            Background = CreateThemeBackgroundBrush(),
            Child = wheel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = Visibility.Collapsed
        };

        Grid.SetRow(container, 1);
        Grid.SetRowSpan(container, 2);
        Grid.SetColumn(container, 0);
        Grid.SetColumnSpan(container, 3);
        Panel.SetZIndex(container, 20);
        return container;
    }

    private static System.Windows.Media.Brush CreateThemeBackgroundBrush()
    {
        if (TryFindResource("Color850") is Color themeColor)
            return new SolidColorBrush(themeColor);

        if (TryFindResource(SystemColors.ControlBrushKey) is SolidColorBrush solidBrush)
            return solidBrush;

        if (TryFindResource(SystemColors.ControlBrushKey) is System.Windows.Media.Brush controlBrush)
            return controlBrush;

        return SystemColors.ControlBrush;
    }


    private static object? TryFindResource(object key)
    {
        var app = Application.Current;
        if (app == null) return null;

        try
        {
            return app.TryFindResource(key);
        }
        catch
        {
            return null;
        }
    }

    private static void ApplySettings(WheelState state, HdrColorPickerSettings settings)
    {
        state.TriangleWheel?.ApplySettings(settings);
        state.SquareWheel?.ApplySettings(settings);
    }

    private static Border CreateTabButton(string text, bool isActive)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = SystemColors.ControlTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        return new Border
        {
            Background = isActive ? CreateThemeBackgroundBrush() : SystemColors.ControlBrush,
            BorderBrush = SystemColors.ActiveBorderBrush,
            BorderThickness = new Thickness(1, 1, 1, isActive ? 0 : 1),
            CornerRadius = new CornerRadius(3, 3, 0, 0),
            Padding = new Thickness(8, 1, 8, 1),
            Margin = new Thickness(0, 0, 1, 0),
            Cursor = Cursors.Hand,
            Child = textBlock
        };
    }

    private static void SetTabActive(Border[] tabs, int activeIndex)
    {
        for (var i = 0; i < tabs.Length; i++)
        {
            var active = i == activeIndex;
            tabs[i].Background = active ? CreateThemeBackgroundBrush() : SystemColors.ControlBrush;
            tabs[i].BorderThickness = new Thickness(1, 1, 1, active ? 0 : 1);
        }
    }
}
