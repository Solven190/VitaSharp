using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceDebugForKernel (sysmem 3.74): every export has its entry; the handler setters, the console ring and the two
/// printers answer as the module (the ring takes a character and answers 1).
/// </summary>
[Collection("guest")]
public sealed class DebugKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "dbgk");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0x0EDA5174u)]
    [InlineData(0x38F1A352u)]
    [InlineData(0x3A21194Eu)]
    [InlineData(0x4C62F8CCu)]
    [InlineData(0x5D6F0CFCu)]
    [InlineData(0x67E9D1ECu)]
    [InlineData(0x78027203u)]
    [InlineData(0x91E7D24Fu)]
    [InlineData(0xBCC8D0B2u)]
    [InlineData(0xCCABDD98u)]
    public void EveryExportHasAnEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceDebugForKernel").ToList();
        Assert.Single(bindings);
        Assert.False(bindings[0].Default);
    }

    [Fact]
    public void TheEntriesAnswerAsTheModule()
    {
        Assert.Equal(0, Call(Services, "DebugKernelZero"));
        Assert.Equal(1, Call(Services, "DebugPutRing", (int)'k'));
        Assert.Equal(1, Call(Services, "DebugPutRing", (int)'\n'));
        Call(Services, "DebugSetHandler91E7", 0u);
        uint frame = _harness.Alloc(0x40);
        Call(Services, "DebugPrintSyscallFrame", 0x10005, frame); // the kernel: nothing printed
        Call(Services, "DebugPrintSyscallFrame", 0x40010003, frame);
        Call(Services, "DebugPrintKernelException", frame, 0u, 0u, _harness.Str("Data Abort"));
    }
}
