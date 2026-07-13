using System.IO;
using NAudio.Wave;

namespace SimpleDAW;

/// <summary>
/// Fills a <see cref="WaveformBuffer"/> from an existing audio file so a loaded
/// project's tracks show their recorded waveform without re-recording.
/// </summary>
public static class WaveformLoader
{
    public static void LoadInto(WaveformBuffer buffer, string path)
    {
        buffer.Clear();

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            using var reader = new AudioFileReader(path);
            buffer.SampleRate = reader.WaveFormat.SampleRate;
            int channels = Math.Max(1, reader.WaveFormat.Channels);
            var chunk = new float[4096 * channels];

            float min = 0f;
            float max = 0f;
            int count = 0;

            int read;
            while ((read = reader.Read(chunk, 0, chunk.Length)) > 0)
            {
                int frames = read / channels;
                for (int f = 0; f < frames; f++)
                {
                    float sample = 0f;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        sample += chunk[(f * channels) + ch];
                    }

                    sample /= channels;

                    if (sample < min)
                    {
                        min = sample;
                    }

                    if (sample > max)
                    {
                        max = sample;
                    }

                    if (++count >= WaveformBuffer.SamplesPerSlice)
                    {
                        buffer.Add(min, max);
                        min = 0f;
                        max = 0f;
                        count = 0;
                    }
                }
            }

            if (count > 0)
            {
                buffer.Add(min, max);
            }
        }
        catch (Exception ex)
        {
            // Leave the buffer empty if the file can't be read.
            AppLog.Warn($"WaveformLoader: failed to load waveform from '{path}'", ex);
        }
    }
}
