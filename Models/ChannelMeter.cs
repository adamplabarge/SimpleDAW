using System.ComponentModel;

namespace SimpleDAW;

/// <summary>Live level for a single hardware input channel, shown in the meter strip.</summary>
public sealed class ChannelMeter : INotifyPropertyChanged
{
    private float _level;

    public ChannelMeter(int index, string label, bool isMain)
    {
        Index = index;
        Label = label;
        IsMain = isMain;
    }

    public int Index { get; }

    public string Label { get; }

    public bool IsMain { get; }

    private bool _isMuted;

    /// <summary>When true this input channel is silenced in the monitor mix (its meter still moves).</summary>
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (_isMuted != value)
            {
                _isMuted = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMuted)));
            }
        }
    }

    public float Level
    {
        get => _level;
        set
        {
            if (Math.Abs(_level - value) > 0.001f)
            {
                _level = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Level)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
