namespace Baton.Media;

/// <summary>Splitting an H.264 Annex-B access unit into its NAL units.</summary>
public static class AnnexB
{
    /// <summary>
    /// Splits <paramref name="data"/> into the parameter sets (SPS/PPS, with start codes) and the
    /// rest. A decoder is configured with the first part and fed the second.
    /// </summary>
    public static (byte[] Config, byte[] Frame) SplitParameterSets(byte[] data)
    {
        var config = new MemoryStream();
        var frame = new MemoryStream();
        foreach (var (start, end) in Units(data))
        {
            var header = data[start + StartCodeLength(data, start)];
            var type = header & 0x1F;
            (type is 7 or 8 ? config : frame).Write(data, start, end - start);
        }

        return (config.ToArray(), frame.ToArray());
    }

    /// <summary>Each NAL unit as [start, end) including its start code.</summary>
    public static IEnumerable<(int Start, int End)> Units(byte[] data)
    {
        var starts = new List<int>();
        for (var index = 0; index + 3 <= data.Length; index++)
        {
            if (data[index] == 0 && data[index + 1] == 0 && (data[index + 2] == 1 || (index + 3 < data.Length && data[index + 2] == 0 && data[index + 3] == 1)))
            {
                starts.Add(index);
                index += data[index + 2] == 1 ? 2 : 3;
            }
        }

        for (var index = 0; index < starts.Count; index++)
        {
            yield return (starts[index], index + 1 < starts.Count ? starts[index + 1] : data.Length);
        }
    }

    private static int StartCodeLength(byte[] data, int start) => data[start + 2] == 1 ? 3 : 4;
}
