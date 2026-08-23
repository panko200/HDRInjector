using YukkuriMovieMaker.Plugin;

namespace HDRInjector.Settings;

public class HdrColorPickerSettings : SettingsBase<HdrColorPickerSettings>
{
    private bool _triangleTrackingRotationEnabled = true;
    private bool _squareTrackingRotationEnabled = true;
    private bool _triangleFixedAngleEnabled;
    private bool _squareFixedAngleEnabled;
    private double _triangleFixedAngle;
    private double _squareFixedAngle;

    public override SettingsCategory Category => SettingsCategory.None;

    public override string Name => "HDR ColorPicker";

    public override bool HasSettingView => true;

    public override object? SettingView => new HdrColorPickerSettingsView { DataContext = this };

    public bool TriangleTrackingRotationEnabled
    {
        get => _triangleTrackingRotationEnabled;
        set
        {
            if (Set(ref _triangleTrackingRotationEnabled, value) && value)
            {
                TriangleFixedAngleEnabled = false;
            }
        }
    }

    public bool SquareTrackingRotationEnabled
    {
        get => _squareTrackingRotationEnabled;
        set
        {
            if (Set(ref _squareTrackingRotationEnabled, value) && value)
            {
                SquareFixedAngleEnabled = false;
            }
        }
    }

    public bool TriangleFixedAngleEnabled
    {
        get => _triangleFixedAngleEnabled;
        set
        {
            if (Set(ref _triangleFixedAngleEnabled, value) && value)
            {
                TriangleTrackingRotationEnabled = false;
            }
        }
    }

    public bool SquareFixedAngleEnabled
    {
        get => _squareFixedAngleEnabled;
        set
        {
            if (Set(ref _squareFixedAngleEnabled, value) && value)
            {
                SquareTrackingRotationEnabled = false;
            }
        }
    }

    public double TriangleFixedAngle
    {
        get => _triangleFixedAngle;
        set => Set(ref _triangleFixedAngle, NormalizeAngle(value));
    }

    public double SquareFixedAngle
    {
        get => _squareFixedAngle;
        set => Set(ref _squareFixedAngle, NormalizeAngle(value));
    }

    public override void Initialize()
    {
    }

    private static double NormalizeAngle(double angle)
    {
        angle %= 360.0;
        if (angle < 0)
            angle += 360.0;
        return angle;
    }
}
