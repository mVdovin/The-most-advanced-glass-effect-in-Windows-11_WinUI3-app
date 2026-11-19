using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using WinRT;

namespace RefactorApp
{
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
    internal interface ISwapChainPanelNative
    {
        [PreserveSig]
        int SetSwapChain(IntPtr swapChain);
    }

    internal sealed class DesktopBackgroundCaptureService : IDisposable
    {
        private readonly Window _window;
        private readonly SwapChainPanel _swapChainPanel;
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly IntPtr _hwnd;

        public Func<bool>? BeforeFrameRender { get; set; }
        public volatile bool IsDragging = false;

        // =========================================================================
        // НАСТРАИВАЕМЫЕ ПАРАМЕТРЫ ПРОИЗВОДИТЕЛЬНОСТИ:
        // =========================================================================
        // Запас захвата вокруг панели в пикселях (нужен для плавного сдвига и размытия по Гауссу)
        private const int CaptureMargin = 320;
        // Интервал опроса фона в состоянии покоя (33 мс ≈ 30 FPS)
        private const int IdleCaptureIntervalMs = 33;
        // Интервал опроса фона при перетаскивании окна (8 мс ≈ 120 FPS)
        private const int DragCaptureIntervalMs = 8;

        // =========================================================================
        // 1. РЕСУРСЫ UI-ПОТОКА И ИНКАПСУЛИРОВАННЫЙ КОНВЕЙЕР ШЕЙДЕРА
        // =========================================================================
        private IDXGIFactory2? _uiFactory;
        private ID3D11Device? _renderDevice;
        private IDXGISwapChain1? _swapChain;
        private ISwapChainPanelNative? _panelNative;
        private bool _swapChainBound = false;

        private LiquidGlassShaderPipeline? _shaderPipeline;

        private ID3D11Texture2D? _renderRegionTex;
        private ID3D11ShaderResourceView? _renderRegionSrv;
        private RECT _renderedScreenRect;
        private ModeRotation _renderedEffectiveRotation = ModeRotation.Identity;
        private volatile bool _needsShaderRedraw = false;

        // =========================================================================
        // 2. БУФЕР ОБМЕНА МЕЖДУ ПОТОКАМИ
        // =========================================================================
        private readonly object _frameLock = new();
        private byte[]? _sharedFrameBytes;
        private int _sharedTexWidth;
        private int _sharedTexHeight;
        private Format _sharedFormat = Format.B8G8R8A8_UNorm;
        private RECT _sharedCapturedScreenRect;
        private ModeRotation _sharedEffectiveRotation = ModeRotation.Identity;
        private volatile bool _hasPendingFrame = false;

        // =========================================================================
        // 3. РЕСУРСЫ ФОНОВОГО ПОТОКА ЗАХВАТА
        // =========================================================================
        private IDXGIFactory2? _captureFactory;
        private ID3D11Device? _captureDevice;
        private IDXGIOutputDuplication? _outputDuplication;

        private ID3D11Texture2D? _bgFullDesktopTex;
        private ID3D11Texture2D? _stagingTexture;
        private bool _bgHasFullFrame = false;

        private IntPtr _bgMonitorHandle = IntPtr.Zero;
        private RECT _bgMonitorRect;
        private ModeRotation _bgMonitorRotation = ModeRotation.Identity;
        private RECT _bgLastCapturedRect;
        private bool _bgHasInitialRegion = false;

        private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
        private const uint WDA_NONE = 0x00000000;

        private bool _isRunning;
        private bool _disposed;

        private float _scaleX = 1f, _scaleY = 1f;
        private double _panelOffsetX, _panelOffsetY;
        private double _panelWidth, _panelHeight;

        private int _lastWinX;
        private int _lastWinY;
        private bool _hasWinPos = false;

        private Task? _captureTask;
        private readonly CancellationTokenSource _cts = new();
        private readonly AutoResetEvent _wakeEvent = new(false);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left; public int Top; public int Right; public int Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        public DesktopBackgroundCaptureService(Window window, SwapChainPanel swapChainPanel)
        {
            _window = window;
            _swapChainPanel = swapChainPanel;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);

            _uiFactory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();

            if (_hwnd != IntPtr.Zero)
                SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE);    //WDA_NONE  WDA_EXCLUDEFROMCAPTURE

