using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;
using VitaSharp.Renderer.Vulkan;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// VITASHARP_GPU_SUBMIT_AT_NOTIFICATION=1 (VulkanRenderer EndScene): a scene whose notification waits for GPU results
/// (here its visibility test) ends the batch, so the notification can arrive as soon as that scene finished. On the
/// Vita each scene's notification is written when that scene is done; a game that polls notification memory from
/// guest code (Killzone Mercenary's render job waits for the previous frame's first scene) otherwise waits until the
/// worker runs out of work or presents, which on a busy worker is the end of the whole frame. The contract that must
/// hold either way: a notification never shows its value before the results of the work before it are in memory, and
/// notifications arrive in submission order.
/// </summary>
[Collection("guest")]
public sealed class SubmitAtNotificationTests(GuestMemoryFixture fx)
{
    private const int W = 64, H = 64;
    private const int Scenes = 4;
    private const uint Marker = 0xDEADBEEF;

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

    /// <summary>A context, render target and programs drawing one full-surface triangle with the visibility test on.</summary>
    private sealed class Setup
    {
        public required Gxm G;
        public required uint Ctx, Rt, Color, Vp, Fp, Verts, Idx, Visibility;
    }

    private static Setup Create(HleHarness h, Gxm g)
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
        uint verts = h.Alloc(36), idx = h.Alloc(8);
        float[] v = [-1, -1, 0.5f, 3, -1, 0.5f, -1, 3, 0.5f];
        for (int i = 0; i < v.Length; i++)
        {
            GuestMemory.Write(verts + (uint)i * 4, v[i]);
        }

