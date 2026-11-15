using VitaSharp.Core.Firmware;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>How hle-coverage counts a function: the HLE's, a real module's (LLE), or missing.</summary>
public sealed class CoverageStatusTests
{
    [Fact]
    public void FunctionTheHleServesIsHleEvenWhereAModuleExportsIt()
    {
        Assert.Equal(new CoverageStatus.Counts(true, false, false), CoverageStatus.Of(true, true, false, true, true));
        Assert.Equal(new CoverageStatus.Counts(true, true, false), CoverageStatus.Of(true, true, true, true, false));
    }

    [Fact]
    public void FirmwareFallbackIsTheRealModulesWhereTheFirmwareExportsIt()
    {
        // libc, libpaf, sqlite...: the HLE stands in only when the module is missing, so the firmware's copy covers it
        Assert.Equal(new CoverageStatus.Counts(true, false, true), CoverageStatus.Of(true, false, false, true, false));
        // a fallback default is not counted as an HLE default either
        Assert.Equal(new CoverageStatus.Counts(true, false, true), CoverageStatus.Of(true, false, true, true, false));
        // no firmware module exports it: the fallback is all there is
        Assert.Equal(new CoverageStatus.Counts(true, false, false), CoverageStatus.Of(true, false, false, false, false));
    }

    [Fact]
    public void InstalledModuleCoversWhatTheHleLacks()
    {
        Assert.Equal(new CoverageStatus.Counts(true, false, true), CoverageStatus.Of(false, false, false, true, true));
        Assert.Equal(new CoverageStatus.Counts(false, false, false), CoverageStatus.Of(false, false, false, true, false));
    }
}
