using System.Collections.Concurrent;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Cpu;

/// <summary>
/// Translates guest code before it runs, on several threads: the blocks given as seeds and, when a code range is
/// given, the code reachable from them by static control flow (each translated block's static exits, and the return
/// point after a call), which is how the precompilation finds code no earlier session ran (RPCS3's PPU analyser walks
/// functions from the module's entry points the same way). Whatever the analysis misses, or reaches only through
/// computed branches (jump tables, function pointers it was not given), the JIT compiles when the game gets there.
/// </summary>
public static class CodePrecompiler
{
    /// <summary>Statistics of one run.</summary>
    public readonly record struct Result(int Translated, int Visited, int Found, bool Cancelled, bool ArenaLimit);

    /// <summary>
    /// Translates <paramref name="seeds"/> and, with <paramref name="follow"/>, the blocks reachable from them whose start
    /// it accepts, on <paramref name="workers"/> threads working in <paramref name="space"/> (0: every core the process may
    /// use but one). <paramref name="progress"/> gets (done, known so far) about every 100 ms on the calling thread.
    /// </summary>
    public static Result Run(BlockCache cache, AddressSpace? space, IEnumerable<(uint Pc, bool Thumb)> seeds, Func<uint, bool>? follow, int workers,
        Action<int, int>? progress, CancellationToken cancel)
    {
        var seen = new ConcurrentDictionary<ulong, byte>();
        var queue = new ConcurrentQueue<(uint Pc, bool Thumb)>();
        int known = 0;
        int done = 0;
        int translated = 0;
        int found = 0;
        int outstanding = 0; // queued or being translated
        bool arenaLimit = false;
        bool Add((uint Pc, bool Thumb) target)
        {
            if (!seen.TryAdd(target.Pc | (target.Thumb ? 1ul << 32 : 0), 0))
            {
                return false;
            }

            Interlocked.Increment(ref outstanding);
            Interlocked.Increment(ref known);
            queue.Enqueue(target);
            return true;
        }

        foreach ((uint Pc, bool Thumb) seed in seeds)
        {
            Add(seed);
        }

        if (workers <= 0)
        {
            workers = Math.Max(1, Environment.ProcessorCount - 1);
        }

        var threads = new Thread[workers];
        for (int i = 0; i < workers; i++)
        {
            threads[i] = new Thread(() =>
            {
                Util.NativeThread.Register("JIT precompile", Util.NativeThread.Role.JitWorker);
                using AddressSpace.Activation activation = AddressSpace.Use(space);
                var exits = new List<(uint Pc, bool Thumb)>();
                Jit.StaticExits = follow != null ? exits : null;
                var spin = new SpinWait();
                while (!cancel.IsCancellationRequested && Volatile.Read(ref outstanding) > 0)
                {
                    if (!queue.TryDequeue(out (uint Pc, bool Thumb) target))
                    {
                        spin.SpinOnce(sleep1Threshold: 10);
                        continue;
                    }

                    try
                    {
                        if (!Jit.ArenaHasRoomForAhead())
                        {
                            Volatile.Write(ref arenaLimit, true);
                            continue;
                        }

                        exits.Clear();
                        Block block = cache.Get(target.Pc, target.Thumb);
                        if (Jit.CompileAheadOne(block))
                        {
                            Interlocked.Increment(ref translated);
                        }

                        if (follow != null)
                        {
                            if (block.Insts.Length > 0 && ReturnsAfter(in block.Insts[^1]))
                            {
                                exits.Add((block.End, block.Thumb));
                            }

                            foreach ((uint Pc, bool Thumb) exit in exits)
                            {
                                if (follow(exit.Pc) && Add(exit))
                                {
                                    Interlocked.Increment(ref found);
                                }
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Console.Error.WriteLine($"[JIT] precompile {target.Pc:X8}: {e.Message}");
                    }
                    finally
                    {
                        Interlocked.Increment(ref done);
                        Interlocked.Decrement(ref outstanding);
                    }
                }

                Jit.StaticExits = null;
            })
            {
                IsBackground = true,
                Name = $"JIT precompile {i}",
            };
            threads[i].Start();
        }

        foreach (Thread thread in threads)
        {
            while (!thread.Join(100))
            {
                progress?.Invoke(Volatile.Read(ref done), Volatile.Read(ref known));
            }
        }

        progress?.Invoke(done, known);
        return new Result(translated, done, found, cancel.IsCancellationRequested, arenaLimit);
    }

    /// <summary>Whether execution continues after the block's last instruction once it returns: calls (BL, BLX) and
    /// supervisor calls come back to the next instruction, which the block's static exits do not name.</summary>
    public static bool ReturnsAfter(in DecodedInst last) => last.Op switch
    {
        Op.A32_Bl or Op.A32_BlxImm or Op.A32_BlxReg or Op.T32_Bl or Op.T32_BlxImm or Op.A32_Svc or Op.T16_Svc => true,
        Op.T16_BxBlx => (last.Raw & 0x80) != 0, // BLX Rm (BX Rm has the L bit clear)
        _ => false,
    };

    /// <summary>
    /// The entry points the static analysis starts from in a loaded module: module_start/stop/exit, the exported
    /// functions and the function starts of its exception index table (.ARM.exidx: one entry per function with unwind
    /// information, which compilers emit for nearly every function). Bit 0 of an address selects Thumb; index entries
    /// carry no instruction set, so they take the module's (the one most of its explicit entry points use).
    /// </summary>
    public static List<(uint Pc, bool Thumb)> ModuleEntryPoints(LoadedModule module)
    {
        var entries = new List<(uint Pc, bool Thumb)>();
        int thumbVotes = 0;
        void AddEntry(uint address)
        {
            if (address == 0 || address == 0xFFFFFFFF)
            {
                return;
            }

            bool thumb = (address & 1) != 0;
            thumbVotes += thumb ? 1 : -1;
            entries.Add((address & ~1u, thumb));
        }

        AddEntry(module.StartEntry);
        AddEntry(module.StopEntry);
        AddEntry(module.ExitEntry);
        foreach (uint entry in module.ExportedFunctionEntries)
        {
            AddEntry(entry);
        }

        bool moduleThumb = thumbVotes >= 0;
        if (module.ExidxTop != 0 && module.ExidxEnd > module.ExidxTop && module.ExidxEnd - module.ExidxTop < 64u << 20)
        {
            for (uint at = module.ExidxTop; at + 8 <= module.ExidxEnd; at += 8)
            {
                if (!GuestMemory.IsMapped(at))
                {
                    break;
                }

                uint word = GuestMemory.R32(at);
                if ((word & 0x80000000) != 0)
                {
                    continue; // not a prel31 function offset
                }

                uint function = at + (uint)((int)(word << 1) >> 1);
                entries.Add((function & ~1u, (function & 1) != 0 || moduleThumb));
            }
        }

        return entries;
    }

    /// <summary>Whether <paramref name="address"/> is in an executable segment of <paramref name="module"/>.</summary>
    public static bool InCode(LoadedModule module, uint address)
    {
        foreach (KeyValuePair<int, SegmentInfo> segment in module.Segments)
        {
            if (module.SegmentFlags.TryGetValue(segment.Key, out (uint Flags, uint MemSz, uint FileSz) flags) && (flags.Flags & 1) == 0)
            {
                continue; // PF_X clear: data
            }

            if (address >= segment.Value.Address && address - segment.Value.Address < segment.Value.Size)
            {
                return true;
            }
        }

        return false;
    }
}
