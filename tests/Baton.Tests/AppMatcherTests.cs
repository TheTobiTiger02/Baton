using Baton.Host.Apps;
using Baton.Protocol;

namespace Baton.Tests;

public class AppMatcherTests
{
    private static readonly CatalogApp[] Pc =
    [
        new("5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App", "WhatsApp"),
        new("TelegramMessengerLLP.TelegramDesktop_t4vj0pshhgkwm!Telegram.TelegramDesktop.Store", "Telegram Desktop"),
        new("googleplaygames://launch/?id=com.supercell.clashroyale&lid=1&pid=1", "Clash Royale"),
        new("SAMSUNGELECTRONICSCoLtd.SamsungNotes_wyx1vj98g3asy!App", "Samsung Notes"),
        new("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "Rechner"),
        new(@"browser:C:\Program Files\Zen\zen.exe", "Zen", @"C:\Program Files\Zen\zen.exe", IsBrowser: true),
        new(@"browser:C:\Opera\opera.exe", "Opera GX", @"C:\Opera\opera.exe", IsBrowser: true),
    ];

    private static readonly CatalogApp[] Phone =
    [
        new("com.whatsapp", "WhatsApp"),
        new("com.sec.android.app.sbrowser", "Samsung Internet", IsBrowser: true),
        new("com.android.chrome", "Chrome", IsBrowser: true),
    ];

    private static Activity PhoneApp(string package, string name) =>
        new($"app:{package}", "phone", ActivityKind.WindowStream, name, new ActivityApp(name, package), DateTimeOffset.UtcNow);

    private static HandoffChoice[] Options(Activity activity, string from, string to, CatalogApp[] apps, bool stream = true) =>
        AppMatcher.Options(activity, from, to, apps, stream).ToArray();

    [Fact]
    public void KnownPairOpensTheDesktopAppFirstThenTheWebThenTheMirror()
    {
        var options = Options(PhoneApp("com.whatsapp", "WhatsApp"), Platforms.Android, Platforms.Windows, Pc);
        Assert.Equal([ChoiceKinds.App, ChoiceKinds.Web, ChoiceKinds.Stream], options.Select(option => option.Kind));
        Assert.Equal("5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App", options[0].AppId);
        Assert.Equal("https://web.whatsapp.com", options[1].Url);
    }

    [Fact]
    public void SameNameWithDesktopSuffixMatches()
    {
        var options = Options(PhoneApp("some.other.telegram", "Telegram"), Platforms.Android, Platforms.Windows, Pc);
        Assert.Equal("Telegram Desktop", options[0].Label);
        Assert.False(AppMatcher.SameName(AppMatcher.Words("Notes"), AppMatcher.Words("Samsung Notes")));
    }

    [Fact]
    public void PlayGamesTitlesMatchByPackage()
    {
        var options = Options(PhoneApp("com.supercell.clashroyale", "Clash Royale"), Platforms.Android, Platforms.Windows, Pc);
        Assert.StartsWith("googleplaygames://", options[0].AppId);
        Assert.Single(options, option => option.Kind == ChoiceKinds.App);
    }

    [Fact]
    public void AppsWithoutCounterpartOnlyMirror()
    {
        var options = Options(PhoneApp("com.nianticlabs.pokemongo", "Pokémon GO"), Platforms.Android, Platforms.Windows, Pc);
        Assert.Equal([ChoiceKinds.Stream], options.Select(option => option.Kind));
    }

    [Fact]
    public void PagesOfferTheDefaultBrowserThenEachBrowser()
    {
        var page = new Activity("page:com.sec.android.app.sbrowser", "phone", ActivityKind.WebPage, "News",
            new ActivityApp("Samsung Internet", "com.sec.android.app.sbrowser"), DateTimeOffset.UtcNow, Url: "https://example.com");
        var options = Options(page, Platforms.Android, Platforms.Windows, Pc);
        Assert.Equal(["Default browser", "Zen", "Opera GX", "Mirror the phone"], options.Select(option => option.Label));
    }

    [Fact]
    public void PcWindowGoesToThePhoneAppOrStream()
    {
        var window = new Activity("window:1", "pc", ActivityKind.WindowStream, "WhatsApp", new ActivityApp("WhatsApp", "WhatsApp"),
            DateTimeOffset.UtcNow, Window: new ActivityWindow("1", "WhatsApp"));
        var options = Options(window, Platforms.Windows, Platforms.Android, Phone);
        Assert.Equal("com.whatsapp", options[0].AppId);
        Assert.Equal(ChoiceKinds.Stream, options[^1].Kind);
    }

    [Fact]
    public void RememberedChoicesSurviveARestart()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        var whatsapp = PhoneApp("com.whatsapp", "WhatsApp");
        var choice = new HandoffChoice(ChoiceKinds.Web, "WhatsApp on the web", Url: "https://web.whatsapp.com", Remember: true);
        new AppDirectory(folder, new Baton.Host.DiagnosticsLog()).Remember(whatsapp, Platforms.Android, Platforms.Windows, "pc", choice);

        var reloaded = new AppDirectory(folder, new Baton.Host.DiagnosticsLog());
        Assert.Equal(choice with { Remember = false }, reloaded.Remembered(whatsapp, Platforms.Android, Platforms.Windows, "pc"));
        Assert.Null(reloaded.Remembered(whatsapp, Platforms.Android, Platforms.Android, "phone"));
        Assert.Equal("android:com.whatsapp->windows", Assert.Single(reloaded.Preferences).Key);
    }

