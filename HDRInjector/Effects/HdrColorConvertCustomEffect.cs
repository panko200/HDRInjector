using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;

namespace HDRInjector.Effects;

public enum HdrConvertMode
{
    PqToLinear = 0,     // PQ (ST 2084) -> Linear Float (HDR10 展開)
    HlgToLinear = 1,    // HLG (ARIB STD-B67) -> Linear Float (HLG 展開)
    LinearToPq = 2,     // Linear Float -> PQ (ST 2084) エンコード
    LinearToHlg = 3,    // Linear Float -> HLG エンコード
    PqToPipelineSrgb = 4, // PQ -> Linear -> pseudo-sRGB for YMM4 SDR->Linear bridge
    HlgToPipelineSrgb = 5  // HLG -> Linear -> pseudo-sRGB for YMM4 SDR->Linear bridge
}

/// <summary>
/// MPC Video Renderer 準拠の HDR 色空間変換カスタムエフェクト。
/// BT.2020 / ST 2084 (PQ) / HLG を 16bit Float リニア空間へ相互変換します。
/// </summary>
internal class HdrColorConvertCustomEffect : D2D1CustomShaderEffectBase
{
    public HdrConvertMode Mode { set => SetValue((int)Props.Mode, (float)value); }
    public float TargetNits { set => SetValue((int)Props.TargetNits, value); }
    public float SdrNits { set => SetValue((int)Props.SdrNits, value); }
    public bool ConvertGamut { set => SetValue((int)Props.ConvertGamut, value ? 1.0f : 0.0f); }

    public HdrColorConvertCustomEffect(IGraphicsDevicesAndContext devices) : base(Create<EffectImpl>(devices)) { }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConstantBuffer
    {
        public float Mode;          // 0 PQ->Linear, 1 HLG->Linear, 2 Linear->PQ, 3 Linear->HLG, 4/5 HDR->pipeline-sRGB
        public float TargetNits;    // 4 bytes
        public float SdrNits;       // 4 bytes
        public float ConvertGamut;  // 4 bytes -> 16 bytes
    }

    private enum Props
    {
        Mode,
        TargetNits,
        SdrNits,
        ConvertGamut
    }

    [CustomEffect(1)]
    private class EffectImpl : D2D1CustomShaderEffectImplBase<EffectImpl>
    {
        private ConstantBuffer constants;

        private static byte[] LoadShader()
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("HDRInjector.Shaders.HdrColorConvertShader.cso");
            if (stream == null)
            {
                throw new FileNotFoundException("HdrColorConvertShader.cso not found in embedded resources");
            }
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        public EffectImpl() : base(LoadShader())
        {
            constants = new ConstantBuffer
            {
                Mode = 0.0f,            // デフォルト: PQ -> Linear
                TargetNits = 1000.0f,
                SdrNits = 80.0f,
                ConvertGamut = 1.0f     // BT.2020 <-> Rec.709
            };
        }

        public override void SetDrawInfo(ID2D1DrawInfo drawInfo)
        {
            this.drawInformation = drawInfo;
            this.drawInformation.SetPixelShader(GUID_PixelShader, PixelOptions.None);

            try
            {
                this.drawInformation.SetOutputBuffer((BufferPrecision)4 /* 16bpc Float */, ChannelDepth.Four);
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

        [CustomEffectProperty(PropertyType.Float, (int)Props.Mode)]
        public float Mode { get => constants.Mode; set { constants.Mode = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)Props.TargetNits)]
        public float TargetNits { get => constants.TargetNits; set { constants.TargetNits = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)Props.SdrNits)]
        public float SdrNits { get => constants.SdrNits; set { constants.SdrNits = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)Props.ConvertGamut)]
        public float ConvertGamut { get => constants.ConvertGamut; set { constants.ConvertGamut = value; UpdateConstants(); } }
    }
}
