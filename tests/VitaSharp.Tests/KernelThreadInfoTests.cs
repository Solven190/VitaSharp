using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;
using Mutex = VitaSharp.Core.Kernel.Mutex;

namespace VitaSharp.Tests;

/// <summary>
/// SceThreadmgrForDriver's thread and object queries as the FW 3.74 threadmgr has them: the user UID of a thread (-1
/// for a kernel thread), the lightweight mutex and condition variable info by UID (the checks before the lookup, the
/// firmware's own structure copied at the caller's size) and the thread UID lists of a process or of every thread.
/// </summary>
[Collection("guest")]
public sealed class KernelThreadInfoTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int IllegalAddr = unchecked((int)0x80020006), IllegalSize = unchecked((int)0x8002000B),
        InvalidArgument = unchecked((int)0x80020005), UnknownThreadId = unchecked((int)0x80028021),
        UnknownLwMutexId = unchecked((int)0x80028181), UnknownLwCondId = unchecked((int)0x800281C1),
        InvalidPid = unchecked((int)0x80029001);

    private const int EveryThreadPid = 0x10021;

    private readonly HleHarness _harness = new(fixture, "kthreadinfo");

    public void Dispose() => _harness.Dispose();

    private ThreadmgrKernel Threadmgr => _harness.Module<ThreadmgrKernel>();

    private ThreadMgr UserThreadmgr => _harness.Module<ThreadMgr>();

    /// <summary>A thread object that is never started (it stays in the object table for the whole test).</summary>
    private GuestThread IdleThread(string name, bool kernel)
    {
        GuestThread thread = _harness.Emu.Kernel.CreateThread(name, 0, 0x10000100, 0x4000, 0, 0x70000);
        thread.KernelMode = kernel;
        return thread;
    }

    /// <summary>A lightweight mutex made by the user call (on a guest thread: an initial count locks it for it).</summary>
    private int LwMutex(string name, int initCount, uint attr = 0)
    {
        uint work = _harness.Alloc(0x20);
        Assert.Equal(0, Call(UserThreadmgr, "sceKernelCreateLwMutex", work, name, attr, initCount, 0u));
        return (int)GuestMemory.R32(work + Mutex.LwUidOffset);
    }

    private uint LwWork(int uid) => _harness.Emu.Kernel.Objects.Get<Mutex>(uid)!.WorkArea;

    private uint Info(uint size)
    {
        uint info = _harness.Alloc(0x48);
        GuestMemory.Fill(info, 0xCC, 0x48);
        GuestMemory.W32(info, size);
        return info;
    }

    [Fact]
    public void TheUserThreadIdIsTheThreadsOwnUidForAUserThreadAndMinusOneForAKernelThread()
    {
        GuestThread user = IdleThread("user", kernel: false);
        GuestThread kernel = IdleThread("kernel", kernel: true);
        Assert.Equal(user.Uid, Call(Threadmgr, "ksceKernelGetUserThreadId", user.Uid));
        Assert.Equal(-1, Call(Threadmgr, "ksceKernelGetUserThreadId", kernel.Uid));
        _harness.OnGuest(t =>
        {
            Assert.Equal(t.Uid, Call(Threadmgr, "ksceKernelGetUserThreadId", t.Uid));
            // 0 is not the current thread, and a UID of another class is not a thread
            Assert.Equal(UnknownThreadId, Call(Threadmgr, "ksceKernelGetUserThreadId", 0));
            Assert.Equal(UnknownThreadId, Call(Threadmgr, "ksceKernelGetUserThreadId", LwMutex("lw", 0)));
        });
    }

    [Fact]
    public void LwMutexInfoChecksTheBufferFirstAndCopiesTheCallersSize()
    {
        _harness.OnGuest(t =>
        {
            int uid = LwMutex("kernel lw", 2, attr: 0x2u);
            uint work = LwWork(uid);

            Assert.Equal(IllegalAddr, Call(Threadmgr, "ksceKernelGetLwMutexInfo", uid, 0u));
            Assert.Equal(IllegalSize, Call(Threadmgr, "ksceKernelGetLwMutexInfo", uid, Info(0x41)));
            // the size is checked before the UID
            Assert.Equal(IllegalSize, Call(Threadmgr, "ksceKernelGetLwMutexInfo", 0x7FFF0001, Info(0x41)));
            Assert.Equal(UnknownLwMutexId, Call(Threadmgr, "ksceKernelGetLwMutexInfo", 0x7FFF0001, Info(0x40)));

            uint info = Info(0x40);
            Assert.Equal(0, Call(Threadmgr, "ksceKernelGetLwMutexInfo", uid, info));
            Assert.Equal(0x40u, GuestMemory.R32(info));
            Assert.Equal((uint)uid, GuestMemory.R32(info + 4));
            Assert.Equal("kernel lw", GuestMemory.ReadCString(info + 8));
            Assert.Equal(0x2u, GuestMemory.R32(info + 0x28));
            Assert.Equal(work, GuestMemory.R32(info + 0x2C));
            Assert.Equal(2u, GuestMemory.R32(info + 0x30));
            Assert.Equal(2u, GuestMemory.R32(info + 0x34));
            Assert.Equal((uint)t.Uid, GuestMemory.R32(info + 0x38));
            Assert.Equal(0u, GuestMemory.R32(info + 0x3C));
            Assert.Equal(0xCCCCCCCCu, GuestMemory.R32(info + 0x40));

            // a smaller size: only that many bytes, the size word becoming the firmware's 0x40
            uint part = Info(8);
            Assert.Equal(0, Call(Threadmgr, "ksceKernelGetLwMutexInfo", uid, part));
            Assert.Equal(0x40u, GuestMemory.R32(part));
            Assert.Equal((uint)uid, GuestMemory.R32(part + 4));
            Assert.Equal(0xCCCCCCCCu, GuestMemory.R32(part + 8));

            // a normal mutex is not a lightweight one
            int mutex = Call(UserThreadmgr, "sceKernelCreateMutex", "normal", 0u, 0, 0u);
            Assert.True(mutex > 0);
            Assert.Equal(UnknownLwMutexId, Call(Threadmgr, "ksceKernelGetLwMutexInfo", mutex, Info(0x40)));
        });
    }

    [Fact]
    public void LwCondInfoChecksTheBufferFirstAndCopiesTheCallersSize()
    {
        _harness.OnGuest(_ =>
        {
            uint mutexWork = LwWork(LwMutex("lw", 0));
            uint condWork = _harness.Alloc(0x10);
            Assert.True(Call(UserThreadmgr, "sceKernelCreateLwCond", condWork, "kernel cond", 0u, mutexWork, 0u) >= 0);
            int uid = (int)GuestMemory.R32(condWork);
            Assert.True(uid > 0);

            Assert.Equal(IllegalAddr, Call(Threadmgr, "ksceKernelGetLwCondInfo", uid, 0u));
            Assert.Equal(IllegalSize, Call(Threadmgr, "ksceKernelGetLwCondInfo", 0x7FFF0001, Info(0x39)));
            Assert.Equal(UnknownLwCondId, Call(Threadmgr, "ksceKernelGetLwCondInfo", 0x7FFF0001, Info(0x38)));

            uint info = Info(0x38);
            Assert.Equal(0, Call(Threadmgr, "ksceKernelGetLwCondInfo", uid, info));
            Assert.Equal(0x38u, GuestMemory.R32(info));
            Assert.Equal((uint)uid, GuestMemory.R32(info + 4));
            Assert.Equal("kernel cond", GuestMemory.ReadCString(info + 8));
            Assert.Equal(condWork, GuestMemory.R32(info + 0x2C));
            Assert.Equal(mutexWork, GuestMemory.R32(info + 0x30));
            Assert.Equal(0u, GuestMemory.R32(info + 0x34));
            Assert.Equal(0xCCCCCCCCu, GuestMemory.R32(info + 0x38));

            // a normal condition variable is not a lightweight one
            int mutex = Call(UserThreadmgr, "sceKernelCreateMutex", "normal", 0u, 0, 0u);
            int cond = Call(UserThreadmgr, "sceKernelCreateCond", "normal cond", 0u, mutex, 0u);
            Assert.True(cond > 0);
            Assert.Equal(UnknownLwCondId, Call(Threadmgr, "ksceKernelGetLwCondInfo", cond, Info(0x38)));
        });
    }

    [Fact]
    public void TheThreadIdListOfAProcessHasItsUserThreadsAndTheWholeListHasEveryThread()
    {
        GuestThread user = IdleThread("listed", kernel: false);
        GuestThread kernel = IdleThread("kernel", kernel: true);
        uint ids = _harness.Alloc(0x100);
        uint count = _harness.Word(0xFFFFFFFF);

        int processTotal = Call(Threadmgr, "ksceKernelGetThreadIdList", user.Pid, ids, 0x100u, count);
        Assert.True(processTotal >= 1);
        Assert.Equal((uint)processTotal, GuestMemory.R32(count));
        var processIds = Enumerable.Range(0, processTotal).Select(i => (int)GuestMemory.R32(ids + (uint)i * 4)).ToList();
        Assert.Contains(user.Uid, processIds);
        Assert.DoesNotContain(kernel.Uid, processIds);

        int everyTotal = Call(Threadmgr, "ksceKernelGetThreadIdList", EveryThreadPid, ids, 0x100u, count);
        Assert.Equal(processTotal + 1, everyTotal);
        var everyIds = Enumerable.Range(0, everyTotal).Select(i => (int)GuestMemory.R32(ids + (uint)i * 4)).ToList();
        Assert.Contains(user.Uid, everyIds);
        Assert.Contains(kernel.Uid, everyIds);

        // a short buffer: the total still comes back, the count says how many were copied
        GuestMemory.W32(ids + 4, 0xDEADBEEF);
        Assert.Equal(everyTotal, Call(Threadmgr, "ksceKernelGetThreadIdList", EveryThreadPid, ids, 7u, count));
        Assert.Equal(1u, GuestMemory.R32(count));
        Assert.Equal(0xDEADBEEFu, GuestMemory.R32(ids + 4));
        // less than one UID of room: no buffer needed, only the total
        Assert.Equal(processTotal, Call(Threadmgr, "ksceKernelGetThreadIdList", user.Pid, 0u, 3u, count));
        Assert.Equal(0u, GuestMemory.R32(count));
    }

    [Fact]
    public void TheThreadIdListRefusesANullBufferTheKernelPidAndAnUnknownPid()
    {
        uint count = _harness.Word(0);
        Assert.Equal(InvalidArgument, Call(Threadmgr, "ksceKernelGetThreadIdList", EveryThreadPid, 0u, 4u, count));
        uint ids = _harness.Alloc(0x10);
        Assert.Equal(InvalidArgument, Call(Threadmgr, "ksceKernelGetThreadIdList", KernelSpace.KernelPid, ids, 0x10u, count));
        Assert.Equal(InvalidPid, Call(Threadmgr, "ksceKernelGetThreadIdList", 0x7FFF0001, ids, 0x10u, count));
    }
}
