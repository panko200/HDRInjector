using System;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;

namespace HDRInjector.Effects;

/// <summary>
/// 16bit/32bit Float 精度で動作する Direct2D カスタム HDR グローシェーダーエフェクト。
/// Glowplus の色収差、放射光、インナー/アウターティント、逆2乗物理減衰、TPDF ディザリングに対応。
/// </summary>
internal class HdrGlowCustomEffect : D2D1CustomShaderEffectBase
{
    public Vector4 InnerColor { set => SetValue((int)Props.InnerColor, value); }
    public Vector4 OuterColor { set => SetValue((int)Props.OuterColor, value); }
    public float Exposure { set => SetValue((int)Props.Exposure, value); }
    public float Threshold { set => SetValue((int)Props.Threshold, value); }
    public float Contrast { set => SetValue((int)Props.Contrast, value); }
    public float SourceOpacity { set => SetValue((int)Props.SourceOpacity, value); }
    public bool Colorize { set => SetValue((int)Props.Colorize, value ? 1.0f : 0.0f); }
    public bool LinearColor { set => SetValue((int)Props.LinearColor, value ? 1.0f : 0.0f); }
    public float TintScale { set => SetValue((int)Props.TintScale, value); }
    public float TintGamma { set => SetValue((int)Props.TintGamma, value); }
    public float ChromaR { set => SetValue((int)Props.ChromaR, value); }
    public float ChromaG { set => SetValue((int)Props.ChromaG, value); }
    public float ChromaB { set => SetValue((int)Props.ChromaB, value); }
    public float RayLength { set => SetValue((int)Props.RayLength, value); }
    public float RayCenterX { set => SetValue((int)Props.RayCenterX, value); }
    public float RayCenterY { set => SetValue((int)Props.RayCenterY, value); }
    public float RaySamples { set => SetValue((int)Props.RaySamples, value); }
    public float TexWidth { set => SetValue((int)Props.TexWidth, value); }
    public float TexHeight { set => SetValue((int)Props.TexHeight, value); }
    public float RayFalloff { set => SetValue((int)Props.RayFalloff, value); }
    public float RayStyle { set => SetValue((int)Props.RayStyle, value); }
    public float RayAngle { set => SetValue((int)Props.RayAngle, value); }
    public float ChromaStyle { set => SetValue((int)Props.ChromaStyle, value); }
    public float ChromaAngle { set => SetValue((int)Props.ChromaAngle, value); }
    public float ChromaCenterX { set => SetValue((int)Props.ChromaCenterX, value); }
    public float ChromaCenterY { set => SetValue((int)Props.ChromaCenterY, value); }
    public float FalloffMode { set => SetValue((int)Props.FalloffMode, value); }
    public float FalloffPower { set => SetValue((int)Props.FalloffPower, value); }
    public float DitherStrength { set => SetValue((int)Props.DitherStrength, value); }
    public bool ClampAlpha { set => SetValue((int)Props.ClampAlpha, value ? 1.0f : 0.0f); }

