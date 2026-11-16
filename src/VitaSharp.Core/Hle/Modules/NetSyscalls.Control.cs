using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// The SceNetPs control commands (sceNetSyscallControl) as FW 3.74's SceNetPs handles them (read in its code, study
// only), with the state the emulated stack keeps for them. The kernel of an emulated unit has the loopback interface
// only and no network service behind it: nothing ever changes a value the kernel would learn from the network.
public sealed partial class Net
{
    const int KernelEio = -5, KernelEinval = -22;

    /// <summary>lo0's index in the kernel's interface list (its record's +0x20).</summary>
    const int Lo0KernelIndex = 1;

    /// <summary>The stack's DNS record (commands 2 / 0x20000002, 0x28 bytes): +0 / +4 the two server addresses.</summary>
    [SnapshotMissing]
    readonly byte[] _stackDns = new byte[0x28];

    /// <summary>The processes that read the DNS record since it was set: the first read of each gets bit 0 of +8 (its
    /// libnet then drops its resolver cache).</summary>
    [SnapshotMissing]
    readonly HashSet<int> _stackDnsReaders = new();

    /// <summary>lo0's link words (commands 4 / 0x20000004): the interface record's +0x118, +0x11C, +0x134, +0x138,
    /// +0x13C.</summary>
    [SnapshotMissing]
    readonly uint[] _lo0Link = new uint[5];

    /// <summary>The per-process network state (commands 0x10000017 / 0x20000017), by process id.</summary>
    [SnapshotMissing]
    readonly Dictionary<int, int> _processNetState = new();

    /// <summary>The per-process setting 0x19 (commands 0x10000019 / 0x20000019), by process id.</summary>
    [SnapshotMissing]
    readonly Dictionary<int, int> _processSetting19 = new();

    /// <summary>The stack-wide settings 0x20 (one word) and 0x23 (two words).</summary>
    [SnapshotMissing]
    int _stackSetting20;

    [SnapshotMissing]
    readonly uint[] _stackSetting23 = new uint[2];

    /// <summary>The kernel keeps a record for every process that uses the network; here that is the running one.</summary>
    bool HasProcessRecord(int pid)
    {
        return pid == Emu.Pid || _processNetState.ContainsKey(pid);
    }

    /// <summary>One control command on the stack or on lo0 (the net lock held).</summary>
    int StackControl(bool onInterface, int cmd, uint data, int size)
    {
        switch (cmd)
        {
            case 2:
                return onInterface ? KernelEio : size < 0x28 ? KernelEinval : ReadStackDns(data);
            case 0x20000002:
                return onInterface ? KernelEio : size < 0x28 || GuestMemory.R32(data + 8) != 0 ? KernelEinval : WriteStackDns(data);
            case 4:
                return !onInterface ? KernelEio : size < 0x14 ? KernelEinval : ReadLo0Link(data);
            case 0x20000004:
                return !onInterface ? KernelEio : size < 0x14 ? KernelEinval : WriteLo0Link(data);
            case 0x20000006 when size >= 8:
                return 0; // lo0 never had a link to bring back: nothing is re-announced
            case 0x20000011 when size >= 4 && GuestMemory.R32(data) == 0:
                return 0; // clears the interfaces' address-conflict words, never set on lo0
            case 0x22:
                return onInterface ? KernelEio : size < 0x44 ? KernelEinval : ReadAndClearCounters(data);
            case 0x14 when size >= 4:
                GuestMemory.W32(data, 0);
                return 0;
            case 0x16:
                return size < 0 ? KernelEinval : 0; // the kernel's four network-process slots: none taken here
            case 0x10000002 when size >= 0x20:
                return KernelEio; // the event entry of a three-slot table looked up by the id in data[0]: empty
            case 0x10000017 when size >= 8 && GuestMemory.R32(data + 4) == 0:
                return ReadProcessNetState(data);
            case 0x20000017 when size >= 8:
                return WriteProcessNetState((int)GuestMemory.R32(data), (int)GuestMemory.R32(data + 4));
            case 0x10000019 when size >= 0xC && GuestMemory.R32(data + 8) == 0:
                return ReadProcessSetting19(data);
            case 0x20000019 when size >= 0xC:
                _processSetting19[(int)GuestMemory.R32(data)] = (int)GuestMemory.R32(data + 8);
                return 0;
            case 0x20000012 when size >= 0x200:
                return 0;
            case 0x20000003 when size >= 8:
                return 0;
            case 0x20000020 when size >= 4:
                _stackSetting20 = (int)GuestMemory.R32(data);
                return 0;
            case 0x20000023 when size >= 8:
                _stackSetting23[0] = GuestMemory.R32(data);
                _stackSetting23[1] = GuestMemory.R32(data + 4);
                return 0;
            case 0x21:
                return 0; // the {word, word} entries of the stack's flagged list, size / 8 at most: none here
            case 0x10000018 when size >= 8: // { int value; int flags }: read setting 0x18 (bit 0 of flags picks the slot)
                GuestMemory.W32(data, (uint)((GuestMemory.R32(data + 4) & 1) == 0 ? _ctl18a : _ctl18b));
                return 0;
            case 0x20000018 when size >= 8:
                SetCtl18((int)GuestMemory.R32(data + 4) & 1, (int)GuestMemory.R32(data));
                return 0;
            default:
                Log.WarnOnce($"netctl{cmd:X}", $"sceNetSyscallControl: command 0x{cmd:X} (size {size}) not modelled");
                return KernelEinval;
        }
    }

