using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace RefactorApp
{
    public struct LensVisualSettings
    {
        public float Width;
        public float Height;
        public float CornerRadius;
        public bool IsCircle;
        public float EdgeWidth;
        public float Distortion;
        public float LensCurve;
        public float RgbShift;
        public float BlurIntensity;
        public float ShadowBlurRadius;
        public float ShadowDepth;
        public float ShadowOpacity;
        public float BorderThickness;
        public float BorderGlow;
        public float BorderGlowColor;
        public float OverlayGradientAngle;
        public float OverlayGradientIntensity;
        public float OverlayGradientColor;
        public float TextureDensity;
        public float TextureBrightness;
    }

    public static class WindowsSystemDispatcherQueueHelper
    {
        [StructLayout(LayoutKind.Sequential)]
        struct DispatcherQueueOptions { public int dwSize; public int threadType; public int apartmentType; }

        [DllImport("CoreMessaging.dll")]
        private static extern int CreateDispatcherQueueController([In] DispatcherQueueOptions options, [Out] out IntPtr dispatcherQueueController);
        private static IntPtr _dispatcherQueueController = IntPtr.Zero;

        public static void EnsureWindowsSystemDispatcherQueueController()
        {
            if (Windows.System.DispatcherQueue.GetForCurrentThread() != null) return;
            DispatcherQueueOptions options = new DispatcherQueueOptions
            {
                dwSize = Marshal.SizeOf(typeof(DispatcherQueueOptions)),
                threadType = 2,
                apartmentType = 2
            };
            CreateDispatcherQueueController(options, out _dispatcherQueueController);
        }
    }

    public class PureTransparentBackdrop : SystemBackdrop
    {
        private Windows.UI.Composition.Compositor? _windowsCompositor;

        protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
        {
            base.OnTargetConnected(connectedTarget, xamlRoot);
            WindowsSystemDispatcherQueueHelper.EnsureWindowsSystemDispatcherQueueController();
            _windowsCompositor = new Windows.UI.Composition.Compositor();
            connectedTarget.SystemBackdrop = _windowsCompositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        }

        protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
        {
            base.OnTargetDisconnected(disconnectedTarget);
            disconnectedTarget.SystemBackdrop = null;
            _windowsCompositor?.Dispose();
            _windowsCompositor = null;
        }
    }

    public class TransparentWindowController : IDisposable
    {
        private readonly Window _window;
        private readonly UIElement _dragRegionGrid;
        private readonly UIElement _targetBorder;
        private readonly DesktopBackgroundCaptureService _backgroundCapture;
        public readonly AppWindow AppWindow;
        private readonly IntPtr _hwnd;
        private readonly DispatcherTimer _hitTestTimer;

        private SpriteVisual? _shadowVisual;
        private DropShadow? _dropShadow;
        private CompositionRoundedRectangleGeometry? _shadowGeometry;
        private CompositionSpriteShape? _shadowShape;
        private ShapeVisual? _shadowShapeVisual;
        private CompositionVisualSurface? _shadowVisualSurface;
        private CompositionSurfaceBrush? _shadowMaskBrush;

        public bool IsMenuOpen { get; set; } = false;
        public LensVisualSettings CurrentSettings { get; private set; }

        private bool _isDragging = false;
        private int _dragStartCursorX;
        private int _dragStartCursorY;
        private int _dragStartWinX;
        private int _dragStartWinY;

        private double _targetWidth = 350;
        private double _targetHeight = 350;

        private const double MaxCanvasWidth = 1000;
        private const double MaxCanvasHeight = 800;

        public readonly int PaddingTop = 1010;
        public readonly int PaddingBottom = 50;
        public readonly int PaddingLeft = 50;
        public readonly int PaddingRight = 50;

        #region Win32 API
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MARGINS { public int cxLeftWidth; public int cxRightWidth; public int cyTopHeight; public int cyBottomHeight; }

        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const uint WS_POPUP = 0x80000000;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_DONOTROUND = 1;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
        private const int VK_LBUTTON = 0x01;
        #endregion

        public TransparentWindowController(
            Window window,
            Microsoft.UI.Xaml.Controls.SwapChainPanel targetPanel,
            UIElement dragRegionGrid,
            UIElement targetBorder)
        {
            _window = window;
            _dragRegionGrid = dragRegionGrid;
            _targetBorder = targetBorder;
            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
            AppWindow = AppWindow.GetFromWindowId(windowId);

            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
            }

            int style = GetWindowLong(_hwnd, GWL_STYLE);
            SetWindowLong(_hwnd, GWL_STYLE, (int)((style & ~0x00C00000 & ~0x00040000) | WS_POPUP));
            int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
            SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);

            int cornerPreference = DWMWCP_DONOTROUND;
            DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));
            int borderColor = DWMWA_COLOR_NONE;
            DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));

            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);

            var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            DwmExtendFrameIntoClientArea(_hwnd, ref margins);
            _window.SystemBackdrop = new PureTransparentBackdrop();

            InitializeCompositionShadow();

            _backgroundCapture = new DesktopBackgroundCaptureService(_window, targetPanel);
            _backgroundCapture.BeforeFrameRender = SyncDragWithFrame;

            InitializeFixedWorkspaceSize();
            _backgroundCapture.Start();

            _hitTestTimer = new DispatcherTimer();
            _hitTestTimer.Interval = TimeSpan.FromMilliseconds(20);
            _hitTestTimer.Tick += HitTestTimer_Tick;
            _hitTestTimer.Start();

            _window.Closed += OnWindowClosed;
        }

        private void InitializeCompositionShadow()
        {
            Visual hostVisual = ElementCompositionPreview.GetElementVisual(_dragRegionGrid);
            Compositor compositor = hostVisual.Compositor;

            _shadowGeometry = compositor.CreateRoundedRectangleGeometry();
            _shadowGeometry.Size = new Vector2(350f, 350f);
            _shadowGeometry.CornerRadius = new Vector2(34f, 34f);

            _shadowShape = compositor.CreateSpriteShape(_shadowGeometry);
            _shadowShape.FillBrush = compositor.CreateColorBrush(Colors.White);

            _shadowShapeVisual = compositor.CreateShapeVisual();
            _shadowShapeVisual.Size = new Vector2(350f, 350f);
            _shadowShapeVisual.Shapes.Add(_shadowShape);

            _shadowVisualSurface = compositor.CreateVisualSurface();
            _shadowVisualSurface.SourceVisual = _shadowShapeVisual;
            _shadowVisualSurface.SourceSize = new Vector2(350f, 350f);

            _shadowMaskBrush = compositor.CreateSurfaceBrush(_shadowVisualSurface);
            _shadowMaskBrush.Stretch = CompositionStretch.None;

            _dropShadow = compositor.CreateDropShadow();
            _dropShadow.Color = Colors.Black;
            _dropShadow.BlurRadius = 12f;
            _dropShadow.Opacity = 0.41f;
            _dropShadow.Offset = new Vector3(3.1f, 3.5f, 0f);
            _dropShadow.Mask = _shadowMaskBrush;

            _shadowVisual = compositor.CreateSpriteVisual();
            _shadowVisual.Size = new Vector2(350f, 350f);
            _shadowVisual.Shadow = _dropShadow;

            ElementCompositionPreview.SetElementChildVisual(_dragRegionGrid, _shadowVisual);
        }

        private void UpdateCompositionShadow(LensVisualSettings settings)
        {
            if (_shadowVisual == null || _dropShadow == null || _shadowGeometry == null ||
                _shadowShapeVisual == null || _shadowVisualSurface == null)
            {
                return;
            }

            float w = Math.Max(1f, settings.Width);
            float h = Math.Max(1f, settings.Height);
            float maxR = Math.Min(w, h) * 0.5f;
            float r = settings.IsCircle ? maxR : Math.Clamp(settings.CornerRadius, 0f, maxR);

            var sizeVec = new Vector2(w, h);
            _shadowGeometry.Size = sizeVec;
            _shadowGeometry.CornerRadius = new Vector2(r, r);
            _shadowShapeVisual.Size = sizeVec;
            _shadowVisualSurface.SourceSize = sizeVec;
            _shadowVisual.Size = sizeVec;

            float opacity = Math.Clamp(settings.ShadowOpacity, 0f, 1f);
            if (opacity <= 0.001f)
            {
                _dropShadow.Opacity = 0f;
                _shadowVisual.IsVisible = false;
                return;
            }

            _shadowVisual.IsVisible = true;
            _dropShadow.Opacity = opacity;
            _dropShadow.BlurRadius = Math.Max(0f, settings.ShadowBlurRadius * 2.0f);

            _dropShadow.Offset = new Vector3(
                settings.ShadowDepth * 2.2f,
                settings.ShadowDepth * 2.5f,
                0f);
        }

        private void InitializeFixedWorkspaceSize()
        {
            double scale = GetDpiForWindow(_hwnd) / 96.0;
            int workspaceWidth = (int)Math.Round((MaxCanvasWidth + PaddingLeft + PaddingRight) * scale);
            int workspaceHeight = (int)Math.Round((MaxCanvasHeight + PaddingTop + PaddingBottom) * scale);

            AppWindow.Resize(new Windows.Graphics.SizeInt32(workspaceWidth, workspaceHeight));
        }

        public void UpdateVisualSettings(LensVisualSettings settings)
        {
            CurrentSettings = settings;
            UpdateCompositionShadow(settings);
            _backgroundCapture?.UpdateVisualSettings(settings);
        }

        public void BeginDrag()
        {
            GetCursorPos(out POINT pt);
            GetWindowRect(_hwnd, out RECT rect);

            _dragStartCursorX = pt.X;
            _dragStartCursorY = pt.Y;
            _dragStartWinX = rect.Left;
            _dragStartWinY = rect.Top;
            _isDragging = true;
            _backgroundCapture.IsDragging = true;
        }

        public void EndDrag()
        {
            _isDragging = false;
            _backgroundCapture.IsDragging = false;
        }

        private bool SyncDragWithFrame()
        {
            if (!_isDragging) return false;

            if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0)
            {
                EndDrag();
                return false;
            }

            GetCursorPos(out POINT currentPt);
            int newX = _dragStartWinX + (currentPt.X - _dragStartCursorX);
            int newY = _dragStartWinY + (currentPt.Y - _dragStartCursorY);

            _backgroundCapture.RenderForWindowPosition(newX, newY);
            SetWindowPos(_hwnd, IntPtr.Zero, newX, newY, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            return true;
        }

        public void UpdatePanelBounds(double newWidth, double newHeight)
        {
            _targetWidth = newWidth;
            _targetHeight = newHeight;
            _backgroundCapture?.UpdatePanelMetrics();
        }

        private void HitTestTimer_Tick(object? sender, object e)
        {
            if (_isDragging) return;

            bool isLeftButtonPressed = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
            if (isLeftButtonPressed) return;

            int currentExStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);

            if (IsMenuOpen)
            {
                if ((currentExStyle & WS_EX_TRANSPARENT) == WS_EX_TRANSPARENT)
                    SetWindowLong(_hwnd, GWL_EXSTYLE, currentExStyle & ~WS_EX_TRANSPARENT);
                return;
            }

            GetCursorPos(out POINT cursorPt);
            GetWindowRect(_hwnd, out RECT windowRect);
            double scale = GetDpiForWindow(_hwnd) / 96.0;

            int left = windowRect.Left + (int)(PaddingLeft * scale);
            int right = windowRect.Left + (int)((PaddingLeft + _targetWidth) * scale);
            int top = windowRect.Top + (int)(PaddingTop * scale);
            int bottom = windowRect.Top + (int)((PaddingTop + _targetHeight) * scale);

            bool isCursorInsidePanel = (cursorPt.X >= left && cursorPt.X <= right && cursorPt.Y >= top && cursorPt.Y <= bottom);

            if (isCursorInsidePanel)
            {
                if ((currentExStyle & WS_EX_TRANSPARENT) == WS_EX_TRANSPARENT)
                    SetWindowLong(_hwnd, GWL_EXSTYLE, currentExStyle & ~WS_EX_TRANSPARENT);
            }
            else
            {
                if ((currentExStyle & WS_EX_TRANSPARENT) != WS_EX_TRANSPARENT)
                    SetWindowLong(_hwnd, GWL_EXSTYLE, currentExStyle | WS_EX_TRANSPARENT);
            }
        }

        private void OnWindowClosed(object sender, WindowEventArgs args) => Dispose();

        public void Dispose()
        {
            _window.Closed -= OnWindowClosed;
            _hitTestTimer.Stop();

            _shadowVisual?.Dispose();
            _dropShadow?.Dispose();
            _shadowMaskBrush?.Dispose();
            _shadowVisualSurface?.Dispose();
            _shadowShapeVisual?.Dispose();
            _shadowShape?.Dispose();
            _shadowGeometry?.Dispose();

            _backgroundCapture?.Dispose();
        }
    }
}