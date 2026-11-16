using System.Buffers.Binary;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The metadata sceScreenShotGetParam reads from a photo, as libSceScreenShot 3.74 does it (read in its code, study
/// only): the JPEG segments up to the first SOS or EOI; in an APP1 "Exif" segment the TIFF IFD0 and the Exif IFD it
/// points to (tag 0x8769), where DateTimeOriginal (0x9003) gives the capture date and the maker note (0x927C, the
/// "SONY PS...SIE" note) carries ID3v2-style TXXX frames: "ALBM" the game title, "GDSC" the comment. The photo title
/// is the file name without its extension.
/// </summary>
internal static class ScreenShotFile
{
    /// <summary>The record sceScreenShotGetParam fills: three strings (0x100 + 0x100 + 0x200 bytes), then the date.</summary>
    public const int ParamInfoSize = 0x410, StringsSize = 0x400;
    const int GameTitleOffset = 0x100, CommentOffset = 0x200, TitleBytes = 0x100, CommentBytes = 0x200;

    internal const int ERR_NOT_SUPPORTED_FORMAT = unchecked((int)0x80102F04), ERR_INTERNAL = unchecked((int)0x80102F06),
        ERR_BROKEN_DATA = unchecked((int)0x80102F0D), ERR_NO_METADATA = unchecked((int)0x80102F0E);

    /// <summary>The 18 bytes a maker note starts with (the module's memcmp against its .rodata).</summary>
    static ReadOnlySpan<byte> MakerNoteSignature => "SONY PS\0\0\0\0\0SIE\u0001\0\0"u8;

    /// <summary>The size of one value of each TIFF field type 1..12.</summary>
    static ReadOnlySpan<byte> TypeSizes => [0, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8];

    const ushort TagExifIfd = 0x8769, TagMakerNote = 0x927C, TagDateTimeOriginal = 0x9003;
    const uint FrameTxxx = 0x54585858, KeyGameTitle = 0x414C424D, KeyComment = 0x47445343;

    /// <summary>Parses <paramref name="file"/> into <paramref name="record"/> (<see cref="ParamInfoSize"/> bytes, zeroed);
    /// <paramref name="path"/> is the guest path the title comes from. <paramref name="dated"/>: the date words were
    /// written. Returns 0 or the module's error.</summary>
    public static int ReadParam(Stream file, ReadOnlySpan<byte> path, Span<byte> record, out bool dated)
    {
        dated = false;
        long size = file.Length;
        if (size >= 0x100000000L)
        {
            return ERR_INTERNAL;
        }

        Span<byte> two = stackalloc byte[2];
        if (Read(file, two) < 1)
        {
            return ERR_INTERNAL;
        }

        if (BinaryPrimitives.ReadUInt16BigEndian(two) != 0xFFD8)
        {
            return ERR_NOT_SUPPORTED_FORMAT;
        }

        long position = 2;
        bool endOfImage = false;
        bool startOfScan = false;
        int result = 0;
        while (result == 0 && !endOfImage && !startOfScan)
        {
            if (Read(file, two) != 2)
            {
                return ERR_INTERNAL;
            }

            ushort marker = BinaryPrimitives.ReadUInt16BigEndian(two);
            long afterMarker = position + 2;
            if (size < position + 4)
            {
                return ERR_INTERNAL;
            }

            long lengthAt = 0;
            int length = 0;
            if (marker != 0xFFD8 && marker != 0xFFD9)
            {
                lengthAt = afterMarker;
                if (Read(file, two) != 2)
                {
                    return ERR_INTERNAL;
                }

                length = BinaryPrimitives.ReadUInt16BigEndian(two);
                if (length < 2)
                {
                    return ERR_BROKEN_DATA;
                }
            }

            if (marker == 0xFFD9)
            {
                endOfImage = true;
            }
            else if (marker == 0xFFDA)
            {
                startOfScan = true;
            }
            else if (marker == 0xFFE1)
            {
                result = ReadExifSegment(file, lengthAt + 2, lengthAt + length, record, ref dated);
            }

            position = afterMarker + length;
            if (position > size)
            {
                // the module's lseek past the end succeeds; the next read then fails
                return ERR_INTERNAL;
            }

            file.Position = position;
        }

        if (result != 0)
        {
            return result;
        }

        WritePhotoTitle(path, record);
        return 0;
    }

    /// <summary>The photo title: the bytes after the last '/' up to the extension (strnlen 0x105 minus strnlen of the
    /// part from the last '.', at most 5); the module copies them over the record and then ends the title field.</summary>
    static void WritePhotoTitle(ReadOnlySpan<byte> path, Span<byte> record)
    {
        int slash = path.LastIndexOf((byte)'/');
        ReadOnlySpan<byte> name = path[(slash + 1)..];
        int nameLength = Math.Min(name.Length, 0x105);
        int dot = name.LastIndexOf((byte)'.');
        int extensionLength = dot < 0 ? 0 : Math.Min(name.Length - dot, 5);
        int copy = Math.Max(nameLength - extensionLength, 0);
        name[..copy].CopyTo(record);
        record[TitleBytes - 1] = 0;
    }

    /// <summary>An APP1 segment whose data starts at <paramref name="start"/> and ends at <paramref name="end"/>.</summary>
    static int ReadExifSegment(Stream file, long start, long end, Span<byte> record, ref bool dated)
    {
        Span<byte> header = stackalloc byte[6];
        int read = Read(file, header);
        if (read < 4 || !header[..4].SequenceEqual("Exif"u8))
        {
            return ERR_NO_METADATA;
        }

        long tiff = start + read;
        Span<byte> tiffHeader = stackalloc byte[8];
        if (Read(file, tiffHeader) < 1)
        {
            return ERR_INTERNAL;
        }

        bool bigEndian;
        if (tiffHeader[0] == 'I' && tiffHeader[1] == 'I')
        {
            bigEndian = false;
        }
        else if (tiffHeader[0] == 'M' && tiffHeader[1] == 'M')
        {
            bigEndian = true;
        }
        else
        {
            return ERR_BROKEN_DATA;
        }

        if (U16(tiffHeader[2..], bigEndian) != 42)
        {
            return ERR_BROKEN_DATA;
        }

        uint ifd = U32(tiffHeader[4..], bigEndian);
        int result = ReadIfd(file, tiff, ifd, bigEndian, record, ref dated, out uint exifIfd);
        if (result != 0)
        {
            return result;
        }

        // the Exif IFD the first walk found (else IFD0 again), when it lies inside the segment
        uint second = exifIfd != 0 ? exifIfd : ifd;
        if (tiff + second < end)
        {
            result = ReadIfd(file, tiff, second, bigEndian, record, ref dated, out _);
        }

        return result;
    }

    /// <summary>One IFD at <paramref name="tiff"/> + <paramref name="offset"/>: the maker note and the date are read as
    /// they come; an Exif IFD pointer (type LONG) ends the walk and is returned in <paramref name="exifIfd"/>.</summary>
    static int ReadIfd(Stream file, long tiff, uint offset, bool bigEndian, Span<byte> record, ref bool dated, out uint exifIfd)
    {
        exifIfd = 0;
        long at = tiff + offset;
        if (at > file.Length)
        {
            return ERR_INTERNAL;
        }

        file.Position = at;
        Span<byte> two = stackalloc byte[2];
        if (Read(file, two) < 1)
        {
            return ERR_INTERNAL;
        }

        int count = U16(two, bigEndian);
        long next = at + 2;
        Span<byte> entry = stackalloc byte[12];
        for (int i = 0; i < count; i++)
        {
            if (Read(file, entry) != 12)
            {
                return ERR_INTERNAL;
            }

            next += 12;
            ushort tag = U16(entry, bigEndian);
            ushort type = U16(entry[2..], bigEndian);
            uint values = U32(entry[4..], bigEndian);
            uint value = U32(entry[8..], bigEndian);
            if (type == 0 || type > 12)
            {
                continue;
            }

            if (tag == TagExifIfd && type == 4)
            {
                exifIfd = value;
                return 0;
            }

            if (tag == TagMakerNote)
            {
                int result = ReadMakerNote(file, tiff + value, values, record);
                if (result != 0)
                {
                    return result;
                }

                file.Position = next;
            }
            else if (tag == TagDateTimeOriginal)
            {
                int result = ReadDate(file, tiff + value, record);
                if (result != 0)
                {
                    return result;
                }

                dated = true;
                file.Position = next;
            }
        }

        return 0;
    }

    /// <summary>DateTimeOriginal: 20 bytes, six numbers read with strtoll(base 0), each from the character after the
    /// previous one's end; stored as 16-bit words at +0x400, then a zero word at +0x40C.</summary>
    static int ReadDate(Stream file, long at, Span<byte> record)
    {
        if (at > file.Length)
        {
            return ERR_BROKEN_DATA;
        }

        file.Position = at;
        Span<byte> text = stackalloc byte[0x15];
        text.Clear();
        if (Read(file, text[..0x14]) != 0x14)
        {
            return ERR_BROKEN_DATA;
        }

        int cursor = 0;
        for (int i = 0; i < 6; i++)
        {
            long number = StrToLl(text, cursor, out int end);
            BinaryPrimitives.WriteUInt16LittleEndian(record[(StringsSize + i * 2)..], (ushort)number);
            cursor = end + 1;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(record[(StringsSize + 0xC)..], 0);
        return 0;
    }

    /// <summary>C strtoll with base 0 on a NUL-terminated buffer: white space, a sign, then hexadecimal after "0x",
    /// octal after "0", decimal otherwise; <paramref name="end"/> is where the number stopped (the start when none).</summary>
    internal static long StrToLl(ReadOnlySpan<byte> text, int start, out int end)
    {
        int i = Math.Min(start, text.Length);
        while (i < text.Length && text[i] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\v' or (byte)'\f' or (byte)'\r')
        {
            i++;
        }

        bool negative = false;
        if (i < text.Length && text[i] is (byte)'+' or (byte)'-')
        {
            negative = text[i] == '-';
            i++;
        }

        int radix = 10;
        if (i < text.Length && text[i] == '0')
        {
            if (i + 2 < text.Length && text[i + 1] is (byte)'x' or (byte)'X' && Digit(text[i + 2]) is >= 0 and < 16)
            {
                radix = 16;
                i += 2;
            }
            else
            {
                radix = 8;
            }
        }

        int digits = i;
        long number = 0;
        while (i < text.Length && Digit(text[i]) is int d && d >= 0 && d < radix)
        {
            number = number * radix + d;
            i++;
        }

        end = i == digits ? start : i;
        return negative ? -number : number;
    }

    static int Digit(byte c) => c switch
    {
        >= (byte)'0' and <= (byte)'9' => c - '0',
        >= (byte)'a' and <= (byte)'z' => c - 'a' + 10,
        >= (byte)'A' and <= (byte)'Z' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>The maker note: <paramref name="count"/> bytes at <paramref name="at"/>; after the 18-byte signature a
    /// big-endian size and frame count (under 100, the size under count &lt;&lt; 13), then ID3v2 frames of a 10-byte header
    /// (id, size: syncsafe unless the last size byte has bit 7 set, flags) whose TXXX frames give the game title
    /// ("ALBM") and the comment ("GDSC"), the value starting at data byte 9.</summary>
    static int ReadMakerNote(Stream file, long at, uint count, Span<byte> record)
    {
        if (at > file.Length)
        {
            return ERR_INTERNAL;
        }

        file.Position = at;
        if (count < 0x16)
        {
            return ERR_NO_METADATA;
        }

        Span<byte> signature = stackalloc byte[0x12];
        Read(file, signature);
        if (!signature.SequenceEqual(MakerNoteSignature))
        {
            return ERR_NO_METADATA;
        }

        byte[] note = new byte[Math.Min(count, 0x2000)];
        int noteLength = Read(file, note);
        if (noteLength < 6)
        {
            return ERR_BROKEN_DATA;
        }

        uint tagSize = BinaryPrimitives.ReadUInt32BigEndian(note);
        int frames = BinaryPrimitives.ReadUInt16BigEndian(note.AsSpan(4));
        if (frames >= 100 || tagSize >= (uint)frames << 13)
        {
            return ERR_NO_METADATA;
        }

        int offset = 6;
        int limit = (int)Math.Min(count, (uint)noteLength);
        for (int i = 0; i < frames; i++)
        {
            int remaining = limit - offset;
            if (remaining < 10)
            {
                return ERR_BROKEN_DATA;
            }

            ReadOnlySpan<byte> frame = note.AsSpan(offset, remaining);
            uint frameSize = (frame[7] & 0x80) == 0
                ? (uint)((frame[4] & 0x7F) << 21 | (frame[5] & 0x7F) << 14 | (frame[6] & 0x7F) << 7 | (frame[7] & 0x7F))
                : BinaryPrimitives.ReadUInt32BigEndian(frame[4..]);
            if (remaining < frameSize + 10 || frame[0] == 0)
            {
                return ERR_BROKEN_DATA;
            }

            if (BinaryPrimitives.ReadUInt32BigEndian(frame) == FrameTxxx)
            {
                if (frameSize < 10)
                {
                    return ERR_NO_METADATA;
                }

                ReadOnlySpan<byte> data = frame.Slice(10, (int)frameSize);
                uint key = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                if (key == KeyGameTitle)
                {
                    CopyString(data[9..], record.Slice(GameTitleOffset, TitleBytes));
                }
                else if (key == KeyComment)
                {
                    CopyString(data[9..], record.Slice(CommentOffset, CommentBytes));
                }
            }

            offset += (int)frameSize + 10;
        }

        return 0;
    }

    /// <summary>strnlen(value, field size) bytes of <paramref name="value"/> into <paramref name="field"/>.</summary>
    static void CopyString(ReadOnlySpan<byte> value, Span<byte> field)
    {
        int length = value.IndexOf((byte)0);
        if (length < 0)
        {
            length = value.Length;
        }

        length = Math.Min(length, field.Length);
        value[..length].CopyTo(field);
    }

    static int Read(Stream file, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = file.Read(buffer[total..]);
            if (n <= 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    static ushort U16(ReadOnlySpan<byte> b, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(b) : BinaryPrimitives.ReadUInt16LittleEndian(b);

    static uint U32(ReadOnlySpan<byte> b, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(b) : BinaryPrimitives.ReadUInt32LittleEndian(b);
}
