using Baton.Host;
using Baton.Host.Handoff;
using Baton.Host.Media;
using Baton.Protocol;

namespace Baton.Tests;

public class ProtocolTests
{
    [Fact]
    public void ChallengeProofMatchesAndroidVector()
    {
        // Same vector as dev.baton.android.link.SessionAuthenticationTest.
        var secret = string.Concat(Enumerable.Range(0, 32).Select(i => i.ToString("x2")));
        var proof = DeviceRegistry.ComputeChallengeProof(secret, "challenge-1", "nonce-1", "android-device", "host-1");
        Assert.Equal("910c5114e936b0b3cb8c5db8f848bca9f68b58aa72206ea4e508f753c3e67a13", proof);
    }

    [Fact]
    public void PairingLinkRoundTrips()
    {
        var link = new PairingLink("host", "My PC & more", "ab12", "042133", [new HostEndpoint("192.168.1.5", 7838, "lan", 10)]);
        var parsed = PairingLink.TryParse(link.ToString());
        Assert.NotNull(parsed);
        Assert.Equal("My PC & more", parsed.PcName);
        Assert.Equal("042133", parsed.Code);
        Assert.Equal("192.168.1.5", Assert.Single(parsed.Endpoints).Host);
    }

    [Fact]
    public void EnvelopeUsesCamelCaseEnumsAndOmitsNulls()
    {
        var activity = new Activity("id", "dev", ActivityKind.WebMedia, "t", new ActivityApp("App", "app"), DateTimeOffset.UnixEpoch);
        var json = Json.Serialize(Envelope.Create(MessageTypes.ActivityList, "dev", new ActivityListPayload([activity], PresenceState.Active)));
        Assert.Contains("\"kind\":\"webMedia\"", json);
        Assert.Contains("\"presence\":\"active\"", json);
        Assert.DoesNotContain("\"url\"", json);
        var back = Json.Deserialize(json).ReadRequired<ActivityListPayload>();
        Assert.Equal(ActivityKind.WebMedia, back.Activities[0].Kind);
    }

    [Fact]
    public void PlaybackAdvancesOnlyWhilePlaying()
    {
        var start = DateTimeOffset.UtcNow;
        Assert.Equal(15_000, new Playback(10_000, 60_000, true, 1, start).PositionAt(start.AddSeconds(5)));
        Assert.Equal(10_000, new Playback(10_000, 60_000, false, 1, start).PositionAt(start.AddSeconds(5)));
        Assert.Equal(60_000, new Playback(58_000, 60_000, true, 1, start).PositionAt(start.AddSeconds(5)));
    }

    [Fact]
    public void SuggestionPrefersPlayingThenRecentAndSkipsDevicesInUse()
    {
        var now = DateTimeOffset.UtcNow;
        Activity Make(string id, TimeSpan age, bool? playing = null) =>
            new(id, "phone", ActivityKind.AppMedia, id, new ActivityApp("App", "app"), now - age,
                Playback: playing is { } p ? new Playback(0, 60_000, p, 1, now) : null);

        var old = Make("old", TimeSpan.FromMinutes(20));
        var recent = Make("recent", TimeSpan.FromMinutes(1));
        var older = Make("older", TimeSpan.FromMinutes(3));
        var playing = Make("playing", TimeSpan.FromMinutes(10), playing: true);

        Assert.Equal("playing", HandoffSuggestions.Pick([recent, playing], PresenceState.Active, now)?.Id);
        Assert.Equal("recent", HandoffSuggestions.Pick([old, older, recent], PresenceState.Locked, now)?.Id);
        Assert.Null(HandoffSuggestions.Pick([recent], PresenceState.Active, now));
        Assert.Null(HandoffSuggestions.Pick([old], PresenceState.Idle, now));
    }

