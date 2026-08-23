using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Threading;
using SharpGen.Runtime;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using HDRInjector.Effects;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.FileSource;

namespace HDRInjector.FileSource;

/// <summary>
/// 10bit HDR 動画 (HDR10 PQ / HLG / BT.2020) を FFmpeg でデコードし、
/// 16bit Float HDR テクスチャ (Format.R16G16B16A16_Float) として出力するビデオファイルソース。
/// </summary>
public class HdrVideoFileSource : IVideoFileSource, IDisposable
{
    private readonly IGraphicsDevicesAndContext devices;
    private readonly string filePath;
    private readonly HdrInfo hdrInfo;
    private readonly DisposeCollector disposer = new();
    private readonly bool ownsDevicesContext;
    private static int nextSourceId;
    private static int liveSourceCount;
    private readonly int sourceId;

    private ID2D1Bitmap? outputBitmap;
    private readonly Queue<ID2D1Bitmap1> retiredGpuBitmaps = new();

    private void RetireGpuBitmap(ID2D1Bitmap1 bitmap, int frameIndex)
    {
        retiredGpuBitmaps.Enqueue(bitmap);
        GpuLeakDiagnostics.RetireD2DBitmap(bitmap, frameIndex, retiredGpuBitmaps.Count);
        while (retiredGpuBitmaps.Count > 3)
        {
            var retired = retiredGpuBitmaps.Dequeue();
            try
            {
                GpuLeakDiagnostics.DisposeD2DBitmap(retired, frameIndex, "retire");
            }
            catch (Exception ex)
            {
                GpuLeakDiagnostics.D2DBitmapDisposeFailed(retired, ex, frameIndex, "retire");
            }
        }
    }
    private HdrColorConvertCustomEffect? colorConvertEffect;
    private AffineTransform2D? transformEffect; // 中心原点へのオフセット用エフェクト
    private ID2D1Image? effectOutput;

    private Process? ffmpegProcess;
    private Stream? ffmpegStdout;
    private readonly object frameLock = new();
    // Fix91: YMM4 may invoke TimelineSource.Update in parallel. Serialize the GPU
    // producer/update state so outputBitmap/retiredGpuBitmaps/native decoder cannot race.
    private readonly object gpuUpdateLock = new();
    private int gpuUpdateInFlight;
    private int gpuUpdateMaxInFlight;

    private int width = 1920;
    private int height = 1080;
    private double fps = 30.0;
    private TimeSpan duration = TimeSpan.FromSeconds(10);
    private int bytesPerPixel = 8; // RGBA64 = 8 bytes per pixel
    private int decodedWidth = 1920;
    private int decodedHeight = 1080;
    private bool disposedValue;
    // Fix52: prefer D3D11VA hardware decode, then fall back to the existing CPU path if needed.
    private bool useD3D11HardwareDecode = false;
    private bool hardwareFallbackAttempted;

    // GPU native decode path (Fix84+)
    private FfmpegNativeGpuDecoder? nativeGpuDecoder;
    private bool gpuDecodeActive;

    // Frame cache for seeking
    private readonly Dictionary<int, byte[]> frameCache = new();
    private int lastFrameIndex = -1;

    // Performance diagnostics (Fix35): counters/timers only; decoding behavior is unchanged.
    private int perfFrameCount;
    private long perfReadTicks;
    private long perfCpuConvertTicks;
    private long perfBitmapUploadTicks;
    private long perfCacheLookupTicks;
    private long perfSeekRestartTicks;
    private long perfPipeReadTicks;
    private long perfFfmpegStartupTicks;
    private long perfFirstFrameTicks;
    private bool perfFirstFrameLogged;


    public TimeSpan Duration => duration;
    public ID2D1Image Output => effectOutput ?? (ID2D1Image)outputBitmap!;

    public HdrVideoFileSource(IGraphicsDevicesAndContext devices, string filePath, HdrInfo hdrInfo, bool ownsDevicesContext = false)
    {
        this.devices = devices;
        this.filePath = filePath;
        this.hdrInfo = hdrInfo;
        this.ownsDevicesContext = ownsDevicesContext;
        sourceId = Interlocked.Increment(ref nextSourceId);
        int live = Interlocked.Increment(ref liveSourceCount);
        Debug.WriteLine($"[HDRInjector][HDRGPU][SOURCE] CREATE id={sourceId} live={live} ownsContext={ownsDevicesContext} file={Path.GetFileName(filePath)}");

        InitializeSource();

        // Try GPU native decode path after InitializeSource sets up the effect chain.
        TryInitializeGpuDecoder();
    }

