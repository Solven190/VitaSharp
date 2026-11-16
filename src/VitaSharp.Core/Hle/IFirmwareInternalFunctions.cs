namespace VitaSharp.Core.Hle;

/// <summary>An HLE module that provides functions a firmware module has without exporting them (kernel code reaches
/// them by their offset in the module's code segment).</summary>
public interface IFirmwareInternalFunctions
{
    /// <summary>Declares each of them with <see cref="FirmwareKernelModules.RegisterInternal"/>, by the firmware
    /// module's NID and the offset.</summary>
    void RegisterInternalFunctions(FirmwareKernelModules firmware);
}
