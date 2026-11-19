namespace VitaSharp.Core.Loader.Self;

/// <summary>
/// An NPDRM application SELF with encrypted segments was given no klicensee: without the title's license (work.bin or
/// ux0:license/app/&lt;title id&gt;/*.rif) its keys cannot be derived, so it cannot be loaded. The module manager
/// answers such a load with its loader error (<see cref="Emulator.ModuleLoadError"/>); the application launch with
/// the application manager's missing-license error (<see cref="Package.TitleLicense.NoLicenseError"/>).
/// </summary>
public sealed class SelfLicenseMissingException() : InvalidOperationException("Encrypted application SELF needs a klicensee (license)");