    [Fact]
    public void AYouTubeBuildIsRememberedPerPhone()
    {
        // RVX picked for the S25 must not pin the S21, which has ReVanced instead.
        var directory = new AppDirectory(Directory.CreateTempSubdirectory().FullName, new Baton.Host.DiagnosticsLog());
        directory.SetPhoneApps("s25", [new InstalledApp("app.rvx.android.youtube", "RVX")]);
        directory.SetPhoneApps("s21", [new InstalledApp("app.revanced.android.youtube", "YouTube ReVanced")]);
        var video = new Activity("tab:1:1", "pc", ActivityKind.WebMedia, "Video", new ActivityApp("Zen", "zen"), DateTimeOffset.UtcNow,
            Url: "https://www.youtube.com/watch?v=abc", Content: new ActivityContent("youtube", "abc"));
        directory.Remember(video, Platforms.Windows, Platforms.Android, "s25",
            new HandoffChoice(ChoiceKinds.App, "RVX", "app.rvx.android.youtube", Remember: true));

        Assert.Equal("app.rvx.android.youtube", directory.Remembered(video, Platforms.Windows, Platforms.Android, "s25")?.AppId);
        Assert.Null(directory.Remembered(video, Platforms.Windows, Platforms.Android, "s21"));
    }

    [Fact]
    public void ARememberedAppTheTargetLacksIsIgnored()
    {
        // Choices remembered before builds were kept per phone.
        var folder = Directory.CreateTempSubdirectory().FullName;
        var video = new Activity("tab:1:1", "pc", ActivityKind.WebMedia, "Video", new ActivityApp("Zen", "zen"), DateTimeOffset.UtcNow,
            Content: new ActivityContent("youtube", "abc"));
        File.WriteAllText(Path.Combine(folder, "app-preferences.json"),
            "[{\"key\":\"windows:site:youtube->android\",\"sourceName\":\"YouTube\",\"choice\":{\"kind\":\"app\",\"label\":\"RVX\",\"appId\":\"app.rvx.android.youtube\",\"remember\":false}}]");
        var directory = new AppDirectory(folder, new Baton.Host.DiagnosticsLog());
        directory.SetPhoneApps("s21", [new InstalledApp("app.revanced.android.youtube", "YouTube ReVanced")]);
        Assert.Null(directory.Remembered(video, Platforms.Windows, Platforms.Android, "s21"));
    }

    // Same JSON as dev.baton.android.apps.ChoiceWireTest decodes.
    public const string ChoiceJson = "{\"kind\":\"app\",\"label\":\"WhatsApp\",\"appId\":\"5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App\",\"remember\":true}";

