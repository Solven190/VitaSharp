namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// ScePervasiveForDriver's clock gate page (0xE3102000) and the getter of the eight 0xE3108000 pages: the gate exports
/// set ("clock enable") or clear ("clock disable") their register's bits as the reset ones do, followed by the
/// firmware's barrier, and answer whether any of the bits was set before.
/// </summary>
public sealed partial class PervasiveKernel
{
    const uint GatePage = 0xE3102000 - PhysicalBase;

    /// <summary>Opens a device's clock gate: sets its bits in the gate page.</summary>
    int GateEnable(uint register, uint mask) => UpdateBits(GatePage + register, mask, true);

    /// <summary>Closes a device's clock gate: clears its bits in the gate page.</summary>
    int GateDisable(uint register, uint mask) => UpdateBits(GatePage + register, mask, false);

    /// <summary>NID_270BE98A(index): the address of page 0xE3108000 + 0x1000 * index for index 0..7, else 0.</summary>
    [Hle(Nid = 0x270BE98A, Library = "ScePervasiveForDriver", ReturnsValue = true)]
    uint NID_270BE98A(uint index) => index < 8 ? PageAddress(0xE3108000 + index * 0x1000) : 0;

    /// <summary>NID_39E51AE2(mask): gate register 0x10, the bits of <paramref name="mask"/> &amp; 0xF000F on.</summary>
    [Hle(Nid = 0x39E51AE2, Library = "ScePervasiveForDriver")]
    int NID_39E51AE2(uint mask) => GateEnable(0x10, mask & 0xF000F);

    /// <summary>NID_CA0ACFC5(mask): gate register 0x10, the bits of <paramref name="mask"/> &amp; 0xF000F off.</summary>
    [Hle(Nid = 0xCA0ACFC5, Library = "ScePervasiveForDriver")]
    int NID_CA0ACFC5(uint mask) => GateDisable(0x10, mask & 0xF000F);

    /// <summary>NID_FB01A2DD(): gate register 0x30, bit 0 on.</summary>
    [Hle(Nid = 0xFB01A2DD, Library = "ScePervasiveForDriver")]
    int NID_FB01A2DD() => GateEnable(0x30, 1);

    /// <summary>NID_2EEBE9AE(): gate register 0x30, bit 0 off.</summary>
    [Hle(Nid = 0x2EEBE9AE, Library = "ScePervasiveForDriver")]
    int NID_2EEBE9AE() => GateDisable(0x30, 1);

    /// <summary>NID_B9904983(): gate register 0x30, bit 1 on.</summary>
    [Hle(Nid = 0xB9904983, Library = "ScePervasiveForDriver")]
    int NID_B9904983() => GateEnable(0x30, 2);

    /// <summary>NID_FBE5AA2F(): gate register 0x30, bit 1 off.</summary>
    [Hle(Nid = 0xFBE5AA2F, Library = "ScePervasiveForDriver")]
    int NID_FBE5AA2F() => GateDisable(0x30, 2);

    /// <summary>NID_B1CFA18F(): gate register 0x34, bit 0 on.</summary>
    [Hle(Nid = 0xB1CFA18F, Library = "ScePervasiveForDriver")]
    int NID_B1CFA18F() => GateEnable(0x34, 1);

    /// <summary>NID_03E1FAA6(): gate register 0x34, bit 0 off.</summary>
    [Hle(Nid = 0x03E1FAA6, Library = "ScePervasiveForDriver")]
    int NID_03E1FAA6() => GateDisable(0x34, 1);

    /// <summary>NID_1068C067(index): gate register 0x70 + 4 * index, bit 0 on.</summary>
    [Hle(Nid = 0x1068C067, Library = "ScePervasiveForDriver")]
    int NID_1068C067(uint index) => GateEnable(Indexed(0x70, index), 1);

    /// <summary>NID_4B223AF5(index): gate register 0x70 + 4 * index, bit 0 off.</summary>
    [Hle(Nid = 0x4B223AF5, Library = "ScePervasiveForDriver")]
    int NID_4B223AF5(uint index) => GateDisable(Indexed(0x70, index), 1);

