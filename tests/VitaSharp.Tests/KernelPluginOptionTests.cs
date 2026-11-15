using VitaSharp.Core;

namespace VitaSharp.Tests;

/// <summary>
/// The emulator option "Enable Kernel Plugins" (<see cref="EmulatorOptions.KernelPlugins"/>): off, the default, the pure
/// firmware runs and nothing loads even with a plugin configuration; on, the emulator reads the configuration of its own
/// plugin folder (kernel_plugins, apart from the Vita's file system; paths relative to it), else the Vita's
/// (ur0:tai / ux0:tai), and loads and starts the *KERNEL modules it lists. The first time, the Vita's configuration and
/// the kernel plugins it lists move into the folder (a .bak copy kept, the moved lines rewritten), leaving the plugin
/// framework's own file and the plugins' data. The plugins are synthetic kernel modules.
/// </summary>
[Collection("guest")]
public sealed class KernelPluginOptionTests(GuestMemoryFixture fx) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-pluginoption-" + Guid.NewGuid().ToString("N"));

    private string Tai => Path.Combine(_root, "ur0", "tai");

    private string Folder => Path.Combine(_root, "kernel_plugins");

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

    private static byte[] Probe(string name) => new SceModuleBuilder(name).Start(0).Build();

    /// <summary>A module that exports a plugin framework function (the user's copy of the framework).</summary>
    private static byte[] FrameworkCopy()
    {
        var builder = new SceModuleBuilder("FrameworkCopy");
        int function = builder.ReturnConstant(0);
        return builder.Export("PluginLib", 0x12345678, 0x0001, (0x6E931197u, function)).Start(0).Build();
    }

    private Emulator NewEmulator(bool? kernelPlugins)
    {
        return new Emulator(new EmulatorOptions
        {
            FsRoot = _root,
            Memory = fx.Memory,
            EnableJit = false,
            CpuBackend = "interp",
            KernelPlugins = kernelPlugins,
            KernelPluginsDir = Folder,
        });
    }

    /// <summary>The Vita's configuration in ur0:tai listing one kernel plugin there.</summary>
    private void VitaConfiguration()
    {
        Directory.CreateDirectory(Tai);
        File.WriteAllBytes(Path.Combine(Tai, "probe.skprx"), Probe("PluginProbe"));
        File.WriteAllText(Path.Combine(Tai, "config.txt"), "*KERNEL\nur0:tai/probe.skprx\n");
    }

    [Fact]
    public void OffLoadsNoPluginAndMovesNothing()
    {
        VitaConfiguration();
        using Emulator emu = NewEmulator(false);
        Assert.False(emu.KernelPluginsEnabled);
        Assert.Empty(emu.LoadPluginsIfEnabled());
        Assert.DoesNotContain(emu.Kernel.Modules.Values, m => m.IsKernel);
        Assert.True(File.Exists(Path.Combine(Tai, "config.txt")));
        Assert.False(Directory.Exists(Folder));
    }

    [Fact]
    public void OffIsTheDefault()
    {
        if (Environment.GetEnvironmentVariable(VitaSharp.Core.Util.EnvVars.KernelPlugins) == "1")
        {
            return;
        }

        VitaConfiguration();
        using Emulator emu = NewEmulator(null);
        Assert.False(emu.KernelPluginsEnabled);
        Assert.Empty(emu.LoadPluginsIfEnabled());
    }

    [Fact]
    public void TheSwitchesAreOffByDefaultAndReachTheCommandLine()
    {
        var settings = new VitaSharp.Desktop.Launcher.LauncherSettings();
        Assert.False(settings.KernelPlugins);
        Assert.Contains("--kernel-plugins=off", settings.RunOptions());
        settings.KernelPlugins = true;
        Assert.Contains("--kernel-plugins=on", settings.RunOptions());
        Assert.False(new VitaSharp.Mobile.Core.Settings.AppPreferences().KernelPlugins);

        // the command line's on/off, as the runner reads it
        System.Reflection.MethodInfo onOff = typeof(VitaSharp.Desktop.Runner).GetMethod("OnOff",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal(true, onOff.Invoke(null, [new[] { "--kernel-plugins=on" }, "--kernel-plugins"]));
        Assert.Equal(false, onOff.Invoke(null, [new[] { "--kernel-plugins=off" }, "--kernel-plugins"]));
        Assert.Null(onOff.Invoke(null, [Array.Empty<string>(), "--kernel-plugins"]));
    }

    [Fact]
    public void TheFoldersConfigurationLoadsItsPluginsAndSkipsAMissingOne()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(Path.Combine(Folder, "probe.skprx"), Probe("PluginProbe"));
        File.WriteAllText(Path.Combine(Folder, "config.txt"), "*KERNEL\nmissing.skprx\nprobe.skprx\n");
        using Emulator emu = NewEmulator(true);
        List<string> results = emu.LoadPluginsIfEnabled();
        Assert.Equal(2, results.Count);
        Assert.StartsWith("missing.skprx: not loaded", results[0]);
        Assert.Equal("probe.skprx: loaded and started (PluginProbe)", results[1]);
        Assert.Single(emu.Kernel.Modules.Values, m => m.IsKernel && m.Name == "PluginProbe");
    }

    [Fact]
    public void WithoutTheFoldersConfigurationTheVitasIsRead()
    {
        VitaConfiguration();
        using Emulator emu = NewEmulator(true);
        // the loader alone (no move): the Vita's configuration and path
        Assert.Equal(["ur0:tai/probe.skprx: loaded and started (PluginProbe)"], emu.LoadPlugins());
    }

    [Fact]
    public void TheFirstRunMovesOnlyTheConfigurationAndThePluginsStayInTai()
    {
        VitaConfiguration();
        File.WriteAllBytes(Path.Combine(Tai, "framework.skprx"), FrameworkCopy());
        File.WriteAllText(Path.Combine(Tai, "probe.cfg"), "plugin data");
        string original = "# plugins\n*KERNEL\nur0:tai/framework.skprx\nur0:tai/probe.skprx\n*ALL\nux0:tai/user.suprx\n";
        File.WriteAllText(Path.Combine(Tai, "config.txt"), original);
        using (Emulator emu = NewEmulator(true))
        {
            List<string> results = emu.LoadPluginsIfEnabled();
            Assert.Equal(["ur0:tai/framework.skprx: not loaded (the emulator provides its libraries)", "ur0:tai/probe.skprx: loaded and started (PluginProbe)"],
                results.Where(r => !r.StartsWith("ux0:", StringComparison.Ordinal)));
        }

        // the configuration moved unchanged (a .bak copy kept); the plugins, the framework's file and the data stayed
        Assert.Equal(original, File.ReadAllText(Path.Combine(Folder, "config.txt")));
        Assert.Equal(original, File.ReadAllText(Path.Combine(Folder, "config.txt.bak")));
        Assert.False(File.Exists(Path.Combine(Tai, "config.txt")));
        Assert.True(File.Exists(Path.Combine(Tai, "probe.skprx")));
        Assert.True(File.Exists(Path.Combine(Tai, "framework.skprx")));
        Assert.True(File.Exists(Path.Combine(Tai, "probe.cfg")));
        Assert.False(File.Exists(Path.Combine(Folder, "probe.skprx")));

        // a second run finds the folder's configuration and moves nothing
        File.WriteAllText(Path.Combine(Tai, "config.txt"), "*KERNEL\nur0:tai/late.skprx\n");
        using (Emulator again = NewEmulator(true))
        {
            again.LoadPluginsIfEnabled();
        }

        Assert.True(File.Exists(Path.Combine(Tai, "config.txt")));
        Assert.Equal(original, File.ReadAllText(Path.Combine(Folder, "config.txt")));
    }

    [Fact]
    public void APluginAnEarlierVersionMovedGoesBackToItsVitaPath()
    {
        // the layout an earlier version left: the plugin in the folder, its line rewritten, the original in the .bak
        Directory.CreateDirectory(Folder);
        Directory.CreateDirectory(Tai);
        File.WriteAllBytes(Path.Combine(Folder, "probe.skprx"), Probe("PluginProbe"));
        File.WriteAllText(Path.Combine(Folder, "config.txt"), "*KERNEL\nprobe.skprx\n");
        File.WriteAllText(Path.Combine(Folder, "config.txt.bak"), "*KERNEL\nur0:tai/probe.skprx\n");
        using Emulator emu = NewEmulator(true);
        Assert.Equal(["ur0:tai/probe.skprx: loaded and started (PluginProbe)"], emu.LoadPluginsIfEnabled());
        Assert.True(File.Exists(Path.Combine(Tai, "probe.skprx")));
        Assert.False(File.Exists(Path.Combine(Folder, "probe.skprx")));
        Assert.Equal("*KERNEL\nur0:tai/probe.skprx\n", File.ReadAllText(Path.Combine(Folder, "config.txt")));
    }
}
