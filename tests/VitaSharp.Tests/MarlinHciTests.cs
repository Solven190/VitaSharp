using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader.Self;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;
using Mutex = VitaSharp.Core.Kernel.Mutex;

namespace VitaSharp.Tests;

/// <summary>SceMarlinHci as os0:kd/marlin_hci.skprx 3.74 answers it on a unit without the console's Marlin keys.</summary>
[Collection("guest")]
public sealed class MarlinHciTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "marlin-hci");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    /// <summary>A capability with only bit <paramref name="bit"/> set (counted from the first byte's top bit).</summary>
    private void RunWithCapabilityBit(int bit)
    {
        byte[] capability = new byte[ProgramAccess.CapabilitySize];
        capability[bit >> 3] = (byte)(0x80 >> (bit & 7));
        _harness.Emu.Access = new ProgramAccess(0x2800000000000001UL, capability, false);
    }

    private void RunAllowed() => RunWithCapabilityBit(SystemServicesVsh.MarlinCapabilityBit);

    /// <summary>A block of <paramref name="size"/> bytes filled with 0xAA, so the words the module writes show.</summary>
    private uint Block(uint size)
    {
        uint block = _harness.Alloc(size);
        GuestMemory.Fill(block, 0xAA, size);
        return block;
    }

    private static void Pair(uint at, uint size, uint data)
    {
        GuestMemory.W32(at, size);
        GuestMemory.W32(at + 4, data);
    }

    [Fact]
    public void CapabilityBit0x80IsRequired()
    {
        RunWithCapabilityBit(0x81); // the MagicGate video bit is not enough
        _harness.OnGuest(_ =>
        {
            Assert.Equal(SystemServicesVsh.MarlinErrNotAllowed, Call(Services, "MarlinLock"));
            Assert.Equal(SystemServicesVsh.MarlinErrNotAllowed, Call(Services, "MarlinUnlock"));
        });
        Assert.Equal(SystemServicesVsh.MarlinErrNotAllowed, Call(Services, "MarlinStart"));
        Assert.Equal(SystemServicesVsh.MarlinErrNotAllowed, Call(Services, "MarlinStop"));
        uint handle = Block(4);
        Assert.Equal(SystemServicesVsh.MarlinErrNotAllowed, Call(Services, "MarlinMakeKeyObject", 0u, handle));
        Assert.Equal(0xAAAAAAAAu, GuestMemory.R32(handle));
        Assert.Equal(SystemServicesVsh.MarlinErrNotAllowed, Call(Services, "Marlin24D94277", 0u)); // before the block copy
        Assert.Equal(SystemServicesVsh.MarlinErrNotAllowed, Call(Services, "MarlinCheckKind", 0));
        Assert.Equal(SystemServicesVsh.MarlinErrNotAllowed, Call(Services, "MarlinWriteKeys", 0, 0u, 0u));
    }

    [Fact]
    public void LockAndUnlockUseTheModulesMutexUntilItsStop()
    {
        RunAllowed();
        _harness.OnGuest(_ =>
        {
            Assert.Equal(0, Call(Services, "MarlinLock"));
            Assert.Equal(SceError.MUTEX_RECURSIVE, Call(Services, "MarlinLock")); // a mutex without the recursive attribute
            Assert.Equal(0, Call(Services, "MarlinUnlock"));
            Assert.Equal(0, Call(Services, "MarlinUnlock")); // the unlock's own result is not returned
        });

        Assert.Equal(0, Call(Services, "MarlinStop"));
        Assert.Equal(-1, _harness.Emu.Vita.MarlinHciMutex);
        _harness.OnGuest(_ => Assert.Equal(SceError.UNKNOWN_MUTEX_ID, Call(Services, "MarlinLock")));

        Assert.Equal(0, Call(Services, "MarlinStart"));
        Assert.NotNull(_harness.Emu.Kernel.Objects.Get<Mutex>(_harness.Emu.Vita.MarlinHciMutex));
        _harness.OnGuest(_ =>
        {
            Assert.Equal(0, Call(Services, "MarlinLock"));
            Assert.Equal(0, Call(Services, "MarlinUnlock"));
        });
    }

    [Fact]
    public void ASecondThreadWaitsForTheLockHolder()
    {
        RunAllowed();
        using var locked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        GuestRun holder = _harness.Start(_ =>
        {
            Assert.Equal(0, Call(Services, "MarlinLock"));
            locked.Set();
            release.Wait();
            Assert.Equal(0, Call(Services, "MarlinUnlock"));
        }, "holder");
        locked.Wait();

        bool waiterGotIt = false;
        GuestRun waiter = _harness.Start(_ =>
        {
            Assert.Equal(0, Call(Services, "MarlinLock"));
            waiterGotIt = true;
            Assert.Equal(0, Call(Services, "MarlinUnlock"));
        }, "waiter");

        Mutex mutex = _harness.Emu.Kernel.Objects.Get<Mutex>(_harness.Emu.Vita.MarlinHciMutex)!;
        Until(() => Volatile.Read(ref mutex.Waiters) == 1, "the second thread waiting on the module's mutex");
        Assert.False(waiterGotIt);
        release.Set();
        _harness.Join(holder);
        _harness.Join(waiter);
        Assert.True(waiterGotIt);
    }

    [Fact]
    public void KeyObjectsAreNotMadeWithoutTheKeyFile()
    {
        RunAllowed();
        foreach (uint kind in new uint[] { 0, 1, 2, 3, 4 })
        {
            uint handle = Block(4);
            Assert.Equal(0, Call(Services, "MarlinMakeKeyObject", kind, handle));
            Assert.Equal(0u, GuestMemory.R32(handle));
        }

        uint second = Block(4);
        Assert.Equal(0, Call(Services, "MarlinMakeSecondKeyObject", second));
        Assert.Equal(0u, GuestMemory.R32(second));

        // the handle is written whatever the engine answered: a NULL handle pointer is the copy's fault
        Assert.Equal(KernelSpace.UserCopyFault, Call(Services, "MarlinMakeKeyObject", 0u, 0u));
        Assert.Equal(KernelSpace.UserCopyFault, Call(Services, "MarlinMakeSecondKeyObject", 0u));
    }

    [Fact]
    public void HandlesNotInTheTableAnswerAsTheEngineDoes()
    {
        RunAllowed();
        uint output = Block(4);
        Assert.Equal(0, Call(Services, "MarlinFromHandle", 0x1000u, output));
        Assert.Equal(0u, GuestMemory.R32(output));
        Assert.Equal(KernelSpace.UserCopyFault, Call(Services, "MarlinFromHandle", 0x1000u, 0u));
        Assert.Equal(0, Call(Services, "MarlinHandleState", 0x1000u));
        Assert.Equal(SystemServicesVsh.MarlinEngineInvalid, Call(Services, "MarlinRelease", 0x1000u));

        uint data = Block(8);
        Assert.Equal(SystemServicesVsh.MarlinEngineInvalid, Call(Services, "MarlinTakeData", 0x1000u, 0u, data));
        Assert.Equal(0xAAAAAAAAu, GuestMemory.R32(data)); // nothing copied out
    }

    [Fact]
    public void ParameterBlocksAndTheirPairsAreCopiedThenTheOutputWordIsCleared()
    {
        RunAllowed();
        uint block = Block(0x20);
        uint data = Block(0x10);
        Pair(block + 8, 0x10, data);
        Pair(block + 0x10, 0, 0);
        Assert.Equal(0, Call(Services, "Marlin24D94277", block));
        Assert.Equal(0u, GuestMemory.R32(block + 0x1C));
        Assert.Equal(0xAAAAAAAAu, GuestMemory.R32(block + 0x18)); // only the output word

        // a pair whose data cannot be read fails the copy; one above 0xFFFF bytes is passed on without data
        Pair(block + 0x10, 4, 0x10);
        Assert.Equal(KernelSpace.UserCopyFault, Call(Services, "Marlin24D94277", block));
        Pair(block + 0x10, 0x10000, 0x10);
        Assert.Equal(0, Call(Services, "Marlin24D94277", block));
        Pair(block + 0x10, 4, 0); // no pointer: no copy
        Assert.Equal(0, Call(Services, "Marlin24D94277", block));

        Assert.Equal(KernelSpace.UserCopyFault, Call(Services, "Marlin24D94277", 0u));
        Assert.Equal(KernelSpace.UserCopyFault, Call(Services, "Marlin52985887", 0u));
    }

    [Fact]
    public void EachBlockEntryHasItsOwnLayout()
    {
        RunAllowed();
        uint license = Block(0x2C);
        for (uint at = 8; at <= 0x20; at += 8)
        {
            Pair(license + at, 0, 0);
        }

        Assert.Equal(0, Call(Services, "Marlin00F544DF", license));
        Assert.Equal(0u, GuestMemory.R32(license + 0x28));
        Pair(license + 0x20, 8, 0x10); // the fourth pair is copied too
        Assert.Equal(KernelSpace.UserCopyFault, Call(Services, "Marlin00F544DF", license));

        uint keyed = Block(0x24);
        Pair(keyed + 0x10, 0, 0);
        Pair(keyed + 0x18, 0, 0);
        GuestMemory.W32(keyed + 0x0C, 0x10); // the optional 8-byte value cannot be read
        Assert.Equal(KernelSpace.UserCopyFault, Call(Services, "MarlinAE0FBC3F", keyed));
        GuestMemory.W32(keyed + 0x0C, 0);
        Assert.Equal(0, Call(Services, "MarlinAE0FBC3F", keyed));
        Assert.Equal(0u, GuestMemory.R32(keyed + 0x20));

        uint twoPairs = Block(0x1C);
        Pair(twoPairs + 8, 0, 0);
        Pair(twoPairs + 0x10, 0, 0);
        Assert.Equal(0, Call(Services, "MarlinTwoPairBlock", twoPairs));
        Assert.Equal(0u, GuestMemory.R32(twoPairs + 0x18));
    }

    [Fact]
    public void PairArgumentsAreOptionalExceptForEAD075E3()
    {
        RunAllowed();
        uint output = Block(4);
        Assert.Equal(0, Call(Services, "MarlinB7FB96EC", 0x1000u, 0u, 0u, output));
        Assert.Equal(0u, GuestMemory.R32(output));
        Assert.Equal(0, Call(Services, "MarlinF7303D28", 0x1000u, 0u, 0u, output));

        uint pair = Block(8);
        Pair(pair, 4, 0x10);
        Assert.Equal(KernelSpace.UserCopyFault, Call(Services, "MarlinB7FB96EC", 0x1000u, 0u, pair, output));
        Assert.Equal(KernelSpace.UserCopyFault, Call(Services, "MarlinF7303D28", 0x1000u, 0u, pair, output));

        // EAD075E3 needs its pair and copies any size below 0x10000, also from a NULL pointer
        Assert.Equal(KernelSpace.UserCopyFault, Call(Services, "MarlinEAD075E3", 0x1000u, 0u, output));
        Pair(pair, 4, 0);
        Assert.Equal(KernelSpace.UserCopyFault, Call(Services, "MarlinEAD075E3", 0x1000u, pair, output));
        Pair(pair, 0, 0);
        GuestMemory.W32(output, 0xAAAAAAAA);
        Assert.Equal(0, Call(Services, "MarlinEAD075E3", 0x1000u, pair, output));
        Assert.Equal(0u, GuestMemory.R32(output));
        Pair(pair, 0x10000, 0);
        Assert.Equal(0, Call(Services, "MarlinEAD075E3", 0x1000u, pair, output));
    }

    [Fact]
    public void TheKeyHandleListIsEmpty()
    {
        RunAllowed();
        uint handles = Block(0x40);
        uint count = Block(4);
        Assert.Equal(0, Call(Services, "MarlinKeyHandles", handles, count));
        Assert.Equal(0u, GuestMemory.R32(count));
        Assert.Equal(KernelSpace.UserCopyFault, Call(Services, "MarlinKeyHandles", handles, 0u));

        // the second set's copy: the size word first, at most 0x40 bytes of handles, the buffers copied out zeroed
        uint size = _harness.Alloc(8);
        GuestMemory.W32(size, 0x41);
        Assert.Equal(SystemServicesVsh.MarlinErrTooLarge, Call(Services, "MarlinKeyHandlesCopy", handles, count, size));
        Assert.Equal(KernelSpace.UserCopyFault, Call(Services, "MarlinKeyHandlesCopy", handles, count, 0u));
        GuestMemory.W32(size, 0x40);
        GuestMemory.Fill(handles, 0xAA, 0x40);
        Assert.Equal(0, Call(Services, "MarlinKeyHandlesCopy", handles, count, size));
        Assert.Equal(0u, GuestMemory.R32(handles + 0x3C));
        Assert.Equal(KernelSpace.UserCopyFault, Call(Services, "MarlinKeyHandlesCopy", handles, 0u, size));

        RunWithCapabilityBit(1);
        GuestMemory.Fill(handles, 0xAA, 0x40);
        Assert.Equal(SystemServicesVsh.MarlinErrNotAllowed, Call(Services, "MarlinKeyHandlesCopy", handles, count, size));
        Assert.Equal(0u, GuestMemory.R32(handles)); // copied out zeroed even so
    }

    [Fact]
    public void KeyKindsAreZeroAndOne()
    {
        RunAllowed();
        Assert.Equal(0, Call(Services, "MarlinCheckKind", 0));
        Assert.Equal(0, Call(Services, "MarlinCheckKind", 1));
        Assert.Equal(SystemServicesVsh.MarlinEngineInvalid, Call(Services, "MarlinCheckKind", 2));
        Assert.Equal(SystemServicesVsh.MarlinEngineInvalid, Call(Services, "MarlinCheckKind", -1));
    }

    [Fact]
    public void WritingNoKeysRemovesTheKeyFile()
    {
        RunAllowed();
        string folder = _harness.Emu.Vfs.Resolve(SystemServicesVsh.MarlinKeyFolder, forWrite: true)!;
        string file = _harness.Emu.Vfs.Resolve(SystemServicesVsh.MarlinKeyFile(1), forWrite: true)!;
        Assert.False(Directory.Exists(folder));

        Assert.Equal(0, Call(Services, "MarlinWriteKeys", 1, 0u, 0u)); // no file: 0, the folder is made
        Assert.True(Directory.Exists(folder));
        File.WriteAllBytes(file, [1, 2, 3]);
        string other = _harness.Emu.Vfs.Resolve(SystemServicesVsh.MarlinKeyFile(0), forWrite: true)!;
        File.WriteAllBytes(other, [4]);
        Assert.Equal(0, Call(Services, "MarlinWriteKeys", 1, 0u, 0u));
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(other)); // only the given kind's file
    }

    [Fact]
    public void WritingKeysChecksTheCopyTheSizeAndTheKind()
    {
        RunAllowed();
        uint data = Block(0x20);
        Assert.Equal(KernelSpace.UserCopyFault, Call(Services, "MarlinWriteKeys", 0, 0x20u, 0x10u));
        Assert.Equal(SystemServicesVsh.MarlinEngineInvalid, Call(Services, "MarlinWriteKeys", 2, 0x20u, data));
        Assert.Equal(SystemServicesVsh.MarlinEngineInvalid, Call(Services, "MarlinWriteKeys", 0, 0x10001u, 0x10u));
        Assert.Equal(SystemServicesVsh.MarlinEngineInvalid, Call(Services, "MarlinWriteKeys", 2, 0u, 0u));

        // the keys need the console's device key to be encrypted: the module's answer when the file cannot be written
        Assert.Equal(SystemServicesVsh.MarlinEngineFileError, Call(Services, "MarlinWriteKeys", 0, 0x20u, data));
        Assert.False(File.Exists(_harness.Emu.Vfs.Resolve(SystemServicesVsh.MarlinKeyFile(0), forWrite: true)!));
    }
}
