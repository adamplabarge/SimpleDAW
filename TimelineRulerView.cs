using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SimpleDAW;

/// <summary>
/// Single shared time ruler drawn once above the track list. Previously this
/// grid/label drawing was duplicated inside every track's <see cref="WaveformView"/>,
/// so it was recomputed once per track per frame; hoisting it here means it is
/// computed once per frame regardless of track count.
/// </summary>
public sealed class TimelineRulerView : FrameworkElement
{
    private static readonly Brush BackgroundBrush = CreateFrozen("#202020");
    private static readonly Brush GridBrush = CreateFrozen("#2E2E2E");
    private static readonly Brush LabelBrush = CreateFrozen("#8A8A8A");
    private static readonly Typeface LabelTypeface = new("Segoe UI");

    private readonly Pen _gridPen;

    public TimelineRulerView()
    {
        _gridPen = new Pen(GridBrush, 1);
        _gridPen.Freeze();
    }

    public static readonly DependencyProperty PixelsPerSecondProperty = DependencyProperty.Register(
        nameof(PixelsPerSecond), typeof(double), typeof(TimelineRulerView),
        new FrameworkPropertyMetadata(80.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlayheadSecondsProperty = DependencyProperty.Register(
        nameof(PlayheadSeconds), typeof(double), typeof(TimelineRulerView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ManualOffsetSecondsProperty = DependencyProperty.Register(
        nameof(ManualOffsetSeconds), typeof(double), typeof(TimelineRulerView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsFollowingProperty = DependencyProperty.Register(
        nameof(IsFollowing), typeof(bool), typeof(TimelineRulerView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public double PixelsPerSecond
    {
        get => (double)GetValue(PixelsPerSecondProperty);
        set => SetValue(PixelsPerSecondProperty, value);
    }

    public double PlayheadSeconds
    {
        get => (double)GetValue(PlayheadSecondsProperty);
        set => SetValue(PlayheadSecondsProperty, value);
    }

    public double ManualOffsetSeconds
    {
        get => (double)GetValue(ManualOffsetSecondsProperty);
        set => SetValue(ManualOffsetSecondsProperty, value);
    }

    public bool IsFollowing
    {
        get => (bool)GetValue(IsFollowingProperty);
        set => SetValue(IsFollowingProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, width, height));

        double pps = PixelsPerSecond > 0 ? PixelsPerSecond : 1.0;
        double offset = TimelineMath.GetOffsetSeconds(width, pps, IsFollowing, PlayheadSeconds, ManualOffsetSeconds);

        const double minLabelSpacingPx = 64.0;
        double interval = NiceInterval(minLabelSpacingPx / pps);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double firstTick = Math.Ceiling(offset / interval) * interval;

        for (double t = firstTick; ; t += interval)
        {
            double x = (t - offset) * pps;
            if (x > width)
            {
                break;
            }

            dc.DrawLine(_gridPen, new Point(x, 0), new Point(x, height));

            var label = new FormattedText(
                FormatTick(t, interval),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                10.0,
                LabelBrush,
                pixelsPerDip);

            dc.DrawText(label, new Point(x + 3, 1));
        }
    }

    private static double NiceInterval(double seconds)
    {
        double[] steps = { 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300 };
        foreach (double step in steps)
        {
            if (step >= seconds)
            {
                return step;
            }
        }

        return 600;
    }

    private static string FormatTick(double seconds, double interval)
    {
        int minutes = (int)(seconds / 60);
        double secs = seconds - (minutes * 60);
        return interval < 1.0 ? $"{minutes}:{secs:00.0}" : $"{minutes}:{secs:00}";
    }

    private static Brush CreateFrozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
