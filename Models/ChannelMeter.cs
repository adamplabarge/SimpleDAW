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

    private float _gain = 0.5f;

    /// <summary>
    /// Software input/record gain (0..1) for this hardware channel. Applied to
    /// the captured signal, so it scales both this channel's meter and anything
    /// recorded from it. Does not affect the hardware's own direct monitoring.
    /// </summary>
    public float Gain
    {
        get => _gain;
        set
        {
            float clamped = Math.Clamp(value, 0f, 1f);
            if (Math.Abs(_gain - clamped) > 0.0001f)
            {
                _gain = clamped;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Gain)));
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
