using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// System-imported functions without public names of SceLibLocation, SceNpWebApi and SceNpCommon, each as its FW
/// 3.74 module answers (liblocation, np_webapi, np_common read in their code).
/// </summary>
[Collection("guest")]
public sealed class SystemImportsLot3Tests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "sys-lot3");

    public void Dispose()
    {
        _harness.Dispose();
    }

    [Theory]
    [InlineData(0xEFC9BD25u, true)] // SceLibLocation
    [InlineData(0x7F74AA94u, false)]
    [InlineData(0x0C0BB66Au, true)]
    [InlineData(0x822BBF06u, false)]
    [InlineData(0x991AF2D0u, false)]
    [InlineData(0x4C8B6F21u, true)]
    [InlineData(0x0341758Bu, true)]
    [InlineData(0x5C7185D2u, false)]
    [InlineData(0x2311B24Au, false)]
    [InlineData(0x41C1E68Fu, false)]
    [InlineData(0xC1CFEC92u, true)]
    [InlineData(0xC42F1E67u, false)] // SceNpWebApi
    [InlineData(0xB280EA3Du, false)]
    [InlineData(0x07913970u, false)]
    [InlineData(0xCB51F139u, false)]
    [InlineData(0x4D7CC67Cu, true)]
    [InlineData(0x66CEC1A6u, false)]
    [InlineData(0x0D910413u, false)]
    [InlineData(0x6CDD45B6u, false)]
    [InlineData(0x1D427258u, false)]
    [InlineData(0xC359A88Au, false)]
    [InlineData(0xFDCBD57Du, false)]
    [InlineData(0xC18E6DAAu, false)]
    [InlineData(0x8D83A2D0u, false)]
    [InlineData(0x85503FA6u, false)] // SceNpCommon
    [InlineData(0x9AEF645Bu, false)]
    [InlineData(0x48314C6Eu, false)]
    [InlineData(0x40724BEAu, false)]
    [InlineData(0xBDCD318Eu, false)]
    [InlineData(0xA7EC5CDBu, false)]
    [InlineData(0xE2743C07u, false)]
    [InlineData(0x659ED07Eu, false)]
    [InlineData(0x540B307Eu, false)]
    [InlineData(0x38B88BA3u, false)]
    [InlineData(0xD2B9BCA8u, false)]
    [InlineData(0x85699AE2u, false)]
    [InlineData(0x9B7CAA67u, false)]
    [InlineData(0x40CC4D15u, false)]
    public void TheLibrarysSystemFunctionsHaveAnEntry(uint nid, bool isDefault)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Nid == nid).ToList();
        Assert.NotEmpty(bindings);
        Assert.All(bindings, b => Assert.Equal(isDefault, b.Default));
        Assert.NotNull(_harness.Emu.Hle.ImplOf(nid));
    }

    [Fact]
    public void LocationSharesItsStateWithTheOtherLocationModules()
    {
        Location l = _harness.Module<Location>();
        Assert.Equal(0u, (uint)Invoke(l, "LocationControlBlock")!);
        Assert.Equal(0u, (uint)Invoke(l, "LocationHandleObject", 1u)!);
        Assert.Equal(0, Call(l, "LocationIsExtensionStarted"));
        Assert.Equal(0, Call(l, "LocationExtensionStarted"));
        Assert.Equal(1, Call(l, "LocationIsExtensionStarted"));

        uint pair = _harness.Alloc(8);
        Assert.Equal(Location.ERR_NULL, Call(l, "LocationProviderRequest", 0u));
        Assert.Equal(Location.ERR_NO_PROVIDER, Call(l, "LocationProviderRequest", pair));
        Assert.Equal(0, Call(l, "LocationSetAllocator", pair, (byte)0)); // nobody set one yet
        Assert.Equal(0, Call(l, "LocationSetAllocator", pair, (byte)1));
        Assert.Equal(Location.ERR_ALLOCATOR_SET, Call(l, "LocationSetAllocator", pair, (byte)0));
        Assert.Equal(0, Call(l, "LocationProviderRequest", pair));

        uint block = _harness.Alloc(0x80);
        GuestMemory.W32(block + 100, 0x102);
        Assert.Equal(0, Call(l, "LocationSetControlBlock", block));
        Assert.Equal(block, (uint)Invoke(l, "LocationControlBlock")!);
        Assert.Equal(0x102u, (uint)Invoke(l, "LocationProviderStatus")!);
        Assert.Equal(0, Call(l, "LocationSetProviderState", 1u));
        Assert.Equal(1u, GuestMemory.R32(block + 0x70));

        uint priority = _harness.Word(0), affinity = _harness.Word(7);
        Assert.Equal(0, Call(l, "LocationThreadParameters", priority, affinity));
        Assert.Equal(0x10000100u, GuestMemory.R32(priority));
        Assert.Equal(0u, GuestMemory.R32(affinity));
    }

    [Fact]
    public void WebApiSystemCallsKnowTheirContextsAndRequests()
    {
        NpWebApi w = _harness.Module<NpWebApi>();
        const int Arg = unchecked((int)0x80552902), Context = unchecked((int)0x80552904), Request = unchecked((int)0x80552905);
        Assert.Equal(Context, Call(w, "NpWebApiSuspendContext", 999));
        int ctx = Call(w, "sceNpWebApiInitialize", 1, 0x10000u);
        Assert.Equal(0, Call(w, "NpWebApiSuspendContext", ctx));
        Assert.Equal(0, Call(w, "NpWebApiResumeContext", ctx));
        Assert.Equal(Arg, Call(w, "NpWebApiRemoveNamed", ctx, 0u));
        Assert.Equal(0, Call(w, "NpWebApiRemoveNamed", ctx, _harness.Str("x")));
        Assert.Equal(Context, Call(w, "NpWebApiDeletePushObject", 999, 1));
        Assert.Equal(0, Call(w, "NpWebApiDeletePushObject", ctx, 1));
        Assert.Equal(Arg, Call(w, "NpWebApiCommonOutput", 0, 0u));

        uint requestId = _harness.Alloc(8), status = _harness.Alloc(4);
        Assert.Equal(Arg, Call(w, "NpWebApiCreateRequestForSystem", 0, 0u, 0u, 0, 0u, 0, requestId));
        Assert.Equal(Context, Call(w, "NpWebApiCreateRequestForSystem", 999, 0u, 0u, 0, 0u, 0, requestId));
        Assert.Equal(0, Call(w, "NpWebApiCreateRequestForSystem", ctx, _harness.Str("userProfile"), _harness.Str("/v1"), 0, 0u, 0, requestId));
        long id = GuestMemory.Read<long>(requestId);
        Assert.Equal(ctx, (int)(id >> 16));
        Assert.Equal(Arg, Call(w, "NpWebApiRequestStatus", id, 0u));
        Assert.Equal(unchecked((int)0x8055A602), Call(w, "NpWebApiRequestStatus", id, status)); // never sent
        Assert.Equal(Request, Call(w, "NpWebApiRequestStatus", id + 1, status));
        Assert.Equal(0, Call(w, "NpWebApiSetRequestOption", id, 5u));
        Assert.Equal(Request, Call(w, "NpWebApiSetRequestOption2", id + 1, 5u));

        Assert.Equal(Arg, Call(w, "NpWebApiTerminateForSystem", 0));
        Assert.Equal(0, Call(w, "NpWebApiTerminateForSystem", ctx));
        Assert.Equal(Context, Call(w, "NpWebApiTerminateForSystem", ctx));
        Assert.Equal(Context, Call(w, "NpWebApiSetRequestOption", id, 5u));
        Assert.Equal(0, Call(w, "NpWebApiRefreshForShell"));
    }

    private uint Ticket(params byte[] data)
    {
        uint address = _harness.Alloc((uint)data.Length);
        data.CopyTo(GuestMemory.Span(address, data.Length));
        return address;
    }

    [Fact]
    public void TheTicketCheckWalksToTheBody()
    {
        NpManager np = _harness.Module<NpManager>();
        const int Invalid = unchecked((int)0x80550303), Version = unchecked((int)0x8055030C), Format = unchecked((int)0x80550603);
        Assert.Equal(Invalid, Call(np, "NpCommonTicketCheck", 0u, 16u, 0u, 0u));
        Assert.Equal(Invalid, Call(np, "NpCommonTicketCheck", Ticket(0x41, 0, 0, 0), 3u, 0u, 0u));
        Assert.Equal(Version, Call(np, "NpCommonTicketCheck", Ticket(0x31, 0, 0, 0, 0, 0, 0, 0), 8u, 0u, 0u));
        // v4: the 4-byte size as a container (tag 0x0000), a 2-byte element skipped, the body, a value in it
        uint good = Ticket(0x41, 0, 0, 0, 0, 0, 0, 0, 0x00, 0x10, 0x30, 0x02, 0x00, 0x02, 0xAA, 0xBB, 0x30, 0x12, 0x00, 0x08,
            0x00, 0x01, 0x00, 0x04, 1, 2, 3, 4);
        Assert.Equal(0, Call(np, "NpCommonTicketCheck", good, 28u, 0u, 0u));
        uint noBody = Ticket(0x41, 0, 0, 0, 0, 0, 0, 0, 0x00, 0x10, 0x30, 0x02, 0x00, 0x02, 0xAA, 0xBB);
        Assert.Equal(Format, Call(np, "NpCommonTicketCheck", noBody, 16u, 0u, 0u));
        uint emptyBody = Ticket(0x41, 0, 0, 0, 0, 0, 0, 0, 0x30, 0x12, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00);
        Assert.Equal(Format, Call(np, "NpCommonTicketCheck", emptyBody, 16u, 0u, 0u));
    }

    /// <summary>The library's test sink (by reflection, so the test builds against code without it).</summary>
    private static void SetSink(NpManager np, Action<uint, uint>? sink)
    {
        typeof(NpManager).GetField("CalloutSink", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(np, sink);
    }

    [Fact]
    public void CalloutsRunInTimeOrderAndCanBeCancelled()
    {
        NpManager np = _harness.Module<NpManager>();
        List<(uint Function, uint Argument)> calls = [];
        SetSink(np, (function, argument) => calls.Add((function, argument)));
        uint context = _harness.Alloc(0x18), first = _harness.Alloc(0x18), second = _harness.Alloc(0x18), third = _harness.Alloc(0x18);
        uint removed = _harness.Word(7);
        Assert.Equal(unchecked((int)0x80558001), Call(np, "NpCommonCalloutQueue", context, first, 10UL, 0x81000001u, 1u));
        Assert.Equal(0, Call(np, "NpCommonCalloutStart", context, _harness.Str("test"), 0x4000u, 0x10000100, 0));
        Assert.Equal(unchecked((int)0x80558002), Call(np, "NpCommonCalloutStart", context, 0u, 0x4000u, 0x10000100, 0));
        ulong now = _harness.Emu.Kernel.ProcessTimeUs;
        Assert.Equal(0, Call(np, "NpCommonCalloutQueue", context, first, 30_000_000UL, 0x81000001u, 1u));
        Assert.Equal(0, Call(np, "NpCommonCalloutQueue", context, second, 10_000_000UL, 0x81000002u, 2u));
        Assert.Equal(0, Call(np, "NpCommonCalloutQueue", context, third, 20_000_000UL, 0x81000003u, 3u));
        Assert.Equal(unchecked((int)0x80558006), Call(np, "NpCommonCalloutQueue", context, third, 1UL, 0x81000003u, 3u));
        Assert.Equal(second, GuestMemory.R32(context + 0x14));
        Assert.Equal(0, Call(np, "NpCommonCalloutCancel", context, third, removed));
        Assert.Equal(1u, GuestMemory.R32(removed));
        Assert.Equal(0, Call(np, "NpCommonCalloutCancel", context, third, removed));
        Assert.Equal(0u, GuestMemory.R32(removed));
        Assert.Equal(0, Call(np, "DeliverDueCallouts", context, now));
        Assert.Equal(2, Call(np, "DeliverDueCallouts", context, now + 40_000_000));
        Assert.Equal([(0x81000002u, 2u), (0x81000001u, 1u)], calls);
        Assert.Equal(0, Call(np, "NpCommonCalloutStop", context));
        Assert.Equal(unchecked((int)0x80558001), Call(np, "NpCommonCalloutCancel", context, first, removed));

        Assert.Equal(0u, (uint)Invoke(np, "NpCommonNetCallout")!);
        Assert.Equal(0, Call(np, "NpCommonNetCalloutStart", 0x4000u, 0x10000100));
        uint net = (uint)Invoke(np, "NpCommonNetCallout")!;
        Assert.NotEqual(0u, net);
        Assert.Equal(0, Call(np, "NpCommonNetCalloutQueue", first, 0u, 5UL, 0x81000005u, 5u));
        Assert.Equal(0, Call(np, "NpCommonNetCalloutCancel", first, removed));
        Assert.Equal(1u, GuestMemory.R32(removed));
        Assert.Equal(0, Call(np, "NpCommonNetCalloutStop"));
        Assert.Equal(0u, (uint)Invoke(np, "NpCommonNetCallout")!);
        SetSink(np, null);
    }

    [Fact]
    public void JidsSplitJoinAndBecomeNpIds()
    {
        NpManager np = _harness.Module<NpManager>();
        uint parts = _harness.Alloc(0x10), buffer = _harness.Alloc(0x100), needed = _harness.Word(0);
        Assert.Equal(unchecked((int)0x80550601), Call(np, "NpCommonJidParse", parts, 0u, buffer, 0x100u, needed));
        Assert.Equal(unchecked((int)0x80550604), Call(np, "NpCommonJidParse", parts, _harness.Str("   "), buffer, 0x100u, needed));
        Assert.Equal(0, Call(np, "NpCommonJidParse", parts, _harness.Str("Alice@ab.cd.rc.playstation.net/abc"), buffer, 0x100u, needed));
        Assert.Equal("Alice", GuestMemory.ReadCString(GuestMemory.R32(parts)));
        Assert.Equal("ab.cd.rc.playstation.net", GuestMemory.ReadCString(GuestMemory.R32(parts + 4)));
        Assert.Equal("abc", GuestMemory.ReadCString(GuestMemory.R32(parts + 8)));
        Assert.Equal(unchecked((int)0x80550602), Call(np, "NpCommonJidParse", parts, _harness.Str("Alice@ab.cd.rc.playstation.net/abc"), buffer, 8u, needed));
        Assert.Equal(0, Call(np, "NpCommonJidParse", parts, _harness.Str("Alice@ab.cd.rc.playstation.net/abc"), buffer, 0x100u, needed));

        uint joined = _harness.Alloc(0x100);
        Assert.Equal(0, Call(np, "NpCommonJidJoin", parts, joined, 0x100u, needed));
        Assert.Equal("Alice@ab.cd.rc.playstation.net/abc", GuestMemory.ReadCString(joined));
        Assert.Equal(unchecked((int)0x80550602), Call(np, "NpCommonJidJoin", parts, joined, 4u, needed));

        uint npId = _harness.Alloc(0x24);
        Assert.Equal(0, Call(np, "NpCommonNpIdFromJid", npId, parts));
        Assert.Equal("Alice", GuestMemory.ReadCString(npId));
        Assert.Equal("abcd", System.Text.Encoding.ASCII.GetString(GuestMemory.Span(npId + 0x14, 4)));
        Assert.Equal(1, GuestMemory.Read<byte>(npId + 0x1C));
        uint other = _harness.Alloc(0x10);
        GuestMemory.W32(other, _harness.Str("Bob"));
        GuestMemory.W32(other + 4, _harness.Str("example.com"));
        Assert.Equal(unchecked((int)0x80550604), Call(np, "NpCommonNpIdFromJid", npId + 0x40, other));

        uint back = _harness.Alloc(0x10);
        Assert.Equal(0, Call(np, "NpCommonJidFromNpId", back, npId, buffer, 0x100u, needed));
        Assert.Equal("Alice", GuestMemory.ReadCString(GuestMemory.R32(back)));
        Assert.Equal("ab.cd.rc.playstation.net", GuestMemory.ReadCString(GuestMemory.R32(back + 4)));
        Assert.Equal("abc", GuestMemory.ReadCString(GuestMemory.R32(back + 8)));
        GuestMemory.Write<byte>(npId + 0x1C, 0);
        Assert.Equal(unchecked((int)0x80550605), Call(np, "NpCommonJidFromNpId", back, npId, buffer, 0x100u, needed));
    }
}
