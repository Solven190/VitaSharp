using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The plugin framework libraries the emulator offers with the option "Enable Kernel Plugins" on
/// (<see cref="PluginFramework"/>): module information by name, a module's exports and segment offsets, data
/// injections and their release, the framework's error codes; with the option off every call does nothing. The module
/// looked at is a synthetic kernel module.
/// </summary>
[Collection("guest")]
public sealed class PluginFrameworkTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int ErrorSystem = unchecked((int)0x90010000), ErrorNotFound = unchecked((int)0x90010002),
        ErrorInvalidArgs = unchecked((int)0x90010003), ErrorPatchExists = unchecked((int)0x90010005);

    private const uint ProbeLibNid = 0x0BADF00D, ProbeFunctionNid = 0x13572468;

    private HleHarness? _harness;

    public void Dispose() => _harness?.Dispose();

    private HleHarness Harness(bool enabled)
    {
        _harness = new HleHarness(fixture, "kplugfw", enabled);
        return _harness;
    }

    private static LoadedModule LoadProbe(HleHarness harness, out int function)
    {
        var builder = new SceModuleBuilder("ProbeModule");
        function = builder.ReturnConstant(7);
        byte[] elf = builder.Export("ProbeLib", ProbeLibNid, 0x0001, (ProbeFunctionNid, function)).Build();
        string dir = Path.Combine(harness.Root, "ur0", "tai");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "probe.skprx"), elf);
        return harness.Emu.LoadModule("ur0:tai/probe.skprx", kernel: true);
    }

    private static uint Info(HleHarness harness, uint size)
    {
        uint info = harness.Alloc(0x40);
        GuestMemory.Fill(info, 0xEE, 0x40);
        GuestMemory.W32(info, size);
        return info;
    }

    [Fact]
    public void ModuleInformationByName()
    {
        HleHarness harness = Harness(true);
        PluginFramework framework = harness.Module<PluginFramework>();
        LoadedModule probe = LoadProbe(harness, out _);
        uint info = Info(harness, 0x38);
        Assert.Equal(0, Call(framework, "GetModuleInfoForKernel", KernelSpace.KernelPid, harness.Str("ProbeModule"), info));
        Assert.Equal((uint)probe.Uid, GuestMemory.R32(info + 4));
        Assert.Equal(probe.ModuleNid, GuestMemory.R32(info + 8));
        Assert.Equal("ProbeModule", GuestMemory.ReadCString(info + 0xC));
        Assert.Equal(probe.ExportsStart, GuestMemory.R32(info + 0x28));
        Assert.Equal(probe.ExportsEnd, GuestMemory.R32(info + 0x2C));
        Assert.Equal(probe.ImportsStart, GuestMemory.R32(info + 0x30));
        Assert.Equal(probe.ImportsEnd, GuestMemory.R32(info + 0x34));

        // by name and module NID; a wrong NID, an unknown name and a short record are refused
        uint byNid = Info(harness, 0x38);
        Assert.Equal(0, Call(framework, "ModuleGetByNameNid", KernelSpace.KernelPid, harness.Str("ProbeModule"), probe.ModuleNid, byNid));
        Assert.Equal((uint)probe.Uid, GuestMemory.R32(byNid + 4));
        Assert.Equal(ErrorNotFound, Call(framework, "ModuleGetByNameNid", KernelSpace.KernelPid, harness.Str("ProbeModule"), ~probe.ModuleNid, byNid));
        Assert.Equal(ErrorNotFound, Call(framework, "GetModuleInfoForKernel", KernelSpace.KernelPid, harness.Str("NoSuchModule"), info));
        Assert.Equal(ErrorSystem, Call(framework, "GetModuleInfoForKernel", KernelSpace.KernelPid, harness.Str("ProbeModule"), Info(harness, 0x34)));

        // no name: the newest module of the process
        uint newest = Info(harness, 0x38);
        Assert.Equal(0, Call(framework, "GetModuleInfoForKernel", KernelSpace.KernelPid, 0u, newest));
        Assert.Equal((uint)probe.Uid, GuestMemory.R32(newest + 4));
    }

    [Fact]
    public void ExportsAndSegmentOffsets()
    {
        HleHarness harness = Harness(true);
        PluginFramework framework = harness.Module<PluginFramework>();
        LoadedModule probe = LoadProbe(harness, out int function);
        uint codeBase = probe.Segments[0].Address;
        uint result = harness.Word(0);
        Assert.Equal(0, Call(framework, "ModuleGetExportFunc", KernelSpace.KernelPid, harness.Str("ProbeModule"), ProbeLibNid, ProbeFunctionNid, result));
        Assert.Equal(SceModuleBuilder.FunctionAddress(codeBase, function), GuestMemory.R32(result));
        Assert.Equal(0, Call(framework, "ModuleGetExportFunc", KernelSpace.KernelPid, harness.Str("ProbeModule"), 0xFFFFFFFFu, ProbeFunctionNid, result));
        Assert.Equal(ErrorNotFound, Call(framework, "ModuleGetExportFunc", KernelSpace.KernelPid, harness.Str("ProbeModule"), ProbeLibNid, 0x1u, result));

        Assert.Equal(0, Call(framework, "ModuleGetOffset", KernelSpace.KernelPid, probe.Uid, 0, 0x10u, result));
        Assert.Equal(codeBase + 0x10, GuestMemory.R32(result));
        Assert.Equal(ErrorInvalidArgs, Call(framework, "ModuleGetOffset", KernelSpace.KernelPid, probe.Uid, 4, 0u, result));
        Assert.Equal(ErrorInvalidArgs, Call(framework, "ModuleGetOffset", KernelSpace.KernelPid, probe.Uid, 0, probe.Segments[0].Size + 1, result));
    }

    [Fact]
    public void InjectionsReplaceTheBytesUntilReleased()
    {
        HleHarness harness = Harness(true);
        PluginFramework framework = harness.Module<PluginFramework>();
        LoadedModule probe = LoadProbe(harness, out int function);
        uint target = SceModuleBuilder.FunctionAddress(probe.Segments[0].Address, function);
        uint original = GuestMemory.R32(target & ~1u);
        uint data = harness.Word(0x47702009); // movs r0, #9; bx lr
        harness.OnGuest(t =>
        {
            Assert.Equal(7u, t.CallGuest(target));
            int uid = Call(framework, "InjectDataForKernel", KernelSpace.KernelPid, probe.Uid, 0, (target & ~1u) - probe.Segments[0].Address, data, 4u);
            Assert.True(uid > 0, $"inject returned 0x{uid:X8}");
            Assert.Equal(9u, t.CallGuest(target));
            Assert.Equal(ErrorPatchExists, Call(framework, "InjectAbsForKernel", KernelSpace.KernelPid, (target & ~1u) + 2, data, 4u));
            Assert.Equal(0, Call(framework, "InjectReleaseForKernel", uid));
            Assert.Equal(original, GuestMemory.R32(target & ~1u));
            Assert.Equal(7u, t.CallGuest(target));
            Assert.Equal(ErrorSystem, Call(framework, "InjectReleaseForKernel", uid));
        });
    }

    [Fact]
    public void WithTheOptionOffNothingHappens()
    {
        HleHarness harness = Harness(false);
        PluginFramework framework = harness.Module<PluginFramework>();
        LoadProbe(harness, out _);
        uint info = Info(harness, 0x38);
        Assert.Equal(0, Call(framework, "GetModuleInfoForKernel", KernelSpace.KernelPid, harness.Str("ProbeModule"), info));
        Assert.Equal(0xEEEEEEEEu, GuestMemory.R32(info + 4));
    }

    [Fact]
    public void TheLoaderStartsPluginsThatImportTheFrameworkAndSkipsAModuleThatExportsIt()
    {
        HleHarness harness = Harness(true);
        string dir = Path.Combine(harness.Root, "ur0", "tai");
        Directory.CreateDirectory(dir);
        // a plugin importing a framework function (module information) starts
        SceModuleBuilder user = new SceModuleBuilder("FrameworkUser").Import("PluginLib", 0x12345678, 0x6E931197u).Start(0);
        user.CallImport(0, 0);
        File.WriteAllBytes(Path.Combine(dir, "user.skprx"), user.Build());
        // a module that exports the framework's functions is the user's copy of the framework: not loaded
        var provider = new SceModuleBuilder("FrameworkCopy");
        int fn = provider.ReturnConstant(0);
        File.WriteAllBytes(Path.Combine(dir, "copy.skprx"), provider.Export("PluginLib", 0x12345678, 0x0001, (0x6E931197u, fn)).Start(0).Build());
        File.WriteAllText(Path.Combine(dir, "config.txt"), "*KERNEL\nur0:tai/copy.skprx\nur0:tai/user.skprx\n");
        List<string> results = harness.Emu.LoadPluginsIfEnabled();
        Assert.Equal(["ur0:tai/copy.skprx: not loaded (the emulator provides its libraries)", "ur0:tai/user.skprx: loaded and started (FrameworkUser)"], results);
        Assert.DoesNotContain(HleHarness.KernelModules(harness.Emu).Values, m => m.Name == "FrameworkCopy");
    }
}
