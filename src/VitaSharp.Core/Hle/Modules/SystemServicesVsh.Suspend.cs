using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceKernelSuspendForDriver (sysmem 3.74, read in the module, study only): the kernel's suspend framework. The power
// service registers an operations table (SetOps); until it does, the power lock is a counter in sysmem and the
// other operations answer 0 (or 0x80020004 for the four that need the power service). With a table, each entry
// calls the table's function at its offset. Two lists of 32 suspend/resume handlers (function and argument) are
// called in order (suspend: first to last; resume: last to first). Debug hooks are called around the power
// lock and unlock and before the two notification entries.
public sealed partial class SystemServicesVsh
{
    /// <summary>The number of handlers in each of the two lists.</summary>
    const int SuspendHandlerCount = 0x20;

    [SnapshotMissing] // states of builds before it: no table was ever registered, no lock taken
    uint _suspendOps;

    [SnapshotMissing] // states of builds before it: the power lock count was not kept
    int _suspendLockCount;

    /// <summary>The debug hooks, by slot: before the lock, after the lock, after the unlock, the two notifications,
    /// and the two the module keeps for its other entries.</summary>
    const int HookLock = 0, HookLockDone = 1, HookUnlock = 2, HookNotify = 3, HookNotify2 = 4, HookD612 = 5, HookB5C5 = 6;

    [SnapshotMissing] // states of builds before it: no hook was ever set
    readonly uint[] _suspendHooks = new uint[7];

    [SnapshotMissing] // states of builds before it: no handler was ever registered
    readonly uint[] _suspendHandlers = new uint[SuspendHandlerCount * 2], _resumeHandlers = new uint[SuspendHandlerCount * 2];

    /// <summary>Calls a guest function (when it is set and a guest thread runs the call); 0 otherwise.</summary>
    static int SuspendCall(uint function, params ReadOnlySpan<uint> args)
    {
        GuestThread? thread = GuestThread.Current;
        if (function == 0 || thread == null)
        {
            return 0;
        }

        return (int)thread.CallGuest(function, args);
    }

    /// <summary>The operations table's function at <paramref name="offset"/>, or 0 without a table.</summary>
    uint SuspendOp(uint offset)
    {
        return _suspendOps == 0 ? 0 : GuestMemory.R32(_suspendOps + offset);
    }

    /// <summary>NID_250ACD90 (SceKernelSuspendForDriver)(ops): registers the power service's operations table;
    /// returns the power lock count taken before it.</summary>
    [Hle("NID_250ACD90", Nid = 0x250ACD90, Library = "SceKernelSuspendForDriver")]
    int SuspendSetOps(uint ops)
    {
        _suspendOps = ops;
        return _suspendLockCount;
    }

    /// <summary>NID_4DF40893 (SceKernelSuspendForDriver)(int type): takes the power lock (the table's +4, or the
    /// sysmem counter up to 0x7FFFFFFF), between the lock hooks.</summary>
    [Hle("NID_4DF40893", Nid = 0x4DF40893, Library = "SceKernelSuspendForDriver")]
    int SuspendPowerLock(uint type)
    {
        SuspendCall(_suspendHooks[HookLock], type, _suspendOps, SuspendOp(4));
        if (_suspendOps != 0)
        {
            int result = SuspendCall(SuspendOp(4), type);
            SuspendCall(_suspendHooks[HookLockDone], _suspendOps, SuspendOp(4), (uint)result, 0);
            return result;
        }

        if (_suspendLockCount < int.MaxValue)
        {
            _suspendLockCount++;
        }

        return 0;
    }

    /// <summary>NID_2BB92967 (SceKernelSuspendForDriver)(int type): releases the power lock (the table's +8, or the
    /// sysmem counter), then the unlock hook.</summary>
    [Hle("NID_2BB92967", Nid = 0x2BB92967, Library = "SceKernelSuspendForDriver")]
    int SuspendPowerUnlock(uint type)
    {
        if (_suspendOps == 0)
        {
            if (_suspendLockCount > 0)
            {
                _suspendLockCount--;
            }

            SuspendCall(_suspendHooks[HookUnlock], type, 0, 0, (uint)_suspendLockCount);
            return 0;
        }

        int result = SuspendCall(SuspendOp(8), type);
        SuspendCall(_suspendHooks[HookUnlock], type, _suspendOps, (uint)result, 0);
        return result;
    }

    /// <summary>The table entries that answer 0 without a table: +0xC, +0x10, +0x2C, +0x30, +0x34.</summary>
    [Hle("NID_254525F8", Nid = 0x254525F8, Library = "SceKernelSuspendForDriver")]
    int SuspendOp0C() => SuspendCall(SuspendOp(0xC));

    [Hle("NID_230495ED", Nid = 0x230495ED, Library = "SceKernelSuspendForDriver")]
    int SuspendOp10() => SuspendCall(SuspendOp(0x10));

    [Hle("NID_E677B343", Nid = 0xE677B343, Library = "SceKernelSuspendForDriver")]
    int SuspendOp2C() => SuspendCall(SuspendOp(0x2C));

    [Hle("NID_81D9E41C", Nid = 0x81D9E41C, Library = "SceKernelSuspendForDriver")]
    int SuspendOp30() => SuspendCall(SuspendOp(0x30));

    [Hle("NID_B4B13615", Nid = 0xB4B13615, Library = "SceKernelSuspendForDriver")]
    int SuspendOp34() => SuspendCall(SuspendOp(0x34));

