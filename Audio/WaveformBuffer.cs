namespace SimpleDAW;

/// <summary>
/// Growable store of waveform envelope data (min/max sample pairs per time
/// slice). The audio engine appends slices while recording (from the
/// real-time audio callback thread) and the UI reads a down-sampled envelope
/// for drawing (from the UI thread), potentially many times per second on
/// every track. Reads are lock-free so the UI thread never blocks the
/// real-time audio thread (or vice versa): the array references and count are
/// published via <c>volatile</c> with the append-then-publish-count ordering
/// that makes a torn read impossible (a reader that observes a given count
/// always observes fully-written data up to that count). A monotonically
/// increasing <see cref="Version"/> lets views redraw only when new data has
/// arrived.
/// </summary>
public sealed class WaveformBuffer
{
    /// <summary>Number of source samples represented by each stored envelope slice.</summary>
    public const int SamplesPerSlice = 128;

    // Only Add/Clear take this lock, and in practice there is only ever one
    // writer thread active at a time (the audio callback while recording, or
    // the UI thread while loading a project) - so it is never contended. It
    // exists purely to serialise the rare Clear-then-Add sequence, not to
    // protect readers, which never take a lock at all (see below).
    private readonly object _writeLock = new();

    private volatile float[] _min = new float[4096];
    private volatile float[] _max = new float[4096];
    private volatile int _count;
    private volatile int _version;

    /// <summary>Sample rate of the source audio, needed to map slices to seconds.</summary>
    public int SampleRate { get; set; } = 48000;

    /// <summary>Incremented on every change so views can detect new data.</summary>
    public int Version => _version;

    public int Count => _count;

    /// <summary>Total length of the stored waveform in seconds.</summary>
    public double DurationSeconds
    {
        get
        {
            int sampleRate = SampleRate;
            return sampleRate > 0 ? (double)_count * SamplesPerSlice / sampleRate : 0.0;
        }
    }

    /// <summary>Appends one envelope slice (minimum and maximum sample values).</summary>
    public void Add(float min, float max)
    {
        lock (_writeLock)
        {
            var minArr = _min;
            var maxArr = _max;
            int count = _count;

            if (count >= minArr.Length)
            {
                var newMin = new float[minArr.Length * 2];
                var newMax = new float[maxArr.Length * 2];
                Array.Copy(minArr, newMin, count);
                Array.Copy(maxArr, newMax, count);

                // Publish the bigger arrays before any reader could observe
                // the count that requires them.
                _min = newMin;
                _max = newMax;
                minArr = newMin;
                maxArr = newMax;
            }

            minArr[count] = min;
            maxArr[count] = max;

            // Publish the new element (count) only after it has been fully
            // written above, so a lock-free reader that sees the new count
            // always sees fully-written data.
            _count = count + 1;
            _version++;
        }
    }

    /// <summary>Clears all stored data (used when a new take starts).</summary>
    public void Clear()
    {
        lock (_writeLock)
        {
            _count = 0;
            _version++;
        }
    }

    /// <summary>
    /// Fills a min/max envelope for a horizontal window into caller-supplied
    /// buffers: one pair per output pixel starting at
    /// <paramref name="offsetSeconds"/> at the given zoom. Callers own and
    /// reuse <paramref name="destMin"/>/<paramref name="destMax"/> (each must
    /// be at least <paramref name="pixelCount"/> long) so repeated redraws
    /// (e.g. every frame while following the playhead) don't allocate. This
    /// is a lock-free read and never blocks the audio thread's <see cref="Add"/>.
    /// </summary>
    public void FillEnvelopePixels(double offsetSeconds, double pixelsPerSecond, float[] destMin, float[] destMax, int pixelCount)
    {
        if (pixelCount < 1)
        {
            pixelCount = 1;
        }

        // Snapshot the arrays and count together, then clamp to the arrays'
        // actual length. Even if a resize races with this read, the clamp
        // means we can never index past what's actually allocated.
        var minArr = _min;
        var maxArr = _max;
        int count = Math.Min(_count, Math.Min(minArr.Length, maxArr.Length));

        if (count == 0 || SampleRate <= 0 || pixelsPerSecond <= 0)
        {
            Array.Clear(destMin, 0, pixelCount);
            Array.Clear(destMax, 0, pixelCount);
            return;
        }

        double slicesPerSecond = (double)SampleRate / SamplesPerSlice;

        for (int x = 0; x < pixelCount; x++)
        {
            double t0 = offsetSeconds + (x / pixelsPerSecond);
            double t1 = offsetSeconds + ((x + 1) / pixelsPerSecond);
            int s0 = (int)(t0 * slicesPerSecond);
            int s1 = (int)(t1 * slicesPerSecond);
            if (s1 <= s0)
            {
                s1 = s0 + 1;
            }

            if (s0 < 0)
            {
                s0 = 0;
            }

            float mn = 0f;
            float mx = 0f;
            for (int i = s0; i < s1 && i < count; i++)
            {
                if (minArr[i] < mn)
                {
                    mn = minArr[i];
                }

                if (maxArr[i] > mx)
                {
                    mx = maxArr[i];
                }
            }

            destMin[x] = mn;
            destMax[x] = mx;
        }
    }
}
