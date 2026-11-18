namespace VitaSharp.Core.Hle;

/// <summary>
/// The identity of the emulated unit as the boot loader hands it to the kernel (the KBL parameters, read by
/// SceSblAIMgr in SceSysmem 3.74): the product code block (pscode, +0xA0: company, product, sub code, factory code;
/// stored big-endian there) and the hardware info word (+0xD4). The emulated unit is a retail PS Vita handheld
/// (PCH-1000, CEX, North America), the one every SceSblAimgr query, the bridge and the registry manager report.
/// </summary>
public static class ConsoleIdentity
{
    /// <summary>The company code: 1, Sony Computer Entertainment.</summary>
    public const ushort CompanyCode = 1;

    /// <summary>The product code: 0x100 test, 0x101 tool, 0x102 DEX, 0x103..0x111 CEX by region (0x104 North
    /// America, the region the registry manager reports).</summary>
    public const ushort ProductCode = 0x104;

    /// <summary>The product sub code: 0x10, a PCH-1000 handheld (bit 0x200 marks a PS TV).</summary>
    public const ushort ProductSubCode = 0x10;

    /// <summary>The factory code: 0.</summary>
    public const ushort FactoryCode = 0;

    /// <summary>The hardware info word: bits 16..23 hold the board kind that makes a PS TV (0x51, 0x70, 0x72);
    /// the emulated handheld's word is 0 (the one ksceSysconGetHardwareInfo reports through the bridge).</summary>
    public const uint HardwareInfo = 0;

    /// <summary>The secure module information word (KBL parameters +8): 0.</summary>
    public const uint SecureModuleInfo = 0;
}