    /// <summary>kscePervasiveDsiClockEnable(index, mask): gate register 0x80 + 4 * index, the mask bits on.</summary>
    [Hle(Nid = 0xBC42C72F, Library = "ScePervasiveForDriver")]
    int kscePervasiveDsiClockEnable(uint index, uint mask) => GateEnable(Indexed(0x80, index), mask);

    /// <summary>kscePervasiveDsiClockDisable(index, mask): gate register 0x80 + 4 * index, the mask bits off.</summary>
    [Hle(Nid = 0x25AE181E, Library = "ScePervasiveForDriver")]
    int kscePervasiveDsiClockDisable(uint index, uint mask) => GateDisable(Indexed(0x80, index), mask);

    /// <summary>NID_07F2A738(): gate register 0x88, bit 0 on.</summary>
    [Hle(Nid = 0x07F2A738, Library = "ScePervasiveForDriver")]
    int NID_07F2A738() => GateEnable(0x88, 1);

    /// <summary>NID_5AFE0AF0(): gate register 0x88, bit 0 off.</summary>
    [Hle(Nid = 0x5AFE0AF0, Library = "ScePervasiveForDriver")]
    int NID_5AFE0AF0() => GateDisable(0x88, 1);

    /// <summary>NID_C0C842FE(): gate register 0x8C, bit 0 on.</summary>
    [Hle(Nid = 0xC0C842FE, Library = "ScePervasiveForDriver")]
    int NID_C0C842FE() => GateEnable(0x8C, 1);

    /// <summary>NID_9BB7B932(): gate register 0x8C, bit 0 off.</summary>
    [Hle(Nid = 0x9BB7B932, Library = "ScePervasiveForDriver")]
    int NID_9BB7B932() => GateDisable(0x8C, 1);

    /// <summary>NID_A2EFD7AF(index, mask): gate register 0x90 + 4 * index, the mask bits on.</summary>
    [Hle(Nid = 0xA2EFD7AF, Library = "ScePervasiveForDriver")]
    int NID_A2EFD7AF(uint index, uint mask) => GateEnable(Indexed(0x90, index), mask);

    /// <summary>NID_AD1E81EB(index, mask): gate register 0x90 + 4 * index, the mask bits off.</summary>
    [Hle(Nid = 0xAD1E81EB, Library = "ScePervasiveForDriver")]
    int NID_AD1E81EB(uint index, uint mask) => GateDisable(Indexed(0x90, index), mask);

    /// <summary>NID_3CF7A012(index): gate register 0xA0 + 4 * index, bit 0 on.</summary>
    [Hle(Nid = 0x3CF7A012, Library = "ScePervasiveForDriver")]
    int NID_3CF7A012(uint index) => GateEnable(Indexed(0xA0, index), 1);

    /// <summary>NID_4E43299D(index): gate register 0xA0 + 4 * index, bit 0 off.</summary>
    [Hle(Nid = 0x4E43299D, Library = "ScePervasiveForDriver")]
    int NID_4E43299D(uint index) => GateDisable(Indexed(0xA0, index), 1);

    /// <summary>kscePervasiveMsifClockEnable(): gate register 0xB0, bit 0 on.</summary>
    [Hle(Nid = 0x7704C013, Library = "ScePervasiveForDriver")]
    int kscePervasiveMsifClockEnable() => GateEnable(0xB0, 1);

    /// <summary>kscePervasiveMsifClockDisable(): gate register 0xB0, bit 0 off.</summary>
    [Hle(Nid = 0x2A9778CD, Library = "ScePervasiveForDriver")]
    int kscePervasiveMsifClockDisable() => GateDisable(0xB0, 1);

    /// <summary>NID_4235EB7D(index): gate register 0xC0 + 4 * index, bit 0 on.</summary>
    [Hle(Nid = 0x4235EB7D, Library = "ScePervasiveForDriver")]
    int NID_4235EB7D(uint index) => GateEnable(Indexed(0xC0, index), 1);

