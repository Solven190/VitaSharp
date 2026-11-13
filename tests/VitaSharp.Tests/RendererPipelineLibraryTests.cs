using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using VitaSharp.Renderer.Vulkan;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// First-use pipelines with VK_EXT_graphics_pipeline_library (VulkanRenderer.PipelineLibrary.cs) on the software
/// device: one vertex and one fragment program drawn with several colour write masks (one pipeline each, the same two
/// shader modules). The measure is the renderer's pipeline counters, not the host clock: with the libraries the GPU
/// thread compiles each shader once and links every pipeline; without them (option off, or a driver without fast
/// linking: MoltenVK, Dozen, phone drivers) it compiles every pipeline whole. The written-back colour surface must be
/// the same either way, and also after the optimised pipelines replaced the linked ones.
/// </summary>
[Collection("guest")]
public sealed class RendererPipelineLibraryTests(GuestMemoryFixture fx)
{
    private const int W = 32, H = 32;
    private static readonly byte[] s_masks = [0xF, 0x7, 0x3, 0x9];

    private static uint Program(HleHarness h, bool fragment)
    {
        ulong[] code = fragment ? [] : [0x6081000460600003, 0x50C9000A20200180];
        uint size = 0x100 + (uint)code.Length * 8;
        uint p = h.Alloc(size);
        GuestMemory.W32(p, 0x00505847);
        GuestMemory.W32(p + 8, size);
        if (fragment)
        {
            GuestMemory.W32(p + 0x14, GxpProgram.FlagFragment);
        }

        GuestMemory.W32(p + 0x3C, (uint)code.Length);
        GuestMemory.W32(p + 0x40, 0x100 - 0x40);
        for (int i = 0; i < code.Length; i++)
        {
            GuestMemory.W32(p + 0x100 + (uint)i * 8, (uint)code[i]);
            GuestMemory.W32(p + 0x104 + (uint)i * 8, (uint)(code[i] >> 32));
        }

        return p;
    }

    /// <summary>Draws one full-screen triangle per write mask, in <paramref name="frames"/> frames; returns the
    /// renderer's pipeline counters and the colour surface after each frame.</summary>
    private (VulkanRenderer.PipelineStatistics Stats, List<byte[]> Surfaces) Run(bool pipelineLibrary, int frames)
    {
        using var h = new HleHarness(fx, pipelineLibrary ? "gplon" : "gploff");
        var renderer = new VulkanRenderer(new VulkanRendererOptions
        {
            VSync = false,
            AsyncShaders = false,
            AsyncPipelines = false,
            PipelineLibrary = pipelineLibrary,
        });
        var surfaces = new List<byte[]>();
        try
        {
            renderer.BeginTitle("PCSA00000");
            h.Emu.Gpu.Backend = renderer;
            Gxm g = h.Module<Gxm>();
            uint initParams = h.Alloc(0x14);
            GuestMemory.W32(initParams + 4, 2);
            Assert.Equal(0, Call(g, "sceGxmInitialize", initParams));
            try
            {
                uint contextParams = h.Alloc(0x24);
                uint result = h.Word(0);
                GuestMemory.W32(contextParams + 8, h.Alloc(0x1000));
                GuestMemory.W32(contextParams + 12, 0x1000);
                GuestMemory.W32(contextParams + 16, h.Alloc(0x4000));
                GuestMemory.W32(contextParams + 20, 0x4000);
                GuestMemory.W32(contextParams + 24, h.Alloc(0x4000));
                GuestMemory.W32(contextParams + 28, 0x4000);
                Assert.Equal(0, Call(g, "sceGxmCreateContext", contextParams, new Ptr<uint>(result)));
                uint context = GuestMemory.R32(result);
                uint targetParams = h.Alloc(0x14);
                GuestMemory.Write(targetParams + 4, (ushort)W);
                GuestMemory.Write(targetParams + 6, (ushort)H);
                GuestMemory.Write(targetParams + 8, (ushort)1);
                Assert.Equal(0, Call(g, "sceGxmCreateRenderTarget", targetParams, new Ptr<uint>(result)));
                uint target = GuestMemory.R32(result);
                uint data = h.Alloc(W * H * 4);
                uint color = h.Alloc(0x40);
                for (uint i = 0; i < W * H; i++)
                {
                    GuestMemory.W32(data + i * 4, 0x80402010u | (i * 2654435761u >> 24));
                }

                Assert.Equal(0, Call(g, "sceGxmColorSurfaceInit", color, 0u, 0u, 0u, 0u, (uint)W, (uint)H, (uint)W, data));
                uint patcherParams = h.Alloc(0x40);
                Assert.Equal(0, Call(g, "sceGxmShaderPatcherCreate", patcherParams, new Ptr<uint>(result)));
                uint patcher = GuestMemory.R32(result);
                uint vertexId = h.Word(0);
                uint fragmentId = h.Word(0);
                Call(g, "sceGxmShaderPatcherRegisterProgram", patcher, Program(h, false), new Ptr<uint>(vertexId));
                Call(g, "sceGxmShaderPatcherRegisterProgram", patcher, Program(h, true), new Ptr<uint>(fragmentId));
                uint attribute = h.Alloc(8);
                uint stream = h.Alloc(4);
                uint vertexProgram = h.Word(0);
                GuestMemory.Write(attribute + 5, (byte)3);
                GuestMemory.Write(stream, (ushort)12);
                Assert.Equal(0, Call(g, "sceGxmShaderPatcherCreateVertexProgram", patcher, GuestMemory.R32(vertexId), attribute, 1u, stream, 1u, new Ptr<uint>(vertexProgram)));
                // one fragment program per write mask: same GXP (same shader module), one pipeline each
                var fragmentPrograms = new List<uint>();
                foreach (byte mask in s_masks)
                {
                    uint blend = h.Alloc(4);
                    GuestMemory.W32(blend, mask | 1u << 16 | 0u << 20 | 1u << 24 | 0u << 28); // ADD ONE ZERO: no blending
                    uint fragmentProgram = h.Word(0);
                    Assert.Equal(0, Call(g, "sceGxmShaderPatcherCreateFragmentProgram", patcher, GuestMemory.R32(fragmentId), 0u, 0u, blend,
                        GuestMemory.R32(vertexProgram), new Ptr<uint>(fragmentProgram)));
                    fragmentPrograms.Add(GuestMemory.R32(fragmentProgram));
                }

                uint vertices = h.Alloc(36);
                uint indices = h.Alloc(8);
                float[] positions = [-1, -1, 0.5f, 3, -1, 0.5f, -1, 3, 0.5f];
                for (int i = 0; i < positions.Length; i++)
                {
                    GuestMemory.Write(vertices + (uint)i * 4, positions[i]);
                }

                GuestMemory.Write(indices, (ushort)0);
                GuestMemory.Write(indices + 2, (ushort)1);
                GuestMemory.Write(indices + 4, (ushort)2);
                for (int frame = 0; frame < frames; frame++)
                {
                    Assert.Equal(0, Call(g, "sceGxmBeginScene", context, 0u, target, 0u, 0u, 0u, color, 0u));
                    Invoke(g, "sceGxmSetVertexProgram", context, GuestMemory.R32(vertexProgram));
                    Invoke(g, "sceGxmSetCullMode", context, 0u);
                    Assert.Equal(0, Call(g, "sceGxmSetVertexStream", context, 0u, vertices));
                    foreach (uint fragmentProgram in fragmentPrograms)
                    {
                        Invoke(g, "sceGxmSetFragmentProgram", context, fragmentProgram);
                        Assert.Equal(0, Call(g, "sceGxmDraw", context, 0u, 0u, indices, 3u));
                    }

                    Assert.Equal(0, Call(g, "sceGxmEndScene", context, 0u, 0u));
                    Assert.Equal(0, Call(g, "sceGxmFinish", context));
                    h.Emu.Gpu.WaitIdle();
                    surfaces.Add(GuestMemory.ReadSpan(data, W * H * 4).ToArray());
                    if (frame == 0 && pipelineLibrary)
                    {
                        // the optimised pipelines are built on a shader worker: the next frames use them
                        renderer.WaitForOptimizedPipelines();
                    }
                }
            }
            finally
            {
                Call(g, "sceGxmTerminate");
            }
        }
        finally
        {
            h.Emu.Gpu.WaitIdle();
            h.Emu.Gpu.Backend = null;
        }

        VulkanRenderer.PipelineStatistics stats = renderer.PipelineStats;
        renderer.Dispose();
        return (stats, surfaces);
    }

