using System.Buffers;
using System.Buffers.Binary;
using VitaSharp.Core.Loader.Self;

namespace VitaSharp.Core.Gxm.Shader;

/// <summary>
/// Finds the GXP programs (SceGxmProgram binaries, "GXP\0" magic) stored in a title's files: loose .gxp files, the
/// programs the shader compiler linked into the executable and its modules (SELF files are decrypted first), and
/// programs kept uncompressed inside the game's own archives. A game hands these same bytes to
/// sceGxmShaderPatcherRegisterProgram, so the program hash matches the one the renderer sees at draw time.
/// Programs inside compressed archives are not visible to the scan (they only appear in guest memory).
/// </summary>
public static class GxpScanner
{
    /// <summary>Programs are 4-byte aligned in every container seen (the SDK aligns them).</summary>
    private const int Alignment = 4;
    private const int ChunkBytes = 4 << 20;
    /// <summary>The magic and the size field (at +8) must be in the same chunk.</summary>
    private const int Overlap = 16;
    /// <summary>A program larger than this is not a program (the largest seen are a few hundred KB).</summary>
    private const int MaxProgramBytes = 16 << 20;
    /// <summary>SELF files bigger than this are not decrypted (no Vita executable comes close).</summary>
    private const long MaxSelfBytes = 512L << 20;

    /// <summary>What a scan found.</summary>
    /// <param name="Files">files read</param>
    /// <param name="Blocks">"GXP\0" blocks with a plausible size</param>
    /// <param name="Invalid">blocks whose tables lie outside their size (<see cref="GxpProgram.Validate"/>)</param>
    /// <param name="Duplicates">valid programs already found in another place</param>
    public readonly record struct Stats(int Files, int Blocks, int Invalid, int Duplicates);

    /// <summary>
    /// Every distinct valid program under <paramref name="appDir"/> (app0), in file order. <paramref name="onFile"/>
    /// gets (files done, file count) after each file; <paramref name="onInvalid"/> (file, offset, reason) each block
    /// that is not a well-formed program. Cancelling returns what was found so far.
    /// </summary>
    public static List<GxpProgram> ScanDirectory(string appDir, byte[]? klicensee, out Stats stats, CancellationToken cancel = default,
        Action<int, int>? onFile = null, Action<string, long, string>? onInvalid = null)
    {
        var found = new List<GxpProgram>();
        var seen = new HashSet<ulong>();
        int blocks = 0, invalid = 0, duplicates = 0, done = 0;
        string[] files;
        try
        {
            files = Directory.GetFiles(appDir, "*", SearchOption.AllDirectories);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            stats = default;
            return found;
        }

        Array.Sort(files, StringComparer.Ordinal); // the same order on every file system
        foreach (string file in files)
        {
            if (cancel.IsCancellationRequested)
            {
                break;
            }

            try
            {
                ScanFile(file, klicensee, found, seen, ref blocks, ref invalid, ref duplicates, onInvalid);
            }
            catch (Exception)
            {
                // unreadable file, or a SELF the decryptor rejects (any exception type): nothing to find in it
            }
            done++;
            onFile?.Invoke(done, files.Length);
        }
        stats = new Stats(done, blocks, invalid, duplicates);
        return found;
    }

    /// <summary>The distinct valid programs in one buffer (an executable, an archive); appended to <paramref name="found"/>.</summary>
    public static void ScanBuffer(ReadOnlySpan<byte> data, List<GxpProgram> found, HashSet<ulong> seen, ref int blocks, ref int invalid, ref int duplicates,
        Action<long, string>? onInvalid = null)
    {
        for (int i = 0; i + Overlap <= data.Length; i += Alignment)
        {
            if (!IsMagic(data[i..]))
            {
                continue;
            }

            int size = ProgramSize(data[i..]);
            if (size < 0 || i + size > data.Length)
            {
                continue;
            }

            Add(data.Slice(i, size), i, found, seen, ref blocks, ref invalid, ref duplicates, onInvalid);
        }
    }

    private static void ScanFile(string file, byte[]? klicensee, List<GxpProgram> found, HashSet<ulong> seen, ref int blocks, ref int invalid, ref int duplicates,
        Action<string, long, string>? onInvalid)
    {
        using var handle = File.OpenHandle(file);
        long length = RandomAccess.GetLength(handle);
        if (length < Overlap)
        {
            return;
        }

        Action<long, string>? report = onInvalid == null ? null : (offset, reason) => onInvalid(file, offset, reason);
        Span<byte> head = stackalloc byte[4];
        RandomAccess.Read(handle, head, 0);
        if (head.SequenceEqual("SCE\0"u8) && length <= MaxSelfBytes)
        {
            // an executable or module: the programs are in its (encrypted, compressed) segments
            byte[] self = new byte[length];
            RandomAccess.Read(handle, self, 0);
            ScanBuffer(SelfDecryptor.ToElf(self, klicensee), found, seen, ref blocks, ref invalid, ref duplicates, report);
            return;
        }

        byte[] chunk = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        try
        {
            for (long offset = 0; offset < length; offset += ChunkBytes - Overlap)
            {
                int read = RandomAccess.Read(handle, chunk.AsSpan(0, (int)Math.Min(ChunkBytes, length - offset)), offset);
                ReadOnlySpan<byte> data = chunk.AsSpan(0, read);
                for (int i = 0; i + Overlap <= read; i += Alignment)
                {
                    if (!IsMagic(data[i..]))
                    {
                        continue;
                    }

                    int size = ProgramSize(data[i..]);
                    if (size < 0 || offset + i + size > length)
                    {
                        continue;
                    }

                    if (i + size <= read)
                    {
                        Add(data.Slice(i, size), offset + i, found, seen, ref blocks, ref invalid, ref duplicates, report);
                        continue;
                    }
                    // crosses the chunk end: read it on its own
                    byte[] program = new byte[size];
                    RandomAccess.Read(handle, program, offset + i);
                    Add(program, offset + i, found, seen, ref blocks, ref invalid, ref duplicates, report);
                }
                if (read < ChunkBytes)
                {
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    private static bool IsMagic(ReadOnlySpan<byte> d) => d[0] == (byte)'G' && d[1] == (byte)'X' && d[2] == (byte)'P' && d[3] == 0;

    /// <summary>The program's size field (SceGxmProgram.size at +8), or -1 when it cannot be a program.</summary>
    private static int ProgramSize(ReadOnlySpan<byte> d)
    {
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(d[8..]);
        return size is < 0x40 or > MaxProgramBytes ? -1 : (int)size;
    }

    private static void Add(ReadOnlySpan<byte> program, long offset, List<GxpProgram> found, HashSet<ulong> seen, ref int blocks, ref int invalid, ref int duplicates,
        Action<long, string>? onInvalid)
    {
        blocks++;
        if (GxpProgram.Validate(program) is { } bad)
        {
            invalid++;
            onInvalid?.Invoke(offset, bad);
            return;
        }

        ulong hash = System.IO.Hashing.XxHash64.HashToUInt64(program);
        if (!seen.Add(hash))
        {
            duplicates++;
            return;
        }

        found.Add(new GxpProgram(program.ToArray(), 0));
    }
}
