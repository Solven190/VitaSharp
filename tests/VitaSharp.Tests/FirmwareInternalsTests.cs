using VitaSharp.Core;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// The loader's rule for a library the HLE serves only in part: system software that calls functions of it the HLE
/// does not have (the library's unnamed internals) gets the real firmware module for the whole library; a game, or
/// system software that only calls what the HLE has, keeps the HLE. The firmware module is a synthetic np_utility.suprx
/// exporting SceNpUtility (one public function the HLE implements, one internal one it does not).
/// </summary>
[Collection("guest")]
public sealed class FirmwareInternalsTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint LibraryNid = 0x7B47D9CF;
    private const uint PublicNid = 0xE0EBFBF6; // sceNpBandwidthTestGetStatus, in the HLE
    // a synthetic NID (no firmware module exports it, nids.txt does not list it), so the HLE never implements it: a real
    // internal NID stops being one when the HLE gains it (NID_548625FC did)
    private const uint InternalNid = 0x5EC4E7A1;
    private const string ModulePath = "vs0:sys/external/np_utility.suprx";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-internals-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private Emulator Create()
    {
        string dir = Path.Combine(_root, "vs0", "sys", "external");
        Directory.CreateDirectory(dir);
        var builder = new SceModuleBuilder("SceNpUtility");
        int function = builder.ReturnConstant(0);
        File.WriteAllBytes(Path.Combine(dir, "np_utility.suprx"),
            builder.Start(0).Export("SceNpUtility", LibraryNid, 0x0001, (PublicNid, function), (InternalNid, function)).Build());
        return new Emulator(new EmulatorOptions { FsRoot = _root, Memory = fixture.Memory, EnableJit = false, CpuBackend = "interp" });
    }

    private static LoadedModule Importer(Emulator emu, string path, params uint[] nids)
    {
        uint segment = emu.Memory.Alloc(0x100, "internals importer");
        GuestMemory.Fill(segment, 0, 0x100);
        var importer = new LoadedModule { Name = "importer", Path = path, Segments = { [0] = new SegmentInfo(segment, 0, 0x100) } };
        importer.Imports.Add(("SceNpUtility", LibraryNid, nids));
        for (int i = 0; i < nids.Length; i++)
        {
            emu.Linker.BindFunctionImport(nids[i], LibraryNid, "SceNpUtility", segment + (uint)i * 0x10, importer.Segments);
        }

        return importer;
    }

    private static bool LoadsRealModule(Emulator emu, LoadedModule importer) =>
        emu.LoadFirmwareDependencies(importer).Any(m => m.Path.Equals(ModulePath, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void TheInternalNidIsOneTheHleCannotHave()
    {
        using Emulator emu = Create();
        Assert.Null(VitaSharp.Core.Hle.NidDatabase.Lookup(InternalNid));
        Assert.False(emu.Hle.Implements(InternalNid));
        Assert.True(emu.Hle.Implements(PublicNid));
    }

    [Fact]
    public void GameKeepsTheHleEvenForInternals()
    {
        using Emulator emu = Create();
        Assert.True(emu.Hle.Implements(PublicNid));
        Assert.False(emu.Hle.Implements(InternalNid));
        emu.IsSystemProgram = false;
        Assert.False(LoadsRealModule(emu, Importer(emu, "app0:eboot.bin", PublicNid, InternalNid)));
    }

    [Fact]
    public void SystemProgramCallingOnlyWhatTheHleHasKeepsTheHle()
    {
        using Emulator emu = Create();
        emu.IsSystemProgram = true;
        Assert.False(LoadsRealModule(emu, Importer(emu, "app0:eboot.bin", PublicNid)));
    }

    [Fact]
    public void SystemProgramCallingInternalsGetsTheRealModuleForTheWholeLibrary()
    {
        using Emulator emu = Create();
        emu.IsSystemProgram = true;
        Assert.True(LoadsRealModule(emu, Importer(emu, "app0:eboot.bin", PublicNid, InternalNid)));
        // the public function comes from the same module as the internals
        Assert.True(emu.Linker.TryGetFunctionExport(PublicNid, out _));
        Assert.True(emu.Linker.TryGetFunctionExport(InternalNid, out _));
    }

    [Fact]
    public void FirmwareModuleCallingInternalsGetsTheRealModuleInAnyProcess()
    {
        using Emulator emu = Create();
        emu.IsSystemProgram = false;
        Assert.True(LoadsRealModule(emu, Importer(emu, "vs0:sys/external/np_party.suprx", PublicNid, InternalNid)));
    }
}
