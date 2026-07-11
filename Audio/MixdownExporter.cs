using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SimpleDAW;

/// <summary>
/// Renders the recorded tracks offline into a single stereo 16-bit WAV file,
/// applying each track's volume, pan and mute exactly as during playback.
/// </summary>
public static class MixdownExporter
{
    /// <summary>
    /// Mixes every track that has audio down to <paramref name="path"/>.
    /// Returns false if there was nothing to render.
    /// </summary>
    public static bool Export(IReadOnlyList<TrackModel> tracks, int sampleRate, string path)
    {
        using var player = new MultiTrackPlayer(sampleRate, tracks);
        if (player.IsFinished)
        {
            return false; // no track had usable audio
        }

        var provider16 = new SampleToWaveProvider16(player);
        using var writer = new WaveFileWriter(path, provider16.WaveFormat);

        // ~85 ms of stereo 16-bit audio per block keeps any trailing silence small.
        byte[] buffer = new byte[4096 * 2 * 2];
        while (!player.IsFinished)
        {
            int bytes = provider16.Read(buffer, 0, buffer.Length);
            if (bytes <= 0)
            {
                break;
            }

            writer.Write(buffer, 0, bytes);
        }

        return true;
    }
}
