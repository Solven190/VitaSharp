using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Hle.Np;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;
using VitaSharp.Desktop;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// The command-line side of a run (VitaSharp.Desktop <see cref="Runner"/>) outside the emulation: options and trace
/// filters as the docs write them, on/off switches, --env= switches (first wins), the executable a run boots, the
/// frame dump options, the contact sheets and frame metrics of a --dump-frames run (black, blown-out, magenta,
/// flicker and frozen frames), --poke= and the emulator keys of an --input script.
/// </summary>
[Collection("guest")]
public sealed class RunnerOptionsTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-runner-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void OptionsAreNameEqualsValueAndTheFirstWins()
    {
        string[] o = ["--cpu=interp", "--seconds=30", "--seconds=60", "--self=Jak1.self", "--headless"];
        Assert.Equal("interp", Runner.Opt(o, "--cpu"));
        Assert.Equal("30", Runner.Opt(o, "--seconds")); // a scenario's args= come after the command line's
        Assert.Null(Runner.Opt(o, "--headless")); // a flag has no value
        Assert.Null(Runner.Opt(o, "--sec")); // not a prefix match
        Assert.Equal("", Runner.Opt(["--dump-frames="], "--dump-frames"));
    }

    /// <summary>The HLE docs write trace filters as "--trace-hle sceGxm" too: without this the filter was ignored.</summary>
    [Fact]
    public void TraceFiltersTakeTheValueAfterASpaceToo()
    {
        Assert.Equal("sceGxm*", Runner.TraceOpt(["--trace-hle=sceGxm*"], "--trace-hle"));
        Assert.Equal("sceGxm*", Runner.TraceOpt(["--trace-hle", "sceGxm*", "--headless"], "--trace-hle"));
        Assert.Null(Runner.TraceOpt(["--trace-hle", "--headless"], "--trace-hle")); // the next option is not its value
        Assert.Null(Runner.TraceOpt(["--trace-hle"], "--trace-hle"));
        Assert.Null(Runner.TraceOpt(["--cpu=interp"], "--trace-hle"));
    }

    [Fact]
    public void OnOffSwitchesTakeTheUsualSpellings()
    {
        foreach (string on in new[] { "on", "1", "true", "YES", " On " })
        {
            Assert.True(Runner.OnOff([$"--psn={on}"], "--psn"), on);
        }

        foreach (string off in new[] { "off", "0", "false", "No" })
        {
            Assert.False(Runner.OnOff([$"--psn={off}"], "--psn"), off);
        }

        Assert.Null(Runner.OnOff(["--psn=maybe"], "--psn")); // not recognised: the default
        Assert.Null(Runner.OnOff(["--net=on"], "--psn"));
    }

    /// <summary>--env=KEY=VALUE sets the switch for the run; for a key given twice the first wins (the command line
    /// before a scenario's args=), and a malformed one is ignored.</summary>
    [Fact]
    public void EnvOptionsSetTheSwitchesFirstWins()
    {
        string key = "VITASHARP_TEST_RUNNER_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        try
        {
            Runner.ApplyEnvOptions(["--cpu=interp", $"--env={key}=a=b", $"--env={key}=second", "--env==nokey", "--env=noequals"]);
            Assert.Equal("a=b", Environment.GetEnvironmentVariable(key)); // the value may contain '='
            Assert.Null(Environment.GetEnvironmentVariable("noequals"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    [Fact]
    public void RecordingTimesAreMinutesAndSeconds()
    {
        Assert.Equal("00:00", Runner.RecTime(0));
        Assert.Equal("00:59", Runner.RecTime(59.9));
        Assert.Equal("01:05", Runner.RecTime(65));
        Assert.Equal("61:01", Runner.RecTime(3661));
    }

    [Fact]
    public void ATrophyNoticeNamesTheTrophyAndItsGrade()
    {
        var e = new TrophyUnlockedEvent("NPWR00000_00", "Test set", new TrophyInfo(3, TrophyGrade.Gold, false, 0, 0, "Golden test", "detail"));
        string notice = Runner.TrophyNotice(e);
        Assert.Contains("Golden test", notice);
        Assert.Contains(VitaSharp.Desktop.Host.TrophyToast.GradeName(TrophyGrade.Gold), notice);
    }

    /// <summary>The executable a run boots: --self, else the one a --load-state state was saved in (a state of a
    /// program a LoadExec started), else eboot.bin; a state that names no program (or no file) boots eboot.bin, also
    /// with an @fN frame suffix.</summary>
    [Fact]
    public void TheBootExecutableIsSelfOrTheStatesOrEboot()
    {
        Assert.Equal("Jak1.self", Runner.BootSelf(["--self=Jak1.self", "--load-state=x.vss"]));
        Assert.Equal("eboot.bin", Runner.BootSelf(["--cpu=interp"]));
        string missing = Path.Combine(_root, "none.vss");
        Assert.Equal("eboot.bin", Runner.BootSelf([$"--load-state={missing}"]));
        Assert.Equal("eboot.bin", Runner.BootSelf([$"--load-state={missing}@f120"]));
    }

    /// <summary>--dump-frames=DIR (the folder's old PNGs deleted, other files kept), --dump-every, --dump-from,
    /// --trace-draws and --no-inspect; without --dump-frames only the defaults (every 60th present, from 0, no draw
    /// trace) are set.</summary>
    [Fact]
    public void FrameDumpOptionsSetTheRendererOptions()
    {
        var plain = new VulkanRendererOptions();
        Assert.Null(Runner.ApplyFrameDumpOptions(["--cpu=interp"], plain));
        Assert.Equal(60, plain.DumpEvery);
        Assert.Equal(0, plain.DumpFrom);
        Assert.Equal(-1, plain.TraceDrawsFrame);
        Assert.Null(plain.DumpFramesDir);

        string dir = Path.Combine(_root, "frames");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "frame_1.png"), [1]);
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "kept");
        var ro = new VulkanRendererOptions();
        Assert.Equal(dir, Runner.ApplyFrameDumpOptions([$"--dump-frames={dir}", "--dump-every=5", "--dump-from=100", "--trace-draws=120", "--no-inspect"], ro));
        Assert.Equal(5, ro.DumpEvery);
        Assert.Equal(100, ro.DumpFrom);
        Assert.Equal(120, ro.TraceDrawsFrame);
        Assert.Equal(dir, ro.DumpFramesDir);
        Assert.False(ro.InspectTextures);
        Assert.Empty(Directory.GetFiles(dir, "*.png"));
        Assert.True(File.Exists(Path.Combine(dir, "notes.txt")));

        var inspect = new VulkanRendererOptions();
        Runner.ApplyFrameDumpOptions([$"--dump-frames={dir}", "--dump-every=x"], inspect);
        Assert.True(inspect.InspectTextures);
        Assert.Equal(60, inspect.DumpEvery); // not a number: the default
    }

    /// <summary>
    /// The contact sheets of a --dump-frames run: 30 thumbnails (240x136, 5 per row) per sheet, in frame order, each
    /// with its frame number burnt in (yellow on black, top left); metrics.csv has one line per frame and the log
    /// names the black, blown-out, magenta (missing texture), flicker and frozen frames.
    /// </summary>
    [Fact]
    public void ContactSheetsAndFrameMetricsFindTheBadFrames()
    {
        const int W = 240, H = 136;
        byte[] Fill(byte r, byte g, byte b)
        {
            byte[] px = new byte[W * H * 4];
            for (int i = 0; i < px.Length; i += 4)
            {
                px[i] = r;
                px[i + 1] = g;
                px[i + 2] = b;
                px[i + 3] = 255;
            }

            return px;
        }

        var thumbs = new List<(long Frame, byte[] Rgba)>();
        // 31 frames: 0 black, 1 white (out of black: a fade, not a flicker), 2 magenta, 3..5 the same grey (frozen),
        // 6 dark grey then 7 bright grey (a flicker), 8..30 near 7's brightness, each a little different
        thumbs.Add((0, Fill(0, 0, 0)));
        thumbs.Add((1, Fill(250, 250, 250)));
        thumbs.Add((2, Fill(255, 0, 255)));
        for (long f = 3; f <= 5; f++)
        {
            thumbs.Add((f, Fill(128, 128, 128)));
        }

        thumbs.Add((6, Fill(40, 40, 40)));
        thumbs.Add((7, Fill(200, 200, 200)));
        for (long f = 8; f <= 30; f++)
        {
            thumbs.Add((f, Fill(200, 200, (byte)(4 * f))));
        }

        thumbs.Reverse(); // the sheets are in frame order whatever order the dumps came in
        string dir = Path.Combine(_root, "sheets");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "sheet_9.png"), [0]); // an old run's sheet is replaced
        // metrics.csv is read by tools and spreadsheets: its numbers keep the '.' on a pt-BR machine (it had "0,000")
        System.Globalization.CultureInfo culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("pt-BR");
        try
        {
            Runner.WriteContactSheet(dir, thumbs);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }

        Assert.False(File.Exists(Path.Combine(dir, "sheet_9.png")));
        (byte[] sheet, int sw, int sh) = PngReader.Read(Path.Combine(dir, "sheet.png"));
        Assert.Equal((5 * W, 6 * H), (sw, sh)); // 30 frames: 6 rows of 5
        (byte[] second, int w2, int h2) = PngReader.Read(Path.Combine(dir, "sheet_2.png"));
        Assert.Equal((5 * W, H), (w2, h2)); // the 31st
        // the first cell is frame 0 (black), its number "0" drawn in yellow from (3, 3): the top bar of the digit
        Assert.Equal(new byte[] { 255, 230, 0, 255 }, Pixel(sheet, sw, 3, 3));
        // the second cell (frame 1, white) keeps its colour away from the number
        Assert.Equal(new byte[] { 250, 250, 250, 255 }, Pixel(sheet, sw, W + 120, 60));
        // sheet 2's only cell is frame 30, with "30" burnt in on a black box
        Assert.Equal(new byte[] { 255, 230, 0, 255 }, Pixel(second, w2, 3, 3));

        string[] csv = File.ReadAllLines(Path.Combine(dir, "metrics.csv"));
        Assert.Equal("frame,mean,white,black,hash", csv[0]);
        Assert.Equal(32, csv.Length);
        Assert.StartsWith("0,0.000,0.000,1.000,", csv[1]);
        Assert.StartsWith("1,0.980,1.000,0.000,", csv[2]);

        string[] findings = Runner.WriteFrameMetrics(dir, thumbs.OrderBy(t => t.Frame).ToList());
        string Metric(string what) => findings.Single(l => l.StartsWith("METRICS " + what, StringComparison.Ordinal));

        Assert.Contains("1 frames (0..0)", Metric("black"));
        Assert.Contains("1 frames (1..1)", Metric("blown-out"));
        Assert.Contains("1 frames (2..2)", Metric("magenta"));
        Assert.Contains("2 frames (2..7)", Metric("flicker")); // white to magenta, dark to bright grey; 1 comes out of black: a fade
        Assert.Contains("longest run of identical dumps 3 from frame 4", Metric("frozen"));
    }

    private static byte[] Pixel(byte[] rgba, int width, int x, int y)
    {
        int at = ((y * width) + x) * 4;
        return rgba[at..(at + 4)];
    }

    /// <summary>--poke=ADDR:VALUE@fN writes the 32-bit value into guest memory once frame N is presented.</summary>
    [Fact]
    public void PokesWriteGuestMemoryAtTheirFrame()
    {
        using var harness = new HleHarness(fixture, "poke");
        uint a = harness.Alloc(16);
        long now = Display.FramesPresented;
        Runner.InstallPokes($"{a:X}:CAFEF00D@f{now},{a + 4:X}:12345678@{now}");
        HleHarness.Until(() => GuestMemory.R32(a) == 0xCAFEF00D && GuestMemory.R32(a + 4) == 0x12345678, "the pokes");
        Assert.Equal(0u, GuestMemory.R32(a + 8));
    }

    /// <summary>The emulator keys of an --input script go to the graphics hotkeys (F2 / Shift+F2 here: the frame rate
    /// mode), once each, when they come due; game keys are not emulator keys.</summary>
    [Fact]
    public void ScriptedHostKeysReachTheGraphicsHotkeys()
    {
        FrameRateMode saved = FrameRate.Mode;
        using var harness = new HleHarness(fixture, "hostkeys");
        var notices = new List<string>();
        var graphics = new GraphicsHotkeys(harness.Emu, new VulkanRendererOptions(), () => null, () => throw new InvalidOperationException("no renderer"), notices.Add);
        try
        {
            FrameRate.Mode = FrameRateMode.Native;
            var script = new ScriptedInput("key@0:F2,key@0.01:F2,key@0.02:Shift+F2,cross@0,key@3600:F2");
            Thread.Sleep(50); // the three first keys are due
            Runner.RunScriptedHostKeys(script, null!, null, graphics);
            Assert.Equal(FrameRateMode.Fixed30, FrameRate.Mode); // F2, F2, Shift+F2
            Assert.Equal(3, notices.Count);
            Runner.RunScriptedHostKeys(script, null!, null, graphics); // nothing new came due (the last one is in an hour)
            Assert.Equal(3, notices.Count);
        }
        finally
        {
            FrameRate.Mode = saved;
        }
    }
}
