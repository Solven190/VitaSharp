using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// A kernel module built for FW 3.74 imports the firmware's kernel libraries by their 3.74 NIDs: the renumbered
/// <c>*ForKernel</c> functions (SceModulemgrForKernel, SceSysmemForKernel moved to new NIDs in 3.63) and the
/// <c>*ForDriver</c> twins of the services the HLE has for user code. Built with <see cref="SceModuleBuilder"/> and
/// called through the module's own import stubs, so the loader's NID binding is what is tested.
/// </summary>
[Collection("guest")]
public sealed class KernelFw374ExportsTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint CpuForDriver = 0x40ECDB0E, AtomicAddAndGet32 = 0x5F6A8743;
    private const uint ModulemgrForKernel = 0x92C9FFC2, GetModuleInfo374 = 0xDAA90093;
    private const uint ThreadmgrForDriver = 0xE2C40624, GetThreadId = 0x59D06540;

    private readonly HleHarness _harness = new(fixture, "kern374");

    public void Dispose() => _harness.Dispose();

    /// <summary>FW 3.74 kernel NIDs (renumbered *ForKernel functions and *ForDriver twins): each has an HLE.</summary>
    private static readonly uint[] s_fw374Nids =
    [
        // SceModulemgrForKernel 3.63+: GetModuleInfo, GetModuleList, GetModuleCB, StopModuleForPid, UnloadModuleForPid
        0xDAA90093, 0xB72C75A4, 0x37512E29, 0xBDBD391D, 0xFCA9FDB1,
        // SceSysmemForKernel 3.63+: ksceGUIDKernelCreateWithOpt
        0xFB6390CE,
        // SceThreadmgrForDriver: event flags, semaphores, thread id
        0x4336BAA4, 0xD4780C3E, 0x0C1D3F20, 0x71ECB352, 0x4F1DA3BE, 0x30E93C31, 0xD270498B, 0x3C8B55A9, 0x16A35E58,
        0x59D06540, 0xAC834F3F,
        // SceCpuForDriver atomics, SceIofilemgrForDriver write/remove, SceRtcForDriver current tick
        0x5F6A8743, 0xCDA96E81, 0x21EE91F0, 0x0D7BB3E1,
    ];

    [Fact]
    public void Fw374KernelNidsAreImplemented()
    {
        var missing = s_fw374Nids.Where(n => !_harness.Emu.Hle.IsImplemented(n)).Select(n => $"0x{n:X8}").ToList();
        Assert.True(missing.Count == 0, string.Join(", ", missing));
    }

    [Fact]
    public void KernelModuleCallsFw374ImportsThroughItsStubs()
    {
        SceModuleBuilder builder = new SceModuleBuilder("Fw374Client")
            .Import("SceCpuForDriver", CpuForDriver, AtomicAddAndGet32)
            .Import("SceModulemgrForKernel", ModulemgrForKernel, GetModuleInfo374)
            .Import("SceThreadmgrForDriver", ThreadmgrForDriver, GetThreadId);
        int callAtomic = builder.CallImport(0, 0);
        int callModuleInfo = builder.CallImport(1, 0);
        int callThreadId = builder.CallImport(2, 0);
        byte[] elf = builder.Export("Fw374ClientLib", 0x0BADF374, 0x0001, (0x37400001, callAtomic)).Build();
        string dir = Path.Combine(_harness.Root, "ur0", "tai");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "fw374.skprx"), elf);
        LoadedModule module = _harness.Emu.LoadModule("ur0:tai/fw374.skprx", kernel: true);
        uint codeBase = module.Segments[0].Address;

        uint counter = _harness.Word(40);
        uint info = _harness.Alloc(0x1B8);
        _harness.OnGuest(thread =>
        {
            // ksceKernelAtomicAddAndGet32 (the user atomic's kernel twin): the new value
            Assert.Equal(42u, thread.CallGuest(SceModuleBuilder.FunctionAddress(codeBase, callAtomic), counter, 2));
            Assert.Equal(42u, GuestMemory.R32(counter));
            // ksceKernelGetModuleInfo by its 3.74 NID: the kernel module's own info
            Assert.Equal(0u, thread.CallGuest(SceModuleBuilder.FunctionAddress(codeBase, callModuleInfo), (uint)KernelSpace.KernelPid,
                (uint)module.Uid, info));
            Assert.Equal("Fw374Client", GuestMemory.ReadCString(info + 0x0C));
            // ksceKernelGetThreadId: the calling thread
            Assert.Equal((uint)thread.Uid, thread.CallGuest(SceModuleBuilder.FunctionAddress(codeBase, callThreadId)));
        });
    }
}
