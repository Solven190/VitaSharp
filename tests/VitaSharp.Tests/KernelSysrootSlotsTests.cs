using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// The FW 3.74 sysroot's handler slots (SceSysrootForDriver): a word set and cleared by one export and called by
/// another with the caller's arguments (stack ones included), a fixed result or untouched registers when nothing is
/// registered, tables checked by their size word and called by entry, words of a registered table read and written.
/// The handlers are small Thumb functions in guest memory.
/// </summary>
[Collection("guest")]
public sealed class KernelSysrootSlotsTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint InvalidArgument = 0x80020005, NoHandler = 0x80010086;

    private readonly HleHarness _harness = new(fixture, "ksysroot");

    public void Dispose() => _harness.Dispose();

    /// <summary>A Thumb function made of the given halfwords (its address with the Thumb bit).</summary>
    private uint Code(params ushort[] halfwords)
    {
        uint code = _harness.Alloc((uint)halfwords.Length * 2 + 4);
        for (int i = 0; i < halfwords.Length; i++)
        {
            GuestMemory.Write(code + (uint)i * 2, halfwords[i]);
        }

        return code | 1;
    }

    /// <summary>Runs the export <paramref name="nid"/> with r0-r3; returns r0 and r1 after it.</summary>
    private unsafe (uint R0, uint R1) Run(GuestThread thread, uint nid, params uint[] args)
    {
        Action<HleContext> implementation = _harness.Emu.Hle.ImplOf(nid) ?? throw new InvalidOperationException($"0x{nid:X8} not implemented");
        for (int i = 0; i < 4; i++)
        {
            thread.Core.State->R[i] = i < args.Length ? args[i] : 0xA5A5A5A5;
        }

        implementation(thread.Hle);
        return (thread.Core.State->R[0], thread.Core.State->R[1]);
    }

    private uint R0(GuestThread thread, uint nid, params uint[] args) => Run(thread, nid, args).R0;

    [Fact]
    public void AHandlerWordIsSetCalledWithTheArgumentsAndClearedAgain()
    {
        uint add = Code(0x1840, 0x4770); // adds r0, r0, r1; bx lr
        _harness.OnGuest(t =>
        {
            // ksceKernelSysrootGetShellPid (+0x32C): -1 until a handler is set
            Assert.Equal(0xFFFFFFFFu, R0(t, 0x05093E7B, 2, 3));
            R0(t, 0xE9B4221F, add);
            Assert.Equal(5u, R0(t, 0x05093E7B, 2, 3));

            // +0x3CC: set, called (0x80010086 without it), cleared
            Assert.Equal(NoHandler, R0(t, 0x6C036B53, 4, 5));
            R0(t, 0xECADF8C3, add);
            Assert.Equal(9u, R0(t, 0x6C036B53, 4, 5));
            R0(t, 0x42FC9FFF);
            Assert.Equal(NoHandler, R0(t, 0x6C036B53, 4, 5));

            // an export that returns nothing leaves the registers alone without a handler (+0x3AC)
            Assert.Equal(0x1234u, R0(t, 0xCC7A0E63, 0x1234));
            R0(t, 0x2D6B2A79, add);
            Assert.Equal(0x1235u, R0(t, 0xCC7A0E63, 0x1234, 1));
        });
    }

    [Fact]
    public void ATableIsRegisteredOnlyWithItsSizeAndCalledByEntry()
    {
        uint add = Code(0x1840, 0x4770);
        _harness.OnGuest(t =>
        {
            uint table = _harness.Alloc(0x28);
            GuestMemory.W32(table, 0x20);
            GuestMemory.W32(table + 0x1C, add);
            // ksceKernelSysrootSetProcessHandler (+0x3C4) wants a 0x28-byte table
            Assert.Equal(InvalidArgument, R0(t, 0x0F07C3FC, 0));
            Assert.Equal(InvalidArgument, R0(t, 0x0F07C3FC, table));
            Assert.Equal(NoHandler, R0(t, 0x421EFC96, 1, 2));
            GuestMemory.W32(table, 0x28);
            Assert.Equal(0u, R0(t, 0x0F07C3FC, table));
            Assert.Equal(3u, R0(t, 0x421EFC96, 1, 2)); // entry +0x1C
            // entry +0x20 is empty in the table: the call goes to whatever the table holds, so it is filled first
            GuestMemory.W32(table + 0x20, add);
            Assert.Equal(7u, R0(t, 0x1D61C837, 3, 4));
            R0(t, 0x5A7FFDC1); // cleared
            Assert.Equal(NoHandler, R0(t, 0x421EFC96, 1, 2));
        });
    }

    [Fact]
    public unsafe void TheHandlerSeesTheCallersStackArguments()
    {
        uint firstStackWord = Code(0x9800, 0x4770); // ldr r0, [sp]; bx lr
        _harness.OnGuest(t =>
        {
            uint table = _harness.Alloc(0x5C);
            GuestMemory.W32(table, 0x5C);
            GuestMemory.W32(table + 0x40, firstStackWord);
            // ksceKernelSysrootRegisterDbgpHandler (+0x3C8), entry +0x40 (NID_73522F65)
            Assert.Equal(0u, R0(t, 0x3999F917, table));
            uint sp = t.Core.State->R[13];
            GuestMemory.W32(sp, 0xCAFE0001);
            Assert.Equal(0xCAFE0001u, R0(t, 0x73522F65, 1, 2, 3, 4));
            R0(t, 0x7B7ED1E9); // ksceKernelSysrootUnregisterDbgpHandler
            Assert.Equal(NoHandler, R0(t, 0x73522F65, 1, 2, 3, 4));
        });
    }

    [Fact]
    public void TheWordsOfARegisteredTableAreReadAndWritten()
    {
        _harness.OnGuest(t =>
        {
            // without the +0x3FC table: reads give 0, writes go nowhere
            Assert.Equal(0u, R0(t, 0x483EF108));
            R0(t, 0xA6A0A038, 0x77);
            uint table = _harness.Alloc(0x1C);
            Assert.Equal(InvalidArgument, R0(t, 0xFF2DD7AB, table));
            GuestMemory.W32(table, 0x1C);
            Assert.Equal(0u, R0(t, 0xFF2DD7AB, table));
            Assert.Equal(0u, GuestMemory.R32(table + 4));
            R0(t, 0xA6A0A038, 0x77); // +4
            R0(t, 0x9CDE3623, 0x99); // +0x14
            Assert.Equal(0x77u, GuestMemory.R32(table + 4));
            Assert.Equal(0x77u, R0(t, 0x483EF108));
            Assert.Equal(0x99u, R0(t, 0x10788257));
            R0(t, 0x0ECD711E); // +4 cleared
            Assert.Equal(0u, R0(t, 0x483EF108));
            Assert.Equal(0x99u, R0(t, 0x10788257));
        });
    }

    [Fact]
    public void TheThreeGraphicsHandlersComeFromOneStructureAndOneReturns64Bits()
    {
        uint wide = Code(0x2007, 0x2109, 0x4770); // movs r0, #7; movs r1, #9; bx lr
        _harness.OnGuest(t =>
        {
            Assert.Equal((0u, 0u), Run(t, 0x273EAE53, 0, 0xFFFF));
            uint handlers = _harness.Alloc(0x10);
            GuestMemory.W32(handlers, 0x10);
            GuestMemory.W32(handlers + 8, wide);
            R0(t, 0xC94C76FA, handlers);
            Assert.Equal((7u, 9u), Run(t, 0x273EAE53, 0, 0));
            Assert.Equal(0u, R0(t, 0x6655F48C)); // +0x414 not given
            GuestMemory.W32(handlers, 0xC); // another size clears all three
            R0(t, 0xC94C76FA, handlers);
            Assert.Equal((0u, 0u), Run(t, 0x273EAE53, 0, 0xFFFF));
            Assert.Equal(0u, R0(t, 0xF804F761));
        });
    }

    [Fact]
    public void TheExportsSharedWithSceSysrootForKernelAreInBothLibraries()
    {
        System.Reflection.MethodInfo method = typeof(SysmemKernel).GetMethod("ksceKernelSysrootRegisterDbgpHandler",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var libraries = method.GetCustomAttributes(typeof(HleAttribute), false).Cast<HleAttribute>()
            .Where(a => a.Nid == 0x3999F917).Select(a => a.Library ?? "").OrderBy(l => l, StringComparer.Ordinal).ToArray();
        Assert.Equal(["SceSysrootForDriver", "SceSysrootForKernel"], libraries);
        Assert.True(_harness.Emu.Hle.IsImplemented(0x3999F917));
    }
}
