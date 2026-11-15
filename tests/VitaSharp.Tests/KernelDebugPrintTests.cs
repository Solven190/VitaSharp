using VitaSharp.Core.Hle;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// The kernel's debug output (SceDebugForDriver / SceDebugForKernel, FW 3.74 SceSysmem) called through NID-bound stubs
/// as a kernel module calls it: kprintf into the TTY ring (ksceKernelGetTtyInfo) and the putchar handler, a registered
/// kprintf handler, the level filters, the "with info" prefix, assertions that print and go on, the panic that stops.
/// </summary>
[Collection("guest")]
public sealed class KernelDebugPrintTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint DebugForDriver = 0x88758561, DebugForKernel = 0x13D793B7;
    private const uint Printf = 0x391B74B7, Vprintf = 0x411C0733, PrintfLevel = 0x1A3F2AA4, PrintfWithInfo = 0x02B04343,
        Assert_ = 0x35A35322, AssertLevel = 0xFD753E7A, PrintfPanic = 0x00CCE39C, GetAssertLevel = 0x374B7868, GetTtyInfo = 0x62466B0A;
    private const uint SetAssertLevel = 0xA0CE3287, SetKprintfLevel = 0xC5889385, RegisterKprintf = 0x88AD6D0C,
        RegisterPutchar = 0x22546577, GetPutchar = 0x8D474850, Putchar = 0x2AABAEDA;

    private readonly HleHarness _harness = new(fixture, "kdebugprint");
    private readonly Dictionary<uint, uint> _stubs = [];
    private uint _next;

    public void Dispose() => _harness.Dispose();

    private uint Next(uint size)
    {
        if (_next == 0)
        {
            _next = _harness.Emu.Memory.Alloc(0x1000, "debug print test stubs");
        }

        uint at = _next;
        _next += size;
        return at;
    }

    private uint Stub(uint nid)
    {
        if (_stubs.TryGetValue(nid, out uint at))
        {
            return at;
        }

        Assert.True(_harness.Emu.Hle.Implements(nid), $"0x{nid:X8} is not implemented");
        uint library = nid is SetAssertLevel or SetKprintfLevel or RegisterKprintf or RegisterPutchar or GetPutchar or Putchar ? DebugForKernel : DebugForDriver;
        at = Next(16);
        Linker.WriteHleStub(at, _harness.Emu.Hle.GetHleFunctionIndex(nid, library, library == DebugForKernel ? "SceDebugForKernel" : "SceDebugForDriver"), nid);
        _harness.Emu.Code.Invalidate(at, 16);
        return _stubs[nid] = at;
    }

    private uint Host(string name, Action<HleContext> body)
    {
        uint at = Next(16);
        Linker.WriteHleStub(at, _harness.Emu.Hle.RegisterHostFunction(name, body), 0);
        _harness.Emu.Code.Invalidate(at, 16);
        return at;
    }

    private uint C(GuestThread thread, uint nid, params uint[] args) => thread.CallGuest(Stub(nid), args);

    /// <summary>The newest <paramref name="count"/> bytes of the kernel TTY.</summary>
    private string TtyTail(GuestThread thread, int count)
    {
        uint buffer = _harness.Alloc(0x1000);
        Assert.Equal(0x1000u, C(thread, GetTtyInfo, buffer, 0x2000));
        byte[] tty = GuestMemory.Span(buffer, 0x1000).ToArray();
        return System.Text.Encoding.ASCII.GetString(tty, 0x1000 - count, count);
    }

    [Fact]
    public void PrintfWritesIntoTheTtyAndReturnsTheCount()
    {
        _harness.OnGuest(t =>
        {
            Assert.Equal(8u, C(t, Printf, _harness.Str("k=%d %s\n"), 42, _harness.Str("ok"), 0));
            Assert.EndsWith("k=42 ok\n", TtyTail(t, 8));
            Assert.Equal(3u, C(t, Vprintf, _harness.Str("%x\n"), _harness.Word(0xAB)));
            Assert.EndsWith("k=42 ok\nab\n", TtyTail(t, 11));
        });
    }

    [Fact]
    public void PutcharAndKprintfHandlersSeeWhatTheKernelPrints()
    {
        var characters = new List<int>();
        _harness.OnGuest(t =>
        {
            Assert.Equal(0u, C(t, GetPutchar));
            uint putchar = Host("test:putchar", context => characters.Add((int)context.Gpr(1)));
            C(t, RegisterPutchar, putchar, 0x55);
            Assert.Equal(putchar, C(t, GetPutchar));
            Assert.Equal(2u, C(t, Printf, _harness.Str("hi")));
            Assert.Equal([0x200, 'h', 'i', 0x201], characters);
            Assert.Equal(1u, C(t, Putchar, 'z'));
            Assert.Equal('z', characters[^1]);

            uint seen = 0;
            uint kprintf = Host("test:kprintf", context =>
            {
                seen = context.Gpr(0);
                context.Return(77);
            });
            C(t, RegisterKprintf, kprintf, 0x1234);
            Assert.Equal(77u, C(t, Printf, _harness.Str("x")));
            Assert.Equal(0x1234u, seen);
        });
    }

    [Fact]
    public void TheLevelAndInfoVariantsFollowTheFirmware()
    {
        _harness.OnGuest(t =>
        {
            Assert.Equal(0u, C(t, PrintfLevel, 1, _harness.Str("hidden\n")));
            Assert.Equal(6u, C(t, PrintfLevel, 0, _harness.Str("shown\n")));
            Assert.Equal(0u, C(t, SetKprintfLevel, 2));
            Assert.Equal(5u, C(t, PrintfLevel, 2, _harness.Str("now\n%c"), 'Z'));
            Assert.EndsWith("shown\nnow\nZ", TtyTail(t, 11));

            uint info = _harness.Alloc(0x18);
            GuestMemory.Fill(info, 0, 0x18);
            GuestMemory.W32(info, 0xDEADBEEF);
            GuestMemory.W32(info + 0xC, _harness.Str("mod"));
            Assert.Equal(4u, C(t, PrintfWithInfo, 3, info, _harness.Str("msg\n")));
            Assert.EndsWith("0:0xdeadbeef(mod):msg\n", TtyTail(t, 22));
        });
    }

    [Fact]
    public void AFailedAssertionPrintsAndGoesOnAndAPanicStops()
    {
        _harness.OnGuest(t =>
        {
            C(t, Assert_, 1, 0);
            C(t, Assert_, 0, 0);
            Assert.Contains("*** Kernel Assertion failed at <0:0x0000000000000000:0x00000000:0x", TtyTail(t, 100));
            // above the assertion level nothing prints
            Assert.Equal(0u, C(t, SetAssertLevel, 1));
            Assert.Equal(1u, C(t, GetAssertLevel));
            string before = TtyTail(t, 200);
            C(t, AssertLevel, 2, 0, 0);
            Assert.Equal(before, TtyTail(t, 200));
            C(t, AssertLevel, 1, 0, 0);
            Assert.NotEqual(before, TtyTail(t, 200));
        });

        Exception? fault = null;
        HleHarness.GuestRun run = _harness.Start(t =>
        {
            try
            {
                C(t, PrintfPanic, 0, _harness.Str("bad %d\n"), 7);
            }
            catch (GuestFaultException e)
            {
                fault = e;
            }
        });
        _harness.Join(run);
        Assert.NotNull(fault);
        _harness.OnGuest(t => Assert.Contains("*** Kernel Panic at <0:", TtyTail(t, 200)));
        _harness.OnGuest(t => Assert.EndsWith("bad 7\n", TtyTail(t, 6)));
    }
}
