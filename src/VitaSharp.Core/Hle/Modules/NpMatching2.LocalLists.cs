using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// Two unnamed np_matching2.suprx 3.74 entries that read a context's local lists (no request to the server). Both start
// with the library's initialized flag (0x80550C03 while it is clear), then find the context by id among the library's
// contexts (0x80550C06 when there is none); these are the codes the 3.74 module returns for them.
public sealed partial class NpMatching2
{
    const int Fw374NotInitialized = unchecked((int)0x80550C03), Fw374ContextNotFound = unchecked((int)0x80550C06),
        Fw374InvalidArgument = unchecked((int)0x80550C0A), Fw374NotInRoom = unchecked((int)0x80550C2D);

    /// <summary>The context's local state for the two entries: 0 and the context, or the module's error.</summary>
    int LocalContext(ushort ctxId, out Context? context)
    {
        context = null;
        if (!_initialized)
        {
            return Fw374NotInitialized;
        }

        if (!_contexts.TryGetValue(ctxId, out context))
        {
            return Fw374ContextNotFound;
        }

        return 0;
    }

    /// <summary>NID_54692CAA(ctxId, uint16 *serverIds, uint16 max): copies up to max ids of the servers the context
    /// knows (the list a started context gets from the matching service) and returns how many it copied. Locally the
    /// one server is known once the context is started; offline the list stays empty.</summary>
    [Hle("NID_54692CAA", Nid = 0x54692CAA, Library = "SceNpMatching2")]
    int NpMatching2ServerIdListLocal(ushort ctxId, uint serverIds, ushort max)
    {
        lock (_lock)
        {
            int result = LocalContext(ctxId, out Context? context);
            if (result != 0)
            {
                return result;
            }

            int known = Local && context!.Started ? 1 : 0;
            int copied = Math.Min(known, max);
            if (copied > 0 && serverIds != 0)
            {
                GuestMemory.Write(serverIds, ServerId);
            }

            return copied;
        }
    }

    /// <summary>NID_DF322C5B(ctxId, list, entries, uint32 *count): fills entries (memberId | slot &lt;&lt; 16) from the
    /// room the context is in. The three pointers are checked first (0x80550C0A); a context in no room answers
    /// 0x80550C2D, which is every context here (the local matching server has no rooms to be in).</summary>
    [Hle("NID_DF322C5B", Nid = 0xDF322C5B, Library = "SceNpMatching2")]
    int NpMatching2RoomMemberEntriesLocal(ushort ctxId, uint list, uint entries, uint count)
    {
        lock (_lock)
        {
            int result = LocalContext(ctxId, out _);
            if (result != 0)
            {
                return result;
            }
        }

        if (list == 0 || entries == 0 || count == 0)
        {
            return Fw374InvalidArgument;
        }

        return Fw374NotInRoom;
    }
}
