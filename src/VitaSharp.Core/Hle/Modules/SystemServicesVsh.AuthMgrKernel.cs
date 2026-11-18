namespace VitaSharp.Core.Hle.Modules;

// SceSblAuthMgrForKernel / SceSblAuthMgrForDriver (os0:kd/authmgr.skprx 3.74, read in the module, study only): the
// kernel side of the secure processor's authenticator (the module manager authenticates SELF headers, segments and
// blocks through it; NpDrm decrypts bound data and keys with it). Every entry keeps the module's own checks (the one
// context id 1, NULL pointers, sizes and alignments) and then sends a command to the secure processor through
// SceSblSmSchedProxy; the emulated unit has no secure processor, so those commands answer the HLE's "the secure
// service is not available" (0x80020004), as defaults. The two entries the module answers by itself are real:
// CompareSwVersion (the firmware version a SELF needs) and NID_2A83A012.
public sealed partial class SystemServicesVsh
{
    /// <summary>SceSblAuthMgr: a NULL pointer, a size out of range or a misaligned buffer.</summary>
    const int AUTHMGR_ERR_INVALID_ARGUMENT = unchecked((int)0x800F0516);

    /// <summary>SceSblAuthMgr: a context id other than the one Open hands out (1).</summary>
    const int AUTHMGR_ERR_BAD_CONTEXT = unchecked((int)0x800F0509);

    /// <summary>SceSblAuthMgr: a SELF that needs a newer firmware than 3.74.</summary>
    const int AUTHMGR_ERR_NEWER_FIRMWARE = unchecked((int)0x800F0537);

    /// <summary>The only context id of the authenticator.</summary>
    const int AuthMgrContext = 1;

    /// <summary>The firmware version (3.74, the 0x03740000 form with the build bits cleared) SELFs are checked against.</summary>
    const uint AuthMgrFirmwareVersion = 0x03740000;

    /// <summary>ksceSblAuthMgrOpen (0xA9CD2A09)(int *context): NULL is 0x800F0516; otherwise the module starts the
    /// authenticator in the secure processor, which the emulated unit does not have.</summary>
    [Hle(Nid = 0xA9CD2A09, Library = "SceSblAuthMgrForKernel", Default = true)]
    int ksceSblAuthMgrOpen(uint context)
    {
        if (context == 0)
        {
            return AUTHMGR_ERR_INVALID_ARGUMENT;
        }

        return KERNEL_ERR_UNSUP;
    }

    /// <summary>ksceSblAuthMgrClose (0x026ACBAD)(int context): a context other than 1 is 0x800F0509; closing ends the
    /// secure processor's session, which never started here: 0.</summary>
    [Hle(Nid = 0x026ACBAD, Library = "SceSblAuthMgrForKernel")]
    int ksceSblAuthMgrClose(int context)
    {
        return context == AuthMgrContext ? 0 : AUTHMGR_ERR_BAD_CONTEXT;
    }

    /// <summary>ksceSblAuthMgrAuthHeader (0xF3411881)(int context, const void *header, SceSize size,
    /// SceSblSmCommContext130 *info): the secure processor checks a SELF header. Context 1 only (0x800F0509), header
    /// and info needed (0x800F0516).</summary>
    [Hle(Nid = 0xF3411881, Library = "SceSblAuthMgrForKernel", Default = true)]
    int ksceSblAuthMgrAuthHeader(int context, uint header, uint size, uint info)
    {
        if (context != AuthMgrContext)
        {
            return AUTHMGR_ERR_BAD_CONTEXT;
        }

        if (header == 0 || info == 0)
        {
            return AUTHMGR_ERR_INVALID_ARGUMENT;
        }

        return KERNEL_ERR_UNSUP;
    }

    /// <summary>ksceSblAuthMgrSetupAuthSegment (0x89CCDA2C)(int context, int segmentIndex): the secure processor
    /// prepares the key of a segment of the authenticated SELF. Context 1 only (0x800F0509).</summary>
    [Hle(Nid = 0x89CCDA2C, Library = "SceSblAuthMgrForKernel", Default = true)]
    int ksceSblAuthMgrSetupAuthSegment(int context, int segmentIndex)
    {
        return context == AuthMgrContext ? KERNEL_ERR_UNSUP : AUTHMGR_ERR_BAD_CONTEXT;
    }

    /// <summary>ksceSblAuthMgrLoadBlock (0xBC422443)(int context, void *buffer, SceSize size): the secure processor
    /// decrypts a block of the segment in place. Context 1 only (0x800F0509); a NULL or 32-byte misaligned buffer or
    /// a size of 0 is 0x800F0516.</summary>
    [Hle(Nid = 0xBC422443, Library = "SceSblAuthMgrForKernel", Default = true)]
    int ksceSblAuthMgrLoadBlock(int context, uint buffer, uint size)
    {
        if (context != AuthMgrContext)
        {
            return AUTHMGR_ERR_BAD_CONTEXT;
        }

        if (buffer == 0 || size == 0 || (buffer & 0x1F) != 0)
        {
            return AUTHMGR_ERR_INVALID_ARGUMENT;
        }

        return KERNEL_ERR_UNSUP;
    }

