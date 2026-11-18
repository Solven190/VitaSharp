using VitaSharp.Core.Memory;
using static VitaSharp.Core.Hle.ShellSvcIpc;

namespace VitaSharp.Core.Hle.Modules;

// The last general exports of libnetctl.suprx (FW 3.74, NIDs without public names), read in the module: the network
// configuration request, the one-SSID scan list, the ad hoc authentication stop and the periodical scan. Each keeps
// the library's checks; the scan finds no access point on a unit without Wi-Fi (no results), the ad hoc
// authentication server is never running (its start fails: not connected).

public sealed partial class Net
{
    const int NETCTL_RESERVED_NOT_ZERO = unchecked((int)0x804121CD), NETCTL_INVALID_FLAGS = unchecked((int)0x804121CE);

    /// <summary>sizeof the optional periodical-scan parameter block (its first word).</summary>
    const uint PeriodicalScanParamSize = 0x30;

    /// <summary>NID_EA423A96(const config *c): hands the shell's netctl service a network configuration (method
    /// 0x20008: the two sizes, the 0x608-byte block, then the two buffers whose address and size words sit at +0x5C8 /
    /// +0x5CC and +0x5D0 / +0x5D4). The block's reserved parts must be zero (the byte at +0x71, the four 0x62-byte
    /// strings at +0x420, the words at +0x5A8, +0x5AC, +0x5D8 and the areas at +0x5B0 (8), +0x5B8 (0x10), +0x5DC
    /// (0x2C)), else 0x804121CD.</summary>
    [Hle("sceNetCtl_EA423A96", Nid = 0xEA423A96, Library = "SceNetCtl")]
    int NetCtlSetConfiguration(uint config)
    {
        if (!_netctlInited)
        {
            return NETCTL_NOT_INITIALIZED;
        }

        if (config == 0)
        {
            return NETCTL_INVALID_ADDR;
        }

        if (!ConfigurationReservedIsZero(config))
        {
            return NETCTL_RESERVED_NOT_ZERO;
        }

        uint firstData = GuestMemory.R32(config + 0x5C8);
        uint firstSize = GuestMemory.R32(config + 0x5CC);
        uint secondData = GuestMemory.R32(config + 0x5D0);
        uint secondSize = GuestMemory.R32(config + 0x5D4);
        return Svc(0x20008, Val(firstSize, secondSize), Buf(config, 0x608), Buf(firstData, firstSize), Buf(secondData, secondSize));
    }

    static bool ConfigurationReservedIsZero(uint config)
    {
        if (GuestMemory.Read<byte>(config + 0x71) != 0)
        {
            return false;
        }

        for (uint i = 0; i < 4; i++)
        {
            if (!IsZero(config + 0x420 + i * 0x62, 0x62))
            {
                return false;
            }
        }

        return GuestMemory.R32(config + 0x5A8) == 0 && GuestMemory.R32(config + 0x5AC) == 0 && IsZero(config + 0x5B0, 8)
            && IsZero(config + 0x5B8, 0x10) && GuestMemory.R32(config + 0x5D8) == 0 && IsZero(config + 0x5DC, 0x2C);
    }

    static bool IsZero(uint address, int size) => GuestMemory.Span(address, size).IndexOfAnyExcept((byte)0) < 0;

    /// <summary>NID_BB163832(const char *ssid, const param *p): the SSID list of NID_6A56FC3A with one SSID. A NULL SSID
    /// is 0x80412190; the optional parameter block may only use flag bits 0-1 of its word at +4 (else 0x804121CE) and
    /// keeps its reserved areas +8 (0x20) and +0x30 (8) zero (else 0x804121CD).</summary>
    [Hle("sceNetCtl_BB163832", Nid = 0xBB163832, Library = "SceNetCtl")]
    int NetCtlSetSsid(uint ssid, uint param)
    {
        if (!_netctlInited)
        {
            return NETCTL_NOT_INITIALIZED;
        }

        if (ssid == 0)
        {
            return NETCTL_SSID_TOO_LONG;
        }

        if (param != 0)
        {
            if ((GuestMemory.R32(param + 4) & ~3u) != 0)
            {
                return NETCTL_INVALID_FLAGS;
            }

            if (!IsZero(param + 8, 0x20) || !IsZero(param + 0x30, 8))
            {
                return NETCTL_RESERVED_NOT_ZERO;
            }
        }

        // the list of NID_6A56FC3A with this one entry: the same length check, nothing to scan
        return GuestMemory.ReadCString(ssid, 0x22).Length >= 0x21 ? NETCTL_SSID_TOO_LONG : 0;
    }

    /// <summary>NID_15BEEEF5(void): stops the ad hoc authentication server NID_1172C5FC starts (its thread and
    /// sockets). The server is never running here (its start answers not connected), so the library's answer for
    /// no server: 0x80412108.</summary>
    [Hle("sceNetCtl_15BEEEF5", Nid = 0x15BEEEF5, Library = "SceNetCtl")]
    int NetCtlAdhocAuthStop()
    {
        return _netctlInited ? NETCTL_NOT_CONNECTED : NETCTL_NOT_INITIALIZED;
    }

    /// <summary>NID_586C4DD7(int priority, int cpuAffinity, const param *p): starts the periodical Wi-Fi scan (the
    /// "SceNetCtlPeriodicalScan" thread and event flag; the result table is cleared). The parameter block is optional;
    /// given, its size word must be 0x30 (else 0x80412111). No access point is ever found on a unit without Wi-Fi, so
    /// the HLE keeps no thread: the results stay empty.</summary>
    [Hle("sceNetCtl_586C4DD7", Nid = 0x586C4DD7, Library = "SceNetCtl")]
    int NetCtlPeriodicalScanStart(int priority, int cpuAffinity, uint param)
    {
        if (!_netctlInited)
        {
            return NETCTL_NOT_INITIALIZED;
        }

        if (param != 0 && GuestMemory.R32(param) != PeriodicalScanParamSize)
        {
            return NETCTL_INVALID_SIZE;
        }

        return 0;
    }

    /// <summary>NID_67926E00(void): stops the periodical scan (0 also when it was not running).</summary>
    [Hle("sceNetCtl_67926E00", Nid = 0x67926E00, Library = "SceNetCtl")]
    int NetCtlPeriodicalScanStop()
    {
        return _netctlInited ? 0 : NETCTL_NOT_INITIALIZED;
    }

    /// <summary>NID_BBA99D34(result *buf, uint max): copies up to <paramref name="max"/> 0x70-byte scan results and
    /// returns how many: none here. NULL is 0x80412107, a max of 0 is 0x80412111.</summary>
    [Hle("sceNetCtl_BBA99D34", Nid = 0xBBA99D34, Library = "SceNetCtl")]
    int NetCtlPeriodicalScanGetResults(uint buffer, uint max)
    {
        if (!_netctlInited)
        {
            return NETCTL_NOT_INITIALIZED;
        }

        if (buffer == 0)
        {
            return NETCTL_INVALID_ADDR;
        }

        if (max == 0)
        {
            return NETCTL_INVALID_SIZE;
        }

        return 0;
    }
}
