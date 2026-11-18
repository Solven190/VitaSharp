using System.Runtime.InteropServices;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// What a new guest block costs on the host: it reads as zeros (the kernel hands out cleared memory, also where a freed
/// block was) but takes host RAM only for the pages the guest touches. Games take their whole heap at boot (Killzone
/// Mercenary: about 300 MB of user and CDRAM memory in its first seconds) and clearing each block by hand made all of it
/// resident at once.
/// </summary>
[Collection("guest")]
public sealed unsafe partial class GuestMemoryCommitTests(GuestMemoryFixture fixture)
{
    // user and CDRAM ranges high enough that nothing else in the test process uses them
    private const uint UserBlock = 0xD800_0000, CdramBlock = 0x6F00_0000;
    private const uint BlockSize = 32u << 20;

    [Fact]
    public void ANewUserBlockTakesNoHostMemoryUntilTouched()
    {
        Assert.Equal(UserBlock, fixture.Memory.TryAllocAt(UserBlock, BlockSize, "untouched heap"));
        try
        {
            byte* host = fixture.Memory.Primary.Base + UserBlock;
            long resident = ResidentBytes(host, BlockSize);
            if (resident < 0)
            {
                return; // no residency query on this host
            }

            // the old clear by hand made all 32 MB resident; a few pages may be in for other reasons
            Assert.True(resident < BlockSize / 8, $"{resident >> 10} KB of a new {BlockSize >> 20} MB block are resident before the guest touched it");
            Assert.Equal(0u, GuestMemory.Read<uint>(UserBlock));
            Assert.Equal(0u, GuestMemory.Read<uint>(UserBlock + BlockSize - 4));
        }
        finally
        {
            fixture.Memory.Free(UserBlock);
        }
    }

    [Fact]
    public void AUserRangeAllocatedAgainReadsZero()
    {
        AllocWriteFreeAndAllocAgain(UserBlock, 3u << 20);
    }

    /// <summary>CDRAM is shared memory mapped in every address space: a freed block there is only discarded, so the
    /// next block in its place is cleared by hand.</summary>
    [Fact]
    public void ACdramRangeAllocatedAgainReadsZero()
    {
        AllocWriteFreeAndAllocAgain(CdramBlock, 3u << 20);
    }

    /// <summary>A block whose start and end are not on a 16 KB boundary (guest pages are 4 KB, host pages can be 16 KB)
    /// next to a live block: the new one reads zero and the neighbour keeps its data.</summary>
    [Fact]
    public void ANewBlockBesideALiveOneLeavesTheNeighbourIntact()
    {
        const uint Neighbour = UserBlock, NeighbourSize = 0x5000;
        Assert.Equal(Neighbour, fixture.Memory.TryAllocAt(Neighbour, NeighbourSize, "live neighbour"));
        try
        {
            GuestMemory.Write(Neighbour + NeighbourSize - 4, 0xCAFE_F00Du);
            AllocWriteFreeAndAllocAgain(Neighbour + NeighbourSize, 0x3000);
            Assert.Equal(0xCAFE_F00Du, GuestMemory.Read<uint>(Neighbour + NeighbourSize - 4));
        }
        finally
        {
            fixture.Memory.Free(Neighbour);
        }
    }

    /// <summary>CDRAM and kernel memory are shared memory mapped in every address space (a section view on Windows, a
    /// memfd/shm mapping elsewhere): a new block there takes no host RAM until the guest or the GPU writes it. The
    /// ranges are used by no other test, so this is their first block.</summary>
    [Theory]
    [InlineData(0x6C00_0000u)]
    [InlineData(0x4C00_0000u)]
    public void ANewSharedBlockTakesNoHostMemoryUntilTouched(uint addr)
    {
        Assert.Equal(addr, fixture.Memory.TryAllocAt(addr, BlockSize, "untouched video memory"));
        try
        {
            byte* host = fixture.Memory.Primary.Base + addr;
            long resident = ResidentBytes(host, BlockSize);
            if (resident < 0)
            {
                return; // no residency query on this host
            }

            Assert.True(resident < BlockSize / 8, $"{resident >> 10} KB of a new {BlockSize >> 20} MB shared block are resident before the guest touched it");
            Assert.Equal(0u, GuestMemory.Read<uint>(addr));
            Assert.Equal(0u, GuestMemory.Read<uint>(addr + BlockSize - 4));
        }
        finally
        {
            fixture.Memory.Free(addr);
        }
    }

    [Fact]
    public void AKernelRangeAllocatedAgainReadsZero()
    {
        AllocWriteFreeAndAllocAgain(0x4F00_0000, 3u << 20);
    }