    /// <summary>ksceSblAuthMgrCompareSwVersion (0xABAB8466)(uint version): 0 when a SELF of that firmware version
    /// runs on 3.74 (-1, no requirement, too), 0x800F0537 for a newer one; the low 12 bits (the build) do not
    /// count.</summary>
    [Hle(Nid = 0xABAB8466, Library = "SceSblAuthMgrForKernel")]
    int ksceSblAuthMgrCompareSwVersion(uint version)
    {
        if (version == uint.MaxValue)
        {
            return 0;
        }

        return (version & 0xFFFFF000) <= AuthMgrFirmwareVersion ? 0 : AUTHMGR_ERR_NEWER_FIRMWARE;
    }

    /// <summary>NID_2A83A012 (SceSblAuthMgrForKernel)(): the module answers 0 and does nothing else.</summary>
    [Hle("NID_2A83A012", Nid = 0x2A83A012, Library = "SceSblAuthMgrForKernel")]
    int AuthMgrNid2A83A012()
    {
        return 0;
    }

    /// <summary>ksceSblAuthMgrSetDmac5Key (0x122ACDEA)(const void *key, SceSize size, int slot, int mode): the
    /// secure processor loads a key into a DMAC5 slot. The key is needed, the size a multiple of 16 from 16 to 32, the
    /// mode 0, 0x10000, 0x10001, 0x20000 or 0x20001 (else 0x800F0516).</summary>
    [Hle(Nid = 0x122ACDEA, Library = "SceSblAuthMgrForKernel", Default = true)]
    int ksceSblAuthMgrSetDmac5Key(uint key, uint size, int slot, int mode)
    {
        bool knownMode = mode is 0 or 0x10000 or 0x10001 or 0x20000 or 0x20001;
        if (key == 0 || !knownMode || (size & 0xF) != 0 || size - 1 >= 0x20)
        {
            return AUTHMGR_ERR_INVALID_ARGUMENT;
        }

        return KERNEL_ERR_UNSUP;
    }

    /// <summary>ksceSblAuthMgrClearDmac5Key (0xF2BB723E)(int slot, int value): the secure processor clears a DMAC5
    /// slot; the second argument must be 0 (0x800F0516).</summary>
    [Hle(Nid = 0xF2BB723E, Library = "SceSblAuthMgrForKernel", Default = true)]
    int ksceSblAuthMgrClearDmac5Key(int slot, int value)
    {
        return value != 0 ? AUTHMGR_ERR_INVALID_ARGUMENT : KERNEL_ERR_UNSUP;
    }

    /// <summary>ksceSblAuthMgrGetEKc (0x868B9E9A)(void *data, SceSize size, int type): the secure processor derives
    /// a content key in place. The data is needed, the size a multiple of 16 from 16 to 0x100, the type 0..2 (else
    /// 0x800F0516).</summary>
    [Hle(Nid = 0x868B9E9A, Library = "SceSblAuthMgrForDriver", Default = true)]
    int ksceSblAuthMgrGetEKc(uint data, uint size, uint type)
    {
        if (data == 0 || type >= 3 || (size & 0xF) != 0 || size - 1 >= 0x100)
        {
            return AUTHMGR_ERR_INVALID_ARGUMENT;
        }

        return KERNEL_ERR_UNSUP;
    }

    /// <summary>ksceSblAuthMgrDecBindData (0x41DAEA12)(void *data, SceSize dataSize, const void *bind,
    /// SceSize bindSize, int zero): the secure processor decrypts data bound to the unit (NpDrm's license keys).
    /// Both buffers are needed and 64-byte aligned, the sizes multiples of 16 (the data up to 0x1000, the bind data
    /// 16..0x1000), the last argument 0 (else 0x800F0516).</summary>
    [Hle(Nid = 0x41DAEA12, Library = "SceSblAuthMgrForDriver", Default = true)]
    int ksceSblAuthMgrDecBindData(uint data, uint dataSize, uint bind, uint bindSize, int zero)
    {
        bool aligned = ((data | bind) & 0x3F) == 0 && ((bindSize | dataSize) & 0xF) == 0;
        if (data == 0 || bind == 0 || bindSize - 1 > 0xFFF || zero != 0 || !aligned || dataSize > 0x1000)
        {
            return AUTHMGR_ERR_INVALID_ARGUMENT;
        }

        return KERNEL_ERR_UNSUP;
    }

    /// <summary>ksceSblAuthMgrVerifySpfsoCtx (0x24C4CE64)(SceSblAuthMgrSpfsoCtx *context): the secure processor
    /// verifies a signed file (its buffer at +4, its size at +8). The context and a 32-byte aligned buffer of at most
    /// 0x8000 bytes are needed (else 0x800F0516).</summary>
    [Hle(Nid = 0x24C4CE64, Library = "SceSblAuthMgrForDriver", Default = true)]
    int ksceSblAuthMgrVerifySpfsoCtx(uint context)
    {
        if (context == 0)
        {
            return AUTHMGR_ERR_INVALID_ARGUMENT;
        }

        uint buffer = Memory.GuestMemory.R32(context + 4);
        uint size = Memory.GuestMemory.R32(context + 8);
        if (buffer == 0 || size > 0x8000 || (buffer & 0x1F) != 0)
        {
            return AUTHMGR_ERR_INVALID_ARGUMENT;
        }

        return KERNEL_ERR_UNSUP;
    }
}
