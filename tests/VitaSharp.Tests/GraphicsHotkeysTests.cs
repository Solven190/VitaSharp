using VitaSharp.Core.Gpu;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Desktop;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// The window's graphics hotkeys (VitaSharp.Desktop <see cref="GraphicsHotkeys"/>) on a running emulator with a
/// software Vulkan renderer: F1 / Shift+F1 step the internal resolution through 1x..4x and back, F3 / Shift+F3 the
/// MSAA mode (native, 2x supersampling, off), each by a new renderer swapped in between two GPU scenes, with a notice
/// of the new value; F2 / Shift+F2 step the frame rate mode at once. A change the guest cannot take (a thread that
/// keeps a GPU scene open) is refused and leaves the options and the renderer as they were.
/// </summary>
[Collection("guest")]
public sealed class GraphicsHotkeysTests(GuestMemoryFixture fixture)
{
    /// <summary>The emulator, its renderer and the notices of the hotkeys.</summary>
    private sealed class Session : IDisposable
    {
        private readonly IDisposable _driver = SoftwareVulkan.Use();
        public readonly HleHarness Harness;
        public readonly VulkanRendererOptions Options = new() { VSync = false, AsyncShaders = false, AsyncPipelines = false };
        public readonly List<string> Notices = [];
        public readonly GraphicsHotkeys Keys;

        public Session(GuestMemoryFixture fixture, string tag)
        {
            Harness = new HleHarness(fixture, tag);
            Harness.Emu.Gpu.Backend = new VulkanRenderer(Options);
            Keys = new GraphicsHotkeys(Harness.Emu, Options, () => Renderer, () => new VulkanRenderer(Options), n =>
            {
                lock (Notices)
                {
                    Notices.Add(n);
                }
            });
        }

        public VulkanRenderer? Renderer => Harness.Emu.Gpu.Backend as VulkanRenderer;

        /// <summary>Presses <paramref name="key"/> and waits for its notice (the renderer swap runs on a task).</summary>
        public string Press(string key)
        {
            int before;
            lock (Notices)
            {
                before = Notices.Count;
            }
            Keys.Press(key);
            HleHarness.Until(() =>
            {
                lock (Notices)
                {
                    return Notices.Count > before;
                }
            }, $"the notice of {key}");
            lock (Notices)
            {
                return Notices[^1];
            }
        }

        public void Dispose()
        {
            GpuQueue gpu = Harness.Emu.Gpu;
            gpu.WaitIdle();
            gpu.Backend?.Dispose();
            gpu.Backend = null;
            Harness.Dispose();
            _driver.Dispose();
        }
    }

    [Fact]
    public void OnlyTheSixGraphicsKeysAreHotkeys()
    {
        foreach (string k in new[] { "F1", "Shift+F1", "F2", "Shift+F2", "F3", "Shift+F3" })
        {
            Assert.True(GraphicsHotkeys.IsHotkey(k), k);
        }
        // F4.. are the other window keys (pause, states, captures) and a game key is never a hotkey
        foreach (string k in new[] { "F4", "F8", "Shift+F4", "Ctrl+F1", "f1", "Cross", "" })
        {
            Assert.False(GraphicsHotkeys.IsHotkey(k), k);
        }
    }

    /// <summary>F1 steps 1x, 2x, 3x, 4x and wraps to 1x; Shift+F1 goes the other way (1x to 4x). Each step is a new
    /// renderer of that scale, the option a later renderer (a state load) is made with follows it, and the notice
    /// names the size of the scaled 960x544 screen.</summary>
    [SoftwareVulkanFact]
    public void F1StepsTheInternalResolutionWithANewRenderer()
    {
        using var s = new Session(fixture, "hotkeys-res");
        VulkanRenderer first = s.Renderer!;
        Assert.Equal(1f, first.InternalScale);
        foreach ((float scale, string size) in new[] { (2f, "2x (1920x1088)"), (3f, "3x (2880x1632)"), (4f, "4x (3840x2176)") })
        {
            VulkanRenderer before = s.Renderer!;
            string notice = s.Press("F1");
            Assert.Contains(size, notice);
            VulkanRenderer after = s.Renderer!;
            Assert.NotSame(before, after);
            Assert.Equal(scale, after.InternalScale);
            Assert.Equal(scale, s.Options.ResScale);
        }

        string native = s.Press("F1"); // 4x wraps to the native resolution
        Assert.Equal(1f, s.Renderer!.InternalScale);
        Assert.Contains(VitaSharp.Core.Localization.Loc.Get("GameResNative"), native);
        s.Press("Shift+F1"); // and back from 1x to 4x
        Assert.Equal(4f, s.Renderer!.InternalScale);
        s.Press("Shift+F1");
        Assert.Equal(3f, s.Renderer!.InternalScale);
    }

