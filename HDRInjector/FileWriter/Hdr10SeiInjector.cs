using System;
using System.Collections.Generic;
using System.IO;

namespace HDRInjector.FileWriter;

/// <summary>
/// Injects HDR10 static metadata into the first HEVC access unit as PREFIX SEI.
/// This is used because the bundled FFmpeg/NVENC build does not expose the HDR10
/// master_display / max_cll encoder options.
/// </summary>
internal static class Hdr10SeiInjector
{
    // ITU-T H.265 / HEVC SEI payload types.
    private const int MasteringDisplayColourVolumePayloadType = 137;
    private const int ContentLightLevelInfoPayloadType = 144;

    // Default declared mastering display profile used by this prototype:
    // BT.2020 primaries, D65 white point, 1000 nit max, 0.0001 nit min.
    // These are metadata declarations, not a measurement of the user's physical display.
    private const double RedX = 0.708;
    private const double RedY = 0.292;
    private const double GreenX = 0.170;
    private const double GreenY = 0.797;
    private const double BlueX = 0.131;
    private const double BlueY = 0.046;
    private const double WhiteX = 0.3127;
    private const double WhiteY = 0.3290;

    public static void Inject(
        string inputPath,
        string outputPath,
        float maxContentLightLevelNits,
        float maxFrameAverageLightLevelNits,
        double masteringMaxNits = 1000.0,
        double masteringMinNits = 0.0001,
        Action<string>? log = null)
    {
        byte[] input = File.ReadAllBytes(inputPath);
        if (input.Length == 0)
            throw new InvalidOperationException("HDR10メタデータ注入対象のHEVCストリームが空です。");

        byte[] masterDisplayPayload = BuildMasteringDisplayPayload(masteringMaxNits, masteringMinNits);
        ushort maxCll = ClampLightLevel(maxContentLightLevelNits);
        ushort maxFall = ClampLightLevel(maxFrameAverageLightLevelNits);
        byte[] contentLightPayload = BuildContentLightPayload(maxCll, maxFall);
        byte[] seiNal = BuildPrefixSeiNal(masterDisplayPayload, contentLightPayload);

        int insertAt = FindFirstVclInsertionPoint(input);
        if (insertAt < 0)
            throw new InvalidOperationException("HEVCストリームから最初のVCL NALを見つけられませんでした。");

        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Write(input, 0, insertAt);
        fs.Write(seiNal, 0, seiNal.Length);
        fs.Write(input, insertAt, input.Length - insertAt);

        log?.Invoke($"[HDRInjector][HDRWriter] Injected HDR10 PREFIX SEI: MDCV + CLL(MaxCLL={maxCll}, MaxFALL={maxFall}) at byte={insertAt}");
        log?.Invoke($"[HDRInjector][HDRWriter] MDCV: BT.2020 primaries / D65 / Max={masteringMaxNits:0.####} nits / Min={masteringMinNits:0.####} nits");
    }

    private static ushort ClampLightLevel(float nits)
    {
        if (!float.IsFinite(nits) || nits <= 0f)
            return 0;
        return (ushort)Math.Clamp(MathF.Round(nits), 0f, ushort.MaxValue);
    }

    private static byte[] BuildMasteringDisplayPayload(double maxMasteringNits, double minMasteringNits)
    {
        // HEVC Mastering Display Colour Volume uses G, B, R ordering.
        using var ms = new MemoryStream(24);
        WriteBe16(ms, ToChromaticity(GreenX));
        WriteBe16(ms, ToChromaticity(GreenY));
        WriteBe16(ms, ToChromaticity(BlueX));
        WriteBe16(ms, ToChromaticity(BlueY));
        WriteBe16(ms, ToChromaticity(RedX));
        WriteBe16(ms, ToChromaticity(RedY));
        WriteBe16(ms, ToChromaticity(WhiteX));
        WriteBe16(ms, ToChromaticity(WhiteY));
        WriteBe32(ms, ToMaxLuminance(maxMasteringNits));
        WriteBe32(ms, ToMinLuminance(minMasteringNits));
        return ms.ToArray();
    }