    private void InitializeSource()
    {
        var dc = devices.DeviceContext;

        // FFprobe で動画情報を取得
        ProbeVideoInfo();

        // HW capability probing must never block timeline insertion/startup.
        // Run diagnostics in the background; the normal HDR decode path remains unchanged.
        _ = Task.Run(ProbeHardwareAcceleration);
        _ = Task.Run(() => FfmpegNativeLoaderProbe.Run(filePath, devices.D3D.Device.NativePointer));

        // HDR 色空間変換エフェクト (PQ/HLG → Linear → SDR)
        var convertMode = hdrInfo.TransferFunction == HdrTransferFunction.Pq
            ? HdrConvertMode.PqToPipelineSrgb
            : HdrConvertMode.HlgToPipelineSrgb;

        colorConvertEffect = new HdrColorConvertCustomEffect(devices)
        {
            Mode = convertMode,
            TargetNits = 1000.0f,
            SdrNits = 80.0f * HDRInjector.HdrPreviewManager.CurrentSdrWhiteScale,
            ConvertGamut = true
        };
        disposer.Collect(colorConvertEffect);

        // 1. RGBA64 フレームデータを保持する D2D ビットマップ
        CreateOutputBitmap();
        if (outputBitmap != null)
        {
            colorConvertEffect.SetInput(0, outputBitmap, (RawBool)true);
        }

        // 2. YMM4の仕様に合わせて原点を画像の中心 (0, 0) に合わせる
        transformEffect = new AffineTransform2D(dc);
        transformEffect.SetInput(0, colorConvertEffect.Output, (RawBool)true);
        transformEffect.TransformMatrix = Matrix3x2.CreateTranslation(-width / 2.0f, -height / 2.0f);
        disposer.Collect(transformEffect);

        effectOutput = transformEffect.Output;
        disposer.Collect(effectOutput);
    }

