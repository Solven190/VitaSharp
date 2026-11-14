using VitaSharp.Core.Gpu;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using VitaSharp.Renderer.Vulkan;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// GXM transfers run on the Vita's GPU in order with the scenes: a draw issued after a transfer reads its result. A
/// surface rendered in a frame, turned into a smaller texture by sceGxmTransferDownscale/Copy (a chain of them, like
/// Killzone's light-shaft mips) and sampled by the next scene of the same frame must show that frame's pixels, with the
/// transfers deferred to the frame's retirement (VITASHARP_TRANSFER_DEFER, the Android default) exactly as without.
/// Run on the software Vulkan device, every frame with a new colour, so a result one frame late shows up.
/// </summary>
[Collection("guest")]
public sealed class RendererTransferTests(GuestMemoryFixture fx)
{
    private const uint SrcSize = 16;
    /// <summary>SCE_GXM_TEXTURE_FORMAT_U8U8U8U8_ABGR, and the transfer format of 32-bit pixels.</summary>
    private const uint TexAbgr = 0x0C000000, Xfer32 = 0x000C0000;

    private enum Chain
    {
        /// <summary>The surface downscaled once, the result sampled.</summary>
        Downscale,
        /// <summary>The surface downscaled, and that result downscaled again (a mip chain built by transfers).</summary>
        TwoDownscales,
        /// <summary>The surface copied whole, the copy sampled.</summary>
        Copy,
    }

    [SoftwareVulkanFact]
    public void ASceneReadsTheTransferResultOfItsOwnFrame_DeferredOrNot()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        using var h = new HleHarness(fx, "vkxfer");
        var w = new RendererTextureTests.Workload(h);
        Gxm g = h.Module<Gxm>();
        try
        {
            var failures = new List<string>();
            foreach (bool defer in new[] { false, true })
            {
                foreach (Chain chain in Enum.GetValues<Chain>())
                {
                    failures.AddRange(RunFrames(h, g, w, defer, chain));
                }
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }
        finally
        {
            w.Terminate();
        }
    }

