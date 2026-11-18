using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceSblSmCommForKernel (sm_comm.skprx 3.74) on a unit without the secure processor: an image is checked as the
/// module checks it (a missing file, more than 0x20000 bytes) and then cannot be started (0x80020004); no session
/// ever exists, so commands and stops answer the module's "no such session" (0x800F0026).
/// </summary>
[Collection("guest")]
public sealed class SblSmCommKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int TooLarge = unchecked((int)0x800F0016), NoSession = unchecked((int)0x800F0026);
    private const int Unavailable = unchecked((int)0x80020004), NoEntry = unchecked((int)0x80010002);

    private readonly HleHarness _harness = new(fixture, "smcomm");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0x7863A0CCu, true)]
    [InlineData(0x039C73B1u, true)]
    [InlineData(0xDB9FC204u, false)]
    [InlineData(0x0631F8EDu, false)]
    public void EveryExportHasAnEntry(uint nid, bool isDefault)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceSblSmCommForKernel").ToList();
        Assert.Single(bindings);
        Assert.Equal(isDefault, bindings[0].Default);
    }

    [Fact]
    public void AnImageIsCheckedAndCannotBeStarted()
    {
        uint id = _harness.Word(0);
        Assert.Equal(NoEntry, Call(Services, "ksceSblSmCommStartSmFromFile", 0, "os0:sm/missing.self", 0, 0u, id));
        string host = _harness.Emu.Vfs.Resolve("ux0:data/smcomm_image.bin", forWrite: true)!;
        Directory.CreateDirectory(Path.GetDirectoryName(host)!);
        File.WriteAllBytes(host, new byte[0x100]);
        Assert.Equal(Unavailable, Call(Services, "ksceSblSmCommStartSmFromFile", 0, "ux0:data/smcomm_image.bin", 0, 0u, id));
        File.WriteAllBytes(host, new byte[0x20001]);
        Assert.Equal(TooLarge, Call(Services, "ksceSblSmCommStartSmFromFile", 0, "ux0:data/smcomm_image.bin", 0, 0u, id));
        File.Delete(host);

        uint data = _harness.Alloc(0x100);
        Assert.Equal(Unavailable, Call(Services, "ksceSblSmCommStartSmFromData", 0, data, 0x100u, 0, 0u, id));
        Assert.Equal(TooLarge, Call(Services, "ksceSblSmCommStartSmFromData", 0, data, 0x20001u, 0, 0u, id));
    }

    [Fact]
    public void NoSessionEverExists()
    {
        Assert.Equal(NoSession, Call(Services, "ksceSblSmCommCallFunc", 1, 0x1000, 0u, 0u, 0u));
        Assert.Equal(NoSession, Call(Services, "ksceSblSmCommStopSm", 1, 0u));
    }
}
