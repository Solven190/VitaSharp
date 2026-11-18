using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceRegMgrForDebugger and SceRegMgrForTool (SceRegistryMgr 3.74): the development-environment entries of the
/// registry; on a retail unit (environment mode 1) every one of them refuses with 0x800D001E before reading its
/// arguments.
/// </summary>
[Collection("guest")]
public sealed class RegMgrToolKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int NotDevelopmentEnvironment = unchecked((int)0x800D001E);

    private readonly HleHarness _harness = new(fixture, "regtool");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData("SceRegMgrForDebugger", 0x46AC0CCBu)]
    [InlineData("SceRegMgrForDebugger", 0x53970232u)]
    [InlineData("SceRegMgrForDebugger", 0x810D2F09u)]
    [InlineData("SceRegMgrForDebugger", 0x8AE5757Eu)]
    [InlineData("SceRegMgrForDebugger", 0x8E798E47u)]
    [InlineData("SceRegMgrForDebugger", 0xB9A0BB77u)]
    [InlineData("SceRegMgrForDebugger", 0xC588D3E7u)]
    [InlineData("SceRegMgrForDebugger", 0xC64C67A8u)]
    [InlineData("SceRegMgrForDebugger", 0xD668ABD2u)]
    [InlineData("SceRegMgrForTool", 0x0B623D0Bu)]
    [InlineData("SceRegMgrForTool", 0x0CFA929Bu)]
    [InlineData("SceRegMgrForTool", 0x2A76051Au)]
    [InlineData("SceRegMgrForTool", 0x2D415472u)]
    [InlineData("SceRegMgrForTool", 0x3AAB71EFu)]
    [InlineData("SceRegMgrForTool", 0x4EDF87F9u)]
    [InlineData("SceRegMgrForTool", 0x6276E7D8u)]
    [InlineData("SceRegMgrForTool", 0x6808B7ADu)]
    [InlineData("SceRegMgrForTool", 0x7B30AC2Cu)]
    [InlineData("SceRegMgrForTool", 0xB441660Bu)]
    [InlineData("SceRegMgrForTool", 0xC689E36Cu)]
    [InlineData("SceRegMgrForTool", 0xE6E81558u)]
    [InlineData("SceRegMgrForTool", 0xF55E6E4Fu)]
    public void EveryExportHasAnEntry(string library, uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == library).ToList();
        Assert.Single(bindings);
        Assert.False(bindings[0].Default);
    }

    [Theory]
    [InlineData("RegMgrDebuggerEntry")]
    [InlineData("RegMgrToolModeEntry")]
    [InlineData("RegMgrToolEntry")]
    public void ARetailUnitRefusesEveryEntry(string entry)
    {
        Assert.Equal(NotDevelopmentEnvironment, Call(Services, entry));
    }
}
