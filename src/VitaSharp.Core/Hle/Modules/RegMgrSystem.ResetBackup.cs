using VitaSharp.Core.Firmware;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The registry's reset, backup and restore syscalls as SceRegistryMgr (FW 3.74) has them. Each key of the firmware's
/// template carries a reset level (field 4), the kinds of unit it exists on (field 5) and a backup level (field 7):
/// <see cref="Registry.Key"/>. The emulated unit is a retail handheld (the registry manager's unit mask 1).
/// </summary>
public sealed partial class RegMgrSystem
{
    /// <summary>SceRegistryMgr's unit mask on a retail handheld (PS TV 4, development units 2/8, test units 0x10/0x20).</summary>
    internal const byte RetailHandheldMask = 1;

    internal const int ErrPathTooLong = unchecked((int)0x800D000A);
    internal const int ErrBadLevel = unchecked((int)0x800D000D);
    internal const int ErrNoCategory = unchecked((int)0x800D0017);
    internal const int ErrBadBackupLevel = unchecked((int)0x800D0020);

    /// <summary>The reset levels the registry manager accepts with a category.</summary>
    private static bool IsResetLevel(int level) => level is 0 or 0x10 or 0x20 or 0x30 or 0x40 or 0x100 or 0x1000;

    /// <summary>The backup levels sceRegMgrDbBackup accepts.</summary>
    private static bool IsBackupLevel(int level) => level is 0 or 10 or 20 or 30 or 40 or 99;

    private static bool ExistsOnThisUnit(Registry.Key key) =>
        key.ModelMask == 0 || (key.ModelMask & RetailHandheldMask) == RetailHandheldMask;

    /// <summary>Whether a reset (or the backup's pass, level 5) of <paramref name="level"/> touches the key: never a key
    /// of level 0x1000; level 0x40 the keys flagged 0x2000, level 0x100 those flagged 0x4000, level 0x10 every key,
    /// any other level the keys whose own level is at least it; and only keys that exist on this unit.</summary>
    internal static bool ResetSelects(Registry.Key key, int level)
    {
        int own = key.Level & ~Registry.LevelFlagsMask;
        if (own == Registry.LevelNeverReset || !ExistsOnThisUnit(key))
        {
            return false;
        }

        return level switch
        {
            0x40 => (key.Level & 0x2000) != 0,
            0x100 => (key.Level & 0x4000) != 0,
            0x10 => true,
            _ => level <= own,
        };
    }

    /// <summary>The caller's path argument as the registry syscalls copy it (at most 0xFF characters).</summary>
    private static int CopyPath(uint pointer, int tooLongError, out string path)
    {
        path = "";
        if (pointer == 0)
        {
            return ERR_INVALID;
        }

        string text = GuestMemory.ReadCString(pointer, 0x100);
        if (text.Length >= 0x100)
        {
            return tooLongError;
        }

        path = text;
        return 0;
    }

    /// <summary>
    /// sceRegMgrResetRegistryLv(const char *category, int level): puts the keys of a category (and the categories under
    /// it) back to their factory defaults, the keys chosen by <paramref name="level"/> (<see cref="ResetSelects"/>).
    /// System programs only (0x80010058); NULL is 0x800D0004, a path of 0x100 characters or more 0x800D000A, a level
    /// the manager does not know 0x800D000D, a category with no keys 0x800D0017. Registry callbacks hear the keys
    /// whose value changes.
    /// </summary>
    [Hle("sceRegMgrResetRegistryLv", Nid = 0x2B5F3E20, Library = "SceRegMgr")]
    int sceRegMgrResetRegistryLv(uint category, int level)
    {
        if (!Emu.Access.IsSystemProgram)
        {
            return ErrNotSystemProgram;
        }

        int copied = CopyPath(category, ErrPathTooLong, out string path);
        if (copied != 0)
        {
            return copied;
        }

        if (!IsResetLevel(level))
        {
            return ErrBadLevel;
        }

        List<Registry.Key> keys = Reg.KeysUnder(path);
        if (keys.Count == 0)
        {
            return ErrNoCategory;
        }

        keys.RemoveAll(key => !ResetSelects(key, level));
        Reg.Replace(keys, new Dictionary<string, byte[]>());
        return 0;
    }

    /// <summary>
    /// sceRegMgrDbBackup(const char *path, int level): writes the registry to a backup file. The keys the backup pass
    /// selects (reset level at least 5, so not the level-0 keys, and a backup level below <paramref name="level"/>) go
    /// into the backup at their factory default; every other key keeps its value. System programs only (0x80010058);
    /// NULL or a path of 0x100 characters or more is 0x800D0004, a level other than 0, 10, 20, 30, 40 or 99 0x800D0020.
    /// The file is written in VitaSharp's registry store format (the store itself is not the firmware's system.dreg).
    /// </summary>
    [Hle("sceRegMgrDbBackup", Nid = 0xB68B5422, Library = "SceRegMgr")]
    int sceRegMgrDbBackup(uint pathPointer, int level)
    {
        if (!Emu.Access.IsSystemProgram)
        {
            return ErrNotSystemProgram;
        }

        int copied = CopyPath(pathPointer, ERR_INVALID, out string path);
        if (copied != 0)
        {
            return copied;
        }

        if (!IsBackupLevel(level))
        {
            return ErrBadBackupLevel;
        }

        if (Emu.Vfs.Resolve(path, true) is not { } host)
        {
            return SceError.IO_ENOENT;
        }

        Dictionary<string, byte[]> values = Reg.WrittenValues();
        foreach (Registry.Key key in Reg.KeysUnder("/"))
        {
            if (ResetSelects(key, 5) && key.BackupLevel < level)
            {
                values.Remove(key.Path);
            }
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(host)!);
            Reg.WriteValues(host, values);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return SceError.IO_ENOENT;
        }

        return 0;
    }

    /// <summary>
    /// sceRegMgrDbRestore(const char *path): replaces the registry with a backup made by sceRegMgrDbBackup. Keys of
    /// backup level 5 keep the unit's own value; every other key that exists on this unit takes the backup's value, or
    /// its factory default when the backup has none. System programs only (0x80010058); NULL or a path of 0x100
    /// characters or more is 0x800D0004; a backup that cannot be opened is the file system's error.
    /// </summary>
    [Hle("sceRegMgrDbRestore", Nid = 0xA87D2562, Library = "SceRegMgr")]
    int sceRegMgrDbRestore(uint pathPointer)
    {
        if (!Emu.Access.IsSystemProgram)
        {
            return ErrNotSystemProgram;
        }

        int copied = CopyPath(pathPointer, ERR_INVALID, out string path);
        if (copied != 0)
        {
            return copied;
        }

        if (Emu.Vfs.Resolve(path) is not { } host || !File.Exists(host))
        {
            return SceError.IO_ENOENT;
        }

        Dictionary<string, byte[]> values;
        try
        {
            values = Reg.ReadValues(host);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return SceError.IO_ENOENT;
        }

        List<Registry.Key> keys = Reg.KeysUnder("/");
        keys.RemoveAll(key => key.BackupLevel == 5 || !ExistsOnThisUnit(key));
        Reg.Replace(keys, values);
        return 0;
    }
}
