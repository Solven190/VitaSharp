using System.Reflection;
using VitaSharp.Core;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// Which of the module manager's preloads a game gets from the firmware (modulemgr 3.74 loads libSceFt2, libpvf,
/// libshellsvc... only from vs0:sys/external), and the libraries whose importers get the firmware module, as the
/// coverage counts them. The PSM runtime (PCSI00011) is where this showed: its libpsm calls libSceFt2's FreeType and
/// its eboot IPMI::Client::create of libshellsvc, which the HLE does not have.
/// </summary>
[Collection("guest")]
public sealed class GameFirmwarePreloadTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint IpmiClientCreate = 0xB282B430;
    private const uint IpmiEstimateClientMemorySize = 0x4E255C31;

    private readonly HleHarness _harness = new(fixture, "game-preload");

    public void Dispose() => _harness.Dispose();

    private static bool Takes(IReadOnlyList<string> libraries, (string Library, uint[] Nids)[] imports,
        Func<string, bool> knows, Func<string, bool> serves, Func<uint, bool> implements)
    {
        MethodInfo method = typeof(Emulator).GetMethod("GameTakesFirmwarePreload", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(Emulator), "GameTakesFirmwarePreload");
        return (bool)method.Invoke(null, [libraries, imports, knows, serves, implements])!;
    }

    [Fact]
    public void AModuleTheHleKnowsNothingOfComesFromTheFirmware()
    {
        // libSceFt2: no HLE of SceFt2, whoever imports it (libpsm, loaded later, not the eboot)
        Assert.True(Takes(["SceFt2"], [], _ => false, _ => false, _ => false));
    }

    [Fact]
    public void AModuleTheHleEmulatesStaysOnHle()
    {
        // libpvf: the HLE serves ScePvf; libcdlg / apputil likewise
        Assert.False(Takes(["ScePvf"], [("ScePvf", [1u, 2u])], l => l == "ScePvf", l => l == "ScePvf", _ => true));
        Assert.False(Takes([], [], _ => false, _ => false, _ => false)); // a module that exports nothing
    }

    [Fact]
    public void AFallbackLibraryWithAMissingImportedFunctionComesFromTheFirmware()
    {
        string[] shellsvc = ["SceShellSvc", "SceShellUtil", "SceIpmi"];
        bool Knows(string library) => true;
        bool Serves(string library) => library != "SceIpmi"; // SceIpmi only in a firmware fallback module
        bool Implements(uint nid) => nid != IpmiClientCreate;

        // the PSM runtime's eboot: IPMI::Client::create, which the fallback does not have
        Assert.True(Takes(shellsvc, [("SceIpmi", [IpmiClientCreate, IpmiEstimateClientMemorySize])], Knows, Serves, Implements));
        // a game that imports only what the fallback has keeps the HLE
        Assert.False(Takes(shellsvc, [("SceIpmi", [IpmiEstimateClientMemorySize])], Knows, Serves, Implements));
        // a missing function of a library the HLE serves is the HLE's to add, not a reason to swap the module
        Assert.False(Takes(shellsvc, [("SceShellSvc", [IpmiClientCreate])], Knows, Serves, Implements));
        // imports of another module's library do not count
        Assert.False(Takes(shellsvc, [("SceFt2", [IpmiClientCreate])], Knows, Serves, Implements));
    }

    [Fact]
    public void ImportersGetTheFirmwareModuleOfLibrariesTheHleDoesNotEmulate()
    {
        VitaSharp.Core.Hle.HleRegistry hle = _harness.Emu.Hle;
        MethodInfo method = hle.GetType().GetMethod("FirmwareServesImporters")
            ?? throw new MissingMethodException(hle.GetType().Name, "FirmwareServesImporters");
        bool Serves(string library) => (bool)method.Invoke(hle, [library])!;
        Assert.True(Serves("SceFt2"));        // nothing of it in the HLE
        Assert.True(Serves("SceDrmPsmKdc"));  // psmkdc: the PSM runtime loads it through sysmodule
        Assert.True(Serves("SceIpmi"));       // only a firmware fallback (libshellsvc's)
        Assert.False(Serves("SceGxm"));       // emulated
        Assert.False(Serves("ScePvf"));
    }
}
