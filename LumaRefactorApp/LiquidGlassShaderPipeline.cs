using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RefactorApp
{
    /// <summary>
    /// Графический конвейер Direct3D 11 (Shader Model 5.0 / ps_5_0):
    /// 1) Краевое суперэллиптическое преломление (Distortion), выпуклость/вогнутость (LensCurve) и RGBShift;
    /// 2) Двухпроходное сепарабельное размытие Гаусса;
    /// 3) Сглаженный процедурный микрорельеф матового стекла с крупными органическими частицами;
    /// 4) Направленный сглаженный градиент с гамма-коррекцией + свечение окантовки + треугольный дизеринг.
    /// </summary>
    internal sealed class LiquidGlassShaderPipeline : IDisposable
    {
        private readonly ID3D11Device _device;

        private ID3D11VertexShader? _vertexShader;
        private ID3D11PixelShader? _blurHorizontalShader;
        private ID3D11PixelShader? _blurVerticalShader;
        private ID3D11PixelShader? _mainGlassShader;

        private ID3D11Buffer? _constantBuffer;
        private ID3D11SamplerState? _linearSampler;

        private ID3D11Texture2D? _blurTempTex;
        private ID3D11RenderTargetView? _blurTempRtv;
        private ID3D11ShaderResourceView? _blurTempSrv;

        private ID3D11Texture2D? _blurFinalTex;
        private ID3D11RenderTargetView? _blurFinalRtv;
        private ID3D11ShaderResourceView? _blurFinalSrv;

        private LensVisualSettings _settings;
        private bool _disposed;

        [StructLayout(LayoutKind.Sequential)]
        private struct ShaderConstantBufferData
        {
            public Vector2 UvTopLeft;
            public Vector2 UvTopRight;

            public Vector2 UvBottomLeft;
            public Vector2 UvBottomRight;

            public Vector2 TexelSize;
            public Vector2 PanelSize;

            public float CornerRadius;
            public float IsCircle;
            public float EdgeWidth;
            public float Distortion;

            public float LensCurve;
            public float RgbShift;
            public float BlurIntensity;
            public float BorderThickness;

            public Vector4 BlurBoundsUv;

            public float DpiScale;
            public float BorderGlow;
            public float BorderGlowColor;
            public float OverlayGradientAngle;

            public float OverlayGradientIntensity;
            public float OverlayGradientColor;
            public float TextureDensity;
            public float TextureBrightness;
        }

        private const string LiquidGlassHlsl = @"
cbuffer LensParams : register(b0)
{
    float2 UvTopLeft;
    float2 UvTopRight;
    float2 UvBottomLeft;
    float2 UvBottomRight;

    float2 TexelSize;
    float2 PanelSize;

    float CornerRadius;
    float IsCircle;
    float EdgeWidth;
    float Distortion;

    float LensCurve;
    float RgbShift;
    float BlurIntensity;
    float BorderThickness;

    float4 BlurBoundsUv;

    float DpiScale;
    float BorderGlow;
    float BorderGlowColor;
    float OverlayGradientAngle;

    float OverlayGradientIntensity;
    float OverlayGradientColor;
    float TextureDensity;
    float TextureBrightness;
};

Texture2D DesktopTex : register(t0);
SamplerState LinearSampler : register(s0);

struct VSInput
{
    uint VertexId : SV_VertexID;
};

struct PSInput
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
    float2 LocalUv  : TEXCOORD1;
};

PSInput VSMain(VSInput input)
{
    PSInput output;

    float2 pos[4] = {
        float2(-1.0,  1.0),
        float2( 1.0,  1.0),
        float2(-1.0, -1.0),
        float2( 1.0, -1.0)
    };

    float2 local[4] = {
        float2(0.0, 0.0),
        float2(1.0, 0.0),
        float2(0.0, 1.0),
        float2(1.0, 1.0)
    };

    float2 uvs[4] = {
        UvTopLeft,
        UvTopRight,
        UvBottomLeft,
        UvBottomRight
    };

    output.Position = float4(pos[input.VertexId], 0.0, 1.0);
    output.TexCoord = uvs[input.VertexId];
    output.LocalUv  = local[input.VertexId];
    return output;
}

// =========================================================================
// ПРОХОД 1 И 2: Двухпроходный сепарабельный фильтр Гаусса
// =========================================================================
float4 PSBlurHorizontal(PSInput input) : SV_TARGET
{
    float2 centerUv = input.Position.xy * TexelSize;
    float sigma = max(0.5, BlurIntensity * 0.55);
    float twoSigmaSq = 2.0 * sigma * sigma;

    float3 accum = float3(0.0, 0.0, 0.0);
    float weightSum = 0.0;

    const int RADIUS = 24;
    [unroll]
    for (int i = -RADIUS; i <= RADIUS; i++)
    {
        float fi = (float)i;
        float w = exp(-(fi * fi) / twoSigmaSq);
        float2 sampleUv = clamp(
            centerUv + float2(fi * TexelSize.x, 0.0),
            BlurBoundsUv.xy + TexelSize * 0.5,
            BlurBoundsUv.zw - TexelSize * 0.5);

        accum += DesktopTex.SampleLevel(LinearSampler, sampleUv, 0).rgb * w;
        weightSum += w;
    }

    return float4(accum / weightSum, 1.0);
}

float4 PSBlurVertical(PSInput input) : SV_TARGET
{
    float2 centerUv = input.Position.xy * TexelSize;
    float sigma = max(0.5, BlurIntensity * 0.55);
    float twoSigmaSq = 2.0 * sigma * sigma;

    float3 accum = float3(0.0, 0.0, 0.0);
    float weightSum = 0.0;

    const int RADIUS = 24;
    [unroll]
    for (int i = -RADIUS; i <= RADIUS; i++)
    {
        float fi = (float)i;
        float w = exp(-(fi * fi) / twoSigmaSq);
        float2 sampleUv = clamp(
            centerUv + float2(0.0, fi * TexelSize.y),
            BlurBoundsUv.xy + TexelSize * 0.5,
            BlurBoundsUv.zw - TexelSize * 0.5);

        accum += DesktopTex.SampleLevel(LinearSampler, sampleUv, 0).rgb * w;
        weightSum += w;
    }

    return float4(accum / weightSum, 1.0);
}

// =========================================================================
// ПРОХОД 3: Основной шейдер стекла
// =========================================================================

// Внешняя маска формы (скруглённый прямоугольник или круг) для прозрачных углов окна
float SdOuterClip(float2 p, float2 halfSize, float radius)
{
    if (IsCircle > 0.5)
    {
        return length(p) - halfSize.x;
    }

    float2 q = abs(p) - (halfSize - radius);
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
}

// Перевод локальных UV объекта [0..1] в экранные UV текстуры DesktopTex
float2 LocalToScreenUv(float2 localUv)
{
    float2 topUv = lerp(UvTopLeft, UvTopRight, localUv.x);
    float2 botUv = lerp(UvBottomLeft, UvBottomRight, localUv.x);
    return lerp(topUv, botUv, localUv.y);
}

// Сглаженный 2D-шум с квинтической интерполяцией (C2-гладкость без резких граней)
float Hash21(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float SmoothNoise2D(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);

    float a = Hash21(i);
    float b = Hash21(i + float2(1.0, 0.0));
    float c = Hash21(i + float2(0.0, 1.0));
    float d = Hash21(i + float2(1.0, 1.0));

    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

// Крупнозернистая сглаженная карта микровысот (округлые купола без пиксельной ряби)
float GlassMicroHeight(float2 dipPos, float densityFactor)
{
    const float2x2 rot1 = float2x2( 0.80, -0.60,  0.60,  0.80);
    const float2x2 rot2 = float2x2(-0.42, -0.91,  0.91, -0.42);

    // Увеличенный размер частиц (примерно в 2.7 раза крупнее для плавного рельефа)
    float scaleBoost = 0.88 + densityFactor * 0.28;
    float2 p1 = dipPos * (0.28 * scaleBoost);
    float2 p2 = mul(rot1, dipPos) * (0.54 * scaleBoost) + float2(19.3, 47.1);
    float2 p3 = mul(rot2, dipPos) * (0.98 * scaleBoost) + float2(83.7, 11.4);

    // Мягкие округлые выпуклости (sqrt(v^2 + 0.08) убирает острые изломы)
    float v1 = SmoothNoise2D(p1) * 2.0 - 1.0;
    float v2 = SmoothNoise2D(p2) * 2.0 - 1.0;
    float n1 = 1.0 - sqrt(v1 * v1 + 0.08);
    float n2 = 1.0 - sqrt(v2 * v2 + 0.10);
    float n3 = SmoothNoise2D(p3);

    return n1 * 0.54 + n2 * 0.32 + n3 * 0.14;
}

// Треугольный дизеринг (Interleaved Gradient Noise) для полного устранения 8-битной «лесенки»
float InterleavedGradientNoise(float2 pixelPos)
{
    float3 magic = float3(0.06711056, 0.00583715, 52.9829189);
    return frac(magic.z * frac(dot(pixelPos, magic.xy)));
}

float3 ApplyTriangularDither(float3 color, float2 pixelPos)
{
    float n1 = InterleavedGradientNoise(pixelPos);
    float n2 = InterleavedGradientNoise(pixelPos + float2(17.0, 31.0));
    float triNoise = (n1 + n2 - 1.0) * (0.85 / 255.0);
    return saturate(color + triNoise);
}

float4 PSMain(PSInput input) : SV_TARGET
{
    // 0. Сглаженная обрезка по контуру XAML-рамки (чтобы углы SwapChainPanel были прозрачными)
    float2 halfSize = PanelSize * 0.5;
    float2 p = (input.LocalUv - 0.5) * PanelSize;
    float minHalf = min(halfSize.x, halfSize.y);
    float radius = (IsCircle > 0.5) ? minHalf : clamp(CornerRadius, 0.0, max(0.0, minHalf - 0.001));

    float outerDist = SdOuterClip(p, halfSize, radius);
    float shapeAlpha = 1.0 - smoothstep(-1.0, 0.5, outerDist);
    if (shapeAlpha <= 0.001)
    {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    // Локальные координаты объекта [0..1]
    float2 uv = input.LocalUv;

    // Дополнительная выпуклая/вогнутая линза (при LensCurve == 0 не влияет на uv)
    if (abs(LensCurve) > 0.0001)
    {
        float2 mLens = uv - 0.5;
        float r2 = saturate(dot(mLens * 2.0, mLens * 2.0));
        uv = 0.5 + mLens * (1.0 - LensCurve * 0.25 * (1.0 - r2));
    }

    // Расчет суперэллиптического поля (квадрат - степень 10, круг - степень 6)
    float shapeType = (IsCircle > 0.5) ? 1.0 : 0.0;
    float currentWidth = (shapeType < 0.5) ? (PanelSize.x / 4.0) : PanelSize.x;
    float2 m = uv - 0.5;
    float aspect = currentWidth / (PanelSize.y + 0.0001);

    float shape = 0.0;
    if (shapeType < 0.5)
    {
        shape = pow(abs(m.x * 2.0), 10.0) + pow(abs(m.y * 2.0), 10.0);
    }
    else
    {
        float2 distVec = m * 2.0 * float2(aspect, 1.0);
        shape = pow(length(distVec), 6.0);
    }

    float mask = saturate(shape / max(0.001, EdgeWidth));
    float2 dir = normalize(m * float2(aspect, 1.0) + 1e-6);

    // 1. Краевое искажение управляется параметром Distortion
    float sidePull = m.x * Distortion * 0.22;
    float refractPower = (Distortion * 1.65) * shape;

    float2 targetUV = uv - (dir * refractPower * 0.1);
    targetUV.x += sidePull * mask;

    float2 finalLocalUV = lerp(uv, clamp(targetUV, 0.001, 0.999), mask);

    // 2. Сглаженный микрорельеф матового стекла: симметричный градиент высот смещает лучи преломления
    float2 microNormal = float2(0.0, 0.0);
    float texDensity = saturate(TextureDensity);
    if (texDensity > 0.001)
    {
        float2 dipPos = p / max(DpiScale, 0.5);
        float eps = 0.75;
        float hx1 = GlassMicroHeight(dipPos + float2(eps, 0.0), texDensity);
        float hx0 = GlassMicroHeight(dipPos - float2(eps, 0.0), texDensity);
        float hy1 = GlassMicroHeight(dipPos + float2(0.0, eps), texDensity);
        float hy0 = GlassMicroHeight(dipPos - float2(0.0, eps), texDensity);
        microNormal = float2(hx1 - hx0, hy1 - hy0) / (2.0 * eps);

        float2 microRefract = microNormal * (texDensity * 0.026);
        finalLocalUV = clamp(finalLocalUV + microRefract, 0.001, 0.999);
    }

    // 3. Хроматическая аберрация
    float2 shift = dir * RgbShift * mask;

    float2 localUvR = clamp(finalLocalUV + shift, 0.001, 0.999);
    float2 localUvG = clamp(finalLocalUV,         0.001, 0.999);
    float2 localUvB = clamp(finalLocalUV - shift, 0.001, 0.999);

    float2 minScreenUv = BlurBoundsUv.xy + TexelSize * 0.5;
    float2 maxScreenUv = BlurBoundsUv.zw - TexelSize * 0.5;

    float2 screenUvR = clamp(LocalToScreenUv(localUvR), minScreenUv, maxScreenUv);
    float2 screenUvG = clamp(LocalToScreenUv(localUvG), minScreenUv, maxScreenUv);
    float2 screenUvB = clamp(LocalToScreenUv(localUvB), minScreenUv, maxScreenUv);

    float4 rCol = DesktopTex.SampleLevel(LinearSampler, screenUvR, 0);
    float4 gCol = DesktopTex.SampleLevel(LinearSampler, screenUvG, 0);
    float4 bCol = DesktopTex.SampleLevel(LinearSampler, screenUvB, 0);

    float3 resultRgb = float3(rCol.r, gCol.g, bCol.b);

    // 4. Мягкие шелковистые блики и объёмная глубина фактуры (TextureBrightness)
    if (texDensity > 0.001)
    {
        float texBright = saturate(TextureBrightness);
        float bgLuma = dot(resultRgb, float3(0.299, 0.587, 0.114));

        // 3D-нормаль сглаженной частицы для плавного перелива света
        float3 facetN = normalize(float3(microNormal * (2.6 + texDensity * 2.2), 1.0));
        float spec1 = pow(saturate(dot(facetN, normalize(float3(-0.45, -0.55, 0.70)))), 3.5);
        float spec2 = pow(saturate(dot(facetN, normalize(float3( 0.50,  0.40, 0.76)))), 4.5);
        float glint = (spec1 + spec2 * 0.60) * texDensity;

        // Плавная объёмная тень на обратных склонах зёрен
        float reliefShade = clamp(dot(microNormal, float2(-0.6, -0.6)) * 1.4, -0.28, 0.28);
        resultRgb *= (1.0 + reliefShade * texDensity * (0.22 + texBright * 0.55) * (0.25 + bgLuma));

        // Мягкие световые блики в зонах, где сквозь стекло проходит свет
        float lightResponse = 0.07 + pow(bgLuma, 0.85) * 1.50;
        float3 glintTint = lerp(float3(1.0, 1.0, 1.0), saturate(resultRgb * 1.40 + 0.25), 0.45);
        resultRgb += glintTint * (glint * texBright * lightResponse * 1.15);
    }

    // 5. Легкое притенение у самого края
    float edgeDarkening = 1.0 - pow(mask, 4.0) * 0.09;
    resultRgb *= edgeDarkening;

    // 6. Направленный сглаженный градиент поверх скляного вікна
    // Смягчён в углах (cornerFeather) и смешан в линейном пространстве (Gamma 2.2)
    if (OverlayGradientIntensity > 0.001)
    {
        float rad = radians(OverlayGradientAngle);
        float2 gradDir = float2(cos(rad), sin(rad));
        float2 centered = input.LocalUv - 0.5;

        float rawT = saturate(0.5 + dot(centered, gradDir) * 0.85);
        float gradProfile = rawT * rawT * rawT * (rawT * (rawT * 6.0 - 15.0) + 10.0);

        float cornerFeather = 1.0 - 0.55 * smoothstep(0.55, 1.65, shape);

        float gradTone = 1.0 - saturate(OverlayGradientColor);
        float3 gradRgb = float3(gradTone, gradTone, gradTone);
        float gradAlpha = saturate(gradProfile * cornerFeather * OverlayGradientIntensity * 0.72);

        float3 baseLinear = pow(max(resultRgb, 0.0), 2.2);
        float3 gradLinear = pow(max(gradRgb, 0.0), 2.2);
        float3 blendedLinear = lerp(baseLinear, gradLinear, gradAlpha);
        resultRgb = pow(max(blendedLinear, 0.0), 1.0 / 2.2);
    }

    // 7. Свечение окантовки (интенсивность BorderGlow + цвет от белого 0.0 к чёрному 1.0)
    if (BorderGlow > 0.001 && BorderThickness > 0.01)
    {
        float distFromEdge = max(0.0, -outerDist);
        float glowSpreadPx = (4.0 + BorderThickness * 3.2) * DpiScale;
        float glowHalo = exp(-distFromEdge / max(1.0, glowSpreadPx));

        float tone = 1.0 - saturate(BorderGlowColor);
        float3 glowColor = float3(tone, tone, tone);

        float thicknessFactor = saturate(BorderThickness / 1.5);
        float glowBlend = saturate(glowHalo * BorderGlow * 0.42 * thicknessFactor);

        resultRgb = lerp(resultRgb, glowColor, glowBlend) + glowColor * (glowBlend * 0.25);
    }

    // 8. Финальный треугольный дизеринг против 8-битной «лесенки»
    resultRgb = ApplyTriangularDither(resultRgb, input.Position.xy);

    return float4(resultRgb * shapeAlpha, shapeAlpha);
}
";

        #region D3DCompiler P/Invoke
        [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall, PreserveSig = true)]
        private static extern int D3DCompile(
            [In] byte[] pSrcData,
            nuint srcDataSize,
            [MarshalAs(UnmanagedType.LPStr)] string? pSourceName,
            IntPtr pDefines,
            IntPtr pInclude,
            [MarshalAs(UnmanagedType.LPStr)] string pEntrypoint,
            [MarshalAs(UnmanagedType.LPStr)] string pTarget,
            uint flags1,
            uint flags2,
            out IntPtr ppCode,
            out IntPtr ppErrorMsgs);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr GetBufferPointerDelegate(IntPtr pThis);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate nuint GetBufferSizeDelegate(IntPtr pThis);
        #endregion

        public LiquidGlassShaderPipeline(ID3D11Device device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));

            _settings = new LensVisualSettings
            {
                Width = 350,
                Height = 350,
                CornerRadius = 34,
                IsCircle = false,
                EdgeWidth = 1.20f,
                Distortion = 1.20f,
                LensCurve = 0.00f,
                RgbShift = 0.015f,
                BlurIntensity = 0f,
                BorderThickness = 1.5f,
                BorderGlow = 0.50f,
                BorderGlowColor = 0.00f,
                OverlayGradientAngle = 135f,
                OverlayGradientIntensity = 0.15f,
                OverlayGradientColor = 0.00f,
                TextureDensity = 0.45f,
                TextureBrightness = 0.40f
            };

            InitializeResources();
        }

        private void InitializeResources()
        {
            byte[] vsBytes = CompileHlsl(LiquidGlassHlsl, "VSMain", "vs_5_0");
            byte[] psBlurHBytes = CompileHlsl(LiquidGlassHlsl, "PSBlurHorizontal", "ps_5_0");
            byte[] psBlurVBytes = CompileHlsl(LiquidGlassHlsl, "PSBlurVertical", "ps_5_0");
            byte[] psMainBytes = CompileHlsl(LiquidGlassHlsl, "PSMain", "ps_5_0");

            _vertexShader = _device.CreateVertexShader(vsBytes);
            _blurHorizontalShader = _device.CreatePixelShader(psBlurHBytes);
            _blurVerticalShader = _device.CreatePixelShader(psBlurVBytes);
            _mainGlassShader = _device.CreatePixelShader(psMainBytes);

            _constantBuffer = _device.CreateBuffer(new BufferDescription
            {
                ByteWidth = (uint)Marshal.SizeOf<ShaderConstantBufferData>(),
                BindFlags = BindFlags.ConstantBuffer,
                Usage = ResourceUsage.Dynamic,
                CPUAccessFlags = CpuAccessFlags.Write
            });

            _linearSampler = _device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunc = ComparisonFunction.Never,
                MinLOD = 0,
                MaxLOD = float.MaxValue
            });
        }

        public void UpdateSettings(LensVisualSettings settings)
        {
            _settings = settings;
        }

        private void EnsureBlurTargets(int width, int height, Format format)
        {
            if (_blurTempTex != null &&
                _blurTempTex.Description.Width == width &&
                _blurTempTex.Description.Height == height &&
                _blurTempTex.Description.Format == format)
            {
                return;
            }

            DisposeBlurTargets();

            var blurDesc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            };

            _blurTempTex = _device.CreateTexture2D(blurDesc);
            _blurTempRtv = _device.CreateRenderTargetView(_blurTempTex);
            _blurTempSrv = _device.CreateShaderResourceView(_blurTempTex);

            _blurFinalTex = _device.CreateTexture2D(blurDesc);
            _blurFinalRtv = _device.CreateRenderTargetView(_blurFinalTex);
            _blurFinalSrv = _device.CreateShaderResourceView(_blurFinalTex);
        }

        public void Render(
            IDXGISwapChain1 swapChain,
            ID3D11ShaderResourceView backgroundSrv,
            int textureWidth,
            int textureHeight,
            Format textureFormat,
            int panelWidthPx,
            int panelHeightPx,
            float dpiScale,
            Vector2 uvTopLeft,
            Vector2 uvTopRight,
            Vector2 uvBottomLeft,
            Vector2 uvBottomRight)
        {
            if (_disposed || _vertexShader == null || _mainGlassShader == null ||
                _blurHorizontalShader == null || _blurVerticalShader == null ||
                _constantBuffer == null || _linearSampler == null)
            {
                return;
            }

            float innerRadiusDip = Math.Max(0f, _settings.CornerRadius - _settings.BorderThickness);
            float radiusPx = innerRadiusDip * dpiScale;
            float scaledBlur = _settings.BlurIntensity * dpiScale;
            bool useBlur = scaledBlur > 0.05f;

            float minU = Math.Min(Math.Min(uvTopLeft.X, uvTopRight.X), Math.Min(uvBottomLeft.X, uvBottomRight.X));
            float maxU = Math.Max(Math.Max(uvTopLeft.X, uvTopRight.X), Math.Max(uvBottomLeft.X, uvBottomRight.X));
            float minV = Math.Min(Math.Min(uvTopLeft.Y, uvTopRight.Y), Math.Min(uvBottomLeft.Y, uvBottomRight.Y));
            float maxV = Math.Max(Math.Max(uvTopLeft.Y, uvTopRight.Y), Math.Max(uvBottomLeft.Y, uvBottomRight.Y));

            int padPx = 300;
            int vpLeft = Math.Clamp((int)Math.Floor(minU * textureWidth) - padPx, 0, textureWidth);
            int vpTop = Math.Clamp((int)Math.Floor(minV * textureHeight) - padPx, 0, textureHeight);
            int vpRight = Math.Clamp((int)Math.Ceiling(maxU * textureWidth) + padPx, 0, textureWidth);
            int vpBottom = Math.Clamp((int)Math.Ceiling(maxV * textureHeight) + padPx, 0, textureHeight);

            int vpWidth = Math.Max(1, vpRight - vpLeft);
            int vpHeight = Math.Max(1, vpBottom - vpTop);

            var blurBoundsUv = new Vector4(
                (float)vpLeft / textureWidth,
                (float)vpTop / textureHeight,
                (float)vpRight / textureWidth,
                (float)vpBottom / textureHeight);

            var cbData = new ShaderConstantBufferData
            {
                UvTopLeft = uvTopLeft,
                UvTopRight = uvTopRight,
                UvBottomLeft = uvBottomLeft,
                UvBottomRight = uvBottomRight,
                TexelSize = new Vector2(1.0f / textureWidth, 1.0f / textureHeight),
                PanelSize = new Vector2(panelWidthPx, panelHeightPx),
                CornerRadius = radiusPx,
                IsCircle = _settings.IsCircle ? 1.0f : 0.0f,
                EdgeWidth = _settings.EdgeWidth,
                Distortion = _settings.Distortion,
                LensCurve = _settings.LensCurve,
                RgbShift = _settings.RgbShift,
                BlurIntensity = scaledBlur,
                BorderThickness = _settings.BorderThickness,
                BlurBoundsUv = blurBoundsUv,
                DpiScale = dpiScale,
                BorderGlow = _settings.BorderGlow,
                BorderGlowColor = _settings.BorderGlowColor,
                OverlayGradientAngle = _settings.OverlayGradientAngle,
                OverlayGradientIntensity = _settings.OverlayGradientIntensity,
                OverlayGradientColor = _settings.OverlayGradientColor,
                TextureDensity = _settings.TextureDensity,
                TextureBrightness = _settings.TextureBrightness
            };

            var ctx = _device.ImmediateContext;

            var mapped = ctx.Map(_constantBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            try
            {
                Marshal.StructureToPtr(cbData, mapped.DataPointer, false);
            }
            finally
            {
                ctx.Unmap(_constantBuffer, 0);
            }

            ctx.IASetInputLayout(null);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
            ctx.VSSetShader(_vertexShader);
            ctx.VSSetConstantBuffer(0, _constantBuffer);
            ctx.PSSetConstantBuffer(0, _constantBuffer);
            ctx.PSSetSampler(0, _linearSampler);

            ID3D11ShaderResourceView sourceForGlassSrv = backgroundSrv;

            if (useBlur)
            {
                EnsureBlurTargets(textureWidth, textureHeight, textureFormat);

                if (_blurTempRtv != null && _blurTempSrv != null && _blurFinalRtv != null && _blurFinalSrv != null)
                {
                    ctx.RSSetViewport(vpLeft, vpTop, vpWidth, vpHeight);

                    ctx.OMSetRenderTargets(_blurTempRtv);
                    ctx.PSSetShader(_blurHorizontalShader);
                    ctx.PSSetShaderResource(0, backgroundSrv);
                    ctx.Draw(4, 0);

                    ctx.PSSetShaderResource(0, (ID3D11ShaderResourceView?)null!);
                    ctx.OMSetRenderTargets(_blurFinalRtv);
                    ctx.PSSetShader(_blurVerticalShader);
                    ctx.PSSetShaderResource(0, _blurTempSrv);
                    ctx.Draw(4, 0);

                    ctx.PSSetShaderResource(0, (ID3D11ShaderResourceView?)null!);
                    sourceForGlassSrv = _blurFinalSrv;
                }
            }

            using var backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
            using var rtv = _device.CreateRenderTargetView(backBuffer);

            ctx.OMSetRenderTargets(rtv);
            ctx.RSSetViewport(0, 0, panelWidthPx, panelHeightPx);
            ctx.PSSetShader(_mainGlassShader);
            ctx.PSSetShaderResource(0, sourceForGlassSrv);

            ctx.Draw(4, 0);
            ctx.ClearState();

            swapChain.Present(0, PresentFlags.None);
        }

        private static byte[] CompileHlsl(string hlsl, string entryPoint, string target)
        {
            byte[] srcBytes = Encoding.UTF8.GetBytes(hlsl);

            int hr = D3DCompile(
                srcBytes,
                (nuint)srcBytes.Length,
                null,
                IntPtr.Zero,
                IntPtr.Zero,
                entryPoint,
                target,
                0,
                0,
                out IntPtr codeBlob,
                out IntPtr errorBlob);

            string? compilerMessage = null;
            if (errorBlob != IntPtr.Zero)
            {
                try
                {
                    compilerMessage = ReadBlobString(errorBlob);
                }
                finally
                {
                    Marshal.Release(errorBlob);
                }
            }

            if (hr < 0 || codeBlob == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"Ошибка компиляции HLSL ({entryPoint}): 0x{hr:X8}. {compilerMessage}");
            }

            try
            {
                return ReadBlobBytes(codeBlob);
            }
            finally
            {
                Marshal.Release(codeBlob);
            }
        }

        private static byte[] ReadBlobBytes(IntPtr blob)
        {
            IntPtr vtable = Marshal.ReadIntPtr(blob);
            var getPtr = Marshal.GetDelegateForFunctionPointer<GetBufferPointerDelegate>(Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size));
            var getSize = Marshal.GetDelegateForFunctionPointer<GetBufferSizeDelegate>(Marshal.ReadIntPtr(vtable, 4 * IntPtr.Size));

            IntPtr dataPtr = getPtr(blob);
            int size = (int)getSize(blob);

            byte[] result = new byte[size];
            Marshal.Copy(dataPtr, result, 0, size);
            return result;
        }

        private static string ReadBlobString(IntPtr blob)
        {
            byte[] bytes = ReadBlobBytes(blob);
            return Encoding.UTF8.GetString(bytes).TrimEnd('\0', '\r', '\n');
        }

        private void DisposeBlurTargets()
        {
            _blurFinalSrv?.Dispose();
            _blurFinalRtv?.Dispose();
            _blurFinalTex?.Dispose();

            _blurTempSrv?.Dispose();
            _blurTempRtv?.Dispose();
            _blurTempTex?.Dispose();

            _blurFinalSrv = null;
            _blurFinalRtv = null;
            _blurFinalTex = null;

            _blurTempSrv = null;
            _blurTempRtv = null;
            _blurTempTex = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            DisposeBlurTargets();

            _linearSampler?.Dispose();
            _constantBuffer?.Dispose();
            _mainGlassShader?.Dispose();
            _blurVerticalShader?.Dispose();
            _blurHorizontalShader?.Dispose();
            _vertexShader?.Dispose();
        }
    }
}