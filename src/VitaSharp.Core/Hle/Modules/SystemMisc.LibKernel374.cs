using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// The last libkernel.suprx 3.74 exports of SceLibKernel that work on the process's modules (read in the module, study
// only): a library variable looked up by NID through sceKernelGetLibraryInfoByNID (NID_0A4DF821), the ARM exception
// index entry of a code address (NID_6C7365C4, the lookup the unwinder uses) and the backtrace printer
// (NID_20E2D4B7).

public sealed partial class SystemMisc
{
    /// <summary>sizeof(SceKernelLibraryInfo), the size libkernel passes to sceKernelGetLibraryInfoByNID.</summary>
    const int LibraryInfoSize = 0x1C;

    const int LIBKERNEL_ERR_NO_VARIABLE = unchecked((int)0x8002D00C), LIBKERNEL_ERR_NO_EXIDX = unchecked((int)0x8002D01C),
        LIBKERNEL_ERR_ADDR_RANGE = unchecked((int)0x80020008), LIBKERNEL_ERR_OTHER_PROCESS = unchecked((int)0x80029001);

    /// <summary>The user address range libkernel's exception-index lookup covers (a per-page table the kernel keeps
    /// for 0x81000000-0xEFFFFFFF); any other address is 0x80020008.</summary>
    const uint ExidxRangeStart = 0x81000000, ExidxRangeEnd = 0xF0000000;

    /// <summary>One library of a module's export table (sce_module_exports, read from guest memory).</summary>
    readonly record struct ExportEntry(ushort Version, ushort Attributes, ushort FunctionCount, ushort VariableCount,
        uint NidTable, uint EntryTable);

    /// <summary>The export entry of <paramref name="libNid"/> in <paramref name="module"/>'s export table. A module
    /// without one in guest memory (an HLE module) gives the counts it registered and no tables.</summary>
    static bool FindExportEntry(Loader.LoadedModule module, uint libNid, out ExportEntry entry)
    {
        for (uint p = module.ExportsStart; p != 0 && p < module.ExportsEnd;)
        {
            ushort size = GuestMemory.Read<ushort>(p);
            if (GuestMemory.R32(p + 0x10) == libNid)
            {
                entry = new ExportEntry(GuestMemory.Read<ushort>(p + 2), GuestMemory.Read<ushort>(p + 4),
                    GuestMemory.Read<ushort>(p + 6), (ushort)GuestMemory.R32(p + 8), GuestMemory.R32(p + 0x18),
                    GuestMemory.R32(p + 0x1C));
                return true;
            }

            if (size == 0)
            {
                break;
            }

            p += size;
        }

        int index = module.Exports.FindIndex(e => e.LibNid == libNid);
        if (index < 0)
        {
            entry = default;
            return false;
        }

        entry = new ExportEntry(0, 0, (ushort)module.Exports[index].FuncNids.Length,
            (ushort)module.Exports[index].VarNids.Length, 0, 0);
        return true;
    }

    /// <summary>NID_0A4DF821(SceUID modid, SceNID libnid, SceNID varnid, void **address): the address of variable
    /// <paramref name="varNid"/> of library <paramref name="libNid"/> exported by module <paramref name="modid"/>.
    /// libkernel asks sceKernelGetLibraryInfoByNID (its error passes through) and walks the variable part of the NID
    /// table (after the functions); a NID that is not there, or whose entry is NULL, is 0x8002D00C.</summary>
    [Hle("NID_0A4DF821", Nid = 0x0A4DF821, Library = "SceLibKernel")]
    int LibKernelGetLibraryVariable(int modid, uint libNid, uint varNid, uint address)
    {
        if (!K.Modules.TryGetValue(modid, out Loader.LoadedModule? module))
        {
            return Kernel.SceError.MODULEMGR_NO_MOD;
        }

        if (!FindExportEntry(module, libNid, out ExportEntry entry))
        {
            return unchecked((int)0x8002D003);
        }

        for (uint i = 0; i < entry.VariableCount && entry.NidTable != 0; i++)
        {
            uint slot = entry.FunctionCount + i;
            if (GuestMemory.R32(entry.NidTable + slot * 4) != varNid)
            {
                continue;
            }

            uint value = GuestMemory.R32(entry.EntryTable + slot * 4);
            if (value == 0)
            {
                break;
            }

            GuestMemory.W32(address, value);
            return 0;
        }

        return LIBKERNEL_ERR_NO_VARIABLE;
    }

