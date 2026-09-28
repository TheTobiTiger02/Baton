using System.Text.Json;
using System.Text.Json.Serialization;

namespace Baton.Protocol;

/// <summary>
/// One protocol message. Mirrored by <c>dev.baton.android.protocol.Envelope</c>; field names are
/// the camelCase JSON the web serializer defaults produce.
/// </summary>
public sealed record Envelope
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("v")]
    public int Version { get; init; } = CurrentVersion;

    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageTypes.Error;

    /// <summary>The sender: a phone's device id, or the PC's host id.</summary>
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; init; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; } = JsonSerializer.SerializeToElement(new Dictionary<string, object>());

    public static Envelope Create<TPayload>(string type, string deviceId, TPayload payload) => new()
    {
        Type = type,
        DeviceId = deviceId,
        Payload = JsonSerializer.SerializeToElement(payload, Json.Options)
    };

    public TPayload? Read<TPayload>() => Payload.Deserialize<TPayload>(Json.Options);

    public TPayload ReadRequired<TPayload>() =>
        Read<TPayload>() ?? throw new InvalidOperationException($"Message '{Type}' has no payload.");
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Serialize(Envelope envelope) => JsonSerializer.Serialize(envelope, Options);

    public static Envelope Deserialize(string json) =>
        JsonSerializer.Deserialize<Envelope>(json, Options) ?? throw new JsonException("Envelope was empty.");

    /// <summary>Returns null when the envelope is acceptable, otherwise the reason it is not.</summary>
    public static string? Validate(Envelope envelope)
    {
        if (envelope.Version != Envelope.CurrentVersion)
        {
            return $"Unsupported protocol version {envelope.Version}; expected {Envelope.CurrentVersion}.";
        }

        if (string.IsNullOrWhiteSpace(envelope.Id))
        {
            return "Envelope id is required.";
        }

        return string.IsNullOrWhiteSpace(envelope.Type) ? "Envelope type is required." : null;
    }
}
