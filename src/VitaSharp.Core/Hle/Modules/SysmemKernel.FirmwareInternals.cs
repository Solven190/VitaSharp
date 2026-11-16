using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// Functions of the FW 3.74 SceSysmem that the firmware does not export but kernel code reaches by their offset in the
/// module's code segment (module_get_offset): the lookup of a memory block by address in an address space and the
/// virtual page operations of a block (allocate/free a page object, map/unmap a page range, back a page with physical
/// memory). They work on the firmware's objects as kernel code reads and writes them, so the memory block, its page
/// list, its partition and the address space have the FW 3.74 layout (studied in the decompiled module):
/// <list type="bullet">
/// <item>address space (0x170): +0x08 lock, +0x10 flag (kernel 0x30000002, user 0x10000001), +0x14 pid, +0x1C the
/// partitions (0x20 pointers), +0x16C magic 0x4D95AEEC;</item>
/// <item>partition (0x80): +0x14 base, +0x18 size, +0x1C the MMU context (0x28: +0x0C the process TTBR record, +0x1C
/// the page-object heap), +0x34 magic 0xD946F262, +0x64 pid;</item>
/// <item>memory block (0x40): +0x0C paging flags (0x40000 in 0xF0000: a page list), +0x10 the block's code (its
/// access bits in 0xF0), +0x18 base, +0x1C size, +0x20 its spinlock, +0x24 partition, +0x28 physical partition, +0x2C
/// name, +0x30 the page list, +0x3C its UID;</item>
/// <item>page (0x20): +0x00 next, +0x08 flags (0x3F: 1 reserved, 4 backed, 0x10 mapped from another block), +0x0C
/// base, +0x10 size, +0x14 physical page, +0x18 physical address.</item>
/// </list>
/// VitaSharp has no MMU permissions or physical pages: a block's memory is always there, so mapping a range succeeds
/// and drops the translated code of the range (code written while the range was writable runs as written), unmapping
/// succeeds and keeps the memory, a page is backed by the block's own memory and its "physical address" is its virtual
/// address.
/// </summary>
public sealed partial class SysmemKernel : IFirmwareInternalFunctions
{
    /// <summary>The FW 3.74 SceSysmem's module NID (module_info +0x34): the build the offsets below are of.</summary>
    const uint Sysmem374ModuleNid = 0xDCF937EF;

    const uint AddressSpaceSize = 0x170, PartitionSize = 0x80, MmuContextSize = 0x28, MemBlockObjectSize = 0x40, PageObjectSize = 0x20;
    const uint AddressSpaceMagic = 0x4D95AEEC, PartitionMagic = 0xD946F262;
    const uint KernelAddressSpaceFlag = 0x30000002, UserAddressSpaceFlag = 0x10000001;

    /// <summary>Paging flags of a block whose memory is a list of virtual pages.</summary>
    const uint PageListPaging = 0x40000;

    /// <summary>A page backed by memory.</summary>
    const uint PageBacked = 0x4;

    /// <summary>The block lookup's "not found" (FW 3.74: SCE_KERNEL_ERROR_ERROR, the output cleared).</summary>
    const int BlockNotFound = unchecked((int)0x80020001);

    /// <summary>Each address space's partition record (address space -> partition).</summary>
    [SnapshotMissing] // states of builds before it: made again on first request
    readonly Dictionary<uint, uint> _partitions = new();

    public void RegisterInternalFunctions(FirmwareKernelModules firmware)
    {
        firmware.RegisterInternal(Sysmem374ModuleNid, 0x10CE1, (t, a) => (uint)Of(t).AddressSpaceFindMemBlockByAddr(a[0], a[1], a[2], a[3]));
        firmware.RegisterInternal(Sysmem374ModuleNid, 0xA115, (t, a) => (uint)Of(t).AllocVirPageObject(a[0], a[1]));
        firmware.RegisterInternal(Sysmem374ModuleNid, 0x15DAD, (t, a) => (uint)Of(t).FreeVirPageObject(a[0], a[1], a[2]));
        firmware.RegisterInternal(Sysmem374ModuleNid, 0x149E5, (t, a) => (uint)Of(t).MapVirPageCore(a[3], a[4], a[5]));
        firmware.RegisterInternal(Sysmem374ModuleNid, 0x148D9, (t, a) => (uint)Of(t).UnmapVirPageWithOpt(a[1], a[2]));
        firmware.RegisterInternal(Sysmem374ModuleNid, 0x15B1D, (t, a) => (uint)Of(t).VirPageAllocPhyPage(a[0], a[3]));
        firmware.RegisterInternal(Sysmem374ModuleNid, 0xE52D, (_, _) => 0);
    }

