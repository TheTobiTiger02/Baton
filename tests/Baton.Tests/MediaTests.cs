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
        { "0500", new NavigateMessage(NavigationTarget.Back) },
        { "08", new KeyframeRequestMessage() },
        { "0901", new RotateMessage(1) },
        { "0BFFFFFFFB0000000C", new MouseMoveMessage(-5, 12) },
        { "0C0201", new MouseButtonMessage(MouseButton.Right, KeyAction.Up) },
    };

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
