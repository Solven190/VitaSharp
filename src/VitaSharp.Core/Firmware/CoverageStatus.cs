namespace VitaSharp.Core.Firmware;

/// <summary>
/// How the coverage report (hle-coverage) counts one firmware function: covered or not, and whether by the HLE (real or
/// a documented default) or by a real module the emulator runs (LLE).
/// </summary>
public static class CoverageStatus
{
    /// <summary>The three counts of one function.</summary>
    public readonly record struct Counts(bool Covered, bool Default, bool Lle);

    /// <summary>
    /// <paramref name="hasHle"/>: an [Hle] entry point exists; <paramref name="servedByHle"/>: one of them is in a module
    /// that is not a firmware fallback (<see cref="Hle.HleModule.FirmwareFallbackOnly"/>); <paramref name="isDefault"/>:
    /// every entry point is a documented firmware default; <paramref name="firmwareExports"/>: an installed firmware
    /// module exports it; <paramref name="installedModuleExports"/>: a module the
    /// loader runs (a game's own module, a data module, a firmware module served to every importer) exports it.
    /// A function the HLE has only as a firmware fallback is the real module's wherever the firmware is installed (the
    /// loader gives it to system programs; a game uses the fallback only when it ships no copy), so it counts as LLE
    /// when the firmware exports it, not as HLE.
    /// </summary>
    public static Counts Of(bool hasHle, bool servedByHle, bool isDefault, bool firmwareExports, bool installedModuleExports)
    {
        if (hasHle && servedByHle)
        {
            return new Counts(true, isDefault, false);
        }

        if (installedModuleExports || (hasHle && firmwareExports))
        {
            return new Counts(true, false, true);
        }

        return new Counts(hasHle, hasHle && isDefault, false);
    }
}
