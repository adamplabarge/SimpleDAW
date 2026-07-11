using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SimpleDAW;

/// <summary>
/// Mixes the recorded audio of several tracks into a single stereo stream for
/// playback. Each source is read as mono, resampled to the engine sample rate
/// if necessary, scaled by the track volume, and summed. Supports rewinding to
/// the start and exposes per-track peak levels for metering.
/// </summary>
public sealed class MultiTrackPlayer : ISampleProvider, IDisposable
{
    private sealed class Source
    {
        public required TrackModel Track { get; init; }
        public required AudioFileReader Reader { get; init; }
        public required ISampleProvider Provider { get; init; }
        public bool Ended { get; set; }
        public float Peak { get; set; }
    }

    private readonly List<Source> _sources = new();
    private readonly float[] _mono;
    private readonly WaveFormat _waveFormat;

    public MultiTrackPlayer(int sampleRate, IEnumerable<TrackModel> tracks)
    {
        _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        _mono = new float[sampleRate]; // scratch buffer, comfortably large

        foreach (var track in tracks)
        {
            if (string.IsNullOrEmpty(track.RecordedFilePath) || !File.Exists(track.RecordedFilePath))
            {
                continue;
            }

            var reader = new AudioFileReader(track.RecordedFilePath);
            ISampleProvider provider = reader;

            if (reader.WaveFormat.Channels > 1)
            {
                provider = new StereoToMonoSampleProvider(provider) { LeftVolume = 0.5f, RightVolume = 0.5f };
            }

            if (provider.WaveFormat.SampleRate != sampleRate)
            {
                provider = new WdlResamplingSampleProvider(provider, sampleRate);
            }

            _sources.Add(new Source { Track = track, Reader = reader, Provider = provider });
        }
    }

    public WaveFormat WaveFormat => _waveFormat;

    /// <summary>True once every source has played to its end.</summary>
    public bool IsFinished => _sources.Count == 0 || _sources.All(s => s.Ended);

    /// <summary>Rewinds every source back to the beginning.</summary>
    public void Rewind()
    {
        foreach (var source in _sources)
        {
            source.Reader.Position = 0;
            source.Ended = false;
        }
    }

    /// <summary>Latest peak level (0..1) for a track, or 0 if it is not playing.</summary>
    public float GetPeak(TrackModel track)
    {
        foreach (var source in _sources)
        {
            if (source.Track == track)
            {
                return source.Peak;
            }
        }

        return 0f;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / 2;

        // Start from silence, then add each track.
        for (int i = 0; i < count; i++)
        {
            buffer[offset + i] = 0f;
        }

        foreach (var source in _sources)
        {
            float peak = 0f;

            if (source.Track.IsMuted || source.Ended)
            {
                source.Peak = 0f;
                continue;
            }

            int read = source.Provider.Read(_mono, 0, frames);
            if (read < frames)
            {
                source.Ended = true;
            }

            float volume = source.Track.Volume;

            // Constant-power pan: -1 = hard left, 0 = centre, +1 = hard right.
            float pan = source.Track.Pan;
            double angle = (pan + 1.0) * 0.25 * Math.PI; // 0..pi/2
            float leftGain = (float)Math.Cos(angle) * volume;
            float rightGain = (float)Math.Sin(angle) * volume;

            for (int f = 0; f < read; f++)
            {
                float mono = _mono[f];
                float abs = Math.Abs(mono * volume);
                if (abs > peak)
                {
                    peak = abs;
                }

                buffer[offset + (f * 2)] += mono * leftGain;       // Left
                buffer[offset + (f * 2) + 1] += mono * rightGain;  // Right
            }

            source.Peak = peak;
        }

        // Always return a full buffer so the device keeps running; the transport
        // decides when to stop based on IsFinished.
        return count;
    }

    public void Dispose()
    {
        foreach (var source in _sources)
        {
            source.Reader.Dispose();
        }

        _sources.Clear();
    }
}
