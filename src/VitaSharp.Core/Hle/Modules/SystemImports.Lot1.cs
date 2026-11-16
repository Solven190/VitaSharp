using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// System-imported functions of SceHttp, SceNpBasic, SceNpManager and SceTriggerUtil without public names (FW 3.74
// libhttp, np_basic, np_manager and trigger_util read in their code, study only).

public sealed partial class Http
{
    sealed partial class Settings
    {
        /// <summary>libhttp's option 0x13 (NID_43A7CC70), one word.</summary>
        [SnapshotMissing] // states of builds before it: the call did not exist, so it was never set
        public int Option13;
    }

    const int SSL_ERR_INVALID = unchecked((int)0x80435801);

    /// <summary>NID_43A7CC70 (SceHttp)(int id, int value): libhttp's option 0x13 on a template, connection or request,
    /// set the way its other one-word options are (the safe mode updater and the e-mail engine call it).</summary>
    [Hle("NID_43A7CC70", Nid = 0x43A7CC70, Library = "SceHttp")]
    int HttpSetOption13(int id, int value)
    {
        return SetSetting(id, s => s.Option13 = value);
    }

    /// <summary>NID_A9A39BA1 (SceHttp)(SSL *ssl, callback): NULL ssl or callback 0x80435801; then libhttp turns on
    /// the context's peer verification (SceSslInternal 0x260F5A85(ssl, 1)) and installs its own verify callback
    /// with the caller's as its argument. The HLE's TLS verifies through the host; the call only checks.</summary>
    [Hle("NID_A9A39BA1", Nid = 0xA9A39BA1, Library = "SceHttp")]
    int HttpSslSetVerifyCallback(uint ssl, uint callback)
    {
        return ssl == 0 || callback == 0 ? SSL_ERR_INVALID : 0;
    }
}

public sealed partial class NpSocial
{
    const int BASIC_NOT_INITIALIZED = unchecked((int)0x80551D04);

    /// <summary>
    /// NID_7F13243D (SceNpBasic)(const presence *p) and NID_FB1B7762 (): the system side of the in-game presence,
    /// np_basic 3.74's requests 0x9000D (a 0x1CC-byte presence whose type at +4 is 1 or 2, else 0x80551D02) and
    /// 0x9000E (clear it) to the NP service. As sceNpBasicSetInGamePresence / Unset: no friends see it; with the
    /// local PSN services accepted, otherwise the server's error.
    /// </summary>
    [Hle("NID_7F13243D", Nid = 0x7F13243D, Library = "SceNpBasic")]
    int NpBasicSystemSetPresence(uint presence)
    {
        if (presence == 0)
        {
            return BASIC_INVALID_ARGUMENT;
        }

        int type = (int)GuestMemory.R32(presence + 4);
        if (type is < 1 or > 2)
        {
            return BASIC_INVALID_ARGUMENT;
        }

        return PsnAccount.Current.UsesLocalServices ? 0 : PsnAccount.Current.ServerError;
    }

    [Hle("NID_FB1B7762", Nid = 0xFB1B7762, Library = "SceNpBasic")]
    int NpBasicSystemClearPresence()
    {
        return PsnAccount.Current.UsesLocalServices ? 0 : PsnAccount.Current.ServerError;
    }
}

public sealed partial class NpManager
{
    /// <summary>NID_44DF26F5 (SceNpManager)(uint32 *out): NULL 0x80550003, before the library's initialisation
    /// 0x80550002; then the synchronous request 0x90038 to the shell's NP service with one 4-byte output.</summary>
    [Hle("NID_44DF26F5", Nid = 0x44DF26F5, Library = "SceNpManager")]
    int NpManagerRequest90038(uint output)
    {
        if (output == 0)
        {
            return unchecked((int)0x80550003);
        }

        if (!NpManagerReady)
        {
            return ERR_NOT_INITIALIZED;
        }

        return Sync(0x90038, [new(output, 4)]);
    }

    /// <summary>
    /// NID_BF8921C8 (SceNpManager)(int *has): clears *has, reads the account's ticket (NID_2EA038D8 into a 0x600-byte
    /// buffer; its error is the result), then *has = 1 when the registry key 0x7C9764 is 1 or the ticket carries one
    /// of five entitlements (sceNpAuthGetEntitlementById). An entitlement check: the ticket is the PSN server's, which
    /// cannot be reached (its error), and the local services' ticket carries none: never 1 unless the registry says so.
    /// </summary>
    [Hle("NID_BF8921C8", Nid = 0xBF8921C8, Library = "SceNpManager")]
    int NpManagerHasEntitlement(uint has)
    {
        GuestMemory.W32(has, 0);
        if (!PsnAccount.Current.UsesLocalServices)
        {
            return PsnAccount.Current.ServerError;
        }

        if (Emu.Hle.Modules.OfType<RegMgr>().FirstOrDefault()?.Value(0x007C9764, out _) is int flag && flag == 1)
        {
            GuestMemory.W32(has, 1);
        }

        return 0;
    }
}

public sealed partial class TriggerUtil
{
    const int TRIGGER_ERR_NULL = unchecked((int)0x80103604), TRIGGER_ERR_KIND = unchecked((int)0x80103605);

    /// <summary>
    /// NID_2C43AC0C (SceTriggerUtil)(uint kind, callback, arg) and NID_DA93949A (titleId, const param *p, uint kind,
    /// callback, arg): kind 0..3 (0x80103605), the second also non-NULL title id and parameter with a word at +8
    /// (0x80103604); one request at a time (0x80103601 while one runs). The library sends the shell's trigger
    /// service request 0x100001 (or its titled form), then, without a callback, waits for its answer (0x100011).
    /// The emulated shell schedules no automatic start: the request is accepted and ends at once (a default).
    /// </summary>
    [Hle("NID_2C43AC0C", Nid = 0x2C43AC0C, Library = "SceTriggerUtil", Default = true)]
    int TriggerUtilRequestKind(uint kind, uint callback, uint argument)
    {
        if (kind >= 4)
        {
            return TRIGGER_ERR_KIND;
        }

        Log.InfoOnce("trigger-2C43AC0C", $"SceTriggerUtil NID_2C43AC0C: kind {kind} accepted (no automatic start is scheduled)");
        return 0;
    }

    [Hle("NID_DA93949A", Nid = 0xDA93949A, Library = "SceTriggerUtil", Default = true)]
    int TriggerUtilRequestTitle(uint titleId, uint parameter, uint kind, uint callback, uint argument)
    {
        if (titleId == 0 || parameter == 0)
        {
            return TRIGGER_ERR_NULL;
        }

        if (kind >= 4 || GuestMemory.R32(parameter + 8) == 0)
        {
            return TRIGGER_ERR_KIND;
        }

        Log.InfoOnce("trigger-DA93949A", $"SceTriggerUtil NID_DA93949A: {GuestMemory.ReadCString(titleId, 16)} kind {kind} accepted (no automatic start is scheduled)");
        return 0;
    }

    /// <summary>NID_257BA954 (SceTriggerUtil)(int id, uint8 value): a one-byte setting of request <paramref name="id"/>
    /// sent to the shell's trigger service; nothing is scheduled here: 0 (a default).</summary>
    [Hle("NID_257BA954", Nid = 0x257BA954, Library = "SceTriggerUtil", Default = true)]
    int TriggerUtilSetRequestByte(int id, byte value)
    {
        return 0;
    }
}
