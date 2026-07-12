using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SimpleDAW;

public enum TransportMode
{
    Stopped,
    Playing,
    Recording,
}

/// <summary>
/// Owns the audio hardware. It can capture individual input channels (one per
/// armed track) from an ASIO device such as the TASCAM Model 12, or from the
/// default Windows (WASAPI) input as a fallback for testing without hardware,
/// and it plays existing tracks back as a stereo mix.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private sealed class Recording
    {
        public required TrackModel Track { get; init; }
        public required int Channel { get; init; }
        public required WaveFileWriter Writer { get; init; }
        public float SliceMin;
        public float SliceMax;
        public int SliceCount;
    }

    private readonly ConcurrentDictionary<int, float> _inputPeaks = new();
    private readonly List<Recording> _recordings = new();
    private readonly Stopwatch _positionClock = new();

    private volatile HashSet<int> _mutedInputChannels = new();

    private Metronome? _metronome;
    private bool _clickEnabled;
    private ClickAccent _clickAccent = ClickAccent.Beat1;
    private double _clickBpm = 120.0;
    private float _clickVolume = 0.5f;
    private bool _clickActive; // true only while a Play/Record stream should click

    private AsioOut? _asio;
    private WasapiOut? _wasapiOut;
    private WasapiCapture? _wasapiCapture;
    private MultiTrackPlayer? _player;
    private MediaFoundationResampler? _renderResampler;
    private IReadOnlyList<TrackModel>? _tracks;

    private float[]? _interleaved;
    private float[]? _chPeak;
    private float[]? _chMin;
    private float[]? _chMax;

    /// <summary>ASIO driver name to use. Null selects the WASAPI fallback.</summary>
    public string? AsioDriverName { get; set; }

    /// <summary>Engine sample rate in Hz (used for ASIO and for recorded files).</summary>
    public int SampleRate { get; set; } = 48000;

    /// <summary>When true (and an ASIO input is used), output goes to the ASIO device.</summary>
    public bool UseAsioOutput { get; set; } = true;

    /// <summary>WASAPI render device id to output to (null = system default).</summary>
    public string? OutputDeviceId { get; set; }

    /// <summary>When true, a metronome click is mixed into the output during play/record.</summary>
    public bool ClickEnabled
    {
        get => _clickEnabled;
        set
        {
            _clickEnabled = value;
            if (_metronome != null)
            {
                _metronome.Enabled = value && _clickActive;
            }
        }
    }

    /// <summary>Which beats of the bar are accented in the click.</summary>
    public ClickAccent ClickAccent
    {
        get => _clickAccent;
        set
        {
            _clickAccent = value;
            if (_metronome != null)
            {
                _metronome.Accent = value;
            }
        }
    }

    /// <summary>Click tempo in BPM (kept in sync with the transport tempo).</summary>
    public double ClickBpm
    {
        get => _clickBpm;
        set
        {
            _clickBpm = value;
            if (_metronome != null)
            {
                _metronome.Bpm = value;
            }
        }
    }

    /// <summary>Overall click level (0..1).</summary>
    public float ClickVolume
    {
        get => _clickVolume;
        set
        {
            _clickVolume = value;
            if (_metronome != null)
            {
                _metronome.Volume = value;
            }
        }
    }

    public TransportMode Mode { get; private set; } = TransportMode.Stopped;

    /// <summary>Input channel count of the last opened device (0 if unknown).</summary>
    public int LastInputChannelCount { get; private set; }

    /// <summary>True while a device stream (monitor, play or record) is open.</summary>
    public bool IsActive => _asio != null || _wasapiCapture != null || _wasapiOut != null;

    /// <summary>Current transport position in seconds (0 when stopped).</summary>
    public double PositionSeconds => _positionClock.Elapsed.TotalSeconds;

    /// <summary>The mixer currently driving playback, for reading track meters.</summary>
    public MultiTrackPlayer? Player => _player;

    /// <summary>Last error from opening/using a device, or null if none.</summary>
    public string? LastError { get; private set; }

    public event EventHandler? PlaybackFinished;

    public static IReadOnlyList<string> GetAsioDriverNames() => AsioOut.GetDriverNames();

    /// <summary>Lists the active Windows (WASAPI) output devices.</summary>
    public static IReadOnlyList<AudioOutputInfo> GetRenderDevices()
    {
        var list = new List<AudioOutputInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                list.Add(new AudioOutputInfo(device.ID, device.FriendlyName));
            }
        }
        catch
        {
            // Ignore enumeration failures and return what we have.
        }

        return list;
    }

    /// <summary>Number of input channels the selected device exposes.</summary>
    public int GetInputChannelCount()
    {
        if (IsActive && LastInputChannelCount > 0)
        {
            return LastInputChannelCount;
        }

        if (!string.IsNullOrEmpty(AsioDriverName))
        {
            try
            {
                using var probe = new AsioOut(AsioDriverName);
                return probe.DriverInputChannelCount;
            }
            catch
            {
                return 0;
            }
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            return device.AudioClient.MixFormat.Channels;
        }
        catch
        {
            return 2;
        }
    }

    /// <summary>Opens the ASIO driver's own control panel, if one is selected.</summary>
    public void ShowAsioControlPanel()
    {
        if (string.IsNullOrEmpty(AsioDriverName))
        {
            return;
        }

        if (_asio != null)
        {
            Stop();
        }

        using var asio = new AsioOut(AsioDriverName);
        asio.ShowControlPanel();
    }

    /// <summary>Most recent peak level (0..1) captured on a hardware input channel.</summary>
    public float GetInputLevel(int channel) =>
        _inputPeaks.TryGetValue(channel, out float v) ? v : 0f;

    /// <summary>Sets which hardware input channels are muted in the monitor mix.</summary>
    public void SetMutedInputChannels(IEnumerable<int> channels) =>
        _mutedInputChannels = new HashSet<int>(channels);

    /// <summary>
    /// Opens the device for metering (and live monitoring) without recording or
    /// playing, so the input meters run whenever the transport is stopped.
    /// </summary>
    public void StartMonitor(IReadOnlyList<TrackModel> tracks)
    {
        Stop();
        _tracks = tracks;
        LastError = null;

        try
        {
            if (!string.IsNullOrEmpty(AsioDriverName))
            {
                StartAsioStream(subscribeStopped: false);
            }
            else
            {
                var capture = new WasapiCapture();
                LastInputChannelCount = capture.WaveFormat.Channels;
                capture.DataAvailable += OnWasapiDataAvailable;
                capture.StartRecording();
                _wasapiCapture = capture;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Stop();
        }

        Mode = TransportMode.Stopped;
    }

    private MixingSampleProvider BuildMixer()
    {
        var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2))
        {
            ReadFully = true,
        };

        if (_player != null)
        {
            mixer.AddMixerInput(_player);
        }

        if (_metronome != null)
        {
            mixer.AddMixerInput(_metronome);
        }

        return mixer;
    }

    /// <summary>
    /// Creates (or reuses) the metronome for a play/record run and aligns it so
    /// the first <paramref name="preRollBeats"/> beats are the quieter count-in.
    /// </summary>
    private void PrepareClick(int preRollBeats)
    {
        if (_metronome == null || _metronome.WaveFormat.SampleRate != SampleRate)
        {
            _metronome = new Metronome(SampleRate);
        }

        _metronome.Bpm = _clickBpm;
        _metronome.Accent = _clickAccent;
        _metronome.Volume = _clickVolume;
        _metronome.Restart(preRollBeats);
        _clickActive = true;
        _metronome.Enabled = _clickEnabled;
    }

    /// <summary>Silences the click when leaving a play/record run.</summary>
    private void DeactivateClick()
    {
        _clickActive = false;
        if (_metronome != null)
        {
            _metronome.Enabled = false;
        }
    }

    private void StartAsioStream(bool subscribeStopped)
    {
        var asio = new AsioOut(AsioDriverName);

        // Store the instance immediately so that if InitRecordAndPlayback/Play throws
        // (e.g. ASE_NotPresent), Stop() disposes it and releases the ASIO driver.
        // Otherwise the failed instance leaks, keeps the driver locked, and every
        // subsequent start attempt also fails with ASE_NotPresent.
        _asio = asio;

        int inputChannels = asio.DriverInputChannelCount;
        LastInputChannelCount = inputChannels;

        asio.AudioAvailable += OnAsioAudioAvailable;
        if (subscribeStopped)
        {
            asio.PlaybackStopped += OnDeviceStopped;
        }

        var mixer = BuildMixer();

        // Only initialise the ASIO device's OUTPUT when we actually render through
        // ASIO. When output is routed to Windows (WASAPI), open ASIO record-only by
        // passing a null provider. Forcing an ASIO output here makes the driver claim
        // its output channels, which fails with ASE_NotPresent when the same device
        // is already held by the Windows audio engine (e.g. it's the default device).
        IWaveProvider? asioOutput = UseAsioOutput ? new SampleToWaveProvider(mixer) : null;

        asio.InitRecordAndPlayback(asioOutput!, inputChannels, SampleRate);
        asio.Play();

        if (!UseAsioOutput)
        {
            StartWasapiRender(mixer, subscribeStopped);
        }
    }

    private void StartWasapiRender(ISampleProvider mixer, bool subscribeStopped)
    {
        var device = GetRenderDevice();
        var wasapi = new WasapiOut(device, AudioClientShareMode.Shared, false, 100);
        if (subscribeStopped)
        {
            wasapi.PlaybackStopped += OnDeviceStopped;
        }

        IWaveProvider wave = new SampleToWaveProvider(mixer);
        _renderResampler = new MediaFoundationResampler(wave, device.AudioClient.MixFormat)
        {
            ResamplerQuality = 60,
        };

        wasapi.Init(_renderResampler);
        wasapi.Play();
        _wasapiOut = wasapi;
    }

    private MMDevice GetRenderDevice()
    {
        var enumerator = new MMDeviceEnumerator();
        if (string.IsNullOrEmpty(OutputDeviceId))
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        try
        {
            return enumerator.GetDevice(OutputDeviceId);
        }
        catch
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
    }

    /// <summary>Starts playing back the recorded audio of the given tracks.</summary>
    public void StartPlayback(IReadOnlyList<TrackModel> tracks)
    {
        Stop();
        _tracks = tracks;
        LastError = null;
        _player = new MultiTrackPlayer(SampleRate, tracks);
        PrepareClick(0);

        try
        {
            if (!string.IsNullOrEmpty(AsioDriverName))
            {
                StartAsioStream(subscribeStopped: true);
            }
            else
            {
                StartWasapiRender(BuildMixer(), subscribeStopped: true);
            }

            Mode = TransportMode.Playing;
            _positionClock.Restart();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Stop();
        }
    }

    /// <summary>
    /// Starts recording: armed tracks capture their assigned input channel while
    /// any other tracks with audio are played back for monitoring/overdubbing.
    /// </summary>
    public void StartRecording(IReadOnlyList<TrackModel> tracks, string recordDirectory, int preRollBeats = 0)
    {
        Stop();
        _tracks = tracks;
        LastError = null;
        Directory.CreateDirectory(recordDirectory);

        var armed = tracks.Where(t => t.IsArmed).ToList();
        var playback = tracks.Where(t => !t.IsArmed && t.HasAudio).ToList();
        _player = new MultiTrackPlayer(SampleRate, playback);
        PrepareClick(preRollBeats);

        try
        {
            if (!string.IsNullOrEmpty(AsioDriverName))
            {
                StartAsioRecording(armed, recordDirectory);
            }
            else
            {
                StartWasapiRecording(armed, recordDirectory);
            }

            Mode = TransportMode.Recording;
            _positionClock.Restart();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Stop();
        }
    }

    private void StartAsioRecording(List<TrackModel> armed, string recordDirectory)
    {
        // Determine the input channel count without opening a second stream.
        int inputChannels;
        using (var probe = new AsioOut(AsioDriverName))
        {
            inputChannels = probe.DriverInputChannelCount;
        }

        foreach (var track in armed)
        {
            if (track.InputChannel < 0 || track.InputChannel >= inputChannels)
            {
                continue;
            }

            AddRecording(track, track.InputChannel, recordDirectory);
        }

        StartAsioStream(subscribeStopped: true);
    }

    private void StartWasapiRecording(List<TrackModel> armed, string recordDirectory)
    {
        var capture = new WasapiCapture();
        int inputChannels = capture.WaveFormat.Channels;
        int captureRate = capture.WaveFormat.SampleRate;
        LastInputChannelCount = inputChannels;

        foreach (var track in armed)
        {
            int channel = Math.Clamp(track.InputChannel, 0, inputChannels - 1);
            AddRecording(track, channel, recordDirectory, captureRate);
        }

        capture.DataAvailable += OnWasapiDataAvailable;
        capture.RecordingStopped += (_, _) => OnDeviceStopped(this, EventArgs.Empty);
        capture.StartRecording();
        _wasapiCapture = capture;

        // Render playback tracks and/or the click out to the device. When there is
        // nothing to play and the click is off, skip opening an output stream.
        if (_player != null || (_clickEnabled && _clickActive))
        {
            try
            {
                StartWasapiRender(BuildMixer(), subscribeStopped: false);
            }
            catch
            {
                _wasapiOut = null;
            }
        }
    }

    private void AddRecording(TrackModel track, int channel, string recordDirectory, int? rate = null)
    {
        string safeName = string.Concat(track.Name.Split(Path.GetInvalidFileNameChars()));
        string fileName = $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.wav";
        string path = Path.Combine(recordDirectory, fileName);
        var format = new WaveFormat(rate ?? SampleRate, 24, 1);
        var writer = new WaveFileWriter(path, format);
        track.Waveform.SampleRate = rate ?? SampleRate;
        track.Waveform.Clear();
        track.RecordedFilePath = path;
        _recordings.Add(new Recording { Track = track, Channel = channel, Writer = writer });
    }

    private void OnAsioAudioAvailable(object? sender, AsioAudioAvailableEventArgs e)
    {
        int channels = e.InputBuffers.Length;
        int samplesPerBuffer = e.SamplesPerBuffer;
        int needed = channels * samplesPerBuffer;

        if (_interleaved == null || _interleaved.Length < needed)
        {
            _interleaved = new float[needed];
        }

        if (_chPeak == null || _chPeak.Length < channels)
        {
            _chPeak = new float[channels];
            _chMin = new float[channels];
            _chMax = new float[channels];
        }

        e.GetAsInterleavedSamples(_interleaved);

        // Per-channel peak/min/max for meters (all channels) and waveforms.
        Array.Clear(_chPeak, 0, channels);
        Array.Clear(_chMin!, 0, channels);
        Array.Clear(_chMax!, 0, channels);

        for (int f = 0; f < samplesPerBuffer; f++)
        {
            int baseIndex = f * channels;
            for (int ch = 0; ch < channels; ch++)
            {
                float sample = _interleaved[baseIndex + ch];
                if (sample < _chMin![ch])
                {
                    _chMin[ch] = sample;
                }

                if (sample > _chMax![ch])
                {
                    _chMax[ch] = sample;
                }

                float abs = Math.Abs(sample);
                if (abs > _chPeak[ch])
                {
                    _chPeak[ch] = abs;
                }
            }
        }

        for (int ch = 0; ch < channels; ch++)
        {
            _inputPeaks[ch] = _chPeak[ch];
        }

        // Write armed tracks to disk and to their waveform.
        foreach (var rec in _recordings)
        {
            int ch = rec.Channel;
            if (ch < 0 || ch >= channels)
            {
                continue;
            }

            for (int f = 0; f < samplesPerBuffer; f++)
            {
                float sample = _interleaved[(f * channels) + ch];
                rec.Writer.WriteSample(sample);
                AccumulateSlice(rec, sample);
            }
        }

        e.WrittenToOutputBuffers = false;
    }

    private void OnWasapiDataAvailable(object? sender, WaveInEventArgs e)
    {
        var format = _wasapiCapture!.WaveFormat;
        int channels = format.Channels;
        int bytesPerSample = format.BitsPerSample / 8;
        int frameSize = bytesPerSample * channels;
        int frames = e.BytesRecorded / frameSize;
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;

        var peaks = new float[channels];
        var mins = new float[channels];
        var maxs = new float[channels];

        for (int f = 0; f < frames; f++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                int index = (f * frameSize) + (ch * bytesPerSample);
                float sample = isFloat
                    ? BitConverter.ToSingle(e.Buffer, index)
                    : BitConverter.ToInt16(e.Buffer, index) / 32768f;

                if (sample < mins[ch])
                {
                    mins[ch] = sample;
                }

                if (sample > maxs[ch])
                {
                    maxs[ch] = sample;
                }

                float abs = Math.Abs(sample);
                if (abs > peaks[ch])
                {
                    peaks[ch] = abs;
                }

                foreach (var rec in _recordings)
                {
                    if (rec.Channel == ch)
                    {
                        rec.Writer.WriteSample(sample);
                        AccumulateSlice(rec, sample);
                    }
                }
            }
        }

        for (int ch = 0; ch < channels; ch++)
        {
            _inputPeaks[ch] = peaks[ch];
        }
    }

    private static void AccumulateSlice(Recording rec, float sample)
    {
        if (sample < rec.SliceMin)
        {
            rec.SliceMin = sample;
        }

        if (sample > rec.SliceMax)
        {
            rec.SliceMax = sample;
        }

        if (++rec.SliceCount >= WaveformBuffer.SamplesPerSlice)
        {
            rec.Track.Waveform.Add(rec.SliceMin, rec.SliceMax);
            rec.SliceMin = 0f;
            rec.SliceMax = 0f;
            rec.SliceCount = 0;
        }
    }

    private void OnDeviceStopped(object? sender, EventArgs e) =>
        PlaybackFinished?.Invoke(this, EventArgs.Empty);

    /// <summary>Rewinds playback to the start without stopping.</summary>
    public void Rewind()
    {
        _player?.Rewind();
        if (_positionClock.IsRunning)
        {
            _positionClock.Restart();
        }
    }

    /// <summary>
    /// True once the playback mix has reached the end of every track. A click-only
    /// run (no audio sources) never reports finished, so it plays until stopped.
    /// </summary>
    public bool IsPlaybackFinished => _player != null && _player.HasSources && _player.IsFinished;

    public void Stop()
    {
        DeactivateClick();

        if (_asio != null)
        {
            _asio.AudioAvailable -= OnAsioAudioAvailable;
            _asio.PlaybackStopped -= OnDeviceStopped;
            try { _asio.Stop(); } catch { }
            _asio.Dispose();
            _asio = null;
        }

        if (_wasapiCapture != null)
        {
            try { _wasapiCapture.StopRecording(); } catch { }
            _wasapiCapture.Dispose();
            _wasapiCapture = null;
        }

        if (_wasapiOut != null)
        {
            try { _wasapiOut.Stop(); } catch { }
            _wasapiOut.Dispose();
            _wasapiOut = null;
        }

        if (_renderResampler != null)
        {
            try { _renderResampler.Dispose(); } catch { }
            _renderResampler = null;
        }

        foreach (var rec in _recordings)
        {
            rec.Writer.Dispose();
        }

        _recordings.Clear();
        _inputPeaks.Clear();

        _player?.Dispose();
        _player = null;

        _positionClock.Reset();
        Mode = TransportMode.Stopped;
    }

    public void Dispose() => Stop();
}
