using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SimpleDAW;

/// <summary>
/// Base class implementing the standard <see cref="INotifyPropertyChanged"/>
/// "Set backing field, raise change if it differs" pattern. View models and
/// observable models derive from this instead of each reimplementing the same
/// event/Set/OnPropertyChanged trio.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Sets <paramref name="field"/> to <paramref name="value"/> and raises
    /// <see cref="PropertyChanged"/> if it actually changed. Returns whether it
    /// changed, so callers can chain follow-up work (e.g. raising a dependent
    /// computed property) only when needed.
    /// </summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
