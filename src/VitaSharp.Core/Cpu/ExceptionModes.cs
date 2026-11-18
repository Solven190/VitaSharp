namespace VitaSharp.Core.Cpu;

/// <summary>
/// The ARMv7-A processor modes of the Cortex-A9 that kernel exception handlers run in (ARM ARM B1.3): banked R13/R14
/// (and R8-R12 of FIQ mode) and the SPSR of each exception mode, kept in <see cref="CpuState"/> so the running mode's
/// registers are always R[13]/R[14] (the backends never see banking). User code never changes mode: only an exception
/// entry (<see cref="EnterException"/>) and the privileged instructions of its handler (MSR CPSR_c, CPS, RFE, SUBS PC,
/// LR, LDM with ^ and PC) do, so a thread that takes no exception pays nothing.
/// </summary>
public static unsafe class ExceptionModes
{
    public const uint User = 0x10, Fiq = 0x11, Irq = 0x12, Supervisor = 0x13, Abort = 0x17, Undefined = 0x1B, System = 0x1F;

    /// <summary>CPSR.A, CPSR.I, CPSR.F.</summary>
    public const uint MaskA = 1u << 8, MaskI = 1u << 7, MaskF = 1u << 6;

    /// <summary>The CPSR bits <see cref="CpuState.Control"/> holds (E, A, I, F, M).</summary>
    public const uint ControlMask = 0x3DF;

    /// <summary>The register bank of <paramref name="mode"/>: 0 user/system, 1 FIQ, 2 IRQ, 3 supervisor, 4 abort, 5
    /// undefined; -1 for a mode the Cortex-A9 without the security extensions' monitor does not run.</summary>
    public static int Bank(uint mode) => mode switch
    {
        User or System => 0,
        Fiq => 1,
        Irq => 2,
        Supervisor => 3,
        Abort => 4,
        Undefined => 5,
        _ => -1,
    };

    /// <summary>True in any mode but user mode (the privileged instructions do something only then).</summary>
    public static bool IsPrivileged(CpuState* s) => s->Mode != User;

    /// <summary>True when the current mode has an SPSR (an exception mode: not user or system).</summary>
    public static bool HasSpsr(CpuState* s) => Bank(s->Mode) > 0;

    /// <summary>The SPSR of the current mode (the CPSR in user and system mode, which have none: UNPREDICTABLE).</summary>
    public static uint ReadSpsr(CpuState* s)
    {
        int bank = Bank(s->Mode);
        if (bank <= 0)
        {
            return s->Cpsr;
        }

        return s->Spsr[bank];
    }

    /// <summary>Writes the bytes of the current mode's SPSR selected by <paramref name="fieldMask"/> (MSR field mask:
    /// bit 0 c, 1 x, 2 s, 3 f). Ignored in user and system mode.</summary>
    public static void WriteSpsr(CpuState* s, uint fieldMask, uint value)
    {
        int bank = Bank(s->Mode);
        if (bank <= 0)
        {
            return;
        }

        uint byteMask = FieldBytes(fieldMask);
        s->Spsr[bank] = (s->Spsr[bank] & ~byteMask) | (value & byteMask);
    }

    /// <summary>The CPSR/SPSR bits an MSR field mask selects.</summary>
    public static uint FieldBytes(uint fieldMask)
    {
        uint byteMask = 0;
        for (int i = 0; i < 4; i++)
        {
            if ((fieldMask & (1u << i)) != 0)
            {
                byteMask |= 0xFFu << (8 * i);
            }
        }

        return byteMask;
    }

    /// <summary>
    /// Switches to <paramref name="mode"/>: the running mode's R13/R14 (and R8-R12 between FIQ and the others) go to
    /// its bank and the new mode's come out of theirs. False (nothing changes) for a mode the core does not have.
    /// </summary>
    public static bool SwitchMode(CpuState* s, uint mode)
    {
        int to = Bank(mode);
        int from = Bank(s->Mode);
        if (to < 0)
        {
            return false;
        }

        if (from < 0)
        {
            from = 0;
        }

        if (to != from)
        {
            s->BankedSp[from] = s->R[13];
            s->BankedLr[from] = s->R[14];
            s->R[13] = s->BankedSp[to];
            s->R[14] = s->BankedLr[to];
            if ((from == 1) != (to == 1))
            {
                int save = from == 1 ? 5 : 0;
                int load = to == 1 ? 5 : 0;
                for (int i = 0; i < 5; i++)
                {
                    s->BankedHigh[save + i] = s->R[8 + i];
                    s->R[8 + i] = s->BankedHigh[load + i];
                }
            }
        }

        s->ControlXor = ((s->Control & ~0x1Fu) | mode) ^ User;
        return true;
    }

