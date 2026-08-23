using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Diagnostics;
using System.Text;
using System.Reflection;
using FFmpeg.AutoGen;
using Vortice.Direct3D;

namespace HDRInjector.FileSource;

internal static class FfmpegNativeLoaderProbe
{
    private static readonly object GetFormatRootLock = new();
    private static AvGetFormatDelegate? RootedGetFormatDelegate;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectoryW(string? lpPathName);

    public static void Run(string? inputFile = null, IntPtr ymmD3D11DevicePtr = default)
    {
        try
        {
            string root = FindFfmpegDirectory();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] FFmpeg directory not found.");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] Root={root}");

            string[] bases =
            {
                "avutil",
                "swresample",
                "swscale",
                "avcodec",
                "avformat"
            };

            var loaded = new List<(string Name, IntPtr Handle)>();
            try
            {
                // FFmpeg DLLs depend on one another. Add their directory to the DLL
                // search path for this process before loading the native modules.
                SetDllDirectoryW(root);

                foreach (string baseName in bases)
                {
                    string? path = FindVersionedDll(root, baseName);
                    if (path == null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] {baseName}: NOT FOUND");
                        continue;
                    }

                    FileInfo fi = new(path);
                    System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] {baseName}: FOUND {Path.GetFileName(path)} ({fi.Length:N0} bytes)");

                    try
                    {
                        IntPtr handle = NativeLibrary.Load(path);
                        loaded.Add((Path.GetFileName(path), handle));
                        System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] {baseName}: LOAD SUCCESS");

