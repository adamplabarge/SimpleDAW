using NAudio.Wave;

namespace SimpleDAW;

/// <summary>
/// A bounded ring buffer exposed as an <see cref="ISampleProvider"/>. The audio
/// callback writes captured input frames into it, and the output device reads
/// them back for live monitoring. If the reader falls behind, the oldest samples
/// are overwritten to keep latency bounded; if it gets ahead, silence is output.
/// </summary>
public sealed class LiveInputProvider : ISampleProvider
{
    private readonly WaveFormat _format;
    private readonly float[] _ring;
    private readonly object _sync = new();
    private int _writePos;
    private int _readPos;
    private int _available;

    public LiveInputProvider(int sampleRate, int channels, int capacityFrames)
    {
        _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        _ring = new float[capacityFrames * channels];
    }

    public WaveFormat WaveFormat => _format;

    /// <summary>Writes <paramref name="count"/> interleaved samples into the buffer.</summary>
    public void Write(float[] data, int count)
    {
        lock (_sync)
        {
            for (int i = 0; i < count; i++)
            {
                _ring[_writePos] = data[i];
                _writePos = (_writePos + 1) % _ring.Length;

                if (_available < _ring.Length)
                {
                    _available++;
                }
                else
                {
                    _readPos = (_readPos + 1) % _ring.Length;
                }
            }
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_sync)
        {
            for (int i = 0; i < count; i++)
            {
                if (_available > 0)
                {
                    buffer[offset + i] = _ring[_readPos];
                    _readPos = (_readPos + 1) % _ring.Length;
                    _available--;
                }
                else
                {
                    buffer[offset + i] = 0f;
                }
            }
        }

        return count;
    }
}
