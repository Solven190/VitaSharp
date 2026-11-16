using VitaSharp.Core.Hle;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The FW 3.74 ScePervasiveForDriver clock gate exports (lowio.skprx): each one sets ("clock enable") or clears
/// ("clock disable") its bits of its register in the gate page 0xE3102000 and answers whether any of them was set
/// before, without touching the reset page; NID_270BE98A hands out the eight 0xE3108000 pages. The gate exports are
/// found by name so the proof without them still builds.
/// </summary>
[Collection("guest")]
public sealed class KernelPervasiveGateTests(GuestMemoryFixture fixture) : IDisposable
{
    /// <summary>(enable export, disable export, register offset in the gate page, bit, index argument or -1).</summary>
    private static readonly (string Enable, string Disable, uint Register, uint Bit, int Index)[] s_fixedMaskPairs =
    [
        ("NID_FB01A2DD", "NID_2EEBE9AE", 0x30, 1, -1),
        ("NID_B9904983", "NID_FBE5AA2F", 0x30, 2, -1),
        ("NID_B1CFA18F", "NID_03E1FAA6", 0x34, 1, -1),
        ("NID_1068C067", "NID_4B223AF5", 0x70, 1, 2),
        ("NID_07F2A738", "NID_5AFE0AF0", 0x88, 1, -1),
        ("NID_C0C842FE", "NID_9BB7B932", 0x8C, 1, -1),
        ("NID_3CF7A012", "NID_4E43299D", 0xA0, 1, 1),
        ("kscePervasiveMsifClockEnable", "kscePervasiveMsifClockDisable", 0xB0, 1, -1),
        ("NID_4235EB7D", "NID_A3CF9844", 0xC0, 1, 3),
        ("NID_47F2C35C", "NID_F861FEE4", 0xE0, 1, 1),
        ("NID_6B5DEDA2", "NID_51981FEE", 0xF0, 1, -1),
        ("kscePervasiveGpioClockEnable", "kscePervasiveGpioClockDisable", 0x100, 1, -1),
        ("kscePervasiveSpiClockEnable", "kscePervasiveSpiClockDisable", 0x104, 1, 2),
        ("NID_C37CFDFC", "NID_3F3E1AEA", 0x110, 1, 3),
        ("kscePervasiveUartClockEnable", "kscePervasiveUartClockDisable", 0x120, 1, 6),
    ];

    private readonly HleHarness _harness = new(fixture, "kpgate");

    public void Dispose() => _harness.Dispose();

    private HleModule Pervasive => _harness.Emu.Hle.Modules.Single(module => module.GetType().Name == "PervasiveKernel");

    private uint GateRegister(uint offset) => (uint)Invoke(Pervasive, "Register", 0x2000u + offset)!;

    private uint ResetRegister(uint offset) => (uint)Invoke(Pervasive, "Register", 0x1000u + offset)!;

    private int CallWithIndex(string export, int index) =>
        index < 0 ? Call(Pervasive, export) : Call(Pervasive, export, (uint)index);

    [Fact]
    public void EachGateExportSetsAndClearsItsBitAndAnswersThePreviousState()
    {
        foreach ((string enable, string disable, uint register, uint bit, int index) in s_fixedMaskPairs)
        {
            uint offset = register + (uint)Math.Max(index, 0) * 4;
            uint before = GateRegister(offset);
            Assert.Equal(0u, before & bit);

            Assert.Equal(0, CallWithIndex(enable, index));
            Assert.Equal(before | bit, GateRegister(offset));
            Assert.Equal(1, CallWithIndex(enable, index));

            Assert.Equal(1, CallWithIndex(disable, index));
            Assert.Equal(before, GateRegister(offset));
            Assert.Equal(0, CallWithIndex(disable, index));
            Assert.Equal(0u, ResetRegister(offset));
        }
    }

    [Fact]
    public void TheTwoBitsOfRegister0x30AreSeparate()
    {
        Assert.Equal(0, Call(Pervasive, "NID_FB01A2DD"));
        Assert.Equal(0, Call(Pervasive, "NID_B9904983"));
        Assert.Equal(0x3u, GateRegister(0x30));
        Assert.Equal(1, Call(Pervasive, "NID_2EEBE9AE"));
        Assert.Equal(0x2u, GateRegister(0x30));
    }

    [Fact]
    public void TheMaskedExportsChangeOnlyTheirBits()
    {
        // register 0x10 keeps only the bits 0xF000F of the mask
        Assert.Equal(0, Call(Pervasive, "NID_39E51AE2", 0xFFFFFFFFu));
        Assert.Equal(0xF000Fu, GateRegister(0x10));
        Assert.Equal(1, Call(Pervasive, "NID_CA0ACFC5", 0x10001u));
        Assert.Equal(0xE000Eu, GateRegister(0x10));

        Assert.Equal(0, Call(Pervasive, "kscePervasiveDsiClockEnable", 1u, 0xFu));
        Assert.Equal(0xFu, GateRegister(0x84));
        Assert.Equal(1, Call(Pervasive, "kscePervasiveDsiClockDisable", 1u, 0x5u));
        Assert.Equal(0xAu, GateRegister(0x84));

        Assert.Equal(0, Call(Pervasive, "NID_A2EFD7AF", 0u, 0xBu));
        Assert.Equal(1, Call(Pervasive, "NID_AD1E81EB", 0u, 0x1u));
        Assert.Equal(0xAu, GateRegister(0x90));
    }

    [Fact]
    public void TheEightPagesGetterAnswersZeroPastTheLastPage()
    {
        uint misc = (uint)Invoke(Pervasive, "NID_81A155F1")!;
        for (uint index = 0; index < 8; index++)
        {
            Assert.Equal(misc + 0x8000 + index * 0x1000, (uint)Invoke(Pervasive, "NID_270BE98A", index)!);
        }

        Assert.Equal(0u, (uint)Invoke(Pervasive, "NID_270BE98A", 8u)!);
        Assert.Equal(0u, (uint)Invoke(Pervasive, "NID_270BE98A", 0xFFFFFFFFu)!);

        Call(Pervasive, "kscePervasiveUartClockEnable", 0u);
        Assert.Equal(1u, GuestMemory.R32(misc + 0x2120));
    }
}
