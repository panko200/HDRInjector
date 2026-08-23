using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HDRInjector.FileWriter;

/// <summary>
/// GPU-side final HDR10 color conversion for export.
/// The expensive per-pixel color conversion is performed by a D3D11 compute shader.
///
/// IMPORTANT: do not retain an ID3D11Device/DeviceContext wrapper obtained from a
/// per-frame ID3D11Texture2D. In YMM4's export path those wrappers can become invalid
/// when the frame resource is released. Acquire the current device/context from the
/// current source texture on every Convert() call.
/// </summary>
internal sealed class HdrExportGpuConverter : IDisposable
{
    private readonly ID3D11ComputeShader shader;
    private readonly ID3D11Buffer constantBuffer;

    private ID3D11Texture2D? inputTexture;
    private ID3D11ShaderResourceView? inputSrv;
    private ID3D11Texture2D? outputTexture;
    private ID3D11UnorderedAccessView? outputUav;
    private ID3D11Texture2D? staging;
    private int width;
    private int height;

    public int Width => width;
    public int Height => height;

    public readonly record struct GpuPerfBreakdown(
        long CopyInputTicks,
        long ConstantBufferTicks,
        long ShaderDispatchTicks,
        long UnbindTicks,
        long CopyToStagingTicks,
        long TotalTicks);

    public GpuPerfBreakdown LastPerf { get; private set; }

    public HdrExportGpuConverter(ID3D11Device device)
    {
        if (device == null)
            throw new ArgumentNullException(nameof(device));

        shader = device.CreateComputeShader(LoadShader());
        constantBuffer = device.CreateBuffer(16, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
    }

    private static byte[] LoadShader()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("HDRInjector.Shaders.HdrExportColorConvertCS.cso")
            ?? throw new FileNotFoundException("HdrExportColorConvertCS.cso not found in embedded resources.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private void EnsureResources(ID3D11Device device, int newWidth, int newHeight)
    {
        if (inputTexture != null && inputSrv != null && outputTexture != null && outputUav != null && staging != null
            && width == newWidth && height == newHeight)
            return;

        inputSrv?.Dispose();
        inputTexture?.Dispose();
        outputUav?.Dispose();
        outputTexture?.Dispose();
        staging?.Dispose();

        width = newWidth;
        height = newHeight;

        var inputDesc = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R16G16B16A16_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        inputTexture = device.CreateTexture2D(inputDesc);
        inputSrv = device.CreateShaderResourceView(inputTexture);

        var outputDesc = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R16G16B16A16_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.UnorderedAccess,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        outputTexture = device.CreateTexture2D(outputDesc);
        outputUav = device.CreateUnorderedAccessView(outputTexture);

        var stagingDesc = outputDesc;
        stagingDesc.Usage = ResourceUsage.Staging;
        stagingDesc.BindFlags = BindFlags.None;
        stagingDesc.CPUAccessFlags = CpuAccessFlags.Read;
        staging = device.CreateTexture2D(stagingDesc);

        Console.WriteLine($"[HDRInjector][HDRPerf][GPU] resources created {width}x{height}");
    }

    public void Convert(ID3D11Texture2D source, float sdrWhiteScale)
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));

        // IMPORTANT: get fresh wrappers for the current frame. Do not keep the Device /
        // DeviceContext wrapper returned by a previous frame's source texture.
        var device = source.Device ?? throw new InvalidOperationException("Source texture device is unavailable.");
        var context = device.ImmediateContext ?? throw new InvalidOperationException("D3D11 immediate context is unavailable.");

        var desc = source.Description;
        EnsureResources(device, desc.Width, desc.Height);

        long totalStart = Stopwatch.GetTimestamp();

        long t0 = Stopwatch.GetTimestamp();
        context.CopyResource(inputTexture!, source);
        long t1 = Stopwatch.GetTimestamp();

        long cbStart = Stopwatch.GetTimestamp();
        var mapped = context.Map(constantBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            Marshal.WriteInt32(mapped.DataPointer, BitConverter.SingleToInt32Bits(Math.Max(0.01f, sdrWhiteScale)));
        }
        finally
        {
            context.Unmap(constantBuffer, 0);
        }
        long cbEnd = Stopwatch.GetTimestamp();

        long dispatchStart = Stopwatch.GetTimestamp();
        context.CSSetShader(shader);
        context.CSSetConstantBuffer(0, constantBuffer);
        context.CSSetShaderResource(0, inputSrv);
        context.CSSetUnorderedAccessView(0, outputUav);
        context.Dispatch((width + 7) / 8, (height + 7) / 8, 1);

        // The staging copy must not overlap the UAV write. Clear bindings using the
        // array overloads that are stable in this Vortice build.
        long dispatchEnd = Stopwatch.GetTimestamp();

        long unbindStart = Stopwatch.GetTimestamp();
        context.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { null! });
        context.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { null! });
        context.CSSetShader(null);
        long unbindEnd = Stopwatch.GetTimestamp();

        long copyStagingStart = Stopwatch.GetTimestamp();
        context.CopyResource(staging!, outputTexture!);
        long copyStagingEnd = Stopwatch.GetTimestamp();

        LastPerf = new GpuPerfBreakdown(
            t1 - t0,
            cbEnd - cbStart,
            dispatchEnd - dispatchStart,
            unbindEnd - unbindStart,
            copyStagingEnd - copyStagingStart,
            copyStagingEnd - totalStart);
    }

    public MappedSubresource MapOutput()
    {
        // Map with a fresh context wrapper associated with the staging resource's device.
        var device = staging?.Device ?? throw new InvalidOperationException("GPU staging resource device is unavailable.");
        var context = device.ImmediateContext ?? throw new InvalidOperationException("D3D11 immediate context is unavailable.");
        return context.Map(staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
    }

    public void UnmapOutput()
    {
        var device = staging?.Device ?? throw new InvalidOperationException("GPU staging resource device is unavailable.");
        var context = device.ImmediateContext ?? throw new InvalidOperationException("D3D11 immediate context is unavailable.");
        context.Unmap(staging!, 0);
    }

    public void Dispose()
    {
        staging?.Dispose();
        outputUav?.Dispose();
        outputTexture?.Dispose();
        inputSrv?.Dispose();
        inputTexture?.Dispose();
        constantBuffer.Dispose();
        shader.Dispose();
    }
}
