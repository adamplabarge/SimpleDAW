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
    public bool MonitorEnabled { get; set; } = true;
    public bool SendMidiClock { get; set; } = true;
    public List<ProjectTrack> Tracks { get; set; } = new();
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
