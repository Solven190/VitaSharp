using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The last functions of SceGpuEs4UserForVsh, SceNpPartyGameUtil, SceNpMatching2 and SceMusicExport, each answered as
/// its FW 3.74 module answers (libgpu_es4, np_party, np_matching2 and libSceMusicExport read in their code). The NID
/// test closes each library: every function the firmware exports there has an [Hle] entry.
/// </summary>
[Collection("guest")]
public sealed class HleSmallLibraries3Tests(GuestMemoryFixture fixture) : IDisposable
{
    private const int NotInParty = unchecked((int)0x80553402);

    private readonly HleHarness _harness = new(fixture, "small-libs3");
    private readonly PsnAccount _savedAccount = PsnAccount.Current;
    private readonly string _exportRoot = Path.Combine(Path.GetTempPath(), $"vs_small3_{Guid.NewGuid():N}");

    public void Dispose()
    {
        PsnAccount.Current = _savedAccount;
        _harness.Dispose();
        try
        {
            Directory.Delete(_exportRoot, true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData(0x010DA2CBu, true)] // SceGpuEs4UserForVsh
    [InlineData(0x0A9556D9u, true)]
    [InlineData(0x3C09BFCAu, true)]
    [InlineData(0x3DC96093u, true)]
    [InlineData(0x8A27AB0Au, true)]
    [InlineData(0x6370CEB6u, true)] // SceNpPartyGameUtil
    [InlineData(0x089134D1u, true)]
    [InlineData(0x54692CAAu, false)] // SceNpMatching2
    [InlineData(0xDF322C5Bu, false)]
    [InlineData(0xD1011570u, false)] // SceMusicExport
    public void TheLibrarysLastFunctionsHaveAnEntry(uint nid, bool isDefault)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Nid == nid).ToList();
        Assert.NotEmpty(bindings);
        Assert.All(bindings, b => Assert.Equal(isDefault, b.Default));
        Assert.NotNull(_harness.Emu.Hle.ImplOf(nid));
    }

    /// <summary>A PVRSRV connection as libgpu_es4 keeps it: connection -> data -> services record (+4 = the
    /// hKernelServices CONNECT_SERVICES handed out).</summary>
    private uint Connection(uint kernelServices)
    {
        uint record = _harness.Alloc(8);
        GuestMemory.W32(record + 4, kernelServices);
        uint data = _harness.Word(record);
        return _harness.Word(data);
    }

    /// <summary>The wrappers check their pointers first (PVRSRV_ERROR_INVALID_PARAMS); a call that reaches the driver
    /// is refused there, before or after the process is connected (no SGX device: its init server never ran), and the
    /// output stays untouched.</summary>
    [Fact]
    public void GpuEs4UserForVshCallsAreRefusedByTheBridgeWithoutTheDevice()
    {
        GpuEs4 g = _harness.Module<GpuEs4>();
        uint connection = Connection(GpuEs4.KernelServicesHandle);
        uint output = _harness.Word(0xAAAAAAAA);
        Assert.Equal(GpuEs4.PvrInvalidParams, Call(g, "GpuEs4UserForVsh3C09BFCA", 0u, 1u, 2u));
        Assert.Equal(GpuEs4.PvrInvalidParams, Call(g, "GpuEs4UserForVsh3C09BFCA", connection, 0u, 2u));
        Assert.Equal(GpuEs4.PvrInvalidParams, Call(g, "GpuEs4UserForVsh3C09BFCA", connection, 1u, 0u));
        Assert.Equal(GpuEs4.PvrInvalidParams, Call(g, "GpuEs4UserForVsh0A9556D9", connection, 1u, 0u));
        Assert.Equal(GpuEs4.PvrInvalidParams, Call(g, "GpuEs4UserForVsh8A27AB0A", connection, 1u, 0u));
        Assert.Equal(GpuEs4.PvrInvalidParams, Call(g, "GpuEs4UserForVsh010DA2CB", connection, 0u));
        Assert.Equal(GpuEs4.PvrInvalidParams, Call(g, "GpuEs4UserForVsh3DC96093", 0u, 1u, 2u, 3u, 4u));

        Assert.Equal(GpuEs4.PvrBridgeCallFailed, Call(g, "GpuEs4UserForVsh010DA2CB", connection, 1u)); // not connected
        Assert.Equal(0, Call(g, "PVRSRVOpen"));
        Assert.Equal(GpuEs4.PvrBridgeCallFailed, Call(g, "GpuEs4UserForVsh3C09BFCA", connection, 1u, 2u));
        Assert.Equal(GpuEs4.PvrBridgeCallFailed, Call(g, "GpuEs4UserForVsh0A9556D9", connection, 1u, 2u));
        Assert.Equal(GpuEs4.PvrBridgeCallFailed, Call(g, "GpuEs4UserForVsh8A27AB0A", connection, 1u, output));
        Assert.Equal(GpuEs4.PvrBridgeCallFailed, Call(g, "GpuEs4UserForVsh010DA2CB", connection, 1u));
        Assert.Equal(GpuEs4.PvrBridgeCallFailed, Call(g, "GpuEs4UserForVsh3DC96093", connection, 1u, 2u, 3u, 4u));
        Assert.Equal(GpuEs4.PvrBridgeCallFailed, Call(g, "GpuEs4UserForVsh010DA2CB", Connection(7), 1u)); // not the process's handle
        Assert.Equal(0xAAAAAAAAu, GuestMemory.R32(output));
        Assert.Equal(0, Call(g, "PVRSRVRelease"));
    }

