using System.Collections.Concurrent;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using System.Text.Json;
using Baton.Protocol;

namespace Baton.Host.Apps;

/// <summary>
/// Which apps each device has, how an activity maps onto them, and what the user chose before.
/// Choices are remembered per source app and direction (<see cref="ChoiceKeys"/>) and live here on
/// the PC, the hub every handoff passes through; phones get a copy so they never have to ask.
/// </summary>
public sealed class AppDirectory
{
    private static readonly JsonSerializerOptions FileOptions = new(Json.Options) { WriteIndented = true };
    private readonly ConcurrentDictionary<string, IReadOnlyList<CatalogApp>> _phoneApps = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly string? _path;
    private readonly DiagnosticsLog _diagnostics;
    private Dictionary<string, AppPreference> _preferences = new(StringComparer.Ordinal);

    public AppDirectory(string? storageDirectory, DiagnosticsLog diagnostics)
    {
        _diagnostics = diagnostics;
        Pc = new PcApps(diagnostics);
        _path = storageDirectory is null ? null : Path.Combine(storageDirectory, "app-preferences.json");
        Load();
    }

    public PcApps Pc { get; }

    /// <summary>Continues media in the tab or app on this PC that still has it; set by the runtime.</summary>
    public Func<Activity, long, Task<bool>>? ResumeExisting { get; set; }

    /// <summary>A remembered choice was added or removed. Any thread.</summary>
    public event Action? PreferencesChanged;

    public IReadOnlyList<AppPreference> Preferences
    {
        get
        {
            lock (_gate)
            {
                return _preferences.Values.OrderBy(preference => preference.SourceName, StringComparer.CurrentCultureIgnoreCase).ToArray();
            }
        }
    }

    public void SetPhoneApps(string deviceId, IReadOnlyList<InstalledApp> apps) =>
        _phoneApps[deviceId] = apps.Select(app => new CatalogApp(app.Id, app.Name, IsBrowser: IsAndroidBrowser(app.Id))).ToArray();

    public IReadOnlyList<CatalogApp> AppsOf(string platform, string deviceId) =>
        platform == Platforms.Windows ? Pc.Apps : _phoneApps.GetValueOrDefault(deviceId) ?? [];

    /// <summary>How <paramref name="activity"/> from one platform can continue on a device of another.</summary>
    public IReadOnlyList<HandoffChoice> Options(Activity activity, string sourcePlatform, string targetPlatform, string targetDeviceId) =>
        AppMatcher.Options(activity, sourcePlatform, targetPlatform, AppsOf(targetPlatform, targetDeviceId),
            canStream: sourcePlatform == Platforms.Android || activity.Window is not null);

    public HandoffChoice? Remembered(Activity activity, string sourcePlatform, string targetPlatform)
    {
        lock (_gate)
        {
            return _preferences.GetValueOrDefault(ChoiceKeys.For(sourcePlatform, activity, targetPlatform))?.Choice;
        }
    }

    /// <summary>The remembered choice, or the best option (null when there is nothing but the usual way).</summary>
    public HandoffChoice? Resolve(Activity activity, string sourcePlatform, string targetPlatform, string targetDeviceId) =>
        Remembered(activity, sourcePlatform, targetPlatform)
        ?? Options(activity, sourcePlatform, targetPlatform, targetDeviceId).FirstOrDefault();

    public void Remember(Activity activity, string sourcePlatform, string targetPlatform, HandoffChoice choice)
    {
        var key = ChoiceKeys.For(sourcePlatform, activity, targetPlatform);
        lock (_gate)
        {
            if (_preferences.TryGetValue(key, out var existing) && existing.Choice == (choice with { Remember = false }))
            {
                return;
            }

            _preferences[key] = new AppPreference(key, SourceName(activity), choice with { Remember = false });
            Save();
        }

        _diagnostics.Record(DiagnosticsCategory.Handoff, "choice.remembered", $"{key} = {choice.Kind} {choice.Label}");
        PreferencesChanged?.Invoke();
    }

    public void Forget(string key)
    {
        lock (_gate)
        {
            if (!_preferences.Remove(key))
            {
                return;
            }

            Save();
        }

        PreferencesChanged?.Invoke();
    }

