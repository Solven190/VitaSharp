using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// The rest of SceBacktraceForDriver (modulemgr.skprx 3.74, read in the module, study only), next to
// ksceKernelBacktrace (a twin of the user call, in KernelTwins).
public sealed partial class SystemServicesVsh
{
    /// <summary>The user sceKernelBacktrace, whose walk both kernel backtraces share.</summary>
    const uint SceKernelBacktraceNid = 0x894037E8;

    /// <summary>NID_888E99B8 (SceBacktraceForDriver): the same walk as ksceKernelBacktrace without its check of the
    /// caller's privilege (the two share modulemgr's walker; this one is for the kernel's own crash paths).</summary>
    [Hle("NID_888E99B8", Nid = 0x888E99B8, Library = "SceBacktraceForDriver")]
    void KernelBacktraceUnchecked(HleContext context)
    {
        if (Emu.Hle.ImplOf(SceKernelBacktraceNid) is { } walk)
        {
            walk(context);
        }
        else
        {
            context.Return(SceError.NOT_IMPLEMENTED);
        }
    }

    /// <summary>NID_7C878F90 (SceBacktraceForDriver)(SceUID pid, const SceKernelCallFrame *frames, int count,
    /// uint startPc): prints a backtrace (0x10-byte frames: +0 sp, +4 pc) to the kernel's console, from the frame
    /// whose pc is <paramref name="startPc"/> when it is set, one line per frame with the distance to the next
    /// frame's sp; NULL frames is 0x80020006.</summary>
    [Hle("NID_7C878F90", Nid = 0x7C878F90, Library = "SceBacktraceForDriver")]
    int KernelPrintBacktrace(int pid, uint frames, int count, uint startPc)
    {
        if (frames == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        if (startPc != 0)
        {
            for (int i = 0; i < count; i++)
            {
                if (GuestMemory.R32(frames + (uint)i * 0x10 + 4) == startPc)
                {
                    frames += (uint)i * 0x10;
                    count -= i;
                    break;
                }
            }
        }

        Log.Info($"[backtrace] pid 0x{pid:X}");
        for (int i = 0; i < count; i++)
        {
            uint frame = frames + (uint)i * 0x10;
            uint sp = GuestMemory.R32(frame);
            uint pc = GuestMemory.R32(frame + 4);
            uint depth = i == count - 1 ? 0 : GuestMemory.R32(frame + 0x10) - sp;
            Log.Info($"[backtrace] #{i} pc 0x{pc:X8} sp 0x{sp:X8} depth 0x{depth:X}");
        }

        return 0;
    }
}
