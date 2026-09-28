namespace Baton.Media;

/// <summary>
/// Rewrites H.264 sequence parameter sets so decoders show each frame the moment it is decoded.
/// </summary>
/// <remarks>
/// Without a VUI <c>bitstream_restriction</c>, a decoder must assume frames may be reordered and
/// holds up to a full picture buffer (about a second at an idle window's frame rate) before
/// showing anything. The encoder never reorders (no B-frames), so declaring
/// <c>max_num_reorder_frames = 0</c> is true and removes that wait.
/// </remarks>
public static class H264Sps
{
    /// <summary>
    /// Returns <paramref name="annexB"/> with every SPS declaring no reordering; other NAL units are
    /// copied unchanged. An SPS that cannot be parsed is also copied unchanged.
    /// </summary>
    public static byte[] DeclareNoReordering(byte[] annexB)
    {
        var output = new MemoryStream(annexB.Length + 16);
        foreach (var (start, end) in AnnexB.Units(annexB))
        {
            var codeLength = annexB[start + 2] == 1 ? 3 : 4;
            var nal = start + codeLength;
            if (nal < end && (annexB[nal] & 0x1F) == 7 && TryRewrite(annexB.AsSpan(nal + 1, end - nal - 1), out var rewritten))
            {
                output.Write(annexB, start, codeLength + 1);
                output.Write(rewritten);
            }
            else
            {
                output.Write(annexB, start, end - start);
            }
        }

        return output.ToArray();
    }

