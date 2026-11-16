using System.Buffers.Binary;
using System.Text;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceScreenShot's parameters as libSceScreenShot 3.74 handles them: SetParam takes UTF-8 strings (0x100/0x100/0x200
/// byte fields) and its unnamed twin NID_0757A17F sends the same with the flag byte 1; GetParam(path, info) reads a
/// photo's file: the title from the file name, the game title and comment from the maker note of the Exif data, the
/// date from DateTimeOriginal. The photos are synthetic JPEGs laid out as the console writes them.
/// </summary>
[Collection("guest")]
public sealed class ScreenShotTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int InvalidArgument = unchecked((int)0x80102F01);
    private const int FileNotFound = unchecked((int)0x80102F03);
    private const int NotSupportedFormat = unchecked((int)0x80102F04);
    private const int NoMetadata = unchecked((int)0x80102F0E);

    private readonly HleHarness _harness = new(fixture, "screenshot");

    public void Dispose() => _harness.Dispose();

    private ScreenShot Module => _harness.Module<ScreenShot>();

    private uint Param(uint photo, uint game, uint comment, uint reserved = 0)
    {
        uint param = _harness.Alloc(0x10);
        GuestMemory.W32(param, photo);
        GuestMemory.W32(param + 4, game);
        GuestMemory.W32(param + 8, comment);
        GuestMemory.W32(param + 12, reserved);
        return param;
    }

    [Fact]
    public void SetParamTakesUtf8StringsWithTheModulesLimits()
    {
        uint param = Param(_harness.Str("Foto サ😀"), 0, _harness.Str("Comentário"));
        Assert.Equal(0, Call(Module, "sceScreenShotSetParam", param));
        Assert.Equal("Foto サ😀", Module.PhotoTitle);
        Assert.Equal("", Module.GameTitle);
        Assert.Equal("Comentário", Module.GameComment);
        Assert.False(Module.ParamFlag);

        Assert.Equal(0, Call(Module, "ScreenShotSetParamFlagged", param));
        Assert.True(Module.ParamFlag);

        Assert.Equal(InvalidArgument, Call(Module, "sceScreenShotSetParam", 0u));
        Assert.Equal(InvalidArgument, Call(Module, "sceScreenShotSetParam", Param(0, 0, 0, reserved: 1)));
        Assert.Equal(0, Call(Module, "sceScreenShotSetParam", Param(_harness.Str(new string('a', 0xFF)), 0, _harness.Str(new string('c', 0x1FF)))));
        Assert.Equal(InvalidArgument, Call(Module, "sceScreenShotSetParam", Param(_harness.Str(new string('a', 0x100)), 0, 0)));
        Assert.Equal(InvalidArgument, Call(Module, "ScreenShotSetParamFlagged", Param(0, 0, _harness.Str(new string('c', 0x200)))));
    }

    [Fact]
    public void GetParamReadsTheTitleGameCommentAndDateOfAPhoto()
    {
        Photo("ux0:picture/SCREENSHOT/Jogo_0001.jpg", Jpeg(bigEndian: false, game: "Jogo", comment: "Um comentário", date: "2026:11:16 08:09:10"));
        uint info = Info();
        Assert.Equal(0, Call(Module, "sceScreenShotGetParam", _harness.Str("ux0:picture/SCREENSHOT/Jogo_0001.jpg"), info));
        Assert.Equal("Jogo_0001", GuestMemory.ReadCString(info));
        Assert.Equal("Jogo", GuestMemory.ReadCString(info + 0x100));
        Assert.Equal("Um comentário", GuestMemory.ReadCString(info + 0x200));
        // strtoll with base 0 (the module passes 0): "08" is octal 0 stopping at '8'; the next number starts after the '8',
        // at ":09" (none: 0, ending where it started), then "09" (octal 0 again)
        ushort[] date = Enumerable.Range(0, 6).Select(i => GuestMemory.Read<ushort>(info + 0x400 + (uint)i * 2)).ToArray();
        Assert.Equal(new ushort[] { 2026, 11, 16, 0, 0, 0 }, date);
        Assert.Equal(0u, GuestMemory.R32(info + 0x40C));
    }

    [Fact]
    public void GetParamReadsBigEndianExif()
    {
        Photo("ux0:picture/a.jpg", Jpeg(bigEndian: true, game: "BE", comment: "", date: "2025:12:25 23:59:58"));
        uint info = Info();
        Assert.Equal(0, Call(Module, "sceScreenShotGetParam", _harness.Str("ux0:picture/a.jpg"), info));
        Assert.Equal("a", GuestMemory.ReadCString(info));
        Assert.Equal("BE", GuestMemory.ReadCString(info + 0x100));
        Assert.Equal(2025, GuestMemory.Read<ushort>(info + 0x400));
        Assert.Equal(23, GuestMemory.Read<ushort>(info + 0x406));
    }

    [Fact]
    public void GetParamErrorsFollowTheModule()
    {
        uint info = Info();
        Assert.Equal(FileNotFound, Call(Module, "sceScreenShotGetParam", _harness.Str("ux0:picture/none.jpg"), info));
        Photo("ux0:picture/b.png", [0x89, (byte)'P', (byte)'N', (byte)'G']);
        Assert.Equal(NotSupportedFormat, Call(Module, "sceScreenShotGetParam", _harness.Str("ux0:picture/b.png"), info));

        // an APP1 that is not Exif: no metadata, and the title is not written
        var notExif = new List<byte> { 0xFF, 0xD8 };
        Segment(notExif, 0xE1, "XMP\0\0\0"u8.ToArray());
        notExif.AddRange([0xFF, 0xDA, 0x00, 0x02]);
        Photo("ux0:picture/c.jpg", notExif.ToArray());
        GuestMemory.Fill(info, 0xAA, 0x410);
        Assert.Equal(NoMetadata, Call(Module, "sceScreenShotGetParam", _harness.Str("ux0:picture/c.jpg"), info));
        Assert.Equal(0, GuestMemory.Read<byte>(info));
        Assert.Equal(0xAAAAAAAAu, GuestMemory.R32(info + 0x400)); // no date read: left as it was

        // a JPEG without Exif: only the title
        Photo("ux0:picture/d.jpg", [0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x02]);
        Assert.Equal(0, Call(Module, "sceScreenShotGetParam", _harness.Str("ux0:picture/d.jpg"), info));
        Assert.Equal("d", GuestMemory.ReadCString(info));
        Assert.Equal("", GuestMemory.ReadCString(info + 0x100));
    }

    [Theory]
    [InlineData("2026", 0, 2026L, 4)]
    [InlineData("08", 0, 0L, 1)]
    [InlineData("0x1F:", 0, 31L, 4)]
    [InlineData("  -12", 0, -12L, 5)]
    [InlineData(":5", 0, 0L, 0)]
    public void StrToLlFollowsC(string text, int start, long value, int end)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text + "\0");
        Assert.Equal(value, ScreenShotFile.StrToLl(bytes, start, out int stop));
        Assert.Equal(end, stop);
    }

    private uint Info()
    {
        return _harness.Alloc(0x410);
    }

    private void Photo(string guestPath, byte[] data)
    {
        string host = Path.Combine(_harness.Root, guestPath.Replace(":", "/").Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(host)!);
        File.WriteAllBytes(host, data);
    }

    private static void Segment(List<byte> jpeg, byte marker, byte[] data)
    {
        jpeg.Add(0xFF);
        jpeg.Add(marker);
        jpeg.Add((byte)((data.Length + 2) >> 8));
        jpeg.Add((byte)(data.Length + 2));
        jpeg.AddRange(data);
    }

    /// <summary>A JPEG with an APP1 Exif segment: IFD0 with the Exif IFD pointer, the Exif IFD with DateTimeOriginal and
    /// the maker note ("SONY PS...SIE" signature, a size and frame count, TXXX frames ALBM and GDSC), then SOS.</summary>
    private static byte[] Jpeg(bool bigEndian, string game, string comment, string date)
    {
        var tiff = new List<byte>();
        void U16(int v)
        {
            byte[] b = new byte[2];
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v);
            }

            tiff.AddRange(b);
        }

        void U32(uint v)
        {
            byte[] b = new byte[4];
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(b, v);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            }

            tiff.AddRange(b);
        }

        byte[] note = MakerNote(game, comment);
        byte[] dateBytes = Encoding.ASCII.GetBytes(date + "\0");
        const uint Ifd0 = 8, ExifIfd = Ifd0 + 2 + 12 + 4;
        const uint ExifData = ExifIfd + 2 + 2 * 12 + 4;
        uint noteAt = ExifData + (uint)dateBytes.Length;
        tiff.AddRange(bigEndian ? "MM"u8.ToArray() : "II"u8.ToArray());
        U16(42);
        U32(Ifd0);
        U16(1);
        U16(0x8769);
        U16(4);
        U32(1);
        U32(ExifIfd);
        U32(0);
        U16(2);
        U16(0x9003);
        U16(2);
        U32((uint)dateBytes.Length);
        U32(ExifData);
        U16(0x927C);
        U16(7);
        U32((uint)note.Length);
        U32(noteAt);
        U32(0);
        tiff.AddRange(dateBytes);
        tiff.AddRange(note);

        var jpeg = new List<byte> { 0xFF, 0xD8 };
        Segment(jpeg, 0xE1, "Exif\0\0"u8.ToArray().Concat(tiff).ToArray());
        Segment(jpeg, 0xDB, new byte[4]);
        jpeg.AddRange([0xFF, 0xDA, 0x00, 0x02, 0x00, 0xFF, 0xD9]);
        return jpeg.ToArray();
    }

    private static byte[] MakerNote(string game, string comment)
    {
        var frames = new List<byte>();
        void Txxx(string key, string value)
        {
            byte[] data = new byte[] { 3, 0, 0, 0 }.Concat(Encoding.ASCII.GetBytes(key)).Concat(new byte[] { 0 })
                .Concat(Encoding.UTF8.GetBytes(value + "\0")).ToArray();
            frames.AddRange("TXXX"u8.ToArray());
            frames.AddRange([0, 0, (byte)(data.Length >> 7), (byte)(data.Length & 0x7F)]);
            frames.AddRange([0, 0]);
            frames.AddRange(data);
        }

        Txxx("ALBM", game);
        Txxx("GDSC", comment);
        var note = new List<byte>();
        note.AddRange("SONY PS\0\0\0\0\0SIE\u0001\0\0"u8.ToArray());
        byte[] size = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(size, (uint)frames.Count);
        note.AddRange(size);
        note.AddRange([0, 2]);
        note.AddRange(frames);
        return note.ToArray();
    }
}
