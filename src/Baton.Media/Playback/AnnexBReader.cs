namespace Baton.Media.Playback;

public readonly record struct AccessUnit(int Offset, int Length, bool IsKeyframe, bool HasParameterSets);

/// <summary>
/// Splits an H.264 Annex-B byte stream into access units - one per coded picture.
///
/// The decoder is fed one access unit per <c>IMFSample</c>, so the sample count equals the frame
/// count and the per-frame timings mean what they say. Picture boundaries are found the way every
/// H.264 parser finds them: a VCL NAL whose <c>first_mb_in_slice</c> is 0 starts a new picture.
/// Relying on access unit delimiters instead does not work here, because Android's MediaCodec
/// emits long runs of bare slice NALs with nothing between them.
/// </summary>
public static class AnnexBReader
{
    private const int NalSliceNonIdr = 1;
    private const int NalSliceIdr = 5;
    private const int NalSei = 6;
    private const int NalSps = 7;
    private const int NalPps = 8;
    private const int NalAud = 9;

    /// <summary>
    /// Returns access units as ranges over <paramref name="stream"/>. Each range starts at a start
    /// code and runs to the byte before the next access unit, so the payload feeds Media Foundation
    /// directly with no repackaging.
    /// </summary>
    public static IReadOnlyList<AccessUnit> Split(ReadOnlySpan<byte> stream)
    {
        var nals = FindNals(stream);
        if (nals.Count == 0)
        {
            return Array.Empty<AccessUnit>();
        }

        var boundaries = FindPictureBoundaries(stream, nals);
        var units = new List<AccessUnit>(boundaries.Count);

        for (var index = 0; index < boundaries.Count; index++)
        {
            var start = boundaries[index];
            var end = index + 1 < boundaries.Count ? boundaries[index + 1] : stream.Length;
            units.Add(Describe(stream, nals, start, end));
        }

        return units;
    }

    /// <summary>
    /// The leading SPS/PPS run, which Media Foundation wants as <c>MF_MT_MPEG_SEQUENCE_HEADER</c>
    /// on the input media type. Empty when the stream does not begin with parameter sets.
    /// </summary>
    public static byte[] ExtractParameterSets(ReadOnlySpan<byte> stream)
    {
        var nals = FindNals(stream);
        var start = -1;
        var end = -1;

        foreach (var nal in nals)
        {
            var type = stream[nal.PayloadStart] & 0x1F;
            if (type is NalSps or NalPps)
            {
                if (start < 0)
                {
                    start = nal.Start;
                }

                end = nal.End;
            }
            else if (start >= 0)
            {
                break;
            }
        }

        return start < 0 ? Array.Empty<byte>() : stream[start..end].ToArray();
    }

    private static List<int> FindPictureBoundaries(ReadOnlySpan<byte> stream, List<Nal> nals)
    {
        var boundaries = new List<int> { nals[0].Start };
        var sawSlice = false;

        // Non-VCL NALs that follow a picture belong to the NEXT access unit, so the boundary is
        // placed at the start of that run rather than at the new slice.
        var prefixStart = -1;

        foreach (var nal in nals)
        {
            var type = stream[nal.PayloadStart] & 0x1F;
            var isSlice = type is NalSliceNonIdr or NalSliceIdr;

            if (isSlice)
            {
                if (sawSlice && StartsNewPicture(stream, nal))
                {
                    boundaries.Add(prefixStart >= 0 ? prefixStart : nal.Start);
                    sawSlice = false;
                }

                sawSlice = true;
                prefixStart = -1;
            }
            else if (sawSlice && type is NalSei or NalSps or NalPps or NalAud && prefixStart < 0)
            {
                prefixStart = nal.Start;
            }
        }

        return boundaries;
    }

    /// <summary>
    /// True when the slice header's <c>first_mb_in_slice</c> is zero, which marks the first slice
    /// of a picture. <c>first_mb_in_slice</c> is the leading ue(v) of the slice header, so a set
    /// top bit is exactly the encoding of zero.
    /// </summary>
    private static bool StartsNewPicture(ReadOnlySpan<byte> stream, Nal nal)
    {
        var sliceHeader = nal.PayloadStart + 1;
        return sliceHeader < stream.Length && (stream[sliceHeader] & 0x80) != 0;
    }

    private static AccessUnit Describe(ReadOnlySpan<byte> stream, List<Nal> nals, int start, int end)
    {
        var isKeyframe = false;
        var hasParameterSets = false;

        foreach (var nal in nals)
        {
            if (nal.Start < start)
            {
                continue;
            }

            if (nal.Start >= end)
            {
                break;
            }

            var type = stream[nal.PayloadStart] & 0x1F;
            if (type == NalSliceIdr)
            {
                isKeyframe = true;
            }
            else if (type is NalSps or NalPps)
            {
                hasParameterSets = true;
            }
        }

        return new AccessUnit(start, end - start, isKeyframe, hasParameterSets);
    }

    private static List<Nal> FindNals(ReadOnlySpan<byte> stream)
    {
        var nals = new List<Nal>();
        var starts = new List<(int Start, int PayloadStart)>();

        for (var index = 0; index + 3 < stream.Length; index++)
        {
            if (stream[index] != 0x00 || stream[index + 1] != 0x00)
            {
                continue;
            }

            if (stream[index + 2] == 0x01)
            {
                starts.Add((index, index + 3));
                index += 2;
            }
            else if (stream[index + 2] == 0x00 && index + 4 < stream.Length && stream[index + 3] == 0x01)
            {
                starts.Add((index, index + 4));
                index += 3;
            }
        }

        for (var index = 0; index < starts.Count; index++)
        {
            var (start, payloadStart) = starts[index];
            if (payloadStart >= stream.Length)
            {
                break;
            }

            var end = index + 1 < starts.Count ? starts[index + 1].Start : stream.Length;
            nals.Add(new Nal(start, payloadStart, end));
        }

        return nals;
    }

    private readonly record struct Nal(int Start, int PayloadStart, int End);
}
