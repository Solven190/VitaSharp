using System.Runtime.InteropServices;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Cpu.Backends;
using VitaSharp.Core.Memory;
using Xunit;
using Xunit.Abstractions;

namespace VitaSharp.Tests;

/// <summary>
/// The ARMv7-A exception modes of the emulated Cortex-A9 (ARM ARM B1.3, B1.8, B9): an exception entry banks R13/R14
/// and saves the CPSR in the mode's SPSR, and a kernel handler written like the firmware's and kubridge's (push on the
/// abort stack, MRS SPSR, MRC DFAR/DFSR, STM/LDM of the user registers with ^, SRS, CPS, RFE, SUBS PC, LR, LDM with ^
/// and PC) runs and returns to the interrupted user code with every user register as it was. Each sequence runs on
/// the interpreter, the IL backend and (x64 hosts) the native backend, which must agree. In user mode every one of
/// these encodings keeps the behaviour it had before (CPS a no-op, SRS/RFE undefined, the plain LDM and data
/// processing forms).
/// </summary>
[Collection("guest")]
public sealed unsafe class ExceptionModeTests(GuestMemoryFixture fx, ITestOutputHelper output)
{
    private const uint CodeBase = 0x0040_0000, DataBase = 0x0041_0000, DataSize = 0x4000;
    private const uint UserSp = DataBase + 0x3000, UserLr = 0x8123_4567, AbortSp = DataBase + 0x2000, SvcSp = DataBase + 0x1000;
    private const uint UndefinedSp = DataBase + 0x2800;
    private const uint Faulting = 0x0050_1232; // a Thumb instruction of the interrupted user code
    private const uint End = 0x0042_0000;       // the handlers branch here when they do not return (bx r8)

    public enum Backend
    {
        Interpreter,
        Il,
        Native,
    }

    public static TheoryData<Backend> Backends()
    {
        var data = new TheoryData<Backend> { Backend.Interpreter };
        if (RuntimeFeature())
        {
            data.Add(Backend.Il);
        }

        if (NativeJitAvailability.SkipReason == null)
        {
            data.Add(Backend.Native);
        }

        return data;
    }

    private static bool RuntimeFeature() => System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;

    private void EnsurePages()
    {
        if (fx.Memory.FindBlock(CodeBase) == null)
        {
            fx.Memory.MapFixed(CodeBase, 0x1000, "exception mode code");
        }

        if (fx.Memory.FindBlock(DataBase) == null)
        {
            fx.Memory.MapFixed(DataBase, DataSize, "exception mode data");
        }

        GuestMemory.Span(DataBase, (int)DataSize).Clear();
    }

    private static void WriteArm(uint address, params uint[] words)
    {
        for (int i = 0; i < words.Length; i++)
        {
            GuestMemory.W32(address + (uint)(4 * i), words[i]);
        }
    }

    /// <summary>Thumb code: 16-bit halfwords and 32-bit instructions (high halfword first).</summary>
    private static void WriteThumb(uint address, params uint[] instructions)
    {
        uint at = address;
        foreach (uint instruction in instructions)
        {
            if (instruction > 0xFFFF)
            {
                GuestMemory.Write(at, (ushort)(instruction >> 16));
                GuestMemory.Write(at + 2, (ushort)instruction);
                at += 4;
            }
            else
            {
                GuestMemory.Write(at, (ushort)instruction);
                at += 2;
            }
        }
    }

    /// <summary>A user-mode thread interrupted at <see cref="Faulting"/> (Thumb, flags N and C set, GE 0xA).</summary>
    private static CpuState* UserState()
    {
        CpuState* s = CpuState.Allocate();
        s->MemBase = GuestMemory.Base;
        for (int r = 0; r < 13; r++)
        {
            s->R[r] = 0x1000u + (uint)r;
        }

        s->R[13] = UserSp;
        s->R[14] = UserLr;
        s->R[15] = Faulting;
        s->Cpsr = 0xA00A_0030;
        s->BankedSp[ExceptionModes.Bank(ExceptionModes.Abort)] = AbortSp;
        s->BankedSp[ExceptionModes.Bank(ExceptionModes.Supervisor)] = SvcSp;
        s->BankedSp[ExceptionModes.Bank(ExceptionModes.Undefined)] = UndefinedSp;
        return s;
    }

