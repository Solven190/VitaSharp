using VitaSharp.Core.Kernel;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The registry callbacks of SceRegistryMgr (FW 3.74), the SceRegMgr syscalls system programs use to hear about key
/// changes. Every syscall answers only system programs (ksceSblACMgrIsSystemProgram, else 0x80010058) and takes the
/// caller's callback UID. The kernel keeps 16 slots of 0x10 bytes: the callback, a "started" flag and five watched
/// key ranges (one per category added).
/// <list type="bullet">
/// <item>An unknown callback UID fails where the kernel converts it (0x80024501).</item>
/// <item>Register: the first free slot, returned as the result; a callback already in a slot is 0x800D0025, no free
/// slot 0x800D0026.</item>
/// <item>Add(callback, category): one more watched category, returned as its index in the slot (a NULL one
/// 0x800D0004, 0x100 characters or more 0x800D000A, one the registry does not have 0x800D0004); a sixth is
/// 0x800D0026.</item>
/// <item>Start: watches every key when no category was added; Stop clears the flag; Unregister frees the slot. Each
/// returns the slot.</item>
/// <item>A callback that is not in a slot is 0x800D0027 for Add, Start, Stop and Unregister.</item>
/// </list>
/// When a key's value really changes, every started slot that watches it is notified
/// (ksceKernelNotifyCallback) with the index of the first watched category that holds the key (0 for "every key").
/// </summary>
public sealed partial class RegMgrSystem
{
    internal const int SlotCount = 16;
    internal const int CategoriesPerSlot = 5;

    internal const int ErrAlreadyRegistered = unchecked((int)0x800D0025);
    internal const int ErrNoSlot = unchecked((int)0x800D0026);
    internal const int ErrNotRegistered = unchecked((int)0x800D0027);
    private const int ErrNameTooLong = unchecked((int)0x800D000A);

    private const int FreeSlot = -1;
    private const int FlagStarted = 1;
    /// <summary>The kernel's 0xFFFE range: started without a category, every key is watched.</summary>
    private const int FlagEveryKey = 2;

    /// <summary>The callback UID of each slot (<see cref="FreeSlot"/> when free).</summary>
    [SnapshotMissing] // states of builds before it: the calls registered nothing
    private readonly int[] _slotCallback = Enumerable.Repeat(FreeSlot, SlotCount).ToArray();

    [SnapshotMissing] // states of builds before it: the calls registered nothing
    private readonly int[] _slotFlags = new int[SlotCount];

    /// <summary>The watched categories, in the order they were added (a category's index in its slot is its place
    /// among that slot's entries).</summary>
    [SnapshotMissing] // states of builds before it: the calls registered nothing
    private readonly List<(int Slot, string Category)> _slotCategories = [];

    /// <summary>The syscalls' own checks, in their order: a system program, then the callback UID turned into the
    /// kernel's (an unknown UID fails there). Null when the call may go on.</summary>
    private int? CheckCaller(int callback)
    {
        if (!Emu.Access.IsSystemProgram)
        {
            return ErrNotSystemProgram;
        }

        if (Emu.Kernel.Objects.Get<KernelObject>(callback) == null)
        {
            return SceError.INVALID_UID;
        }

        return null;
    }

    private int SlotOf(int callback) => Array.IndexOf(_slotCallback, callback);

    private int CategoryCount(int slot)
    {
        int count = 0;
        foreach ((int owner, string _) in _slotCategories)
        {
            if (owner == slot)
            {
                count++;
            }
        }

        return count;
    }

    [Hle(ReturnsValue = true)]
    int sceRegMgrRegisterCallback(int callback)
    {
        if (CheckCaller(callback) is int refused)
        {
            return refused;
        }

        lock (_slotCallback)
        {
            if (SlotOf(callback) >= 0)
            {
                return ErrAlreadyRegistered;
            }

            int free = Array.IndexOf(_slotCallback, FreeSlot);
            if (free < 0)
            {
                return ErrNoSlot;
            }

            _slotCallback[free] = callback;
            _slotFlags[free] = 0;
            _slotCategories.RemoveAll(entry => entry.Slot == free);
            return free;
        }
    }

    [Hle(ReturnsValue = true)]
    int sceRegMgrAddRegistryCallback(int callback, uint category)
    {
        if (CheckCaller(callback) is int refused)
        {
            return refused;
        }

        if (category == 0)
        {
            return ERR_INVALID;
        }

        string? path = Str(category);
        if (path == null || path.Length >= 0x100)
        {
            return ErrNameTooLong;
        }

        lock (_slotCallback)
        {
            int slot = SlotOf(callback);
            if (slot < 0)
            {
                return ErrNotRegistered;
            }

            int count = CategoryCount(slot);
            if (count >= CategoriesPerSlot)
            {
                return ErrNoSlot;
            }

            if (!CategoryExists(path))
            {
                return ERR_INVALID;
            }

            _slotCategories.Add((slot, path));
            return count;
        }
    }