    /// <summary>A CDRAM block on 4 KB but not 16/64 KB boundaries next to a live one: the new block reads zero, the
    /// neighbour (a render target the GPU wrote, say) keeps its data on both sides.</summary>
    [Fact]
    public void ANewCdramBlockBetweenLiveOnesLeavesTheNeighboursIntact()
    {
        const uint Before = CdramBlock + 0x0040_0000, BeforeSize = 0x5000;
        const uint Middle = Before + BeforeSize, MiddleSize = 0x3000;
        const uint After = Middle + MiddleSize, AfterSize = 0x2000;
        Assert.Equal(Before, fixture.Memory.TryAllocAt(Before, BeforeSize, "live before"));
        Assert.Equal(After, fixture.Memory.TryAllocAt(After, AfterSize, "live after"));
        try
        {
            GuestMemory.Write(Before + BeforeSize - 4, 0xCAFE_F00Du);
            GuestMemory.Write(After, 0x0BAD_CAFEu);
            AllocWriteFreeAndAllocAgain(Middle, MiddleSize);
            Assert.Equal(0xCAFE_F00Du, GuestMemory.Read<uint>(Before + BeforeSize - 4));
            Assert.Equal(0x0BAD_CAFEu, GuestMemory.Read<uint>(After));
        }
        finally
        {
            fixture.Memory.Free(Before);
            fixture.Memory.Free(After);
        }
    }

    /// <summary>The same CDRAM is seen by every process: a block written and freed by one and allocated again reads zero
    /// through another process's view too.</summary>
    [Fact]
    public void ACdramRangeReusedReadsZeroInEveryAddressSpace()
    {
        if (!fixture.Memory.SeparateSpaces)
        {
            return; // one address space on this host
        }

        const uint Addr = CdramBlock + 0x0080_0000, Size = 0x0010_0000;
        AddressSpace other = fixture.Memory.CreateSpace("second process");
        try
        {
            Assert.Equal(Addr, fixture.Memory.TryAllocAt(Addr, Size, "first"));
            for (uint offset = 0; offset < Size; offset += 0x1000)
            {
                GuestMemory.Write(Addr + offset, 0xDEAD_BEEFu);
            }

            Assert.Equal(0xDEAD_BEEFu, *(uint*)(other.Base + Addr)); // one memory behind both views
            fixture.Memory.Free(Addr);
            Assert.Equal(Addr, fixture.Memory.TryAllocAt(Addr, Size, "second"));
            for (uint offset = 0; offset < Size; offset += 0x1000)
            {
                Assert.Equal(0u, *(uint*)(other.Base + Addr + offset));
                Assert.Equal(0u, GuestMemory.Read<uint>(Addr + offset));
            }
            fixture.Memory.Free(Addr);
        }
        finally
        {
            fixture.Memory.ReleaseSpace(other);
        }
    }

    /// <summary>A stray write to CDRAM no block holds (it still lands, data accesses to free pages are allowed) must
    /// not show up in the block allocated there later.</summary>
    [Fact]
    public void AStrayWriteToFreeCdramDoesNotLeakIntoTheNextBlock()
    {
        const uint Addr = CdramBlock + 0x00C0_0000, Size = 0x4000;
        GuestMemory.Write(Addr + 0x1000, 0xDEAD_BEEFu);
        Assert.Equal(Addr, fixture.Memory.TryAllocAt(Addr, Size, "after a stray write"));
        try
        {
            Assert.Equal(0u, GuestMemory.Read<uint>(Addr + 0x1000));
        }
        finally
        {
            fixture.Memory.Free(Addr);
        }
    }

    private void AllocWriteFreeAndAllocAgain(uint addr, uint size)
    {
        Assert.Equal(addr, fixture.Memory.TryAllocAt(addr, size, "first"));
        for (uint offset = 0; offset < size; offset += 0x1000)
        {
            GuestMemory.Write(addr + offset, 0xDEAD_BEEFu);
        }

        fixture.Memory.Free(addr);
        Assert.Equal(addr, fixture.Memory.TryAllocAt(addr, size, "second"));
        try
        {
            for (uint offset = 0; offset < size; offset += 0x1000)
            {
                Assert.Equal(0u, GuestMemory.Read<uint>(addr + offset));
            }
        }
        finally
        {
            fixture.Memory.Free(addr);
        }
    }

    /// <summary>Bytes of [<paramref name="start"/>, + <paramref name="size"/>) in the process's physical memory;
    /// -1 when the host has no query for it.</summary>
    private static long ResidentBytes(byte* start, uint size)
    {
        int pageSize = Environment.SystemPageSize;
        int pages = (int)(size / (uint)pageSize);
        long resident = 0;
        if (OperatingSystem.IsWindows())
        {
            var info = new WorkingSetExInformation[pages];
            for (int i = 0; i < pages; i++)
            {
                info[i].VirtualAddress = (nint)(start + (long)i * pageSize);
            }

            fixed (WorkingSetExInformation* p = info)
            {
                if (!QueryWorkingSetEx(GetCurrentProcess(), p, pages * sizeof(WorkingSetExInformation)))
                {
                    return -1;
                }
            }
            foreach (WorkingSetExInformation page in info)
            {
                if ((page.VirtualAttributes & 1) != 0)
                {
                    resident += pageSize;
                }
            }
            return resident;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            byte[] vector = new byte[pages];
            fixed (byte* v = vector)
            {
                if (mincore(start, (nuint)size, v) != 0)
                {
                    return -1;
                }
            }
            foreach (byte b in vector)
            {
                if ((b & 1) != 0)
                {
                    resident += pageSize;
                }
            }
            return resident;
        }
        return -1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WorkingSetExInformation
    {
        public nint VirtualAddress;
        public nuint VirtualAttributes;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "K32QueryWorkingSetEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryWorkingSetEx(nint process, WorkingSetExInformation* info, int bytes);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("libc", SetLastError = true)]
    private static partial int mincore(void* addr, nuint length, byte* vector);
}
