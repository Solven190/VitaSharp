using VitaSharp.Core;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// The Vita has one kernel: the plugin configuration's <c>*KERNEL</c> modules load into it once, at boot, and every
/// process sees them, the shell and an application the shell opens next to it alike (on the Vita, processmgr creates
/// each process in the same kernel, whose modules stay until the system ends). VitaSharp kept a kernel module in the
/// emulator of the process it was loaded for: an application opened by the shell did not see the shell's kernel
/// plugins (its imports of their syscall exports became HLE stubs that did nothing) and a kernel module loaded from an
/// application went away with it. The kernel process (<see cref="VitaSystem.KernelProcess"/>) holds them now.
/// Synthetic: two synthetic titles (the first process as the shell, the second spawned next to it) and synthetic
/// kernel and user modules (<see cref="SceModuleBuilder"/>).
/// </summary>
[Collection("guest")]
public sealed class KernelSharedProcessTests : IDisposable
{
    private const uint BridgeLib = 0x5EA7B71E, BridgeFn = 0x0BADF00E;
    private readonly HleHarness _harness;
    private readonly Emulator _shell;
    private readonly List<Emulator> _apps = [];

    public KernelSharedProcessTests(GuestMemoryFixture fixture)
    {
        // the title's segment is fixed at the start of user memory: blocks other tests of the shared memory left there
        for (uint address = SyntheticTitle.Base; address < SyntheticTitle.Base + 0x0010_0000; address += 0x1000)
        {
            if (fixture.Memory.FindBlock(address) is { } leftover)
            {
                fixture.Memory.Free(leftover.Address);
            }
        }

        _harness = new HleHarness(fixture, "kshared", kernelPlugins: true);
        _shell = _harness.Emu;
        File.WriteAllBytes(Path.Combine(Tai, "bridge.skprx"), Bridge());
    }

    public void Dispose()
    {
        foreach (Emulator app in _apps)
        {
            app.Stop();
        }

        _shell.Stop();
        _harness.Dispose();
    }

    private string Tai
    {
        get
        {
            string tai = Path.Combine(_harness.Root, "ur0", "tai");
            Directory.CreateDirectory(tai);
            return tai;
        }
    }

    /// <summary>A kernel module exporting one syscall library (its function returns 42), started with success.</summary>
    private static byte[] Bridge()
    {
        SceModuleBuilder builder = new SceModuleBuilder("SharedBridge").Start(0);
        int function = builder.ReturnConstant(42);
        return builder.Export("SharedBridge", BridgeLib, Linker.SyscallExportAttr | 1, (BridgeFn, function)).Build();
    }

    /// <summary>A user module importing the bridge's function.</summary>
    private string WriteClient(string file)
    {
        SceModuleBuilder builder = new SceModuleBuilder("BridgeClient").Import("SharedBridge", BridgeLib, BridgeFn);
        builder.CallImport(0, 0);
        File.WriteAllBytes(Path.Combine(Tai, file), builder.Build());
        return "ur0:tai/" + file;
    }

    /// <summary>The shell's process with the plugin configuration listing the bridge, then an application next to it.</summary>
    private Emulator BootShellAndOpenApp()
    {
        File.WriteAllText(Path.Combine(Tai, "config.txt"), "*KERNEL\nur0:tai/bridge.skprx\n");
        _shell.LoadTitle(SyntheticTitle.Install(_harness.Root, "VSTS00001"));
        return OpenApp();
    }

    private Emulator OpenApp()
    {
        Emulator app = _shell.SpawnProcess(SyntheticTitle.Install(_harness.Root, "VSTS00002"), "eboot.bin", []);
        _apps.Add(app);
        return app;
    }

    private List<LoadedModule> Bridges => HleHarness.KernelModules(_shell).Values.Where(m => m.Name == "SharedBridge").ToList();

    /// <summary>The target of import stub <paramref name="stub"/> when it jumps to an export (movw/movt r12; bx r12),
    /// else null (an HLE stub).</summary>
    private static uint? JumpTarget(Emulator process, uint stub)
    {
        using (AddressSpace.Use(process.Space))
        {
            uint w0 = GuestMemory.R32(stub), w1 = GuestMemory.R32(stub + 4);
            if ((w0 & 0xFFF0F000) != 0xE300C000 || (w1 & 0xFFF0F000) != 0xE340C000)
            {
                return null;
            }

            return ((w0 >> 4) & 0xF000) | (w0 & 0xFFF) | ((((w1 >> 4) & 0xF000) | (w1 & 0xFFF)) << 16);
        }
    }