    [Fact]
    public void ChoiceWireFormatMatchesAndroid()
    {
        var choice = new HandoffChoice(ChoiceKinds.App, "WhatsApp", "5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App", Remember: true);
        Assert.Equal(ChoiceJson, System.Text.Json.JsonSerializer.Serialize(choice, Json.Options));
        Assert.Equal("android:com.whatsapp->windows", ChoiceKeys.For(Platforms.Android, "com.WhatsApp", Platforms.Windows));
    }

    [Fact]
    public void YouTubeForksAreYouTube()
    {
        var revanced = new CatalogApp("app.revanced.android.youtube", "YouTube ReVanced");
        var video = new Activity("tab:b1:3", "pc", ActivityKind.WebMedia, "Video", new ActivityApp("Edge", "msedge"), DateTimeOffset.UtcNow,
            Url: "https://www.youtube.com/watch?v=abc", Content: new ActivityContent("youtube", "abc"));
        Assert.Equal("YouTube ReVanced", Options(video, Platforms.Windows, Platforms.Android, [.. Phone, revanced])[0].Label);
        var both = Options(video, Platforms.Windows, Platforms.Android, [.. Phone, revanced, new CatalogApp("app.rvx.android.youtube", "YouTube RVX")]);
        Assert.Equal(["YouTube (the one you use)", "YouTube ReVanced", "YouTube RVX"], both.Take(3).Select(option => option.Label));
        Assert.Equal("youtube", Baton.Host.Media.KnownApps.FromAndroidPackage("app.rvx.android.youtube")?.Provider);
        Assert.Equal("youtubemusic", Baton.Host.Media.KnownApps.FromAndroidPackage("app.revanced.android.apps.youtube.music")?.Provider);

        var phoneApp = Options(PhoneApp("app.revanced.android.youtube", "YouTube ReVanced"), Platforms.Android, Platforms.Windows, Pc);
        Assert.Contains(phoneApp, option => option.Url == "https://www.youtube.com");
    }

    [Fact]
    public void ChoicesInABrowserAreRememberedPerSite()
    {
        Activity InZen(string title, string? url, string provider) => new("media:zen", "pc", ActivityKind.WebMedia, title,
            new ActivityApp("Zen", "F0DC299D809B9700"), DateTimeOffset.UtcNow, Url: url, Content: new ActivityContent(provider));
        Assert.Equal("windows:site:youtube->android", ChoiceKeys.For(Platforms.Windows, InZen("Video", null, "youtube"), Platforms.Android));
        Assert.Equal("windows:site:x.com->android", ChoiceKeys.For(Platforms.Windows, InZen("Post", "https://x.com/home", "web"), Platforms.Android));
        Assert.Equal("windows:site:web->android", ChoiceKeys.For(Platforms.Windows, InZen("Something", null, "web"), Platforms.Android));
        Assert.Equal("android:com.whatsapp->windows", ChoiceKeys.For(Platforms.Android, PhoneApp("com.whatsapp", "WhatsApp"), Platforms.Windows));

        // A choice stored per browser by an older version is dropped when loading.
        var folder = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(folder, "app-preferences.json"),
            """[{"key":"windows:f0dc299d809b9700->android","sourceName":"Zen","choice":{"kind":"default","label":"YouTube"}},{"key":"android:com.whatsapp->windows","sourceName":"WhatsApp","choice":{"kind":"web","label":"Web"}}]""");
        Assert.Equal(["android:com.whatsapp->windows"], new AppDirectory(folder, new Baton.Host.DiagnosticsLog()).Preferences.Select(item => item.Key));
    }

    [Fact]
    public void SamePageKeepsVideosApart()
    {
        Assert.False(Baton.Host.Browser.BrowserBridge.SamePage("https://www.youtube.com/watch?v=aaa", "https://www.youtube.com/watch?v=bbb"));
        Assert.True(Baton.Host.Browser.BrowserBridge.SamePage("https://www.youtube.com/watch?v=aaa&t=42s", "https://youtube.com/watch?v=aaa"));
        Assert.True(Baton.Host.Browser.BrowserBridge.SamePage("https://x.com/a/status/1?utm_source=x", "https://x.com/a/status/1"));
        Assert.False(Baton.Host.Browser.BrowserBridge.SamePage("https://x.com/a/status/1", "https://x.com/a/status/2"));
    }
}
