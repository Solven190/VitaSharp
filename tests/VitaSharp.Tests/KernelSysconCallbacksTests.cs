using VitaSharp.Core.Hle;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The FW 3.74 SceSyscon module's event handler table: each registration export keeps (callback, argument) for its
/// event number and returns 0, 0 removes the handler, and the other setters of the module answer 0. The module is
/// found by name so the proof without it still builds.
/// </summary>
[Collection("guest")]
public sealed class KernelSysconCallbacksTests(GuestMemoryFixture fixture) : IDisposable
{
    private static readonly (string Export, int Event)[] s_registrations =
    [
        ("NID_18A6F4D9", 0),
        ("NID_DE613081", 1),
        ("NID_229A07C2", 2),
        ("NID_8351526D", 3),
        ("NID_9F8340FF", 4),
        ("NID_35E1689F", 5),
        ("NID_474A9EA7", 6),
        ("NID_4E88B4D9", 8),
        ("NID_376CCCB8", 9),
        ("NID_3BAAC8A9", 10),
        ("NID_4A42712F", 15),
        ("ksceSysconSetLowBatteryCallback", 16),
        ("NID_80D6E061", 17),
        ("NID_7682FE69", 18),
        ("NID_E0D52DF0", 19),
        ("NID_2D471528", 20),
        ("NID_129EA022", 21),
        ("NID_85E5DEBF", 22),
        ("ksceSysconSetThermalAlertCallback", 23),
        ("NID_2E4BA4B8", 25),
        ("NID_C442D0BE", 26),
        ("NID_61AE3970", 27),
        ("NID_94678881", 28),
        ("NID_7AA00C01", 29),
        ("NID_423D0C58", 32),
        ("NID_154676F1", 33),
        ("NID_63352A39", 34),
        ("NID_14730196", 35),
        ("ksceSysconSetAlarmCallback", 36),
        ("NID_A26586B2", 37),
        ("NID_67A4CB9F", 38),
    ];

    private readonly HleHarness _harness = new(fixture, "ksyscon");

    public void Dispose() => _harness.Dispose();

    private HleModule Syscon => _harness.Emu.Hle.Modules.Single(module => module.GetType().Name == "SysconKernel");

    private (uint Callback, uint Argument) Handler(int number) =>
        ((uint, uint))Invoke(Syscon, "EventHandler", number)!;

    [Fact]
    public void EachRegistrationKeepsItsEventsHandler()
    {
        foreach ((string export, int number) in s_registrations)
        {
            uint callback = 0x81000001u + (uint)number * 0x10;
            Assert.Equal(0, Call(Syscon, export, callback, (uint)number + 0x100));
            Assert.Equal((callback, (uint)number + 0x100), Handler(number));
        }

        // the events are separate: removing one leaves the others
        Assert.Equal(0, Call(Syscon, "ksceSysconSetAlarmCallback", 0u, 0u));
        Assert.Equal((0u, 0u), Handler(36));
        Assert.Equal((0x81000001u + 35 * 0x10, 35u + 0x100), Handler(35));
        Assert.Equal((0x81000001u + 37 * 0x10, 37u + 0x100), Handler(37));
    }

    [Fact]
    public void TheOtherSettersAnswerZero()
    {
        Assert.Equal(0, Call(Syscon, "ksceSysconSetDebugHandlers", 0x81000101u));
        Assert.Equal(0, Call(Syscon, "NID_901D6CD4", 1u));
        Assert.Equal(0, Call(Syscon, "NID_0D58E297", 1u));
        Assert.Equal(0, Call(Syscon, "ksceSysconWaitInitialized"));
        Assert.True(_harness.Emu.Hle.IsImplemented(0x32418370));
    }
}
