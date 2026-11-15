using System.Reflection;
using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Loader.Self;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>sceAppMgrSuspendUntilActivated and sceAppMgrEnableCoredumpForTest as the SceAppMgr module of FW 3.74
/// answers them (who may call, what happens to the caller's application).</summary>
[Collection("guest")]
public sealed class AppMgrSuspendTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int NotSystemProgram = unchecked((int)0x8080201F);
    private const ulong GameAuthorityId = 0x210000101CC404D8UL;

    private readonly HleHarness _harness = new(fixture, "appmgr-suspend");
    private Emulator? _other;

    public void Dispose()
    {
        _other?.Dispose();
        _harness.Dispose();
    }

    private void RunAsSystemProgram()
    {
        byte[] capability = new byte[ProgramAccess.CapabilitySize];
        capability[0] = 0x40;
        _harness.Emu.Access = new ProgramAccess(0x2800000000000025UL, capability, false);
        Assert.True(_harness.Emu.Access.IsSystemProgram);
    }

    private void RunAsGame()
    {
        _harness.Emu.Access = new ProgramAccess(GameAuthorityId, new byte[ProgramAccess.CapabilitySize], false);
        Assert.False(_harness.Emu.Access.IsSystemProgram);
    }

    /// <summary>A second process of the same system (the one the shell brought to the front instead).</summary>
    private Emulator OtherProcess()
    {
        ConstructorInfo constructor = typeof(Emulator).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(c => c.GetParameters().Length == 2 && c.GetParameters()[0].ParameterType == typeof(Emulator));
        _other = (Emulator)constructor.Invoke([_harness.Emu, null]);
        return _other;
    }

    private static bool CoredumpForTestEnabled(AppMgr appMgr) =>
        (bool)(typeof(AppMgr).GetProperty("CoredumpForTestEnabled", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(nameof(AppMgr), "CoredumpForTestEnabled")).GetValue(appMgr)!;

    [Fact]
    public void SuspendUntilActivatedRefusesAProgramThatIsNotASystemProgram()
    {
        AppMgr appMgr = _harness.Module<AppMgr>();
        RunAsGame();
        Emulator other = OtherProcess();
        _harness.Emu.Vita.SetForeground(other);
        Assert.Equal(NotSystemProgram, Call(appMgr, "sceAppMgrSuspendUntilActivated"));
        Assert.False(_harness.Emu.Suspended); // refused before anything is changed
    }

    [Fact]
    public void SuspendUntilActivatedLeavesTheActiveApplicationRunning()
    {
        AppMgr appMgr = _harness.Module<AppMgr>();
        RunAsSystemProgram();
        Assert.True(_harness.Emu.IsForeground);
        Assert.Equal(0, Call(appMgr, "sceAppMgrSuspendUntilActivated"));
        Assert.False(_harness.Emu.Suspended);
    }

    [Fact]
    public void SuspendUntilActivatedSuspendsABackgroundApplicationUntilItIsActivated()
    {
        AppMgr appMgr = _harness.Module<AppMgr>();
        RunAsSystemProgram();
        Emulator other = OtherProcess();
        _harness.Emu.Vita.SetForeground(other);
        Assert.False(_harness.Emu.IsForeground);

        Assert.Equal(0, Call(appMgr, "sceAppMgrSuspendUntilActivated"));
        Assert.True(_harness.Emu.Suspended);

        // the shell activates it: it runs again and is in front
        Assert.Equal(0, Call(_harness.Module<AppMgrShell>(), "sceAppMgrActivateApp", _harness.Emu.AppId));
        Assert.False(_harness.Emu.Suspended);
        Assert.True(_harness.Emu.IsForeground);
    }

    [Fact]
    public void EnableCoredumpForTestIsTheShellsOnly()
    {
        AppMgr appMgr = _harness.Module<AppMgr>();

        // a system application that is not the shell is refused
        RunAsSystemProgram();
        Assert.False(_harness.Emu.IsShell);
        Assert.Equal(NotSystemProgram, Call(appMgr, "sceAppMgrEnableCoredumpForTest"));
        Assert.False(CoredumpForTestEnabled(appMgr));

        _harness.Emu.IsShell = true;
        Assert.Equal(0, Call(appMgr, "sceAppMgrEnableCoredumpForTest"));
        Assert.True(CoredumpForTestEnabled(appMgr));
        // a second call answers the same
        Assert.Equal(0, Call(appMgr, "sceAppMgrEnableCoredumpForTest"));
    }
}