        GuestMemory.Write(idx, (ushort)0);
        GuestMemory.Write(idx + 2, (ushort)1);
        GuestMemory.Write(idx + 4, (ushort)2);
        uint visibility = h.Alloc(0x100);
        Assert.Equal(0, Call(g, "sceGxmSetVisibilityBuffer", ctx, visibility, 64u));
        return new Setup
        {
            G = g, Ctx = ctx, Rt = rt, Color = color, Vp = GuestMemory.R32(vp), Fp = GuestMemory.R32(fpo), Verts = verts, Idx = idx,
            Visibility = visibility,
        };
    }

    /// <summary>One scene: draws with visibility test <paramref name="index"/> on, ending with notification
    /// <paramref name="notification"/> = <paramref name="value"/>.</summary>
    private static void Scene(Setup s, uint index, uint notification, uint value)
    {
        Gxm g = s.G;
        GuestMemory.W32(notification + 4, value);
        Assert.Equal(0, Call(g, "sceGxmBeginScene", s.Ctx, 0u, s.Rt, 0u, 0u, 0u, s.Color, 0u));
        Invoke(g, "sceGxmSetVertexProgram", s.Ctx, s.Vp);
        Invoke(g, "sceGxmSetFragmentProgram", s.Ctx, s.Fp);
        Invoke(g, "sceGxmSetCullMode", s.Ctx, 0u);
        Assert.Equal(0, Call(g, "sceGxmSetVertexStream", s.Ctx, 0u, s.Verts));
        Invoke(g, "sceGxmSetFrontVisibilityTestIndex", s.Ctx, index);
        Invoke(g, "sceGxmSetFrontVisibilityTestOp", s.Ctx, 1u); // SET
        Invoke(g, "sceGxmSetFrontVisibilityTestEnable", s.Ctx, 1u);
        for (int d = 0; d < 4; d++)
        {
            Assert.Equal(0, Call(g, "sceGxmDraw", s.Ctx, 0u, 0u, s.Idx, 3u));
        }

        Assert.Equal(0, Call(g, "sceGxmEndScene", s.Ctx, 0u, notification));
    }

    private static VulkanRenderer NewRenderer(string mode)
    {
        string? saved = Environment.GetEnvironmentVariable(EnvVars.GpuSubmitAtNotification);
        Environment.SetEnvironmentVariable(EnvVars.GpuSubmitAtNotification, mode.Length == 0 ? null : mode);
        try
        {
            return new VulkanRenderer(new VulkanRendererOptions { VSync = false, AsyncShaders = false, AsyncPipelines = false });
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVars.GpuSubmitAtNotification, saved);
        }
    }

    private static void Run(GuestMemoryFixture fx, string mode, Action<HleHarness, VulkanRenderer, Setup> body)
    {
        using IDisposable driver = SoftwareVulkan.Use();
        using var h = new HleHarness(fx, "submitnotify");
        VulkanRenderer renderer = NewRenderer(mode);
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
                body(h, renderer, Create(h, g));
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

    /// <summary>The switch's default: on for Android (Killzone Mercenary in gameplay on a Helio G99 phone, A B B A:
    /// 21,8/20,3 fps off, 26,2/27,3 fps on), off elsewhere; "1" and "0" force it either way.</summary>
    [Theory]
    [InlineData(null, true, true)]
    [InlineData("", true, true)]
    [InlineData(" ", true, true)]
    [InlineData(null, false, false)]
    [InlineData("0", true, false)]
    [InlineData("1", false, true)]
    [InlineData("1", true, true)]
    public void OnByDefaultOnAndroidOnly(string? setting, bool android, bool expected)
    {
        Assert.Equal(expected, VulkanRenderer.SubmitAtNotificationEnabled(setting, android));
    }

    /// <summary>
    /// The worker records several notifying scenes back to back (it never runs out of work in between, as on a busy
    /// phone): with the switch each one is its own submission; without it they share one, made when the worker goes
    /// idle. Either way every notification and every visibility result reaches guest memory, in order.
    /// </summary>
    [SoftwareVulkanTheory]
    [InlineData("", false)]
    [InlineData("1", true)]
    public void NotifyingScenesEndTheBatchOnlyWhenAsked(string mode, bool ownSubmission)
    {
        Run(fx, mode, (h, renderer, s) =>
        {
            Gxm g = s.G;
            uint[] words = new uint[Scenes];
            uint[] notifications = new uint[Scenes];
            for (int i = 0; i < Scenes; i++)
            {
                words[i] = h.Word(0);
                notifications[i] = h.Alloc(8);
                GuestMemory.W32(notifications[i], words[i]);
                GuestMemory.W32(s.Visibility + (uint)i * 4, Marker);
            }

            Assert.Equal(0, Call(g, "sceGxmFinish", s.Ctx));
            long before = renderer.SubmitCount;
            // hold the worker until every scene is queued, so it records them without going idle in between
            using var gate = new ManualResetEventSlim(false);
            h.Emu.Gpu.Submit(new SignalCommand { Signal = () => gate.Wait(), Immediate = true });
            for (int i = 0; i < Scenes; i++)
            {
                Scene(s, (uint)i, notifications[i], (uint)(100 + i));
            }

            gate.Set();
            // the guest polls the last notification from its own code (no sceGxmNotificationWait)
            while (GuestMemory.R32(words[Scenes - 1]) != 100 + Scenes - 1)
            {
                Thread.Yield();
            }

            long submits = renderer.SubmitCount - before;
            for (int i = 0; i < Scenes; i++)
            {
                Assert.Equal((uint)(100 + i), GuestMemory.R32(words[i]));
                Assert.NotEqual(Marker, GuestMemory.R32(s.Visibility + (uint)i * 4));
            }

            if (ownSubmission)
            {
                Assert.Equal(Scenes, submits);
            }
            else
            {
                Assert.True(submits < Scenes, $"{submits} submissions for {Scenes} scenes recorded back to back");
            }
            Assert.Equal(0, Call(g, "sceGxmFinish", s.Ctx));
        });
    }

    /// <summary>
    /// With the switch, a scene's notification never shows before its own visibility result, while later scenes keep
    /// the worker recording (the RetireWake contract, polled from the test thread as a game's job thread does).
    /// </summary>
    [SoftwareVulkanTheory]
    [InlineData("")]
    [InlineData("1")]
    public void ANotificationNeverShowsBeforeItsResult(string mode)
    {
        Run(fx, mode, (h, renderer, s) =>
        {
            Gxm g = s.G;
            uint word = h.Word(0), notification = h.Alloc(8);
            GuestMemory.W32(notification, word);
            GuestMemory.W32(s.Visibility, Marker);
            Scene(s, 0, notification, 1);
            Assert.Equal(0, Call(g, "sceGxmFinish", s.Ctx));
            uint result = GuestMemory.R32(s.Visibility);
            Assert.NotEqual(Marker, result);
            for (uint round = 2; round < 30; round++)
            {
                Assert.Equal(0, Call(g, "sceGxmFinish", s.Ctx));
                GuestMemory.W32(s.Visibility, Marker);
                Scene(s, 0, notification, round);
                Scene(s, 1, notification, round + 1000);
                while (GuestMemory.R32(word) != round && GuestMemory.R32(word) != round + 1000)
                {
                    Thread.Yield();
                }

                Assert.Equal(result, GuestMemory.R32(s.Visibility));
            }
            Assert.Equal(0, Call(g, "sceGxmFinish", s.Ctx));
        });
    }
}
