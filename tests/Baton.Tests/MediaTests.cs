using Baton.Host.Streaming;
using Baton.Media;

namespace Baton.Tests;

public class MediaTests
{
    // SPS + PPS as the NVIDIA H.264 MFT emits them: High profile, VUI with timing, no bitstream_restriction.
    private static readonly byte[] NvidiaConfig = Convert.FromHexString(
        "000000016764002aac2b403c036fde022000007d00003a9801e385540000000168ee3cb0");

    [Fact]
    public void SpsRewriteDeclaresNoReorderingAndKeepsThePps()
    {
        var rewritten = H264Sps.DeclareNoReordering(NvidiaConfig);

        Assert.NotEqual(NvidiaConfig, rewritten);
        Assert.EndsWith("0000000168EE3CB0", Convert.ToHexString(rewritten));
        // Everything before the VUI's end is untouched.
        Assert.Equal(NvidiaConfig[..20], rewritten[..20]);
        // Rewriting again parses our own output and changes nothing: the restriction is well formed.
        Assert.Equal(rewritten, H264Sps.DeclareNoReordering(rewritten));
    }

    // Same vectors as dev.baton.android.stream.ControlMessageCodecTest: the two codecs must agree byte for byte.
    public static TheoryData<string, ControlMessage> ControlVectors => new()
    {
        { "0100000000000000000700000064000000C807800438FFFF", new TouchMessage(TouchAction.Down, 7, 100, 200, 1920, 1080, 65535) },
        { "020000000A0000001407800438FFFF0002", new ScrollMessage(10, 20, 1920, 1080, -1, 2) },
        { "03000000001D0000000000001000", new KeyMessage(KeyAction.Down, 29, 0, 0x1000) },
        { "040000000368C3A9", new TextMessage("hé") },
        { "0D0000000368C3A9", new ClipboardMessage("hé") },
        { "0500", new NavigateMessage(NavigationTarget.Back) },
        { "08", new KeyframeRequestMessage() },
        { "0901", new RotateMessage(1) },
        { "0BFFFFFFFB0000000C", new MouseMoveMessage(-5, 12) },
        { "0C0201", new MouseButtonMessage(MouseButton.Right, KeyAction.Up) },
        { "0E00", new VisibilityMessage(false) },
        { "0E01", new VisibilityMessage(true) },
    };

