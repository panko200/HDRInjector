using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Numerics;
using Vortice;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;

namespace HDRInjector.Effects;

/// <summary>
/// YMM4's normal SDR (sRGB/Rec.709 transfer curve) image to linear RGB.
/// Used only at the SDR -> float HDR render-target boundary.
/// </summary>
internal sealed class SrgbToLinearCustomEffect : D2D1CustomShaderEffectBase
{
    public float SdrWhiteScale
    {
        set => SetValue((int)Props.SdrWhiteScale, Math.Max(0.01f, value));
    }

    private enum Props
    {
        SdrWhiteScale,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConstantBuffer
    {
        public float SdrWhiteScale;
        public Vector3 Padding;
    }

    public SrgbToLinearCustomEffect(IGraphicsDevicesAndContext devices) : base(Create<EffectImpl>(devices)) { }

    [CustomEffect(1)]
    private sealed class EffectImpl : D2D1CustomShaderEffectImplBase<EffectImpl>
    {
        private static byte[] LoadShader()
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("HDRInjector.Shaders.SrgbToLinearShader.cso");
            if (stream == null)
                throw new FileNotFoundException("SrgbToLinearShader.cso not found in embedded resources");
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        public EffectImpl() : base(LoadShader()) { }

        public override void SetDrawInfo(ID2D1DrawInfo drawInfo)
        {
            drawInformation = drawInfo;
            drawInformation.SetPixelShader(GUID_PixelShader, PixelOptions.None);
            try
            {
                drawInformation.SetOutputBuffer((BufferPrecision)4, ChannelDepth.Four);
            }
            catch
            {
                // Let D2D choose a supported precision if explicit 16bpc is unavailable.
            }

            for (int i = 0; i < GetInputCount(); i++)
            {
                drawInformation.SetInputDescription(i, new InputDescription
                {
                    Filter = Filter.MinMagMipLinear,
                    LevelOfDetailCount = 1
                });
            }
        }

        private ConstantBuffer constants = new()
        {
            SdrWhiteScale = 1.0f,
            Padding = Vector3.Zero
        };

        [CustomEffectProperty(PropertyType.Float, (int)Props.SdrWhiteScale)]
        public float SdrWhiteScale
        {
            get => constants.SdrWhiteScale;
            set
            {
                constants.SdrWhiteScale = Math.Max(0.01f, value);
                UpdateConstants();
            }
        }

        protected override void UpdateConstants()
        {
            drawInformation?.SetPixelShaderConstantBuffer(constants);
        }

        public override void MapInputRectsToOutputRect(
            RawRect[] inputRects,
            RawRect[] inputOpaqueSubRects,
            out RawRect outputRect,
            out RawRect outputOpaqueSubRect)
        {
            outputRect = inputRects.Length > 0 ? inputRects[0] : new RawRect();
            outputOpaqueSubRect = new RawRect();
        }

        public override void MapOutputRectToInputRects(RawRect outputRect, RawRect[] inputRects)
        {
            for (int i = 0; i < inputRects.Length; i++)
                inputRects[i] = outputRect;
        }
    }
}
