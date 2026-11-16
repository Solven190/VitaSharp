using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Kernel;

/// <summary>
/// The kernel's process event handlers (SceProcEventForDriver, in FW 3.74 sysmem): kernel code registers a handler
/// table {size, create, exit, kill, stop, start, switch} with ksceKernelRegisterProcEventHandler (0x8101F540: the
/// table is copied into a SceUIDProcEventClass object, the switch entry only when size is 0x1C, and the object goes to
/// the end of one system-wide list), and processmgr calls ksceKernelInvokeProcEventHandler (0x8101F5E4) as a process
/// is created (event 1, after its main thread is made and before it starts; a handler that fails refuses the process,
/// and those called before it then get the exit event), exits (2) or is killed (3). The invoke walks the list in
/// registration order and calls create/exit/kill(pid, param, argument), stop/start(pid, type, param, argument) and
/// switch(event, type, param, argument), argument being the third parameter of the registration; it stops at the
/// handler named by its last parameter, and, when asked for the failing handler, at the first that returns a negative
/// value.
/// </summary>
public static class ProcessEvents
{
    /// <summary>The event ids processmgr passes (FW 3.74 processmgr: 1 when the process is created, 2 when it exits, 3
    /// when it is killed).</summary>
    public const int Create = 1, Exit = 2, Kill = 3, Stop = 4, Start = 5;

    /// <summary>The size word processmgr writes at the start of the parameter block: 0x14 for the create event, 0x10 for
    /// the others.</summary>
    public const uint CreateParamSize = 0x14, ParamSize = 0x10;

    /// <summary>The registered handlers, in registration order.</summary>
    internal static List<KernelData> Handlers(ObjectTable objects)
    {
        return objects.All<KernelData>().Where(d => d.Kind == KernelData.ProcEventHandler).OrderBy(d => d.Uid).ToList();
    }

    /// <summary>The entry of handler table <paramref name="handler"/> for <paramref name="eventId"/> (0: none).</summary>
    private static uint EntryFor(KernelData handler, int eventId)
    {
        uint offset = eventId switch
        {
            Create => 4,
            Exit => 8,
            Kill => 0xC,
            Stop => 0x10,
            Start => 0x14,
            _ => 0x18,
        };
        return offset < handler.Size ? GuestMemory.R32(handler.Address + offset) : 0;
    }

    /// <summary>
    /// Calls the handlers of <paramref name="handlers"/> for one event on guest thread <paramref name="thread"/>, as
    /// ksceKernelInvokeProcEventHandler does: up to the handler <paramref name="stopAt"/> (0: all of them); with
    /// <paramref name="stopAtFailure"/>, up to the first that returns a negative value, whose UID goes to
    /// <paramref name="failedUid"/>. Returns what the last handler called returned (0 when none was).
    /// </summary>
    internal static int Run(GuestThread thread, IReadOnlyList<KernelData> handlers, int pid, int eventId, int eventType, uint param,
        bool stopAtFailure, int stopAt, out int failedUid)
    {
        failedUid = 0;
        int result = 0;
        foreach (KernelData handler in handlers)
        {
            if (handler.Uid == stopAt)
            {
                break;
            }

            uint entry = EntryFor(handler, eventId);
            if (entry == 0)
            {
                continue;
            }

            result = eventId switch
            {
                Create or Exit or Kill => (int)thread.CallGuest(entry, (uint)pid, param, handler.Aux),
                Stop or Start => (int)thread.CallGuest(entry, (uint)pid, (uint)eventType, param, handler.Aux),
                _ => (int)thread.CallGuest(entry, (uint)eventId, (uint)eventType, param, handler.Aux),
            };
            if (result < 0 && stopAtFailure)
            {
                failedUid = handler.Uid;
                break;
            }
        }

        return result;
    }
}
