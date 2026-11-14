using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceThreadmgrForDriver's fast mutexes as the FW 3.74 threadmgr has them: the checks and error codes of each call, the
/// state the firmware keeps in the caller's 0x40-byte work area (magic, UID, count, owner), recursion only with the
/// attribute, the ceiling priority, and a contended lock handed to the waiting thread on unlock (the order between the
/// threads forced by waiting for the waiter to be queued, never by the host clock).
/// </summary>
[Collection("guest")]
public sealed class KernelFastMutexTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int IllegalAttr = unchecked((int)0x8002000E), IllegalSize = unchecked((int)0x8002000B),
        InvalidArgument = unchecked((int)0x80020005), IllegalPriority = unchecked((int)0x80028023), CanNotWait = unchecked((int)0x80028004),
        Recursive = unchecked((int)0x80028162), FailedToOwn = unchecked((int)0x80028164), NotOwned = unchecked((int)0x80028165),
        Locked = unchecked((int)0x80028166), AlreadyInitialized = unchecked((int)0x80028167), NotInitialized = unchecked((int)0x80028168);

    private const uint Magic = 0xA35DB473;

    private readonly HleHarness _harness = new(fixture, "kfastmutex");

    public void Dispose() => _harness.Dispose();

    private ThreadmgrKernel Threadmgr => _harness.Module<ThreadmgrKernel>();

    private int Fm(string function, params object?[] args) => Call(Threadmgr, function, args);

    private uint Work()
    {
        uint work = _harness.Alloc(0x40);
        GuestMemory.Fill(work, 0, 0x40);
        return work;
    }

    private uint Option(uint size, uint ceiling)
    {
        uint option = _harness.Alloc(8);
        GuestMemory.W32(option, size);
        GuestMemory.W32(option + 4, ceiling);
        return option;
    }

    [Fact]
    public void InitializeChecksItsArgumentsAsTheFirmware()
    {
        _harness.OnGuest(_ =>
        {
            Assert.Equal(IllegalAttr, Fm("ksceKernelInitializeFastMutex", Work(), "fm", 0x1u, 0u));
            Assert.Equal(IllegalAttr, Fm("ksceKernelInitializeFastMutex", Work(), "fm", 0x10u, 0u));
            Assert.Equal(IllegalSize, Fm("ksceKernelInitializeFastMutex", Work(), "fm", 0u, Option(12, 0)));
            Assert.Equal(InvalidArgument, Fm("ksceKernelInitializeFastMutex", Work(), "fm", 0x4u, 0u));
            Assert.Equal(IllegalPriority, Fm("ksceKernelInitializeFastMutex", Work(), "fm", 0x4u, Option(8, 0xFF)));
            uint work = Work();
            Assert.Equal(0, Fm("ksceKernelInitializeFastMutex", work, "fm", 0xA002u, 0u));
            Assert.Equal(Magic, GuestMemory.R32(work + 0x24));
            Assert.Equal(work, GuestMemory.R32(work));
            Assert.Equal(0xFEu, GuestMemory.R32(work + 0x20));
            Assert.NotEqual(0u, GuestMemory.R32(work + 0x08));
            Assert.Equal(AlreadyInitialized, Fm("ksceKernelInitializeFastMutex", work, "fm", 0u, 0u));
        });
    }

    [Fact]
    public void AWorkAreaThatIsNotInitializedIsRefusedByEveryCall()
    {
        _harness.OnGuest(_ =>
        {
            uint work = Work();
            uint info = _harness.Word(0x44);
            Assert.Equal(NotInitialized, Fm("ksceKernelLockFastMutex", work));
            Assert.Equal(NotInitialized, Fm("ksceKernelTryLockFastMutex", work));
            Assert.Equal(NotInitialized, Fm("ksceKernelUnlockFastMutex", work));
            Assert.Equal(NotInitialized, Fm("ksceKernelFinalizeFastMutex", work));
            Assert.Equal(NotInitialized, Fm("ksceKernelGetFastMutexInfo", work, info));
        });
    }

    [Fact]
    public void RecursionNeedsTheAttributeAndOnlyTheOwnerUnlocks()
    {
        _harness.OnGuest(t =>
        {
            uint plain = Work();
            Assert.Equal(0, Fm("ksceKernelInitializeFastMutex", plain, "plain", 0u, 0u));
            Assert.Equal(0, Fm("ksceKernelLockFastMutex", plain));
            Assert.Equal(Recursive, Fm("ksceKernelLockFastMutex", plain));
            Assert.Equal(Recursive, Fm("ksceKernelTryLockFastMutex", plain));
            Assert.Equal((uint)t.Uid, GuestMemory.R32(plain + 0x1C));
            Assert.Equal(1u, GuestMemory.R32(plain + 0x18));
            Assert.Equal(Locked, Fm("ksceKernelFinalizeFastMutex", plain));
            Assert.Equal(0, Fm("ksceKernelUnlockFastMutex", plain));
            Assert.Equal(NotOwned, Fm("ksceKernelUnlockFastMutex", plain));
            Assert.Equal(0u, GuestMemory.R32(plain + 0x1C));

            uint recursive = Work();
            Assert.Equal(0, Fm("ksceKernelInitializeFastMutex", recursive, "recursive", 0x2u, 0u));
            Assert.Equal(0, Fm("ksceKernelLockFastMutex", recursive));
            Assert.Equal(0, Fm("ksceKernelTryLockFastMutex", recursive));
            Assert.Equal(2u, GuestMemory.R32(recursive + 0x18));
            Assert.Equal(0, Fm("ksceKernelUnlockFastMutex", recursive));
            Assert.Equal((uint)t.Uid, GuestMemory.R32(recursive + 0x1C));
            Assert.Equal(0, Fm("ksceKernelUnlockFastMutex", recursive));
            Assert.Equal(0u, GuestMemory.R32(recursive + 0x1C));

            // finalize clears the whole work area
            Assert.Equal(0, Fm("ksceKernelFinalizeFastMutex", recursive));
            Assert.True(GuestMemory.Span(recursive, 0x40).ToArray().All(b => b == 0));
            Assert.Equal(NotInitialized, Fm("ksceKernelLockFastMutex", recursive));
        });
    }

    [Fact]
    public void TheInfoIsTheFirmwaresLayout()
    {
        _harness.OnGuest(t =>
        {
            uint work = Work();
            Assert.Equal(0, Fm("ksceKernelInitializeFastMutex", work, "SceTestFastMutex", 0xAu, 0u));
            uint info = _harness.Alloc(0x48);
            GuestMemory.Fill(info, 0xCC, 0x48);
            GuestMemory.W32(info, 0x48);
            Assert.Equal(IllegalSize, Fm("ksceKernelGetFastMutexInfo", work, info));
            GuestMemory.W32(info, 0x44);
            Assert.Equal(unchecked((int)0x80020006), Fm("ksceKernelGetFastMutexInfo", work, 0u));
            Assert.Equal(0, Fm("ksceKernelGetFastMutexInfo", work, info));
            Assert.Equal(0x44u, GuestMemory.R32(info));
            Assert.Equal(GuestMemory.R32(work + 8), GuestMemory.R32(info + 4));
            Assert.Equal("SceTestFastMutex", GuestMemory.ReadCString(info + 8));
            Assert.Equal(0xAu, GuestMemory.R32(info + 0x28));
            Assert.Equal(work, GuestMemory.R32(info + 0x2C));
            // attribute 0x8: locked by the caller at initialization
            Assert.Equal(1u, GuestMemory.R32(info + 0x30));
            Assert.Equal((uint)t.Uid, GuestMemory.R32(info + 0x34));
            Assert.Equal(0xFEu, GuestMemory.R32(info + 0x38));
            Assert.Equal(0xCCu, GuestMemory.Read<byte>(info + 0x44));
            // a smaller size copies only that much
            uint small = _harness.Alloc(0x10);
            GuestMemory.Fill(small, 0xCC, 0x10);
            GuestMemory.W32(small, 8);
            Assert.Equal(0, Fm("ksceKernelGetFastMutexInfo", work, small));
            Assert.Equal(0x44u, GuestMemory.R32(small));
            Assert.Equal(0xCCu, GuestMemory.Read<byte>(small + 8));
        });
    }

    [Fact]
    public void AContendedLockGoesToTheWaiterWhenTheOwnerUnlocks()
    {
        uint info = _harness.Word(0x44);
        uint second = Work();
        using var release = new ManualResetEventSlim();
        GuestRun owner = _harness.Start(t =>
        {
            Assert.Equal(0, Fm("ksceKernelInitializeFastMutex", second, "contended2", 0x2000u, 0u));
            Assert.Equal(0, Fm("ksceKernelLockFastMutex", second));
            release.Wait();
            Assert.Equal(0, Fm("ksceKernelUnlockFastMutex", second));
        }, "owner");
        Until(() => GuestMemory.R32(second + 0x1C) != 0, "the owner to lock");

        int waiterUid = 0;
        GuestRun waiter = _harness.Start(t =>
        {
            waiterUid = t.Uid;
            Assert.Equal(FailedToOwn, Fm("ksceKernelTryLockFastMutex", second));
            Assert.Equal(0, Fm("ksceKernelLockFastMutex", second));
            Assert.Equal((uint)t.Uid, GuestMemory.R32(second + 0x1C));
            Assert.Equal(1u, GuestMemory.R32(second + 0x18));
            Assert.Equal(0, Fm("ksceKernelUnlockFastMutex", second));
        }, "waiter");
        Until(() => Fm("ksceKernelGetFastMutexInfo", second, info) == 0 && GuestMemory.R32(info + 0x40) == 1, "the waiter to queue");
        release.Set();
        _harness.Join(owner);
        _harness.Join(waiter);
        Assert.NotEqual(0, waiterUid);
        Assert.Equal(0u, GuestMemory.R32(second + 0x1C));
    }

    [Fact]
    public void LockWithInterruptsMaskedCannotWait()
    {
        _harness.OnGuest(_ =>
        {
            uint work = Work();
            Assert.Equal(0, Fm("ksceKernelInitializeFastMutex", work, "masked", 0u, 0u));
            SysmemKernel sysmem = _harness.Module<SysmemKernel>();
            int previous = Call(sysmem, "ksceKernelCpuSuspendIntr");
            Assert.Equal(CanNotWait, Fm("ksceKernelLockFastMutex", work));
            Assert.Equal(0, Fm("ksceKernelTryLockFastMutex", work));
            Invoke(sysmem, "ksceKernelCpuResumeIntr", previous);
            Assert.Equal(0, Fm("ksceKernelUnlockFastMutex", work));
        });
    }

    [Fact]
    public void TheCeilingPriorityRaisesTheOwnerWhileItHoldsTheMutex()
    {
        _harness.OnGuest(t =>
        {
            int own = t.Priority;
            uint work = Work();
            Assert.Equal(0, Fm("ksceKernelInitializeFastMutex", work, "ceiling", 0x4u, Option(8, (uint)(own - 8))));
            Assert.Equal((uint)(own - 8), GuestMemory.R32(work + 0x20));
            Assert.Equal(0, Fm("ksceKernelLockFastMutex", work));
            Assert.Equal(own - 8, t.Priority);
            Assert.Equal(0, Fm("ksceKernelUnlockFastMutex", work));
            Assert.Equal(own, t.Priority);
            // a ceiling of 0 is the caller's priority; locked at once, it may not be lower than the caller's
            uint mine = Work();
            Assert.Equal(0, Fm("ksceKernelInitializeFastMutex", mine, "mine", 0x4u, Option(8, 0)));
            Assert.Equal((uint)own, GuestMemory.R32(mine + 0x20));
            Assert.Equal(IllegalPriority, Fm("ksceKernelInitializeFastMutex", Work(), "lower", 0xCu, Option(8, (uint)(own + 1))));
        });
    }
}
