using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// ksceKernelMMUVAtoPA, ksceKernelMMUVAtoPAWithMode and ksceKernelMMUGetMemoryType as the FW 3.74 SceSysmem has them:
/// the first asked access kind (privileged read/write, user read/write) that translates gives the address and 1, no
/// translation clears the output and answers 0x80022007, and the memory type follows the block's cache attributes.
/// </summary>
[Collection("guest")]
public sealed class KernelMmuTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint KernelRw = 0x1020D006, UserRw = 0x0C20D060, UserRx = 0x0C20D050, UserNcRw = 0x0C208060;

    private readonly HleHarness _harness = new(fixture, "kmmu");

    public void Dispose() => _harness.Dispose();

    private SysmemKernel Sysmem => _harness.Module<SysmemKernel>();

    /// <summary>The base of a new block of <paramref name="type"/>.</summary>
    private uint Block(uint type)
    {
        uint basePtr = _harness.Alloc(4);
        int uid = Call(Sysmem, "ksceKernelAllocMemBlock", _harness.Str("mmu"), type, 0x1000u, 0u);
        Assert.True(uid > 0, $"0x{uid:X8}");
        Assert.Equal(0, Call(Sysmem, "ksceKernelGetMemBlockBase", uid, basePtr));
        return GuestMemory.R32(basePtr);
    }

    [Fact]
    public void AMappedAddressIsItsOwnPhysicalAddressInPages()
    {
        using ProcessScope.Scope scope = ProcessScope.Enter(_harness.Emu);
        uint user = Block(UserRw) + 0x123;
        uint physical = _harness.Alloc(4);
        Assert.Equal(1, Call(Sysmem, "ksceKernelMMUVAtoPA", user, physical));
        Assert.Equal(user, GuestMemory.R32(physical));

        uint kernel = Block(KernelRw) + 0x10;
        Assert.Equal(1, Call(Sysmem, "ksceKernelMMUVAtoPA", kernel, physical));
        Assert.Equal(kernel, GuestMemory.R32(physical));
    }

    [Fact]
    public void NoTranslationClearsTheOutputAndAnswers0x80022007()
    {
        using ProcessScope.Scope scope = ProcessScope.Enter(_harness.Emu);
        uint physical = _harness.Word(0xAAAAAAAA);
        Assert.Equal(SysmemKernel.MmuNoTranslation, Call(Sysmem, "ksceKernelMMUVAtoPA", 0x00001000u, physical));
        Assert.Equal(0u, GuestMemory.R32(physical));
        Assert.Equal(SysmemKernel.MmuNoTranslation, Call(Sysmem, "ksceKernelMMUGetMemoryType", 0x00001000u));
    }

    [Fact]
    public void TheModeNamesTheAccessesThatMayTranslate()
    {
        using ProcessScope.Scope scope = ProcessScope.Enter(_harness.Emu);
        uint code = Block(UserRx);
        uint kernel = Block(KernelRw);
        uint physical = _harness.Alloc(4);

        // a user read-execute block: user and privileged reads translate, writes do not
        Assert.Equal(1, Call(Sysmem, "ksceKernelMMUVAtoPAWithMode", 0x10u, code, physical));
        Assert.Equal(1, Call(Sysmem, "ksceKernelMMUVAtoPAWithMode", 0x01u, code, physical));
        Assert.Equal(SysmemKernel.MmuNoTranslation, Call(Sysmem, "ksceKernelMMUVAtoPAWithMode", 0x20u, code, physical));
        Assert.Equal(SysmemKernel.MmuNoTranslation, Call(Sysmem, "ksceKernelMMUVAtoPAWithMode", 0x02u, code, physical));
        Assert.Equal(1, Call(Sysmem, "ksceKernelMMUVAtoPAWithMode", 0x22u | 0x10u, code, physical)); // the read wins

        // a kernel block: no user access
        Assert.Equal(SysmemKernel.MmuNoTranslation, Call(Sysmem, "ksceKernelMMUVAtoPAWithMode", 0x30u, kernel, physical));
        Assert.Equal(1, Call(Sysmem, "ksceKernelMMUVAtoPAWithMode", 0x02u, kernel, physical));

        // a mode without any access kind never translates
        Assert.Equal(SysmemKernel.MmuNoTranslation, Call(Sysmem, "ksceKernelMMUVAtoPAWithMode", 0x0Cu, kernel, physical));
        Assert.Equal(0u, GuestMemory.R32(physical));
    }

    [Fact]
    public void TheMemoryTypeFollowsTheBlocksCacheAttributes()
    {
        using ProcessScope.Scope scope = ProcessScope.Enter(_harness.Emu);
        Assert.Equal(SysmemKernel.MmuTypeWriteBack, Call(Sysmem, "ksceKernelMMUGetMemoryType", Block(UserRw)));
        Assert.Equal(SysmemKernel.MmuTypeNonCacheable, Call(Sysmem, "ksceKernelMMUGetMemoryType", Block(UserNcRw)));
        Assert.Equal(SysmemKernel.MmuTypeWriteBack, Call(Sysmem, "ksceKernelMMUGetMemoryType", Block(KernelRw)));
    }

    [Fact]
    public void VAtoPAChecksItsOutputAndProcModeNamesTheProcess()
    {
        using ProcessScope.Scope scope = ProcessScope.Enter(_harness.Emu);
        uint user = Block(UserRw);
        uint physical = _harness.Alloc(4);
        Assert.Equal(SceError.INVALID_ARGUMENT, Call(Sysmem, "ksceKernelVAtoPA", user, 0u));
        Assert.Equal(1, Call(Sysmem, "ksceKernelVAtoPA", user, physical));
        Assert.Equal(user, GuestMemory.R32(physical));

        Assert.Equal(SceError.INVALID_ARGUMENT, Call(Sysmem, "ksceKernelProcModeVAtoPA", 0, 0x33u, user, 0u));
        Assert.Equal(SceError.INVALID_PID, Call(Sysmem, "ksceKernelProcModeVAtoPA", 0x7FFF0001, 0x33u, user, physical));
        Assert.Equal(1, Call(Sysmem, "ksceKernelProcModeVAtoPA", 0, 0x10u, user, physical));
        Assert.Equal(SysmemKernel.MmuNoTranslation, Call(Sysmem, "ksceKernelProcModeVAtoPA", 0, 0x10u, 0x1000u, physical));
    }

    [Fact]
    public void ARangeIsOnePhysicalRangeWhenEveryPageTranslates()
    {
        using ProcessScope.Scope scope = ProcessScope.Enter(_harness.Emu);
        uint user = Block(UserRw);
        uint virtualRange = _harness.Alloc(8);
        uint physicalRange = _harness.Alloc(8);
        GuestMemory.W32(virtualRange, user + 0x10);
        GuestMemory.W32(virtualRange + 4, 0x800);
        Assert.Equal(SceError.INVALID_ARGUMENT, Call(Sysmem, "ksceKernelVARangeToPARange", 0u, physicalRange));
        Assert.Equal(SceError.INVALID_ARGUMENT, Call(Sysmem, "ksceKernelVARangeToPARange", virtualRange, 0u));
        Assert.Equal(0, Call(Sysmem, "ksceKernelVARangeToPARange", virtualRange, physicalRange));
        Assert.Equal(user + 0x10, GuestMemory.R32(physicalRange));
        Assert.Equal(0x800u, GuestMemory.R32(physicalRange + 4));

        // a range running past the block's end into unmapped memory
        GuestMemory.W32(virtualRange, 0x00001000);
        Assert.Equal(SysmemKernel.MmuNoTranslation, Call(Sysmem, "ksceKernelVARangeToPARange", virtualRange, physicalRange));
    }

    [Fact]
    public void TheVectorCountsItsRangesAndWritesWhatFits()
    {
        using ProcessScope.Scope scope = ProcessScope.Enter(_harness.Emu);
        uint user = Block(UserRw);
        uint virtualRange = _harness.Alloc(8);
        GuestMemory.W32(virtualRange, user);
        GuestMemory.W32(virtualRange + 4, 0x1000);
        uint vector = _harness.Alloc(0x14);
        uint pairs = _harness.Alloc(16);

        GuestMemory.W32(vector, 0x10);
        Assert.Equal(unchecked((int)0x80020009), Call(Sysmem, "ksceKernelVARangeToPAVector", virtualRange, vector));
        GuestMemory.W32(vector, 0x14);
        GuestMemory.W32(vector + 4, 2);
        GuestMemory.W32(vector + 0x10, 0);
        Assert.Equal(SceError.INVALID_ARGUMENT, Call(Sysmem, "ksceKernelVARangeToPAVector", virtualRange, vector));

        GuestMemory.W32(vector + 0x10, pairs);
        Assert.Equal(0, Call(Sysmem, "ksceKernelVARangeToPAVector", virtualRange, vector));
        Assert.Equal(1u, GuestMemory.R32(vector + 8));
        Assert.Equal(1u, GuestMemory.R32(vector + 0xC));
        Assert.Equal(user, GuestMemory.R32(pairs));
        Assert.Equal(0x1000u, GuestMemory.R32(pairs + 4));

        // no capacity: only the count of ranges
        GuestMemory.W32(vector + 4, 0);
        GuestMemory.W32(vector + 0x10, 0);
        Assert.Equal(0, Call(Sysmem, "ksceKernelVARangeToPAVector", virtualRange, vector));
        Assert.Equal(0u, GuestMemory.R32(vector + 8));
        Assert.Equal(1u, GuestMemory.R32(vector + 0xC));
    }
}
