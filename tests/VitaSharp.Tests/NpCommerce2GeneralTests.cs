using System.Text;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The last general-group functions of SceNpCommerce2 (np_commerce2.suprx FW 3.74): four result parsers (also checked
/// against the firmware by NpCommerce2FirmwareTests), two request twins, the request with required stack arguments,
/// deleting every context, the content-id string helpers, the sign-up field validator and the sign-up flow. With the
/// firmware's np_commerce2.suprx at hand the string helpers, the validator and the sign-up argument checks also run
/// through the module's own exports, and the answers must match.
/// </summary>
[Collection("guest")]
public sealed class NpCommerce2GeneralTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int NotInitialized = unchecked((int)0x80550F01);
    private const int InvalidArgument = unchecked((int)0x80550F03);
    private const int BadString = unchecked((int)0x80550F07);

    private readonly HleHarness _harness = new(fixture, "c2-general");

    public void Dispose() => _harness.Dispose();

    private NpCommerce2 Commerce => _harness.Module<NpCommerce2>();

    [Theory]
    [InlineData(0x043F86FFu)]
    [InlineData(0x303DE914u)]
    [InlineData(0x31782CA7u)]
    [InlineData(0x60187EF1u)]
    [InlineData(0x87A71049u)]
    [InlineData(0x884BF45Au)]
    [InlineData(0x922382C1u)]
    [InlineData(0x9AF7E167u)]
    [InlineData(0xA94C2A99u)]
    [InlineData(0xB45A7935u)]
    [InlineData(0xDFC84F4Fu)]
    [InlineData(0xE46F89EAu)]
    [InlineData(0xF9DE9CDFu)]
    public void TheLibrarysLastFunctionsHaveAnEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Nid == nid).ToList();
        Assert.NotEmpty(bindings);
        Assert.All(bindings, b => Assert.False(b.Default));
        Assert.NotNull(_harness.Emu.Hle.ImplOf(nid));
    }

    [Fact]
    public void ContentIdHelpersFindTheDashes()
    {
        uint id = _harness.Str("UP9000-PCSA00001_00-GAMEDATA-EXTRA");
        Assert.Equal(19, Call(Commerce, "ContentIdSecondDash", id));
        Assert.Equal(0, Call(Commerce, "CutContentId", id));
        Assert.Equal("UP9000-PCSA00001_00-GAMEDATA", GuestMemory.ReadCString(id));
        Assert.Equal(0, Call(Commerce, "CutContentId", id));
        Assert.Equal("UP9000-PCSA00001_00-GAMEDATA", GuestMemory.ReadCString(id));
        Assert.Equal(BadString, Call(Commerce, "CutContentId", _harness.Str("UP9000-PCSA00001")));
        Assert.Equal(BadString, Call(Commerce, "CutContentId", 0u));
        Assert.Equal(InvalidArgument, Call(Commerce, "ContentIdSecondDash", _harness.Str("NODASH")));
        Assert.Equal(InvalidArgument, Call(Commerce, "ContentIdSecondDash", 0u));
    }

    [Theory]
    [InlineData("IP9100-NPIA09002_00-CC", 1u)]
    [InlineData("IP9100-NPIA09006_00-CCXYZ", 1u)]
    [InlineData("IP9100-NPIA09001_00-CC", 0u)]
    [InlineData("IP9100-NPIA09007_00-CC", 0u)]
    [InlineData("IP9100-NPIA09003_00-C", 0u)]
    [InlineData("IP9100-NPIA09003_01-CC", 0u)]
    [InlineData("UP9100-NPIA09003_00-CC", 0u)]
    public void LinkContentIdIsRecognised(string contentId, uint expected)
    {
        uint result = _harness.Word(0x55);
        Assert.Equal(0, Call(Commerce, "IsLinkContentId", _harness.Str(contentId), result));
        Assert.Equal(expected, GuestMemory.R32(result));
        Assert.Equal(InvalidArgument, Call(Commerce, "IsLinkContentId", 0u, result));
    }

    [Theory]
    [InlineData(0x28, "", 0x01u)]
    [InlineData(0x28, "abc12345", 0u)]
    [InlineData(0x28, "abc1", 0x02u)]
    [InlineData(0x28, "abcdefgh", 0x04u)]
    [InlineData(0x28, "12345678", 0x08u)]
    [InlineData(0x28, "aAa12345", 0x10u)]
    [InlineData(0x28, "ab1ab1ab", 0u)]
    [InlineData(0x28, "ababab12", 0u)]
    [InlineData(0x28, "a1a1a1a1", 0x20u)]
    [InlineData(0x28, "pass\tword1", 0x80u)]
    [InlineData(0x27, "user@host.com", 0u)]
    [InlineData(0x27, "@host.com", 0x80u)]
    [InlineData(0x27, "user@host", 0x80u)]
    [InlineData(0x27, "user.name@", 0x80u)]
    [InlineData(0x27, "", 0x01u)]
    [InlineData(0x10, "Vita_Fan-9", 0u)]
    [InlineData(0x10, "9vita", 0x40u)]
    [InlineData(0x10, "ab", 0x02u)]
    [InlineData(0x10, "ab$cd", 0x80u)]
    [InlineData(0xFF00, "any text, with spaces", 0u)]
    [InlineData(0xFF00, "", 0x01u)]
    public void SignUpFieldsAreChecked(int kind, string value, uint expected)
    {
        Assert.Equal(0, Call(Commerce, "sceNpCommerce2Init"));
        uint verdict = _harness.Word(0xFFFFFFFF);
        Assert.Equal(0, Call(Commerce, "CheckSignUpField", kind, _harness.Str(value), verdict));
        Assert.Equal(expected, GuestMemory.R32(verdict));
    }

    [Fact]
    public void SignUpFieldCheckNeedsTheLibraryAndAKnownKind()
    {
        uint verdict = _harness.Word(0);
        uint value = _harness.Str("abc12345");
        Assert.Equal(NotInitialized, Call(Commerce, "CheckSignUpField", 0x28, value, verdict));
        Assert.Equal(0, Call(Commerce, "sceNpCommerce2Init"));
        Assert.Equal(InvalidArgument, Call(Commerce, "CheckSignUpField", 0x28, 0u, verdict));
        Assert.Equal(InvalidArgument, Call(Commerce, "CheckSignUpField", 0x28, value, 0u));
        Assert.Equal(InvalidArgument, Call(Commerce, "CheckSignUpField", 0x29, value, verdict));
    }

    /// <summary>The sign-up flow checks its arguments and the two-letter codes, then refuses the production NP
    /// environment.</summary>
    [Fact]
    public void SignUpIsRefusedInTheProductionEnvironment()
    {
        uint us = _harness.Str("us");
        uint en = _harness.Str("en");
        uint buffer = _harness.Alloc(0x40);
        Assert.Equal(unchecked((int)0x80550FD3), Call(Commerce, "SignUpAccount", 1u, 1u, us, en, 1, 0u, 0u, 0u, 0u, 0u, 1u, 1u, 1u));
        Assert.Equal(unchecked((int)0x80550FD3), Call(Commerce, "SignUpAccount", 1u, 1u, us, en, 0, 0u, 0u, 0u, buffer, 0x1Fu, 1u, 1u, 1u));
        Assert.Equal(InvalidArgument, Call(Commerce, "SignUpAccount", 1u, 1u, us, en, 0, 0u, 0u, 0u, buffer, 0x1Eu, 1u, 1u, 1u));
        Assert.Equal(InvalidArgument, Call(Commerce, "SignUpAccount", 1u, 1u, us, en, 1, 0u, 0u, 0u, 0u, 0u, 1u, 0u, 1u));
        Assert.Equal(InvalidArgument, Call(Commerce, "SignUpAccount", 1u, 1u, _harness.Str("u"), en, 1, 0u, 0u, 0u, 0u, 0u, 1u, 1u, 1u));
    }

    [Fact]
    public void DeletingEveryContextTakesTheRequestsToo()
    {
        Assert.Equal(NotInitialized, Call(Commerce, "DeleteAllContexts"));
        Assert.Equal(0, Call(Commerce, "sceNpCommerce2Init"));
        uint ctx = _harness.Word(0);
        Assert.Equal(0, Call(Commerce, "sceNpCommerce2CreateCtx", 2u, new Ptr<uint>(ctx)));
        uint ctxId = GuestMemory.R32(ctx);
        uint req = _harness.Word(0);
        Assert.Equal(0, Call(Commerce, "Create07C16199", ctxId, req));
        uint reqId = GuestMemory.R32(req);
        Assert.Equal(0, Call(Commerce, "DeleteAllContexts"));
        Assert.Equal(unchecked((int)0x80550F0C), Call(Commerce, "StoreOp", reqId));
        Assert.Equal(unchecked((int)0x80550F0C), Call(Commerce, "StoreOp922382C1", reqId, 0u, 1u, 1u));
        Assert.Equal(InvalidArgument, Call(Commerce, "StoreOp922382C1", reqId, 1u, 0u, 1u));
    }
}

