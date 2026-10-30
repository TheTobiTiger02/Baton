using System.Diagnostics;
using System.Runtime.InteropServices;
using Baton.Media;
using Baton.Protocol;

namespace Baton.Host.Streaming;

/// <summary>
/// A phone as this PC's remote: its touchpad and keyboard drive the whole desktop when no window
/// is streamed to it (a streamed window takes the input itself), and <c>pc.control</c> sets the
/// volume, plays and skips media, mutes the microphone, and locks, sleeps or shuts down the PC.
/// Each command is answered with the PC's sound settings, which the remote shows.
/// </summary>
public sealed class PcRemote
{
    private readonly BatonHost _host;

    internal PcRemote(BatonHost host)
    {
        _host = host;
        host.MediaChannels.RecordReceived += OnRecord;
        host.MessageReceived += OnMessageAsync;
    }

    private void OnRecord(string deviceId, StreamRecordHeader header, byte[] payload)
    {
        if (header.Channel != StreamChannel.Control || _host.WindowStreams.IsStreamingTo(deviceId))
        {
            return;
        }

        ControlMessage message;
        try
        {
            message = ControlMessages.Decode(payload);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            return;
        }

        switch (message)
        {
            case MouseMoveMessage move:
                InputInjector.MoveByOnDesktop(move.Dx, move.Dy);
                break;
            case MouseButtonMessage button:
                InputInjector.Button(button.Button, button.Action == KeyAction.Down);
                break;
            case ScrollMessage scroll:
                InputInjector.ScrollAtCursor(scroll.Horizontal, scroll.Vertical);
                break;
            case KeyMessage { Action: KeyAction.Down } key when KeyMapWindows.VirtualKey(key.KeyCode) is var vk and not 0:
                InputInjector.Chord(vk, KeyMapWindows.Modifiers(key.MetaState));
                break;
            case TextMessage text:
                InputInjector.Text(text.Value);
                break;
        }
    }

    private async Task OnMessageAsync(string deviceId, Envelope envelope)
    {
        if (envelope.Type != MessageTypes.PcControl || envelope.Read<PcControlPayload>() is not { } control)
        {
            return;
        }

        switch (control.Action)
        {
            case PcControlActions.Volume when control.Value is { } level:
                AppVolume.SetMasterLevel(level);
                break;
            case PcControlActions.Mute:
                AppVolume.SetMuted(control.Value is > 0);
                break;
            case PcControlActions.MicMute:
                AppVolume.SetMuted(control.Value is > 0, microphone: true);
                break;
            case PcControlActions.PlayPause:
                InputInjector.PressVirtualKey(0xB3);
                break;
            case PcControlActions.Next:
                InputInjector.PressVirtualKey(0xB0);
                break;
            case PcControlActions.Previous:
                InputInjector.PressVirtualKey(0xB1);
                break;
            case PcControlActions.Lock:
                LockWorkStation();
                break;
            case PcControlActions.Sleep:
                // After the answer: the network goes down with the PC.
                _ = Task.Delay(500).ContinueWith(_ => SetSuspendState(false, false, false));
                break;
            case PcControlActions.Keys when control.Text is { } keys && KeyCombo.Parse(keys) is { } combo:
                InputInjector.Chord(combo.Key, combo.Modifiers);
                break;
            case PcControlActions.Open when control.Text is { } address && IsWebAddress(address):
                Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
                break;
            case PcControlActions.Shutdown:
                Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 5 /c \"Shut down from your phone with Baton.\"") { CreateNoWindow = true });
                break;
        }

        if (control.Action != PcControlActions.State)
        {
            _host.Diagnostics.Record(DiagnosticsCategory.Media, "remote.control", control.Action, deviceId);
        }

        await _host.SendAsync(deviceId, MessageTypes.PcState, State());
    }

    /// <summary>Only web pages: a shortcut can't start programs or open files this way.</summary>
    public static bool IsWebAddress(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    public static PcStatePayload State() => new(AppVolume.MasterLevel(), AppVolume.Muted(), AppVolume.Muted(microphone: true));

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    [DllImport("powrprof.dll")]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
}
