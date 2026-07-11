using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
    private bool _monitorEnabled = true;
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
        RemoveTrackCommand = new RelayCommand(RemoveSelectedTrack, () => SelectedTrack != null);
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
        ZoomInCommand = new RelayCommand(() => PixelsPerSecond = Math.Min(1000.0, PixelsPerSecond * 1.5));
        ZoomOutCommand = new RelayCommand(() => PixelsPerSecond = Math.Max(10.0, PixelsPerSecond / 1.5));

        _suppressMonitor = true;
        LoadDevices();

        // Start with a couple of tracks so the UI is not empty.
        AddTrack();
        AddTrack();

        _engine.PlaybackFinished += OnEnginePlaybackFinished;

        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _meterTimer.Tick += UpdateMeters;
        _meterTimer.Start();

        _suppressMonitor = false;
        StartMonitorSafe();
    }

    public ObservableCollection<TrackModel> Tracks { get; } = new();
    public ObservableCollection<string> AudioDevices { get; } = new();
    public ObservableCollection<string> AudioOutputs { get; } = new();
    public ObservableCollection<string> MidiDevices { get; } = new();
    public ObservableCollection<ChannelOption> AvailableInputChannels { get; } = new();
    public ObservableCollection<ChannelMeter> MasterInputMeters { get; } = new();
    public int[] SampleRateOptions { get; } = { 44100, 48000, 88200, 96000 };

    public RelayCommand AddTrackCommand { get; }
    public RelayCommand RemoveTrackCommand { get; }
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
        set
        {
            if (Set(ref _selectedTrack, value))
            {
                RemoveTrackCommand.RaiseCanExecuteChanged();
            }
        }
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

    public bool MonitorEnabled
    {
        get => _monitorEnabled;
        set
        {
            if (Set(ref _monitorEnabled, value))
            {
                _engine.MonitorEnabled = value;
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

        MasterInputMeters.Clear();
        for (int i = 0; i < count; i++)
        {
            MasterInputMeters.Add(new ChannelMeter(i, ChannelLabels.Label(i, count), ChannelLabels.IsMain(i, count)));
        }
    }

    private void AddTrack()
    {
        _trackCounter++;
        var track = new TrackModel
        {
            Name = $"Track {_trackCounter}",
            InputChannel = Math.Min(_trackCounter - 1, Math.Max(AvailableInputChannels.Count - 1, 0)),
        };
        track.PropertyChanged += OnTrackPropertyChanged;
        Tracks.Add(track);
        SelectedTrack = track;
        RefreshTransport();
        StartMonitorSafe();
    }

    private void RemoveSelectedTrack()
    {
        if (SelectedTrack == null)
        {
            return;
        }

        SelectedTrack.PropertyChanged -= OnTrackPropertyChanged;
        Tracks.Remove(SelectedTrack);
        SelectedTrack = Tracks.LastOrDefault();
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
        AddTrack();
        AddTrack();
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
        MonitorEnabled = project.MonitorEnabled;

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
            MonitorEnabled = _monitorEnabled,
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

    private void Play()
    {
        if (HasPlayableAudio)
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

            _engine.StartRecording(Tracks.ToList(), recordDirectory);
            _midiStatusNote = "Running";
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
        _midiClock.Bpm = _tempo;
        int index = MidiDevices.IndexOf(_selectedMidiDevice) - 1; // -1 accounts for the "None" entry
        bool started = _midiClock.Start(index);
        if (!started)
        {
            _midiStatusNote = "Not started";
        }

        OnPropertyChanged(nameof(MidiClockStatus));
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

        // Timeline playhead and length.
        PlayheadSeconds = _engine.PositionSeconds;
        double maxDuration = 0.0;
        foreach (var track in Tracks)
        {
            double d = track.Waveform.DurationSeconds;
            if (d > maxDuration)
            {
                maxDuration = d;
            }
        }

        TimelineMaxSeconds = Math.Max(maxDuration, Math.Max(PlayheadSeconds, 10.0));

        // Master input meters run whenever a device is open (monitor/record).
        foreach (var meter in MasterInputMeters)
        {
            meter.Level = _engine.GetInputLevel(meter.Index);
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
            if (inputActive && track.IsArmed)
            {
                track.Level = _engine.GetInputLevel(track.InputChannel);
            }
            else if (Mode == TransportMode.Playing && player != null && track.HasAudio)
            {
                track.Level = player.GetPeak(track);
            }
            else
            {
                track.Level = 0f;
            }
        }
    }

    private void RefreshTransport()
    {
        PlayCommand.RaiseCanExecuteChanged();
        RecordCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
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
        _meterTimer.Stop();
        _midiClock.Dispose();
        _engine.Dispose();
    }
}
