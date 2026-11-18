namespace VitaSharp.Core.Hle.Modules;

// SceQafMgrForDriver (sysmem 3.74, read in the module, study only): every entry answers one bit of the unit's QA
// flags, the 16 bytes the boot loader hands the kernel (KBL parameters +0x20..+0x2F, the same flags
// ksceSblQafManagerGetQAFlags reports). A retail unit has no QA flags: every query answers 0. The unnamed entries
// keep the NID as their name; several of them read the same bit as a named one, as in the module.
public sealed partial class SystemServicesVsh
{
    /// <summary>Bit <paramref name="bit"/> of byte <paramref name="index"/> of the QA flags.</summary>
    static int QaFlag(int index, int bit)
    {
        return (QaFlags[index] >> bit) & 1;
    }

    /// <summary>NID_382C71E8 (0x382C71E8)(): QA flag byte 0xD, bit 0.</summary>
    [Hle("NID_382C71E8", Nid = 0x382C71E8, Library = "SceQafMgrForDriver")]
    int QafMgrFlag382C71E8()
    {
        return QaFlag(0xD, 0);
    }

    /// <summary>ksceSblQafMgrIsAllowSystemAppDebug (0xCAD47130)(): QA flag byte 0xD, bit 1.</summary>
    [Hle(Nid = 0xCAD47130, Library = "SceQafMgrForDriver")]
    int ksceSblQafMgrIsAllowSystemAppDebug()
    {
        return QaFlag(0xD, 1);
    }

    /// <summary>NID_694D1096 (0x694D1096)(): QA flag byte 0xB, bit 2.</summary>
    [Hle("NID_694D1096", Nid = 0x694D1096, Library = "SceQafMgrForDriver")]
    int QafMgrFlag694D1096()
    {
        return QaFlag(0xB, 2);
    }

    /// <summary>NID_41E04800 (0x41E04800)(): QA flag byte 0xB, bit 2.</summary>
    [Hle("NID_41E04800", Nid = 0x41E04800, Library = "SceQafMgrForDriver")]
    int QafMgrFlag41E04800()
    {
        return QaFlag(0xB, 2);
    }

    /// <summary>ksceSblQafMgrIsAllowLoadMagicGate (0x36E5312E)(): QA flag byte 0xB, bit 4.</summary>
    [Hle(Nid = 0x36E5312E, Library = "SceQafMgrForDriver")]
    int ksceSblQafMgrIsAllowLoadMagicGate()
    {
        return QaFlag(0xB, 4);
    }

    /// <summary>NID_3CB55F98 (0x3CB55F98)(): QA flag byte 0xF, bit 0.</summary>
    [Hle("NID_3CB55F98", Nid = 0x3CB55F98, Library = "SceQafMgrForDriver")]
    int QafMgrFlag3CB55F98()
    {
        return QaFlag(0xF, 0);
    }

    /// <summary>NID_8C423C18 (0x8C423C18)(): QA flag byte 0xF, bit 1.</summary>
    [Hle("NID_8C423C18", Nid = 0x8C423C18, Library = "SceQafMgrForDriver")]
    int QafMgrFlag8C423C18()
    {
        return QaFlag(0xF, 1);
    }

    /// <summary>NID_0E588747 (0x0E588747)(): QA flag byte 0xF, bit 0.</summary>
    [Hle("NID_0E588747", Nid = 0x0E588747, Library = "SceQafMgrForDriver")]
    int QafMgrFlag0E588747()
    {
        return QaFlag(0xF, 0);
    }

    /// <summary>ksceSblQafMgrIsAllowHost0Access (0x082A4FC2)(): QA flag byte 0xE, bit 0.</summary>
    [Hle(Nid = 0x082A4FC2, Library = "SceQafMgrForDriver")]
    int ksceSblQafMgrIsAllowHost0Access()
    {
        return QaFlag(0xE, 0);
    }

    /// <summary>NID_B9770A13 (0xB9770A13)(): QA flag byte 0xD, bit 1.</summary>
    [Hle("NID_B9770A13", Nid = 0xB9770A13, Library = "SceQafMgrForDriver")]
    int QafMgrFlagB9770A13()
    {
        return QaFlag(0xD, 1);
    }

    /// <summary>ksceSblQafMgrIsAllowDecryptedBootConfigLoad (0x883E9465)(): QA flag byte 0xE, bit 0.</summary>
    [Hle(Nid = 0x883E9465, Library = "SceQafMgrForDriver")]
    int ksceSblQafMgrIsAllowDecryptedBootConfigLoad()
    {
        return QaFlag(0xE, 0);
    }

    /// <summary>NID_52B4E164 (0x52B4E164)(): QA flag byte 0xD, bit 0.</summary>
    [Hle("NID_52B4E164", Nid = 0x52B4E164, Library = "SceQafMgrForDriver")]
    int QafMgrFlag52B4E164()
    {
        return QaFlag(0xD, 0);
    }

