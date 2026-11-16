using System.Text;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceScreenShot: the game controls whether the system screenshot shortcut (PS + START) is allowed and which metadata and
/// watermark the captures carry. VitaSharp has no system shortcut yet; the state is kept for a host screenshot feature.
/// </summary>
public sealed partial class ScreenShot : HleModule
{
    internal const int ERR_INVALID_ARGUMENT = unchecked((int)0x80102F01), ERR_NO_MEMORY = unchecked((int)0x80102F02),
        ERR_FILE_NOT_FOUND = unchecked((int)0x80102F03), ERR_NOT_SUPPORTED_FORMAT = unchecked((int)0x80102F04),
        ERR_MEDIA_FULL = unchecked((int)0x80102F05), ERR_INTERNAL = unchecked((int)0x80102F06);


    readonly object _lock = new();

    /// <summary>The system screenshot shortcut is allowed (sceScreenShotEnable/Disable).</summary>
    public bool Enabled { get; private set; } = true;
    /// <summary>The app wants the screenshot app event (sceScreenShotEnable/DisableNotification).</summary>
    public bool Notification { get; private set; }
    public string? PhotoTitle { get; private set; }
    public string? GameTitle { get; private set; }
    public string? GameComment { get; private set; }
    /// <summary>Host path of the watermark image drawn over captures, and its offset.</summary>
    public string? OverlayPath { get; private set; }
    public int OverlayX { get; private set; }
    public int OverlayY { get; private set; }

    [Hle] int sceScreenShotEnable() { Enabled = true; return 0; }
    [Hle] int sceScreenShotDisable() { Enabled = false; return 0; }
    [Hle] int sceScreenShotEnableNotification() { Notification = true; return 0; }
    [Hle] int sceScreenShotDisableNotification() { Notification = false; return 0; }

    /// <summary>Reads a NUL-terminated UTF-32LE string of at most <paramref name="maxChars"/> characters.</summary>
    internal static string? ReadUtf32(uint addr, int maxChars)
    {
        if (addr == 0) return null;
        var sb = new StringBuilder();
        for (int i = 0; i < maxChars; i++)
        {
            uint c = GuestMemory.R32(addr + (uint)i * 4);
            if (c == 0) break;
            if (c <= 0x10FFFF && (c < 0xD800 || c > 0xDFFF)) sb.Append(char.ConvertFromUtf32((int)c));
            else sb.Append('�');
        }
        return sb.ToString();
    }

    /// <summary>Writes <paramref name="s"/> as UTF-32LE with a NUL, truncated to fit <paramref name="maxChars"/> + NUL.</summary>
    internal static void WriteUtf32(uint addr, string? s, int maxChars)
    {
        int n = 0;
        if (s != null)
        {
            for (int i = 0; i < s.Length && n < maxChars; i += char.IsSurrogatePair(s, i) ? 2 : 1, n++)
                GuestMemory.W32(addr + (uint)n * 4, (uint)char.ConvertToUtf32(s, i));
        }
        GuestMemory.W32(addr + (uint)n * 4, 0);
    }

    /// <summary>SceScreenShotParam (0x10): +0 photoTitle, +4 gameTitle, +8 gameComment (UTF-8 strings), +0xC reserved
    /// (NULL). As libSceScreenShot 3.74: a NULL param or a non-NULL reserved word is 0x80102F01; a NULL string is an
    /// empty one; a title of 0x100 bytes or more, or a comment of 0x200 or more, is 0x80102F01. The module then hands the
    /// three strings to the screenshot service of the shell, which keeps them for the captures.</summary>
    [Hle]
    int sceScreenShotSetParam(uint param)
    {
        return SetParam(param, persistent: false);
    }

    /// <summary>NID_0757A17F(param): sceScreenShotSetParam's twin; the module sends the same request with its flag
    /// byte set to 1 instead of 0.</summary>
    [Hle("NID_0757A17F", Nid = 0x0757A17F, Library = "SceScreenShot")]
    int ScreenShotSetParamFlagged(uint param)
    {
        return SetParam(param, persistent: true);
    }

    /// <summary>The flag byte of the last SetParam request (NID_0757A17F sends 1, sceScreenShotSetParam 0).</summary>
    [SnapshotMissing(false)] // states of builds before it: the call did not exist, so the flag was never set
    public bool ParamFlag { get; private set; }

    int SetParam(uint param, bool persistent)
    {
        if (param == 0 || GuestMemory.R32(param + 12) != 0)
        {
            return ERR_INVALID_ARGUMENT;
        }

        string? photo = ReadParamString(GuestMemory.R32(param), TitleBytes);
        string? game = ReadParamString(GuestMemory.R32(param + 4), TitleBytes);
        string? comment = ReadParamString(GuestMemory.R32(param + 8), CommentBytes);
        if (photo == null || game == null || comment == null)
        {
            return ERR_INVALID_ARGUMENT;
        }

        lock (_lock)
        {
            PhotoTitle = photo;
            GameTitle = game;
            GameComment = comment;
            ParamFlag = persistent;
        }

        Log.Debug($"sceScreenShotSetParam(photo='{photo}', game='{game}', comment='{comment}', flag={(persistent ? 1 : 0)})");
        return 0;
    }

