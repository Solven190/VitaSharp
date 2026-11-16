using VitaSharp.Core.Hle;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// Small exports of the FW 3.74 user libraries that the system modules import (decompiled, study only): apputil's
/// SDK version record, event callback and background download count; np_common's request record initializer and
/// atoi; np_matching2's option word. The exports are found by name so the proof without them still builds.
/// </summary>
[Collection("guest")]
public sealed class SystemImportsHelpersTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int NotInitialized = unchecked((int)0x80100601);

    private readonly HleHarness _harness = new(fixture, "sysimp1");

    public void Dispose() => _harness.Dispose();

    private HleModule ModuleNamed(string name) => _harness.Emu.Hle.Modules.Single(module => module.GetType().Name == name);

    private HleModule AppUtil => ModuleNamed("AppUtil");

    private void InitAppUtil() =>
        Assert.Equal(0, Call(AppUtil, "sceAppUtilInit", _harness.Alloc(0x40), _harness.Alloc(0x40)));

    [Fact]
    public void TheAppUtilRecordHasTheSdkVersionFirst()
    {
        uint record = _harness.Alloc(0x40);
        Assert.Equal(unchecked((int)0x801006D0), Call(AppUtil, "NID_A3E0694F", 0u));
        Assert.Equal(NotInitialized, Call(AppUtil, "NID_A3E0694F", record));

        InitAppUtil();
        GuestMemory.Fill(record, 0xAA, 0x40);
        Assert.Equal(0, Call(AppUtil, "NID_A3E0694F", record));
        uint sdk = Convert.ToUInt32(Invoke(AppUtil, "MainModuleSdkVersion"));
        Assert.Equal(sdk, GuestMemory.R32(record));
        for (uint offset = 4; offset < 0x40; offset += 4)
        {
            Assert.Equal(0u, GuestMemory.R32(record + offset));
        }
    }

    [Fact]
    public void TheAppUtilEventCallbackGetsBothValuesAndItsArgument()
    {
        // str r1, [r2] ; bx lr: the callback stores its second value at its argument
        uint code = _harness.Alloc(8);
        GuestMemory.Write(code, (ushort)0x6011);
        GuestMemory.Write(code + 2, (ushort)0x4770);
        uint target = _harness.Word(0);
        _harness.OnGuest(_ =>
        {
            Invoke(AppUtil, "NID_F93C691C", 1u, 2u); // nothing registered: nothing called
            Assert.Equal(0, Call(AppUtil, "NID_CD0E5C59", code | 1, target));
            Invoke(AppUtil, "NID_F93C691C", 7u, 0x1234u);
            Assert.Equal(0x1234u, GuestMemory.R32(target));

            Assert.Equal(0, Call(AppUtil, "NID_CD0E5C59", 0u, target));
            Invoke(AppUtil, "NID_F93C691C", 7u, 0x5678u);
            Assert.Equal(0x1234u, GuestMemory.R32(target));
        });
    }

    [Fact]
    public void TheBackgroundDownloadCountIsEmptyWithoutDownloads()
    {
        uint waiting = _harness.Word(9);
        uint running = _harness.Word(9);
        Assert.Equal(NotInitialized, Call(AppUtil, "NID_B2FE3BB1", waiting, running));
        InitAppUtil();
        Assert.Equal(0, Call(AppUtil, "NID_B2FE3BB1", waiting, running));
        Assert.Equal(0u, GuestMemory.R32(waiting));
        Assert.Equal(0u, GuestMemory.R32(running));
        Assert.Equal(0, Call(AppUtil, "NID_B2FE3BB1", 0u, 0u));
    }

    [Fact]
    public void TheNpCommonHelpersInitializeTheRecordAndParseDecimal()
    {
        HleModule np = ModuleNamed("NpManager");
        uint record = _harness.Alloc(0x248);
        GuestMemory.Fill(record, 0xAA, 0x248);
        Assert.Equal(0, Call(np, "NID_650D111B", record, 0x11u, 0x22u, 0x33u));
        Assert.Equal(0x11u, GuestMemory.R32(record));
        Assert.Equal(0u, GuestMemory.R32(record + 4));
        Assert.Equal(0u, GuestMemory.R32(record + 0x23C));
        Assert.Equal(0x22u, GuestMemory.R32(record + 0x240));
        Assert.Equal(0x33u, GuestMemory.R32(record + 0x244));

        Assert.Equal(-42, Call(np, "NID_CC8E6AC6", _harness.Str("-42xyz")));
        Assert.Equal(17, Call(np, "NID_CC8E6AC6", _harness.Str("017")));
        Assert.Equal(0, Call(np, "NID_CC8E6AC6", 0u));
    }

    [Fact]
    public void TheMatchingOptionWordIsKept()
    {
        HleModule matching = ModuleNamed("NpMatching2");
        Assert.Equal(unchecked((int)0x80550C0A), Call(matching, "NID_28CF3F83", 0u));
        Assert.Equal((0u, false), ((uint, bool))Invoke(matching, "InternalOption")!);
        Assert.Equal(0, Call(matching, "NID_28CF3F83", _harness.Word(0x5)));
        Assert.Equal((5u, true), ((uint, bool))Invoke(matching, "InternalOption")!);
    }
}
