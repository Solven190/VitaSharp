using VitaSharp.Core.Package;

namespace VitaSharp.Core;

/// <summary>
/// The executable of application <see cref="TitleId"/> is encrypted and the title has no license installed: the
/// console's application manager refuses the launch with <see cref="TitleLicense.NoLicenseError"/> (it reads the
/// license file before it creates the process, FW 3.74 SceAppMgr) and no process starts. <see cref="Error"/> is that
/// code and <see cref="UserMessage"/> the localized text the launchers show.
/// </summary>
public sealed class TitleLicenseMissingException(string titleId, Exception cause)
    : InvalidOperationException($"{titleId}: {cause.Message}", cause)
{
    public string TitleId { get; } = titleId;

    public int Error => TitleLicense.NoLicenseError;

    public string UserMessage => TitleLicense.Message(TitleId);
}
