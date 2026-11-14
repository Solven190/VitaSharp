using VitaSharp.Core.Hle;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;

namespace VitaSharp.Tests;

/// <summary>
/// The emulated CPU cost of an HLE call comes from what the function is on the Vita, declared on its [Hle] attribute
/// (<see cref="HleAttribute.Cost"/>) or its module (<see cref="HleModule.DefaultCost"/>), never from its name:
/// user-mode code (TLS, atomics, lwmutex, Clib, libgxm, libngs, librtc, libfiber), a kernel entry (a syscall that does
/// not go through the scheduler) or a wait in the scheduler. The class sets the guest cycles a call charges to the
/// calling thread, which guest clocks and the CPU pacing see, so each declaration is a guest-time contract.
/// </summary>
[Collection("guest")]
public sealed class HleCostTests : IDisposable
{
    private readonly HleHarness _harness;

    public HleCostTests(GuestMemoryFixture fixture) => _harness = new HleHarness(fixture, "hlecost");

    public void Dispose() => _harness.Dispose();

    /// <summary>The registry's function for an implemented export.</summary>
    private HleFunction Function(string name)
    {
        HleRegistry hle = _harness.Emu.Hle;
        NidInfo? info = NidDatabase.LookupName(name).FirstOrDefault(n => hle.Implements(n.Nid));
        if (info == null)
        {
            // an export registered by its NID only (NID_9967EBCB, sceNpCommerce2_984036CB): the NID ends the name
            uint nid = uint.Parse(name[^8..], System.Globalization.NumberStyles.HexNumber);
            Assert.True(hle.Implements(nid), name);
            return hle.GetFunction(hle.GetHleFunctionIndex(nid, 0, NidDatabase.Lookup(nid)?.Library ?? ""));
        }

        return hle.GetFunction(hle.GetHleFunctionIndex(info.Nid, 0, info.Library));
    }

    [Theory]
    // user-mode code with no kernel entry
    [InlineData("sceKernelGetTLSAddr", HleCost.UserInline)]
    [InlineData("sceKernelGetThreadId", HleCost.UserInline)]
    [InlineData("sceKernelAtomicAddAndGet32", HleCost.UserInline)]
    [InlineData("sceKernelLockLwMutex", HleCost.UserCall)]
    [InlineData("sceKernelUnlockLwMutex", HleCost.UserCall)]
    [InlineData("sceClibMemcpy", HleCost.UserCall)]
    [InlineData("sceGxmSetFragmentTexture", HleCost.UserCall)]
    [InlineData("sceGxmTextureGetType", HleCost.UserCall)]
    [InlineData("sceGxmDraw", HleCost.UserLibrary)]
    [InlineData("sceNgsSystemUpdate", HleCost.UserLibrary)]
    [InlineData("sceRtcGetCurrentTick", HleCost.UserLibrary)]
    [InlineData("sceFiberSwitch", HleCost.UserLibrary)]
    // syscalls that do not go through the scheduler
    [InlineData("_sceRtcGetCurrentTick", HleCost.KernelEntry)]
    [InlineData("sceKernelLockMutex", HleCost.KernelEntry)]
    [InlineData("sceKernelUnlockMutex", HleCost.KernelEntry)]
    [InlineData("sceKernelCreateSema", HleCost.KernelEntry)]
    [InlineData("_sceIoOpen", HleCost.KernelEntry)]
    // the display's getters and setters are a kernel entry; a vcount polling loop (Wipeout 2048's flip callback)
    // paid a wait's 12,000 cycles for every poll when they were charged like the waits
    [InlineData("sceDisplayGetVcount", HleCost.KernelEntry)]
    [InlineData("sceDisplayGetFrameBuf", HleCost.KernelEntry)]
    [InlineData("sceDisplaySetFrameBuf", HleCost.KernelEntry)]
    [InlineData("sceDisplayGetRefreshRate", HleCost.KernelEntry)]
    // waits in the scheduler
    [InlineData("sceDisplayWaitVblankStart", HleCost.SchedulerWait)]
    [InlineData("sceDisplayWaitVblankStartMultiCB", HleCost.SchedulerWait)]
    [InlineData("sceDisplayWaitSetFrameBuf", HleCost.SchedulerWait)]
    [InlineData("sceKernelWaitSema", HleCost.SchedulerWait)]
    [InlineData("sceKernelWaitSemaCB", HleCost.SchedulerWait)]
    [InlineData("sceKernelSignalSema", HleCost.SchedulerWait)]
    [InlineData("sceKernelWaitEventFlag", HleCost.SchedulerWait)]
    [InlineData("sceIoRead", HleCost.SchedulerWait)]
    [InlineData("sceCtrlReadBufferPositive", HleCost.SchedulerWait)]
    public void DeclaredClass(string name, HleCost expected)
    {
        HleFunction function = Function(name);
        Assert.Equal(expected, function.CostClass);
        Assert.Equal(HleCosts.Cycles(expected), function.Cost);
    }

