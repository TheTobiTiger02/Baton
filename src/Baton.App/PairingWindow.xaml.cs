using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QRCoder;

namespace Baton.App;

public partial class PairingWindow : Window
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _countdown;
    private readonly HashSet<string> _knownDevices;
    private DateTimeOffset _expiresAt;

    internal PairingWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        WindowBackdrop.Attach(this);
        _knownDevices = services.Host.Devices.GetDevices().Select(device => device.DeviceId).ToHashSet();
        _countdown = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => UpdateExpiry(), Dispatcher);
        services.Host.Devices.Changed += OnDevicesChanged;
        Closed += (_, _) =>
        {
            _countdown.Stop();
            services.Host.Devices.Changed -= OnDevicesChanged;
        };
        NewCode();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => NewCode();

    /// <summary>The newest release's APK: a phone without Baton scans this first.</summary>
    private const string ApkLink = Updates.Repository + "/releases/latest/download/Baton.apk";

    private static BitmapImage Qr(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8, drawQuietZones: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        return image;
    }

    private void NewCode()
    {
        AppQr.Source ??= Qr(ApkLink);
        var link = _services.Host.OpenPairing();
        _expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        CodeText.Text = $"{link.Code[..3]} {link.Code[3..]}";
        QrImage.Source = Qr(link.ToString());
        QrImage.Opacity = 1;
        _countdown.Start();
        UpdateExpiry();
    }

    private void UpdateExpiry()
    {
        var left = _expiresAt - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            _countdown.Stop();
            ExpiryText.Text = "This code expired.";
            QrImage.Opacity = 0.15;
            return;
        }

        ExpiryText.Text = $"Code valid for {left:m\\:ss}";
    }

    private void OnDevicesChanged() => Dispatcher.BeginInvoke(() =>
    {
        var added = _services.Host.Devices.GetDevices().FirstOrDefault(device => !_knownDevices.Contains(device.DeviceId));
        if (added is null)
        {
            return;
        }

        _knownDevices.Add(added.DeviceId);
        SuccessText.Text = $"{added.DisplayName} is paired.";
        SuccessBar.Visibility = Visibility.Visible;
        StartupRegistration.EnableAfterPairingIfAllowed();
        _ = CloseSoonAsync();
    });

    private async Task CloseSoonAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Close();
    }
}