                        if (NativeLibrary.TryGetExport(handle, "av_version_info", out IntPtr avVersionInfo))
                        {
                            System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] {baseName}: export av_version_info FOUND");
                        }
                        if (NativeLibrary.TryGetExport(handle, $"{baseName}_version", out _))
                        {
                            System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] {baseName}: export {baseName}_version FOUND");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] {baseName}: LOAD FAILED {ex.GetType().Name}: {ex.Message}");
                    }
                }

                // Extra export checks tell us whether these are the expected FFmpeg
                // libraries without invoking any decoding API yet.
                foreach (var (name, handle) in loaded)
                {
                    CheckExport(name, handle, "avcodec_find_decoder");
                    CheckExport(name, handle, "avcodec_send_packet");
                    CheckExport(name, handle, "avcodec_receive_frame");
                    CheckExport(name, handle, "avformat_open_input");
                    CheckExport(name, handle, "avformat_find_stream_info");
                    CheckExport(name, handle, "av_hwdevice_ctx_create");
                }

                // Minimal native API probe: resolve the hardware-device helpers and
                // actually create/destroy a D3D11VA device context. This does not open
                // or decode the user's video yet; it only proves the bundled FFmpeg
                // libraries can be driven directly from the plugin process.
                RunHardwareDeviceContextProbe(loaded);
                if (ymmD3D11DevicePtr != IntPtr.Zero)
                    RunExistingYmmD3D11DeviceProbe(loaded, ymmD3D11DevicePtr);
                if (!string.IsNullOrWhiteSpace(inputFile))
                    RunNativeOpenProbe(loaded, inputFile, ymmD3D11DevicePtr);
            }
            finally
            {
                // The native probe intentionally leaves AVHWDevice/AVCodec state alive while
                // we investigate D3D11 interop. Do not unload FFmpeg DLLs underneath those
                // live native objects/callbacks; doing so can cause process-level heap/native
                // teardown crashes. The process will unload them at shutdown.
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] Probe failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // Restore normal DLL lookup behavior as best effort.
            try { SetDllDirectoryW(null); } catch { }
        }
    }


    private static void RunExistingYmmD3D11DeviceProbe(List<(string Name, IntPtr Handle)> loaded, IntPtr ymmDevicePtr)
    {
        if (ymmDevicePtr == IntPtr.Zero)
        {
            System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] Existing YMM4 D3D11 device probe skipped: device=NULL.");
            return;
        }

        IntPtr avutilHandle = IntPtr.Zero;
        foreach (var item in loaded)
        {
            if (item.Name.StartsWith("avutil-", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Name, "avutil.dll", StringComparison.OrdinalIgnoreCase))
            {
                avutilHandle = item.Handle;
                break;
            }
        }

        if (avutilHandle == IntPtr.Zero)
        {
            System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] Existing YMM4 D3D11 device probe skipped: avutil handle missing.");
            return;
        }

        if (!NativeLibrary.TryGetExport(avutilHandle, "av_hwdevice_find_type_by_name", out IntPtr findTypePtr) ||
            !NativeLibrary.TryGetExport(avutilHandle, "av_hwdevice_ctx_alloc", out IntPtr allocPtr) ||
            !NativeLibrary.TryGetExport(avutilHandle, "av_hwdevice_ctx_init", out IntPtr initPtr))
        {
            System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] Existing YMM4 D3D11 device probe skipped: required exports missing.");
            return;
        }

        try
        {
            var findType = Marshal.GetDelegateForFunctionPointer<AvHwDeviceFindTypeByNameDelegate>(findTypePtr);
            var alloc = Marshal.GetDelegateForFunctionPointer<AvHwDeviceCtxAllocDelegate>(allocPtr);
            var init = Marshal.GetDelegateForFunctionPointer<AvHwDeviceCtxInitDelegate>(initPtr);

            IntPtr typeName = Marshal.StringToCoTaskMemUTF8("d3d11va");
            try
            {
                int hwType = findType(typeName);
                if (hwType < 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] Existing YMM4 D3D11 device probe: type lookup failed rc={hwType}");
                    return;
                }

                IntPtr bufferRef = alloc(hwType);
                System.Diagnostics.Debug.WriteLine(
                    $"[HDRInjector][FFmpegDLL] Existing-device hwctx alloc: type={hwType}, bufferRef={(bufferRef != IntPtr.Zero ? "NONNULL" : "NULL")}");
                if (bufferRef == IntPtr.Zero)
                    return;

                // AVBufferRef is { AVBuffer* buffer; uint8_t* data; size_t size; ... }.
                // On x64, data is at offset 8. Its data points at AVHWDeviceContext.
                IntPtr hwDeviceContext = Marshal.ReadIntPtr(bufferRef, IntPtr.Size);
                if (hwDeviceContext == IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] Existing-device hwctx probe: AVHWDeviceContext data=NULL.");
                    return;
                }

                int hwctxOffset = checked((int)Marshal.OffsetOf<AVHWDeviceContext>("hwctx"));
                IntPtr apiHwContext = Marshal.ReadIntPtr(hwDeviceContext, hwctxOffset);
                System.Diagnostics.Debug.WriteLine(
                    $"[HDRInjector][FFmpegDLL] Existing-device hwctx layout: AVHWDeviceContext.hwctx=0x{hwctxOffset:X}, apiHwctx={(apiHwContext != IntPtr.Zero ? "NONNULL" : "NULL")}");
                if (apiHwContext == IntPtr.Zero)
                    return;

                // AVD3D11VADeviceContext starts with ID3D11Device* device.
                IntPtr addRefPtr = Marshal.ReadIntPtr(Marshal.ReadIntPtr(ymmDevicePtr), IntPtr.Size);
                var addRef = Marshal.GetDelegateForFunctionPointer<AddRefDelegate>(addRefPtr);
                uint refCount = addRef(ymmDevicePtr);
                System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] Existing-device AddRef => {refCount}");

                Marshal.WriteIntPtr(apiHwContext, 0, ymmDevicePtr);
                System.Diagnostics.Debug.WriteLine(
                    $"[HDRInjector][FFmpegDLL] Existing-device hwctx assigned: device=0x{ymmDevicePtr.ToInt64():X}");

                int initRc = init(bufferRef);
                System.Diagnostics.Debug.WriteLine(
                    $"[HDRInjector][FFmpegDLL] av_hwdevice_ctx_init(existing YMM4 device) => {initRc}");

                if (initRc == 0)
                {
                    IntPtr initializedDevice = Marshal.ReadIntPtr(apiHwContext, 0);
                    bool same = initializedDevice == ymmDevicePtr;
                    System.Diagnostics.Debug.WriteLine(
                        $"[HDRInjector][FFmpegDLL] Existing-device D3D11VA context: SUCCESS, deviceAfterInit=0x{initializedDevice.ToInt64():X}, samePointer={same}");
                    System.Diagnostics.Debug.WriteLine(
                        "[HDRInjector][FFmpegDLL] YMM4 D3D11Device can be wrapped by FFmpeg D3D11VA without creating a second device.");
                    // Intentionally keep bufferRef alive for this diagnostic probe. FFmpeg owns
                    // the AddRef we gave it, and the process teardown will reclaim the context.
                }
                else
                {
                    // If init failed, release the reference we manually added.
                    ReleaseCom(ymmDevicePtr);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(typeName);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[HDRInjector][FFmpegDLL] Existing-device D3D11VA wrap probe failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void RunHardwareDeviceContextProbe(List<(string Name, IntPtr Handle)> loaded)
    {
        IntPtr avutilHandle = IntPtr.Zero;
        foreach (var item in loaded)
        {
            if (item.Name.StartsWith("avutil-", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Name, "avutil.dll", StringComparison.OrdinalIgnoreCase))
            {
                avutilHandle = item.Handle;
                break;
            }
        }

        if (avutilHandle == IntPtr.Zero)
        {
            System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] HW device probe skipped: avutil handle missing.");
            return;
        }

        try
        {
            if (!NativeLibrary.TryGetExport(avutilHandle, "av_hwdevice_find_type_by_name", out IntPtr findTypePtr) ||
                !NativeLibrary.TryGetExport(avutilHandle, "av_hwdevice_ctx_create", out IntPtr createPtr) ||
                !NativeLibrary.TryGetExport(avutilHandle, "av_buffer_unref", out IntPtr unrefPtr))
            {
                System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] HW device probe skipped: required exports missing.");
                return;
            }

            var findType = Marshal.GetDelegateForFunctionPointer<AvHwDeviceFindTypeByNameDelegate>(findTypePtr);
            var create = Marshal.GetDelegateForFunctionPointer<AvHwDeviceCtxCreateDelegate>(createPtr);
            var unref = Marshal.GetDelegateForFunctionPointer<AvBufferUnrefDelegate>(unrefPtr);

            IntPtr typeName = Marshal.StringToCoTaskMemUTF8("d3d11va");
            IntPtr deviceRef = IntPtr.Zero;
            try
            {
                int hwType = findType(typeName);
                System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] av_hwdevice_find_type_by_name(d3d11va) => {hwType}");
                if (hwType < 0)
                    return;

                int rc = create(ref deviceRef, hwType, IntPtr.Zero, IntPtr.Zero, 0);
                System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] av_hwdevice_ctx_create(d3d11va) => {rc}, deviceRef={(deviceRef != IntPtr.Zero ? "NONNULL" : "NULL")}");
                if (rc == 0 && deviceRef != IntPtr.Zero)
                    System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] D3D11VA native device context creation: SUCCESS");
                else
                    System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] D3D11VA native device context creation: FAILED");
            }
            finally
            {
                if (deviceRef != IntPtr.Zero)
                {
                    IntPtr tmp = deviceRef;
                    unref(ref tmp);
                    deviceRef = IntPtr.Zero;
                }
                Marshal.FreeCoTaskMem(typeName);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] D3D11VA native probe failed: {ex.GetType().Name}: {ex.Message}");
        }
    }


    private static void RunNativeOpenProbe(List<(string Name, IntPtr Handle)> loaded, string inputFile, IntPtr ymmD3D11DevicePtr)
    {
        IntPtr avformatHandle = IntPtr.Zero;
        foreach (var item in loaded)
        {
            if (item.Name.StartsWith("avformat-", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Name, "avformat.dll", StringComparison.OrdinalIgnoreCase))
            {
                avformatHandle = item.Handle;
                break;
            }
        }

        if (avformatHandle == IntPtr.Zero)
        {
            System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] Native open probe skipped: avformat handle missing.");
            return;
        }

        try
        {
            if (!NativeLibrary.TryGetExport(avformatHandle, "avformat_open_input", out IntPtr openPtr) ||
                !NativeLibrary.TryGetExport(avformatHandle, "avformat_close_input", out IntPtr closePtr))
            {
                System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] Native open probe skipped: required exports missing.");
                return;
            }

            var open = Marshal.GetDelegateForFunctionPointer<AvFormatOpenInputDelegate>(openPtr);
            var close = Marshal.GetDelegateForFunctionPointer<AvFormatCloseInputDelegate>(closePtr);

            IntPtr url = Marshal.StringToCoTaskMemUTF8(inputFile);
            IntPtr formatContext = IntPtr.Zero;
            IntPtr options = IntPtr.Zero;
            try
            {
                long start = Stopwatch.GetTimestamp();
                int rc = open(ref formatContext, url, IntPtr.Zero, ref options);
                double openMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] avformat_open_input rc={rc}, elapsed={openMs:0.00}ms, context={(formatContext != IntPtr.Zero ? "NONNULL" : "NULL")}");
                if (rc < 0 || formatContext == IntPtr.Zero)
                    return;

                System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] Native libavformat open probe: SUCCESS");

                if (NativeLibrary.TryGetExport(avformatHandle, "avformat_find_stream_info", out IntPtr findInfoPtr))
                {
                    var findInfo = Marshal.GetDelegateForFunctionPointer<AvFormatFindStreamInfoDelegate>(findInfoPtr);
                    long infoStart = Stopwatch.GetTimestamp();
                    // AVDictionary** may be NULL. Passing IntPtr.Zero avoids passing a
                    // managed by-ref local across the native ABI boundary.
                    int infoRc = findInfo(formatContext, IntPtr.Zero);
                    double infoMs = (Stopwatch.GetTimestamp() - infoStart) * 1000.0 / Stopwatch.Frequency;
                    System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] avformat_find_stream_info rc={infoRc}, elapsed={infoMs:0.00}ms");
                    if (infoRc >= 0)
                    {
                        System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] Native libavformat stream-info probe: SUCCESS");
                        RunNativeCodecOpenProbe(loaded, inputFile, ymmD3D11DevicePtr);
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] Native libavformat stream-info probe: FAILED");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] avformat_find_stream_info export missing; stream-info probe skipped.");
                }
            }
            finally
            {
                if (formatContext != IntPtr.Zero)
                {
                    IntPtr tmp = formatContext;
                    close(ref tmp);
                    formatContext = IntPtr.Zero;
                }
                Marshal.FreeCoTaskMem(url);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] Native open probe failed: {ex.GetType().Name}: {ex.Message}");
        }
    }


    private static void RunNativeCodecOpenProbe(List<(string Name, IntPtr Handle)> loaded, string inputFile, IntPtr ymmD3D11DevicePtr)
    {
        IntPtr avcodecHandle = IntPtr.Zero;
        IntPtr avutilHandle = IntPtr.Zero;
        foreach (var item in loaded)
        {
            if (item.Name.StartsWith("avcodec-", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Name, "avcodec.dll", StringComparison.OrdinalIgnoreCase))
                avcodecHandle = item.Handle;
            else if (item.Name.StartsWith("avutil-", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(item.Name, "avutil.dll", StringComparison.OrdinalIgnoreCase))
                avutilHandle = item.Handle;
        }

        if (avcodecHandle == IntPtr.Zero)
        {
            System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] Native codec-open probe skipped: avcodec handle missing.");
            return;
        }

        try
        {
            // We intentionally use the codec name already reported by the existing
            // ffprobe-based diagnostic path. This probe only tests decoder lookup,
            // context allocation, and avcodec_open2; it does not decode a frame yet.
            string? codecName = TryGetCodecNameWithFfprobe(inputFile);
            if (string.IsNullOrWhiteSpace(codecName))
            {
                System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] Native codec-open probe skipped: codec name unavailable.");
                return;
            }

            if (!NativeLibrary.TryGetExport(avcodecHandle, "avcodec_find_decoder_by_name", out IntPtr findDecoderPtr) ||
                !NativeLibrary.TryGetExport(avcodecHandle, "avcodec_alloc_context3", out IntPtr allocContextPtr) ||
                !NativeLibrary.TryGetExport(avcodecHandle, "avcodec_open2", out IntPtr open2Ptr) ||
                !NativeLibrary.TryGetExport(avcodecHandle, "avcodec_free_context", out IntPtr freeContextPtr))
            {
                System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] Native codec-open probe skipped: required exports missing.");
                return;
            }

            var findDecoder = Marshal.GetDelegateForFunctionPointer<AvCodecFindDecoderByNameDelegate>(findDecoderPtr);
            var allocContext = Marshal.GetDelegateForFunctionPointer<AvCodecAllocContext3Delegate>(allocContextPtr);
            var open2 = Marshal.GetDelegateForFunctionPointer<AvCodecOpen2Delegate>(open2Ptr);
            var freeContext = Marshal.GetDelegateForFunctionPointer<AvCodecFreeContextDelegate>(freeContextPtr);

            IntPtr codecNamePtr = Marshal.StringToCoTaskMemUTF8(codecName);
            IntPtr codec = IntPtr.Zero;
            IntPtr codecContext = IntPtr.Zero;
            try
            {
                codec = findDecoder(codecNamePtr);
                System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] avcodec_find_decoder_by_name({codecName}) => {(codec != IntPtr.Zero ? "NONNULL" : "NULL")}");
                if (codec == IntPtr.Zero)
                    return;

                RunHardwareCodecConfigProbe(loaded, codec, codecName);

                codecContext = allocContext(codec);
                System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] avcodec_alloc_context3({codecName}) => {(codecContext != IntPtr.Zero ? "NONNULL" : "NULL")}");
                if (codecContext == IntPtr.Zero)
                    return;

                // First try the real D3D11VA path. This is still a probe only: the
                // existing HDR FileSource remains on the known-good FFmpeg.exe path.
                // When a YMM4 D3D11Device is available, prefer an AVHWDeviceContext
                // wrapping that exact device so decoded D3D11 surfaces belong to the
                // same device as YMM4 instead of creating a second D3D11Device.
                if (!TryConfigureD3D11HardwareDecoder(loaded, codec, codecContext, ymmD3D11DevicePtr, out int hwPixFmt, out var getFormatDelegate, out bool deviceTransferred))
                {
                    System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] D3D11VA native decoder wiring probe: NOT CONFIGURED; using CPU decoder probe.");
                    long cpuStart = Stopwatch.GetTimestamp();
                    int cpuRc = open2(codecContext, codec, IntPtr.Zero);
                    double cpuMs = (Stopwatch.GetTimestamp() - cpuStart) * 1000.0 / Stopwatch.Frequency;
                    System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] avcodec_open2({codecName}) [CPU probe] rc={cpuRc}, elapsed={cpuMs:0.00}ms");
                    if (cpuRc == 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] Native decoder-open probe: SUCCESS codec={codecName} (CPU fallback)");
                        RunNativePacketFrameProbe(loaded, inputFile, codecContext, ymmD3D11DevicePtr);
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] Native decoder-open probe: FAILED codec={codecName}, rc={cpuRc}");
                    }
                }
                else
                {
                    long start = Stopwatch.GetTimestamp();
                    int rc = open2(codecContext, codec, IntPtr.Zero);
                    double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                    System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] avcodec_open2({codecName}) [D3D11VA] rc={rc}, elapsed={ms:0.00}ms, hw_pix_fmt={hwPixFmt}");
                    if (rc == 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] Native D3D11VA decoder-open probe: SUCCESS codec={codecName}");
                        RunNativePacketFrameProbe(loaded, inputFile, codecContext, ymmD3D11DevicePtr);
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] Native D3D11VA decoder-open probe: FAILED codec={codecName}, rc={rc}");
                    }
                    GC.KeepAlive(getFormatDelegate);
                }
            }
            finally
            {
                // NOTE: Do not call avcodec_free_context() from this diagnostic probe.
                // The D3D11VA codec context has been wired through ABI-level fields and
                // observed teardown can raise ExecutionEngineException inside native
                // cleanup. This probe is intentionally short-lived; keep the context
                // alive until process teardown while we validate GPU interop safely.
                if (codecContext != IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] Native codec-open probe: skipping avcodec_free_context() cleanup to avoid native teardown crash; temporary codec context will be reclaimed with process teardown.");
                    codecContext = IntPtr.Zero;
                }
                Marshal.FreeCoTaskMem(codecNamePtr);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] Native codec-open probe failed: {ex.GetType().Name}: {ex.Message}");
        }
    }


    private static bool TryConfigureD3D11HardwareDecoder(
        List<(string Name, IntPtr Handle)> loaded,
        IntPtr codec,
        IntPtr codecContext,
        IntPtr ymmD3D11DevicePtr,
        out int hwPixFmt,
        out AvGetFormatDelegate? getFormatDelegate,
        out bool deviceTransferred)
    {
        hwPixFmt = -1;
        getFormatDelegate = null;
        deviceTransferred = false;

        IntPtr avutilHandle = IntPtr.Zero;
        IntPtr avcodecHandle = IntPtr.Zero;
        foreach (var item in loaded)
        {
            if (item.Name.StartsWith("avutil-", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Name, "avutil.dll", StringComparison.OrdinalIgnoreCase))
                avutilHandle = item.Handle;
            else if (item.Name.StartsWith("avcodec-", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(item.Name, "avcodec.dll", StringComparison.OrdinalIgnoreCase))
                avcodecHandle = item.Handle;
        }

        if (avutilHandle == IntPtr.Zero || avcodecHandle == IntPtr.Zero || codecContext == IntPtr.Zero)
            return false;

        if (!NativeLibrary.TryGetExport(avutilHandle, "av_hwdevice_find_type_by_name", out IntPtr findTypePtr) ||
            !NativeLibrary.TryGetExport(avutilHandle, "av_hwdevice_ctx_create", out IntPtr createPtr) ||
            !NativeLibrary.TryGetExport(avutilHandle, "av_hwdevice_ctx_alloc", out IntPtr allocPtr) ||
            !NativeLibrary.TryGetExport(avutilHandle, "av_hwdevice_ctx_init", out IntPtr initPtr) ||
            !NativeLibrary.TryGetExport(avcodecHandle, "avcodec_get_hw_config", out IntPtr getConfigPtr))
        {
            System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] D3D11VA wiring probe: required exports missing.");
            return false;
        }

        try
        {
            // The FFmpeg.AutoGen binding is used only to obtain ABI-accurate field offsets
            // for the current AVCodecContext. We still call the actual FFmpeg functions via
            // the already-validated dynamically loaded delegates above.
            int hwDeviceOffset = checked((int)Marshal.OffsetOf<AVCodecContext>("hw_device_ctx"));
            int getFormatOffset = checked((int)Marshal.OffsetOf<AVCodecContext>("get_format"));
            int ctxSize = Marshal.SizeOf<AVCodecContext>();

            System.Diagnostics.Debug.WriteLine(
                $"[HDRInjector][FFmpegDLL] AVCodecContext ABI offsets: size={ctxSize}, hw_device_ctx=0x{hwDeviceOffset:X}, get_format=0x{getFormatOffset:X}");

            if ((hwDeviceOffset & 7) != 0 || (getFormatOffset & 7) != 0 || hwDeviceOffset < 0 || getFormatOffset < 0 ||
                hwDeviceOffset + IntPtr.Size > ctxSize || getFormatOffset + IntPtr.Size > ctxSize)
            {
                System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] D3D11VA wiring probe: ABI offsets failed sanity checks.");
                return false;
            }

            var findType = Marshal.GetDelegateForFunctionPointer<AvHwDeviceFindTypeByNameDelegate>(findTypePtr);
            var create = Marshal.GetDelegateForFunctionPointer<AvHwDeviceCtxCreateDelegate>(createPtr);
            var alloc = Marshal.GetDelegateForFunctionPointer<AvHwDeviceCtxAllocDelegate>(allocPtr);
            var init = Marshal.GetDelegateForFunctionPointer<AvHwDeviceCtxInitDelegate>(initPtr);
            var getConfig = Marshal.GetDelegateForFunctionPointer<AvCodecGetHwConfigDelegate>(getConfigPtr);

            IntPtr typeName = Marshal.StringToCoTaskMemUTF8("d3d11va");
            IntPtr deviceRef = IntPtr.Zero;
            try
            {
                int hwType = findType(typeName);
                if (hwType < 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] D3D11VA wiring probe: device type lookup failed rc={hwType}");
                    return false;
                }

                // Find the AV_PIX_FMT_D3D11 configuration specifically for a device-context
                // based decoder. This is exactly the configuration that Fix63 identified.
                for (int i = 0; i < 32; i++)
                {
                    IntPtr cfg = getConfig(codec, i);
                    if (cfg == IntPtr.Zero)
                        break;
                    int pixFmt = Marshal.ReadInt32(cfg, 0);
                    int methods = Marshal.ReadInt32(cfg, 4);
                    int deviceType = Marshal.ReadInt32(cfg, 8);
                    if ((methods & 0x01) != 0 && deviceType == hwType)
                    {
                        hwPixFmt = pixFmt;
                        break;
                    }
                }

                if (hwPixFmt < 0)
                {
                    System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] D3D11VA wiring probe: no HW_DEVICE_CTX config found.");
                    return false;
                }

                int createRc;
                if (ymmD3D11DevicePtr != IntPtr.Zero)
                {
                    // Build an AVHWDeviceContext around YMM4's existing D3D11Device.
                    // This avoids the separate FFmpeg-created D3D11Device that caused the
                    // decoded AVFrame texture to be owned by a different device.
                    deviceRef = alloc(hwType);
                    System.Diagnostics.Debug.WriteLine(
                        $"[HDRInjector][FFmpegDLL] D3D11VA wiring using existing YMM4 device: alloc ref={(deviceRef != IntPtr.Zero ? "NONNULL" : "NULL")}, hw_pix_fmt={hwPixFmt}");
                    if (deviceRef == IntPtr.Zero)
                        return false;

                    IntPtr hwDeviceContext = Marshal.ReadIntPtr(deviceRef, IntPtr.Size);
                    if (hwDeviceContext == IntPtr.Zero)
                    {
                        System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] Existing-device wiring failed: AVHWDeviceContext data=NULL.");
                        return false;
                    }

                    int hwctxOffset = checked((int)Marshal.OffsetOf<AVHWDeviceContext>("hwctx"));
                    IntPtr apiHwContext = Marshal.ReadIntPtr(hwDeviceContext, hwctxOffset);
                    if (apiHwContext == IntPtr.Zero)
                    {
                        System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] Existing-device wiring failed: D3D11VA API context=NULL.");
                        return false;
                    }

                    // AVD3D11VADeviceContext begins with ID3D11Device* device. Hold one COM
                    // reference for the lifetime of the native HW device context.
                    IntPtr vtable = Marshal.ReadIntPtr(ymmD3D11DevicePtr);
                    IntPtr addRefPtr = Marshal.ReadIntPtr(vtable, IntPtr.Size);
                    var addRef = Marshal.GetDelegateForFunctionPointer<AddRefDelegate>(addRefPtr);
                    uint addRefCount = addRef(ymmD3D11DevicePtr);
                    Marshal.WriteIntPtr(apiHwContext, 0, ymmD3D11DevicePtr);
                    System.Diagnostics.Debug.WriteLine(
                        $"[HDRInjector][FFmpegDLL] Existing YMM4 device assigned to HW context: device=0x{ymmD3D11DevicePtr.ToInt64():X}, AddRef={addRefCount}");

                    createRc = init(deviceRef);
                    System.Diagnostics.Debug.WriteLine(
                        $"[HDRInjector][FFmpegDLL] av_hwdevice_ctx_init(existing YMM4 device) => {createRc}");
                    if (createRc < 0)
                        return false;

                    IntPtr initializedDevice = Marshal.ReadIntPtr(apiHwContext, 0);
                    System.Diagnostics.Debug.WriteLine(
                        $"[HDRInjector][FFmpegDLL] Existing-device HW context initialized: deviceAfterInit=0x{initializedDevice.ToInt64():X}, samePointer={initializedDevice == ymmD3D11DevicePtr}");
                }
                else
                {
                    createRc = create(ref deviceRef, hwType, IntPtr.Zero, IntPtr.Zero, 0);
                    System.Diagnostics.Debug.WriteLine(
                        $"[HDRInjector][FFmpegDLL] D3D11VA wiring device create rc={createRc}, ref={(deviceRef != IntPtr.Zero ? "NONNULL" : "NULL")}, hw_pix_fmt={hwPixFmt}");
                    if (createRc < 0 || deviceRef == IntPtr.Zero)
                        return false;
                }

                // Copy the out-parameter value to a local before capturing it in the
                // unmanaged callback. C# does not allow an out/ref parameter to be
                // captured by a lambda.
                int selectedHwPixFmt = hwPixFmt;
                int selected = 0;
                getFormatDelegate = (ctx, formats) =>
                {
                    if (formats == IntPtr.Zero)
                        return -1;

                    for (int i = 0; i < 64; i++)
                    {
                        int fmt = Marshal.ReadInt32(formats, i * sizeof(int));
                        if (fmt == -1)
                            break;
                        if (fmt == selectedHwPixFmt)
                        {
                            if (Interlocked.Exchange(ref selected, 1) == 0)
                                System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] get_format selected D3D11 pix_fmt={fmt}");
                            return fmt;
                        }
                    }

                    if (Interlocked.Exchange(ref selected, -1) == 0)
                        System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] get_format did not offer D3D11; returning NONE.");
                    return -1;
                };

                // The native AVCodecContext is intentionally not freed by this diagnostic probe.
                // Keep the managed callback rooted for the same lifetime so native callbacks never
                // jump through a collected delegate after this method returns.
                lock (GetFormatRootLock)
                {
                    RootedGetFormatDelegate = getFormatDelegate;
                }

                IntPtr callbackPtr = Marshal.GetFunctionPointerForDelegate(getFormatDelegate);
                Marshal.WriteIntPtr(codecContext, hwDeviceOffset, deviceRef);
                Marshal.WriteIntPtr(codecContext, getFormatOffset, callbackPtr);
                deviceTransferred = true;
                deviceRef = IntPtr.Zero;

                System.Diagnostics.Debug.WriteLine(
                    $"[HDRInjector][FFmpegDLL] D3D11VA wiring applied: deviceMode={(ymmD3D11DevicePtr != IntPtr.Zero ? "existing-YMM4" : "ffmpeg-created")}, hw_device_ctx=0x{hwDeviceOffset:X}, get_format=0x{getFormatOffset:X}, callback=0x{callbackPtr.ToInt64():X}");
                return true;
            }
            finally
            {
                if (deviceRef != IntPtr.Zero && NativeLibrary.TryGetExport(avutilHandle, "av_buffer_unref", out IntPtr unrefPtr))
                {
                    var unref = Marshal.GetDelegateForFunctionPointer<AvBufferUnrefDelegate>(unrefPtr);
                    IntPtr tmp = deviceRef;
                    unref(ref tmp);
                }
                Marshal.FreeCoTaskMem(typeName);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] D3D11VA wiring probe failed safely: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void RunHardwareCodecConfigProbe(List<(string Name, IntPtr Handle)> loaded, IntPtr codec, string codecName)
    {
        IntPtr avcodecHandle = IntPtr.Zero;
        IntPtr avutilHandle = IntPtr.Zero;
        foreach (var item in loaded)
        {
            if (item.Name.StartsWith("avcodec-", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Name, "avcodec.dll", StringComparison.OrdinalIgnoreCase))
                avcodecHandle = item.Handle;
            else if (item.Name.StartsWith("avutil-", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(item.Name, "avutil.dll", StringComparison.OrdinalIgnoreCase))
                avutilHandle = item.Handle;
        }

        if (avcodecHandle == IntPtr.Zero || avutilHandle == IntPtr.Zero || codec == IntPtr.Zero)
        {
            System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] HW codec config probe skipped: required handles/codec missing.");
            return;
        }

        if (!NativeLibrary.TryGetExport(avcodecHandle, "avcodec_get_hw_config", out IntPtr getConfigPtr) ||
            !NativeLibrary.TryGetExport(avutilHandle, "av_hwdevice_get_type_name", out IntPtr getTypeNamePtr) ||
            !NativeLibrary.TryGetExport(avutilHandle, "av_get_pix_fmt_name", out IntPtr getPixFmtNamePtr))
        {
            System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] HW codec config probe skipped: required exports missing.");
            DebugMissingExport(avcodecHandle, "avcodec_get_hw_config");
            DebugMissingExport(avutilHandle, "av_hwdevice_get_type_name", "av_get_pix_fmt_name");
            return;
        }

        try
        {
            var getConfig = Marshal.GetDelegateForFunctionPointer<AvCodecGetHwConfigDelegate>(getConfigPtr);
            var getTypeName = Marshal.GetDelegateForFunctionPointer<AvHwDeviceGetTypeNameDelegate>(getTypeNamePtr);
            var getPixFmtName = Marshal.GetDelegateForFunctionPointer<AvGetPixFmtNameDelegate>(getPixFmtNamePtr);

            const int HwDeviceCtxMethod = 0x01;
            const int D3D11vaType = 7;
            int count = 0;
            bool d3d11Found = false;
            int d3d11PixFmt = -1;

            System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] HW config enumeration start codec={codecName}");
            for (int i = 0; i < 32; i++)
            {
                IntPtr cfg = getConfig(codec, i);
                if (cfg == IntPtr.Zero)
                    break;

                // AVCodecHWConfig public layout is:
                // enum AVPixelFormat pix_fmt; int methods; enum AVHWDeviceType device_type;
                // Three consecutive 32-bit enum/int values; no pointer fields.
                int pixFmt = Marshal.ReadInt32(cfg, 0);
                int methods = Marshal.ReadInt32(cfg, 4);
                int deviceType = Marshal.ReadInt32(cfg, 8);
                string pixFmtName = $"#{pixFmt}";
                IntPtr pixNamePtr = getPixFmtName(pixFmt);
                if (pixNamePtr != IntPtr.Zero)
                    pixFmtName = Marshal.PtrToStringAnsi(pixNamePtr) ?? pixFmtName;

                string deviceName = $"#{deviceType}";
                IntPtr typeNamePtr = getTypeName(deviceType);
                if (typeNamePtr != IntPtr.Zero)
                    deviceName = Marshal.PtrToStringAnsi(typeNamePtr) ?? deviceName;

                System.Diagnostics.Debug.WriteLine(
                    $"[HDRInjector][FFmpegDLL] HW config[{i}]: pix_fmt={pixFmtName}({pixFmt}), methods=0x{methods:X2}, device={deviceName}({deviceType})");
                count++;

                if ((methods & HwDeviceCtxMethod) != 0 && deviceType == D3D11vaType)
                {
                    d3d11Found = true;
                    d3d11PixFmt = pixFmt;
                }
            }

            System.Diagnostics.Debug.WriteLine(
                $"[HDRInjector][FFmpegDLL] D3D11VA codec config verdict: codec={codecName}, configs={count}, " +
                $"hw_device_ctx={(d3d11Found ? "SUPPORTED" : "NOT_FOUND")}, " +
                $"hw_pix_fmt={(d3d11Found ? d3d11PixFmt.ToString() : "N/A")}");

            if (d3d11Found)
            {
                IntPtr namePtr = getPixFmtName(d3d11PixFmt);
                string name = namePtr != IntPtr.Zero ? (Marshal.PtrToStringAnsi(namePtr) ?? $"#{d3d11PixFmt}") : $"#{d3d11PixFmt}";
                System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] D3D11VA selected HW pixel format candidate: {name} ({d3d11PixFmt})");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] HW codec config probe failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void RunNativePacketFrameProbe(List<(string Name, IntPtr Handle)> loaded, string inputFile, IntPtr codecContext, IntPtr ymmD3D11DevicePtr)
    {
        IntPtr avformatHandle = IntPtr.Zero;
        IntPtr avcodecHandle = IntPtr.Zero;
        IntPtr avutilHandle = IntPtr.Zero;
        foreach (var item in loaded)
        {
            if (item.Name.StartsWith("avformat-", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Name, "avformat.dll", StringComparison.OrdinalIgnoreCase))
                avformatHandle = item.Handle;
            else if (item.Name.StartsWith("avcodec-", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(item.Name, "avcodec.dll", StringComparison.OrdinalIgnoreCase))
                avcodecHandle = item.Handle;
            else if (item.Name.StartsWith("avutil-", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(item.Name, "avutil.dll", StringComparison.OrdinalIgnoreCase))
                avutilHandle = item.Handle;
        }

        if (avformatHandle == IntPtr.Zero || avcodecHandle == IntPtr.Zero || avutilHandle == IntPtr.Zero || codecContext == IntPtr.Zero)
        {
            System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] Native packet/frame probe skipped: required handles/context missing.");
            return;
        }

        try
        {
            bool ok = true;
            ok &= NativeLibrary.TryGetExport(avformatHandle, "avformat_open_input", out IntPtr openPtr);
            ok &= NativeLibrary.TryGetExport(avformatHandle, "avformat_close_input", out IntPtr closePtr);
            ok &= NativeLibrary.TryGetExport(avformatHandle, "av_read_frame", out IntPtr readFramePtr);
            ok &= NativeLibrary.TryGetExport(avcodecHandle, "av_packet_alloc", out IntPtr packetAllocPtr);
            ok &= NativeLibrary.TryGetExport(avcodecHandle, "av_packet_free", out IntPtr packetFreePtr);
            ok &= NativeLibrary.TryGetExport(avcodecHandle, "av_packet_unref", out IntPtr packetUnrefPtr);
            ok &= NativeLibrary.TryGetExport(avcodecHandle, "avcodec_send_packet", out IntPtr sendPacketPtr);
            ok &= NativeLibrary.TryGetExport(avcodecHandle, "avcodec_receive_frame", out IntPtr receiveFramePtr);
            ok &= NativeLibrary.TryGetExport(avutilHandle, "av_frame_alloc", out IntPtr frameAllocPtr);
            ok &= NativeLibrary.TryGetExport(avutilHandle, "av_frame_free", out IntPtr frameFreePtr);

            if (!ok)
            {
                System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] Native packet/frame probe skipped: one or more required exports missing.");
                DebugMissingExport(avformatHandle, "avformat_open_input", "avformat_close_input", "av_read_frame");
                DebugMissingExport(avcodecHandle, "av_packet_alloc", "av_packet_free", "av_packet_unref", "avcodec_send_packet", "avcodec_receive_frame");
                DebugMissingExport(avutilHandle, "av_frame_alloc", "av_frame_free");
                return;
            }

            var open = Marshal.GetDelegateForFunctionPointer<AvFormatOpenInputDelegate>(openPtr);
            var close = Marshal.GetDelegateForFunctionPointer<AvFormatCloseInputDelegate>(closePtr);
            var readFrame = Marshal.GetDelegateForFunctionPointer<AvFormatReadFrameDelegate>(readFramePtr);
            var packetAlloc = Marshal.GetDelegateForFunctionPointer<AvPacketAllocDelegate>(packetAllocPtr);
            var packetFree = Marshal.GetDelegateForFunctionPointer<AvPacketFreeDelegate>(packetFreePtr);
            var packetUnref = Marshal.GetDelegateForFunctionPointer<AvPacketUnrefDelegate>(packetUnrefPtr);
            var frameAlloc = Marshal.GetDelegateForFunctionPointer<AvFrameAllocDelegate>(frameAllocPtr);
            var frameFree = Marshal.GetDelegateForFunctionPointer<AvFrameFreeDelegate>(frameFreePtr);
            var sendPacket = Marshal.GetDelegateForFunctionPointer<AvCodecSendPacketDelegate>(sendPacketPtr);
            var receiveFrame = Marshal.GetDelegateForFunctionPointer<AvCodecReceiveFrameDelegate>(receiveFramePtr);

            IntPtr url = Marshal.StringToCoTaskMemUTF8(inputFile);
            IntPtr formatContext = IntPtr.Zero;
            IntPtr packet = IntPtr.Zero;
            IntPtr frame = IntPtr.Zero;
            try
            {
                IntPtr options = IntPtr.Zero;
                int openRc = open(ref formatContext, url, IntPtr.Zero, ref options);
                if (openRc < 0 || formatContext == IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] Native packet/frame probe: open failed rc={openRc}");
                    return;
                }

                packet = packetAlloc();
                frame = frameAlloc();
                System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] av_packet_alloc => {(packet != IntPtr.Zero ? "NONNULL" : "NULL")}");
                System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] av_frame_alloc => {(frame != IntPtr.Zero ? "NONNULL" : "NULL")}");
                if (packet == IntPtr.Zero || frame == IntPtr.Zero)
                    return;

                for (int attempt = 0; attempt < 32; attempt++)
                {
                    int readRc = readFrame(formatContext, packet);
                    System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] av_read_frame attempt={attempt + 1} rc={readRc}");
                    if (readRc < 0)
                        break;

                    long sendStart = Stopwatch.GetTimestamp();
                    int sendRc = sendPacket(codecContext, packet);
                    double sendMs = (Stopwatch.GetTimestamp() - sendStart) * 1000.0 / Stopwatch.Frequency;
                    System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] avcodec_send_packet attempt={attempt + 1} rc={sendRc}, elapsed={sendMs:0.00}ms");

                    if (sendRc >= 0)
                    {
                        long recvStart = Stopwatch.GetTimestamp();
                        int recvRc = receiveFrame(codecContext, frame);
                        double recvMs = (Stopwatch.GetTimestamp() - recvStart) * 1000.0 / Stopwatch.Frequency;
                        System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] avcodec_receive_frame attempt={attempt + 1} rc={recvRc}, elapsed={recvMs:0.00}ms");

                        if (recvRc == 0)
                        {
                            InspectDecodedAvFrame(loaded, frame, ymmD3D11DevicePtr);
                            return;
                        }
                    }

                    packetUnref(packet);
                    // Any negative send/receive code is logged above; continue to probe
                    // subsequent packets rather than assuming the first packet is video.
                }

                System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] Native packet/frame probe: no decoded frame received within 32 packets.");
            }
            finally
            {
                if (frame != IntPtr.Zero)
                {
                    IntPtr tmpFrame = frame;
                    frameFree(ref tmpFrame);
                    frame = IntPtr.Zero;
                }
                if (packet != IntPtr.Zero)
                {
                    IntPtr tmpPacket = packet;
                    packetFree(ref tmpPacket);
                    packet = IntPtr.Zero;
                }
                // NOTE: Do not call avformat_close_input() from this probe cleanup path.
                // The native D3D11VA codec context can outlive the temporary format context,
                // and this particular FFmpeg 8.x build was observed to raise an
                // ExecutionEngineException inside the generated interop wrapper here.
                // This probe is short-lived and intentionally leaves the temporary format
                // context for process teardown rather than risking a process-level crash.
                if (formatContext != IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] Native packet/frame probe: skipping avformat_close_input() cleanup to avoid native teardown crash; temporary context will be reclaimed with process teardown.");
                    formatContext = IntPtr.Zero;
                }
                Marshal.FreeCoTaskMem(url);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] Native packet/frame probe failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
    private static void InspectDecodedAvFrame(List<(string Name, IntPtr Handle)> loaded, IntPtr frame, IntPtr ymmD3D11DevicePtr)
    {
        if (frame == IntPtr.Zero)
        {
            System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] AVFrame inspection skipped: frame=NULL");
            return;
        }

        try
        {
            // AVFrame's public prefix has remained stable across the FFmpeg ABI used here:
            // data[8] (64 bytes) + linesize[8] (32 bytes) + extended_data (8 bytes),
            // followed by width, height, nb_samples and format. On 64-bit Windows,
            // format therefore sits at byte offset 116. We only read this documented
            // public prefix and never attempt to reinterpret the rest of AVFrame.
            if (IntPtr.Size != 8)
            {
                System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] AVFrame inspection skipped: 64-bit process required.");
                return;
            }

            const int Data0Offset = 0;
            const int Data1Offset = 8;
            const int ExtendedDataOffset = 96;
            const int WidthOffset = 104;
            const int HeightOffset = 108;
            const int NbSamplesOffset = 112;
            const int FormatOffset = 116;

            IntPtr data0 = Marshal.ReadIntPtr(frame, Data0Offset);
            IntPtr data1 = Marshal.ReadIntPtr(frame, Data1Offset);
            IntPtr extendedData = Marshal.ReadIntPtr(frame, ExtendedDataOffset);
            int width = Marshal.ReadInt32(frame, WidthOffset);
            int height = Marshal.ReadInt32(frame, HeightOffset);
            int nbSamples = Marshal.ReadInt32(frame, NbSamplesOffset);
            int format = Marshal.ReadInt32(frame, FormatOffset);

            string formatName = $"#{format}";
            IntPtr avutilHandle = IntPtr.Zero;
            foreach (var item in loaded)
            {
                if (item.Name.StartsWith("avutil-", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.Name, "avutil.dll", StringComparison.OrdinalIgnoreCase))
                {
                    avutilHandle = item.Handle;
                    break;
                }
            }

            if (avutilHandle != IntPtr.Zero &&
                NativeLibrary.TryGetExport(avutilHandle, "av_get_pix_fmt_name", out IntPtr getNamePtr))
            {
                var getName = Marshal.GetDelegateForFunctionPointer<AvGetPixFmtNameDelegate>(getNamePtr);
                IntPtr namePtr = getName(format);
                if (namePtr != IntPtr.Zero)
                {
                    formatName = Marshal.PtrToStringAnsi(namePtr) ?? formatName;
                }
            }

            bool looksLikeD3D11 = formatName.Equals("d3d11", StringComparison.OrdinalIgnoreCase);
            System.Diagnostics.Debug.WriteLine(
                $"[HDRInjector][FFmpegDLL] AVFrame inspect: format={formatName} ({format}), " +
                $"width={width}, height={height}, nb_samples={nbSamples}, " +
                $"data0={(data0 != IntPtr.Zero ? "NONNULL" : "NULL")}, " +
                $"data1={(data1 != IntPtr.Zero ? "NONNULL" : "NULL")}, " +
                $"extended_data={(extendedData != IntPtr.Zero ? "NONNULL" : "NULL")}, " +
                $"d3d11={looksLikeD3D11}");

            if (looksLikeD3D11)
            {
                long index = data1.ToInt64();
                System.Diagnostics.Debug.WriteLine(
                    $"[HDRInjector][FFmpegDLL] AVFrame D3D11 surface candidate: texturePtr=0x{data0.ToInt64():X}, textureArrayIndex={index}");
                InspectD3D11Texture(data0, index, ymmD3D11DevicePtr);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] AVFrame inspection failed: {ex.GetType().Name}: {ex.Message}");
        }
    }


    private static void InspectD3D11Texture(IntPtr texturePtr, long arrayIndex, IntPtr ymmD3D11DevicePtr)
    {
        if (texturePtr == IntPtr.Zero)
        {
            System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] D3D11 texture inspection skipped: texturePtr=NULL");
            return;
        }

        try
        {
            if (IntPtr.Size != 8)
            {
                System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] D3D11 texture inspection skipped: 64-bit process required.");
                return;
            }

            // ID3D11Texture2D::GetDesc is vtable slot 10:
            // IUnknown(0..2), ID3D11DeviceChild(3..6), ID3D11Resource(7..9), GetDesc(10).
            IntPtr vtable = Marshal.ReadIntPtr(texturePtr);
            if (vtable == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] D3D11 texture inspection failed: vtable=NULL");
                return;
            }

            IntPtr getDescPtr = Marshal.ReadIntPtr(vtable, 10 * IntPtr.Size);
            if (getDescPtr == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] D3D11 texture inspection failed: GetDesc pointer=NULL");
                return;
            }

            var getDesc = Marshal.GetDelegateForFunctionPointer<GetTexture2DDescDelegate>(getDescPtr);
            Texture2DDescNative desc = default;
            getDesc(texturePtr, ref desc);

            System.Diagnostics.Debug.WriteLine(
                $"[HDRInjector][FFmpegDLL] D3D11 texture desc: width={desc.Width}, height={desc.Height}, " +
                $"mipLevels={desc.MipLevels}, arraySize={desc.ArraySize}, format={desc.Format}, " +
                $"sampleCount={desc.SampleCount}, sampleQuality={desc.SampleQuality}, usage={desc.Usage}, " +
                $"bindFlags=0x{desc.BindFlags:X8}, cpuAccess=0x{desc.CpuAccessFlags:X8}, misc=0x{desc.MiscFlags:X8}");

            bool indexValid = arrayIndex >= 0 && arrayIndex < desc.ArraySize;
            System.Diagnostics.Debug.WriteLine(
                $"[HDRInjector][FFmpegDLL] D3D11 texture surface validation: arrayIndex={arrayIndex}, " +
                $"indexValid={indexValid}, sameSize={desc.Width == 3840 && desc.Height == 2160}");

            LogD3D11Shareability(desc);
            CompareD3D11Devices(texturePtr, ymmD3D11DevicePtr);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] D3D11 texture inspection failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void LogD3D11Shareability(Texture2DDescNative desc)
    {
        const uint ResourceMiscShared = 0x00000002;
        const uint ResourceMiscSharedKeyedMutex = 0x00000100;
        const uint ResourceMiscSharedNthandle = 0x00000800;

        uint misc = desc.MiscFlags;
        bool shared = (misc & ResourceMiscShared) != 0;
        bool keyedMutex = (misc & ResourceMiscSharedKeyedMutex) != 0;
        bool sharedNtHandle = (misc & ResourceMiscSharedNthandle) != 0;

        System.Diagnostics.Debug.WriteLine(
            $"[HDRInjector][FFmpegDLL] D3D11 texture shareability: " +
            $"misc=0x{misc:X8}, shared={shared}, keyedMutex={keyedMutex}, sharedNtHandle={sharedNtHandle}");

        if (!shared && !sharedNtHandle)
        {
            System.Diagnostics.Debug.WriteLine(
                "[HDRInjector][FFmpegDLL] D3D11 texture is NOT marked shared; direct cross-device OpenSharedResource path is unavailable for this surface.");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine(
                "[HDRInjector][FFmpegDLL] D3D11 texture has a sharing-capable misc flag; a cross-device sharing experiment may be possible later.");
        }
    }

    private static void CompareD3D11Devices(IntPtr texturePtr, IntPtr ymmD3D11DevicePtr)
    {
        if (texturePtr == IntPtr.Zero)
        {
            System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] Device compare skipped: texture=NULL");
            return;
        }

        if (ymmD3D11DevicePtr == IntPtr.Zero)
        {
            System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] Device compare skipped: YMM4 D3D11 device pointer=NULL");
            return;
        }

        try
        {
            // ID3D11DeviceChild::GetDevice is vtable slot 3:
            // IUnknown(0..2), GetDevice(3).
            IntPtr vtable = Marshal.ReadIntPtr(texturePtr);
            if (vtable == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] Device compare failed: texture vtable=NULL");
                return;
            }

            IntPtr getDevicePtr = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
            if (getDevicePtr == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] Device compare failed: GetDevice pointer=NULL");
                return;
            }

            var getDevice = Marshal.GetDelegateForFunctionPointer<GetD3D11DeviceDelegate>(getDevicePtr);
            IntPtr ffmpegDevicePtr = IntPtr.Zero;
            getDevice(texturePtr, out ffmpegDevicePtr);

            if (ffmpegDevicePtr == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] FFmpeg texture GetDevice => NULL");
                return;
            }

            try
            {
                bool samePointer = ffmpegDevicePtr == ymmD3D11DevicePtr;
                System.Diagnostics.Debug.WriteLine(
                    $"[HDRInjector][FFmpegDLL] D3D11 device compare: ffmpegDevice=0x{ffmpegDevicePtr.ToInt64():X}, " +
                    $"ymmDevice=0x{ymmD3D11DevicePtr.ToInt64():X}, samePointer={samePointer}");

                if (samePointer)
                {
                    System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] D3D11 device compatibility: SAME DEVICE");
                    TrySameDeviceGpuCopy(texturePtr, ymmD3D11DevicePtr);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] D3D11 device compatibility: DIFFERENT DEVICE (sharing/copy path may be required)");
                }

                // Fix72: adapter comparison disabled temporarily.
                // The manual IDXGIDevice/IDXGIAdapter COM probing can corrupt the host process;
                // same-device comparison above is retained, while adapter identity is deferred.
                System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] Fix72: skipping IDXGIDevice/IDXGIAdapter adapter comparison to isolate native heap corruption.");
            }
            finally
            {
                // Fix72: deliberately keep this temporary COM reference alive until process teardown.
                // We are isolating native heap corruption in the diagnostic probe; do not change production code.
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] D3D11 device compare failed: {ex.GetType().Name}: {ex.Message}");
        }
    }


    private static void TrySameDeviceGpuCopy(IntPtr sourceTexturePtr, IntPtr ymmDevicePtr)
    {
        if (sourceTexturePtr == IntPtr.Zero || ymmDevicePtr == IntPtr.Zero)
            return;

        try
        {
            var device = new Vortice.Direct3D11.ID3D11Device(ymmDevicePtr);
            var source = new Vortice.Direct3D11.ID3D11Texture2D(sourceTexturePtr);
            var desc = source.Description;

            var dstDesc = new Vortice.Direct3D11.Texture2DDescription
            {
                Width = desc.Width,
                Height = desc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = (Vortice.DXGI.Format)desc.Format,
                SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                Usage = Vortice.Direct3D11.ResourceUsage.Default,
                BindFlags = Vortice.Direct3D11.BindFlags.ShaderResource,
                CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.None,
                MiscFlags = Vortice.Direct3D11.ResourceOptionFlags.None,
            };

            using var destination = device.CreateTexture2D(dstDesc);
            var context = device.ImmediateContext;
            if (destination == null || context == null)
            {
                System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] Same-device GPU copy: destination/context unavailable.");
                return;
            }

            // The FFmpeg AVFrame exposes the actual array slice in data[1]. The caller
            // currently passes only the texture pointer, so the structural copy probe
            // intentionally copies slice 0. The next stage will carry the real index.
            const int sourceSlice = 0;
            context.CopySubresourceRegion(destination, 0, 0, 0, 0, source, sourceSlice, null);
            System.Diagnostics.Debug.WriteLine(
                $"[HDRInjector][FFmpegDLL] Same-device GPU copy: SUCCESS, srcSlice={sourceSlice}, srcFormat={desc.Format}, size={desc.Width}x{desc.Height}");

            TryGpuFp16ShaderPath(device, source, sourceSlice);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[HDRInjector][FFmpegDLL] Same-device GPU copy: FAILED {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void TryGpuFp16ShaderPath(Vortice.Direct3D11.ID3D11Device device, Vortice.Direct3D11.ID3D11Texture2D source, int sourceSlice)
    {
        Vortice.Direct3D11.ID3D11ShaderResourceView? srv = null;
        try
        {
            var desc = source.Description;
            var context = device.ImmediateContext;
            if (context == null)
                throw new InvalidOperationException("ImmediateContext unavailable.");

            System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] GPU FP16 diagnostic: sourceFormat={desc.Format}, sourceSlice={sourceSlice}, arraySize={desc.ArraySize}");

            // The decoder-owned P010 texture uses BIND_DECODER and cannot be bound as an SRV directly.
            // First copy the selected array slice into a normal P010 texture created with SHADER_RESOURCE.
            var readableDesc = new Vortice.Direct3D11.Texture2DDescription
            {
                Width = desc.Width,
                Height = desc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Vortice.DXGI.Format.P010,
                SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                Usage = Vortice.Direct3D11.ResourceUsage.Default,
                BindFlags = Vortice.Direct3D11.BindFlags.ShaderResource,
                CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.None,
                MiscFlags = Vortice.Direct3D11.ResourceOptionFlags.None,
            };

            using var readableP010 = device.CreateTexture2D(readableDesc);
            System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] GPU FP16 diagnostic: shader-readable P010 intermediate creation=SUCCESS");

            context.CopySubresourceRegion(readableP010, 0, 0, 0, 0, source, sourceSlice, null);
            System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] GPU FP16 diagnostic: decoder P010 -> shader-readable P010 GPU copy=SUCCESS");

            // Now create an explicit luma SRV on the shader-readable P010 intermediate.
            var lumaSrvDesc = new Vortice.Direct3D11.ShaderResourceViewDescription
            {
                Format = Vortice.DXGI.Format.R16_UNorm,
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = new Vortice.Direct3D11.Texture2DShaderResourceView
                {
                    MostDetailedMip = 0,
                    MipLevels = 1,
                },
            };

            try
            {
                srv = device.CreateShaderResourceView(readableP010, lumaSrvDesc);
                System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] GPU FP16 diagnostic: intermediate R16_UNORM luma SRV creation=SUCCESS");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] GPU FP16 diagnostic: intermediate R16_UNORM luma SRV creation=FAILED {ex.GetType().Name}: {ex.Message}");
                return;
            }

            var outputDesc = new Vortice.Direct3D11.Texture2DDescription
            {
                Width = desc.Width,
                Height = desc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Vortice.DXGI.Format.R16G16B16A16_Float,
                SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                Usage = Vortice.Direct3D11.ResourceUsage.Default,
                BindFlags = Vortice.Direct3D11.BindFlags.ShaderResource | Vortice.Direct3D11.BindFlags.UnorderedAccess,
                CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.None,
                MiscFlags = Vortice.Direct3D11.ResourceOptionFlags.None,
            };

            using var output = device.CreateTexture2D(outputDesc);
            System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] GPU FP16 diagnostic: FP16 output texture creation=SUCCESS");

            using var uav = device.CreateUnorderedAccessView(output);
            System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] GPU FP16 diagnostic: FP16 UAV creation=SUCCESS");

            using var shader = device.CreateComputeShader(LoadEmbeddedShader("HDRInjector.Shaders.HdrP010ProbeCS.cso"));
            System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] GPU FP16 diagnostic: compute shader creation=SUCCESS");

            context.CSSetShader(shader);
            context.CSSetShaderResource(0, srv);
            context.CSSetUnorderedAccessView(0, uav);
            System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] GPU FP16 diagnostic: resource binding=SUCCESS");

            context.Dispatch((desc.Width + 7) / 8, (desc.Height + 7) / 8, 1);
            System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] GPU FP16 diagnostic: Dispatch=SUCCESS");

            context.CSSetShaderResources(0, new Vortice.Direct3D11.ID3D11ShaderResourceView[] { null! });
            context.CSSetUnorderedAccessViews(0, new Vortice.Direct3D11.ID3D11UnorderedAccessView[] { null! });
            context.CSSetShader(null);
            context.Flush();

            System.Diagnostics.Debug.WriteLine(
                $"[HDRInjector][FFmpegDLL] GPU FP16 shader path: DISPATCH SUCCESS, input=P010/R16 luma, output=R16G16B16A16_FLOAT, size={desc.Width}x{desc.Height}");

            TryGpuHlgYuvToFp16(device, readableP010, sourceSlice, desc.Width, desc.Height);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[HDRInjector][FFmpegDLL] GPU FP16 shader path: FAILED {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            srv?.Dispose();
        }
    }


    private static void TryGpuHlgYuvToFp16(Vortice.Direct3D11.ID3D11Device device, Vortice.Direct3D11.ID3D11Texture2D readableP010, int sourceSlice, int width, int height)
    {
        try
        {
            var context = device.ImmediateContext;
            if (context == null)
                throw new InvalidOperationException("ImmediateContext unavailable.");

            System.Diagnostics.Debug.WriteLine(
                $"[HDRInjector][FFmpegDLL] GPU HLG YUV diagnostic: input=P010, slice={sourceSlice}, size={width}x{height}");

            var ySrvDesc = new Vortice.Direct3D11.ShaderResourceViewDescription
            {
                Format = Vortice.DXGI.Format.R16_UNorm,
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = new Vortice.Direct3D11.Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = 1 }
            };
            using var ySrv = device.CreateShaderResourceView(readableP010, ySrvDesc);
            System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] GPU HLG YUV diagnostic: Y SRV=SUCCESS");

            var uvSrvDesc = new Vortice.Direct3D11.ShaderResourceViewDescription
            {
                Format = Vortice.DXGI.Format.R16G16_UNorm,
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = new Vortice.Direct3D11.Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = 1 }
            };
            using var uvSrv = device.CreateShaderResourceView(readableP010, uvSrvDesc);
            System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] GPU HLG YUV diagnostic: UV SRV=SUCCESS");

            var outputDesc = new Vortice.Direct3D11.Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Vortice.DXGI.Format.R16G16B16A16_Float,
                SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                Usage = Vortice.Direct3D11.ResourceUsage.Default,
                BindFlags = Vortice.Direct3D11.BindFlags.ShaderResource | Vortice.Direct3D11.BindFlags.UnorderedAccess,
                CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.None,
                MiscFlags = Vortice.Direct3D11.ResourceOptionFlags.None,
            };
            using var output = device.CreateTexture2D(outputDesc);
            using var uav = device.CreateUnorderedAccessView(output);
            System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] GPU HLG YUV diagnostic: FP16 output/UAV=SUCCESS");

            using var shader = device.CreateComputeShader(LoadEmbeddedShader("HDRInjector.Shaders.HdrP010ToRgbCS.cso"));
            System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] GPU HLG YUV diagnostic: shader creation=SUCCESS");

            context.CSSetShader(shader);
            context.CSSetShaderResources(0, new[] { ySrv, uvSrv });
            context.CSSetUnorderedAccessViews(0, new[] { uav });
            context.Dispatch((width + 7) / 8, (height + 7) / 8, 1);
            System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] GPU HLG YUV diagnostic: YUV420 10-bit -> FP16 Dispatch=SUCCESS");

            context.CSSetShaderResources(0, new Vortice.Direct3D11.ID3D11ShaderResourceView[] { null!, null! });
            context.CSSetUnorderedAccessViews(0, new Vortice.Direct3D11.ID3D11UnorderedAccessView[] { null! });
            context.CSSetShader(null);
            context.Flush();

            System.Diagnostics.Debug.WriteLine(
                $"[HDRInjector][FFmpegDLL] GPU HLG YUV path: SUCCESS, input=P010(Y+UV), output=R16G16B16A16_FLOAT, size={width}x{height}, slice={sourceSlice}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[HDRInjector][FFmpegDLL] GPU HLG YUV path: FAILED {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static byte[] LoadEmbeddedShader(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded shader not found: {resourceName}");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static void CompareD3D11DeviceAdapters(IntPtr ffmpegDevicePtr, IntPtr ymmDevicePtr)
    {
        try
        {
            if (ffmpegDevicePtr == IntPtr.Zero || ymmDevicePtr == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine("[HDRInjector][FFmpegDLL] Adapter compare skipped: device pointer NULL");
                return;
            }

            Guid iidDxgiDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C"); // IDXGIDevice
            IntPtr ffmpegDxgiDevice = QueryInterface(ffmpegDevicePtr, iidDxgiDevice);
            IntPtr ymmDxgiDevice = QueryInterface(ymmDevicePtr, iidDxgiDevice);
            if (ffmpegDxgiDevice == IntPtr.Zero || ymmDxgiDevice == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[HDRInjector][FFmpegDLL] IDXGIDevice QueryInterface failed: ffmpeg={(ffmpegDxgiDevice != IntPtr.Zero)}, ymm={(ymmDxgiDevice != IntPtr.Zero)}");
                ReleaseCom(ffmpegDxgiDevice);
                ReleaseCom(ymmDxgiDevice);
                return;
            }

            try
            {
                IntPtr ffmpegAdapter = GetDxgiAdapter(ffmpegDxgiDevice);
                IntPtr ymmAdapter = GetDxgiAdapter(ymmDxgiDevice);
                if (ffmpegAdapter == IntPtr.Zero || ymmAdapter == IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[HDRInjector][FFmpegDLL] IDXGIAdapter acquisition failed: ffmpeg={(ffmpegAdapter != IntPtr.Zero)}, ymm={(ymmAdapter != IntPtr.Zero)}");
                    ReleaseCom(ffmpegAdapter);
                    ReleaseCom(ymmAdapter);
                    return;
                }

                try
                {
                    long ffmpegLuid = GetDxgiAdapterLuid(ffmpegAdapter);
                    long ymmLuid = GetDxgiAdapterLuid(ymmAdapter);
                    bool sameAdapter = ffmpegLuid != 0 && ffmpegLuid == ymmLuid;

                    System.Diagnostics.Debug.WriteLine(
                        $"[HDRInjector][FFmpegDLL] D3D11 adapter compare: ffmpegLuid=0x{ffmpegLuid:X16}, ymmLuid=0x{ymmLuid:X16}, sameAdapter={sameAdapter}");

                    System.Diagnostics.Debug.WriteLine(
                        sameAdapter
                            ? "[HDRInjector][FFmpegDLL] D3D11 adapter compatibility: SAME ADAPTER/GPU"
                            : "[HDRInjector][FFmpegDLL] D3D11 adapter compatibility: DIFFERENT ADAPTER/GPU (check adapter selection)" );
                }
                finally
                {
                    ReleaseCom(ffmpegAdapter);
                    ReleaseCom(ymmAdapter);
                }
            }
            finally
            {
                ReleaseCom(ffmpegDxgiDevice);
                ReleaseCom(ymmDxgiDevice);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] Adapter compare failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static IntPtr QueryInterface(IntPtr comObject, Guid iid)
    {
        IntPtr vtable = Marshal.ReadIntPtr(comObject);
        IntPtr queryPtr = Marshal.ReadIntPtr(vtable, 0 * IntPtr.Size);
        var query = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(queryPtr);
        IntPtr iidPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
        try
        {
            Marshal.StructureToPtr(iid, iidPtr, false);
            int hr = query(comObject, iidPtr, out IntPtr result);
            return hr >= 0 ? result : IntPtr.Zero;
        }
        finally
        {
            Marshal.FreeHGlobal(iidPtr);
        }
    }

    private static IntPtr GetDxgiAdapter(IntPtr dxgiDevice)
    {
        IntPtr vtable = Marshal.ReadIntPtr(dxgiDevice);
        IntPtr getAdapterPtr = Marshal.ReadIntPtr(vtable, 7 * IntPtr.Size);
        var getAdapter = Marshal.GetDelegateForFunctionPointer<GetDxgiAdapterDelegate>(getAdapterPtr);
        int hr = getAdapter(dxgiDevice, out IntPtr adapter);
        return hr >= 0 ? adapter : IntPtr.Zero;
    }

    private static long GetDxgiAdapterLuid(IntPtr adapter)
    {
        // IDXGIAdapter::GetDesc is vtable slot 8. DXGI_ADAPTER_DESC.AdapterLuid is
        // at byte offset 272 in the 288-byte structure (Description[128] WCHARs + 4 uints).
        IntPtr vtable = Marshal.ReadIntPtr(adapter);
        IntPtr getDescPtr = Marshal.ReadIntPtr(vtable, 8 * IntPtr.Size);
        var getDesc = Marshal.GetDelegateForFunctionPointer<GetDxgiAdapterDescDelegate>(getDescPtr);
        IntPtr desc = Marshal.AllocHGlobal(288);
        try
        {
            Span<byte> zero = stackalloc byte[288];
            zero.Clear();
            Marshal.Copy(zero.ToArray(), 0, desc, 288);
            int hr = getDesc(adapter, desc);
            if (hr < 0)
                return 0;
            return Marshal.ReadInt64(desc, 272);
        }
        finally
        {
            Marshal.FreeHGlobal(desc);
        }
    }

    private static void ReleaseCom(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return;
        try { Marshal.Release(ptr); } catch { }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceDelegate(IntPtr self, IntPtr riid, out IntPtr ppvObject);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDxgiAdapterDelegate(IntPtr self, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDxgiAdapterDescDelegate(IntPtr self, IntPtr desc);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GetD3D11DeviceDelegate(IntPtr self, out IntPtr device);

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDescNative
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleCount;
        public uint SampleQuality;
        public uint Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GetTexture2DDescDelegate(IntPtr self, ref Texture2DDescNative desc);

    private static string? TryGetCodecNameWithFfprobe(string inputFile)
    {
        try
        {
            string root = FindFfmpegDirectory();
            string ffprobe = Path.Combine(root, "ffprobe.exe");
            if (!File.Exists(ffprobe))
            {
                // Some YMM4 builds ship ffprobe next to ffmpeg.exe only under a
                // different executable name. Keep this probe non-fatal.
                return null;
            }

            var psi = new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = $"-v error -select_streams v:0 -show_entries stream=codec_name -of default=nw=1:nk=1 \"{inputFile.Replace("\\\"", "\\\\\"")}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi);
            if (process == null) return null;
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.StandardError.ReadToEnd();
            process.WaitForExit(3000);
            return string.IsNullOrWhiteSpace(output) ? null : output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        }
        catch
        {
            return null;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AvGetFormatDelegate(IntPtr avctx, IntPtr pixFmts);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AvFormatOpenInputDelegate(ref IntPtr ps, IntPtr url, IntPtr fmt, ref IntPtr options);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AvFormatCloseInputDelegate(ref IntPtr ic);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AvCodecGetHwConfigDelegate(IntPtr codec, int index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AvHwDeviceGetTypeNameDelegate(int type);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AvCodecFindDecoderByNameDelegate(IntPtr name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AvCodecAllocContext3Delegate(IntPtr codec);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AvCodecOpen2Delegate(IntPtr avctx, IntPtr codec, IntPtr options);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AvCodecFreeContextDelegate(ref IntPtr avctx);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AvFormatReadFrameDelegate(IntPtr s, IntPtr packet);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AvPacketAllocDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AvPacketFreeDelegate(ref IntPtr packet);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AvPacketUnrefDelegate(IntPtr packet);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AvFrameAllocDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AvFrameFreeDelegate(ref IntPtr frame);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AvCodecSendPacketDelegate(IntPtr avctx, IntPtr avpkt);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AvCodecReceiveFrameDelegate(IntPtr avctx, IntPtr frame);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AvGetPixFmtNameDelegate(int pixFmt);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AvFormatFindStreamInfoDelegate(IntPtr ic, IntPtr options);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AvHwDeviceFindTypeByNameDelegate(IntPtr name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AvHwDeviceCtxCreateDelegate(ref IntPtr deviceRef, int type, IntPtr device, IntPtr options, int flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AvHwDeviceCtxAllocDelegate(int type);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AvHwDeviceCtxInitDelegate(IntPtr bufferRef);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint AddRefDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AvBufferUnrefDelegate(ref IntPtr buffer);

    private static void DebugMissingExport(IntPtr handle, params string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                bool found = handle != IntPtr.Zero && NativeLibrary.TryGetExport(handle, name, out _);
                System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] export {name}: {(found ? "FOUND" : "MISSING")}");
            }
            catch
            {
                System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] export {name}: ERROR");
            }
        }
    }

    private static void CheckExport(string dllName, IntPtr handle, string exportName)
    {
        try
        {
            if (NativeLibrary.TryGetExport(handle, exportName, out _))
                System.Diagnostics.Debug.WriteLine($"[HDRInjector][FFmpegDLL] {dllName}: export {exportName} FOUND");
        }
        catch { }
    }

    private static string? FindVersionedDll(string root, string baseName)
    {
        string exact = Path.Combine(root, baseName + ".dll");
        if (File.Exists(exact)) return exact;

        string[] matches;
        try
        {
            matches = Directory.GetFiles(root, baseName + "-*.dll", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return null;
        }

        Array.Sort(matches, StringComparer.OrdinalIgnoreCase);
        return matches.Length == 0 ? null : matches[^1];
    }

    private static string FindFfmpegDirectory()
    {
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(appDir, "Resources", "bin", "x64", "ffmpeg"),
            Path.Combine(appDir, "ffmpeg"),
            Path.Combine(appDir, "tools", "ffmpeg"),
        };

        foreach (string candidate in candidates)
        {
            if (Directory.Exists(candidate))
                return candidate;
        }

        // Development installation fallback. This is intentionally based on the
        // same YMM4 installation path used by the existing test environment.
        const string knownYmmRoot = @"K:\開発用YMM4\YukkuriMovieMaker_v4_Lite";
        string known = Path.Combine(knownYmmRoot, "Resources", "bin", "x64", "ffmpeg");
        return Directory.Exists(known) ? known : string.Empty;
    }
}
