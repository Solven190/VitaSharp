using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceNetInternal's exports that reach the kernel through the SceNetPs control commands, the ioctl and the route
/// sysctl, each with libnet.suprx 3.74's argument checks and error conversion over the emulated stack (the loopback
/// interface only, no network service): link records, process state, DNS servers, counters, interface list,
/// statistics and routes. The exports are found by name so the proof without them still builds.
/// </summary>
[Collection("guest")]
public sealed class NetInternalControlTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int NotInit = unchecked((int)0x804101C8);
    private const int Invalid = unchecked((int)0x80410116);
    private const int NoDevice = unchecked((int)0x80410113);
    private const int NotFound = unchecked((int)0x80410159);

    private readonly HleHarness _harness = new(fixture, "net-internal-control");

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
    [InlineData(0x01B090F1u, false)]
    [InlineData(0xE03F6A77u, false)]
    [InlineData(0x26EFF46Du, false)]
    [InlineData(0x4F5E0E8Au, false)]
    [InlineData(0xEE94BF52u, false)]
    [InlineData(0x0DFC23D6u, false)]
    [InlineData(0xEF421A65u, false)]
    [InlineData(0xDEF2357Fu, false)]
    [InlineData(0xA9F2277Cu, true)]
    [InlineData(0xA6B9F5D2u, false)]
    [InlineData(0xEE1985D9u, false)]
    [InlineData(0x16222ED6u, false)]
    [InlineData(0x7F63379Eu, false)]
    [InlineData(0xAA414742u, false)]
    [InlineData(0x333F9CB3u, false)]
    [InlineData(0xBDA92B76u, true)]
    [InlineData(0x5B0E370Cu, true)]
    [InlineData(0x8157DE3Eu, false)]
    [InlineData(0xEE94BA15u, true)]
    public void TheControlExportsHaveAnEntry(uint nid, bool isDefault)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Nid == nid).ToList();
        Assert.NotEmpty(bindings);
        Assert.All(bindings, b => Assert.Equal(isDefault, b.Default));
        Assert.NotNull(_harness.Emu.Hle.ImplOf(nid));
    }

    [Fact]
    public void EveryExportNeedsSceNetInit()
    {
        uint buffer = _harness.Alloc(0x100);
        Assert.Equal(NotInit, Call(NetModule, "NetInternalGetLinkRecord", 0, buffer, 0));
        Assert.Equal(NotInit, Call(NetModule, "NetInternalSetLinkRecord", 0, buffer, 0));
        Assert.Equal(NotInit, Call(NetModule, "NetInternalAnnounceLink", 0, 1u, 0u));
        Assert.Equal(NotInit, Call(NetModule, "NetInternalClearConflict", 0, 0, 0));
        Assert.Equal(NotInit, Call(NetModule, "NetInternalGetProcessState", 1, buffer, 0));
        Assert.Equal(NotInit, Call(NetModule, "NetInternalSetProcessState", 1, 1u, 0));
        Assert.Equal(NotInit, Call(NetModule, "NetInternalGetSetting19", 1, buffer, 0u));
        Assert.Equal(NotInit, Call(NetModule, "NetInternalListProcesses", buffer, 1, 0));
        Assert.Equal(NotInit, Call(NetModule, "NetInternalReadCounters", buffer, 0));
        Assert.Equal(NotInit, Call(NetModule, "NetInternalSetSetting23", buffer, 0));
        Assert.Equal(NotInit, Call(NetModule, "NetInternalSetSetting20", 1u));
        Assert.Equal(NotInit, Call(NetModule, "NetInternalSetStackDns", buffer, 0));
        Assert.Equal(NotInit, Call(NetModule, "NetInternalGetDns", buffer, 0));
        Assert.Equal(NotInit, Call(NetModule, "NetInternalControl", -1, 1, 0x14, buffer, 4));
        Assert.Equal(NotInit, Call(NetModule, "NetInternalIoctl", 1, 0u, buffer));
        Assert.Equal(NotInit, Call(NetModule, "NetInternalGetRoutes", 2, 0u, 0, 0u));
        Assert.Equal(0, Call(NetModule, "NetInternalAllocRoutes", 0, buffer)); // NULL
        Assert.Equal(NotInit, Call(NetModule, "NetInternalListInterfaces", buffer, 4, 1u));
        Assert.Equal(NotInit, Call(NetModule, "NetInternalStatisticsDelta", 0, buffer, 1, buffer));
        Assert.Equal(NotInit, Call(NetModule, "sceNetSetDnsInfo", 0u, 0));
    }

    [Fact]
    public void TheLinkRecordIsLo0sAndOtherInterfacesAreNotFound()
    {
        NetInit();
        uint link = _harness.Alloc(0x14);
        Assert.Equal(Invalid, Call(NetModule, "NetInternalGetLinkRecord", 0, 0u, 0));
        Assert.Equal(Invalid, Call(NetModule, "NetInternalGetLinkRecord", 0, link, 1));
        Assert.Equal(NoDevice, Call(NetModule, "NetInternalGetLinkRecord", -1, link, 0));
        Assert.Equal(NoDevice, Call(NetModule, "NetInternalGetLinkRecord", 4, link, 0)); // wlan0: not in the list
        Assert.Equal(NoDevice, Call(NetModule, "NetInternalSetLinkRecord", 1, link, 0)); // eth0

        GuestMemory.W32(link, 0x11);
        GuestMemory.W32(link + 4, 1); // the link-up word is the kernel's
        GuestMemory.W32(link + 8, 7);
        GuestMemory.W32(link + 12, 3);
        Assert.Equal(0, Call(NetModule, "NetInternalSetLinkRecord", 0, link, 0));
        GuestMemory.Fill(link, 0xAA, 0x14);
        Assert.Equal(0, Call(NetModule, "NetInternalGetLinkRecord", 0, link, 0));
        Assert.Equal(0x11u, GuestMemory.R32(link));
        Assert.Equal(0u, GuestMemory.R32(link + 4)); // no carrier on lo0
        Assert.Equal(7u, GuestMemory.R32(link + 8));
        Assert.Equal(3u, GuestMemory.R32(link + 12));
        Assert.Equal(0u, GuestMemory.R32(link + 16));

        GuestMemory.W32(link + 12, 99); // count -1..99
        Assert.Equal(0, Call(NetModule, "NetInternalSetLinkRecord", 0, link, 0));
        GuestMemory.W32(link + 12, 100);
        Assert.Equal(Invalid, Call(NetModule, "NetInternalSetLinkRecord", 0, link, 0));
        GuestMemory.W32(link + 12, 0);
        GuestMemory.W32(link + 8, 0xFFFFFFFF);
        Assert.Equal(Invalid, Call(NetModule, "NetInternalSetLinkRecord", 0, link, 0));
    }

    [Fact]
    public void AnnounceAndClearTakeTheStackOrAnInterface()
    {
        NetInit();
        Assert.Equal(Invalid, Call(NetModule, "NetInternalAnnounceLink", 0, 1u, 2u));
        Assert.Equal(NoDevice, Call(NetModule, "NetInternalAnnounceLink", 1, 1u, 0u));
        Assert.Equal(0, Call(NetModule, "NetInternalAnnounceLink", 0, 1u, 1u));
        Assert.Equal(0, Call(NetModule, "NetInternalAnnounceLink", -1, 1u, 0u));
        Assert.Equal(Invalid, Call(NetModule, "NetInternalClearConflict", 0, 1, 0));
        Assert.Equal(Invalid, Call(NetModule, "NetInternalClearConflict", 0, 0, 1));
        Assert.Equal(NoDevice, Call(NetModule, "NetInternalClearConflict", 9, 0, 0));
        Assert.Equal(0, Call(NetModule, "NetInternalClearConflict", 0, 0, 0));
        Assert.Equal(0, Call(NetModule, "NetInternalClearConflict", -1, 0, 0));
    }

    [Fact]
    public void TheProcessStateIsKeptForTheRunningProcessOnly()
    {
        NetInit();
        int pid = _harness.Emu.Pid;
        uint state = _harness.Word(0xAAAAAAAA);
        Assert.Equal(Invalid, Call(NetModule, "NetInternalGetProcessState", pid, 0u, 0));
        Assert.Equal(Invalid, Call(NetModule, "NetInternalGetProcessState", pid, state, 1));
        Assert.Equal(0, Call(NetModule, "NetInternalGetProcessState", pid, state, 0));
        Assert.Equal(0u, GuestMemory.R32(state));
        Assert.Equal(0, Call(NetModule, "NetInternalSetProcessState", pid, 3u, 0));
        Assert.Equal(0, Call(NetModule, "NetInternalGetProcessState", pid, state, 0));
        Assert.Equal(3u, GuestMemory.R32(state));
        Assert.Equal(Invalid, Call(NetModule, "NetInternalSetProcessState", pid, 4u, 0));
        Assert.Equal(Invalid, Call(NetModule, "NetInternalSetProcessState", pid, 1u, 1));
        GuestMemory.W32(state, 0xAAAAAAAA);
        Assert.Equal(Invalid, Call(NetModule, "NetInternalGetProcessState", pid + 0x1000, state, 0)); // no record
        Assert.Equal(0xAAAAAAAAu, GuestMemory.R32(state));
        Assert.Equal(Invalid, Call(NetModule, "NetInternalSetProcessState", pid + 0x1000, 1u, 0));
        uint entries = _harness.Alloc(48);
        Assert.Equal(0, Call(NetModule, "NetInternalListProcesses", entries, 4, 0));
        Assert.Equal(Invalid, Call(NetModule, "NetInternalListProcesses", entries, 4, 1));
    }

    [Fact]
    public void Setting19IsReadAfterTheStackGotIt()
    {
        NetInit();
        uint value = _harness.Word(0xAAAAAAAA);
        Assert.Equal(Invalid, Call(NetModule, "NetInternalGetSetting19", 0x40, value, 2u));
        Assert.Equal(Invalid, Call(NetModule, "NetInternalGetSetting19", 0x40, 0u, 0u));
        Assert.Equal(Invalid, Call(NetModule, "NetInternalGetSetting19", 0x40, value, 0u)); // no record yet
        Assert.Equal(0xAAAAAAAAu, GuestMemory.R32(value));

        uint block = _harness.Alloc(12);
        GuestMemory.W32(block, 0x40);
        GuestMemory.W32(block + 8, 5);
        Assert.Equal(0, Call(NetModule, "NetInternalControl", -1, 1, 0x20000019, block, 12));
        Assert.Equal(0, Call(NetModule, "NetInternalGetSetting19", 0x40, value, 1u));
        Assert.Equal(5u, GuestMemory.R32(value));
    }

    [Fact]
    public void TheDnsServersAreTheProcessOwnOverTheStacks()
    {
        NetInit();
        uint servers = _harness.Alloc(8);
        Assert.Equal(Invalid, Call(NetModule, "NetInternalGetDns", 0u, 0));
        Assert.Equal(Invalid, Call(NetModule, "NetInternalGetDns", servers, 1));
        Assert.Equal(0, Call(NetModule, "NetInternalGetDns", servers, 0)); // none set
        Assert.Equal(0u, GuestMemory.R32(servers));

        uint stack = _harness.Alloc(8);
        GuestMemory.W32(stack, 0x04030201);
        GuestMemory.W32(stack + 4, 0x08070605);
        Assert.Equal(Invalid, Call(NetModule, "NetInternalSetStackDns", 0u, 0));
        Assert.Equal(Invalid, Call(NetModule, "NetInternalSetStackDns", stack, 1));
        Assert.Equal(0, Call(NetModule, "NetInternalSetStackDns", stack, 0));
        Assert.Equal(2, Call(NetModule, "NetInternalGetDns", servers, 0));
        Assert.Equal(0x04030201u, GuestMemory.R32(servers));
        Assert.Equal(0x08070605u, GuestMemory.R32(servers + 4));

        uint own = _harness.Alloc(8);
        GuestMemory.W32(own, 0x09090909);
        Assert.Equal(Invalid, Call(NetModule, "sceNetSetDnsInfo", own, 1));
        Assert.Equal(0, Call(NetModule, "sceNetSetDnsInfo", own, 0));
        Assert.Equal(1, Call(NetModule, "NetInternalGetDns", servers, 0));
        Assert.Equal(0x09090909u, GuestMemory.R32(servers));
        Assert.Equal(0u, GuestMemory.R32(servers + 4));
        GuestMemory.W32(own, 0);
        Assert.Equal(Invalid, Call(NetModule, "sceNetSetDnsInfo", own, 0)); // a first address 0
        Assert.Equal(0, Call(NetModule, "sceNetSetDnsInfo", 0u, 0)); // NULL clears the process's own
        Assert.Equal(2, Call(NetModule, "NetInternalGetDns", servers, 0));
        Assert.Equal(0x04030201u, GuestMemory.R32(servers));
    }

    [Fact]
    public void TheStackDnsRecordFlagsEachProcessFirstReadAfterASet()
    {
        NetInit();
        uint record = _harness.Alloc(0x28);
        Assert.Equal(-5, Call(NetModule, "sceNetSyscallControl", 1, 2, record, 0x28)); // the stack's, not lo0's
        Assert.Equal(-22, Call(NetModule, "sceNetSyscallControl", -1, 2, record, 0x27));
        Assert.Equal(0, Call(NetModule, "sceNetSyscallControl", -1, 2, record, 0x28));
        Assert.Equal(1u, GuestMemory.R32(record + 8) & 1);
        Assert.Equal(0, Call(NetModule, "sceNetSyscallControl", -1, 2, record, 0x28));
        Assert.Equal(0u, GuestMemory.R32(record + 8) & 1);
        GuestMemory.Fill(record, 0, 0x28);
        GuestMemory.W32(record, 0x01010101);
        GuestMemory.W32(record + 8, 1);
        Assert.Equal(-22, Call(NetModule, "sceNetSyscallControl", -1, 0x20000002, record, 0x28)); // +8 must be 0
        GuestMemory.W32(record + 8, 0);
        Assert.Equal(0, Call(NetModule, "sceNetSyscallControl", -1, 0x20000002, record, 0x28));
        Assert.Equal(0, Call(NetModule, "sceNetSyscallControl", -1, 2, record, 0x28));
        Assert.Equal(0x01010101u, GuestMemory.R32(record));
        Assert.Equal(1u, GuestMemory.R32(record + 8) & 1);
        Assert.Equal(-5, Call(NetModule, "sceNetSyscallControl", 2, 0x14, record, 4)); // no interface 2
        Assert.Equal(-5, Call(NetModule, "sceNetSyscallControl", -1, 4, record, 0x14)); // lo0's, not the stack's
    }

    [Fact]
    public void CountersSettingsAndIoctlFollowTheStack()
    {
        NetInit();
        uint counters = _harness.Alloc(0x44);
        GuestMemory.Fill(counters, 0xAA, 0x44);
        Assert.Equal(Invalid, Call(NetModule, "NetInternalReadCounters", 0u, 0));
        Assert.Equal(Invalid, Call(NetModule, "NetInternalReadCounters", counters, 1));
        Assert.Equal(0, Call(NetModule, "NetInternalReadCounters", counters, 0));
        Assert.Equal(0u, GuestMemory.R32(counters + 0x40));

        uint pair = _harness.Alloc(8);
        Assert.Equal(Invalid, Call(NetModule, "NetInternalSetSetting23", pair, 1));
        Assert.Equal(0, Call(NetModule, "NetInternalSetSetting23", pair, 0));
        Assert.Equal(Invalid, Call(NetModule, "NetInternalSetSetting23", 0u, 0)); // the stack's EINVAL
        Assert.Equal(0, Call(NetModule, "NetInternalSetSetting20", 7u));

        Assert.Equal(unchecked((int)0x80410109), Call(NetModule, "NetInternalIoctl", 0x7777, 0xC0206911u, pair)); // EBADF
    }

    [Fact]
    public void TheRawControlTakesAKernelTargetOrAnInterface()
    {
        NetInit();
        uint data = _harness.Alloc(0x14);
        Assert.Equal(Invalid, Call(NetModule, "NetInternalControl", -1, -1, 0x14, data, 4));
        Assert.Equal(0, Call(NetModule, "NetInternalControl", -1, 1, 4, data, 0x14)); // lo0 by its kernel index
        Assert.Equal(unchecked((int)0x80410105), Call(NetModule, "NetInternalControl", -1, 5, 4, data, 0x14)); // -EIO
        Assert.Equal(Invalid, Call(NetModule, "NetInternalControl", 0, 1, 4, data, 0x14));
        Assert.Equal(0, Call(NetModule, "NetInternalControl", 0, 0, 4, data, 0x14));
        Assert.Equal(NotFound, Call(NetModule, "NetInternalControl", 1, 0, 4, data, 0x14)); // eth0
        Assert.Equal(Invalid, Call(NetModule, "NetInternalControl", -1, 1, 0x7777, data, 4)); // unknown command
    }

    [Fact]
    public void TheInterfaceListHasLo0WhenAsked()
    {
        NetInit();
        uint indexes = _harness.Word(0xAAAAAAAA);
        Assert.Equal(Invalid, Call(NetModule, "NetInternalListInterfaces", indexes, 4, 2u));
        Assert.Equal(Invalid, Call(NetModule, "NetInternalListInterfaces", 0u, 4, 0u));
        Assert.Equal(Invalid, Call(NetModule, "NetInternalListInterfaces", indexes, 0, 0u));
        Assert.Equal(0, Call(NetModule, "NetInternalListInterfaces", indexes, 4, 0u)); // 1..8 only
        Assert.Equal(0xAAAAAAAAu, GuestMemory.R32(indexes));
        Assert.Equal(1, Call(NetModule, "NetInternalListInterfaces", indexes, 4, 1u));
        Assert.Equal(0u, GuestMemory.R32(indexes));
    }

    [Fact]
    public void StatisticsDeltasUseTheCallersHistorySlot()
    {
        NetInit();
        uint delta = _harness.Alloc(0x44);
        uint history = _harness.Alloc(0x48 * 10);
        Assert.Equal(Invalid, Call(NetModule, "NetInternalStatisticsDelta", 0, delta, 2, history));
        Assert.Equal(Invalid, Call(NetModule, "NetInternalStatisticsDelta", 0, 0u, 1, history));
        Assert.Equal(Invalid, Call(NetModule, "NetInternalStatisticsDelta", -1, delta, 1, history));

        GuestMemory.Fill(delta, 0xAA, 0x44);
        Assert.Equal(0, Call(NetModule, "NetInternalStatisticsDelta", 1, delta, 1, history)); // eth0: not listed
        Assert.Equal(0xAAAAAAAAu, GuestMemory.R32(delta));

        GuestMemory.Fill(history, 0, 0x48);
        GuestMemory.W32(history, 5); // lo0's slot: the previous reading
        Assert.Equal(0, Call(NetModule, "NetInternalStatisticsDelta", 0, delta, 1, history));
        Assert.Equal(unchecked((uint)-5), GuestMemory.R32(delta));
        Assert.Equal(0u, GuestMemory.R32(delta + 0x40));
        Assert.Equal(0u, GuestMemory.R32(history)); // now the current reading
        Assert.Equal(0, Call(NetModule, "NetInternalStatisticsDelta", 0, delta, 1, history));
        Assert.Equal(0u, GuestMemory.R32(delta));
    }

    [Fact]
    public void RoutesComeFromTheRoutingSysctl()
    {
        NetInit();
        uint routes = _harness.Alloc(0x58);
        uint count = _harness.Word(0xAAAAAAAA);
        Assert.Equal(Invalid, Call(NetModule, "NetInternalGetRoutes", 3, 0u, 0, 0u));
        Assert.Equal(Invalid, Call(NetModule, "NetInternalGetRoutes", 2, 0u, 1, 1u));
        Assert.Equal(Invalid, Call(NetModule, "NetInternalGetRoutes", 2, routes, 2, 1u));
        Assert.Equal(unchecked((int)0x80410102), Call(NetModule, "NetInternalGetRoutes", 2, 0u, 0, 0u)); // no MIB: ENOENT
        Assert.Equal(0, Call(NetModule, "NetInternalAllocRoutes", 0, count)); // NULL
        Assert.Equal(0, Call(NetModule, "NetInternalAllocRoutes", 1, count));
        Assert.Equal(0xAAAAAAAAu, GuestMemory.R32(count));
    }
}
