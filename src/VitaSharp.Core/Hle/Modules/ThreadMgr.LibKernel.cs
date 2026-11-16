using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// Two small libkernel.suprx 3.74 exports (decompiled, study only): the TLS slot address of the calling thread checked
/// against a thread id, and the helper that turns a 0 count into 1.
/// </summary>
public sealed partial class ThreadMgr
{
    /// <summary>NID_35D20E49(thid, key): sceKernelGetTLSAddr(key) when <paramref name="thid"/> is 0 or the calling
    /// thread's id (the one in its TLS block, slot 1); 0xFFFFFFFF for another thread, NULL for a key of 0x100 or
    /// more.</summary>
    [Hle(Nid = 0x35D20E49, Library = "SceLibKernel", ReturnsValue = true, Cost = HleCost.UserInline)]
    uint NID_35D20E49(int thid, uint key)
    {
        if (key >= 0x100)
        {
            return 0;
        }

        uint slots = Self.TlsBase;
        if (thid != 0 && thid != GuestMemory.Read<int>(slots + 4))
        {
            return 0xFFFFFFFF;
        }

        return slots + key * 4;
    }

    /// <summary>NID_DC277B4D(unused, count): <paramref name="count"/>, or 1 for 0.</summary>
    [Hle(Nid = 0xDC277B4D, Library = "SceLibKernel", ReturnsValue = true, Cost = HleCost.UserInline)]
    uint NID_DC277B4D(uint unused, uint count) => count == 0 ? 1 : count;
}
