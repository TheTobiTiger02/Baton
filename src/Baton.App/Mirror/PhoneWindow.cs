using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Baton.Host;
using Baton.Host.Streaming;
using Baton.Media;
using Baton.Media.Playback;
using Baton.Protocol;
using Vortice.MediaFoundation;
using TouchAction = Baton.Media.TouchAction;

namespace Baton.App.Mirror;

/// <summary>
/// A phone app continued on this PC: the phone's screen, live, driven with the mouse and keyboard.
/// A click is a tap, dragging swipes, the wheel scrolls, typing types into the phone's focused
/// field, right click or Esc is Back.
///
/// Video, sound and input share the phone's always-open media channel, so the window opens at once
/// and shows the picture as soon as the phone has asked for (and been given) screen-capture consent.
/// </summary>
internal sealed class PhoneWindow : Window
{
    private readonly BatonHost _host;
    private readonly string _deviceId;
    private readonly string _sessionId;
    private readonly MirrorSurfaceHost _surface = new();
    private readonly TextBlock _status = new() { Foreground = Brushes.Gainsboro, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private D3D11Context? _context;
    private NV12Presenter? _presenter;
    private VideoDecoder? _decoder;
    private AudioStreamPlayer? _audio;
    private byte[]? _codecConfig;
    private int _videoWidth, _videoHeight;
    private bool _firstFrame;
    private bool? _inputReady;
    private long _inputsSent;
    private bool _pointerDown;
    private bool _closedByPhone;

    /// <summary>The phone's mirror session is running and listens for input (and the clipboard).</summary>
    public event Action? PhoneReady;

    public PhoneWindow(BatonHost host, string deviceId, string phoneName, Activity activity, StreamOffer offer)
    {
        _host = host;
        _deviceId = deviceId;
        _sessionId = offer.SessionId;
        _videoWidth = offer.Width;
        _videoHeight = offer.Height;
        Title = $"{activity.Title} — {phoneName}";
        Background = new SolidColorBrush(Color.FromRgb(18, 19, 24));
        SizeToPhone(offer.Width, offer.Height);

        var bar = new DockPanel { Height = 44, Background = new SolidColorBrush(Color.FromRgb(28, 29, 36)), LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(BarButton("◀", "Back (right click, Esc)", () => Send(new NavigateMessage(NavigationTarget.Back))));
        buttons.Children.Add(BarButton("●", "Home", () => Send(new NavigateMessage(NavigationTarget.Home))));
        buttons.Children.Add(BarButton("▮▮", "Recent apps", () => Send(new NavigateMessage(NavigationTarget.Recents))));
        buttons.Children.Add(BarButton("Continue on phone", "Close this window and keep going on the phone", Close));
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        bar.Children.Add(_status);
        _status.Text = $"Waiting for {phoneName}… (allow screen sharing on the phone)";

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(_surface);
        Content = root;

        _surface.SurfaceCreated += OnSurfaceCreated;
        _surface.SurfaceResized += (width, height) => { lock (_gate) { _presenter?.Resize(width, height); } };
        _surface.PointerInput += OnPointer;
        _surface.WheelInput += OnWheel;
        _surface.KeyInput += OnKey;
        _surface.CharacterInput += character => Send(new TextMessage(character.ToString()));
        _host.MediaChannels.RecordReceived += OnRecord;
        _host.MediaChannels.ChannelChanged += OnChannelChanged;
        Closed += (_, _) => Shutdown();

        // Typing goes to the phone as soon as the window is in front, without clicking into it first.
        Activated += (_, _) => FocusSurface();
    }

    private void FocusSurface()
    {
        if (_surface.SurfaceHandle != nint.Zero)
        {
            SetFocus(_surface.SurfaceHandle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SetFocus(nint hwnd);

    private void ShowStatus()
    {
        _status.Foreground = _inputReady == false ? Brushes.Orange : Brushes.Gainsboro;
        _status.Text = _inputReady == false
            ? "View only: turn on Baton in the phone's Accessibility settings to control it"
            : _firstFrame ? "Live · click to tap, drag to swipe, hold for long press, right click for Back" : _status.Text;
    }

    /// <summary>The phone ended the mirror (or the user stopped it there).</summary>
    public void EndFromPhone()
    {
        _closedByPhone = true;
        Close();
    }

    public string SessionId => _sessionId;

    private void SizeToPhone(int width, int height)
    {
        var work = SystemParameters.WorkArea;
        var aspect = width > 0 && height > 0 ? (double)width / height : 9.0 / 20;
        var videoHeight = work.Height * 0.85 - 44;
        var videoWidth = videoHeight * aspect;
        if (videoWidth > work.Width * 0.9)
        {
            videoWidth = work.Width * 0.9;
            videoHeight = videoWidth / aspect;
        }

        Width = videoWidth + 16;
        Height = videoHeight + 44 + 39;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    private static Button BarButton(string label, string tip, Action onClick)
    {
        var button = new Button
        {
            Content = label,
            ToolTip = tip,
            Margin = new Thickness(4, 6, 4, 6),
            Padding = new Thickness(12, 0, 12, 0),
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void OnSurfaceCreated(nint handle, int width, int height)
    {
        lock (_gate)
        {
            _context = D3D11Context.Create();
            _presenter = NV12Presenter.Create(_context, handle, width, height);
            _presenter.SetVideoSize(_videoWidth, _videoHeight);
        }

        // Anything that arrived before the surface existed was dropped: start from a fresh keyframe.
        Send(new KeyframeRequestMessage());
    }

    /// <summary>Records from the phone, on the media channel's reader thread.</summary>
    private void OnRecord(string deviceId, StreamRecordHeader header, byte[] payload)
    {
        if (deviceId != _deviceId || _stop.IsCancellationRequested)
        {
            return;
        }

        try
        {
            switch (header.Channel)
            {
                case StreamChannel.Video:
                    OnVideo(header, payload);
                    break;
                case StreamChannel.Audio:
                    (_audio ??= new AudioStreamPlayer()).Submit(payload);
                    break;
                case StreamChannel.Meta:
                    OnMeta(payload);
                    break;
            }
        }
        catch (Exception ex)
        {
            _host.Diagnostics.Record(DiagnosticsCategory.Stream, "mirror.record.failed", ex.Message, deviceId, DiagnosticsSeverity.Warning);
        }
    }

    /**
     * A Wi-Fi blip drops the channel but not the phone's capture, which carries on over the next
     * one. A phone that doesn't come back within a few seconds has really stopped.
     */
    private void OnChannelChanged(string deviceId, bool open)
    {
        if (deviceId != _deviceId || open)
        {
            return;
        }

        var seen = Interlocked.Read(ref _videoRecords);
        Dispatcher.BeginInvoke(() => _status.Text = "Reconnecting to the phone…");
        _ = Task.Delay(TimeSpan.FromSeconds(15)).ContinueWith(_ =>
        {
            if (Interlocked.Read(ref _videoRecords) == seen)
            {
                Dispatcher.BeginInvoke(EndFromPhone);
            }
        });
    }

    private long _videoRecords;

    private void OnVideo(StreamRecordHeader header, byte[] payload)
    {
        Interlocked.Increment(ref _videoRecords);
        lock (_gate)
        {
            if (_context is null)
            {
                if (header.IsCodecConfig)
                {
                    _codecConfig = payload;
                }

                return;
            }

            if (header.IsCodecConfig)
            {
                // A new config (rotation, new size) needs a new decoder; the same one is fed in band.
                if (_decoder is not null && _codecConfig is not null && !payload.AsSpan().SequenceEqual(_codecConfig))
                {
                    _decoder.Dispose();
                    _decoder = null;
                }

                _codecConfig = payload;
                EnsureDecoder(payload);
                _decoder!.Decode(payload, header.PresentationTimeUs, isKeyframe: false);
                return;
            }

            if (_decoder is null && _codecConfig is { } config && header.IsKeyframe)
            {
                // The config came before the window was ready; the phone only resends keyframes.
                EnsureDecoder(config);
                _decoder!.Decode(config, header.PresentationTimeUs, isKeyframe: false);
            }

            _decoder?.Decode(payload, header.PresentationTimeUs, header.IsKeyframe);
        }
    }

    private void EnsureDecoder(byte[] parameterSets)
    {
        if (_decoder is not null)
        {
            return;
        }

        var decoder = VideoDecoder.Create(_context!, VideoFormatGuids.H264, _videoWidth, _videoHeight, parameterSets);
        decoder.FrameDecoded += OnFrameDecoded;
        _decoder = decoder;
        if (decoder.IsAsync)
        {
            new Thread(() => decoder.PumpAsync(_stop.Token)) { IsBackground = true, Name = "Baton phone decoder events" }.Start();
        }
    }

    private void OnFrameDecoded(DecodedFrame frame)
    {
        try
        {
            lock (_gate)
            {
                _presenter?.Present(frame);
            }
        }
        finally
        {
            frame.Dispose();
        }

        if (!_firstFrame)
        {
            _firstFrame = true;
            Dispatcher.BeginInvoke(ShowStatus);
        }
    }

    private void OnMeta(byte[] payload)
    {
        using var meta = JsonDocument.Parse(payload);
        var root = meta.RootElement;
        if (root.TryGetProperty("session", out var session) && session.GetString() != _sessionId)
        {
            return;
        }

        switch (root.GetProperty("type").GetString())
        {
            case "format":
                var width = root.GetProperty("width").GetInt32();
                var height = root.GetProperty("height").GetInt32();
                lock (_gate)
                {
                    _videoWidth = width;
                    _videoHeight = height;
                    _presenter?.SetVideoSize(width, height);
                }

                break;
            case "end":
                Dispatcher.BeginInvoke(EndFromPhone);
                break;
            case "input":
                _inputReady = root.TryGetProperty("ready", out var ready) && ready.GetBoolean();
                Dispatcher.BeginInvoke(ShowStatus);
                Dispatcher.BeginInvoke(() => PhoneReady?.Invoke());
                break;
        }
    }

    private void OnPointer(SurfacePointerEvent input)
    {
        if (input.Buttons.HasFlag(SurfaceButtons.Right))
        {
            if (input.Action == SurfacePointerAction.Down)
            {
                Send(new NavigateMessage(NavigationTarget.Back));
            }

            return;
        }

        var mapped = Map(input.X, input.Y, clamp: input.Action != SurfacePointerAction.Down);
        if (mapped is not { } point)
        {
            return;
        }

        switch (input.Action)
        {
            case SurfacePointerAction.Down when !_pointerDown:
                _pointerDown = true;
                Send(new TouchMessage(TouchAction.Down, 0, point.X, point.Y, _videoWidth, _videoHeight, 65535));
                break;
            case SurfacePointerAction.Move when _pointerDown:
                Send(new TouchMessage(TouchAction.Move, 0, point.X, point.Y, _videoWidth, _videoHeight, 65535));
                break;
            case SurfacePointerAction.Up when _pointerDown:
                _pointerDown = false;
                Send(new TouchMessage(TouchAction.Up, 0, point.X, point.Y, _videoWidth, _videoHeight, 0));
                break;
        }
    }

    private void OnWheel(SurfaceWheelEvent input)
    {
        if (Map(input.X, input.Y, clamp: true) is { } point)
        {
            Send(new ScrollMessage(point.X, point.Y, _videoWidth, _videoHeight, input.HorizontalNotches, input.VerticalNotches));
        }
    }

    private void OnKey(SurfaceKeyEvent input)
    {
        if (input.VirtualKey == KeyMap.VirtualKeyEscape)
        {
            if (input.IsDown)
            {
                Send(new NavigateMessage(NavigationTarget.Back));
            }

            return;
        }

        if (input.IsDown && input.Control && input.VirtualKey == KeyMap.VirtualKeyV)
        {
            // Ctrl+V types the PC's clipboard into the phone's focused field.
            var text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
            if (!string.IsNullOrEmpty(text))
            {
                Send(new TextMessage(text));
            }

            return;
        }

        if (KeyMap.ToAndroid(input.VirtualKey, input.Control) is { } keyCode)
        {
            Send(new KeyMessage(input.IsDown ? KeyAction.Down : KeyAction.Up, keyCode, Math.Max(0, input.RepeatCount - 1),
                KeyMap.MetaState(input.Control, input.Shift, input.Alt)));
        }
    }

    /// <summary>Surface pixels to phone pixels through the presenter's letterbox.</summary>
    private (int X, int Y)? Map(int surfaceX, int surfaceY, bool clamp)
    {
        if (_videoWidth <= 0 || _videoHeight <= 0)
        {
            return null;
        }

        var (surfaceWidth, surfaceHeight) = _surface.PixelSize;
        var fit = NV12Presenter.FitRect(surfaceWidth, surfaceHeight, _videoWidth, _videoHeight);
        var x = (surfaceX - fit.X) / (double)fit.Width;
        var y = (surfaceY - fit.Y) / (double)fit.Height;
        if (!clamp && (x < 0 || x > 1 || y < 0 || y > 1))
        {
            return null;
        }

        return ((int)Math.Round(Math.Clamp(x, 0, 1) * (_videoWidth - 1)), (int)Math.Round(Math.Clamp(y, 0, 1) * (_videoHeight - 1)));
    }

    private void Send(ControlMessage message)
    {
        if (_host.MediaChannels.Send(_deviceId, StreamChannel.Control, 0, 0, ControlMessages.Encode(message)))
        {
            Interlocked.Increment(ref _inputsSent);
        }
    }

    /// <summary>Control messages delivered to the phone's channel, for diagnosing "input does nothing".</summary>
    public long InputsSent => Interlocked.Read(ref _inputsSent);

    private void Shutdown()
    {
        _host.MediaChannels.RecordReceived -= OnRecord;
        _host.MediaChannels.ChannelChanged -= OnChannelChanged;
        _stop.Cancel();
        if (!_closedByPhone)
        {
            _ = _host.SendAsync(_deviceId, MessageTypes.StreamControl, new StreamControlPayload(_sessionId, StreamActions.Stop));
        }

        lock (_gate)
        {
            _decoder?.Dispose();
            _decoder = null;
            _presenter?.Dispose();
            _presenter = null;
            _context?.Dispose();
            _context = null;
        }

        _audio?.Dispose();
        _audio = null;
    }
}
