using System.Buffers.Binary;
using System.Security.Cryptography;

namespace VitaSharp.Core.Package;

/// <summary>
/// A PlayStation Mobile protected file (PSSE; "PSME" in some titles), as the PSM runtime opens the files its title
/// lists in RO/Application/psse.list (format: psdevwiki "PSSE"; SilicaAndPina's psse-decrypt, public domain):
/// <code>
/// 0x000  "PSSE"          0x004  version (1)        0x008  u64 plain size     0x010  type (1)
/// 0x014  content ID (0x24 bytes, the title's)       0x040  MD5 of the plain file
/// 0x070  the file IV, AES-128-CBC under the runtime's header key (IV 00 01 .. 0F)
/// 0x680  the data: 0x8000-byte blocks from the file start, each AES-128-CBC under the title's content key with the IV
///        (block number, 16 bytes little-endian) XOR (the file IV); every 0x80000 bytes a block starts with a
///        0x400-byte signature, which is not data
/// </code>
/// The content key is the key of the title's key set (the license's 0x200-byte key area, ScePsmDrmKeySet: HMAC key
/// 0x20 bytes, then the AES key), which scePsmDrmGetRifKey gives the runtime (<see cref="PsmLicense"/>).
/// </summary>
public static class PsseFile
{
    public const int HeaderSize = 0x680;
    private const int BlockSize = 0x8000, SignatureEvery = 0x80000, SignatureSize = 0x400;

    // the PSM runtime's header key: the same for every retail title (it only opens the file IV); a format constant
    private static readonly byte[] s_headerKey = Convert.FromHexString("4E298B40F531F469D21F75B133C307BE");
    private static readonly byte[] s_headerIv = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");

    /// <summary>Whether <paramref name="file"/> starts like a PSSE file.</summary>
    public static bool IsPsse(ReadOnlySpan<byte> file) =>
        file.Length >= HeaderSize && (file[..4].SequenceEqual("PSSE"u8) || file[..4].SequenceEqual("PSME"u8));

    /// <summary>The content ID the file was made for.</summary>
    public static string ContentId(ReadOnlySpan<byte> file) => System.Text.Encoding.ASCII.GetString(file.Slice(0x14, 0x24)).TrimEnd('\0');

    /// <summary>The plain file, or null when <paramref name="file"/> is not a PSSE file of version and type 1, or when
    /// <paramref name="contentKey"/> does not open it (its MD5 differs).</summary>
    public static byte[]? Decrypt(ReadOnlySpan<byte> file, ReadOnlySpan<byte> contentKey)
    {
        if (!IsPsse(file) || contentKey.Length != 16 || BinaryPrimitives.ReadInt32LittleEndian(file[4..]) != 1
            || BinaryPrimitives.ReadInt32LittleEndian(file[0x10..]) != 1)
        {
            return null;
        }

        ulong size = BinaryPrimitives.ReadUInt64LittleEndian(file[8..]);
        if (size > (ulong)file.Length)
        {
            return null;
        }

        using var header = Aes.Create();
        header.Key = s_headerKey;
        byte[] fileIv = header.DecryptCbc(file.Slice(0x70, 16), s_headerIv, PaddingMode.None);
        using var content = Aes.Create();
        content.Key = contentKey.ToArray();
        byte[] plain = new byte[size];
        int written = 0;
        Span<byte> iv = stackalloc byte[16];
        for (long block = 0; written < plain.Length; block++)
        {
            long start = block * BlockSize, end = start + BlockSize;
            if (block == 0)
            {
                start = HeaderSize;
            }
            else if (start % SignatureEvery == 0)
            {
                start += SignatureSize;
            }

            int want = (int)Math.Min(end - start, plain.Length - written);
            int read = (want + 15) & ~15;
            if (start + read > file.Length)
            {
                return null;
            }

            iv.Clear();
            BinaryPrimitives.WriteInt64LittleEndian(iv, block);
            for (int i = 0; i < 16; i++)
            {
                iv[i] ^= fileIv[i];
            }

            byte[] data = content.DecryptCbc(file.Slice((int)start, read), iv, PaddingMode.None);
            data.AsSpan(0, want).CopyTo(plain.AsSpan(written));
            written += want;
        }

        return MD5.HashData(plain).AsSpan().SequenceEqual(file.Slice(0x40, 16)) ? plain : null;
    }

