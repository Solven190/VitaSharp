namespace VitaSharp.Core.Hle.Modules;

public sealed partial class RegMgrSystem
{
    /// <summary>The value of /REGISTRY/category_version in the FW 3.74 registry template (os0:kd/registry.db0, whose
    /// header is stamped with the same number), for a file system without the template.</summary>
    internal const int Fw374RegistryVersion = 0x00357004;

    /// <summary>The syscall's answer to a caller that is not a system program (SceRegistryMgr checks
    /// ksceSblACMgrIsSystemProgram first).</summary>
    const int ErrNotSystemProgram = unchecked((int)0x80010058);

    /// <summary>
    /// sceRegMgrGetRegVersion(void): the registry's version, returned directly. SceRegistryMgr (FW 3.74) answers only
    /// system programs (others: 0x80010058) and returns the value of the registry's first key, /REGISTRY/category_version
    /// (the template's default is 0x00357004, the number in the template's header), after taking the registry lock.
    /// </summary>
    [Hle(ReturnsValue = true)]
    int sceRegMgrGetRegVersion()
    {
        if (!Emu.Access.IsSystemProgram)
        {
            return ErrNotSystemProgram;
        }

        if (Reg.TryGetInt("/REGISTRY/", "category_version", out int version))
        {
            return version;
        }

        return Fw374RegistryVersion;
    }
}