    [SoftwareVulkanFact]
    public void FirstUsePipelinesAreLinkedFromSharedShaderLibraries()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        (VulkanRenderer.PipelineStatistics whole, List<byte[]> wholeSurfaces) = Run(pipelineLibrary: false, frames: 2);
        (VulkanRenderer.PipelineStatistics linked, List<byte[]> linkedSurfaces) = Run(pipelineLibrary: true, frames: 2);

        // without libraries: every pipeline is compiled whole on the GPU thread
        Assert.False(whole.PipelineLibrary);
        Assert.Equal(s_masks.Length, whole.CompiledOnGpuThread);
        Assert.Equal(0, whole.LinkedOnGpuThread);
        // turned on, the renderer links wherever the device can (SwiftShader and lavapipe report fast linking)
        Assert.Equal(linked.Supported, linked.PipelineLibrary);
        if (linked.PipelineLibrary)
        {
            // with them: two shader libraries (one per module) and a link per pipeline; nothing compiled whole on the
            // GPU thread, the optimised pipelines built in the background and swapped in for the second frame
            Assert.Equal(0, linked.CompiledOnGpuThread);
            Assert.Equal(s_masks.Length, linked.LinkedOnGpuThread);
            Assert.Equal(2, linked.LibrariesOnGpuThread);
            Assert.Equal(s_masks.Length, linked.Optimized);
            Assert.Equal(s_masks.Length, linked.Swapped);
            Assert.True(linked.CriticalPath < whole.CriticalPath);
        }
        else
        {
            // a driver without fast linking (the fallback): the same whole pipelines as with the option off
            Assert.Equal(whole.CompiledOnGpuThread, linked.CompiledOnGpuThread);
            Assert.Equal(0, linked.LinkedOnGpuThread);
        }

        // the image is the same with linked, optimised and whole pipelines
        for (int frame = 0; frame < wholeSurfaces.Count; frame++)
        {
            Assert.True(wholeSurfaces[frame].AsSpan().SequenceEqual(linkedSurfaces[frame]), $"frame {frame} differs");
        }
    }
}
