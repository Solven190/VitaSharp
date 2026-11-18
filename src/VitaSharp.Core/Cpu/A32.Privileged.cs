using System.Numerics;
using static VitaSharp.Core.Cpu.Arm;

namespace VitaSharp.Core.Cpu;

/// <summary>
/// The privileged ARM instructions kernel exception handlers use (ARM ARM B9): CPS, SRS, RFE, the exception returns
/// (SUBS PC, LR / MOVS PC, LR and LDM with ^ and PC) and LDM/STM of the user registers. In user mode each one does what
/// its encoding did before exception modes existed (CPS is a no-op, SRS/RFE are undefined, the others run as their
/// plain forms), so user code sees no change. They run in every backend through these handlers (the native backend
/// calls them out, the IL backend calls them).
/// </summary>
internal static unsafe partial class A32
{
    /// <summary>CPS{IE,ID} {a}{i}{f} {, #mode}: 1111 0001 0000 imod M 0 0000 000 A I F 0 mode.</summary>
    public static void Cps(CpuState* state, uint ins, uint pc)
    {
        ChangeProcessorState(state, Bits(ins, 19, 18), Bit(ins, 17) != 0, Bits(ins, 8, 6), ins & 0x1F);
    }

    /// <summary>The CPS operation: imod 2 clears the masks named by <paramref name="aif"/> (A, I, F as bits 2..0), 3
    /// sets them; <paramref name="changeMode"/> switches to <paramref name="mode"/>. A no-op in user mode.</summary>
    internal static void ChangeProcessorState(CpuState* state, uint imod, bool changeMode, uint aif, uint mode)
    {
        if (!ExceptionModes.IsPrivileged(state))
        {
            return;
        }

        uint masks = aif << 6;
        uint control = state->Control;
        if (imod == 2)
        {
            control &= ~masks;
        }
        else if (imod == 3)
        {
            control |= masks;
        }

        state->ControlXor = control ^ ExceptionModes.User;
        if (changeMode)
        {
            ExceptionModes.SwitchMode(state, mode);
        }
    }

    /// <summary>SRS{DA,IA,DB,IB} SP{!}, #mode: 1111 100P U1W0 1101 0000 0101 000 mode.</summary>
    public static void Srs(CpuState* state, uint ins, uint pc)
    {
        if (!ExceptionModes.HasSpsr(state))
        {
            Udf(state, ins, pc);
            return;
        }

        StoreReturnState(state, ins & 0x1F, Bit(ins, 24) != 0, Bit(ins, 23) != 0, Bit(ins, 21) != 0);
    }

    /// <summary>The SRS operation: LR and SPSR of the current mode stored on the stack of <paramref name="mode"/>.</summary>
    internal static void StoreReturnState(CpuState* state, uint mode, bool before, bool up, bool writeback)
    {
        ref uint sp = ref ModeSp(state, mode);
        uint address = TransferStart(sp, 2, before, up);
        Mem.W32(state, address, state->R[14]);
        Mem.W32(state, address + 4, ExceptionModes.ReadSpsr(state));
        if (writeback)
        {
            sp = up ? sp + 8 : sp - 8;
        }
    }

    /// <summary>RFE{DA,IA,DB,IB} Rn{!}: 1111 100P U0W1 Rn 0000 1010 0000 0000.</summary>
    public static void Rfe(CpuState* state, uint ins, uint pc)
    {
        if (!ExceptionModes.IsPrivileged(state))
        {
            Udf(state, ins, pc);
            return;
        }

        ReturnFromExceptionState(state, Bits(ins, 19, 16), Bit(ins, 24) != 0, Bit(ins, 23) != 0, Bit(ins, 21) != 0);
    }

    /// <summary>The RFE operation: PC and CPSR loaded from [Rn] (two words), Rn written back in the current mode.</summary>
    internal static void ReturnFromExceptionState(CpuState* state, uint rn, bool before, bool up, bool writeback)
    {
        uint baseValue = state->R[rn];
        uint address = TransferStart(baseValue, 2, before, up);
        uint target = Mem.R32(state, address);
        uint status = Mem.R32(state, address + 4);
        if (writeback)
        {
            state->R[rn] = up ? baseValue + 8 : baseValue - 8;
        }

        ExceptionModes.WriteCpsr(state, status);
        state->R[15] = state->Thumb != 0 ? target & ~1u : target & ~3u;
    }

    /// <summary>The first address of a block transfer of <paramref name="count"/> words (IA, IB, DA, DB).</summary>
    private static uint TransferStart(uint baseValue, uint count, bool before, bool up)
    {
        if (up)
        {
            return before ? baseValue + 4 : baseValue;
        }

        return before ? baseValue - 4 * count : baseValue - 4 * count + 4;
    }

    /// <summary>R13 of <paramref name="mode"/>: the running one, or its bank.</summary>
    private static ref uint ModeSp(CpuState* state, uint mode)
    {
        int bank = ExceptionModes.Bank(mode);
        if (bank < 0 || bank == ExceptionModes.Bank(state->Mode))
        {
            return ref state->R[13];
        }

        return ref state->BankedSp[bank];
    }

