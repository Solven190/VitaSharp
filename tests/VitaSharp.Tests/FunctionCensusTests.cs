using VitaSharp.Core.Firmware;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// The census behind hle-coverage (docs/hle/user-exports.md): which firmware module exports a user function and how
/// many system modules and games import it (each importer counted once, however many of its libraries or files list
/// the NID).
/// </summary>
public class FunctionCensusTests : IDisposable
{
    private const uint ExportedNid = 0x11111111, OtherNid = 0x22222222, GameOnlyNid = 0x33333333;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "census-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private void Write(string relativePath, byte[] image)
    {
        string path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, image);
    }

    [Fact]
    public void Scan_CountsEachImporterOnce()
    {
        var provider = new SceModuleBuilder("SceProvider");
        int function = provider.ReturnConstant(0);
        Write("vs0/sys/external/provider.suprx", provider.Start(0).Export("SceProvider", 0xAAAA0001, 0x0001, (ExportedNid, function), (OtherNid, function)).Build());
        // one module listing the same NID in two import libraries counts once
        Write("vs0/sys/external/client_a.suprx", new SceModuleBuilder("ClientA").Start(0)
            .Import("SceProvider", 0xAAAA0001, ExportedNid).Import("SceProviderAlias", 0xAAAA0002, ExportedNid).Build());
        Write("vs0/vsh/common/client_b.suprx", new SceModuleBuilder("ClientB").Start(0).Import("SceProvider", 0xAAAA0001, ExportedNid, OtherNid).Build());
        // a game counts once for its eboot.bin and its own module together
        Write("ux0/app/TEST00001/eboot.bin", new SceModuleBuilder("Game1").Start(0).Import("SceProvider", 0xAAAA0001, ExportedNid, GameOnlyNid).Build());
        Write("ux0/app/TEST00001/sce_module/own.suprx", new SceModuleBuilder("Own1").Start(0).Import("SceProvider", 0xAAAA0001, ExportedNid).Build());
        Write("ux0/app/TEST00002/eboot.bin", new SceModuleBuilder("Game2").Start(0).Import("SceProvider", 0xAAAA0001, ExportedNid).Build());

        var census = FunctionCensus.Scan(_root);

        Assert.Equal(("SceProvider", "vs0:sys/external/provider.suprx"), census.Exported[ExportedNid]);
        Assert.Equal(2, census.SystemImporterCount[ExportedNid]);
        Assert.Equal(1, census.SystemImporterCount[OtherNid]);
        Assert.Equal(2, census.GameImporterCount[ExportedNid]);
        Assert.Equal(1, census.GameImporterCount[GameOnlyNid]);
        Assert.False(census.SystemImporterCount.ContainsKey(GameOnlyNid));
        Assert.Equal(2, census.Games);
    }
}
