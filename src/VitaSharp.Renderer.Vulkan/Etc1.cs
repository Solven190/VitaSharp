namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// CPU decoding of GXM's ETC1 textures (SCE_GXM_TEXTURE_BASE_FORMAT_ETC1, 0x84000000; vita-headers gxm.h: "Ericsson
/// Texture Compression (ETC1). Requires SCE_GXM_INITIALIZE_FLAG_EXTENDED_FORMAT"; its only format is ETC1_1BGR, RGB with
/// alpha 1). The blocks follow the Khronos definition (OES_compressed_ETC1_RGB8_texture): 8 bytes per 4x4 texels, two
/// 32-bit words; the first word holds the two base colours (individual mode: 4+4 bits per channel; differential mode: a
/// 5-bit base and a 3-bit signed delta), the two 3-bit table codewords, the diff bit and the flip bit; the second word
/// holds the 2-bit pixel indices, most significant bits first, texel (x, y) at bit x * 4 + y. Khronos stores each word
/// big-endian; the SGX reads them little-endian: vitaGL, which runs on the console, byte-swaps each 32-bit word of a
/// Khronos block when it uploads an ETC1 texture (texture_swizzler.cpp: CopyETCBlock's __builtin_bswap32 per word,
/// CopyETC1Tile's vrev32q_u8), so <see cref="Decode"/> takes the blocks in that memory order and
/// <see cref="DecodeBlock"/> in the Khronos one. Desktop GPUs rarely sample ETC2 (whose RGB8 mode is ETC1), so the
/// texels are decoded to RGBA8 on every device.
/// </summary>
public static class Etc1
{
    /// <summary>The intensity modifiers of the eight codewords (Khronos table 3.17.2), for index values 0-3 (msb, lsb).</summary>
    private static readonly int[,] s_modifiers =
    {
        { 2, 8, -2, -8 },
        { 5, 17, -5, -17 },
        { 9, 29, -9, -29 },
        { 13, 42, -13, -42 },
        { 18, 60, -18, -60 },
        { 24, 80, -24, -80 },
        { 33, 106, -33, -106 },
        { 47, 183, -47, -183 },
    };

    /// <summary>Decodes one level (blocks in row order, ((width+3)/4)*((height+3)/4) of them, each word little-endian as
    /// GXM has them) into width*height RGBA8 texels.</summary>
    public static Texels Decode(ReadOnlySpan<byte> blocks, uint width, uint height)
    {
        uint blocksWide = (width + 3) / 4;
        uint blocksHigh = (height + 3) / 4;
        var texels = Texels.Rent(width * height * 4L);
        byte[] destination = texels.Array;
        Span<byte> block = stackalloc byte[4 * 4 * 3];
        Span<byte> khronos = stackalloc byte[8];
        for (uint blockY = 0; blockY < blocksHigh; blockY++)
        {
            for (uint blockX = 0; blockX < blocksWide; blockX++)
            {
                int offset = (int)((blockY * blocksWide + blockX) * 8);
                if (offset + 8 > blocks.Length)
                {
                    return texels;
                }

                ReadOnlySpan<byte> stored = blocks.Slice(offset, 8);
                for (int i = 0; i < 8; i++)
                {
                    // each 32-bit word back to the Khronos (big-endian) byte order
                    khronos[i] = stored[i ^ 3];
                }

                DecodeBlock(khronos, block);
                for (uint y = 0; y < 4; y++)
                {
                    uint pixelY = blockY * 4 + y;
                    if (pixelY >= height)
                    {
                        break;
                    }

                    for (uint x = 0; x < 4; x++)
                    {
                        uint pixelX = blockX * 4 + x;
                        if (pixelX >= width)
                        {
                            break;
                        }

                        int source = (int)((y * 4 + x) * 3);
                        long target = (pixelY * width + pixelX) * 4L;
                        destination[target] = block[source];
                        destination[target + 1] = block[source + 1];
                        destination[target + 2] = block[source + 2];
                        destination[target + 3] = 255;
                    }
                }
            }
        }

        return texels;
    }

    /// <summary>Decodes one 8-byte block in the Khronos byte order into 16 RGB texels, row blockY row (texel (x, y) at (y * 4 + x) * 3).</summary>
    public static void DecodeBlock(ReadOnlySpan<byte> source, Span<byte> rgb)
    {
        bool differential = (source[3] & 2) != 0;
        bool flip = (source[3] & 1) != 0;
        int codeword1 = source[3] >> 5;
        int codeword2 = (source[3] >> 2) & 7;
        Span<int> base1 = stackalloc int[3];
        Span<int> base2 = stackalloc int[3];
        for (int channel = 0; channel < 3; channel++)
        {
            int value = source[channel];
            if (differential)
            {
                int first = value >> 3;
                int delta = value & 7;
                if (delta >= 4)
                {
                    delta -= 8;
                }

                int second = (first + delta) & 31;
                base1[channel] = Expand5(first);
                base2[channel] = Expand5(second);
            }
            else
            {
                base1[channel] = Expand4(value >> 4);
                base2[channel] = Expand4(value & 15);
            }
        }

        int msb = source[4] << 8 | source[5];
        int lsb = source[6] << 8 | source[7];
        for (int x = 0; x < 4; x++)
        {
            for (int y = 0; y < 4; y++)
            {
                int bit = x * 4 + y;
                int index = ((msb >> bit) & 1) << 1 | ((lsb >> bit) & 1);
                bool second = flip ? y >= 2 : x >= 2;
                int modifier = s_modifiers[second ? codeword2 : codeword1, index];
                Span<int> baseColour = second ? base2 : base1;
                int target = (y * 4 + x) * 3;
                for (int channel = 0; channel < 3; channel++)
                {
                    rgb[target + channel] = (byte)Math.Clamp(baseColour[channel] + modifier, 0, 255);
                }
            }
        }
    }

    private static int Expand4(int value)
    {
        return value << 4 | value;
    }

    private static int Expand5(int value)
    {
        return value << 3 | value >> 2;
    }
}
