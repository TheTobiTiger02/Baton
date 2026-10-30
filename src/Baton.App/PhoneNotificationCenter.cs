using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Baton.Host;
using Baton.Protocol;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;

namespace Baton.App;

/// <summary>
/// Phones' notifications as Windows notifications: each arrives as the phone posts it and leaves
/// when the phone drops it. Dismissing one here dismisses it on the phone; where the app takes a
/// reply, the notification has a reply box. A one-time code in a notification is copied to the
/// clipboard at once.
/// </summary>
internal sealed class PhoneNotificationCenter : IDisposable
{
    private const string ReplyBox = "reply";

    private readonly BatonHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly Func<string, string> _nameOf;

    /// <summary>Windows tags are short; the phone's keys are not. Tag to (phone, key).</summary>
    private readonly ConcurrentDictionary<string, (string DeviceId, string Key)> _byTag = new(StringComparer.Ordinal);

    public PhoneNotificationCenter(BatonHost host, Dispatcher dispatcher, Func<string, string> nameOf)
    {
        _host = host;
        _dispatcher = dispatcher;
        _nameOf = nameOf;
        host.MessageReceived += OnMessageAsync;
        ToastNotificationManagerCompat.OnActivated += OnActivated;
    }

    /// <summary>Show phones' notifications here.</summary>
    public bool Enabled { get; set; } = true;

    public void Dispose()
    {
        _host.MessageReceived -= OnMessageAsync;
        ToastNotificationManagerCompat.OnActivated -= OnActivated;
    }

    private Task OnMessageAsync(string deviceId, Envelope envelope)
    {
        switch (envelope.Type)
        {
            case MessageTypes.NotificationPosted when Enabled && envelope.Read<NotificationPayload>() is { } notification:
                _dispatcher.BeginInvoke(() => Show(deviceId, notification));
                break;
            case MessageTypes.NotificationRemoved when envelope.Read<NotificationKeyPayload>() is { } removed:
                var tag = Tag(deviceId, removed.Key);
                _byTag.TryRemove(tag, out _);
                Try(() => ToastNotificationManagerCompat.History.Remove(tag, Group(deviceId)));
                break;
        }

        return Task.CompletedTask;
    }

    private void Show(string deviceId, NotificationPayload notification)
    {
        var tag = Tag(deviceId, notification.Key);
        _byTag[tag] = (deviceId, notification.Key);
        var builder = new ToastContentBuilder()
            .AddArgument("tag", tag)
            .AddText(string.IsNullOrWhiteSpace(notification.Title) ? notification.AppName : notification.Title)
            .AddAttributionText($"{notification.AppName} · {_nameOf(deviceId)}");
        if (!string.IsNullOrWhiteSpace(notification.Text))
        {
            builder.AddText(notification.Text);
        }

        if (notification.Code is { } code)
        {
            // The code is what the notification is for: it is ready to paste before anyone looks.
            Try(() => Clipboard.SetText(code));
            builder.AddText($"Code {code} copied");
            _host.Diagnostics.Record(DiagnosticsCategory.Handoff, "notification.code-copied", deviceId: deviceId);
        }

        if (notification.CanReply)
        {
            builder.AddInputTextBox(ReplyBox, placeHolderContent: "Reply")
                .AddButton(new ToastButton().SetContent("Send").AddArgument("action", "reply").SetTextBoxId(ReplyBox));
        }

        Try(() => builder.Show(toast =>
        {
            toast.Tag = tag;
            toast.Group = Group(deviceId);
            toast.Dismissed += (sender, args) =>
            {
                if (args.Reason == ToastDismissalReason.UserCanceled && _byTag.TryRemove(tag, out var target))
                {
                    _ = _host.SendAsync(target.DeviceId, MessageTypes.NotificationDismiss, new NotificationKeyPayload(target.Key));
                }
            };
        }));
    }

    /// <summary>A notification's button or body was clicked (on any thread).</summary>
    private void OnActivated(ToastNotificationActivatedEventArgsCompat activation)
    {
        var arguments = ToastArguments.Parse(activation.Argument);
        if (!arguments.TryGetValue("tag", out var tag) || !_byTag.TryGetValue(tag, out var target))
        {
            return;
        }

        if (arguments.TryGetValue("action", out var action) && action == "reply"
            && activation.UserInput.TryGetValue(ReplyBox, out var text) && text is string reply && !string.IsNullOrWhiteSpace(reply))
        {
            _ = _host.SendAsync(target.DeviceId, MessageTypes.NotificationReply, new NotificationReplyPayload(target.Key, reply));
            _host.Diagnostics.Record(DiagnosticsCategory.Handoff, "notification.replied", deviceId: target.DeviceId);
        }
    }

    private static string Tag(string deviceId, string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{deviceId}|{key}")))[..32];

    private static string Group(string deviceId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId)))[..16];

    /// <summary>Windows can refuse (notifications off for Baton, Focus assist): that loses a pop-up, nothing more.</summary>
    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
        }
    }
}