/// <summary>The NpCommerce2GeneralTests functions that read no store reply, through np_commerce2.suprx's own exports
/// and the HLE: the string helpers, the sign-up field validator and the sign-up argument checks must answer the same.
/// Skipped without the firmware module.</summary>
[Collection("guest")]
public sealed class NpCommerce2GeneralFirmwareTests(GuestMemoryFixture fixture)
{
    private static readonly string[] s_fields =
    [
        "", "a", "ab", "abc", "abc1", "abc12345", "abcdefgh", "12345678", "aAa12345", "ab1ab1ab", "ababab12", "a1a1a1a1",
        "aab12", "Pass word1", "pass\tword1", "éabc1234", new string('x', 40) + "1", "user@host.com", "@host.com",
        "user@host", "user.name@", "a.b@c", "a@b.c", "Vita_Fan-9", "9vita", "ab$cd", "ok-id_1", new string('a', 17),
        "aBc", "AAAb1234", "zz", "abc@def.", "x@.",
    ];

    private static readonly int[] s_kinds = [0xFF00, 0x28, 0x27, 0x10, 0x11];

    [C2FirmwareFact]
    public void StringHelpersAndValidatorMatchTheFirmware()
    {
        using var c2 = new C2Harness(fixture, C2Firmware.ModulePath);
        var differences = new List<string>();
        foreach (int kind in s_kinds)
        {
            foreach (string field in s_fields)
            {
                uint fwValue = Text(c2, field);
                uint hleValue = Text(c2, field);
                uint fwVerdict = c2.Scratch(4);
                uint hleVerdict = c2.Scratch(4);
                int fw = c2.CallFirmware(0x60187EF1, (uint)kind, fwValue, fwVerdict);
                int hle = c2.CallHle("CheckSignUpField", kind, hleValue, hleVerdict);
                if (fw != hle || GuestMemory.R32(fwVerdict) != GuestMemory.R32(hleVerdict))
                {
                    differences.Add($"60187EF1 {kind:X} \"{field}\": fw {fw:X8}/{GuestMemory.R32(fwVerdict):X} hle {hle:X8}/{GuestMemory.R32(hleVerdict):X}");
                }
            }
        }

        string[] ids =
        [
            "UP9000-PCSA00001_00-GAMEDATA-EXTRA", "UP9000-PCSA00001_00-GAMEDATA", "UP9000-PCSA00001", "NODASH", "-",
            "--", "---", "IP9100-NPIA09002_00-CC", "IP9100-NPIA09006_00-CCXYZ", "IP9100-NPIA09001_00-CC",
            "IP9100-NPIA09007_00-CC", "IP9100-NPIA09003_00-C", "IP9100-NPIA09003_01-CC", "ip9100-npia09003_00-cc",
        ];
        foreach (string id in ids)
        {
            Compare(c2, differences, 0x884BF45A, "CutContentId", id, withResult: false, compareText: true);
            Compare(c2, differences, 0x87A71049, "ContentIdSecondDash", id, withResult: false, compareText: false);
            Compare(c2, differences, 0x043F86FF, "IsLinkContentId", id, withResult: true, compareText: false);
        }

        Assert.True(differences.Count == 0, string.Join("\n", differences.Take(30)));
    }

