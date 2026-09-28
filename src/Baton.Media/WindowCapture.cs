using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using WinRT;

namespace Baton.Media;

/// <summary>
/// Captures a window or a whole monitor with Windows.Graphics.Capture, the API behind Snipping
/// Tool and Game Bar, and hands over GPU textures, never pixels in system memory. Window capture
/// works even when the window is covered; monitor capture also shows the window's menus, popups
/// and tooltips, which are separate windows.
/// </summary>
public sealed class WindowCapture : IDisposable
{
    private static readonly Guid CaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid Texture2DIid = typeof(ID3D11Texture2D).GUID;

    private readonly Gpu _gpu;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private SizeInt32 _poolSize;
    private int _disposed;

    public WindowCapture(Gpu gpu, IntPtr window)
        : this(gpu, CreateItem(window, monitor: false))
    {
    }

    private WindowCapture(Gpu gpu, GraphicsCaptureItem item)
    {
        _gpu = gpu;
        _item = item;

        _poolSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(gpu.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _pool.FrameArrived += OnFrameArrived;
        _item.Closed += (_, _) => Closed?.Invoke();
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = true;
        try
        {
            // No yellow capture border (Windows 11). Older builds refuse; the border is harmless.
            _session.IsBorderRequired = false;
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Captures everything shown on a monitor (an HMONITOR).</summary>
    public static WindowCapture ForMonitor(Gpu gpu, IntPtr monitor) => new(gpu, CreateItem(monitor, monitor: true));

    private static GraphicsCaptureItem CreateItem(IntPtr handle, bool monitor)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = CaptureItemIid;
        var pointer = monitor ? interop.CreateForMonitor(handle, ref iid) : interop.CreateForWindow(handle, ref iid);
        var item = GraphicsCaptureItem.FromAbi(pointer);
        Marshal.Release(pointer);
        return item;
    }

    /// <summary>The window's current size in pixels.</summary>
    public (int Width, int Height) Size => (_item.Size.Width, _item.Size.Height);

    /// <summary>
    /// A new frame. The texture is only valid during the callback; copy or convert it there. Raised
    /// on a capture thread.
    /// </summary>
    public event Action<ID3D11Texture2D, int, int>? FrameArrived;

    /// <summary>The window was closed.</summary>
    public event Action? Closed;

    /// <summary>A frame could not be read; capture continues with the next one.</summary>
    public event Action<Exception>? FrameFailed;

    public void Start() => _session.StartCapture();

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            ReadFrame(sender);
        }
        catch (Exception ex)
        {
            FrameFailed?.Invoke(ex);
        }
    }

    private void ReadFrame(Direct3D11CaptureFramePool sender)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var size = frame.ContentSize;
        if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
        {
            // The window was resized; the next frames come at the new size.
            _poolSize = size;
            _pool.Recreate(_gpu.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
        }

        var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
        var iid = Texture2DIid;
        using var texture = new ID3D11Texture2D(access.GetInterface(ref iid));
        FrameArrived?.Invoke(texture, Math.Max(1, size.Width), Math.Max(1, size.Height));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _session.Dispose();
        _pool.Dispose();
    }
}
