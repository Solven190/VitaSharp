using VitaSharp.Core.Cpu;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// Small libkernel.suprx 3.74 exports (read in the module, study only): the TLS slot address of the calling thread
/// checked against a thread id, and the library's own setjmp/longjmp pair (NID_37F4ED04 / NID_DC277B4D).
/// </summary>
public sealed partial class ThreadMgr
{
    /// <summary>NID_35D20E49(thid, key): sceKernelGetTLSAddr(key) when <paramref name="thid"/> is 0 or the calling
    /// thread's id (the one in its TLS block, slot 1); 0xFFFFFFFF for another thread, NULL for a key of 0x100 or
    /// more.</summary>
    [Hle(Nid = 0x35D20E49, Library = "SceLibKernel", ReturnsValue = true, Cost = HleCost.UserInline)]
    uint NID_35D20E49(int thid, uint key)
    {
        if (key >= 0x100)
        {
            return 0;
        }

        uint slots = Self.TlsBase;
        if (thid != 0 && thid != GuestMemory.Read<int>(slots + 4))
        {
            return 0xFFFFFFFF;
        }

        return slots + key * 4;
    }

    /// <summary>The words libkernel's setjmp/longjmp leave in the scratch registers on their way out: r1-r3 and r12
    /// get 0xDEADBEEF, d0-d7 and d16-d31 the NaN pair (0x7F80DEAD, 0x7FF8DEAD).</summary>
    const uint JmpScratchPoison = 0xDEADBEEF;
    const ulong JmpVfpPoison = 0x7FF8DEAD_7F80DEADUL;

    /// <summary>The FPSCR bits libkernel's setjmp keeps (bfc #8, #19): the condition flags, the mode bits and the
    /// cumulative exception flags. The rest (trap enables, vector length and stride) is cleared in the register too.</summary>
    const uint JmpFpscrKept = 0xF80000FF;

    /// <summary>NID_37F4ED04(jmp_buf env): libkernel's setjmp. Saves r4-r11, sp, lr, d8-d15 and the FPSCR (only the
    /// bits 0xF80000FF, which it also writes back to the FPSCR) into 0x6C bytes and returns 0. Unlike libc's there is
    /// no TLS key on sp and lr.</summary>
    [Hle("NID_37F4ED04", Nid = 0x37F4ED04, Library = "SceLibKernel")]
    unsafe int LibKernelSetjmp(HleContext context, uint env)
    {
        CpuState* cpu = context.Cpu;
        for (int i = 0; i < 8; i++)
        {
            GuestMemory.W32(env + (uint)i * 4, cpu->R[4 + i]);
        }

        GuestMemory.W32(env + 32, cpu->R[13]);
        GuestMemory.W32(env + 36, cpu->R[14]);
        ulong* vfp = cpu->D;
        for (int i = 0; i < 8; i++)
        {
            GuestMemory.Write(env + 40 + (uint)i * 8, vfp[8 + i]);
        }

        uint fpscr = cpu->Fpscr & JmpFpscrKept;
        cpu->Fpscr = fpscr;
        GuestMemory.W32(env + 104, fpscr);
        PoisonJmpScratch(cpu);
        return 0;
    }

    /// <summary>NID_DC277B4D(jmp_buf env, int value): libkernel's longjmp. Restores what NID_37F4ED04 saved (the whole
    /// FPSCR word) and returns <paramref name="value"/>, 1 for 0, from that setjmp call.</summary>
    [Hle("NID_DC277B4D", Nid = 0xDC277B4D, Library = "SceLibKernel")]
    unsafe void LibKernelLongjmp(HleContext context, uint env, int value)
    {
        CpuState* cpu = context.Cpu;
        for (int i = 0; i < 8; i++)
        {
            cpu->R[4 + i] = GuestMemory.R32(env + (uint)i * 4);
        }

        cpu->R[13] = GuestMemory.R32(env + 32);
        cpu->R[14] = GuestMemory.R32(env + 36);
        ulong* vfp = cpu->D;
        for (int i = 0; i < 8; i++)
        {
            vfp[8 + i] = GuestMemory.Read<ulong>(env + 40 + (uint)i * 8);
        }

        cpu->Fpscr = GuestMemory.R32(env + 104);
        PoisonJmpScratch(cpu);
        cpu->R[0] = value == 0 ? 1u : (uint)value;
    }

    static unsafe void PoisonJmpScratch(CpuState* cpu)
    {
        cpu->R[1] = JmpScratchPoison;
        cpu->R[2] = JmpScratchPoison;
        cpu->R[3] = JmpScratchPoison;
        cpu->R[12] = JmpScratchPoison;
        ulong* vfp = cpu->D;
        for (int i = 0; i < 8; i++)
        {
            vfp[i] = JmpVfpPoison;
        }

        for (int i = 16; i < 32; i++)
        {
            vfp[i] = JmpVfpPoison;
        }
    }
}
