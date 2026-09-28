using System.Diagnostics;
using Microsoft.Win32;

namespace Baton.Host.Apps;

/// <summary>
/// An app a handoff can continue in. <see cref="Id"/> is what launches it: an AUMID, a path or a
/// URI on Windows, a package on Android. <see cref="BrowserExe"/> is set for Windows browsers,
/// which open links handed to them.
/// </summary>
public sealed record CatalogApp(string Id, string Name, string? BrowserExe = null, bool IsBrowser = false);

/// <summary>
/// The apps installed on this PC, as the Start menu lists them (Store apps, desktop apps, and
/// Google Play Games titles), plus the registered web browsers. Read in the background.
/// </summary>
public sealed class PcApps
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(10);
    private readonly DiagnosticsLog _diagnostics;
    private IReadOnlyList<CatalogApp> _apps = [];
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;
    private int _reading;

    public PcApps(DiagnosticsLog diagnostics) => _diagnostics = diagnostics;

    /// <summary>The last catalog read; refreshes itself when old. Never blocks.</summary>
    public IReadOnlyList<CatalogApp> Apps
    {
        get
        {
            if (DateTimeOffset.UtcNow - _readAt > RefreshEvery)
            {
                Refresh();
            }

            return _apps;
        }
    }

    /// <summary>Reads the catalog now, on a background STA thread (the shell requires one).</summary>
    public Task RefreshAsync()
    {
        var done = new TaskCompletionSource();
        if (Interlocked.Exchange(ref _reading, 1) == 1)
        {
            done.SetResult();
            return done.Task;
        }

        var thread = new Thread(() =>
        {
            try
            {
                var started = Stopwatch.StartNew();
                var browsers = Browsers();
                var apps = StartMenuApps()
                    .Where(app => browsers.All(browser => !SameBrowser(browser, app)))
                    .Concat(browsers)
                    .GroupBy(app => app.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
                _apps = apps;
                _readAt = DateTimeOffset.UtcNow;
                _diagnostics.Record(DiagnosticsCategory.Handoff, "apps.read", $"{apps.Length} apps, {browsers.Count} browsers in {started.ElapsedMilliseconds} ms");
            }
            catch (Exception ex)
            {
                _diagnostics.Record(DiagnosticsCategory.Handoff, "apps.read.failed", ex.Message, severity: DiagnosticsSeverity.Warning);
            }
            finally
            {
                Volatile.Write(ref _reading, 0);
                done.TrySetResult();
            }
        }) { IsBackground = true, Name = "Baton app catalog" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private void Refresh() => _ = RefreshAsync();

    private static bool SameBrowser(CatalogApp browser, CatalogApp app) =>
        string.Equals(browser.Name, app.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>shell:AppsFolder, the list behind the Start menu's "All apps".</summary>
    private static List<CatalogApp> StartMenuApps()
    {
        var apps = new List<CatalogApp>();
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null)
        {
            return apps;
        }

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic folder = shell.NameSpace("shell:AppsFolder");
        foreach (dynamic item in folder.Items())
        {
            string name = item.Name;
            string path = item.Path;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path) || IsNoise(name, path))
            {
                continue;
            }

            // Sites installed as apps from a browser ("TikTok" from Edge next to the real TikTok).
            var siteApp = path.Contains("_crx_", StringComparison.Ordinal) || path.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
            apps.Add(new CatalogApp(path, siteApp ? $"{name} (web app)" : name));
        }

        return apps;
    }

    /// <summary>Uninstallers, readmes, help links and the like are not apps to continue in.</summary>
    private static bool IsNoise(string name, string path)
    {
        var lower = name.ToLowerInvariant();
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".txt" or ".url" or ".html" or ".htm" or ".pdf" or ".chm" or ".zip" or ".bat" or ".cmd"
            || lower.Contains("uninstall") || lower.Contains("deinstall") || lower.Contains("readme")
            || lower.Contains("release notes") || lower.Contains("documentation") || lower.Contains("user guide");
    }

    /// <summary>Browsers registered with Windows (the list behind "Default apps").</summary>
    private static List<CatalogApp> Browsers()
    {
        var browsers = new List<CatalogApp>();
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var clients = hive.OpenSubKey(@"SOFTWARE\Clients\StartMenuInternet");
            if (clients is null)
            {
                continue;
            }

            foreach (var name in clients.GetSubKeyNames())
            {
                using var client = clients.OpenSubKey(name);
                var display = client?.GetValue(null) as string;
                var command = client?.OpenSubKey(@"shell\open\command")?.GetValue(null) as string;
                var exe = ExeOf(command);
                if (string.IsNullOrWhiteSpace(display) || exe is null || !File.Exists(exe)
                    || browsers.Any(browser => string.Equals(browser.BrowserExe, exe, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                browsers.Add(new CatalogApp($"browser:{exe}", display, exe, IsBrowser: true));
            }
        }

        return browsers;
    }

    private static string? ExeOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }

        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? command[..(exe + 4)] : command;
    }
}