    /// <summary>Runs blocks from the state's pc on <paramref name="backend"/> until the pc leaves the handler code
    /// (the return, or <see cref="End"/>) or an exit is raised.</summary>
    private void Run(CpuState* s, Backend backend)
    {
        for (int n = 0; n < 32; n++)
        {
            if (s->R[15] < CodeBase || s->R[15] >= CodeBase + 0x1000 || s->Exit != ExitReason.None)
            {
                return;
            }

            Block b = BlockBuilder.Build(s->R[15], s->Thumb != 0);
            output.WriteLine($"{backend}: block {b.Start:X8} ({b.Insts.Length} instructions, mode 0x{s->Mode:X2})");
            switch (backend)
            {
                case Backend.Interpreter:
                    CpuCore.Interpret(s, b);
                    break;
                case Backend.Il:
                    IlJitCompiler.Instance.Compile(b)!.Managed!(s);
                    break;
                default:
                    RunNative(s, NativeX64Compiler.Instance.Compile(b)!.NativeEntry);
                    break;
            }
        }

        Assert.Fail("the handler did not leave its code");
    }

    private static void RunNative(CpuState* s, nint entry)
    {
        s->CycleAcc = 0;
        s->CheckWindow = 0;
        s->LinkBreak = 1;
        s->NBlocks = 0;
        s->NInstr = 0;
        Core.Cpu.Native.NativeRuntime.Instance.Run(s, entry);
        if (s->CalloutFault != 0)
        {
            Core.Cpu.Native.NativeRuntime.ThrowPendingFault(s);
        }
    }

    private static void AssertUserStateRestored(CpuState* s)
    {
        Assert.Equal(ExceptionModes.User, s->Mode);
        Assert.Equal(1u, s->Thumb);
        Assert.Equal(UserSp, s->R[13]);
        Assert.Equal(UserLr, s->R[14]);
        Assert.Equal(0xA00A_0030u, s->Cpsr & 0xFFFF_FFFF);
        for (int r = 0; r < 13; r++)
        {
            Assert.Equal(0x1000u + (uint)r, s->R[r]);
        }
    }

    [Fact]
    public void EnteringAnAbortBanksTheStackAndLinkAndSavesTheCpsr()
    {
        CpuState* s = UserState();
        try
        {
            ExceptionModes.EnterException(s, ExceptionModes.Abort, Faulting + 8, CodeBase);
            Assert.Equal(ExceptionModes.Abort, s->Mode);
            Assert.Equal(AbortSp, s->R[13]);
            Assert.Equal(Faulting + 8, s->R[14]);
            Assert.Equal(CodeBase, s->R[15]);
            Assert.Equal(0u, s->Thumb);
            Assert.Equal(0xA00A_0030u, s->Spsr[ExceptionModes.Bank(ExceptionModes.Abort)]);
            // MRS of the CPSR shows abort mode with IRQs and asynchronous aborts masked
            Assert.Equal(0xA00A_0197u, s->Cpsr);
            Assert.Equal(UserSp, s->BankedSp[0]);
            Assert.Equal(UserLr, s->BankedLr[0]);
        }
        finally
        {
            CpuState.Free(s);
        }
    }

