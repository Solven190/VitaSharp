namespace VitaSharp.Core.Hle.Modules;

// SceRegMgrForDebugger and SceRegMgrForTool (os0 bootimage SceRegistryMgr 3.74, read in the module, study only): the
// development-environment side of the registry (the /DEVENV/TOOL keys, the tool setting defaults of vs0:, the
// registry dumps and their serials). At start the module derives its environment mode from the product code: the test
// and tool units (0x100..0x102) get mode 2 or 3, every retail unit (0x103..0x10D) mode 1. Each SceRegMgrForDebugger
// entry, after taking the registry lock, refuses with 0x800D001E while bit 0 of that mode is set; each SceRegMgrForTool
// entry refuses with the same code unless bit 1 is set or the unit loaded the internal tool settings (the two that change
// the mode refuse while bit 0 is set). The refusal comes before any argument is read. The emulated unit is a retail one
// (ConsoleIdentity), so every entry gives 0x800D001E as on a retail Vita.
public sealed partial class SystemServicesVsh
{
    /// <summary>SceRegMgr: the entry belongs to a development environment this unit is not (0x800D001E).</summary>
    const int REGMGR_ERR_NOT_DEVENV = unchecked((int)0x800D001E);

    /// <summary>The environment mode SceRegistryMgr derives from the product code: 1 on a retail unit.</summary>
    static int RegMgrEnvironmentMode()
    {
        bool testOrTool = ConsoleIdentity.ProductCode >= 0x100 && ConsoleIdentity.ProductCode <= 0x102;
        return testOrTool ? 2 : 1;
    }

    [Hle("NID_810D2F09", Nid = 0x810D2F09, Library = "SceRegMgrForDebugger")]
    [Hle("NID_C64C67A8", Nid = 0xC64C67A8, Library = "SceRegMgrForDebugger")]
    [Hle("NID_8E798E47", Nid = 0x8E798E47, Library = "SceRegMgrForDebugger")]
    [Hle("NID_B9A0BB77", Nid = 0xB9A0BB77, Library = "SceRegMgrForDebugger")]
    [Hle("NID_C588D3E7", Nid = 0xC588D3E7, Library = "SceRegMgrForDebugger")]
    [Hle("NID_D668ABD2", Nid = 0xD668ABD2, Library = "SceRegMgrForDebugger")]
    [Hle("NID_8AE5757E", Nid = 0x8AE5757E, Library = "SceRegMgrForDebugger")]
    [Hle("NID_46AC0CCB", Nid = 0x46AC0CCB, Library = "SceRegMgrForDebugger")]
    [Hle("NID_53970232", Nid = 0x53970232, Library = "SceRegMgrForDebugger")]
    int RegMgrDebuggerEntry()
    {
        if ((RegMgrEnvironmentMode() & 1) != 0)
        {
            return REGMGR_ERR_NOT_DEVENV;
        }

        return 0;
    }

    [Hle("NID_E6E81558", Nid = 0xE6E81558, Library = "SceRegMgrForTool")]
    [Hle("NID_0CFA929B", Nid = 0x0CFA929B, Library = "SceRegMgrForTool")]
    int RegMgrToolModeEntry()
    {
        if ((RegMgrEnvironmentMode() & 1) != 0)
        {
            return REGMGR_ERR_NOT_DEVENV;
        }

        return 0;
    }

    [Hle("NID_6808B7AD", Nid = 0x6808B7AD, Library = "SceRegMgrForTool")]
    [Hle("NID_0B623D0B", Nid = 0x0B623D0B, Library = "SceRegMgrForTool")]
    [Hle("NID_2A76051A", Nid = 0x2A76051A, Library = "SceRegMgrForTool")]
    [Hle("NID_B441660B", Nid = 0xB441660B, Library = "SceRegMgrForTool")]
    [Hle("NID_F55E6E4F", Nid = 0xF55E6E4F, Library = "SceRegMgrForTool")]
    [Hle("NID_3AAB71EF", Nid = 0x3AAB71EF, Library = "SceRegMgrForTool")]
    [Hle("NID_7B30AC2C", Nid = 0x7B30AC2C, Library = "SceRegMgrForTool")]
    [Hle("NID_6276E7D8", Nid = 0x6276E7D8, Library = "SceRegMgrForTool")]
    [Hle("NID_C689E36C", Nid = 0xC689E36C, Library = "SceRegMgrForTool")]
    [Hle("NID_2D415472", Nid = 0x2D415472, Library = "SceRegMgrForTool")]
    [Hle("NID_4EDF87F9", Nid = 0x4EDF87F9, Library = "SceRegMgrForTool")]
    int RegMgrToolEntry()
    {
        // The internal tool settings (vs0:tool setting defaults "for internal") are never loaded on a retail unit.
        if ((RegMgrEnvironmentMode() & 2) == 0)
        {
            return REGMGR_ERR_NOT_DEVENV;
        }

        return 0;
    }
}
