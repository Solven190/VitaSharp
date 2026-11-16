using VitaSharp.Core.Host;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: the open session (handler, filter, buffer, text, caret, keyboard type) and the events
/// waiting for the next sceImeUpdate. The host keyboard is not state of the game: the running session's is closed and,
/// when the loaded session is open, the host keyboard opens again on its text (automated runs with --ime-text typed it
/// at sceImeOpen already: their events are in the loaded queue).</summary>
[GenerateSnapshot, SnapshotIgnoreFields("GuestCallOverride", "_backend")]
public sealed partial class Ime
{
    void LoadExtra(SnapshotReader r)
    {
        ITextInputBackend? running;
        ITextInputBackend? reopen = null;
        int session;
        string text;
        uint maxLen, type, option;
        lock (_lock)
        {
            running = _backend;
            _backend = null;
            if (_open && CommonDialog.ImeDefaultText == null)
            {
                reopen = _backend = Emu?.Host.TextInput;
            }

            session = _session;
            text = _text;
            maxLen = _maxLen;
            type = _type;
            option = _option;
        }

        running?.End();
        if (reopen != null)
        {
            BeginHostKeyboard(reopen, session, text, maxLen, type, option);
        }
    }
}
