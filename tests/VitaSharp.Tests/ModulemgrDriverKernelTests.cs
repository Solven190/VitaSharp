using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The SceModulemgrForDriver lookups and shared-module forms (modulemgr 3.74): kernel modules by name (over 0x1A
/// characters) and by address, 0x8002D082 when none; the shared forms need a process (0x8002D017) and, to stop, no
/// flags (0x8002000A).
/// </summary>
[Collection("guest")]
public sealed class ModulemgrDriverKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int NoModule = unchecked((int)0x8002D082), NoProcess = unchecked((int)0x8002D017);

    private readonly HleHarness _harness = new(fixture, "modk");

    public void Dispose() => _harness.Dispose();

    private ModulemgrKernel Modulemgr => _harness.Module<ModulemgrKernel>();

    private (LoadedModule Module, uint Segment) AddKernelModule(string name, int uid)
    {
        uint segment = _harness.Alloc(0x100);
        var module = new LoadedModule { Uid = uid, Name = name, IsKernel = true, Segments = { [0] = new SegmentInfo(segment, 0, 0x100) }, SegmentFlags = { [0] = (5u, 0x100u, 0x100u) } };
        KernelModules(_harness.Emu)[uid] = module;
        return (module, segment);
    }

    [Theory]
    [InlineData(0xBBE1771Cu)]
    [InlineData(0x1D9E0F7Eu)]
    [InlineData(0xE2ADEF8Du)]
    [InlineData(0x02D3D0C1u)]
    public void EveryEntryIsBound(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceModulemgrForDriver").ToList();
        Assert.Single(bindings);
        Assert.False(bindings[0].Default);
    }

    [Fact]
    public void AKernelModuleIsFoundByNameAndAddress()
    {
        (LoadedModule module, uint segment) = AddKernelModule("SceSyntheticDriverWithALongName", 0x10F0003);
        Assert.Equal(module.Uid, Call(Modulemgr, "ksceKernelSearchModuleByName", _harness.Str("SceSyntheticDriverWithALongNameAndMore"))); // the first 0x1A characters
        Assert.Equal(NoModule, Call(Modulemgr, "ksceKernelSearchModuleByName", _harness.Str("SceSynthetic")));
        uint info = _harness.Alloc(0x1B8);
        GuestMemory.W32(info, 0x1B8);
        Assert.Equal(0, Call(Modulemgr, "ksceKernelGetModuleInfoByAddr", segment + 0x80, info));
        Assert.Equal(NoModule, Call(Modulemgr, "ksceKernelGetModuleInfoByAddr", segment + 0x100, info));
    }

    [Fact]
    public void TheSharedFormsCheckTheirArguments()
    {
        Assert.Equal(NoProcess, Call(Modulemgr, "ksceKernelLoadStartSharedModuleForPid", 0, "ux0:/none.suprx", 0u, 0u, 0, 0u, 0u));
        Assert.Equal(NoProcess, Call(Modulemgr, "ksceKernelStopUnloadSharedModuleForPid", 0, 1, 0u, 0u, 0, 0u, 0u));
        Assert.Equal(unchecked((int)0x8002000A), Call(Modulemgr, "ksceKernelStopUnloadSharedModuleForPid", 0x40010003, 1, 0u, 0u, 1, 0u, 0u));
    }
}
