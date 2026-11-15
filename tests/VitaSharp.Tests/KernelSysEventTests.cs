using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceKernelSuspendForDriver's system event handlers as the FW 3.74 SceSysmem has them: registered by name, called by
/// ksceKernelSysEventDispatch in registration order with (resume, eventid, args, the registration argument), stopped
/// at a failing handler only when the caller gave a place for its UID, removed by UID.
/// </summary>
[Collection("guest")]
public sealed class KernelSysEventTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "ksysevent");
    private readonly List<(string Handler, uint Resume, uint EventId, uint Args, uint Argument)> _calls = [];
    private uint _next;

    public void Dispose() => _harness.Dispose();

    private SysmemKernel Sysmem => _harness.Module<SysmemKernel>();

    /// <summary>A guest-callable handler that records its call and answers <paramref name="result"/>.</summary>
    private uint Handler(string name, int result)
    {
        if (_next == 0)
        {
            _next = _harness.Emu.Memory.Alloc(0x400, "sysevent test handlers");
        }

        uint at = _next;
        _next += 16;
        Linker.WriteHleStub(at, _harness.Emu.Hle.RegisterHostFunction("test:" + name, context =>
        {
            _calls.Add((name, context.Gpr(0), context.Gpr(1), context.Gpr(2), context.Gpr(3)));
            context.Return(result);
        }), 0);
        _harness.Emu.Code.Invalidate(at, 16);
        return at;
    }

    private int Register(string name, uint handler, uint argument) => Call(Sysmem, "ksceKernelRegisterSysEventHandler", name, handler, argument);

    [Fact]
    public void DispatchCallsEveryHandlerInRegistrationOrder()
    {
        _harness.OnGuest(_ =>
        {
            int first = Register("first", Handler("first", 0), 0x11);
            int second = Register("second", Handler("second", 5), 0x22);
            Assert.True(first > 0);
            Assert.True(second > 0);
            Assert.Equal(5, Call(Sysmem, "ksceKernelSysEventDispatch", 1, 0x10, 0xABCDu, 0u));
            Assert.Equal([("first", 1u, 0x10u, 0xABCDu, 0x11u), ("second", 1u, 0x10u, 0xABCDu, 0x22u)], _calls);
        });
    }

    [Fact]
    public void AFailingHandlerStopsTheDispatchOnlyWhenTheCallerAsksForItsUid()
    {
        _harness.OnGuest(_ =>
        {
            Register("ok", Handler("ok", 0), 0);
            int failing = Register("failing", Handler("failing", unchecked((int)0x80020001)), 0);
            Register("after", Handler("after", 7), 0);
            uint failedUid = _harness.Word(0);
            Assert.Equal(unchecked((int)0x80020001), Call(Sysmem, "ksceKernelSysEventDispatch", 0, 2, 0u, failedUid));
            Assert.Equal((uint)failing, GuestMemory.R32(failedUid));
            Assert.Equal(["ok", "failing"], _calls.Select(c => c.Handler));
            _calls.Clear();
            // without a place for the UID every handler runs and the last one's result comes back
            Assert.Equal(7, Call(Sysmem, "ksceKernelSysEventDispatch", 0, 2, 0u, 0u));
            Assert.Equal(["ok", "failing", "after"], _calls.Select(c => c.Handler));
        });
    }

    [Fact]
    public void UnregisterRemovesTheHandlerAndRefusesOtherObjects()
    {
        _harness.OnGuest(_ =>
        {
            Assert.Equal(0, Call(Sysmem, "ksceKernelSysEventDispatch", 0, 1, 0u, 0u));
            int handler = Register("gone", Handler("gone", 3), 0);
            Assert.Equal(0, Call(Sysmem, "ksceKernelUnregisterSysEventHandler", handler));
            Assert.Equal(0, Call(Sysmem, "ksceKernelSysEventDispatch", 0, 1, 0u, 0u));
            Assert.Empty(_calls);
            Assert.Equal(SceError.INVALID_UID, Call(Sysmem, "ksceKernelUnregisterSysEventHandler", handler));
            int block = Call(Sysmem, "ksceKernelAllocMemBlock", _harness.Str("block"), 0x0C20D060u, 0x1000u, 0u);
            Assert.True(block > 0);
            Assert.Equal(SceError.INVALID_UID_CLASS, Call(Sysmem, "ksceKernelUnregisterSysEventHandler", block));
        });
    }
}
