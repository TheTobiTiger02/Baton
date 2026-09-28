using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;
using System.Security.Cryptography;
using Baton.Protocol;

namespace Baton.Host.Media;

/// <summary>
/// Local media files this PC offers to its phones. Each file gets a random token; the phone
/// streams it from <c>https://pc:7838/files/{token}</c> over the pinned connection. Tokens only
/// travel inside authenticated sessions, and a token maps to exactly one file.
/// </summary>
public sealed class LocalFiles
{
    public static readonly string[] MediaExtensions =
    [
        ".mkv", ".mp4", ".m4v", ".mov", ".avi", ".webm", ".ts", ".m2ts", ".wmv", ".flv",
        ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav"
    ];

    /// <summary>Players that take a start position on their command line, by executable name.</summary>
    private static readonly Dictionary<string, Func<string, long, string>> StartArguments = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vlc"] = (file, ms) => $"--start-time={ms / 1000} \"{file}\"",
        ["mpv"] = (file, ms) => $"--start={ms / 1000} \"{file}\"",
        ["mpc-hc64"] = (file, ms) => $"\"{file}\" /start {ms}",
        ["mpc-hc"] = (file, ms) => $"\"{file}\" /start {ms}",
        ["mpc-be64"] = (file, ms) => $"\"{file}\" /start {ms}",
        ["PotPlayerMini64"] = (file, ms) => $"\"{file}\" /seek={ms / 1000}"
    };

    private readonly ConcurrentDictionary<string, string> _byToken = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _playerByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, (DateTime At, string? File)> _commandLineCache = new();

    public static bool IsKnownPlayer(string processName) => StartArguments.ContainsKey(processName);

    /// <summary>The file for a token, when this PC shared it.</summary>
    public string? Resolve(string token) => _byToken.TryGetValue(token, out var path) && File.Exists(path) ? path : null;

    /// <summary>Offers <paramref name="path"/>, remembering which player showed it so it can reopen there.</summary>
    public ActivityFile Share(string path, string? playerPath = null)
    {
        var token = _byPath.GetOrAdd(path, _ =>
        {
            var created = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            _byToken[created] = path;
            return created;
        });
        if (playerPath is not null)
        {
            _playerByPath[path] = playerPath;
        }

        return new ActivityFile(token, Path.GetFileName(path), new FileInfo(path).Length, MimeFor(path));
    }

    /// <summary>
    /// The media file a player process is showing: from its command line (how Explorer opens
    /// files), or else the recently opened file whose name matches the window or session title.
    /// </summary>
    public string? FindPlaying(int processId, string title)
    {
        var fromCommandLine = FileFromCommandLine(processId);
        if (fromCommandLine is not null)
        {
            return fromCommandLine;
        }

        return FileFromRecent(title);
    }

    /// <summary>Opens a file in the player that last showed it, at <paramref name="positionMs"/> when the player takes one.</summary>
    public void Open(string path, long positionMs)
    {
        if (_playerByPath.TryGetValue(path, out var player) && StartArguments.TryGetValue(Path.GetFileNameWithoutExtension(player), out var arguments))
        {
            Process.Start(new ProcessStartInfo(player, arguments(path, positionMs)) { UseShellExecute = false });
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static bool TakesStartPosition(string? playerPath) =>
        playerPath is not null && StartArguments.ContainsKey(Path.GetFileNameWithoutExtension(playerPath));

    public string? PlayerFor(string path) => _playerByPath.TryGetValue(path, out var player) ? player : null;

    public static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mkv" => "video/x-matroska",
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".avi" => "video/x-msvideo",
        ".ts" or ".m2ts" => "video/mp2t",
        ".mp3" => "audio/mpeg",
        ".flac" => "audio/flac",
        ".m4a" or ".aac" => "audio/mp4",
        ".ogg" or ".opus" => "audio/ogg",
        ".wav" => "audio/wav",
        _ => "application/octet-stream"
    };

    private string? FileFromCommandLine(int processId)
    {
        if (_commandLineCache.TryGetValue(processId, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(10))
        {
            return cached.File;
        }

        string? found = null;
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId}");
            foreach (var process in searcher.Get())
            {
                var commandLine = process["CommandLine"] as string ?? string.Empty;
                found = SplitArguments(commandLine).Skip(1).LastOrDefault(IsMediaFile);
            }
        }
        catch (Exception)
        {
            // WMI unavailable; the recent-files fallback still works.
        }

        _commandLineCache[processId] = (DateTime.UtcNow, found);
        return found;
    }

    private static string? FileFromRecent(string title)
    {
        var recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (string.IsNullOrWhiteSpace(title) || !Directory.Exists(recent))
        {
            return null;
        }

        var wanted = Normalize(title);
        foreach (var link in new DirectoryInfo(recent).EnumerateFiles("*.lnk").OrderByDescending(file => file.LastWriteTimeUtc).Take(60))
        {
            var stem = Normalize(Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(link.Name)));
            if (stem.Length < 4 || !(wanted.Contains(stem) || stem.Contains(wanted)))
            {
                continue;
            }

            var target = ShortcutTarget(link.FullName);
            if (target is not null && IsMediaFile(target))
            {
                return target;
            }
        }

        return null;
    }

    private static bool IsMediaFile(string path) =>
        MediaExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) && File.Exists(path);

    private static string Normalize(string value) => new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Splits a Windows command line the way the C runtime does, near enough for file paths.</summary>
    public static IEnumerable<string> SplitArguments(string commandLine)
    {
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var character in commandLine)
        {
            if (character == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static string? ShortcutTarget(string path)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return null;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(path);
            return (string)shortcut.TargetPath;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
