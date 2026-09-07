using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace HDRInjector.Settings;

public sealed class HdrInjectorSettingsView : UserControl
{
    public HdrInjectorSettingsView()
    {
        var checkBox = new CheckBox
        {
            Content = "HDR機能を有効にする",
            Margin = new Thickness(0, 3, 0, 6)
        };
        checkBox.SetBinding(
            CheckBox.IsCheckedProperty,
            new Binding(nameof(HdrInjectorSettings.EnableHdr))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });

        Content = new StackPanel
        {
            Margin = new Thickness(6),
            Children =
            {
                checkBox,
                new TextBlock
                {
                    Text = "※この設定はYMM4の再起動後に反映されます。無効にすると、HDR用のグローバル描画・プレビュー処理を適用せず、通常のSDR描画で起動します。",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11,
                    Opacity = 0.7,
                    Margin = new Thickness(4, 0, 4, 4)
                }
            }
        };
    }
}
