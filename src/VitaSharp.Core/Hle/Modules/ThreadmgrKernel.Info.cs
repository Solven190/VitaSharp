using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Mutex = VitaSharp.Core.Kernel.Mutex;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The thread and object queries of SceThreadmgrForDriver, as the FW 3.74 threadmgr.skprx has them (decompiled, study
/// only). The firmware keeps in each thread object (+0xC0) the UID the thread has in its process: a user thread gets one
/// when it is created (ksceKernelCreateUserUid of the process for the thread's kernel UID), a kernel thread keeps -1.
/// VitaSharp has one UID space for the kernel and the processes, so a user thread's user UID is its own UID.
/// </summary>
public sealed partial class ThreadmgrKernel
{
    /// <summary>The pid ksceKernelGetThreadIdList takes for every thread object (the firmware lists the whole thread
    /// class then, instead of the handles one process holds).</summary>
    const int EveryThreadPid = 0x10021;

    /// <summary>ksceKernelGetUserThreadId(thid): the UID thread <paramref name="thid"/> has in its process (its +0xC0
    /// word): -1 for a kernel thread; not a thread object is SCE_KERNEL_ERROR_UNKNOWN_THREAD_ID (0 is not one).</summary>
    [Hle(Nid = 0xF311808F, Library = "SceThreadmgrForDriver")]
    int ksceKernelGetUserThreadId(int thid)
    {
        if (K.Objects.Get<GuestThread>(thid) is not { } thread)
        {
            return SceError.UNKNOWN_THREAD_ID;
        }

        return UserThreadId(thread);
    }

    static int UserThreadId(GuestThread thread) => thread.KernelMode ? -1 : thread.Uid;

    /// <summary>
    /// ksceKernelGetLwMutexInfo(SceUID lwmutexid, SceKernelLwMutexInfo *info): the info of a lightweight mutex by its
    /// UID. A null info is SCE_KERNEL_ERROR_ILLEGAL_ADDR and a size above 0x40 SCE_KERNEL_ERROR_ILLEGAL_SIZE, both
    /// before the UID is looked up; the firmware fills its own 0x40 bytes (+0 the size 0x40) and copies the caller's
    /// size of them. The owner is the thread's user UID.
    /// </summary>
    [Hle(Nid = 0x15287EF6, Library = "SceThreadmgrForDriver")]
    int ksceKernelGetLwMutexInfo(int uid, uint info)
    {
        if (info == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        if (GuestMemory.R32(info) > ThreadMgr.LwMutexInfoSize)
        {
            return SceError.ILLEGAL_SIZE;
        }

        if (K.Objects.Get<Mutex>(uid) is not { IsLight: true } mutex)
        {
            return SceError.UNKNOWN_LW_MUTEX_ID;
        }

        Span<byte> fields = stackalloc byte[(int)ThreadMgr.LwMutexInfoSize];
        ThreadMgr.FillLwMutexInfo(mutex, fields);
        CopyInfo(info, fields);
        return 0;
    }

    /// <summary>ksceKernelGetLwCondInfo(SceUID lwcondid, SceKernelLwCondInfo *info): as
    /// <see cref="ksceKernelGetLwMutexInfo"/> for a lightweight condition variable (0x38 bytes).</summary>
    [Hle(Nid = 0xC2AFFBD0, Library = "SceThreadmgrForDriver")]
    int ksceKernelGetLwCondInfo(int uid, uint info)
    {
        if (info == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        if (GuestMemory.R32(info) > ThreadMgr.LwCondInfoSize)
        {
            return SceError.ILLEGAL_SIZE;
        }

        if (K.Objects.Get<CondVar>(uid) is not { WorkArea: not 0 } cond)
        {
            return SceError.UNKNOWN_LW_COND_ID;
        }

        Span<byte> fields = stackalloc byte[(int)ThreadMgr.LwCondInfoSize];
        ThreadMgr.FillLwCondInfo(cond, fields);
        CopyInfo(info, fields);
        return 0;
    }

    /// <summary>Copies the caller's size (read again, at most the whole structure) of the filled structure.</summary>
    static void CopyInfo(uint info, ReadOnlySpan<byte> fields)
    {
        int size = (int)Math.Min(GuestMemory.R32(info), (uint)fields.Length);
        fields[..size].CopyTo(GuestMemory.Span(info, size));
    }

    /// <summary>
    /// ksceKernelGetThreadIdList(SceUID pid, SceUID *ids, int size, int *count): the threads of process
    /// <paramref name="pid"/> (the thread UIDs it holds: its user threads), or every thread object for 0x10021. Up to
    /// size / 4 UIDs go to <paramref name="ids"/> and their number to <paramref name="count"/>; the return value is how
    /// many there are in all. A buffer of 4 bytes or more at null is SCE_KERNEL_ERROR_INVALID_ARGUMENT, and so is the
    /// kernel's pid (it has no handles of its own); a pid without a process is SCE_KERNEL_ERROR_INVALID_PID.
    /// </summary>
    [Hle(Nid = 0xEA7B8AEF, Library = "SceThreadmgrForDriver")]
    int ksceKernelGetThreadIdList(int pid, uint ids, uint size, uint count)
    {
        if (size >= 4 && ids == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        Emulator? process = null;
        if (pid != EveryThreadPid)
        {
            if (pid == KernelSpace.KernelPid)
            {
                return SceError.INVALID_ARGUMENT;
            }

            process = KernelSpace.ProcessOf(Emu, pid);
            if (process == null)
            {
                return SceError.INVALID_PID;
            }
        }

        int capacity = (int)(size >> 2);
        int total = 0;
        int copied = 0;
        foreach (GuestThread thread in K.Objects.All<GuestThread>().OrderBy(static t => t.Uid))
        {
            if (process != null && (thread.KernelMode || thread.Process != process))
            {
                continue;
            }

            total++;
            if (ids != 0 && copied < capacity)
            {
                GuestMemory.W32(ids + (uint)copied * 4, (uint)thread.Uid);
                copied++;
            }
        }

        if (count != 0)
        {
            GuestMemory.W32(count, (uint)copied);
        }

        return total;
    }
}
