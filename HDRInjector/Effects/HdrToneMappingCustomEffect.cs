using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;

namespace HDRInjector.Effects;

/// <summary>
/// HDR トーンマッピングを16bit Float精度で実行するカスタムシェーダーエフェクト。
/// D2D組み込み ColorMatrix+GammaTransfer の代わりに使用し、HDR値 (>1.0) をクランプせずに保持する。
/// </summary>
internal class HdrToneMappingCustomEffect : D2D1CustomShaderEffectBase
{
    public float Exposure { set => SetValue((int)Props.Exposure, value); }
    public float Gamma { set => SetValue((int)Props.Gamma, value); }
    public float Contrast { set => SetValue((int)Props.Contrast, value); }
    public float Saturation { set => SetValue((int)Props.Saturation, value); }

    public HdrToneMappingCustomEffect(IGraphicsDevicesAndContext devices) : base(Create<EffectImpl>(devices)) { }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConstantBuffer
    {
        public float Exposure;      // 4 bytes
        public float Gamma;         // 4 bytes
        public float Contrast;      // 4 bytes
        public float Saturation;    // 4 bytes -> 16 bytes
    }

    private enum Props
    {
        Exposure,
        Gamma,
        Contrast,
        Saturation
    }

    [CustomEffect(1)]
    private class EffectImpl : D2D1CustomShaderEffectImplBase<EffectImpl>
    {
        private ConstantBuffer constants;

        private static byte[] LoadShader()
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("HDRInjector.Shaders.HdrToneMappingShader.cso");
            if (stream == null)
            {
                throw new FileNotFoundException("HdrToneMappingShader.cso not found in embedded resources");
            }
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        public EffectImpl() : base(LoadShader())
        {
            constants = new ConstantBuffer
            {
                Exposure = 0.0f,
                Gamma = 1.0f,
                Contrast = 1.0f,
                Saturation = 1.0f
            };
        }

        public override void SetDrawInfo(ID2D1DrawInfo drawInfo)
        {
            this.drawInformation = drawInfo;
            this.drawInformation.SetPixelShader(GUID_PixelShader, PixelOptions.None);

            try
            {
                this.drawInformation.SetOutputBuffer((BufferPrecision)4 /* 32bpc Float */, ChannelDepth.Four);
            }
            catch
            {
                // フォールバック
            }

            for (int i = 0; i < GetInputCount(); ++i)
            {
                this.drawInformation.SetInputDescription(i, new InputDescription
                {
                    Filter = Filter.MinMagMipLinear,
                    LevelOfDetailCount = 1
                });
            }
        }

        protected override void UpdateConstants()
        {
            drawInformation?.SetPixelShaderConstantBuffer(constants);
        }

        public override void MapInputRectsToOutputRect(RawRect[] inputRects, RawRect[] inputOpaqueSubRects, out RawRect outputRect, out RawRect outputOpaqueSubRect)
        {
            if (inputRects.Length > 0)
            {
                outputRect = inputRects[0];
            }
            else
            {
                outputRect = new RawRect();
            }
            outputOpaqueSubRect = new RawRect();
        }

        public override void MapOutputRectToInputRects(RawRect outputRect, RawRect[] inputRects)
        {
            for (int i = 0; i < inputRects.Length; i++)
            {
                inputRects[i] = outputRect;
            }
        }

        [CustomEffectProperty(PropertyType.Float, (int)Props.Exposure)]
        public float Exposure { get => constants.Exposure; set { constants.Exposure = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)Props.Gamma)]
        public float Gamma { get => constants.Gamma; set { constants.Gamma = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)Props.Contrast)]
        public float Contrast { get => constants.Contrast; set { constants.Contrast = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)Props.Saturation)]
        public float Saturation { get => constants.Saturation; set { constants.Saturation = value; UpdateConstants(); } }
    }
}
