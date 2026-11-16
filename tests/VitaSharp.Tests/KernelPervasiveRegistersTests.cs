using VitaSharp.Core.Hle;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The FW 3.74 ScePervasiveForDriver setters of the base clock (0xE3103000) and misc (0xE3100000) pages
/// (lowio.skprx): the value each writes into its register, the firmware's range checks (0x803F0000), the switches
/// that answer their previous state and the USB port sequences built on the reset and gate exports. The exports are
/// found by name so the proof without them still builds.
/// </summary>
[Collection("guest")]
public sealed class KernelPervasiveRegistersTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int InvalidArgument = unchecked((int)0x803F0000);

    /// <summary>(export, argument, base clock register, value written; or the rejected argument when the register is
    /// uint.MaxValue).</summary>
    private static readonly (string Export, uint Argument, uint Register, uint Value)[] s_baseClockSetters =
    [
        ("NID_725B4F59", 7, 0x20, 7),
        ("NID_725B4F59", 0, uint.MaxValue, 0),
        ("NID_725B4F59", 8, uint.MaxValue, 0),
        ("NID_12D17D47", 6, 0x30, 6),
        ("NID_12D17D47", 7, uint.MaxValue, 0),
        ("NID_589D0291", 5, 0x44, 5),
        ("NID_589D0291", 0, uint.MaxValue, 0),
        ("NID_925D9D24", 1, 0x70, 0x10001),
        ("NID_925D9D24", 0, 0x70, 0x10000),
        ("NID_925D9D24", 2, uint.MaxValue, 0),
        ("kscePervasiveMsifSetClock", 6, 0xB0, 0x10002),
        ("kscePervasiveMsifSetClock", 1, 0xB0, 1),
        ("kscePervasiveMsifSetClock", 3, uint.MaxValue, 0),
        ("kscePervasiveMsifSetClock", 7, uint.MaxValue, 0),
        ("NID_A6832B33", 4, 0x1F0, 4),
        ("NID_A6832B33", 5, uint.MaxValue, 0),
        ("NID_2FB5F88F", 2, 0x210, 2),
        ("NID_2FB5F88F", 3, uint.MaxValue, 0),
        ("NID_98D8914A", 1, 0x214, 1),
        ("NID_98D8914A", 2, uint.MaxValue, 0),
        ("NID_A96642E3", 1, 0x68, 3),
        ("NID_A96642E3", 0xFF, 0x68, 0),
        ("NID_A96642E3", 2, uint.MaxValue, 0),
    ];

    private readonly HleHarness _harness = new(fixture, "kpregs");

    public void Dispose() => _harness.Dispose();

    private HleModule Pervasive => _harness.Emu.Hle.Modules.Single(module => module.GetType().Name == "PervasiveKernel");

    private uint Register(uint offset) => (uint)Invoke(Pervasive, "Register", offset)!;

    [Fact]
    public void TheBaseClockSettersWriteTheirRegisterOrRejectTheArgument()
    {
        foreach ((string export, uint argument, uint register, uint value) in s_baseClockSetters)
        {
            if (register == uint.MaxValue)
            {
                Assert.Equal(InvalidArgument, Call(Pervasive, export, argument));
            }
            else
            {
                Assert.Equal(0, Call(Pervasive, export, argument));
                Assert.Equal(value, Register(0x3000 + register));
            }
        }

        Assert.Equal(0, Call(Pervasive, "NID_6AF0196F", 5u, 3u));
        Assert.Equal(0x50003u, Register(0x3010));
        Assert.Equal(InvalidArgument, Call(Pervasive, "NID_6AF0196F", 8u, 3u));
        Assert.Equal(InvalidArgument, Call(Pervasive, "NID_6AF0196F", 3u, 8u));
        Assert.Equal(0x50003u, Register(0x3010));

        Assert.Equal(0, Call(Pervasive, "NID_26B51075"));
        Assert.Equal(1u, Register(0x31D0));
        Assert.Equal(0, Call(Pervasive, "NID_D185D235"));
        Assert.Equal(0u, Register(0x31D0));
    }

    [Fact]
    public void TheClockSourceSetterKeepsTheGateAndChecksItsPairs()
    {
        Assert.Equal(0, Call(Pervasive, "NID_8847825F"));
        Assert.Equal(1, Call(Pervasive, "NID_8847825F"));
        Assert.Equal(1u, Register(0x30A4));

        Assert.Equal(0, Call(Pervasive, "NID_59B7CA34", 0u, 16u));
        Assert.Equal(15u, Register(0x3060));
        Assert.Equal(0u, Register(0x3064));
        Assert.Equal(1u, Register(0x30A4));
        Assert.Equal(0, Call(Pervasive, "NID_59B7CA34", 1u, 1u));
        Assert.Equal(0u, Register(0x3060));
        Assert.Equal(1u, Register(0x3064));
        Assert.Equal(InvalidArgument, Call(Pervasive, "NID_59B7CA34", 0u, 0u));
        Assert.Equal(InvalidArgument, Call(Pervasive, "NID_59B7CA34", 0u, 17u));
        Assert.Equal(InvalidArgument, Call(Pervasive, "NID_59B7CA34", 1u, 2u));
        Assert.Equal(InvalidArgument, Call(Pervasive, "NID_59B7CA34", 2u, 1u));

        Assert.Equal(1, Call(Pervasive, "NID_9E81E057"));
        Assert.Equal(0, Call(Pervasive, "NID_9E81E057"));
    }

    [Fact]
    public void TheMiscSwitchesAnswerTheirPreviousState()
    {
        Assert.Equal(0, Call(Pervasive, "NID_F1908D3C", 0u));
        Assert.Equal(1u, Register(0x140));
        Assert.Equal(0, Call(Pervasive, "NID_F1908D3C", 5u));
        Assert.Equal(1u, Register(0x148));
        Assert.Equal(1, Call(Pervasive, "NID_350BF570", 0u));
        Assert.Equal(0u, Register(0x140));
        Assert.Equal(1u, Register(0x148));

        Assert.Equal(0, Call(Pervasive, "NID_0C1A7FFA"));
        Assert.Equal(1, Call(Pervasive, "NID_5B4EF352"));
        Assert.Equal(0u, Register(0x144));

        Assert.Equal(0, Call(Pervasive, "NID_6B2BE37E", 2u, 3u));
        Assert.Equal(1u, Register(0x8C));
        Assert.Equal(1, Call(Pervasive, "NID_6B2BE37E", 2u, 0u));
        Assert.Equal(InvalidArgument, Call(Pervasive, "NID_6B2BE37E", 3u, 1u));
    }

    [Fact]
    public void TheMiscFieldSettersChangeOnlyTheirBits()
    {
        Assert.Equal(0, Call(Pervasive, "NID_453BD2EC", 3u, 1u));
        Assert.Equal(0, Call(Pervasive, "NID_453BD2EC", 0u, 1u));
        Assert.Equal(0x9u, Register(0x124));
        Assert.Equal(0, Call(Pervasive, "NID_453BD2EC", 3u, 0u));
        Assert.Equal(0x1u, Register(0x124));
        Assert.Equal(InvalidArgument, Call(Pervasive, "NID_453BD2EC", 4u, 0u));
        Assert.Equal(InvalidArgument, Call(Pervasive, "NID_453BD2EC", 0u, 2u));

        Assert.Equal(0, Call(Pervasive, "NID_043B33F5", 2u, 6u));
        Assert.Equal(0, Call(Pervasive, "NID_043B33F5", 0u, 5u));
        Assert.Equal(0x60005u, Register(0x310));
        Assert.Equal(0, Call(Pervasive, "NID_043B33F5", 2u, 1u));
        Assert.Equal(0x10005u, Register(0x310));
        Assert.Equal(InvalidArgument, Call(Pervasive, "NID_043B33F5", 0u, 7u));

        Assert.Equal(0, Call(Pervasive, "NID_9F8E589C", 3u, 0x700u));
        Assert.Equal(0x700u, Register(0x11C));
        Assert.Equal(InvalidArgument, Call(Pervasive, "NID_9F8E589C", 3u, 0x800u));
        Assert.Equal(InvalidArgument, Call(Pervasive, "NID_9F8E589C", 4u, 0u));

        Assert.Equal(0, Call(Pervasive, "NID_A1D2E440", 0x1234u));
        Assert.Equal(0x1234u, Register(0x190));

        (string On, string Off, uint Register)[] bitSwitches =
        [
            ("NID_5E20D7D5", "NID_E6728E30", 0x194),
            ("NID_03EA9418", "NID_BD868E6E", 0x198),
            ("NID_FDD445C3", "NID_132E514A", 0x188),
        ];
        foreach ((string on, string off, uint register) in bitSwitches)
        {
            Assert.Equal(0, Call(Pervasive, on));
            Assert.Equal(1u, Register(register));
            Assert.Equal(0, Call(Pervasive, off));
            Assert.Equal(0u, Register(register));
        }

        Assert.Equal(0, Call(Pervasive, "NID_4B99558F"));
        Assert.Equal(3u, Register(0x1AC));
        Assert.Equal(3u, Register(0x18C));
        Assert.Equal(0, Call(Pervasive, "NID_34537B12"));
        Assert.Equal(0u, Register(0x1AC));
        Assert.Equal(0u, Register(0x18C));
    }

    [Fact]
    public void TheUsbPortSequencesSetTheirResetGateAndModeBits()
    {
        Assert.Equal(1, Call(Pervasive, "NID_4F64DEF6", 1u));
        Assert.Equal(0x3u, Register(0x1000 + 0x94));
        Assert.Equal(0u, Register(0x2000 + 0x94));
        Assert.Equal(1u, Register(0x88));
        Assert.Equal(0x8u, Register(0x17C));

        Assert.Equal(1, Call(Pervasive, "NID_4F64DEF6", 2u));
        Assert.Equal(0x28u, Register(0x17C));

        Assert.Equal(1, Call(Pervasive, "NID_BB67EF58", 1u));
        Assert.Equal(0xBu, Register(0x1000 + 0x94));
        Assert.Equal(0x20u, Register(0x17C));

        Assert.Equal(1, Call(Pervasive, "NID_29E65AE1", 2u, 0x11u, 0x22u));
        Assert.Equal(0x22u, Register(0x164));
        Assert.Equal(0x11u, (uint)Invoke(Pervasive, "NID_E3A735B9", 2u)!);
        Assert.Equal(1, Call(Pervasive, "NID_29E65AE1", 3u, 0x11u, 0x22u));
        Assert.Equal(0u, (uint)Invoke(Pervasive, "NID_E3A735B9", 3u)!);
    }

    [Fact]
    public void TheCardPageReportsItsInsertBit()
    {
        Assert.Equal(0, Call(Pervasive, "kscePervasiveRemovableMemoryGetCardInsertState"));
        uint card = (uint)Invoke(Pervasive, "NID_2A563E35")!;
        GuestMemory.W32(card + 0xF40, 0x3);
        Assert.Equal(1, Call(Pervasive, "kscePervasiveRemovableMemoryGetCardInsertState"));
        GuestMemory.W32(card + 0xF44, 0x5);
        Invoke(Pervasive, "NID_F9E7B023");
        Assert.Equal(0x5u, Register(0x10F44));
    }
}