    [Fact]
    public void SuggestionIsOfferedOncePerWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var activity = new Activity("a", "phone", ActivityKind.WebPage, "Page", new ActivityApp("App", "app"), now);
        var suggestions = new HandoffSuggestions();
        Assert.True(suggestions.TryOffer("phone", activity, now));
        Assert.False(suggestions.TryOffer("phone", activity, now.AddMinutes(10)));
        Assert.True(suggestions.TryOffer("other", activity, now.AddMinutes(10)));
        Assert.True(suggestions.TryOffer("phone", activity, now + HandoffSuggestions.RepeatAfter));
    }

    [Fact]
    public void ImplausiblePlaybackBecomesUnknownInsteadOfOverflowing()
    {
        // A live stream's media session reported this; formatting it crashed the PC app.
        var live = new Playback(long.MaxValue, long.MaxValue, true, double.NaN, DateTimeOffset.MinValue).Normalized();
        Assert.Equal(0, live.DurationMs);
        Assert.Equal(Playback.MaxPlausibleMs, live.PositionMs);
        Assert.InRange(new Playback(long.MaxValue, 0, true, 1, DateTimeOffset.MinValue).PositionAt(DateTimeOffset.UtcNow), 0, 2 * Playback.MaxPlausibleMs);
        Assert.Equal(0, new Playback(-5, 60_000, false, 1, DateTimeOffset.UtcNow).Normalized().PositionMs);
        Assert.Equal(60_000, new Playback(1_000, 60_000, false, 1, DateTimeOffset.UtcNow).Normalized().DurationMs);
    }

    [Fact]
    public void RankingPutsPlayingMediaFirst()
    {
        var now = DateTimeOffset.UtcNow;
        var app = new ActivityApp("a", "a");
        var page = new Activity("page", "d", ActivityKind.WebPage, "page", app, now);
        var paused = new Activity("paused", "d", ActivityKind.AppMedia, "paused", app, now.AddMinutes(-1), Playback: new Playback(0, 1, false, 1, now));
        var playing = new Activity("playing", "d", ActivityKind.AppMedia, "playing", app, now.AddMinutes(-5), Playback: new Playback(0, 1, true, 1, now));
        Assert.Equal(["playing", "paused", "page"], ActivityRanking.Rank([page, paused, playing], now).Select(a => a.Id));
    }

    [Fact]
    public void RankingPrefersWhatIsHeardThenWhatIsInFront()
    {
        // A muted autoplay video on the second monitor must not beat the video being watched.
        var now = DateTimeOffset.UtcNow;
        var app = new ActivityApp("Zen", "zen");
        Activity Playing(string id, bool? audible, bool? focused, int ageSeconds) =>
            new(id, "d", ActivityKind.WebMedia, id, app, now.AddSeconds(-ageSeconds),
                Playback: new Playback(0, 60_000, true, 1, now), Audible: audible, Focused: focused);

        var mutedTwitter = Playing("twitter", audible: false, focused: false, ageSeconds: 0);
        var youtube = Playing("youtube", audible: true, focused: false, ageSeconds: 30);
        Assert.Equal("youtube", ActivityRanking.Rank([mutedTwitter, youtube], now)[0].Id);

        var background = Playing("background", audible: null, focused: false, ageSeconds: 0);
        var inFront = Playing("front", audible: null, focused: true, ageSeconds: 30);
        Assert.Equal("front", ActivityRanking.Rank([background, inFront], now)[0].Id);

        // Attention only orders within a tier: a paused video is still below anything playing.
        var paused = new Activity("paused", "d", ActivityKind.WebMedia, "paused", app, now,
            Playback: new Playback(0, 60_000, false, 1, now), Audible: false, Focused: true);
        Assert.Equal("twitter", ActivityRanking.Rank([paused, mutedTwitter], now)[0].Id);
    }

    [Fact]
    public void AWindowPlayingMediaIsOfferedOnceAsItsMedia()
    {
        var now = DateTimeOffset.UtcNow;
        var player = new ActivityWindow("1a2b", "harbor");
        var media = new Activity("media", "d", ActivityKind.AppMedia, "Agatha All Along", new ActivityApp("Harbor", "harbor"), now,
            Playback: new Playback(0, 60_000, true, 1, now), Window: player);
        var sameWindow = new Activity("window:1a2b", "d", ActivityKind.WindowStream, "Harbor", new ActivityApp("harbor", "harbor"), now, Window: player);
        var otherWindow = sameWindow with { Id = "window:ffff", Window = new ActivityWindow("ffff", "code") };

        Assert.Equal(["media", "window:ffff"], ActivityRanking.Rank([media, sameWindow, otherWindow], now).Select(activity => activity.Id));
    }
}

