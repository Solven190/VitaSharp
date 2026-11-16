using VitaSharp.Core.Gpu;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SCE_DISPLAY_SETBUF_IMMEDIATE flips are at most one per vblank. On the Vita an IMMEDIATE flip only moves the scanout
/// pointer and the panel shows at most one frame per vblank; a game that flips IMMEDIATE without any vblank wait
/// (Unity's "don't sync": The Deer God) is bounded by its GPU, which is never the brake here, so without the rule it
/// rendered 140-650 frames per second nobody saw. The guest clock is frozen while the flips run: whether two flips fall
/// in one vblank is then decided by the test, never by how fast the host gets from one call to the next.
/// </summary>
[Collection("guest")]
public sealed class DisplayImmediatePaceTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint Immediate = 0, NextFrame = 1;
    private readonly HleHarness _harness = new(fixture, "dispimm");

    public void Dispose() => _harness.Dispose();

    /// <summary>A display that presents nothing: the flips' presents complete at once.</summary>
    private sealed class NullDisplay : IGpuBackend
    {
        public long PresentCount => 0;
        public byte[]? ExportDumpState() => null;

        public void Execute(GpuCommand command)
        {
        }

        public void Dispose()
        {
        }
    }

    private uint FrameBuf()
    {
        uint pixels = _harness.Alloc(64 * 64 * 4);
        uint param = _harness.Alloc(0x20);
        GuestMemory.W32(param, 0x18);
        GuestMemory.W32(param + 4, pixels);
        GuestMemory.W32(param + 8, 64);
        GuestMemory.W32(param + 12, 0);
        GuestMemory.W32(param + 16, 64);
        GuestMemory.W32(param + 20, 64);
        return param;
    }

    /// <summary>Runs <paramref name="flips"/> back-to-back flips on a guest thread with the guest clock frozen and
    /// returns how many returned before the clock moved, the flips held and the vblank count each one returned at.</summary>
    private (int ReturnedWhileFrozen, long Held, long[] Vcounts) FlipWhileFrozen(uint[] syncs, FrameRateMode mode)
    {
        _harness.Emu.Gpu.Backend = new NullDisplay();
        FrameRateMode oldMode = FrameRate.Mode;
        bool oldTurbo = GuestClock.Turbo;
        FrameRate.Mode = mode;
        GuestClock.Turbo = false;
        Display display = _harness.Module<Display>();
        uint[] buffers = [FrameBuf(), FrameBuf()];
        long[] vcounts = new long[syncs.Length];
        int returned = 0;
        long heldBefore = Display.ImmediateFlipsPaced;
        GuestRun? run = null;
        int returnedWhileFrozen;
        long held;
        GuestClock.Freeze();
        try
        {
            run = _harness.Start(_ =>
            {
                for (int index = 0; index < syncs.Length; index++)
                {
                    Assert.Equal(0, Call(display, "sceDisplaySetFrameBuf", buffers[index & 1], syncs[index]));
                    vcounts[index] = VBlank.Count;
                    Interlocked.Increment(ref returned);
                }
            });
            // the guest either finishes or is held in a flip (the counter goes up before the wait)
            Until(() => Volatile.Read(ref returned) == syncs.Length || Interlocked.Read(ref Display.ImmediateFlipsPaced) > heldBefore,
                "the flips to finish or one to be held");
            Thread.Sleep(50); // a held flip stays held while the clock is frozen; a wrong one would return here
            returnedWhileFrozen = Volatile.Read(ref returned);
            held = Interlocked.Read(ref Display.ImmediateFlipsPaced) - heldBefore;
        }
        finally
        {
            GuestClock.Thaw();
            FrameRate.Mode = oldMode;
            GuestClock.Turbo = oldTurbo;
        }
        _harness.Join(run!);
        _harness.Emu.Gpu.WaitIdle();
        _harness.Emu.Gpu.Backend = null;
        return (returnedWhileFrozen, held, vcounts);
    }

    [Fact]
    public void ASecondImmediateFlipInTheSameVblankWaitsForTheNextOne()
    {
        (int returnedWhileFrozen, long held, long[] vcounts) = FlipWhileFrozen([Immediate, Immediate], FrameRateMode.Native);

        Assert.Equal(1, returnedWhileFrozen);
        Assert.Equal(1, held);
        Assert.True(vcounts[1] >= vcounts[0] + 1, $"the held flip returned at vblank {vcounts[1]}, the first at {vcounts[0]}");
    }

    [Fact]
    public void NextFrameFlipsAreNotHeld()
    {
        (int returnedWhileFrozen, long held, _) = FlipWhileFrozen([NextFrame, NextFrame, NextFrame], FrameRateMode.Native);

        Assert.Equal(3, returnedWhileFrozen);
        Assert.Equal(0, held);
    }

    [Fact]
    public void TheUnlimitedFrameRateModeDoesNotHoldImmediateFlips()
    {
        (int returnedWhileFrozen, long held, _) = FlipWhileFrozen([Immediate, Immediate, Immediate], FrameRateMode.Unlimited);

        Assert.Equal(3, returnedWhileFrozen);
        Assert.Equal(0, held);
    }
}
