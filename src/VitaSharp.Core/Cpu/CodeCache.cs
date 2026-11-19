using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Cpu;

/// <summary>A translated block of a module, by where it is in the module: the segment and the offset from the
/// segment's load address, so the entry holds wherever the loader places the module.</summary>
public readonly record struct CodeCacheEntry(int Segment, uint Offset, bool Thumb);

/// <summary>
/// The on-disk file of <see cref="CodeCache"/>: the blocks one module had translated, for one module image and one
/// translator configuration. Layout (little endian): magic "VSJC", format version, configuration key
/// (<see cref="ConfigurationKey"/>), module image hash, entry count, then per entry the offset (u32), the segment (u8)
/// and the instruction set (u8, 1 = Thumb), and last the XxHash64 of everything before it. A file of another format,
/// configuration or image, a truncated or corrupted one, is ignored (and rewritten at the next save).
/// </summary>
public static class CodeCacheFile
{
    public const uint Magic = 0x434A5356; // "VSJC"

    /// <summary>The file layout; bump when it changes.</summary>
    public const int FormatVersion = 1;

    /// <summary>
    /// What makes the recorded block starts valid besides the guest code itself: where the block decoder splits blocks
    /// (<see cref="BlockBuilder.MaxInstructions"/>, its end-of-block rules: bump <see cref="TranslatorVersion"/> when
    /// they change), the host architecture and the CPU backend the blocks were translated for.
    /// </summary>
    public const int TranslatorVersion = 1;

    private const int HeaderSize = 4 + 4 + 8 + 8 + 4;
    private const int EntrySize = 6;

    /// <summary>The configuration this build and host translate with (the key every file must carry).</summary>
    public static ulong ConfigurationKey => KeyOf(Jit.Mode, RuntimeInformation.ProcessArchitecture);

    /// <summary>The configuration key of a translator version, backend and host architecture.</summary>
    public static ulong KeyOf(string backend, Architecture host, int translatorVersion = TranslatorVersion) =>
        XxHash64.HashToUInt64(System.Text.Encoding.UTF8.GetBytes(
            $"{FormatVersion}|{translatorVersion}|{BlockBuilder.MaxInstructions}|{backend}|{host}"));

    /// <summary>The file of a module image in <paramref name="directory"/>.</summary>
    public static string PathFor(string directory, ulong imageHash) => Path.Combine(directory, $"{imageHash:x16}.vsjc");

    public static byte[] Serialize(ulong imageHash, ulong configurationKey, IReadOnlyCollection<CodeCacheEntry> entries)
    {
        byte[] data = new byte[HeaderSize + EntrySize * entries.Count + 8];
        Span<byte> span = data;
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], FormatVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], configurationKey);
        BinaryPrimitives.WriteUInt64LittleEndian(span[16..], imageHash);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], entries.Count);
        int at = HeaderSize;
        foreach (CodeCacheEntry entry in entries)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[at..], entry.Offset);
            span[at + 4] = (byte)entry.Segment;
            span[at + 5] = entry.Thumb ? (byte)1 : (byte)0;
            at += EntrySize;
        }

        BinaryPrimitives.WriteUInt64LittleEndian(span[at..], XxHash64.HashToUInt64(span[..at]));
        return data;
    }

    /// <summary>The entries of a file's bytes, or null when they are not a valid file for this image and configuration.</summary>
    public static List<CodeCacheEntry>? Deserialize(ReadOnlySpan<byte> data, ulong imageHash, ulong configurationKey)
    {
        if (data.Length < HeaderSize + 8
            || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic
            || BinaryPrimitives.ReadInt32LittleEndian(data[4..]) != FormatVersion
            || BinaryPrimitives.ReadUInt64LittleEndian(data[8..]) != configurationKey
            || BinaryPrimitives.ReadUInt64LittleEndian(data[16..]) != imageHash)
        {
            return null;
        }

        int count = BinaryPrimitives.ReadInt32LittleEndian(data[24..]);
        if (count < 0 || (long)HeaderSize + (long)EntrySize * count + 8 != data.Length)
        {
            return null;
        }

        int end = HeaderSize + EntrySize * count;
        if (XxHash64.HashToUInt64(data[..end]) != BinaryPrimitives.ReadUInt64LittleEndian(data[end..]))
        {
            return null;
        }

        var entries = new List<CodeCacheEntry>(count);
        for (int at = HeaderSize; at < end; at += EntrySize)
        {
            entries.Add(new CodeCacheEntry(data[at + 4], BinaryPrimitives.ReadUInt32LittleEndian(data[at..]), data[at + 5] != 0));
        }

        return entries;
    }

    /// <summary>Reads a module's file; null when there is none or it is not valid (<see cref="Deserialize"/>).</summary>
    public static List<CodeCacheEntry>? Read(string directory, ulong imageHash, ulong configurationKey)
    {
        string path = PathFor(directory, imageHash);
        try
        {
            return File.Exists(path) ? Deserialize(File.ReadAllBytes(path), imageHash, configurationKey) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes a module's file (through a temporary file: a crash never leaves half a file behind).</summary>
    public static void Write(string directory, ulong imageHash, ulong configurationKey, IReadOnlyCollection<CodeCacheEntry> entries)
    {
        Directory.CreateDirectory(directory);
        string path = PathFor(directory, imageHash);
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, Serialize(imageHash, configurationKey, entries));
        File.Move(temporary, path, overwrite: true);
    }
}

