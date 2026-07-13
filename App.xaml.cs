using System.Configuration;
using System.Data;
using System.Threading;
using System.Windows;

namespace SimpleDAW;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    // Named so it's unique to this app across the machine. Only one instance
    // may run at a time because the app needs exclusive access to the ASIO
    // driver; a second instance left running (e.g. after closing the window
    // but before the process fully exits) previously caused every subsequent
    // launch to fail to open the audio device with ASE_NotPresent.
    private const string SingleInstanceMutexName = "SimpleDAW-SingleInstance-Mutex";

    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "SimpleDAW is already running. Only one instance can run at a time because it needs exclusive access to the audio device.",
                "SimpleDAW",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstanceMutex != null)
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
    }
}