    /// <summary>
    /// A privileged write of the CPSR bytes <paramref name="fieldMask"/> selects (MSR CPSR, Rn: the execution state
    /// bits T and IT are not written). The control byte (mode and masks) is written only in a privileged mode; in user
    /// mode the write changes the flags and GE as before. An invalid mode leaves the mode unchanged.
    /// </summary>
    public static void WriteCpsrFields(CpuState* s, uint fieldMask, uint value)
    {
        uint byteMask = FieldBytes(fieldMask);
        if ((byteMask & 0xFF000000) != 0)
        {
            s->N = (value >> 31) & 1;
            s->Z = (value >> 30) & 1;
            s->C = (value >> 29) & 1;
            s->V = (value >> 28) & 1;
            s->Q = (value >> 27) & 1;
        }

        if ((byteMask & 0x000F0000) != 0)
        {
            s->GE = (value >> 16) & 0xF;
        }

        if (!IsPrivileged(s))
        {
            return;
        }

        uint control = s->Control;
        if ((byteMask & 0x0000FF00) != 0)
        {
            control = (control & ~0x200u) | (value & 0x200u);
            control = (control & ~MaskA) | (value & MaskA);
        }

        if ((byteMask & 0xFF) != 0)
        {
            control = (control & ~(MaskI | MaskF)) | (value & (MaskI | MaskF));
            if (Bank(value & 0x1F) >= 0)
            {
                SwitchMode(s, value & 0x1F);
            }

            control = (control & ~0x1Fu) | s->Mode;
        }

        s->ControlXor = control ^ User;
    }

    /// <summary>
    /// The whole CPSR as an exception return writes it (RFE, SUBS PC, LR, LDM with ^ and PC): every bit, the
    /// execution state (T, IT) included. Only from a privileged mode; an invalid mode in the value keeps the mode.
    /// </summary>
    public static void WriteCpsr(CpuState* s, uint value)
    {
        if (!IsPrivileged(s))
        {
            return;
        }

        uint mode = value & 0x1F;
        if (Bank(mode) >= 0)
        {
            SwitchMode(s, mode);
        }

        s->Cpsr = value; // flags, Q, GE, IT, T
        s->ControlXor = ((value & ControlMask & ~0x1Fu) | s->Mode) ^ User;
    }

    /// <summary>An exception return: CPSR = SPSR, then the branch to <paramref name="target"/> in the restored state.</summary>
    public static void ReturnFromException(CpuState* s, uint target)
    {
        WriteCpsr(s, ReadSpsr(s));
        s->R[15] = s->Thumb != 0 ? target & ~1u : target & ~3u;
    }

    /// <summary>
    /// Takes an exception to <paramref name="mode"/> (ARM ARM B1.8): SPSR_mode = CPSR, the mode's banked R13/R14,
    /// LR = <paramref name="returnLink"/>, ARM state (SCTLR.TE = 0), IT cleared, IRQs masked (and asynchronous aborts
    /// for an abort, FIQs and asynchronous aborts for FIQ), then the vector <paramref name="vector"/>.
    /// </summary>
    public static void EnterException(CpuState* s, uint mode, uint returnLink, uint vector)
    {
        uint saved = s->Cpsr;
        SwitchMode(s, mode);
        s->Spsr[Bank(mode)] = saved;
        s->R[14] = returnLink;
        uint control = (s->Control & ~0x200u) | MaskI;
        if (mode is Abort or Irq or Fiq)
        {
            control |= MaskA;
        }

        if (mode == Fiq)
        {
            control |= MaskF;
        }

        s->ControlXor = control ^ User;
        s->Thumb = 0;
        s->ITState = 0;
        s->ExclusiveValid = 0;
        s->R[15] = vector & ~3u;
    }

    /// <summary>The user-mode copy of register <paramref name="r"/> (8..14), as LDM/STM with ^ address it from a
    /// privileged mode.</summary>
    public static ref uint UserRegister(CpuState* s, int r)
    {
        int bank = Bank(s->Mode);
        if (r >= 13 && bank > 0)
        {
            return ref r == 13 ? ref s->BankedSp[0] : ref s->BankedLr[0];
        }

        if (r >= 8 && r <= 12 && bank == 1)
        {
            return ref s->BankedHigh[r - 8];
        }

        return ref s->R[r];
    }
}
