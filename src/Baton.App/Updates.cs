using Velopack;
using Velopack.Sources;

namespace Baton.App;

/// <summary>
/// Updates from the GitHub releases of Baton's repository: checked in the background, downloaded
/// quietly, applied on "Restart to update". Only for a copy installed by Setup.exe; a development
/// build or scripts\Install.ps1 copy never updates itself.
/// </summary>
internal sealed class Updates
{
    public const string Repository = "https://github.com/TheTobiTiger02/Baton";

    /// <summary>Velopack's package id, and the folder under %LOCALAPPDATA% it installs to. Not "Baton": that folder holds the pairings.</summary>
    public const string PackageId = "Baton.App";

    private readonly UpdateManager _manager = new(new GithubSource(Repository, accessToken: null, prerelease: false));
    private UpdateInfo? _ready;

    public bool IsInstalled => _manager.IsInstalled;

    public string CurrentVersion => _manager.CurrentVersion?.ToString() ?? typeof(Updates).Assembly.GetName().Version?.ToString(3) ?? "dev";

    /// <summary>The version downloaded and waiting for a restart, if any.</summary>
    public string? ReadyVersion => _ready?.TargetFullRelease.Version.ToString();

    /// <summary>Looks for a newer release and downloads it. Returns its version when one is ready.</summary>
    public async Task<string?> CheckAsync()
    {
        if (!IsInstalled)
        {
            return null;
        }

        if (_ready is null && await _manager.CheckForUpdatesAsync() is { } update)
        {
            await _manager.DownloadUpdatesAsync(update);
            _ready = update;
        }

        return ReadyVersion;
    }

    /// <summary>Installs the downloaded version and starts it again, in the background as after sign-in.</summary>
    public void RestartToUpdate()
    {
        if (_ready is { } ready)
        {
            _manager.ApplyUpdatesAndRestart(ready.TargetFullRelease, ["--background"]);
        }
    }

    /// <summary>Velopack's install and uninstall hooks; must run before anything else at startup.</summary>
    public static void RunHooks() =>
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => StartupRegistration.SetEnabled(false, rememberUserChoice: false))
            .Run();
}