    private void TryInitializeGpuDecoder()
    {
        try
        {
            if (FfmpegNativeGpuDecoder.TryCreate(filePath, devices.D3D.Device.NativePointer, devices.DeviceContext, out var decoder))
            {
                nativeGpuDecoder = decoder;
                nativeGpuDecoder.SetFrameRate(fps);
                nativeGpuDecoder.SetHdrTransfer(
                    hdrInfo.TransferFunction == HdrTransferFunction.Pq,
                    80.0f * HDRInjector.HdrPreviewManager.CurrentSdrWhiteScale);
                gpuDecodeActive = true;
                Debug.WriteLine($"[HDRInjector][GPU] GPU native decoder INITIALIZED: codec={decoder}");
            }
            else
            {
                Debug.WriteLine("[HDRInjector][GPU] GPU native decoder NOT available; using legacy subprocess path.");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HDRInjector][GPU] GPU decoder init failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ProbeVideoInfo()
    {
        try
        {
            string ffprobePath = FindFfprobe();
            if (ffprobePath == null) return;

            var startInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = $"-v quiet -print_format json -show_streams -show_format \"{filePath}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process == null) return;

            string json = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            int w = ExtractJsonInt(json, "\"width\"");
            if (w > 0) width = w;

            int h = ExtractJsonInt(json, "\"height\"");
            if (h > 0) height = h;

            double r = ExtractJsonDouble(json, "\"r_frame_rate\"");
            if (r > 0) fps = r;

            int nbFrames = ExtractJsonInt(json, "\"nb_frames\"");
            if (nbFrames > 0 && fps > 0)
                duration = TimeSpan.FromSeconds(nbFrames / fps);
            else
            {
                double dur = ExtractJsonDouble(json, "\"duration\"");
                if (dur > 0) duration = TimeSpan.FromSeconds(dur);
            }

            bytesPerPixel = 8;

            // Fix52: keep the source resolution. The previous 1920x1080 cap was only
            // a bandwidth experiment and must not be part of the real input path.
            int sourceWidth = width;
            int sourceHeight = height;
            decodedWidth = sourceWidth;
            decodedHeight = sourceHeight;
            System.Diagnostics.Debug.WriteLine($"[HDRInjector][HDR] Video source={sourceWidth}x{sourceHeight}, decode target={width}x{height}, {fps}fps, {duration.TotalSeconds:F2}s");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HDRInjector][HDR] Probe failed: {ex.Message}");
        }
    }

    /// <summary>
    /// FFmpeg が利用可能な HW デコード方式と、現在の入力ファイルで実際に初期化できるかを調べる診断用プローブ。
    /// まだ通常のデコード経路は変更しない。
    /// </summary>
    private void ProbeHardwareAcceleration()
    {
        try
        {
            string ffmpegPath = FindFfmpeg();
            if (string.IsNullOrWhiteSpace(ffmpegPath))
                return;

            string hwaccels = RunFfmpegDiagnostic(ffmpegPath, "-hide_banner -hwaccels", 5000);
            var methods = new List<string>();
            foreach (var rawLine in hwaccels.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("Hardware acceleration methods:", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!line.StartsWith("[", StringComparison.Ordinal))
                    methods.Add(line);
            }

            Debug.WriteLine($"[HDRInjector][HDRHW] FFmpeg hwaccels: {string.Join(", ", methods)}");

            string? codecName = null;
            string? pixFmt = null;
            string? codecLongName = null;
            string? ffprobePath = FindFfprobe();
            if (!string.IsNullOrWhiteSpace(ffprobePath))
            {
                string codecInfo = RunFfprobeDiagnostic(
                    ffprobePath!,
                    $"-v error -select_streams v:0 -show_entries stream=codec_name,codec_long_name,pix_fmt -of default=nw=1 \"{filePath}\"",
                    5000);
                Debug.WriteLine($"[HDRInjector][HDRHW] Input stream: {codecInfo.Replace(Environment.NewLine, " | ").Replace("\n", " | ")}");

                foreach (var rawLine in codecInfo.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = rawLine.Split('=', 2);
                    if (parts.Length != 2) continue;
                    switch (parts[0].Trim())
                    {
                        case "codec_name": codecName = parts[1].Trim(); break;
                        case "codec_long_name": codecLongName = parts[1].Trim(); break;
                        case "pix_fmt": pixFmt = parts[1].Trim(); break;
                    }
                }
            }
            else
            {
                Debug.WriteLine("[HDRInjector][HDRHW] ffprobe not found; skipping codec info diagnostic.");
            }

            if (string.IsNullOrWhiteSpace(codecName))
            {
                Debug.WriteLine("[HDRInjector][HDRHW] No codec_name was detected; skipping hardware decode probes.");
                return;
            }

            Debug.WriteLine($"[HDRInjector][HDRHW] Probe target codec={codecName}, pix_fmt={pixFmt ?? "<unknown>"}");

            // The previous probe assumed HEVC and ran multiple long ffmpeg processes.
            // This file is actually VP9 10-bit, so probe the real codec and keep the
            // diagnostic bounded to one frame per method.
            // Fix53: benchmark a hardware-decoded 10-bit YUV path without changing the active decode path.
            // If hwdownload to P010 is substantially faster than RGBA64, the next step can be
            // a GPU-side P010 -> RGBA64 conversion and direct D3D11 surface integration.
            foreach (string method in new[] { "d3d11va", "cuda" })
            {
                if (!methods.Contains(method, StringComparer.OrdinalIgnoreCase))
                    continue;

                string outputFormat = method.Equals("d3d11va", StringComparison.OrdinalIgnoreCase)
                    ? "d3d11"
                    : "cuda";

                string p010Args = $"-hide_banner -loglevel verbose -hwaccel {method} -hwaccel_output_format {outputFormat} -i \"{filePath}\" -map 0:v:0 -frames:v 1 -vf \"hwdownload,format=p010le\" -f rawvideo -pix_fmt p010le -";
                Debug.WriteLine($"[HDRInjector][HDRHW] Probe {method}/p010 start codec={codecName} pix_fmt={pixFmt ?? "<unknown>"}");
                long start = Stopwatch.GetTimestamp();
                var result = RunFfmpegDiagnosticWithExitCode(ffmpegPath, p010Args, 15000);
                double elapsedMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                long expectedBytes = (long)width * height * 3 / 2 * 2; // P010 is 16-bit storage at 4:2:0: 1.5 samples/pixel.
                Debug.WriteLine($"[HDRInjector][HDRHW] Probe {method}/p010: exit={result.ExitCode}, elapsed={elapsedMs:0.00}ms, expectedFrameBytes={expectedBytes}, trace={NormalizeHardwareDiagnostic(result.Output)}");
            }

            foreach (string method in new[] { "d3d11va", "cuda" })
            {
                if (!methods.Contains(method, StringComparer.OrdinalIgnoreCase))
                    continue;

                string outputFormat = method.Equals("d3d11va", StringComparison.OrdinalIgnoreCase)
                    ? "d3d11"
                    : "cuda";

                string decodeArgs = $"-hide_banner -loglevel verbose -hwaccel {method} -hwaccel_output_format {outputFormat} -i \"{filePath}\" -map 0:v:0 -frames:v 1 -f null -";
                Debug.WriteLine($"[HDRInjector][HDRHW] Probe {method}/decode start codec={codecName} pix_fmt={pixFmt ?? "<unknown>"}");
                var decodeResult = RunFfmpegDiagnosticWithExitCode(ffmpegPath, decodeArgs, 10000);
                Debug.WriteLine($"[HDRInjector][HDRHW] Probe {method}/decode: exit={decodeResult.ExitCode}, trace={NormalizeHardwareDiagnostic(decodeResult.Output)}");

                bool decodeSuccess = decodeResult.ExitCode == 0;
                bool looksLikeHwFrame = ContainsAny(decodeResult.Output,
                    "d3d11", "AV_PIX_FMT_D3D11", "d3d11va", "cuda", "AV_PIX_FMT_CUDA", "hardware");

                // A second probe forces a real download from the HW surface to system memory.
                // This is more meaningful than a null output alone because it requires the
                // decoded frame to actually exist in the requested hardware frame context.
                string downloadArgs = $"-hide_banner -loglevel verbose -hwaccel {method} -hwaccel_output_format {outputFormat} -i \"{filePath}\" -map 0:v:0 -frames:v 1 -vf \"hwdownload,format=p010le\" -f null -";
                Debug.WriteLine($"[HDRInjector][HDRHW] Probe {method}/hwdownload start codec={codecName} pix_fmt={pixFmt ?? "<unknown>"}");
                var downloadResult = RunFfmpegDiagnosticWithExitCode(ffmpegPath, downloadArgs, 10000);
                Debug.WriteLine($"[HDRInjector][HDRHW] Probe {method}/hwdownload: exit={downloadResult.ExitCode}, trace={NormalizeHardwareDiagnostic(downloadResult.Output)}");

                if (method.Equals("d3d11va", StringComparison.OrdinalIgnoreCase))
                {
                    bool downloadSuccess = downloadResult.ExitCode == 0;
                    Debug.WriteLine($"[HDRInjector][HDRHW] D3D11VA verdict: decodeSuccess={decodeSuccess}, hwTrace={looksLikeHwFrame}, hwdownloadSuccess={downloadSuccess}, codec={codecName}, pix_fmt={pixFmt ?? "<unknown>"}");
                }
                else if (method.Equals("cuda", StringComparison.OrdinalIgnoreCase))
                {
                    bool downloadSuccess = downloadResult.ExitCode == 0;
                    Debug.WriteLine($"[HDRInjector][HDRHW] CUDA verdict: decodeSuccess={decodeSuccess}, hwTrace={looksLikeHwFrame}, hwdownloadSuccess={downloadSuccess}, codec={codecName}, pix_fmt={pixFmt ?? "<unknown>"}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HDRInjector][HDRHW] Probe failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string RunFfprobeDiagnostic(string ffprobePath, string arguments, int timeoutMs)
    {
        var result = RunProcessDiagnostic(ffprobePath, arguments, timeoutMs);
        return string.IsNullOrWhiteSpace(result.stderr) ? result.stdout.Trim() : result.stderr.Trim();
    }

    private static string RunFfmpegDiagnostic(string ffmpegPath, string arguments, int timeoutMs)
    {
        var result = RunProcessDiagnostic(ffmpegPath, arguments, timeoutMs);
        return string.IsNullOrWhiteSpace(result.stderr) ? result.stdout.Trim() : result.stderr.Trim();
    }

    private static (int ExitCode, string Output) RunFfmpegDiagnosticWithExitCode(string ffmpegPath, string arguments, int timeoutMs)
    {
        var result = RunProcessDiagnostic(ffmpegPath, arguments, timeoutMs);
        return (result.exitCode, string.IsNullOrWhiteSpace(result.stderr) ? result.stdout.Trim() : result.stderr.Trim());
    }

    private static (int exitCode, string stdout, string stderr) RunProcessDiagnostic(string executable, string arguments, int timeoutMs)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (!process.Start())
            return (-1, string.Empty, "Process start failed.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-1, string.Empty, "Diagnostic timeout; process terminated.");
        }

        try
        {
            Task.WhenAll(stdoutTask, stderrTask).Wait(2000);
        }
        catch { }
        string stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
        string stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty;
        return (process.ExitCode, stdout, stderr);
    }

    private static string NormalizeDiagnostic(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "<none>";
        return text.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private static string NormalizeHardwareDiagnostic(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "<none>";

        var interesting = new List<string>();
        foreach (var rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.IndexOf("d3d11", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("dxva", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("cuda", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("hwaccel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("hardware", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("AV_PIX_FMT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("hevc", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                interesting.Add(line);
            }
        }

        return interesting.Count == 0
            ? NormalizeDiagnostic(text)
            : string.Join(" | ", interesting);
    }

    private static bool ContainsAny(string text, params string[] markers)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (var marker in markers)
        {
            if (text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
    }

    private void CreateOutputBitmap()
    {
        var dc = devices.DeviceContext;

        // FFmpeg produces packed RGBA64 (UNorm). D2D's color conversion effect
        // will perform the transfer/gamut conversion into the float HDR pipeline.
        var props = new BitmapProperties(
            new PixelFormat(Format.R16G16B16A16_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            96.0f,
            96.0f
        );

        outputBitmap = dc.CreateBitmap(
            new SizeI(width, height),
            IntPtr.Zero,
            0,
            props
        );
        disposer.Collect(outputBitmap);
    }

    private string BuildFfmpegVideoArguments(double? seekTime = null)
    {
        string seek = seekTime.HasValue ? $"-ss {seekTime.Value:F6} " : string.Empty;
        if (useD3D11HardwareDecode)
        {
            // D3D11VA decodes on the GPU. hwdownload is intentionally kept for this
            // first integration step so the rest of the proven RGBA64 pipeline stays
            // unchanged; the next step can pass the D3D11 surface directly.
            return $"{seek}-hide_banner -hwaccel d3d11va -hwaccel_output_format d3d11 -i \"{filePath}\" -vf \"hwdownload,format=rgba64le\" -f rawvideo -pix_fmt rgba64le -v quiet -";
        }

        return $"{seek}-hide_banner -i \"{filePath}\" -f rawvideo -pix_fmt rgba64le -v quiet -";
    }

    private void EnsureFfmpegProcess()
    {
        if (ffmpegProcess != null && !ffmpegProcess.HasExited)
            return;

        try
        {
            string ffmpegPath = FindFfmpeg();
            if (ffmpegPath == null)
            {
                System.Diagnostics.Debug.WriteLine("[HDRInjector][HDR] FFmpeg not found.");
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = BuildFfmpegVideoArguments(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            long startTicks = Stopwatch.GetTimestamp();
            ffmpegProcess = Process.Start(startInfo);
            long startedTicks = Stopwatch.GetTimestamp();
            perfFfmpegStartupTicks += startedTicks - startTicks;
            if (ffmpegProcess != null)
            {
                ffmpegStdout = ffmpegProcess.StandardOutput.BaseStream;
                System.Diagnostics.Debug.WriteLine($"[HDRInjector][HDRPerf][Input] FFmpeg started pid={ffmpegProcess.Id}, startup={(startedTicks - startTicks) * 1000.0 / Stopwatch.Frequency:0.00}ms");
                System.Diagnostics.Debug.WriteLine($"[HDRInjector][HDRPerf][Input] FFmpeg args: {ffmpegPath} {startInfo.Arguments}");
                Debug.WriteLine($"[HDRInjector][HDRHW] Active decode mode: {(useD3D11HardwareDecode ? "D3D11VA + hwdownload" : "software")}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HDRInjector][HDR] FFmpeg start failed: {ex.Message}");
        }
    }

    private int updateCallCount;
    private int gpuFallbackCount;

    private void DisableGpuPath(string reason, Exception? ex = null, int? frameIndex = null)
    {
        gpuDecodeActive = false;
        int count = Interlocked.Increment(ref gpuFallbackCount);
        Debug.WriteLine(
            $"[HDRInjector][HDRGPU][FALLBACK] SOURCE id={sourceId} count={count} frame={(frameIndex.HasValue ? frameIndex.Value.ToString() : "?")} reason={reason}" +
            (ex == null ? string.Empty : $" exception={ex.GetType().Name}: {ex.Message}"));
    }

    private void DisposeGpuOutputForFallback(int frameIndex, string phase)
    {
        if (outputBitmap is not ID2D1Bitmap1 gpuBitmap)
            return;

        try
        {
            GpuLeakDiagnostics.DisposeD2DBitmap(gpuBitmap, frameIndex, phase);
        }
        catch (Exception ex)
        {
            GpuLeakDiagnostics.D2DBitmapDisposeFailed(gpuBitmap, ex, frameIndex, phase);
        }
        finally
        {
            outputBitmap = null;
        }
    }

    public void Update(TimeSpan time)
    {
        if (disposedValue) return;

        int frameIndex = (int)Math.Floor(time.TotalSeconds * fps);
        if (frameIndex < 0) frameIndex = 0;
        updateCallCount++;
        if (updateCallCount <= 3)
            Debug.WriteLine($"[HDRInjector][GPU] Update called #{updateCallCount}: time={time.TotalSeconds:F3}s frame={frameIndex} gpuActive={gpuDecodeActive} decoder={nativeGpuDecoder != null}");

        // ── GPU native decode path ──
        if (gpuDecodeActive && nativeGpuDecoder != null)
        {
            // Count callers before taking the lock so queued YMM4 Update calls are observable.
            int inFlight = Interlocked.Increment(ref gpuUpdateInFlight);
            int maxObserved;
            while (true)
            {
                maxObserved = Volatile.Read(ref gpuUpdateMaxInFlight);
                if (inFlight <= maxObserved) break;
                if (Interlocked.CompareExchange(ref gpuUpdateMaxInFlight, inFlight, maxObserved) == maxObserved) break;
            }

            long gpuLockStart = Stopwatch.GetTimestamp();
            Monitor.Enter(gpuUpdateLock);
            double waitMs = (Stopwatch.GetTimestamp() - gpuLockStart) * 1000.0 / Stopwatch.Frequency;
            if (waitMs > 0.5)
            {
                Debug.WriteLine($"[HDRInjector][HDRGPU][SYNC] GPU Update waited {waitMs:0.00}ms; inFlight={inFlight}, maxInFlight={Volatile.Read(ref gpuUpdateMaxInFlight)}");
            }

            try
            {
                try
                {
                    int gpuNext = nativeGpuDecoder.NextFrameIndex;
                    int gpuLast = nativeGpuDecoder.LastDecodedFrame;

                    if (frameIndex == gpuLast)
                        return;

                    // Fix94: Smart seek logic to avoid unnecessary seeks during normal playback.
                    // YMM4's video task loop may skip frames when Update() takes too long,
                    // so small forward gaps should be handled by sequential decode, not seek.
                    int skipThreshold = Math.Max(5, (int)(fps / 4.0)); // ~15 frames at 60fps

                    if (frameIndex < gpuLast)
                    {
                        // Backward jump: must seek
                        Debug.WriteLine($"[HDRInjector][GPU][Fix94] Backward seek: want={frameIndex}, last={gpuLast}; seeking.");
                        if (!nativeGpuDecoder.SeekToFrame(frameIndex, fps))
                        {
                            Debug.WriteLine("[HDRInjector][GPU] Native seek failed; falling back to legacy path.");
                            DisableGpuPath("native-seek-failed", frameIndex: frameIndex);
                        }
                    }
                    else if (frameIndex > gpuNext + skipThreshold)
                    {
                        // Large forward jump: seek is faster than decoding many intermediate frames
                        Debug.WriteLine($"[HDRInjector][GPU][Fix94] Large forward jump: want={frameIndex}, next={gpuNext}, gap={frameIndex - gpuNext}; seeking.");
                        if (!nativeGpuDecoder.SeekToFrame(frameIndex, fps))
                        {
                            Debug.WriteLine("[HDRInjector][GPU] Native seek failed; falling back to legacy path.");
                            DisableGpuPath("native-seek-failed", frameIndex: frameIndex);
                        }
                    }
                    // else: small forward gap (frameIndex is between gpuNext and gpuNext+skipThreshold).
                    // Let sequential decode naturally catch up. The decoder will decode frames
                    // one at a time until it reaches frameIndex, discarding intermediate frames.

                    if (gpuDecodeActive)
                    {
                        long gpuStart = Stopwatch.GetTimestamp();
                        // Fix95: Use TryDecodeToFrame to catch up to the target frame in a single
                        // Update() call. This prevents the gap from growing when the decoder is
                        // slightly behind — previously each Update() only advanced one frame.
                        if (nativeGpuDecoder.TryDecodeToFrame(frameIndex, out var gpuBitmap, out double gpuMs, out string decodeReason))
                        {
                            // Fix94: Ring buffer bitmaps are pre-allocated and don't need retirement.
                            // The old bitmap is still valid in another ring slot and will be overwritten
                            // on its next rotation. Simply update the output reference.
                            outputBitmap = gpuBitmap;

                            // IMPORTANT: native GPU output is already encoded as pipeline-sRGB in FP16.
                            // The existing YMM4 HDR bridge will perform its global sRGB->Linear
                            // reconstruction later. Do not run HdrColorConvertCustomEffect here.
                            transformEffect!.SetInput(0, outputBitmap, (RawBool)true);

                            perfFrameCount++;
                            if ((perfFrameCount % 30) == 0)
                            {
                                Debug.WriteLine($"[HDRInjector][HDRPerf][InputGPU] frames={perfFrameCount}, gpu={gpuMs:0.00}ms/frame, frame={nativeGpuDecoder.LastDecodedFrame}");
                            }
                            return;
                        }

                        // Fix93: a false return is diagnostic only. Do NOT kill the GPU path or
                        // reinitialize the legacy source here. A false result may be a transient
                        // EAGAIN/packet-boundary condition, and Fix92 proved that disabling the
                        // GPU path here can stall video rendering while audio continues.
                        Debug.WriteLine($"[HDRInjector][HDRGPU][DECODE-MISS] SOURCE id={sourceId} frame={frameIndex} reason={decodeReason}; GPU path remains active.");
                        GpuLeakDiagnostics.Log("decode-miss", frameIndex);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[HDRInjector][GPU] Update error: {ex.GetType().Name}: {ex.Message}");
                    DisableGpuPath("update-exception", ex, frameIndex);
                }
            }
            finally
            {
                Interlocked.Decrement(ref gpuUpdateInFlight);
                Monitor.Exit(gpuUpdateLock);
            }
        }

        // ── Legacy subprocess decode path (fallback) ──
        // outputBitmap may be null after a GPU-path failure; the legacy uploader creates
        // the CPU bitmap itself, so do not return early here.
        EnsureFfmpegProcess();
        if (ffmpegProcess == null || ffmpegProcess.HasExited || ffmpegStdout == null) return;

        try
        {
            var decode = DecodeFrame(frameIndex);
            byte[]? frameData = decode.FrameData;
            if (frameData == null) return;

            if (outputBitmap is ID2D1Bitmap1)
            {
                // The GPU path may have failed immediately before this legacy fallback.
                // Dispose the GPU bitmap through the diagnostic path too, otherwise the
                // live Bitmap counter would falsely report it as leaked.
                DisposeGpuOutputForFallback(frameIndex, "gpu-to-legacy");
                Debug.WriteLine($"[HDRInjector][HDRGPU][FALLBACK] SOURCE id={sourceId} frame={frameIndex} legacy bitmap path reinitialized");
            }

            var timings = ConvertRgb48ToFloat16AndUpload(frameData);
            perfFrameCount++;
            perfReadTicks += decode.TotalTicks;
            perfCacheLookupTicks += decode.CacheLookupTicks;
            perfSeekRestartTicks += decode.SeekRestartTicks;
            perfPipeReadTicks += decode.PipeReadTicks;
            perfCpuConvertTicks += timings.CpuConvertTicks;
            perfBitmapUploadTicks += timings.BitmapUploadTicks;

            if ((perfFrameCount % 30) == 0)
            {
                double scale = 1000.0 / Stopwatch.Frequency;
                Debug.WriteLine(
                    $"[HDRInjector][HDRPerf][Input] frames={perfFrameCount}, " +
                    $"avg/frame read={perfReadTicks * scale / perfFrameCount:0.00}ms, " +
                    $"cacheLookup={perfCacheLookupTicks * scale / perfFrameCount:0.00}ms, " +
                    $"seekRestart={perfSeekRestartTicks * scale / perfFrameCount:0.00}ms, " +
                    $"pipeRead={perfPipeReadTicks * scale / perfFrameCount:0.00}ms, " +
                    $"firstByte(avg-first-frame-only)={perfFirstFrameTicks * scale / Math.Max(1, perfFrameCount):0.00}ms, " +
                    $"ffmpegStartupTotal={perfFfmpegStartupTicks * scale:0.00}ms, " +
                    $"CPU convert={perfCpuConvertTicks * scale / perfFrameCount:0.00}ms, " +
                    $"bitmap upload={perfBitmapUploadTicks * scale / perfFrameCount:0.00}ms");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HDRInjector][HDR] Update error: {ex.Message}");
        }
    }

    private FrameDecodePerfBreakdown DecodeFrame(int frameIndex)
    {
        lock (frameLock)
        {
            long totalStart = Stopwatch.GetTimestamp();
            long cacheStart = Stopwatch.GetTimestamp();

            if (frameCache.TryGetValue(frameIndex, out var cached))
            {
                long cacheEnd = Stopwatch.GetTimestamp();
                return new FrameDecodePerfBreakdown(
                    cached,
                    cacheEnd - totalStart,
                    cacheEnd - cacheStart,
                    0,
                    0);
            }

            long cacheEndMiss = Stopwatch.GetTimestamp();
            long seekTicks = 0;
            if (frameIndex != lastFrameIndex + 1)
            {
                long seekStart = Stopwatch.GetTimestamp();
                RestartFfmpegWithSeek(frameIndex);
                seekTicks = Stopwatch.GetTimestamp() - seekStart;
            }

            int frameSize = width * height * bytesPerPixel;
            byte[] frameData = new byte[frameSize];

            long pipeStart = Stopwatch.GetTimestamp();
            bool firstChunk = true;
            long firstChunkTicks = 0;
            int totalRead = 0;
            while (totalRead < frameSize)
            {
                int read = ffmpegStdout!.Read(frameData, totalRead, frameSize - totalRead);
                if (firstChunk)
                {
                    firstChunk = false;
                    firstChunkTicks = Stopwatch.GetTimestamp() - pipeStart;
                    perfFirstFrameTicks += firstChunkTicks;
                    if (!perfFirstFrameLogged)
                    {
                        perfFirstFrameLogged = true;
                        Debug.WriteLine($"[HDRInjector][HDRPerf][Input] First frame first-byte wait={firstChunkTicks * 1000.0 / Stopwatch.Frequency:0.00}ms, frameBytes={frameSize}");
                    }
                }
                if (read <= 0)
                {
                    // If D3D11VA could not produce the requested frame, retry once with
                    // the long-standing CPU decode path instead of making the HDR source fail.
                    if (useD3D11HardwareDecode && !hardwareFallbackAttempted)
                    {
                        hardwareFallbackAttempted = true;
                        useD3D11HardwareDecode = false;
                        Debug.WriteLine($"[HDRInjector][HDRHW] D3D11VA frame read failed at frame={frameIndex}; falling back to software FFmpeg decode.");
                        RestartFfmpegWithSeek(frameIndex);
                        return DecodeFrame(frameIndex);
                    }

                    System.Diagnostics.Debug.WriteLine($"[HDRInjector][HDR] Stream ended at frame {frameIndex} (read {totalRead}/{frameSize})");
                    return new FrameDecodePerfBreakdown(null, Stopwatch.GetTimestamp() - totalStart, cacheEndMiss - cacheStart, seekTicks, Stopwatch.GetTimestamp() - pipeStart);
                }
                totalRead += read;
            }
            long pipeEnd = Stopwatch.GetTimestamp();

            lastFrameIndex = frameIndex;
            frameCache[frameIndex] = frameData;

            if (frameCache.Count > 30)
            {
                int oldest = frameIndex - 30;
                if (oldest >= 0) frameCache.Remove(oldest);
            }

            return new FrameDecodePerfBreakdown(
                frameData,
                pipeEnd - totalStart,
                cacheEndMiss - cacheStart,
                seekTicks,
                pipeEnd - pipeStart);
        }
    }

    private void RestartFfmpegWithSeek(int frameIndex)
    {
        ffmpegProcess?.Kill();
        ffmpegProcess?.Dispose();
        ffmpegProcess = null;
        ffmpegStdout?.Dispose();
        ffmpegStdout = null;

        try
        {
            string ffmpegPath = FindFfmpeg();
            if (ffmpegPath == null) return;

            double seekTime = frameIndex / fps;
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = BuildFfmpegVideoArguments(seekTime),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            long startTicks = Stopwatch.GetTimestamp();
            ffmpegProcess = Process.Start(startInfo);
            long startedTicks = Stopwatch.GetTimestamp();
            perfFfmpegStartupTicks += startedTicks - startTicks;
            if (ffmpegProcess != null)
            {
                ffmpegStdout = ffmpegProcess.StandardOutput.BaseStream;
                Debug.WriteLine($"[HDRInjector][HDRPerf][Input] FFmpeg seek-start pid={ffmpegProcess.Id}, startup={(startedTicks - startTicks) * 1000.0 / Stopwatch.Frequency:0.00}ms, seek={seekTime:0.000}s");
                Debug.WriteLine($"[HDRInjector][HDRPerf][Input] FFmpeg seek args: {ffmpegPath} {startInfo.Arguments}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HDRInjector][HDR] FFmpeg seek failed: {ex.Message}");
        }
    }

    private unsafe FrameInputPerfBreakdown ConvertRgb48ToFloat16AndUpload(byte[] frameData)
    {
        // FFmpeg already emitted packed 16-bit RGBA values, so there is no
        // per-pixel CPU conversion step here. The raw bytes are uploaded directly
        // to a UNorm bitmap and the existing HDR effect performs the conversion.
        long cpuStart = Stopwatch.GetTimestamp();
        long cpuEnd = Stopwatch.GetTimestamp();

        long uploadStart = Stopwatch.GetTimestamp();
        var props = new BitmapProperties(
            new PixelFormat(Format.R16G16B16A16_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            96.0f,
            96.0f
        );

        if (outputBitmap is ID2D1Bitmap1)
        {
            DisposeGpuOutputForFallback(lastFrameIndex, "legacy-upload");
        }
        else
        {
            outputBitmap?.Dispose();
        }
        var gch = GCHandle.Alloc(frameData, GCHandleType.Pinned);
        try
        {
            outputBitmap = devices.DeviceContext.CreateBitmap(
                new SizeI(width, height),
                gch.AddrOfPinnedObject(),
                width * bytesPerPixel,
                props
            );
        }
        finally
        {
            gch.Free();
        }

        float currentScale = Math.Max(0.01f, HDRInjector.HdrPreviewManager.CurrentSdrWhiteScale);
        colorConvertEffect!.SdrNits = 80.0f * currentScale;
        colorConvertEffect.SetInput(0, outputBitmap, (RawBool)true);
        long uploadEnd = Stopwatch.GetTimestamp();
        return new FrameInputPerfBreakdown(cpuEnd - cpuStart, uploadEnd - uploadStart);
    }

    private readonly record struct FrameInputPerfBreakdown(long CpuConvertTicks, long BitmapUploadTicks);

    private readonly record struct FrameDecodePerfBreakdown(
        byte[]? FrameData,
        long TotalTicks,
        long CacheLookupTicks,
        long SeekRestartTicks,
        long PipeReadTicks);

    public int GetFrameIndex(TimeSpan time)
    {
        return (int)Math.Floor(time.TotalSeconds * fps);
    }

    private static string? FindFfmpeg()
    {
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string[] candidates = new[]
        {
            Path.Combine(appDir, "ffmpeg.exe"),
            Path.Combine(appDir, "ffmpeg", "ffmpeg.exe"),
            Path.Combine(appDir, "tools", "ffmpeg.exe"),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path)) return path;
        }

        return "ffmpeg";
    }

    private static string? FindFfprobe()
    {
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

        return "ffprobe";
    }

    private static int ExtractJsonInt(string json, string key)
    {
        int idx = json.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return -1;
        idx = json.IndexOf(':', idx) + 1;
        while (idx < json.Length && (json[idx] == ' ' || json[idx] == '"')) idx++;
        int start = idx;
        while (idx < json.Length && char.IsDigit(json[idx])) idx++;
        if (idx > start && int.TryParse(json[start..idx], out int val))
            return val;
        return -1;
    }

    private static double ExtractJsonDouble(string json, string key)
    {
        int idx = json.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return -1;
        idx = json.IndexOf(':', idx) + 1;
        while (idx < json.Length && (json[idx] == ' ' || json[idx] == '"')) idx++;
        int start = idx;
        while (idx < json.Length && (char.IsDigit(json[idx]) || json[idx] == '.')) idx++;
        if (idx > start && double.TryParse(json[start..idx], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double val))
            return val;
        return -1;
    }

    public void Dispose()
    {
        if (disposedValue) return;
        disposedValue = true;

        // Fix91: serialize teardown with the GPU Update critical section.
        Monitor.Enter(gpuUpdateLock);
        try
        {
            gpuDecodeActive = false;
            nativeGpuDecoder?.Dispose();
            nativeGpuDecoder = null;

            ffmpegProcess?.Kill();
            ffmpegProcess?.Dispose();
            ffmpegStdout?.Dispose();

            Debug.WriteLine($"[HDRInjector][HDRGPU][FALLBACK] SOURCE id={sourceId} totalFallbacks={Volatile.Read(ref gpuFallbackCount)}");

            if (perfFrameCount > 0)
            {
                double scale = 1000.0 / Stopwatch.Frequency;
                Debug.WriteLine(
                    $"[HDRInjector][HDRPerf][Input] FINAL frames={perfFrameCount}, " +
                    $"avg/frame read={perfReadTicks * scale / perfFrameCount:0.00}ms, " +
                    $"cacheLookup={perfCacheLookupTicks * scale / perfFrameCount:0.00}ms, " +
                    $"seekRestart={perfSeekRestartTicks * scale / perfFrameCount:0.00}ms, " +
                    $"pipeRead={perfPipeReadTicks * scale / perfFrameCount:0.00}ms, " +
                    $"ffmpegStartupTotal={perfFfmpegStartupTicks * scale:0.00}ms, " +
                    $"CPU convert={perfCpuConvertTicks * scale / perfFrameCount:0.00}ms, " +
                    $"bitmap upload={perfBitmapUploadTicks * scale / perfFrameCount:0.00}ms");
            }

            while (retiredGpuBitmaps.Count > 0)
            {
                var retired = retiredGpuBitmaps.Dequeue();
                try
                {
                    GpuLeakDiagnostics.DisposeD2DBitmap(retired, lastFrameIndex, "source-dispose");
                }
                catch (Exception ex)
                {
                    GpuLeakDiagnostics.D2DBitmapDisposeFailed(retired, ex, lastFrameIndex, "source-dispose");
                }
            }

            if (outputBitmap is ID2D1Bitmap1 outputGpuBitmap)
            {
                try
                {
                    GpuLeakDiagnostics.DisposeD2DBitmap(outputGpuBitmap, lastFrameIndex, "source-output-dispose");
                }
                catch (Exception ex)
                {
                    GpuLeakDiagnostics.D2DBitmapDisposeFailed(outputGpuBitmap, ex, lastFrameIndex, "source-output-dispose");
                }
            }
            else
            {
                try { outputBitmap?.Dispose(); } catch { }
            }

            outputBitmap = null;
            disposer.Dispose();
            frameCache.Clear();

            if (ownsDevicesContext)
            {
                try
                {
                    devices.Dispose();
                    Debug.WriteLine($"[HDRInjector][HDRGPU][SOURCE] CONTEXT DISPOSE id={sourceId}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[HDRInjector][HDRGPU][SOURCE] CONTEXT DISPOSE FAILED id={sourceId}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            int liveAfter = Interlocked.Decrement(ref liveSourceCount);
            Debug.WriteLine($"[HDRInjector][HDRGPU][SOURCE] DISPOSE id={sourceId} live={liveAfter}");
            Debug.WriteLine($"[HDRInjector][HDRGPU][SYNC] SOURCE id={sourceId} gpuMaxInFlight={Volatile.Read(ref gpuUpdateMaxInFlight)}");
            GpuLeakDiagnostics.Log("source-dispose", sourceId);
        }
        finally
        {
            Monitor.Exit(gpuUpdateLock);
        }
    }

}