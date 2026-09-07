using YukkuriMovieMaker.Plugin;

namespace HDRInjector.Settings;

/// <summary>
/// HDRInjector 全体のHDR機能設定。
/// デバイスコンテキストやSwapChainなどのグローバルな描画経路を切り替えるため、
/// 設定変更は次回のYMM4起動時に反映されます。
/// </summary>
public sealed class HdrInjectorSettings : SettingsBase<HdrInjectorSettings>
{
    private bool _enableHdr = true;

    public override SettingsCategory Category => SettingsCategory.None;

    public override string Name => "HDRInjector";

    public override bool HasSettingView => true;

    public override object? SettingView => new HdrInjectorSettingsView { DataContext = this };

    /// <summary>
    /// HDRInjectorのグローバルHDR描画・HDR動画入力機能を有効にします。
    /// 無効にすると、HDR関連のグローバルHarmonyパッチを適用せず、
    /// YMM4本来のSDR描画経路で起動します。
    /// </summary>
    public bool EnableHdr
    {
        get => _enableHdr;
        set => Set(ref _enableHdr, value);
    }

    public override void Initialize()
    {
    }
}