    /// <summary>Makes a PSSE file of <paramref name="plain"/> (tests: the inverse of <see cref="Decrypt"/>).</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> contentKey, string contentId, ReadOnlySpan<byte> fileIv)
    {
        var file = new List<byte>(HeaderSize + plain.Length + 0x1000);
        byte[] head = new byte[HeaderSize];
        "PSSE"u8.CopyTo(head);
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(8), (ulong)plain.Length);
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(0x10), 1);
        System.Text.Encoding.ASCII.GetBytes(contentId).CopyTo(head, 0x14);
        MD5.HashData(plain).CopyTo(head, 0x40);
        using var header = Aes.Create();
        header.Key = s_headerKey;
        header.EncryptCbc(fileIv, s_headerIv, PaddingMode.None).CopyTo(head, 0x70);
        file.AddRange(head);
        using var content = Aes.Create();
        content.Key = contentKey.ToArray();
        int taken = 0;
        byte[] iv = new byte[16];
        for (long block = 0; taken < plain.Length; block++)
        {
            long start = block * BlockSize, end = start + BlockSize;
            if (block > 0 && start % SignatureEvery == 0)
            {
                file.AddRange(new byte[SignatureSize]);
                start += SignatureSize;
            }
            else if (block == 0)
            {
                start = HeaderSize;
            }

            int want = (int)Math.Min(end - start, plain.Length - taken);
            byte[] chunk = new byte[(want + 15) & ~15];
            plain.Slice(taken, want).CopyTo(chunk);
            Array.Clear(iv);
            BinaryPrimitives.WriteInt64LittleEndian(iv, block);
            for (int i = 0; i < 16; i++)
            {
                iv[i] ^= fileIv[i];
            }

            file.AddRange(content.EncryptCbc(chunk, iv, PaddingMode.None));
            taken += want;
        }

        return file.ToArray();
    }
}

/// <summary>
/// A PlayStation Mobile license (.rif, 0x400 bytes; vita-headers ScePsmDrmLicense): account ID at 0x10, start and
/// expiration times at 0x20/0x28, content ID at 0x50, the key set (0x200 bytes) at 0x100, the signature area (0x100
/// bytes) at 0x300. A license the PSN store issued is signed and its key set is encrypted for the account's activation:
/// only the security processor opens it (scePsmDrmGetRifKey's job on the console), which the emulated unit does not
/// have. A license whose signature area is empty was written on a unit with the key set already opened (the form the
/// user installs from their own console): its key set is read as it is, as VitaSharp reads the klicensee of a PS Vita
/// license.
/// </summary>
public static class PsmLicense
{
    public const int Size = 0x400, KeySetOffset = 0x100, KeySetSize = 0x200, ContentKeyOffset = KeySetOffset + 0x20;
    private const int SignatureOffset = 0x300;

    /// <summary>Whether the license's key set is readable without the security processor (no signature).</summary>
    public static bool KeySetInClear(ReadOnlySpan<byte> license) =>
        license.Length >= Size && license.Slice(SignatureOffset, Size - SignatureOffset).IndexOfAnyExcept((byte)0) < 0;

    /// <summary>The title's content key (16 bytes) from a license with its key set in clear; null otherwise.</summary>
    public static byte[]? ContentKey(ReadOnlySpan<byte> license) =>
        KeySetInClear(license) ? license.Slice(ContentKeyOffset, 16).ToArray() : null;

    public static string ContentId(ReadOnlySpan<byte> license) => PsmPkgInstaller.LicenseContentId(license);

    /// <summary>The content key of <paramref name="contentId"/> from the licenses installed for PSM title
    /// <paramref name="titleId"/> (ux0:psm/&lt;id&gt;/RO/License/*.rif, where the console keeps them); null when no
    /// license for that content with its key set in clear is installed.</summary>
    public static byte[]? FindContentKey(string fsRoot, string titleId, string contentId)
    {
        string dir = Io.Vfs.ResolveCaseInsensitive(Io.FirmwareLayout.PsmTitles.Host(fsRoot, titleId, Io.FirmwareLayout.PsmReadOnly), Io.FirmwareLayout.PsmLicense);
        if (!Directory.Exists(dir))
        {
            return null;
        }

        foreach (string file in Directory.GetFiles(dir, "*.rif").Order(StringComparer.OrdinalIgnoreCase))
        {
            byte[] license = File.ReadAllBytes(file);
            if (ContentId(license) == contentId && ContentKey(license) is { } key)
            {
                return key;
            }
        }
        return null;
    }
}
