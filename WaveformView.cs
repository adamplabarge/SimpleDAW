using System.Windows;
using System.Windows.Media;

namespace SimpleDAW;

/// <summary>
/// Lightweight waveform display. Draws the min/max envelope stored in a
/// <see cref="WaveformBuffer"/> as a filled shape around a centre line, and
/// repaints itself as new audio arrives while recording. The time ruler is
/// drawn once by the shared <see cref="TimelineRulerView"/> instead of being
/// duplicated in every track's view.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    private static readonly Brush BackgroundBrush = CreateFrozen("#1A1A1A");
    private static readonly Brush CentreBrush = CreateFrozen("#3A3A3A");
    private static readonly Brush WaveBrush = CreateFrozen("#4EC94E");
    private static readonly Brush PlayheadBrush = CreateFrozen("#FF5050");

    private readonly Pen _centrePen;
    private readonly Pen _playheadPen;
    private int _lastVersion = -1;

    // Reused across frames so redrawing while following the playhead (which
    // happens every render frame) doesn't allocate a new envelope buffer
    // each time. Grown on demand, never shrunk.
    private float[] _envMin = Array.Empty<float>();
    private float[] _envMax = Array.Empty<float>();

    public WaveformView()
    {
        _centrePen = new Pen(CentreBrush, 1);
        _centrePen.Freeze();
        _playheadPen = new Pen(PlayheadBrush, 1.5);
        _playheadPen.Freeze();
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

    private double GetOffsetSeconds(double width) =>
        TimelineMath.GetOffsetSeconds(width, PixelsPerSecond, IsFollowing, PlayheadSeconds, ManualOffsetSeconds);

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

        var buffer = Buffer;
        if (buffer != null && buffer.Count > 0)
        {
            int pixels = Math.Max(1, (int)width);
            if (_envMin.Length < pixels)
            {
                _envMin = new float[pixels];
                _envMax = new float[pixels];
            }

            buffer.FillEnvelopePixels(offset, pps, _envMin, _envMax, pixels);
            double halfHeight = centre;

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(0, centre - (_envMax[0] * halfHeight)), true, true);
                for (int x = 1; x < pixels; x++)
                {
                    ctx.LineTo(new Point(x, centre - (_envMax[x] * halfHeight)), true, false);
                }

                for (int x = pixels - 1; x >= 0; x--)
                {
                    ctx.LineTo(new Point(x, centre - (_envMin[x] * halfHeight)), true, false);
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
}