    /// <summary>NID_6C7365C4(void *pc, uint out[2]): the .ARM.exidx entry of the function holding
    /// <paramref name="pc"/> (the Thumb bit ignored) in the table of the module that maps it. out[0] is the entry's
    /// second word; out[1] the address of its unwind data (the prel31 target) or 0xFFFFFFFF when the word is inline
    /// (bit 31) or EXIDX_CANTUNWIND (1). An address outside 0x81000000-0xEFFFFFFF is 0x80020008; no module, no table
    /// or an address before the table's first function is 0x8002D01C.</summary>
    [Hle("NID_6C7365C4", Nid = 0x6C7365C4, Library = "SceLibKernel")]
    int LibKernelFindExidxEntry(uint pc, uint output)
    {
        if (pc - ExidxRangeStart >= ExidxRangeEnd - ExidxRangeStart)
        {
            return LIBKERNEL_ERR_ADDR_RANGE;
        }

        uint address = pc & ~1u;
        Loader.LoadedModule? module = ModuleAt(address);
        if (module == null || module.ExidxTop == 0 || module.ExidxEnd <= module.ExidxTop)
        {
            return LIBKERNEL_ERR_NO_EXIDX;
        }

        uint count = (module.ExidxEnd - module.ExidxTop) / 8;
        uint top = module.ExidxTop;
        if (count == 0 || (ExidxFunction(top) & ~1u) > address)
        {
            return LIBKERNEL_ERR_NO_EXIDX;
        }

        uint low = 0;
        uint high = count;
        while (high - low > 1)
        {
            uint middle = (low + high) / 2;
            if ((ExidxFunction(top + middle * 8) & ~1u) <= address)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        uint entry = top + low * 8;
        uint content = GuestMemory.R32(entry + 4);
        GuestMemory.W32(output, content);
        bool hasData = (content & 0x80000000) == 0 && content != 1;
        GuestMemory.W32(output + 4, hasData ? entry + 4 + SignExtend31(content) : 0xFFFFFFFF);
        return 0;
    }

    /// <summary>The module of the process whose segments hold <paramref name="address"/>.</summary>
    Loader.LoadedModule? ModuleAt(uint address)
    {
        foreach (Loader.LoadedModule module in UserModules)
        {
            foreach (Loader.SegmentInfo segment in module.Segments.Values)
            {
                if (address - segment.Address < segment.Size)
                {
                    return module;
                }
            }
        }

        return null;
    }

    /// <summary>The function start an exidx entry's first word (prel31) points at.</summary>
    static uint ExidxFunction(uint entry) => entry + SignExtend31(GuestMemory.R32(entry));

    static uint SignExtend31(uint word) => (word & 0x40000000) != 0 ? word | 0x80000000 : word & 0x7FFFFFFF;

    /// <summary>NID_20E2D4B7(SceUID pid, const SceKernelCallFrame *frames, int count): prints the frames (sp, pc) a
    /// backtrace gave through sceClibPrintf, between two rulers: "(i) PC:..., SP:...(size=...) [module + offset]"
    /// (the size is the distance to the next frame's sp, 0 for the last; the offset is from the module's first
    /// segment), "[no module info]" when no module holds the pc. Only the calling process (0 or its pid), else
    /// 0x80029001; NULL frames is 0x80020006.</summary>
    [Hle("NID_20E2D4B7", Nid = 0x20E2D4B7, Library = "SceLibKernel")]
    int LibKernelPrintBacktrace(int pid, uint frames, int count)
    {
        if (pid != 0 && pid != Emu.Pid)
        {
            return LIBKERNEL_ERR_OTHER_PROCESS;
        }

        if (frames == 0)
        {
            return Kernel.SceError.ILLEGAL_ADDR;
        }

        foreach (string line in BacktraceLines(frames, count))
        {
            Log.Info("[guest] " + line);
        }

        return 0;
    }

    /// <summary>The lines NID_20E2D4B7 prints for <paramref name="count"/> frames.</summary>
    internal List<string> BacktraceLines(uint frames, int count)
    {
        var lines = new List<string> { "=== Print Backtrace =================" };
        for (int i = 0; i < count; i++)
        {
            uint frame = frames + (uint)i * 8;
            uint sp = GuestMemory.R32(frame);
            uint pc = GuestMemory.R32(frame + 4);
            uint size = i == count - 1 ? 0 : GuestMemory.R32(frame + 8) - sp;
            Loader.LoadedModule? module = ModuleAt(pc);
            if (module == null)
            {
                lines.Add($"({i}) PC:0x{pc:X8}, SP:0x{sp:X8}(size=0x{size:X8} byte) [no module info]");
                continue;
            }

            uint segmentBase = module.Segments.TryGetValue(0, out Loader.SegmentInfo first) ? first.Address : 0;
            lines.Add($"({i}) PC:0x{pc:X8}, SP:0x{sp:X8}(size=0x{size:X8} byte) [{module.Name} + 0x{pc - segmentBase:X8}]");
        }

        lines.Add("=====================================");
        return lines;
    }
}
