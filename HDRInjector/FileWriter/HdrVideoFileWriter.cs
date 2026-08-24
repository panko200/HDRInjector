using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using YukkuriMovieMaker.Plugin.FileWriter;
using YukkuriMovieMaker.Project;

namespace HDRInjector.FileWriter;

/// <summary>
/// HDR10 video writer prototype.
///
/// YMM4 gives us an FP16 scRGB/linear-ish render target through IVideoFileWriter3.
/// This writer performs the final export conversion on the CPU:
///   YMM4 pipeline pseudo-sRGB -> linear scRGB -> BT.2020 -> PQ -> 10-bit YCbCr
/// and feeds yuv420p10le to FFmpeg/NVENC. The encoded HEVC is then augmented with
/// HDR10 static metadata SEI messages before the final MP4 mux.
///
/// This is intentionally a correctness-first prototype. The conversion is CPU-bound;
/// a later version can move the conversion to D3D11/NVENC once the file format is verified.
/// </summary>
public sealed class HdrVideoFileWriter : IVideoFileWriter3, IVideoFileWriter2, IVideoFileWriter, IDisposable
{
    private static readonly List<string> LastEncoderProbeFailures = new();

    private readonly string outputPath;
    private readonly VideoInfo videoInfo;
    private readonly HdrVideoWriterSettings settings;

    private Process? videoProcess;
    private Stream? videoStream;
    private Process? audioProcess;
    private Stream? audioStream;
    private Process? muxProcess;

    private string videoTempPath;
    private string audioTempPath;
    private string videoError = "[HDRInjector][HDRWriter] FFmpeg video process error: ";
    private string audioError = "[HDRInjector][HDRWriter] FFmpeg audio process error: ";
    private string muxError = "[HDRInjector][HDRWriter] FFmpeg mux process error: ";

    // Performance diagnostics (Fix35): counters/timers only; export behavior is unchanged.
    private int perfFrameCount;
    private long perfCopyMapTicks;
    private long perfConvertTicks;
    private long perfUnmapTicks;
    private long perfWriteTicks;
    private long perfTotalTicks;
    private long perfPixelsProcessed;
    private long perfGpuCopyInputTicks;
    private long perfGpuConstantBufferTicks;
    private long perfGpuDispatchTicks;
    private long perfGpuUnbindTicks;
    private long perfGpuCopyToStagingTicks;
    private long perfGpuTotalTicks;
    private long perfGpuMapOutputTicks;
    private long perfGpuCpuPackTicks;
    private Stopwatch perfTotalWatch = Stopwatch.StartNew();

    private ID3D11Texture2D? staging;
    private HdrExportGpuConverter? gpuConverter;
    private byte[]? yuvBuffer;
    private float[]? chromaU;
    private float[]? chromaV;
    private double[]? gpuRowLuminanceSums;
    private float[]? gpuRowMaxLuminance;
    private int lastWidth;
    private int lastHeight;
    private bool started;
    private bool disposed;
    private string selectedVideoEncoder = string.Empty;

    // HDR10 Content Light Level Information (SEI), measured while converting frames.
    private float maxContentLightLevelNits;
    private float maxFrameAverageLightLevelNits;

    // scRGB's nominal reference white is 80 nits. The existing preview bridge
    // scales SDR content by SDRWhite/80 before presenting to the FP16/scRGB surface.
    private const float ScRgbReferenceWhiteNits = 80.0f;

    // BT.709/Rec.709 linear RGB -> BT.2020 linear RGB.
    private static readonly float[,] Rec709ToBt2020 =
    {
        { 0.6274040f, 0.3292820f, 0.0433136f },
        { 0.0690970f, 0.9195400f, 0.0113623f },
        { 0.0163910f, 0.0880130f, 0.8955950f },
    };

    // SMPTE ST 2084 constants.
    private const float PqM1 = 2610.0f / 16384.0f;
    private const float PqM2 = 2523.0f / 32.0f;
    private const float PqC1 = 3424.0f / 4096.0f;
    private const float PqC2 = 2413.0f / 128.0f;
    private const float PqC3 = 2392.0f / 128.0f;

    public VideoFileWriterSupportedStreams SupportedStreams =>
        VideoFileWriterSupportedStreams.Video | VideoFileWriterSupportedStreams.Audio;

    public bool IsGpuFrameSupported => true;

    public HdrVideoFileWriter(string outputPath, VideoInfo videoInfo, HdrVideoWriterSettings settings)
    {
        this.outputPath = outputPath;
        this.videoInfo = videoInfo;
        this.settings = settings;

        // Keep the encoded video as a raw Annex-B HEVC elementary stream until we
        // inject the HDR10 static SEI messages. This avoids asking the bundled FFmpeg
        // build to support HDR10 SEI options that it does not expose.
        videoTempPath = outputPath + ".hdrvideo.hevc";
        audioTempPath = outputPath + ".hdraudio.mp4.tmp";

        StartProcesses();
    }

    private static string FindFfmpeg()
    {
        // Prefer the same FFmpeg resource locator used by YMM4's own FFmpeg plugin.
        try
        {
            var assembly = Assembly.Load("YukkuriMovieMaker.Plugin.FileSource.FFmpeg");
            var locator = assembly.GetType("YukkuriMovieMaker.Plugin.FileSource.FFmpeg.FFmpegResourceLocator", throwOnError: false);
            var method = locator?.GetMethod("GetFFmpegExePath", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method != null && method.GetParameters().Length == 0 && method.Invoke(null, null) is string path && File.Exists(path))
                return path;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HDRInjector][HDRWriter] FFmpegResourceLocator lookup failed: {ex.Message}");
        }