    /// <summary>The sign-up flow's argument and code-length checks, and the refusal of the production environment ("np",
    /// which np_common gives the firmware module here too), all before any request.</summary>
    [C2FirmwareFact]
    public void SignUpArgumentChecksMatchTheFirmware()
    {
        using var c2 = new C2Harness(fixture, C2Firmware.ModulePath);
        uint us = Text(c2, "us");
        uint en = Text(c2, "en");
        uint shortCode = Text(c2, "u");
        uint empty = Text(c2, "");
        uint buffer = c2.Scratch(0x40);
        uint[][] cases =
        [
            [1, 0, us, en, 1, 0, 0, 0, 0, 0, 1, 1, 1],
            [1, 1, 0, en, 1, 0, 0, 0, 0, 0, 1, 1, 1],
            [1, 1, us, 0, 1, 0, 0, 0, 0, 0, 1, 1, 1],
            [1, 1, us, en, 1, 0, 0, 0, 0, 0, 0, 1, 1],
            [1, 1, us, en, 1, 0, 0, 0, 0, 0, 1, 0, 1],
            [1, 1, us, en, 1, 0, 0, 0, 0, 0, 1, 1, 0],
            [1, 1, us, en, 0, 0, 0, 0, 0, 0x1F, 1, 1, 1],
            [1, 1, us, en, 0, 0, 0, 0, buffer, 0x1E, 1, 1, 1],
            [1, 1, shortCode, en, 0, 0, 0, 0, buffer, 0x1F, 1, 1, 1],
            [1, 1, us, empty, 0, 0, 0, 0, buffer, 0x1F, 1, 1, 1],
            [1, 1, shortCode, en, 1, 0, 0, 0, 0, 0, 1, 1, 1],
            [1, 1, us, en, 1, 0, 0, 0, 0, 0, 1, 1, 1],
            [1, 1, us, en, 0, 0, 0, 0, buffer, 0x1F, 1, 1, 1],
        ];
        var differences = new List<string>();
        foreach (uint[] arguments in cases)
        {
            int fw = c2.CallFirmware(0xF9DE9CDF, arguments);
            int hle = c2.CallHle("SignUpAccount", arguments[0], arguments[1], arguments[2], arguments[3], (int)arguments[4],
                arguments[5], arguments[6], arguments[7], arguments[8], arguments[9], arguments[10], arguments[11], arguments[12]);
            if (fw != hle)
            {
                differences.Add($"F9DE9CDF [{string.Join(", ", arguments.Select(a => a.ToString("X")))}]: fw {fw:X8} hle {hle:X8}");
            }
        }

        Assert.True(differences.Count == 0, string.Join("\n", differences));
    }

