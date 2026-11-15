using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using VitaSharp.Renderer.Vulkan;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// A scene rendering that continues on attachments an earlier rendering wrote waits for those writes
/// (VulkanRenderer.AttachmentSync.cs): Vulkan orders nothing between two render passes or dynamic renderings. A scene
/// broken in the middle (here: a draw samples the surface it renders into, which ends the rendering for the barrier
/// and resumes it after) and the next scene on the same surface load what the earlier rendering stored; a scene on
/// another surface is not held back. Without the dependency the Mali-G57 loaded half-stored colour and depth after
/// Uncharted's mid-scene breaks (one frame in ~15 with the fog brighter or the depth of another moment). Run on the
/// software device with the desktop path (dynamic rendering) and the phone path (gpu profile mali: render passes,
/// deferred tiled renderings).
/// </summary>
[Collection("guest")]
public sealed class RendererAttachmentSyncTests(GuestMemoryFixture fx)
{
    private const uint Size = 64;
    /// <summary>SCE_GXM_TEXTURE_FORMAT_U8U8U8U8_ABGR.</summary>
    private const uint TexAbgr = 0x0C000000;

    private enum Case
    {
        /// <summary>One scene: a draw, a draw sampling the scene's own surface (a mid-scene break), a draw.</summary>
        MidSceneBreak,
        /// <summary>Two scenes on the same surface, one after the other.</summary>
        NextSceneSameSurface,
        /// <summary>A scene on the workload's surface, then one on another surface.</summary>
        NextSceneOtherSurface,
    }

    /// <summary>The renderer's count of renderings that waited for earlier attachment writes, for one case.</summary>
    private long Barriers(string? gpuProfile, Case scenario)
    {
        var renderer = new VulkanRenderer(new VulkanRendererOptions { VSync = false, AsyncShaders = false, AsyncPipelines = false, GpuProfile = gpuProfile });
        using var h = new HleHarness(fx, "vkattach");
        Gxm g = h.Module<Gxm>();
        try
        {
            h.Emu.Gpu.Backend = renderer;
            var w = new RendererTextureTests.Workload(h);
            uint solid = h.Alloc(16), texels = h.Alloc(8 * 8 * 4);
            for (uint i = 0; i < 64; i++)
            {
                GuestMemory.W32(texels + i * 4, 0xFF336699u);
            }

            Assert.Equal(0, Call(g, "sceGxmTextureInitLinear", solid, texels, TexAbgr, 8u, 8u, 1u));
            // a texture over the surface the scene renders into
            uint own = h.Alloc(16);
            Assert.Equal(0, Call(g, "sceGxmTextureInitLinear", own, w.Data, TexAbgr, Size, Size, 1u));
            // the warm-up scene: pipelines built, the surface rendered once (its first scene loads guest memory)
            Scene(g, w, w.Rt, w.Color, [solid]);
            Assert.Equal(0, Call(g, "sceGxmFinish", w.Ctx));
            h.Emu.Gpu.WaitIdle();
            long before = renderer.AttachmentBarriers;
            switch (scenario)
            {
                case Case.MidSceneBreak:
                    Scene(g, w, w.Rt, w.Color, [solid, own, solid]);
                    break;
                case Case.NextSceneSameSurface:
                    Scene(g, w, w.Rt, w.Color, [solid]);
                    break;
                default:
                    uint o = h.Word(0), rp = h.Alloc(0x14);
                    GuestMemory.Write(rp + 4, (ushort)Size);
                    GuestMemory.Write(rp + 6, (ushort)Size);
                    GuestMemory.Write(rp + 8, (ushort)1);
                    Assert.Equal(0, Call(g, "sceGxmCreateRenderTarget", rp, new Ptr<uint>(o)));
                    uint otherRt = GuestMemory.R32(o), otherColor = h.Alloc(0x40);
                    Assert.Equal(0, Call(g, "sceGxmColorSurfaceInit", otherColor, 0u, 0u, 0u, 0u, Size, Size, Size, h.Alloc(Size * Size * 4)));
                    // a depth/stencil surface of its own: the default one would be the same image as the first scene's
                    uint depth = h.Alloc(0x20);
                    Assert.Equal(0, Call(g, "sceGxmDepthStencilSurfaceInit", depth, 0x00044000u, 0u, Size, h.Alloc(Size * Size * 4), 0u));
                    Scene(g, w, otherRt, otherColor, [solid], depth);
                    break;
            }

            Assert.Equal(0, Call(g, "sceGxmFinish", w.Ctx));
            h.Emu.Gpu.WaitIdle();
            long barriers = renderer.AttachmentBarriers - before;
            w.Terminate();
            return barriers;
        }
        finally
        {
            h.Emu.Gpu.WaitIdle();
            h.Emu.Gpu.Backend = null;
            renderer.Dispose();
        }
    }

    /// <summary>One scene on <paramref name="rt"/>: the full-surface triangle once per texture, depth test ALWAYS
    /// with depth writes on (the depth/stencil is written as well as the colour).</summary>
    private static void Scene(Gxm g, RendererTextureTests.Workload w, uint rt, uint color, uint[] textures, uint depth = 0)
    {
        Assert.Equal(0, Call(g, "sceGxmBeginScene", w.Ctx, 0u, rt, 0u, 0u, 0u, color, depth));
        Invoke(g, "sceGxmSetVertexProgram", w.Ctx, w.Vp);
        Invoke(g, "sceGxmSetFragmentProgram", w.Ctx, w.Fp);
        Invoke(g, "sceGxmSetFrontDepthFunc", w.Ctx, 0x00E00000u); // ALWAYS
        Invoke(g, "sceGxmSetCullMode", w.Ctx, 0u);
        Assert.Equal(0, Call(g, "sceGxmSetVertexStream", w.Ctx, 0u, w.Verts));
        foreach (uint texture in textures)
        {
            Assert.Equal(0, Call(g, "sceGxmSetFragmentTexture", w.Ctx, 3u, texture));
            Assert.Equal(0, Call(g, "sceGxmDraw", w.Ctx, 0u, 0u, w.Idx, 3u));
        }

        Assert.Equal(0, Call(g, "sceGxmEndScene", w.Ctx, 0u, 0u));
    }

    [SoftwareVulkanTheory]
    [InlineData(null)]
    [InlineData("mali")]
    public void AMidSceneBreakResumesAfterTheEarlierRenderingsWrites(string? gpuProfile)
    {
        using IDisposable driver = SoftwareVulkan.Use();
        Assert.True(Barriers(gpuProfile, Case.MidSceneBreak) >= 1, "the rendering resumed after the break did not wait for the attachment writes before it");
    }

    [SoftwareVulkanTheory]
    [InlineData(null)]
    [InlineData("mali")]
    public void TheNextSceneOnTheSameSurfaceWaitsForTheLastOnesWrites(string? gpuProfile)
    {
        using IDisposable driver = SoftwareVulkan.Use();
        Assert.True(Barriers(gpuProfile, Case.NextSceneSameSurface) >= 1, "the next scene on the surface did not wait for the previous scene's attachment writes");
    }

    [SoftwareVulkanTheory]
    [InlineData(null)]
    [InlineData("mali")]
    public void ASceneOnOtherAttachmentsIsNotHeldBack(string? gpuProfile)
    {
        using IDisposable driver = SoftwareVulkan.Use();
        Assert.Equal(0, Barriers(gpuProfile, Case.NextSceneOtherSurface));
    }
}
