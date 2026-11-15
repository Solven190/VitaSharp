using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceSysmemForDriver's user-memory accessors as the FW 3.74 SceSysmem has them: user-to-user copies, string length
/// and copies with the firmware's zero fill and return values, the *Proc variants in a process's address space, and
/// the fault error for memory the user may not touch (kernel memory).
/// </summary>
[Collection("guest")]
public sealed class KernelUserCopyTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "kusercopy");

    public void Dispose() => _harness.Dispose();

    private SysmemKernel Sysmem => _harness.Module<SysmemKernel>();

    private int S(string function, params object?[] args) => Call(Sysmem, function, args);

    private uint KernelBuffer(uint size)
    {
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            uint at = _harness.Emu.Memory.Alloc(size, "usercopy test kernel", MemoryRegion.Kernel, 16);
            GuestMemory.Fill(at, 0xAA, size);
            return at;
        }
    }

    [Fact]
    public void UserToUserCopiesAndTheUserStringLength()
    {
        uint src = _harness.Str("abcdef");
        uint dst = _harness.Alloc(8);
        Assert.Equal(0, S("ksceKernelCopyFromToUser", dst, src, 7u));
        Assert.Equal("abcdef", GuestMemory.ReadCString(dst));
        Assert.Equal(6, S("ksceKernelStrnlenFromUser", src, 100u));
        Assert.Equal(3, S("ksceKernelStrnlenFromUser", src, 3u));
        uint kernel = KernelBuffer(16);
        Assert.Equal(KernelSpace.UserCopyFault, S("ksceKernelCopyFromToUser", dst, kernel, 4u));
        Assert.Equal(KernelSpace.UserCopyFault, S("ksceKernelCopyFromToUser", kernel, src, 4u));
        Assert.Equal(KernelSpace.UserCopyFault, S("ksceKernelStrnlenFromUser", kernel, 4u));
    }

    [Fact]
    public void StrncpyToUserZeroFillsAndAnswersTheLength()
    {
        uint kernel = KernelBuffer(16);
        GuestMemory.WriteCString(kernel, "hey");
        uint dst = _harness.Alloc(8);
        GuestMemory.Fill(dst, 0xCC, 8);
        Assert.Equal(3, S("ksceKernelStrncpyToUser", dst, kernel, 6u));
        Assert.Equal(new byte[] { 0x68, 0x65, 0x79, 0, 0, 0, 0xCC }, GuestMemory.Span(dst, 7).ToArray());
        Assert.Equal(2, S("ksceKernelStrncpyToUser", dst, kernel, 2u));
        Assert.Equal(KernelSpace.UserCopyFault, S("ksceKernelStrncpyToUser", KernelBuffer(8), kernel, 4u));
    }

    [Fact]
    public void TheProcVariantsWorkInTheProcessAddressSpace()
    {
        int pid = _harness.Emu.Pid;
        uint kernel = KernelBuffer(16);
        uint user = _harness.Str("proc!");
        Assert.Equal(5, S("ksceKernelStrncpyFromUserProc", pid, kernel, user, 8u));
        Assert.Equal("proc!", GuestMemory.ReadCString(kernel));
        Assert.Equal(0, GuestMemory.Read<byte>(kernel + 7));
        uint target = _harness.Alloc(8);
        Assert.Equal(0, S("ksceKernelCopyToUserProc", pid, target, kernel, 6u));
        Assert.Equal("proc!", GuestMemory.ReadCString(target));
        Assert.Equal(SceError.INVALID_PID, S("ksceKernelCopyToUserProc", 0x7FFF0001, target, kernel, 6u));

        uint words = _harness.Alloc(16);
        GuestMemory.W32(words, 0x11111111);
        GuestMemory.W32(words + 4, 0x11111111);
        GuestMemory.W32(words + 8, 0x22222222);
        Assert.Equal(8, S("ksceKernelCountFillValueFromUserProc", pid, words, 0x11111111u, 16u));
        Assert.Equal(0, S("ksceKernelCountFillValueFromUserProc", pid, words, 0x33333333u, 16u));
    }
}
