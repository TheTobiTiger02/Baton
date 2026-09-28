using Baton.Host.Handoff;
using Baton.Protocol;

namespace Baton.Host.Media;

/// <summary>
/// Brings a local file back to this PC after it was watched on a phone: reopens it in the player
/// that showed it, at the phone's position.
/// </summary>
public sealed class LocalMediaOpener(LocalFiles files, MediaSessionSource sessions, DiagnosticsLog diagnostics) : IActivityOpener, IPositionReconciler
{
    public Task<bool> ReconcileAsync(Activity activity, long positionMs, CancellationToken cancellationToken)
    {
        if (activity.Kind != ActivityKind.LocalMedia || activity.File is not { } file || files.Resolve(file.FileId) is not { } path)
        {
            return Task.FromResult(false);
        }

        return sessions.ReconcileAsync(candidate => MediaOpener.TitlesMatch(candidate.Title, Path.GetFileNameWithoutExtension(path))
            || MediaOpener.TitlesMatch(candidate.Title, activity.Title), positionMs, play: true, TimeSpan.FromSeconds(15), toleranceMs: 1_500);
    }

    public Task<OpenResult?> TryOpenAsync(Activity activity, CancellationToken cancellationToken)
    {
        if (activity.Kind != ActivityKind.LocalMedia || activity.File is not { } file)
        {
            return Task.FromResult<OpenResult?>(null);
        }

        if (files.Resolve(file.FileId) is not { } path)
        {
            return Task.FromResult<OpenResult?>(OpenResult.Failed($"{file.Name} isn't on this PC."));
        }

        var positionMs = activity.Playback?.PositionAt(DateTimeOffset.UtcNow) ?? 0;
        diagnostics.Record(DiagnosticsCategory.Handoff, "open.file", $"{path} at {positionMs} ms");
        files.Open(path, positionMs);
        if (!LocalFiles.TakesStartPosition(files.PlayerFor(path)) && positionMs > 0)
        {
            // Players opened through their file association take no start time; move their media
            // session there once it appears.
            _ = sessions.ReconcileAsync(candidate => MediaOpener.TitlesMatch(candidate.Title, Path.GetFileNameWithoutExtension(path))
                || MediaOpener.TitlesMatch(candidate.Title, activity.Title), positionMs, play: true, TimeSpan.FromSeconds(20));
        }

        return Task.FromResult<OpenResult?>(OpenResult.Opened());
    }
}
