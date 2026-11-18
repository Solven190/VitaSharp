using System.Buffers.Binary;
using VitaSharp.Core.Gxm.Shader;

namespace VitaSharp.Tests;

/// <summary>
/// The "Precompilar shaders" scan of a title's files (<see cref="GxpScanner"/>): loose .gxp files, programs inside a
/// game archive (also across the scanner's read chunks), duplicates and "GXP\0" blocks that are not programs.
/// </summary>
public sealed class GxpScannerTests
{
    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vitasharp-gxpscan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A well-formed program (<see cref="GxpProgram.Validate"/>): the header, then a primary program of two
    /// instructions made of <paramref name="seed"/> (a distinct program per seed).</summary>
    internal static byte[] Program(bool fragment, uint seed)
    {
        byte[] d = new byte[0x100];
        BinaryPrimitives.WriteUInt32LittleEndian(d, 0x00505847); // "GXP\0"
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(8), (uint)d.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(0x14), fragment ? GxpProgram.FlagFragment : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(0x3C), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(0x40), 0xC0 - 0x40);
        for (int i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(0xC0 + i * 4), seed + (uint)i);
        }

        return d;
    }

    /// <summary>A "GXP\0" block with a plausible size whose parameter table lies far past its end.</summary>
    private static byte[] BrokenProgram()
    {
        byte[] d = Program(false, 99);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(0x24), 1000);
        return d;
    }

    [Fact]
    public void SyntheticProgramsAreWellFormed()
    {
        Assert.Null(GxpProgram.Validate(Program(false, 1)));
        Assert.Null(GxpProgram.Validate(Program(true, 2)));
        Assert.NotNull(GxpProgram.Validate(BrokenProgram()));
    }

    [Fact]
    public void FindsLooseArchivedAndChunkStraddlingProgramsOnce()
    {
        string dir = NewDir();
        try
        {
            byte[] vertex = Program(false, 1);
            byte[] fragment = Program(true, 2);
            byte[] straddling = Program(true, 3);
            File.WriteAllBytes(Path.Combine(dir, "a.gxp"), vertex);
            Directory.CreateDirectory(Path.Combine(dir, "data"));
            // an archive: the fragment program among other data, the vertex program again, a broken block
            using (var archive = new MemoryStream())
            {
                archive.Write(new byte[0x1234 * 4]);
                archive.Write(fragment);
                archive.Write(new byte[64]);
                archive.Write(vertex);
                archive.Write(BrokenProgram());
                archive.Write(new byte[100]);
                File.WriteAllBytes(Path.Combine(dir, "data", "pack.bin"), archive.ToArray());
            }
            // a program that starts 32 bytes before the end of the scanner's first 4 MB read (read on its own)
            byte[] big = new byte[(4 << 20) + 0x1000];
            straddling.CopyTo(big.AsSpan((4 << 20) - 32));
            File.WriteAllBytes(Path.Combine(dir, "data", "big.dat"), big);
            File.WriteAllBytes(Path.Combine(dir, "readme.txt"), "GXP"u8.ToArray());

            var invalid = new List<string>();
            List<GxpProgram> found = GxpScanner.ScanDirectory(dir, null, out GxpScanner.Stats stats, onInvalid: (file, _, _) => invalid.Add(Path.GetFileName(file)));

            // file order: a.gxp, data/big.dat, data/pack.bin
            Assert.Equal(new[] { vertex, straddling, fragment }.Select(b => System.IO.Hashing.XxHash64.HashToUInt64(b)), found.Select(p => p.Hash));
            Assert.Equal(4, stats.Files);
            Assert.Equal(1, stats.Duplicates);
            Assert.Equal(1, stats.Invalid);
            Assert.Equal(["pack.bin"], invalid);
            Assert.Equal(5, stats.Blocks);
            Assert.True(found[0].IsVertex);
            Assert.True(found[2].IsFragment);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CancellingStopsBetweenFiles()
    {
        string dir = NewDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "a.gxp"), Program(false, 1));
            File.WriteAllBytes(Path.Combine(dir, "b.gxp"), Program(true, 2));
            using var cancel = new CancellationTokenSource();
            List<GxpProgram> found = GxpScanner.ScanDirectory(dir, null, out GxpScanner.Stats stats, cancel.Token, onFile: (_, _) => cancel.Cancel());
            Assert.Single(found);
            Assert.Equal(1, stats.Files);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void MissingDirectoryFindsNothing()
    {
        List<GxpProgram> found = GxpScanner.ScanDirectory(Path.Combine(Path.GetTempPath(), "vitasharp-gxpscan-missing-" + Guid.NewGuid().ToString("N")), null, out GxpScanner.Stats stats);
        Assert.Empty(found);
        Assert.Equal(0, stats.Files);
    }
}
