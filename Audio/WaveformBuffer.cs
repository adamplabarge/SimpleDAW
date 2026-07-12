namespace SimpleDAW;

/// <summary>
/// Thread-safe, growable store of waveform envelope data (min/max sample pairs
/// per time slice). The audio engine appends slices while recording and the UI
/// reads a down-sampled envelope for drawing. A monotonically increasing
/// <see cref="Version"/> lets the view redraw only when new data has arrived.
/// </summary>
public sealed class WaveformBuffer
{
    /// <summary>Number of source samples represented by each stored envelope slice.</summary>
    public const int SamplesPerSlice = 128;

    private readonly object _sync = new();
    private float[] _min = new float[4096];
    private float[] _max = new float[4096];
    private int _count;

    /// <summary>Sample rate of the source audio, needed to map slices to seconds.</summary>
    public int SampleRate { get; set; } = 48000;

    /// <summary>Incremented on every change so views can detect new data.</summary>
    public int Version { get; private set; }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    /// <summary>Total length of the stored waveform in seconds.</summary>
    public double DurationSeconds
    {
        get
        {
            lock (_sync)
            {
                return SampleRate > 0 ? (double)_count * SamplesPerSlice / SampleRate : 0.0;
            }
        }
    }

    /// <summary>Appends one envelope slice (minimum and maximum sample values).</summary>
    public void Add(float min, float max)
    {
        lock (_sync)
        {
            if (_count >= _min.Length)
            {
                Array.Resize(ref _min, _min.Length * 2);
                Array.Resize(ref _max, _max.Length * 2);
            }

            _min[_count] = min;
            _max[_count] = max;
            _count++;
            Version++;
        }
    }

    /// <summary>Clears all stored data (used when a new take starts).</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _count = 0;
            Version++;
        }
    }

    /// <summary>
    /// Fills a min/max envelope for a horizontal window into caller-supplied
    /// buffers: one pair per output pixel starting at
    /// <paramref name="offsetSeconds"/> at the given zoom. Callers own and
    /// reuse <paramref name="destMin"/>/<paramref name="destMax"/> (each must
    /// be at least <paramref name="pixelCount"/> long) so repeated redraws
    /// (e.g. every frame while following the playhead) don't allocate.
    /// </summary>
    public void FillEnvelopePixels(double offsetSeconds, double pixelsPerSecond, float[] destMin, float[] destMax, int pixelCount)
    {
        if (pixelCount < 1)
        {
            pixelCount = 1;
        }

        lock (_sync)
        {
            if (_count == 0 || SampleRate <= 0 || pixelsPerSecond <= 0)
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
                for (int i = s0; i < s1 && i < _count; i++)
                {
                    if (_min[i] < mn)
                    {
                        mn = _min[i];
                    }

                    if (_max[i] > mx)
                    {
                        mx = _max[i];
                    }
                }

                destMin[x] = mn;
                destMax[x] = mx;
            }
        }
    }
}
