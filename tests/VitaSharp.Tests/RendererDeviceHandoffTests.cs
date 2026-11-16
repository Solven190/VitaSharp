using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using System.Text.RegularExpressions;
using VitaSharp.Core.Gpu;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// A renderer replaced on the same window (a state load, a LoadExec) keeps the window's Vulkan device, for every
/// front-end. The Android session made a new instance, device, surface and swapchain at each replacement (only the
/// desktop window set KeepDevice): on Mali the second recreation lost the device in the first frame's fence (Killzone
/// loading a state twice; Uncharted loading one after its LoadExec, frozen at 0 fps, 13/11/2026), as the recreation had
/// crashed the NVIDIA driver before the desktop kept its device (06/11/2026).
/// </summary>
[Collection("guest")]
public sealed class RendererDeviceHandoffTests(GuestMemoryFixture fixture)
{
    /// <summary>A presentation surface without a window (VK_EXT_headless_surface, which the software devices have).</summary>
    private sealed unsafe class HeadlessSurface : IPresentationSurface
    {
        public string[] RequiredInstanceExtensions => [ExtHeadlessSurface.ExtensionName, "VK_KHR_surface"];

        public (int Width, int Height) FramebufferSize => (960, 544);

        public SurfaceKHR CreateSurface(Instance instance) => throw new NotSupportedException("needs the renderer's entry points");

        public SurfaceKHR CreateSurface(Vk vk, Instance instance)
        {
            if (!vk.TryGetInstanceExtension(instance, out ExtHeadlessSurface headless))
            {
                throw new NotSupportedException("VK_EXT_headless_surface is not available");
            }

            var info = new HeadlessSurfaceCreateInfoEXT { SType = StructureType.HeadlessSurfaceCreateInfoExt };
            SurfaceKHR surface;
            headless.CreateHeadlessSurface(instance, &info, null, &surface).Ok("vkCreateHeadlessSurfaceEXT");
            return surface;
        }
    }

    /// <summary>
    /// The scenarios that lost the device on the phone, through the path the emulator takes
    /// (<see cref="GpuQueue.ReplaceBackend"/>, as LoadExec and SaveStates.Load call it), with the options a front-end
    /// makes: a LoadExec then a state load (Uncharted), then a second load right after (Killzone). Every replacement
    /// keeps the first renderer's device and presents frames.
    /// </summary>
    [SoftwareVulkanHeadlessFact]
    public void LoadExecThenTwoStateLoadsKeepTheWindowsDevice()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        using var harness = new HleHarness(fixture, "vkhandoff");
        var options = new VulkanRendererOptions { Surface = new HeadlessSurface(), VSync = false };
        GpuQueue gpu = harness.Emu.Gpu;
        try
        {
            var workload = new RendererTextureTests.Workload(harness, multisample: 0);
            var first = new VulkanRenderer(options);
            Assert.False(first.DeviceKept);
            string device = first.DeviceName;
            gpu.Backend = first;
            Assert.True(workload.Run(64) > 0);
            foreach (string step in new[] { "LoadExec", "state load", "second state load" })
            {
                gpu.ReplaceBackend(() => new VulkanRenderer(options), sameTitle: step != "LoadExec");
                VulkanRenderer renderer = Assert.IsType<VulkanRenderer>(gpu.Backend);
                Assert.True(renderer.DeviceKept, $"the {step} made a new Vulkan device for the same window");
                Assert.Equal(device, renderer.DeviceName);
                long presents = renderer.PresentCount;
                Assert.True(workload.Run(64) > 0);
                gpu.WaitIdle();
                Assert.True(renderer.PresentCount > presents, $"no frame presented after the {step}");
            }

            // the session ends: its last renderer and the device it kept are released...
            gpu.WaitIdle();
            gpu.Backend!.Dispose();
            gpu.Backend = null;
            options.ReleaseKeptDevice();
            // ...and the next session of the same process (another game, a new window) makes its device and presents
            var nextSession = new VulkanRendererOptions { Surface = new HeadlessSurface(), VSync = false };
            var fresh = new VulkanRenderer(nextSession);
            try
            {
                Assert.False(fresh.DeviceKept);
                gpu.Backend = fresh;
                Assert.True(workload.Run(64) > 0);
                gpu.WaitIdle();
                Assert.True(fresh.PresentCount > 0, "the next session presented no frame");
            }
            finally
            {
                gpu.WaitIdle();
                gpu.Backend = null;
                fresh.Dispose();
                nextSession.ReleaseKeptDevice();
            }
        }
        finally
        {
            gpu.WaitIdle();
            gpu.Backend?.Dispose();
            gpu.Backend = null;
            options.ReleaseKeptDevice();
        }
    }

    [Fact]
    public void RendererOptionsKeepTheDeviceByDefault()
    {
        Assert.True(new VulkanRendererOptions().KeepDevice);
    }

    /// <summary>
    /// Every front-end that makes renderers for a window (the desktop Runner and its tools, the Android session) leaves
    /// <see cref="VulkanRendererOptions.KeepDevice"/> on: the bug existed because only the desktop window turned it on.
    /// The front-ends are read from the sources (the Android project is not built by the suite).
    /// </summary>
    [Fact]
    public void NoFrontEndTurnsKeepDeviceOff()
    {
        string root = RepoRoot();
        var makers = new List<string>();
        foreach (string project in new[] { "VitaSharp.Desktop", "VitaSharp.Android", "VitaSharp.Mobile.Core" })
        {
            string dir = Path.Combine(root, "src", project);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                string text = File.ReadAllText(file);
                Assert.False(Regex.IsMatch(text, @"KeepDevice\s*=\s*false"), $"{Path.GetRelativePath(root, file)} turns KeepDevice off");
                if (text.Contains("new VulkanRendererOptions", StringComparison.Ordinal))
                {
                    makers.Add(project);
                }
            }
        }

        // both front-ends make their own options: the default reaches them
        Assert.Contains("VitaSharp.Desktop", makers);
        Assert.Contains("VitaSharp.Android", makers);
        Assert.True(new VulkanRendererOptions().KeepDevice);
    }

    /// <summary>The checkout this test file was compiled from: found from the source path, not the output folder,
    /// so a build with -o outside the repository still finds it.</summary>
    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (string? dir = Path.GetDirectoryName(source); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "VitaSharp.sln")))
            {
                return dir;
            }
        }

        throw new InvalidOperationException("repository root not found from " + source);
    }
}
