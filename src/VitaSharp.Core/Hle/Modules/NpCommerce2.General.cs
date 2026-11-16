using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// The last general exports of np_commerce2.suprx (FW 3.74, NIDs without public names), read in the module: four result
// parsers, the store request whose stack arguments are required, deleting every context, the content-id string
// helpers, the sign-up field validator and the account sign-up flow, which the library refuses in the production NP
// environment.

public sealed partial class NpCommerce2
{
    const int C2_BAD_STRING = unchecked((int)0x80550F07), C2_PRODUCTION_ENV = unchecked((int)0x80550FD3);

    /// <summary>The validator's verdict bits: empty, bad length, no digit, no letter, a character three times in a row,
    /// fewer than three different characters, a first character that is not a letter, a character not allowed.</summary>
    const uint FieldEmpty = 0x01, FieldBadLength = 0x02, FieldNoDigit = 0x04, FieldNoLetter = 0x08,
        FieldTripleRepeat = 0x10, FieldFewDistinct = 0x20, FieldBadFirst = 0x40, FieldBadCharacter = 0x80;

    /// <summary>The field kinds of NID_60187EF1: a free text, the password, the e-mail address and the online id.</summary>
    const int FieldText = 0xFF00, FieldPassword = 0x28, FieldEmail = 0x27, FieldOnlineId = 0x10;

    /// <summary>The prefix and suffix of the content id NID_043F86FF recognises ("IP9100-NPIA0900" + '2'..'6' +
    /// "_00-CC").</summary>
    const string LinkContentPrefix = "IP9100-NPIA0900", LinkContentSuffix = "_00-CC";

    // ------------------------------------------------------------------ parsers

    /// <summary>The 0x34 item of the parent (at least 0x1D bytes): two 32-bit values, a pointer to its data at +8 and
    /// the string 0x13 after the fixed part (+0x19); 0x6C bytes.</summary>
    [Hle("sceNpCommerce2_303DE914", Nid = 0x303DE914, Library = C2)]
    int C2Info34(uint parent, uint info)
    {
        if (Need(parent, info) is int e)
        {
            return e;
        }

        uint p = Rd(parent, 3);
        int r = TlvFind(p, Rd(parent, 4), 0x34, 0x1D, out ushort l);
        if (r < 0)
        {
            return r;
        }

        uint d = p + (uint)r;
        Child(parent, info, 0x6C, d, l);
        Be32To(At(info, 0x17), d);
        Be32To(At(info, 0x18), d + 4);
        Wr(info, 0x19, d + 8);
        uint c = d + 0x19;
        Wr(info, 5, c);
        return Str(info, At(info, 0x1A), c, (uint)l - 0x19, 0x13);
    }

    /// <summary>The 0x61 item of the parent (the single-item form of NID_1A37B2BA): a value and the string 0x60; 100
    /// bytes.</summary>
    [Hle("sceNpCommerce2_DFC84F4F", Nid = 0xDFC84F4F, Library = C2)]
    int C2Info61(uint parent, uint info)
    {
        if (Need(parent, info) is int e)
        {
            return e;
        }

        uint p = Rd(parent, 3);
        int r = TlvFind(p, Rd(parent, 4), 0x61, 8, out ushort l);
        if (r < 0)
        {
            return r;
        }

        uint d = p + (uint)r;
        return ValueAndString(parent, d, d, l, info, 0x60);
    }

    /// <summary>Item <paramref name="index"/> of section 0x93 (at least 4 bytes): one 32-bit value; 0x60 bytes.</summary>
    [Hle("sceNpCommerce2_A94C2A99", Nid = 0xA94C2A99, Library = C2)]
    int C2Item93(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e)
        {
            return e;
        }

        uint p = Rd(parent, 3);
        int r = TlvElement(p, Rd(parent, 4), 0x93, 4, index, out ushort l);
        if (r < 0)
        {
            return r;
        }

