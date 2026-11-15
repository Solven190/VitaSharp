using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceSysmemForDriver's memory block queries for kernel code (FW 3.74): a block found by an address range in the
/// calling process (kernel blocks included) or in a given process, its base through a required pointer, and its
/// reference count that refuses to go below zero.
/// </summary>
[Collection("guest")]
public sealed class KernelMemBlockQueryTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int NoPartition = unchecked((int)0x80024A03), Underflow = unchecked((int)0x80024700);

    private readonly HleHarness _harness = new(fixture, "kmemblockq");

    public void Dispose() => _harness.Dispose();

    private SysmemKernel Sysmem => _harness.Module<SysmemKernel>();

    private int S(string function, params object?[] args) => Call(Sysmem, function, args);

    private (int Uid, uint Base) Block(string name, uint size)
    {
        int uid = S("ksceKernelAllocMemBlock", _harness.Str(name), 0x0C20D060u, size, 0u);
        Assert.True(uid > 0);
        uint baseOut = _harness.Word(0);
        Assert.Equal(0, S("ksceKernelGetMemBlockBase", uid, baseOut));
        return (uid, GuestMemory.R32(baseOut));
    }

    [Fact]
    public void ABlockIsFoundByAnyRangeInsideIt()
    {
        (int uid, uint start) = Block("query", 0x2000);
        Assert.Equal(uid, S("ksceKernelFindMemBlockByAddr", start, 0u));
        Assert.Equal(uid, S("ksceKernelFindMemBlockByAddr", start + 0x1000, 0x1000u));
        Assert.Equal(NoPartition, S("ksceKernelFindMemBlockByAddr", start + 0x1000, 0x1001u));
        Assert.Equal(uid, S("ksceKernelFindProcMemBlockByAddr", _harness.Emu.Pid, start + 4, 4u));
        Assert.Equal(SceError.INVALID_PID, S("ksceKernelFindProcMemBlockByAddr", 0x7FFF0001, start, 4u));
    }

    [Fact]
    public void BasesNeedAPointerAndAKnownBlock()
    {
        (int uid, uint start) = Block("bases", 0x1000);
        uint out_ = _harness.Word(0xFFFFFFFF);
        Assert.Equal(0, S("ksceKernelGetMemBlockVBase", uid, out_));
        Assert.Equal(start, GuestMemory.R32(out_));
        GuestMemory.W32(out_, 0xFFFFFFFF);
        Assert.Equal(0, S("ksceKernelGetMemBlockMappedBase", uid, out_));
        Assert.Equal(start, GuestMemory.R32(out_));
        Assert.Equal(SceError.INVALID_ARGUMENT, S("ksceKernelGetMemBlockVBase", uid, 0u));
        GuestMemory.W32(out_, 0xFFFFFFFF);
        Assert.Equal(SceError.INVALID_UID, S("ksceKernelGetMemBlockMappedBase", uid + 2000, out_));
        Assert.Equal(0u, GuestMemory.R32(out_));
    }

    [Fact]
    public void TheReferenceCountRefusesToGoBelowZero()
    {
        (int uid, _) = Block("refs", 0x1000);
        Assert.Equal(Underflow, S("ksceKernelDecRefCountMemBlock", uid));
        Assert.Equal(0, S("ksceKernelIncRefCountMemBlock", uid));
        Assert.Equal(0, S("ksceKernelIncRefCountMemBlock", uid));
        Assert.Equal(0, S("ksceKernelDecRefCountMemBlock", uid));
        Assert.Equal(0, S("ksceKernelDecRefCountMemBlock", uid));
        Assert.Equal(Underflow, S("ksceKernelDecRefCountMemBlock", uid));
        Assert.Equal(SceError.INVALID_UID, S("ksceKernelIncRefCountMemBlock", uid + 2000));
    }
    [Fact]
    public void TheRangeProbeChecksEveryPageForTheAskedAccess()
    {
        (_, uint start) = Block("probe", 0x2000);
        Assert.Equal(0, S("ksceKernelIsAccessibleRange", 0x30u, start, 0x2000u));
        Assert.Equal(0, S("ksceKernelIsAccessibleRange", 0x13u, start + 0x1FFF, 0u));
        Assert.Equal(SceError.INVALID_ARGUMENT, S("ksceKernelIsAccessibleRange", 0x4u, start, 4u));
        Assert.Equal(SceError.INVALID_ARGUMENT, S("ksceKernelIsAccessibleRange", 0x10u, 0xFFFFF000u, 0x2000u));
        uint kernel;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            kernel = _harness.Emu.Memory.Alloc(0x1000, "probe kernel", MemoryRegion.Kernel);
        }

        Assert.Equal(0, S("ksceKernelIsAccessibleRange", 0x3u, kernel, 0x1000u));
        Assert.Equal(unchecked((int)0x80022007), S("ksceKernelIsAccessibleRange", 0x10u, kernel, 4u));
        Assert.Equal(0, S("ksceKernelIsAccessibleRangeProc", _harness.Emu.Pid, 0x10u, start, 0x1000u));
        Assert.Equal(0, S("ksceKernelIsAccessibleRangeProc", 0, 0x10u, start, 0x1000u));
        Assert.Equal(SceError.INVALID_PID, S("ksceKernelIsAccessibleRangeProc", 0x7FFF0001, 0x10u, start, 4u));
    }
}