    /// <summary>
    /// A data-processing instruction with S that writes the PC (SUBS PC, LR, #4; MOVS PC, LR): in an exception mode an
    /// exception return (PC = result, CPSR = SPSR); elsewhere the plain data-processing form, as before.
    /// </summary>
    public static void DPExceptionReturn(CpuState* state, uint ins, uint pc)
    {
        if (!ExceptionModes.HasSpsr(state))
        {
            if (Bit(ins, 25) != 0)
            {
                DPImm(state, ins, pc);
            }
            else if (Bit(ins, 4) != 0)
            {
                DPRegReg(state, ins, pc);
            }
            else
            {
                DPRegImm(state, ins, pc);
            }

            return;
        }

        uint rn = Bits(ins, 19, 16);
        uint operand;
        if (Bit(ins, 25) != 0)
        {
            operand = ArmExpandImmC(ins & 0xFFF, state->C, out _);
        }
        else if (Bit(ins, 4) != 0)
        {
            operand = ShiftRegC(state->R[ins & 0xF], Bits(ins, 6, 5), state->R[Bits(ins, 11, 8)], state->C, out _);
        }
        else
        {
            DecodeImmShift(Bits(ins, 6, 5), Bits(ins, 11, 7), out int type, out int amount);
            operand = ShiftC(Rg(state, ins & 0xF, pc), type, amount, state->C, out _);
        }

        uint result = DataProcessingResult(state, Bits(ins, 24, 21), Rg(state, rn, pc), operand);
        ExceptionModes.ReturnFromException(state, result);
    }

    /// <summary>The value a data-processing opcode computes (the flag-setting compare opcodes are never passed).</summary>
    private static uint DataProcessingResult(CpuState* state, uint opcode, uint a, uint b)
    {
        return opcode switch
        {
            0x0 => a & b,
            0x1 => a ^ b,
            0x2 => a - b,
            0x3 => b - a,
            0x4 => a + b,
            0x5 => a + b + state->C,
            0x6 => a + ~b + state->C,
            0x7 => ~a + b + state->C,
            0xC => a | b,
            0xD => b,
            0xE => a & ~b,
            _ => ~b,
        };
    }

    /// <summary>
    /// LDM/STM with ^ (cond 100P U1WL Rn list): from a privileged mode, LDM with the PC in the list is an exception
    /// return and the other forms transfer the user-mode registers; in user mode the plain LDM/STM, as before.
    /// </summary>
    public static void LdmStmUser(CpuState* state, uint ins, uint pc)
    {
        if (!ExceptionModes.IsPrivileged(state))
        {
            LdmStm(state, ins, pc);
            return;
        }

        bool before = Bit(ins, 24) != 0, up = Bit(ins, 23) != 0, writeback = Bit(ins, 21) != 0, load = Bit(ins, 20) != 0;
        uint rn = Bits(ins, 19, 16);
        uint list = ins & 0xFFFF;
        uint count = (uint)BitOperations.PopCount(list);
        uint baseValue = state->R[rn];
        uint address = TransferStart(baseValue, count, before, up);
        uint newBase = up ? baseValue + 4 * count : baseValue - 4 * count;
        if (load && (list & 0x8000) != 0)
        {
            uint target = 0;
            for (int i = 0; i < 16; i++)
            {
                if ((list & (1u << i)) == 0)
                {
                    continue;
                }

                uint value = Mem.R32(state, address);
                address += 4;
                if (i == 15)
                {
                    target = value;
                }
                else
                {
                    state->R[i] = value;
                }
            }

            if (writeback)
            {
                state->R[rn] = newBase;
            }

            ExceptionModes.ReturnFromException(state, target);
            return;
        }

        // user registers: writeback is UNPREDICTABLE and not done
        for (int i = 0; i < 16; i++)
        {
            if ((list & (1u << i)) == 0)
            {
                continue;
            }

            if (load)
            {
                ExceptionModes.UserRegister(state, i) = Mem.R32(state, address);
            }
            else
            {
                Mem.W32(state, address, i == 15 ? pc + 8 : ExceptionModes.UserRegister(state, i));
            }

            address += 4;
        }
    }

    /// <summary>
    /// The CP15 fault registers an MRC/MCR names (ARM ARM B4.1.52/.53/.96/.97): 0 DFSR (c5, c0, 0), 1 DFAR (c6, c0,
    /// 0), 2 IFSR (c5, c0, 1), 3 IFAR (c6, c0, 2), 4 ADFSR (c5, c1, 0), 5 AIFSR (c5, c1, 1), as the words after
    /// <see cref="CpuState.Dfsr"/>; -1 for any other register.
    /// </summary>
    internal static int FaultRegister(uint ins)
    {
        uint crn = Bits(ins, 19, 16), crm = ins & 0xF, opc1 = Bits(ins, 23, 21), opc2 = Bits(ins, 7, 5);
        if (Bits(ins, 11, 8) != 15 || opc1 != 0)
        {
            return -1;
        }

        return (crn, crm, opc2) switch
        {
            (5, 0, 0) => 0,
            (6, 0, 0) => 1,
            (5, 0, 1) => 2,
            (6, 0, 2) => 3,
            (5, 1, 0) => 4,
            (5, 1, 1) => 5,
            _ => -1,
        };
    }
}