            D3D11.D3D11CreateDevice(
                null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_0 },
                out _renderDevice,
                out FeatureLevel _);

            if (_renderDevice != null)
            {
                _shaderPipeline = new LiquidGlassShaderPipeline(_renderDevice);
            }
        }

        public void UpdateVisualSettings(LensVisualSettings settings)
        {
            _shaderPipeline?.UpdateSettings(settings);
            _needsShaderRedraw = true;
        }

        public void Start()
        {
            if (_disposed || _isRunning) return;

            _panelNative = WinRT.CastExtensions.As<ISwapChainPanelNative>(_swapChainPanel);
            UpdatePanelMetrics();

            _window.AppWindow.Changed += OnAppWindowChanged;
            _swapChainPanel.SizeChanged += OnPanelSizeChanged;
            _swapChainPanel.CompositionScaleChanged += OnCompositionScaleChanged;

            CompositionTarget.Rendering += OnCompositionRendering;

            _isRunning = true;

            _captureTask = Task.Factory.StartNew(
                DesktopCaptureLoop,
                _cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        public void RenderForWindowPosition(int winX, int winY)
        {
            if (_disposed) return;

            Volatile.Write(ref _lastWinX, winX);
            Volatile.Write(ref _lastWinY, winY);
            _hasWinPos = true;

            RenderShaderFrameOnUiThread(winX, winY);
        }

        private void OnCompositionRendering(object? sender, object e)
        {
            if (_disposed) return;

            bool handledByDrag = BeforeFrameRender?.Invoke() ?? false;

            if (!handledByDrag && (_needsShaderRedraw || _hasPendingFrame))
            {
                int wx = Volatile.Read(ref _lastWinX);
                int wy = Volatile.Read(ref _lastWinY);
                if (!_hasWinPos && GetWindowRect(_hwnd, out var rect))
                {
                    wx = rect.Left;
                    wy = rect.Top;
                    Volatile.Write(ref _lastWinX, wx);
                    Volatile.Write(ref _lastWinY, wy);
                    _hasWinPos = true;
                }
                RenderShaderFrameOnUiThread(wx, wy);
            }
        }

        public void UpdatePanelMetrics()
        {
            if (_swapChainPanel.XamlRoot == null) return;
            try
            {
                var transform = _swapChainPanel.TransformToVisual(null);
                var pt = transform.TransformPoint(new Windows.Foundation.Point(0, 0));

                float sx = (float)_swapChainPanel.CompositionScaleX;
                float sy = (float)_swapChainPanel.CompositionScaleY;

                float newScaleX = sx > 0 ? sx : 1f;
                float newScaleY = sy > 0 ? sy : 1f;
                double newW = _swapChainPanel.ActualWidth;
                double newH = _swapChainPanel.ActualHeight;

                if (Math.Abs(newScaleX - _scaleX) < 0.001f &&
                    Math.Abs(newScaleY - _scaleY) < 0.001f &&
                    Math.Abs(pt.X - _panelOffsetX) < 0.1 &&
                    Math.Abs(pt.Y - _panelOffsetY) < 0.1 &&
                    Math.Abs(newW - _panelWidth) < 0.1 &&
                    Math.Abs(newH - _panelHeight) < 0.1)
                {
                    return;
                }

                _scaleX = newScaleX;
                _scaleY = newScaleY;
                _panelOffsetX = pt.X;
                _panelOffsetY = pt.Y;
                _panelWidth = newW;
                _panelHeight = newH;

                _bgHasInitialRegion = false;
                _needsShaderRedraw = true;
                _wakeEvent.Set();
            }
            catch { }
        }

        private void OnCompositionScaleChanged(SwapChainPanel sender, object args) => UpdatePanelMetrics();
        private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePanelMetrics();

        private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (args.DidSizeChange)
            {
                UpdatePanelMetrics();
            }
        }

        // =========================================================================
        // ФОНОВЫЙ ПОТОК: Захват и кэширование экрана в VRAM
        // =========================================================================
        private void DesktopCaptureLoop()
        {
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            _captureFactory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();

            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    int waitMs = IsDragging ? DragCaptureIntervalMs : IdleCaptureIntervalMs;
                    _wakeEvent.WaitOne(waitMs);
                    if (_cts.IsCancellationRequested) break;

                    try
                    {
                        CaptureRegionInBackground();
                    }
                    catch (Exception ex) when (IsDeviceLost(ex))
                    {
                        DisposeBackgroundCaptureObjects();
                    }
                    catch { }
                }
            }
            finally
            {
                DisposeBackgroundCaptureObjects();
                _captureFactory?.Dispose();
                _captureFactory = null;
            }
        }

        private void CaptureRegionInBackground()
        {
            if (_disposed || _hwnd == IntPtr.Zero || _captureFactory == null) return;

            int wx, wy;
            if (_hasWinPos)
            {
                wx = Volatile.Read(ref _lastWinX);
                wy = Volatile.Read(ref _lastWinY);
            }
            else
            {
                if (!GetWindowRect(_hwnd, out var rect)) return;
                wx = rect.Left;
                wy = rect.Top;
            }

            int panelW = (int)Math.Round(_panelWidth * _scaleX);
            int panelH = (int)Math.Round(_panelHeight * _scaleY);
            if (panelW <= 0 || panelH <= 0) return;

            int panelLeft = wx + (int)Math.Round(_panelOffsetX * _scaleX);
            int panelTop = wy + (int)Math.Round(_panelOffsetY * _scaleY);

            int centerX = panelLeft + panelW / 2;
            int centerY = panelTop + panelH / 2;

            if (!EnsureMonitorDuplicationInBackground(centerX, centerY)) return;
            if (_outputDuplication == null || _captureDevice == null) return;

            int acquireTimeout = _bgHasFullFrame ? 0 : 16;
            bool desktopPixelsUpdated = false;

            var acquireResult = _outputDuplication.AcquireNextFrame((uint)acquireTimeout, out var frameInfo, out var resource);
            if (acquireResult.Success)
            {
                try
                {
                    if (frameInfo.LastPresentTime != 0 || !_bgHasFullFrame)
                    {
                        using var desktopTexture = resource.QueryInterface<ID3D11Texture2D>();
                        var desc = desktopTexture.Description;

                        if (_bgFullDesktopTex == null ||
                            _bgFullDesktopTex.Description.Width != desc.Width ||
                            _bgFullDesktopTex.Description.Height != desc.Height ||
                            _bgFullDesktopTex.Description.Format != desc.Format)
                        {
                            _bgFullDesktopTex?.Dispose();
                            _bgFullDesktopTex = _captureDevice.CreateTexture2D(new Texture2DDescription
                            {
                                Width = desc.Width,
                                Height = desc.Height,
                                MipLevels = 1,
                                ArraySize = 1,
                                Format = desc.Format,
                                SampleDescription = new SampleDescription(1, 0),
                                Usage = ResourceUsage.Default,
                                BindFlags = BindFlags.None,
                                CPUAccessFlags = CpuAccessFlags.None,
                                MiscFlags = ResourceOptionFlags.None
                            });
                        }

                        _captureDevice.ImmediateContext.CopyResource(_bgFullDesktopTex, desktopTexture);
                        _bgHasFullFrame = true;
                        desktopPixelsUpdated = true;
                    }
                }
                finally
                {
                    _outputDuplication.ReleaseFrame();
                    resource.Dispose();
                }
            }
            else if (acquireResult.Code != DXGI_ERROR_WAIT_TIMEOUT)
            {
                DisposeBackgroundCaptureObjects();
                return;
            }

            if (!_bgHasFullFrame || _bgFullDesktopTex == null) return;

            int capLeft = Math.Clamp(panelLeft - CaptureMargin, _bgMonitorRect.Left, _bgMonitorRect.Right);
            int capTop = Math.Clamp(panelTop - CaptureMargin, _bgMonitorRect.Top, _bgMonitorRect.Bottom);
            int capRight = Math.Clamp(panelLeft + panelW + CaptureMargin, _bgMonitorRect.Left, _bgMonitorRect.Right);
            int capBottom = Math.Clamp(panelTop + panelH + CaptureMargin, _bgMonitorRect.Top, _bgMonitorRect.Bottom);

            int capScreenW = capRight - capLeft;
            int capScreenH = capBottom - capTop;
            if (capScreenW <= 0 || capScreenH <= 0) return;

            int safetyBuffer = CaptureMargin / 2;
            bool windowApproachingBufferEdge = !_bgHasInitialRegion ||
                (panelLeft - _bgLastCapturedRect.Left < safetyBuffer && capLeft < _bgLastCapturedRect.Left) ||
                (_bgLastCapturedRect.Right - (panelLeft + panelW) < safetyBuffer && capRight > _bgLastCapturedRect.Right) ||
                (panelTop - _bgLastCapturedRect.Top < safetyBuffer && capTop < _bgLastCapturedRect.Top) ||
                (_bgLastCapturedRect.Bottom - (panelTop + panelH) < safetyBuffer && capBottom > _bgLastCapturedRect.Bottom);

            if (!desktopPixelsUpdated && !windowApproachingBufferEdge)
            {
                return;
            }

            var fullDesc = _bgFullDesktopTex.Description;
            int monW = _bgMonitorRect.Width;
            int monH = _bgMonitorRect.Height;

            bool isPortrait = (_bgMonitorRotation == ModeRotation.Rotate90 || _bgMonitorRotation == ModeRotation.Rotate270);
            bool textureIsUnrotated = isPortrait && ((int)fullDesc.Width == monH && (int)fullDesc.Height == monW);

            ModeRotation effectiveRotation = textureIsUnrotated
                ? _bgMonitorRotation
                : (_bgMonitorRotation == ModeRotation.Rotate180 ? ModeRotation.Rotate180 : ModeRotation.Identity);

            var (texX, texY, texW, texH) = ScreenRectToTextureRect(
                capLeft - _bgMonitorRect.Left,
                capTop - _bgMonitorRect.Top,
                capScreenW,
                capScreenH,
                monW,
                monH,
                effectiveRotation);

            if (texX < 0 || texY < 0 || texX + texW > fullDesc.Width || texY + texH > fullDesc.Height || texW <= 0 || texH <= 0)
                return;

            if (_stagingTexture == null ||
                _stagingTexture.Description.Width != texW ||
                _stagingTexture.Description.Height != texH ||
                _stagingTexture.Description.Format != fullDesc.Format)
            {
                _stagingTexture?.Dispose();
                _stagingTexture = _captureDevice.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)texW,
                    Height = (uint)texH,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = fullDesc.Format,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    MiscFlags = ResourceOptionFlags.None
                });
            }

            var srcBox = new Box(texX, texY, 0, texX + texW, texY + texH, 1);
            _captureDevice.ImmediateContext.CopySubresourceRegion(_stagingTexture, 0, 0, 0, 0, _bgFullDesktopTex, 0, srcBox);

            var mapped = _captureDevice.ImmediateContext.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int bytesPerPixel = (fullDesc.Format == Format.R16G16B16A16_Float) ? 8 : 4;
                int rowBytes = texW * bytesPerPixel;
                int totalBytes = rowBytes * texH;

                lock (_frameLock)
                {
                    if (_sharedFrameBytes == null || _sharedFrameBytes.Length != totalBytes)
                    {
                        _sharedFrameBytes = new byte[totalBytes];
                    }

                    for (int y = 0; y < texH; y++)
                    {
                        Marshal.Copy(
                            IntPtr.Add(mapped.DataPointer, y * (int)mapped.RowPitch),
                            _sharedFrameBytes,
                            y * rowBytes,
                            rowBytes);
                    }

                    _sharedTexWidth = texW;
                    _sharedTexHeight = texH;
                    _sharedFormat = fullDesc.Format;
                    _sharedCapturedScreenRect = new RECT
                    {
                        Left = capLeft,
                        Top = capTop,
                        Right = capRight,
                        Bottom = capBottom
                    };
                    _sharedEffectiveRotation = effectiveRotation;
                    _hasPendingFrame = true;
                }

                _bgLastCapturedRect = new RECT { Left = capLeft, Top = capTop, Right = capRight, Bottom = capBottom };
                _bgHasInitialRegion = true;
            }
            finally
            {
                _captureDevice.ImmediateContext.Unmap(_stagingTexture, 0);
            }
        }

        private static (int texX, int texY, int texW, int texH) ScreenRectToTextureRect(
            int screenX, int screenY, int screenW, int screenH,
            int monScreenW, int monScreenH,
            ModeRotation rotation)
        {
            return rotation switch
            {
                ModeRotation.Rotate90 => (
                    texX: screenY,
                    texY: monScreenW - (screenX + screenW),
                    texW: screenH,
                    texH: screenW
                ),
                ModeRotation.Rotate270 => (
                    texX: monScreenH - (screenY + screenH),
                    texY: screenX,
                    texW: screenH,
                    texH: screenW
                ),
                ModeRotation.Rotate180 => (
                    texX: monScreenW - (screenX + screenW),
                    texY: monScreenH - (screenY + screenH),
                    texW: screenW,
                    texH: screenH
                ),
                _ => (
                    texX: screenX,
                    texY: screenY,
                    texW: screenW,
                    texH: screenH
                )
            };
        }

        private bool EnsureMonitorDuplicationInBackground(int centerX, int centerY)
        {
            if (_outputDuplication != null && _bgMonitorHandle != IntPtr.Zero)
            {
                if (centerX >= _bgMonitorRect.Left && centerX < _bgMonitorRect.Right &&
                    centerY >= _bgMonitorRect.Top && centerY < _bgMonitorRect.Bottom)
                {
                    return true;
                }
            }

            IDXGIAdapter1? foundAdapter = null;
            IDXGIOutput? foundOutput = null;
            RECT foundBounds = default;
            ModeRotation foundRotation = ModeRotation.Identity;

            for (uint i = 0; _captureFactory!.EnumAdapters1(i, out var adapter).Success; i++)
            {
                for (uint j = 0; adapter.EnumOutputs(j, out var output).Success; j++)
                {
                    var desc = output.Description;
                    var coords = desc.DesktopCoordinates;
                    var bounds = new RECT
                    {
                        Left = coords.Left,
                        Top = coords.Top,
                        Right = coords.Right,
                        Bottom = coords.Bottom
                    };

                    if (centerX >= bounds.Left && centerX < bounds.Right &&
                        centerY >= bounds.Top && centerY < bounds.Bottom)
                    {
                        foundAdapter = adapter;
                        foundOutput = output;
                        foundBounds = bounds;
                        foundRotation = desc.Rotation;
                        break;
                    }
                    output.Dispose();
                }

                if (foundOutput != null) break;
                adapter.Dispose();
            }

            if (foundOutput == null || foundAdapter == null) return false;

            if (_outputDuplication != null && _bgMonitorHandle == foundOutput.Description.Monitor)
            {
                foundOutput.Dispose();
                foundAdapter.Dispose();
                return true;
            }

            DisposeBackgroundCaptureObjects();

            _bgMonitorRect = foundBounds;
            _bgMonitorHandle = foundOutput.Description.Monitor;
            _bgMonitorRotation = foundRotation;

            try
            {
                D3D11.D3D11CreateDevice(
                    foundAdapter,
                    DriverType.Unknown,
                    DeviceCreationFlags.BgraSupport,
                    new[] { FeatureLevel.Level_11_0 },
                    out _captureDevice,
                    out FeatureLevel _);

                using var output1 = foundOutput.QueryInterface<IDXGIOutput1>();
                _outputDuplication = output1.DuplicateOutput(_captureDevice);
            }
            catch
            {
                DisposeBackgroundCaptureObjects();
                return false;
            }
            finally
            {
                foundOutput.Dispose();
                foundAdapter.Dispose();
            }

            return _outputDuplication != null;
        }

        private void DisposeBackgroundCaptureObjects()
        {
            _outputDuplication?.Dispose();
            _outputDuplication = null;

            _stagingTexture?.Dispose();
            _stagingTexture = null;

            _bgFullDesktopTex?.Dispose();
            _bgFullDesktopTex = null;

            if (_captureDevice != null)
            {
                _captureDevice.ImmediateContext?.ClearState();
                _captureDevice.ImmediateContext?.Flush();
                _captureDevice.Dispose();
                _captureDevice = null;
            }

            _bgMonitorHandle = IntPtr.Zero;
            _bgMonitorRect = default;
            _bgMonitorRotation = ModeRotation.Identity;
            _bgHasFullFrame = false;
            _bgHasInitialRegion = false;
        }

        // =========================================================================
        // UI-ПОТОК: Передача текстуры в инкапсулированный шейдерный конвейер
        // =========================================================================
        private void UploadPendingFrameOnUiThread()
        {
            if (!_hasPendingFrame || _renderDevice == null) return;

            lock (_frameLock)
            {
                if (!_hasPendingFrame || _sharedFrameBytes == null || _sharedTexWidth <= 0 || _sharedTexHeight <= 0)
                    return;

                if (_renderRegionTex == null ||
                    _renderRegionTex.Description.Width != _sharedTexWidth ||
                    _renderRegionTex.Description.Height != _sharedTexHeight ||
                    _renderRegionTex.Description.Format != _sharedFormat)
                {
                    _renderRegionSrv?.Dispose();
                    _renderRegionTex?.Dispose();

                    _renderRegionTex = _renderDevice.CreateTexture2D(new Texture2DDescription
                    {
                        Width = (uint)_sharedTexWidth,
                        Height = (uint)_sharedTexHeight,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = _sharedFormat,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.ShaderResource,
                        CPUAccessFlags = CpuAccessFlags.None,
                        MiscFlags = ResourceOptionFlags.None
                    });

                    _renderRegionSrv = _renderDevice.CreateShaderResourceView(_renderRegionTex);
                }

                int bytesPerPixel = (_sharedFormat == Format.R16G16B16A16_Float) ? 8 : 4;
                uint rowPitch = (uint)(_sharedTexWidth * bytesPerPixel);

                var pinnedArray = GCHandle.Alloc(_sharedFrameBytes, GCHandleType.Pinned);
                try
                {
                    _renderDevice.ImmediateContext.UpdateSubresource(
                        _renderRegionTex,
                        0,
                        null,
                        pinnedArray.AddrOfPinnedObject(),
                        rowPitch,
                        0);
                }
                finally
                {
                    pinnedArray.Free();
                }

                _renderedScreenRect = _sharedCapturedScreenRect;
                _renderedEffectiveRotation = _sharedEffectiveRotation;
                _hasPendingFrame = false;
            }
        }

        private void RenderShaderFrameOnUiThread(int winX, int winY)
        {
            if (_disposed || _renderDevice == null || _shaderPipeline == null) return;

            try
            {
                if (_hasPendingFrame)
                {
                    UploadPendingFrameOnUiThread();
                }

                if (_renderRegionTex == null || _renderRegionSrv == null) return;
                if (_renderedScreenRect.Width <= 0 || _renderedScreenRect.Height <= 0) return;

                int panelWidthPx = (int)Math.Round(_panelWidth * _scaleX);
                int panelHeightPx = (int)Math.Round(_panelHeight * _scaleY);
                if (panelWidthPx <= 0 || panelHeightPx <= 0) return;

                if (!EnsurePanelSwapChainOnUiThread(panelWidthPx, panelHeightPx, _scaleX, _scaleY))
                    return;

                if (_swapChain == null || !_swapChainBound) return;

                float panelLeftPx = winX + (float)(_panelOffsetX * _scaleX);
                float panelTopPx = winY + (float)(_panelOffsetY * _scaleY);

                int safetyBuffer = CaptureMargin / 2;
                if (panelLeftPx - _renderedScreenRect.Left < safetyBuffer ||
                    _renderedScreenRect.Right - (panelLeftPx + panelWidthPx) < safetyBuffer ||
                    panelTopPx - _renderedScreenRect.Top < safetyBuffer ||
                    _renderedScreenRect.Bottom - (panelTopPx + panelHeightPx) < safetyBuffer)
                {
                    _wakeEvent.Set();
                }

                float u0 = (panelLeftPx - _renderedScreenRect.Left) / _renderedScreenRect.Width;
                float v0 = (panelTopPx - _renderedScreenRect.Top) / _renderedScreenRect.Height;
                float u1 = (panelLeftPx + panelWidthPx - _renderedScreenRect.Left) / _renderedScreenRect.Width;
                float v1 = (panelTopPx + panelHeightPx - _renderedScreenRect.Top) / _renderedScreenRect.Height;

                // Передаём все параметры, включая формат текстуры (textureFormat)
                _shaderPipeline.Render(
                    swapChain: _swapChain,
                    backgroundSrv: _renderRegionSrv,
                    textureWidth: (int)_renderRegionTex.Description.Width,
                    textureHeight: (int)_renderRegionTex.Description.Height,
                    textureFormat: _renderRegionTex.Description.Format,
                    panelWidthPx: panelWidthPx,
                    panelHeightPx: panelHeightPx,
                    dpiScale: _scaleX,
                    uvTopLeft: MapScreenUvToTextureUv(u0, v0, _renderedEffectiveRotation),
                    uvTopRight: MapScreenUvToTextureUv(u1, v0, _renderedEffectiveRotation),
                    uvBottomLeft: MapScreenUvToTextureUv(u0, v1, _renderedEffectiveRotation),
                    uvBottomRight: MapScreenUvToTextureUv(u1, v1, _renderedEffectiveRotation));

                _needsShaderRedraw = false;
            }
            catch { }
        }

        private static Vector2 MapScreenUvToTextureUv(float u, float v, ModeRotation rotation)
        {
            return rotation switch
            {
                ModeRotation.Rotate90 => new Vector2(v, 1.0f - u),
                ModeRotation.Rotate270 => new Vector2(1.0f - v, u),
                ModeRotation.Rotate180 => new Vector2(1.0f - u, 1.0f - v),
                _ => new Vector2(u, v)
            };
        }

        private bool EnsurePanelSwapChainOnUiThread(int width, int height, float scaleX, float scaleY)
        {
            if (_uiFactory == null || _renderDevice == null) return false;

            try
            {
                if (_swapChain != null)
                {
                    if (_swapChain.Description1.Width != width || _swapChain.Description1.Height != height)
                    {
                        _renderDevice.ImmediateContext.ClearState();
                        _swapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
                    }
                }
                else
                {
                    var desc = new SwapChainDescription1
                    {
                        Width = (uint)width,
                        Height = (uint)height,
                        Format = Format.B8G8R8A8_UNorm,
                        Stereo = false,
                        SampleDescription = new SampleDescription(1, 0),
                        BufferUsage = Usage.RenderTargetOutput,
                        BufferCount = 2,
                        Scaling = Scaling.Stretch,
                        SwapEffect = SwapEffect.FlipSequential,
                        AlphaMode = AlphaMode.Premultiplied
                    };

                    _swapChain = _uiFactory.CreateSwapChainForComposition(_renderDevice, desc);
                    if (_panelNative != null)
                    {
                        _panelNative.SetSwapChain(_swapChain.NativePointer);
                        _swapChainBound = true;
                    }
                }

                using var swapChain2 = _swapChain.QueryInterface<IDXGISwapChain2>();
                if (swapChain2 != null)
                {
                    swapChain2.MatrixTransform = Matrix3x2.CreateScale(1.0f / scaleX, 1.0f / scaleY);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsDeviceLost(Exception ex)
        {
            int code = (int)(ex is SharpGenException sge ? sge.ResultCode : (ex is COMException ce ? ce.ErrorCode : 0));
            return code == unchecked((int)0x887A0005) || code == unchecked((int)0x887A0006);
        }

        public void Dispose()
        {
            _disposed = true;
            _cts.Cancel();
            _wakeEvent.Set();

            if (_isRunning)
            {
                CompositionTarget.Rendering -= OnCompositionRendering;
                _window.AppWindow.Changed -= OnAppWindowChanged;
                _swapChainPanel.SizeChanged -= OnPanelSizeChanged;
                _swapChainPanel.CompositionScaleChanged -= OnCompositionScaleChanged;
                _isRunning = false;
            }

            _captureTask?.Wait(500);
            _wakeEvent.Dispose();

            _renderRegionSrv?.Dispose();
            _renderRegionTex?.Dispose();

            _shaderPipeline?.Dispose();
            _shaderPipeline = null;

            if (_swapChain != null)
            {
                _renderDevice?.ImmediateContext?.ClearState();
                _swapChain.Dispose();
                _swapChain = null;
            }

            _renderDevice?.Dispose();
            _renderDevice = null;

            _uiFactory?.Dispose();
            _uiFactory = null;

            _cts.Dispose();
        }
    }
}