    private static uint Text(C2Harness c2, string text)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(text);
        uint p = c2.Scratch((uint)bytes.Length + 1);
        bytes.CopyTo(GuestMemory.Span(p, bytes.Length));
        return p;
    }

    private static void Compare(C2Harness c2, List<string> differences, uint nid, string method, string text, bool withResult, bool compareText)
    {
        uint fwText = Text(c2, text);
        uint hleText = Text(c2, text);
        uint fwResult = c2.Scratch(4);
        uint hleResult = c2.Scratch(4);
        int fw = withResult ? c2.CallFirmware(nid, fwText, fwResult) : c2.CallFirmware(nid, fwText);
        int hle = withResult ? c2.CallHle(method, hleText, hleResult) : c2.CallHle(method, hleText);
        string fwAfter = GuestMemory.ReadCString(fwText);
        string hleAfter = GuestMemory.ReadCString(hleText);
        if (fw != hle || GuestMemory.R32(fwResult) != GuestMemory.R32(hleResult) || (compareText && fwAfter != hleAfter))
        {
            differences.Add($"{nid:X8} \"{text}\": fw {fw:X8}/{GuestMemory.R32(fwResult)}/{fwAfter} hle {hle:X8}/{GuestMemory.R32(hleResult)}/{hleAfter}");
        }
    }
}
