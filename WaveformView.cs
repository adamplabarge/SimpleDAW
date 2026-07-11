using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SimpleDAW;

/// <summary>
/// Lightweight waveform display. Draws the min/max envelope stored in a
/// <see cref="WaveformBuffer"/> as a filled shape around a centre line, and
/// repaints itself as new audio arrives while recording.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    private static readonly Brush BackgroundBrush = CreateFrozen("#1A1A1A");
    private static readonly Brush CentreBrush = CreateFrozen("#3A3A3A");
    private static readonly Brush WaveBrush = CreateFrozen("#4EC94E");
    private static readonly Brush PlayheadBrush = CreateFrozen("#FF5050");
    private static readonly Brush GridBrush = CreateFrozen("#2E2E2E");
    private static readonly Brush LabelBrush = CreateFrozen("#8A8A8A");
    private static readonly Typeface LabelTypeface = new("Segoe UI");

    private readonly Pen _centrePen;
    private readonly Pen _playheadPen;
    private readonly Pen _gridPen;
    private int _lastVersion = -1;

    public WaveformView()
    {
        _centrePen = new Pen(CentreBrush, 1);
        _centrePen.Freeze();
        _playheadPen = new Pen(PlayheadBrush, 1.5);
        _playheadPen.Freeze();
        _gridPen = new Pen(GridBrush, 1);
        _gridPen.Freeze();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public static readonly DependencyProperty BufferProperty = DependencyProperty.Register(
        nameof(Buffer), typeof(WaveformBuffer), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnBufferChanged));

    public static readonly DependencyProperty PixelsPerSecondProperty = DependencyProperty.Register(
        nameof(PixelsPerSecond), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(80.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlayheadSecondsProperty = DependencyProperty.Register(
        nameof(PlayheadSeconds), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ManualOffsetSecondsProperty = DependencyProperty.Register(
        nameof(ManualOffsetSeconds), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsFollowingProperty = DependencyProperty.Register(
        nameof(IsFollowing), typeof(bool), typeof(WaveformView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public WaveformBuffer? Buffer
    {
        get => (WaveformBuffer?)GetValue(BufferProperty);
        set => SetValue(BufferProperty, value);
    }

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

    private static void OnBufferChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (WaveformView)d;
        view._lastVersion = -1;
        view.InvalidateVisual();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) =>
        CompositionTarget.Rendering += OnRendering;

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        CompositionTarget.Rendering -= OnRendering;

    private void OnRendering(object? sender, EventArgs e)
    {
        var buffer = Buffer;
        if (buffer != null && buffer.Version != _lastVersion)
        {
            _lastVersion = buffer.Version;
            InvalidateVisual();
        }
    }

    private double GetOffsetSeconds(double width)
    {
        double pps = PixelsPerSecond > 0 ? PixelsPerSecond : 1.0;
        if (IsFollowing)
        {
            double viewportSeconds = width / pps;
            return Math.Max(0.0, PlayheadSeconds - (viewportSeconds * 0.5));
        }

        return Math.Max(0.0, ManualOffsetSeconds);
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

        double centre = height / 2.0;
        dc.DrawLine(_centrePen, new Point(0, centre), new Point(width, centre));

        double pps = PixelsPerSecond > 0 ? PixelsPerSecond : 1.0;
        double offset = GetOffsetSeconds(width);

        DrawTimeRuler(dc, width, height, pps, offset);

        var buffer = Buffer;
        if (buffer != null && buffer.Count > 0)
        {
            int pixels = Math.Max(1, (int)width);
            var envelope = buffer.GetEnvelopePixels(offset, pps, pixels);
            double halfHeight = centre;

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(0, centre - (envelope[0].Max * halfHeight)), true, true);
                for (int x = 1; x < envelope.Length; x++)
                {
                    ctx.LineTo(new Point(x, centre - (envelope[x].Max * halfHeight)), true, false);
                }

                for (int x = envelope.Length - 1; x >= 0; x--)
                {
                    ctx.LineTo(new Point(x, centre - (envelope[x].Min * halfHeight)), true, false);
                }
            }

            geometry.Freeze();
            dc.DrawGeometry(WaveBrush, null, geometry);
        }

        // Playhead.
        double playX = (PlayheadSeconds - offset) * pps;
        if (playX >= 0 && playX <= width)
        {
            dc.DrawLine(_playheadPen, new Point(playX, 0), new Point(playX, height));
        }
    }

    private static Brush CreateFrozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private void DrawTimeRuler(DrawingContext dc, double width, double height, double pps, double offset)
    {
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
}
