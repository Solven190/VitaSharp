using VitaSharp.Core;
using VitaSharp.Core.Firmware;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// The index of the firmware's user modules (<see cref="FirmwareLibraries"/>) covers every vs0 module, the ones
/// programs load by path from the other folders too (vs0:vsh/common/mms, vs0:app/NPXS*/, vs0:data/external/webcore):
/// the loader gives their libraries to the importers and the coverage counts them as LLE. The system module folders
/// keep the first word for a library both export. Synthetic modules stand for the firmware's.
/// </summary>
[Collection("guest")]
public sealed class FirmwareLibrariesTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint SharedLibraryNid = 0x11111111;
    private const uint PromoterLibraryNid = 0x22222222;
    private const uint AppLibraryNid = 0x33333333;
    private const uint SharedFunction = 0x0A0A0A0A;
    private const uint PromoterFunction = 0x0B0B0B0B;
    private const uint AppFunction = 0x0C0C0C0C;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-fwlibs-" + Guid.NewGuid().ToString("N"));

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

    private void Module(string vs0Path, string name, params (string Library, uint LibNid, uint Function)[] exports)
    {
        string file = Path.Combine(_root, "vs0", vs0Path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var builder = new SceModuleBuilder(name);
        int function = builder.ReturnConstant(0);
        builder.Start(0);
        foreach ((string library, uint libNid, uint nid) in exports)
        {
            builder.Export(library, libNid, 0x0001, (nid, function));
        }

        File.WriteAllBytes(file, builder.Build());
    }

    private void InstallModules()
    {
        Module("sys/external/shared.suprx", "SceShared", ("SceTestShared", SharedLibraryNid, SharedFunction));
        Module("vsh/common/mms/test_promoter.suprx", "SceTestPromoter", ("SceTestPromoter", PromoterLibraryNid, PromoterFunction),
            ("SceTestShared", SharedLibraryNid, SharedFunction));
        Module("app/NPXS19999/app_module.suprx", "SceTestApp", ("SceTestAppLib", AppLibraryNid, AppFunction));
    }

    [Fact]
    public void ModulesInEveryVs0FolderAreIndexedAfterTheSystemFolders()
    {
        InstallModules();
        string[] paths = FirmwareLibraries.ModuleFiles(_root).Select(f => f.VitaPath).ToArray();
        Assert.Equal(["vs0:sys/external/shared.suprx", "vs0:app/NPXS19999/app_module.suprx", "vs0:vsh/common/mms/test_promoter.suprx"], paths);

        var index = FirmwareLibraries.For(_root);
        Assert.Equal("vs0:sys/external/shared.suprx", index.Provider("SceTestShared")?.VitaPath);
        Assert.Equal("vs0:vsh/common/mms/test_promoter.suprx", index.Provider("SceTestPromoter")?.VitaPath);
        Assert.Equal("vs0:app/NPXS19999/app_module.suprx", index.Provider("SceTestAppLib")?.VitaPath);
    }

    [Fact]
    public void TheCoverageCountsTheirFunctionsAsRealModules()
    {
        InstallModules();
        HashSet<uint> installed = LleLibraries.InstalledNids(_root, _ => true);
        Assert.Contains(PromoterFunction, installed);
        Assert.Contains(AppFunction, installed);
    }

    /// <summary>A program importing a library only a subfolder module exports gets that module from the loader.</summary>
    [Fact]
    public void TheLoaderLoadsASubfolderModuleForItsImporter()
    {
        InstallModules();
        using var emu = new Emulator(new EmulatorOptions { FsRoot = _root, Memory = fixture.Memory, EnableJit = false, CpuBackend = "interp" });
        emu.IsSystemProgram = true;
        uint segment = emu.Memory.Alloc(0x100, "fwlibs importer");
        GuestMemory.Fill(segment, 0, 0x100);
        var importer = new LoadedModule { Name = "importer", Path = "vs0:app/NPXS19998/eboot.bin", Segments = { [0] = new SegmentInfo(segment, 0, 0x100) } };
        importer.Imports.Add(("SceTestPromoter", PromoterLibraryNid, [PromoterFunction]));
        emu.Linker.BindFunctionImport(PromoterFunction, PromoterLibraryNid, "SceTestPromoter", segment, importer.Segments);

        List<LoadedModule> loaded = emu.LoadFirmwareDependencies(importer);
        Assert.Contains(loaded, m => m.Path.Equals("vs0:vsh/common/mms/test_promoter.suprx", StringComparison.OrdinalIgnoreCase));
        Assert.True(emu.Linker.TryGetFunctionExport(PromoterFunction, out _));
    }
}
