using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// The renderer's own writebacks are not taken for CPU writes when it decides whether a surface's memory was
/// overwritten since the surface was rendered (<see cref="VulkanRenderer.WritebackLandedSince"/>). A writeback marks
/// the pages it writes like a CPU store and lands when its frame retires, at a time set by the GPU: Metal Gear Solid
/// HD's 360x224 scene and 1024x512 bloom atlas share 0x60E0CA00, and the scene's late writeback made the next surface
/// start from memory in some replays and from the atlas in others (frame 2749 of a capture differed between runs).
/// </summary>
public sealed class WritebackOverlapTests
{
    private static readonly (uint Start, uint End, uint Epoch)[] s_landed =
    [
        (0x60E0CA00, 0x60E0CA00 + 360 * 4 * 224, 40), // the 360x224 scene's writeback, landed in epoch 40
        (0x61000000, 0x61010000, 90),                 // another surface's
    ];

    [Theory]
    // the atlas was rendered at epoch 38 (MemStamp 40): the scene's writeback landed after it -> not the CPU
    [InlineData(0x60E0CA00u, 0x60E40000u, 40u, true)]
    // rendered after the writeback landed (MemStamp 50): pages written since are someone else's
    [InlineData(0x60E0CA00u, 0x60E40000u, 50u, false)]
    // pages the writebacks never touched
    [InlineData(0x62000000u, 0x62010000u, 10u, false)]
    // a range that only touches the end of the other surface's writeback, landed late
    [InlineData(0x6100F000u, 0x61020000u, 80u, true)]
    // the ranges meet without overlapping (end is exclusive)
    [InlineData(0x61010000u, 0x61020000u, 10u, false)]
    public void AWritebackThatLandedAfterTheRenderingIsNotACpuWrite(uint start, uint end, uint memStamp, bool expected)
    {
        Assert.Equal(expected, VulkanRenderer.WritebackLandedSince(s_landed, start, end, memStamp));
    }

    [Fact]
    public void NoWritebacksNothingToExcuse()
    {
        Assert.False(VulkanRenderer.WritebackLandedSince([], 0x60E0CA00, 0x60E40000, 1));
    }
}