    /// <summary>The SceSysmem of the calling thread's process: the firmware modules live in the kernel process, but the
    /// kernel code that reaches their internal functions runs on the threads of every process (a user thread in a
    /// kernel module's syscall export), and the address spaces, partitions and block objects it passes are the ones
    /// that process's SceSysmem gave it.</summary>
    SysmemKernel Of(GuestThread thread)
    {
        foreach (HleModule module in thread.Process.Hle.Modules)
        {
            if (module is SysmemKernel sysmem)
            {
                return sysmem;
            }
        }

        return this;
    }

    /// <summary>The address space record of process <paramref name="key"/> (the kernel's for the kernel process), in
    /// the FW 3.74 layout with its partition, stable for the process.</summary>
    uint AddressSpaceOf(int key)
    {
        lock (_addressSpaces)
        {
            if (_addressSpaces.TryGetValue(key, out uint space))
            {
                return space;
            }

            space = KernelAlloc(AddressSpaceSize, $"kernel:addressspace:{key:X}", 0x10);
            GuestMemory.W32(space, space);
            GuestMemory.W32(space + 0x04, RootClass());
            GuestMemory.W32(space + 0x10, key == KernelSpace.KernelPid ? KernelAddressSpaceFlag : UserAddressSpaceFlag);
            GuestMemory.W32(space + 0x14, (uint)key);
            GuestMemory.W32(space + 0x1C, PartitionOf(space, key));
            GuestMemory.W32(space + 0x16C, AddressSpaceMagic);
            _addressSpaces[key] = space;
            return space;
        }
    }

    /// <summary>The partition record of address space <paramref name="space"/> (process <paramref name="pid"/>): the
    /// process's user range, its MMU context and the page-object heap.</summary>
    uint PartitionOf(uint space, int pid)
    {
        lock (_partitions)
        {
            if (_partitions.TryGetValue(space, out uint partition))
            {
                return partition;
            }

            partition = KernelAlloc(PartitionSize, "kernel:partition", 0x10);
            uint mmuContext = KernelAlloc(MmuContextSize, "kernel:mmucontext", 0x10);
            uint ttbr = KernelAlloc(0x14, "kernel:ttbr", 0x10);
            uint pageHeap = KernelAlloc(0x40, "kernel:virpageheap", 0x10);
            GuestMemory.W32(mmuContext + 0x0C, ttbr);
            GuestMemory.W32(mmuContext + 0x1C, pageHeap);
            GuestMemory.W32(partition, partition);
            GuestMemory.W32(partition + 0x04, RootClass());
            bool kernel = pid == KernelSpace.KernelPid;
            GuestMemory.W32(partition + 0x14, kernel ? GuestMemory.KernelStart : GuestMemory.UserStart);
            GuestMemory.W32(partition + 0x18, kernel ? GuestMemory.KernelEnd - GuestMemory.KernelStart : GuestMemory.UserEnd - GuestMemory.UserStart);
            GuestMemory.W32(partition + 0x1C, mmuContext);
            GuestMemory.W32(partition + 0x34, PartitionMagic);
            GuestMemory.W32(partition + 0x64, (uint)pid);
            _partitions[space] = partition;
            return partition;
        }
    }

    /// <summary>The process whose address space record is <paramref name="space"/>, or null.</summary>
    int? ProcessOfAddressSpace(uint space)
    {
        lock (_addressSpaces)
        {
            foreach (KeyValuePair<int, uint> entry in _addressSpaces)
            {
                if (entry.Value == space)
                {
                    return entry.Key;
                }
            }
        }

        return null;
    }

