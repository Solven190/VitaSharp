namespace VitaSharp.Core.Hle.Modules;

// SceSblFwLoaderForDriver (post_ss_mgr.skprx 3.74, read in the module, study only): loads the firmware blob of a
// coprocessor (the Wi-Fi/Bluetooth chip, the PSP emulator's) for its driver. Lock(path) takes the loader for a
// firmware file (its name at most 0xFF characters), Load(1, buffer, size, out) has the module manager decrypt the
// file into the buffer, Unlock releases the loader. Decrypting a firmware blob is the secure processor's work,
// which the emulated unit does not have: Load answers 0x80020004 after the module's checks.
public sealed partial class SystemServicesVsh
{
    /// <summary>SceSblFwLoader: a NULL argument, a context other than 1, a name too long.</summary>
    const int FWLOADER_ERR_INVALID_ARGUMENT = unchecked((int)0x800F0E16);

    /// <summary>SceSblFwLoader: Load or Unlock without the loader locked.</summary>
    const int FWLOADER_ERR_NOT_LOCKED = unchecked((int)0x800F0E29);

    /// <summary>The longest firmware path the loader takes.</summary>
    const int FwLoaderMaxPath = 0xFF;

    [SaveState.SnapshotMissing] // states of builds before it: the loader was never locked
    bool _fwLoaderLocked;

    /// <summary>ksceSblFwLoaderLock (0xA6278D27)(const char *path, int zero): takes the loader for a firmware file.</summary>
    [Hle(Nid = 0xA6278D27, Library = "SceSblFwLoaderForDriver")]
    int ksceSblFwLoaderLock(uint path, int zero)
    {
        if (path == 0 || zero != 0)
        {
            return FWLOADER_ERR_INVALID_ARGUMENT;
        }

        string name = Memory.GuestMemory.ReadCString(path, FwLoaderMaxPath + 1);
        if (name.Length > FwLoaderMaxPath || (name.LastIndexOf('/') < 0 && name.LastIndexOf(':') < 0))
        {
            return FWLOADER_ERR_INVALID_ARGUMENT;
        }

        _fwLoaderLocked = true;
        return 0;
    }

    /// <summary>ksceSblFwLoaderLoad (0x91C73A54)(int context, void *buffer, SceSize size, SceSize *loaded): decrypts
    /// the locked firmware file into the buffer (context 1, buffer and output needed: 0x800F0E16; not locked:
    /// 0x800F0E29).</summary>
    [Hle(Nid = 0x91C73A54, Library = "SceSblFwLoaderForDriver", Default = true)]
    int ksceSblFwLoaderLoad(int context, uint buffer, uint size, uint loaded)
    {
        if (context != 1 || buffer == 0 || loaded == 0)
        {
            return FWLOADER_ERR_INVALID_ARGUMENT;
        }

        return _fwLoaderLocked ? KERNEL_ERR_UNSUP : FWLOADER_ERR_NOT_LOCKED;
    }

    /// <summary>ksceSblFwLoaderUnlock (0xBB59FC7A)(): releases the loader (0x800F0E29 when it is not locked).</summary>
    [Hle(Nid = 0xBB59FC7A, Library = "SceSblFwLoaderForDriver")]
    int ksceSblFwLoaderUnlock()
    {
        if (!_fwLoaderLocked)
        {
            return FWLOADER_ERR_NOT_LOCKED;
        }

        _fwLoaderLocked = false;
        return 0;
    }
}