    [Fact]
    public void NpPartyServiceRequestsAnswerNotInAParty()
    {
        NpSocial social = _harness.Module<NpSocial>();
        Assert.Equal(NotInParty, Call(social, "NpPartyRequest110018"));
        Assert.Equal(NotInParty, Call(social, "NpPartyRequest110019"));
    }

    /// <summary>Both local-list entries check the library's flag, then the context; the server id list is the one
    /// server once a local context has started, and no context is ever in a room.</summary>
    [Fact]
    public void NpMatching2LocalListsFollowTheContext()
    {
        PsnAccount.Current = PsnAccount.Create(true, "Alice");
        HleModule matching = _harness.Emu.Hle.Modules.Single(m => m.GetType().Name == "NpMatching2");
        uint ids = _harness.Word(0xAAAAAAAA);
        uint entries = _harness.Alloc(16);
        uint count = _harness.Word(4);
        uint list = _harness.Alloc(8);
        Assert.Equal(unchecked((int)0x80550C03), Call(matching, "NpMatching2ServerIdListLocal", (ushort)1, ids, (ushort)4));
        Assert.Equal(unchecked((int)0x80550C03), Call(matching, "NpMatching2RoomMemberEntriesLocal", (ushort)1, list, entries, count));
        Assert.Equal(0, Call(matching, "sceNpMatching2Init", 0x10000u, 0, 0, 0u));
        uint ctx = _harness.Word(0);
        Assert.Equal(0, Call(matching, "sceNpMatching2CreateContext", 0u, 0u, 0u, new Ptr<ushort>(ctx)));
        ushort id = GuestMemory.Read<ushort>(ctx);
        Assert.Equal(unchecked((int)0x80550C06), Call(matching, "NpMatching2ServerIdListLocal", (ushort)(id + 1), ids, (ushort)4));
        Assert.Equal(unchecked((int)0x80550C06), Call(matching, "NpMatching2RoomMemberEntriesLocal", (ushort)(id + 1), list, entries, count));

        Assert.Equal(0, Call(matching, "NpMatching2ServerIdListLocal", id, ids, (ushort)4)); // not started: no servers yet
        Assert.Equal(0xAAAAAAAAu, GuestMemory.R32(ids));
        Assert.Equal(0, Call(matching, "sceNpMatching2ContextStart", id, 0UL));
        Assert.Equal(0, Call(matching, "NpMatching2ServerIdListLocal", id, ids, (ushort)0));
        Assert.Equal(1, Call(matching, "NpMatching2ServerIdListLocal", id, ids, (ushort)4));
        Assert.Equal(1, GuestMemory.Read<ushort>(ids));

        Assert.Equal(unchecked((int)0x80550C0A), Call(matching, "NpMatching2RoomMemberEntriesLocal", id, 0u, entries, count));
        Assert.Equal(unchecked((int)0x80550C0A), Call(matching, "NpMatching2RoomMemberEntriesLocal", id, list, 0u, count));
        Assert.Equal(unchecked((int)0x80550C0A), Call(matching, "NpMatching2RoomMemberEntriesLocal", id, list, entries, 0u));
        Assert.Equal(unchecked((int)0x80550C2D), Call(matching, "NpMatching2RoomMemberEntriesLocal", id, list, entries, count));
        Assert.Equal(4u, GuestMemory.R32(count));
        Assert.Equal(0, Call(matching, "sceNpMatching2Term"));
    }