/// <summary>Progress of <see cref="CodeCache.PrecompilePending"/>: blocks translated so far, out of those queued.</summary>
public readonly record struct CodePrecompileProgress(int Done, int Total, bool Finished);

/// <summary>
/// The ARM code precompilation of a process (like RPCS3's "Compiling PPU modules"): the blocks its modules had
/// translated in earlier sessions are recorded per module image (<see cref="CodeCacheFile"/>), and when the module is
/// loaded again, at any address, they are translated before the process starts (<see cref="PrecompilePending"/>, on
/// every host core the process may use) or, for a module loaded while the game runs, queued to the JIT workers at once.
/// The game then finds its code already translated instead of compiling it while it plays.
/// <para>
/// Precompiled blocks are ordinary blocks of the process's <see cref="BlockCache"/>: rewriting their code invalidates
/// them like any other translation, and the block decoder reads the module's memory as it is now (its relocations
/// applied), so an entry only says where code starts: whatever the bytes there are, the translation is theirs. The
/// translated machine code itself is not stored: it embeds per-process addresses (link slots, runtime stubs, the
/// dispatch table, the constant pool) that would need a relocation table in both native emitters.
/// </para>
/// </summary>
public sealed class CodeCache
{
    private readonly string? _directory;
    private readonly BlockCache _code;
    private readonly ulong _configurationKey;
    private readonly object _lock = new();
    private readonly List<ModuleRecord> _modules = [];
    private readonly List<(uint Pc, bool Thumb)> _pending = [];
    private bool _started;

    private sealed class ModuleRecord(LoadedModule module, HashSet<CodeCacheEntry> known)
    {
        public LoadedModule Module { get; } = module;
        public HashSet<CodeCacheEntry> Known { get; } = known;
        public int SavedCount = known.Count;
    }

    /// <param name="directory">Folder of the cache files; null: no cache (only the static analysis of the boot step).</param>
    /// <param name="code">The process's translation cache.</param>
    /// <param name="configurationKey">Key the files must carry (tests); null: <see cref="CodeCacheFile.ConfigurationKey"/>.</param>
    public CodeCache(string? directory, BlockCache code, ulong? configurationKey = null)
    {
        _directory = directory;
        _code = code;
        _configurationKey = configurationKey ?? CodeCacheFile.ConfigurationKey;
    }

    /// <summary>Blocks waiting for <see cref="PrecompilePending"/>.</summary>
    public int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>A module was loaded (its image hash set, its segments placed and relocated): its recorded blocks are queued
    /// for the boot precompilation, or for the JIT workers once the process runs.</summary>
    public void ModuleLoaded(LoadedModule module)
    {
        List<CodeCacheEntry> entries = (_directory != null ? CodeCacheFile.Read(_directory, module.ImageHash, _configurationKey) : null) ?? [];
        var targets = new List<(uint Pc, bool Thumb)>(entries.Count);
        foreach (CodeCacheEntry entry in entries)
        {
            if (module.Segments.TryGetValue(entry.Segment, out SegmentInfo segment) && entry.Offset < segment.Size)
            {
                targets.Add((segment.Address + entry.Offset, entry.Thumb));
            }
        }

        bool started;
        lock (_lock)
        {
            _modules.Add(new ModuleRecord(module, [.. entries]));
            started = _started;
            if (!started)
            {
                _pending.AddRange(targets);
            }
        }

        if (started)
        {
            EnqueueInBackground(targets);
        }
    }

    /// <summary>The game runs: the JIT workers translate the blocks in the background (no pause), each block once.</summary>
    private void EnqueueInBackground(IReadOnlyList<(uint Pc, bool Thumb)> targets)
    {
        if (!Jit.Enabled || Jit.Chain.Length == 0)
        {
            return;
        }

        foreach ((uint pc, bool thumb) in targets)
        {
            Jit.Enqueue(_code.Get(pc, thumb));
        }
    }