    /// <summary>ksceSblQafMgrIsAllowRemotePlayDebug (0xBFD5E463)(): QA flag byte 0xC, bit 1.</summary>
    [Hle(Nid = 0xBFD5E463, Library = "SceQafMgrForDriver")]
    int ksceSblQafMgrIsAllowRemotePlayDebug()
    {
        return QaFlag(0xC, 1);
    }

    /// <summary>NID_4BC1883F (0x4BC1883F)(): QA flag byte 0x6, bit 1.</summary>
    [Hle("NID_4BC1883F", Nid = 0x4BC1883F, Library = "SceQafMgrForDriver")]
    int QafMgrFlag4BC1883F()
    {
        return QaFlag(0x6, 1);
    }

    /// <summary>ksceSblQafMgrIsAllowPSPEmuShowQAInfo (0xB7B195B2)(): QA flag byte 0xF, bit 0.</summary>
    [Hle(Nid = 0xB7B195B2, Library = "SceQafMgrForDriver")]
    int ksceSblQafMgrIsAllowPSPEmuShowQAInfo()
    {
        return QaFlag(0xF, 0);
    }

    /// <summary>ksceSblQafMgrIsAllowNearTest (0x9644171D)(): QA flag byte 0x6, bit 1.</summary>
    [Hle(Nid = 0x9644171D, Library = "SceQafMgrForDriver")]
    int ksceSblQafMgrIsAllowNearTest()
    {
        return QaFlag(0x6, 1);
    }

    /// <summary>NID_E573F124 (0xE573F124)(): QA flag byte 0x0, bit 4.</summary>
    [Hle("NID_E573F124", Nid = 0xE573F124, Library = "SceQafMgrForDriver")]
    int QafMgrFlagE573F124()
    {
        return QaFlag(0x0, 4);
    }

    /// <summary>NID_7B14DC45 (0x7B14DC45)(): QA flag byte 0xD, bit 1.</summary>
    [Hle("NID_7B14DC45", Nid = 0x7B14DC45, Library = "SceQafMgrForDriver")]
    int QafMgrFlag7B14DC45()
    {
        return QaFlag(0xD, 1);
    }

    /// <summary>NID_AE033133 (0xAE033133)(): QA flag byte 0x0, bit 4.</summary>
    [Hle("NID_AE033133", Nid = 0xAE033133, Library = "SceQafMgrForDriver")]
    int QafMgrFlagAE033133()
    {
        return QaFlag(0x0, 4);
    }

    /// <summary>ksceSblQafMgrIsAllowMarlinTest (0x10283EB8)(): QA flag byte 0xD, bit 1.</summary>
    [Hle(Nid = 0x10283EB8, Library = "SceQafMgrForDriver")]
    int ksceSblQafMgrIsAllowMarlinTest()
    {
        return QaFlag(0xD, 1);
    }

    /// <summary>NID_70A67A4B (0x70A67A4B)(): QA flag byte 0xF, bit 0.</summary>
    [Hle("NID_70A67A4B", Nid = 0x70A67A4B, Library = "SceQafMgrForDriver")]
    int QafMgrFlag70A67A4B()
    {
        return QaFlag(0xF, 0);
    }

    /// <summary>ksceSblQafMgrIsAllowKeepCoreFile (0xC1EA75C8)(): QA flag byte 0xF, bit 0.</summary>
    [Hle(Nid = 0xC1EA75C8, Library = "SceQafMgrForDriver")]
    int ksceSblQafMgrIsAllowKeepCoreFile()
    {
        return QaFlag(0xF, 0);
    }

    /// <summary>ksceSblQafMgrIsAllowDtcpIpReset (0xE8B8F31F)(): QA flag byte 0x6, bit 1.</summary>
    [Hle(Nid = 0xE8B8F31F, Library = "SceQafMgrForDriver")]
    int ksceSblQafMgrIsAllowDtcpIpReset()
    {
        return QaFlag(0x6, 1);
    }

    /// <summary>NID_DEC6DF4E (0xDEC6DF4E)(): QA flag byte 0xF, bit 0.</summary>
    [Hle("NID_DEC6DF4E", Nid = 0xDEC6DF4E, Library = "SceQafMgrForDriver")]
    int QafMgrFlagDEC6DF4E()
    {
        return QaFlag(0xF, 0);
    }

    /// <summary>ksceSblQafMgrIsAllowControlIduAutoUpdate (0xF8BFEE48)(): QA flag byte 0x6, bit 1.</summary>
    [Hle(Nid = 0xF8BFEE48, Library = "SceQafMgrForDriver")]
    int ksceSblQafMgrIsAllowControlIduAutoUpdate()
    {
        return QaFlag(0x6, 1);
    }
}
