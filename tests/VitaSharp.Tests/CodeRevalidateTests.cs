using VitaSharp.Core.Cpu;
using VitaSharp.Core.Cpu.Backends;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>
/// Loading a save state with <see cref="EnvVars.LoadKeepCode"/> keeps the translations of the guest code the state did
/// not change (<see cref="BlockCache.Revalidate"/>) instead of recompiling everything (seconds of JIT work on the phone
/// after every load), and drops exactly the blocks whose code, or code their translation relied on, is different.
/// </summary>
[Collection("guest")]
public sealed unsafe class CodeRevalidateTests(GuestMemoryFixture fixture)
{
    [Fact]
    public void UnchangedBlocksStayAndChangedOnesAreDropped()
    {
        uint code = fixture.Memory.Alloc(0x1000, "revalidate");
        GuestMemory.W32(code + 0x000, 0xE2800001); // A (ARM): add r0, r0, #1
        GuestMemory.W32(code + 0x004, 0xE12FFF1E); //            bx lr
        GuestMemory.Write<ushort>(code + 0x100, 0x2001);     // B (Thumb): movs r0, #1
        GuestMemory.Write<ushort>(code + 0x102, 0xF04F);     //            mov.w r1, #2 (32-bit)
        GuestMemory.Write<ushort>(code + 0x104, 0x0102);
        GuestMemory.Write<ushort>(code + 0x106, 0x4770);     //            bx lr
        GuestMemory.W32(code + 0x200, 0xE2800002); // C (ARM): add r0, r0, #2
        GuestMemory.W32(code + 0x204, 0xE12FFF1E); //            bx lr
        GuestMemory.W32(code + 0x300, 0xE2800003); // D (ARM): add r0, r0, #3; bx lr (depends on C)
        GuestMemory.W32(code + 0x304, 0xE12FFF1E);
        GuestMemory.W32(code + 0x400, 0xE2800004); // E (ARM): add r0, r0, #4; bx lr (depends on A)
        GuestMemory.W32(code + 0x404, 0xE12FFF1E);
        var cache = new BlockCache();
        Block a = cache.Get(code, false), b = cache.Get(code + 0x100, true), c = cache.Get(code + 0x200, false);
        Block d = cache.Get(code + 0x300, false), e = cache.Get(code + 0x400, false);
        d._deps = [c.Start, c.End];
        e._deps = [a.Start, a.End];

        // the same code: everything stays
        Assert.Equal((5, 0), cache.Revalidate());
        Assert.Same(a, cache.Get(code, false));
        Assert.Same(b, cache.Get(code + 0x100, true));

        // the state holds other code at C (and the second half of B's 32-bit instruction): C, B and D (which relied on C)
        // are dropped, A and E stay
        GuestMemory.W32(code + 0x200, 0xE2800005);
        GuestMemory.Write<ushort>(code + 0x104, 0x0103);
        Assert.Equal((2, 3), cache.Revalidate());
        Assert.Same(a, cache.Get(code, false));
        Assert.Same(e, cache.Get(code + 0x400, false));
        Assert.True(b.Invalidated && c.Invalidated && d.Invalidated);
        Assert.False(a.Invalidated || e.Invalidated);
        Block c2 = cache.Get(code + 0x200, false);
        Assert.NotSame(c, c2);
        Assert.Equal(0xE2800005u, c2.Insts[0].Raw);
    }

    [Fact]
    public void KeptNativeCodeRunsAndChangedCodeRunsAsRewritten()
    {
        if (!NativeX64Compiler.Instance.IsSupported)
        {
            return; // native code runs in this dispatcher only on an x64 host (the ARM64 emitter has its sandbox tests)
        }

        uint code = fixture.Memory.Alloc(0x100, "revalidate native");
        GuestMemory.W32(code + 0x00, 0xE3A01064); // mov r1, #100
        GuestMemory.W32(code + 0x04, 0xE2800003); // loop: add r0, r0, #3
        GuestMemory.W32(code + 0x08, 0xE2511001); //       subs r1, r1, #1
        GuestMemory.W32(code + 0x0C, 0x1AFFFFFC); //       bne loop
        GuestMemory.W32(code + 0x10, 0xEF000002); //       svc #2
        long oldHot = NativeX64Compiler.Instance.HotInstructions;
        string oldMode = Jit.Mode;
        bool oldTurbo = GuestClock.Turbo;
        NativeX64Compiler.Instance.HotInstructions = 1;
        GuestClock.Turbo = true;
        using JitTestState.Scope jitOn = JitTestState.Enable();
        try
        {
            Assert.True(Jit.Select("native"));
            var cache = new BlockCache();
            using var core = new CpuCore(cache);
            CpuState* s = core.State;
            uint Run()
            {
                s->R[0] = 0;
                s->R[15] = code;
                s->Thumb = 0;
                Assert.Equal(ExitReason.Svc, core.Run());
                return s->R[0];
            }

            Assert.Equal(300u, Run());
            Assert.True(Jit.WaitIdle(TimeSpan.FromSeconds(60)));
            Block loop = cache.Get(code + 4, false);
            nint entry = loop.NativeEntry;
            Assert.NotEqual(0, entry);

            // a load with the same code keeps the compiled loop, which runs again (no recompilation)
            Assert.Equal(0, cache.Revalidate().Dropped);
            Assert.Equal(entry, cache.Get(code + 4, false).NativeEntry);
            core.FlushFastCache();
            Assert.Equal(300u, Run());
            Assert.Equal(entry, cache.Get(code + 4, false).NativeEntry);

            // a load with another loop body: the old translation must not run
            GuestMemory.W32(code + 0x04, 0xE2800007); // add r0, r0, #7
            Assert.True(cache.Revalidate().Dropped >= 1);
            core.FlushFastCache();
            Assert.Equal(700u, Run());
            Assert.True(loop.Invalidated);
        }
        finally
        {
            NativeX64Compiler.Instance.HotInstructions = oldHot;
            GuestClock.Turbo = oldTurbo;
            Jit.Select(oldMode);
        }
    }
}
