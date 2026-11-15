using VitaSharp.Core.Host;
using VitaSharp.Core.Localization;
using VitaSharp.Desktop.Launcher;
using VitaSharp.Mobile.Core.Settings;

namespace VitaSharp.Tests;

/// <summary>
/// The source of each Vita camera, chosen the same way on both apps (<see cref="FacingCameras.FromSettings"/>): the
/// desktop's "Camera frontal" / "Camera traseira" (--camera-front= / --camera-back=) and the Android app's camera
/// settings (<see cref="AppPreferences.CameraFront"/>, <see cref="CameraSettings"/>): the host's camera, a still
/// picture or nothing (grey frames).
/// </summary>
public sealed class AndroidCameraSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-camera-source-" + Guid.NewGuid().ToString("N"));

    public AndroidCameraSourceTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }

    private sealed class Probe : ICameraSource
    {
        public IReadOnlyList<string> DeviceNames => ["probe"];

        public ICameraStream? Open(CameraFacing facing, int width, int height, int fps)
        {
            return null;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>A 1x1 RGB PNG at <paramref name="path"/> (what StillImageCamera reads).</summary>
    private static void WritePng(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var ms = new MemoryStream();
        void Chunk(string type, byte[] body)
        {
            byte[] length = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, body.Length);
            ms.Write(length);
            ms.Write(System.Text.Encoding.ASCII.GetBytes(type));
            ms.Write(body);
            ms.Write(new byte[4]);
        }

        ms.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10]);
        Chunk("IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 2, 0, 0, 0]);
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, true))
            {
                zlib.Write([0, 10, 20, 30]);
            }

            Chunk("IDAT", compressed.ToArray());
        }

        Chunk("IEND", []);
        File.WriteAllBytes(path, ms.ToArray());
    }

    [Fact]
    public void DefaultSettingsGiveBothCamerasTheHostCamera()
    {
        var probe = new Probe();
        var calls = new List<(string?, string?)>();
        FacingCameras cameras = Assert.IsType<FacingCameras>(FacingCameras.FromSettings("", "", (front, back) =>
        {
            calls.Add((front, back));
            return probe;
        }));
        Assert.Equal([((string?)null, (string?)null)], calls);
        Assert.Same(probe, cameras[CameraFacing.Front]);
        Assert.Same(probe, cameras[CameraFacing.Back]);
    }

    [Fact]
    public void NoneOnBothSidesIsNoCameraAndNeverOpensTheHostOne()
    {
        bool made = false;
        Assert.Null(FacingCameras.FromSettings("none", "NONE", (_, _) =>
        {
            made = true;
            return new Probe();
        }));
        Assert.False(made);
    }

    [Fact]
    public void APictureRelativeToTheDataFolderIsLoadedAndTheOtherSideKeepsItsDevice()
    {
        WritePng(Path.Combine(_root, "camera", "front.png"));
        var probe = new Probe();
        (string?, string?) selection = default;
        FacingCameras cameras = Assert.IsType<FacingCameras>(FacingCameras.FromSettings("camera/front.png", "USB", (front, back) =>
        {
            selection = (front, back);
            return probe;
        }, _root));
        Assert.IsType<StillImageCamera>(cameras[CameraFacing.Front]);
        Assert.Same(probe, cameras[CameraFacing.Back]);
        Assert.Equal(((string?)null, "USB"), selection);
    }

    [Fact]
    public void AMissingPictureIsGreyFramesOnThatSide()
    {
        FacingCameras cameras = Assert.IsType<FacingCameras>(FacingCameras.FromSettings("camera/missing.jpg", "", (_, _) => new Probe(), _root));
        Assert.Null(cameras[CameraFacing.Front]);
        Assert.NotNull(cameras[CameraFacing.Back]);
    }

    [Fact]
    public void CameraOptionsHaveTheDesktopDefaultsAndValues()
    {
        var android = new AppPreferences();
        var desktop = new LauncherSettings();
        Assert.Equal(desktop.CameraFront, android.CameraFront);
        Assert.Equal(desktop.CameraBack, android.CameraBack);
        Assert.Equal("", android.CameraFront);
        // the Android choices are values the desktop passes as --camera-front= / --camera-back=
        Assert.Equal(["", CameraSettings.PictureChoice, "none"], CameraSettings.Choices.Select(c => c.Value));
        Assert.Contains("--camera-front=none", new LauncherSettings { CameraFront = "none" }.RunOptions());
        Assert.Equal(CameraSpecKind.None, CameraSpec.Parse("none").Kind);
        Assert.Equal(CameraSpecKind.Image, CameraSpec.Parse(CameraSettings.PictureSetting(CameraFacing.Back, "x.png")).Kind);
    }

    [Fact]
    public void PickedPicturesGoToTheDataFolderByCameraAndOnlyPngOrJpeg()
    {
        Assert.Equal("camera/front.jpg", CameraSettings.PictureSetting(CameraFacing.Front, "IMG_0001.JPG"));
        Assert.Equal("camera/back.png", CameraSettings.PictureSetting(CameraFacing.Back, "face.png"));
        Assert.Equal("camera/front.jpeg", CameraSettings.PictureSetting(CameraFacing.Front, "a.jpeg"));
        Assert.Null(CameraSettings.PictureSetting(CameraFacing.Front, "anim.gif"));
        Assert.Equal(Loc.Format("CameraPicture", "front.jpg"), CameraSettings.Label("camera/front.jpg"));
        Assert.Equal(Loc.Get("CameraNone"), CameraSettings.Label("none"));
        Assert.Equal(Loc.Get("MCameraPhone"), CameraSettings.Label(""));
    }

    [Fact]
    public void CameraSettingsSurviveTheSettingsFileAndOldFilesGetTheDefaults()
    {
        string path = Path.Combine(_root, "app.json");
        File.WriteAllText(path, "{\"VSync\":true}");
        Assert.Equal(("", ""), (AppPreferences.Load(path).CameraFront, AppPreferences.Load(path).CameraBack));
        new AppPreferences { CameraFront = "camera/front.png", CameraBack = "none" }.Save(path);
        var saved = AppPreferences.Load(path);
        Assert.Equal(("camera/front.png", "none"), (saved.CameraFront, saved.CameraBack));
    }
}