    /// <summary>The shape of kubridge's abort handler: context saved on the abort stack, the user SP/LR stored with
    /// STM ^, the SPSR and the fault registers read, then SUBS PC, LR, #8 back to the faulting instruction.</summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public void AbortHandlerReadsTheFaultAndReturnsWithSubsPcLr(Backend backend)
    {
        EnsurePages();
        uint record = DataBase + 0x100;
        WriteArm(CodeBase,
            0xE92D401F, // push {r0-r4, lr}
            0xE59F4020, // ldr r4, [pc, #0x20]  (record)
            0xE14F0000, // mrs r0, spsr
            0xEE161F10, // mrc p15, 0, r1, c6, c0, 0  (DFAR)
            0xEE152F10, // mrc p15, 0, r2, c5, c0, 0  (DFSR)
            0xE8C46000, // stmia r4, {sp, lr}^
            0xE5840008, // str r0, [r4, #8]
            0xE584100C, // str r1, [r4, #12]
            0xE5842010, // str r2, [r4, #16]
            0xE8BD401F, // pop {r0-r4, lr}
            0xE25EF008, // subs pc, lr, #8
            record);
        CpuState* s = UserState();
        try
        {
            s->Dfar = 0xDEAD_0000;
            s->Dfsr = 0x805;
            ExceptionModes.EnterException(s, ExceptionModes.Abort, Faulting + 8, CodeBase);
            Run(s, backend);
            AssertUserStateRestored(s);
            Assert.Equal(Faulting, s->R[15]);
            Assert.Equal(AbortSp, s->BankedSp[ExceptionModes.Bank(ExceptionModes.Abort)]); // push/pop balanced
            Assert.Equal(UserSp, GuestMemory.R32(record));
            Assert.Equal(UserLr, GuestMemory.R32(record + 4));
            Assert.Equal(0xA00A_0030u, GuestMemory.R32(record + 8));
            Assert.Equal(0xDEAD_0000u, GuestMemory.R32(record + 12));
            Assert.Equal(0x805u, GuestMemory.R32(record + 16));
        }
        finally
        {
            CpuState.Free(s);
        }
    }

    /// <summary>SRS onto the supervisor stack, CPS to supervisor mode, RFE back to user mode.</summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public void SrsCpsAndRfeMoveTheReturnStateBetweenModes(Backend backend)
    {
        EnsurePages();
        WriteArm(CodeBase,
            0xF96D0513, // srsdb sp!, #0x13
            0xF1020013, // cps #0x13
            0xF8BD0A00); // rfeia sp!
        CpuState* s = UserState();
        try
        {
            ExceptionModes.EnterException(s, ExceptionModes.Abort, Faulting, CodeBase);
            Run(s, backend);
            AssertUserStateRestored(s);
            Assert.Equal(Faulting, s->R[15]);
            Assert.Equal(SvcSp, s->BankedSp[ExceptionModes.Bank(ExceptionModes.Supervisor)]);
            Assert.Equal(Faulting, GuestMemory.R32(SvcSp - 8));
            Assert.Equal(0xA00A_0030u, GuestMemory.R32(SvcSp - 4));
        }
        finally
        {
            CpuState.Free(s);
        }
    }

    /// <summary>LDM with ^ and the PC in the list is an exception return (CPSR = SPSR).</summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public void LdmWithPcAndCaretReturnsFromTheException(Backend backend)
    {
        EnsurePages();
        WriteArm(CodeBase,
            0xE92D4001, // push {r0, lr}
            0xE3A00055, // mov r0, #0x55
            0xE8FD8001); // ldmia sp!, {r0, pc}^
        CpuState* s = UserState();
        try
        {
            ExceptionModes.EnterException(s, ExceptionModes.Undefined, Faulting + 2, CodeBase);
            Run(s, backend);
            AssertUserStateRestored(s);
            Assert.Equal(Faulting + 2, s->R[15]);
        }
        finally
        {
            CpuState.Free(s);
        }
    }

    /// <summary>MSR/MRS of the SPSR, and MSR CPSR_c switching to a mode whose banked SP the code then reads.</summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public void MsrAndMrsReachTheSpsrAndSwitchModes(Backend backend)
    {
        EnsurePages();
        WriteArm(CodeBase,
            0xE169F005, // msr spsr_fc, r5
            0xE14F6000, // mrs r6, spsr
            0xE321F0D3, // msr cpsr_c, #0xd3  (supervisor, I and F masked)
            0xE1A0700D, // mov r7, sp
            0xE10F9000, // mrs r9, cpsr
            0xE12FFF18); // bx r8
        CpuState* s = UserState();
        try
        {
            ExceptionModes.EnterException(s, ExceptionModes.Abort, Faulting, CodeBase);
            s->R[5] = 0x6000_01D0;
            s->R[8] = End;
            Run(s, backend);
            Assert.Equal(End, s->R[15]);
            // only the f and c bytes are written: the x byte (A) and the s byte (GE 0xA) of the saved user CPSR stay
            Assert.Equal(0x600A_00D0u, s->R[6]);
            Assert.Equal(0x600A_00D0u, s->Spsr[ExceptionModes.Bank(ExceptionModes.Abort)]);
            Assert.Equal(SvcSp, s->R[7]);
            Assert.Equal(ExceptionModes.Supervisor, s->Mode);
            Assert.Equal(0x1D3u, s->R[9] & 0x3FF); // A kept from the abort entry, I and F set, supervisor
            Assert.Equal(AbortSp, s->BankedSp[ExceptionModes.Bank(ExceptionModes.Abort)]);
        }
        finally
        {
            CpuState.Free(s);
        }
    }