        string[] candidates =
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg", "ffmpeg.exe"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "ffmpeg.exe"),
        };

        foreach (var candidate in candidates)
            if (File.Exists(candidate))
                return candidate;

        return "ffmpeg";
    }

    private void StartProcesses()
    {
        string ffmpeg = FindFfmpeg();
        Debug.WriteLine($"[HDRInjector][HDRWriter] FFmpeg={ffmpeg}");

        string size = $"{videoInfo.Width}x{videoInfo.Height}";
        string fps = Math.Max(1, videoInfo.FPS).ToString();

        // Video process: receive planar 10-bit YUV and encode HEVC Main10.
        // Prefer NVIDIA NVENC when available, otherwise use AMD AMF. This keeps HDR
        // output working on both NVIDIA and AMD systems while continuing to use the
        // same bundled YMM4 FFmpeg executable.
        var encoder = SelectHevcEncoder(ffmpeg, settings.Qp, videoInfo.Width, videoInfo.Height, videoInfo.FPS, out var encoderArgs);
        selectedVideoEncoder = encoder;
        string videoArgs =
            $"-y -f rawvideo -pix_fmt yuv420p10le -s {size} -r {fps} -i - " +
            encoderArgs +
            " -pix_fmt p010le " +
            "-color_range tv -colorspace bt2020nc -color_primaries bt2020 -color_trc smpte2084 " +
            // Do not run hevc_metadata inline. Some hardware encoders (notably AMF)
            // do not expose VPS/SPS extradata to the bitstream filter until after the
            // first encoded access unit, so the inline filter can fail before frame 0.
            // We apply the same VUI rewrite as a separate post-encode pass below.
            $"-an -f hevc \"{videoTempPath}\"";

        Debug.WriteLine($"[HDRInjector][HDRWriter] HEVC encoder selected: {encoder}");
        Debug.WriteLine($"[HDRInjector][HDRWriter] Video args: {videoArgs}");
        Debug.WriteLine("[HDRInjector][HDRWriter] Raw HEVC output enabled: VUI signaling + post-encode HDR10 SEI injection");
        videoProcess = StartProcess(ffmpeg, videoArgs, line => videoError += line + Environment.NewLine, out videoStream);

        // Audio process mirrors the YMM4 standard FFmpeg writer: f32le stereo -> AAC.
        string audioArgs =
            $"-y -f f32le -ar {Math.Max(1, videoInfo.Hz)} -ac 2 -i - " +
            "-vn -c:a aac -b:a 192k -f mp4 " +
            $"\"{audioTempPath}\"";

        audioProcess = StartProcess(ffmpeg, audioArgs, line => audioError += line + Environment.NewLine, out audioStream);

        started = videoProcess != null && audioProcess != null && videoStream != null && audioStream != null;
        if (!started)
            throw new InvalidOperationException("HDR動画出力用のFFmpegプロセスを起動できませんでした。");

        Debug.WriteLine("[HDRInjector][HDRWriter] HDR10 writer started: yuv420p10le input -> HEVC Main10 raw stream + BT.2020/PQ");
        Debug.WriteLine("[HDRInjector][HDRWriter] HEVC VUI: range=limited, primaries=9(bt2020), transfer=16(smpte2084), matrix=9(bt2020nc)");
    }


    private static string SelectHevcEncoder(string ffmpeg, int qp, int width, int height, int fps, out string encoderArgs)
    {
        lock (LastEncoderProbeFailures)
            LastEncoderProbeFailures.Clear();

        string available = GetAvailableHevcEncoders(ffmpeg);
        Debug.WriteLine($"[HDRInjector][HDRWriter] Available HEVC encoders: {available}");
        Debug.WriteLine($"[HDRInjector][HDRWriter] Encoder probe source: {width}x{height}@{fps}fps, QP={qp}");

        var candidates = new[]
        {
            (name: "hevc_nvenc", args: $"-c:v hevc_nvenc -profile:v main10 -preset p5 -rc constqp -qp {qp}"),
            (name: "hevc_amf", args: $"-c:v hevc_amf -profile:v main10 -quality balanced -rc cqp -qp_i {qp} -qp_p {qp}"),
            // Software fallback when available.
            (name: "libx265", args: $"-c:v libx265 -preset medium -qp {qp}")
        };

        foreach (var candidate in candidates)
        {
            if (!EncoderListed(available, candidate.name))
                continue;

            if (ProbeHevcEncoder(ffmpeg, candidate.name, candidate.args, width, height, fps))
            {
                encoderArgs = candidate.args;
                return candidate.name;
            }
        }

        encoderArgs = string.Empty;
        string probeDetails;
        lock (LastEncoderProbeFailures)
            probeDetails = LastEncoderProbeFailures.Count == 0
                ? "(probe details unavailable)"
                : string.Join(" | ", LastEncoderProbeFailures);

        throw new InvalidOperationException(
            "HDR動画出力に使用できるHEVCエンコーダーが見つかりませんでした。\n" +
            $"YMM4付属FFmpegのHEVCエンコーダー: {available}\n" +
            $"エンコーダープローブ結果: {probeDetails}\n" +
            "NVIDIA: NVENC / AMD: AMF / ソフトウェア: libx265 のいずれかが必要です。");
    }

    private static string GetAvailableHevcEncoders(string ffmpeg)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = "-hide_banner -encoders",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(psi);
            if (process == null)
                return "(FFmpeg process start failed)";

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(5000);

            var names = new List<string>();
            foreach (var line in (stdout + Environment.NewLine + stderr).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (!trimmed.Contains("hevc_", StringComparison.OrdinalIgnoreCase) &&
                    !trimmed.Contains("libx265", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Typical ffmpeg output: " V..... hevc_nvenc NVIDIA NVENC hevc encoder"
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    if (part.Equals("hevc_nvenc", StringComparison.OrdinalIgnoreCase) ||
                        part.Equals("hevc_amf", StringComparison.OrdinalIgnoreCase) ||
                        part.Equals("libx265", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!names.Exists(x => string.Equals(x, part, StringComparison.OrdinalIgnoreCase)))
                            names.Add(part);
                    }
                }
            }

            return names.Count > 0 ? string.Join(", ", names) : "(none detected)";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HDRInjector][HDRWriter] FFmpeg encoder enumeration failed: {ex.Message}");
            return "(enumeration failed)";
        }
    }

    private static bool EncoderListed(string available, string encoder)
    {
        return available.Contains(encoder, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ProbeHevcEncoder(string ffmpeg, string encoder, string encoderArgs, int width, int height, int fps)
    {
        if (string.Equals(encoder, "hevc_amf", StringComparison.OrdinalIgnoreCase))
        {
            // One build, multiple AMF probes. The important point is to use the actual
            // YMM4 output dimensions instead of a tiny 64x64 synthetic frame: AMF may
            // reject out-of-range dimensions even though the real 4K target is valid.
            var qp = ExtractQp(encoderArgs);
            var probes = new[]
            {
                (label: "AMF-P010-Main10-minimal", args: $"-c:v hevc_amf -profile:v main10"),
                (label: "AMF-P010-Main10-quality", args: $"-c:v hevc_amf -profile:v main10 -quality balanced"),
                (label: "AMF-P010-Main10-CQP", args: $"-c:v hevc_amf -profile:v main10 -quality balanced -rc cqp -qp_i {qp} -qp_p {qp}"),
                (label: "AMF-P010-Main10-CQP-HDR", args: $"-c:v hevc_amf -profile:v main10 -quality balanced -rc cqp -qp_i {qp} -qp_p {qp} -color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc")
            };

            bool anySuccess = false;
            lock (LastEncoderProbeFailures)
                LastEncoderProbeFailures.Add($"AMF probe target: {width}x{height}@{fps}fps, input=p010le");

            foreach (var probe in probes)
            {
                var result = RunEncoderProbe(ffmpeg, probe.label, probe.args, width, height, fps);
                if (result.ok)
                {
                    anySuccess = true;
                    Debug.WriteLine($"[HDRInjector][HDRWriter][AMF] {probe.label}: SUCCESS");
                    // Return the real production args that include QP; if the minimal or
                    // quality-only probe was the first successful one, keep probing so the
                    // full CQP/HDR condition can still be reported in one build.
                }
                else
                {
                    Debug.WriteLine($"[HDRInjector][HDRWriter][AMF] {probe.label}: FAIL {result.detail}");
                    lock (LastEncoderProbeFailures)
                        LastEncoderProbeFailures.Add($"{probe.label}: {result.detail}");
                }
            }

            // The production configuration is the CQP variant if it succeeds.
            var productionProbe = RunEncoderProbe(
                ffmpeg,
                "AMF-PRODUCTION-RECHECK",
                $"-c:v hevc_amf -profile:v main10 -quality balanced -rc cqp -qp_i {qp} -qp_p {qp}",
                width,
                height,
                fps);

            if (productionProbe.ok)
            {
                Debug.WriteLine("[HDRInjector][HDRWriter][AMF] AMF-PRODUCTION-RECHECK: SUCCESS");
                return true;
            }

            lock (LastEncoderProbeFailures)
                LastEncoderProbeFailures.Add($"AMF-PRODUCTION-RECHECK: {productionProbe.detail}");

            // Return failure here so the caller can continue to other available encoders.
            // The detailed probe log above is retained for the final exception.
            return false;
        }

        return RunEncoderProbe(ffmpeg, encoder, encoderArgs, width, height, fps).ok;
    }

    private static int ExtractQp(string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i] == "-qp_i" && int.TryParse(parts[i + 1], out var qp))
                return qp;
        }
        return 20;
    }

    private static (bool ok, string detail) RunEncoderProbe(string ffmpeg, string label, string encoderArgs, int width, int height, int fps)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-hide_banner -loglevel error -f lavfi -i " +
                            $"nullsrc=s={Math.Max(2, width)}x{Math.Max(2, height)}:r={Math.Max(1, fps)},format=p010le " +
                            $"-frames:v 1 -pix_fmt p010le {encoderArgs} -f null -",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };

            using var process = Process.Start(psi);
            if (process == null)
                return (false, "process start failed");

            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(10000);
            bool ok = process.HasExited && process.ExitCode == 0;
            string detail = string.IsNullOrWhiteSpace(stderr) ? $"exit={(process.HasExited ? process.ExitCode.ToString() : "timeout")}" : stderr.Trim();
            Debug.WriteLine($"[HDRInjector][HDRWriter][Probe] {label}: ok={ok}, exit={(process.HasExited ? process.ExitCode.ToString() : "timeout")}, detail={detail}");
            return (ok, detail);
        }
        catch (Exception ex)
        {
            return (false, $"exception={ex.Message}");
        }
    }

    private static Process? StartProcess(string fileName, string arguments, Action<string>? errorHandler, out Stream? stdin)
    {
        stdin = null;
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };

        var process = Process.Start(psi);
        if (process == null)
            return null;

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                errorHandler?.Invoke(e.Data);
                Debug.WriteLine(e.Data);
            }
        };
        process.BeginErrorReadLine();
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            Debug.WriteLine($"[HDRInjector][HDRWriter] FFmpeg process exited: PID={process.Id}, ExitCode={process.ExitCode}, Args={arguments}");
        };
        stdin = process.StandardInput.BaseStream;
        return process;
    }

    public void WriteVideo(ID2D1Bitmap1 frame)
    {
        if (disposed) throw new ObjectDisposedException(nameof(HdrVideoFileWriter));
        if (!started || videoStream == null) throw new InvalidOperationException("HDR動画Writerが開始されていません。");

        try
        {
            long before = Stopwatch.GetTimestamp();
            var breakdown = ConvertFrameToYuv420P10(frame);
            long afterConvert = Stopwatch.GetTimestamp();

            long beforeWrite = Stopwatch.GetTimestamp();
            videoStream.Write(yuvBuffer!, 0, yuvBuffer!.Length);
            long afterWrite = Stopwatch.GetTimestamp();

            perfFrameCount++;
            perfCopyMapTicks += breakdown.CopyMapTicks;
            perfConvertTicks += breakdown.ConvertTicks;
            perfUnmapTicks += breakdown.UnmapTicks;
            perfWriteTicks += afterWrite - beforeWrite;
            perfTotalTicks += afterWrite - before;
            perfPixelsProcessed += breakdown.Pixels;

            if (gpuConverter != null)
            {
                var gpuPerf = gpuConverter.LastPerf;
                perfGpuCopyInputTicks += gpuPerf.CopyInputTicks;
                perfGpuConstantBufferTicks += gpuPerf.ConstantBufferTicks;
                perfGpuDispatchTicks += gpuPerf.ShaderDispatchTicks;
                perfGpuUnbindTicks += gpuPerf.UnbindTicks;
                perfGpuCopyToStagingTicks += gpuPerf.CopyToStagingTicks;
                perfGpuTotalTicks += gpuPerf.TotalTicks;
                perfGpuMapOutputTicks += breakdown.CopyMapTicks;
                perfGpuCpuPackTicks += breakdown.CpuPackTicks;
            }

            if ((perfFrameCount % 30) == 0)
            {
                double scale = 1000.0 / Stopwatch.Frequency;
                Debug.WriteLine(
                    $"[HDRInjector][HDRPerf][Export] frames={perfFrameCount}, " +
                    $"avg/frame: copy+map={perfCopyMapTicks * scale / perfFrameCount:0.00}ms, " +
                    $"convert={perfConvertTicks * scale / perfFrameCount:0.00}ms, " +
                    $"unmap={perfUnmapTicks * scale / perfFrameCount:0.00}ms, " +
                    $"write={perfWriteTicks * scale / perfFrameCount:0.00}ms, " +
                    $"total={perfTotalTicks * scale / perfFrameCount:0.00}ms, " +
                    $"pixels/frame={perfPixelsProcessed / (double)perfFrameCount:0}, " +
                    $"GPU(copyIn={perfGpuCopyInputTicks * scale / perfFrameCount:0.00}ms, " +
                    $"CB={perfGpuConstantBufferTicks * scale / perfFrameCount:0.00}ms, " +
                    $"dispatch={perfGpuDispatchTicks * scale / perfFrameCount:0.00}ms, " +
                    $"unbind={perfGpuUnbindTicks * scale / perfFrameCount:0.00}ms, " +
                    $"copyStage={perfGpuCopyToStagingTicks * scale / perfFrameCount:0.00}ms, " +
                    $"gpuTotal={perfGpuTotalTicks * scale / perfFrameCount:0.00}ms, " +
                    $"map={perfGpuMapOutputTicks * scale / perfFrameCount:0.00}ms, " +
                    $"pack={perfGpuCpuPackTicks * scale / perfFrameCount:0.00}ms)");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HDRInjector][HDRWriter] WriteVideo failed: {ex}");
            throw new InvalidOperationException("HDRフレームの変換またはFFmpegへの送信に失敗しました。", ex);
        }
    }

    public void WriteVideo(byte[] frame)
    {
        // YMM4 uses the IVideoFileWriter3 GPU path because IsGpuFrameSupported=true.
        throw new NotSupportedException("HDR writer requires the GPU/FP16 frame path (IVideoFileWriter3).");
    }

    public void WriteAudio(float[] samples)
    {
        if (disposed) throw new ObjectDisposedException(nameof(HdrVideoFileWriter));
        if (!started || audioStream == null) return;

        try
        {
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(samples.AsSpan());
            audioStream.Write(bytes);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HDRInjector][HDRWriter] WriteAudio failed: {ex}");
            throw new InvalidOperationException("音声のFFmpegへの送信に失敗しました。", ex);
        }
    }

    private void EnsureStaging(ID3D11Texture2D source)
    {
        var desc = source.Description;
        if (staging != null && lastWidth == desc.Width && lastHeight == desc.Height && staging.Description.Format == desc.Format)
            return;

        staging?.Dispose();
        staging = null;

        var stagingDesc = new Texture2DDescription
        {
            Width = desc.Width,
            Height = desc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = desc.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };

        staging = source.Device.CreateTexture2D(stagingDesc);
        lastWidth = desc.Width;
        lastHeight = desc.Height;

        int yBytes = desc.Width * 2 * desc.Height;
        int chromaWidth = (desc.Width + 1) / 2;
        int chromaHeight = (desc.Height + 1) / 2;
        int chromaBytes = chromaWidth * chromaHeight * 2;
        yuvBuffer = new byte[yBytes + chromaBytes * 2];
        chromaU = new float[chromaWidth * chromaHeight];
        chromaV = new float[chromaWidth * chromaHeight];

        Debug.WriteLine($"[HDRInjector][HDRWriter] Staging created: {desc.Width}x{desc.Height} format={desc.Format}, output={yuvBuffer.Length} bytes/frame");
    }

    private FramePerfBreakdown ConvertFrameToYuv420P10Gpu(ID3D11Texture2D source, int width, int height)
    {
        gpuConverter ??= new HdrExportGpuConverter(source.Device);
        var sw = Stopwatch.StartNew();
        gpuConverter.Convert(source, HdrPreviewManager.CurrentSdrWhiteScale);
        sw.Stop();
        long gpuDispatchTicks = sw.ElapsedTicks;

        var mapStart = Stopwatch.GetTimestamp();
        var mapped = gpuConverter.MapOutput();
        var mapEnd = Stopwatch.GetTimestamp();

        long packStart = Stopwatch.GetTimestamp();
        try
        {
            if (yuvBuffer == null)
                throw new InvalidOperationException("HDR GPU output buffers are not initialized.");

            int chromaWidth = (width + 1) / 2;
            int chromaHeight = (height + 1) / 2;
            int yPlaneBytes = width * height * 2;
            int uPlaneOffset = yPlaneBytes;
            int vPlaneOffset = uPlaneOffset + chromaWidth * chromaHeight * 2;

            // GPU output is still float16 Y/Cb/Cr/luminance. The old implementation
            // scanned every pixel serially, accumulated chroma into large arrays, and
            // then made a second full-frame pass. Fix41 processes one 2x2 block per
            // iteration so Y is written once and Cb/Cr are averaged directly. The rows
            // are independent, so Parallel.For removes the remaining CPU bottleneck.
            if (gpuRowLuminanceSums == null || gpuRowLuminanceSums.Length != chromaHeight)
            {
                gpuRowLuminanceSums = new double[chromaHeight];
                gpuRowMaxLuminance = new float[chromaHeight];
            }

            unsafe
            {
                byte* basePtr = (byte*)mapped.DataPointer;
                double[] rowSums = gpuRowLuminanceSums;
                float[] rowMax = gpuRowMaxLuminance!;

                Parallel.For(0, chromaHeight, cy =>
                {
                    int y0 = cy * 2;
                    int y1 = Math.Min(y0 + 1, height - 1);
                    double rowLuminanceSum = 0.0;
                    float rowMaxLuminance = 0.0f;

                    for (int cx = 0; cx < chromaWidth; cx++)
                    {
                        int x0 = cx * 2;
                        int x1 = Math.Min(x0 + 1, width - 1);
                        int ci = cy * chromaWidth + cx;
                        float uSum = 0.0f;
                        float vSum = 0.0f;
                        int sampleCount = 0;

                        ProcessGpuPixel(basePtr, mapped.RowPitch, width, x0, y0,
                            yuvBuffer!, 0, ref uSum, ref vSum, ref rowLuminanceSum, ref rowMaxLuminance, ref sampleCount);

                        ProcessGpuPixel(basePtr, mapped.RowPitch, width, x1, y0,
                            yuvBuffer!, 0, ref uSum, ref vSum, ref rowLuminanceSum, ref rowMaxLuminance, ref sampleCount);

                        if (y1 != y0)
                        {
                            ProcessGpuPixel(basePtr, mapped.RowPitch, width, x0, y1,
                                yuvBuffer!, 0, ref uSum, ref vSum, ref rowLuminanceSum, ref rowMaxLuminance, ref sampleCount);
                            ProcessGpuPixel(basePtr, mapped.RowPitch, width, x1, y1,
                                yuvBuffer!, 0, ref uSum, ref vSum, ref rowLuminanceSum, ref rowMaxLuminance, ref sampleCount);
                        }

                        float inv = 1.0f / Math.Max(1, sampleCount);
                        WriteU16(yuvBuffer!, uPlaneOffset + ci * 2, LimitedC10(uSum * inv));
                        WriteU16(yuvBuffer!, vPlaneOffset + ci * 2, LimitedC10(vSum * inv));
                    }

                    rowSums[cy] = rowLuminanceSum;
                    rowMax[cy] = rowMaxLuminance;
                });

                double frameLuminanceSum = 0.0;
                float frameMaxLuminanceNits = 0.0f;
                for (int i = 0; i < chromaHeight; i++)
                {
                    frameLuminanceSum += rowSums[i];
                    if (rowMax[i] > frameMaxLuminanceNits)
                        frameMaxLuminanceNits = rowMax[i];
                }

                maxContentLightLevelNits = Math.Max(maxContentLightLevelNits, frameMaxLuminanceNits);
                maxFrameAverageLightLevelNits = Math.Max(
                    maxFrameAverageLightLevelNits,
                    (float)(frameLuminanceSum / Math.Max(1L, (long)width * height)));
            }
        }
        finally
        {
            gpuConverter.UnmapOutput();
        }
        long packEnd = Stopwatch.GetTimestamp();

        return new FramePerfBreakdown(
            mapEnd - mapStart,
            gpuDispatchTicks + (packEnd - packStart),
            0,
            (long)width * height,
            gpuDispatchTicks,
            packEnd - packStart);
    }

    private static unsafe void ProcessGpuPixel(
        byte* basePtr,
        int rowPitch,
        int width,
        int x,
        int y,
        byte[] output,
        int unusedOutputOffset,
        ref float uSum,
        ref float vSum,
        ref double luminanceSum,
        ref float maxLuminance,
        ref int sampleCount)
    {
        int clampedX = Math.Min(x, width - 1);
        ushort* row = (ushort*)(basePtr + y * rowPitch);
        int i = clampedX * 4;

        float yPrime = (float)BitConverter.UInt16BitsToHalf(row[i + 0]);
        float cb = (float)BitConverter.UInt16BitsToHalf(row[i + 1]);
        float cr = (float)BitConverter.UInt16BitsToHalf(row[i + 2]);
        float luminance = Math.Max(0.0f, (float)BitConverter.UInt16BitsToHalf(row[i + 3]) * 10000.0f);

        int yOffset = y * width * 2 + clampedX * 2;
        WriteU16(output, yOffset, LimitedY10(yPrime));

        uSum += cb;
        vSum += cr;
        luminanceSum += luminance;
        if (luminance > maxLuminance)
            maxLuminance = luminance;
        sampleCount++;
    }

    private FramePerfBreakdown ConvertFrameToYuv420P10(ID2D1Bitmap1 frame)
    {
        using var surface = frame.Surface;
        using var source = surface.QueryInterface<ID3D11Texture2D>();
        var desc = source.Description;
        Debug.WriteLine($"[HDRInjector][HDRWriter] Frame texture: {desc.Width}x{desc.Height} Format={desc.Format}");

        if (desc.Format != Format.R16G16B16A16_Float)
        {
            throw new InvalidOperationException($"HDR出力にはFP16フレームが必要ですが、実際のフォーマットは {desc.Format} です。");
        }

        EnsureStaging(source);

        try
        {
            var gpuBreakdown = ConvertFrameToYuv420P10Gpu(source, desc.Width, desc.Height);
            var gpuPerf = gpuConverter.LastPerf;
            double ms = 1000.0 / Stopwatch.Frequency;
            Debug.WriteLine(
                $"[HDRInjector][HDRPerf][GPU] frame conversion completed: " +
                $"copyInput={gpuPerf.CopyInputTicks * ms:0.00}ms, " +
                $"constantBuffer={gpuPerf.ConstantBufferTicks * ms:0.00}ms, " +
                $"dispatch+bind={gpuPerf.ShaderDispatchTicks * ms:0.00}ms, " +
                $"unbind={gpuPerf.UnbindTicks * ms:0.00}ms, " +
                $"copyToStaging={gpuPerf.CopyToStagingTicks * ms:0.00}ms, " +
                $"gpuTotal={gpuPerf.TotalTicks * ms:0.00}ms, " +
                $"MapOutput={gpuBreakdown.CopyMapTicks * ms:0.00}ms, " +
                $"CPU-pack={gpuBreakdown.CpuPackTicks * ms:0.00}ms, " +
                $"frameTotal={gpuBreakdown.CopyMapTicks * ms + gpuBreakdown.CpuPackTicks * ms + gpuPerf.TotalTicks * ms:0.00}ms");
            return gpuBreakdown;
        }
        catch (Exception gpuEx)
        {
            Debug.WriteLine($"[HDRInjector][HDRPerf][GPU] GPU conversion failed; falling back to CPU: {gpuEx}");
        }

        var context = source.Device.ImmediateContext;
        long copyMapStart = Stopwatch.GetTimestamp();
        context.CopyResource(staging!, source);
        var mapped = context.Map(staging!, 0);
        long copyMapEnd = Stopwatch.GetTimestamp();

        long convertStart = Stopwatch.GetTimestamp();
        try
        {
            var stats = ConvertMappedFp16(mapped.DataPointer, mapped.RowPitch, desc.Width, desc.Height, yuvBuffer!);
            maxContentLightLevelNits = Math.Max(maxContentLightLevelNits, stats.MaxContentLightLevelNits);
            maxFrameAverageLightLevelNits = Math.Max(maxFrameAverageLightLevelNits, stats.FrameAverageLightLevelNits);
        }
        finally
        {
            long convertEnd = Stopwatch.GetTimestamp();
            long unmapStart = Stopwatch.GetTimestamp();
            try
            {
                context.Unmap(staging!, 0);
            }
            finally
            {
                long unmapEnd = Stopwatch.GetTimestamp();
                _pendingPerfBreakdown = new FramePerfBreakdown(
                    copyMapEnd - copyMapStart,
                    convertEnd - convertStart,
                    unmapEnd - unmapStart,
                    (long)desc.Width * desc.Height,
                    0,
                    convertEnd - convertStart);
            }
        }

        return _pendingPerfBreakdown;
    }

    private FramePerfBreakdown _pendingPerfBreakdown;

    private readonly record struct FrameLightStats(float MaxContentLightLevelNits, float FrameAverageLightLevelNits);
    private readonly record struct FramePerfBreakdown(
        long CopyMapTicks,
        long ConvertTicks,
        long UnmapTicks,
        long Pixels,
        long GpuTicks = 0,
        long CpuPackTicks = 0);

    private FrameLightStats ConvertMappedFp16(IntPtr basePtr, int rowPitch, int width, int height, byte[] output)
    {
        int chromaWidth = (width + 1) / 2;
        int chromaHeight = (height + 1) / 2;
        int yPlaneBytes = width * height * 2;
        int uPlaneOffset = yPlaneBytes;
        int vPlaneOffset = uPlaneOffset + chromaWidth * chromaHeight * 2;

        // Fixed chroma accumulators are used for the 2x2 averaging pass.
        if (chromaU == null || chromaV == null || chromaU.Length != chromaWidth * chromaHeight)
        {
            chromaU = new float[chromaWidth * chromaHeight];
            chromaV = new float[chromaWidth * chromaHeight];
        }
        Array.Clear(chromaU);
        Array.Clear(chromaV);
        double frameLuminanceSum = 0.0;
        float frameMaxLuminanceNits = 0.0f;

        for (int y = 0; y < height; y++)
        {
            IntPtr row = IntPtr.Add(basePtr, y * rowPitch);
            int yRowOffset = y * width * 2;

            for (int x = 0; x < width; x++)
            {
                int pixelOffset = x * 8; // RGBA16F
                ushort rBits = unchecked((ushort)Marshal.ReadInt16(row, pixelOffset));
                ushort gBits = unchecked((ushort)Marshal.ReadInt16(row, pixelOffset + 2));
                ushort bBits = unchecked((ushort)Marshal.ReadInt16(row, pixelOffset + 4));
                ushort aBits = unchecked((ushort)Marshal.ReadInt16(row, pixelOffset + 6));

                float r = (float)BitConverter.UInt16BitsToHalf(rBits);
                float g = (float)BitConverter.UInt16BitsToHalf(gBits);
                float b = (float)BitConverter.UInt16BitsToHalf(bBits);
                float a = Math.Clamp((float)BitConverter.UInt16BitsToHalf(aBits), 0.0f, 1.0f);

                // The preview bridge un-premultiplies, sRGB-decodes, scales by SDR white,
                // and premultiplies again. Mirror that operation for export so the file
                // sees the same linear/scRGB signal the HDR display saw.
                if (a > 0.000001f)
                {
                    float invA = 1.0f / a;
                    r *= invA;
                    g *= invA;
                    b *= invA;
                }
                else
                {
                    r = g = b = 0.0f;
                }

                r = SrgbToLinearExport(r);
                g = SrgbToLinearExport(g);
                b = SrgbToLinearExport(b);

                // Current preview SDR reference white is stored globally by the existing
                // preview manager. Read it without a hard dependency on a gettable property
                // in the shader effect itself.
                float scale = HdrPreviewManager.CurrentSdrWhiteScale;
                r *= scale;
                g *= scale;
                b *= scale;

                // Linear scRGB is nominally 80 nits at 1.0.
                r = Math.Max(0.0f, r) * ScRgbReferenceWhiteNits;
                g = Math.Max(0.0f, g) * ScRgbReferenceWhiteNits;
                b = Math.Max(0.0f, b) * ScRgbReferenceWhiteNits;

                // Rec.709 -> BT.2020 gamut conversion, still linear.
                float r2020 = Rec709ToBt2020[0, 0] * r + Rec709ToBt2020[0, 1] * g + Rec709ToBt2020[0, 2] * b;
                float g2020 = Rec709ToBt2020[1, 0] * r + Rec709ToBt2020[1, 1] * g + Rec709ToBt2020[1, 2] * b;
                float b2020 = Rec709ToBt2020[2, 0] * r + Rec709ToBt2020[2, 1] * g + Rec709ToBt2020[2, 2] * b;

                r2020 = PqEncode(r2020);
                g2020 = PqEncode(g2020);
                b2020 = PqEncode(b2020);

                // BT.2020 non-constant-luminance Y'CbCr.
                float yPrime = 0.2627f * r2020 + 0.6780f * g2020 + 0.0593f * b2020;
                float cb = -0.13963f * r2020 - 0.36037f * g2020 + 0.5f * b2020;
                float cr = 0.5f * r2020 - 0.459786f * g2020 - 0.040214f * b2020;

                ushort y10 = LimitedY10(yPrime);
                WriteU16(output, yRowOffset + x * 2, y10);

                int ci = (y / 2) * chromaWidth + (x / 2);
                chromaU[ci] += cb;
                chromaV[ci] += cr;
            }
        }

        for (int cy = 0; cy < chromaHeight; cy++)
        {
            for (int cx = 0; cx < chromaWidth; cx++)
            {
                int ci = cy * chromaWidth + cx;
                int x0 = cx * 2;
                int y0 = cy * 2;
                int sampleCount = 1 + (x0 + 1 < width ? 1 : 0) + (y0 + 1 < height ? 1 : 0) + (x0 + 1 < width && y0 + 1 < height ? 1 : 0);
                float inv = 1.0f / sampleCount;
                ushort u10 = LimitedC10(chromaU[ci] * inv);
                ushort v10 = LimitedC10(chromaV[ci] * inv);
                WriteU16(output, uPlaneOffset + ci * 2, u10);
                WriteU16(output, vPlaneOffset + ci * 2, v10);
            }
        }

        float frameAverageLuminanceNits = (float)(frameLuminanceSum / Math.Max(1L, (long)width * height));
        return new FrameLightStats(frameMaxLuminanceNits, frameAverageLuminanceNits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SrgbToLinearExport(float x)
    {
        x = Math.Max(x, 0.0f);
        return x <= 0.04045f
            ? x / 12.92f
            : MathF.Pow((x + 0.055f) / 1.055f, 2.4f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float PqEncode(float nits)
    {
        float x = Math.Clamp(nits / 10000.0f, 0.0f, 1.0f);
        if (x <= 0.0f) return 0.0f;

        float p = MathF.Pow(x, PqM1);
        float e = MathF.Pow((PqC1 + PqC2 * p) / (1.0f + PqC3 * p), PqM2);
        return Math.Clamp(e, 0.0f, 1.0f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort LimitedY10(float y)
    {
        return (ushort)Math.Clamp(MathF.Round(64.0f + Math.Clamp(y, 0.0f, 1.0f) * 876.0f), 64.0f, 940.0f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort LimitedC10(float c)
    {
        return (ushort)Math.Clamp(MathF.Round(512.0f + Math.Clamp(c, -0.5f, 0.5f) * 896.0f), 64.0f, 960.0f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteU16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)(value >> 8);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        HDRInjector.Patches.HdrExportRenderTargetPatch.EndExport();

        try { videoStream?.Flush(); } catch { }
        try { audioStream?.Flush(); } catch { }
        try { videoStream?.Dispose(); } catch { }
        try { audioStream?.Dispose(); } catch { }
        videoStream = null;
        audioStream = null;

        try { videoProcess?.WaitForExit(15_000); } catch { }
        try { audioProcess?.WaitForExit(15_000); } catch { }

        // IMPORTANT: The real encoder process can fail after the startup probe succeeds.
        // Check its exit code and stderr BEFORE attempting HDR10 SEI injection, otherwise
        // an empty/broken HEVC temp file produces a misleading "HEVC stream is empty" error.
        if (videoProcess == null)
            throw new InvalidOperationException("HDR動画のHEVCエンコーダープロセスが開始されていません。");

        if (!videoProcess.HasExited)
        {
            throw new InvalidOperationException(
                "HDR動画のHEVCエンコーダーが終了しませんでした（15秒タイムアウト）。" +
                Environment.NewLine + videoError);
        }

        if (videoProcess.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"HDR動画のHEVCエンコードに失敗しました。exitCode={videoProcess.ExitCode}" +
                Environment.NewLine + videoError);
        }

        if (!File.Exists(videoTempPath))
        {
            throw new InvalidOperationException(
                "HDR動画のHEVC一時ファイルが生成されませんでした。" +
                Environment.NewLine + videoError);
        }

        var videoTempLength = new FileInfo(videoTempPath).Length;
        if (videoTempLength <= 0)
        {
            throw new InvalidOperationException(
                $"HDR動画のHEVC一時ファイルが空です。size={videoTempLength} bytes" +
                Environment.NewLine + videoError);
        }

        if (audioProcess != null && audioProcess.HasExited && audioProcess.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"HDR動画の音声エンコードに失敗しました。exitCode={audioProcess.ExitCode}" +
                Environment.NewLine + audioError);
        }

        try
        {
            string ffmpeg = FindFfmpeg();

            // Apply HEVC VUI color metadata AFTER encoding. This is intentionally a
            // separate pass: AMF may not provide VPS/SPS extradata early enough for
            // hevc_metadata to initialize when it is placed inline on the encoder.
            string vuiVideoTempPath = videoTempPath + ".vui.hevc";
            string vuiError = "[HDRInjector][HDRWriter] HEVC VUI post-process error: ";
            string vuiArgs =
                $"-y -hide_banner -f hevc -i \"{videoTempPath}\" -c:v copy " +
                "-bsf:v hevc_metadata=video_full_range_flag=0:colour_primaries=9:transfer_characteristics=16:matrix_coefficients=9 " +
                $"-f hevc \"{vuiVideoTempPath}\"";
            Debug.WriteLine($"[HDRInjector][HDRWriter] Post-encode HEVC VUI pass: encoder={selectedVideoEncoder}");
            using (var vuiProcess = StartProcessWithoutInput(ffmpeg, vuiArgs, line => vuiError += line + Environment.NewLine))
            {
                if (vuiProcess == null)
                    throw new InvalidOperationException("HEVC VUI後処理用FFmpegを起動できませんでした。" + Environment.NewLine + vuiError);
                vuiProcess.WaitForExit();
                if (vuiProcess.ExitCode != 0)
                    throw new InvalidOperationException($"HEVC VUI後処理に失敗しました。exitCode={vuiProcess.ExitCode}" + Environment.NewLine + vuiError);
            }

            if (!File.Exists(vuiVideoTempPath) || new FileInfo(vuiVideoTempPath).Length <= 0)
                throw new InvalidOperationException("HEVC VUI後処理で有効な一時ファイルが生成されませんでした。" + Environment.NewLine + vuiError);

            // Inject the HDR10 static SEI after the VUI rewrite.
            string metadataVideoTempPath = videoTempPath + ".hdr10.hevc";
            Hdr10SeiInjector.Inject(
                vuiVideoTempPath,
                metadataVideoTempPath,
                maxContentLightLevelNits,
                maxFrameAverageLightLevelNits,
                settings.MasteringMaxNits,
                settings.MasteringMinNits,
                line => Debug.WriteLine(line));

            string muxArgs =
                $"-y -i \"{metadataVideoTempPath}\" -i \"{audioTempPath}\" " +
                "-map 0:v:0 -map 1:a:0 -c:v copy -c:a copy -tag:v hvc1 -movflags +faststart " +
                $"\"{outputPath}\"";
            muxProcess = StartProcessWithoutInput(ffmpeg, muxArgs, line => muxError += line + Environment.NewLine);
            muxProcess?.WaitForExit();

            if (muxProcess != null && muxProcess.ExitCode != 0)
            {
                throw new InvalidOperationException("HDR動画の音声・映像muxに失敗しました。" + Environment.NewLine + muxError);
            }

            try { File.Delete(vuiVideoTempPath); } catch { }
            try { File.Delete(metadataVideoTempPath); } catch { }
            Debug.WriteLine($"[HDRInjector][HDRWriter] HDR10 metadata applied: MaxCLL={maxContentLightLevelNits:0.##} nits, MaxFALL={maxFrameAverageLightLevelNits:0.##} nits");
            Debug.WriteLine($"[HDRInjector][HDRWriter] Output completed: {outputPath}");
            if (perfFrameCount > 0)
            {
                double scale = 1000.0 / Stopwatch.Frequency;
                Debug.WriteLine(
                    $"[HDRInjector][HDRPerf][Export] FINAL frames={perfFrameCount}, " +
                    $"avg/frame copy+map={perfCopyMapTicks * scale / perfFrameCount:0.00}ms, " +
                    $"convert={perfConvertTicks * scale / perfFrameCount:0.00}ms, " +
                    $"unmap={perfUnmapTicks * scale / perfFrameCount:0.00}ms, " +
                    $"write={perfWriteTicks * scale / perfFrameCount:0.00}ms, " +
                    $"total={perfTotalTicks * scale / perfFrameCount:0.00}ms, " +
                    $"GPU(copyIn={perfGpuCopyInputTicks * scale / perfFrameCount:0.00}ms, " +
                    $"CB={perfGpuConstantBufferTicks * scale / perfFrameCount:0.00}ms, " +
                    $"dispatch={perfGpuDispatchTicks * scale / perfFrameCount:0.00}ms, " +
                    $"unbind={perfGpuUnbindTicks * scale / perfFrameCount:0.00}ms, " +
                    $"copyStage={perfGpuCopyToStagingTicks * scale / perfFrameCount:0.00}ms, " +
                    $"gpuTotal={perfGpuTotalTicks * scale / perfFrameCount:0.00}ms, " +
                    $"map={perfGpuMapOutputTicks * scale / perfFrameCount:0.00}ms, " +
                    $"pack={perfGpuCpuPackTicks * scale / perfFrameCount:0.00}ms)");
            }
        }
        finally
        {
            try { videoProcess?.Dispose(); } catch { }
            try { audioProcess?.Dispose(); } catch { }
            try { muxProcess?.Dispose(); } catch { }
            try { gpuConverter?.Dispose(); } catch { }
        try { staging?.Dispose(); } catch { }
            try { if (File.Exists(videoTempPath)) File.Delete(videoTempPath); } catch { }
            try { if (File.Exists(videoTempPath + ".vui.hevc")) File.Delete(videoTempPath + ".vui.hevc"); } catch { }
            try { if (File.Exists(videoTempPath + ".hdr10.hevc")) File.Delete(videoTempPath + ".hdr10.hevc"); } catch { }
            try { if (File.Exists(audioTempPath)) File.Delete(audioTempPath); } catch { }
        }
    }

    private static Process? StartProcessWithoutInput(string fileName, string arguments, Action<string>? errorHandler)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        var process = Process.Start(psi);
        if (process == null) return null;
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) errorHandler?.Invoke(e.Data);
        };
        process.BeginErrorReadLine();
        return process;
    }
}
