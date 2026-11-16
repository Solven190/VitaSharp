using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using VitaSharp.Renderer.Vulkan;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// Render to texture on the software Vulkan device with the workload of <see cref="RendererTextureTests"/>. The Vita's
/// GPU and CPU share one memory, so a texture whose data is a colour surface an earlier scene rendered reads what that
/// scene drew, whether it covers the whole surface or a rectangle inside it (a strided texture at an offset, as shadow
/// maps, reflections and post-processing chains sample their atlases). Each case renders the texture over the
/// GPU-written surface and then over a CPU copy of the same texels: the two presented frames must be the same.
/// </summary>
[Collection("guest")]
public sealed class RendererRenderToTextureTests(GuestMemoryFixture fx)
{
    private const int W = 64, H = 64;
    private const uint DepthAlways = 0x01C00000; // SCE_GXM_DEPTH_FUNC_ALWAYS

    private static uint RenderTarget(HleHarness harness, Gxm gxm, int width, int height)
    {
        uint renderTargetParams = harness.Alloc(0x14), outWord = harness.Word(0);
        GuestMemory.Write(renderTargetParams + 4, (ushort)width);
        GuestMemory.Write(renderTargetParams + 6, (ushort)height);
        GuestMemory.Write(renderTargetParams + 8, (ushort)1);
        Assert.Equal(0, Call(gxm, "sceGxmCreateRenderTarget", renderTargetParams, new Ptr<uint>(outWord)));
        return GuestMemory.R32(outWord);
    }

    /// <summary>A U8U8U8U8 texture over <paramref name="data"/> (linear, or strided by <paramref name="stride"/>
    /// bytes), point sampled.</summary>
    private static uint Texture(HleHarness harness, Gxm gxm, uint data, uint width, uint height, uint stride = 0)
    {
        uint t = harness.Alloc(16);
        int r = stride == 0
            ? Call(gxm, "sceGxmTextureInitLinear", t, data, (uint)TexBase.U8U8U8U8, width, height, 1u)
            : Call(gxm, "sceGxmTextureInitLinearStrided", t, data, (uint)TexBase.U8U8U8U8, width, height, stride);
        Assert.Equal(0, r);
        Call(gxm, "sceGxmTextureSetMinFilter", t, 0u);
        Call(gxm, "sceGxmTextureSetMagFilter", t, 0u);
        return t;
    }

    /// <summary>One scene on <paramref name="renderTarget"/> / <paramref name="color"/> drawing the workload's triangle
    /// over the whole surface with <paramref name="texture"/> on unit 3.</summary>
    private static void Scene(Gxm gxm, RendererTextureTests.Workload workload, uint renderTarget, uint color, uint texture)
    {
        Assert.Equal(0, Call(gxm, "sceGxmBeginScene", workload.Ctx, 0u, renderTarget, 0u, 0u, 0u, color, 0u));
        Invoke(gxm, "sceGxmSetVertexProgram", workload.Ctx, workload.Vp);
        Invoke(gxm, "sceGxmSetFragmentProgram", workload.Ctx, workload.Fp);
        Invoke(gxm, "sceGxmSetFrontDepthFunc", workload.Ctx, DepthAlways);
        Invoke(gxm, "sceGxmSetCullMode", workload.Ctx, 0u);
        Assert.Equal(0, Call(gxm, "sceGxmSetVertexStream", workload.Ctx, 0u, workload.Verts));
        Assert.Equal(0, Call(gxm, "sceGxmSetFragmentTexture", workload.Ctx, 3u, texture));
        Assert.Equal(0, Call(gxm, "sceGxmDraw", workload.Ctx, 0u, 0u, workload.Idx, 3u));
        Assert.Equal(0, Call(gxm, "sceGxmEndScene", workload.Ctx, 0u, 0u));
    }