    /// <summary>The Thumb-2 forms: SRSDB, CPS, RFEIA and SUBS PC, LR, #imm.</summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public void ThumbSrsCpsRfeAndSubsPcLrWork(Backend backend)
    {
        EnsurePages();
        WriteThumb(CodeBase,
            0xE82DC013, // srsdb sp!, #0x13
            0xF3AF8113, // cps #0x13
            0xE9BDC000); // rfeia sp!
        WriteThumb(CodeBase + 0x100,
            0xF3DE8F08); // subs pc, lr, #8
        CpuState* s = UserState();
        try
        {
            ExceptionModes.EnterException(s, ExceptionModes.Abort, Faulting, CodeBase);
            s->Thumb = 1;
            Run(s, backend);
            AssertUserStateRestored(s);
            Assert.Equal(Faulting, s->R[15]);

            ExceptionModes.EnterException(s, ExceptionModes.Abort, Faulting + 8, CodeBase + 0x100);
            s->Thumb = 1;
            Run(s, backend);
            AssertUserStateRestored(s);
            Assert.Equal(Faulting, s->R[15]);
        }
        finally
        {
            CpuState.Free(s);
        }
    }

    /// <summary>In user mode the privileged encodings do what they did before exception modes existed.</summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public void UserModeKeepsThePlainBehaviour(Backend backend)
    {
        EnsurePages();
        WriteArm(CodeBase,
            0xF1020013, // cps #0x13: no-op
            0xE321F0D3, // msr cpsr_c, #0xd3: the control byte is not written
            0xE10F9000, // mrs r9, cpsr
            0xE1B0F00E); // movs pc, lr: a plain branch (no SPSR)
        CpuState* s = UserState();
        try
        {
            s->Thumb = 0;
            s->R[15] = CodeBase;
            s->R[14] = End;
            Run(s, backend);
            Assert.Equal(ExceptionModes.User, s->Mode);
            Assert.Equal(End, s->R[15]);
            Assert.Equal(0xA00A_0010u, s->R[9]);
            Assert.Equal(UserSp, s->R[13]);

            WriteArm(CodeBase + 0x200, 0xF8BD0A00); // rfeia sp!: undefined in user mode
            s->R[15] = CodeBase + 0x200;
            Run(s, backend);
            Assert.Equal(ExitReason.Undefined, s->Exit);
            Assert.Equal(CodeBase + 0x200, s->SvcPc);
        }
        finally
        {
            CpuState.Free(s);
        }
    }

    [Fact]
    public void CpuStateLayoutKeepsTheFaultRegistersTogether()
    {
        int dfsr = (int)Marshal.OffsetOf<CpuState>(nameof(CpuState.Dfsr));
        Assert.Equal(dfsr + 4, (int)Marshal.OffsetOf<CpuState>(nameof(CpuState.Dfar)));
        Assert.Equal(dfsr + 8, (int)Marshal.OffsetOf<CpuState>(nameof(CpuState.Ifsr)));
        Assert.Equal(dfsr + 12, (int)Marshal.OffsetOf<CpuState>(nameof(CpuState.Ifar)));
        Assert.Equal(dfsr + 16, (int)Marshal.OffsetOf<CpuState>(nameof(CpuState.Adfsr)));
        Assert.Equal(dfsr + 20, (int)Marshal.OffsetOf<CpuState>(nameof(CpuState.Aifsr)));
    }
}