        uint d = p + (uint)r;
        Child(parent, info, 0x60, d, l);
        Be32To(At(info, 0x17), d);
        Wr(info, 5, d + 4);
        return r;
    }

    /// <summary>Item <paramref name="index"/> of section 0x7B after the parent's cursor (at least 0x49 bytes): its data
    /// pointer, three 32-bit values at +0x39 / +0x3D / +0x41 and the string 0x08 after the fixed part (+0x45); 0x70
    /// bytes.</summary>
    [Hle("sceNpCommerce2_E46F89EA", Nid = 0xE46F89EA, Library = C2)]
    int C2Item7B(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e)
        {
            return e;
        }

        Rest(parent, out uint p, out uint n);
        int r = TlvElement(p, n, 0x7B, 0x49, index, out ushort l);
        if (r < 0)
        {
            return r;
        }

        uint d = p + (uint)r;
        Child(parent, info, 0x70, d, l);
        Wr(info, 0x17, d);
        Be32To(At(info, 0x18), d + 0x39);
        Be32To(At(info, 0x19), d + 0x3D);
        Be32To(At(info, 0x1A), d + 0x41);
        uint c = d + 0x45;
        Wr(info, 5, c);
        return Str(info, At(info, 0x1B), c, (uint)l - 0x45, 0x08);
    }

    // ------------------------------------------------------------------ requests and contexts

    /// <summary>(reqId, a, b, c): a request to the store whose last two arguments are required.</summary>
    [Hle("sceNpCommerce2_922382C1", Nid = 0x922382C1, Library = C2)]
    int StoreOp922382C1(uint reqId, uint a, uint b, uint c)
    {
        return InitOr(b != 0 && c != 0, reqId);
    }

    /// <summary>NID_B45A7935(void): deletes every context (the library's list, up to six) with its requests.</summary>
    [Hle("sceNpCommerce2_B45A7935", Nid = 0xB45A7935, Library = C2)]
    int DeleteAllContexts()
    {
        lock (_lock)
        {
            if (!_inited)
            {
                return ERR_LIB_NOT_INITIALIZED;
            }

            _ctx.Clear();
            _reqs.Clear();
            _reqTypes.Clear();
            return 0;
        }
    }

    // ------------------------------------------------------------------ content id strings

    /// <summary>NID_884BF45A(char *contentId): cuts the string at its third '-' (keeps the first two parts and what
    /// follows up to the next dash). NULL or fewer than two dashes is 0x80550F07.</summary>
    [Hle("sceNpCommerce2_884BF45A", Nid = 0x884BF45A, Library = C2)]
    int CutContentId(uint contentId)
    {
        if (contentId == 0)
        {
            return C2_BAD_STRING;
        }

        uint p = contentId;
        for (int i = 0; i < 2; i++)
        {
            p = StrChr(p, '-');
            if (p == 0)
            {
                return C2_BAD_STRING;
            }

            p++;
        }

        uint third = StrChr(p, '-');
        if (third != 0)
        {
            GuestMemory.Write(third, (byte)0);
        }

        return 0;
    }

    /// <summary>NID_87A71049(const char *contentId): the offset of its second '-'; NULL or fewer than two dashes is
    /// 0x80550F03.</summary>
    [Hle("sceNpCommerce2_87A71049", Nid = 0x87A71049, Library = C2)]
    int ContentIdSecondDash(uint contentId)
    {
        if (contentId == 0)
        {
            return ERR_SYS_INVALID;
        }

        uint p = contentId;
        for (int i = 0; i < 2; i++)
        {
            p = StrChr(p, '-');
            if (p == 0)
            {
                return ERR_SYS_INVALID;
            }

            p++;
        }

        return (int)(p - contentId - 1);
    }

    /// <summary>NID_043F86FF(const char *contentId, int *result): *result = 1 when the id (at most 0x38 characters) is
    /// "IP9100-NPIA0900", a digit '2'..'6', then "_00-CC" (the PS4 Link content), else 0. NULL arguments: 0x80550F03.</summary>
    [Hle("sceNpCommerce2_043F86FF", Nid = 0x043F86FF, Library = C2)]
    int IsLinkContentId(uint contentId, uint result)
    {
        if (contentId == 0 || result == 0)
        {
            return ERR_SYS_INVALID;
        }

        byte[] id = CBytes(contentId, 0x38);
        int prefix = LinkContentPrefix.Length;
        bool match = id.Length >= prefix + LinkContentSuffix.Length + 1
            && StartsWithAscii(id, 0, LinkContentPrefix)
            && id[prefix] is >= (byte)'2' and <= (byte)'6'
            && StartsWithAscii(id, prefix + 1, LinkContentSuffix);
        GuestMemory.W32(result, match ? 1u : 0u);
        return 0;
    }

    /// <summary>The bytes of the C string at <paramref name="address"/>, at most <paramref name="max"/> (strnlen).</summary>
    static byte[] CBytes(uint address, int max)
    {
        int length = 0;
        while (length < max && GuestMemory.Read<byte>(address + (uint)length) != 0)
        {
            length++;
        }

        return GuestMemory.Span(address, length).ToArray();
    }

    static bool StartsWithAscii(byte[] text, int offset, string value)
    {
        if (text.Length - offset < value.Length)
        {
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            if (text[offset + i] != value[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The address of the first <paramref name="ch"/> in the string at <paramref name="p"/>, or 0 (strchr).</summary>
    static uint StrChr(uint p, char ch)
    {
        while (true)
        {
            byte b = GuestMemory.Read<byte>(p);
            if (b == ch)
            {
                return p;
            }

            if (b == 0)
            {
                return 0;
            }

            p++;
        }
    }

    // ------------------------------------------------------------------ sign-up fields

    /// <summary>NID_60187EF1(int kind, const char *value, uint *verdict): checks a sign-up field and ORs what is wrong
    /// into *verdict (cleared first). Kinds: 0xFF00 a text of up to 0x400 printable ASCII characters; 0x28 the password
    /// (8 to 30 printable characters with a digit and a letter, no character three times in a row, ignoring case, and
    /// at least three different characters); 0x27 the e-mail address (up to 0x40 printable characters with an '@' that
    /// is neither first nor last and a '.' after it); 0x10 the online id (3 to 16 characters: a letter, then letters,
    /// digits, '_' or '-'). Another kind is 0x80550F03.</summary>
    [Hle("sceNpCommerce2_60187EF1", Nid = 0x60187EF1, Library = C2)]
    int CheckSignUpField(int kind, uint value, uint verdict)
    {
        lock (_lock)
        {
            if (!_inited)
            {
                return ERR_LIB_NOT_INITIALIZED;
            }
        }

        if (value == 0 || verdict == 0)
        {
            return ERR_SYS_INVALID;
        }

        GuestMemory.W32(verdict, 0);
        uint flags;
        switch (kind)
        {
            case FieldText:
                flags = CheckPrintable(CBytes(value, 0x400));
                break;
            case FieldPassword:
                flags = CheckPassword(CBytes(value, 0x1E));
                break;
            case FieldEmail:
                flags = CheckEmail(CBytes(value, 0x40));
                break;
            case FieldOnlineId:
                flags = CheckOnlineId(CBytes(value, 0x10));
                break;
            default:
                return ERR_SYS_INVALID;
        }

        GuestMemory.W32(verdict, flags);
        return 0;
    }

    /// <summary>Empty, or a character that is not printable ASCII (the library's ctype classes upper, lower, digit,
    /// punctuation and blank).</summary>
    static uint CheckPrintable(byte[] text)
    {
        if (text.Length == 0)
        {
            return FieldEmpty;
        }

        foreach (byte c in text)
        {
            if (c >= 0x80 || c < 0x20 || c == 0x7F)
            {
                return FieldBadCharacter;
            }
        }

        return 0;
    }

    static bool IsLetter(byte c) => c is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z';

    static bool IsDigit(byte c) => c is >= (byte)'0' and <= (byte)'9';

    /// <summary>The library's toupper: ASCII letters only.</summary>
    static byte UpperAscii(byte c) => c is >= (byte)'a' and <= (byte)'z' ? (byte)(c - 0x20) : c;

    static uint CheckPassword(byte[] text)
    {
        uint flags = CheckPrintable(text);
        if (text.Length == 0)
        {
            return flags;
        }

        if (text.Length < 8)
        {
            flags |= FieldBadLength;
        }

        if (Array.FindIndex(text, IsDigit) < 0)
        {
            flags |= FieldNoDigit;
        }

        if (Array.FindIndex(text, IsLetter) < 0)
        {
            flags |= FieldNoLetter;
        }

        int run = 0;
        byte previous = 0;
        foreach (byte c in text)
        {
            run = UpperAscii(previous) == UpperAscii(c) ? run + 1 : 1;
            if (run > 2)
            {
                flags |= FieldTripleRepeat;
                break;
            }

            previous = c;
        }

        // the library stops looking once it has seen three different characters
        var distinct = new List<byte>(3);
        int index = 0;
        for (; index < text.Length; index++)
        {
            if (!distinct.Contains(text[index]))
            {
                distinct.Add(text[index]);
            }

            if (distinct.Count > 2)
            {
                break;
            }
        }

        if (index == text.Length)
        {
            flags |= FieldFewDistinct;
        }

        return flags;
    }

    static uint CheckEmail(byte[] text)
    {
        uint flags = CheckPrintable(text);
        if (text.Length == 0)
        {
            return flags;
        }

        int at = Array.IndexOf(text, (byte)'@');
        if (text[0] == '@' || text[^1] == '@' || at < 0 || Array.IndexOf(text, (byte)'.', at) < 0)
        {
            flags |= FieldBadCharacter;
        }

        return flags;
    }

    static uint CheckOnlineId(byte[] text)
    {
        if (text.Length == 0)
        {
            return FieldEmpty;
        }

        uint flags = 0;
        if (text.Length < 3)
        {
            flags |= FieldBadLength;
        }

        if (!IsLetter(text[0]))
        {
            flags |= FieldBadFirst;
        }

        for (int i = 1; i < text.Length; i++)
        {
            byte c = text[i];
            if (!IsLetter(c) && !IsDigit(c) && c != '_' && c != '-')
            {
                flags |= FieldBadCharacter;
                break;
            }
        }

        return flags;
    }

    // ------------------------------------------------------------------ account sign-up

    /// <summary>NID_F9DE9CDF(ctx, a, country, language, int mode, b, c, d, output, outputSize, f, g, h): the
    /// account sign-up flow (the store servers' sign-up requests, then mode 1 runs the confirmation requests and copies
    /// the result). The library checks its arguments (a, country, language and the last three; outside mode 1 also
    /// the output buffer and its size, at least 0x1F; else 0x80550F03), then that the two codes have two
    /// characters (else 0x80550F03 too), then refuses the production NP environment ("np", the only
    /// one a retail unit has) with 0x80550FD3 before any request.</summary>
    [Hle("sceNpCommerce2_F9DE9CDF", Nid = 0xF9DE9CDF, Library = C2)]
    int SignUpAccount(uint ctx, uint a, uint country, uint language, int mode, uint b, uint c, uint d, uint output,
        uint outputSize, uint f, uint g, uint h)
    {
        bool argumentsSet = a != 0 && country != 0 && language != 0 && f != 0 && g != 0 && h != 0
            && (mode == 1 || (output != 0 && outputSize >= 0x1F));
        if (!argumentsSet)
        {
            return ERR_SYS_INVALID;
        }

        if (CBytes(country, 2).Length != 2 || CBytes(language, 2).Length != 2)
        {
            return ERR_SYS_INVALID;
        }

        return C2_PRODUCTION_ENV;
    }
}
