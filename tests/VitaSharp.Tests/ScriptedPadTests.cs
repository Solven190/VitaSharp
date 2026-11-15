using VitaSharp.Core.Host;
using VitaSharp.Mobile.Core.Input;

namespace VitaSharp.Tests;

/// <summary>
/// The Android app's scripted input (Mobile.Core/Input/ScriptedPad.cs, VITASHARP_ANDROID_INPUT and the live file): a
/// <c>savestate</c>/<c>loadstate</c> step fires exactly once even when several guest threads read the pad at the same
/// time (a game polls it from more than one thread). A step fired twice ran a second load right after the first one on
/// the phone (Killzone, "thread 'PadThread' did not stop").
/// </summary>
public sealed class ScriptedPadTests
{
    private const int Threads = 8;

    [Fact]
    public void StateStepsFireOnceWithManyThreadsPollingThePad()
    {
        const int steps = 64;
        string script = string.Join(",", Enumerable.Range(0, steps).Select(i => $"loadstate@0:{(i % 9) + 1}"));
        for (int round = 0; round < 50; round++)
        {
            var pad = new ScriptedPad(script);
            int fired = 0;
            pad.StateRequested += (_, _) => Interlocked.Increment(ref fired);
            PollFromManyThreads(pad);
            Assert.Equal(steps, fired);
        }
    }

    [Fact]
    public void LiveFileStepsAreTakenOnceWithManyThreadsPollingThePad()
    {
        string folder = Path.Combine(Path.GetTempPath(), "vitasharp-scriptedpad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            for (int round = 0; round < 50; round++)
            {
                string live = Path.Combine(folder, $"input_live_{round}");
                File.WriteAllText(live, "loadstate@0:1\nsavestate@0:2\nloadstate@0:3\n");
                var pad = new ScriptedPad("") { LivePath = live };
                int fired = 0;
                pad.StateRequested += (_, _) => Interlocked.Increment(ref fired);
                PollFromManyThreads(pad);
                Assert.Equal(3, fired);
                Assert.False(File.Exists(live));
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    /// <summary>Every thread starts polling at the same moment (a barrier), like guest threads reading the pad.</summary>
    private static void PollFromManyThreads(ScriptedPad pad)
    {
        using var start = new Barrier(Threads);
        var workers = new Thread[Threads];
        for (int i = 0; i < Threads; i++)
        {
            workers[i] = new Thread(() =>
            {
                start.SignalAndWait();
                for (int poll = 0; poll < 200; poll++)
                {
                    pad.Apply(default(ControllerState));
                }
            });
            workers[i].Start();
        }

        foreach (Thread worker in workers)
        {
            worker.Join();
        }
    }
}
