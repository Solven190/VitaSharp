using VitaSharp.Core.Kernel;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceKernelSuspendForDriver's system event handlers, as the FW 3.74 SceSysmem has them: a kernel module registers a
/// handler (int handler(int resume, int eventid, void *args, void *opt)) by name and gets the UID of a
/// SceSysEventHandler object; ksceKernelSysEventDispatch calls every handler in the order they were registered with
/// (resume, eventid, args, the argument given at registration), and when a handler fails and the caller gave a place
/// for it, stops there with that handler's UID and its error; otherwise it answers the last handler's result (0 with
/// none). Power management (ScePower) dispatches suspend and resume this way.
/// </summary>
public sealed partial class SysmemKernel
{
    /// <summary>The registered handlers' UIDs, in registration order (the firmware's list).</summary>
    [SaveState.SnapshotMissing] // states of builds before it: the calls did not exist, so no handler was registered
    readonly List<int> _sysEventHandlers = new();

    /// <summary>ksceKernelRegisterSysEventHandler(name, handler, argp): the handler's UID, appended to the list.</summary>
    [Hle(Library = "SceKernelSuspendForDriver")]
    int ksceKernelRegisterSysEventHandler(string name, uint handler, uint argument)
    {
        int uid = K.Objects.Register(new KernelData { Name = name, Kind = KernelData.SysEventHandler, Address = handler, Aux = argument });
        lock (_sysEventHandlers)
        {
            _sysEventHandlers.Add(uid);
        }

        return uid;
    }

    /// <summary>ksceKernelUnregisterSysEventHandler(uid): removes a handler; a UID of another class is refused with the
    /// object manager's error.</summary>
    [Hle(Library = "SceKernelSuspendForDriver")]
    int ksceKernelUnregisterSysEventHandler(int uid)
    {
        switch (K.Objects.Get<KernelObject>(uid))
        {
            case null:
                return SceError.INVALID_UID;
            case KernelData { Kind: KernelData.SysEventHandler }:
                lock (_sysEventHandlers)
                {
                    _sysEventHandlers.Remove(uid);
                }

                K.Objects.Remove(uid);
                return 0;
            default:
                return SceError.INVALID_UID_CLASS;
        }
    }

    /// <summary>ksceKernelSysEventDispatch(resume, eventid, args, int *failedUid): calls the handlers in order (see the
    /// class summary).</summary>
    [Hle(Library = "SceKernelSuspendForDriver", Cost = HleCost.UserLibrary)]
    int ksceKernelSysEventDispatch(int resume, int eventId, uint args, uint failedUid)
    {
        int[] handlers;
        lock (_sysEventHandlers)
        {
            handlers = _sysEventHandlers.ToArray();
        }

        int result = 0;
        GuestThread thread = GuestThread.Current!;
        foreach (int uid in handlers)
        {
            if (K.Objects.Get<KernelData>(uid) is not { Kind: KernelData.SysEventHandler } handler)
            {
                continue;
            }

            result = (int)thread.CallGuest(handler.Address, (uint)resume, (uint)eventId, args, handler.Aux);
            if (result < 0 && failedUid != 0)
            {
                Memory.GuestMemory.W32(failedUid, (uint)uid);
                return result;
            }
        }

        return result;
    }
}
