using NAudio.Wave;

namespace SimpleDAW;

/// <summary>Which beats of a 4/4 bar are accented (played louder) by the click.</summary>
public enum ClickAccent
{
    None,
    Beat1,
    Beats1And3,
    Beats2And4,
}

/// <summary>
/// Generates a stereo metronome click on every beat at the configured tempo.
/// Selected beats can be accented (louder, higher pitched). An optional run of
/// quieter, unaccented "pre-roll" beats can lead the first bar so a count-in is
/// heard before the downbeat. The click is synthesised (a short decaying tone),
/// so no audio sample file is required.
/// </summary>
public sealed class Metronome : ISampleProvider
{
    private const double AccentFrequency = 1500.0;
    private const double NormalFrequency = 1000.0;
    private const double ClickSeconds = 0.045;

    private const float AccentAmplitude = 0.9f;
    private const float NormalAmplitude = 0.5f;
    private const float PreRollAmplitude = 0.28f;

    private readonly WaveFormat _waveFormat;
    private readonly int _sampleRate;
    private readonly int _clickSamples;

    private volatile bool _enabled;
    private volatile int _bpmTimes100 = 12000; // tempo * 100 for lock-free updates
    private volatile ClickAccent _accent = ClickAccent.Beat1;
    private volatile float _volume = 0.5f;
    private int _preRollBeats;

    private double _beatCounter;   // samples until the next beat fires
    private long _beatIndex;       // beats elapsed since the last Restart
    private int _clickRemaining;   // samples left in the current click
    private double _phase;
    private double _phaseInc;
    private float _clickAmp;

    public Metronome(int sampleRate)
    {
        _sampleRate = sampleRate;
        _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        _clickSamples = Math.Max(1, (int)(sampleRate * ClickSeconds));
    }

    public WaveFormat WaveFormat => _waveFormat;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public ClickAccent Accent
    {
        get => _accent;
        set => _accent = value;
    }

    /// <summary>Overall click level (0..1).</summary>
    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 1f);
    }

    public double Bpm
    {
        get => _bpmTimes100 / 100.0;
        set => _bpmTimes100 = (int)(Math.Clamp(value, 20.0, 300.0) * 100);
    }

    /// <summary>
    /// Resets the beat position for a new run. The first <paramref name="preRollBeats"/>
    /// beats are played quieter and unaccented; the beat after them is the downbeat
    /// to which the accent pattern is aligned.
    /// </summary>
    public void Restart(int preRollBeats)
    {
        _preRollBeats = Math.Max(0, preRollBeats);
        _beatCounter = 0.0; // fire the first beat on the very first sample
        _beatIndex = 0;
        _clickRemaining = 0;
        _phase = 0.0;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        // Always fill the buffer (silence when disabled) so the click keeps a
        // steady beat position relative to the rest of the output mix.
        int frames = count / 2;
        double samplesPerBeat = _sampleRate * 6000.0 / _bpmTimes100; // sr * 60 / bpm

        for (int i = 0; i < frames; i++)
        {
            if (_enabled)
            {
                if (_beatCounter <= 0.0)
                {
                    TriggerClick();
                    _beatCounter += samplesPerBeat;
                }

                _beatCounter -= 1.0;
            }

            float sample = 0f;
            if (_clickRemaining > 0)
            {
                double env = _clickRemaining / (double)_clickSamples;
                sample = (float)(Math.Sin(_phase) * env * _clickAmp) * _volume;
                _phase += _phaseInc;
                _clickRemaining--;
            }

            buffer[offset + (i * 2)] = sample;
            buffer[offset + (i * 2) + 1] = sample;
        }

        return count;
    }

    private void TriggerClick()
    {
        long index = _beatIndex++;
        double freq;

        if (index < _preRollBeats)
        {
            freq = NormalFrequency;
            _clickAmp = PreRollAmplitude;
        }
        else
        {
            long barBeat = (index - _preRollBeats) % 4; // 0..3
            bool accented = IsAccented(barBeat);
            freq = accented ? AccentFrequency : NormalFrequency;
            _clickAmp = accented ? AccentAmplitude : NormalAmplitude;
        }

        _phase = 0.0;
        _phaseInc = 2.0 * Math.PI * freq / _sampleRate;
        _clickRemaining = _clickSamples;
    }

    private bool IsAccented(long barBeat) => _accent switch
    {
        ClickAccent.Beat1 => barBeat == 0,
        ClickAccent.Beats1And3 => barBeat == 0 || barBeat == 2,
        ClickAccent.Beats2And4 => barBeat == 1 || barBeat == 3,
        _ => false,
    };
}
