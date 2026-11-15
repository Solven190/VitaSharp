using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceIofilemgrForDriver's kernel-only entries as the FW 3.74 iofilemgr has them: the VSH bridge's getstat, chstat and
/// dread, ksceIoSyncByFd2 and ksceIoFlock run the plain operation (same results and errors), ksceIoOpenForPid opens as
/// the process sees the path, and one path mapping function at a time.
/// </summary>
[Collection("guest")]
public sealed class KernelIoDriverTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int PathMappingBusy = unchecked((int)0x80010010);

    private readonly HleHarness _harness = new(fixture, "kiodriver");

    public void Dispose() => _harness.Dispose();

    private IoFileMgr Io => _harness.Module<IoFileMgr>();

    private void WriteFile(string relative, string text)
    {
        string path = Path.Combine(_harness.Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void TheBridgeVariantsGiveThePlainResults()
    {
        WriteFile(Path.Combine("ux0", "data", "kio", "five.txt"), "12345");
        _harness.OnGuest(_ =>
        {
            uint stat = _harness.Alloc(0x58);
            Assert.Equal(0, Call(Io, "ksceIoGetstatForVshbridge", "ux0:data/kio/five.txt", stat, 0u));
            Assert.Equal(5ul, GuestMemory.Read<ulong>(stat + 8));
            int missing = Call(Io, "sceIoGetstat", "ux0:data/kio/none.txt", stat);
            Assert.True(missing < 0);
            Assert.Equal(missing, Call(Io, "ksceIoGetstatForVshbridge", "ux0:data/kio/none.txt", stat, 0u));
            Assert.Equal(Call(Io, "sceIoChstat", "ux0:data/kio/five.txt", stat, 0),
                Call(Io, "ksceIoChstatForVshbridge", "ux0:data/kio/five.txt", stat, 0));

            int dir = Call(Io, "sceIoDopen", "ux0:data/kio");
            Assert.True(dir > 0);
            uint dirent = _harness.Alloc(0x160);
            Assert.True(Call(Io, "ksceIoDreadForVshbridge", dir, dirent) > 0);
            Assert.Equal("five.txt", GuestMemory.ReadCString(dirent + 0x58));
            Assert.Equal(0, Call(Io, "ksceIoDreadForVshbridge", dir, dirent));
            Assert.Equal(0, Call(Io, "sceIoDclose", dir));
        });
    }

    [Fact]
    public void OpenForPidSyncAndFlockWorkOnTheDescriptor()
    {
        WriteFile(Path.Combine("ux0", "data", "kio", "open.txt"), "abc");
        _harness.OnGuest(t =>
        {
            int fd = Call(Io, "ksceIoOpenForPid", t.Pid, "ux0:data/kio/open.txt", 1, 0);
            Assert.True(fd > 0);
            uint buffer = _harness.Alloc(8);
            Assert.Equal(3, Call(Io, "sceIoRead", fd, buffer, 8u));
            Assert.Equal(0, Call(Io, "ksceIoSyncByFd2", fd, 0));
            Assert.Equal(0, Call(Io, "ksceIoFlock", fd, 1));
            Assert.Equal(0, Call(Io, "sceIoClose", fd));
            Assert.Equal(SceError.IO_EBADF, Call(Io, "ksceIoSyncByFd2", fd, 0));
            Assert.Equal(SceError.IO_EBADF, Call(Io, "ksceIoFlock", fd, 1));
            Assert.True(Call(Io, "ksceIoOpenForPid", t.Pid, "ux0:data/kio/none.txt", 1, 0) < 0);
        });
    }

    [Fact]
    public void OnePathMappingFunctionAtATime()
    {
        Assert.Equal(0, Call(Io, "ksceIoSetPathMappingFunction", 0x81001001u));
        Assert.Equal(PathMappingBusy, Call(Io, "ksceIoSetPathMappingFunction", 0x81002001u));
        Assert.Equal(0, Call(Io, "ksceIoSetPathMappingFunction", 0u));
        Assert.Equal(0, Call(Io, "ksceIoSetPathMappingFunction", 0x81002001u));
    }
}
