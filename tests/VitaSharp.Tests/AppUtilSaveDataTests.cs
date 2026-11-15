using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceAppUtil save data on a savedata0: of the test's own folder: the 64 KiB safe memory round trip, slots
/// (SceAppUtilSaveDataSlotParam, 0x34C bytes: create, get, set only on an existing slot, delete), the file list of
/// sceAppUtilSaveDataDataSave (write at an offset, truncate, make a directory) and its removal, and slot search sorted
/// by id or user parameter. Errors are SCE_APPUTIL_ERROR_* (0x801006xx).
/// </summary>
[Collection("guest")]
public sealed class AppUtilSaveDataTests : IDisposable
{
    private const int ERR_PARAMETER = unchecked((int)0x80100600);
    private const int ERR_NOT_INITIALIZED = unchecked((int)0x80100601);
    private readonly HleHarness _harness;
    private readonly string _save;
    private AppUtil AppUtil => _harness.Module<AppUtil>();

    public AppUtilSaveDataTests(GuestMemoryFixture fixture)
    {
        _harness = new HleHarness(fixture, "apputil");
        _save = Path.Combine(_harness.Root, "savedata");
        Directory.CreateDirectory(_save);
        _harness.Emu.Vfs.Mount("savedata0", _save);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void SafeMemoryRoundTrip()
    {
        uint buf = _harness.Alloc(64);
        GuestMemory.Fill(buf, 0xEE, 64);
        // apputil.suprx 3.74 answers both before sceAppUtilInit with NOT_INITIALIZED
        Assert.Equal(ERR_NOT_INITIALIZED, Call(AppUtil, "sceAppUtilLoadSafeMemory", buf, 64u, 0L));
        Assert.Equal(ERR_NOT_INITIALIZED, Call(AppUtil, "sceAppUtilSaveSafeMemory", buf, 64u, 0L));
        Assert.Equal(0, Call(AppUtil, "sceAppUtilInit", _harness.Alloc(0x40), _harness.Alloc(0x40)));
        Assert.Equal(64, Call(AppUtil, "sceAppUtilLoadSafeMemory", buf, 64u, 0L)); // nothing saved yet: zeros, and the size copied (apputil.suprx 3.74)
        Assert.All(GuestMemory.ReadSpan(buf, 64).ToArray(), b => Assert.Equal(0, b));
        for (uint i = 0; i < 64; i++)
        {
            GuestMemory.Write(buf + i, (byte)(i + 1));
        }

        Assert.Equal(64, Call(AppUtil, "sceAppUtilSaveSafeMemory", buf, 64u, 100L));
        Assert.True(File.Exists(Path.Combine(_save, "sce_sys", "safemem.dat")));
        Assert.Equal(64 * 1024, new FileInfo(Path.Combine(_save, "sce_sys", "safemem.dat")).Length);
        uint back = _harness.Alloc(64);
        Assert.Equal(64, Call(AppUtil, "sceAppUtilLoadSafeMemory", back, 64u, 100L));
        Assert.Equal(GuestMemory.ReadSpan(buf, 64).ToArray(), GuestMemory.ReadSpan(back, 64).ToArray());
        Assert.Equal(ERR_PARAMETER, Call(AppUtil, "sceAppUtilSaveSafeMemory", buf, 64u, 64L * 1024 - 32));
        Assert.Equal(ERR_PARAMETER, Call(AppUtil, "sceAppUtilLoadSafeMemory", 0u, 4u, 0L));
        Assert.Equal(unchecked((int)0x80801001), AppUtil.LoadSafeMemoryKernel(0u, 4u, 0L)); // the kernel code sceAppMgrLoadSafeMemory returns
        Assert.Equal(unchecked((int)0x80801000), AppUtil.LoadSafeMemoryKernel(buf, 64u, 64L * 1024));
        Assert.Equal(unchecked((int)0x80801001), AppUtil.SaveSafeMemoryKernel(0u, 4u, 0L)); // what sceAppMgrSaveSafeMemory returns
        Assert.Equal(unchecked((int)0x80801000), AppUtil.SaveSafeMemoryKernel(buf, 64u, 64L * 1024 - 32));
        Assert.Equal(0, Call(AppUtil, "sceAppUtilLoadSafeMemory", buf, 0u, 0L));
    }

    private uint SlotParam(string title, uint userParam)
    {
        uint p = _harness.Alloc(AppUtil.SlotParamSize);
        GuestMemory.WriteCString(p + 4, title, 64);
        GuestMemory.W32(p + 4 + 64 + 128 + 512 + 64, userParam);
        return p;
    }

    [Fact]
    public void SlotsCreateUpdateSearchAndDelete()
    {
        uint get = _harness.Alloc(AppUtil.SlotParamSize);
        Assert.NotEqual(0, Call(AppUtil, "sceAppUtilSaveDataSlotGetParam", 3u, get, 0u)); // no such slot
        Assert.NotEqual(0, Call(AppUtil, "sceAppUtilSaveDataSlotSetParam", 3u, SlotParam("x", 0), 0u)); // set needs the slot
        Assert.Equal(ERR_PARAMETER, Call(AppUtil, "sceAppUtilSaveDataSlotSetParam", 3u, 0u, 0u));
        Assert.Equal(0, Call(AppUtil, "sceAppUtilSaveDataSlotCreate", 3u, SlotParam("Chapter 1", 30), 0u));
        Assert.Equal(0, Call(AppUtil, "sceAppUtilSaveDataSlotCreate", 1u, SlotParam("Chapter 2", 10), 0u));
        Assert.Equal(0, Call(AppUtil, "sceAppUtilSaveDataSlotCreate", 2u, SlotParam("Chapter 3", 20), 0u));
        Assert.True(AppUtil.SlotExists(3));
        Assert.Equal(0, Call(AppUtil, "sceAppUtilSaveDataSlotGetParam", 3u, get, 0u));
        Assert.Equal("Chapter 1", GuestMemory.ReadCString(get + 4, 64));
        Assert.Equal(0u, GuestMemory.R32(get)); // status: available
        Assert.Equal(0, Call(AppUtil, "sceAppUtilSaveDataSlotSetParam", 3u, SlotParam("Chapter 1b", 30), 0u));
        Call(AppUtil, "sceAppUtilSaveDataSlotGetParam", 3u, get, 0u);
        Assert.Equal("Chapter 1b", GuestMemory.ReadCString(get + 4, 64));

        // search used slots (type 0) 0..9 by user parameter, then by id descending; free slots (type 1)
        uint cond = _harness.Alloc(0x20), result = _harness.Alloc(0x10), list = _harness.Alloc(16 * 16), work = _harness.Alloc(8);
        GuestMemory.W32(work, list);
        GuestMemory.W32(work + 4, 16 * 16);
        GuestMemory.W32(cond, 0);
        GuestMemory.W32(cond + 4, 0);
        GuestMemory.W32(cond + 8, 10);
        GuestMemory.W32(cond + 12, 1);
        GuestMemory.W32(cond + 16, 0);
        Assert.Equal(0, Call(AppUtil, "sceAppUtilSaveDataSlotSearch", work, cond, result, 0u));
        Assert.Equal(3u, GuestMemory.R32(result));
        Assert.Equal(new uint[] { 1, 2, 3 }, Enumerable.Range(0, 3).Select(i => GuestMemory.R32(list + (uint)i * 16)).ToArray());
        Assert.Equal(10u, GuestMemory.R32(list + 8));
        GuestMemory.W32(cond + 12, 0);
        GuestMemory.W32(cond + 16, 1);
        Call(AppUtil, "sceAppUtilSaveDataSlotSearch", work, cond, result, 0u);
        Assert.Equal(new uint[] { 3, 2, 1 }, Enumerable.Range(0, 3).Select(i => GuestMemory.R32(list + (uint)i * 16)).ToArray());
        GuestMemory.W32(cond, 1);
        GuestMemory.W32(cond + 16, 0);
        Call(AppUtil, "sceAppUtilSaveDataSlotSearch", work, cond, result, 0u);
        Assert.Equal(7u, GuestMemory.R32(result)); // 0 and 4..9 are free
        Assert.Equal(ERR_PARAMETER, Call(AppUtil, "sceAppUtilSaveDataSlotSearch", work, 0u, result, 0u));

        Assert.Equal(0, Call(AppUtil, "sceAppUtilSaveDataSlotDelete", 2u, 0u));
        Assert.False(AppUtil.SlotExists(2));
        Assert.Equal(0, Call(AppUtil, "sceAppUtilSaveDataSlotDelete", 2u, 0u)); // already gone: fine
    }

    [Fact]
    public void DataSaveWritesFilesAndUpdatesTheSlot()
    {
        uint path = _harness.Str("savedata0:game/progress.bin"), dirPath = _harness.Str("savedata0:game/extra");
        uint data = _harness.Alloc(16);
        for (uint i = 0; i < 16; i++)
        {
            GuestMemory.Write(data + i, (byte)(0x40 + i));
        }
        // SceAppUtilSaveDataFile (0x40): dataPath, buf, bufSize, pad, offset (s64), mode (0 write, 1 truncate, 2 dir)
        uint files = _harness.Alloc(64 * 3);
        GuestMemory.W32(files, path);
        GuestMemory.W32(files + 4, data);
        GuestMemory.W32(files + 8, 16);
        GuestMemory.W32(files + 64, path);
        GuestMemory.W32(files + 68, data);
        GuestMemory.W32(files + 72, 4);
        GuestMemory.Write(files + 64 + 16, 20L);
        GuestMemory.W32(files + 64 + 24, 1); // 4 bytes at 20, then cut there
        GuestMemory.W32(files + 128, dirPath);
        GuestMemory.W32(files + 128 + 24, 2);
        uint slot = _harness.Alloc(8), req = _harness.Word(9);
        GuestMemory.W32(slot, 5);
        GuestMemory.W32(slot + 4, SlotParam("Autosave", 7));
        Assert.Equal(0, Call(AppUtil, "sceAppUtilSaveDataDataSave", slot, files, 3u, 0u, new Ptr<uint>(req)));
        Assert.Equal(0u, GuestMemory.R32(req));
        string host = Path.Combine(_save, "game", "progress.bin");
        byte[] bytes = File.ReadAllBytes(host);
        Assert.Equal(24, bytes.Length);
        Assert.Equal((byte)0x40, bytes[0]);
        Assert.Equal((byte)0, bytes[16]);
        Assert.Equal((byte)0x40, bytes[20]);
        Assert.True(Directory.Exists(Path.Combine(_save, "game", "extra")));
        // the slot was written with the save's time
        uint get = _harness.Alloc(AppUtil.SlotParamSize);
        Assert.Equal(0, Call(AppUtil, "sceAppUtilSaveDataSlotGetParam", 5u, get, 0u));
        Assert.Equal("Autosave", GuestMemory.ReadCString(get + 4, 64));
        Assert.Equal((ushort)DateTime.Now.Year, GuestMemory.Read<ushort>(get + 4 + 64 + 128 + 512 + 64 + 8));

        // removal: SceAppUtilSaveDataRemoveItem (0x2C) {dataPath, mode}, paths relative to the mount point; mode 0 also
        // removes the slot, which then may not carry a new param
        Assert.Equal(0, Call(AppUtil, "sceAppUtilInit", _harness.Alloc(0x40), _harness.Alloc(0x40)));
        uint rm = RemoveList(("game/progress.bin", 0u), ("game/extra", 0u));
        GuestMemory.W32(slot + 4, 0);
        Assert.Equal(0, Call(AppUtil, "sceAppUtilSaveDataDataRemove", slot, rm, 2u, 0u));
        Assert.False(File.Exists(host));
        Assert.False(Directory.Exists(Path.Combine(_save, "game", "extra")));
        Assert.False(AppUtil.SlotExists(5));
        GuestMemory.W32(slot, 256);
        Assert.Equal(ERR_PARAMETER, Call(AppUtil, "sceAppUtilSaveDataDataRemove", slot, rm, 0u, 0u));
    }

    /// <summary>A SceAppUtilSaveDataRemoveItem list (0x2C bytes each: dataPath*, mode, reserved[36] zero).</summary>
    private uint RemoveList(params (string Path, uint Mode)[] items)
    {
        uint list = _harness.Alloc(AppUtil.RemoveItemSize * (uint)items.Length);
        GuestMemory.Fill(list, 0, AppUtil.RemoveItemSize * (uint)items.Length);
        for (int i = 0; i < items.Length; i++)
        {
            uint item = list + (uint)i * AppUtil.RemoveItemSize;
            GuestMemory.W32(item, _harness.Str(items[i].Path));
            GuestMemory.W32(item + 4, items[i].Mode);
        }

        return list;
    }

    private string MakeSaveFile(string relative)
    {
        string host = Path.Combine(_save, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(host)!);
        File.WriteAllBytes(host, [1, 2, 3]);
        return host;
    }

    /// <summary>apputil.suprx 3.74 (0x81000D2C, 0x81001D6C, 0x81001BEC) checks the whole remove list before the kernel
    /// removes anything: NOT_INITIALIZED before sceAppUtilInit; PARAMETER for a NULL list, 0 or more than 5 items, a
    /// path with a character outside letters/digits/-/_/./slashes (a "savedata0:" prefix too), "..", more than 3
    /// separators, an "sce_" name, a mode other than DEFAULT/NO_SLOT, reserved bytes set, a mount point other than
    /// savedata0:, or a new slot param while item 0 removes the slot. A path that does not exist is not an error.
    /// Before, every list was removed as given: "savedata0:" paths, ".." and "sce_sys" too.</summary>
    [Fact]
    public void DataRemoveChecksTheWholeListBeforeRemovingAnything()
    {
        string keep = MakeSaveFile(Path.Combine("game", "keep.bin"));
        uint first = RemoveList(("game/keep.bin", 1u));
        Assert.Equal(ERR_NOT_INITIALIZED, Call(AppUtil, "sceAppUtilSaveDataDataRemove", 0u, first, 1u, 0u));
        Assert.True(File.Exists(keep));
        Assert.Equal(0, Call(AppUtil, "sceAppUtilInit", _harness.Alloc(0x40), _harness.Alloc(0x40)));

        Assert.Equal(ERR_PARAMETER, Call(AppUtil, "sceAppUtilSaveDataDataRemove", 0u, 0u, 1u, 0u));
        Assert.Equal(ERR_PARAMETER, Call(AppUtil, "sceAppUtilSaveDataDataRemove", 0u, first, 0u, 0u));
        uint six = RemoveList(("game/keep.bin", 1u), ("a", 1u), ("b", 1u), ("c", 1u), ("d", 1u), ("e", 1u));
        Assert.Equal(ERR_PARAMETER, Call(AppUtil, "sceAppUtilSaveDataDataRemove", 0u, six, 6u, 0u));
        Assert.True(File.Exists(keep));

        // a bad second item: the first, valid one is not removed either
        foreach (string bad in new[] { "savedata0:game", "game/../..", "a/b/c/d/e", "sce_sys", "game/sce_x", "game/k*" })
        {
            uint list = RemoveList(("game/keep.bin", 1u), (bad, 1u));
            Assert.Equal(ERR_PARAMETER, Call(AppUtil, "sceAppUtilSaveDataDataRemove", 0u, list, 2u, 0u));
        }

        uint badMode = RemoveList(("game/keep.bin", 1u), ("x", 2u));
        Assert.Equal(ERR_PARAMETER, Call(AppUtil, "sceAppUtilSaveDataDataRemove", 0u, badMode, 2u, 0u));
        uint reservedSet = RemoveList(("game/keep.bin", 1u), ("x", 1u));
        GuestMemory.Write(reservedSet + AppUtil.RemoveItemSize + 8 + 35, (byte)1);
        Assert.Equal(ERR_PARAMETER, Call(AppUtil, "sceAppUtilSaveDataDataRemove", 0u, reservedSet, 2u, 0u));
        uint otherMount = _harness.Str("savedata1:");
        Assert.Equal(ERR_PARAMETER, Call(AppUtil, "sceAppUtilSaveDataDataRemove", 0u, first, 1u, otherMount));
        uint slot = _harness.Alloc(8);
        GuestMemory.W32(slot, 1);
        GuestMemory.W32(slot + 4, SlotParam("new", 0));
        uint removesSlot = RemoveList(("game/keep.bin", 0u));
        Assert.Equal(ERR_PARAMETER, Call(AppUtil, "sceAppUtilSaveDataDataRemove", slot, removesSlot, 1u, 0u));
        Assert.True(File.Exists(keep));

        // the same list, valid: removed; a path that does not exist is fine; savedata0: named as the mount point
        uint ok = RemoveList(("game/keep.bin", 1u), ("game\\missing", 1u));
        Assert.Equal(0, Call(AppUtil, "sceAppUtilSaveDataDataRemove", 0u, ok, 2u, _harness.Str("savedata0:")));
        Assert.False(File.Exists(keep));
    }

    /// <summary>An empty path names savedata0: itself, which both apputil and SceAppMgr accept (FW 3.74): its contents
    /// go, the save data root and the system's own data in it (sce_sys, the slot params) stay. Before, the root was
    /// deleted as a whole, sce_sys and slots included.</summary>
    [Fact]
    public void DataRemoveOfTheEmptyPathEmptiesSaveDataButKeepsTheSystemData()
    {
        Assert.Equal(0, Call(AppUtil, "sceAppUtilInit", _harness.Alloc(0x40), _harness.Alloc(0x40)));
        string game = MakeSaveFile(Path.Combine("game", "progress.bin"));
        string top = MakeSaveFile("top.bin");
        string system = MakeSaveFile(Path.Combine("sce_sys", "safemem.dat"));
        Assert.Equal(0, Call(AppUtil, "sceAppUtilSaveDataSlotCreate", 3u, SlotParam("kept", 0), 0u));
        uint list = RemoveList(("", 1u));
        Assert.Equal(0, Call(AppUtil, "sceAppUtilSaveDataDataRemove", 0u, list, 1u, 0u));
        Assert.False(File.Exists(game));
        Assert.False(File.Exists(top));
        Assert.False(Directory.Exists(Path.Combine(_save, "game")));
        Assert.True(Directory.Exists(_save));
        Assert.True(File.Exists(system));
        Assert.True(AppUtil.SlotExists(3));
        uint dot = RemoveList((".", 1u));
        Assert.Equal(0, Call(AppUtil, "sceAppUtilSaveDataDataRemove", 0u, dot, 1u, 0u));
        Assert.True(Directory.Exists(_save));
        Assert.True(File.Exists(system));
    }

    /// <summary>A file the host refuses to delete (another program holds it; no permission) makes the removal return
    /// an I/O error to the guest, as the kernel's iofilemgr error would be; before, the host exception went up through
    /// the HLE call and stopped the guest.</summary>
    [Fact]
    public void DataRemoveReturnsAnErrorWhenTheHostRefusesTheDelete()
    {
        Assert.Equal(0, Call(AppUtil, "sceAppUtilInit", _harness.Alloc(0x40), _harness.Alloc(0x40)));
        string held = MakeSaveFile(Path.Combine("game", "held.bin"));
        string folder = Path.GetDirectoryName(held)!;
        uint list = RemoveList(("game/held.bin", 1u));
        FileStream? holder = null;
        if (OperatingSystem.IsWindows())
        {
            holder = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        else
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        try
        {
            if (!OperatingSystem.IsWindows() && Environment.UserName == "root")
            {
                return; // root ignores the folder's permissions: nothing refuses the delete
            }

            int result = Call(AppUtil, "sceAppUtilSaveDataDataRemove", 0u, list, 1u, 0u);
            Assert.True(result < 0, $"0x{result:X8}");
            Assert.True(File.Exists(held));
        }
        finally
        {
            holder?.Dispose();
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        Assert.Equal(0, Call(AppUtil, "sceAppUtilSaveDataDataRemove", 0u, list, 1u, 0u));
        Assert.False(File.Exists(held));
    }

    /// <summary>sceAppMgrSaveDataDataRemove (what apputil calls, FW 3.74 SceAppMgr 0x81029E3C): the request is 0x44
    /// bytes (another size 0x80800001) and its items are 0x2C bytes apart, as apputil passes them. Before, the items
    /// were read 0x40 apart (Vita3K), so the second item's path came from the wrong place.</summary>
    [Fact]
    public void AppMgrDataRemoveReadsItemsOfTheApputilLayout()
    {
        string a = MakeSaveFile("a.bin");
        string b = MakeSaveFile("b.bin");
        uint list = RemoveList(("a.bin", 1u), ("b.bin", 1u));
        uint request = _harness.Alloc(0x44);
        GuestMemory.Fill(request, 0, 0x44);
        GuestMemory.W32(request, 0x48);
        GuestMemory.W32(request + 4, 0xFFFFFFFF);
        GuestMemory.W32(request + 0x2C, list);
        GuestMemory.W32(request + 0x30, 2);
        AppMgr appMgr = _harness.Module<AppMgr>();
        Assert.Equal(AppUtil.ERR_APPMGR_INVALID, Call(appMgr, "sceAppMgrSaveDataDataRemove", request));
        Assert.True(File.Exists(a));
        GuestMemory.W32(request, 0x44);
        Assert.Equal(0, Call(appMgr, "sceAppMgrSaveDataDataRemove", request));
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
    }

    [Fact]
    public void AppEventsAndBrowserStubs()
    {
        uint ev = _harness.Alloc(0x1C);
        Assert.Equal(0, Call(AppUtil, "sceAppUtilReceiveAppEvent", ev));
        Assert.Equal(0u, GuestMemory.R32(ev)); // no event
        foreach (string? fn in new[] { "sceAppUtilAppEventParseLiveArea", "sceAppUtilAppEventParseNearGift", "sceAppUtilAppEventParseNpBasicJoinablePresence",
            "sceAppUtilAppEventParseNpInviteMessage", "sceAppUtilAppEventParseNpAppDataMessage", "sceAppUtilAppEventParseGameCustomData",
            "sceAppUtilAppEventParseIncomingDialog", "sceAppUtilAppEventParseNpActivity", "sceAppUtilAppEventParseScreenShotNotification",
            "sceAppUtilAppEventParseSessionInvitation", "sceAppUtilAppEventParseTeleport", "sceAppUtilAppEventParseTriggerUtil", "sceAppUtilAppEventParseWebBrowser" })
        {
            Assert.Equal(ERR_PARAMETER, Call(AppUtil, fn, 0u, 0u));
            Assert.NotEqual(0, Call(AppUtil, fn, ev, _harness.Alloc(0x100))); // never an event of that type
        }
        Assert.Equal(0, Call(AppUtil, "sceAppUtilLaunchWebBrowser", 0u));
        Assert.Equal(ERR_PARAMETER, Call(AppUtil, "sceAppUtilAddCookieWebBrowser", 0u));
        Assert.Equal(0, Call(AppUtil, "sceAppUtilResetCookieWebBrowser"));
    }
}