    private static byte[] BuildContentLightPayload(ushort maxCll, ushort maxFall)
    {
        using var ms = new MemoryStream(4);
        WriteBe16(ms, maxCll);
        WriteBe16(ms, maxFall);
        return ms.ToArray();
    }

    private static byte[] BuildPrefixSeiNal(byte[] masteringDisplayPayload, byte[] contentLightPayload)
    {
        using var rbsp = new MemoryStream();
        WriteSeiMessage(rbsp, MasteringDisplayColourVolumePayloadType, masteringDisplayPayload);
        WriteSeiMessage(rbsp, ContentLightLevelInfoPayloadType, contentLightPayload);

        // rbsp_trailing_bits()
        rbsp.WriteByte(0x80);

        byte[] rbspEscaped = AddEmulationPrevention(rbsp.ToArray());

        // HEVC NAL header: nal_unit_type = PREFIX_SEI_NUT (39), layer_id=0,
        // temporal_id_plus1=1. Annex-B start code is 4 bytes.
        var nal = new List<byte>(4 + 2 + rbspEscaped.Length)
        {
            0x00, 0x00, 0x00, 0x01,
            0x4E, 0x01,
        };
        nal.AddRange(rbspEscaped);
        return nal.ToArray();
    }

    private static void WriteSeiMessage(Stream stream, int payloadType, byte[] payload)
    {
        WriteSeiHeaderValue(stream, payloadType);
        WriteSeiHeaderValue(stream, payload.Length);
        stream.Write(payload, 0, payload.Length);
    }

    private static void WriteSeiHeaderValue(Stream stream, int value)
    {
        while (value >= 255)
        {
            stream.WriteByte(0xFF);
            value -= 255;
        }
        stream.WriteByte((byte)value);
    }

    private static byte[] AddEmulationPrevention(byte[] rbsp)
    {
        var output = new List<byte>(rbsp.Length + rbsp.Length / 16);
        int zeroCount = 0;

        foreach (byte b in rbsp)
        {
            if (zeroCount >= 2 && b <= 0x03)
            {
                output.Add(0x03);
                zeroCount = 0;
            }

            output.Add(b);
            zeroCount = b == 0 ? zeroCount + 1 : 0;
        }

        return output.ToArray();
    }

    private static int FindFirstVclInsertionPoint(byte[] annexB)
    {
        var nals = EnumerateNals(annexB);
        foreach (var nal in nals)
        {
            if (nal.EndOffset - nal.StartOffset < 3)
                continue;

            int nalType = (annexB[nal.PayloadOffset] >> 1) & 0x3F;
            if (nalType <= 31)
                return nal.StartOffset;
        }
        return -1;
    }

    private readonly record struct NalInfo(int StartOffset, int PayloadOffset, int EndOffset);

    private static IEnumerable<NalInfo> EnumerateNals(byte[] data)
    {
        int pos = 0;
        while (TryFindStartCode(data, pos, out int start, out int payload))
        {
            int nextSearch = payload + 2;
            int end = data.Length;
            if (TryFindStartCode(data, nextSearch, out int nextStart, out _))
                end = nextStart;

            yield return new NalInfo(start, payload, end);
            if (end >= data.Length)
                yield break;
            pos = end;
        }
    }

    private static bool TryFindStartCode(byte[] data, int from, out int start, out int payload)
    {
        for (int i = Math.Max(0, from); i + 3 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                start = i;
                payload = i + 3;
                return true;
            }
            if (i + 4 <= data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1)
            {
                start = i;
                payload = i + 4;
                return true;
            }
        }

        start = payload = -1;
        return false;
    }

    private static ushort ToChromaticity(double value) => (ushort)Math.Clamp(Math.Round(value * 50000.0), 0.0, ushort.MaxValue);

    private static uint ToMaxLuminance(double nits) => (uint)Math.Clamp(Math.Round(nits * 10000.0), 0.0, uint.MaxValue);

    private static uint ToMinLuminance(double nits) => (uint)Math.Clamp(Math.Round(nits * 10000.0), 0.0, uint.MaxValue);

    private static void WriteBe16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)(value & 0xFF));
    }

    private static void WriteBe32(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)((value >> 16) & 0xFF));
        stream.WriteByte((byte)((value >> 8) & 0xFF));
        stream.WriteByte((byte)(value & 0xFF));
    }
}
