using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceMgVideo and SceMgVideoForMiniApp: the syscalls of os0:kd/mgvideo.skprx (FW 3.74), the player side of MagicGate
/// video (videos with MagicGate protection on the memory card, used by the Video app, the video profiler and the
/// database recovery plugin).
/// <para>
/// How the module works: every entry checks bit 0x81 of the caller's capability (SceSblACMgrForDriver_A27E47A7; others
/// get 0x80010058) and runs under the module's mutex. The open call (NID_C62CA5A7, its alias NID_E8617A7F and the
/// mini-app copy) takes three arguments that must all be 0 (else 0x8050C002), starts magicgate.skprx's MagicGate
/// emulation (keys from SceSblMgKeyMgr, the memory card's id from SceMsif, the license store in tm0:/drm/magicgate)
/// and returns a session handle (index | 0x10000). Every other call takes that handle; while no session is open they
/// answer 0x8050C011, except the two releases (NID_8A803FB4, NID_553C65CB) that answer 0. Several entries first copy
/// a parameter block from user memory, and that copy (and its size limits, 0x8002710B) is checked before the
/// capability.
/// </para>
/// <para>
/// VitaSharp does not run the MagicGate emulation (it needs the console's MagicGate keys and the memory card's
/// MagicGate identity, which an emulated unit does not have), so no session can open: the open answers as the module
/// does when MagicGate's storage cannot be set up (0x80505001, mapped by mgvideo to 0x8050C021) and the other entries
/// answer as with no session. These are marked Default in the coverage.
/// </para>
/// </summary>
public sealed partial class SystemServicesVsh
{
    internal const int MgVideoCapabilityBit = 0x81;
    internal const int MgVideoErrNotAllowed = unchecked((int)0x80010058);
    internal const int MgVideoErrInvalid = unchecked((int)0x8050C002);
    internal const int MgVideoErrNotOpen = unchecked((int)0x8050C011);
    internal const int MgVideoErrNoMagicGate = unchecked((int)0x8050C021);
    internal const int MgVideoErrTooLarge = unchecked((int)0x8002710B);

    private bool MgVideoAllowed => Emu.Access.HasCapabilityBit(MgVideoCapabilityBit);

    private int MgVideoSession() => MgVideoAllowed ? MgVideoErrNotOpen : MgVideoErrNotAllowed;

    /// <summary>The module's copy of a user buffer (ksceKernelMemcpyUserToKernel): NULL or memory the caller cannot
    /// read fails with the copy's error.</summary>
    private static bool MgVideoReadable(uint address, uint size) => KernelSpace.IsUserRange(address, size);

    /// <summary>An optional pointer of a parameter block: 0 is skipped, anything else must be readable.</summary>
    private static bool MgVideoOptional(uint address, uint size) => address == 0 || MgVideoReadable(address, size);

    /// <summary>NID_C62CA5A7 (a, b, c): opens a MagicGate video session; all three arguments must be 0.</summary>
    [Hle("NID_C62CA5A7", Nid = 0xC62CA5A7, Library = "SceMgVideo", Default = true)]
    [Hle("NID_E8617A7F", Nid = 0xE8617A7F, Library = "SceMgVideo", Default = true)]
    [Hle("NID_3EBD4502", Nid = 0x3EBD4502, Library = "SceMgVideoForMiniApp", Default = true)]
    private int MgVideoOpen(uint first, uint second, uint third)
    {
        if (!MgVideoAllowed)
        {
            return MgVideoErrNotAllowed;
        }

        if (first != 0 || second != 0 || third != 0)
        {
            return MgVideoErrInvalid;
        }

        return MgVideoErrNoMagicGate;
    }

    /// <summary>The entries that take the session handle directly: close (NID_D6EE7B53), the content key
    /// (NID_18D553EB, NID_22D74C37), the event flag wait/set (NID_4C014C4D, NID_8CDC8D33), the license state
    /// (NID_11FF1A1B), the stream calls (NID_CD12E1AC, NID_5A62101E, NID_F46B72E6, NID_A271BBED) and their aliases.</summary>
    [Hle("NID_D6EE7B53", Nid = 0xD6EE7B53, Library = "SceMgVideo", Default = true)]
    [Hle("NID_653809E0", Nid = 0x653809E0, Library = "SceMgVideo", Default = true)]
    [Hle("NID_02E5F373", Nid = 0x02E5F373, Library = "SceMgVideoForMiniApp", Default = true)]
    [Hle("NID_18D553EB", Nid = 0x18D553EB, Library = "SceMgVideo", Default = true)]
    [Hle("NID_5ADD5F9B", Nid = 0x5ADD5F9B, Library = "SceMgVideo", Default = true)]
    [Hle("NID_EA3A897A", Nid = 0xEA3A897A, Library = "SceMgVideoForMiniApp", Default = true)]
    [Hle("NID_22D74C37", Nid = 0x22D74C37, Library = "SceMgVideo", Default = true)]
    [Hle("NID_5B57B197", Nid = 0x5B57B197, Library = "SceMgVideo", Default = true)]
    [Hle("NID_CF0D2A06", Nid = 0xCF0D2A06, Library = "SceMgVideoForMiniApp", Default = true)]
    [Hle("NID_4C014C4D", Nid = 0x4C014C4D, Library = "SceMgVideo", Default = true)]
    [Hle("NID_8CDC8D33", Nid = 0x8CDC8D33, Library = "SceMgVideo", Default = true)]
    [Hle("NID_11FF1A1B", Nid = 0x11FF1A1B, Library = "SceMgVideo", Default = true)]
    [Hle("NID_CD12E1AC", Nid = 0xCD12E1AC, Library = "SceMgVideo", Default = true)]
    [Hle("NID_5A62101E", Nid = 0x5A62101E, Library = "SceMgVideo", Default = true)]
    [Hle("NID_F46B72E6", Nid = 0xF46B72E6, Library = "SceMgVideo", Default = true)]
    [Hle("NID_A271BBED", Nid = 0xA271BBED, Library = "SceMgVideo", Default = true)]
    private int MgVideoWithSession() => MgVideoSession();

