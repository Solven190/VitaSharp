using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The per-thread permission word of the FW 3.74 thread control block (+0x2C: the kernel process's 0x80 for a kernel
/// thread, set and restored by kernel code, kept per thread) and the attribute bits SceThreadmgrForDriver reports
/// (a user thread; bit 14 by UID).
/// </summary>
[Collection("guest")]
public sealed class KernelThreadPermissionTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int IllegalContext = unchecked((int)0x80027101), InvalidArgument = unchecked((int)0x80020005);

    private readonly HleHarness _harness = new(fixture, "kperm");

    public void Dispose() => _harness.Dispose();

    private ThreadmgrKernel Threadmgr => _harness.Module<ThreadmgrKernel>();

    [Fact]
    public void APermissionIsSetAndRestoredPerThread()
    {
        _harness.OnGuest(t =>
        {
            t.KernelMode = true;
            Assert.Equal(0x80, Call(Threadmgr, "NID_332E127C"));
            Assert.Equal(0x80, Call(Threadmgr, "ksceKernelSetPermission", 0x40));
            Assert.Equal(0x40, Call(Threadmgr, "NID_332E127C"));
            Assert.Equal(InvalidArgument, Call(Threadmgr, "ksceKernelSetPermission", -1));
            Assert.Equal(0x40, Call(Threadmgr, "ksceKernelSetPermission", 0x80));
            Assert.Equal(0x80, Call(Threadmgr, "NID_332E127C"));
            Assert.Equal(0x80, Call(Threadmgr, "ksceKernelSetPermission", 0x10));
        }, "first");

        // another thread keeps its own word; a user thread starts at 0
        _harness.OnGuest(t =>
        {
            Assert.Equal(0, Call(Threadmgr, "NID_332E127C"));
            Assert.Equal(0, Call(Threadmgr, "ksceKernelSetPermission", 0x80));
            Assert.Equal(0x80, Call(Threadmgr, "NID_332E127C"));
        }, "second");

        // no current thread: the value is checked first, then the context
        Assert.Equal(InvalidArgument, Call(Threadmgr, "ksceKernelSetPermission", -1));
        Assert.Equal(IllegalContext, Call(Threadmgr, "ksceKernelSetPermission", 0x80));
        Assert.Equal(IllegalContext, Call(Threadmgr, "NID_332E127C"));
    }

    [Fact]
    public void TheAttributeQueriesFollowTheThread()
    {
        GuestThread flagged = _harness.Emu.Kernel.CreateThread("flagged", 0, 0x10000100, 0x4000, 0x4000, 0x70000);
        GuestThread plain = _harness.Emu.Kernel.CreateThread("plain", 0, 0x10000100, 0x4000, 0, 0x70000);
        Assert.Equal(1u, (uint)Call(Threadmgr, "NID_1096042E", flagged.Uid));
        Assert.Equal(0u, (uint)Call(Threadmgr, "NID_1096042E", plain.Uid));
        Assert.Equal(0x40000000u, (uint)Call(Threadmgr, "NID_1096042E", 0x7FFF0001));
        Assert.Equal(0u, (uint)Call(Threadmgr, "NID_1096042E", 0));
        Assert.Equal(0, Call(Threadmgr, "NID_91382762"));
        _harness.OnGuest(t =>
        {
            Assert.Equal(1, Call(Threadmgr, "NID_91382762"));
            t.KernelMode = true;
            Assert.Equal(0, Call(Threadmgr, "NID_91382762"));
        });
    }
}
