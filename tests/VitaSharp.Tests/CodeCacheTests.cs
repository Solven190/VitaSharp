using System.Runtime.InteropServices;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Cpu.Backends;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>
/// "Precompilar código ARM" (<see cref="CodeCache"/>, <see cref="CodePrecompiler"/>): a module's code is translated
/// before the process runs, from the code cache of earlier sessions (per module image, valid at any load address) and
/// from the static analysis of its entry points. The precompiled code must give what the interpreter gives, the game
/// must then compile nothing for that code, rewritten code must still be dropped, and a cache file of another
/// translator configuration (an older build), image or a corrupted one must be ignored.
/// </summary>
[Collection("guest")]
public sealed unsafe class CodeCacheTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vitasharp-codecache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static bool NativeHost => NativeX64Compiler.Instance.IsSupported || NativeArm64Compiler.Instance.IsSupported;

    /// <summary>
    /// A loop calling a function: main (C+0) sets r0 = 0, r1 = 100; loop (C+8) calls func (C+0x20, r0 += 3) and counts r1
    /// down; then SVC #2. r0 ends at 300. Blocks: C+0, C+8 (the BL), C+0xC (the return point), C+0x14 (SVC), C+0x20.
    /// </summary>
    private static void WriteProgram(uint code, uint addend = 3)
    {
        GuestMemory.W32(code + 0x00, 0xE3A00000); // mov r0, #0
        GuestMemory.W32(code + 0x04, 0xE3A01064); // mov r1, #100
        GuestMemory.W32(code + 0x08, 0xEB000004); // loop: bl func
        GuestMemory.W32(code + 0x0C, 0xE2511001); //       subs r1, r1, #1
        GuestMemory.W32(code + 0x10, 0x1AFFFFFC); //       bne loop
        GuestMemory.W32(code + 0x14, 0xEF000002); //       svc #2
        GuestMemory.W32(code + 0x18, 0xE12FFF1E); //       bx lr (never reached)
        GuestMemory.W32(code + 0x20, 0xE2800000 | addend); // func: add r0, r0, #addend
        GuestMemory.W32(code + 0x24, 0xE12FFF1E); //       bx lr
    }

    private static LoadedModule Module(uint code, ulong imageHash) => new()
    {
        Name = "codecache test",
        ImageHash = imageHash,
        StartEntry = code, // ARM (bit 0 clear)
        Segments = { [0] = new SegmentInfo(code, 0x81000000, 0x100) },
        SegmentFlags = { [0] = (5, 0x100, 0x100) }, // R X
    };

    private static uint Run(CpuCore core, uint entry)
    {
        CpuState* s = core.State;
        s->R[0] = 0;
        s->R[15] = entry;
        s->Thumb = 0;
        Assert.Equal(ExitReason.Svc, core.Run());
        return s->R[0];
    }

    /// <summary>Blocks the JIT compiled while the program ran (not precompiled).</summary>
    private static HashSet<uint> Compiled(BlockCache cache) =>
        cache.Blocks.Where(b => b.JitState == 2 && !b.Ahead && !b.Invalidated).Select(b => b.Start).ToHashSet();

    /// <summary>Blocks that have compiled code, run or not (precompiled ones wait unpublished until first reached).</summary>
    private static HashSet<uint> Translated(BlockCache cache) =>
        cache.Blocks.Where(b => b.JitState is 2 or Jit.AheadPending && !b.Invalidated).Select(b => b.Start).ToHashSet();
    /// <summary>Runs <paramref name="body"/> with the native backend selected (fast pacing, every block hot at once).</summary>
    private static void WithNative(Action body)
    {
        long oldX64 = NativeX64Compiler.Instance.HotInstructions;
        long oldArm64 = NativeArm64Compiler.Instance.HotInstructions;
        string oldMode = Jit.Mode;
        bool oldTurbo = GuestClock.Turbo;
        using JitTestState.Scope jitOn = JitTestState.Enable();
        try
        {
            NativeX64Compiler.Instance.HotInstructions = 1;
            NativeArm64Compiler.Instance.HotInstructions = 1;
            GuestClock.Turbo = true;
            Assert.True(Jit.Select("native"));
            body();
        }
        finally
        {
            NativeX64Compiler.Instance.HotInstructions = oldX64;
            NativeArm64Compiler.Instance.HotInstructions = oldArm64;
            GuestClock.Turbo = oldTurbo;
            Jit.Select(oldMode);
        }
    }

    [Fact]
    public void FileRoundTripsAndRejectsOtherBuildsImagesAndCorruption()
    {
        ulong key = CodeCacheFile.KeyOf("native", Architecture.X64);
        CodeCacheEntry[] entries = [new(0, 0x10, true), new(1, 0x2000, false), new(0, 0xFFFC, true)];
        byte[] data = CodeCacheFile.Serialize(0xABCDEF, key, entries);
        Assert.Equal(entries, CodeCacheFile.Deserialize(data, 0xABCDEF, key));

        // a file of an older translator (block splitting rules), another backend or host: rejected
        Assert.Null(CodeCacheFile.Deserialize(data, 0xABCDEF, CodeCacheFile.KeyOf("native", Architecture.X64, CodeCacheFile.TranslatorVersion + 1)));
        Assert.Null(CodeCacheFile.Deserialize(CodeCacheFile.Serialize(0xABCDEF, CodeCacheFile.KeyOf("native", Architecture.X64, CodeCacheFile.TranslatorVersion - 1), entries), 0xABCDEF, key));
        Assert.Null(CodeCacheFile.Deserialize(data, 0xABCDEF, CodeCacheFile.KeyOf("native", Architecture.Arm64)));
        Assert.Null(CodeCacheFile.Deserialize(data, 0xABCDEF, CodeCacheFile.KeyOf("il", Architecture.X64)));
        // another module image
        Assert.Null(CodeCacheFile.Deserialize(data, 0xABCDEE, key));
        // a flipped bit anywhere, a truncated file, an empty one
        for (int i = 0; i < data.Length; i++)
        {
            byte[] corrupt = (byte[])data.Clone();
            corrupt[i] ^= 0x10;
            Assert.Null(CodeCacheFile.Deserialize(corrupt, 0xABCDEF, key));
        }

        Assert.Null(CodeCacheFile.Deserialize(data.AsSpan(0, data.Length - 1), 0xABCDEF, key));
        Assert.Null(CodeCacheFile.Deserialize([], 0xABCDEF, key));

        // on disk, through the module's file name
        CodeCacheFile.Write(_directory, 0xABCDEF, key, entries);
        Assert.Equal(entries, CodeCacheFile.Read(_directory, 0xABCDEF, key));
        Assert.Null(CodeCacheFile.Read(_directory, 0xABCDEE, key));
    }

    [Fact]
    public void StaticAnalysisTranslatesTheModuleBeforeItRunsAndTheGameCompilesNothing()
    {
        if (!NativeHost)
        {
            return; // native code runs in this dispatcher only on an x64 or ARM64 host
        }

        uint code = fixture.Memory.Alloc(0x1000, "codecache analysis");
        WriteProgram(code);
        uint interpreted;
        string oldMode = Jit.Mode;
        try
        {
            Assert.True(Jit.Select("interp"));
            using var interpreterCore = new CpuCore(new BlockCache());
            interpreted = Run(interpreterCore, code);
        }
        finally
        {
            Jit.Select(oldMode);
        }

        Assert.Equal(300u, interpreted);
        WithNative(() =>
        {
            // without the precompilation the game compiles its blocks while it runs
            var control = new BlockCache();
            using (var controlCore = new CpuCore(control))
            {
                Assert.Empty(Compiled(control));
                Assert.Equal(interpreted, Run(controlCore, code));
                Assert.True(Jit.WaitIdle(TimeSpan.FromSeconds(60)));
                Assert.NotEmpty(Compiled(control));
            }

            var cache = new BlockCache();
            var codeCache = new CodeCache(_directory, cache);
            codeCache.ModuleLoaded(Module(code, 1));
            // a first session: the boot step waits for the code reached from the module's entry point
            CodePrecompiler.Result result = codeCache.PrecompilePending(null, CancellationToken.None, workers: 2);
            HashSet<uint> before = Translated(cache);
            // every block the program runs: the call target, the return point after the call (no static exit names it)
            // and both ways of the loop's branch
            Assert.Superset(new HashSet<uint> { code, code + 0x08, code + 0x0C, code + 0x14, code + 0x20 }, before);
            Assert.True(result.Translated >= 5 && result.Found >= 4, result.ToString());
            Assert.All(before, start => Assert.InRange(start, code, code + 0xFF));
            // precompiled code is published when the guest first reaches it
            Assert.All(cache.Blocks.Where(b => b.Ahead), b => Assert.Equal(0, b.NativeEntry));

            using (var core = new CpuCore(cache))
            {
                Assert.Equal(interpreted, Run(core, code));
            }

            Assert.True(Jit.WaitIdle(TimeSpan.FromSeconds(60)));
            Assert.Empty(Compiled(cache)); // nothing compiled while the program ran
            Assert.NotEqual(0, cache.Get(code + 0x20, false).NativeEntry);

            // the cache records what ran, in the order it first ran; not the precompiled code that never ran
            codeCache.Save();
            List<CodeCacheEntry>? recorded = CodeCacheFile.Read(_directory, 1, CodeCacheFile.ConfigurationKey);
            Assert.NotNull(recorded);
            Assert.Equal([0x00u, 0x20, 0x0C, 0x08, 0x14], recorded.Select(e => e.Offset));
        });
    }

    [Fact]
    public void BackgroundPrecompilationDoesNotWaitAndStillFillsIn()
    {
        if (!NativeHost)
        {
            return;
        }

        uint code = fixture.Memory.Alloc(0x1000, "codecache background");
        WriteProgram(code);
        WithNative(() =>
        {
            var cache = new BlockCache();
            var codeCache = new CodeCache(null, cache);
            codeCache.ModuleLoaded(Module(code, 3));
            codeCache.PrecompilePending(null, CancellationToken.None, wait: false);
            CodePrecompiler run = Assert.Single(codeCache.Runs);
            Assert.True(run.WaitFinished(TimeSpan.FromSeconds(60)));
            Assert.Superset(new HashSet<uint> { code, code + 0x08, code + 0x0C, code + 0x14, code + 0x20 }, Translated(cache));
            using var core = new CpuCore(cache);
            Assert.Equal(300u, Run(core, code));
            Assert.True(Jit.WaitIdle(TimeSpan.FromSeconds(60)));
            Assert.Empty(Compiled(cache));

            // a module loaded while the process runs is analysed in the background too; a stopped cache starts nothing
            uint late = fixture.Memory.Alloc(0x1000, "codecache late module");
            WriteProgram(late);
            codeCache.ModuleLoaded(Module(late, 4));
            Assert.Equal(2, codeCache.Runs.Count);
            Assert.True(codeCache.Runs[1].WaitFinished(TimeSpan.FromSeconds(60)));
            Assert.Contains(late + 0x20, Translated(cache));
            codeCache.Stop();
            codeCache.ModuleLoaded(Module(fixture.Memory.Alloc(0x1000, "codecache after stop"), 5));
            Assert.Equal(2, codeCache.Runs.Count);
        });
    }
    [Fact]
    public void RecordedBlocksPrecompileInTheNextSessionAtAnotherLoadAddress()
    {
        if (!NativeHost)
        {
            return;
        }

        uint first = fixture.Memory.Alloc(0x1000, "codecache session 1");
        uint second = fixture.Memory.Alloc(0x1000, "codecache session 2");
        Assert.NotEqual(first, second);
        WriteProgram(first);
        WriteProgram(second);
        WithNative(() =>
        {
            // session 1: the JIT compiles the hot blocks; the session end records them
            var cache1 = new BlockCache();
            var codeCache1 = new CodeCache(_directory, cache1);
            codeCache1.ModuleLoaded(Module(first, 0x5EED));
            codeCache1.Analyze = false;
            codeCache1.PrecompilePending(null, CancellationToken.None, wait: false);
            using (var core1 = new CpuCore(cache1))
            {
                Assert.Equal(300u, Run(core1, first));
                Assert.True(Jit.WaitIdle(TimeSpan.FromSeconds(60)));
            }

            var recorded = Compiled(cache1).Select(start => start - first).ToHashSet();
            Assert.Empty(Translated(cache1).Except(Compiled(cache1)));
            Assert.NotEmpty(recorded);
            codeCache1.Save();
            Assert.True(File.Exists(CodeCacheFile.PathFor(_directory, 0x5EED)));

            // session 2: the same module image somewhere else; the boot step waits for the recorded blocks only
            var cache2 = new BlockCache();
            var codeCache2 = new CodeCache(_directory, cache2) { Analyze = false };
            codeCache2.ModuleLoaded(Module(second, 0x5EED));
            Assert.Equal(recorded.Count, codeCache2.PendingCount);
            CodePrecompiler.Result result = codeCache2.PrecompilePending(null, CancellationToken.None, workers: 2);
            Assert.Equal(recorded.Count, result.Translated);
            HashSet<uint> before = Translated(cache2);
            Assert.Equal(recorded, before.Select(start => start - second).ToHashSet());
            using var core2 = new CpuCore(cache2);
            Assert.Equal(300u, Run(core2, second));
            Assert.True(Jit.WaitIdle(TimeSpan.FromSeconds(60)));
            Assert.Empty(Compiled(cache2));
            Assert.Equal(before, Translated(cache2));

            // another image (an updated module) finds nothing; a file of an older build is ignored
            var cache3 = new BlockCache();
            var otherImage = new CodeCache(_directory, cache3);
            otherImage.ModuleLoaded(Module(second, 0x5EEE));
            Assert.Equal(0, otherImage.PendingCount);
            var olderBuild = new CodeCache(_directory, cache3, CodeCacheFile.KeyOf(Jit.Mode, RuntimeInformation.ProcessArchitecture, CodeCacheFile.TranslatorVersion + 1));
            olderBuild.ModuleLoaded(Module(second, 0x5EED));
            Assert.Equal(0, olderBuild.PendingCount);
        });
    }

    [Fact]
    public void PrecompiledCodeThatTheGameRewritesIsDropped()
    {
        if (!NativeHost)
        {
            return;
        }

        uint code = fixture.Memory.Alloc(0x1000, "codecache rewrite");
        WriteProgram(code);
        WithNative(() =>
        {
            var cache = new BlockCache();
            var codeCache = new CodeCache(null, cache);
            codeCache.ModuleLoaded(Module(code, 2));
            codeCache.PrecompilePending(null, CancellationToken.None, workers: 1);
            Block func = cache.Get(code + 0x20, false);
            Assert.Equal(Jit.AheadPending, func.JitState);

            // the game writes another addend into func (the emulator drops the translations of the written range)
            GuestMemory.W32(code + 0x20, 0xE2800007);
            cache.Invalidate(code + 0x20, 4);
            Assert.True(func.Invalidated);
            using var core = new CpuCore(cache);
            Assert.Equal(700u, Run(core, code));
        });
    }

    [Fact]
    public void EntryPointsComeFromTheModuleInfoExportsAndExceptionIndex()
    {
        uint code = fixture.Memory.Alloc(0x1000, "codecache entries");
        var module = new LoadedModule
        {
            StartEntry = code + 0x101, // Thumb
            StopEntry = code + 0x201,
            ExidxTop = code + 0x800,
            ExidxEnd = code + 0x818,
            Segments = { [0] = new SegmentInfo(code, code, 0x1000) },
            SegmentFlags = { [0] = (5, 0x1000, 0x1000) },
        };
        module.ExportedFunctionEntries.Add(code + 0x301);
        // .ARM.exidx: prel31 offsets from each entry to its function, then the unwind word
        GuestMemory.W32(code + 0x800, unchecked((uint)(0x400 - 0x800)) & 0x7FFFFFFF);
        GuestMemory.W32(code + 0x804, 1); // EXIDX_CANTUNWIND
        GuestMemory.W32(code + 0x808, unchecked((uint)(0x500 - 0x808)) & 0x7FFFFFFF);
        GuestMemory.W32(code + 0x80C, 1);
        GuestMemory.W32(code + 0x810, 0x80000000); // not a function offset
        GuestMemory.W32(code + 0x814, 1);
        List<((uint Pc, bool Thumb) Target, CodePrecompiler.Priority Priority)> entries = CodePrecompiler.ModuleEntryPoints(module);
        Assert.Equal(
            [(code + 0x100, true), (code + 0x200, true), (code + 0x300, true), (code + 0x400, true), (code + 0x500, true)],
            entries.Select(e => e.Target));
        // the module's own entry points are followed first; the exception index only names functions
        Assert.Equal(
            [CodePrecompiler.Priority.Flow, CodePrecompiler.Priority.Flow, CodePrecompiler.Priority.Flow, CodePrecompiler.Priority.Index, CodePrecompiler.Priority.Index],
            entries.Select(e => e.Priority));
        Assert.True(CodePrecompiler.InCode(module, code + 0xFFF));
        Assert.False(CodePrecompiler.InCode(module, code + 0x1000));
        module.SegmentFlags[0] = (6, 0x1000, 0x1000); // R W: data
        Assert.False(CodePrecompiler.InCode(module, code + 0x100));
    }
}
