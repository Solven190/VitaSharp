using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceNetInternal's runtime helpers as libnet.suprx 3.74 has them: its allocator, thread and delay wrappers, sync
/// objects, process time, SFMT19937 random numbers, debug print, interface names and per-thread network event
/// handlers (registered and polled; the emulated stack never posts an event). Found by name so the proof without
/// them still builds.
/// </summary>
[Collection("guest")]
public sealed class NetInternalRuntimeTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int NotInit = unchecked((int)0x804101C8);
    private const int Invalid = unchecked((int)0x80410116);

    private readonly HleHarness _harness = new(fixture, "net-internal-runtime");

    public void Dispose() => _harness.Dispose();

    private Net NetModule => _harness.Module<Net>();

    private void NetInit()
    {
        uint param = _harness.Alloc(12);
        GuestMemory.W32(param, _harness.Alloc(0x4000));
        GuestMemory.W32(param + 4, 0x4000);
        Assert.Equal(0, Call(NetModule, "sceNetInit", param));
    }

    [Theory]
    [InlineData(0xA2C2BC5Fu)]
    [InlineData(0xE62A2FAAu)]
    [InlineData(0xCF49E9C4u)]
    [InlineData(0x104E4820u)]
    [InlineData(0x225C4801u)]
    [InlineData(0x3B5F94B7u)]
    [InlineData(0x4EF96CD8u)]
    [InlineData(0x2FEC8B74u)]
    [InlineData(0xF6291476u)]
    [InlineData(0xC2FF615Eu)]
    [InlineData(0x9D12D8A4u)]
    [InlineData(0x689B9D7Du)]
    [InlineData(0xF3917021u)]
    [InlineData(0x694F8996u)]
    [InlineData(0xEAC33599u)]
    [InlineData(0xEDAA3453u)]
    [InlineData(0xB01ABD0Au)]
    [InlineData(0x04E6136Du)]
    public void TheRuntimeExportsHaveAnEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Nid == nid).ToList();
        Assert.NotEmpty(bindings);
        Assert.All(bindings, b => Assert.False(b.Default));
        Assert.NotNull(_harness.Emu.Hle.ImplOf(nid));
    }

    [Fact]
    public void TheAllocatorNeedsTheInitAndZeroesWhenAsked()
    {
        Assert.Equal(0, Call(NetModule, "NetInternalAlloc", 0x20, 2u));
        Assert.Equal(NotInit, Call(NetModule, "NetInternalFreeRoutes", 0u));
        NetInit();
        Assert.Equal(0, Call(NetModule, "NetInternalAlloc", 0, 2u));
        uint block = Convert.ToUInt32(Invoke(NetModule, "NetInternalAlloc", 0x20, 2u));
        Assert.NotEqual(0u, block);
        Assert.Equal(0u, block & 7);
        for (uint offset = 0; offset < 0x20; offset += 4)
        {
            Assert.Equal(0u, GuestMemory.R32(block + offset));
        }

        Invoke(NetModule, "NetInternalFree", block);
        Invoke(NetModule, "NetInternalFree", 0u); // NULL: nothing
        Assert.Equal(0, Call(NetModule, "NetInternalFreeRoutes", 0u));
    }

    [Fact]
    public void InterfaceNamesAreLibnetsTable()
    {
        Assert.Equal("lo0", GuestMemory.ReadCString(Convert.ToUInt32(Invoke(NetModule, "NetInternalInterfaceName", 0u))));
        Assert.Equal("wlan0", GuestMemory.ReadCString(Convert.ToUInt32(Invoke(NetModule, "NetInternalInterfaceName", 4u))));
        Assert.Equal("pppoe0", GuestMemory.ReadCString(Convert.ToUInt32(Invoke(NetModule, "NetInternalInterfaceName", 9u))));
        Assert.Equal(string.Empty, GuestMemory.ReadCString(Convert.ToUInt32(Invoke(NetModule, "NetInternalInterfaceName", 10u))));
    }

    [Fact]
    public void TheRandomWordsAreSfmt19937()
    {
        uint value = _harness.Word(0);
        Assert.Equal(NotInit, Call(NetModule, "NetInternalRandom", value));
        NetInit();
        Assert.Equal(Invalid, Call(NetModule, "NetInternalRandom", 0u));
        Invoke(NetModule, "SeedRandom", 1234u);
        uint[] expected = [3440181298u, 1564997079u, 1510669302u, 2930277156u, 1452439940u];
        foreach (uint word in expected)
        {
            Assert.Equal(0, Call(NetModule, "NetInternalRandom", value));
            Assert.Equal(word, GuestMemory.R32(value));
        }
    }

    [Fact]
    public void DelayDebugPrintAndTimeFollowLibnet()
    {
        Assert.Equal(NotInit, Call(NetModule, "NetInternalDelay", 1));
        NetInit();
        Assert.Equal(Invalid, Call(NetModule, "NetInternalDelay", -1));
        Assert.Equal(0, Call(NetModule, "NetInternalDebugPrint", "x", null)); // no print callback: nothing
        Assert.Equal(0u, Convert.ToUInt32(Invoke(NetModule, "NetInternalDhcpSync", 1)));
        uint time = _harness.Alloc(8);
        GuestMemory.Fill(time, 0xFF, 8);
        _harness.OnGuest(_ =>
        {
            Assert.Equal(0, Call(NetModule, "NetInternalDelay", 10));
            Invoke(NetModule, "NetInternalProcessTime", time);
            Invoke(NetModule, "NetInternalProcessTime", 0u);
        });
        Assert.NotEqual(0xFFFFFFFFu, GuestMemory.R32(time + 4));
    }

    [Fact]
    public void TheDebugPrintGoesThroughTheCallbackNidA64CFBF5Sets()
    {
        NetInit();
        Assert.Equal(0, Call(NetModule, "NetInternalDebugPrint", "ab", null)); // no callback yet
        uint code = _harness.Alloc(4);
        GuestMemory.Write(code, (ushort)0x4770); // bx lr
        Assert.Equal(0, Call(NetModule, "NID_A64CFBF5", code | 1));
        _harness.OnGuest(_ => Assert.Equal(2, Call(NetModule, "NetInternalDebugPrint", "ab", null)));
    }

    [Fact]
    public void TheDhcpSyncObjectWaitsAndSignalsItsEventFlag()
    {
        NetInit();
        uint timeout = _harness.Word(1000);
        _harness.OnGuest(_ =>
        {
            uint sync = Convert.ToUInt32(Invoke(NetModule, "NetInternalDhcpSync", 0));
            Assert.NotEqual(0u, sync);
            Assert.Equal(sync, Convert.ToUInt32(Invoke(NetModule, "NetInternalDhcpSync", 0)));
            Assert.NotEqual(0u, GuestMemory.R32(sync)); // the event flag, created with bit 0 set
            Assert.Equal(1u, Convert.ToUInt32(Invoke(NetModule, "NetInternalSyncWait", sync, timeout)));
            Assert.Equal(0, Call(NetModule, "NetInternalSyncSignal", sync, 4u));
            Assert.Equal(4u, Convert.ToUInt32(Invoke(NetModule, "NetInternalSyncWait", sync, timeout)));
            Assert.Equal(0u, GuestMemory.R32(sync + 4));
            Assert.Equal(0u, Convert.ToUInt32(Invoke(NetModule, "NetInternalSyncWait", sync, timeout))); // timed out
            Assert.Equal(0xFFFFFFFFu, GuestMemory.R32(timeout));
        });
    }

    [Fact]
    public void ALibnetThreadRunsItsEntryWithTheArgument()
    {
        uint thread = _harness.Word(0xAAAAAAAA);
        uint attr = _harness.Alloc(0x14);
        Assert.Equal(NotInit, Call(NetModule, "NetInternalCreateThread", thread, attr, "libnet test"));
        NetInit();
        Assert.Equal(Invalid, Call(NetModule, "NetInternalCreateThread", thread, 0u, "libnet test"));

        // movs r1, #7 ; str r1, [r0] ; bx lr: the entry stores 7 at its argument
        uint code = _harness.Alloc(8);
        GuestMemory.Write(code, (ushort)0x2107);
        GuestMemory.Write(code + 2, (ushort)0x6001);
        GuestMemory.Write(code + 4, (ushort)0x4770);
        uint cell = _harness.Word(0);
        GuestMemory.W32(attr, code | 1);
        GuestMemory.W32(attr + 4, cell);
        GuestMemory.W32(attr + 8, 0x10000100);
        GuestMemory.W32(attr + 12, 0x4000);
        GuestMemory.W32(attr + 16, 1); // exit right after the entry
        _harness.OnGuest(_ =>
        {
            Assert.Equal(0, Call(NetModule, "NetInternalCreateThread", thread, attr, "libnet test"));
            int uid = (int)GuestMemory.R32(thread);
            Assert.True(uid > 0);
            Assert.Equal(0, Call(NetModule, "NetInternalJoinThread", uid));
            Assert.True(Call(NetModule, "NetInternalJoinThread", uid) < 0); // deleted
        });
        Assert.Equal(7u, GuestMemory.R32(cell));
        Assert.ThrowsAny<Exception>(() => Invoke(NetModule, "NetInternalExitThread"));
    }

    [Fact]
    public void EventHandlersAreOnePerThreadAndInterfaceAndNeverGetAnEvent()
    {
        uint callback = _harness.Alloc(4) | 1;
        uint value = _harness.Word(0xAAAAAAAA);
        Assert.Equal(NotInit, Call(NetModule, "NetInternalAddEventHandler", 0, callback, 0u, 0));
        NetInit();
        _harness.OnGuest(_ =>
        {
            Assert.Equal(unchecked((int)0x80410102), Call(NetModule, "NetInternalPollEvents", 0, 0u, 0)); // none: ENOENT
            Assert.Equal(Invalid, Call(NetModule, "NetInternalAddEventHandler", 0, 0u, 0u, 0));
            Assert.Equal(Invalid, Call(NetModule, "NetInternalAddEventHandler", 0, callback, 0u, 1));
            Assert.Equal(0, Call(NetModule, "NetInternalAddEventHandler", 4, callback, 0u, 0)); // wlan0: waits for it
            Assert.Equal(unchecked((int)0x80410102), Call(NetModule, "NetInternalPollEvents", 0, 0u, 0)); // only a waiting one
            Assert.Equal(0, Call(NetModule, "NetInternalAddEventHandler", 0, callback, 0u, 0));
            Assert.Equal(unchecked((int)0x80410111), Call(NetModule, "NetInternalAddEventHandler", 0, callback, 0u, 0)); // EEXIST
            Assert.Equal(0, Call(NetModule, "NetInternalEventValue", 0, value, 0));
            Assert.Equal(0u, GuestMemory.R32(value));
            Assert.Equal(Invalid, Call(NetModule, "NetInternalEventValue", 1, value, 0));
            Assert.Equal(unchecked((int)0x80410159), Call(NetModule, "NetInternalPollEvents", 0, 5u, 0)); // no event
            Assert.Equal(0, Call(NetModule, "NetInternalPollEvents", 100, 5u, 0)); // waited the timeout out
            Assert.Equal(Invalid, Call(NetModule, "NetInternalPollEvents", 100, 5u, 1));
            Assert.Equal(Invalid, Call(NetModule, "NetInternalRemoveEventHandler", 0, 1));
            Assert.Equal(0, Call(NetModule, "NetInternalRemoveEventHandler", 0, 0));
            Assert.Equal(Invalid, Call(NetModule, "NetInternalRemoveEventHandler", 0, 0));
            Assert.Equal(0, Call(NetModule, "NetInternalRemoveEventHandler", 4, 0));
        });
    }
}