    /// <summary>A delay is a scheduler wait like the others: threadmgr (FW 3.74) computes the deadline, queues the
    /// thread on its timer list and switches it out, the same path as a wait with a timeout. Charging it a kernel entry
    /// alone (perf/desktop-24) would make it the only blocking syscall below the class, without a hardware
    /// reference.</summary>
    [Theory]
    [InlineData("sceKernelDelayThread")]
    [InlineData("sceKernelDelayThreadCB")]
    [InlineData("sceKernelDelayThread200")]
    [InlineData("sceKernelDelayThreadCB200")]
    public void DelaysAreSchedulerWaits(string name)
    {
        Assert.Equal(Function("sceKernelWaitSema").CostClass, Function(name).CostClass);
    }

    /// <summary>The callback-processing waits are declared (<see cref="HleAttribute.CallbackWait"/>), including the
    /// firmware 0.89x and 200 us minimum variants whose names do not end in "CB"; an export whose NID only ends in the hex
    /// digits C, B is no wait (the name rule made SceWebKit's NID_9967EBCB and sceNpCommerce2_984036CB callback
    /// waits).</summary>
    [Theory]
    [InlineData("sceKernelWaitSemaCB", true)]
    [InlineData("sceKernelDelayThreadCB", true)]
    [InlineData("sceDisplayWaitVblankStartCB", true)]
    [InlineData("sceKernelDelayThreadCB200", true)]
    [InlineData("sceKernelWaitThreadEndCB_089", true)]
    [InlineData("sceKernelWaitSema", false)]
    [InlineData("sceKernelDelayThread", false)]
    [InlineData("sceKernelDelayThread200", false)]
    [InlineData("sceDisplayWaitVblankStart", false)]
    [InlineData("NID_9967EBCB", false)]
    [InlineData("sceNpCommerce2_984036CB", false)]
    public void CallbackWaitsAreDeclared(string name, bool callbackWait)
    {
        Assert.Equal(callbackWait, Function(name).IsCallbackWait);
    }

    /// <summary>Every callback variant of a wait is declared one: in the firmware's NID database a "CB" export is the
    /// callback-processing twin of the wait without the suffix (sceKernelWaitSema / sceKernelWaitSemaCB), so the twin
    /// is the reference here; a wait that forgot the declaration would never run its callbacks. Exports whose NID merely
    /// ends in the hex digits C, B (NID_9967EBCB, sceNpCommerce2_984036CB) have no twin and are no wait.</summary>
    [Fact]
    public void EveryCallbackTwinOfAWaitIsDeclared()
    {
        HleRegistry hle = _harness.Emu.Hle;
        var missing = new List<string>();
        var stray = new List<string>();
        int twins = 0;
        foreach (NidInfo info in NidDatabase.All)
        {
            if (!hle.Implements(info.Nid))
            {
                continue;
            }

            HleFunction function = hle.GetFunction(hle.GetHleFunctionIndex(info.Nid, 0, info.Library));
            bool hasWaitTwin = function.Name.EndsWith("CB", StringComparison.Ordinal)
                && NidDatabase.LookupName(function.Name[..^2]).Any(n => hle.Implements(n.Nid));
            if (hasWaitTwin)
            {
                twins++;
            }

            if (hasWaitTwin && !function.IsCallbackWait)
            {
                missing.Add(function.Name);
            }
            else if (function.IsCallbackWait && !hasWaitTwin && !function.Name.Contains("CB", StringComparison.Ordinal))
            {
                stray.Add(function.Name);
            }
        }
        Assert.Empty(missing);
        Assert.Empty(stray);
        Assert.True(twins > 30, $"only {twins} callback twins");
    }

