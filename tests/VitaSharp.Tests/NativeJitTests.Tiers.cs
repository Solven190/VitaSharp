using VitaSharp.Core.Cpu;
using VitaSharp.Core.Cpu.Backends;
using VitaSharp.Core.Cpu.Native;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// The CPU tiers line (<see cref="CpuCore.TierReport"/>): guest code that ran in native code is counted in the native
/// tier whatever the generated code counts itself. ARM64 blocks keep only the cycle budget by default
/// (<see cref="Arm64Emitter.A64Opt.NoBlockCounts"/>), and counting the tier from their instruction count read
/// "native 0,00%, interpreter 100,00%" on every phone run while thousands of native blocks ran.
/// </summary>
public sealed unsafe partial class NativeJitTests
{
    /// <summary>A budget-only ARM64 loop (the phone's trampoline) run ten times: its cycles are all in the native
    /// tier, and the report says 100% native.</summary>
    [Arm64SandboxFact]
    public void Arm64BudgetOnlyBlocksCountInNativeTier()
    {
        if (Arm64Runner.Native)
        {
            return; // the switch changes the trampoline (sandbox only)
        }

        EnsureGuestPages();
        using var arena = new ArenaScope();
        // loop: add r0,r0,#3; str r0,[r1]; subs r2,r2,#1; bne loop; b .
        uint[] prog = [0xE2800003, 0xE5810000, 0xE2522001, 0x1AFFFFFB, 0xEAFFFFFE];
        prog.SelectMany(BitConverter.GetBytes).ToArray().CopyTo(GuestMemory.Span(CodeBase, 0x1000));
        using var sb = new Arm64Sandbox(countInstructions: false, noBlockCounts: true);
        Block b = BlockBuilder.Build(CodeBase, false);
        CompiledBlock cb = NativeArm64Compiler.Instance.Compile(b, sb.Stubs, successors: (pc, t) => pc == b.Start && !t ? b : null)!;
        using var core = new CpuCore(new BlockCache());
        CpuState* s = CpuState.Allocate();
        try
        {
            *s = default;
            s->MemBase = GuestMemory.Base;
            s->R[15] = CodeBase;
            s->R[1] = DataBase + 0x10;
            s->R[2] = 10;
            s->CycleAcc = 0;
            s->CheckWindow = 1L << 40;
            s->LinkBreak = 0;
            sb.Run(s, cb.NativeEntry); // one iteration: the back edge is not linked yet
            long sinceCheck = core.AccountNativeRun(s, 0);
            *(nint*)s->LastExitSlot = cb.NativeEntry;
            sb.Run(s, cb.NativeEntry);
            sinceCheck = core.AccountNativeRun(s, sinceCheck);
            *(nint*)s->LastExitSlot = 0;
            Assert.Equal(0u, s->R[2]);
            Assert.Equal(0, s->NInstr); // the budget-only code counts no instructions
            Assert.Equal(10 * b.Cycles, sinceCheck);
            Assert.Equal(10 * b.Cycles, core.NativeCycles);
            Assert.Equal(10 * b.Cycles, core.BlockCycles);
            Assert.Equal(0, core.IlCycles);
            Assert.Equal(0, core.InterpCycles);
            string line = CpuCore.FormatTierReport(core.NativeCycles, core.IlCycles, core.InterpCycles, 0, new long[4]);
            output.WriteLine(line);
            Assert.Contains($"native {100.0:F2}%", line);
            Assert.Contains($"interpreter {0.0:F2}%", line);
        }
        finally
        {
            CpuState.Free(s);
        }
    }

    /// <summary>
    /// A hot loop run through <see cref="CpuCore.Run"/> with the native backend (the host's own: x64 counts its
    /// instructions, ARM64 hosts run the budget-only default): once compiled and linked, nearly all of a run is in the
    /// native tier, and every executed cycle is in exactly one tier (native + il + interpreter = all block cycles).
    /// </summary>
    [NativeFact]
    public void HotLoopRunsInNativeTier()
    {
        EnsureGuestPages();
        // mov r0,#0; mov r1,#0; loop: add r0,r0,#3; add r1,r1,#1; cmp r1,#0x1000; blt loop; svc #0x42
        uint[] prog = [0xE3A00000, 0xE3A01000, 0xE2800003, 0xE2811001, 0xE3510A01, 0xBAFFFFFB, 0xEF000042];
        long oldRate = CpuCore.InstructionsPerSecond;
        long oldHot = NativeX64Compiler.Instance.HotInstructions;
        string oldMode = Jit.Mode;
        CpuCore.InstructionsPerSecond = 0;
        NativeX64Compiler.Instance.HotInstructions = 1;
        using JitTestState.Scope jitOn = JitTestState.Enable(); // other tests' emulators may have switched the JIT off
        try
        {
            Assert.True(Jit.Select("native"));
            Span<byte> code = GuestMemory.Span(CodeBase, 0x1000);
            code.Clear();
            prog.SelectMany(BitConverter.GetBytes).ToArray().CopyTo(code);
            var cache = new BlockCache();
            using var core = new CpuCore(cache);
            CpuState* s = core.State;
            for (int pass = 0; pass < 6; pass++)
            {
                long native0 = core.NativeCycles;
                long all0 = core.BlockCycles;
                s->R[15] = CodeBase;
                s->Thumb = 0;
                Assert.Equal(ExitReason.Svc, core.Run());
                Assert.Equal(3u * 0x1000, s->R[0]);
                Assert.Equal(core.BlockCycles, core.NativeCycles + core.IlCycles + core.InterpCycles);
                double nativeShare = (double)(core.NativeCycles - native0) / (core.BlockCycles - all0);
                output.WriteLine($"pass {pass}: native share {nativeShare:P2}");
                if (pass == 5)
                {
                    Assert.True(cache.Get(CodeBase + 8, false).NativeEntry != 0, "loop block was not compiled natively");
                    Assert.True(nativeShare > 0.9, $"native share {nativeShare:P2} of a compiled, linked loop");
                }
                WaitForJit();
            }
        }
        finally
        {
            CpuCore.InstructionsPerSecond = oldRate;
            NativeX64Compiler.Instance.HotInstructions = oldHot;
            Jit.Select(oldMode);
        }
    }
}
