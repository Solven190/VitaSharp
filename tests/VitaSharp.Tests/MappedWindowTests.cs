using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// The CDRAM mirror of the renderer (VulkanRenderer.Mapped.cs) is 288 MB and was always bound whole as a storage
/// buffer: on drivers whose maxStorageBufferRange is the Vulkan minimum of 128 MB (lavapipe, SwiftShader, some mobile
/// drivers) the descriptor broke VUID-VkWriteDescriptorSet-descriptorType-00333. <see cref="MappedWindow"/> picks a
/// window no larger than the limit that holds every range a stage reads.
/// </summary>
public sealed class MappedWindowTests
{
    private const ulong Mb = 1 << 20;
    private const ulong Mirror = 288 * Mb;
    private const ulong Limit = 128 * Mb;

    [Fact]
    public void TheWholeMirrorIsBoundWhereTheDeviceAllowsIt()
    {
        MappedWindow.Span[] spans = [new(0, 4096), new(280 * Mb, 280 * Mb + 4096)];

        Assert.True(MappedWindow.TryPlace(spans, Mirror, 2048 * Mb, 64, out ulong windowBase, out ulong windowRange));

        Assert.Equal(0ul, windowBase);
        Assert.Equal(Mirror, windowRange);
    }

    [Theory]
    [InlineData(0ul, 64)]
    [InlineData(100 * Mb + 12345, 64)]
    [InlineData(200 * Mb + 4, 256)]
    [InlineData(Mirror - 4096, 16)]
    public void TheWindowNeverExceedsTheLimitAndHoldsEveryRange(ulong lo, ulong alignment)
    {
        MappedWindow.Span[] spans = [new(lo, lo + 4096)];

        Assert.True(MappedWindow.TryPlace(spans, Mirror, Limit, alignment, out ulong windowBase, out ulong windowRange));

        Assert.True(windowRange <= Limit, $"range {windowRange} above the limit");
        Assert.Equal(0ul, windowBase % alignment);
        Assert.True(windowBase <= lo && lo + 4096 <= windowBase + windowRange);
        Assert.True(windowBase + windowRange <= Mirror);
    }

    [Fact]
    public void RangesTheWholeWidthOfTheLimitApartStillShareAnAlignedWindow()
    {
        // the coarse start (half-window steps) cannot hold both; the tight aligned start can
        MappedWindow.Span[] spans = [new(70 * Mb, 70 * Mb + 256), new(197 * Mb, 197 * Mb + 256)];

        Assert.True(MappedWindow.TryPlace(spans, Mirror, Limit, 64, out ulong windowBase, out ulong windowRange));

        Assert.Equal(70 * Mb, windowBase);
        Assert.True(197 * Mb + 256 <= windowBase + windowRange);
    }

    [Fact]
    public void NearbyRangesUseTheSameCoarseWindow()
    {
        MappedWindow.TryPlace([new MappedWindow.Span(10 * Mb, 11 * Mb)], Mirror, Limit, 64, out ulong first, out _);
        MappedWindow.TryPlace([new MappedWindow.Span(50 * Mb, 51 * Mb)], Mirror, Limit, 64, out ulong second, out _);

        Assert.Equal(first, second);
    }

    [Fact]
    public void RangesFartherApartThanTheLimitDoNotFit()
    {
        // a CDRAM surface at the start and the scratch area at the end of the mirror
        MappedWindow.Span[] spans = [new(0, 4096), new(260 * Mb, 260 * Mb + 4096)];

        Assert.False(MappedWindow.TryPlace(spans, Mirror, Limit, 64, out _, out _));
    }

    [Fact]
    public void AStageWithOnlyNullSlotsGetsAValidWindow()
    {
        Assert.True(MappedWindow.TryPlace([], Mirror, Limit, 64, out ulong windowBase, out ulong windowRange));

        Assert.Equal(0ul, windowBase);
        Assert.Equal(Limit, windowRange);
    }
}
