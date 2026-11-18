using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceSblSmSchedProxyForKernel (os0:kd/smsc_proxy.skprx 3.74, read in the module, study only): the ARM side of the
// secure processor's scheduler, reached through the secure monitor (smc). Invoke starts a secure module and opens a
// session; the other calls act on a session (its mailboxes ARM-to-crypto and back, its interrupt handlers, its
// status, a forced stop). The proxy is up from boot (Uninitialize takes it down: 0x800F0426 until Initialize). The
// emulated unit has no secure processor: Invoke and the raw secure command cannot run (0x80020004), so no session
// ever exists and each session call answers the module's "no such session" (0x800F042B) after its own checks.
public sealed partial class SystemServicesVsh
{
    /// <summary>SceSblSmSchedProxy: a mailbox register outside 1..3, a NULL output.</summary>
    const int SMSCHED_ERR_INVALID_ARGUMENT = unchecked((int)0x800F0416);

    /// <summary>SceSblSmSchedProxy: the two entries the module leaves unsupported.</summary>
    const int SMSCHED_ERR_UNSUPPORTED = unchecked((int)0x800F0425);

    /// <summary>SceSblSmSchedProxy: the proxy is down (after Uninitialize).</summary>
    const int SMSCHED_ERR_NOT_INITIALIZED = unchecked((int)0x800F0426);

    /// <summary>SceSblSmSchedProxy: no session with that id.</summary>
    const int SMSCHED_ERR_NO_SESSION = unchecked((int)0x800F042B);

    [SnapshotMissing] // states of builds before it: the proxy was always up
    bool _smSchedDown;

    /// <summary>ksceSblSmSchedProxyInitialize (0xA488D604)(): brings the proxy up. 0.</summary>
    [Hle(Nid = 0xA488D604, Library = "SceSblSmSchedProxyForKernel")]
    int ksceSblSmSchedProxyInitialize()
    {
        _smSchedDown = false;
        return 0;
    }

    /// <summary>ksceSblSmSchedProxyUninitialize (0x33A3A1E2)(): tells the secure monitor and takes the proxy down.</summary>
    [Hle(Nid = 0x33A3A1E2, Library = "SceSblSmSchedProxyForKernel")]
    int ksceSblSmSchedProxyUninitialize()
    {
        _smSchedDown = true;
        return 0;
    }

    /// <summary>ksceSblSmSchedProxyInvoke (0x1916509B)(..., int *id): starts a secure module in the secure processor.</summary>
    [Hle(Nid = 0x1916509B, Library = "SceSblSmSchedProxyForKernel", Default = true)]
    int ksceSblSmSchedProxyInvoke(uint a0, uint a1, uint a2, uint param, uint size, uint id)
    {
        return _smSchedDown ? SMSCHED_ERR_NOT_INITIALIZED : KERNEL_ERR_UNSUP;
    }

    /// <summary>ksceSblSmSchedProxyExecuteSKCommand (0x7894B6F0)(...): a raw command to the secure kernel.</summary>
    [Hle(Nid = 0x7894B6F0, Library = "SceSblSmSchedProxyForKernel", Default = true)]
    int ksceSblSmSchedProxyExecuteSKCommand()
    {
        return _smSchedDown ? SMSCHED_ERR_NOT_INITIALIZED : KERNEL_ERR_UNSUP;
    }

    /// <summary>The order of the session calls' checks: the proxy is up, then the call's own argument check, then
    /// the session (none exists).</summary>
    int SmSchedSessionCall(bool argumentsValid)
    {
        if (_smSchedDown)
        {
            return SMSCHED_ERR_NOT_INITIALIZED;
        }

        return argumentsValid ? SMSCHED_ERR_NO_SESSION : SMSCHED_ERR_INVALID_ARGUMENT;
    }

    /// <summary>The mailbox calls (CallFunc 0x723B382F, WriteCry2Arm 0x15B0E4DF, ClearArm2Cry 0x3CE17233,
    /// ReadArm2Cry 0xF70C04EC, ReadCry2Arm 0x973A4A7D)(int id, int register 1..3, value or out).</summary>
    [Hle("ksceSblSmSchedCallFunc", Nid = 0x723B382F, Library = "SceSblSmSchedProxyForKernel")]
    [Hle("ksceSblSmSchedProxyWriteCry2Arm", Nid = 0x15B0E4DF, Library = "SceSblSmSchedProxyForKernel")]
    [Hle("ksceSblSmSchedProxyClearArm2Cry", Nid = 0x3CE17233, Library = "SceSblSmSchedProxyForKernel")]
    [Hle("ksceSblSmSchedProxyReadArm2Cry", Nid = 0xF70C04EC, Library = "SceSblSmSchedProxyForKernel")]
    [Hle("ksceSblSmSchedProxyReadCry2Arm", Nid = 0x973A4A7D, Library = "SceSblSmSchedProxyForKernel")]
    int SmSchedMailbox(int id, uint register, uint value)
    {
        return SmSchedSessionCall(register - 1 < 3);
    }

    /// <summary>ksceSblSmSchedProxyGetStatus (0x27EB92F1)(int id, int *status): NULL 0x800F0416.</summary>
    [Hle(Nid = 0x27EB92F1, Library = "SceSblSmSchedProxyForKernel")]
    int ksceSblSmSchedProxyGetStatus(int id, uint status)
    {
        return SmSchedSessionCall(status != 0);
    }

    /// <summary>ksceSblSmSchedProxyWait (0xF35EFC1A)(int id, int *result): waits for the session's module to end.</summary>
    [Hle(Nid = 0xF35EFC1A, Library = "SceSblSmSchedProxyForKernel")]
    int ksceSblSmSchedProxyWait(int id, uint result)
    {
        return SmSchedSessionCall(true);
    }

    /// <summary>ksceSblSmSchedProxyForceStop (0xDE4EAC3C)(int id).</summary>
    [Hle(Nid = 0xDE4EAC3C, Library = "SceSblSmSchedProxyForKernel")]
    int ksceSblSmSchedProxyForceStop(int id)
    {
        return SmSchedSessionCall(true);
    }

    /// <summary>ksceSblSmSchedProxyRegisterIntrHandler (0x8B84AC2A)(int id, int slot 0..3, handler, argument): a
    /// handler is needed.</summary>
    [Hle(Nid = 0x8B84AC2A, Library = "SceSblSmSchedProxyForKernel")]
    int ksceSblSmSchedProxyRegisterIntrHandler(int id, uint slot, uint handler, uint argument)
    {
        return SmSchedSessionCall(slot < 4 && handler != 0);
    }

    /// <summary>ksceSblSmSchedProxyReleaseIntrHandler (0x85EDA5FC)(int id, int slot 0..3).</summary>
    [Hle(Nid = 0x85EDA5FC, Library = "SceSblSmSchedProxyForKernel")]
    int ksceSblSmSchedProxyReleaseIntrHandler(int id, uint slot)
    {
        return SmSchedSessionCall(slot < 4);
    }

    /// <summary>NID_1DFC8624 / NID_984EC9D1 (SceSblSmSchedProxyForKernel)(): the module answers 0x800F0425.</summary>
    [Hle("NID_1DFC8624", Nid = 0x1DFC8624, Library = "SceSblSmSchedProxyForKernel")]
    [Hle("NID_984EC9D1", Nid = 0x984EC9D1, Library = "SceSblSmSchedProxyForKernel")]
    int SmSchedUnsupported()
    {
        return SMSCHED_ERR_UNSUPPORTED;
    }
}