    /// <summary>Command 2: the DNS record; the first read of each process since the last set gets bit 0 of +8.</summary>
    int ReadStackDns(uint data)
    {
        _stackDns.CopyTo(GuestMemory.Span(data, 0x28));
        if (_stackDnsReaders.Add(Emu.Pid))
        {
            GuestMemory.W32(data + 8, GuestMemory.R32(data + 8) | 1);
        }

        return 0;
    }

    int WriteStackDns(uint data)
    {
        GuestMemory.Span(data, 0x28).CopyTo(_stackDns);
        _stackDnsReaders.Clear();
        return 0;
    }

    /// <summary>Command 0x22: read and clear the 0x44-byte traffic counters, none on the loopback-only stack.</summary>
    static int ReadAndClearCounters(uint data)
    {
        GuestMemory.Fill(data, 0, 0x44);
        return 0;
    }

    int ReadLo0Link(uint data)
    {
        for (int i = 0; i < _lo0Link.Length; i++)
        {
            GuestMemory.W32(data + (uint)i * 4, _lo0Link[i]);
        }

        return 0;
    }

    /// <summary>{ word0, -, word2, count, - }: count -1..99 and word2 &gt;= 0, else -EINVAL. Non-zero word0 / word2
    /// replace the interface's; with no carrier on lo0 the link-up word (+0x11C) stays 0.</summary>
    int WriteLo0Link(uint data)
    {
        int word2 = (int)GuestMemory.R32(data + 8);
        int count = (int)GuestMemory.R32(data + 12);
        if (word2 < 0 || (uint)(count + 1) > 100)
        {
            return KernelEinval;
        }

        uint word0 = GuestMemory.R32(data);
        if (word0 != 0)
        {
            _lo0Link[0] = word0;
        }

        if (word2 != 0)
        {
            _lo0Link[2] = (uint)word2;
        }

        _lo0Link[3] = (uint)count;
        _lo0Link[1] = 0;
        return 0;
    }

    int ReadProcessNetState(uint data)
    {
        int pid = (int)GuestMemory.R32(data);
        if (!HasProcessRecord(pid))
        {
            return KernelEinval;
        }

        GuestMemory.W32(data + 4, (uint)_processNetState.GetValueOrDefault(pid));
        return 0;
    }

    int WriteProcessNetState(int pid, int value)
    {
        if (!HasProcessRecord(pid))
        {
            return KernelEinval;
        }

        _processNetState[pid] = value;
        return 0;
    }

    int ReadProcessSetting19(uint data)
    {
        int pid = (int)GuestMemory.R32(data);
        if (!_processSetting19.TryGetValue(pid, out int value))
        {
            GuestMemory.W32(data + 8, 0);
            return KernelEinval;
        }

        GuestMemory.W32(data + 8, (uint)value);
        return 0;
    }
}
