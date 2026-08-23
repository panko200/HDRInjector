using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HDRInjector;

/// <summary>
/// Retrieves the Windows Advanced Color SDR reference white for the monitor
/// containing the YMM4 preview window. Windows defines SDR white as a multiple
/// of 80 nits; for FP16/scRGB the corresponding linear RGB values are multiplied
/// by SDRWhiteNits / 80.
/// </summary>
internal static class SdrWhiteLevelHelper
{
    private const uint QdcOnlyActivePaths = 0x00000002;
    private const uint DisplayConfigDeviceInfoGetSourceName = 1;
    private const uint DisplayConfigDeviceInfoGetSdrWhiteLevel = 11;
    private const uint DisplayConfigPathActive = 0x00000001;

    private static readonly ConcurrentDictionary<nint, CacheEntry> Cache = new();

    private readonly record struct CacheEntry(float Scale, long Timestamp);

    public static float GetScaleForWindow(IntPtr hwnd)
    {
        Debug.WriteLine($"[HDRInjector][SDRWhite] GetScaleForWindow invoked hwnd=0x{hwnd.ToInt64():X}");
        if (hwnd == IntPtr.Zero)
        {
            Debug.WriteLine("[HDRInjector][SDRWhite] hwnd is zero; using scale=1.0");
            return 1.0f;
        }

        long now = Environment.TickCount64;
        if (Cache.TryGetValue(hwnd, out var cached) && now - cached.Timestamp < 1000)
            return cached.Scale;

        try
        {
            bool ok = TryGetScaleForWindow(hwnd, out float nits);
            float scale = ok ? nits / 80.0f : 1.0f;
            Cache[hwnd] = new CacheEntry(scale, now);
            if (ok)
                Debug.WriteLine($"[HDRInjector][SDRWhite] hwnd=0x{hwnd.ToInt64():X} SDRWhite={nits:0.##} nits, scale={scale:0.####}");
            else
                Debug.WriteLine($"[HDRInjector][SDRWhite] hwnd=0x{hwnd.ToInt64():X} SDR white target not found; using scale=1.0");
            return scale;
        }
        catch (Exception ex)
        {
            Cache[hwnd] = new CacheEntry(1.0f, now);
            Debug.WriteLine($"[HDRInjector][SDRWhite] Query failed; using 1.0. {ex.Message}");
            return 1.0f;
        }
    }

    private static bool TryGetScaleForWindow(IntPtr hwnd, out float nits)
    {
        nits = 80.0f;
        if (!GetMonitorInfoForWindow(hwnd, out string gdiDeviceName))
            return false;

        uint pathCount = 0;
        uint modeCount = 0;
        int result = GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out pathCount, out modeCount);
        if (result != 0)
            throw new Win32Exception(result, nameof(GetDisplayConfigBufferSizes));

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        result = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        if (result != 0)
            throw new Win32Exception(result, nameof(QueryDisplayConfig));

        for (int i = 0; i < pathCount; i++)
        {
            if ((paths[i].flags & DisplayConfigPathActive) == 0)
                continue;

            string sourceName = GetSourceName(paths[i].sourceInfo.adapterId, paths[i].sourceInfo.id);
            if (!string.Equals(sourceName, gdiDeviceName, StringComparison.OrdinalIgnoreCase))
                continue;

            var header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DisplayConfigDeviceInfoGetSdrWhiteLevel,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SDR_WHITE_LEVEL>(),
                adapterId = paths[i].targetInfo.adapterId,
                id = paths[i].targetInfo.id,
            };

            var info = new DISPLAYCONFIG_SDR_WHITE_LEVEL { header = header };
            result = DisplayConfigGetDeviceInfo(ref info);
            if (result != 0)
                throw new Win32Exception(result, nameof(DisplayConfigGetDeviceInfo));

            nits = info.SDRWhiteLevel / 1000.0f * 80.0f;
            return nits > 0.0f && float.IsFinite(nits);
        }

        return false;
    }

    private static string GetSourceName(LUID adapterId, uint sourceId)
    {
        var header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            type = DisplayConfigDeviceInfoGetSourceName,
            size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
            adapterId = adapterId,
            id = sourceId,
        };

        var info = new DISPLAYCONFIG_SOURCE_DEVICE_NAME { header = header };
        int result = DisplayConfigGetDeviceInfo(ref info);
        if (result != 0)
            throw new Win32Exception(result, nameof(DisplayConfigGetDeviceInfo));
        return info.viewGdiDeviceName.TrimEnd('\0');
    }

    private static bool GetMonitorInfoForWindow(IntPtr hwnd, out string deviceName)
    {
        deviceName = string.Empty;
        IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
            return false;

        var info = new MONITORINFOEX
        {
            cbSize = Marshal.SizeOf<MONITORINFOEX>(),
        };
        if (!GetMonitorInfo(monitor, ref info))
            return false;

        deviceName = info.szDevice.TrimEnd('\0');
        return !string.IsNullOrWhiteSpace(deviceName);
    }

    private const uint MonitorDefaultToNearest = 0x00000002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [In, Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [In, Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SDR_WHITE_LEVEL requestPacket);

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_SDR_WHITE_LEVEL
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint SDRWhiteLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public uint targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    private enum DISPLAYCONFIG_MODE_INFO_TYPE : uint
    {
        Source = 1,
        Target = 2,
        DesktopImage = 3,
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct DISPLAYCONFIG_MODE_INFO_UNION
    {
        // DISPLAYCONFIG_MODE_INFO is only used as a buffer by QueryDisplayConfig;
        // we do not inspect its union payload. Reserving the full native union size
        // keeps the containing struct layout correct.
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public DISPLAYCONFIG_MODE_INFO_TYPE infoType;
        public uint id;
        public LUID adapterId;
        public DISPLAYCONFIG_MODE_INFO_UNION modeInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_TARGET_MODE_INFO
    {
        public DISPLAYCONFIG_TARGET_MODE targetVideoSignalInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_TARGET_MODE
    {
        public DISPLAYCONFIG_RATIONAL targetVideoSignalInfoNumerator;
        public DISPLAYCONFIG_RATIONAL targetVideoSignalInfoDenominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_SOURCE_MODE_INFO
    {
        public uint width;
        public uint height;
        public uint pixelFormat;
        public POINTL position;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DESKTOP_IMAGE_INFO
    {
        public POINTL PathSourceSize;
        public RECTL DesktopImageRegion;
        public RECTL DesktopImageClip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTL
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECTL
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECTL rcMonitor;
        public RECTL rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }
}
