using VitaSharp.Core.Firmware;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Loader.Self;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>The SceRegMgr syscalls of SceRegistryMgr (FW 3.74) that only system programs may call.</summary>
[Collection("guest")]
public sealed class RegMgrUserTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int NotSystemProgram = unchecked((int)0x80010058);
    private const ulong GameAuthorityId = 0x210000101CC404D8UL;

    private readonly HleHarness _harness = new(fixture, "regmgr-user");

    public void Dispose() => _harness.Dispose();

    private void RunAsSystemProgram()
    {
        byte[] capability = new byte[ProgramAccess.CapabilitySize];
        capability[0] = 0x40;
        _harness.Emu.Access = new ProgramAccess(0x2800000000000025UL, capability, false);
        Assert.True(_harness.Emu.Access.IsSystemProgram);
    }

    [Fact]
    public void GetRegVersionAnswersSystemProgramsWithTheRegistryVersion()
    {
        RegMgrSystem regmgr = _harness.Module<RegMgrSystem>();

        // a game is refused before the registry is read
        _harness.Emu.Access = new ProgramAccess(GameAuthorityId, new byte[ProgramAccess.CapabilitySize], false);
        Assert.Equal(NotSystemProgram, Call(regmgr, "sceRegMgrGetRegVersion"));

        // no firmware template: the FW 3.74 template's version
        RunAsSystemProgram();
        Assert.Equal(RegMgrSystem.Fw374RegistryVersion, Call(regmgr, "sceRegMgrGetRegVersion"));

        // the current value of /REGISTRY/category_version, not its default
        var registry = Registry.For(_harness.Root);
        registry.Define("/REGISTRY/category_version", Registry.KeyType.Int, 4, () => RegMgrSystem.Fw374RegistryVersion);
        Assert.True(registry.SetInt("/REGISTRY/", "category_version", 0x00360000));
        Assert.Equal(0x00360000, Call(regmgr, "sceRegMgrGetRegVersion"));
    }
}
