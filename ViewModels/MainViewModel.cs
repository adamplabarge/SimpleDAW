using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SimpleDAW;

/// <summary>
/// Central view model: owns the track list, the audio engine and the MIDI clock,
/// and exposes the transport commands (Play / Record / Stop / Back to start),
/// tempo and device selection to the UI.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    public const string WasapiLabel = "Windows Default (WASAPI)";
    public const string NoMidiLabel = "None";
    public const string AsioOutputLabel = "Same as input (ASIO)";
    public const string DefaultOutputLabel = "Windows Default Output";

    private readonly AudioEngine _engine = new();
    private readonly MidiClock _midiClock = new();
    private readonly DispatcherTimer _meterTimer;

    private string _selectedAudioDevice = WasapiLabel;
    private string _selectedAudioOutput = DefaultOutputLabel;
    private string _selectedMidiDevice = NoMidiLabel;
    private readonly Dictionary<string, string?> _outputIds = new();
    private int _sampleRate = 48000;
    private double _tempo = 120.0;
    private int _trackCounter;
    private string _midiStatusNote = "Idle";
    private bool _sendMidiClock = true;
    private int _preRollBeats = 1;
    private DispatcherTimer? _preRollTimer;
    private bool _clickEnabled;
    private ClickAccent _clickAccent = ClickAccent.Beat1;
    private double _clickVolume = 0.5;
    private string _audioStatus = "Idle";
    private string? _currentProjectPath;
    private string _recordDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "SimpleDAW", "Recordings");
    private bool _suppressMonitor;
    private double _pixelsPerSecond = 80.0;
    private double _timelineOffsetSeconds;
    private double _playheadSeconds;
    private double _timelineMaxSeconds = 10.0;

    public MainViewModel()
    {
        AddTrackCommand = new RelayCommand(AddTrack);
        RemoveTrackCommand = new RelayCommand<TrackModel>(RemoveTrack);
        PlayCommand = new RelayCommand(Play, () => Mode == TransportMode.Stopped);
        RecordCommand = new RelayCommand(Record, () => Mode == TransportMode.Stopped);
        StopCommand = new RelayCommand(Stop, () => Mode != TransportMode.Stopped || _midiClock.IsRunning);
        BackToStartCommand = new RelayCommand(BackToStart);
        ShowAsioPanelCommand = new RelayCommand(ShowAsioPanel, () => _selectedAudioDevice != WasapiLabel);
        RefreshDevicesCommand = new RelayCommand(RefreshDevices);
        NewProjectCommand = new RelayCommand(NewProject);
        OpenProjectCommand = new RelayCommand(OpenProject);
        SaveProjectCommand = new RelayCommand(SaveProject);
        SaveProjectAsCommand = new RelayCommand(SaveProjectAs);
        MixdownCommand = new RelayCommand(Mixdown, () => Tracks.Any(t => t.HasAudio));
        ZoomInCommand = new RelayCommand(() => PixelsPerSecond = Math.Min(1000.0, PixelsPerSecond * 1.5));
        ZoomOutCommand = new RelayCommand(() => PixelsPerSecond = Math.Max(10.0, PixelsPerSecond / 1.5));

        _suppressMonitor = true;
        LoadDevices();
        DetectAndApplySampleRate();

        // Start with a couple of tracks so the UI is not empty. These default
        // to the first two hardware inputs; tracks added later start unassigned.
        AddTrack(0);
        AddTrack(1);

        _engine.PlaybackFinished += OnEnginePlaybackFinished;

        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _meterTimer.Tick += UpdateMeters;
        _meterTimer.Start();

        // Playhead updates are driven by the render loop (not the 33ms meter
        // timer) so the timeline ruler and follow-scroll move in step with
        // what's actually being painted, instead of visibly stepping every
        // 33ms while redraws happen every ~16ms.
        CompositionTarget.Rendering += OnRendering;

        _suppressMonitor = false;
        StartMonitorSafe();

        // The per-track "In" ComboBox binds its channel list via a RelativeSource
        // to the window, which can resolve after the SelectedValue binding on
        // startup and leave the selector blank even though InputChannel is set.
        // Re-assert the selection once the UI has finished loading.
        Dispatcher.CurrentDispatcher.BeginInvoke(
            new Action(() =>
            {
                foreach (var track in Tracks)
                {
                    track.RaiseInputChannelChanged();
                }
            }),
            DispatcherPriority.Loaded);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        PlayheadSeconds = _engine.PositionSeconds;
    }

    public ObservableCollection<TrackModel> Tracks { get; } = new();
    public ObservableCollection<string> AudioDevices { get; } = new();
    public ObservableCollection<string> AudioOutputs { get; } = new();
    public ObservableCollection<string> MidiDevices { get; } = new();
    public ObservableCollection<ChannelOption> AvailableInputChannels { get; } = new();
    public ObservableCollection<ChannelMeter> MasterInputMeters { get; } = new();
    public int[] SampleRateOptions { get; } = { 44100, 48000, 88200, 96000 };
    public int[] PreRollBeatsOptions { get; } = { 1, 2, 3, 4 };

    public IReadOnlyList<ClickAccentOption> ClickAccentOptions { get; } = new[]
    {
        new ClickAccentOption("None", ClickAccent.None),
        new ClickAccentOption("Beat 1", ClickAccent.Beat1),
        new ClickAccentOption("Beats 1 & 3", ClickAccent.Beats1And3),
        new ClickAccentOption("Beats 2 & 4", ClickAccent.Beats2And4),
    };

    public RelayCommand AddTrackCommand { get; }
    public RelayCommand<TrackModel> RemoveTrackCommand { get; }
    public RelayCommand PlayCommand { get; }
    public RelayCommand RecordCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand BackToStartCommand { get; }
    public RelayCommand ShowAsioPanelCommand { get; }
    public RelayCommand RefreshDevicesCommand { get; }
    public RelayCommand NewProjectCommand { get; }
    public RelayCommand OpenProjectCommand { get; }
    public RelayCommand SaveProjectCommand { get; }
    public RelayCommand SaveProjectAsCommand { get; }
    public RelayCommand MixdownCommand { get; }
    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }

    public TransportMode Mode => _engine.Mode;

    public string StatusText => Mode switch
    {
        TransportMode.Recording => "● Recording",
        TransportMode.Playing => "▶ Playing",
        _ => "■ Stopped",
    };

    public string MidiClockStatus
    {
        get
        {
            if (!string.IsNullOrEmpty(_midiClock.LastError))
            {
                return $"MIDI clock error: {_midiClock.LastError}";
            }

            if (_midiClock.IsRunning)
            {
                return $"MIDI clock: running on {_midiClock.ActiveDeviceName} @ {Tempo:F1} BPM (pulses: {_midiClock.PulseCount})";
            }

            if (!SendMidiClock)
            {
                return "MIDI clock: disabled";
            }

            if (SelectedMidiDevice == NoMidiLabel)
            {
                return "MIDI clock: no output selected";
            }

            return $"MIDI clock: {_midiStatusNote}";
        }
    }

    private TrackModel? _selectedTrack;
    public TrackModel? SelectedTrack
    {
        get => _selectedTrack;
        set => Set(ref _selectedTrack, value);
    }

    public string SelectedAudioDevice
    {
        get => _selectedAudioDevice;
        set
        {
            if (Set(ref _selectedAudioDevice, value))
            {
                bool previous = _suppressMonitor;
                _suppressMonitor = true;
                _engine.AsioDriverName = value == WasapiLabel ? null : value;
                RefreshOutputs();
                ApplyOutputSelection();
                _engine.Stop();              // release any open device so ASIO probing below is safe
                DetectAndApplySampleRate();  // match the newly selected device's rate
                _suppressMonitor = previous;
                StartMonitorSafe();      // opens the device so the channel count is known
                RefreshInputChannels();  // now reflects the newly selected device
                ShowAsioPanelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SelectedAudioOutput
    {
        get => _selectedAudioOutput;
        set
        {
            if (Set(ref _selectedAudioOutput, value))
            {
                ApplyOutputSelection();
                StartMonitorSafe();
            }
        }
    }

    public string SelectedMidiDevice
    {
        get => _selectedMidiDevice;
        set
        {
            if (Set(ref _selectedMidiDevice, value))
            {
                _midiStatusNote = "Idle";
                OnPropertyChanged(nameof(MidiClockStatus));
            }
        }
    }

    public int SampleRate
    {
        get => _sampleRate;
        set
        {
            if (Set(ref _sampleRate, value))
            {
                _engine.SampleRate = value;
                StartMonitorSafe();
            }
        }
    }

    public bool SendMidiClock
    {
        get => _sendMidiClock;
        set
        {
            if (Set(ref _sendMidiClock, value))
            {
                if (!value && _midiClock.IsRunning)
                {
                    _midiClock.Stop();
                    _midiStatusNote = "Clock disabled";
                    StopCommand.RaiseCanExecuteChanged();
                }

                OnPropertyChanged(nameof(MidiClockStatus));
            }
        }
    }

    /// <summary>
    /// Number of beats (1-4) to keep recording before the MIDI clock is started,
    /// so the synth's first beat is fully captured instead of clipped.
    /// </summary>
    public int PreRollBeats
    {
        get => _preRollBeats;
        set => Set(ref _preRollBeats, Math.Clamp(value, 1, 4));
    }

    /// <summary>When true, a metronome click plays during record and playback.</summary>
    public bool ClickEnabled
    {
        get => _clickEnabled;
        set
        {
            if (Set(ref _clickEnabled, value))
            {
                _engine.ClickEnabled = value;
            }
        }
    }

    /// <summary>Which beats of the bar the click accents.</summary>
    public ClickAccent ClickAccent
    {
        get => _clickAccent;
        set
        {
            if (Set(ref _clickAccent, value))
            {
                _engine.ClickAccent = value;
            }
        }
    }

    /// <summary>Click level (0..1).</summary>
    public double ClickVolume
    {
        get => _clickVolume;
        set
        {
            if (Set(ref _clickVolume, Math.Clamp(value, 0.0, 1.0)))
            {
                _engine.ClickVolume = (float)_clickVolume;
            }
        }
    }

    public string AudioStatus
    {
        get => _audioStatus;
        private set => Set(ref _audioStatus, value);
    }

    public double PixelsPerSecond
    {
        get => _pixelsPerSecond;
        set => Set(ref _pixelsPerSecond, value);
    }

    public double TimelineOffsetSeconds
    {
        get => _timelineOffsetSeconds;
        set => Set(ref _timelineOffsetSeconds, value);
    }

    public double PlayheadSeconds
    {
        get => _playheadSeconds;
        private set => Set(ref _playheadSeconds, value);
    }

    public double TimelineMaxSeconds
    {
        get => _timelineMaxSeconds;
        private set => Set(ref _timelineMaxSeconds, value);
    }

    public bool IsTransportActive => Mode != TransportMode.Stopped;

    public double Tempo
    {
        get => _tempo;
        set
        {
            double clamped = Math.Clamp(value, 20.0, 300.0);
            if (Set(ref _tempo, clamped))
            {
                _midiClock.Bpm = clamped;
                _engine.ClickBpm = clamped;
                OnPropertyChanged(nameof(MidiClockStatus));
            }
        }
    }

    public string RecordDirectory
    {
        get => _recordDirectory;
        private set => Set(ref _recordDirectory, value);
    }

    private bool HasArmedTrack => Tracks.Any(t => t.IsArmed);
    private bool HasPlayableAudio => Tracks.Any(t => t.HasAudio);

    private void LoadDevices()
    {
        AudioDevices.Clear();
        AudioDevices.Add(WasapiLabel);
        foreach (var name in AudioEngine.GetAsioDriverNames())
        {
            AudioDevices.Add(name);
        }

        MidiDevices.Clear();
        MidiDevices.Add(NoMidiLabel);
        foreach (var name in MidiClock.GetOutputDeviceNames())
        {
            MidiDevices.Add(name);
        }

        if (_selectedMidiDevice == NoMidiLabel && MidiDevices.Count > 1)
        {
            _selectedMidiDevice = MidiDevices[1];
        }

        RefreshOutputs();
        RefreshInputChannels();
    }

    private void RefreshOutputs()
    {
        string previous = _selectedAudioOutput;
        AudioOutputs.Clear();
        _outputIds.Clear();

        bool asioInput = _selectedAudioDevice != WasapiLabel;
        if (asioInput)
        {
            AudioOutputs.Add(AsioOutputLabel);
        }

        AudioOutputs.Add(DefaultOutputLabel);
        _outputIds[DefaultOutputLabel] = null;

        foreach (var device in AudioEngine.GetRenderDevices())
        {
            if (!AudioOutputs.Contains(device.Name))
            {
                AudioOutputs.Add(device.Name);
                _outputIds[device.Name] = device.Id;
            }
        }

        string target = AudioOutputs.Contains(previous)
            ? previous
            : (asioInput ? AsioOutputLabel : DefaultOutputLabel);
        _selectedAudioOutput = target;
        OnPropertyChanged(nameof(SelectedAudioOutput));
    }

    private void ApplyOutputSelection()
    {
        if (_selectedAudioOutput == AsioOutputLabel)
        {
            _engine.UseAsioOutput = true;
            _engine.OutputDeviceId = null;
        }
        else
        {
            _engine.UseAsioOutput = false;
            _engine.OutputDeviceId = _outputIds.TryGetValue(_selectedAudioOutput, out var id) ? id : null;
        }
    }

    private void RefreshDevices()
    {
        string audio = _selectedAudioDevice;
        string midi = _selectedMidiDevice;
        LoadDevices();

        SelectedAudioDevice = AudioDevices.Contains(audio) ? audio : WasapiLabel;
        SelectedMidiDevice = MidiDevices.Contains(midi) ? midi : NoMidiLabel;
    }

    private void RefreshInputChannels()
    {
        int count = _engine.GetInputChannelCount();
        AvailableInputChannels.Clear();
        for (int i = 0; i < Math.Max(count, 1); i++)
        {
            AvailableInputChannels.Add(new ChannelOption(i, ChannelLabels.Label(i, count), ChannelLabels.IsMain(i, count)));
        }

        // Rebuilding the list resets each track's "In" ComboBox selection, so
        // re-assert the (unchanged) InputChannel to make the selector show it.
        foreach (var track in Tracks)
        {
            track.RaiseInputChannelChanged();
        }
    }

    /// <summary>
    /// Queries the selected input device's sample rate and, if it maps to one
    /// of the offered options, applies it. Callers already run this inside a
    /// monitor-suppressed region, so it sets the fields directly rather than
    /// going through the <see cref="SampleRate"/> setter (which would restart
    /// the monitor a second time).
    /// </summary>
    private void DetectAndApplySampleRate()
    {
        int detected = _engine.GetDeviceSampleRate(SampleRateOptions);
        if (detected <= 0)
        {
            return;
        }

        int nearest = SampleRateOptions.OrderBy(r => Math.Abs(r - detected)).First();
        if (nearest != _sampleRate)
        {
            _sampleRate = nearest;
            _engine.SampleRate = nearest;
            OnPropertyChanged(nameof(SampleRate));
        }
    }

    private void StartMonitorSafe()
    {
        if (_suppressMonitor || Mode != TransportMode.Stopped)
        {
            return;
        }

        _engine.StartMonitor(Tracks.ToList());
        RebuildInputMeters();
        AudioStatus = _engine.LastError != null
            ? $"Audio error: {_engine.LastError}"
            : _engine.IsActive
                ? "Monitoring input \u2013 meters live"
                : "No audio device";
    }

    private void RebuildInputMeters()
    {
        int count = Math.Max(_engine.LastInputChannelCount, 0);
        if (MasterInputMeters.Count == count)
        {
            return;
        }

        foreach (var meter in MasterInputMeters)
        {
            meter.PropertyChanged -= OnInputMeterPropertyChanged;
        }

        MasterInputMeters.Clear();
        for (int i = 0; i < count; i++)
        {
            var meter = new ChannelMeter(i, ChannelLabels.Label(i, count), ChannelLabels.IsMain(i, count));
            meter.PropertyChanged += OnInputMeterPropertyChanged;
            MasterInputMeters.Add(meter);
        }

        PushMutedInputChannels();
        PushInputChannelGains();
    }

    private void OnInputMeterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChannelMeter.IsMuted))
        {
            PushMutedInputChannels();
        }
        else if (e.PropertyName == nameof(ChannelMeter.Gain))
        {
            PushInputChannelGains();
        }
    }

    private void PushMutedInputChannels() =>
        _engine.SetMutedInputChannels(MasterInputMeters.Where(m => m.IsMuted).Select(m => m.Index));

    private void PushInputChannelGains()
    {
        int count = MasterInputMeters.Count;
        var gains = new float[count];
        for (int i = 0; i < count; i++)
        {
            gains[i] = 1f;
        }

        foreach (var meter in MasterInputMeters)
        {
            if (meter.Index >= 0 && meter.Index < count)
            {
                gains[meter.Index] = meter.Gain;
            }
        }

        _engine.SetInputChannelGains(gains);
    }

    // Parameterless overload backs the "Add track" command: new tracks start
    // with no input assigned (-1) so they don't auto-grab a channel or show a
    // meter until the user picks one.
    private void AddTrack() => AddTrack(-1);

    private void AddTrack(int inputChannel)
    {
        _trackCounter++;
        var track = new TrackModel
        {
            Name = $"Track {_trackCounter}",
            InputChannel = inputChannel,
        };
        track.PropertyChanged += OnTrackPropertyChanged;
        Tracks.Add(track);
        SelectedTrack = track;
        RefreshTransport();
        StartMonitorSafe();
    }

    private void RemoveTrack(TrackModel? track)
    {
        track ??= SelectedTrack;
        if (track == null)
        {
            return;
        }

        var result = MessageBox.Show(
            $"Remove \"{track.Name}\"? This cannot be undone.",
            "Remove Track",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        track.PropertyChanged -= OnTrackPropertyChanged;
        Tracks.Remove(track);
        if (SelectedTrack == track)
        {
            SelectedTrack = Tracks.LastOrDefault();
        }

        RefreshTransport();
        StartMonitorSafe();
    }

    private void OnTrackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TrackModel.IsArmed) or nameof(TrackModel.RecordedFilePath))
        {
            RefreshTransport();
            if (e.PropertyName == nameof(TrackModel.IsArmed))
            {
                StartMonitorSafe();
            }
        }
    }

    private void ClearTracks()
    {
        foreach (var track in Tracks)
        {
            track.PropertyChanged -= OnTrackPropertyChanged;
        }

        Tracks.Clear();
    }

    private void NewProject()
    {
        Stop();
        ClearTracks();
        _trackCounter = 0;
        _currentProjectPath = null;
        UpdateRecordDirectory();
        AddTrack(0);
        AddTrack(1);
    }

    private void OpenProject()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Simple Recorder Project (*.srproj)|*.srproj|All files (*.*)|*.*",
            DefaultExt = ".srproj",
        };

        if (dialog.ShowDialog() == true)
        {
            LoadProject(dialog.FileName);
        }
    }

    private void LoadProject(string path)
    {
        ProjectFile? project;
        try
        {
            project = JsonSerializer.Deserialize<ProjectFile>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            AudioStatus = $"Failed to open project: {ex.Message}";
            return;
        }

        if (project == null)
        {
            return;
        }

        Stop();
        _suppressMonitor = true;

        ClearTracks();
        _trackCounter = 0;

        SampleRate = project.SampleRate;
        SelectedAudioDevice = AudioDevices.Contains(project.AudioDevice ?? WasapiLabel)
            ? project.AudioDevice ?? WasapiLabel
            : WasapiLabel;
        SelectedMidiDevice = MidiDevices.Contains(project.MidiDevice ?? NoMidiLabel)
            ? project.MidiDevice ?? NoMidiLabel
            : NoMidiLabel;
        if (!string.IsNullOrEmpty(project.AudioOutput) && AudioOutputs.Contains(project.AudioOutput))
        {
            SelectedAudioOutput = project.AudioOutput;
        }
        Tempo = project.Tempo;
        SendMidiClock = project.SendMidiClock;
        PreRollBeats = project.PreRollBeats;
        ClickEnabled = project.ClickEnabled;
        ClickAccent = project.ClickAccent;
        ClickVolume = project.ClickVolume;

        foreach (var pt in project.Tracks)
        {
            _trackCounter++;
            var track = new TrackModel
            {
                Name = pt.Name,
                InputChannel = pt.InputChannel,
                IsArmed = pt.IsArmed,
                IsMuted = pt.IsMuted,
                Volume = pt.Volume,
                Pan = pt.Pan,
                RecordedFilePath = pt.RecordedFilePath,
            };

            WaveformLoader.LoadInto(track.Waveform, pt.RecordedFilePath ?? string.Empty);
            track.PropertyChanged += OnTrackPropertyChanged;
            Tracks.Add(track);
        }

        SelectedTrack = Tracks.LastOrDefault();
        _currentProjectPath = path;
        UpdateRecordDirectory();

        _suppressMonitor = false;
        RefreshInputChannels();
        RefreshTransport();
        StartMonitorSafe();
    }

    private void SaveProject()
    {
        if (string.IsNullOrEmpty(_currentProjectPath))
        {
            SaveProjectAs();
            return;
        }

        WriteProject(_currentProjectPath);
    }

    private void SaveProjectAs()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Simple Recorder Project (*.srproj)|*.srproj",
            DefaultExt = ".srproj",
            FileName = "Untitled.srproj",
        };

        if (dialog.ShowDialog() == true)
        {
            _currentProjectPath = dialog.FileName;
            UpdateRecordDirectory();
            WriteProject(dialog.FileName);
        }
    }

    private void UpdateRecordDirectory()
    {
        if (!string.IsNullOrEmpty(_currentProjectPath))
        {
            string? projectDir = Path.GetDirectoryName(_currentProjectPath);
            if (!string.IsNullOrEmpty(projectDir))
            {
                RecordDirectory = Path.Combine(projectDir, "Recordings");
                return;
            }
        }

        RecordDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "SimpleDAW", "Recordings");
    }

    private string? EnsureRecordDirectoryForRecording()
    {
        if (string.IsNullOrEmpty(_currentProjectPath))
        {
            AudioStatus = "Save the project to choose where recordings are written.";
            SaveProjectAs();
        }

        if (string.IsNullOrEmpty(_currentProjectPath))
        {
            AudioStatus = "Recording cancelled: no project save location selected.";
            return null;
        }

        return RecordDirectory;
    }

    private void WriteProject(string path)
    {
        var project = new ProjectFile
        {
            AudioDevice = _selectedAudioDevice,
            AudioOutput = _selectedAudioOutput,
            SampleRate = _sampleRate,
            MidiDevice = _selectedMidiDevice,
            Tempo = _tempo,
            SendMidiClock = _sendMidiClock,
            PreRollBeats = _preRollBeats,
            ClickEnabled = _clickEnabled,
            ClickAccent = _clickAccent,
            ClickVolume = _clickVolume,
            Tracks = Tracks.Select(t => new ProjectTrack
            {
                Name = t.Name,
                InputChannel = t.InputChannel,
                IsArmed = t.IsArmed,
                IsMuted = t.IsMuted,
                Volume = t.Volume,
                Pan = t.Pan,
                RecordedFilePath = t.RecordedFilePath,
            }).ToList(),
        };

        try
        {
            string json = JsonSerializer.Serialize(project, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            AudioStatus = $"Saved project: {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            AudioStatus = $"Failed to save project: {ex.Message}";
        }
    }

    private void Mixdown()
    {
        if (!Tracks.Any(t => t.HasAudio))
        {
            AudioStatus = "Nothing to mix down yet \u2013 record or load some audio first.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "WAV audio (*.wav)|*.wav",
            DefaultExt = ".wav",
            FileName = "Mixdown.wav",
        };

        if (!string.IsNullOrEmpty(_currentProjectPath))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(_currentProjectPath);
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        // Stop the transport so recorded files are closed and the device is free.
        Stop();

        try
        {
            bool exported = MixdownExporter.Export(Tracks.ToList(), _sampleRate, dialog.FileName);
            AudioStatus = exported
                ? $"Exported mix: {Path.GetFileName(dialog.FileName)}"
                : "Nothing to mix down.";
        }
        catch (Exception ex)
        {
            AudioStatus = $"Mixdown failed: {ex.Message}";
        }
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds < 0)
        {
            seconds = 0;
        }

        int minutes = (int)(seconds / 60);
        double secs = seconds - (minutes * 60);
        return $"{minutes}:{secs:00.0}";
    }

    private void Play()
    {
        _engine.ClickBpm = _tempo;
        if (HasPlayableAudio || ClickEnabled)
        {
            _engine.StartPlayback(Tracks.ToList());
            _midiStatusNote = "Running";
        }
        else
        {
            _midiStatusNote = "Clock-only mode (no audio tracks to play)";
        }

        StartMidiClock();
        RefreshTransport();
    }

    private void Record()
    {
        if (HasArmedTrack)
        {
            string? recordDirectory = EnsureRecordDirectoryForRecording();
            if (recordDirectory == null)
            {
                return;
            }

            _engine.ClickBpm = _tempo;
            int preRoll = ShouldPreRollBeforeClock() ? PreRollBeats : 0;
            _engine.StartRecording(Tracks.ToList(), recordDirectory, preRoll);
            _midiStatusNote = "Running";

            if (preRoll > 0)
            {
                // Keep recording for a few beats before the synth is told to
                // start, so its first beat is captured in full rather than clipped.
                StartMidiClockAfterPreRoll();
                RefreshTransport();
                return;
            }
        }
        else
        {
            _midiStatusNote = "Clock-only mode (no armed tracks)";
        }

        StartMidiClock();
        RefreshTransport();
    }

    private void Stop()
    {
        CancelPreRoll();
        _midiClock.Stop();
        _midiStatusNote = "Stopped";
        _engine.Stop();
        foreach (var track in Tracks)
        {
            track.Level = 0f;
        }

        RefreshTransport();
        StartMonitorSafe();
    }

    private void BackToStart()
    {
        if (Mode == TransportMode.Playing)
        {
            _engine.Rewind();
        }
    }

    private void StartMidiClock()
    {
        if (!SendMidiClock)
        {
            _midiStatusNote = "Clock disabled";
            OnPropertyChanged(nameof(MidiClockStatus));
            return;
        }

        _midiClock.Bpm = _tempo;
        int index = MidiDevices.IndexOf(_selectedMidiDevice) - 1; // -1 accounts for the "None" entry
        bool started = _midiClock.Start(index);
        if (!started)
        {
            _midiStatusNote = "Not started";
        }

        OnPropertyChanged(nameof(MidiClockStatus));
    }

    private bool ShouldPreRollBeforeClock() =>
        SendMidiClock && _selectedMidiDevice != NoMidiLabel && PreRollBeats > 0;

    private void StartMidiClockAfterPreRoll()
    {
        CancelPreRoll();

        double delaySeconds = PreRollBeats * (60.0 / _tempo);
        _midiStatusNote = $"Pre-roll {PreRollBeats} beat{(PreRollBeats == 1 ? string.Empty : "s")}...";
        OnPropertyChanged(nameof(MidiClockStatus));

        _preRollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delaySeconds) };
        _preRollTimer.Tick += OnPreRollElapsed;
        _preRollTimer.Start();
    }

    private void OnPreRollElapsed(object? sender, EventArgs e)
    {
        CancelPreRoll();

        // The user may have pressed Stop during the pre-roll; only start the
        // clock if we are still recording.
        if (Mode == TransportMode.Recording)
        {
            StartMidiClock();
            RefreshTransport();
        }
    }

    private void CancelPreRoll()
    {
        if (_preRollTimer != null)
        {
            _preRollTimer.Stop();
            _preRollTimer.Tick -= OnPreRollElapsed;
            _preRollTimer = null;
        }
    }

    private void ShowAsioPanel()
    {
        try
        {
            _engine.ShowAsioControlPanel();
        }
        catch
        {
            // Driver may not expose a panel; ignore.
        }
    }

    private void OnEnginePlaybackFinished(object? sender, EventArgs e)
    {
        // Marshal to the UI thread and auto-stop when the mix reaches the end.
        _meterTimer.Dispatcher.BeginInvoke(() =>
        {
            if (Mode == TransportMode.Playing && _engine.IsPlaybackFinished)
            {
                Stop();
            }
        });
    }

    private void UpdateMeters(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(MidiClockStatus));

        // Timeline length. PlayheadSeconds itself is kept up to date by
        // OnRendering (render-cadence), not here.
        double maxDuration = 0.0;
        foreach (var track in Tracks)
        {
            double d = track.Waveform.DurationSeconds;
            track.DurationText = FormatDuration(d);
            if (d > maxDuration)
            {
                maxDuration = d;
            }
        }

        TimelineMaxSeconds = Math.Max(maxDuration, Math.Max(PlayheadSeconds, 10.0));

        // Master input meters run whenever a device is open (monitor/record).
        foreach (var meter in MasterInputMeters)
        {
            meter.Level = ToMeterScale(_engine.GetInputLevel(meter.Index));
        }

        if (Mode == TransportMode.Playing && _engine.IsPlaybackFinished)
        {
            Stop();
            return;
        }

        var player = _engine.Player;
        bool inputActive = _engine.IsActive && Mode != TransportMode.Playing;
        foreach (var track in Tracks)
        {
            if (inputActive && track.IsArmed && track.InputChannel >= 0)
            {
                // While recording, use the post-gain peak so the meter reflects
                // the track's record volume. While only monitoring (not yet
                // recording) there is no recording, so scale the raw input
                // level by the track volume for the same feedback.
                float? recPeak = _engine.GetRecordingPeak(track);
                float level = recPeak ?? (_engine.GetInputLevel(track.InputChannel) * track.Volume);
                track.Level = ToMeterScale(level);
            }
            else if (Mode == TransportMode.Playing && player != null && track.HasAudio)
            {
                track.Level = ToMeterScale(player.GetPeak(track));
            }
            else
            {
                track.Level = 0f;
            }
        }
    }

    /// <summary>
    /// Maps a linear peak amplitude (0..1) to a perceptual meter deflection
    /// (0..1) on a decibel scale, so ordinary signals produce a usefully tall
    /// bar instead of a barely-visible sliver. -60 dBFS reads as empty and
    /// 0 dBFS as full. This only affects the on-screen meter, not the audio.
    /// </summary>
    private static float ToMeterScale(float linearPeak)
    {
        if (linearPeak <= 0f)
        {
            return 0f;
        }

        const double minDb = -60.0;
        double db = 20.0 * Math.Log10(linearPeak);
        double norm = (db - minDb) / -minDb;
        return (float)Math.Clamp(norm, 0.0, 1.0);
    }

    private void RefreshTransport()
    {
        PlayCommand.RaiseCanExecuteChanged();
        RecordCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        MixdownCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(MidiClockStatus));
        OnPropertyChanged(nameof(IsTransportActive));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        CompositionTarget.Rendering -= OnRendering;
        _meterTimer.Stop();
        _midiClock.Dispose();
        _engine.Dispose();
    }
}
