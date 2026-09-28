using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Baton.Media;
using Baton.Protocol;

namespace Baton.Host.Streaming;

/// <summary>
/// Streams a PC window to a phone, and plays the phone's touches, trackpad and typing back into it.
///
/// Built to start instantly: the frames go over the phone's already-open media channel, and the
/// GPU converter and hardware encoder are kept warm per phone and orientation, so starting a
/// stream is just "start capturing and ask for a keyframe". The output always has the phone's
/// shape, so the window fills the phone screen, and it can be resized to that shape too.
/// </summary>
public sealed class WindowStreamService : IDisposable
{
    public const int MaxLongEdge = 1920;
    private static readonly TimeSpan MinFrameInterval = TimeSpan.FromMilliseconds(15);

    private readonly MediaChannelHub _channels;
    private readonly DiagnosticsLog _diagnostics;
    private readonly HandoffTimeline _timeline;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Device, int Width, int Height), Pipeline> _pipelines = new();
    private readonly ConcurrentDictionary<string, (int Width, int Height)> _screens = new(StringComparer.Ordinal);
    private readonly object _gpuGate = new();
    private Gpu? _gpu;

    public WindowStreamService(MediaChannelHub channels, DiagnosticsLog diagnostics, HandoffTimeline timeline)
    {
        _channels = channels;
        _diagnostics = diagnostics;
        _timeline = timeline;
        channels.RecordReceived += OnRecord;
        channels.KeyframeNeeded += deviceId =>
        {
            if (_sessions.TryGetValue(deviceId, out var session))
            {
                session.RequestKeyframe();
            }
        };
        channels.ChannelChanged += (deviceId, open) =>
        {
            if (!open && _sessions.TryGetValue(deviceId, out var session))
            {
                session.Stop("the phone disconnected");
            }
        };
    }

    /// <summary>A stream started (true) or ended (false), with the window's title. Any thread.</summary>
    public event Action<string, bool>? SessionChanged;

    /// <summary>Resize streamed windows to the phone's shape (restored afterwards).</summary>
    public bool FitToPhone { get; set; } = true;

    /// <summary>Keep the PC awake and unlocked while a phone is controlling it.</summary>
    public bool KeepAwakeWhileStreaming { get; set; } = true;

    public bool IsStreaming => !_sessions.IsEmpty;

    /// <summary>
    /// The phone's screen in its natural orientation. Warms up a landscape pipeline in the
    /// background so the first stream starts as fast as later ones.
    /// </summary>
    public void SetScreen(string deviceId, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        _screens[deviceId] = (Math.Min(width, height), Math.Max(width, height));
        var (w, h) = OutputSize(_screens[deviceId], landscape: true);
        _ = Task.Run(() =>
        {
            try
            {
                GetPipeline(deviceId, w, h);
            }
            catch (Exception ex)
            {
                _diagnostics.Record(DiagnosticsCategory.Stream, "prewarm.failed", ex.Message, deviceId, DiagnosticsSeverity.Warning);
            }
        });
    }

    /// <summary>
    /// Starts streaming <paramref name="window"/> to a phone at once and returns what the viewer
    /// needs to know. The phone gets the frames on its media channel.
    /// </summary>
    public StreamOffer Start(string deviceId, IntPtr window, string title, string sessionId)
    {
        if (!_channels.IsOpen(deviceId))
        {
            throw new InvalidOperationException("The phone's media channel is not open.");
        }

        if (_sessions.TryRemove(deviceId, out var previous))
        {
            previous.Stop("replaced by a new stream", notify: false);
        }

        DesktopWindows.EnsureRestored(window);
        var (_, _, windowWidth, windowHeight) = DesktopWindows.Bounds(window);
        var landscape = windowWidth >= windowHeight;
        var (width, height) = _screens.TryGetValue(deviceId, out var screen)
            ? OutputSize(screen, landscape)
            : OutputSize(windowWidth, windowHeight);
        var session = new Session(this, deviceId, sessionId, window, title, width, height);
        _sessions[deviceId] = session;
        _ = Task.Run(session.Start);
        return new StreamOffer(sessionId, width, height, title, StreamKinds.Window);
    }

    /// <summary>A session-level command from the viewer: stop, return, fit, unfit.</summary>
    public void Control(string deviceId, StreamControlPayload control)
    {
        if (control.Action == StreamActions.FirstFrame)
        {
            _timeline.Mark(control.SessionId, "viewer first frame", control.ElapsedMs);
            return;
        }

        if (!_sessions.TryGetValue(deviceId, out var session) || session.SessionId != control.SessionId)
        {
            return;
        }

        switch (control.Action)
        {
            case StreamActions.Stop:
                session.Stop("the phone closed the stream");
                break;
            case StreamActions.Return:
                session.Stop("continued on the PC", bringToFront: true);
                break;
            case StreamActions.Fit:
                session.SetFit(true);
                break;
            case StreamActions.Unfit:
                session.SetFit(false);
                break;
        }
    }

    public void StopAll()
    {
        foreach (var session in _sessions.Values)
        {
            session.Stop("stopped on the PC");
        }
    }

    public void Dispose()
    {
        StopAll();
        foreach (var pipeline in _pipelines.Values)
        {
            pipeline.Dispose();
        }

        _gpu?.Dispose();
    }

    /// <summary>Scales a window into at most 1920x1080 keeping its shape, with even dimensions.</summary>
    public static (int Width, int Height) OutputSize(int width, int height)
    {
        width = Math.Max(width, 64);
        height = Math.Max(height, 64);
        var scale = Math.Min(1.0, Math.Min((double)MaxLongEdge / width, 1080.0 / height));
        return (Math.Max(64, (int)(width * scale) & ~1), Math.Max(64, (int)(height * scale) & ~1));
    }

    /// <summary>The phone's screen (natural orientation), turned and capped at 1920 on the long edge.</summary>
    public static (int Width, int Height) OutputSize((int Width, int Height) screen, bool landscape)
    {
        var shortEdge = Math.Min(screen.Width, screen.Height);
        var longEdge = Math.Max(screen.Width, screen.Height);
        var scale = Math.Min(1.0, (double)MaxLongEdge / longEdge);
        var s = Math.Max(64, (int)(shortEdge * scale) & ~1);
        var l = Math.Max(64, (int)(longEdge * scale) & ~1);
        return landscape ? (l, s) : (s, l);
    }

    /// <summary>
    /// Maps a point in the video frame to the screen. The window sits letterboxed inside the frame
    /// with the shape of <paramref name="content"/>; <paramref name="window"/> is where it is now.
    /// </summary>
    public static (int X, int Y) FrameToScreen(int x, int y, int frameWidth, int frameHeight,
        (int Width, int Height) content, (int Left, int Top, int Width, int Height) window)
    {
        var scale = Math.Min((double)frameWidth / content.Width, (double)frameHeight / content.Height);
        var fitWidth = content.Width * scale;
        var fitHeight = content.Height * scale;
        var fx = Math.Clamp((x - (frameWidth - fitWidth) / 2) / fitWidth, 0, 1);
        var fy = Math.Clamp((y - (frameHeight - fitHeight) / 2) / fitHeight, 0, 1);
        return ((int)Math.Round(window.Left + fx * (window.Width - 1)), (int)Math.Round(window.Top + fy * (window.Height - 1)));
    }

    private Gpu SharedGpu()
    {
        lock (_gpuGate)
        {
            return _gpu ??= Gpu.Create();
        }
    }

    private Pipeline GetPipeline(string deviceId, int width, int height) =>
        _pipelines.GetOrAdd((deviceId, width, height), key =>
        {
            var started = Stopwatch.StartNew();
            var pipeline = new Pipeline(SharedGpu(), key.Width, key.Height);
            _diagnostics.Record(DiagnosticsCategory.Stream, "pipeline.ready",
                $"{key.Width}x{key.Height} via {pipeline.Encoder.Name} in {started.ElapsedMilliseconds} ms", deviceId);
            return pipeline;
        });

    private void OnRecord(string deviceId, StreamRecordHeader header, byte[] payload)
    {
        if (header.Channel == StreamChannel.Control && _sessions.TryGetValue(deviceId, out var session))
        {
            try
            {
                session.HandleInput(ControlMessages.Decode(payload));
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException or ArgumentOutOfRangeException)
            {
                _diagnostics.Record(DiagnosticsCategory.Stream, "input.bad", ex.Message, deviceId, DiagnosticsSeverity.Warning);
            }
        }
    }

    /// <summary>A warm converter and encoder for one output size. Frames go to its current session.</summary>
    private sealed class Pipeline : IDisposable
    {
        public Pipeline(Gpu gpu, int width, int height)
        {
            Converter = new Nv12Converter(gpu, width, height);
            Encoder = H264Encoder.Create(gpu, width, height);
            Encoder.FrameEncoded += frame => Sink?.OnEncoded(frame);
        }

        public Nv12Converter Converter { get; }
        public H264Encoder Encoder { get; }
        public volatile Session? Sink;

        /// <summary>SPS/PPS from the last keyframe that carried them; re-sent before later keyframes.</summary>
        public byte[]? CodecConfig { get; set; }

        public void Dispose()
        {
            Encoder.Dispose();
            Converter.Dispose();
        }
    }

    private sealed class Session(WindowStreamService owner, string deviceId, string sessionId, IntPtr window, string title, int width, int height)
    {
        private readonly object _gate = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly TouchInjector _touch = new();
        private Pipeline? _pipeline;
        private WindowCapture? _capture;
        private AudioLoopback? _audio;
        private KeepAwake? _keepAwake;
        private Timer? _idleRefresh;
        private Vortice.Direct3D11.ID3D11Texture2D? _lastNv12;
        private TimeSpan _lastFrame = TimeSpan.FromDays(-1);
        private (int Left, int Top) _monitorOrigin;
        private bool _monitorMode;
        private (int Width, int Height) _content = (width, height);
        private byte[]? _savedPlacement;
        private bool _keyframeSent;
        private int _width = width, _height = height;
        private int _stopped;

        public string SessionId { get; } = sessionId;

        public void Start()
        {
            try
            {
                DesktopWindows.BringToFront(window);
                if (owner.FitToPhone)
                {
                    SetFit(true, restart: false);
                }

                StartCapture();
                SendMeta(new { type = "format", session = SessionId, width = _width, height = _height, title });

                if (owner.KeepAwakeWhileStreaming)
                {
                    _keepAwake = new KeepAwake("Baton is streaming a window to your phone.");
                }

                // A still window sends no frames of its own; repeat the last one, often at first
                // so a late viewer never waits.
                _idleRefresh = new Timer(_ => RepeatLastFrame(), null, 100, 100);
                owner._timeline.Mark(SessionId, "stream started");
                owner.SessionChanged?.Invoke(title, true);
                StartAudio();
            }
            catch (Exception ex)
            {
                owner._diagnostics.Record(DiagnosticsCategory.Stream, "stream.start.failed", ex.ToString(), deviceId, DiagnosticsSeverity.Error);
                Stop("the stream could not start");
            }
        }

        private void StartCapture()
        {
            lock (_gate)
            {
                _capture?.Dispose();
                _pipeline = owner.GetPipeline(deviceId, _width, _height);
                _pipeline.Sink = this;
                _keyframeSent = false;
                _pipeline.Encoder.RequestKeyframe();

                // Monitor capture shows menus and popups; it needs the window on screen and in front.
                _monitorMode = DesktopWindows.IsOnScreen(window);
                if (_monitorMode)
                {
                    var (monitor, left, top, _, _) = DesktopWindows.MonitorOf(window);
                    _monitorOrigin = (left, top);
                    _capture = WindowCapture.ForMonitor(owner.SharedGpu(), monitor);
                }
                else
                {
                    _capture = new WindowCapture(owner.SharedGpu(), window);
                    _capture.Closed += () => Stop("the window was closed");
                }

                _capture.FrameArrived += OnFrame;
                _capture.FrameFailed += ex => owner._diagnostics.Record(DiagnosticsCategory.Stream, "frame.failed", ex.Message, deviceId, DiagnosticsSeverity.Warning);
                _capture.Start();
            }
        }

        private void StartAudio()
        {
            try
            {
                _audio = AudioLoopback.ForWindow(window);
                _audio.Captured += (pcm, count) =>
                    owner._channels.Send(deviceId, StreamChannel.Audio, StreamRecordFlags.None, (long)_clock.Elapsed.TotalMicroseconds, pcm[..count]);
                _audio.Start();
            }
            catch (Exception ex)
            {
                // Video without sound still beats nothing.
                owner._diagnostics.Record(DiagnosticsCategory.Stream, "stream.audio.failed", ex.Message, deviceId, DiagnosticsSeverity.Warning);
            }
        }

        public void RequestKeyframe() => _pipeline?.Encoder.RequestKeyframe();

        /// <summary>Resizes the window to the phone's shape (remembering where it was), or puts it back.</summary>
        public void SetFit(bool fit, bool restart = true)
        {
            if (fit && _savedPlacement is null && !DesktopWindows.IsFullscreen(window))
            {
                _savedPlacement = DesktopWindows.SavePlacement(window);
                var target = DesktopWindows.FitRect(DesktopWindows.WorkAreaOf(window), (double)_width / _height);
                DesktopWindows.MoveTo(window, target);
            }
            else if (!fit && _savedPlacement is { } saved)
            {
                DesktopWindows.RestorePlacement(window, saved);
                _savedPlacement = null;
            }
            else
            {
                return;
            }

            if (restart && Volatile.Read(ref _stopped) == 0)
            {
                // The window may now be on screen where it was not; pick the right capture again.
                StartCapture();
            }
        }

        private void OnFrame(Vortice.Direct3D11.ID3D11Texture2D texture, int sourceWidth, int sourceHeight)
        {
            var now = _clock.Elapsed;
            var pipeline = _pipeline;
            if (now - _lastFrame < MinFrameInterval || pipeline is null || Volatile.Read(ref _stopped) != 0)
            {
                return;
            }

            _lastFrame = now;
            try
            {
                Vortice.Direct3D11.ID3D11Texture2D nv12;
                if (_monitorMode)
                {
                    var (left, top, w, h) = DesktopWindows.Bounds(window);
                    _content = (w, h);
                    nv12 = pipeline.Converter.Convert(texture, sourceWidth, sourceHeight, (left - _monitorOrigin.Left, top - _monitorOrigin.Top, w, h));
                }
                else
                {
                    _content = (sourceWidth, sourceHeight);
                    nv12 = pipeline.Converter.Convert(texture, sourceWidth, sourceHeight);
                }

                _lastNv12 = nv12;
                pipeline.Encoder.TryEncode(nv12, (long)now.TotalMicroseconds);
            }
            catch (Exception ex)
            {
                owner._diagnostics.Record(DiagnosticsCategory.Stream, "frame.failed", ex.Message, deviceId, DiagnosticsSeverity.Warning);
            }
        }

        private void RepeatLastFrame()
        {
            var now = _clock.Elapsed;
            var interval = now < TimeSpan.FromSeconds(2) ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromMilliseconds(500);
            if (_lastNv12 is not { } nv12 || _pipeline is not { } pipeline || now - _lastFrame < interval || Volatile.Read(ref _stopped) != 0)
            {
                return;
            }

            _lastFrame = now;
            try
            {
                pipeline.Encoder.TryEncode(nv12, (long)now.TotalMicroseconds);
            }
            catch (Exception)
            {
            }
        }

        public void OnEncoded(EncodedFrame frame)
        {
            if (Volatile.Read(ref _stopped) != 0 || _pipeline is not { } pipeline)
            {
                return;
            }

            if (frame.IsKeyframe)
            {
                var (config, rest) = AnnexB.SplitParameterSets(frame.Data);
                if (config.Length > 0)
                {
                    pipeline.CodecConfig = H264Sps.DeclareNoReordering(config);
                }

                if (pipeline.CodecConfig is { } cached)
                {
                    owner._channels.Send(deviceId, StreamChannel.Video, StreamRecordFlags.CodecConfig, frame.PresentationTimeUs, cached);
                }

                owner._channels.Send(deviceId, StreamChannel.Video, StreamRecordFlags.Keyframe, frame.PresentationTimeUs, rest);
                if (!_keyframeSent)
                {
                    _keyframeSent = true;
                    owner._timeline.Mark(SessionId, "first keyframe sent");
                }

                return;
            }

            // A decoder cannot start on a delta frame.
            if (_keyframeSent)
            {
                owner._channels.Send(deviceId, StreamChannel.Video, StreamRecordFlags.None, frame.PresentationTimeUs, frame.Data);
            }
        }

        public void HandleInput(ControlMessage message)
        {
            if (Volatile.Read(ref _stopped) != 0)
            {
                return;
            }

            var bounds = DesktopWindows.Bounds(window);
            (int X, int Y) ToScreen(int x, int y, int frameWidth, int frameHeight) =>
                FrameToScreen(x, y, frameWidth <= 0 ? _width : frameWidth, frameHeight <= 0 ? _height : frameHeight, _content, bounds);

            switch (message)
            {
                case TouchMessage touch:
                {
                    if (touch.Action == TouchAction.Down && !_touch.IsTouching)
                    {
                        DesktopWindows.BringToFront(window);
                    }

                    var (x, y) = ToScreen(touch.X, touch.Y, touch.FrameWidth, touch.FrameHeight);
                    _touch.Touch(touch.Action, touch.PointerId, x, y);
                    break;
                }

                case ScrollMessage scroll when scroll.FrameWidth <= 0:
                    InputInjector.ScrollAtCursor(scroll.Horizontal, scroll.Vertical);
                    break;
                case ScrollMessage scroll:
                {
                    var (x, y) = ToScreen(scroll.X, scroll.Y, scroll.FrameWidth, scroll.FrameHeight);
                    InputInjector.ScrollAt(x, y, scroll.Horizontal, scroll.Vertical);
                    break;
                }

                case MouseMoveMessage move:
                {
                    // Frame pixels to screen pixels, at the scale the window is shown.
                    var scale = Math.Max((double)_content.Width / _width, (double)_content.Height / _height) * ((double)bounds.Width / Math.Max(1, _content.Width));
                    InputInjector.MoveBy((int)Math.Round(move.Dx * scale), (int)Math.Round(move.Dy * scale), bounds);
                    break;
                }

                case MouseButtonMessage button:
                    if (button.Action == KeyAction.Down)
                    {
                        DesktopWindows.BringToFront(window);
                    }

                    InputInjector.Button(button.Button, button.Action == KeyAction.Down);
                    break;
                case KeyMessage { Action: KeyAction.Down } key:
                {
                    var vk = KeyMapWindows.VirtualKey(key.KeyCode);
                    if (vk != 0)
                    {
                        InputInjector.Chord(vk, KeyMapWindows.Modifiers(key.MetaState));
                    }

                    break;
                }

                case TextMessage text:
                    InputInjector.Text(text.Value);
                    break;
                case NavigateMessage { Target: NavigationTarget.Back }:
                    InputInjector.Chord(0xA6 /* VK_BROWSER_BACK */, []);
                    break;
                case KeyframeRequestMessage:
                    RequestKeyframe();
                    break;
                case RotateMessage rotate:
                    Rotate(rotate.Orientation == 1);
                    break;
            }
        }

        /// <summary>The phone turned: switch to the other output shape (and refit the window).</summary>
        private void Rotate(bool landscape)
        {
            if (!owner._screens.TryGetValue(deviceId, out var screen))
            {
                return;
            }

            var (w, h) = OutputSize(screen, landscape);
            if (w == _width && h == _height)
            {
                return;
            }

            _width = w;
            _height = h;
            var wasFitted = _savedPlacement is not null;
            if (wasFitted)
            {
                SetFit(false, restart: false);
                SetFit(true, restart: false);
            }

            SendMeta(new { type = "format", session = SessionId, width = _width, height = _height, title });
            StartCapture();
        }

        private void SendMeta(object meta) =>
            owner._channels.Send(deviceId, StreamChannel.Meta, StreamRecordFlags.None, 0, JsonSerializer.SerializeToUtf8Bytes(meta));

        public void Stop(string reason, bool bringToFront = false, bool notify = true)
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
            {
                return;
            }

            lock (_gate)
            {
                _idleRefresh?.Dispose();
                _capture?.Dispose();
                _audio?.Dispose();
                _touch.Dispose();
                _keepAwake?.Dispose();
                if (_pipeline is { } pipeline && ReferenceEquals(pipeline.Sink, this))
                {
                    pipeline.Sink = null;
                }
            }

            if (_savedPlacement is { } saved)
            {
                DesktopWindows.RestorePlacement(window, saved);
            }

            if (bringToFront)
            {
                DesktopWindows.BringToFront(window);
            }

            if (notify)
            {
                SendMeta(new { type = "end", session = SessionId, reason });
            }

            owner._sessions.TryRemove(new KeyValuePair<string, Session>(deviceId, this));
            owner._diagnostics.Record(DiagnosticsCategory.Stream, "stream.stopped", $"{title}: {reason}", deviceId);
            owner.SessionChanged?.Invoke(title, false);
        }
    }
}