    /// <summary>NID_8A803FB4 and NID_553C65CB (handle): release a stream or a key slot; with no session there is
    /// nothing to release and the module answers 0.</summary>
    [Hle("NID_8A803FB4", Nid = 0x8A803FB4, Library = "SceMgVideo", Default = true)]
    [Hle("NID_553C65CB", Nid = 0x553C65CB, Library = "SceMgVideo", Default = true)]
    private int MgVideoRelease() => MgVideoAllowed ? 0 : MgVideoErrNotAllowed;

    /// <summary>NID_BCC6E991 (handle, b, u64 *first, u64 *second): both optional 8-byte values are copied in.</summary>
    [Hle("NID_BCC6E991", Nid = 0xBCC6E991, Library = "SceMgVideo", Default = true)]
    private int MgVideoBCC6E991(uint handle, uint second, uint first8, uint second8)
    {
        if (!MgVideoOptional(first8, 8) || !MgVideoOptional(second8, 8))
        {
            return KernelSpace.UserCopyFault;
        }

        return MgVideoSession();
    }

    /// <summary>NID_E9367823 (handle, b, c, params): params is 0x10 bytes whose first three words are optional
    /// pointers to 8-byte values.</summary>
    [Hle("NID_E9367823", Nid = 0xE9367823, Library = "SceMgVideo", Default = true)]
    private int MgVideoE9367823(uint handle, uint second, uint third, uint parameters)
    {
        if (!MgVideoReadable(parameters, 0x10))
        {
            return KernelSpace.UserCopyFault;
        }

        for (uint i = 0; i < 3; i++)
        {
            if (!MgVideoOptional(GuestMemory.R32(parameters + i * 4), 8))
            {
                return KernelSpace.UserCopyFault;
            }
        }

        return MgVideoSession();
    }

    /// <summary>NID_3F3E3A8E (handle, data, c, u32 *size): the size word is copied in first; data (optional) is at
    /// most 0x400 bytes.</summary>
    [Hle("NID_3F3E3A8E", Nid = 0x3F3E3A8E, Library = "SceMgVideo", Default = true)]
    private int MgVideo3F3E3A8E(uint handle, uint data, uint third, uint sizeWord)
    {
        if (!MgVideoReadable(sizeWord, 8))
        {
            return KernelSpace.UserCopyFault;
        }

        if (data != 0)
        {
            uint size = GuestMemory.R32(sizeWord);
            if (size > 0x400)
            {
                return MgVideoErrTooLarge;
            }

            if (!MgVideoReadable(data, size))
            {
                return KernelSpace.UserCopyFault;
            }
        }

        return MgVideoSession();
    }

    /// <summary>NID_1F9409D1 and NID_A4B68884 (handle, b, c, params): params (0x10 and 0x18 bytes) carries at +8 an
    /// optional in/out word.</summary>
    [Hle("NID_1F9409D1", Nid = 0x1F9409D1, Library = "SceMgVideo", Default = true)]
    private int MgVideo1F9409D1(uint handle, uint second, uint third, uint parameters) => MgVideoInOutWord(parameters, 0x10);

    [Hle("NID_A4B68884", Nid = 0xA4B68884, Library = "SceMgVideo", Default = true)]
    private int MgVideoA4B68884(uint handle, uint second, uint third, uint parameters) => MgVideoInOutWord(parameters, 0x18);

    private int MgVideoInOutWord(uint parameters, uint blockSize)
    {
        if (!MgVideoReadable(parameters, blockSize) || !MgVideoOptional(GuestMemory.R32(parameters + 8), 4))
        {
            return KernelSpace.UserCopyFault;
        }

        return MgVideoSession();
    }

    /// <summary>NID_ECB4B61C (handle, b, c, params): a 0x10-byte parameter block.</summary>
    [Hle("NID_ECB4B61C", Nid = 0xECB4B61C, Library = "SceMgVideo", Default = true)]
    private int MgVideoECB4B61C(uint handle, uint second, uint third, uint parameters) =>
        MgVideoReadable(parameters, 0x10) ? MgVideoSession() : KernelSpace.UserCopyFault;

