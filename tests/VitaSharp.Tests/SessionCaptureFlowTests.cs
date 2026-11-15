using VitaSharp.Core.Capture;

namespace VitaSharp.Tests;

/// <summary>
/// "Gravar tudo" end to end on an emulator without a game: a recording started and stopped with frames presented in
/// between leaves a folder (frames file, input log, notes) and its zip; a second start while one runs is refused, a
/// stop without a recording does nothing, and the pre-roll can be armed and disarmed.
/// </summary>
[Collection("guest")]
public sealed class SessionCaptureFlowTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "capture");

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void RecordingWritesAFolderAndItsZip()
    {
        string root = Path.Combine(_harness.Root, "captures");
        IPresentTap? tap = null;
        var status = new List<string>();
        var cap = new SessionCapture(_harness.Emu, new SessionCaptureOptions
        {
            Root = root,
            SetPresentTap = t => tap = t,
            StartState = false,
            GpuDump = false,
            Zip = true,
            Status = s =>
            {
                lock (status)
                {
                    status.Add(s);
                }
            },
            Threads = () => "no threads",
            Info = () => "test info",
        });
        Assert.False(cap.IsRecording);
        Assert.False(cap.Stop()); // nothing to stop
        Assert.True(cap.Start("test"));
        HleHarness.Until(() => cap.IsRecording && tap != null, "the recording to start");
        Assert.False(cap.Start("again")); // one at a time
        const int Width = 32, Height = 16;
        byte[] px = new byte[Width * Height * 4];
        for (int f = 0; f < 5; f++)
        {
            for (int i = 0; i < px.Length; i++)
            {
                px[i] = (byte)(i * 7 + f * 13);
            }

            var info = new PresentInfo { Present = f + 1, Flip = f, VBlank = f * 2, PresentNs = CaptureClock.NowNs, IntervalMs = 16.7f, Hitch = "" };
            tap!.OnPresent(info, px, Width, Height);
        }
        cap.NoteKey("F8");
        Assert.True(cap.RecordingSeconds >= 0);
        Assert.True(cap.Stop("test end"));
        Assert.True(cap.Idle.Wait(120_000), "the recording was not saved");
        Assert.False(cap.IsRecording);
        Assert.NotNull(cap.Dir);
        Assert.True(File.Exists(Path.Combine(cap.Dir!, "frames.vsfr")));
        Assert.True(new FileInfo(Path.Combine(cap.Dir!, "frames.vsfr")).Length > 0);
        Assert.NotNull(cap.LastZip);
        Assert.True(File.Exists(cap.LastZip));
        Assert.Equal(-1, cap.SavePercent);
        lock (status)
        {
            Assert.NotEmpty(status);
        }

        cap.SetPreRoll(5);
        Assert.True(cap.Armed);
        Assert.Equal(5, cap.PreRollSeconds);
        cap.SetPreRoll(0);
        Assert.False(cap.Armed);
        cap.Hurry();
        cap.Dispose();
    }

    /// <summary>A pause of the emulation during a recording (the pause menu, a save state) leaves a pause and a resume
    /// mark in input.tsv: the analyzer tells a present interval spent paused from a stall of the game.</summary>
    [Fact]
    public void PausesDuringARecordingAreMarked()
    {
        string root = Path.Combine(_harness.Root, "captures_pause");
        IPresentTap? tap = null;
        var cap = new SessionCapture(_harness.Emu, new SessionCaptureOptions
        {
            Root = root,
            SetPresentTap = t => tap = t,
            StartState = false,
            GpuDump = false,
            Zip = false,
            Threads = () => "no threads",
            Info = () => "test info",
        });
        try
        {
            Assert.True(cap.Start("test"));
            HleHarness.Until(() => cap.IsRecording && tap != null, "the recording to start");
            try
            {
                Assert.True(VitaSharp.Core.EmulationPause.Pause("test menu"));
            }
            finally
            {
                VitaSharp.Core.EmulationPause.Resume("test menu");
            }
            Assert.True(cap.Stop("test end"));
            Assert.True(cap.Idle.Wait(120_000), "the recording was not saved");
            var marks = File.ReadAllLines(Path.Combine(cap.Dir!, "input.tsv")).Select(InputEvent.Parse)
                .Where(e => e is { Kind: "mark" }).Select(e => e!.Value.Data).ToList();
            int paused = marks.IndexOf(CaptureMarks.Paused);
            Assert.True(paused >= 0, "no pause mark: " + string.Join(", ", marks));
            Assert.Equal(CaptureMarks.Resumed, marks[paused + 1]);
        }
        finally
        {
            cap.Hurry();
            cap.Dispose();
        }
    }

    /// <summary>Arming the pre-roll starts the encoder thread and disarming it before that thread runs clears the
    /// queue: the thread must work on the queue it was started with, not read the field after the stop cleared it
    /// (that read crashed the whole test process with a NullReferenceException on the encoder thread).</summary>
    [Fact]
    public void PreRollDisarmedBeforeTheEncoderThreadRunsKeepsItsQueue()
    {
        var cap = new SessionCapture(_harness.Emu, new SessionCaptureOptions
        {
            Root = Path.Combine(_harness.Root, "captures_arm"),
            SetPresentTap = _ => { },
            StartState = false,
            GpuDump = false,
            Zip = false,
            Threads = () => "no threads",
            Info = () => "test info",
        });
        using var stopped = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        Thread? encoder = null;
        cap.EncoderStarting = () =>
        {
            encoder = Thread.CurrentThread;
            started.Set();
            stopped.Wait();
        };
        try
        {
            cap.SetPreRoll(5);
            Assert.True(started.Wait(TimeSpan.FromMinutes(2)), "the encoder thread did not start");
            cap.SetPreRoll(0); // the stop clears the queue while the new encoder thread has not looked at it yet
            Assert.False(cap.Armed);
            stopped.Set();
            Assert.True(encoder!.Join(TimeSpan.FromMinutes(2)), "the encoder thread did not end with its queue");
            cap.EncoderStarting = null;
            cap.SetPreRoll(5); // arming again starts a working encoder
            Assert.True(cap.Armed);
        }
        finally
        {
            stopped.Set();
            cap.Hurry();
            cap.Dispose();
        }
    }
}