    /// <summary>Four frames: a 16x16 surface rendered in a solid colour new each frame, the transfer(s), then the main
    /// surface drawn from the last destination; after each frame the main surface must hold that frame's colour.</summary>
    private static List<string> RunFrames(HleHarness h, Gxm g, RendererTextureTests.Workload w, bool defer, Chain chain)
    {
        var failures = new List<string>();
        var renderer = new VulkanRenderer(new VulkanRendererOptions { VSync = false, AsyncShaders = false, AsyncPipelines = false, TransferDefer = defer });
        try
        {
            h.Emu.Gpu.Backend = renderer;
            uint o = h.Word(0), rp = h.Alloc(0x14);
            GuestMemory.Write(rp + 4, (ushort)SrcSize);
            GuestMemory.Write(rp + 6, (ushort)SrcSize);
            GuestMemory.Write(rp + 8, (ushort)1);
            Assert.Equal(0, Call(g, "sceGxmCreateRenderTarget", rp, new Ptr<uint>(o)));
            uint srcRt = GuestMemory.R32(o);
            uint srcData = h.Alloc(SrcSize * SrcSize * 4), srcColor = h.Alloc(0x40);
            Assert.Equal(0, Call(g, "sceGxmColorSurfaceInit", srcColor, 0u, 0u, 0u, 0u, SrcSize, SrcSize, SrcSize, srcData));
            uint half = SrcSize / 2, quarter = SrcSize / 4;
            uint dst1 = h.Alloc(half * half * 4), dst2 = h.Alloc(Pitch(quarter) * quarter), copy = h.Alloc(SrcSize * SrcSize * 4);
            (uint sampled, uint size) = chain switch
            {
                Chain.Downscale => (dst1, half),
                Chain.TwoDownscales => (dst2, quarter),
                _ => (copy, SrcSize),
            };
            uint result = h.Alloc(16);
            Assert.Equal(0, Call(g, "sceGxmTextureInitLinear", result, sampled, TexAbgr, size, size, 1u));
            for (int frame = 0; frame < 4; frame++)
            {
                uint colour = 0xFF000000u | (uint)(0x30 + frame * 0x30) | (uint)(0xC0 - frame * 0x20) << 8 | (uint)(0x10 + frame * 0x40) << 16;
                // a solid texture of this frame's colour (its own memory: no cache invalidation involved)
                uint texels = h.Alloc(8 * 8 * 4), solid = h.Alloc(16);
                for (uint i = 0; i < 64; i++)
                {
                    GuestMemory.W32(texels + i * 4, colour);
                }

                Assert.Equal(0, Call(g, "sceGxmTextureInitLinear", solid, texels, TexAbgr, 8u, 8u, 1u));
                Draw(g, w, srcRt, srcColor, solid);
                if (chain == Chain.Copy)
                {
                    Assert.Equal(0, Call(g, "sceGxmTransferCopy", SrcSize, SrcSize, 0u, 0u, 0u, Xfer32, 0u, srcData, 0u, 0u, (int)(SrcSize * 4),
                        Xfer32, 0u, copy, 0u, 0u, (int)(SrcSize * 4), 0u, 0u, 0u));
                }
                else
                {
                    Assert.Equal(0, Call(g, "sceGxmTransferDownscale", Xfer32, srcData, 0u, 0u, SrcSize, SrcSize, (int)(SrcSize * 4),
                        Xfer32, dst1, 0u, 0u, (int)(half * 4), 0u, 0u, 0u));
                    if (chain == Chain.TwoDownscales)
                    {
                        Assert.Equal(0, Call(g, "sceGxmTransferDownscale", Xfer32, dst1, 0u, 0u, half, half, (int)(half * 4),
                            Xfer32, dst2, 0u, 0u, (int)Pitch(quarter), 0u, 0u, 0u));
                    }
                }
                Draw(g, w, w.Rt, w.Color, result);
                Assert.Equal(0, Call(g, "sceGxmFinish", w.Ctx));
                uint got = GuestMemory.R32(w.Data + (32 * 64 + 32) * 4);
                if (!Close(got, colour))
                {
                    failures.Add($"defer {defer}, {chain}, frame {frame}: the scene after the transfer drew 0x{got:X8}, the frame's colour is 0x{colour:X8}");
                }
            }
            // a frame without a transfer: the destination's memory now holds the last result, read from there
            Draw(g, w, w.Rt, w.Color, result);
            Assert.Equal(0, Call(g, "sceGxmFinish", w.Ctx));
            Assert.Equal(GuestMemory.R32(sampled), GuestMemory.R32(w.Data + (32 * 64 + 32) * 4));
        }
        finally
        {
            h.Emu.Gpu.WaitIdle();
            h.Emu.Gpu.Backend = null;
            renderer.Dispose();
        }
        return failures;
    }

    /// <summary>One scene on <paramref name="rt"/>: the full-surface triangle sampling <paramref name="texture"/>.</summary>
    private static void Draw(Gxm g, RendererTextureTests.Workload w, uint rt, uint color, uint texture)
    {
        Assert.Equal(0, Call(g, "sceGxmBeginScene", w.Ctx, 0u, rt, 0u, 0u, 0u, color, 0u));
        Invoke(g, "sceGxmSetVertexProgram", w.Ctx, w.Vp);
        Invoke(g, "sceGxmSetFragmentProgram", w.Ctx, w.Fp);
        Invoke(g, "sceGxmSetFrontDepthFunc", w.Ctx, 0x00E00000u); // ALWAYS
        Invoke(g, "sceGxmSetCullMode", w.Ctx, 0u);
        Assert.Equal(0, Call(g, "sceGxmSetVertexStream", w.Ctx, 0u, w.Verts));
        Assert.Equal(0, Call(g, "sceGxmSetFragmentTexture", w.Ctx, 3u, texture));
        Assert.Equal(0, Call(g, "sceGxmDraw", w.Ctx, 0u, 0u, w.Idx, 3u));
        Assert.Equal(0, Call(g, "sceGxmEndScene", w.Ctx, 0u, 0u));
    }

    /// <summary>The row pitch of a linear texture <paramref name="width"/> pixels wide (rows of 8-pixel multiples).</summary>
    private static uint Pitch(uint width) => (width + 7) / 8 * 8 * 4;

    /// <summary>Every channel within 2 (the downscale's rounding).</summary>
    private static bool Close(uint a, uint b)
    {
        for (int shift = 0; shift < 32; shift += 8)
        {
            if (Math.Abs((int)(a >> shift & 0xFF) - (int)(b >> shift & 0xFF)) > 2)
            {
                return false;
            }
        }
        return true;
    }
}
