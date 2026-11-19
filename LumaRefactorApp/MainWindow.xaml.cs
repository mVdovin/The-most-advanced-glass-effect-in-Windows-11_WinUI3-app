using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Globalization;
using Windows.Foundation;
using Windows.UI;

namespace RefactorApp
{
    public sealed partial class MainWindow : Window
    {
        private readonly TransparentWindowController _windowController;
        private bool _isUiReady = false;
        private bool _isUpdatingControls = false;

        public MainWindow()
        {
            this.InitializeComponent();

            _windowController = new TransparentWindowController(
                window: this,
                targetPanel: TargetPanel,
                dragRegionGrid: DragRegionGrid,
                targetBorder: TargetBorder);

            _isUiReady = true;

            ApplyObjectGeometry();
            ApplyBorderAppearance();
            PushVisualSettingsToController();
        }

        private void Flyout_Opened(object sender, object e)
        {
            _windowController.IsMenuOpen = true;
        }

        private void Flyout_Closed(object sender, object e)
        {
            _windowController.IsMenuOpen = false;
        }

        private void TargetBorder_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var properties = e.GetCurrentPoint((UIElement)sender).Properties;
            if (properties.IsLeftButtonPressed)
            {
                TargetBorder.CapturePointer(e.Pointer);
                _windowController.BeginDrag();
            }
        }