    /// <summary>The name decides nothing: a function registered without a declaration is a kernel entry and no
    /// callback wait, whatever it is called.</summary>
    [Fact]
    public void UndeclaredFunctionsAreAKernelEntryWhateverTheirName()
    {
        HleRegistry hle = _harness.Emu.Hle;
        HleFunction host = hle.GetFunction(hle.RegisterHostFunction("sceKernelWaitSemaCB", _ => { }));
        Assert.Equal(HleCost.KernelEntry, host.CostClass);
        Assert.Equal(2_000, host.Cost);
        Assert.False(host.IsCallbackWait);

        NidInfo unimplemented = NidDatabase.All.First(n => !hle.Implements(n.Nid));
        HleFunction function = hle.GetFunction(hle.GetHleFunctionIndex(unimplemented.Nid, 0, unimplemented.Library));
        Assert.Equal(HleCost.KernelEntry, function.CostClass);
    }

    [Fact]
    public void ClassesChargeInOrder()
    {
        Assert.Equal(20, HleCosts.Cycles(HleCost.UserInline));
        Assert.Equal(50, HleCosts.Cycles(HleCost.UserCall));
        Assert.Equal(200, HleCosts.Cycles(HleCost.UserLibrary));
        Assert.Equal(2_000, HleCosts.Cycles(HleCost.KernelEntry));
        Assert.Equal(12_000, HleCosts.Cycles(HleCost.SchedulerWait));
        Assert.Equal(HleCosts.Cycles(HleCost.KernelEntry), HleCosts.Cycles(HleCost.Module));
    }

    /// <summary>Guest time: a call through its import stub charges the calling thread exactly its class's cycles
    /// (nothing else is charged for these, so a change of class or of the dispatcher's charge shows here).</summary>
    [Theory]
    [InlineData("sceKernelGetThreadId", 20)]
    [InlineData("sceKernelGetProcessTimeWide", 200)]
    [InlineData("sceKernelGetSystemTimeWide", 200)]
    [InlineData("sceDisplayGetVcount", 2_000)]
    [InlineData("sceKernelDelayThread", 12_000)]
    public void ACallChargesItsClassToTheCallingThread(string name, long cycles)
    {
        HleRegistry hle = _harness.Emu.Hle;
        NidInfo info = NidDatabase.LookupName(name).First(n => hle.Implements(n.Nid));
        uint stub = _harness.Alloc(16);
        Linker.WriteHleStub(stub, hle.GetHleFunctionIndex(info.Nid, 0, info.Library), info.Nid);
        long charged = -1;
        _harness.OnGuest(t =>
        {
            long before = t.Core.ChargedCycles;
            t.CallGuest(stub, 0);
            t.CallGuest(stub, 0);
            charged = t.Core.ChargedCycles - before;
        });
        Assert.Equal(2 * cycles, charged);
    }

    /// <summary>A callback wait runs the thread's notified callbacks; any other call leaves them pending (the firmware
    /// runs callbacks only in the CB waits and sceKernelCheckCallback). The name rule ran them in five exports whose NID
    /// only ends in the hex digits C, B (SceWebKit, ScePaf and SceNpCommerce2 functions that wait for nothing).</summary>
    [Theory]
    [InlineData("sceKernelDelayThreadCB200", true)]
    [InlineData("sceKernelDelayThread200", false)]
    [InlineData("NID_9967EBCB", false)]
    [InlineData("NID_EFFCB2CB", false)]
    [InlineData("NID_BE315CCB", false)]
    [InlineData("NID_00714ACB", false)]
    [InlineData("sceNpCommerce2_984036CB", false)]
    public void OnlyCallbackWaitsRunNotifiedCallbacks(string name, bool runs)
    {
        HleRegistry hle = _harness.Emu.Hle;
        HleFunction function = Function(name);
        uint stub = _harness.Alloc(16);
        Linker.WriteHleStub(stub, hle.GetHleFunctionIndex(function.Nid, 0, NidDatabase.Lookup(function.Nid)?.Library ?? ""), function.Nid);
        int callbackRuns = 0;
        uint callback = _harness.Alloc(16);
        Linker.WriteHleStub(callback, hle.RegisterHostFunction("test callback", c =>
        {
            callbackRuns++;
            c.Return(0);
        }), 0);
        Core.Hle.Modules.ThreadMgr threadMgr = _harness.Module<Core.Hle.Modules.ThreadMgr>();
        _harness.OnGuest(t =>
        {
            int uid = HleHarness.Call(threadMgr, "sceKernelCreateCallback", "cb", 0u, callback, 0u);
            Assert.True(uid > 0);
            Assert.Equal(0, HleHarness.Call(threadMgr, "sceKernelNotifyCallback", uid, 1));
            Assert.Equal(1, t.PendingCallbacks);
            t.CallGuest(stub, 0, 0, 0, 0);
        });
        Assert.Equal(runs ? 1 : 0, callbackRuns);
    }
}
