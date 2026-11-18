using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// SceSblGcAuthMgrMsSaveBBForDriver (os0:kd/gcauthmgr.skprx 3.74, read in the module, study only): the PSP save data
// BBMac/BBCipher of the PSP compatibility layer. The two context set-ups run in the module itself: the MAC context
// (0x28 bytes: +0 the MAC type, +4 and +0x14 16 zero bytes each, +0x24 0) and the cipher context's clearing at the end
// (0x18 bytes: +0, +4 and the 16-byte key zeroed); NULL is 0x808A0003. The user syscalls (_sceSblGcAuthMgrMsSaveBBMacInit,
// _sceSblGcAuthMgrMsSaveBBCipherFinal, with no permission check) copy the context in, run the same set-up and copy it
// out, and libkernel's forms call them. The MAC and cipher work itself (the updates, the final MAC, the cipher's key
// set-up) runs in the secure processor with its keys: 0x80020004.
public sealed partial class SystemServicesVsh
{
    /// <summary>SceSblGcAuthMgr: a NULL context (0x808A0003).</summary>
    const int GCAUTH_ERR_INVALID_ARGUMENT = unchecked((int)0x808A0003);

    const uint MsSaveBBMacContextSize = 0x28, MsSaveBBCipherContextSize = 0x18;

    /// <summary>NID_4A249828 (SceSblGcAuthMgrMsSaveBBForDriver)(ctx, int type): the MAC context set up for a type.</summary>
    [Hle("NID_4A249828", Nid = 0x4A249828, Library = "SceSblGcAuthMgrMsSaveBBForDriver")]
    [Hle("_sceSblGcAuthMgrMsSaveBBMacInit", Nid = 0x3A92780D, Library = "SceSblGcAuthMgr")]
    [Hle("sceSblGcAuthMgrMsSaveBBMacInit", Nid = 0x1693032E, Library = "SceLibKernel")]
    int MsSaveBBMacInit(uint context, int type)
    {
        if (context == 0)
        {
            return GCAUTH_ERR_INVALID_ARGUMENT;
        }

        GuestMemory.Fill(context, 0, MsSaveBBMacContextSize);
        GuestMemory.W32(context, (uint)type);
        return 0;
    }

    /// <summary>NID_805C860B (SceSblGcAuthMgrMsSaveBBForDriver)(ctx): the cipher context cleared at the end.</summary>
    [Hle("NID_805C860B", Nid = 0x805C860B, Library = "SceSblGcAuthMgrMsSaveBBForDriver")]
    [Hle("_sceSblGcAuthMgrMsSaveBBCipherFinal", Nid = 0x18EAAD73, Library = "SceSblGcAuthMgr")]
    [Hle("sceSblGcAuthMgrMsSaveBBCipherFinal", Nid = 0xEB02F15D, Library = "SceLibKernel")]
    int MsSaveBBCipherFinal(uint context)
    {
        if (context == 0)
        {
            return GCAUTH_ERR_INVALID_ARGUMENT;
        }

        GuestMemory.Fill(context, 0, MsSaveBBCipherContextSize);
        return 0;
    }

    /// <summary>NID_3C185A67 / 79A9BE44 / 8E6626A0 / F66D5F76 (SceSblGcAuthMgrMsSaveBBForDriver): the cipher's key
    /// set-up, the MAC and cipher updates and the final MAC, run on the module's large stack against the secure
    /// processor: 0x80020004.</summary>
    [Hle("NID_3C185A67", Nid = 0x3C185A67, Library = "SceSblGcAuthMgrMsSaveBBForDriver", Default = true)]
    [Hle("NID_79A9BE44", Nid = 0x79A9BE44, Library = "SceSblGcAuthMgrMsSaveBBForDriver", Default = true)]
    [Hle("NID_8E6626A0", Nid = 0x8E6626A0, Library = "SceSblGcAuthMgrMsSaveBBForDriver", Default = true)]
    [Hle("NID_F66D5F76", Nid = 0xF66D5F76, Library = "SceSblGcAuthMgrMsSaveBBForDriver", Default = true)]
    int MsSaveBBSecure()
    {
        return KERNEL_ERR_UNSUP;
    }
}
