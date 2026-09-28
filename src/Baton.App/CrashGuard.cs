using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace Baton.App;

/// <summary>
/// Keeps one bad value or failed task from taking the whole host down (and every phone's
/// connection with it). Errors are logged to crash.log; a crash that still gets through makes
/// Windows restart Baton in the background.
/// </summary>
internal static class CrashGuard
{
    private const int RestartNoReboot = 8;
    private const long MaxLogBytes = 256 * 1024;
    private static readonly object LogLock = new();

    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Baton", "crash.log");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? commandLine, int flags);

    public static void Install(Application app)
    {
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log("UI", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("Fatal", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("Task", e.Exception);
            e.SetObserved();
        };

        // Windows relaunches a process that crashed after running at least 60 seconds.
        RegisterApplicationRestart("--background", RestartNoReboot);
    }

    private static void Log(string kind, Exception? exception)
    {
        try
        {
            lock (LogLock)
            {
                var path = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > MaxLogBytes)
                {
                    File.Delete(path);
                }

                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {kind}: {exception}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never be the next crash.
        }
    }
}
