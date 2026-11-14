using VitaSharp.Core.Cpu;
using VitaSharp.Core.Cpu.Backends;
using VitaSharp.Core.Cpu.Native;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>
/// The dispatcher's count of why native code returned (<see cref="CpuCore.NativeReturns"/>, the PERF cycles line's
/// "returns/frame"): every native entry ends in exactly one reason, and a loop through linked and indirect exits only
/// comes back for the pacing window, its exit to a cold block and the first visit of each indirect target.
/// </summary>
[Collection("guest")]
public sealed unsafe class NativeReturnReasonTests(GuestMemoryFixture fixture)
{
    private const uint DoneSvc = 2;

    [Fact]
    public void EveryNativeReturnHasOneReason()
    {
        if (!NativeX64Compiler.Instance.IsSupported)
        {
            return; // the dispatcher runs native code only on an x64 host here (ARM64 blocks run in the Unicorn sandbox)
        }

        uint code = fixture.Memory.Alloc(0x40, "return reasons");
        GuestMemory.W32(code + 0x00, 0xE3A01601); // mov r1, #0x100000
        GuestMemory.W32(code + 0x04, 0xE28F3010); // add r3, pc, #16 (the function at +0x1C)
        GuestMemory.W32(code + 0x08, 0xE12FFF33); // loop: blx r3 (indirect exit)
        GuestMemory.W32(code + 0x0C, 0xE2511001); //       subs r1, r1, #1
        GuestMemory.W32(code + 0x10, 0x1AFFFFFC); //       bne loop (static exit, linked)
        GuestMemory.W32(code + 0x14, 0xEF000000 | DoneSvc); // svc #2
        GuestMemory.W32(code + 0x18, 0xE1A00000); // nop
        GuestMemory.W32(code + 0x1C, 0xE12FFF1E); // bx lr (indirect exit)
        long oldRate = CpuCore.InstructionsPerSecond;
        long oldHot = NativeX64Compiler.Instance.HotInstructions;
        string oldMode = Jit.Mode;
        bool oldTurbo = GuestClock.Turbo;
        bool oldSites = CpuCore.CountReturnSites;
        CpuCore.CountReturnSites = true;
        CpuCore.InstructionsPerSecond = CpuCore.DefaultClockHz; // a pacing window of ~83K cycles
        NativeX64Compiler.Instance.HotInstructions = 1;
        GuestClock.Turbo = true; // pacing checks run, without sleeping
        using JitTestState.Scope jitOn = JitTestState.Enable();
        try
        {
            Assert.True(Jit.Select("native"));
            using var core = new CpuCore(new BlockCache());
            CpuState* s = core.State;
            // warm-up from +4: 200 interpreted iterations make the loop's blocks hot
            s->R[1] = 200;
            s->R[15] = code + 4;
            s->Thumb = 0;
            Assert.Equal(ExitReason.Svc, core.Run());
            Assert.True(Jit.WaitIdle(TimeSpan.FromSeconds(60)));
            core.ResetNativeReturns();
            core.NativeEntries = 0;
            s->R[15] = code;
            Assert.Equal(ExitReason.Svc, core.Run());
            Assert.Equal(DoneSvc, s->SvcImm);
            Assert.Equal(0u, s->R[1]);

            long[] returns = core.NativeReturns;
            string report = core.NativeReturnReport(1);
            Assert.Equal(core.NativeEntries, returns.Sum());
            // 2^20 iterations of 3 blocks: millions of cycles, so tens of pacing windows
            Assert.True(returns[(int)CpuCore.NativeReturn.Window] >= 10, report);
            // the SVC block ran once in the warm-up (never hot): the loop's last exit leaves for it unlinked
            Assert.Equal(1, returns[(int)CpuCore.NativeReturn.StaticNoCode]);
            Assert.Equal(0, returns[(int)CpuCore.NativeReturn.Svc]);
            Assert.Equal(0, returns[(int)CpuCore.NativeReturn.OtherExit]);
            Assert.Equal(0, returns[(int)CpuCore.NativeReturn.LinkBreak]);
            Assert.Equal(0, returns[(int)CpuCore.NativeReturn.IndirectNoCode]);
            // the two indirect targets miss the table at most once each before they are republished
            Assert.True(returns[(int)CpuCore.NativeReturn.IndirectTableMiss] <= 2, report);
            // the busiest return sites name the reason and the guest target: here the cold SVC block
            Assert.Contains($"StaticNoCode {code + 0x14:X8} 1.0", core.NativeReturnSitesReport(1).Replace(',', '.'));

            // the reports are per window: the next one, with no native run in between, shows nothing but the entries
            Assert.Equal("entries 0.0", core.NativeReturnReport(2).Replace(',', '.'));
            Assert.Equal("", core.NativeReturnSitesReport(2));
            // a second run over the next ten frames reports its own returns, per frame of that window
            s->R[15] = code;
            Assert.Equal(ExitReason.Svc, core.Run());
            string second = core.NativeReturnReport(12).Replace(',', '.');
            Assert.Contains("StaticNoCode 0.1", second);
            Assert.StartsWith("entries ", second);
            Assert.Contains($"StaticNoCode {code + 0x14:X8} 0.1", core.NativeReturnSitesReport(12).Replace(',', '.'));
        }
        finally
        {
            CpuCore.CountReturnSites = oldSites;
            CpuCore.InstructionsPerSecond = oldRate;
            NativeX64Compiler.Instance.HotInstructions = oldHot;
            GuestClock.Turbo = oldTurbo;
            Jit.Select(oldMode);
        }
    }

