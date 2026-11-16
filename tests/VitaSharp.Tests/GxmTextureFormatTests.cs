using Silk.NET.Vulkan;
using VitaSharp.Core.Gxm;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// GXM texture and colour formats against their definitions in vita-headers (gxm.h), the findings of the VitaSDK test
/// game (11/11/2026): U2U10U10U10_ABGR is A2B10G10R10 (red in the low 10 bits) and ETC1 (0x84000000) is decoded.
/// </summary>
public sealed class GxmTextureFormatTests
{
    private const uint U2U10U10U10Abgr = 0x0E000000;
    private const uint ColorU2U10U10U10Abgr = 0x60800000;
    private const uint Etc1Base = 0x84000000;

    [Fact]
    public void U2U10U10U10AbgrKeepsRedInTheLowBits()
    {
        // gxm.h: SCE_GXM_COLOR_FORMAT_U2U10U10U10_ABGR "32-bit unsigned A2B10G10R10 color format"; the texture format
        // of the same name has the same memory order. Vulkan's A2B10G10R10 has red in bits 0-9 and alpha in 30-31.
        Assert.Equal(Format.A2B10G10R10UnormPack32, Formats.Texture(GxmFormats.BaseOf(U2U10U10U10Abgr)).Format);
        Assert.Equal(Format.A2B10G10R10UnormPack32, Formats.ColorFormat(ColorU2U10U10U10Abgr));
        // _ABGR is the identity swizzle, as for U8U8U8U8_ABGR on R8G8B8A8
        ComponentMapping mapping = Formats.TextureSwizzle(U2U10U10U10Abgr);
        Assert.Equal(ComponentSwizzle.R, mapping.R);
        Assert.Equal(ComponentSwizzle.B, mapping.B);
    }

    [Fact]
    public void Etc1IsDecodedOnTheCpu()
    {
        TexFormatInfo info = Formats.Texture(GxmFormats.BaseOf(Etc1Base));
        Assert.Equal(TexConvert.Etc1, info.Convert);
        Assert.Equal(8, info.BlockBytes);
        Assert.Equal(4, info.BlockW);
        Assert.Equal(4, info.BlockH);
    }

    [Fact]
    public void Etc1IndividualBlockDecodesItsBaseColourPlusTheModifier()
    {
        // the test game's block: both halves 4-bit (8, 4, 12), codeword 0, every pixel index 0 (+2)
        byte[] block = [0x88, 0x44, 0xCC, 0x00, 0, 0, 0, 0];
        using Texels texels = Etc1.Decode(AsGxm(block), 4, 4);
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(0x8A, texels.Span[i * 4]);
            Assert.Equal(0x46, texels.Span[i * 4 + 1]);
            Assert.Equal(0xCE, texels.Span[i * 4 + 2]);
            Assert.Equal(0xFF, texels.Span[i * 4 + 3]);
        }
    }

    [Fact]
    public void Etc1DifferentialFlippedBlockFollowsTheKhronosLayout()
    {
        // differential mode, flipped (top 4x2 / bottom 4x2): red base 16 with delta -1 (132 and 123), green and blue 0;
        // codeword 7 on top, 0 below. Texel (0, 0): index 3 (-183, clamped to 0); texel (0, 2): index 1 (+8); texel
        // (1, 0): index 2 (-47); every other texel index 0
        byte[] block = new byte[8];
        block[0] = 16 << 3 | 7;
        block[3] = 7 << 5 | 0 << 2 | 2 | 1;
        int msb = 1 << (0 * 4 + 0) | 1 << (1 * 4 + 0);
        int lsb = 1 << (0 * 4 + 0) | 1 << (0 * 4 + 2);
        block[4] = (byte)(msb >> 8);
        block[5] = (byte)msb;
        block[6] = (byte)(lsb >> 8);
        block[7] = (byte)lsb;
        using Texels texels = Etc1.Decode(AsGxm(block), 4, 4);
        byte Red(int x, int y) => texels.Span[(y * 4 + x) * 4];
        Assert.Equal(0, Red(0, 0));
        Assert.Equal(132 - 47, Red(1, 0));
        Assert.Equal(132 + 47, Red(2, 0));
        Assert.Equal(123 + 8, Red(0, 2));
        Assert.Equal(123 + 2, Red(3, 3));
        Assert.Equal(47, texels.Span[(0 * 4 + 2) * 4 + 1]); // green of texel (2, 0): 0 + 47
    }

    /// <summary>
    /// GXM's ETC1 blocks have each 32-bit word little-endian, as vitaGL uploads them on the console (each word of a
    /// Khronos block byte-swapped: texture_swizzler.cpp, CopyETCBlock / CopyETC1Tile): a texture in that order decodes to
    /// its colours, block by block, not to the noise a Khronos-order read gives (Real Racing 3, 16/11/2026: every ETC1
    /// texture of the track and the car came out as cyan and red noise).
    /// </summary>
    [Fact]
    public void Etc1BlocksAreReadWithEachWordLittleEndianAsTheConsoleHasThem()
    {
        // two blocks side by side: individual mode, first block (8, 4, 12), second (2, 14, 6), codeword 0; the first
        // with every texel at index 1 (+8), the second at index 2 (-2)
        byte[] first = [0x88, 0x44, 0xCC, 0x00, 0x00, 0x00, 0xFF, 0xFF];
        byte[] second = [0x22, 0xEE, 0x66, 0x00, 0xFF, 0xFF, 0x00, 0x00];
        byte[] texture = [.. AsGxm(first), .. AsGxm(second)];
        using Texels texels = Etc1.Decode(texture, 8, 4);
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                int at = (y * 8 + x) * 4;
                (int r, int g, int b) = x < 4 ? (0x88 + 8, 0x44 + 8, 0xCC + 8) : (0x22 - 2, 0xEE - 2, 0x66 - 2);
                Assert.Equal((r, g, b, 0xFF), (texels.Span[at], texels.Span[at + 1], texels.Span[at + 2], texels.Span[at + 3]));
            }
        }
    }

    /// <summary>A Khronos-order ETC1 block as GXM memory has it: each 32-bit word byte-swapped (vitaGL's upload).</summary>
    private static byte[] AsGxm(byte[] khronos)
    {
        byte[] gxm = new byte[khronos.Length];
        for (int i = 0; i < khronos.Length; i++)
        {
            gxm[i] = khronos[i ^ 3];
        }

        return gxm;
    }
}
