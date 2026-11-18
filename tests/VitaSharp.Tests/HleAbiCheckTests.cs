using VitaSharp.Core.Firmware;
using VitaSharp.Core.Hle;

namespace VitaSharp.Tests;

/// <summary>The vita-headers prototype parser and register comparison behind build/hle-abi-check, on synthetic headers.</summary>
public sealed class HleAbiCheckTests
{
    private const string Header = """
        #include <psp2/types.h>
        typedef unsigned int SceUInt32;
        typedef unsigned long long SceUInt64;
        typedef SceUInt64 SceRtcTick2;
        typedef int SceUID;
        typedef struct SceThing { int a; int b; } SceThing;
        typedef enum SceMode { SCE_MODE_A = 1 } SceMode;
        typedef int (*SceCallback)(int arg);
        /* a comment with sceNotAFunction(int x); inside */
        int sceAbiPlain(SceUID id, void *out, SceMode mode, const char *name, int fifth);
        int sceAbiWide(SceUID id, SceRtcTick2 tick, SceUInt32 *out);
        int sceAbiFloat(SceUID id, float difference, SceCallback cb, void *userdata);
        double sceAbiDouble(double x, float y, double z);
        int sceAbiByValue(SceThing thing);
        int sceAbiVariadic(const char *fmt, ...);
        int sceAbiVoid(void);
        """;

    private static Dictionary<string, HleAbiCheck.Prototype> Parse() => HleAbiCheck.ParseHeaders([("include/psp2/abi.h", Header)]);

    private static string Registers(string name) => string.Join(",", HleAbiCheck.Layout(Parse()[name].Kinds) ?? ["unresolved"]);

    [Fact]
    public void PrototypesGiveTheAapcsVfpRegisters()
    {
        Assert.Equal("r0,r1,r2,r3,sp0", Registers("sceAbiPlain"));
        Assert.Equal("r0,r2r3,sp0", Registers("sceAbiWide"));          // a 64-bit typedef of a typedef: even pair, shifts the rest
        Assert.Equal("r0,s0,r1,r2", Registers("sceAbiFloat"));        // floats leave the core registers alone
        Assert.Equal("d0,s2,d2", Registers("sceAbiDouble"));
        Assert.Equal("unresolved", Registers("sceAbiByValue"));       // a struct by value is reported, not guessed
        Assert.Equal("r0,va@1", Registers("sceAbiVariadic"));
        Assert.Equal("", Registers("sceAbiVoid"));
        Assert.DoesNotContain("sceNotAFunction", Parse().Keys);
    }

    [Fact]
    public void BindingTokensDropTheContextAndTypes()
    {
        Assert.Equal(["r0", "s0", "r1", "sp0+8"], HleAbiCheck.BindingTokens("ctx,r0=uint,s0=float,r1=uint,sp0+8=long->int"));
        Assert.Empty(HleAbiCheck.BindingTokens("->void"));
    }

    /// <summary>The comparison on the real bindings: a prototype that matches an [Hle] function's registers is not
    /// reported, one whose arguments sit in other registers is a MISMATCH (a 64-bit timeout would move the output to
    /// the stack), and reading only a prefix is FEWER, listed only on request. Two 32-bit arguments swapped give the same
    /// registers: that kind of bug needs the firmware module, not this check.</summary>
    [Fact]
    public void CompareReportsBindingsThatReadOtherRegisters()
    {
        const string Location = """
            typedef unsigned int SceLocationHandle;
            int sceLocationGetLocationWithTimeout(SceLocationHandle handle, unsigned long long timeout, void *info);
            int sceLocationGetLocation(SceLocationHandle handle, void *info);
            int sceLocationClose(SceLocationHandle handle, int extra);
            """;
        Dictionary<string, HleAbiCheck.Prototype> prototypes = HleAbiCheck.ParseHeaders([("include/psp2/location.h", Location)]);
        List<HleAbiCheck.Finding> findings = HleAbiCheck.Compare(prototypes, HleBindings.Modules, includeFewer: true);
        HleAbiCheck.Finding timeout = Assert.Single(findings, finding => finding.Function == "sceLocationGetLocationWithTimeout");
        Assert.Equal("MISMATCH", timeout.Status);
        Assert.Equal("r0,r1,r2", timeout.HleLayout);
        Assert.Equal("r0,r2r3,sp0", timeout.HeaderLayout);
        Assert.DoesNotContain(findings, finding => finding.Function == "sceLocationGetLocation");
        Assert.Equal("FEWER", Assert.Single(findings, finding => finding.Function == "sceLocationClose").Status);
        Assert.DoesNotContain(HleAbiCheck.Compare(prototypes, HleBindings.Modules, includeFewer: false), finding => finding.Status == "FEWER");
    }
}