    public HdrGlowCustomEffect(IGraphicsDevicesAndContext devices) : base(Create<EffectImpl>(devices)) { }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConstantBuffer
    {
        public Vector4 InnerColor;      // 16 bytes
        public Vector4 OuterColor;      // 16 bytes

        public float Exposure;          // 4 bytes
        public float Threshold;         // 4 bytes
        public float Contrast;          // 4 bytes
        public float SourceOpacity;     // 4 bytes -> 16 bytes

        public float Colorize;          // 4 bytes
        public float LinearColor;       // 4 bytes
        public float TintScale;         // 4 bytes
        public float TintGamma;         // 4 bytes -> 16 bytes

        public float ChromaR;           // 4 bytes
        public float ChromaG;           // 4 bytes
        public float ChromaB;           // 4 bytes
        public float RayLength;         // 4 bytes -> 16 bytes

        public float RayCenterX;        // 4 bytes
        public float RayCenterY;        // 4 bytes
        public float RaySamples;        // 4 bytes
        public float TexWidth;          // 4 bytes -> 16 bytes

        public float TexHeight;         // 4 bytes
        public float RayFalloff;        // 4 bytes
        public float RayStyle;          // 4 bytes
        public float RayAngle;          // 4 bytes -> 16 bytes

        public float ChromaStyle;       // 4 bytes
        public float ChromaAngle;       // 4 bytes
        public float ChromaCenterX;     // 4 bytes
        public float ChromaCenterY;     // 4 bytes -> 16 bytes

        public float FalloffMode;       // 4 bytes (0: Gaussian, 1: InverseSquare, 2: Exponential)
        public float FalloffPower;      // 4 bytes
        public float DitherStrength;    // 4 bytes
        public float ClampAlpha;        // 4 bytes -> 16 bytes
    }

    private enum Props
    {
        InnerColor,
        OuterColor,
        Exposure,
        Threshold,
        Contrast,
        SourceOpacity,
        Colorize,
        LinearColor,
        TintScale,
        TintGamma,
        ChromaR,
        ChromaG,
        ChromaB,
        RayLength,
        RayCenterX,
        RayCenterY,
        RaySamples,
        TexWidth,
        TexHeight,
        RayFalloff,
        RayStyle,
        RayAngle,
        ChromaStyle,
        ChromaAngle,
        ChromaCenterX,
        ChromaCenterY,
        FalloffMode,
        FalloffPower,
        DitherStrength,
        ClampAlpha
    }

    [CustomEffect(2)]
    private class EffectImpl : D2D1CustomShaderEffectImplBase<EffectImpl>
    {
        private ConstantBuffer constants;