    /// <summary>A SetParam string: empty for a NULL pointer, null when it does not end within <paramref name="limit"/> - 1
    /// bytes (the module's sceClibStrnlen(s, limit) &gt; limit - 1 check).</summary>
    static string? ReadParamString(uint address, int limit)
    {
        if (address == 0)
        {
            return "";
        }

        ReadOnlySpan<byte> bytes = GuestMemory.ReadSpan(address, limit);
        int length = bytes.IndexOf((byte)0);
        if (length < 0)
        {
            return null;
        }

        return Encoding.UTF8.GetString(bytes[..length]);
    }

    /// <summary>Bytes of the photo and game title fields and of the comment field (SceScreenShotParam strings and the
    /// sceScreenShotGetParam record).</summary>
    const int TitleBytes = 0x100, CommentBytes = 0x200;

    /// <summary>sceScreenShotGetParam(const char *path, SceScreenShotParamInfo *info): the metadata of a photo file, as
    /// libSceScreenShot 3.74 reads it (<see cref="ScreenShotFile.ReadParam"/>): +0 the photo title (the file name without
    /// its extension), +0x100 the game title and +0x200 the comment (the maker note of the JPEG's Exif data), +0x400 the
    /// capture date (DateTimeOriginal: year, month, day, hour, minute, second as 16-bit words, then a zero word).</summary>
    [Hle]
    int sceScreenShotGetParam(uint pathPtr, uint info)
    {
        if (info == 0)
        {
            // the module clears the record before anything else: a NULL record is a fault on the console
            return ERR_INVALID_ARGUMENT;
        }

        string path = pathPtr == 0 ? "" : GuestMemory.ReadCString(pathPtr, 0x400);
        string? host = path.Length == 0 ? null : Emu.Vfs.Resolve(path);
        Stream? file = null;
        try
        {
            if (host != null && File.Exists(host))
            {
                file = File.OpenRead(host);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            file = null;
        }

        if (file == null)
        {
            return ERR_FILE_NOT_FOUND;
        }

        using (file)
        {
            byte[] record = new byte[ScreenShotFile.ParamInfoSize];
            int result = ScreenShotFile.ReadParam(file, GuestMemory.ReadSpan(pathPtr, Encoding.UTF8.GetByteCount(path)), record, out bool dated);
            // the module clears the three strings first and writes each field as the parse reaches it
            record.AsSpan(0, ScreenShotFile.StringsSize).CopyTo(GuestMemory.Span(info, ScreenShotFile.StringsSize));
            if (dated)
            {
                int dateSize = ScreenShotFile.ParamInfoSize - ScreenShotFile.StringsSize;
                record.AsSpan(ScreenShotFile.StringsSize, dateSize).CopyTo(GuestMemory.Span(info + ScreenShotFile.StringsSize, dateSize));
            }

            return result;
        }
    }

    /// <summary>Registers the watermark drawn over captures (normally a PNG in app0:); a null path removes it.</summary>
    [Hle]
    int sceScreenShotSetOverlayImage(uint pathPtr, int offsetX, int offsetY)
    {
        if (pathPtr == 0)
        {
            lock (_lock) { OverlayPath = null; OverlayX = OverlayY = 0; }
            return 0;
        }
        string path = GuestMemory.ReadCString(pathPtr);
        string resolved = Emu.Hle.Modules.OfType<Fios2User>().FirstOrDefault()?.Resolve(path) ?? path;
        var host = Emu.Vfs.Resolve(resolved);
        if (host == null || !File.Exists(host))
        {
            Log.Warn($"sceScreenShotSetOverlayImage: '{path}' not found");
            return ERR_FILE_NOT_FOUND;
        }
        lock (_lock) { OverlayPath = host; OverlayX = offsetX; OverlayY = offsetY; }
        Log.Debug($"sceScreenShotSetOverlayImage('{path}', {offsetX}, {offsetY})");
        return 0;
    }

    /// <summary>Programmatic capture to the photo gallery. The prototype is not public (not in the SDK headers, not
    /// implemented by Vita3K): the arguments are logged so a game that calls it reveals them. Nothing is written.</summary>
    [Hle]
    int sceScreenShotCapture(uint a0, uint a1, uint a2, uint a3)
    {
        Log.WarnOnce("screenshot-capture", $"sceScreenShotCapture(0x{a0:X8}, 0x{a1:X8}, 0x{a2:X8}, 0x{a3:X8}): capture not saved (no host capture yet)");
        return 0;
    }
}