    [Fact]
    public void OpusCarriesStreamAudioAtATenthOfThePcmBitrate()
    {
        // One second of a 440 Hz tone, fed in the uneven chunks WASAPI hands out.
        const int samples = OpusAudio.SampleRate;
        var pcm = new byte[samples * OpusAudio.Channels * sizeof(short)];
        for (var i = 0; i < samples; i++)
        {
            var value = (short)(Math.Sin(2 * Math.PI * 440 * i / OpusAudio.SampleRate) * 12_000);
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 4), value);
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 4 + 2), value);
        }

        using var encoder = new OpusAudioEncoder();
        using var decoder = new OpusAudioDecoder();
        var packets = new List<byte[]>();
        for (var offset = 0; offset < pcm.Length; offset += 1_764)
        {
            encoder.Encode(pcm.AsSpan(offset, Math.Min(1_764, pcm.Length - offset)), packets.Add);
        }

        Assert.Equal(50, packets.Count);
        var bitsPerSecond = packets.Sum(packet => packet.Length) * 8;
        Assert.InRange(bitsPerSecond, 100_000, 200_000);

        var decoded = packets.SelectMany(packet => decoder.Decode(packet)).ToArray();
        Assert.Equal(pcm.Length, decoded.Length);

        // Past the codec's start-up, the tone comes back close to what went in (the delay is
        // under a few milliseconds, so compare the strength rather than sample by sample).
        static double Rms(byte[] audio, int from) =>
            Math.Sqrt(Enumerable.Range(from, 9_600).Average(i => Math.Pow(BitConverter.ToInt16(audio, i * 4), 2)));
        Assert.InRange(Rms(decoded, 24_000) / Rms(pcm, 24_000), 0.9, 1.1);
    }

    [Fact]
    public void OpusDecoderIgnoresGarbage() =>
        Assert.Empty(new OpusAudioDecoder().Decode([0xFF, 0xFF, 0xFF]));

    [Theory]
    [MemberData(nameof(ControlVectors))]
    public void ControlMessagesMatchTheAndroidCodec(string hex, ControlMessage message)
    {
        Assert.Equal(hex, Convert.ToHexString(ControlMessages.Encode(message)));
        Assert.Equal(message, ControlMessages.Decode(Convert.FromHexString(hex)));
    }

    [Fact]
    public void RecordHeaderCarriesTheChannelInItsSecondByte()
    {
        var header = StreamFraming.EncodeHeader(StreamRecordFlags.Keyframe, 5, 1_000, 3, StreamChannel.Audio);
        Assert.Equal("0201000500000000000003E800000003", Convert.ToHexString(header));
        var decoded = StreamFraming.DecodeHeader(header);
        Assert.Equal(StreamChannel.Audio, decoded.Channel);
        Assert.True(decoded.IsKeyframe);
        Assert.Equal(1_000, decoded.PresentationTimeUs);
    }

    [Fact]
    public void AndroidKeysMapToWindowsKeys()
    {
        Assert.Equal((ushort)0x08, KeyMapWindows.VirtualKey(67)); // DEL -> Backspace
        Assert.Equal((ushort)0x41, KeyMapWindows.VirtualKey(29)); // A
        Assert.Equal((ushort)0x70, KeyMapWindows.VirtualKey(131)); // F1
        Assert.Equal(new ushort[] { 0x11, 0x10 }, KeyMapWindows.Modifiers(0x1000 | 0x1));
    }

    [Fact]
    public void StreamsArePhoneShapedAndCapped()
    {
        Assert.Equal((1920, 860), WindowStreamService.OutputSize((1280, 2856), landscape: true));
        Assert.Equal((860, 1920), WindowStreamService.OutputSize((1280, 2856), landscape: false));
        Assert.Equal((864, 1920), WindowStreamService.OutputSize((1080, 2400), landscape: false));

        // Tablets keep their resolution up to 2560 on the long edge.
        Assert.Equal((2560, 1600), WindowStreamService.OutputSize((1600, 2560), landscape: true));
        Assert.Equal((2560, 1846), WindowStreamService.OutputSize((2152, 2984), landscape: true));
    }

    [Fact]
    public void FitRectKeepsThePhoneShapeInsideTheWorkArea()
    {
        var fit = DesktopWindows.FitRect((0, 0, 2560, 1032), 2856.0 / 1280);
        Assert.Equal(1032, fit.Height);
        Assert.Equal(2302, fit.Width);
        Assert.Equal(129, fit.Left);
    }

    [Fact]
    public void FramePointsLandOnTheWindowThroughTheLetterbox()
    {
        // A 4:3 window letterboxed into a 1920x860 frame: the frame's centre is the window's centre,
        // and a point in the left bar clamps to the window's edge.
        var window = (100, 50, 800, 600);
        Assert.Equal((500, 350), WindowStreamService.FrameToScreen(960, 430, 1920, 860, (800, 600), window));
        Assert.Equal((100, 350), WindowStreamService.FrameToScreen(10, 430, 1920, 860, (800, 600), window));
    }

    [Fact]
    public void SpsRewriteLeavesOtherUnitsAlone()
    {
        var slice = Convert.FromHexString("0000000165888400FF");
        Assert.Equal(slice, H264Sps.DeclareNoReordering(slice));
    }
}

