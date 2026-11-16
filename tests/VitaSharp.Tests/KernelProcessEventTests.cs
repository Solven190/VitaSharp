using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// The kernel's process event handlers (ksceKernelRegisterProcEventHandler): processmgr calls create(pid, param,
/// argument) once a process's main thread exists and before it starts, exit when it ends by itself and kill when it is
/// ended (FW 3.74 processmgr events 1, 2 and 3; sysmem's invoke walks the handlers in registration order). A create
/// handler that fails refuses the process, and the handlers called before it hear that it exits. VitaSharp delivered no
/// event: a kernel plugin watching processes (to patch each application as it starts, say) never ran. Synthetic: a
/// handler table of A32 recorders in kernel memory, the first process as the shell and synthetic titles spawned next
/// to it.
/// </summary>
[Collection("guest")]
public sealed class KernelProcessEventTests : IDisposable
{
    private const uint Argument = 0x0A11CE00;
    private readonly HleHarness _harness;
    private readonly Emulator _shell;
    private readonly List<Emulator> _apps = [];

    public KernelProcessEventTests(GuestMemoryFixture fixture)
    {
        // the title's segment is fixed at the start of user memory: blocks other tests of the shared memory left there
        for (uint address = SyntheticTitle.Base; address < SyntheticTitle.Base + 0x0010_0000; address += 0x1000)
        {
            if (fixture.Memory.FindBlock(address) is { } leftover)
            {
                fixture.Memory.Free(leftover.Address);
            }
        }

        _harness = new HleHarness(fixture, "kprocev");
        _shell = _harness.Emu;
        _shell.LoadTitle(SyntheticTitle.Install(_harness.Root, "VSTS00001"));
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

    /// <summary>What one handler entry saw: {pid, param, argument, the param block's size word}, in kernel memory.</summary>
    private sealed record Recorder(uint Record)
    {
        public uint Pid => GuestMemory.R32(Record);
        public uint Argument => GuestMemory.R32(Record + 8);
        public uint ParamSize => GuestMemory.R32(Record + 12);
    }

    private uint KernelBlock(uint size)
    {
        uint address;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            address = _shell.Memory.Alloc(size, "test:procevent", MemoryRegion.Kernel);
        }

        GuestMemory.Fill(address, 0, size);
        return address;
    }

    /// <summary>An A32 handler that stores r0 (pid), r1 (param), r2 (argument) and the param block's first word into a
    /// record and returns <paramref name="result"/>.</summary>
    private (uint Entry, Recorder Recorder) Handler(int result)
    {
        uint record = KernelBlock(0x10);
        uint code = KernelBlock(0x28);
        uint[] words =
        [
            0xE59F3018, // ldr r3, [pc, #24]  (the record)
            0xE5830000, // str r0, [r3]
            0xE5831004, // str r1, [r3, #4]
            0xE5832008, // str r2, [r3, #8]
            0xE591C000, // ldr r12, [r1]
            0xE583C00C, // str r12, [r3, #12]
            0xE59F0004, // ldr r0, [pc, #4]   (the result)
            0xE12FFF1E, // bx lr
            record,
            (uint)result,
        ];
        for (int i = 0; i < words.Length; i++)
        {
            GuestMemory.W32(code + (uint)i * 4, words[i]);
        }

        return (code, new Recorder(record));
    }

    private sealed record Handlers(Recorder Create, Recorder Exit, Recorder Kill);

    /// <summary>Registers a handler table {0x1C, create, exit, kill, 0, 0, 0} whose create returns
    /// <paramref name="createResult"/>.</summary>
    private Handlers Register(string name, int createResult = 0)
    {
        (uint create, Recorder createRecord) = Handler(createResult);
        (uint exit, Recorder exitRecord) = Handler(0);
        (uint kill, Recorder killRecord) = Handler(0);
        uint table = _harness.Alloc(0x1C);
        GuestMemory.Fill(table, 0, 0x1C);
        GuestMemory.W32(table, 0x1C);
        GuestMemory.W32(table + 4, create);
        GuestMemory.W32(table + 8, exit);
        GuestMemory.W32(table + 0xC, kill);
        ProcessmgrKernel processmgr = _harness.Module<ProcessmgrKernel>();
        int uid = 0;
        _harness.OnGuest(_ => uid = HleHarness.Call(processmgr, "ksceKernelRegisterProcEventHandler", _harness.Str(name), table, (int)Argument));
        Assert.True(uid > 0, $"register returned 0x{uid:X8}");
        return new Handlers(createRecord, exitRecord, killRecord);
    }

    private Emulator OpenApp(string titleId)
    {
        Emulator app = _shell.SpawnProcess(SyntheticTitle.Install(_harness.Root, titleId), "eboot.bin", []);
        _apps.Add(app);
        return app;
    }

    private static void WaitReleased(Emulator app)
    {
        using var released = new ManualResetEventSlim();
        app.Released += released.Set;
        Assert.True(released.Wait(30_000) || app.Vita.ByPid(app.Pid) == null, "the process was not released");
    }

    [Fact]
    public void AHandlerHearsAProcessCreatedThenExitingOrKilled()
    {
        Handlers handlers = Register("VsTestWatcher");

        Emulator app = OpenApp("VSTS00002");
        HleHarness.Until(() => handlers.Create.Pid == (uint)app.Pid, "the create event");
        Assert.Equal(ProcessEvents.CreateParamSize, handlers.Create.ParamSize);
        Assert.Equal(Argument, handlers.Create.Argument);
        Assert.Equal(0u, handlers.Exit.Pid);

        // it ends by itself: exit, not kill
        app.EndProcess(0);
        WaitReleased(app);
        Assert.Equal((uint)app.Pid, handlers.Exit.Pid);
        Assert.Equal(ProcessEvents.ParamSize, handlers.Exit.ParamSize);
        Assert.Equal(Argument, handlers.Exit.Argument);
        Assert.Equal(0u, handlers.Kill.Pid);

        // ended from outside (the shell closing it): kill
        Emulator killed = OpenApp("VSTS00003");
        HleHarness.Until(() => handlers.Create.Pid == (uint)killed.Pid, "the second create event");
        killed.Terminate("killed by the shell");
        WaitReleased(killed);
        Assert.Equal((uint)killed.Pid, handlers.Kill.Pid);
        Assert.Equal((uint)app.Pid, handlers.Exit.Pid);
    }

    /// <summary>A create handler that fails refuses the process: it never starts, the handler registered before it
    /// hears that it exits and the one after it is not called.</summary>
    [Fact]
    public void AFailingCreateHandlerRefusesTheProcess()
    {
        Handlers first = Register("VsTestFirst");
        Handlers refusing = Register("VsTestRefuser", createResult: unchecked((int)0x80010001));
        Handlers last = Register("VsTestLast");

        Emulator app = OpenApp("VSTS00002");
        WaitReleased(app);
        Assert.Equal((uint)app.Pid, first.Create.Pid);
        Assert.Equal((uint)app.Pid, refusing.Create.Pid);
        Assert.Equal(0u, last.Create.Pid);
        Assert.Equal((uint)app.Pid, first.Exit.Pid);
        Assert.Equal(0u, refusing.Exit.Pid);
        Assert.Equal(0u, first.Kill.Pid);
        Assert.Equal(0u, refusing.Kill.Pid);
        Assert.Null(app.Kernel.MainThread is { } main ? _shell.Vita.Objects.Get<GuestThread>(main.Uid) : null);
    }
}