    /// <summary>The table entries that need the power service (0x80020004 without a table): +0x14, +0x18, +0x1C,
    /// +0x20.</summary>
    int SuspendServiceOp(uint offset)
    {
        return _suspendOps == 0 ? KERNEL_ERR_UNSUP : SuspendCall(SuspendOp(offset));
    }

    [Hle("NID_0A6CA124", Nid = 0x0A6CA124, Library = "SceKernelSuspendForDriver")]
    int SuspendOp14() => SuspendServiceOp(0x14);

    [Hle("NID_0106C0F0", Nid = 0x0106C0F0, Library = "SceKernelSuspendForDriver")]
    int SuspendOp18() => SuspendServiceOp(0x18);

    [Hle("NID_AEA9440D", Nid = 0xAEA9440D, Library = "SceKernelSuspendForDriver")]
    int SuspendOp1C() => SuspendServiceOp(0x1C);

    [Hle("NID_6A503956", Nid = 0x6A503956, Library = "SceKernelSuspendForDriver")]
    int SuspendOp20() => SuspendServiceOp(0x20);

    /// <summary>NID_1FA2F8F1 (SceKernelSuspendForDriver)(a, b): the notification hook, then the table's +0x28(a, b)
    /// (0 without a table).</summary>
    [Hle("NID_1FA2F8F1", Nid = 0x1FA2F8F1, Library = "SceKernelSuspendForDriver")]
    int SuspendNotify(uint a, uint b)
    {
        SuspendCall(_suspendHooks[HookNotify2], a, b);
        return SuspendCall(SuspendOp(0x28), a, b);
    }

    // ---- the hook setters (each stores one debug hook)
    [Hle("NID_4E5A3A23", Nid = 0x4E5A3A23, Library = "SceKernelSuspendForDriver")]
    void SuspendSetLockHook(uint hook) => _suspendHooks[HookLock] = hook;

    [Hle("NID_C00826AC", Nid = 0xC00826AC, Library = "SceKernelSuspendForDriver")]
    void SuspendSetLockDoneHook(uint hook) => _suspendHooks[HookLockDone] = hook;

    [Hle("NID_0DE3CC02", Nid = 0x0DE3CC02, Library = "SceKernelSuspendForDriver")]
    void SuspendSetUnlockHook(uint hook) => _suspendHooks[HookUnlock] = hook;

    [Hle("NID_F2B07167", Nid = 0xF2B07167, Library = "SceKernelSuspendForDriver")]
    void SuspendSetNotifyHook(uint hook) => _suspendHooks[HookNotify] = hook;

    [Hle("NID_8B3F02B8", Nid = 0x8B3F02B8, Library = "SceKernelSuspendForDriver")]
    void SuspendSetNotify2Hook(uint hook) => _suspendHooks[HookNotify2] = hook;

    [Hle("NID_D6124071", Nid = 0xD6124071, Library = "SceKernelSuspendForDriver")]
    void SuspendSetHookD6124071(uint hook) => _suspendHooks[HookD612] = hook;

    [Hle("NID_B5C58EE8", Nid = 0xB5C58EE8, Library = "SceKernelSuspendForDriver")]
    void SuspendSetHookB5C58EE8(uint hook) => _suspendHooks[HookB5C5] = hook;

    // ---- the handler lists

    static int SuspendRegister(uint[] list, uint index, uint handler, uint argument)
    {
        if (index >= SuspendHandlerCount)
        {
            return -1;
        }

        list[index * 2] = handler;
        list[index * 2 + 1] = argument;
        return 0;
    }

    /// <summary>NID_CE7A2207 (SceKernelSuspendForDriver)(int index &lt; 0x20, handler, argument): a suspend handler
    /// (-1 for an index out of range).</summary>
    [Hle("NID_CE7A2207", Nid = 0xCE7A2207, Library = "SceKernelSuspendForDriver")]
    int SuspendRegisterHandler(uint index, uint handler, uint argument)
    {
        return SuspendRegister(_suspendHandlers, index, handler, argument);
    }

    /// <summary>NID_105C5752 (SceKernelSuspendForDriver)(int index &lt; 0x20, handler, argument): a resume handler.</summary>
    [Hle("NID_105C5752", Nid = 0x105C5752, Library = "SceKernelSuspendForDriver")]
    int SuspendRegisterResumeHandler(uint index, uint handler, uint argument)
    {
        return SuspendRegister(_resumeHandlers, index, handler, argument);
    }

    /// <summary>NID_D4958E6F (SceKernelSuspendForDriver)(value): calls each suspend handler, first to last, with the
    /// value and its argument. 0.</summary>
    [Hle("NID_D4958E6F", Nid = 0xD4958E6F, Library = "SceKernelSuspendForDriver")]
    int SuspendCallHandlers(uint value)
    {
        for (int i = 0; i < SuspendHandlerCount; i++)
        {
            SuspendCall(_suspendHandlers[i * 2], value, _suspendHandlers[i * 2 + 1]);
        }

        return 0;
    }

    /// <summary>NID_FE2118BD (SceKernelSuspendForDriver)(value): calls each resume handler, last to first. 0.</summary>
    [Hle("NID_FE2118BD", Nid = 0xFE2118BD, Library = "SceKernelSuspendForDriver")]
    int SuspendCallResumeHandlers(uint value)
    {
        for (int i = SuspendHandlerCount - 1; i >= 0; i--)
        {
            SuspendCall(_resumeHandlers[i * 2], value, _resumeHandlers[i * 2 + 1]);
        }

        return 0;
    }
}
