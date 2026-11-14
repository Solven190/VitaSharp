using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Io;
using VitaSharp.Core.Loader.Self;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The PlayStation Mobile side of appmgr and apputil (FW 3.74 SceAppMgr and apputil.suprx, read for study): the PSM
/// runtime names its title with sceAppMgrInitSafeMemoryById (SceAppUtilPsm NID 0xDEB51A38 wraps it), which keeps the
/// title as the process's PSM title and binds its safe memory to ux0:psm/&lt;id&gt;/RW/System/pm.dat; the NID the runtime
/// 1.00 imports and FW 3.74 does not export answers as the importer's unresolved stub.
/// </summary>
[Collection("guest")]
public sealed class PsmHleTests : IDisposable
{
    private const int ERR_NULL = unchecked((int)0x80800001), ERR_NOT_ALLOWED = unchecked((int)0x80800009),
        ERR_ALREADY_SET = unchecked((int)0x8080004A), ERR_SAFE_MEMORY_FOLDER = unchecked((int)0x80800049),
        ERR_NULL_NAME = unchecked((int)0x80801001), ERR_TOO_LONG = unchecked((int)0x8002710B);
    private const int UTIL_PARAMETER = unchecked((int)0x80100600), UTIL_NOT_INITIALIZED = unchecked((int)0x80100601),
        UTIL_BUSY = unchecked((int)0x80100603), UTIL_NO_PERMISSION = unchecked((int)0x80100605);
    private const string Title = "NPOA00239";

    private readonly HleHarness _harness;

    public PsmHleTests(GuestMemoryFixture fixture)
    {
        _harness = new HleHarness(fixture, "psm");
        _harness.Emu.TitleId = "PCSI00011";
        _harness.Emu.Vfs.Mount("savedata0", Path.Combine(_harness.Root, "savedata"));
    }

    public void Dispose() => _harness.Dispose();

    private AppMgr AppMgr => _harness.Module<AppMgr>();

    private AppUtil AppUtil => _harness.Module<AppUtil>();

    private string PmDat => FirmwareLayout.PsmTitles.Host(_harness.Root, Title, FirmwareLayout.PsmWritable, "System", "pm.dat");

    private void RunAsPsmRuntime() => _harness.Emu.Access = new ProgramAccess(ProgramAccess.PsmRuntimeAuthorityId, null, false);

    private void InitAppUtil() => Assert.Equal(0, Call(AppUtil, "sceAppUtilInit", _harness.Alloc(0x40), _harness.Alloc(0x40)));

    [Fact]
    public void InitSafeMemoryByIdNamesThePsmTitleOfTheRuntime()
    {
        uint title = _harness.Str(Title);
        // only a PSM runtime may name a PSM title
        Assert.Equal(ERR_NOT_ALLOWED, Call(AppMgr, "sceAppMgrInitSafeMemoryById", title));
        Assert.Equal(PsmRuntimeKind.None, _harness.Emu.PsmRuntime);
        RunAsPsmRuntime();
        Assert.Equal(PsmRuntimeKind.Mono, _harness.Emu.PsmRuntime);
        Assert.Equal(ERR_NULL, Call(AppMgr, "sceAppMgrInitSafeMemoryById", 0u));
        Assert.Equal(ERR_NULL, Call(AppMgr, "sceAppMgrInitSafeMemoryById", _harness.Str("")));
        Assert.Equal(ERR_NULL, Call(AppMgr, "sceAppMgrInitSafeMemoryById", _harness.Str("npoa00239")));
        Assert.Equal(ERR_NULL, Call(AppMgr, "sceAppMgrInitSafeMemoryById", _harness.Str("NPOA0023")));
        Assert.Equal(ERR_TOO_LONG, Call(AppMgr, "sceAppMgrInitSafeMemoryById", _harness.Str(new string('A', 0x40))));
        // the title's RW/System must be there (the installer makes it)
        Assert.Equal(ERR_SAFE_MEMORY_FOLDER, Call(AppMgr, "sceAppMgrInitSafeMemoryById", title));
        Assert.Null(AppMgr.PsmTitleId);
        Directory.CreateDirectory(Path.GetDirectoryName(PmDat)!);
        Assert.Equal(0, Call(AppMgr, "sceAppMgrInitSafeMemoryById", title));
        Assert.Equal(Title, AppMgr.PsmTitleId);
        // no pm.dat yet: the file is made from the area (64 KiB)
        Assert.Equal(64 * 1024, new FileInfo(PmDat).Length);
        Assert.Equal(ERR_ALREADY_SET, Call(AppMgr, "sceAppMgrInitSafeMemoryById", title));
        Assert.Equal(ERR_ALREADY_SET, Call(AppMgr, "_sceAppMgrInitSafeMemoryById", title, 0u));
    }

