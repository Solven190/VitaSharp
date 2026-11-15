using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceCpuForDriver / SceCpuForKernel as the FW 3.74 SceSysmem has them (studied in the disassembled module): the
/// interrupt mask and the spinlocks kept in a guest word (what each one stores, returns and puts back), the
/// reader-writer spinlocks, the special atomics, the cache operations (faults on unmapped lines, the regions the outer
/// cache operations skip, the code they drop), the CPU registers, the core lock and the user lock words. The FW 3.74
/// NIDs are called through a kernel module's import stubs, so the NID binding is what is tested.
/// </summary>
[Collection("guest")]
public sealed class KernelCpuTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint CpuForDriver = 0x40ECDB0E, CpuForKernel = 0xA5195D20;
    private const uint SpinlockLowLockCpuSuspendIntr = 0xD32ACE9E, SpinlockLowUnlockCpuResumeIntr = 0x7BB9D5DF;
    private const uint WriteLockCpuSuspendIntr = 0x4C38CE4D, TryReadLockCpuSuspendIntr = 0xF02467D1;
    private const uint CpuSuspendIntr = 0x821FC0EE, CpuResumeIntr = 0xF5BAD43B;

    private readonly HleHarness _harness = new(fixture, "kcpu");

    public void Dispose() => _harness.Dispose();

    private SysmemKernel Sysmem => _harness.Module<SysmemKernel>();

    private int Cpu(string function, params object?[] args) => Call(Sysmem, function, args);

    private uint CpuValue(string function, params object?[] args) => Convert.ToUInt32(Invoke(Sysmem, function, args));

    private uint Word(uint value) => _harness.Word(value);

    private bool Masked => Sysmem.InterruptsMaskedForCurrentThread;

    /// <summary>Every FW 3.74 SceCpuForDriver / SceCpuForKernel export this batch covers.</summary>
    private static readonly uint[] s_cpuNids =
    [
        // SceCpuForDriver: caches, special atomics, the CPU id, the spinlocks and reader-writer spinlocks
        0x103872A5, 0x9CB9F0CE, 0x02796361, 0x8B4C26DF, 0x364E68A4, 0xE551F99B, 0xE813EBB2, 0xC3868071, 0x875B094D,
        0x26F71995, 0x5E4D5DE1, 0x5AC9D394, 0x27C0B340, 0xCAC9AE80, 0xEC53D007, 0xF5FD5676, 0x740A0750, 0x093925BD,
        0xF02467D1, 0x4F7790B4, 0xDE6482C6, 0x3F42B434, 0xCB8ABDF0,
        // SceCpuForKernel: L1 caches, branch predictor, registers, core lock, user lock words, 64-bit atomics
        0x70350360, 0x4236E06A, 0x74B7F445, 0x7ED856FE, 0x597426D2, 0x49180814, 0xF9B0B171, 0x4BBA5C82, 0x15A48AB4,
        0x803C84BF, 0x050B8EF6, 0x7B0643E2, 0xA0A70166, 0xB331AB16, 0xC32687D0, 0xC6426313, 0xFD54A91D, 0x9818A20B,
        0xA65F6F14, 0x2FF54320, 0xFE2B77A3, 0x75A85059, 0xFE9ED689, 0x9B071FD3, 0x8F40D0FD, 0x9DA58A9E, 0x0E04BD2E,
        0x59448055, 0x1A62AD6A, 0xA12D4388, 0x61DDA758, 0x3D4FBB32, 0xA3D7140F,
    ];

    [Fact]
    public void Fw374CpuNidsAreImplemented()
    {
        var missing = s_cpuNids.Where(n => !_harness.Emu.Hle.IsImplemented(n)).Select(n => $"0x{n:X8}").ToList();
        Assert.True(missing.Count == 0, string.Join(", ", missing));
    }

    [Fact]
    public void SpinlockCallsByTheirFw374NidsTakeTheLockAndMaskInterrupts()
    {
        SceModuleBuilder builder = new SceModuleBuilder("CpuClient")
            .Import("SceCpuForDriver", CpuForDriver, SpinlockLowLockCpuSuspendIntr, SpinlockLowUnlockCpuResumeIntr,
                WriteLockCpuSuspendIntr, TryReadLockCpuSuspendIntr, CpuSuspendIntr, CpuResumeIntr);
        int[] calls = Enumerable.Range(0, 6).Select(i => builder.CallImport(0, i)).ToArray();
        byte[] elf = builder.Export("CpuClientLib", 0x0BADC9A0, 0x0001, (0x37400002, calls[0])).Build();
        string dir = Path.Combine(_harness.Root, "ur0", "tai");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "cpu.skprx"), elf);
        LoadedModule module = _harness.Emu.LoadModule("ur0:tai/cpu.skprx", kernel: true);
        uint codeBase = module.Segments[0].Address;
        uint Fn(int call) => SceModuleBuilder.FunctionAddress(codeBase, calls[call]);

        uint spin = Word(0);
        uint rw = Word(0);
        _harness.OnGuest(thread =>
        {
            // 0xD32ACE9E is SpinlockLowLockCpuSuspendIntr: the word gets the caller's return address
            Assert.Equal(0u, thread.CallGuest(Fn(0), spin));
            Assert.NotEqual(0u, GuestMemory.R32(spin));
            Assert.True(Masked);
            thread.CallGuest(Fn(1), spin, 0);
            Assert.Equal(0u, GuestMemory.R32(spin));
            Assert.False(Masked);

            // 0x4C38CE4D is a reader-writer write lock: 0x80000000, and a reader is refused while it is held
            Assert.Equal(0u, thread.CallGuest(Fn(2), rw));
            Assert.Equal(0x80000000u, GuestMemory.R32(rw));
            Assert.Equal(0xFFFFFFFFu, thread.CallGuest(Fn(3), rw));
            Assert.Equal(0x80000000u, GuestMemory.R32(rw));

            // 0x821FC0EE / 0xF5BAD43B are the plain suspend / resume
            Assert.Equal(0x80u, thread.CallGuest(Fn(4)));
            thread.CallGuest(Fn(5), 0u);
            Assert.False(Masked);
        });
    }

    [Fact]
    public void ResumeLooksAtBit7AndUnlockResumeAtTheWholeValue()
    {
        _harness.OnGuest(_ =>
        {
            Assert.Equal(0, Cpu("ksceKernelCpuSuspendIntr"));
            Assert.Equal(0x80, Cpu("ksceKernelCpuSuspendIntr"));
            // a saved value with bit 7 set leaves interrupts masked; the result is the value shifted by 8 (LSRS)
            Assert.Equal(0x12, Cpu("ksceKernelCpuResumeIntr", 0x1280));
            Assert.True(Masked);
            Assert.Equal(0x12, Cpu("ksceKernelCpuResumeIntr", 0x1200));
            Assert.False(Masked);

            // the unlock-and-resume functions test the whole value: 1 keeps interrupts masked
            uint lockWord = Word(0);
            Assert.Equal(0, Cpu("ksceKernelRWSpinlockLowReadLockCpuSuspendIntr", lockWord));
            Invoke(Sysmem, "ksceKernelRWSpinlockLowReadUnlockCpuResumeIntr", lockWord, 1);
            Assert.True(Masked);
            Assert.Equal(0u, GuestMemory.R32(lockWord));
            Assert.Equal(0x80, Cpu("ksceKernelRWSpinlockLowReadLockCpuSuspendIntr", lockWord));
            Invoke(Sysmem, "ksceKernelRWSpinlockLowReadUnlockCpuResumeIntr", lockWord, 0);
            Assert.False(Masked);
        });
    }

    [Fact]
    public void TryLocksReturnMinusOneAndPutTheInterruptStateBack()
    {
        uint lockWord = Word(0x81000001);
        _harness.OnGuest(t =>
        {
            unsafe
            {
                t.Core.State->R[0] = lockWord;
                t.Core.State->R[14] = 0x81002001;
            }

            Assert.Equal(-1, Cpu("ksceKernelSpinlockLowTrylock", t.Hle));
            Assert.Equal(-1, Cpu("ksceKernelSpinlockLowTrylockCpuSuspendIntr", t.Hle));
            Assert.False(Masked);
            Assert.Equal(-1, Cpu("ksceKernelRWSpinlockLowTryWriteLockCpuSuspendIntr", lockWord));
            Assert.False(Masked);

            // already masked: the failed try leaves it masked
            Assert.Equal(0, Cpu("ksceKernelCpuSuspendIntr"));
            Assert.Equal(-1, Cpu("ksceKernelSpinlockLowTrylockCpuSuspendIntr", t.Hle));
            Assert.True(Masked);
            Cpu("ksceKernelCpuResumeIntr", 0);

            GuestMemory.W32(lockWord, 0);
            Assert.Equal(0, Cpu("ksceKernelSpinlockLowTrylockCpuSuspendIntr", t.Hle));
            Assert.Equal(0x81002001u, GuestMemory.R32(lockWord));
            Assert.True(Masked);
            Invoke(Sysmem, "ksceKernelSpinlockLowUnlockCpuResumeIntr", lockWord, 0);
            Assert.Equal(0, Cpu("ksceKernelSpinlockLowTrylock", t.Hle));
            Invoke(Sysmem, "ksceKernelSpinlockLowUnlock", lockWord);
            Assert.Equal(0u, GuestMemory.R32(lockWord));
        });
    }

    [Fact]
    public void ReaderWriterSpinlocksCountReadersAndKeepWritersOut()
    {
        uint lockWord = Word(0);
        _harness.OnGuest(_ =>
        {
            Invoke(Sysmem, "ksceKernelRWSpinlockLowReadLock", lockWord);
            Invoke(Sysmem, "ksceKernelRWSpinlockLowReadLock", lockWord);
            Assert.Equal(2u, GuestMemory.R32(lockWord));
            Assert.Equal(-1, Cpu("ksceKernelRWSpinlockLowTryWriteLock", lockWord));
            Invoke(Sysmem, "ksceKernelRWSpinlockLowReadUnlock", lockWord);
            Invoke(Sysmem, "ksceKernelRWSpinlockLowReadUnlock", lockWord);
            Assert.Equal(0, Cpu("ksceKernelRWSpinlockLowTryWriteLock", lockWord));
            Assert.Equal(0x80000000u, GuestMemory.R32(lockWord));
            Assert.Equal(-1, Cpu("ksceKernelRWSpinlockLowTryReadLockCpuSuspendIntr", lockWord));
            Assert.False(Masked);
            Invoke(Sysmem, "ksceKernelRWSpinlockLowWriteUnlock", lockWord);
            Assert.Equal(0, Cpu("ksceKernelRWSpinlockLowTryReadLockCpuSuspendIntr", lockWord));
            Assert.Equal(1u, GuestMemory.R32(lockWord));
            Invoke(Sysmem, "ksceKernelRWSpinlockLowReadUnlockCpuResumeIntr", lockWord, 0);
        });
    }

    [Fact]
    public void TryReadLockEndsWhereTheFirmwaresLoopEnds()
    {
        // FW 3.74 loops while the new count is not 0: a free word ends at 0x7FFFFFFF with -1, a word of -1 at 0 with 0
        uint lockWord = Word(0);
        Assert.Equal(-1, Cpu("ksceKernelRWSpinlockLowTryReadLock", lockWord));
        Assert.Equal(0x7FFFFFFFu, GuestMemory.R32(lockWord));
        GuestMemory.W32(lockWord, 0xFFFFFFFF);
        Assert.Equal(0, Cpu("ksceKernelRWSpinlockLowTryReadLock", lockWord));
        Assert.Equal(0u, GuestMemory.R32(lockWord));
        GuestMemory.W32(lockWord, 0x80000000);
        Assert.Equal(-1, Cpu("ksceKernelRWSpinlockLowTryReadLock", lockWord));
        Assert.Equal(0x80000000u, GuestMemory.R32(lockWord));
    }

    [Fact]
    public void AReaderWaitsForTheWriterToUnlock()
    {
        // the order is forced by the lock word itself: the reader can only get in after the writer stored 0, and the
        // writer marks its release before it
        uint lockWord = Word(0);
        int released = 0;
        Assert.Equal(0, Cpu("ksceKernelRWSpinlockLowTryWriteLock", lockWord));
        GuestRun reader = _harness.Start(_ =>
        {
            Assert.Equal(0, Cpu("ksceKernelRWSpinlockLowReadLockCpuSuspendIntr", lockWord));
            Assert.Equal(1, Volatile.Read(ref released));
            Assert.Equal(1u, GuestMemory.R32(lockWord));
            Invoke(Sysmem, "ksceKernelRWSpinlockLowReadUnlockCpuResumeIntr", lockWord, 0);
        }, "reader");
        Volatile.Write(ref released, 1);
        Invoke(Sysmem, "ksceKernelRWSpinlockLowWriteUnlock", lockWord);
        _harness.Join(reader);
        Assert.Equal(0u, GuestMemory.R32(lockWord));
    }

    [Fact]
    public void LockWordsOutsideMappedMemoryFault()
    {
        Assert.Throws<GuestFaultException>(() => Cpu("ksceKernelRWSpinlockLowTryWriteLock", 0u));
        Assert.Throws<GuestFaultException>(() => Cpu("ksceKernelRWSpinlockLowTryWriteLock", Word(0) + 2));
        Assert.Throws<GuestFaultException>(() => Invoke(Sysmem, "ksceKernelSpinlockLowUnlock", 0u));
    }

    [Fact]
    public void SetIfGreaterComparesSignedAndReturnsTheLarger()
    {
        uint word = Word(0x00008080);
        // byte at +0 is -128: 5 is greater; halfword at +2 is 0
        Assert.Equal(5, Cpu("ksceKernelAtomicSetIfGreaterGet8", word, 5));
        Assert.Equal(0x00008005u, GuestMemory.R32(word));
        Assert.Equal(5, Cpu("ksceKernelAtomicSetIfGreaterGet8", word, -3));
        Assert.Equal(0, Cpu("ksceKernelAtomicSetIfGreaterGet16", word + 2, -1));
        Assert.Equal(0x1234, Cpu("ksceKernelAtomicSetIfGreaterGet16", word + 2, 0x1234));
        Assert.Equal(0x12348005u, GuestMemory.R32(word));
        uint word32 = Word(unchecked((uint)-10));
        Assert.Equal(-10, Cpu("ksceKernelAtomicSetIfGreaterGet32", word32, -20));
        Assert.Equal(7, Cpu("ksceKernelAtomicSetIfGreaterGet32", word32, 7));
        Assert.Equal(7u, GuestMemory.R32(word32));
    }

    [Fact]
    public void RangeAtomicsFollowTheFirmware()
    {
        uint word = Word(unchecked((uint)-1));
        Assert.Equal(2, Cpu("ksceKernelAtomicAddAndGetPositive32InRange", word, 1, 10));
        Assert.Equal(0xFFFFFFFFu, GuestMemory.R32(word));
        GuestMemory.W32(word, 5);
        Assert.Equal(0, Cpu("ksceKernelAtomicAddAndGetPositive32InRange", word, 4, 10));
        Assert.Equal(9u, GuestMemory.R32(word));
        Assert.Equal(1, Cpu("ksceKernelAtomicAddAndGetPositive32InRange", word, 4, 10));
        Assert.Equal(10u, GuestMemory.R32(word));

        // high water counter: the high half follows the low half when it passes it
        GuestMemory.W32(word, 0x00020002);
        Assert.Equal(0x00030003u, CpuValue("ksceKernelAtomicIncrementHighwaterCounter", word));
        GuestMemory.W32(word, 0x00050001);
        Assert.Equal(0x00050002u, CpuValue("ksceKernelAtomicIncrementHighwaterCounter", word));

        GuestMemory.W32(word, 0x00070000);
        Assert.Equal(0x00070000u, CpuValue("ksceKernelAtomicDecIfLowPositive32", word));
        GuestMemory.W32(word, 0x00070002);
        Assert.Equal(0x00070001u, CpuValue("ksceKernelAtomicDecIfLowPositive32", word));
        Assert.Equal(0x00070001u, GuestMemory.R32(word));
    }

    [Fact]
    public void SixtyFourBitAtomicsFollowTheFirmware()
    {
        uint pair = _harness.Alloc(8);
        void Set(int low, int high)
        {
            GuestMemory.W32(pair, (uint)low);
            GuestMemory.W32(pair + 4, (uint)high);
        }

        (int, int) Get() => ((int)GuestMemory.R32(pair), (int)GuestMemory.R32(pair + 4));

        Set(1, 2);
        Invoke(Sysmem, "ksceKernelAtomicGet32AndSet64", pair, 9);
        Assert.Equal((9, 9), Get());
        Invoke(Sysmem, "ksceKernelAtomicGet32AndSet64Second", pair, -4);
        Assert.Equal((-4, -4), Get());

        Set(3, 100);
        Assert.Equal(0, Cpu("ksceKernelAtomicAdd32AndGet64InRange", pair, 4, 8));
        Assert.Equal((7, 100), Get());
        Assert.Equal(1, Cpu("ksceKernelAtomicAdd32AndGet64InRange", pair, 1, 8));
        Assert.Equal((7, 100), Get());

        // the high half is a high water mark of the low half
        Set(3, 4);
        Assert.Equal(0, Cpu("ksceKernelAtomicAdd32AndGet64InHiLoRange", pair, 3, 10));
        Assert.Equal((6, 6), Get());
        Assert.Equal(0, Cpu("ksceKernelAtomicAdd32AndGet64InHiLoRange", pair, -2, 10));
        Assert.Equal((4, 6), Get());

        Set(5, 9);
        Assert.Equal(1, Cpu("ksceKernelAtomicSubIfGreater64", pair, 6));
        Assert.Equal(0, Cpu("ksceKernelAtomicSubIfGreater64", pair, 5));
        Assert.Equal((0, 9), Get());

        // the high half is a low water mark of the low half
        Set(10, 8);
        Assert.Equal(0, Cpu("ksceKernelAtomicLimit64", pair, 3));
        Assert.Equal((7, 7), Get());
        Assert.Equal(1, Cpu("ksceKernelAtomicLimit64", pair, 8));
        Assert.Equal((7, 7), Get());
        Set(7, 2);
        Assert.Equal(0, Cpu("ksceKernelAtomicLimit64", pair, 1));
        Assert.Equal((6, 2), Get());

        Assert.Throws<GuestFaultException>(() => Cpu("ksceKernelAtomicSubIfGreater64", pair + 4, 1));
    }

    [Fact]
    public void L1RangeLoopsReturnTheirEndAndFaultOnUnmappedLines()
    {
        uint block = _harness.Alloc(0x1000);
        Assert.Equal(block + 0x40, CpuValue("ksceKernelL1DcacheCleanRange", block, 0x21u));
        Assert.Equal(block + 0x20, CpuValue("ksceKernelL1DcacheInvalidateRange", block, 0x20u));
        Assert.Equal(block + 0x5F, CpuValue("ksceKernelL1DcacheCleanInvalidateRange", block + 0x1F, 2u));
        // a length of 0 at the start of a line wraps the firmware's counter: the loop runs over the whole space
        Assert.Throws<GuestFaultException>(() => Cpu("ksceKernelL1DcacheCleanRange", block, 0u));
        Assert.Equal(block + 0x24, CpuValue("ksceKernelL1DcacheCleanRange", block + 4, 0u));
        Assert.Throws<GuestFaultException>(() => Invoke(Sysmem, "ksceKernelL1DcacheInvalidate", 0x10u));
        Invoke(Sysmem, "ksceKernelL1DcacheInvalidate", block);
        Invoke(Sysmem, "ksceKernelL1DcacheCleanAll");
    }

    [Fact]
    public void DcacheRangesSkipTheUncachedRegionsOnlyWhenTheyReachTheOuterCache()
    {
        uint block = _harness.Alloc(0x1000);
        Invoke(Sysmem, "ksceKernelDcacheCleanRange", block, 0x100u);
        Invoke(Sysmem, "ksceKernelDcacheCleanRangeForL1WBWA", block, 0x100u);
        // length 0: nothing, even unmapped
        Invoke(Sysmem, "ksceKernelDcacheInvalidateRange", 0x10u, 0u);
        // the outer variants leave [0x60000000, 0x81000000) and [0x20000000, 0x40000000) alone
        foreach (uint skipped in new uint[] { 0x60000000, 0x80FFF000, 0x20000000 })
        {
            if (!KernelSpace.IsMappedRange(skipped, 0x40))
            {
                Invoke(Sysmem, "ksceKernelDcacheCleanInvalidateRange", skipped, 0x40u);
                Assert.Throws<GuestFaultException>(() => Invoke(Sysmem, "ksceKernelDcacheCleanInvalidateRangeForL1WBWA", skipped, 0x40u));
            }
        }

        Assert.Throws<GuestFaultException>(() => Invoke(Sysmem, "ksceKernelDcacheCleanRange", 0x10u, 0x40u));
        Assert.Throws<GuestFaultException>(() => Invoke(Sysmem, "ksceKernelDcacheInvalidateRangeForL1WBWA", 0x10u, 0x40u));
    }

    [Fact]
    public void InstructionCacheOperationsDropTheTranslatedCode()
    {
        // a Thumb function returning 1, run, rewritten: each icache operation makes the new code run
        uint code = _harness.Alloc(0x100);
        GuestMemory.Write(code, (ushort)0x2001);
        GuestMemory.Write(code + 2, (ushort)0x4770);
        _harness.OnGuest(t =>
        {
            Assert.Equal(1u, t.CallGuest(code | 1));
            GuestMemory.Write(code, (ushort)0x2002);
            Assert.Equal(code + 0x20, CpuValue("ksceKernelL1IcacheInvalidateRange", code, 4u));
            Assert.Equal(2u, t.CallGuest(code | 1));
            GuestMemory.Write(code, (ushort)0x2003);
            Invoke(Sysmem, "ksceKernelIcacheInvalidateRange", code, 4u);
            Assert.Equal(3u, t.CallGuest(code | 1));
            GuestMemory.Write(code, (ushort)0x2004);
            Assert.Equal(0, Cpu("ksceKernelL1IcacheInvalidateEntire"));
            Assert.Equal(4u, t.CallGuest(code | 1));
            GuestMemory.Write(code, (ushort)0x2005);
            Assert.Equal(0, Cpu("ksceKernelL1IcacheInvalidateEntireAllCore"));
            Assert.Equal(5u, t.CallGuest(code | 1));
        });
        Assert.Throws<GuestFaultException>(() => Invoke(Sysmem, "ksceKernelIcacheInvalidateRange", 0x10u, 4u));
        Invoke(Sysmem, "ksceKernelIcacheInvalidateRange", 0x10u, 0u);
    }

    [Fact]
    public void CpuRegistersAndConstants()
    {
        Assert.Equal(0x00C5187Fu, CpuValue("ksceKernelCpuUpdateSCTLR"));
        Assert.Equal(0x00C5187Fu, CpuValue("ksceKernelCpuUpdateSCTLR"));
        Assert.Equal(0x8100004Au, CpuValue("ksceKernelCpuTtbrWithAttributes", 0x81000000u));
        Assert.Equal(0, Cpu("ksceKernelCpuBranchPredictorInvalidateAll"));
        uint start = Word(0xFFFFFFFF);
        uint end = Word(0xFFFFFFFF);
        Invoke(Sysmem, "ksceKernelGetVmaccessRange", start, end);
        Assert.Equal(GuestMemory.R32(start), GuestMemory.R32(end));
        _harness.OnGuest(_ =>
        {
            int asid = Cpu("ksceKernelCpuGetCONTEXTIDR");
            Assert.InRange(asid, 1, 0xFF);
            Assert.Equal(asid, Cpu("ksceKernelCpuGetCONTEXTIDR"));
            using (ProcessScope.Enter(KernelSpace.KernelPid))
            {
                Assert.Equal(0, Cpu("ksceKernelCpuGetCONTEXTIDR"));
            }
        });
    }

    [Fact]
    public void CpuIdIsTheUserCallsCore()
    {
        KernelTwins twins = _harness.Module<KernelTwins>();
        SysMem sysMem = _harness.Module<SysMem>();
        _harness.OnGuest(t =>
        {
            t.Affinity = 0x40000;
            Invoke(twins, "ksceKernelCpuId", t.Hle);
            unsafe
            {
                Assert.Equal(2u, t.Core.State->R[0]);
            }

            Assert.Equal(2, sysMem.sceKernelGetCpuId());
        });
    }

    [Fact]
    public void CoreLockLetsTheFirstCoreInAndTheOthersInTurn()
    {
        uint corelock = _harness.Alloc(8);
        Invoke(Sysmem, "ksceKernelCorelockInitialize", corelock);
        Assert.Equal(1u, GuestMemory.R32(corelock));
        Assert.Equal(0x00040004u, GuestMemory.R32(corelock + 4));
        _harness.OnGuest(t =>
        {
            // the first core goes in without the spinlock and takes the turn
            t.Affinity = 0x10000;
            unsafe
            {
                t.Core.State->R[0] = corelock;
                t.Core.State->R[1] = 0;
                t.Core.State->R[14] = 0x81003001;
            }

            Invoke(Sysmem, "ksceKernelCorelockLock", t.Hle);
            Assert.Equal(0u, (uint)GuestMemory.Read<ushort>(corelock + 6));
            Assert.Equal(1u, GuestMemory.R32(corelock));
        });

        // core 1 waits for its turn, then takes the spinlock its predecessor released
        GuestRun second = _harness.Start(t =>
        {
            t.Affinity = 0x20000;
            unsafe
            {
                t.Core.State->R[0] = corelock;
                t.Core.State->R[1] = 0;
                t.Core.State->R[14] = 0x81003101;
            }

            Invoke(Sysmem, "ksceKernelCorelockLock", t.Hle);
            Assert.Equal(1u, (uint)GuestMemory.Read<ushort>(corelock + 6));
        }, "core1");
        GuestMemory.W32(corelock, 0);
        GuestMemory.Write(corelock + 6, (ushort)1);
        _harness.Join(second);
        Assert.Equal(0x81003101u, GuestMemory.R32(corelock));
    }

    [Fact]
    public void UserLockWordsAreUserAccesses()
    {
        uint word = _harness.Alloc(16);
        GuestMemory.Fill(word, 0, 16);
        Assert.Equal(0, Cpu("ksceKernelUserLockWordTryLock", word, 0x40010003));
        Assert.Equal(0x40010003u, GuestMemory.R32(word));
        Assert.Equal(1, Cpu("ksceKernelUserLockWordTryLock", word, 0x40010005));
        Assert.Equal(1, Cpu("ksceKernelUserLockWordTryLock", word, 0x40010005));
        Assert.Equal(0x40010003u, GuestMemory.R32(word));
        Assert.Equal(2u, GuestMemory.R32(word + 4));
        Assert.Equal(0, Cpu("ksceKernelUserLockWordSet", word, 7u, 8u, 9u));
        Assert.Equal((7u, 8u, 9u), (GuestMemory.R32(word), GuestMemory.R32(word + 4), GuestMemory.R32(word + 8)));
        Assert.Equal(0, Cpu("ksceKernelUserLockWordRelease", word));
        Assert.Equal((0u, 8u, 0u), (GuestMemory.R32(word), GuestMemory.R32(word + 4), GuestMemory.R32(word + 8)));

        // a kernel word is not a user access
        uint kernelWord;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            kernelWord = _harness.Emu.Memory.Alloc(16, "kcpu:kernel", MemoryRegion.Kernel);
        }

        Assert.Equal(unchecked((int)0xC0022005), Cpu("ksceKernelUserLockWordTryLock", kernelWord, 1));
        Assert.Equal(unchecked((int)0xC0022005), Cpu("ksceKernelUserLockWordRelease", kernelWord));
        Assert.Equal(unchecked((int)0xC0022005), Cpu("ksceKernelUserLockWordSet", kernelWord, 1u, 2u, 3u));
    }
}