    /// <summary>The block's object body in the FW 3.74 layout (what ksceGUIDReferObject gives for it): one page that
    /// holds the whole block, backed by its memory.</summary>
    void FillMemBlockObject(uint body, MemBlock block, int owner)
    {
        uint space = AddressSpaceOf(owner == KernelSpace.KernelPid ? KernelSpace.KernelPid : owner);
        GuestMemory.W32(body + 0x0C, block.IsUnmapped ? 0 : PageListPaging);
        GuestMemory.W32(body + 0x10, block.Type);
        GuestMemory.W32(body + 0x18, block.Address);
        GuestMemory.W32(body + 0x1C, block.Size);
        GuestMemory.W32(body + 0x24, PartitionOf(space, owner));
        GuestMemory.W32(body + 0x3C, (uint)block.Uid);
        if (block.IsUnmapped || block.Size == 0)
        {
            return;
        }

        uint name = KernelAlloc((uint)block.Name.Length + 1, "kernel:memblockname", 0x10);
        GuestMemory.WriteCString(name, block.Name, block.Name.Length + 1);
        GuestMemory.W32(body + 0x2C, name);
        uint page = KernelAlloc(PageObjectSize, "kernel:virpage", 0x10);
        GuestMemory.W32(page + 0x08, PageBacked);
        GuestMemory.W32(page + 0x0C, block.Address);
        GuestMemory.W32(page + 0x10, block.Size);
        GuestMemory.W32(page + 0x18, block.Address);
        GuestMemory.W32(body + 0x30, page);
    }

    /// <summary>
    /// The address space's block lookup (FW 3.74 SceSysmem+0x10CE1): <paramref name="addr"/> must fall in one of the
    /// address space's partitions (else 0x80024A03); then the block that holds it (size 0) or that starts there with
    /// exactly <paramref name="size"/> bytes is written to <paramref name="blockOut"/> as its object, or 0 and
    /// SCE_KERNEL_ERROR_ERROR when there is none.
    /// </summary>
    int AddressSpaceFindMemBlockByAddr(uint space, uint addr, uint size, uint blockOut)
    {
        if (ProcessOfAddressSpace(space) is not { } pid)
        {
            return NoPartitionForRange;
        }

        bool inKernel = addr >= GuestMemory.KernelStart && addr < GuestMemory.KernelEnd;
        bool inUser = (addr >= GuestMemory.UserStart && addr < GuestMemory.UserEnd) || (addr >= GuestMemory.CdramStart && addr < GuestMemory.CdramEnd);
        if (!inKernel && (pid == KernelSpace.KernelPid || !inUser))
        {
            return NoPartitionForRange;
        }

        MemBlock? block = BlockHolding(pid, addr, size == 0 ? 1 : size);
        if (block is null || block.IsUnmapped || (size != 0 && (block.Address != addr || block.Size != size)))
        {
            GuestMemory.W32(blockOut, 0);
            return BlockNotFound;
        }

        GuestMemory.W32(blockOut, BodyOf(block));
        return 0;
    }

    /// <summary>A zeroed page object from the partition's heap (FW 3.74 SceSysmem+0xA115), written to
    /// <paramref name="pageOut"/>.</summary>
    int AllocVirPageObject(uint partition, uint pageOut)
    {
        if (pageOut == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        GuestMemory.W32(pageOut, KernelAlloc(PageObjectSize, "kernel:virpage", 0x10));
        return 0;
    }

    /// <summary>Gives a page object back to the partition's heap (FW 3.74 SceSysmem+0x15DAD).</summary>
    int FreeVirPageObject(uint partition, uint heap, uint page)
    {
        if (page != 0)
        {
            using (ProcessScope.Enter(KernelSpace.KernelPid))
            {
                Mem.Free(page);
            }
        }

        return 0;
    }

    /// <summary>Maps [<paramref name="vbase"/>, +<paramref name="size"/>) of a block with the block code's access
    /// (FW 3.74 SceSysmem+0x149E5): the memory stays as it is; the range's translated code is dropped, as a mapping
    /// change makes the CPU fetch the range again.</summary>
    int MapVirPageCore(uint memBlock, uint vbase, uint size)
    {
        InvalidateCode(vbase, size);
        return 0;
    }

    /// <summary>Unmaps a page of a block (FW 3.74 SceSysmem+0x148D9): the memory stays reachable (no MMU permissions);
    /// the page's translated code is dropped.</summary>
    int UnmapVirPageWithOpt(uint memBlock, uint page)
    {
        if (page != 0)
        {
            InvalidateCode(GuestMemory.R32(page + 0x0C), GuestMemory.R32(page + 0x10));
        }

        return 0;
    }

    /// <summary>Backs a page with physical memory (FW 3.74 SceSysmem+0x15B1D): the block's memory is already there,
    /// so the page is its own backing; the last page of the run is written to <paramref name="lastPageOut"/>.</summary>
    int VirPageAllocPhyPage(uint page, uint lastPageOut)
    {
        if (lastPageOut != 0)
        {
            GuestMemory.W32(lastPageOut, page);
        }

        return 0;
    }
}
