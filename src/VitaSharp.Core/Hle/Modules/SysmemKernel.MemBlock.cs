using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The memory block queries and reference counts of SceSysmemForDriver for kernel code, as the FW 3.74 SceSysmem has
/// them: a block is found by an address range in an address space (the calling process's, which every process shares
/// with the kernel's own blocks, or a given process's), its base comes back through a pointer the caller must give,
/// and its reference count (a 16-bit count in the block) goes up and down, refusing to go below zero.
/// </summary>
public sealed partial class SysmemKernel
{
    /// <summary>The firmware's "no partition holds the range" error of the block lookup.</summary>
    const int NoPartitionForRange = unchecked((int)0x80024A03);

    /// <summary>The error of a reference count decrement below zero.</summary>
    const int ReferenceCountUnderflow = unchecked((int)0x80024700);

    /// <summary>Each block's reference count (ksceKernelIncRefCountMemBlock): UID -> count.</summary>
    [SnapshotMissing] // states of builds before it: no count was taken
    readonly Dictionary<int, int> _blockReferences = new();

    /// <summary>The block of <paramref name="pid"/>'s blocks or the kernel's that holds [addr, addr + size).</summary>
    MemBlock? BlockHolding(int pid, uint addr, uint size)
    {
        ulong end = (ulong)addr + Math.Max(size, 1u);
        foreach (int owner in new[] { pid, KernelSpace.KernelPid })
        {
            foreach (MemBlock block in K.Objects.Owned<MemBlock>(owner))
            {
                if (addr >= block.Address && end <= (ulong)block.Address + block.Size)
                {
                    return block;
                }
            }
        }

        return null;
    }

    /// <summary>ksceKernelFindMemBlockByAddr(addr, size): the UID of the block holding the range in the calling
    /// process's address space (the kernel's blocks included).</summary>
    [Hle(Nid = 0x8A1742F6, Library = "SceSysmemForDriver")]
    int ksceKernelFindMemBlockByAddr(uint addr, uint size) => BlockHolding(ProcessScope.CurrentPid, addr, size)?.Uid ?? NoPartitionForRange;

    /// <summary>ksceKernelFindProcMemBlockByAddr(pid, addr, size): the same in process <paramref name="pid"/>.</summary>
    [Hle(Nid = 0x857F1D5A, Library = "SceSysmemForDriver")]
    int ksceKernelFindProcMemBlockByAddr(int pid, uint addr, uint size)
    {
        if (pid != KernelSpace.KernelPid && ProcessOf(pid) == null)
        {
            return SceError.INVALID_PID;
        }

        return BlockHolding(pid, addr, size)?.Uid ?? NoPartitionForRange;
    }

    /// <summary>ksceKernelGetMemBlockVBase(uid, void **vbase): the block's virtual base.</summary>
    [Hle(Nid = 0xB81CF0A3, Library = "SceSysmemForDriver")]
    int ksceKernelGetMemBlockVBase(int uid, uint baseOut) => BlockBaseInto(uid, baseOut);

    /// <summary>ksceKernelGetMemBlockMappedBase(uid, void **base): where the block is mapped (its base; VitaSharp
    /// maps every block at its virtual address). The output is cleared first.</summary>
    [Hle(Nid = 0x0B1FD5C3, Library = "SceSysmemForDriver")]
    int ksceKernelGetMemBlockMappedBase(int uid, uint baseOut)
    {
        if (baseOut != 0)
        {
            GuestMemory.W32(baseOut, 0);
        }

        return BlockBaseInto(uid, baseOut);
    }

    int BlockBaseInto(int uid, uint baseOut)
    {
        if (baseOut == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (K.Objects.Get<MemBlock>(uid) is not { } block)
        {
            return SceError.INVALID_UID;
        }

        GuestMemory.W32(baseOut, block.Address);
        return 0;
    }

    /// <summary>ksceKernelIncRefCountMemBlock(uid): one more reference to the block.</summary>
    [Hle(Nid = 0xEAF3849B, Library = "SceSysmemForDriver")]
    int ksceKernelIncRefCountMemBlock(int uid)
    {
        if (K.Objects.Get<MemBlock>(uid) is null)
        {
            return SceError.INVALID_UID;
        }

        lock (_blockReferences)
        {
            _blockReferences[uid] = (ushort)(_blockReferences.GetValueOrDefault(uid) + 1);
        }

        return 0;
    }

    /// <summary>ksceKernelDecRefCountMemBlock(uid): one reference fewer; at 0 the firmware's underflow error.</summary>
    [Hle(Nid = 0xF50BDC0C, Library = "SceSysmemForDriver")]
    int ksceKernelDecRefCountMemBlock(int uid)
    {
        if (K.Objects.Get<MemBlock>(uid) is null)
        {
            return SceError.INVALID_UID;
        }

        lock (_blockReferences)
        {
            int count = _blockReferences.GetValueOrDefault(uid);
            if (count == 0)
            {
                return ReferenceCountUnderflow;
            }

            _blockReferences[uid] = count - 1;
        }

        return 0;
    }
    /// <summary>The error of a range the asked access does not reach.</summary>
    const int RangeNotAccessible = unchecked((int)0x80022007);

    /// <summary>
    /// The firmware's range probe: <paramref name="mode"/> asks for privileged read (0x1) / write (0x2) and user read
    /// (0x10) / write (0x20); every page of [addr, addr + size) (the page at addr for size 0) must allow each asked
    /// access. No asked access, or a range past 4 GB, is SCE_KERNEL_ERROR_INVALID_ARGUMENT; a page that does not allow
    /// it, 0x80022007. The emulated MMU does not keep read-only pages apart, so read and write are checked alike.
    /// </summary>
    int ProbeRange(uint mode, uint addr, uint size)
    {
        if ((mode & 0x33) == 0 || (ulong)addr + size > 0x1_0000_0000UL)
        {
            return SceError.INVALID_ARGUMENT;
        }

        uint first = addr & ~(GuestMemory.PageSize - 1);
        ulong end = size == 0 ? (ulong)first + 1 : (ulong)addr + size;
        for (ulong page = first; page < end; page += GuestMemory.PageSize)
        {
            if ((mode & 0x3) != 0 && !KernelSpace.IsMappedRange((uint)page, 1))
            {
                return RangeNotAccessible;
            }

            if ((mode & 0x30) != 0 && !IsUserAccessible((uint)page, 1))
            {
                return RangeNotAccessible;
            }
        }

        return 0;
    }

    /// <summary>ksceKernelIsAccessibleRange(mode, addr, size): <see cref="ProbeRange"/> in the current address
    /// space; 0 when accessible.</summary>
    [Hle(Nid = 0x9C78064C, Library = "SceSysmemForDriver")]
    int ksceKernelIsAccessibleRange(uint mode, uint addr, uint size) => ProbeRange(mode, addr, size);

    /// <summary>ksceKernelIsAccessibleRangeProc(pid, mode, addr, size): the same in process <paramref name="pid"/>
    /// (0: the current address space).</summary>
    [Hle(Nid = 0x9F6E45E3, Library = "SceSysmemForDriver")]
    int ksceKernelIsAccessibleRangeProc(int pid, uint mode, uint addr, uint size)
    {
        if (pid == 0)
        {
            return ProbeRange(mode, addr, size);
        }

        if ((ulong)addr + size > 0x1_0000_0000UL)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (ProcessOf(pid) is not { } process)
        {
            return SceError.INVALID_PID;
        }

        return InProcess(process, () => ProbeRange(mode, addr, size));
    }
}
