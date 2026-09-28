namespace Baton.Protocol;

public static class MessageTypes
{
    // Session: the PC is the server; phones dial in over pinned WSS.
    public const string SessionChallenge = "session.challenge";
    public const string SessionAuthenticate = "session.authenticate";
    public const string SessionReady = "session.ready";
    public const string PairHello = "pair.hello";
    public const string PairConfirm = "pair.confirm";
    public const string HeartbeatPing = "heartbeat.ping";
    public const string HeartbeatPong = "heartbeat.pong";
    public const string HostEndpoints = "host.endpoints";
    public const string DeviceInfo = "device.info";
    public const string DeviceForget = "device.forget";
    public const string DeviceForgotten = "device.forgotten";
    public const string Peers = "peers";
    public const string Error = "error";

    // Handoff.
    public const string ActivityList = "activity.list";
    public const string HandoffPull = "handoff.pull";
    public const string HandoffDeliver = "handoff.deliver";
    public const string HandoffResult = "handoff.result";

    // Streams and remote control.
    public const string MediaChannel = "media.channel";
    public const string StreamControl = "stream.control";
    public const string MediaCommand = "media.command";

    // Choosing the app a handoff continues in.
    public const string HandoffOptions = "handoff.options";
    public const string AppCatalog = "apps.catalog";
    public const string AppPreferences = "apps.preferences";
    public const string AppPreferenceRemove = "apps.preference.remove";
}
