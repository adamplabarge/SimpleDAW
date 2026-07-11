using System.Diagnostics;
using System.Threading;
using NAudio.Midi;

namespace SimpleDAW;

/// <summary>
/// Generates a MIDI clock on a selected MIDI output device. When started it
/// sends a MIDI Start (0xFA) message followed by 24 MIDI Timing Clock (0xF8)
/// pulses per quarter note at the configured tempo, and a Stop (0xFC) when
/// stopped. A dedicated high-priority thread with a Stopwatch is used so the
/// clock stays tight independent of the UI and audio threads.
/// </summary>
public sealed class MidiClock : IDisposable
{
    private const int ClockMessage = 0xF8;    // Timing clock (24 per quarter note)
    private const int StartMessage = 0xFA;    // Start
    private const int StopMessage = 0xFC;     // Stop

    private const int PulsesPerQuarterNote = 24;

    private MidiOut? _midiOut;
    private Thread? _thread;
    private volatile bool _running;
    private double _bpm = 120.0;
    private readonly object _sync = new();
    private long _pulseCount;
    private string? _lastError;
    private string _activeDeviceName = string.Empty;

    /// <summary>Gets the names of the available MIDI output devices.</summary>
    public static IReadOnlyList<string> GetOutputDeviceNames()
    {
        var names = new List<string>();
        for (int i = 0; i < MidiOut.NumberOfDevices; i++)
        {
            names.Add(MidiOut.DeviceInfo(i).ProductName);
        }

        return names;
    }

    /// <summary>Tempo in beats per minute. May be changed while running.</summary>
    public double Bpm
    {
        get => _bpm;
        set => _bpm = Math.Clamp(value, 20.0, 300.0);
    }

    public bool IsRunning => _running;
    public long PulseCount => Interlocked.Read(ref _pulseCount);
    public string? LastError => _lastError;
    public string ActiveDeviceName => _activeDeviceName;

    /// <summary>
    /// Opens the given MIDI output device (by index) and starts sending clock.
    /// A negative index means "no MIDI output" and the call is a no-op.
    /// </summary>
    public bool Start(int deviceIndex)
    {
        lock (_sync)
        {
            Stop();

            if (deviceIndex < 0 || deviceIndex >= MidiOut.NumberOfDevices)
            {
                _lastError = "No MIDI output selected.";
                return false;
            }

            _lastError = null;
            _activeDeviceName = MidiOut.DeviceInfo(deviceIndex).ProductName;
            Interlocked.Exchange(ref _pulseCount, 0);

            try
            {
                _midiOut = new MidiOut(deviceIndex);
            }
            catch (Exception ex)
            {
                _lastError = $"Failed to open MIDI output: {ex.Message}";
                _activeDeviceName = string.Empty;
                return false;
            }

            _running = true;
            _thread = new Thread(RunLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest,
                Name = "MidiClock",
            };
            _thread.Start();
            return true;
        }
    }

    /// <summary>Stops the clock and sends a MIDI Stop message.</summary>
    public void Stop()
    {
        lock (_sync)
        {
            if (!_running && _midiOut == null)
            {
                return;
            }

            _running = false;
            _thread?.Join(500);
            _thread = null;

            if (_midiOut != null)
            {
                try
                {
                    _midiOut.Send(StopMessage);
                }
                catch
                {
                    // Ignore send failures during shutdown.
                }

                _midiOut.Dispose();
                _midiOut = null;
            }

            _activeDeviceName = string.Empty;
        }
    }

    private void RunLoop()
    {
        var midiOut = _midiOut;
        if (midiOut == null)
        {
            return;
        }

        try
        {
            midiOut.Send(StartMessage);
        }
        catch (Exception ex)
        {
            _lastError = $"Failed to send MIDI Start: {ex.Message}";
            _running = false;
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        double nextPulseMs = 0.0;

        while (_running)
        {
            double intervalMs = 60000.0 / _bpm / PulsesPerQuarterNote;
            nextPulseMs += intervalMs;

            // Sleep for the bulk of the interval, then spin for accuracy.
            while (_running)
            {
                double remaining = nextPulseMs - stopwatch.Elapsed.TotalMilliseconds;
                if (remaining <= 0)
                {
                    break;
                }

                if (remaining > 2.0)
                {
                    Thread.Sleep(1);
                }
                else
                {
                    Thread.SpinWait(50);
                }
            }

            if (!_running)
            {
                break;
            }

            try
            {
                midiOut.Send(ClockMessage);
                Interlocked.Increment(ref _pulseCount);
            }
            catch (Exception ex)
            {
                _lastError = $"Failed to send MIDI Clock: {ex.Message}";
                _running = false;
                break;
            }

            // Guard against drift if the tempo change made us fall far behind.
            double drift = stopwatch.Elapsed.TotalMilliseconds - nextPulseMs;
            if (drift > 100.0)
            {
                nextPulseMs = stopwatch.Elapsed.TotalMilliseconds;
            }
        }
    }

    public void Dispose() => Stop();
}
