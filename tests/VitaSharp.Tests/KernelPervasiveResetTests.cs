using VitaSharp.Core.Hle;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The FW 3.74 ScePervasiveForDriver reset exports (lowio.skprx): each one sets ("reset enable") or clears ("reset
/// disable") its bits of its register in the reset page 0xE3101000 under the module's lock and answers whether any of
/// them was set before; the indexed ones address 4-byte arrays without a bound; the page getters hand out the kernel
/// addresses of the pages laid out as the physical ones. The module is found by name so the proof without it still
/// builds.
/// </summary>
[Collection("guest")]
public sealed class KernelPervasiveResetTests(GuestMemoryFixture fixture) : IDisposable
{
    /// <summary>(enable export, disable export, register offset in the reset page, index argument or -1).</summary>
    private static readonly (string Enable, string Disable, uint Register, int Index)[] s_fixedMaskPairs =
    [
        ("NID_3E79D3D3", "NID_8A85E36B", 0x10, -1),
        ("NID_28731EC5", "NID_A7E64C6F", 0x30, -1),
        ("NID_31C0A98B", "NID_E2D8F6C3", 0x34, -1),
        ("NID_FF4B3CA6", "NID_AC475961", 0x50, 3),
        ("NID_66BF2885", "NID_D3B59C33", 0x70, 2),
        ("NID_B68254AD", "NID_E92E28FF", 0x88, -1),
        ("NID_7AE2F8E8", "NID_17109C28", 0x8C, -1),
        ("NID_3D8A8712", "NID_D326E15F", 0xA0, 1),
        ("kscePervasiveMsifResetEnable", "kscePervasiveMsifResetDisable", 0xB0, -1),
        ("NID_146108D4", "NID_EEE340ED", 0xC0, 4),
        ("NID_821AE6C7", "NID_5BAFA0DC", 0xE0, 1),
        ("NID_B8AF1249", "NID_6A2FB697", 0xF0, -1),
        ("kscePervasiveGpioResetEnable", "kscePervasiveGpioResetDisable", 0x100, -1),
        ("kscePervasiveSpiResetEnable", "kscePervasiveSpiResetDisable", 0x104, 2),
        ("NID_41F9E1C3", "NID_86BEA9A4", 0x110, 3),
        ("kscePervasiveUartResetEnable", "kscePervasiveUartResetDisable", 0x120, 6),
    ];

    private readonly HleHarness _harness = new(fixture, "kperv");

    public void Dispose() => _harness.Dispose();

    private HleModule Pervasive => _harness.Emu.Hle.Modules.Single(module => module.GetType().Name == "PervasiveKernel");

    private uint ResetRegister(uint offset) => (uint)Invoke(Pervasive, "Register", 0x1000u + offset)!;

    private int CallWithIndex(string export, int index) =>
        index < 0 ? Call(Pervasive, export) : Call(Pervasive, export, (uint)index);

    [Fact]
    public void EachResetExportSetsAndClearsBitZeroOfItsRegisterAndAnswersThePreviousState()
    {
        foreach ((string enable, string disable, uint register, int index) in s_fixedMaskPairs)
        {
            uint offset = register + (uint)Math.Max(index, 0) * 4;
            Assert.Equal(0u, ResetRegister(offset));

            Assert.Equal(0, CallWithIndex(enable, index));
            Assert.Equal(1u, ResetRegister(offset));
            Assert.Equal(1, CallWithIndex(enable, index));

            Assert.Equal(1, CallWithIndex(disable, index));
            Assert.Equal(0u, ResetRegister(offset));
            Assert.Equal(0, CallWithIndex(disable, index));
        }
    }

    [Fact]
    public void TheOtherBitsOfTheRegisterAreKept()
    {
        // a driver wrote the other bits of UART port 0's register through the page
        GuestMemory.W32((uint)Invoke(Pervasive, "PageAddress", 0xE3101000u)! + 0x120, 0xF0);

        Assert.Equal(0, Call(Pervasive, "kscePervasiveUartResetEnable", 0u));
        Assert.Equal(0xF1u, ResetRegister(0x120));
        Assert.Equal(1, Call(Pervasive, "kscePervasiveUartResetDisable", 0u));
        Assert.Equal(0xF0u, ResetRegister(0x120));
    }

    [Fact]
    public void TheMaskedExportsChangeOnlyTheBitsOfTheirMask()
    {
        // DSI port 1 (0x84) and USB port 0 (0x90) take the bits as an argument
        Assert.Equal(0, Call(Pervasive, "kscePervasiveDsiResetEnable", 1u, 0x6u));
        Assert.Equal(0x6u, ResetRegister(0x84));
        Assert.Equal(0u, ResetRegister(0x80));
        Assert.Equal(1, Call(Pervasive, "kscePervasiveDsiResetEnable", 1u, 0x3u));
        Assert.Equal(0x7u, ResetRegister(0x84));
        Assert.Equal(1, Call(Pervasive, "kscePervasiveDsiResetDisable", 1u, 0x2u));
        Assert.Equal(0x5u, ResetRegister(0x84));
        Assert.Equal(0, Call(Pervasive, "kscePervasiveDsiResetDisable", 1u, 0x2u));

        Assert.Equal(0, Call(Pervasive, "NID_4AF7A01E", 0u, 0xBu));
        Assert.Equal(0xBu, ResetRegister(0x90));
        Assert.Equal(1, Call(Pervasive, "NID_13CC07C9", 0u, 0x8u));
        Assert.Equal(0x3u, ResetRegister(0x90));
    }

    [Fact]
    public void ThePageGettersHandOutThePagesInTheirPhysicalLayout()
    {
        uint misc = (uint)Invoke(Pervasive, "NID_81A155F1")!;
        Assert.NotEqual(0u, misc);
        Assert.Equal(0u, misc & 0xFFF);
        Assert.Equal(misc + 0x4000, (uint)Invoke(Pervasive, "NID_47708C03")!);
        Assert.Equal(misc + 0x6000, (uint)Invoke(Pervasive, "NID_04D145C7")!);
        Assert.Equal(misc + 0x10000, (uint)Invoke(Pervasive, "NID_2A563E35")!);

        // a driver that writes the reset page through the getter's address sees the exports' bits
        Call(Pervasive, "kscePervasiveGpioResetEnable");
        Assert.Equal(1u, GuestMemory.R32(misc + 0x1100));
    }

    [Fact]
    public void AnIndexPastThePagesIsIgnored()
    {
        Assert.Equal(0, Call(Pervasive, "kscePervasiveUartResetEnable", 0x10000u));
        Assert.True(_harness.Emu.Hle.IsImplemented(0x788B6C61));
    }
}
