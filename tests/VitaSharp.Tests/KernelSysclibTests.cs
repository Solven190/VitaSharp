using VitaSharp.Core;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// SceSysclibForDriver (the kernel's C library in SceSysmem, FW 3.74) called the way a kernel module calls it, through
/// stubs bound by NID, with the firmware's own results: memcmp's word compare, strtol/strtoul's prefixes and missing
/// overflow checks, strtoll's clamping, the formatter's layout, the RTABI helpers' division by zero, and the checks
/// that stop the system.
/// </summary>
[Collection("guest")]
public sealed class KernelSysclibTests : IDisposable
{
    private const uint SysclibForDriver = 0x7EE45391;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-ksysclib-" + Guid.NewGuid().ToString("N"));
    private readonly Emulator _emu;
    private readonly Dictionary<uint, uint> _stubs = [];
    private uint _next;

    public KernelSysclibTests(GuestMemoryFixture fixture)
    {
        Directory.CreateDirectory(_root);
        _emu = new Emulator(new EmulatorOptions { FsRoot = _root, Memory = fixture.Memory, EnableJit = false, CpuBackend = "interp" });
        _next = _emu.Memory.Alloc(0x4000, "sysclib test stubs");
    }

    public void Dispose()
    {
        _emu.Dispose();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private uint Stub(uint nid)
    {
        if (_stubs.TryGetValue(nid, out uint at))
        {
            return at;
        }

        Assert.True(_emu.Hle.Implements(nid), $"SceSysclibForDriver 0x{nid:X8} is not implemented");
        at = _next;
        _next += 16;
        Linker.WriteHleStub(at, _emu.Hle.GetHleFunctionIndex(nid, SysclibForDriver, "SceSysclibForDriver"), nid);
        _emu.Code.Invalidate(at, 16);
        return _stubs[nid] = at;
    }

    private uint HostFunction(string name, Action<HleContext> body)
    {
        uint at = _next;
        _next += 16;
        Linker.WriteHleStub(at, _emu.Hle.RegisterHostFunction(name, body), 0);
        _emu.Code.Invalidate(at, 16);
        return at;
    }

    /// <summary>Runs <paramref name="function"/> on a guest thread; returns r0 and r1, or the thread's fault.</summary>
    private (uint R0, uint R1, Exception? Fault) Run(uint function, params uint[] args)
    {
        uint r0 = 0;
        uint r1 = 0;
        Exception? fault = null;
        GuestThread thread = _emu.Kernel.CreateThread("sysclib-test", 0, 0x10000100, 0x10000, 0, 0x70000);
        thread.HostBody = t =>
        {
            try
            {
                r0 = t.CallGuest(function, args);
                r1 = t.LastCallR1;
            }
            catch (Exception e)
            {
                fault = e;
            }
        };
        thread.Start(0, 0);
        Assert.True(thread.WaitForExit(60_000), "test thread did not finish");
        _emu.Kernel.DeleteThread(thread);
        return (r0, r1, fault);
    }

    private uint C(uint nid, params uint[] args)
    {
        (uint r0, _, Exception? fault) = Run(Stub(nid), args);
        Assert.Null(fault);
        return r0;
    }

    private ulong C64(uint nid, params uint[] args)
    {
        (uint r0, uint r1, Exception? fault) = Run(Stub(nid), args);
        Assert.Null(fault);
        return r0 | ((ulong)r1 << 32);
    }

    /// <summary>Calls a divmod helper through an A32 wrapper that returns its r2:r3 (the remainder) in r0:r1.</summary>
    private ulong Remainder(uint nid, params uint[] args)
    {
        uint wrapper = _next;
        _next += 32;
        uint[] code =
        [
            0xE92D4010, // push {r4, lr}
            0xE59FC00C, // ldr r12, [pc, #12]
            0xE12FFF3C, // blx r12
            0xE1A00002, // mov r0, r2
            0xE1A01003, // mov r1, r3
            0xE8BD8010, // pop {r4, pc}
            Stub(nid),
        ];
        for (int i = 0; i < code.Length; i++)
        {
            GuestMemory.W32(wrapper + (uint)i * 4, code[i]);
        }

        _emu.Code.Invalidate(wrapper, 32);
        (uint r0, uint r1, Exception? fault) = Run(wrapper, args);
        Assert.Null(fault);
        return r0 | ((ulong)r1 << 32);
    }

    private uint Bytes(params byte[] bytes)
    {
        uint at = _emu.Memory.Alloc((uint)bytes.Length + 8, "sysclib test data", MemoryRegion.User, 16);
        bytes.CopyTo(GuestMemory.Span(at, bytes.Length));
        return at;
    }

    private uint Str(string text) => Bytes([.. System.Text.Encoding.ASCII.GetBytes(text), 0]);

    private uint Buffer(uint size, byte fill = 0xAA)
    {
        uint at = _emu.Memory.Alloc(size + 8, "sysclib test buffer", MemoryRegion.User, 16);
        GuestMemory.Fill(at, fill, size + 8);
        return at;
    }

    private uint Words(params uint[] words)
    {
        uint at = _emu.Memory.Alloc((uint)words.Length * 4 + 8, "sysclib test va", MemoryRegion.User, 16);
        for (int i = 0; i < words.Length; i++)
        {
            GuestMemory.W32(at + (uint)i * 4, words[i]);
        }

        return at;
    }

    // NIDs of SceSysclibForDriver (FW 3.74)
    private const uint Memcmp = 0xF939E83D, Memmove = 0x6CC9C1A1, Memchr = 0x60DAEA30, TimingsafeMemcmp = 0xB5A4D745;
    private const uint Strlen = 0xCFC6A9AC, Strnlen = 0xCD4BD884, Strcmp = 0x0B33BC43, Strchr = 0x38463759, Strrchr = 0x7F0E0835,
        Strstr = 0x1304A69D, Strncpy = 0x6D286146, StrncpyS = 0xFE39AEAC, Strncat = 0xA1D1C32C, Strlcpy = 0x7FB4EBEC, Strlcat = 0x12504E09;
    private const uint Strtol = 0xAB77C5AA, Strtoul = 0x4E5042DA, Strtoll = 0x87AAAFA2;
    private const uint LookCtype = 0xCDF7F155, Toupper = 0xA685DCB1, Tolower = 0x0021DAF9;
    private const uint Uidiv = 0xA9FF1205, Uidivmod = 0xA46CB7DE, Idiv = 0x2518CD9E, Idivmod = 0xAC86B4BA, Ldivmod = 0x7554AB04,
        Uldivmod = 0x9D148CDE, Lmul = 0xFEE5E751, Lcmp = 0x709077A1, Ulcmp = 0xFE900DE8, Llsl = 0x72D31F9D, Llsr = 0xE46C47E6, Lasr = 0x1D89F6C0;
    private const uint Snprintf = 0xAE7A8981, Vsnprintf = 0x3DDBE2E1, Prnt = 0xE38E7605;
    private const uint MemcpyChk = 0x8A0B0815, MemmoveChk = 0x35DBB110, MemsetChk = 0x1A30BB28, StrncpyChk = 0x96268C53,
        StrncpyChk2 = 0x545DA5FD, StrncatChk = 0x33EE298B, StrncatChk2 = 0xDE4666F0, StrlcpyChk = 0xCF86EA38, StrlcatChk = 0x224BE33F,
        VsnprintfChk = 0xCBF64DF6, SnprintfChk = 0x7DBE7007, StackChkFail = 0xB997493D;

    [Fact]
    public void MemcmpComparesAlignedWordsAsTheFirmwareDoes()
    {
        // aligned, a difference in a whole word: -1 / 1, not the byte difference
        uint a = Bytes(1, 2, 3, 4, 5, 6, 7, 8);
        uint b = Bytes(1, 2, 3, 4, 5, 6, 7, 0x80);
        Assert.Equal(-1, (int)C(Memcmp, a, b, 8));
        Assert.Equal(1, (int)C(Memcmp, b, a, 8));
        // a difference in the last partial word: its first n bytes as a big-endian number
        uint c = Bytes(1, 2, 3, 4, 0x10, 0x20, 0x30, 0);
        uint d = Bytes(1, 2, 3, 4, 0x10, 0x10, 0x30, 0);
        Assert.Equal(0x1000, (int)C(Memcmp, c, d, 7));
        // bytes past n do not count
        Assert.Equal(0, (int)C(Memcmp, a, b, 7));
        // not co-aligned: the byte difference
        Assert.Equal(8 - 0x80, (int)C(Memcmp, a + 1, b + 1, 7));
        Assert.Equal(0, (int)C(Memcmp, a, a, 8));
        Assert.Equal(0, (int)C(Memcmp, a, b, 0));
        // short compares go byte by byte
        Assert.Equal(-2, (int)C(Memcmp, Bytes(1, 2), Bytes(1, 4), 2));
    }

    [Fact]
    public void MemoryFunctionsFollowTheFirmware()
    {
        uint buffer = Bytes(1, 2, 3, 4, 5, 6, 7, 8);
        Assert.Equal(buffer + 2, C(Memmove, buffer + 2, buffer, 5));
        Assert.Equal(new byte[] { 1, 2, 1, 2, 3, 4, 5, 8 }, GuestMemory.Span(buffer, 8).ToArray());
        Assert.Equal(buffer + 1, C(Memchr, buffer, 0x102, 8));
        Assert.Equal(0u, C(Memchr, buffer, 9, 8));
        Assert.Equal(unchecked((uint)-1), C(TimingsafeMemcmp, Bytes(1, 2, 9), Bytes(1, 3, 0), 3));
        Assert.Equal(0u, C(TimingsafeMemcmp, Bytes(1, 2), Bytes(1, 2), 2));
    }

    [Fact]
    public void StringFunctionsFollowTheFirmware()
    {
        uint text = Str("kernel-kernel");
        Assert.Equal(13u, C(Strlen, text));
        Assert.Equal(4u, C(Strnlen, text, 4));
        Assert.Equal(0u, C(Strcmp, Str("abc"), Str("abc")));
        Assert.Equal(0xE1 - 'b', (int)C(Strcmp, Bytes(0x61, 0xE1, 0), Str("ab")));
        Assert.Equal(text + 6, C(Strchr, text, '-'));
        Assert.Equal(text + 13, C(Strchr, text, 0));
        Assert.Equal(0u, C(Strchr, text, 'z'));
        Assert.Equal(text + 12, C(Strrchr, text, 'l'));
        Assert.Equal(text, C(Strstr, text, Str("kern")));
        Assert.Equal(text + 7, C(Strstr, text + 1, Str("kern")));
        Assert.Equal(text, C(Strstr, text, Str("")));
        Assert.Equal(0u, C(Strstr, text, Str("x")));

        // strncpy fills the rest with zeros; strncpy_s writes the terminator only when it fits
        uint copy = Buffer(8);
        Assert.Equal(copy, C(Strncpy, copy, Str("ab"), 6));
        Assert.Equal(new byte[] { 0x61, 0x62, 0, 0, 0, 0, 0xAA }, GuestMemory.Span(copy, 7).ToArray());
        uint copyS = Buffer(8);
        Assert.Equal(copyS, C(StrncpyS, copyS, Str("abcd"), 3));
        Assert.Equal(new byte[] { 0x61, 0x62, 0x63, 0xAA }, GuestMemory.Span(copyS, 4).ToArray());

        // strncat: an empty source writes nothing, not even a terminator
        uint cat = Bytes(0x61, 0, 0x55, 0x55);
        Assert.Equal(cat, C(Strncat, cat, Str(""), 4));
        Assert.Equal(0x55, GuestMemory.Read<byte>(cat + 2));
        Assert.Equal(cat, C(Strncat, cat, Str("xyz"), 2));
        Assert.Equal("axy", GuestMemory.ReadCString(cat));

        // strlcpy / strlcat answer the length they tried to make
        uint small = Buffer(4);
        Assert.Equal(6u, C(Strlcpy, small, Str("abcdef"), 4));
        Assert.Equal("abc", GuestMemory.ReadCString(small));
        uint join = Buffer(8, 0);
        GuestMemory.WriteCString(join, "ab");
        Assert.Equal(6u, C(Strlcat, join, Str("cdef"), 5));
        Assert.Equal("abcd", GuestMemory.ReadCString(join));
        Assert.Equal(7u, C(Strlcat, join, Str("xyz"), 4));
    }

    [Fact]
    public void CtypeIsTheFirmwaresTable()
    {
        Assert.Equal(0x18u, C(LookCtype, ' '));
        Assert.Equal(0x08u, C(LookCtype, '\t'));
        Assert.Equal(0x41u, C(LookCtype, 'C'));
        Assert.Equal(0x02u, C(LookCtype, 'q'));
        Assert.Equal(0x04u, C(LookCtype, '7'));
        Assert.Equal(0x10u, C(LookCtype, '~'));
        Assert.Equal(0x20u, C(LookCtype, 0x7F));
        Assert.Equal(0u, C(LookCtype, 0x80));
        Assert.Equal((uint)'A', C(Toupper, 'a'));
        Assert.Equal(0xE1u, C(Toupper, 0xE1));
        Assert.Equal((uint)'z', C(Tolower, 'Z'));
        Assert.Equal((uint)'5', C(Tolower, '5'));
    }

    [Fact]
    public void StrtolAndStrtoulHaveTheFirmwaresPrefixesAndNoOverflowCheck()
    {
        uint end = Buffer(4);
        uint text = Str("  -0x1Az");
        Assert.Equal(-26, (int)C(Strtol, text, end, 0));
        Assert.Equal(text + 7, GuestMemory.R32(end));
        // "o" is octal whatever the base; "0b" is binary for base 0
        Assert.Equal(15u, C(Strtol, Str("o17"), 0, 10));
        Assert.Equal(5u, C(Strtol, Str("0b101"), 0, 0));
        Assert.Equal(8u, C(Strtol, Str("010"), 0, 0));
        // no overflow check: the value wraps
        Assert.Equal(1u, C(Strtol, Str("4294967297"), 0, 10));
        // no digit: *end is after the sign, not the start
        uint noDigits = Str("-z");
        Assert.Equal(0u, C(Strtol, noDigits, end, 10));
        Assert.Equal(noDigits + 1, GuestMemory.R32(end));

        // strtoul: no sign, base 0 is 10, the prefixes whatever the base
        uint negative = Str("-5");
        Assert.Equal(0u, C(Strtoul, negative, end, 10));
        Assert.Equal(negative, GuestMemory.R32(end));
        Assert.Equal(10u, C(Strtoul, Str("010"), 0, 0));
        Assert.Equal(31u, C(Strtoul, Str("0x1f"), 0, 10));
        Assert.Equal(3u, C(Strtoul, Str("0b11"), 0, 16));
        Assert.Equal(7u, C(Strtoul, Str(" O7"), 0, 10));
    }

    [Fact]
    public void StrtollClampsAndKeepsTheStandardEnd()
    {
        uint end = Buffer(4);
        uint big = Str("99999999999999999999x");
        Assert.Equal(long.MaxValue, (long)C64(Strtoll, big, end, 10));
        Assert.Equal(big + 20, GuestMemory.R32(end));
        Assert.Equal(long.MinValue, (long)C64(Strtoll, Str("-99999999999999999999"), 0, 0));
        Assert.Equal(0x7BL, (long)C64(Strtoll, Str(" 0x7B"), 0, 0));
        Assert.Equal(-8L, (long)C64(Strtoll, Str("-010"), 0, 0));
        // no digit or a bad base: 0 and *end at the start
        uint none = Str("  +z");
        Assert.Equal(0UL, C64(Strtoll, none, end, 10));
        Assert.Equal(none, GuestMemory.R32(end));
        uint badBase = Str("12");
        Assert.Equal(0UL, C64(Strtoll, badBase, end, 1));
        Assert.Equal(badBase, GuestMemory.R32(end));
        // "0x" without a hex digit: the 0, and *end at the x
        uint hexless = Str("0xg");
        Assert.Equal(0UL, C64(Strtoll, hexless, end, 16));
        Assert.Equal(hexless + 1, GuestMemory.R32(end));
    }

    [Fact]
    public void DivisionHelpersAnswerTheFirmwaresDivideByZero()
    {
        Assert.Equal(uint.MaxValue, C(Uidiv, 7, 0));
        Assert.Equal(0u, C(Uidiv, 0, 0));
        Assert.Equal(3u, C(Uidiv, 10, 3));
        Assert.Equal((1UL << 32) | 3, C64(Uidivmod, 10, 3));
        Assert.Equal((uint)int.MaxValue, C(Idiv, 5, 0));
        Assert.Equal(unchecked((uint)int.MinValue), C(Idiv, unchecked((uint)-5), 0));
        Assert.Equal(unchecked((uint)int.MinValue), C(Idiv, unchecked((uint)int.MinValue), unchecked((uint)-1)));
        Assert.Equal(unchecked((uint)-3), C(Idiv, unchecked((uint)-7), 2));
        ulong idivmod = C64(Idivmod, unchecked((uint)-7), 2);
        Assert.Equal(-3, (int)(uint)idivmod);
        Assert.Equal(-1, (int)(uint)(idivmod >> 32));

        // 64-bit: quotient in r0:r1, remainder in r2:r3
        Assert.Equal(3UL, C64(Uldivmod, 10, 0, 3, 0));
        Assert.Equal(1UL, Remainder(Uldivmod, 10, 0, 3, 0));
        Assert.Equal(ulong.MaxValue, C64(Uldivmod, 1, 0, 0, 0));
        Assert.Equal(-3L, (long)C64(Ldivmod, unchecked((uint)-10), uint.MaxValue, 3, 0));
        Assert.Equal(-1L, (long)Remainder(Ldivmod, unchecked((uint)-10), uint.MaxValue, 3, 0));
        // divide by zero: the sign word is the high word, or the low word when the high word is 0
        Assert.Equal(long.MinValue, (long)C64(Ldivmod, 0x80000000, 0, 0, 0));
        Assert.Equal(long.MaxValue, (long)C64(Ldivmod, 1, 0, 0, 0));
        Assert.Equal(0L, (long)C64(Ldivmod, 0, 0, 0, 0));
        Assert.Equal(long.MinValue, (long)C64(Ldivmod, 0, 0x80000000, 0, 0));
    }

    [Fact]
    public void SixtyFourBitHelpersFollowArmShifts()
    {
        Assert.Equal(0x0000000300000006UL, C64(Lmul, 2, 1, 3, 0));
        Assert.Equal(1UL << 40, C64(Llsl, 1, 0, 40));
        Assert.Equal(0x0000012345678000UL, C64(Llsl, 0x12345678, 0, 12));
        Assert.Equal(0x0000000000FF0000UL, C64(Llsr, 0, 0xFF000000, 40));
        Assert.Equal(0xFFFFFFFFFFFF0000UL, C64(Lasr, 0, 0x80000000, 47));
        Assert.Equal(0xF800000000000000UL, C64(Lasr, 0, 0x80000000, 4));
        Assert.Equal(uint.MaxValue, C(Lcmp, 0, 0xFFFFFFFF, 0, 0));
        Assert.Equal(1u, C(Ulcmp, 0, 0xFFFFFFFF, 0, 0));
        Assert.Equal(0u, C(Ulcmp, 5, 1, 5, 1));
    }

    private string Format(string format, params uint[] args)
    {
        uint buffer = Buffer(64);
        uint length = C(Vsnprintf, buffer, 64, Str(format), Words(args));
        string text = GuestMemory.ReadCString(buffer);
        Assert.Equal((uint)text.Length, length);
        return text;
    }

    [Fact]
    public void TheFormatterLaysOutFieldsAsTheFirmware()
    {
        Assert.Equal("-42|42|+7| 7", Format("%d|%u|%+d|% d", unchecked((uint)-42), 42, 7, 7));
        // the sign and the 0x prefix count toward the precision
        Assert.Equal("+05|-05|0x0f", Format("%+.3d|%.3d|%#.4x", 5, unchecked((uint)-5), 15));
        // "0" pads strings too; %p is 0x and zero-padded to 10
        Assert.Equal("000ab|0x00001234|0x1234  ", Format("%05s|%p|%-0#8x", Str("ab"), 0x1234, 0x1234));
        Assert.Equal("(null)|(nu|x|  ab|ab  ", Format("%s|%.3s|%c|%4s|%-4s", 0, 0, 'x', Str("ab"), Str("ab")));
        // no floating point: an unknown conversion is written as is
        Assert.Equal("f|%|q", Format("%f|%%|%q"));
        Assert.Equal("010|0|7F|0X7F", Format("%#o|%#o|%X|%#X", 8, 0, 0x7F, 0x7F));
        // .0 with a zero value writes nothing; precision clears the 0 flag
        Assert.Equal("[]|[   5]", Format("[%.0d]|[%04.1d]", 0, 5));
        // 64-bit arguments are 8-byte aligned in the va_list; L and ll; x keeps the sign flag
        Assert.Equal("-4294967296|1fffffffe|+ff", Format("%lld|%Lx|%+Lx", 0, 0xFFFFFFFF, 0xFFFFFFFE, 1, 0xFF, 0));
        Assert.Equal("*  9|9  |", Format("*%*d|%*d|", 3, 9, unchecked((uint)-3), 9));
    }

    [Fact]
    public void SnprintfCountsEverythingAndStoresWhatFits()
    {
        uint buffer = Buffer(8);
        Assert.Equal(11u, C(Snprintf, buffer, 5, Str("%s-%d"), Str("hello"), 12345));
        Assert.Equal("hell", GuestMemory.ReadCString(buffer));
        uint untouched = Buffer(4);
        Assert.Equal(3u, C(Snprintf, untouched, 0, Str("abc")));
        Assert.Equal(0xAA, GuestMemory.Read<byte>(untouched));
        // the variadic words continue on the caller's stack after r3
        uint many = Buffer(64);
        Assert.Equal(7u, C(Snprintf, many, 64, Str("%d%d%d%d"), 1, 22, 333, 4));
        Assert.Equal("1223334", GuestMemory.ReadCString(many));
    }

    [Fact]
    public void PrntCallsTheCallbackWithBeginEveryCharacterAndEnd()
    {
        var calls = new List<(uint Argument, int Character)>();
        uint callback = HostFunction("test:prnt-callback", context => calls.Add((context.Gpr(0), (int)context.Gpr(1))));
        uint count = C(Prnt, callback, 0x1234, Bytes((byte)'a', (byte)'%', (byte)'x', (byte)'b', 0xC3, 0xA9, 0), Words(0xBEEF));
        Assert.Equal(8u, count);
        Assert.All(calls, c => Assert.Equal(0x1234u, c.Argument));
        Assert.Equal([0x200, 'a', 'b', 'e', 'e', 'f', 'b', unchecked((int)0xFFFFFFC3), unchecked((int)0xFFFFFFA9), 0x201],
            calls.Select(c => c.Character).ToArray());
        // no format: no call at all
        calls.Clear();
        Assert.Equal(0u, C(Prnt, callback, 0, 0, 0));
        Assert.Empty(calls);
    }

    [Fact]
    public void TheCheckedFunctionsWorkWhenTheBufferIsLargeEnough()
    {
        uint destination = Buffer(16);
        Assert.Equal(destination, C(MemcpyChk, destination, Str("abcd"), 5, 16));
        Assert.Equal("abcd", GuestMemory.ReadCString(destination));
        Assert.Equal(destination, C(MemsetChk, destination, 'z', 3, 3));
        Assert.Equal("zzzd", GuestMemory.ReadCString(destination));
        Assert.Equal(destination + 1, C(MemmoveChk, destination + 1, destination, 2, 2));
        Assert.Equal(destination, C(StrncpyChk, destination, Str("q"), 4, 8));
        Assert.Equal(destination, C(StrncpyChk2, destination, Str("qq"), 4));
        Assert.Equal(destination, C(StrncatChk, destination, Str("r"), 2, 8));
        Assert.Equal("qqr", GuestMemory.ReadCString(destination));
        Assert.Equal(destination, C(StrncatChk2, destination, Str("s"), 8));
        Assert.Equal(2u, C(StrlcpyChk, destination, Str("xy"), 8, 8));
        Assert.Equal(4u, C(StrlcatChk, destination, Str("zw"), 8, 16));
        Assert.Equal("xyzw", GuestMemory.ReadCString(destination));
        Assert.Equal(2u, C(VsnprintfChk, destination, 8, 0, 8, Str("%d"), Words(42)));
        Assert.Equal(3u, C(SnprintfChk, destination, 8, 0, 8, Str("%d"), 123));
        Assert.Equal("123", GuestMemory.ReadCString(destination));
    }

    [Fact]
    public void AFailedCheckStopsTheSystem()
    {
        uint destination = Buffer(16);
        (uint Nid, uint[] Args)[] failing =
        [
            (MemcpyChk, [destination, destination + 8, 8, 4]),
            (MemmoveChk, [destination, destination + 8, 8, 4]),
            (MemsetChk, [destination, 0, 8, 4]),
            (StrncpyChk, [destination, Str("a"), 8, 4]),
            (StrncatChk, [destination, Str("a"), 8, 4]),
            (StrncatChk2, [Str("abcd"), Str("a"), 4]),
            (StrlcpyChk, [destination, Str("a"), 8, 4]),
            (StrlcatChk, [destination, Str("a"), 8, 4]),
            (VsnprintfChk, [destination, 8, 0, 4, Str("x"), 0]),
            (SnprintfChk, [destination, 8, 0, 4, Str("x")]),
            (StackChkFail, []),
        ];
        foreach ((uint nid, uint[] args) in failing)
        {
            (_, _, Exception? fault) = Run(Stub(nid), args);
            Assert.True(fault is GuestFaultException, $"0x{nid:X8}: {fault?.GetType().Name ?? "no fault"}");
            Assert.Contains("BKPT #0x81", fault!.Message);
        }

        // nothing was written by the failed copies
        Assert.Equal(0xAA, GuestMemory.Read<byte>(destination));
    }
}
