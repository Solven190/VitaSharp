using VitaSharp.Core.Gpu;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The picture never falls behind a guest that draws with the CPU. The Vita's display controller reads the framebuffer
/// while it scans it out, so a game that flips buffer A, then writes buffer B and flips it, then writes A again, has
/// each frame on screen as it drew it. Here a present reads guest memory when the GPU worker reaches it: without a
/// bound, a guest ahead of a slow worker rewrote A before A's first present ran, and the screen (and the frame dumps
/// and GPU dumps) showed later frames than the flips (the runner's process tests: a frame of the "pressed" colour where
/// the "idle" one was flipped, under load and on the macOS CI). A flip from a game thread waits until the present of the
/// flip before it has run: at most one present in flight.
/// </summary>
[Collection("guest")]
public sealed class DisplayPresentLagTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "displag");

    public void Dispose() => _harness.Dispose();

    /// <summary>A display that is slow until <see cref="Open"/> is set, recording the first word of each presented
    /// framebuffer as it reads it.</summary>
    private sealed class SlowDisplay : IGpuBackend
    {
        public readonly ManualResetEventSlim Open = new();
        public readonly List<uint> Shown = [];
        public int Started;
        public long PresentCount => 0;
        public byte[]? ExportDumpState() => null;

        public void Execute(GpuCommand command)
        {
            if (command is PresentCommand p)
            {
                Interlocked.Increment(ref Started);
                Open.Wait();
                lock (Shown)
                {
                    Shown.Add(GuestMemory.R32(p.Frame.Address));
                }
            }
        }

        public void Dispose()
        {
        }
    }

    private uint FrameBuf(uint baseAddr)
    {
        uint p = _harness.Alloc(0x20);
        GuestMemory.W32(p, 0x18);
        GuestMemory.W32(p + 4, baseAddr);
        GuestMemory.W32(p + 8, 64);
        GuestMemory.W32(p + 12, 0);
        GuestMemory.W32(p + 16, 64);
        GuestMemory.W32(p + 20, 64);
        return p;
    }

    [Fact]
    public void EachFlippedFrameIsShownAsTheGuestDrewIt()
    {
        var display = new SlowDisplay();
        _harness.Emu.Gpu.Backend = display;
        try
        {
            Display d = _harness.Module<Display>();
            uint a = _harness.Alloc(64 * 64 * 4), b = _harness.Alloc(64 * 64 * 4);
            uint flipA = FrameBuf(a), flipB = FrameBuf(b);
            long waitsBefore = Display.PresentWaits;
            int flips = 0;
            // double buffering: frame 1 into A, frame 2 into B, frame 3 into A again, frame 4 into B
            GuestRun guest = _harness.Start(_ =>
            {
                for (uint frame = 1; frame <= 4; frame++)
                {
                    uint buffer = (frame & 1) != 0 ? a : b;
                    GuestMemory.W32(buffer, frame);
                    Assert.Equal(0, Call(d, "sceDisplaySetFrameBuf", (frame & 1) != 0 ? flipA : flipB, 1u));
                    Interlocked.Increment(ref flips);
                }
            });

            // the display is stuck in frame 1's present: the guest either waits at its next flip (as it must) or ran
            // on and rewrote A meanwhile; then the display catches up
            Until(() => Volatile.Read(ref display.Started) >= 1 && (Display.PresentWaits > waitsBefore || Volatile.Read(ref flips) == 4), "the guest to wait for the display or to finish");
            int flipsWhileStuck = Volatile.Read(ref flips);
            display.Open.Set();
            _harness.Join(guest);
            _harness.Emu.Gpu.WaitIdle();

            Assert.Equal(new uint[] { 1, 2, 3, 4 }, display.Shown);
            // the flip after a present still in flight is the one that waited: frame 2's, behind frame 1's present
            Assert.Equal(1, flipsWhileStuck);
            Assert.True(Display.PresentWaits > waitsBefore);
        }
        finally
        {
            display.Open.Set();
            _harness.Emu.Gpu.WaitIdle();
            _harness.Emu.Gpu.Backend = null;
        }
    }
}
