using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceSblAIMgrForDriver (SceSysmem 3.74): the unit's identity queries over the boot loader's product code block, on
/// the emulated retail North American handheld (product 0x104, sub code 0x10), and the agreement between the kernel
/// entries and what the bridge (_vshSblAimgr*) reports for the same unit.
/// </summary>
[Collection("guest")]
public sealed class SblAimgrKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint DipswRunAsDolce = 0x98;

    private readonly HleHarness _harness = new(fixture, "aimgr");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0x14345161u)]
    [InlineData(0xB33CEC8Fu)]
    [InlineData(0x47D9CF13u)]
    [InlineData(0xD78B04A2u)]
    [InlineData(0xF4B98F66u)]
    [InlineData(0x71608CA3u)]
    [InlineData(0xC6E83F34u)]
    [InlineData(0x963CA644u)]
    [InlineData(0x6D5A3FC9u)]
    [InlineData(0xFF5784B9u)]
    [InlineData(0x05F79D4Au)]
    [InlineData(0x3B638885u)]
    [InlineData(0x274663A0u)]
    [InlineData(0xBB9D146Bu)]
    [InlineData(0x37A79140u)]
    [InlineData(0xE5E47FF7u)]
    [InlineData(0x4273B97Bu)]
    public void EveryExportOfTheLibraryHasARealEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceSblAIMgrForDriver").ToList();
        Assert.Single(bindings);
        Assert.False(bindings[0].Default);
        Assert.NotNull(_harness.Emu.Hle.ImplOf(nid));
    }

    [Fact]
    public void ARetailHandheldIsCexAndNothingElse()
    {
        Assert.Equal(1, Call(Services, "ksceSblAimgrIsCEX"));
        Assert.Equal(0, Call(Services, "ksceSblAimgrIsDEX"));
        Assert.Equal(0, Call(Services, "ksceSblAimgrIsTest"));
        Assert.Equal(0, Call(Services, "ksceSblAimgrIsTool"));
        Assert.Equal(0, Call(Services, "ksceSblAimgrIsJapaneseFat"));
        Assert.Equal(0, Call(Services, "ksceSblAimgrIsToolRev3"));
        Assert.Equal(0, Call(Services, "ksceSblAimgrIsToolRev4"));
        Assert.Equal(0, Call(Services, "ksceSblAimgrIsToolRev5"));
        Assert.Equal(0, Call(Services, "ksceSblAimgrIsPrototypeRev2"));
        Assert.Equal(0, Call(Services, "ksceSblAimgrIsPrototypeRev7"));
    }

    [Fact]
    public void AHandheldIsAVitaAndNotAPsTv()
    {
        Assert.Equal(1, Call(Services, "ksceSblAimgrIsGenuineVITA"));
        Assert.Equal(1, Call(Services, "ksceSblAimgrIsVITA"));
        Assert.Equal(0, Call(Services, "ksceSblAimgrIsGenuineDolce"));
        Assert.Equal(0, Call(Services, "ksceSblAimgrIsDolce"));
    }

    /// <summary>IsDolce also answers yes for a handheld with the debug switch 0x98 (run as a PS TV); the genuine test
    /// and the handheld tests look at the codes only.</summary>
    [Fact]
    public void TheRunAsDolceSwitchMakesOnlyIsDolceAnswerYes()
    {
        Call(Services, "ksceKernelSetDipsw", DipswRunAsDolce);
        try
        {
            Assert.Equal(1, Call(Services, "ksceSblAimgrIsDolce"));
            Assert.Equal(0, Call(Services, "ksceSblAimgrIsGenuineDolce"));
            Assert.Equal(1, Call(Services, "ksceSblAimgrIsGenuineVITA"));
        }
        finally
        {
            Call(Services, "ksceKernelClearDipsw", DipswRunAsDolce);
        }

        Assert.Equal(0, Call(Services, "ksceSblAimgrIsDolce"));
    }

    [Fact]
    public void TheCodesAreTheOnesTheBridgeReports()
    {
        Assert.Equal(ConsoleIdentity.ProductCode, Call(Services, "ksceSblAimgrGetProductCode"));
        Assert.Equal(ConsoleIdentity.ProductSubCode, Call(Services, "ksceSblAimgrGetProductSubCode"));

        _harness.Emu.IsSystemProgram = true;
        uint pscode = _harness.Alloc(8);
        Assert.Equal(0, Call(_harness.Module<VshBridge>(), "_vshSblAimgrGetPscode", pscode));
        Assert.Equal((ushort)Call(Services, "ksceSblAimgrGetProductCode"), GuestMemory.Read<ushort>(pscode + 2));
        Assert.Equal((ushort)Call(Services, "ksceSblAimgrGetProductSubCode"), GuestMemory.Read<ushort>(pscode + 4));
        Assert.Equal(Call(Services, "ksceSblAimgrIsCEX"), Call(_harness.Module<VshBridge>(), "vshSblAimgrIsCEX"));
        Assert.Equal(Call(Services, "ksceSblAimgrIsDEX"), Call(_harness.Module<VshBridge>(), "vshSblAimgrIsDEX"));
        Assert.Equal(Call(Services, "ksceSblAimgrIsDolce"), Call(_harness.Module<VshBridge>(), "vshSblAimgrIsDolce"));
    }

    [Fact]
    public void GetSmiWritesTheSecureModuleWordAndRefusesNull()
    {
        Assert.Equal(unchecked((int)0x800F0C16), Call(Services, "ksceSblAimgrGetSMI", 0u));
        uint smi = _harness.Word(0xFFFFFFFF);
        Assert.Equal(0, Call(Services, "ksceSblAimgrGetSMI", smi));
        Assert.Equal(ConsoleIdentity.SecureModuleInfo, GuestMemory.R32(smi));
    }
}