    /// <summary>Handles SVC #1 (it waits until the host clock moves, like a blocking HLE wait) and leaves the rest to
    /// the dispatcher.</summary>
    private sealed class WaitingHandler : CpuCore.ISvcHandler
    {
        public int Calls;

        public bool TryHandleSvc(CpuState* s) => TryHandleSvcNative(s);

        public bool TryHandleSvcNative(CpuState* s)
        {
            if (s->SvcImm != 1)
            {
                return false;
            }

            Calls++;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            while (System.Diagnostics.Stopwatch.GetTimestamp() == t0)
            {
                Thread.SpinWait(10);
            }

            return true;
        }
    }

    /// <summary>
    /// HLE calls made from native code (SVC callouts) report their wall time apart from generated code when the entry is
    /// timed (<see cref="CpuCore.NativeHleTicks"/>): Killzone's Havok workers showed ~35 ms per frame of "native time" for
    /// 0.3 M guest cycles, most of it asleep in semaphore and event-flag waits made from native code.
    /// </summary>
    [Fact]
    public void HleCalloutTimeIsCountedOnlyInTimedEntries()
    {
        using var core = new CpuCore(new BlockCache());
        var handler = new WaitingHandler();
        core.SvcHandler = handler;
        CpuCore? oldCore = CpuCore.CurrentCore;
        CpuCore.CurrentCore = core;
        try
        {
            CpuState* s = core.State;
            s->SvcImm = 1;
            NativeRuntime.HleCall(s);
            Assert.Equal(1, handler.Calls);
            Assert.Equal(0, core.CalloutHleTicks); // not a timed entry: no clock reads
            Assert.Equal(ExitReason.None, s->Exit);

            core.TimingNative = true;
            NativeRuntime.HleCall(s);
            Assert.Equal(2, handler.Calls);
            Assert.True(core.CalloutHleTicks > 0);
            Assert.Equal(ExitReason.None, s->Exit);

            // a call the handler leaves to the dispatcher raises the SVC exit, timed or not
            s->SvcImm = 2;
            NativeRuntime.HleCall(s);
            Assert.Equal(ExitReason.Svc, s->Exit);
        }
        finally
        {
            CpuCore.CurrentCore = oldCore;
        }
    }
}
