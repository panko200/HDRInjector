using System;
using System.Diagnostics;
using System.Text;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using VorticeD3D11Device = Vortice.Direct3D11.ID3D11Device;
using VorticeD3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;
using VorticeD3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;
using VorticeD3D11ComputeShader = Vortice.Direct3D11.ID3D11ComputeShader;
using VorticeD3D11Buffer = Vortice.Direct3D11.ID3D11Buffer;
using VorticeD3D11ShaderResourceView = Vortice.Direct3D11.ID3D11ShaderResourceView;
using VorticeD3D11UnorderedAccessView = Vortice.Direct3D11.ID3D11UnorderedAccessView;
using Vortice.DXGI;

namespace HDRInjector.FileSource;

internal static class GpuLeakDiagnostics
{
    private static long createdTexture2D;
    private static long disposedTexture2D;
    private static long createdSrv;
    private static long disposedSrv;
    private static long createdUav;
    private static long disposedUav;
    private static long createdD2DBitmap;
    private static long disposedD2DBitmap;
    private static long createdDxgiSurface;
    private static long disposedDxgiSurface;
    private static long createdSourceTextureWrapper;
    private static long disposedSourceTextureWrapper;
    private static long nextBitmapId;
    private static readonly object BitmapLock = new();
    private static readonly Dictionary<ID2D1Bitmap1, long> BitmapIds =
        new(ReferenceEqualityComparer.Instance);
    private static long logs;

    public static void CreatedTexture() => Interlocked.Increment(ref createdTexture2D);
    public static void DisposedTexture() => Interlocked.Increment(ref disposedTexture2D);
    public static void CreatedSrv() => Interlocked.Increment(ref createdSrv);
    public static void DisposedSrv() => Interlocked.Increment(ref disposedSrv);
    public static void CreatedUav() => Interlocked.Increment(ref createdUav);
    public static void DisposedUav() => Interlocked.Increment(ref disposedUav);

    public static long TrackD2DBitmap(ID2D1Bitmap1 bitmap, int frame)
    {
        long id = Interlocked.Increment(ref nextBitmapId);
        IntPtr ptr = bitmap.NativePointer;
        lock (BitmapLock)
        {
            BitmapIds[bitmap] = id;
        }
        Interlocked.Increment(ref createdD2DBitmap);
        Debug.WriteLine($"[HDRInjector][HDRGPU][D2D] CREATE bitmap={id} ptr=0x{ptr.ToInt64():X} frame={frame}");
        return id;
    }

    public static void RetireD2DBitmap(ID2D1Bitmap1 bitmap, int frame, int retiredQueueCount)
    {
        long id;
        IntPtr ptr;
        lock (BitmapLock)
        {
            BitmapIds.TryGetValue(bitmap, out id);
            ptr = bitmap.NativePointer;
        }
        Debug.WriteLine($"[HDRInjector][HDRGPU][D2D] RETIRE bitmap={id} ptr=0x{ptr.ToInt64():X} frame={frame} queue={retiredQueueCount}");
    }

