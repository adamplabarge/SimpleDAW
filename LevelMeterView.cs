using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SimpleDAW;

/// <summary>
/// Self-drawn level meter used for hardware input channels and track levels.
/// Unlike a retemplated <see cref="ProgressBar"/> (whose built-in indicator
/// sizing logic does not reliably follow a custom vertical template), this
/// control draws its own fill directly, so the dimension perpendicular to the
/// fill (width when vertical, height when horizontal) never changes - only the
/// fill extent does.
///
/// The fill is drawn in three fixed colour zones (green/yellow/red) so the bar
/// visibly turns yellow then red as the level approaches full scale, rather
/// than just growing.
///
/// The displayed level is smoothed with VU-style ballistics (fast attack, slow
/// release) driven by the render loop, so motion stays smooth regardless of
/// how often <see cref="Level"/> is actually updated by its source.
/// </summary>
public sealed class LevelMeterView : FrameworkElement
{
    private const double AttackPerSecond = 25.0;  // how fast the bar can rise
    private const double ReleasePerSecond = 3.5;  // how fast the bar falls back
    private const double GreenEnd = 0.65;
    private const double YellowEnd = 0.82;

    private static readonly Brush BackgroundBrush = CreateFrozen("#1A1A1A");
    private static readonly Brush GreenBrush = CreateFrozen("#4EC94E");
    private static readonly Brush YellowBrush = CreateFrozen("#E6D24A");
    private static readonly Brush RedBrush = CreateFrozen("#E23B3B");

    private readonly Stopwatch _clock = new();
    private double _display;

    public LevelMeterView()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(LevelMeterView),
        new FrameworkPropertyMetadata(0.0));

    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(LevelMeterView),
        new FrameworkPropertyMetadata(Orientation.Vertical));

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _clock.Restart();
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        CompositionTarget.Rendering -= OnRendering;

    private void OnRendering(object? sender, EventArgs e)
    {
        double dt = Math.Clamp(_clock.Elapsed.TotalSeconds, 0.0, 0.1);
        _clock.Restart();

        double target = Math.Clamp(Level, 0.0, 1.0);
        double rate = target >= _display ? AttackPerSecond : ReleasePerSecond;
        double maxStep = rate * dt;

        _display = Math.Abs(target - _display) <= maxStep
            ? target
            : _display + (Math.Sign(target - _display) * maxStep);

        InvalidateVisual();
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

        double level = Math.Clamp(_display, 0.0, 1.0);
        if (level <= 0.0)
        {
            return;
        }

        bool vertical = Orientation == Orientation.Vertical;
        double extent = vertical ? height : width;

        void DrawSegment(double fromFrac, double toFrac, Brush brush)
        {
            if (toFrac <= fromFrac)
            {
                return;
            }

            Rect rect = vertical
                ? new Rect(0, extent - (toFrac * extent), width, (toFrac - fromFrac) * extent)
                : new Rect(fromFrac * extent, 0, (toFrac - fromFrac) * extent, height);

            dc.DrawRectangle(brush, null, rect);
        }

        DrawSegment(0.0, Math.Min(level, GreenEnd), GreenBrush);
        DrawSegment(GreenEnd, Math.Min(level, YellowEnd), YellowBrush);
        DrawSegment(YellowEnd, level, RedBrush);
    }

    private static Brush CreateFrozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
