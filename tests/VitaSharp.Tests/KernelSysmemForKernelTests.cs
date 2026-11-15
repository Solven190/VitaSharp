using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceSysmemForKernel entries as the FW 3.74 sysmem has them: the object and class lookups that answer 0 instead of an
/// error, the kernel's general heap, and the copies into the user domain (the text one makes the new code run).
/// </summary>
[Collection("guest")]
public sealed class KernelSysmemForKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "ksysmemk");

    public void Dispose() => _harness.Dispose();

    private SysmemKernel Sysmem => _harness.Module<SysmemKernel>();

    [Fact]
    public void TheObjectAndClassLookupsAnswerZeroForNoObject()
    {
        int heap = Call(Sysmem, "ksceKernelCreateHeap", _harness.Str("lookup"), 0x1000u, 0u);
        Assert.True(heap > 0);
        uint body = _harness.Word(0);
        Assert.Equal(0, Call(Sysmem, "ksceGUIDGetObject", heap, body));
        Assert.Equal(GuestMemory.R32(body), (uint)Call(Sysmem, "NID_FA6C098C", heap));
        uint cls = _harness.Word(0);
        Assert.Equal(0, Call(Sysmem, "ksceGUIDGetClass", heap, cls));
        Assert.Equal(GuestMemory.R32(cls), (uint)Call(Sysmem, "NID_368F1C36", heap));
        foreach (int guid in new[] { 0, 2, -1, 0x7FFF0001 })
        {
            Assert.Equal(0, Call(Sysmem, "NID_FA6C098C", guid));
            Assert.Equal(0, Call(Sysmem, "NID_368F1C36", guid));
        }
    }

    [Fact]
    public void TheKernelHeapHandsOutUsableBlocks()
    {
        uint first = (uint)Call(Sysmem, "ksceKernelAlloc", 0x30u);
        uint second = (uint)Call(Sysmem, "ksceKernelAlloc", 0x30u);
        Assert.NotEqual(0u, first);
        Assert.NotEqual(first, second);
        Assert.Equal(0u, first & 7);
        Assert.True(KernelSpace.IsKernelOwned(first));
        GuestMemory.W32(first + 0x2C, 0x12345678);
        Assert.Equal(0x12345678u, GuestMemory.R32(first + 0x2C));
        Assert.Equal(0, Call(Sysmem, "ksceKernelAlloc", 0u));
        Assert.Equal(0, Call(Sysmem, "ksceKernelFree", first));
        Assert.Equal(0, Call(Sysmem, "ksceKernelFree", second));
        Assert.Equal(0, Call(Sysmem, "ksceKernelFree", 0u));
    }

    [Fact]
    public void TheTextCopyMakesTheNewCodeRun()
    {
        // a Thumb function returning 1, run, then replaced by one returning 2 through the text copy
        uint code = _harness.Alloc(0x40);
        GuestMemory.Write(code, (ushort)0x2001);
        GuestMemory.Write(code + 2, (ushort)0x4770);
        uint replacement = _harness.Alloc(0x10);
        GuestMemory.Write(replacement, (ushort)0x2002);
        GuestMemory.Write(replacement + 2, (ushort)0x4770);
        uint data = _harness.Alloc(0x10);
        GuestMemory.W32(data, 0xA1B2C3D4);
        uint target = _harness.Alloc(0x10);
        _harness.OnGuest(t =>
        {
            Assert.Equal(1u, t.CallGuest(code | 1));
            Assert.Equal(0, Call(Sysmem, "ksceKernelCopyToUserTextDomain", code, replacement, 4u));
            Assert.Equal(2u, t.CallGuest(code | 1));
            Assert.Equal(0, Call(Sysmem, "ksceKernelCopyToUserDomain", target, data, 4u));
            Assert.Equal(0xA1B2C3D4u, GuestMemory.R32(target));
        });
    }
}
