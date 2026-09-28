using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Baton.Host;
using Baton.Host.Streaming;
using Baton.Media;

namespace Baton.App.Mirror;

/// <summary>
/// Keeps the PC's and a phone's clipboards the same while that phone mirrors to the PC or shows a
/// PC window: text copied on either side can be pasted on the other. Only during those sessions,
/// and never what a password manager marks as not to be shared.
/// </summary>
internal sealed class ClipboardBridge : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;

    // Password managers put this format next to secrets so clipboard tools leave them alone.
    private const string ExcludeFormat = "ExcludeClipboardContentFromMonitorProcessing";

    private readonly BatonHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly HwndSource _window;
    private readonly ConcurrentDictionary<string, int> _sessions = new(StringComparer.Ordinal);
    private string? _last;

    public ClipboardBridge(BatonHost host, Dispatcher dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;
        _window = new HwndSource(new HwndSourceParameters("Baton clipboard") { ParentWindow = new IntPtr(-3) });
        _window.AddHook(WndProc);
        AddClipboardFormatListener(_window.Handle);
        host.MediaChannels.RecordReceived += OnRecord;
    }

    /// <summary>A session with a phone began: from now on copies travel, starting with what the PC holds.</summary>
    public void Start(string deviceId) => _dispatcher.BeginInvoke(() =>
    {
        _sessions.AddOrUpdate(deviceId, 1, (_, count) => count + 1);
        if (ReadPc() is { } text)
        {
            Push(deviceId, text);
        }
    });

    public void Stop(string deviceId) => _dispatcher.BeginInvoke(() =>
    {
        if (_sessions.TryGetValue(deviceId, out var count))
        {
            if (count <= 1)
            {
                _sessions.TryRemove(deviceId, out _);
            }
            else
            {
                _sessions[deviceId] = count - 1;
            }
        }
    });

    public void Dispose()
    {
        _host.MediaChannels.RecordReceived -= OnRecord;
        RemoveClipboardFormatListener(_window.Handle);
        _window.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmClipboardUpdate && !_sessions.IsEmpty && ReadPc() is { } text && text != _last)
        {
            _last = text;
            foreach (var deviceId in _sessions.Keys)
            {
                Push(deviceId, text);
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>A phone's copy, on the media channel's reader thread.</summary>
    private void OnRecord(string deviceId, StreamRecordHeader header, byte[] payload)
    {
        if (header.Channel != StreamChannel.Control || payload.Length == 0 || payload[0] != (byte)ControlMessageType.Clipboard
            || !_sessions.ContainsKey(deviceId))
        {
            return;
        }

        string text;
        try
        {
            text = ((ClipboardMessage)ControlMessages.Decode(payload)).Value;
        }
        catch (Exception ex) when (ex is System.IO.InvalidDataException or ArgumentException or InvalidCastException)
        {
            return;
        }

        _dispatcher.BeginInvoke(() =>
        {
            // Remembered first: writing it raises a clipboard update that must not echo back.
            _last = text;
            Retry(() => Clipboard.SetDataObject(text, copy: true));
            _host.Diagnostics.Record(DiagnosticsCategory.Stream, "clipboard.from-phone", $"{text.Length} characters", deviceId);
        });
    }

    private void Push(string deviceId, string text)
    {
        if (ControlMessages.FitsText(text))
        {
            _host.MediaChannels.Send(deviceId, StreamChannel.Control, 0, 0, ControlMessages.Encode(new ClipboardMessage(text)));
        }
    }

    private static string? ReadPc()
    {
        string? text = null;
        Retry(() =>
        {
            var data = Clipboard.GetDataObject();
            text = data is not null && !data.GetDataPresent(ExcludeFormat) && data.GetDataPresent(DataFormats.UnicodeText)
                ? data.GetData(DataFormats.UnicodeText) as string
                : null;
        });
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>Another app may hold the clipboard open for a moment.</summary>
    private static void Retry(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (COMException) when (attempt < 5)
            {
                Thread.Sleep(20);
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