    /// <summary>NID_0F3F58E3 (handle, b, u64 *value, params): params is 0x18 bytes { data, ?, u64 *, size }; data
    /// (optional) is at most 0x400 bytes.</summary>
    [Hle("NID_0F3F58E3", Nid = 0x0F3F58E3, Library = "SceMgVideo", Default = true)]
    private int MgVideo0F3F58E3(uint handle, uint second, uint value, uint parameters)
    {
        if (!MgVideoReadable(parameters, 0x18) || !MgVideoOptional(value, 8))
        {
            return KernelSpace.UserCopyFault;
        }

        uint data = GuestMemory.R32(parameters);
        if (data != 0)
        {
            uint size = GuestMemory.R32(parameters + 0xC);
            if (size > 0x400)
            {
                return MgVideoErrTooLarge;
            }

            if (!MgVideoReadable(data, size))
            {
                return KernelSpace.UserCopyFault;
            }
        }

        return MgVideoOptional(GuestMemory.R32(parameters + 8), 8) ? MgVideoSession() : KernelSpace.UserCopyFault;
    }

    /// <summary>NID_0F6359B1 (handle, out, c, params): params is 0x10 bytes { u64 *, ?, size }; out (optional, at most
    /// 0x40 bytes) is cleared and written back whatever the result.</summary>
    [Hle("NID_0F6359B1", Nid = 0x0F6359B1, Library = "SceMgVideo", Default = true)]
    private int MgVideo0F6359B1(uint handle, uint output, uint third, uint parameters)
    {
        if (!MgVideoReadable(parameters, 0x10))
        {
            return KernelSpace.UserCopyFault;
        }

        uint size = GuestMemory.R32(parameters + 8);
        if (output != 0 && size > 0x40)
        {
            return MgVideoErrTooLarge;
        }

        if (!MgVideoOptional(GuestMemory.R32(parameters), 8))
        {
            return KernelSpace.UserCopyFault;
        }

        int result = MgVideoSession();
        if (output != 0)
        {
            if (!MgVideoReadable(output, size))
            {
                return KernelSpace.UserCopyFault;
            }

            GuestMemory.Fill(output, 0, size);
        }

        return result;
    }

    /// <summary>NID_FBF16BC5 (handle, u64 *value): the optional value is copied in.</summary>
    [Hle("NID_FBF16BC5", Nid = 0xFBF16BC5, Library = "SceMgVideo", Default = true)]
    private int MgVideoFBF16BC5(uint handle, uint value) => MgVideoOptional(value, 8) ? MgVideoSession() : KernelSpace.UserCopyFault;

    /// <summary>NID_ACD50568 (handle, u64 *value, out, params): params is 0x10 bytes { u64 *, 0x30-byte block *, ? };
    /// out (optional) receives 400 bytes, cleared, whatever the result.</summary>
    [Hle("NID_ACD50568", Nid = 0xACD50568, Library = "SceMgVideo", Default = true)]
    private int MgVideoACD50568(uint handle, uint value, uint output, uint parameters)
    {
        if (!MgVideoReadable(parameters, 0x10) || !MgVideoOptional(value, 8)
            || !MgVideoOptional(GuestMemory.R32(parameters), 8) || !MgVideoOptional(GuestMemory.R32(parameters + 4), 0x30))
        {
            return KernelSpace.UserCopyFault;
        }

        int result = MgVideoSession();
        if (output != 0)
        {
            if (!MgVideoReadable(output, 400))
            {
                return KernelSpace.UserCopyFault;
            }

            GuestMemory.Fill(output, 0, 400);
        }

        return result;
    }

    /// <summary>NID_74907E1E (handle, b, c, params): params is 0x18 bytes whose first four words are optional pointers
    /// to 8-byte values.</summary>
    [Hle("NID_74907E1E", Nid = 0x74907E1E, Library = "SceMgVideo", Default = true)]
    private int MgVideo74907E1E(uint handle, uint second, uint third, uint parameters) => MgVideoPointerBlock(parameters, 0x18, 4, 0);

    /// <summary>NID_03F7BC67 (handle, b, c, params): params is 0x20 bytes, five optional pointers to 8-byte values
    /// and one to a 0x10-byte value.</summary>
    [Hle("NID_03F7BC67", Nid = 0x03F7BC67, Library = "SceMgVideo", Default = true)]
    private int MgVideo03F7BC67(uint handle, uint second, uint third, uint parameters) => MgVideoPointerBlock(parameters, 0x20, 5, 0x10);

    private int MgVideoPointerBlock(uint parameters, uint blockSize, uint eightByteCount, uint lastSize)
    {
        if (!MgVideoReadable(parameters, blockSize))
        {
            return KernelSpace.UserCopyFault;
        }

        for (uint i = 0; i < eightByteCount; i++)
        {
            if (!MgVideoOptional(GuestMemory.R32(parameters + i * 4), 8))
            {
                return KernelSpace.UserCopyFault;
            }
        }

        if (lastSize != 0 && !MgVideoOptional(GuestMemory.R32(parameters + eightByteCount * 4), lastSize))
        {
            return KernelSpace.UserCopyFault;
        }

        return MgVideoSession();
    }
}