public class ContentTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=aqz-KE-bpKQ&t=60s", "aqz-KE-bpKQ")]
    [InlineData("https://youtu.be/aqz-KE-bpKQ?si=x", "aqz-KE-bpKQ")]
    [InlineData("https://m.youtube.com/shorts/abcdefghijk", "abcdefghijk")]
    [InlineData("https://example.com/watch?v=aqz-KE-bpKQ", null)]
    public void ReadsYouTubeIds(string url, string? id) => Assert.Equal(id, ContentLinks.YouTubeVideoId(url));

    [Fact]
    public void SetsYouTubeTime() =>
        Assert.Equal("https://www.youtube.com/watch?v=abcdefghijk&t=95s", ContentLinks.WithYouTubeTime("https://youtu.be/abcdefghijk", 95_400));

    [Fact]
    public void ResolverRejectsSameTitleWithOtherLength()
    {
        YouTubeResolver.Candidate[] results =
        [
            new("trailer", "The Love Hypothesis", "Studio", YouTubeResolver.ParseLength("2:31")),
            new("other", "Something else", "Studio", YouTubeResolver.ParseLength("1:44:02"))
        ];
        Assert.Null(YouTubeResolver.Pick(results, "The Love Hypothesis", null, durationMs: 6_240_000));
        Assert.Equal("trailer", YouTubeResolver.Pick(results, "The Love Hypothesis", null, durationMs: 151_000));
        Assert.Equal("trailer", YouTubeResolver.Pick(results, "The Love Hypothesis", null, durationMs: 0));
    }

    [Theory]
    [InlineData("1:43:25", 6_205_000)]
    [InlineData("4:05", 245_000)]
    [InlineData("", 0)]
    public void ParsesLengths(string text, long ms) => Assert.Equal(ms, YouTubeResolver.ParseLength(text));

    [Theory]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "spotify")]
    [InlineData("4DF9E0F8.Netflix_mcm4njqhnhss8!Netflix.App", "netflix")]
    [InlineData("Chrome", "chrome")]
    [InlineData("mpv.exe", null)]
    public void RecognizesWindowsApps(string aumid, string? provider) => Assert.Equal(provider, KnownApps.FromWindowsId(aumid)?.Provider);
}

public class WindowTests
{
    [Theory]
    [InlineData("mpv.exe", "mpv")]
    [InlineData("app.harbor", "harbor")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "SpotifyMusic")]
    public void GuessesProcessNamesFromAppIds(string appId, string expected) =>
        Assert.Contains(expected, Baton.Media.DesktopWindows.CandidateProcessNames(appId));

    [Fact]
    public void SplitsCommandLines() =>
        Assert.Equal([@"C:\vlc.exe", "--started-from-file", @"C:\Movies\A film.mkv"],
            Baton.Host.Media.LocalFiles.SplitArguments(@"""C:\vlc.exe"" --started-from-file ""C:\Movies\A film.mkv"""));

    [Fact]
    public void FitsWindowsIntoStreamSize()
    {
        Assert.Equal((1920, 1080), Baton.Host.Streaming.WindowStreamService.OutputSize(2560, 1440));
        Assert.Equal((1090, 694), Baton.Host.Streaming.WindowStreamService.OutputSize(1090, 695));
        Assert.Equal((606, 1080), Baton.Host.Streaming.WindowStreamService.OutputSize(1080, 1920));
    }

    [Fact]
    public void SplitsParameterSetsFromKeyframes()
    {
        byte[] data = [0, 0, 0, 1, 0x67, 1, 2, 0, 0, 0, 1, 0x68, 3, 0, 0, 1, 0x65, 9, 9];
        var (config, frame) = Baton.Media.AnnexB.SplitParameterSets(data);
        Assert.Equal([0, 0, 0, 1, 0x67, 1, 2, 0, 0, 0, 1, 0x68, 3], config);
        Assert.Equal([0, 0, 1, 0x65, 9, 9], frame);
    }
}