    /// <summary>NID_A3CF9844(index): gate register 0xC0 + 4 * index, bit 0 off.</summary>
    [Hle(Nid = 0xA3CF9844, Library = "ScePervasiveForDriver")]
    int NID_A3CF9844(uint index) => GateDisable(Indexed(0xC0, index), 1);

    /// <summary>NID_47F2C35C(index): gate register 0xE0 + 4 * index, bit 0 on.</summary>
    [Hle(Nid = 0x47F2C35C, Library = "ScePervasiveForDriver")]
    int NID_47F2C35C(uint index) => GateEnable(Indexed(0xE0, index), 1);

    /// <summary>NID_F861FEE4(index): gate register 0xE0 + 4 * index, bit 0 off.</summary>
    [Hle(Nid = 0xF861FEE4, Library = "ScePervasiveForDriver")]
    int NID_F861FEE4(uint index) => GateDisable(Indexed(0xE0, index), 1);

    /// <summary>NID_6B5DEDA2(): gate register 0xF0, bit 0 on.</summary>
    [Hle(Nid = 0x6B5DEDA2, Library = "ScePervasiveForDriver")]
    int NID_6B5DEDA2() => GateEnable(0xF0, 1);

    /// <summary>NID_51981FEE(): gate register 0xF0, bit 0 off.</summary>
    [Hle(Nid = 0x51981FEE, Library = "ScePervasiveForDriver")]
    int NID_51981FEE() => GateDisable(0xF0, 1);

    /// <summary>kscePervasiveGpioClockEnable(): gate register 0x100, bit 0 on.</summary>
    [Hle(Nid = 0x2F195C97, Library = "ScePervasiveForDriver")]
    int kscePervasiveGpioClockEnable() => GateEnable(0x100, 1);

    /// <summary>kscePervasiveGpioClockDisable(): gate register 0x100, bit 0 off.</summary>
    [Hle(Nid = 0xEB176898, Library = "ScePervasiveForDriver")]
    int kscePervasiveGpioClockDisable() => GateDisable(0x100, 1);

    /// <summary>kscePervasiveSpiClockEnable(index): gate register 0x104 + 4 * index, bit 0 on.</summary>
    [Hle(Nid = 0xDFD96BFC, Library = "ScePervasiveForDriver")]
    int kscePervasiveSpiClockEnable(uint index) => GateEnable(Indexed(0x104, index), 1);

    /// <summary>kscePervasiveSpiClockDisable(index): gate register 0x104 + 4 * index, bit 0 off.</summary>
    [Hle(Nid = 0xA85BF98A, Library = "ScePervasiveForDriver")]
    int kscePervasiveSpiClockDisable(uint index) => GateDisable(Indexed(0x104, index), 1);

    /// <summary>NID_C37CFDFC(index): gate register 0x110 + 4 * index, bit 0 on.</summary>
    [Hle(Nid = 0xC37CFDFC, Library = "ScePervasiveForDriver")]
    int NID_C37CFDFC(uint index) => GateEnable(Indexed(0x110, index), 1);

    /// <summary>NID_3F3E1AEA(index): gate register 0x110 + 4 * index, bit 0 off.</summary>
    [Hle(Nid = 0x3F3E1AEA, Library = "ScePervasiveForDriver")]
    int NID_3F3E1AEA(uint index) => GateDisable(Indexed(0x110, index), 1);

    /// <summary>kscePervasiveUartClockEnable(index): gate register 0x120 + 4 * index, bit 0 on.</summary>
    [Hle(Nid = 0xEFD084D8, Library = "ScePervasiveForDriver")]
    int kscePervasiveUartClockEnable(uint index) => GateEnable(Indexed(0x120, index), 1);

    /// <summary>kscePervasiveUartClockDisable(index): gate register 0x120 + 4 * index, bit 0 off.</summary>
    [Hle(Nid = 0x18DD8043, Library = "ScePervasiveForDriver")]
    int kscePervasiveUartClockDisable(uint index) => GateDisable(Indexed(0x120, index), 1);
}