    /// <summary>"YouTube" or "x.com" for a site, the app's name otherwise.</summary>
    public static string SourceName(Activity activity)
    {
        var subject = ChoiceKeys.Subject(activity);
        if (!subject.StartsWith("site:", StringComparison.Ordinal))
        {
            return activity.App.Name;
        }

        var site = subject["site:".Length..];
        return Media.KnownApps.FromProvider(site)?.DisplayName ?? (site == "web" ? "Other web pages" : site);
    }

    /// <summary>Opens an app or website choice on this PC, handing it the activity's link when it can take one.</summary>
    public HandoffStatus Open(Activity activity, HandoffChoice choice, out string? detail)
    {
        detail = null;
        var url = activity.Url;
        switch (choice.Kind)
        {
            case ChoiceKinds.Web:
                Shell(choice.Url ?? url ?? throw new InvalidOperationException("The choice has no address."));
                return HandoffStatus.Opened;

            case ChoiceKinds.App when choice.AppId is { } id:
            {
                var app = Pc.Apps.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
                if (app?.BrowserExe is { } browser || id.StartsWith("browser:", StringComparison.Ordinal))
                {
                    var exe = app?.BrowserExe ?? id["browser:".Length..];
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, ArgumentList = { url ?? "about:blank" } });
                    return HandoffStatus.Opened;
                }

                // An app that takes links of its own kind (tg:, spotify:) gets the link itself.
                var known = Media.KnownApps.CounterpartOf(Platforms.Windows, id);
                if (url is not null && known?.UriScheme is { } scheme && url.StartsWith(scheme + ":", StringComparison.OrdinalIgnoreCase))
                {
                    Shell(url);
                    return HandoffStatus.Opened;
                }

                Launch(id);
                if (url is not null)
                {
                    detail = $"Opened {choice.Label}; it can't be given the page itself.";
                    return HandoffStatus.Fallback;
                }

                return HandoffStatus.Opened;
            }

            default:
                throw new InvalidOperationException($"'{choice.Kind}' is not an app or website.");
        }
    }

    /// <summary>Starts a Start-menu entry: an AUMID, a program path, or a launch URI (Google Play Games).</summary>
    public static void Launch(string id)
    {
        if (id.Contains("://", StringComparison.Ordinal) || Path.IsPathRooted(id) || id.Contains(":\\", StringComparison.Ordinal))
        {
            Shell(id);
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{id}") { UseShellExecute = false });
    }

    private static void Shell(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    private static bool IsAndroidBrowser(string package) => package is
        "com.android.chrome" or "com.sec.android.app.sbrowser" or "org.mozilla.firefox" or "com.microsoft.emmx"
        or "com.opera.browser" or "com.opera.gx" or "com.brave.browser" or "com.vivaldi.browser" or "com.chrome.beta";

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            var list = JsonSerializer.Deserialize<AppPreference[]>(File.ReadAllText(_path), FileOptions) ?? [];
            // Choices once remembered per browser ("everything in Zen") are meaningless now that
            // pages are remembered per site: drop them so the next handoff asks again.
            _preferences = list
                .Where(preference => !IsBrowserKey(preference))
                .ToDictionary(preference => preference.Key, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _diagnostics.Record(DiagnosticsCategory.Handoff, "choices.load.failed", ex.Message, severity: DiagnosticsSeverity.Warning);
        }
    }

    private static bool IsBrowserKey(AppPreference preference)
    {
        var source = preference.Key.Split("->")[0];
        var colon = source.IndexOf(':');
        if (colon < 0 || source[(colon + 1)..].StartsWith("site:", StringComparison.Ordinal))
        {
            return false;
        }

        var platform = source[..colon];
        var id = source[(colon + 1)..];
        return Media.KnownApps.CounterpartOf(platform, id)?.IsBrowser == true
            || Media.KnownApps.All.Any(app => app.IsBrowser && string.Equals(app.DisplayName, preference.SourceName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Written to a temporary file first, so a crash mid-write never loses the list.</summary>
    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_preferences.Values.ToArray(), FileOptions));
        File.Move(temp, _path, overwrite: true);
    }
}