    private uint BridgeExport()
    {
        Assert.True(HleHarness.KernelLinker(_shell).TryGetVisibleExport(BridgeFn, BridgeLib, kernelImporter: false, out uint address));
        return address;
    }

    private uint ClientStub(Emulator process) => Assert.Single(process.Linker.BoundStubs(BridgeFn, BridgeLib, kernel: false));

    [Fact]
    public void TheShellAndAnAppItOpensSeeTheOneKernelModuleLoadedOnce()
    {
        Emulator app = BootShellAndOpenApp();

        // loaded once, into the kernel: neither process holds a kernel module of its own
        LoadedModule bridge = Assert.Single(Bridges);
        Assert.True(bridge.Started);
        Assert.DoesNotContain(_shell.Kernel.Modules.Values, m => m.IsKernel);
        Assert.DoesNotContain(app.Kernel.Modules.Values, m => m.IsKernel);
        Assert.Single(_shell.Vita.Objects.All<ModuleObject>(), m => m.Module.Name == "SharedBridge");

        // both processes name the same kernel process and find the module in its list
        Emulator? kernel = KernelSpace.ProcessOf(app, KernelSpace.KernelPid);
        Assert.NotNull(kernel);
        Assert.True(kernel.IsKernelProcess);
        Assert.Same(kernel, KernelSpace.ProcessOf(_shell, KernelSpace.KernelPid));
        Assert.Same(kernel, _shell.Vita.ByPid(KernelSpace.KernelPid));
        Assert.DoesNotContain(kernel, _shell.Vita.Processes);
        Assert.Contains(bridge, KernelSpace.ModulesOf(app, KernelSpace.KernelPid)!);

        // the application's imports of the module's syscall export bind to its code, as the shell's do
        string client = WriteClient("client.suprx");
        app.LoadModule(client);
        _shell.LoadModule(client);
        Assert.Equal(BridgeExport(), JumpTarget(app, ClientStub(app)));
        Assert.Equal(BridgeExport(), JumpTarget(_shell, ClientStub(_shell)));

        // the plugin loader of the second process does not load (or start) them again
        Assert.Empty(app.LoadKernelPlugins());
        Assert.Single(Bridges);
    }

    /// <summary>A kernel module loaded after a process bound its imports (ksceKernelLoadModule) reaches them, in every
    /// process; unloaded, the imports go back to the HLE.</summary>
    [Fact]
    public void AKernelModuleLoadedLaterRebindsEveryProcesssImports()
    {
        _shell.LoadTitle(SyntheticTitle.Install(_harness.Root, "VSTS00001"));
        Emulator app = OpenApp();
        string client = WriteClient("client.suprx");
        app.LoadModule(client);
        Assert.Null(JumpTarget(app, ClientStub(app)));

        LoadedModule bridge = _shell.LoadModule("ur0:tai/bridge.skprx", kernel: true);
        Assert.Equal(BridgeExport(), JumpTarget(app, ClientStub(app)));

        app.UnloadModule(bridge);
        Assert.Empty(Bridges);
        Assert.Null(JumpTarget(app, ClientStub(app)));
    }

    /// <summary>A kernel module an application loaded stays when the application ends: it is the kernel's.</summary>
    [Fact]
    public void AKernelModuleOutlivesTheProcessThatLoadedIt()
    {
        _shell.LoadTitle(SyntheticTitle.Install(_harness.Root, "VSTS00001"));
        Emulator app = OpenApp();
        app.LoadModule("ur0:tai/bridge.skprx", kernel: true);
        string client = WriteClient("client.suprx");
        _shell.LoadModule(client);
        uint export = BridgeExport();

        using var released = new ManualResetEventSlim();
        app.Released += released.Set;
        app.Terminate("exited (0x0)");
        Assert.True(released.Wait(30_000), "the application was not released");

        Assert.Single(Bridges);
        Assert.Equal(export, JumpTarget(_shell, ClientStub(_shell)));
        using (AddressSpace.Use(_shell.Space))
        {
            Assert.True(KernelSpace.IsKernelOwned(export & ~1u));
        }
    }
}
