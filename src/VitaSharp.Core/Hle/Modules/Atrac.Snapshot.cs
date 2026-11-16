using VitaSharp.Core.Hle.Audio;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: the decoder group and every handle with its whole stream state (<see cref="AtracStream"/>:
/// ring and playback positions, loops, the last decoded superframe); each handle gets a new host decoder on load.</summary>
[GenerateSnapshot, SnapshotIgnoreFields("DecoderFactory")]
public sealed partial class Atrac
{
    /// <summary>The saved handles are in place: each one gets a new decoder, primed to continue where it was.</summary>
    void LoadExtra(SnapshotReader r)
    {
        lock (_lock)
        {
            foreach (var (slot, stream) in _handles)
            {
                stream.RestoreDecoder(DecoderFactory(stream.Info));
                AudioDiagnostics.StreamOpened($"atrac:{slot}", $"Atrac handle {slot}: restored by a save state");
            }
        }
    }
}