public class BitrateLadderTests
{
    [Fact]
    public void StepsDownAtOnceAndUpSlowly()
    {
        var start = DateTimeOffset.UtcNow;
        var ladder = new Baton.Host.Streaming.BitrateLadder(Baton.Host.Streaming.StreamQuality.Auto, remote: false, start);
        Assert.Equal(12_000_000, ladder.Bitrate);

        // A drop right after starting waits for the spacing; then each drop is one step.
        Assert.Null(ladder.Dropped(start.AddSeconds(1)));
        Assert.Equal(6_000_000, ladder.Dropped(start.AddSeconds(3)));
        Assert.Equal(3_000_000, ladder.Dropped(start.AddSeconds(5)));

        // Recovery needs 10 quiet seconds per step.
        Assert.Null(ladder.Tick(start.AddSeconds(10)));
        Assert.Equal(6_000_000, ladder.Tick(start.AddSeconds(15)));
        Assert.Null(ladder.Tick(start.AddSeconds(20)));
        Assert.Equal(12_000_000, ladder.Tick(start.AddSeconds(25)));
        Assert.Null(ladder.Tick(start.AddSeconds(60)));
    }

    [Fact]
    public void QualityAndLinkSetTheRange()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(6_000_000, new Baton.Host.Streaming.BitrateLadder(Baton.Host.Streaming.StreamQuality.Auto, remote: true, now).Bitrate);
        var high = new Baton.Host.Streaming.BitrateLadder(Baton.Host.Streaming.StreamQuality.High, remote: true, now);
        Assert.Equal(12_000_000, high.Bitrate);
        Assert.Null(high.Dropped(now.AddSeconds(5)));
        Assert.Equal(3_000_000, new Baton.Host.Streaming.BitrateLadder(Baton.Host.Streaming.StreamQuality.DataSaver, remote: false, now).Bitrate);

        Assert.True(Baton.Host.Streaming.BitrateLadder.IsRemote(System.Net.IPAddress.Parse("100.101.1.2")));
        Assert.False(Baton.Host.Streaming.BitrateLadder.IsRemote(System.Net.IPAddress.Parse("192.168.178.181")));
        Assert.False(Baton.Host.Streaming.BitrateLadder.IsRemote(System.Net.IPAddress.Parse("100.200.1.2")));

        // The lowest step also sends fewer pixels.
        var saver = new Baton.Host.Streaming.BitrateLadder(Baton.Host.Streaming.StreamQuality.DataSaver, remote: false, now);
        Assert.False(saver.Reduced);
        saver.Dropped(now.AddSeconds(3));
        Assert.True(saver.Reduced);
        Assert.Equal((1280, 576), Baton.Host.Streaming.BitrateLadder.Reduce((1920, 864)));
        Assert.Equal((864, 400), Baton.Host.Streaming.BitrateLadder.Reduce((864, 400)));
    }

    [Fact]
    public void CongestionIsQueueingAboveTheLinksOwnRoundTrip()
    {
        var now = DateTimeOffset.UtcNow;
        var ladder = new Baton.Host.Streaming.BitrateLadder(Baton.Host.Streaming.StreamQuality.Auto, remote: true, now);
        static TimeSpan ms(double value) => TimeSpan.FromMilliseconds(value);

        // Mobile data: 180 ms is the link itself, not congestion; 500 ms is 320 ms of queueing.
        Assert.False(ladder.Congested(ms(180), now));
        Assert.False(ladder.Congested(ms(400), now.AddSeconds(2)));
        Assert.True(ladder.Congested(ms(500), now.AddSeconds(4)));

        // The fast sample ages out after half a minute; a slower link becomes the new normal.
        Assert.False(ladder.Congested(ms(400), now.AddSeconds(40)));
        Assert.False(ladder.Congested(ms(450), now.AddSeconds(42)));

        // At home the same rule still catches a filling link.
        var home = new Baton.Host.Streaming.BitrateLadder(Baton.Host.Streaming.StreamQuality.Auto, remote: false, now);
        Assert.False(home.Congested(ms(5), now));
        Assert.True(home.Congested(ms(300), now.AddSeconds(2)));
    }
}