    [Hle(ReturnsValue = true)]
    int sceRegMgrStartCallback(int callback)
    {
        if (CheckCaller(callback) is int refused)
        {
            return refused;
        }

        lock (_slotCallback)
        {
            int slot = SlotOf(callback);
            if (slot < 0)
            {
                return ErrNotRegistered;
            }

            if (CategoryCount(slot) == 0)
            {
                _slotFlags[slot] |= FlagEveryKey;
            }

            _slotFlags[slot] |= FlagStarted;
            UpdateRegistryWatch();
            return slot;
        }
    }

    [Hle(ReturnsValue = true)]
    int sceRegMgrStopCallback(int callback)
    {
        if (CheckCaller(callback) is int refused)
        {
            return refused;
        }

        lock (_slotCallback)
        {
            int slot = SlotOf(callback);
            if (slot < 0)
            {
                return ErrNotRegistered;
            }

            _slotFlags[slot] &= ~FlagStarted;
            UpdateRegistryWatch();
            return slot;
        }
    }

    [Hle(ReturnsValue = true)]
    int sceRegMgrUnregisterCallback(int callback)
    {
        if (CheckCaller(callback) is int refused)
        {
            return refused;
        }

        lock (_slotCallback)
        {
            int slot = SlotOf(callback);
            if (slot < 0)
            {
                return ErrNotRegistered;
            }

            FreeCallbackSlot(slot);
            UpdateRegistryWatch();
            return slot;
        }
    }

    private void FreeCallbackSlot(int slot)
    {
        _slotCallback[slot] = FreeSlot;
        _slotFlags[slot] = 0;
        _slotCategories.RemoveAll(entry => entry.Slot == slot);
    }

    /// <summary>A category the registry has: some key's path is under it ("/CONFIG/SYSTEM/" or "/CONFIG/SYSTEM").</summary>
    private bool CategoryExists(string category)
    {
        foreach (Firmware.Registry.Key key in Reg.Keys)
        {
            if (Holds(category, key.Path))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Holds(string category, string path)
    {
        if (path == category)
        {
            return true;
        }

        return category.EndsWith('/')
            ? path.StartsWith(category, StringComparison.Ordinal)
            : path.Length > category.Length && path[category.Length] == '/' && path.StartsWith(category, StringComparison.Ordinal);
    }

    /// <summary>Listens to the registry while a slot is started (the registry is shared by every process of the
    /// system, so a change any of them makes reaches this process's callbacks). After a state load the next callback
    /// syscall listens again.</summary>
    private void UpdateRegistryWatch()
    {
        bool anyStarted = false;
        for (int i = 0; i < SlotCount; i++)
        {
            if (_slotCallback[i] != FreeSlot && (_slotFlags[i] & FlagStarted) != 0)
            {
                anyStarted = true;
                break;
            }
        }

        // removed first in both cases: after a state load the flag is unknown, and the handler is never added twice
        Reg.Changed -= OnRegistryChanged;
        if (anyStarted)
        {
            Reg.Changed += OnRegistryChanged;
        }

    }

    /// <summary>The place, among <paramref name="slot"/>'s categories, of the first one that holds
    /// <paramref name="path"/>; -1 when none does.</summary>
    private int WatchedCategory(int slot, string path)
    {
        int index = 0;
        foreach ((int owner, string category) in _slotCategories)
        {
            if (owner != slot)
            {
                continue;
            }

            if (Holds(category, path))
            {
                return index;
            }

            index++;
        }

        return -1;
    }

    private void OnRegistryChanged(string path)
    {
        lock (_slotCallback)
        {
            for (int slot = 0; slot < SlotCount; slot++)
            {
                if (_slotCallback[slot] == FreeSlot || (_slotFlags[slot] & FlagStarted) == 0)
                {
                    continue;
                }

                int watched = (_slotFlags[slot] & FlagEveryKey) != 0 ? 0 : WatchedCategory(slot, path);
                if (watched < 0)
                {
                    continue;
                }

                // the kernel frees the slot of a callback it can no longer notify
                if (Emu.Kernel.Objects.Get<Callback>(_slotCallback[slot]) is not { } callback)
                {
                    FreeCallbackSlot(slot);
                    continue;
                }

                callback.Notify(watched);
            }
        }
    }
}
