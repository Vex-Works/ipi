using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Ipi.Desktop;

/// <summary>
/// Small animated line icons for ipi.
///
/// The public control name stays LucideIcon to avoid noisy XAML churn, but rendering is now a
/// single lightweight Phosphor-style vector subset instead of mixed icon fonts. This keeps icon
/// weight, caps, joins, and proportions consistent across light/dark and language modes.
/// </summary>
public sealed class LucideIcon : FrameworkElement
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(string), typeof(LucideIcon), new FrameworkPropertyMetadata("message-square", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(LucideIcon), new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(LucideIcon), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(LucideIcon), new FrameworkPropertyMetadata(1.55, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _translate = new(0, 0);

    public LucideIcon()
    {
        SnapsToDevicePixels = true;
        IsHitTestVisible = true;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = new TransformGroup { Children = { _scale, _translate } };
        MouseEnter += (_, _) => Animate(1.06, -0.75, 110);
        MouseLeave += (_, _) => Animate(1.0, 0.0, 130);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);
    protected override Size ArrangeOverride(Size finalSize) => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var geometry = Geometry.Parse(GetPath(Icon));
        geometry.Freeze();

        var scale = Math.Min(ActualWidth, ActualHeight) / 24.0;
        var offsetX = (ActualWidth - 24 * scale) / 2.0;
        var offsetY = (ActualHeight - 24 * scale) / 2.0;

        dc.PushTransform(new TranslateTransform(offsetX, offsetY));
        dc.PushTransform(new ScaleTransform(scale, scale));
        if (IsFilledIcon(Icon))
        {
            dc.DrawGeometry(Stroke, null, geometry);
        }
        else
        {
            var pen = new Pen(Stroke, StrokeThickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            dc.DrawGeometry(null, pen, geometry);
        }
        dc.Pop();
        dc.Pop();
    }

    private static bool IsFilledIcon(string icon)
        => (icon ?? "").Trim().Equals("stop", StringComparison.OrdinalIgnoreCase);

    private void Animate(double scale, double y, int ms)
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(ms);
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, duration) { EasingFunction = easing });
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, duration) { EasingFunction = easing });
        _translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, duration) { EasingFunction = easing });
    }

    private static string GetPath(string icon) => (icon ?? "").Trim().ToLowerInvariant() switch
    {
        "plus" => "M12 5v14 M5 12h14",
        "panel-left" or "sidebar" => "M5.5 4.5h13a2 2 0 0 1 2 2v11a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2v-11a2 2 0 0 1 2-2z M9 4.5v15",
        "panel-right" => "M5.5 4.5h13a2 2 0 0 1 2 2v11a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2v-11a2 2 0 0 1 2-2z M15 4.5v15",
        "arrow-left" => "M19.5 12H5 M11 6l-6 6 6 6",
        "arrow-right" => "M4.5 12H19 M13 6l6 6-6 6",
        "arrow-up" => "M12 19.5V5 M6 11l6-6 6 6",
        "arrow-down" => "M12 4.5V19 M6 13l6 6 6-6",
        "chevron-down" => "M6.5 9.5 12 15l5.5-5.5",
        "chevron-right" => "M9.5 6.5 15 12l-5.5 5.5",
        "download" => "M12 4v11 M7.5 10.5 12 15l4.5-4.5 M5 15.5v3a1.5 1.5 0 0 0 1.5 1.5h11a1.5 1.5 0 0 0 1.5-1.5v-3",
        "external-link" => "M14 4.5h5.5V10 M19.5 4.5 11 13 M10 5.5H6.5a2 2 0 0 0-2 2v10a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V14",
        "git-branch" => "M7 5.5a2.5 2.5 0 1 0 0 5 2.5 2.5 0 0 0 0-5z M17 13.5a2.5 2.5 0 1 0 0 5 2.5 2.5 0 0 0 0-5z M7 10.5v3a4 4 0 0 0 4 4h3.5 M7 5.5V3.5",
        "terminal-square" => "M5.5 4.5h13a2 2 0 0 1 2 2v11a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2v-11a2 2 0 0 1 2-2z M7.5 10l3 2-3 2 M12.5 15h4",
        "refresh-cw" => "M19.5 7.5V3.5h-4 M19.2 4.2 16.4 7A7 7 0 0 0 5 10 M4.5 16.5v4h4 M4.8 19.8 7.6 17A7 7 0 0 0 19 14",
        "search" => "M10.8 18.1a7.3 7.3 0 1 0 0-14.6 7.3 7.3 0 0 0 0 14.6z M16 16l4.5 4.5",
        "folder" => "M3.5 7.5A2.5 2.5 0 0 1 6 5h4l2 2h6a2.5 2.5 0 0 1 2.5 2.5v7A2.5 2.5 0 0 1 18 19H6a2.5 2.5 0 0 1-2.5-2.5z",
        "more-horizontal" => "M7.5 12h.01 M12 12h.01 M16.5 12h.01",
        "edit-3" => "M12 20h8 M4.5 16.8 16.2 5.1a2.1 2.1 0 0 1 3 3L7.5 19.8 4 20.5z",
        "pin" => "M12 15.5V21 M7.5 21h9 M8.5 4.5h7l.8 5 3.2 3v2h-15v-2l3.2-3z",
        "archive" => "M4.5 8.5h15v10a2 2 0 0 1-2 2h-11a2 2 0 0 1-2-2z M3.5 4.5h17v4h-17z M10 13h4",
        "mic" => "M12 3.5a3 3 0 0 0-3 3v5a3 3 0 0 0 6 0v-5a3 3 0 0 0-3-3z M18.5 10.5v1a6.5 6.5 0 0 1-13 0v-1 M12 18v3 M8.5 21h7",
        "mic-off" => "M4 4l16 16 M9 8.5v3a3 3 0 0 0 4.8 2.4 M15 11.4V6.5a3 3 0 0 0-5.2-2 M18.5 10.5v1a6.5 6.5 0 0 1-2.3 5 M5.5 10.5v1A6.5 6.5 0 0 0 12 18v3 M8.5 21h7",
        "palette" => "M12 20.5a8.5 8.5 0 1 1 8.5-8.5c0 1.5-1.2 2.7-2.7 2.7h-1.3a2 2 0 0 0-2 2c0 .4.1.8.3 1.1.5.8-.1 2.7-2.8 2.7z M7.8 11.2h.01 M9.8 7.8h.01 M14.2 7.8h.01 M16.5 11.2h.01",
        "monitor" => "M4 5h16v10.5H4z M8.5 20h7 M12 15.5V20",
        "message-square" => "M6 5h12a2.5 2.5 0 0 1 2.5 2.5v7A2.5 2.5 0 0 1 18 17h-5.5L7 20v-3H6a2.5 2.5 0 0 1-2.5-2.5v-7A2.5 2.5 0 0 1 6 5z",
        "zap" => "M13.5 3.5 5 13h7l-1.5 7.5L19 10h-7z",
        "shield-question" => "M12 21c-4.8-1.6-7.5-4.6-7.5-8.8V5.5L12 3l7.5 2.5v6.7c0 4.2-2.7 7.2-7.5 8.8z M9.8 9.4a2.4 2.4 0 0 1 4.6.9c0 1.7-2.4 1.9-2.4 3.7 M12 17h.01",
        "shield-check" => "M12 21c-4.8-1.6-7.5-4.6-7.5-8.8V5.5L12 3l7.5 2.5v6.7c0 4.2-2.7 7.2-7.5 8.8z M8.5 12.2l2.3 2.3 4.8-5",
        "lock" => "M7 10V8a5 5 0 0 1 10 0v2 M5.5 10h13v9.5h-13z",
        "globe" => "M12 20.5a8.5 8.5 0 1 0 0-17 8.5 8.5 0 0 0 0 17z M3.5 12h17 M12 3.5c2.2 2.2 3.2 5 3.2 8.5s-1 6.3-3.2 8.5 M12 3.5C9.8 5.7 8.8 8.5 8.8 12s1 6.3 3.2 8.5",
        "home" => "M4 11.5 12 5l8 6.5V20h-5v-5h-6v5H4z",
        "square" => "M6 6h12v12H6z",
        "circle" => "M12 20.5a8.5 8.5 0 1 0 0-17 8.5 8.5 0 0 0 0 17z",
        "file" => "M7 3.5h7l4 4V20.5H7z M14 3.5v4h4",
        "clock" => "M12 20.5a8.5 8.5 0 1 0 0-17 8.5 8.5 0 0 0 0 17z M12 8v4.5l3 1.8",
        "plug" => "M8.5 3.5v5 M15.5 3.5v5 M7 8.5h10v3.2a5 5 0 0 1-10 0z M12 16.7V21",
        "settings" => "M12 15.2a3.2 3.2 0 1 0 0-6.4 3.2 3.2 0 0 0 0 6.4z M19 13.5v-3l-2-.5a5.5 5.5 0 0 0-.7-1.7l1.1-1.8-2.1-2.1-1.8 1.1a5.5 5.5 0 0 0-1.7-.7L11.3 3h-3l-.5 2a5.5 5.5 0 0 0-1.7.7L4.3 4.6 2.2 6.7l1.1 1.8a5.5 5.5 0 0 0-.7 1.7l-2 .5v3l2 .5a5.5 5.5 0 0 0 .7 1.7l-1.1 1.8 2.1 2.1 1.8-1.1a5.5 5.5 0 0 0 1.7.7l.5 2h3l.5-2a5.5 5.5 0 0 0 1.7-.7l1.8 1.1 2.1-2.1-1.1-1.8a5.5 5.5 0 0 0 .7-1.7z",
        "sliders-horizontal" => "M4 6h8 M16 6h4 M12 6a2 2 0 1 0 4 0 2 2 0 0 0-4 0z M4 12h3 M11 12h9 M7 12a2 2 0 1 0 4 0 2 2 0 0 0-4 0z M4 18h10 M18 18h2 M14 18a2 2 0 1 0 4 0 2 2 0 0 0-4 0z",
        "sun" => "M12 15.8a3.8 3.8 0 1 0 0-7.6 3.8 3.8 0 0 0 0 7.6z M12 3v2 M12 19v2 M5.6 5.6 7 7 M17 17l1.4 1.4 M3 12h2 M19 12h2 M5.6 18.4 7 17 M17 7l1.4-1.4",
        "moon" => "M19.5 14.2A7.8 7.8 0 0 1 9.8 4.5a8.5 8.5 0 1 0 9.7 9.7z",
        "x" => "M6.5 6.5 17.5 17.5 M17.5 6.5 6.5 17.5",
        "minus" => "M5.5 12h13",
        "maximize" => "M5.5 5.5h13v13h-13z",
        "stop" => "M6.5 6.5h11v11h-11z",
        "layout-grid" => "M4.5 4.5h6v6h-6z M13.5 4.5h6v6h-6z M13.5 13.5h6v6h-6z M4.5 13.5h6v6h-6z",
        "sparkles" => "M12 3.5l1.7 5.1 5.1 1.7-5.1 1.7L12 17l-1.7-5-5.1-1.7 5.1-1.7z M5 4.5v3 M3.5 6h3 M19 16.5v3 M17.5 18h3",
        _ => "M6 5h12a2.5 2.5 0 0 1 2.5 2.5v7A2.5 2.5 0 0 1 18 17h-5.5L7 20v-3H6a2.5 2.5 0 0 1-2.5-2.5v-7A2.5 2.5 0 0 1 6 5z",
    };
}
