using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The per-thread words of the FW 3.74 thread control block that SceThreadmgrForDriver reads and writes for the current
/// thread: the process id in TLS (+0xFC, from the thread's process: the kernel's for a kernel thread) and +0x100.
/// </summary>
[Collection("guest")]
public sealed class KernelThreadTlsTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int IllegalContext = unchecked((int)0x80027101), InvalidTlsProcessId = unchecked((int)0x80024501);

    private readonly HleHarness _harness = new(fixture, "ktls");

    public void Dispose() => _harness.Dispose();

    private ThreadmgrKernel Threadmgr => _harness.Module<ThreadmgrKernel>();

    [Fact]
    public void TheProcessIdInTlsStartsAsTheThreadsProcessAndIsPerThread()
    {
        int firstPid = 0;
        _harness.OnGuest(t =>
        {
            Assert.Equal(t.Pid, Call(Threadmgr, "ksceKernelGetProcessIdFromTLS"));
            Assert.Equal(t.Pid, Call(Threadmgr, "ksceKernelSetProcessIdToTLS", KernelSpace.KernelPid));
            Assert.Equal(KernelSpace.KernelPid, Call(Threadmgr, "ksceKernelGetProcessIdFromTLS"));
            Assert.Equal(KernelSpace.KernelPid, Call(Threadmgr, "ksceKernelSetProcessIdToTLS", t.Pid));
            Assert.Equal(t.Pid, Call(Threadmgr, "ksceKernelGetProcessIdFromTLS"));
            Assert.Equal(t.Pid, Call(Threadmgr, "ksceKernelSetProcessIdToTLS", KernelSpace.KernelPid)); // left changed
            firstPid = t.Pid;
        }, "first");

        // another thread keeps its own word
        _harness.OnGuest(t => Assert.Equal(t.Pid, Call(Threadmgr, "ksceKernelGetProcessIdFromTLS")), "second");
        Assert.NotEqual(0, firstPid);
    }

    [Fact]
    public void AKernelThreadWorksForTheKernelAndErrorsComeAsTheFirmwareOrdersThem()
    {
        _harness.OnGuest(t =>
        {
            t.KernelMode = true;
            Assert.Equal(KernelSpace.KernelPid, Call(Threadmgr, "ksceKernelGetProcessIdFromTLS"));
            Assert.Equal(InvalidTlsProcessId, Call(Threadmgr, "ksceKernelSetProcessIdToTLS", -1));
            Assert.Equal(KernelSpace.KernelPid, Call(Threadmgr, "ksceKernelGetProcessIdFromTLS"));
            Assert.Equal(0, Call(Threadmgr, "NID_56C18B11"));
        });

        // no current thread: the negative value is checked first, then the context
        Assert.Equal(InvalidTlsProcessId, Call(Threadmgr, "ksceKernelSetProcessIdToTLS", -1));
        Assert.Equal(IllegalContext, Call(Threadmgr, "ksceKernelSetProcessIdToTLS", 1));
        Assert.Equal(IllegalContext, Call(Threadmgr, "ksceKernelGetProcessIdFromTLS"));
        Assert.Equal(IllegalContext, Call(Threadmgr, "NID_56C18B11"));
    }
}
