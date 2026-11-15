using VitaSharp.Core;
using VitaSharp.Core.Loader;

namespace VitaSharp.Tests;

/// <summary>
/// The emulator option "Enable Kernel Plugins" (<see cref="EmulatorOptions.KernelPlugins"/>): off, the default, the pure
/// firmware runs and nothing of ur0:tai loads even with a plugin configuration; on, the emulator reads the
/// configuration and loads and starts the *KERNEL modules it lists. The plugin is a synthetic kernel module.
/// </summary>
[Collection("guest")]
public sealed class KernelPluginOptionTests(GuestMemoryFixture fx) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-pluginoption-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A plugin configuration listing one synthetic kernel module whose module_start succeeds.</summary>
    private Emulator NewEmulator(bool? kernelPlugins)
    {
        string tai = Path.Combine(_root, "ur0", "tai");
        Directory.CreateDirectory(tai);
        byte[] elf = new SceModuleBuilder("PluginProbe").Start(0).Build();
        File.WriteAllBytes(Path.Combine(tai, "probe.skprx"), elf);
        File.WriteAllText(Path.Combine(tai, "config.txt"), "*KERNEL\nur0:tai/probe.skprx\n");
        return new Emulator(new EmulatorOptions
        {
            FsRoot = _root,
            Memory = fx.Memory,
            EnableJit = false,
            CpuBackend = "interp",
            KernelPlugins = kernelPlugins,
        });
    }

    [Fact]
    public void OffLoadsNoPlugin()
    {
        using Emulator emu = NewEmulator(false);
        Assert.False(emu.KernelPluginsEnabled);
        Assert.Empty(emu.LoadPluginsIfEnabled());
        Assert.DoesNotContain(emu.Kernel.Modules.Values, m => m.IsKernel);
    }

    [Fact]
    public void OffIsTheDefault()
    {
        if (Environment.GetEnvironmentVariable(VitaSharp.Core.Util.EnvVars.KernelPlugins) == "1")
        {
            return;
        }

        using Emulator emu = NewEmulator(null);
        Assert.False(emu.KernelPluginsEnabled);
        Assert.Empty(emu.LoadPluginsIfEnabled());
    }

    [Fact]
    public void OnLoadsAndStartsTheKernelPluginsOfTheConfiguration()
    {
        using Emulator emu = NewEmulator(true);
        Assert.True(emu.KernelPluginsEnabled);
        List<string> results = emu.LoadPluginsIfEnabled();
        Assert.Equal(["ur0:tai/probe.skprx: loaded and started (PluginProbe)"], results);
        LoadedModule probe = Assert.Single(emu.Kernel.Modules.Values, m => m.IsKernel && m.Name == "PluginProbe");
        Assert.Equal("ur0:tai/probe.skprx", probe.Path);
    }
}