    /// <summary>
    /// Renders the workload's random RGBA8 texture into a 64x64 surface A (whose memory first holds another pattern,
    /// so a read of stale memory shows), then presents two frames of the workload's surface: one sampling
    /// <paramref name="view"/> over A as the GPU left it, one sampling the same view over a CPU copy of A taken after
    /// sceGxmFinish. Returns both frames (RGB).
    /// </summary>
    private (byte[] OverRendered, byte[] OverCopy) Run(string tag, Func<HleHarness, Gxm, uint, uint> view)
    {
        string frames = Path.Combine(Path.GetTempPath(), "vitasharp-rtt-" + Guid.NewGuid().ToString("N"));
        try
        {
            VulkanRenderer renderer = new(new VulkanRendererOptions
            {
                VSync = false,
                AsyncShaders = false,
                AsyncPipelines = false,
                DumpFramesDir = frames,
                DumpEvery = 1,
            });
            using (HleHarness harness = new(fx, tag))
            {
                try
                {
                    harness.Emu.Gpu.Backend = renderer;
                    var workload = new RendererTextureTests.Workload(harness);
                    try
                    {
                        Gxm gxm = harness.Module<Gxm>();
                        uint rtA = RenderTarget(harness, gxm, W, H), colorA = harness.Alloc(0x40), dataA = harness.Alloc(W * H * 4);
                        for (uint i = 0; i < W * H; i++)
                        {
                            GuestMemory.W32(dataA + i * 4, 0xFF00FF00u ^ (i * 0x00010101u));
                        }

                        Assert.Equal(0, Call(gxm, "sceGxmColorSurfaceInit", colorA, 0u, 0u, 0u, 0u, (uint)W, (uint)H, (uint)W, dataA));
                        uint source = workload.Textures[workload.TextureFormats.IndexOf(TexBase.U8U8U8U8)];

                        // 1. scene A renders the source texture into surface A; at once a scene samples A
                        Scene(gxm, workload, rtA, colorA, source);
                        Scene(gxm, workload, workload.Rt, workload.Color, view(harness, gxm, dataA));
                        harness.Emu.Gpu.Submit(new PresentCommand { Frame = new DisplayFrame(workload.Data, W, W, H, DisplayPixelFormat.A8B8G8R8) });
                        Assert.Equal(0, Call(gxm, "sceGxmFinish", workload.Ctx));
                        harness.Emu.Gpu.WaitIdle();

                        // 2. the same view over a CPU copy of what scene A stored
                        uint copy = harness.Alloc(W * H * 4);
                        GuestMemory.ReadSpan(dataA, W * H * 4).CopyTo(GuestMemory.Span(copy, W * H * 4));
                        Scene(gxm, workload, workload.Rt, workload.Color, view(harness, gxm, copy));
                        harness.Emu.Gpu.Submit(new PresentCommand { Frame = new DisplayFrame(workload.Data, W, W, H, DisplayPixelFormat.A8B8G8R8) });
                        Assert.Equal(0, Call(gxm, "sceGxmFinish", workload.Ctx));
                        harness.Emu.Gpu.WaitIdle();
                    }
                    finally
                    {
                        workload.Terminate();
                    }
                }
                finally
                {
                    harness.Emu.Gpu.WaitIdle();
                    harness.Emu.Gpu.Backend = null;
                    renderer.Dispose();
                }
            }

            string[] shown = Directory.GetFiles(frames, "frame_*.png").Order().ToArray();
            Assert.True(shown.Length >= 2, $"{shown.Length} frames presented");
            return (Rgb(shown[^2]), Rgb(shown[^1]));
        }
        finally
        {
            try
            {
                Directory.Delete(frames, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static byte[] Rgb(string png)
    {
        using var bitmap = SkiaSharp.SKBitmap.Decode(png);
        Assert.Equal((W, H), (bitmap.Width, bitmap.Height));
        return bitmap.Pixels.SelectMany(pixel => new[] { pixel.Red, pixel.Green, pixel.Blue }).ToArray();
    }

    private static void SameImage(byte[] expected, byte[] actual)
    {
        int differ = 0;
        for (int i = 0; i < expected.Length; i += 3)
        {
            if (Math.Abs(expected[i] - actual[i]) > 2 || Math.Abs(expected[i + 1] - actual[i + 1]) > 2 || Math.Abs(expected[i + 2] - actual[i + 2]) > 2)
            {
                differ++;
            }
        }

        Assert.True(differ == 0, $"{differ} of {expected.Length / 3} pixels differ between the texture over the rendered surface and over its copy");
        // the frames show texels, not a cleared or untouched surface
        Assert.True(expected.Distinct().Count() > 16, "the presented frame is flat");
    }

    [SoftwareVulkanFact]
    public void ATextureOverARenderedSurfaceReadsWhatTheSceneDrew()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        (byte[] overRendered, byte[] overCopy) = Run("vkrtt-whole", (harness, gxm, data) => Texture(harness, gxm, data, W, H));
        SameImage(overCopy, overRendered);
    }

    [SoftwareVulkanFact]
    public void AStridedTextureInsideARenderedSurfaceReadsThatRectangle()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        // a 32x32 rectangle at (16, 8) of the 64x64 surface, rows 256 bytes apart
        (byte[] overRendered, byte[] overCopy) = Run("vkrtt-region", (harness, gxm, data) =>
            Texture(harness, gxm, data + (8u * W + 16u) * 4, 32, 32, W * 4));
        SameImage(overCopy, overRendered);
    }
}
