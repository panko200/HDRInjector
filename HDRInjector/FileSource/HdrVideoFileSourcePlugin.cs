using System;
using System.Diagnostics;
using System.IO;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.FileSource;

namespace HDRInjector.FileSource;

/// <summary>
/// HDR 動画 (HDR10 PQ / HLG / BT.2020) を検出し、HdrVideoFileSource を作成するプラグイン。
/// FFprobe を使用して動画の HDR メタデータを確認する。
/// </summary>
public class HdrVideoFileSourcePlugin : IVideoFileSourcePlugin
{
    public string Name => "HDR 動画読み込み (10bit HDR10 / HLG / ProRes)";

    public IVideoFileSource? CreateVideoFileSource(IGraphicsDevicesAndContext devices, string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return null;

        // HDR メタデータを検出
        var hdrInfo = DetectHdrMetadata(filePath);
        if (hdrInfo == null)
            return null;

        return new HdrVideoFileSource(devices, filePath, hdrInfo.Value);
    }

    /// <summary>
    /// FFprobe を使用して動画の HDR メタデータを検出する。
    /// HDR10 (PQ/ST2084) または HLG の場合は HDR 動画として認識する。
    /// </summary>
    public static HdrInfo? DetectHdrMetadata(string filePath)
    {
        try
        {
            string ffprobePath = FindFfprobe();
            if (ffprobePath == null) return null;

            var startInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = $"-v quiet -print_format json -show_streams -show_format \"{filePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process == null) return null;

            string json = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            if (process.ExitCode != 0)
            {
                Debug.WriteLine($"[HDRInjector][HdrPlugin] FFprobe exit code: {process.ExitCode}");
                return null;
            }

            // HDR10 (PQ/ST2084) 検出
            if (json.Contains("smpte2084") || json.Contains("ST2084"))
            {
                Debug.WriteLine($"[HDRInjector][HdrPlugin] HDR10 (PQ/ST2084) detected");
                return new HdrInfo
                {
                    TransferFunction = HdrTransferFunction.Pq,
                    ColorSpace = HdrColorSpace.Bt2020Nc,
                };
            }

            // HLG 検出
            if (json.Contains("arib-std-b67") || json.Contains("HLG"))
            {
                Debug.WriteLine($"[HDRInjector][HdrPlugin] HLG (ARIB STD-B67) detected");
                return new HdrInfo
                {
                    TransferFunction = HdrTransferFunction.Hlg,
                    ColorSpace = HdrColorSpace.Bt2020Nc,
                };
            }

            // BT.2020 で PQ/HLG 以外の場合（例: 10bit SDR）
            // TransferCharacteristics と ColorPrimaries を抽出してログ出力
            string transfer = ExtractJsonString(json, "\"color_transfer\"");
            string primaries = ExtractJsonString(json, "\"color_primaries\"");
            string colorSpace = ExtractJsonString(json, "\"color_space\"");
            Debug.WriteLine($"[HDRInjector][HdrPlugin] SDR video: transfer={transfer}, primaries={primaries}, colorSpace={colorSpace}");
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindFfprobe()
    {
        // YMM4 付属の FFprobe を探す
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string[] candidates = new[]
        {
            Path.Combine(appDir, "ffprobe.exe"),
            Path.Combine(appDir, "ffmpeg", "ffprobe.exe"),
            Path.Combine(appDir, "tools", "ffprobe.exe"),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path)) return path;
        }

        // PATH から探す
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "ffprobe",
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(startInfo);
            if (process != null)
            {
                process.WaitForExit(2000);
                if (process.ExitCode == 0) return "ffprobe";
            }
        }
        catch { }

        return null;
    }

    private static string ExtractJsonString(string json, string key)
    {
        int idx = json.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return "(not found)";
        idx = json.IndexOf(':', idx) + 1;
        while (idx < json.Length && (json[idx] == ' ' || json[idx] == '"')) idx++;
        int start = idx;
        while (idx < json.Length && json[idx] != '"' && json[idx] != ',' && json[idx] != '}') idx++;
        return json[start..idx].Trim();
    }
}

/// <summary>
/// HDR の伝達関数を表す。
/// </summary>
public enum HdrTransferFunction
{
    Unknown = 0,
    Pq,     // SMPTE ST 2084 (PQ) - HDR10
    Hlg,    // ARIB STD-B67 (HLG)
}

/// <summary>
/// HDR の色空間を表す。
/// </summary>
public enum HdrColorSpace
{
    Unknown = 0,
    Bt2020Nc,   // BT.2020 Non-constant luminance
    Bt2020C,    // BT.2020 Constant luminance
}

/// <summary>
/// HDR 動画のメタデータ情報。
/// </summary>
public struct HdrInfo
{
    public HdrTransferFunction TransferFunction;
    public HdrColorSpace ColorSpace;
}
