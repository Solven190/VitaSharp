using VitaSharp.Core.Host;
using VitaSharp.Core.Localization;

namespace VitaSharp.Mobile.Core.Settings;

/// <summary>
/// "Camera frontal" / "Camera traseira" in Ajustes (<see cref="AppPreferences.CameraFront"/>,
/// <see cref="AppPreferences.CameraBack"/>), the desktop launcher's camera options: the phone's camera of that side, a
/// still picture or nothing (grey frames). The values are the desktop's (<see cref="CameraSpec"/>); a picture picked on
/// the phone is copied into the data folder (camera/front.jpg, camera/back.png) and stored relative to it, so it moves
/// with the folder.
/// </summary>
public static class CameraSettings
{
    /// <summary>The value of the "picture" choice of the picker (the picture itself is asked for next).</summary>
    public const string PictureChoice = "\u0001picture";

    /// <summary>Folder of the copied pictures, relative to the data folder.</summary>
    public const string PictureFolder = "camera";

    /// <summary>The picker's choices: the phone's camera, a picture, none.</summary>
    public static IReadOnlyList<Choice> Choices =>
    [
        new("", Loc.Get("MCameraPhone")),
        new(PictureChoice, Loc.Get("CameraPickPicture")),
        new(CameraSpec.None.Value, Loc.Get("CameraNone")),
    ];

    /// <summary>The row's text for a setting: the phone's camera, none, or the picture's file name.</summary>
    public static string Label(string? value)
    {
        var spec = CameraSpec.Parse(value);
        return spec.Kind switch
        {
            CameraSpecKind.None => Loc.Get("CameraNone"),
            CameraSpecKind.Image => Loc.Format("CameraPicture", Path.GetFileName(spec.Value)),
            _ => Loc.Get("MCameraPhone"),
        };
    }

    /// <summary>
    /// The setting for a picture the user picked (<paramref name="pickedName"/>: its display name) for one camera: the
    /// relative path it is copied to (camera/front.&lt;ext&gt;, camera/back.&lt;ext&gt;), or null when it is no PNG or JPEG
    /// (what <see cref="StillImageCamera"/> reads).
    /// </summary>
    public static string? PictureSetting(CameraFacing facing, string pickedName)
    {
        if (!CameraSpec.IsImagePath(pickedName))
        {
            return null;
        }

        string extension = Path.GetExtension(pickedName).ToLowerInvariant();
        string side = facing == CameraFacing.Front ? "front" : "back";
        return $"{PictureFolder}/{side}{extension}";
    }
}
