using VitaSharp.Core.Cpu;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// Loading a module drops only the translated code that the load made stale, as the Vita's module manager cleans and
/// invalidates the caches over the ranges it wrote: the new module's segments, the import stubs of earlier modules that
/// the linker pointed at its exports, and the words of earlier variable imports it relocated again. The rest of the
/// process keeps its translations (before, every load dropped the code of the whole 4 GB address space).
/// </summary>
[Collection("guest")]
public sealed class ModuleLoadCodeInvalidationTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint TestLib = 0x7E57A11B, TestFn = 0x7E570001;

    private readonly HleHarness _harness = new(fixture, "loadinval");

    public void Dispose() => _harness.Dispose();

    private string Write(string file, byte[] elf)
    {
        string dir = Path.Combine(_harness.Root, "ur0", "tai");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, file), elf);
        return "ur0:tai/" + file;
    }

    /// <summary>A translated A32 block (add r0, r0, #1; bx lr) of the process at an address no module uses.</summary>
    private Block UnrelatedBlock()
    {
        uint code = _harness.Alloc(0x100);
        GuestMemory.W32(code, 0xE2800001);
        GuestMemory.W32(code + 4, 0xE12FFF1E);
        return _harness.Emu.Code.GetFromAnyThread(code, false);
    }

    [Fact]
    public void LoadingAModuleKeepsTheTranslatedCodeOutsideWhatTheLoadWrote()
    {
        Block unrelated = UnrelatedBlock();
        var provider = new SceModuleBuilder("TestProvider");
        int returnFive = provider.ReturnConstant(5);
        _harness.Emu.LoadModule(Write("provider.suprx", provider.Export("TestLib", TestLib, 0x0001, (TestFn, returnFive)).Build()));

        Assert.False(unrelated.Invalidated);
        Assert.Same(unrelated, _harness.Emu.Code.GetFromAnyThread(unrelated.Start, false));
    }

    [Fact]
    public void TheStubsTheLoadRebindsDropTheirCodeAndTheImportersOtherCodeStays()
    {
        SceModuleBuilder client = new SceModuleBuilder("TestClient").Import("TestLib", TestLib, TestFn);
        int callImport = client.CallImport(0, 0);
        int returnThree = client.ReturnConstant(3);
        LoadedModule clientModule = _harness.Emu.LoadModule(Write("client.suprx", client.Build()));
        uint clientBase = clientModule.Segments[0].Address;
        uint stub = Assert.Single(_harness.Emu.Linker.BoundStubs(TestFn, TestLib, kernel: false));
        Assert.Equal(0xEFu, GuestMemory.R32(stub) >> 24); // bound to the HLE until a module exports it
        Block stubBlock = _harness.Emu.Code.GetFromAnyThread(stub, false);
        uint ownCode = SceModuleBuilder.FunctionAddress(clientBase, returnThree) & ~1u;
        Block ownBlock = _harness.Emu.Code.GetFromAnyThread(ownCode, true);
        Block unrelated = UnrelatedBlock();

        var provider = new SceModuleBuilder("TestProvider");
        int returnFive = provider.ReturnConstant(5);
        _harness.Emu.LoadModule(Write("provider.suprx", provider.Export("TestLib", TestLib, 0x0001, (TestFn, returnFive)).Build()));

        // the stub now jumps to the export (movw/movt r12; bx r12): its old translation is gone
        Assert.Equal(0xE300C000u, GuestMemory.R32(stub) & 0xFFF0F000);
        Assert.True(stubBlock.Invalidated);
        Assert.Equal(0xE300C000u, _harness.Emu.Code.GetFromAnyThread(stub, false).Insts[0].Raw & 0xFFF0F000);
        Assert.False(ownBlock.Invalidated);
        Assert.False(unrelated.Invalidated);
        _harness.OnGuest(t => Assert.Equal(5u, t.CallGuest(SceModuleBuilder.FunctionAddress(clientBase, callImport))));
    }

    /// <summary>Resolves nothing: every import stays unbound until a module exports it.</summary>
    private sealed class NoHle : IImportResolver
    {
        public uint GetHleFunctionIndex(uint nid, uint libNid, string libName) => 0;

        public uint GetHleVariable(uint nid) => 0;
    }

    [Fact]
    public void AVariableExportedLaterDropsTheCodeOfTheWordsItRelocates()
    {
        const uint VariableNid = 0x7E57BA12, VariableAddress = 0x81234560;
        uint code = _harness.Alloc(0x100);
        GuestMemory.W32(code + 0x10, 0xE3000000); // movw r0, #0 (the variable's low half, relocated)
        GuestMemory.W32(code + 0x14, 0xE3400000); // movt r0, #0 (its high half)
        // the variable import entry: header (size 0x14, the header included), then two format-1 variable relocations of
        // segment 0: R_ARM_MOVW_ABS_NC at +0x10, R_ARM_MOVT_ABS at +0x14
        uint entry = _harness.Alloc(0x20);
        GuestMemory.W32(entry, 0x14u << 4);
        GuestMemory.W32(entry + 0x04, 1 | (43u << 8));
        GuestMemory.W32(entry + 0x08, 0x10);
        GuestMemory.W32(entry + 0x0C, 1 | (44u << 8));
        GuestMemory.W32(entry + 0x10, 0x14);
        var invalidated = new List<(uint Address, uint Size)>();
        var linker = new Linker(new NoHle()) { InvalidateCode = (address, size) => invalidated.Add((address, size)) };
        var segments = new Dictionary<int, SegmentInfo> { [0] = new SegmentInfo(code, 0, 0x100) };

        linker.BindVariableImport(VariableNid, entry, segments, "TestLib (imported by TestClient)");
        Assert.Equal(0xE3000000u, GuestMemory.R32(code + 0x10)); // nothing exports it yet
        Assert.Empty(invalidated);

        linker.RegisterExports(new LoadedModule { Name = "TestProvider" }, TestLib, [VariableNid], [VariableAddress], funcCount: 0);

        Assert.Equal(0xE3000000u | 0x4u << 16 | 0x560u, GuestMemory.R32(code + 0x10));
        Assert.Equal(0xE3400000u | 0x8u << 16 | 0x123u, GuestMemory.R32(code + 0x14));
        Assert.Equal([(code + 0x10, 8u)], invalidated);
    }
}
