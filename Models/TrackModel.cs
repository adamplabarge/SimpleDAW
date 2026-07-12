using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace SimpleDAW;

/// <summary>
/// Represents a single track in the project. A track can be assigned to one
/// hardware input channel (e.g. an input on the TASCAM Model 12), armed for
/// recording, and has a recorded audio file once something has been captured.
/// </summary>
public class TrackModel : INotifyPropertyChanged
{
    private string _name = "Track";
    private int _inputChannel;
    private bool _isArmed;
    private bool _isMuted;
    private float _volume = 1.0f;
    private float _pan;
    private float _level;
    private string? _recordedFilePath;
    private string _durationText = "0:00.0";

    /// <summary>Display name of the track.</summary>
    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    /// <summary>Zero-based hardware input channel this track records from.</summary>
    public int InputChannel
    {
        get => _inputChannel;
        set => Set(ref _inputChannel, value);
    }

    /// <summary>
    /// Forces the <see cref="InputChannel"/> binding to re-evaluate without
    /// changing its value. The per-track "In" ComboBox resolves its ItemsSource
    /// (the list of available channels) via a RelativeSource to the window,
    /// which can bind after the SelectedValue on startup and leave the selector
    /// blank. Re-raising the change once the list is ready makes it re-select.
    /// </summary>
    public void RaiseInputChannelChanged() => OnPropertyChanged(nameof(InputChannel));

    /// <summary>When true the track will capture audio while recording.</summary>
    public bool IsArmed
    {
        get => _isArmed;
        set => Set(ref _isArmed, value);
    }

    /// <summary>When true the track is silenced during playback.</summary>
    public bool IsMuted
    {
        get => _isMuted;
        set => Set(ref _isMuted, value);
    }

    /// <summary>Playback gain from 0.0 to 1.0.</summary>
    public float Volume
    {
        get => _volume;
        set => Set(ref _volume, value);
    }

    /// <summary>Stereo pan from -1.0 (hard left) through 0.0 (centre) to 1.0 (hard right).</summary>
    public float Pan
    {
        get => _pan;
        set => Set(ref _pan, Math.Clamp(value, -1f, 1f));
    }

    /// <summary>Most recent peak level (0..1) for the meter. Updated by the engine.</summary>
    public float Level
    {
        get => _level;
        set => Set(ref _level, value);
    }

    /// <summary>Formatted length of the recorded/loaded audio (m:ss.d).</summary>
    public string DurationText
    {
        get => _durationText;
        set => Set(ref _durationText, value);
    }

    /// <summary>Path to the WAV file holding this track's recorded audio, if any.</summary>
    public string? RecordedFilePath
    {
        get => _recordedFilePath;
        set
        {
            if (Set(ref _recordedFilePath, value))
            {
                OnPropertyChanged(nameof(HasAudio));
            }
        }
    }

    /// <summary>True once the track has recorded (or loaded) audio.</summary>
    public bool HasAudio => !string.IsNullOrEmpty(_recordedFilePath) && File.Exists(_recordedFilePath);

    /// <summary>Envelope of the recorded audio, drawn on the track's timeline.</summary>
    public WaveformBuffer Waveform { get; } = new();

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
}
