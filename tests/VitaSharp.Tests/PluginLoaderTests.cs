using VitaSharp.Core;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>
/// The plugin configuration (ur0:tai/config.txt): kernel modules (*KERNEL) and user modules (*ALL, *TITLEID) load
/// through the same module loader path, kernel ones first, and a module whose imports name a library nothing provides
/// is not started (<see cref="Emulator.LoadPlugins"/>). The real-module test uses the plugins of the installed data
/// folder (read only: copied into the test's own folder) and is skipped without them; none is in the repository.
/// </summary>
[Collection("guest")]
public sealed class PluginLoaderTests(GuestMemoryFixture fx, Xunit.Abstractions.ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-plugins-" + Guid.NewGuid().ToString("N"));

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

    private static string InstalledTai => Path.Combine(Environment.GetEnvironmentVariable(EnvVars.Data) is { Length: > 0 } d ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VitaSharp"), "fs", "ur0", "tai");

    private Emulator NewEmulator(string config)
    {
        string tai = Path.Combine(_root, "ur0", "tai");
        Directory.CreateDirectory(tai);
        File.WriteAllText(Path.Combine(tai, "config.txt"), config);
        return new Emulator(new EmulatorOptions { FsRoot = _root, Memory = fx.Memory, EnableJit = false, CpuBackend = "interp" });
    }

    [Fact]
    public void ConfigSectionsPickTheModulesKernelFirst()
    {
        using Emulator emu = NewEmulator("# comment\n*ALL\nur0:tai/user_all.suprx\n*OTHER00001\nur0:tai/other.suprx\n*KERNEL\nur0:tai/kernel.skprx\n");
        List<string> results = emu.LoadPlugins();
        Assert.Equal(2, results.Count);
        Assert.StartsWith("ur0:tai/kernel.skprx: not loaded", results[0]);
        Assert.StartsWith("ur0:tai/user_all.suprx: not loaded", results[1]); // missing file: the loader's error, no exception
    }

    [Fact]
    public void NoConfigLoadsNothing()
    {
        using var emu = new Emulator(new EmulatorOptions { FsRoot = _root, Memory = fx.Memory, EnableJit = false, CpuBackend = "interp" });
        Assert.Empty(emu.LoadPlugins());
    }

    private void CopyInstalledPlugins(params string[] only)
    {
        string tai = Path.Combine(_root, "ur0", "tai");
        Directory.CreateDirectory(tai);
        foreach (string file in Directory.GetFiles(InstalledTai, "*.skprx"))
        {
            if (only.Length == 0 || only.Contains(Path.GetFileName(file)))
            {
                File.Copy(file, Path.Combine(tai, Path.GetFileName(file)));
            }
        }
    }

    /// <summary>
    /// The kernel plugins of the installed data folder (copied into the test's own folder), all listed in *KERNEL: each
    /// one is read by the loader. The module that exports the plugin framework libraries (the user's copy of the
    /// framework) is not loaded, since the emulator provides them; the others either load and start or, while a
    /// framework function they import is not there yet, are not started.
    /// </summary>
    [RealPluginsFact]
    public void RealKernelPluginsGoThroughTheGenericLoader()
    {
        CopyInstalledPlugins();
        string tai = Path.Combine(_root, "ur0", "tai");
        var listed = Directory.GetFiles(tai, "*.skprx").Select(f => "ur0:tai/" + Path.GetFileName(f))
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
        using Emulator emu = NewEmulator("*KERNEL\n" + string.Join("\n", listed) + "\n");
        List<string> results = emu.LoadPlugins();
        output.WriteLine(string.Join("\n", results));
        Assert.Equal(listed.Count, results.Count);
        foreach (string r in results)
        {
            bool frameworkCopy = r.EndsWith("not loaded (the emulator provides its libraries)", StringComparison.Ordinal);
            Assert.True(frameworkCopy || r.Contains("loaded and started", StringComparison.Ordinal)
                || r.Contains("not started: no module provides", StringComparison.Ordinal), r);
        }

        foreach (LoadedModule module in emu.Kernel.Modules.Values.Where(m => m.IsKernel))
        {
            Assert.True(module.Started, module.Name);
        }
    }
}

/// <summary>[Fact] skipped when the installed data folder has no kernel plugins (ur0:tai/*.skprx).</summary>
public sealed class RealPluginsFactAttribute : FactAttribute
{
    public RealPluginsFactAttribute()
    {
        string dir = Path.Combine(Environment.GetEnvironmentVariable(EnvVars.Data) is { Length: > 0 } d ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VitaSharp"), "fs", "ur0", "tai");
        if (!Directory.Exists(dir) || Directory.GetFiles(dir, "*.skprx").Length == 0)
        {
            Skip = "no kernel plugins (ur0:tai/*.skprx) in the installed data folder";
        }
    }
}
