using System.Collections.Concurrent;
using System.Security.Cryptography;
using Baton.Protocol;
using Microsoft.AspNetCore.Http;

namespace Baton.Host.Media;

/// <summary>
/// Files sent between this PC and its phones.
/// <para>
/// PC to phone: each file gets a <see cref="LocalFiles"/> token and the phone fetches it from
/// <c>/files/{token}</c>. Phone to PC: the phone offers a file, this PC answers with a single-use
/// token, and the phone uploads to <c>/upload/{token}</c>, both over the pinned TLS port. Tokens
/// only travel inside authenticated sessions.
/// </para>
/// Received files land in Downloads\Baton, marked as downloaded from the internet, so Windows
/// treats them as it treats a browser download.
/// </summary>
public sealed class FileTransfers
{
    private static readonly TimeSpan UploadWindow = TimeSpan.FromMinutes(10);
    private readonly BatonHost _host;
    private readonly ConcurrentDictionary<string, PendingUpload> _uploads = new(StringComparer.Ordinal);

    internal FileTransfers(BatonHost host)
    {
        _host = host;
        host.MessageReceived += OnMessageAsync;
    }

    /// <summary>Where files from phones are saved.</summary>
    public string ReceiveFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Baton");

    /// <summary>A phone's file was saved here: the phone, and the file's path.</summary>
    public event Action<string, string>? Received;

    /// <summary>Offers files to a phone; it downloads and opens them. Returns how many were offered.</summary>
    public async Task<int> SendAsync(string deviceId, IEnumerable<string> paths)
    {
        var sent = 0;
        foreach (var path in paths.Where(File.Exists))
        {
            var file = _host.Files.Share(path);
            if (await _host.SendAsync(deviceId, MessageTypes.FileOffer,
                    new FileOfferPayload(Guid.NewGuid().ToString("N"), file.Name, file.Size, file.Mime, file.FileId)))
            {
                sent++;
                _host.Diagnostics.Record(DiagnosticsCategory.Handoff, "file.sent", $"{file.Size} bytes", deviceId);
            }
        }

        return sent;
    }

    /// <summary>The body of <c>PUT /upload/{token}</c>: one offered file, saved under a name nothing else has.</summary>
    internal async Task<IResult> UploadAsync(HttpContext context, string token)
    {
        if (!_uploads.TryRemove(token, out var upload) || upload.ExpiresAt < DateTimeOffset.UtcNow)
        {
            _host.Diagnostics.Record(DiagnosticsCategory.Handoff, "file.upload.refused", "unknown or expired token", severity: DiagnosticsSeverity.Warning);
            return Results.NotFound();
        }

        if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = upload.Size;
        }

        Directory.CreateDirectory(ReceiveFolder);
        var path = UniquePath(ReceiveFolder, SafeName(upload.Name));
        try
        {
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await context.Request.Body.CopyToAsync(file, context.RequestAborted);
            }

            if (new FileInfo(path).Length != upload.Size)
            {
                throw new IOException($"Received {new FileInfo(path).Length} of {upload.Size} bytes.");
            }

            MarkAsDownloaded(path);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or BadHttpRequestException)
        {
            File.Delete(path);
            _host.Diagnostics.Record(DiagnosticsCategory.Handoff, "file.receive.failed", ex.Message, upload.DeviceId, DiagnosticsSeverity.Warning);
            await _host.SendAsync(upload.DeviceId, MessageTypes.FileResult, new FileResultPayload(upload.TransferId, false, "The upload didn't finish."));
            return Results.BadRequest();
        }

        _host.Diagnostics.Record(DiagnosticsCategory.Handoff, "file.received", $"{upload.Size} bytes", upload.DeviceId);
        await _host.SendAsync(upload.DeviceId, MessageTypes.FileResult, new FileResultPayload(upload.TransferId, true, Path.GetFileName(path)));
        Received?.Invoke(upload.DeviceId, path);
        return Results.Ok();
    }

    private async Task OnMessageAsync(string deviceId, Envelope envelope)
    {
        if (envelope.Type != MessageTypes.FileOffer || envelope.Read<FileOfferPayload>() is not { Token: null } offer)
        {
            return;
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        _uploads[token] = new PendingUpload(deviceId, offer.TransferId, offer.Name, Math.Max(0, offer.Size), DateTimeOffset.UtcNow.Add(UploadWindow));
        foreach (var (stale, _) in _uploads.Where(pair => pair.Value.ExpiresAt < DateTimeOffset.UtcNow))
        {
            _uploads.TryRemove(stale, out _);
        }

        var asked = await _host.SendAsync(deviceId, MessageTypes.FileUpload, new FileUploadPayload(offer.TransferId, token));
        _host.Diagnostics.Record(DiagnosticsCategory.Handoff, "file.offered", $"{offer.Size} bytes, upload {(asked ? "requested" : "not requested")}", deviceId);
    }

    /// <summary>The name's last part, without characters Windows refuses.</summary>
    public static string SafeName(string name)
    {
        // Not Path.GetFileName: it would read "a:b.txt" as a drive and drop "a".
        var bare = name.Replace('\\', '/').Split('/').Last();
        var cleaned = new string(bare.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        return cleaned.Length == 0 ? "file" : cleaned;
    }

    /// <summary>"photo.jpg", or "photo (2).jpg" when that exists already.</summary>
    public static string UniquePath(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var number = 2; File.Exists(path); number++)
        {
            path = Path.Combine(folder, $"{stem} ({number}){extension}");
        }

        return path;
    }

    /// <summary>The Mark of the Web: Windows asks before running it, as for a browser download.</summary>
    private static void MarkAsDownloaded(string path)
    {
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        }
        catch (IOException)
        {
            // Not NTFS (a FAT drive): there is nowhere to put the mark.
        }
    }

    private sealed record PendingUpload(string DeviceId, string TransferId, string Name, long Size, DateTimeOffset ExpiresAt);
}