    [Fact]
    public void SafeMemoryOfThePsmTitleIsItsPmDat()
    {
        RunAsPsmRuntime();
        InitAppUtil();
        // the application's own area first
        uint buf = _harness.Alloc(16);
        GuestMemory.Fill(buf, 0x11, 16);
        Assert.Equal(16, Call(AppUtil, "sceAppUtilSaveSafeMemory", buf, 16u, 0L));
        // an existing pm.dat of the area's size is read into the area
        Directory.CreateDirectory(Path.GetDirectoryName(PmDat)!);
        byte[] saved = new byte[64 * 1024];
        saved.AsSpan(0, 16).Fill(0x5A);
        File.WriteAllBytes(PmDat, saved);
        Assert.Equal(0, Call(AppMgr, "sceAppMgrInitSafeMemoryById", _harness.Str(Title)));
        uint back = _harness.Alloc(16);
        Assert.Equal(16, Call(AppUtil, "sceAppUtilLoadSafeMemory", back, 16u, 0L));
        Assert.All(GuestMemory.ReadSpan(back, 16).ToArray(), b => Assert.Equal(0x5A, b));
        // and later saves go to it
        GuestMemory.Fill(buf, 0x77, 16);
        Assert.Equal(16, Call(AppUtil, "sceAppUtilSaveSafeMemory", buf, 16u, 32L));
        Assert.Equal(0x77, File.ReadAllBytes(PmDat)[32]);
    }

    [Fact]
    public void AppUtilPsmWrapsTheKernelAnswers()
    {
        uint title = _harness.Str(Title);
        Assert.Equal(UTIL_NOT_INITIALIZED, Call(AppUtil, "PsmInitSafeMemory", title));
        InitAppUtil();
        Assert.Equal(UTIL_NO_PERMISSION, Call(AppUtil, "PsmInitSafeMemory", title));
        RunAsPsmRuntime();
        Assert.Equal(UTIL_PARAMETER, Call(AppUtil, "PsmInitSafeMemory", 0u));
        Assert.Equal(UTIL_PARAMETER, Call(AppUtil, "PsmInitSafeMemory", _harness.Str("bad")));
        Directory.CreateDirectory(Path.GetDirectoryName(PmDat)!);
        Assert.Equal(0, Call(AppUtil, "PsmInitSafeMemory", title));
        Assert.Equal(UTIL_BUSY, Call(AppUtil, "PsmInitSafeMemory", title));
    }

    [Fact]
    public void OwnRuntimeNamesItsSafeMemoryInTheCache()
    {
        // a runtime a PSM title carries (Unity for PSM) runs from ux0:psm/<id>/runtime
        string runtime = FirmwareLayout.PsmTitles.Host(_harness.Root, Title, FirmwareLayout.PsmOwnRuntime);
        Directory.CreateDirectory(runtime);
        _harness.Emu.AppPath = runtime;
        Assert.Equal(PsmRuntimeKind.OwnRuntime, _harness.Emu.PsmRuntime);
        Assert.Equal(ERR_NULL_NAME, Call(AppMgr, "sceAppMgrInitSafeMemoryById", 0u));
        Assert.Equal(ERR_NULL, Call(AppMgr, "sceAppMgrInitSafeMemoryById", _harness.Str("a/b")));
        string folder = FirmwareLayout.ApplicationCache.Host(_harness.Root, "PCSI00011", "game-1", "System");
        Directory.CreateDirectory(folder);
        Assert.Equal(0, Call(AppMgr, "sceAppMgrInitSafeMemoryById", _harness.Str("game-1")));
        Assert.True(File.Exists(Path.Combine(folder, "pm.dat")));
        Assert.Equal("game-1", AppMgr.PsmTitleId);
    }

    [Fact]
    public void NidTheFirmwareDoesNotExportReturnsLikeTheUnresolvedStub()
    {
        // the PSM runtime 1.00 imports SceAppMgrUser NID 0x9D6D8058, which no FW 3.74 module exports: the importer's
        // own stub runs (mvn r0, #0; bx lr)
        Assert.Equal(0xFFFFFFFFu, Convert.ToUInt32(Invoke(AppMgr, "NotExported9D6D8058")));
        Assert.True(_harness.Emu.Hle.IsImplemented(0x9D6D8058));
        Assert.True(_harness.Emu.Hle.IsImplemented(0xDEB51A38));
    }

    [Fact]
    public void TargetTransportAnswersLikeTheUnresolvedStub()
    {
        // no retail firmware module exports SceLibTargetTransport (the devkit's host transport libmono_bridge imports)
        Assert.Equal(0xFFFFFFFFu, Convert.ToUInt32(Invoke(_harness.Module<DebugPerf>(), "TargetTransportUnresolved")));
        foreach (uint nid in new uint[] { 0x11EBD107, 0x1AD7239E, 0x4CF1664B, 0x4F644C0F, 0x9D3FC23C, 0xD45C74BE })
        {
            Assert.True(_harness.Emu.Hle.IsImplemented(nid));
        }
    }
}
