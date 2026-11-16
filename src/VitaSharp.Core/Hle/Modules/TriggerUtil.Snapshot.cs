using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: the registered events are the system's (on the Vita the shell's trigger service keeps them,
/// here &lt;FsRoot&gt;/ux0/vitasharp/trigger), not the game's memory, so like saved data and registry values they are
/// not rolled back: the registrations stay on disk and a load drops the cached copy, which is read again from the
/// file at the next call.</summary>
[GenerateSnapshot, SnapshotIgnoreFields("_events")]
public sealed partial class TriggerUtil
{
    void LoadExtra(SnapshotReader r)
    {
        lock (_lock)
        {
            _events = null;
        }
    }
}