    /// <summary>A module is about to be unloaded: what it translated is recorded first.</summary>
    public void ModuleUnloaded(LoadedModule module)
    {
        ModuleRecord? record;
        lock (_lock)
        {
            record = _modules.Find(r => r.Module == module);
            if (record != null)
            {
                _modules.Remove(record);
            }
        }

        if (record != null)
        {
            Collect([record]);
            SaveRecord(record);
        }
    }

    /// <summary>
    /// The boot step: translates the queued blocks (the code cache) and, with <paramref name="analyze"/>, the code the
    /// static analysis finds from the loaded modules' entry points (<see cref="CodePrecompiler"/>), before the process
    /// starts, on <paramref name="workers"/> threads (0: every core the process may use but one), reporting the progress;
    /// cancelling lets the process start at once (what is left compiles as the game reaches it). Later module loads
    /// queue their cached blocks to the JIT workers.
    /// </summary>
    public CodePrecompiler.Result PrecompilePending(Action<CodePrecompileProgress>? progress, CancellationToken cancel, bool analyze = true, int workers = 0)
    {
        (uint Pc, bool Thumb)[] targets;
        LoadedModule[] modules;
        lock (_lock)
        {
            targets = [.. _pending];
            _pending.Clear();
            _started = true;
            modules = new LoadedModule[_modules.Count];
            for (int i = 0; i < modules.Length; i++)
            {
                modules[i] = _modules[i].Module;
            }
        }

        CachedSeeds = targets.Length;
        var seeds = new List<(uint Pc, bool Thumb)>(targets);
        Func<uint, bool>? follow = null;
        if (analyze && modules.Length > 0)
        {
            foreach (LoadedModule module in modules)
            {
                seeds.AddRange(CodePrecompiler.ModuleEntryPoints(module));
            }

            follow = pc =>
            {
                foreach (LoadedModule module in modules)
                {
                    if (CodePrecompiler.InCode(module, pc))
                    {
                        return true;
                    }
                }

                return false;
            };
        }

        if (seeds.Count == 0 || !Jit.Enabled || Jit.Chain.Length == 0)
        {
            progress?.Invoke(new CodePrecompileProgress(0, 0, true));
            return default;
        }

        CodePrecompiler.Result result = CodePrecompiler.Run(_code, AddressSpace.Current, seeds, follow, workers,
            (done, known) => progress?.Invoke(new CodePrecompileProgress(done, known, false)), cancel);
        progress?.Invoke(new CodePrecompileProgress(result.Visited, result.Visited, true));
        return result;
    }

    /// <summary>Blocks the last <see cref="PrecompilePending"/> took from the code cache (the rest came from the analysis).</summary>
    public int CachedSeeds { get; private set; }

    /// <summary>The process starts without the boot step: the cached blocks queue to the JIT workers now, and those of
    /// modules loaded later as they load.</summary>
    public void StartInBackground()
    {
        (uint Pc, bool Thumb)[] targets;
        lock (_lock)
        {
            targets = [.. _pending];
            _pending.Clear();
            _started = true;
        }

        EnqueueInBackground(targets);
    }

    /// <summary>Adds the blocks translated so far to the records (before the translation cache is cleared).</summary>
    public void Collect()
    {
        ModuleRecord[] records;
        lock (_lock)
        {
            records = [.. _modules];
        }

        Collect(records);
    }

    private void Collect(ModuleRecord[] records)
    {
        if (records.Length == 0)
        {
            return;
        }

        foreach (Block block in _code.Blocks)
        {
            if (block.JitState != 2 || block.Invalidated)
            {
                continue;
            }

            foreach (ModuleRecord record in records)
            {
                foreach (KeyValuePair<int, SegmentInfo> segment in record.Module.Segments)
                {
                    uint offset = block.Start - segment.Value.Address;
                    if (block.Start >= segment.Value.Address && offset < segment.Value.Size)
                    {
                        lock (record.Known)
                        {
                            record.Known.Add(new CodeCacheEntry(segment.Key, offset, block.Thumb));
                        }
                    }
                }
            }
        }
    }

    /// <summary>Writes the files of the modules whose records grew (session end, and periodically while it runs).</summary>
    public void Save()
    {
        Collect();
        ModuleRecord[] records;
        lock (_lock)
        {
            records = [.. _modules];
        }

        foreach (ModuleRecord record in records)
        {
            SaveRecord(record);
        }
    }

    private void SaveRecord(ModuleRecord record)
    {
        if (_directory == null)
        {
            return;
        }

        CodeCacheEntry[] entries;
        lock (record.Known)
        {
            if (record.Known.Count == record.SavedCount)
            {
                return;
            }

            entries = [.. record.Known];
            record.SavedCount = entries.Length;
        }

        try
        {
            CodeCacheFile.Write(_directory, record.Module.ImageHash, _configurationKey, entries);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Util.Log.Warn($"Code cache: could not save {record.Module.Name}: {e.Message}");
        }
    }
}
