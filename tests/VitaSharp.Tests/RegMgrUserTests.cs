using VitaSharp.Core.Firmware;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader.Self;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>The SceRegMgr syscalls of SceRegistryMgr (FW 3.74) that only system programs may call.</summary>
[Collection("guest")]
public sealed class RegMgrUserTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int NotSystemProgram = unchecked((int)0x80010058);
    private const ulong GameAuthorityId = 0x210000101CC404D8UL;

    private readonly HleHarness _harness = new(fixture, "regmgr-user");

    public void Dispose() => _harness.Dispose();

    private void RunAsSystemProgram()
    {
        byte[] capability = new byte[ProgramAccess.CapabilitySize];
        capability[0] = 0x40;
        _harness.Emu.Access = new ProgramAccess(0x2800000000000025UL, capability, false);
        Assert.True(_harness.Emu.Access.IsSystemProgram);
    }

    [Fact]
    public void GetRegVersionAnswersSystemProgramsWithTheRegistryVersion()
    {
        RegMgrSystem regmgr = _harness.Module<RegMgrSystem>();

        // a game is refused before the registry is read
        _harness.Emu.Access = new ProgramAccess(GameAuthorityId, new byte[ProgramAccess.CapabilitySize], false);
        Assert.Equal(NotSystemProgram, Call(regmgr, "sceRegMgrGetRegVersion"));

        // no firmware template: the FW 3.74 template's version
        RunAsSystemProgram();
        Assert.Equal(RegMgrSystem.Fw374RegistryVersion, Call(regmgr, "sceRegMgrGetRegVersion"));

        // the current value of /REGISTRY/category_version, not its default
        var registry = Registry.For(_harness.Root);
        registry.Define("/REGISTRY/category_version", Registry.KeyType.Int, 4, () => RegMgrSystem.Fw374RegistryVersion);
        Assert.True(registry.SetInt("/REGISTRY/", "category_version", 0x00360000));
        Assert.Equal(0x00360000, Call(regmgr, "sceRegMgrGetRegVersion"));
    }

    [Fact]
    public void RegionProductCodeAnswersEverySystemProgram()
    {
        RegMgrSystem regmgr = _harness.Module<RegMgrSystem>();
        _harness.Emu.Access = new ProgramAccess(GameAuthorityId, new byte[ProgramAccess.CapabilitySize], false);
        Assert.Equal(NotSystemProgram, Call(regmgr, "sceRegMgrSrvCnvRegionPsCode", 1));
        // a system application, not the shell (Settings converts regions too)
        RunAsSystemProgram();
        Assert.False(_harness.Emu.IsShell);
        Assert.Equal(0x103, Call(regmgr, "sceRegMgrSrvCnvRegionPsCode", 1));
    }

    private int[] CreateCallbacks(int count)
    {
        ThreadMgr threadmgr = _harness.Module<ThreadMgr>();
        int[] uids = new int[count];
        _harness.OnGuest(_ =>
        {
            for (int i = 0; i < count; i++)
            {
                uids[i] = Call(threadmgr, "sceKernelCreateCallback", "regmgr-cb", 0u, 0x81000001u, 0u);
            }
        });
        Assert.All(uids, uid => Assert.True(uid > 0, $"0x{uid:X8}"));
        return uids;
    }

    private Callback CallbackObject(int uid) => _harness.Emu.Kernel.Objects.Get<Callback>(uid)!;

    [Fact]
    public void CallbackSlotsFollowTheKernelTable()
    {
        RegMgrSystem regmgr = _harness.Module<RegMgrSystem>();
        int[] callbacks = CreateCallbacks(RegMgrSystem.SlotCount + 1);

        _harness.Emu.Access = new ProgramAccess(GameAuthorityId, new byte[ProgramAccess.CapabilitySize], false);
        Assert.Equal(NotSystemProgram, Call(regmgr, "sceRegMgrRegisterCallback", callbacks[0]));

        RunAsSystemProgram();
        Assert.Equal(SceError.INVALID_UID, Call(regmgr, "sceRegMgrRegisterCallback", 0x7FFF0001));
        // the slot is the result; one callback holds one slot; 16 slots
        for (int i = 0; i < RegMgrSystem.SlotCount; i++)
        {
            Assert.Equal(i, Call(regmgr, "sceRegMgrRegisterCallback", callbacks[i]));
        }

        Assert.Equal(RegMgrSystem.ErrAlreadyRegistered, Call(regmgr, "sceRegMgrRegisterCallback", callbacks[3]));
        Assert.Equal(RegMgrSystem.ErrNoSlot, Call(regmgr, "sceRegMgrRegisterCallback", callbacks[RegMgrSystem.SlotCount]));

        // a freed slot is the next one given
        Assert.Equal(3, Call(regmgr, "sceRegMgrUnregisterCallback", callbacks[3]));
        Assert.Equal(RegMgrSystem.ErrNotRegistered, Call(regmgr, "sceRegMgrUnregisterCallback", callbacks[3]));
        Assert.Equal(RegMgrSystem.ErrNotRegistered, Call(regmgr, "sceRegMgrStartCallback", callbacks[3]));
        Assert.Equal(RegMgrSystem.ErrNotRegistered, Call(regmgr, "sceRegMgrStopCallback", callbacks[3]));
        Assert.Equal(RegMgrSystem.ErrNotRegistered, Call(regmgr, "sceRegMgrAddRegistryCallback", callbacks[3], _harness.Str("/CONFIG/")));
        Assert.Equal(3, Call(regmgr, "sceRegMgrRegisterCallback", callbacks[RegMgrSystem.SlotCount]));
    }

    [Fact]
    public void StartedCallbackHearsChangesOfTheKeysItWatches()
    {
        RegMgrSystem regmgr = _harness.Module<RegMgrSystem>();
        var registry = Registry.For(_harness.Root);
        registry.Define("/CONFIG/TESTA/first", Registry.KeyType.Int, 4);
        registry.Define("/CONFIG/TESTB/second", Registry.KeyType.Int, 4);
        registry.Define("/CONFIG/TESTC/third", Registry.KeyType.Int, 4);
        int[] callbacks = CreateCallbacks(2);
        RunAsSystemProgram();

        int watcher = callbacks[0];
        Assert.Equal(0, Call(regmgr, "sceRegMgrRegisterCallback", watcher));
        Assert.Equal(unchecked((int)0x800D0004), Call(regmgr, "sceRegMgrAddRegistryCallback", watcher, 0u));
        Assert.Equal(unchecked((int)0x800D0004), Call(regmgr, "sceRegMgrAddRegistryCallback", watcher, _harness.Str("/CONFIG/NOSUCH/")));
        Assert.Equal(unchecked((int)0x800D000A), Call(regmgr, "sceRegMgrAddRegistryCallback", watcher, _harness.Str("/" + new string('A', 0x100))));
        // the result is the category's place in the slot (and what a notification carries)
        Assert.Equal(0, Call(regmgr, "sceRegMgrAddRegistryCallback", watcher, _harness.Str("/CONFIG/TESTA/")));
        Assert.Equal(1, Call(regmgr, "sceRegMgrAddRegistryCallback", watcher, _harness.Str("/CONFIG/TESTB")));

        // not started yet: no notification
        Assert.True(registry.SetInt("/CONFIG/TESTB/", "second", 1));
        Assert.Equal(0, CallbackObject(watcher).NotifyCount);

        Assert.Equal(0, Call(regmgr, "sceRegMgrStartCallback", watcher));
        Assert.True(registry.SetInt("/CONFIG/TESTB/", "second", 2));
        Assert.Equal(1, CallbackObject(watcher).NotifyCount);
        Assert.Equal(1, CallbackObject(watcher).NotifyArg);
        // the same value again is no change; an unwatched category is not heard
        Assert.True(registry.SetInt("/CONFIG/TESTB/", "second", 2));
        Assert.True(registry.SetInt("/CONFIG/TESTC/", "third", 5));
        Assert.Equal(1, CallbackObject(watcher).NotifyCount);
        Assert.True(registry.SetInt("/CONFIG/TESTA/", "first", 7));
        Assert.Equal(2, CallbackObject(watcher).NotifyCount);
        Assert.Equal(0, CallbackObject(watcher).NotifyArg);

        // a callback started without categories watches every key
        int everything = callbacks[1];
        Assert.Equal(1, Call(regmgr, "sceRegMgrRegisterCallback", everything));
        Assert.Equal(1, Call(regmgr, "sceRegMgrStartCallback", everything));
        Assert.True(registry.SetInt("/CONFIG/TESTC/", "third", 6));
        Assert.Equal(1, CallbackObject(everything).NotifyCount);
        Assert.Equal(0, CallbackObject(everything).NotifyArg);

        // stopped: silent again
        Assert.Equal(0, Call(regmgr, "sceRegMgrStopCallback", watcher));
        Assert.True(registry.SetInt("/CONFIG/TESTA/", "first", 8));
        Assert.Equal(2, CallbackObject(watcher).NotifyCount);
        Assert.Equal(2, CallbackObject(everything).NotifyCount);
    }

    [Fact]
    public void SixthCategoryHasNoRoom()
    {
        RegMgrSystem regmgr = _harness.Module<RegMgrSystem>();
        var registry = Registry.For(_harness.Root);
        registry.Define("/CONFIG/TESTA/first", Registry.KeyType.Int, 4);
        int[] callbacks = CreateCallbacks(1);
        RunAsSystemProgram();
        Assert.Equal(0, Call(regmgr, "sceRegMgrRegisterCallback", callbacks[0]));
        for (int i = 0; i < RegMgrSystem.CategoriesPerSlot; i++)
        {
            Assert.Equal(i, Call(regmgr, "sceRegMgrAddRegistryCallback", callbacks[0], _harness.Str("/CONFIG/TESTA/")));
        }

        Assert.Equal(RegMgrSystem.ErrNoSlot, Call(regmgr, "sceRegMgrAddRegistryCallback", callbacks[0], _harness.Str("/CONFIG/TESTA/")));
    }
}
