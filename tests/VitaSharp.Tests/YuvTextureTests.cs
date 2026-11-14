using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// YUV420 textures (YUV420P2 and YUV420P3, the movie players' frames) converted to RGBA by rows from one read of the
/// planes, with table terms: byte for byte what the per-texel conversion gives (TextureDecoder.Yuv420Texel), for both
/// plane layouts, both chroma orders, both colour ranges, odd sizes and padded pitches. A movie texture is converted
/// every frame; F1 2011's intro movie on the phone cost the GPU worker ~38 ms per frame through the per-texel path.
/// </summary>
public sealed class YuvTextureTests
{
    private const uint Base = 0x81000000;

    [Theory]
    [InlineData(960u, 544u, 960u, false, 0u)]
    [InlineData(960u, 544u, 1024u, false, 1u)]
    [InlineData(960u, 544u, 960u, true, 2u)]
    [InlineData(33u, 17u, 40u, false, 3u)]
    [InlineData(33u, 17u, 34u, true, 1u)]
    [InlineData(1u, 1u, 8u, false, 0u)]
    [InlineData(2u, 3u, 2u, true, 3u)]
    public void RowConversionGivesTheSameBytesAsThePerTexelOne(uint width, uint height, uint pitch, bool threePlane, uint swizzle)
    {
        var rng = new Random((int)(width * 31 + height * 7 + pitch + swizzle + (threePlane ? 1000 : 0)));
        // luma plus the larger of the two chroma layouts, with slack after it
        byte[] planes = new byte[pitch * height * 2 + 64];
        rng.NextBytes(planes);
        using (TextureDecoder.UseSource(Base, planes))
        {
            using Texels rows = TextureDecoder.Yuv420(Base, width, height, pitch, threePlane, swizzle);
            using Texels texels = TextureDecoder.Yuv420Texel(Base, width, height, pitch, threePlane, swizzle);
            Assert.Equal(texels.Span.ToArray(), rows.Span.ToArray());
        }
    }

    [Fact]
    public void ExtremeSamplesClampLikeThePerTexelConversion()
    {
        // every combination of 0 and 255 in Y, U and V, in both ranges: the clamping at both ends
        const uint Width = 8, Height = 2, Pitch = 8;
        byte[] planes = new byte[Pitch * Height * 2];
        for (int i = 0; i < planes.Length; i++)
        {
            planes[i] = (byte)(((i * 0x9E37) >> 3 & 1) == 0 ? 0 : 255);
        }

        using (TextureDecoder.UseSource(Base, planes))
        {
            foreach (uint swizzle in new uint[] { 0, 2 })
            {
                using Texels rows = TextureDecoder.Yuv420(Base, Width, Height, Pitch, false, swizzle);
                using Texels texels = TextureDecoder.Yuv420Texel(Base, Width, Height, Pitch, false, swizzle);
                Assert.Equal(texels.Span.ToArray(), rows.Span.ToArray());
            }
        }
    }
}
