using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// pspnet_adhoc.suprx 3.74's system entries (libcdlg_netcheck imports them for the PSP game sharing / ad hoc party
/// check): the SSID a group of the current ad hoc id gets, the unit's 8-byte product id kept for the beacon, and the
/// ad hoc authentication server. Starting the server (NID_A3EC45B9) needs sceNetAdhocctlInit (0x80410B08), stops a
/// running one, then makes its two libnet sockets and its "SceNetAdhocAuth" thread, which answers PSPs that ask to
/// join; NID_8938A4D0 stops it (0x80412108 when none runs). With no ad hoc network around no PSP ever asks, so the
/// server is only marked running (the "default" part).
/// </summary>
public sealed partial class NetAdhoc
{
    const int CTL_ERR_NOT_CONNECTED = unchecked((int)0x80412108), CTL_ERR_BUFFER_SMALL = unchecked((int)0x80412111),
        CTL_ERR_INVALID_ID = unchecked((int)0x8041210F);

    /// <summary>Bytes the SSID buffer must exceed (the 32-byte SSID and its terminator).</summary>
    const uint SsidBufferMin = 0x21;

    /// <summary>Most characters of the group name the SSID takes.</summary>
    const int SsidNameMax = 8;

    [SnapshotMissing] // states of builds before it: the call did not exist, so the server never ran
    bool _authServerRunning;

    [SnapshotMissing] // states of builds before it: the call did not exist, so the id was never set
    readonly byte[] _productId = new byte[8];

    /// <summary>Tests: the product id NID_5C83C5C8 kept.</summary>
    internal byte[] ProductId
    {
        get
        {
            lock (_lock)
            {
                return _productId.ToArray();
            }
        }
    }

    /// <summary>NID_5C83C5C8(const uint8 productId[8]): keeps the 8 bytes (NULL clears them); no result.</summary>
    [Hle("NID_5C83C5C8", Nid = 0x5C83C5C8, Library = "ScePspnetAdhoc")]
    void AdhocSetProductId(uint productId)
    {
        lock (_lock)
        {
            if (productId == 0)
            {
                Array.Clear(_productId);
            }
            else
            {
                GuestMemory.ReadSpan(productId, 8).CopyTo(_productId);
            }
        }
    }

    /// <summary>
    /// NID_F87F9518(const char *groupName, char *ssid, uint size): the PSP ad hoc SSID of a group: the registry's
    /// /CONFIG/NET/pspnet_adhoc_ssid_prefix (3 bytes, "PSP"), '_', the ad hoc id type as a letter (0 'A', 1 'X',
    /// 2 'S', else 0x8041210F), the id's 9 data bytes, "_L_", then up to 8 characters of the name; returns its length.
    /// NULL ssid 0x80410B04, a size of 0x21 or less 0x80412111.
    /// </summary>
    [Hle("NID_F87F9518", Nid = 0xF87F9518, Library = "ScePspnetAdhoc")]
    int AdhocGroupSsid(uint groupName, uint ssid, uint size)
    {
        if (ssid == 0)
        {
            return CTL_ERR_INVALID_ARG;
        }

        if (size < SsidBufferMin)
        {
            return CTL_ERR_BUFFER_SMALL;
        }

        byte letter;
        Span<byte> data = stackalloc byte[9];
        lock (_lock)
        {
            int type = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(_adhocId);
            switch (type)
            {
                case 0:
                    letter = (byte)'A';
                    break;
                case 1:
                    letter = (byte)'X';
                    break;
                case 2:
                    letter = (byte)'S';
                    break;
                default:
                    return CTL_ERR_INVALID_ID;
            }

            _adhocId.AsSpan(4, 9).CopyTo(data);
        }

        string prefix = Emu.Hle.Modules.OfType<RegMgr>().First().Value(0x00134C03, out _) as string ?? "PSP";
        Span<byte> output = GuestMemory.Span(ssid, (int)size);
        output.Clear();
        for (int i = 0; i < 3; i++)
        {
            output[i] = i < prefix.Length ? (byte)prefix[i] : (byte)0;
        }

        output[3] = (byte)'_';
        output[4] = letter;
        data.CopyTo(output[5..]);
        output[14] = (byte)'_';
        output[15] = (byte)'L';
        output[16] = (byte)'_';
        int length = 0x11;
        if (groupName != 0)
        {
            for (int i = 0; i < SsidNameMax; i++)
            {
                byte character = GuestMemory.Read<byte>(groupName + (uint)i);
                if (character == 0)
                {
                    break;
                }

                output[length] = character;
                length++;
            }
        }

        return length;
    }

    /// <summary>NID_A3EC45B9(priority, stackSize, const params *, int count, const uint8 data[0x80]): starts the
    /// authentication server (see the class remarks); 0, or 0x80410B08 before sceNetAdhocctlInit.</summary>
    [Hle("NID_A3EC45B9", Nid = 0xA3EC45B9, Library = "ScePspnetAdhoc", Default = true)]
    int AdhocAuthServerStart(int priority, uint stackSize, uint parameters, int count, uint data)
    {
        lock (_lock)
        {
            if (!_ctlInited)
            {
                return CTL_ERR_NOT_INITIALIZED;
            }

            _authServerRunning = true;
        }

        Log.InfoOnce("adhoc-auth", "ScePspnetAdhoc NID_A3EC45B9: authentication server marked running (no ad hoc network: no PSP asks to join)");
        return 0;
    }

    /// <summary>NID_8938A4D0(): stops the authentication server; 0x80410B08 before sceNetAdhocctlInit, 0x80412108 when
    /// none runs.</summary>
    [Hle("NID_8938A4D0", Nid = 0x8938A4D0, Library = "ScePspnetAdhoc", Default = true)]
    int AdhocAuthServerStop()
    {
        lock (_lock)
        {
            if (!_ctlInited)
            {
                return CTL_ERR_NOT_INITIALIZED;
            }

            if (!_authServerRunning)
            {
                return CTL_ERR_NOT_CONNECTED;
            }

            _authServerRunning = false;
            return 0;
        }
    }
}
