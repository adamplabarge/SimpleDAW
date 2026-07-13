namespace SimpleDAW;

/// <summary>Serializable snapshot of a project, saved as a .srproj JSON file.</summary>
public sealed class ProjectFile
{
    public int Version { get; set; } = 1;
    public string? AudioDevice { get; set; }
    public string? AudioOutput { get; set; }
    public int SampleRate { get; set; } = 48000;
    public string? MidiDevice { get; set; }
    public double Tempo { get; set; } = 120.0;
    public bool SendMidiClock { get; set; } = true;
    public int PreRollBeats { get; set; } = 1;
    public bool ClickEnabled { get; set; }
    public ClickAccent ClickAccent { get; set; } = ClickAccent.Beat1;
    public double ClickVolume { get; set; } = 0.5;
    public double PixelsPerSecond { get; set; } = 80.0;
    public List<ProjectTrack> Tracks { get; set; } = new();
    public List<ProjectInputChannel> InputChannels { get; set; } = new();
}

/// <summary>Serializable state for a single track within a <see cref="ProjectFile"/>.</summary>
public sealed class ProjectTrack
{
    public string Name { get; set; } = "Track";
    public int InputChannel { get; set; }
    public bool IsArmed { get; set; }
    public bool IsMuted { get; set; }
    public float Volume { get; set; } = 1.0f;
    public float Pan { get; set; }
    public string? RecordedFilePath { get; set; }
}

/// <summary>
/// Serializable per-hardware-input-channel settings within a <see cref="ProjectFile"/>
/// (matched back up to a live device's channels by <see cref="Index"/> on load).
/// </summary>
public sealed class ProjectInputChannel
{
    public int Index { get; set; }
    public float Gain { get; set; } = 0.5f;
    public bool IsMuted { get; set; }
}
