using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;
using VitaSharp.Renderer.Vulkan;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// VITASHARP_RETIRE_WAKE (VulkanRenderer.RetireWatch.cs) publishes a frame the GPU finished at the worker's next draw
/// instead of its next scene. The console's contract must hold in every mode: when a scene's notification shows its
/// value, every result of the GPU work before it is already in guest memory (here the scene's visibility test result,
/// which the GPU writes into the visibility buffer), so the guest never reads a stale result after the notification
/// (games read occlusion results once the frame's notification arrived). Each round puts a marker in the result word,
/// draws with the visibility test on and polls the notification from this thread (as a game's sceGxmNotificationWait
/// does) while the next scene keeps the worker recording.
/// </summary>
[Collection("guest")]
public sealed class RetireWakeTests(GuestMemoryFixture fx)
{
    private const int W = 64, H = 64;

    private static uint Program(HleHarness h, bool fragment)
    {
        ulong[] code = fragment ? [] : [0x6081000460600003, 0x50C9000A20200180];
        uint size = 0x100 + (uint)code.Length * 8, p = h.Alloc(size);
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

    [SoftwareVulkanTheory]
    [InlineData("")]
    [InlineData("measure")]
    [InlineData("1")]
    public void ANotificationNeverShowsBeforeTheScenesWriteback(string mode)
    {
        using IDisposable driver = SoftwareVulkan.Use();
        using var h = new HleHarness(fx, "retirewake");
        string? saved = Environment.GetEnvironmentVariable(EnvVars.RetireWake);
        Environment.SetEnvironmentVariable(EnvVars.RetireWake, mode.Length == 0 ? null : mode);
        VulkanRenderer renderer;
        try
        {
            renderer = new VulkanRenderer(new VulkanRendererOptions { VSync = false, AsyncShaders = false, AsyncPipelines = false });
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVars.RetireWake, saved);
        }

        try
        {
            renderer.BeginTitle("PCSA00000");
            h.Emu.Gpu.Backend = renderer;
            Gxm g = h.Module<Gxm>();
            uint ip = h.Alloc(0x14);
            GuestMemory.W32(ip + 4, 2);
            Assert.Equal(0, Call(g, "sceGxmInitialize", ip));
            try
            {
                uint cp = h.Alloc(0x24), o = h.Word(0);
                GuestMemory.W32(cp + 8, h.Alloc(0x1000));
                GuestMemory.W32(cp + 12, 0x1000);
                GuestMemory.W32(cp + 16, h.Alloc(0x4000));
                GuestMemory.W32(cp + 20, 0x4000);
                GuestMemory.W32(cp + 24, h.Alloc(0x4000));
                GuestMemory.W32(cp + 28, 0x4000);
                Assert.Equal(0, Call(g, "sceGxmCreateContext", cp, new Ptr<uint>(o)));
                uint ctx = GuestMemory.R32(o);
                uint rp = h.Alloc(0x14);
                GuestMemory.Write(rp + 4, (ushort)W);
                GuestMemory.Write(rp + 6, (ushort)H);
                GuestMemory.Write(rp + 8, (ushort)1);
                Assert.Equal(0, Call(g, "sceGxmCreateRenderTarget", rp, new Ptr<uint>(o)));
                uint rt = GuestMemory.R32(o);
                uint data = h.Alloc(W * H * 4), color = h.Alloc(0x40);
                Assert.Equal(0, Call(g, "sceGxmColorSurfaceInit", color, 0u, 0u, 0u, 0u, (uint)W, (uint)H, (uint)W, data));
                uint pp = h.Alloc(0x40);
                Assert.Equal(0, Call(g, "sceGxmShaderPatcherCreate", pp, new Ptr<uint>(o)));
                uint patcher = GuestMemory.R32(o), vid = h.Word(0), fid = h.Word(0);
                Call(g, "sceGxmShaderPatcherRegisterProgram", patcher, Program(h, false), new Ptr<uint>(vid));
                Call(g, "sceGxmShaderPatcherRegisterProgram", patcher, Program(h, true), new Ptr<uint>(fid));
                uint attr = h.Alloc(8), stream = h.Alloc(4), vp = h.Word(0), fpo = h.Word(0);
                GuestMemory.Write(attr + 5, (byte)3);
                GuestMemory.Write(stream, (ushort)12);
                Assert.Equal(0, Call(g, "sceGxmShaderPatcherCreateVertexProgram", patcher, GuestMemory.R32(vid), attr, 1u, stream, 1u, new Ptr<uint>(vp)));
                Assert.Equal(0, Call(g, "sceGxmShaderPatcherCreateFragmentProgram", patcher, GuestMemory.R32(fid), 0u, 0u, 0u, GuestMemory.R32(vp), new Ptr<uint>(fpo)));
                // one triangle over the whole surface
                uint verts = h.Alloc(36), idx = h.Alloc(8);
                float[] v = [-1, -1, 0.5f, 3, -1, 0.5f, -1, 3, 0.5f];
                for (int i = 0; i < v.Length; i++)
                {
                    GuestMemory.Write(verts + (uint)i * 4, v[i]);
                }

                GuestMemory.Write(idx, (ushort)0);
                GuestMemory.Write(idx + 2, (ushort)1);
                GuestMemory.Write(idx + 4, (ushort)2);
                uint word = h.Word(0), notif = h.Alloc(8);
                GuestMemory.W32(notif, word);
                uint visibility = h.Alloc(0x100);
                Assert.Equal(0, Call(g, "sceGxmSetVisibilityBuffer", ctx, visibility, 64u));

                void Scene(uint value)
                {
                    GuestMemory.W32(notif + 4, value);
                    Assert.Equal(0, Call(g, "sceGxmBeginScene", ctx, 0u, rt, 0u, 0u, 0u, color, 0u));
                    Invoke(g, "sceGxmSetVertexProgram", ctx, GuestMemory.R32(vp));
                    Invoke(g, "sceGxmSetFragmentProgram", ctx, GuestMemory.R32(fpo));
                    Invoke(g, "sceGxmSetCullMode", ctx, 0u);
                    Assert.Equal(0, Call(g, "sceGxmSetVertexStream", ctx, 0u, verts));
                    Invoke(g, "sceGxmSetFrontVisibilityTestIndex", ctx, 0u);
                    Invoke(g, "sceGxmSetFrontVisibilityTestOp", ctx, 1u); // SET
                    Invoke(g, "sceGxmSetFrontVisibilityTestEnable", ctx, 1u);
                    for (int d = 0; d < 8; d++)
                    {
                        Assert.Equal(0, Call(g, "sceGxmDraw", ctx, 0u, 0u, idx, 3u));
                    }

                    Assert.Equal(0, Call(g, "sceGxmEndScene", ctx, 0u, notif));
                }

                // the result the draws leave (the first round waits for everything)
                const uint Marker = 0xDEADBEEF;
                GuestMemory.W32(visibility, Marker);
                Scene(1);
                Assert.Equal(0, Call(g, "sceGxmFinish", ctx));
                uint result = GuestMemory.R32(visibility);
                Assert.NotEqual(Marker, result);

                for (uint round = 2; round < 40; round++)
                {
                    Assert.Equal(0, Call(g, "sceGxmFinish", ctx));
                    GuestMemory.W32(visibility, Marker);
                    Scene(round);
                    // more scenes keep the worker recording while this one's frame is on the GPU
                    Scene(round + 1000);
                    while (GuestMemory.R32(word) != round && GuestMemory.R32(word) != round + 1000)
                    {
                        Thread.Yield();
                    }

                    Assert.Equal(result, GuestMemory.R32(visibility));
                }
                Assert.Equal(0, Call(g, "sceGxmFinish", ctx));
                if (mode.Length > 0)
                {
                    Assert.Contains("GPU done -> published", renderer.Report());
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
            renderer.Dispose();
        }
    }
}
