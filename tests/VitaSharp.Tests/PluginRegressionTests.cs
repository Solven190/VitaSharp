using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The path a homebrew port that needs kernel plugins takes (seen with the Dead Space port and its two plugins): with the
/// option on, the configuration lists a memory/exception bridge plugin (it imports module information, segment offsets,
/// exports, data injections and the spinlocks) and then a plugin that hooks a firmware export; both load and start in
/// file order, the second's hook works, and the game's user-mode module lookup answers; with the option off nothing
/// loads and the lookup does nothing. Synthetic modules reproduce the imports of the real plugins.
/// </summary>
[Collection("guest")]
public sealed class PluginRegressionTests(GuestMemoryFixture fixture) : IDisposable
{
    // the framework functions the two real plugins import (from their import tables)
    private const uint GetModuleInfoForKernel = 0x6E931197, InjectDataForKernel = 0x9FA50038, InjectReleaseForKernel = 0x51BB3EC9,
        ModuleGetOffset = 0x85FDDCB0, ModuleGetExportFunc = 0xF28E9A36, HookExportForKernel = 0x45CCFFC0, HookReleaseForKernel = 0xD22678CA;

    private const uint SpinlockLowLockCpuSuspendIntr = 0xD32ACE9E, SpinlockLowUnlockCpuResumeIntr = 0x7BB9D5DF, CpuForDriver = 0x40ECDB0E;

    private HleHarness? _harness;

    public void Dispose() => _harness?.Dispose();

    private HleHarness Setup(bool enabled)
    {
        _harness = new HleHarness(fixture, "kplugreg", enabled);
        string folder = Path.Combine(_harness.Root, "kernel_plugins");
        Directory.CreateDirectory(folder);
        SceModuleBuilder bridge = new SceModuleBuilder("BridgePlugin")
            .Import("PluginLib", 0x11111111, GetModuleInfoForKernel, InjectDataForKernel, InjectReleaseForKernel, ModuleGetOffset, ModuleGetExportFunc)
            .Import("SceCpuForDriver", CpuForDriver, SpinlockLowLockCpuSuspendIntr, SpinlockLowUnlockCpuResumeIntr)
            .Start(0);
        bridge.CallImport(0, 0);
        bridge.CallImport(1, 0);
        SceModuleBuilder hooker = new SceModuleBuilder("HookerPlugin").Import("PluginLib", 0x11111111, HookExportForKernel, HookReleaseForKernel).Start(0);
        hooker.CallImport(0, 0);
        File.WriteAllBytes(Path.Combine(folder, "bridge.skprx"), bridge.Build());
        File.WriteAllBytes(Path.Combine(folder, "hooker.skprx"), hooker.Build());
        File.WriteAllText(Path.Combine(folder, "config.txt"), "*KERNEL\nbridge.skprx\nhooker.skprx\n");
        return _harness;
    }

    [Fact]
    public void OnBothPluginsStartInOrderAndTheGamesLookupAnswers()
    {
        HleHarness harness = Setup(true);
        Assert.Equal(["bridge.skprx: loaded and started (BridgePlugin)", "hooker.skprx: loaded and started (HookerPlugin)"],
            harness.Emu.LoadPluginsIfEnabled());

        // the game's user-mode lookup of a module of its own process (here: a module loaded as user code)
        var game = new SceModuleBuilder("GameModule");
        game.ReturnConstant(1);
        string dir = Path.Combine(harness.Root, "ux0", "data");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "game.suprx"), game.Build());
        LoadedModule user = harness.Emu.LoadModule("ux0:data/game.suprx");
        uint info = harness.Alloc(0x38);
        GuestMemory.Fill(info, 0, 0x38);
        GuestMemory.W32(info, 0x38);
        Assert.Equal(0, Call(harness.Module<PluginFramework>(), "GetModuleInfoForUser", harness.Str("GameModule"), info));
        Assert.Equal((uint)user.Uid, GuestMemory.R32(info + 4));
    }

    [Fact]
    public void OffNothingLoadsAndTheLookupDoesNothing()
    {
        HleHarness harness = Setup(false);
        Assert.Empty(harness.Emu.LoadPluginsIfEnabled());
        Assert.DoesNotContain(HleHarness.KernelModules(harness.Emu).Values, m => m.IsKernel && m.Name is "BridgePlugin" or "HookerPlugin");
        uint info = harness.Alloc(0x38);
        GuestMemory.Fill(info, 0xEE, 0x38);
        GuestMemory.W32(info, 0x38);
        Assert.Equal(0, Call(harness.Module<PluginFramework>(), "GetModuleInfoForUser", harness.Str("GameModule"), info));
        Assert.Equal(0xEEEEEEEEu, GuestMemory.R32(info + 4));
    }

    [Fact]
    public void AGameWithoutPluginsRunsTheSameWithTheOptionOnOrOff()
    {
        // a game that imports nothing of the framework: the option changes neither its imports nor its modules
        foreach (bool enabled in new[] { false, true })
        {
            using var harness = new HleHarness(fixture, "kplugnone", enabled);
            SceModuleBuilder game = new SceModuleBuilder("PlainGame").Import("SceThreadmgrForDriver", 0xE2C40624, 0x59D06540u);
            int call = game.CallImport(0, 0);
            string dir = Path.Combine(harness.Root, "ux0", "data");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "plain.skprx"), game.Build());
            Assert.Empty(harness.Emu.LoadPluginsIfEnabled());
            LoadedModule module = harness.Emu.LoadModule("ux0:data/plain.skprx", kernel: true);
            harness.OnGuest(t => Assert.Equal((uint)t.Uid, t.CallGuest(SceModuleBuilder.FunctionAddress(module.Segments[0].Address, call))));
        }
    }
}
