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
