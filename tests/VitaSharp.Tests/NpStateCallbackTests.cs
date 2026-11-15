using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The NP service state callback as np_manager 3.74 delivers it: ONE registered callback (0x44239C35 replaces the
/// one before; 0xD9E6E56C clears it), run by sceNpCheckCallback (0x3B0AE9A9) on the calling thread under the manager's
/// semaphore, so a second thread that checks while the first is inside the callback waits for it (Minecraft's main
/// thread and its NP toolkit thread both poll it). The callback is a small ARM routine that records the state, counts
/// its calls and spins until the test releases it.
/// </summary>
[Collection("guest")]
public sealed class NpStateCallbackTests(GuestMemoryFixture fx) : IDisposable
{
    private readonly HleHarness _h = new(fx, "npstate");

    private NpManager Np => _h.Module<NpManager>();

    public void Dispose() => _h.Dispose();

    // callback(state r0, code r1, arg r2): str r0,[r2]; ldr r1,[r2,#8]; add r1,r1,#1; str r1,[r2,#8];
    // loop: ldr r3,[r2,#4]; cmp r3,#0; beq loop; bx lr
    private static readonly uint[] s_callback = [0xE5820000, 0xE5921008, 0xE2811001, 0xE5821008, 0xE5923004, 0xE3530000, 0x0AFFFFFC, 0xE12FFF1E];

    private uint Callback()
    {
        uint code = _h.Alloc(0x40);
        for (int i = 0; i < s_callback.Length; i++)
        {
            GuestMemory.W32(code + (uint)i * 4, s_callback[i]);
        }

        _h.Emu.Code.Invalidate(code, 0x40);
        return code;
    }

    /// <summary>The callback's argument: +0 the state it was told, +4 the release flag, +8 how many calls.</summary>
    private uint Record(bool released)
    {
        uint record = _h.Alloc(0x10);
        GuestMemory.Fill(record, 0, 0x10);
        GuestMemory.W32(record + 4, released ? 1u : 0u);
        return record;
    }

    /// <summary>While one thread runs the state callback from sceNpCheckCallback, another thread's sceNpCheckCallback
    /// waits for it and returns after it: the callback runs once, on the first thread. Before, the second call came
    /// back at once, before the callback had done its work.</summary>
    [Fact]
    public void ASecondCheckWaitsForTheCallbackRunningOnAnotherThread()
    {
        uint callback = Callback();
        uint record = Record(released: false);
        Assert.Equal(0, Call(Np, "sceNpInit", 0u, 0u));
        Assert.Equal(0, Call(Np, "sceNpRegisterServiceStateCallback", callback, record));

        GuestRun first = _h.Start(_ => Call(Np, "sceNpCheckCallback"), "np-first");
        Until(() => GuestMemory.R32(record + 8) == 1, "the callback on the first thread");
        bool secondReturned = false;
        GuestRun second = _h.Start(_ =>
        {
            Call(Np, "sceNpCheckCallback");
            secondReturned = true;
        }, "np-second");
        Until(() => second.Thread.Status == ThreadStatus.Waiting, "the second check waiting for the callback");
        Assert.False(secondReturned);

        GuestMemory.W32(record + 4, 1);
        _h.Join(first);
        _h.Join(second);
        Assert.True(secondReturned);
        Assert.Equal(1u, GuestMemory.R32(record + 8));
        Assert.Equal((uint)Np.ServiceState, GuestMemory.R32(record));
        Assert.Equal(0, Call(Np, "sceNpTerm"));
    }

    /// <summary>One callback slot: a second registration replaces the first, which is never called; the state is posted
    /// once after registering; unregistering clears the slot. Without sceNpInit both calls answer NOT_INITIALIZED, and a
    /// NULL callback INVALID_ARGUMENT. Before, every callback ever registered was called and nothing was checked.</summary>
    [Fact]
    public void ANewRegistrationReplacesTheCallback()
    {
        uint callback = Callback();
        uint replaced = Record(released: true);
        uint current = Record(released: true);
        Assert.Equal(NpManager.ERR_NOT_INITIALIZED, Call(Np, "sceNpRegisterServiceStateCallback", callback, replaced));
        Assert.Equal(NpManager.ERR_INVALID_ARGUMENT, Call(Np, "sceNpRegisterServiceStateCallback", 0u, replaced));
        _h.OnGuest(_ => Assert.Equal(NpManager.ERR_NOT_INITIALIZED, Call(Np, "sceNpCheckCallback")));

        Assert.Equal(0, Call(Np, "sceNpInit", 0u, 0u));
        Assert.Equal(0, Call(Np, "sceNpRegisterServiceStateCallback", callback, replaced));
        Assert.Equal(0, Call(Np, "sceNpRegisterServiceStateCallback", callback, current));
        _h.OnGuest(_ =>
        {
            Assert.Equal(0, Call(Np, "sceNpCheckCallback"));
            Assert.Equal(0, Call(Np, "sceNpCheckCallback"));
        });
        Assert.Equal(0u, GuestMemory.R32(replaced + 8));
        Assert.Equal(1u, GuestMemory.R32(current + 8));

        Assert.Equal(0, Call(Np, "sceNpUnregisterServiceStateCallback"));
        Assert.Equal(0, Call(Np, "sceNpRegisterServiceStateCallback", callback, replaced));
        _h.OnGuest(_ => Assert.Equal(0, Call(Np, "sceNpCheckCallback")));
        Assert.Equal(1u, GuestMemory.R32(replaced + 8));
        Assert.Equal(1u, GuestMemory.R32(current + 8));
        Assert.Equal(0, Call(Np, "sceNpTerm"));
    }
}