    /// <summary>The batch export checks its arguments, clears every result entry, then copies each entry's file into
    /// music0: (the result gets the new path and the file's result).</summary>
    [Fact]
    public void MusicExportBatchCopiesEveryEntry()
    {
        var vfs = new VitaSharp.Core.Io.Vfs(_exportRoot);
        string app = Path.Combine(_exportRoot, "app");
        Directory.CreateDirectory(app);
        vfs.Mount("app0", app);
        File.WriteAllBytes(Path.Combine(app, "a.mp3"), [0x49, 0x44, 0x33, 1]);
        File.WriteAllBytes(Path.Combine(app, "b.mp3"), [0x49, 0x44, 0x33, 2]);
        var m = new MediaExport { VfsOverride = vfs, TitleIdOverride = "NPXS10003" };
        uint input = _harness.Alloc(3 * MediaExport.MusicBatchInSize);
        uint results = _harness.Alloc(3 * MediaExport.MusicBatchOutSize);
        GuestMemory.WriteCString(input, "app0:a.mp3");
        GuestMemory.WriteCString(input + MediaExport.MusicBatchInSize, "app0:b.mp3");
        GuestMemory.WriteCString(input + 2 * MediaExport.MusicBatchInSize, "app0:none.mp3");

        Assert.Equal(MediaExport.MusicErrInvalid, Call(m, "MusicExportBatch", 0u, 1u, 0u, 0u, 0u, 0u, 0, results));
        Assert.Equal(MediaExport.MusicErrInvalid, Call(m, "MusicExportBatch", input, 0u, 0u, 0u, 0u, 0u, 0, results));
        Assert.Equal(MediaExport.MusicErrInvalid, Call(m, "MusicExportBatch", input, 11u, 0u, 0u, 0u, 0u, 0, results));
        Assert.Equal(MediaExport.MusicErrInvalid, Call(m, "MusicExportBatch", input, 1u, 0u, 0u, 0u, 0u, 3, results));
        Assert.Equal(MediaExport.MusicErrInvalid, Call(m, "MusicExportBatch", input, 1u, 0u, 0u, 0u, 0u, -1, results));

        Assert.Equal(0, Call(m, "MusicExportBatch", input, 2u, 0u, 0u, 0u, 0u, 2, results));
        Assert.Equal("music0:/NPXS10003/a.mp3", GuestMemory.ReadCString(results));
        Assert.Equal(0u, GuestMemory.R32(results + 0x400));
        Assert.Equal(0xFFFFFFFFu, GuestMemory.R32(results + 0x404));
        Assert.Equal("music0:/NPXS10003/b.mp3", GuestMemory.ReadCString(results + MediaExport.MusicBatchOutSize));
        Assert.Equal([0x49, 0x44, 0x33, 2], File.ReadAllBytes(vfs.Resolve("music0:/NPXS10003/b.mp3")!));

        Assert.Equal(MediaExport.ERR_NOENT, Call(m, "MusicExportBatch", input, 3u, 0u, 0u, 0u, 0u, 0, results));
        Assert.Equal("music0:/NPXS10003/a_1.mp3", GuestMemory.ReadCString(results));
        uint missing = results + 2 * MediaExport.MusicBatchOutSize;
        Assert.Equal("", GuestMemory.ReadCString(missing));
        Assert.Equal(unchecked((uint)MediaExport.ERR_NOENT), GuestMemory.R32(missing + 0x400));
    }
}