    /// <summary>A scale below native (0.5x, 0.75x from the launcher) is not in the hotkey's list: F1 goes to 1x first.</summary>
    [SoftwareVulkanFact]
    public void F1FromAScaleBelowNativeGoesToNative()
    {
        using var s = new Session(fixture, "hotkeys-low");
        s.Options.ResScale = 0.5f;
        s.Harness.Emu.Gpu.ReplaceBackend(() => new VulkanRenderer(s.Options), sameTitle: true);
        Assert.Equal(0.5f, s.Renderer!.InternalScale);
        s.Press("F1");
        Assert.Equal(1f, s.Renderer!.InternalScale);
    }

    /// <summary>F3 steps native multisampling, 2x supersampling, off and wraps; Shift+F3 goes the other way.</summary>
    [SoftwareVulkanFact]
    public void F3StepsTheMsaaModeWithANewRenderer()
    {
        using var s = new Session(fixture, "hotkeys-msaa");
        Assert.Equal("native", s.Renderer!.MsaaMode);
        foreach (string mode in new[] { "ss", "off", "native" })
        {
            VulkanRenderer before = s.Renderer!;
            s.Press("F3");
            Assert.NotSame(before, s.Renderer);
            Assert.Equal(mode, s.Renderer!.MsaaMode);
            Assert.Equal(mode, s.Options.Msaa);
        }

        s.Press("Shift+F3");
        Assert.Equal("off", s.Renderer!.MsaaMode);
        s.Press("Shift+F3");
        Assert.Equal("ss", s.Renderer!.MsaaMode);
    }

    /// <summary>F2 changes the frame rate mode at once (display waits read it), without a new renderer: native, fixed
    /// 30, fixed 60, unlimited, then native again; Shift+F2 goes the other way.</summary>
    [Fact]
    public void F2StepsTheFrameRateModeAtOnce()
    {
        FrameRateMode saved = FrameRate.Mode;
        using var harness = new HleHarness(fixture, "hotkeys-rate");
        var notices = new List<string>();
        var keys = new GraphicsHotkeys(harness.Emu, new VulkanRendererOptions(), () => null, () => throw new InvalidOperationException("no renderer for F2"), notices.Add);
        try
        {
            FrameRate.Mode = FrameRateMode.Native;
            foreach (FrameRateMode m in new[] { FrameRateMode.Fixed30, FrameRateMode.Fixed60, FrameRateMode.Unlimited, FrameRateMode.Native })
            {
                keys.Press("F2");
                Assert.Equal(m, FrameRate.Mode);
            }

            keys.Press("Shift+F2");
            Assert.Equal(FrameRateMode.Unlimited, FrameRate.Mode);
            keys.Press("Shift+F2");
            Assert.Equal(FrameRateMode.Fixed60, FrameRate.Mode);
            Assert.Equal(6, notices.Count); // one notice per press, right away
        }
        finally
        {
            FrameRate.Mode = saved;
        }
    }

    /// <summary>Without a renderer (a headless run that draws nothing) F1 and F3 do nothing: no notice, no swap.</summary>
    [Fact]
    public void F1AndF3WithoutARendererDoNothing()
    {
        using var harness = new HleHarness(fixture, "hotkeys-none");
        var notices = new List<string>();
        var ro = new VulkanRendererOptions();
        var keys = new GraphicsHotkeys(harness.Emu, ro, () => null, () => throw new InvalidOperationException("no renderer is made"), notices.Add);
        keys.Press("F1");
        keys.Press("Shift+F3");
        keys.Press("F9"); // not a graphics key
        Assert.Empty(notices);
        Assert.Equal(0f, ro.ResScale);
        Assert.Null(ro.Msaa);
    }

    /// <summary>A guest thread that keeps a GPU scene open past the swap's wait: the change is refused with the reason
    /// in the notice, the options go back to what they were and the renderer is the same one.</summary>
    [SoftwareVulkanFact]
    public void AChangeTheGuestCannotTakeIsRefused()
    {
        using var s = new Session(fixture, "hotkeys-refused");
        VulkanRenderer before = s.Renderer!;
        Core.Kernel.GuestThread scene = s.Harness.Emu.Kernel.CreateThread("scene-thread", 0, 0x10000100, 0x10000, 0, 0x70000);
        s.Harness.Emu.OpenSceneThread = scene;
        try
        {
            string res = s.Press("F1");
            Assert.Contains("scene", res);
            Assert.Equal(0f, s.Options.ResScale);
            string msaa = s.Press("F3");
            Assert.Contains("scene", msaa);
            Assert.Null(s.Options.Msaa);
            Assert.Same(before, s.Renderer);
            Assert.Equal(1f, before.InternalScale);
            Assert.Equal("native", before.MsaaMode);
        }
        finally
        {
            s.Harness.Emu.OpenSceneThread = null;
        }

        s.Press("F1"); // the scene closed: the next press is applied
        Assert.Equal(2f, s.Renderer!.InternalScale);
    }
}
