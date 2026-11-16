# IL backend: developer notes

The IL backend (`Cpu/Backends/IlJitCompiler.cs`) is the second tier of the CPU chain (native > il > interp, see
[native-jit.md](native-jit.md)). It emits one `DynamicMethod` per guest block that calls the interpreter handler of
each instruction with the instruction word and its address as constants; RyuJIT (desktop) or Mono's JIT (Android)
inlines the handlers (`[AggressiveInlining]`) and folds the decoding. It takes the blocks the native backend
rejects and the blocks dominated by NEON callouts (native tier-up), and it is the first tier when the native
backend is off (write watchpoints, `--cpu=il`).

## Where the IL backend is kept (the only Reflection.Emit user)

The IL backend and its dump (`IlDump`) are the only code that uses `Reflection.Emit`. They are kept only for what
the native backends cannot run:

- blocks a native backend rejects (out of spill slots; 0 in the measured games) and the native tier-ups of blocks
  dominated by NEON callouts;
- write watchpoints (the native backends are off while `WriteWatch` is on) and `--cpu=il` / `VITASHARP_CPU=il`;
- hosts without a native backend for their architecture.

`IlJitCompiler.IsSupported` is `RuntimeFeature.IsDynamicCodeSupported`: under Native AOT the backend leaves the
chain (`no dynamic code`) and the chain is native > interpreter. The emitting method and `IlDump` carry `[RequiresDynamicCode]`, and
`Directory.Build.targets` turns IL3050/IL3051 into errors in the AOT-compatible projects, so no new dynamic-code
use enters without being seen. In the games measured on 11/11/2026 the IL tier ran 0.00% of the guest cycles, so it
costs nothing at run time.

## Where guest code runs (CPU tiers line)

Every run's end report (and the Android periodic summary) has a line

```
CPU backend report: CPU tiers: executed guest cycles N M: native X% (of which ~Y% in interpreter callouts),
il Z%, interpreter W% (blocks without compiled code, by state: not hot yet A%, queued B%, ...)
```

The shares are in guest cycles (the block cost model, about one per instruction), counted by the dispatcher from the
cycle budget every native run keeps: ARM64 native code counts no instructions by default (`A64Opt.NoBlockCounts`,
`VITASHARP_NATIVE_COUNTERS=1` turns the counts back on), so a share by instructions read 0% native on every phone run.
The interpreter-callout share is callout hits over cycles (approximate). The by-state breakdown is in interpreted
instructions.

and the `CPU backend:` line lists rejected blocks per backend with the reason
(`[register allocation out of spill slots: n blocks, m instructions]`, `no dynamic code` under Native AOT).

## IL dump mode (VITASHARP_IL_DUMP)

`VITASHARP_IL_DUMP=<folder>` makes the IL backend write every block it compiles a second time, with the same IL,
as a static method `GuestBlocks.blk_XXXXXXXX{a|t}(CpuState* s)` of a `PersistedAssemblyBuilder` assembly
(`il_blocks_000.dll`, 512 methods per file). The files are for reading only (they are never loaded):

- `index.tsv`: method, file, guest start, ARM/Thumb, length, executions, executed instructions; hottest first.
  Written with every assembly and when the process exits. Executions are counted by the dispatcher only in dump
  mode.
- Open a DLL in ILSpy with `VitaSharp.Core.dll` next to it (the handler calls resolve to it), or on the command
  line: `ilspycmd -t GuestBlocks -r <VitaSharp bin folder> <folder>/il_blocks_000.dll` (C#; add `--il` for IL). A
  block reads as one handler call per instruction with its word and address, the pc store before the last one and
  `if (Arm.CondPassed(s, cond))` around conditional instructions.

To see all blocks in the IL tier, run with `--cpu=il` (the native backend then takes none); with the default chain
only the native rejects and the tier-ups are dumped.

### Machine code of the hottest blocks (DOTNET_JitDisasm)

The DynamicMethod of a block has the same name as its dump method, so a second run of the same scenario can print
RyuJIT's code for the hottest ones (CoreCLR, .NET 7+ Release runtime; DynamicMethods are compiled once, fully
optimised, so there is no tier-0 copy):

```powershell
$names = (Get-Content <folder>\index.tsv | Select-Object -Skip 1 -First 10 | ForEach-Object { ($_ -split "`t")[0] }) -join ' '
$env:DOTNET_JitDisasm = $names; $env:DOTNET_JitStdOutFile = "<folder>\disasm.txt"
VitaSharp.Desktop test tests/scenarios/<name>.txt --data=<test data> --cpu=il
```

```bash
names="$(tail -n +2 <folder>/index.tsv | head -n 10 | cut -f1 | tr '\n' ' ')"
DOTNET_JitDisasm="${names}" DOTNET_JitStdOutFile=<folder>/disasm.txt \
  VitaSharp.Desktop test tests/scenarios/<name>.txt --data=<test data> --cpu=il
```

`DOTNET_JitDisasm="blk_*"` prints every block (large). The block addresses are stable between runs of the same
game version, so the names from one run's index select the same blocks in the next.
