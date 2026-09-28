using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using WinRT;

namespace Baton.Media;

/// <summary>
/// The one Direct3D 11 device that capture, colour conversion and the hardware encoder share, so a
/// frame goes from the window to the encoder without ever leaving the GPU.
/// </summary>
public sealed class Gpu : IDisposable
{
    private static int _mediaFoundationStarted;

    private Gpu(ID3D11Device device, ID3D11DeviceContext context, IMFDXGIDeviceManager manager, Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice winrtDevice)
    {
        Device = device;
        Context = context;
        DeviceManager = manager;
        WinRtDevice = winrtDevice;
    }

    public ID3D11Device Device { get; }

    /// <summary>The immediate context. Guarded by <see cref="ContextLock"/>: capture and conversion run on different threads.</summary>
    public ID3D11DeviceContext Context { get; }

    public object ContextLock { get; } = new();

    public IMFDXGIDeviceManager DeviceManager { get; }

    /// <summary>The same device as Windows.Graphics.Capture wants to see it.</summary>
    public Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice WinRtDevice { get; }

    public static Gpu Create()
    {
        if (Interlocked.Exchange(ref _mediaFoundationStarted, 1) == 0)
        {
            MediaFactory.MFStartup().CheckError();
        }

        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
            out var device,
            out _,
            out var context).CheckError();

        // The hardware encoder works on its own thread against this device.
        using (var multithread = device!.QueryInterface<ID3D11Multithread>())
        {
            multithread.SetMultithreadProtected(true);
        }

        var manager = MediaFactory.MFCreateDXGIDeviceManager();
        manager.ResetDevice(device).CheckError();

        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var inspectable));
        var winrtDevice = MarshalInterface<Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice>.FromAbi(inspectable);
        Marshal.Release(inspectable);

        return new Gpu(device, context!, manager, winrtDevice);
    }

    public void Dispose()
    {
        (WinRtDevice as IDisposable)?.Dispose();
        DeviceManager.Dispose();
        Context.Dispose();
        Device.Dispose();
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
}

/// <summary>Reaches the D3D11 texture behind a WinRT capture surface.</summary>
[ComImport]
[Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirect3DDxgiInterfaceAccess
{
    IntPtr GetInterface([In] ref Guid iid);
}

[ComImport]
[Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureItemInterop
{
    IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);

    IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
}