    /// <summary>Rewrites one SPS payload (after the NAL header byte, emulation-prevented).</summary>
    public static bool TryRewrite(ReadOnlySpan<byte> payload, out byte[] rewritten)
    {
        rewritten = [];
        try
        {
            var reader = new BitReader(Unescape(payload));
            var writer = new BitWriter();
            var profile = (int)Copy(reader, writer, 8);
            Copy(reader, writer, 8); // constraint flags
            Copy(reader, writer, 8); // level
            CopyUe(reader, writer); // seq_parameter_set_id
            if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
            {
                var chroma = CopyUe(reader, writer);
                if (chroma == 3)
                {
                    Copy(reader, writer, 1);
                }

                CopyUe(reader, writer); // bit_depth_luma_minus8
                CopyUe(reader, writer); // bit_depth_chroma_minus8
                Copy(reader, writer, 1); // qpprime_y_zero_transform_bypass_flag
                if (Copy(reader, writer, 1) == 1) // seq_scaling_matrix_present_flag
                {
                    for (var list = 0; list < (chroma != 3 ? 8 : 12); list++)
                    {
                        if (Copy(reader, writer, 1) == 1)
                        {
                            CopyScalingList(reader, writer, list < 6 ? 16 : 64);
                        }
                    }
                }
            }

            CopyUe(reader, writer); // log2_max_frame_num_minus4
            var pocType = CopyUe(reader, writer);
            if (pocType == 0)
            {
                CopyUe(reader, writer);
            }
            else if (pocType == 1)
            {
                Copy(reader, writer, 1);
                CopySe(reader, writer);
                CopySe(reader, writer);
                var cycle = CopyUe(reader, writer);
                for (var index = 0; index < cycle; index++)
                {
                    CopySe(reader, writer);
                }
            }

            var referenceFrames = CopyUe(reader, writer);
            Copy(reader, writer, 1); // gaps_in_frame_num_value_allowed_flag
            CopyUe(reader, writer); // pic_width_in_mbs_minus1
            CopyUe(reader, writer); // pic_height_in_map_units_minus1
            if (Copy(reader, writer, 1) == 0) // frame_mbs_only_flag
            {
                Copy(reader, writer, 1);
            }

            Copy(reader, writer, 1); // direct_8x8_inference_flag
            if (Copy(reader, writer, 1) == 1) // frame_cropping_flag
            {
                for (var index = 0; index < 4; index++)
                {
                    CopyUe(reader, writer);
                }
            }

            var vui = reader.Read(1) == 1;
            writer.Write(1, 1);
            if (vui)
            {
                CopyVuiUntilRestriction(reader, writer);
                if (reader.Read(1) == 1)
                {
                    // Replaced below.
                    reader.Read(1);
                    for (var index = 0; index < 6; index++)
                    {
                        reader.ReadUe();
                    }
                }
            }
            else
            {
                // aspect ratio, overscan, video signal, chroma location, timing, NAL HRD, VCL HRD, pic struct: all absent.
                writer.Write(0, 8);
            }

            writer.Write(1, 1); // bitstream_restriction_flag
            writer.Write(1, 1); // motion_vectors_over_pic_boundaries_flag
            writer.WriteUe(2); // max_bytes_per_pic_denom
            writer.WriteUe(1); // max_bits_per_mb_denom
            writer.WriteUe(16); // log2_max_mv_length_horizontal
            writer.WriteUe(16); // log2_max_mv_length_vertical
            writer.WriteUe(0); // max_num_reorder_frames
            writer.WriteUe(Math.Max(1, referenceFrames)); // max_dec_frame_buffering
            writer.Write(1, 1); // rbsp_stop_one_bit
            rewritten = Escape(writer.ToArray());
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static void CopyVuiUntilRestriction(BitReader reader, BitWriter writer)
    {
        if (Copy(reader, writer, 1) == 1) // aspect_ratio_info_present_flag
        {
            if (Copy(reader, writer, 8) == 255)
            {
                Copy(reader, writer, 16);
                Copy(reader, writer, 16);
            }
        }

        if (Copy(reader, writer, 1) == 1) // overscan_info_present_flag
        {
            Copy(reader, writer, 1);
        }

        if (Copy(reader, writer, 1) == 1) // video_signal_type_present_flag
        {
            Copy(reader, writer, 4);
            if (Copy(reader, writer, 1) == 1)
            {
                Copy(reader, writer, 24);
            }
        }

        if (Copy(reader, writer, 1) == 1) // chroma_loc_info_present_flag
        {
            CopyUe(reader, writer);
            CopyUe(reader, writer);
        }

        if (Copy(reader, writer, 1) == 1) // timing_info_present_flag
        {
            Copy(reader, writer, 32);
            Copy(reader, writer, 32);
            Copy(reader, writer, 1);
        }

        var nalHrd = Copy(reader, writer, 1) == 1;
        if (nalHrd)
        {
            CopyHrd(reader, writer);
        }

        var vclHrd = Copy(reader, writer, 1) == 1;
        if (vclHrd)
        {
            CopyHrd(reader, writer);
        }

        if (nalHrd || vclHrd)
        {
            Copy(reader, writer, 1); // low_delay_hrd_flag
        }

        Copy(reader, writer, 1); // pic_struct_present_flag
    }

    private static void CopyHrd(BitReader reader, BitWriter writer)
    {
        var count = CopyUe(reader, writer);
        Copy(reader, writer, 8); // bit_rate_scale, cpb_size_scale
        for (var index = 0; index <= count; index++)
        {
            CopyUe(reader, writer);
            CopyUe(reader, writer);
            Copy(reader, writer, 1);
        }

        Copy(reader, writer, 20);
    }

    private static void CopyScalingList(BitReader reader, BitWriter writer, int size)
    {
        int last = 8, next = 8;
        for (var index = 0; index < size; index++)
        {
            if (next != 0)
            {
                var delta = CopySe(reader, writer);
                next = (last + delta + 256) % 256;
            }

            last = next == 0 ? last : next;
        }
    }

    private static uint Copy(BitReader reader, BitWriter writer, int bits)
    {
        var value = reader.Read(bits);
        writer.Write(value, bits);
        return value;
    }

    private static int CopyUe(BitReader reader, BitWriter writer)
    {
        var value = reader.ReadUe();
        writer.WriteUe(value);
        return value;
    }

    private static int CopySe(BitReader reader, BitWriter writer)
    {
        var code = reader.ReadUe();
        writer.WriteUe(code);
        return (code & 1) == 1 ? (code + 1) / 2 : -(code / 2);
    }

    private static byte[] Unescape(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length);
        var zeros = 0;
        foreach (var value in data)
        {
            if (zeros >= 2 && value == 3)
            {
                zeros = 0;
                continue;
            }

            zeros = value == 0 ? zeros + 1 : 0;
            output.Add(value);
        }

        return [.. output];
    }

    private static byte[] Escape(byte[] data)
    {
        var output = new List<byte>(data.Length + 4);
        var zeros = 0;
        foreach (var value in data)
        {
            if (zeros >= 2 && value <= 3)
            {
                output.Add(3);
                zeros = 0;
            }

            zeros = value == 0 ? zeros + 1 : 0;
            output.Add(value);
        }

        return [.. output];
    }

    private sealed class BitReader(byte[] data)
    {
        private int _position;

        public uint Read(int bits)
        {
            uint value = 0;
            for (var index = 0; index < bits; index++)
            {
                if (_position >= data.Length * 8)
                {
                    throw new InvalidDataException("SPS ended early.");
                }

                value = (value << 1) | (uint)((data[_position >> 3] >> (7 - (_position & 7))) & 1);
                _position++;
            }

            return value;
        }

        public int ReadUe()
        {
            var zeros = 0;
            while (Read(1) == 0)
            {
                if (++zeros > 31)
                {
                    throw new InvalidDataException("Bad Exp-Golomb code.");
                }
            }

            return (int)((1u << zeros) - 1 + (zeros == 0 ? 0 : Read(zeros)));
        }
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _bits;

        public void Write(uint value, int bits)
        {
            for (var index = bits - 1; index >= 0; index--)
            {
                if ((_bits & 7) == 0)
                {
                    _bytes.Add(0);
                }

                if (((value >> index) & 1) == 1)
                {
                    _bytes[^1] |= (byte)(0x80 >> (_bits & 7));
                }

                _bits++;
            }
        }

        public void WriteUe(int value)
        {
            var code = (uint)value + 1;
            var length = 32 - System.Numerics.BitOperations.LeadingZeroCount(code);
            Write(0, length - 1);
            Write(code, length);
        }

        public byte[] ToArray() => [.. _bytes];
    }
}
