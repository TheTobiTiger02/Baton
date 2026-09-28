using System.Security.Principal;
using System.Windows;
using Microsoft.Win32;

namespace Baton.App;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;
    private AppServices? _services;

    public App()
    {
        // Setup.exe and the uninstaller start Baton with hook arguments; those exit here.
        Updates.RunHooks();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        var startHidden = e.Args.Any(argument => string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase));
        var user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        _instanceMutex = new Mutex(false, $@"Local\Baton.App.{user}");
        try
        {
            _ownsInstanceMutex = _instanceMutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            _ownsInstanceMutex = true;
        }

        if (!_ownsInstanceMutex)
        {
            // A second launch means "show me Baton": ask the running copy to open its window.
            SingleInstance.SignalShow();
            Shutdown();
            return;
        }

        CrashGuard.Install(this);
        StartupRegistration.Repair();
        ThemeManager.ApplySystemTheme(Resources);
        SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.BeginInvoke(() => ThemeManager.ApplySystemTheme(Resources));
        base.OnStartup(e);

        _services = new AppServices(this);
        try
        {
            await _services.StartAsync(showWindow: !startHidden);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Baton could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        if (_ownsInstanceMutex)
        {
            _instanceMutex?.ReleaseMutex();
        }

        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
