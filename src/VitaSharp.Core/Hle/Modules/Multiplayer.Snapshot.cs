using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: matching contexts and their callbacks (the online session itself is offline/mock).</summary>
[GenerateSnapshot, SnapshotIgnoreFields("MemoryOverride")]
public sealed partial class NpMatching2
{
    [GenerateSnapshot]
    sealed partial class Context;
}

/// <summary>Save states: the contexts (bind, peer, mux mode) and the polls with their entries. Offline no context
/// ever reaches a peer, so there is no network session to recreate; a thread waiting in sceRudpPollWait is saved
/// before the call and waits again after the load (the count of waits in progress is not state of the game).</summary>
[GenerateSnapshot]
public sealed partial class Rudp
{
    /// <summary>Nothing to recreate: the contexts and polls are loaded with the fields.</summary>
    void LoadExtra(SnapshotReader r)
    {
    }

    /// <summary>No wait is in progress at a load: the threads that waited issue sceRudpPollWait again.</summary>
    void AfterSnapshotLoad()
    {
        lock (_lock)
        {
            _blockedPollWaits = 0;
        }
    }
}
