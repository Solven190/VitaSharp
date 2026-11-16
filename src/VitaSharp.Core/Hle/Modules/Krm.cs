namespace VitaSharp.Core.Hle.Modules;

// SceKrm (os0:kd/bootimage.skprx SceKrm, FW 3.74, read in the module, study only): a kernel key service. At its start
// the module checks its built-in key material (four comparisons of derived values) and copies the keys it will use;
// a second readiness flag is set by another step of its start. Every one of its nineteen syscalls (NIDs without public names) first
// asks for both and, without them, answers the module's not-ready error 0x80A0000A before looking at its arguments.
// The emulated unit has no such key material (it lives in the secure firmware), so the service is never ready: each
// syscall gives that error and nothing is ever derived or unlocked. A security service: an unavailable answer, never
// a value that would pass for a working key.

public sealed partial class SystemServicesVsh
{
    const int KRM_ERR_NOT_READY = unchecked((int)0x80A0000A);

    /// <summary>The nineteen SceKrm syscalls: the key service is not ready (0x80A0000A).</summary>
    [Hle("NID_10202EB4", Nid = 0x10202EB4, Library = "SceKrm", Default = true)]
    [Hle("NID_18DE25C6", Nid = 0x18DE25C6, Library = "SceKrm", Default = true)]
    [Hle("NID_2528D613", Nid = 0x2528D613, Library = "SceKrm", Default = true)]
    [Hle("NID_3257BD35", Nid = 0x3257BD35, Library = "SceKrm", Default = true)]
    [Hle("NID_350C8251", Nid = 0x350C8251, Library = "SceKrm", Default = true)]
    [Hle("NID_41547BE1", Nid = 0x41547BE1, Library = "SceKrm", Default = true)]
    [Hle("NID_5296A954", Nid = 0x5296A954, Library = "SceKrm", Default = true)]
    [Hle("NID_7251237D", Nid = 0x7251237D, Library = "SceKrm", Default = true)]
    [Hle("NID_7401B932", Nid = 0x7401B932, Library = "SceKrm", Default = true)]
    [Hle("NID_820B45C6", Nid = 0x820B45C6, Library = "SceKrm", Default = true)]
    [Hle("NID_949506DC", Nid = 0x949506DC, Library = "SceKrm", Default = true)]
    [Hle("NID_98EEB7F3", Nid = 0x98EEB7F3, Library = "SceKrm", Default = true)]
    [Hle("NID_A100E9C3", Nid = 0xA100E9C3, Library = "SceKrm", Default = true)]
    [Hle("NID_A9201DA3", Nid = 0xA9201DA3, Library = "SceKrm", Default = true)]
    [Hle("NID_D7B35BE6", Nid = 0xD7B35BE6, Library = "SceKrm", Default = true)]
    [Hle("NID_E0AEAE3B", Nid = 0xE0AEAE3B, Library = "SceKrm", Default = true)]
    [Hle("NID_ECE6B5F4", Nid = 0xECE6B5F4, Library = "SceKrm", Default = true)]
    [Hle("NID_ECF53237", Nid = 0xECF53237, Library = "SceKrm", Default = true)]
    [Hle("NID_F0B626E7", Nid = 0xF0B626E7, Library = "SceKrm", Default = true)]
    int KrmNotReady()
    {
        return KRM_ERR_NOT_READY;
    }
}
