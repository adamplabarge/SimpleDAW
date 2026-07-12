namespace SimpleDAW;

/// <summary>
/// Shared time-axis math used by both the per-track <see cref="WaveformView"/>
/// instances and the single shared <see cref="TimelineRulerView"/>, so they
/// always agree on which time window is visible for a given width/zoom/follow
/// state.
/// </summary>
public static class TimelineMath
{
    /// <summary>
    /// Computes the time (in seconds) at the left edge of the visible window.
    /// </summary>
    public static double GetOffsetSeconds(
        double width, double pixelsPerSecond, bool isFollowing,
        double playheadSeconds, double manualOffsetSeconds)
    {
        double pps = pixelsPerSecond > 0 ? pixelsPerSecond : 1.0;
        if (isFollowing)
        {
            double viewportSeconds = width / pps;
            return Math.Max(0.0, playheadSeconds - (viewportSeconds * 0.5));
        }

        return Math.Max(0.0, manualOffsetSeconds);
    }
}
