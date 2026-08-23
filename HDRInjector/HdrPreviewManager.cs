using System;

namespace HDRInjector;

public static class HdrPreviewManager
{
    /// <summary>Current SDR reference white scale (SDR white nits / 80).</summary>
    public static float CurrentSdrWhiteScale { get; private set; } = 1.0f;

    public static void SetSdrWhiteScale(float scale)
    {
        CurrentSdrWhiteScale = Math.Max(0.01f, scale);
    }
}