        private void TargetBorder_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
        }

        private void TargetBorder_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            TargetBorder.ReleasePointerCapture(e.Pointer);
            _windowController.EndDrag();
        }

        private void ObjectSettings_Changed(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!_isUiReady || _isUpdatingControls) return;
            ApplyObjectGeometry(sender);
            PushVisualSettingsToController();
        }

        private void ShapeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (!_isUiReady || _isUpdatingControls) return;
            ApplyObjectGeometry(sender);
            PushVisualSettingsToController();
        }

        private void ApplyObjectGeometry(object? changedControl = null)
        {
            _isUpdatingControls = true;
            try
            {
                double width = WidthSlider.Value;
                double height = HeightSlider.Value;
                bool isCircle = CircleRadio.IsChecked == true;

                if (isCircle)
                {
                    // Общий безопасный диапазон диаметра, допустимый для обоих ползунков (60..800)
                    double minAllowedDiameter = Math.Max(WidthSlider.Minimum, HeightSlider.Minimum);
                    double maxAllowedDiameter = Math.Min(WidthSlider.Maximum, HeightSlider.Maximum);

                    double diameter;
                    if (changedControl == WidthSlider)
                    {
                        diameter = width;
                    }
                    else if (changedControl == HeightSlider)
                    {
                        diameter = height;
                    }
                    else
                    {
                        // При переключении с прямоугольника на круг выравниваем стороны по меньшей стороне
                        diameter = Math.Min(width, height);
                    }

                    diameter = Math.Clamp(diameter, minAllowedDiameter, maxAllowedDiameter);
                    width = diameter;
                    height = diameter;

                    WidthSlider.Value = diameter;
                    HeightSlider.Value = diameter;
                }

                double maxRadius = Math.Min(width, height) / 2.0;
                RadiusSlider.Maximum = maxRadius;

                double radius;
                if (isCircle)
                {
                    radius = maxRadius;
                    RadiusSlider.Value = radius;
                    RadiusSlider.IsEnabled = false;
                }
                else
                {
                    RadiusSlider.IsEnabled = true;
                    if (RadiusSlider.Value > RadiusSlider.Maximum)
                        RadiusSlider.Value = RadiusSlider.Maximum;
                    radius = RadiusSlider.Value;
                }

                WidthValueText.Text = width.ToString("0", CultureInfo.InvariantCulture);
                HeightValueText.Text = height.ToString("0", CultureInfo.InvariantCulture);
                RadiusValueText.Text = radius.ToString("0", CultureInfo.InvariantCulture);

                TargetBorder.Width = width;
                TargetBorder.Height = height;
                TargetBorder.CornerRadius = new CornerRadius(radius);

                _windowController.UpdatePanelBounds(width, height);
            }
            finally
            {
                _isUpdatingControls = false;
            }
        }

        private void ShaderSettings_Changed(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!_isUiReady || _isUpdatingControls) return;

            EdgeWidthValueText.Text = EdgeWidthSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
            DistortionValueText.Text = DistortionSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
            LensCurveValueText.Text = LensCurveSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
            RgbShiftValueText.Text = RgbShiftSlider.Value.ToString("0.000", CultureInfo.InvariantCulture);

            BlurIntensityValueText.Text = BlurIntensitySlider.Value.ToString("0.#", CultureInfo.InvariantCulture);
            ShadowBlurValueText.Text = ShadowBlurSlider.Value.ToString("0.#", CultureInfo.InvariantCulture);
            ShadowDepthValueText.Text = ShadowDepthSlider.Value.ToString("0.0", CultureInfo.InvariantCulture);
            ShadowOpacityValueText.Text = ShadowOpacitySlider.Value.ToString("0.00", CultureInfo.InvariantCulture);

            BorderThicknessValueText.Text = BorderThicknessSlider.Value.ToString("0.0", CultureInfo.InvariantCulture);
            BorderGlowValueText.Text = BorderGlowSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
            BorderGlowColorValueText.Text = BorderGlowColorSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);

            GradientAngleValueText.Text = GradientAngleSlider.Value.ToString("0", CultureInfo.InvariantCulture);
            GradientIntensityValueText.Text = GradientIntensitySlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
            GradientColorValueText.Text = GradientColorSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);

            TextureDensityValueText.Text = TextureDensitySlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
            TextureBrightnessValueText.Text = TextureBrightnessSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);

            ApplyBorderAppearance();
            PushVisualSettingsToController();
        }

        private void ApplyBorderAppearance()
        {
            double thickness = BorderThicknessSlider.Value;
            double glowIntensity = Math.Clamp(BorderGlowSlider.Value / 2.0, 0.0, 1.0);
            double colorFactor = Math.Clamp(BorderGlowColorSlider.Value, 0.0, 1.0);

            TargetBorder.BorderThickness = new Thickness(thickness);

            if (thickness <= 0.01)
            {
                TargetBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0, 255, 255, 255));
                return;
            }

            byte baseTone = (byte)Math.Round(255.0 * (1.0 - colorFactor));
            byte midTone = (byte)Math.Round((210.0 * (1.0 - colorFactor)) + (35.0 * colorFactor));

            byte alphaEdge = (byte)Math.Round(110 + glowIntensity * 145);
            byte alphaMid = (byte)Math.Round(55 + glowIntensity * 155);

            var gradient = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(alphaEdge, baseTone, baseTone, baseTone), Offset = 0.0 });
            gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(alphaMid, midTone, midTone, midTone), Offset = 0.5 });
            gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(alphaEdge, baseTone, baseTone, baseTone), Offset = 1.0 });

            TargetBorder.BorderBrush = gradient;
        }

        private void PushVisualSettingsToController()
        {
            var settings = new LensVisualSettings
            {
                Width = (float)WidthSlider.Value,
                Height = (float)HeightSlider.Value,
                CornerRadius = (float)RadiusSlider.Value,
                IsCircle = CircleRadio.IsChecked == true,
                EdgeWidth = (float)EdgeWidthSlider.Value,
                Distortion = (float)DistortionSlider.Value,
                LensCurve = (float)LensCurveSlider.Value,
                RgbShift = (float)RgbShiftSlider.Value,
                BlurIntensity = (float)BlurIntensitySlider.Value,
                ShadowBlurRadius = (float)ShadowBlurSlider.Value,
                ShadowDepth = (float)ShadowDepthSlider.Value,
                ShadowOpacity = (float)ShadowOpacitySlider.Value,
                BorderThickness = (float)BorderThicknessSlider.Value,
                BorderGlow = (float)BorderGlowSlider.Value,
                BorderGlowColor = (float)BorderGlowColorSlider.Value,
                OverlayGradientAngle = (float)GradientAngleSlider.Value,
                OverlayGradientIntensity = (float)GradientIntensitySlider.Value,
                OverlayGradientColor = (float)GradientColorSlider.Value,
                TextureDensity = (float)TextureDensitySlider.Value,
                TextureBrightness = (float)TextureBrightnessSlider.Value
            };

            _windowController.UpdateVisualSettings(settings);
        }

        private void CloseAppButton_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Exit();
        }
    }
}