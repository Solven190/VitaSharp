using VitaSharp.Core.Gpu;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// A process that ends takes its GPU surfaces with it, as on the Vita, where its memory goes. The renderer kept the
/// render targets and depth targets of an ended process (the capture of Metal Gear Solid resumed after God of War was
/// closed still listed God of War's surfaces): device memory held for nothing, and stale surfaces at addresses another
/// process's next allocation reuses. On the software Vulkan device: the targets of the ended process go, those of the
/// process still running stay, and a surface another process rendered into after the ended one stays too.
/// </summary>
[Collection("guest")]
public sealed class RendererProcessEndTests(GuestMemoryFixture fx)
{
    private const uint W = 64, H = 64;

    private static ColorSurfaceInfo Color(uint data) => new(data, W, H, W, 0, 0, false, 0);

    private static DepthSurfaceInfo Depth(uint data) => new(data, 0, W, 0x00044000, 0, 1f, 0, false, false);

    private static void Scene(VulkanRenderer renderer, int pid, uint color, uint depth)
    {
        renderer.Execute(new BeginSceneCommand { Width = W, Height = H, Color = Color(color), Depth = depth != 0 ? Depth(depth) : null, Pid = pid });
        renderer.Execute(new EndSceneCommand { Pid = pid });
    }

    [SoftwareVulkanFact]
    public void TheSurfacesOfAnEndedProcessAreReleased()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        using var h = new HleHarness(fx, "vkprocend");
        uint a = h.Alloc(W * H * 4), b = h.Alloc(W * H * 4), shared = h.Alloc(W * H * 4);
        uint depthA = h.Alloc(W * H * 4), depthB = h.Alloc(W * H * 4);
        using var renderer = new VulkanRenderer(new VulkanRendererOptions { VSync = false, AsyncShaders = false, AsyncPipelines = false });

        // process 5 renders into A (with a depth surface) and into a surface process 6 renders into after it
        Scene(renderer, 5, a, depthA);
        Scene(renderer, 5, shared, 0);
        // process 6 renders into B (with its own depth surface) and then into the shared surface
        Scene(renderer, 6, b, depthB);
        Scene(renderer, 6, shared, 0);
        renderer.Execute(new FinishCommand());
        // (a scene without a depth surface has a depth image of its own too)
        (int targets, int depths, _) = renderer.HeldSurfaces;
        Assert.Equal(3, targets);
        Assert.True(depths >= 2, $"{depths} depth targets");

        // process 5 ends: A and its depth surface go; B, B's depth and the shared surface (process 6's since) stay
        renderer.Execute(new ProcessEndedCommand { Pid = 5 });
        renderer.Execute(new FinishCommand());
        (targets, int depthsAfter, _) = renderer.HeldSurfaces;
        Assert.Equal(2, targets);
        Assert.True(depthsAfter < depths && depthsAfter >= 1, $"depth targets {depths} -> {depthsAfter}: process 5's go, process 6's (B's at least) stay");

        // process 6 ends: nothing of it is left
        renderer.Execute(new ProcessEndedCommand { Pid = 6 });
        renderer.Execute(new FinishCommand());
        (targets, depths, _) = renderer.HeldSurfaces;
        Assert.Equal(0, targets);
        Assert.Equal(0, depths);

        // host work (pid 0) is never a process's: an end of pid 0 releases nothing
        Scene(renderer, 0, a, 0);
        renderer.Execute(new ProcessEndedCommand { Pid = 0 });
        Assert.Equal(1, renderer.HeldSurfaces.RenderTargets);
    }

    [SoftwareVulkanFact]
    public void AProcessEndingWithItsSceneOpenEndsItAndReleasesItsSurfaces()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        using var h = new HleHarness(fx, "vkprocopen");
        // without interleaved scenes (VITASHARP_GPU_SCENE_INTERLEAVE=0) a process never leaves a scene open to another
        if (!GpuQueue.InterleaveScenes)
        {
            return;
        }

        uint a = h.Alloc(W * H * 4), b = h.Alloc(W * H * 4);
        using var renderer = new VulkanRenderer(new VulkanRendererOptions { VSync = false, AsyncShaders = false, AsyncPipelines = false });
        Scene(renderer, 6, b, 0);
        // process 5 begins a scene and ends without closing it
        renderer.Execute(new BeginSceneCommand { Width = W, Height = H, Color = Color(a), Pid = 5 });
        renderer.Execute(new ProcessEndedCommand { Pid = 5, HadOpenScene = GpuQueue.InterleaveScenes });
        renderer.Execute(new FinishCommand());
        Assert.Equal(1, renderer.HeldSurfaces.RenderTargets);
        // the other process renders on
        Scene(renderer, 6, b, 0);
        renderer.Execute(new FinishCommand());
        Assert.Equal(1, renderer.HeldSurfaces.RenderTargets);
    }
}
