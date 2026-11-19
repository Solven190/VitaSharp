using VitaSharp.Core.Localization;
using VitaSharp.Core.Util;

namespace VitaSharp.Mobile.Core.Input;

/// <summary>
/// The save line of Controles > Mapeamento: every change is written to input.json at once, and the screen says so
/// ("Salvo", the desktop launcher's save state text); a write that fails leaves "Alteracoes nao salvas" with a row that
/// tries again, so a mapping that did not reach the file is never shown as saved.
/// </summary>
public sealed class MappingSaveState
{
    /// <summary>The last change has not reached the file.</summary>
    public bool Unsaved { get; private set; }

    /// <summary>"Salvo" or "Alteracoes nao salvas".</summary>
    public string Text => Loc.Get(Unsaved ? "CsSaveUnsaved" : "CsSaveSaved");

    /// <summary>Runs <paramref name="save"/> (the store's write); false, with <see cref="Unsaved"/>, when it failed.</summary>
    public bool Save(Action save)
    {
        try
        {
            save();
            Unsaved = false;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Controls: the mapping could not be saved: {e.Message}");
            Unsaved = true;
            return false;
        }
    }
}