    public static void DisposeD2DBitmap(ID2D1Bitmap1 bitmap, int frame, string phase)
    {
        long id;
        IntPtr ptr;
        lock (BitmapLock)
        {
            BitmapIds.TryGetValue(bitmap, out id);
            ptr = bitmap.NativePointer;
        }

        try
        {
            bitmap.Dispose();
            Interlocked.Increment(ref disposedD2DBitmap);
            lock (BitmapLock)
            {
                BitmapIds.Remove(bitmap);
            }
            Debug.WriteLine($"[HDRInjector][HDRGPU][D2D] DISPOSE bitmap={id} ptr=0x{ptr.ToInt64():X} frame={frame} phase={phase} SUCCESS");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HDRInjector][HDRGPU][D2D] DISPOSE_FAILED bitmap={id} ptr=0x{ptr.ToInt64():X} frame={frame} phase={phase} error={ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    public static void D2DBitmapDisposeFailed(ID2D1Bitmap1 bitmap, Exception ex, int frame, string phase)
    {
        long id;
        IntPtr ptr;
        lock (BitmapLock)
        {
            BitmapIds.TryGetValue(bitmap, out id);
            ptr = bitmap.NativePointer;
        }
        Debug.WriteLine($"[HDRInjector][HDRGPU][D2D] DISPOSE_FAILED bitmap={id} ptr=0x{ptr.ToInt64():X} frame={frame} phase={phase} error={ex.GetType().Name}: {ex.Message}");
    }

    public static void CreatedDxgiSurface() => Interlocked.Increment(ref createdDxgiSurface);
    public static void DisposedDxgiSurface() => Interlocked.Increment(ref disposedDxgiSurface);
    public static void CreatedSourceWrapper() => Interlocked.Increment(ref createdSourceTextureWrapper);
    public static void DisposedSourceWrapper() => Interlocked.Increment(ref disposedSourceTextureWrapper);

    public static void Log(string phase, int? frame = null)
    {
        long n = Interlocked.Increment(ref logs);
        if ((n % 10) != 0 && phase != "dispose") return;
        long working = 0, privateBytes = 0;
        try
        {
            using var process = Process.GetCurrentProcess();
            working = process.WorkingSet64;
            privateBytes = process.PrivateMemorySize64;
        }
        catch { }

        long liveBitmap;
        lock (BitmapLock)
            liveBitmap = BitmapIds.Count;

        Debug.WriteLine(
            $"[HDRInjector][HDRGPU][RES] phase={phase}" +
            (frame.HasValue ? $" frame={frame.Value}" : string.Empty) +
            $" tex={Volatile.Read(ref createdTexture2D) - Volatile.Read(ref disposedTexture2D)}" +
            $" srv={Volatile.Read(ref createdSrv) - Volatile.Read(ref disposedSrv)}" +
            $" uav={Volatile.Read(ref createdUav) - Volatile.Read(ref disposedUav)}" +
            $" d2dbitmap={liveBitmap}" +
            $" d2dbitmapCreated={Volatile.Read(ref createdD2DBitmap)}" +
            $" d2dbitmapDisposed={Volatile.Read(ref disposedD2DBitmap)}" +
            $" dxgisurface={Volatile.Read(ref createdDxgiSurface) - Volatile.Read(ref disposedDxgiSurface)}" +
            $" sourcewrap={Volatile.Read(ref createdSourceTextureWrapper) - Volatile.Read(ref disposedSourceTextureWrapper)}" +
            $" wsMB={working / 1024.0 / 1024.0:F1}" +
            $" privateMB={privateBytes / 1024.0 / 1024.0:F1}");
    }
}

/// <summary>
/// Experimental production GPU input path.
/// Uses FFmpeg native APIs + D3D11VA on YMM4's existing D3D11 device,
/// converts P010 to linear FP16 on the GPU, and exposes that surface as a D2D bitmap.
/// Seeking intentionally falls back to the legacy path until native teardown/seek
/// lifetime management is hardened.
/// </summary>
internal sealed class FfmpegNativeGpuDecoder : IDisposable
{
    private static readonly object RootLock = new();
    private static readonly object D3D11ContextLock = new();
    private static readonly List<IntPtr> RootedHandles = new();
    private static bool DllDirectorySet;
    private static AvGetFormatDelegate? RootedGetFormatDelegate;

    private readonly string inputFile;
    private readonly IntPtr ymmDevicePtr;
    private readonly ID2D1DeviceContext6 d2dContext;
    // Reuse the Vortice D3D11 wrappers for the entire decoder lifetime.
    // Creating new wrappers for every frame leaks COM references at 60 fps.
    private VorticeD3D11Device? d3dDevice;
    private VorticeD3D11DeviceContext? d3dContext;
    private readonly string codecName;
    private int videoStreamIndex;
    private readonly int hwPixFmt;
    private readonly IntPtr avformatHandle;
    private readonly IntPtr avcodecHandle;
    private readonly IntPtr avutilHandle;

    private readonly AvFormatOpenInputDelegate openInput;
    private readonly AvFormatFindStreamInfoDelegate findStreamInfo;
    private readonly AvFormatReadFrameDelegate readFrame;
    private readonly AvFormatCloseInputDelegate closeInput;
    private readonly AvCodecFindDecoderByNameDelegate findDecoderByName;
    private readonly AvCodecAllocContext3Delegate allocCodecContext;
    private readonly AvCodecFreeContextDelegate freeCodecContext;
    private readonly AvCodecParametersToContextDelegate parametersToContext;
    private readonly AvCodecOpen2Delegate openCodec;
    private IntPtr codecPtr;
    private readonly AvCodecSendPacketDelegate sendPacket;
    private readonly AvCodecReceiveFrameDelegate receiveFrame;
    private readonly AvPacketAllocDelegate packetAlloc;
    private readonly AvPacketFreeDelegate packetFree;
    private readonly AvPacketUnrefDelegate packetUnref;
    private readonly AvFrameAllocDelegate frameAlloc;
    private readonly AvFrameFreeDelegate frameFree;
    private readonly AvHwDeviceFindTypeByNameDelegate findHwType;
    private readonly AvHwDeviceCtxAllocDelegate hwDeviceCtxAlloc;
    private readonly AvHwDeviceCtxInitDelegate hwDeviceCtxInit;
    private readonly AvBufferRefDelegate? bufferRef;
    private readonly AvBufferUnrefDelegate? bufferUnref;
    private readonly AvGetPixFmtNameDelegate? getPixFmtName;
    private readonly AvCodecFlushBuffersDelegate flushCodecBuffers;
    private readonly AvSeekFrameDelegate seekFrame;

    private IntPtr formatContext;
    private IntPtr codecContext;
    private IntPtr packet;
    private IntPtr frame;
    private IntPtr hwDeviceBufferRef;
    private IntPtr codecNamePtr;
    private IntPtr inputUrlPtr;
    private VorticeD3D11Texture2D? readableP010;
    private VorticeD3D11Texture2D? currentOutputTexture;
    private readonly Queue<VorticeD3D11Texture2D> retiredOutputTextures = new();
    private int readableWidth;
    private int readableHeight;
    private bool disposed;
    private int lastDecodedFrame = -1;
    private int pendingSeekFrame = -1;
    private int timeBaseNum = 1;
    private int timeBaseDen = 1000000;
    private double _fpsForFrameIndex = 60.0;

    // HDR GPU color bridge (Fix85). The shader outputs pipeline-sRGB so YMM4's existing
    // global sRGB->Linear HDR pass reconstructs the intended linear HDR value.
    private int hdrTransferMode = 1; // 0=PQ, 1=HLG
    private float hdrSdrWhiteNits = 80.0f;
    private VorticeD3D11ComputeShader? p010ToPipelineShader;
    private VorticeD3D11Buffer? p010ColorConstants;
    private static readonly object P010ShaderLock = new();
    private static byte[]? P010ShaderBytecode;

    // Fix94: Pooled GPU resources to avoid per-frame allocation.
    // These are reused as long as the frame dimensions don't change.
    private VorticeD3D11Texture2D? pooledComputeOutput;
    private VorticeD3D11ShaderResourceView? pooledYSrv;
    private VorticeD3D11ShaderResourceView? pooledUvSrv;
    private VorticeD3D11UnorderedAccessView? pooledUav;
    private int pooledWidth;
    private int pooledHeight;

    // Fix94: D2D output ring buffer. Pre-allocated textures + bitmaps rotated each frame.
    // 3 slots allow YMM4 to reference the previous frame while we write the next.
    private const int D2DRingSize = 3;
    private readonly VorticeD3D11Texture2D?[] d2dRingTextures = new VorticeD3D11Texture2D?[D2DRingSize];
    private readonly ID2D1Bitmap1?[] d2dRingBitmaps = new ID2D1Bitmap1?[D2DRingSize];
    private int d2dRingIndex;
    private int d2dRingWidth;
    private int d2dRingHeight;

    private FfmpegNativeGpuDecoder(
        string inputFile,
        IntPtr ymmDevicePtr,
        ID2D1DeviceContext6 d2dContext,
        string codecName,
        int videoStreamIndex,
        int hwPixFmt,
        int timeBaseNum,
        int timeBaseDen,
        IntPtr avformatHandle,
        IntPtr avcodecHandle,
        IntPtr avutilHandle,
        AvFormatOpenInputDelegate openInput,
        AvFormatFindStreamInfoDelegate findStreamInfo,
        AvFormatReadFrameDelegate readFrame,
        AvFormatCloseInputDelegate closeInput,
        AvCodecFindDecoderByNameDelegate findDecoderByName,
        AvCodecAllocContext3Delegate allocCodecContext,
        AvCodecFreeContextDelegate freeCodecContext,
        AvCodecParametersToContextDelegate parametersToContext,
        AvCodecOpen2Delegate openCodec,
        AvCodecSendPacketDelegate sendPacket,
        AvCodecReceiveFrameDelegate receiveFrame,
        AvPacketAllocDelegate packetAlloc,
        AvPacketFreeDelegate packetFree,
        AvPacketUnrefDelegate packetUnref,
        AvFrameAllocDelegate frameAlloc,
        AvFrameFreeDelegate frameFree,
        AvHwDeviceFindTypeByNameDelegate findHwType,
        AvHwDeviceCtxAllocDelegate hwDeviceCtxAlloc,
        AvHwDeviceCtxInitDelegate hwDeviceCtxInit,
        AvBufferRefDelegate? bufferRef,
        AvBufferUnrefDelegate? bufferUnref,
        AvGetPixFmtNameDelegate? getPixFmtName,
        AvCodecFlushBuffersDelegate flushCodecBuffers,
        AvSeekFrameDelegate seekFrame)
    {
        this.inputFile = inputFile;
        this.ymmDevicePtr = ymmDevicePtr;
        this.d2dContext = d2dContext;
        this.codecName = codecName;
        this.videoStreamIndex = videoStreamIndex;
        this.hwPixFmt = hwPixFmt;
        this.timeBaseNum = timeBaseNum > 0 ? timeBaseNum : 1;
        this.timeBaseDen = timeBaseDen > 0 ? timeBaseDen : 1000000;
        this.avformatHandle = avformatHandle;
        this.avcodecHandle = avcodecHandle;
        this.avutilHandle = avutilHandle;
        this.openInput = openInput;
        this.findStreamInfo = findStreamInfo;
        this.readFrame = readFrame;
        this.closeInput = closeInput;
        this.findDecoderByName = findDecoderByName;
        this.allocCodecContext = allocCodecContext;
        this.freeCodecContext = freeCodecContext;
        this.parametersToContext = parametersToContext;
        this.openCodec = openCodec;
        this.sendPacket = sendPacket;
        this.receiveFrame = receiveFrame;
        this.packetAlloc = packetAlloc;
        this.packetFree = packetFree;
        this.packetUnref = packetUnref;
        this.frameAlloc = frameAlloc;
        this.frameFree = frameFree;
        this.findHwType = findHwType;
        this.hwDeviceCtxAlloc = hwDeviceCtxAlloc;
        this.hwDeviceCtxInit = hwDeviceCtxInit;
        this.bufferRef = bufferRef;
        this.bufferUnref = bufferUnref;
        this.getPixFmtName = getPixFmtName;
        this.flushCodecBuffers = flushCodecBuffers;
        this.seekFrame = seekFrame;

        // The native FFmpeg decoder and the GPU conversion pipeline share YMM4's device.
        // Create the managed Vortice wrappers once and reuse them for every frame.
        d3dDevice = new VorticeD3D11Device(ymmDevicePtr);
        d3dContext = d3dDevice.ImmediateContext
            ?? throw new InvalidOperationException("D3D11 ImmediateContext unavailable.");
    }

    public static bool TryCreate(string inputFile, IntPtr ymmDevicePtr, ID2D1DeviceContext6 d2dContext, out FfmpegNativeGpuDecoder? decoder)
    {
        decoder = null;
        try
        {
            if (string.IsNullOrWhiteSpace(inputFile) || ymmDevicePtr == IntPtr.Zero)
            {
                Debug.WriteLine($"[HDRInjector][HDRGPU] TryCreate: early fail - inputFile={(!string.IsNullOrWhiteSpace(inputFile))}, devicePtr={(ymmDevicePtr != IntPtr.Zero)}");
                return false;
            }

            string root = FindFfmpegDirectory();
            if (!Directory.Exists(root))
            {
                Debug.WriteLine($"[HDRInjector][HDRGPU] TryCreate: FFmpeg directory not found: {root}");
                return false;
            }

            EnsureLibrariesLoaded(root);
            IntPtr avformatHandle = FindLoaded("avformat-");
            IntPtr avcodecHandle = FindLoaded("avcodec-");
            IntPtr avutilHandle = FindLoaded("avutil-");
            if (avformatHandle == IntPtr.Zero || avcodecHandle == IntPtr.Zero || avutilHandle == IntPtr.Zero)
            {
                Debug.WriteLine($"[HDRInjector][HDRGPU] TryCreate: DLL handles missing - avformat={(avformatHandle != IntPtr.Zero)}, avcodec={(avcodecHandle != IntPtr.Zero)}, avutil={(avutilHandle != IntPtr.Zero)}");
                return false;
            }

            if (!TryGetExport<AvFormatOpenInputDelegate>(avformatHandle, "avformat_open_input", out var openInput) ||
                !TryGetExport<AvFormatFindStreamInfoDelegate>(avformatHandle, "avformat_find_stream_info", out var findStreamInfo) ||
                !TryGetExport<AvFormatReadFrameDelegate>(avformatHandle, "av_read_frame", out var readFrame) ||
                !TryGetExport<AvFormatCloseInputDelegate>(avformatHandle, "avformat_close_input", out var closeInput) ||
                !TryGetExport<AvCodecFindDecoderByNameDelegate>(avcodecHandle, "avcodec_find_decoder_by_name", out var findDecoderByName) ||
                !TryGetExport<AvCodecAllocContext3Delegate>(avcodecHandle, "avcodec_alloc_context3", out var allocCodecContext) ||
                !TryGetExport<AvCodecFreeContextDelegate>(avcodecHandle, "avcodec_free_context", out var freeCodecContext) ||
                !TryGetExport<AvCodecParametersToContextDelegate>(avcodecHandle, "avcodec_parameters_to_context", out var parametersToContext) ||
                !TryGetExport<AvCodecOpen2Delegate>(avcodecHandle, "avcodec_open2", out var openCodec) ||
                !TryGetExport<AvCodecSendPacketDelegate>(avcodecHandle, "avcodec_send_packet", out var sendPacket) ||
                !TryGetExport<AvCodecReceiveFrameDelegate>(avcodecHandle, "avcodec_receive_frame", out var receiveFrame) ||
                !TryGetExport<AvPacketAllocDelegate>(avcodecHandle, "av_packet_alloc", out var packetAlloc) ||
                !TryGetExport<AvPacketFreeDelegate>(avcodecHandle, "av_packet_free", out var packetFree) ||
                !TryGetExport<AvPacketUnrefDelegate>(avcodecHandle, "av_packet_unref", out var packetUnref) ||
                !TryGetExport<AvFrameAllocDelegate>(avutilHandle, "av_frame_alloc", out var frameAlloc) ||
                !TryGetExport<AvFrameFreeDelegate>(avutilHandle, "av_frame_free", out var frameFree) ||
                !TryGetExport<AvHwDeviceFindTypeByNameDelegate>(avutilHandle, "av_hwdevice_find_type_by_name", out var findHwType) ||
                !TryGetExport<AvHwDeviceCtxAllocDelegate>(avutilHandle, "av_hwdevice_ctx_alloc", out var hwDeviceCtxAlloc) ||
                !TryGetExport<AvHwDeviceCtxInitDelegate>(avutilHandle, "av_hwdevice_ctx_init", out var hwDeviceCtxInit) ||
                !TryGetExport<AvBufferRefDelegate>(avutilHandle, "av_buffer_ref", out var bufferRef) ||
                !TryGetExport<AvBufferUnrefDelegate>(avutilHandle, "av_buffer_unref", out var bufferUnref))
            {
                Debug.WriteLine("[HDRInjector][HDRGPU] Required FFmpeg native exports missing.");
                return false;
            }

            TryGetExport(avutilHandle, "av_get_pix_fmt_name", out AvGetPixFmtNameDelegate? getPixFmtName);
            TryGetExport<AvCodecFlushBuffersDelegate>(avcodecHandle, "avcodec_flush_buffers", out var flushCodecBuffers);
            TryGetExport<AvSeekFrameDelegate>(avformatHandle, "av_seek_frame", out var seekFrame);
            if (flushCodecBuffers == null || seekFrame == null)
            {
                Debug.WriteLine($"[HDRInjector][HDRGPU] Seek exports missing: flushCodecBuffers={(flushCodecBuffers != null)}, seekFrame={(seekFrame != null)}");
            }

            if (!TryProbeStreamInfo(inputFile, out string codecName, out int videoStreamIndex, out int width, out int height, out int timeBaseNum, out int timeBaseDen))
                return false;

            IntPtr codecNamePtr = Marshal.StringToCoTaskMemUTF8(codecName);
            IntPtr codec = findDecoderByName(codecNamePtr);
            if (codec == IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(codecNamePtr);
                return false;
            }

            IntPtr typeName = Marshal.StringToCoTaskMemUTF8("d3d11va");
            int hwType;
            try { hwType = findHwType(typeName); }
            finally { Marshal.FreeCoTaskMem(typeName); }
            if (hwType < 0)
            {
                Marshal.FreeCoTaskMem(codecNamePtr);
                return false;
            }

            int hwPixFmt = FindD3D11PixFmt(avcodecHandle, codec);
            if (hwPixFmt < 0)
            {
                Marshal.FreeCoTaskMem(codecNamePtr);
                return false;
            }

            var result = new FfmpegNativeGpuDecoder(inputFile, ymmDevicePtr, d2dContext, codecName, videoStreamIndex, hwPixFmt, timeBaseNum, timeBaseDen,
                avformatHandle, avcodecHandle, avutilHandle, openInput, findStreamInfo, readFrame, closeInput, findDecoderByName,
                allocCodecContext, freeCodecContext, parametersToContext, openCodec, sendPacket, receiveFrame, packetAlloc, packetFree, packetUnref,
                frameAlloc, frameFree, findHwType, hwDeviceCtxAlloc, hwDeviceCtxInit, bufferRef, bufferUnref, getPixFmtName,
                flushCodecBuffers, seekFrame);
            result.codecNamePtr = codecNamePtr;
            result.codecPtr = codec;

            if (!result.Initialize(codec, width, height))
            {
                result.Dispose();
                decoder = null;
                return false;
            }

            decoder = result;
            Debug.WriteLine($"[HDRInjector][HDRGPU] Native GPU input ACTIVE: codec={codecName}, stream={videoStreamIndex}, size={width}x{height}, hwPixFmt={hwPixFmt}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HDRInjector][HDRGPU] Native GPU input creation failed: {ex.GetType().Name}: {ex.Message}");
            try { decoder?.Dispose(); } catch { }
            decoder = null;
            return false;
        }
    }

    public void SetFrameRate(double fps)
    {
        if (fps > 0 && !double.IsNaN(fps) && !double.IsInfinity(fps))
            _fpsForFrameIndex = fps;
    }

    public void SetHdrTransfer(bool pq, float sdrWhiteNits)
    {
        hdrTransferMode = pq ? 0 : 1;
        hdrSdrWhiteNits = Math.Max(1.0f, sdrWhiteNits);
        Debug.WriteLine($"[HDRInjector][HDRGPU] Color bridge: transfer={(pq ? "PQ" : "HLG")}, sdrWhiteNits={hdrSdrWhiteNits:0.##}");
    }

    public int NextFrameIndex => lastDecodedFrame + 1;
    public int LastDecodedFrame => lastDecodedFrame;

    // Fix95: Cache AVPacket.stream_index offset at first use, verified via FFmpeg.AutoGen.
    private static int _avPacketStreamIndexOffset = -1;
    private static int GetAvPacketStreamIndexOffset()
    {
        if (_avPacketStreamIndexOffset < 0)
        {
            try
            {
                unsafe
                {
                    _avPacketStreamIndexOffset = (int)Marshal.OffsetOf<AVPacket>("stream_index");
                }
            }
            catch
            {
                // Fallback: AVPacket layout for FFmpeg 6.x/7.x x64:
                // buf(8) + pts(8) + dts(8) + data(8) + size(4) = 36
                _avPacketStreamIndexOffset = 36;
            }
            Debug.WriteLine($"[HDRInjector][HDRGPU][Fix95] AVPacket.stream_index offset = {_avPacketStreamIndexOffset}");
        }
        return _avPacketStreamIndexOffset;
    }

    public bool TryDecodeNextFrame(out ID2D1Bitmap1? bitmap, out double gpuMs, out string failureReason)
    {
        bitmap = null;
        gpuMs = 0;
        failureReason = "unknown";
        if (disposed || codecContext == IntPtr.Zero || formatContext == IntPtr.Zero)
        {
            failureReason = $"invalid-state disposed={disposed} codec={(codecContext != IntPtr.Zero)} format={(formatContext != IntPtr.Zero)}";
            return false;
        }

        long gpuStart = Stopwatch.GetTimestamp();
        try
        {
            int videoFrameCount = 0;
            int lastReadRc = 0;
            int lastSendRc = 0;
            int lastRecvRc = 0;

            // Fix94: Increased from 64 to 512 for better seek recovery.
            // After a seek, the decoder may need many packets to reach the next keyframe.
            while (videoFrameCount < 512)
            {
                int readRc = readFrame(formatContext, packet);
                lastReadRc = readRc;
                if (readRc < 0)
                {
                    failureReason = $"av_read_frame rc={readRc} afterPackets={videoFrameCount}";
                    Debug.WriteLine($"[HDRInjector][HDRGPU][DECODE] FAIL stage=read rc={readRc} packets={videoFrameCount}");
                    return false;
                }

                int streamIndex = Marshal.ReadInt32(packet, GetAvPacketStreamIndexOffset());
                if (streamIndex != videoStreamIndex)
                {
                    packetUnref(packet);
                    videoFrameCount++;
                    continue;
                }

                int sendRc = sendPacket(codecContext, packet);
                lastSendRc = sendRc;
                packetUnref(packet);
                if (sendRc < 0)
                {
                    Debug.WriteLine($"[HDRInjector][HDRGPU][DECODE] send_packet rc={sendRc}; skipping packet");
                    videoFrameCount++;
                    continue;
                }

                while (true)
                {
                    int recvRc = receiveFrame(codecContext, frame);
                    lastRecvRc = recvRc;
                    if (recvRc == 0)
                    {
                        long data0 = Marshal.ReadIntPtr(frame, 0).ToInt64();
                        long data1 = Marshal.ReadIntPtr(frame, 8).ToInt64();
                        if (data0 == 0)
                        {
                            failureReason = "receive_frame success but data[0] was null";
                            Debug.WriteLine($"[HDRInjector][HDRGPU][DECODE] MISS stage=frame-data-null recv=0");
                            break;
                        }

                        int format = Marshal.ReadInt32(frame, 116);
                        int width = Marshal.ReadInt32(frame, 104);
                        int height = Marshal.ReadInt32(frame, 108);
                        int expected = hwPixFmt;
                        if (format != expected)
                        {
                            Debug.WriteLine($"[HDRInjector][HDRGPU][DECODE] frame-format={format} expected={expected}; skipping frame");
                            break;
                        }

                        long pts = ReadAvFramePts(frame);
                        int ptsFrame = lastDecodedFrame + 1;
                        if (pts != long.MinValue)
                        {
                            double ptsSeconds = pts * (double)timeBaseNum / Math.Max(1, timeBaseDen);
                            ptsFrame = (int)Math.Round(ptsSeconds * _fpsForFrameIndex, MidpointRounding.AwayFromZero);
                        }

                        if (pendingSeekFrame >= 0 && ptsFrame < pendingSeekFrame)
                        {
                            Debug.WriteLine($"[HDRInjector][HDRGPU][DECODE] seek-discard ptsFrame={ptsFrame} target={pendingSeekFrame}");
                            // Continue receiving from decoder queue to advance to the seek target.
                            continue;
                        }

                        int sourceSlice = checked((int)data1);
                        using var source = new VorticeD3D11Texture2D(new IntPtr(data0));
                        GpuLeakDiagnostics.CreatedSourceWrapper();
                        try
                        {
                            GpuLeakDiagnostics.Log("source-create", ptsFrame);
                            bitmap = ConvertP010ToD2DBitmap(source, sourceSlice, width, height);
                        }
                        finally
                        {
                            GpuLeakDiagnostics.DisposedSourceWrapper();
                        }

                        if (bitmap != null)
                        {
                            lastDecodedFrame = ptsFrame;
                            pendingSeekFrame = -1;
                            gpuMs = (Stopwatch.GetTimestamp() - gpuStart) * 1000.0 / Stopwatch.Frequency;
                            Debug.WriteLine($"[HDRInjector][HDRGPU] Frame GPU path SUCCESS frame={lastDecodedFrame}, pts={pts}, gpu={gpuMs:0.00}ms, slice={sourceSlice}, size={width}x{height}");
                            GpuLeakDiagnostics.Log("frame", lastDecodedFrame);
                            return true;
                        }

                        failureReason = $"convert-p010 returned null frame={ptsFrame} slice={sourceSlice}";
                        Debug.WriteLine($"[HDRInjector][HDRGPU][DECODE] MISS stage=convert returned-null ptsFrame={ptsFrame} slice={sourceSlice}");
                        break;
                    }

                    if (recvRc == -11) // AVERROR(EAGAIN) in the FFmpeg build used here.
                    {
                        // EAGAIN is not a decoder failure. It means another packet is needed.
                        break;
                    }

                    if (recvRc < 0)
                    {
                        failureReason = $"avcodec_receive_frame rc={recvRc} readRc={lastReadRc} sendRc={lastSendRc}";
                        Debug.WriteLine($"[HDRInjector][HDRGPU][DECODE] FAIL stage=receive rc={recvRc} read={lastReadRc} send={lastSendRc}");
                        break;
                    }

                    break;
                }

                videoFrameCount++;
            }

            failureReason = $"packet-loop-exhausted packets={videoFrameCount} lastRead={lastReadRc} lastSend={lastSendRc} lastRecv={lastRecvRc}";
            Debug.WriteLine($"[HDRInjector][HDRGPU][DECODE] MISS stage=packet-loop-exhausted packets={videoFrameCount} read={lastReadRc} send={lastSendRc} recv={lastRecvRc}");
        }
        catch (Exception ex)
        {
            failureReason = $"exception {ex.GetType().Name}: {ex.Message}";
            Debug.WriteLine($"[HDRInjector][HDRGPU][DECODE] FAIL stage=exception {ex.GetType().Name}: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// Fix95: Decode frames until we reach or pass the target frame, discarding intermediates.
    /// This allows the decoder to catch up in a single call when YMM4's clock has advanced
    /// past the current decoder position.
    /// </summary>
    public bool TryDecodeToFrame(int targetFrame, out ID2D1Bitmap1? bitmap, out double gpuMs, out string failureReason)
    {
        bitmap = null;
        gpuMs = 0;
        failureReason = "unknown";
        long totalStart = Stopwatch.GetTimestamp();
        int framesDecoded = 0;
        const int maxCatchUpFrames = 30; // Safety limit to prevent runaway decode

        while (framesDecoded < maxCatchUpFrames)
        {
            if (TryDecodeNextFrame(out var frameBitmap, out double frameMs, out failureReason))
            {
                bitmap = frameBitmap;
                framesDecoded++;
                if (lastDecodedFrame >= targetFrame)
                {
                    gpuMs = (Stopwatch.GetTimestamp() - totalStart) * 1000.0 / Stopwatch.Frequency;
                    if (framesDecoded > 1)
                        Debug.WriteLine($"[HDRInjector][HDRGPU][Fix95] Catch-up decoded {framesDecoded} frames to reach frame={lastDecodedFrame} (target={targetFrame}) in {gpuMs:0.00}ms");
                    return true;
                }
                // Keep decoding - haven't reached target yet
            }
            else
            {
                // Decode failed. If we got at least one frame, return it.
                if (bitmap != null)
                {
                    gpuMs = (Stopwatch.GetTimestamp() - totalStart) * 1000.0 / Stopwatch.Frequency;
                    return true;
                }
                return false;
            }
        }

        // Decoded maxCatchUpFrames but still haven't reached target.
        // Return whatever we have.
        if (bitmap != null)
        {
            gpuMs = (Stopwatch.GetTimestamp() - totalStart) * 1000.0 / Stopwatch.Frequency;
            Debug.WriteLine($"[HDRInjector][HDRGPU][Fix95] Catch-up hit limit ({maxCatchUpFrames}), at frame={lastDecodedFrame} (target={targetFrame}) in {gpuMs:0.00}ms");
            return true;
        }
        failureReason = $"catch-up exhausted maxFrames={maxCatchUpFrames}";
        return false;
    }

    /// <summary>
    /// Seek to the specified frame index using av_seek_frame (safe, no format context restart).
    /// </summary>
    public bool SeekToFrame(int frameIndex, double fps)
    {
        if (disposed || formatContext == IntPtr.Zero)
            return false;
        if (flushCodecBuffers == null || seekFrame == null)
        {
            Debug.WriteLine("[HDRInjector][HDRGPU] SeekToFrame: required exports not loaded");
            return false;
        }
        if (frameIndex < 0) frameIndex = 0;
        if (fps <= 0) fps = 60.0;

        try
        {
            _fpsForFrameIndex = fps;
            flushCodecBuffers(codecContext);

            double seekTimeSec = frameIndex / fps;
            const int AVSEEK_FLAG_BACKWARD = 1;
            int seekRc = -1;

            // 1. First try seeking on the specific video stream using its stream time_base
            if (videoStreamIndex >= 0 && timeBaseNum > 0 && timeBaseDen > 0)
            {
                long streamTimestamp = (long)Math.Round(seekTimeSec * (double)timeBaseDen / timeBaseNum);
                seekRc = seekFrame(formatContext, videoStreamIndex, streamTimestamp, AVSEEK_FLAG_BACKWARD);
                if (seekRc < 0)
                {
                    Debug.WriteLine($"[HDRInjector][HDRGPU] Seek: av_seek_frame(stream={videoStreamIndex}) rc={seekRc}, trying fallback to -1");
                }
            }

            // 2. Fallback: stream_index=-1 with AV_TIME_BASE (microseconds)
            if (seekRc < 0)
            {
                long timestamp = (long)Math.Round(seekTimeSec * 1_000_000.0);
                seekRc = seekFrame(formatContext, -1, timestamp, AVSEEK_FLAG_BACKWARD);
            }

            if (seekRc < 0)
            {
                Debug.WriteLine($"[HDRInjector][HDRGPU] Seek: av_seek_frame failed rc={seekRc}, t={seekTimeSec:F3}s");
                return false;
            }

            pendingSeekFrame = frameIndex;
            lastDecodedFrame = -1;
            Debug.WriteLine($"[HDRInjector][HDRGPU] Seek to frame={frameIndex} (time={seekTimeSec:F3}s) SUCCESS; pendingSeekFrame={pendingSeekFrame}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HDRInjector][HDRGPU] Seek failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private bool Initialize(IntPtr codec, int width, int height)
    {
        IntPtr options = IntPtr.Zero;
        inputUrlPtr = Marshal.StringToCoTaskMemUTF8(inputFile);
        if (openInput(ref formatContext, inputUrlPtr, IntPtr.Zero, ref options) < 0 || formatContext == IntPtr.Zero)
            return false;
        if (findStreamInfo(formatContext, IntPtr.Zero) < 0)
            return false;

        codecContext = allocCodecContext(codec);
        if (codecContext == IntPtr.Zero)
            return false;

        // The decoder context must inherit the stream's codec parameters (especially
        // extradata, profile, dimensions and coded pixel format) before avcodec_open2().
        // The old implementation only selected the decoder by name, which can let
        // avcodec_open2() succeed while every real HEVC/MP4 packet is rejected with
        // AVERROR_INVALIDDATA.
        try
        {
            int streamsOffset = checked((int)Marshal.OffsetOf<AVFormatContext>("streams"));
            IntPtr streamsPtr = Marshal.ReadIntPtr(formatContext, streamsOffset);
            if (streamsPtr == IntPtr.Zero)
            {
                Debug.WriteLine("[HDRInjector][FFmpegDLL] AVFormatContext.streams is NULL; cannot copy codecpar.");
                return false;
            }

            IntPtr streamPtr = Marshal.ReadIntPtr(streamsPtr, checked(videoStreamIndex * IntPtr.Size));
            if (streamPtr == IntPtr.Zero)
            {
                Debug.WriteLine($"[HDRInjector][FFmpegDLL] AVStream pointer is NULL for stream={videoStreamIndex}.");
                return false;
            }

            int codecparOffset = checked((int)Marshal.OffsetOf<AVStream>("codecpar"));
            IntPtr codecparPtr = Marshal.ReadIntPtr(streamPtr, codecparOffset);
            if (codecparPtr == IntPtr.Zero)
            {
                Debug.WriteLine($"[HDRInjector][FFmpegDLL] AVStream.codecpar is NULL for stream={videoStreamIndex}.");
                return false;
            }

            Debug.WriteLine($"[HDRInjector][FFmpegDLL] codecpar located: stream={videoStreamIndex}, streamsOffset=0x{streamsOffset:X}, codecparOffset=0x{codecparOffset:X}, ptr=0x{codecparPtr.ToInt64():X}");

            int paramsRc = parametersToContext(codecContext, codecparPtr);
            Debug.WriteLine($"[HDRInjector][FFmpegDLL] avcodec_parameters_to_context({codecName}) rc={paramsRc}");
            if (paramsRc < 0)
                return false;

            try
            {
                int codecIdOffset = checked((int)Marshal.OffsetOf<AVCodecParameters>("codec_id"));
                int formatOffset = checked((int)Marshal.OffsetOf<AVCodecParameters>("format"));
                int widthOffset = checked((int)Marshal.OffsetOf<AVCodecParameters>("width"));
                int heightOffset = checked((int)Marshal.OffsetOf<AVCodecParameters>("height"));
                int extradataSizeOffset = checked((int)Marshal.OffsetOf<AVCodecParameters>("extradata_size"));
                int codecId = Marshal.ReadInt32(codecparPtr, codecIdOffset);
                int format = Marshal.ReadInt32(codecparPtr, formatOffset);
                int cpWidth = Marshal.ReadInt32(codecparPtr, widthOffset);
                int cpHeight = Marshal.ReadInt32(codecparPtr, heightOffset);
                int extra = Marshal.ReadInt32(codecparPtr, extradataSizeOffset);
                Debug.WriteLine($"[HDRInjector][FFmpegDLL] codecpar summary: codec_id={codecId}, format={format}, size={cpWidth}x{cpHeight}, extradata_size={extra}");
            }
            catch (Exception inspectEx)
            {
                Debug.WriteLine($"[HDRInjector][FFmpegDLL] codecpar summary inspection skipped: {inspectEx.GetType().Name}: {inspectEx.Message}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HDRInjector][FFmpegDLL] codecpar copy failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        IntPtr hwDeviceTypeName = Marshal.StringToCoTaskMemUTF8("d3d11va");
        int hwType;
        try { hwType = findHwType(hwDeviceTypeName); }
        finally { Marshal.FreeCoTaskMem(hwDeviceTypeName); }
        if (hwType < 0)
            return false;

        hwDeviceBufferRef = hwDeviceCtxAlloc(hwType);
        if (hwDeviceBufferRef == IntPtr.Zero)
            return false;

        IntPtr hwDeviceContext = Marshal.ReadIntPtr(hwDeviceBufferRef, IntPtr.Size);
        int hwctxOffset = checked((int)Marshal.OffsetOf<AVHWDeviceContext>("hwctx"));
        IntPtr apiHwContext = Marshal.ReadIntPtr(hwDeviceContext, hwctxOffset);
        if (apiHwContext == IntPtr.Zero)
            return false;

        IntPtr deviceVtable = Marshal.ReadIntPtr(ymmDevicePtr);
        IntPtr addRefPtr = Marshal.ReadIntPtr(deviceVtable, IntPtr.Size);
        var addRef = Marshal.GetDelegateForFunctionPointer<AddRefDelegate>(addRefPtr);
        addRef(ymmDevicePtr);
        Marshal.WriteIntPtr(apiHwContext, 0, ymmDevicePtr);

        int initRc = hwDeviceCtxInit(hwDeviceBufferRef);
        if (initRc < 0)
            return false;

        int hwDeviceOffset = checked((int)Marshal.OffsetOf<AVCodecContext>("hw_device_ctx"));
        int getFormatOffset = checked((int)Marshal.OffsetOf<AVCodecContext>("get_format"));

        // Fix100: Give AVCodecContext its own AVBufferRef reference. Keep the original
        // reference in hwDeviceBufferRef so we can explicitly unref our ownership after
        // avcodec_free_context(). Never unref the same AVBufferRef twice.
        if (bufferRef == null)
            throw new InvalidOperationException("av_buffer_ref export is unavailable.");
        IntPtr codecHwDeviceBufferRef = bufferRef(hwDeviceBufferRef);
        if (codecHwDeviceBufferRef == IntPtr.Zero)
            throw new InvalidOperationException("av_buffer_ref(hw_device_ctx) returned NULL.");

        Marshal.WriteIntPtr(codecContext, hwDeviceOffset, codecHwDeviceBufferRef);
        Debug.WriteLine($"[HDRInjector][FFmpegCleanup] hw_device_ctx refs: owner=0x{hwDeviceBufferRef.ToInt64():X}, codec=0x{codecHwDeviceBufferRef.ToInt64():X}");

        var getFormat = new AvGetFormatDelegate((_, pixFmts) =>
        {
            if (pixFmts == IntPtr.Zero) return -1;
            int index = 0;
            while (true)
            {
                int fmt = Marshal.ReadInt32(pixFmts, index * 4);
                if (fmt == -1) break;
                if (fmt == hwPixFmt) return fmt;
                index++;
            }
            return Marshal.ReadInt32(pixFmts);
        });
        lock (RootLock) RootedGetFormatDelegate = getFormat;
        Marshal.WriteIntPtr(codecContext, getFormatOffset, Marshal.GetFunctionPointerForDelegate(getFormat));

        if (openCodec(codecContext, codec, IntPtr.Zero) < 0)
            return false;

        packet = packetAlloc();
        frame = frameAlloc();
        return packet != IntPtr.Zero && frame != IntPtr.Zero;
    }

    private static long ReadAvFramePts(IntPtr avFrame)
    {
        try
        {
            int offset = (int)Marshal.OffsetOf<AVFrame>("pts");
            return Marshal.ReadInt64(avFrame, offset);
        }
        catch
        {
            return long.MinValue;
        }
    }

    // Fix94: Ensure the pooled compute resources (computeOutput, SRVs, UAV) are
    // allocated and match the current frame dimensions. Reuses existing resources
    // when dimensions haven't changed, avoiding per-frame D3D11 allocations.
    private void EnsurePooledComputeResources(VorticeD3D11Device device, int width, int height)
    {
        if (pooledComputeOutput != null && pooledWidth == width && pooledHeight == height)
            return;

        // Dispose old pooled resources
        pooledUav?.Dispose();
        pooledUav = null;
        pooledUvSrv?.Dispose();
        pooledUvSrv = null;
        pooledYSrv?.Dispose();
        pooledYSrv = null;
        pooledComputeOutput?.Dispose();
        pooledComputeOutput = null;

        var computeDesc = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R16G16B16A16_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        pooledComputeOutput = device.CreateTexture2D(computeDesc);
        GpuLeakDiagnostics.CreatedTexture();

        var ySrvDesc = new ShaderResourceViewDescription
        {
            Format = Format.R16_UNorm,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = 1 }
        };
        var uvSrvDesc = new ShaderResourceViewDescription
        {
            Format = Format.R16G16_UNorm,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = 1 }
        };
        pooledYSrv = device.CreateShaderResourceView(readableP010!, ySrvDesc);
        GpuLeakDiagnostics.CreatedSrv();
        pooledUvSrv = device.CreateShaderResourceView(readableP010!, uvSrvDesc);
        GpuLeakDiagnostics.CreatedSrv();
        pooledUav = device.CreateUnorderedAccessView(pooledComputeOutput);
        GpuLeakDiagnostics.CreatedUav();

        pooledWidth = width;
        pooledHeight = height;
        Debug.WriteLine($"[HDRInjector][HDRGPU][Fix94] Pooled compute resources created: {width}x{height}");
    }

    // Fix94: Ensure the D2D output ring buffer (textures + bitmaps) is allocated.
    // 3 slots allow YMM4 to safely reference previous frames during rendering.
    private void EnsureD2DRingBuffer(VorticeD3D11Device device, int width, int height)
    {
        if (d2dRingWidth == width && d2dRingHeight == height && d2dRingTextures[0] != null)
            return;

        // Dispose old ring buffer
        for (int i = 0; i < D2DRingSize; i++)
        {
            if (d2dRingBitmaps[i] != null)
            {
                try { GpuLeakDiagnostics.DisposeD2DBitmap(d2dRingBitmaps[i]!, lastDecodedFrame, "ring-resize"); }
                catch { }
                d2dRingBitmaps[i] = null;
            }
            if (d2dRingTextures[i] != null)
            {
                d2dRingTextures[i]!.Dispose();
                GpuLeakDiagnostics.DisposedTexture();
                d2dRingTextures[i] = null;
            }
        }

        var d2dDesc = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R16G16B16A16_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.Shared,
        };

        for (int i = 0; i < D2DRingSize; i++)
        {
            d2dRingTextures[i] = device.CreateTexture2D(d2dDesc);
            GpuLeakDiagnostics.CreatedTexture();

            using var surface = d2dRingTextures[i]!.QueryInterface<IDXGISurface>();
            GpuLeakDiagnostics.CreatedDxgiSurface();
            d2dRingBitmaps[i] = CreateD2DBitmapFromDxgiSurface(d2dContext, surface, width, height);
            GpuLeakDiagnostics.DisposedDxgiSurface();
            GpuLeakDiagnostics.TrackD2DBitmap(d2dRingBitmaps[i]!, lastDecodedFrame);
        }

        d2dRingWidth = width;
        d2dRingHeight = height;
        d2dRingIndex = 0;
        Debug.WriteLine($"[HDRInjector][HDRGPU][Fix94] D2D ring buffer created: {D2DRingSize} slots, {width}x{height}");
    }

    private ID2D1Bitmap1? ConvertP010ToD2DBitmap(VorticeD3D11Texture2D source, int sourceSlice, int width, int height)
    {
        var device = d3dDevice ?? throw new ObjectDisposedException(nameof(FfmpegNativeGpuDecoder));
        var context = d3dContext ?? throw new ObjectDisposedException(nameof(FfmpegNativeGpuDecoder));

        lock (D3D11ContextLock)
        {
            if (readableP010 is null || readableWidth != width || readableHeight != height)
            {
                if (readableP010 != null) GpuLeakDiagnostics.DisposedTexture();
                readableP010?.Dispose();
                var readableDesc = new Texture2DDescription
                {
                    Width = width,
                    Height = height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.P010,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.None,
                };
                readableP010 = device.CreateTexture2D(readableDesc);
                GpuLeakDiagnostics.CreatedTexture();
                readableWidth = width;
                readableHeight = height;
            }

            context.CopySubresourceRegion(readableP010, 0, 0, 0, 0, source, sourceSlice, null);

            // Fix94: Reuse pooled compute resources and D2D ring buffer.
            EnsurePooledComputeResources(device, width, height);
            EnsureD2DRingBuffer(device, width, height);
            EnsureP010PipelineResources(device);
            UpdateP010PipelineConstants(context);

            // Advance ring buffer index
            int ringSlot = d2dRingIndex;
            d2dRingIndex = (d2dRingIndex + 1) % D2DRingSize;

            var d2dOutput = d2dRingTextures[ringSlot]!;
            var bitmap = d2dRingBitmaps[ringSlot]!;

            context.CSSetShader(p010ToPipelineShader);
            context.CSSetConstantBuffer(0, p010ColorConstants);
            context.CSSetShaderResources(0, new[] { pooledYSrv!, pooledUvSrv! });
            context.CSSetUnorderedAccessViews(0, new[] { pooledUav! });
            context.Dispatch((width + 7) / 8, (height + 7) / 8, 1);
            // Copy compute output to the D2D-facing texture in the ring buffer.
            context.CopyResource(d2dOutput, pooledComputeOutput!);
            // Unbind resources from the pipeline to prevent hazards.
            context.CSSetShaderResources(0, new VorticeD3D11ShaderResourceView[] { null!, null! });
            context.CSSetUnorderedAccessViews(0, new VorticeD3D11UnorderedAccessView[] { null! });
            context.CSSetConstantBuffers(0, new VorticeD3D11Buffer[] { null! });
            context.CSSetShader(null);
            // Synchronize with D2D and other threads so the output surface is ready to read.
            context.Flush();

            return bitmap;
        }
    }

    private void EnsureP010PipelineResources(VorticeD3D11Device device)
    {
        if (p010ToPipelineShader != null && p010ColorConstants != null)
            return;

        p010ToPipelineShader ??= device.CreateComputeShader(GetP010ShaderBytecode());
        p010ColorConstants ??= device.CreateBuffer(
            16,
            BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write);
    }

    private void UpdateP010PipelineConstants(VorticeD3D11DeviceContext context)
    {
        if (p010ColorConstants == null)
            throw new InvalidOperationException("P010 HDR color constant buffer is not initialized.");

        var mapped = context.Map(p010ColorConstants, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            Marshal.WriteInt32(mapped.DataPointer, BitConverter.SingleToInt32Bits(hdrSdrWhiteNits));
            Marshal.WriteInt32(mapped.DataPointer, 4, BitConverter.SingleToInt32Bits((float)hdrTransferMode));
            Marshal.WriteInt32(mapped.DataPointer, 8, 0);
            Marshal.WriteInt32(mapped.DataPointer, 12, 0);
        }
        finally
        {
            context.Unmap(p010ColorConstants, 0);
        }
    }

    private static byte[] GetP010ShaderBytecode()
    {
        lock (P010ShaderLock)
        {
            if (P010ShaderBytecode != null)
                return P010ShaderBytecode;

            byte[] sourceBytes = Encoding.UTF8.GetBytes(LoadEmbeddedShaderText("HDRInjector.Shaders.HdrP010ToRgbCS.hlsl") + "\0");
            int hr = D3DCompile(
                sourceBytes,
                sourceBytes.Length - 1,
                "HDRInjector.HdrP010ToRgbCS.hlsl",
                IntPtr.Zero,
                IntPtr.Zero,
                "main",
                "cs_5_0",
                0,
                0,
                out IntPtr code,
                out IntPtr errors);

            try
            {
                if (hr < 0 || code == IntPtr.Zero)
                {
                    string message = errors != IntPtr.Zero ? ReadBlob(errors) : $"HRESULT=0x{hr:X8}";
                    throw new InvalidOperationException($"D3DCompile(HdrP010ToRgbCS) failed: {message}");
                }

                IntPtr dataPtr = GetBlobBufferPointer(code);
                if (dataPtr == IntPtr.Zero)
                    throw new InvalidOperationException("D3DCompile returned an empty shader blob.");

                nuint byteLength = GetBlobBufferSize(code);
                if (byteLength == 0 || byteLength > int.MaxValue)
                    throw new InvalidOperationException("D3DCompile returned an invalid shader blob size.");

                var bytes = new byte[(int)byteLength];
                Marshal.Copy(dataPtr, bytes, 0, bytes.Length);
                P010ShaderBytecode = bytes;
                Debug.WriteLine($"[HDRInjector][HDRGPU] Compiled P010 HDR compute shader at runtime: {byteLength} bytes");
                return bytes;
            }
            finally
            {
                if (code != IntPtr.Zero) Marshal.Release(code);
                if (errors != IntPtr.Zero) Marshal.Release(errors);
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr BlobGetBufferPointerDelegate(IntPtr blob);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate nuint BlobGetBufferSizeDelegate(IntPtr blob);

    private static IntPtr GetBlobBufferPointer(IntPtr blob)
    {
        IntPtr vtable = Marshal.ReadIntPtr(blob);
        IntPtr fn = Marshal.ReadIntPtr(vtable, IntPtr.Size * 3);
        var getter = Marshal.GetDelegateForFunctionPointer<BlobGetBufferPointerDelegate>(fn);
        return getter(blob);
    }

    private static nuint GetBlobBufferSize(IntPtr blob)
    {
        IntPtr vtable = Marshal.ReadIntPtr(blob);
        IntPtr fn = Marshal.ReadIntPtr(vtable, IntPtr.Size * 4);
        var getter = Marshal.GetDelegateForFunctionPointer<BlobGetBufferSizeDelegate>(fn);
        return getter(blob);
    }

    private static string ReadBlob(IntPtr blob)
    {
        try
        {
            IntPtr dataPtr = GetBlobBufferPointer(blob);
            nuint size = GetBlobBufferSize(blob);
            if (dataPtr == IntPtr.Zero || size == 0 || size > int.MaxValue)
                return "unknown compiler error";
            byte[] bytes = new byte[(int)size];
            Marshal.Copy(dataPtr, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes).TrimEnd('\0', '\r', '\n');
        }
        catch
        {
            return "failed to read D3DCompile error blob";
        }
    }

    private static string LoadEmbeddedShaderText(string resourceName)
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded shader source not found: {resourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(
        byte[] pSrcData,
        int SrcDataSize,
        string pSourceName,
        IntPtr pDefines,
        IntPtr pInclude,
        string pEntrypoint,
        string pTarget,
        uint Flags1,
        uint Flags2,
        out IntPtr ppCode,
        out IntPtr ppErrorMsgs);

    private static ID2D1Bitmap1 CreateD2DBitmapFromDxgiSurface(ID2D1DeviceContext6 dc, IDXGISurface surface, int width, int height)
    {
        // FIX84: Use the managed Vortice API with explicit BitmapProperties1.
        // The previous implementation used a raw vtable call at index 62 which points to
        // GetPrimitiveBlend, not CreateBitmapFromDxgiSurface (which is at ~index 75).
        // Additionally, null BitmapProperties caused D2DERR_UNSUPPORTED_PIXEL_FORMAT (0x8899001E)
        // when YMM4 later called GetImageLocalBounds on the resulting bitmap.
        var bitmapProps = new BitmapProperties1(
            new PixelFormat(Format.R16G16B16A16_Float, Vortice.DCommon.AlphaMode.Premultiplied),
            96.0f, 96.0f, // Match the DPI used by YMM4's CrossDeviceFrameBridge
            BitmapOptions.None);

        ID2D1Bitmap1 bitmap = dc.CreateBitmapFromDxgiSurface(surface, bitmapProps);
        Debug.WriteLine($"[HDRInjector][HDRGPU] CreateBitmapFromDxgiSurface: format=R16G16B16A16_Float alpha=Premultiplied dpi=96x96 size={width}x{height}");
        return bitmap;
    }

    private static byte[] LoadEmbeddedShader(string resourceName)
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded shader not found: {resourceName}");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static int FindD3D11PixFmt(IntPtr avcodecHandle, IntPtr codec)
    {
        if (!NativeLibrary.TryGetExport(avcodecHandle, "avcodec_get_hw_config", out IntPtr ptr) ||
            !NativeLibrary.TryGetExport(FindLoaded("avutil-"), "av_hwdevice_find_type_by_name", out IntPtr findTypePtr))
            return -1;
        var getConfig = Marshal.GetDelegateForFunctionPointer<AvCodecGetHwConfigDelegate>(ptr);
        int count = 0;
        for (int i = 0; i < 16; i++)
        {
            IntPtr configPtr = getConfig(codec, i);
            if (configPtr == IntPtr.Zero) break;
            int pixFmt = Marshal.ReadInt32(configPtr, (int)Marshal.OffsetOf<AVCodecHWConfig>("pix_fmt"));
            int methods = Marshal.ReadInt32(configPtr, (int)Marshal.OffsetOf<AVCodecHWConfig>("methods"));
            int deviceType = Marshal.ReadInt32(configPtr, (int)Marshal.OffsetOf<AVCodecHWConfig>("device_type"));
            count++;
            if ((methods & 0x01) != 0 && deviceType == 7 && pixFmt == 171)
                return pixFmt;
        }
        return -1;
    }

    private static bool TryProbeStreamInfo(string inputFile, out string codecName, out int streamIndex, out int width, out int height, out int timeBaseNum, out int timeBaseDen)
    {
        codecName = string.Empty;
        streamIndex = 0;
        width = 0;
        height = 0;
        timeBaseNum = 1;
        timeBaseDen = 1000000;
        try
        {
            string root = FindFfmpegDirectory();
            string ffprobe = Path.Combine(root, "ffprobe.exe");
            if (!File.Exists(ffprobe)) return false;
            var psi = new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = $"-v error -select_streams v:0 -show_entries stream=index,codec_name,width,height,time_base -of default=nw=1:nk=0 \"{inputFile.Replace("\\\"", "\\\\\"")}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process == null) return false;
            string output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(3000);
            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = line.Split('=', 2);
                if (p.Length != 2) continue;
                switch (p[0].Trim())
                {
                    case "index": int.TryParse(p[1], out streamIndex); break;
                    case "codec_name": codecName = p[1].Trim(); break;
                    case "width": int.TryParse(p[1], out width); break;
                    case "height": int.TryParse(p[1], out height); break;
                    case "time_base":
                        var tb = p[1].Trim().Split('/');
                        if (tb.Length == 2 && int.TryParse(tb[0], out int tbNum) && int.TryParse(tb[1], out int tbDen) && tbNum > 0 && tbDen > 0)
                        {
                            timeBaseNum = tbNum;
                            timeBaseDen = tbDen;
                        }
                        break;
                }
            }
            return !string.IsNullOrWhiteSpace(codecName) && width > 0 && height > 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Attempt to recover the GPU decoder after a legacy fallback by recreating from scratch.
    /// Called when the caller wants to retry the GPU path.
    /// </summary>
    public static bool TryRecreate(string inputFile, IntPtr ymmDevicePtr, ID2D1DeviceContext6 d2dContext, out FfmpegNativeGpuDecoder? decoder)
    {
        return TryCreate(inputFile, ymmDevicePtr, d2dContext, out decoder);
    }

    private static void EnsureLibrariesLoaded(string root)
    {
        lock (RootLock)
        {
            if (RootedHandles.Count > 0) return;
            if (!DllDirectorySet)
            {
                SetDllDirectoryW(root);
                DllDirectorySet = true;
            }
            foreach (string baseName in new[] { "avutil", "swresample", "swscale", "avcodec", "avformat" })
            {
                string? dll = FindVersionedDll(root, baseName);
                if (dll == null) continue;
                try { RootedHandles.Add(NativeLibrary.Load(dll)); } catch { }
            }
        }
    }

    private static IntPtr FindLoaded(string prefix)
    {
        lock (RootLock)
        {
            foreach (IntPtr h in RootedHandles)
            {
                if (h == IntPtr.Zero) continue;
                // Best-effort: test common export names to identify the module.
                if (prefix.StartsWith("avformat", StringComparison.OrdinalIgnoreCase) && NativeLibrary.TryGetExport(h, "avformat_open_input", out _)) return h;
                if (prefix.StartsWith("avcodec", StringComparison.OrdinalIgnoreCase) && NativeLibrary.TryGetExport(h, "avcodec_open2", out _)) return h;
                if (prefix.StartsWith("avutil", StringComparison.OrdinalIgnoreCase) && NativeLibrary.TryGetExport(h, "av_hwdevice_ctx_alloc", out _)) return h;
            }
        }
        return IntPtr.Zero;
    }

    private static bool TryGetExport<T>(IntPtr handle, string name, out T? value) where T : class
    {
        value = null;
        if (handle == IntPtr.Zero || !NativeLibrary.TryGetExport(handle, name, out IntPtr ptr)) return false;
        value = Marshal.GetDelegateForFunctionPointer(ptr, typeof(T)) as T;
        return value != null;
    }

    private static void TryGetExport<T>(IntPtr handle, string name, out T? value, bool dummy = false) where T : class
        => TryGetExport(handle, name, out value);

    private static void TryGetExport(IntPtr handle, string name, out AvGetPixFmtNameDelegate? value)
    {
        value = null;
        if (handle != IntPtr.Zero && NativeLibrary.TryGetExport(handle, name, out IntPtr ptr))
            value = Marshal.GetDelegateForFunctionPointer<AvGetPixFmtNameDelegate>(ptr);
    }

    private static string FindFfmpegDirectory()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(baseDir, "Resources", "bin", "x64", "ffmpeg"),
            Path.Combine(baseDir, "ffmpeg"),
            Path.Combine(baseDir, "Resources", "ffmpeg")
        };
        foreach (string path in candidates)
            if (Directory.Exists(path)) return path;
        return candidates[0];
    }

    private static string? FindVersionedDll(string root, string baseName)
    {
        string? exact = Directory.GetFiles(root, $"{baseName}-*.dll").FirstOrDefault();
        if (!string.IsNullOrEmpty(exact)) return exact;
        string plain = Path.Combine(root, $"{baseName}.dll");
        return File.Exists(plain) ? plain : null;
    }

    private static void SetDllDirectoryW(string? path) => NativeMethods.SetDllDirectory(path);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (readableP010 != null) GpuLeakDiagnostics.DisposedTexture();
        readableP010?.Dispose();
        readableP010 = null;
        while (retiredOutputTextures.Count > 0)
        {
            try { retiredOutputTextures.Dequeue().Dispose(); } catch { }
        }
        if (currentOutputTexture != null) GpuLeakDiagnostics.DisposedTexture();
        currentOutputTexture?.Dispose();
        currentOutputTexture = null;

        // Fix94: Dispose pooled compute resources.
        pooledUav?.Dispose();
        pooledUav = null;
        pooledUvSrv?.Dispose();
        pooledUvSrv = null;
        pooledYSrv?.Dispose();
        pooledYSrv = null;
        if (pooledComputeOutput != null) GpuLeakDiagnostics.DisposedTexture();
        pooledComputeOutput?.Dispose();
        pooledComputeOutput = null;

        // Fix94: Dispose D2D ring buffer.
        for (int i = 0; i < D2DRingSize; i++)
        {
            if (d2dRingBitmaps[i] != null)
            {
                try { GpuLeakDiagnostics.DisposeD2DBitmap(d2dRingBitmaps[i]!, lastDecodedFrame, "ring-dispose"); }
                catch { }
                d2dRingBitmaps[i] = null;
            }
            if (d2dRingTextures[i] != null)
            {
                d2dRingTextures[i]!.Dispose();
                GpuLeakDiagnostics.DisposedTexture();
                d2dRingTextures[i] = null;
            }
        }
        GpuLeakDiagnostics.DisposedSrv();
        GpuLeakDiagnostics.DisposedSrv();
        GpuLeakDiagnostics.DisposedUav();

        try { d3dContext?.Dispose(); } catch { }
        d3dContext = null;
        try { d3dDevice?.Dispose(); } catch { }
        d3dDevice = null;
        try { p010ToPipelineShader?.Dispose(); } catch { }
        p010ToPipelineShader = null;
        try { p010ColorConstants?.Dispose(); } catch { }
        p010ColorConstants = null;
        GpuLeakDiagnostics.Log("dispose");

        // Fix97: First native-cleanup step. AVPacket and AVFrame are owned by this decoder
        // instance and do not participate in the risky codec/format/hw-device teardown path.
        // Free them explicitly, but keep AVCodecContext / AVFormatContext / hw_device_ctx
        // teardown deferred until a later, separately validated fix.
        if (packet != IntPtr.Zero)
        {
            try
            {
                packetFree(ref packet);
                Debug.WriteLine("[HDRInjector][FFmpegCleanup] av_packet_free => SUCCESS");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HDRInjector][FFmpegCleanup] av_packet_free FAILED: {ex.GetType().Name}: {ex.Message}");
            }
            packet = IntPtr.Zero;
        }

        if (frame != IntPtr.Zero)
        {
            try
            {
                frameFree(ref frame);
                Debug.WriteLine("[HDRInjector][FFmpegCleanup] av_frame_free => SUCCESS");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HDRInjector][FFmpegCleanup] av_frame_free FAILED: {ex.GetType().Name}: {ex.Message}");
            }
            frame = IntPtr.Zero;
        }

        // Fix98: Free AVCodecContext only. Do not explicitly free hw_device_ctx here;
        // AVCodecContext owns its reference, and avcodec_free_context() is responsible for
        // releasing its internal codec state. The YMM4 D3D11 device itself is managed separately.
        if (codecContext != IntPtr.Zero)
        {
            try
            {
                freeCodecContext(ref codecContext);
                Debug.WriteLine("[HDRInjector][FFmpegCleanup] avcodec_free_context => SUCCESS");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HDRInjector][FFmpegCleanup] avcodec_free_context FAILED: {ex.GetType().Name}: {ex.Message}");
            }
            codecContext = IntPtr.Zero;
        }

        // Fix100: Release the decoder-side owner of the FFmpeg HW device context.
        // The codec context owns its own AVBufferRef (created with av_buffer_ref above),
        // so avcodec_free_context() releases that reference first. This unrefs only the
        // original reference held by this decoder instance. The underlying YMM4 D3D11
        // device is NOT released directly here.
        if (hwDeviceBufferRef != IntPtr.Zero)
        {
            try
            {
                if (bufferUnref != null)
                {
                    bufferUnref(ref hwDeviceBufferRef);
                    Debug.WriteLine("[HDRInjector][FFmpegCleanup] av_buffer_unref(hw_device_ctx owner) => SUCCESS");
                }
                else
                {
                    Debug.WriteLine("[HDRInjector][FFmpegCleanup] av_buffer_unref FAILED: export unavailable");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HDRInjector][FFmpegCleanup] av_buffer_unref(hw_device_ctx owner) FAILED: {ex.GetType().Name}: {ex.Message}");
            }
            hwDeviceBufferRef = IntPtr.Zero;
        }

        // Fix99: Close AVFormatContext after packet/frame/codec cleanup.
        // This also releases AVStream/codecpar data owned by the format context.
        // hw_device_ctx is intentionally not touched directly here.
        if (formatContext != IntPtr.Zero)
        {
            try
            {
                closeInput(ref formatContext);
                Debug.WriteLine("[HDRInjector][FFmpegCleanup] avformat_close_input => SUCCESS");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HDRInjector][FFmpegCleanup] avformat_close_input FAILED: {ex.GetType().Name}: {ex.Message}");
            }
            formatContext = IntPtr.Zero;
        }

        try { if (inputUrlPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(inputUrlPtr); } catch { }
        inputUrlPtr = IntPtr.Zero;
        try { if (codecNamePtr != IntPtr.Zero) Marshal.FreeCoTaskMem(codecNamePtr); } catch { }
        codecNamePtr = IntPtr.Zero;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectoryWNative(string? lpPathName);

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool SetDllDirectory(string? lpPathName);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AvGetFormatDelegate(IntPtr avctx, IntPtr pixFmts);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AvFormatOpenInputDelegate(ref IntPtr ps, IntPtr url, IntPtr fmt, ref IntPtr options);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AvFormatFindStreamInfoDelegate(IntPtr ic, IntPtr options);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AvFormatReadFrameDelegate(IntPtr s, IntPtr packet);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AvFormatCloseInputDelegate(ref IntPtr ps);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr AvCodecFindDecoderByNameDelegate(IntPtr name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr AvCodecAllocContext3Delegate(IntPtr codec);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AvCodecFreeContextDelegate(ref IntPtr avctx);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AvCodecParametersToContextDelegate(IntPtr avctx, IntPtr codecpar);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AvCodecOpen2Delegate(IntPtr avctx, IntPtr avCodec, IntPtr options);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AvCodecSendPacketDelegate(IntPtr avctx, IntPtr avpkt);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AvCodecReceiveFrameDelegate(IntPtr avctx, IntPtr frame);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr AvPacketAllocDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AvPacketFreeDelegate(ref IntPtr packet);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AvPacketUnrefDelegate(IntPtr packet);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr AvFrameAllocDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AvFrameFreeDelegate(ref IntPtr frame);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AvHwDeviceFindTypeByNameDelegate(IntPtr name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr AvHwDeviceCtxAllocDelegate(int type);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr AvBufferRefDelegate(IntPtr buf);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AvBufferUnrefDelegate(ref IntPtr buf);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AvHwDeviceCtxInitDelegate(IntPtr bufferRef);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr AvCodecGetHwConfigDelegate(IntPtr codec, int index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr AvGetPixFmtNameDelegate(int pixFmt);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AvCodecFlushBuffersDelegate(IntPtr avctx);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AvSeekFrameDelegate(IntPtr s, int streamIndex, long timestamp, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint AddRefDelegate(IntPtr self);
}
