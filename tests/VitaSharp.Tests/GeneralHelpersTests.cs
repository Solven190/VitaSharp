using System.Text;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// Small FW 3.74 user exports no installed program imports (decompiled, study only): np_common's heap create/destroy
/// and UTF-8 checks, np_matching2's signaling port, libkernel's TLS slot of the calling thread checked against a
/// thread id and its count helper. The exports are found by name so the proof without them still builds.
/// </summary>
[Collection("guest")]
public sealed class GeneralHelpersTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int CommonNull = unchecked((int)0x80550601);
    private const int CommonInvalidText = unchecked((int)0x80550608);

    private readonly HleHarness _harness = new(fixture, "genhelp");

    public void Dispose() => _harness.Dispose();

    private HleModule ModuleNamed(string name) => _harness.Emu.Hle.Modules.Single(module => module.GetType().Name == name);

    private uint Utf8(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text + "\0");
        uint address = _harness.Alloc((uint)bytes.Length + 4);
        bytes.CopyTo(GuestMemory.Span(address, bytes.Length));
        return address;
    }

    private uint Bytes(params byte[] bytes)
    {
        uint address = _harness.Alloc((uint)bytes.Length + 4);
        bytes.CopyTo(GuestMemory.Span(address, bytes.Length));
        return address;
    }

    [Fact]
    public void TheCommonHeapIsMadeOnTheGivenMemoryAndDestroyed()
    {
        HleModule np = ModuleNamed("NpManager");
        uint memory = _harness.Alloc(0x10000);
        Assert.Equal(0, Call(np, "NID_619FB198", 0x10000u, memory));
        uint block = Convert.ToUInt32(Invoke(np, "sceNpCommonMallocNpServerName", 0x40u));
        Assert.InRange(block, memory, memory + 0x10000 - 0x40);
        Invoke(np, "sceNpCommonFreeNpServerName", block);
        Invoke(np, "NID_622F004B");
        Assert.Equal(0u, Convert.ToUInt32(Invoke(np, "sceNpCommonMallocNpServerName", 0x40u)));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 3)]
    [InlineData("a\tb\r\nc", 6)]
    [InlineData("\u00E7\u00E3\u20AC\U0001F600", 4)]
    public void TheCharacterCountCountsUtf8Characters(string text, int expected)
    {
        Assert.Equal(expected, Call(ModuleNamed("NpManager"), "NID_73BDFF32", Utf8(text)));
    }

    [Fact]
    public void ControlCharactersAndBrokenSequencesAreRefused()
    {
        HleModule np = ModuleNamed("NpManager");
        Assert.Equal(CommonNull, Call(np, "NID_73BDFF32", 0u));
        Assert.Equal(CommonInvalidText, Call(np, "NID_73BDFF32", Bytes((byte)'a', 0x01, 0)));
        Assert.Equal(CommonInvalidText, Call(np, "NID_73BDFF32", Bytes(0x0B, 0)));
        Assert.Equal(CommonInvalidText, Call(np, "NID_73BDFF32", Bytes(0x7F, 0)));
        Assert.Equal(CommonInvalidText, Call(np, "NID_73BDFF32", Bytes(0xC1, 0x81, 0))); // overlong lead
        Assert.Equal(CommonInvalidText, Call(np, "NID_73BDFF32", Bytes(0xE3, 0x81, 0x41, 0))); // missing continuation
        Assert.Equal(CommonInvalidText, Call(np, "NID_73BDFF32", Bytes(0xF8, 0x80, 0x80, 0x80, 0x80, 0)));
        Assert.Equal(CommonInvalidText, Call(np, "NID_73BDFF32", Bytes(0x80, 0)));
    }

    [Fact]
    public void TheCharacterStepStopsAfterTheCountOrAtTheEnd()
    {
        HleModule np = ModuleNamed("NpManager");
        uint text = Utf8("a\u00E7\u20AC\U0001F600b");
        uint end = _harness.Word(0);
        Assert.Equal(0, Call(np, "NID_A04C77A1", 3u, text, end));
        Assert.Equal(text + 1 + 2 + 3, GuestMemory.R32(end));
        Assert.Equal(0, Call(np, "NID_A04C77A1", 0u, text, end));
        Assert.Equal(text, GuestMemory.R32(end));
        Assert.Equal(0, Call(np, "NID_A04C77A1", 99u, text, end));
        Assert.Equal(text + 1 + 2 + 3 + 4 + 1, GuestMemory.R32(end));
        Assert.Equal(CommonNull, Call(np, "NID_A04C77A1", 1u, 0u, end));
        uint broken = Bytes((byte)'a', 0x1F, 0);
        GuestMemory.W32(end, 0x1234);
        Assert.Equal(CommonInvalidText, Call(np, "NID_A04C77A1", 2u, broken, end));
        Assert.Equal(0x1234u, GuestMemory.R32(end)); // not written on an error
        Assert.Equal(0, Call(np, "NID_A04C77A1", 1u, broken, end)); // the bad byte is past the count
    }

    [Fact]
    public void TheSignalingPortIs9305UntilSetAndAgainAfterTerm()
    {
        HleModule matching = ModuleNamed("NpMatching2");
        uint port = _harness.Word(0);
        Assert.Equal(unchecked((int)0x80550E15), Call(matching, "NID_40585786", new Ptr<ushort>(0)));
        Assert.Equal(0, Call(matching, "NID_40585786", new Ptr<ushort>(port)));
        Assert.Equal(9305, GuestMemory.Read<ushort>(port));
        Assert.Equal(0, Call(matching, "NID_B096B7A9", 0x12756u));
        Assert.Equal(0, Call(matching, "NID_40585786", new Ptr<ushort>(port)));
        Assert.Equal(0x2756, GuestMemory.Read<ushort>(port));

        Assert.Equal(0, Call(matching, "sceNpMatching2Term")); // not initialised: the port stays
        Assert.Equal(0, Call(matching, "NID_40585786", new Ptr<ushort>(port)));
        Assert.Equal(0x2756, GuestMemory.Read<ushort>(port));
        Assert.Equal(0, Call(matching, "sceNpMatching2Init", 0x10000u, 0, 0, 0u));
        Assert.Equal(0, Call(matching, "sceNpMatching2Term"));
        Assert.Equal(0, Call(matching, "NID_40585786", new Ptr<ushort>(port)));
        Assert.Equal(9305, GuestMemory.Read<ushort>(port));
    }

    [Fact]
    public void TheThreadCheckedTlsSlotIsOnlyTheCallingThreads()
    {
        HleModule threads = ModuleNamed("ThreadMgr");
        _harness.OnGuest(self =>
        {
            Assert.Equal(self.TlsBase + 0x40, Convert.ToUInt32(Invoke(threads, "NID_35D20E49", 0, 0x10u)));
            Assert.Equal(self.TlsBase + 0x40, Convert.ToUInt32(Invoke(threads, "NID_35D20E49", self.Uid, 0x10u)));
            Assert.Equal(0xFFFFFFFFu, Convert.ToUInt32(Invoke(threads, "NID_35D20E49", self.Uid + 2, 0x10u)));
            Assert.Equal(0u, Convert.ToUInt32(Invoke(threads, "NID_35D20E49", 0, 0x100u)));
            Assert.Equal(self.TlsBase + 0x3FC, Convert.ToUInt32(Invoke(threads, "NID_35D20E49", 0, 0xFFu)));
        });
    }
}