        private static byte[] LoadShader()
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("HDRInjector.Shaders.HdrGlowShader.cso");
            if (stream == null)
            {
                throw new FileNotFoundException("HdrGlowShader.cso not found in embedded resources");
            }
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        public EffectImpl() : base(LoadShader())
        {
            constants = new ConstantBuffer
            {
                InnerColor = new Vector4(1f, 1f, 1f, 1f),
                OuterColor = new Vector4(1f, 1f, 1f, 1f),
                Exposure = 0.0f,
                Threshold = 0.0f,
                Contrast = 0.0f,
                SourceOpacity = 1.0f,
                Colorize = 0.0f,
                LinearColor = 1.0f,
                TintScale = 1.0f,
                TintGamma = 1.0f,
                ChromaR = 0.0f,
                ChromaG = 0.0f,
                ChromaB = 0.0f,
                RayLength = 0.0f,
                RayCenterX = 0.0f,
                RayCenterY = 0.0f,
                RaySamples = 8.0f,
                TexWidth = 1920.0f,
                TexHeight = 1080.0f,
                RayFalloff = 1.0f,
                RayStyle = 0.0f,
                RayAngle = 0.0f,
                ChromaStyle = 0.0f,
                ChromaAngle = 0.0f,
                ChromaCenterX = 0.0f,
                ChromaCenterY = 0.0f,
                FalloffMode = 1.0f, // デフォルト: 逆2乗型
                FalloffPower = 1.0f,
                DitherStrength = 1.0f,
                ClampAlpha = 1.0f
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
            float maxChroma = Math.Max(Math.Abs(constants.ChromaR), Math.Max(Math.Abs(constants.ChromaG), Math.Abs(constants.ChromaB)));
            float maxDim = Math.Max(constants.TexWidth, constants.TexHeight);
            if (maxDim < 1.0f) maxDim = 1920.0f;

            int margin = (int)(maxDim * constants.RayLength * 1.5f + maxDim * maxChroma * 1.5f) + 100;
            margin = Math.Min(margin, 8000);

            var expandedRect = new RawRect(
                outputRect.Left - margin, outputRect.Top - margin,
                outputRect.Right + margin, outputRect.Bottom + margin
            );

            for (int i = 0; i < inputRects.Length; i++)
            {
                inputRects[i] = expandedRect;
            }
        }

        [CustomEffectProperty(PropertyType.Vector4, (int)Props.InnerColor)] public Vector4 InnerColor { get => constants.InnerColor; set { constants.InnerColor = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Vector4, (int)Props.OuterColor)] public Vector4 OuterColor { get => constants.OuterColor; set { constants.OuterColor = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, (int)Props.Exposure)] public float Exposure { get => constants.Exposure; set { constants.Exposure = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, (int)Props.Threshold)] public float Threshold { get => constants.Threshold; set { constants.Threshold = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, (int)Props.Contrast)] public float Contrast { get => constants.Contrast; set { constants.Contrast = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, (int)Props.SourceOpacity)] public float SourceOpacity { get => constants.SourceOpacity; set { constants.SourceOpacity = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, (int)Props.Colorize)] public float Colorize { get => constants.Colorize; set { constants.Colorize = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, (int)Props.LinearColor)] public float LinearColor { get => constants.LinearColor; set { constants.LinearColor = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, (int)Props.TintScale)] public float TintScale { get => constants.TintScale; set { constants.TintScale = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, (int)Props.TintGamma)] public float TintGamma { get => constants.TintGamma; set { constants.TintGamma = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, (int)Props.ChromaR)] public float ChromaR { get => constants.ChromaR; set { constants.ChromaR = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, (int)Props.ChromaG)] public float ChromaG { get => constants.ChromaG; set { constants.ChromaG = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, (int)Props.ChromaB)] public float ChromaB { get => constants.ChromaB; set { constants.ChromaB = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, (int)Props.RayLength)] public float RayLength { get => constants.RayLength; set { constants.RayLength = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, (int)Props.RayCenterX)] public float RayCenterX { get => constants.RayCenterX; set { constants.RayCenterX = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, (int)Props.RayCenterY)] public float RayCenterY { get => constants.RayCenterY; set { constants.RayCenterY = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, (int)Props.RaySamples)] public float RaySamples { get => constants.RaySamples; set { constants.RaySamples = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, (int)Props.TexWidth)] public float TexWidth { get => constants.TexWidth; set { constants.TexWidth = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, (int)Props.TexHeight)] public float TexHeight { get => constants.TexHeight; set { constants.TexHeight = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, (int)Props.RayFalloff)] public float RayFalloff { get => constants.RayFalloff; set { constants.RayFalloff = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, (int)Props.RayStyle)] public float RayStyle { get => constants.RayStyle; set { constants.RayStyle = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, (int)Props.RayAngle)] public float RayAngle { get => constants.RayAngle; set { constants.RayAngle = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, (int)Props.ChromaStyle)] public float ChromaStyle { get => constants.ChromaStyle; set { constants.ChromaStyle = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, (int)Props.ChromaAngle)] public float ChromaAngle { get => constants.ChromaAngle; set { constants.ChromaAngle = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, (int)Props.ChromaCenterX)] public float ChromaCenterX { get => constants.ChromaCenterX; set { constants.ChromaCenterX = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, (int)Props.ChromaCenterY)] public float ChromaCenterY { get => constants.ChromaCenterY; set { constants.ChromaCenterY = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, (int)Props.FalloffMode)] public float FalloffMode { get => constants.FalloffMode; set { constants.FalloffMode = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, (int)Props.FalloffPower)] public float FalloffPower { get => constants.FalloffPower; set { constants.FalloffPower = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, (int)Props.DitherStrength)] public float DitherStrength { get => constants.DitherStrength; set { constants.DitherStrength = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, (int)Props.ClampAlpha)] public float ClampAlpha { get => constants.ClampAlpha; set { constants.ClampAlpha = value; UpdateConstants(); } }
    }
}
