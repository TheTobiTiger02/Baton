using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Baton.Media.Playback;

/// <summary>
/// The single Direct3D 11 device shared by the hardware decoder and the presenter.
///
/// Sharing one device is what makes decode-to-screen zero-copy: the decoder writes NV12 into a
/// texture the presenter can sample directly. It also forces multithread protection on, because a
/// hardware MFT decodes on its own thread while the render thread reads those textures.
/// </summary>
public sealed class D3D11Context : IDisposable
{
    private static int _mediaFoundationStarted;

    private D3D11Context(ID3D11Device device, ID3D11DeviceContext immediateContext, IMFDXGIDeviceManager deviceManager)
    {
        Device = device;
        ImmediateContext = immediateContext;
        DeviceManager = deviceManager;
    }

    public ID3D11Device Device { get; }

    public ID3D11DeviceContext ImmediateContext { get; }

    public IMFDXGIDeviceManager DeviceManager { get; }

    public FeatureLevel FeatureLevel => Device.FeatureLevel;

    public string AdapterDescription { get; private init; } = "unknown";

    public static D3D11Context Create()
    {
        EnsureMediaFoundationStarted();

        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        var levels = new[]
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0
        };

        var result = D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            flags,
            levels,
            out var device,
            out var featureLevel,
            out var context);

        if (result.Failure)
        {
            throw new InvalidOperationException(
                $"Could not create a hardware Direct3D 11 device with video support: {result.Description}");
        }

        _ = featureLevel;

        // Mandatory. A hardware MFT decodes on its own thread and touches this device; without
        // this the driver is free to corrupt state under concurrent use.
        using (var multithread = device!.QueryInterfaceOrNull<ID3D11Multithread>())
        {
            multithread?.SetMultithreadProtected(true);
        }

        var manager = MediaFactory.MFCreateDXGIDeviceManager();
        manager.ResetDevice(device).CheckError();

        var adapterDescription = "unknown";
        using (var dxgiDevice = device.QueryInterfaceOrNull<Vortice.DXGI.IDXGIDevice>())
        {
            if (dxgiDevice is not null)
            {
                using var adapter = dxgiDevice.GetAdapter();
                adapterDescription = adapter.Description.Description;
            }
        }

        return new D3D11Context(device, context!, manager) { AdapterDescription = adapterDescription };
    }

    private static void EnsureMediaFoundationStarted()
    {
        if (Interlocked.Exchange(ref _mediaFoundationStarted, 1) == 0)
        {
            MediaFactory.MFStartup().CheckError();
        }
    }

    public void Dispose()
    {
        DeviceManager.Dispose();
        ImmediateContext.Dispose();
        Device.Dispose();
    }
}
