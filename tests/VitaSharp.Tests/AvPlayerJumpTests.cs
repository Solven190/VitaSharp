using System.Reflection;
using System.Runtime.CompilerServices;
using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Io;
using VitaSharp.Core.Media;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// sceAvPlayerJumpToTime as libsceavplayer (FW 3.74) does it: the demuxer goes back to the sync sample before the jump
/// time and discards what comes before that time ("DiscardAU ... New PTS start"), and another jump is refused with
/// 0x806a0002 while the player is still in its jump state. Killzone: Mercenary keeps its campaign intro in step with
/// its own audio by jumping; with the pictures before the jump time handed out and every jump accepted, the movie on
/// the phone's software decoder went from 24 to 0.7 fps, decoding the same seconds again and again (14/11/2026).
/// </summary>
[Collection("guest")]
public sealed class AvPlayerJumpTests(GuestMemoryFixture fixture) : IDisposable
{
    private const long FrameUs = 33_367;
    private const int ErrInvalidArgument = unchecked((int)0x806a0002);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-avjump-" + Guid.NewGuid().ToString("N"));
    private readonly bool _legacy = MediaPlayerBase.LegacyJumps;

    public void Dispose()
    {
        MediaPlayerBase.LegacyJumps = _legacy;
        MediaPlayers.Backend = null;
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A media player whose decoder thread is the test: it hands out the seek it got and delivers pictures.</summary>
    private sealed class SteppedPlayer : MediaPlayerBase
    {
        public SteppedPlayer()
        {
            Width = 2;
            Height = 2;
            FrameRate = 1_000_000.0 / FrameUs;
        }

        public override bool HasVideo => true;
        public override bool HasAudio => false;

        /// <summary>The seek the decoder thread would act on now (-1 = none), taken as it does.</summary>
        public long TakeSeek()
        {
            lock (SyncRoot)
            {
                long seek = SeekRequestUs;
                SeekRequestUs = -1;
                return seek;
            }
        }

        /// <summary>Pictures waiting for the guest (the queue holds at most 12; a full one would block the producer).</summary>
        public int Queued
        {
            get
            {
                lock (SyncRoot)
                {
                    return VideoQueue.Count;
                }
            }
        }

        public void Deliver(long timeUs) => Enqueue(new VideoFrame(new byte[6], 2, 2, timeUs));

        public bool Skips(byte[] accessUnit, long timeUs) => SkipBeforeJump(accessUnit, timeUs);

        public void EndOfFile() => EndJump();

        public override void Dispose()
        {
        }
    }

    private static readonly byte[] s_nonReferenceUnit = [0, 0, 0, 1, 0x01, 0x9E, 0x80];
    private static readonly byte[] s_referenceUnit = [0, 0, 0, 1, 0x41, 0x9A, 0x80];
    private static readonly byte[] s_idrUnit = [0, 0, 0, 1, 0x67, 0x42, 0, 0, 0, 1, 0x65, 0x88, 0x80];

    [Fact]
    public void AJumpHandsOutNothingBeforeItsTime()
    {
        MediaPlayerBase.LegacyJumps = false;
        using var player = new SteppedPlayer();
        player.Seek(5_000_000);
        Assert.Equal(5_000_000, player.TakeSeek());
        Assert.True(player.JumpInProgress);
        // the decoder starts again at the sync sample 1 s before the jump time
        for (long t = 4_000_000; t < 5_000_000 - FrameUs; t += FrameUs)
        {
            player.Deliver(t);
            Assert.Equal(0, player.Queued);
            Assert.True(player.JumpInProgress);
        }

        // the picture on screen at the jump time is the first one out, and the jump is over
        long first = 4_000_000 + (long)Math.Ceiling((1_000_000 - FrameUs) / (double)FrameUs) * FrameUs;
        player.Deliver(first);
        Assert.False(player.JumpInProgress);
        player.Deliver(first + FrameUs);
        Assert.Equal(2, player.Queued);
        VideoFrame? frame = player.TakeVideo(long.MaxValue);
        Assert.NotNull(frame);
        Assert.Equal(first + FrameUs, frame!.TimeUs);
    }

    [Fact]
    public void TheEndOfTheFileEndsAJump()
    {
        MediaPlayerBase.LegacyJumps = false;
        using var player = new SteppedPlayer();
        player.Seek(500_000_000);
        player.TakeSeek();
        player.Deliver(10_000_000);
        Assert.True(player.JumpInProgress);
        player.EndOfFile();
        Assert.False(player.JumpInProgress);
        player.Deliver(0); // a looping movie starts again from its first picture
        Assert.Equal(1, player.Queued);
    }

    [Fact]
    public void OnlyNonReferenceUnitsBeforeTheJumpTimeAreLeftOut()
    {
        MediaPlayerBase.LegacyJumps = false;
        using var player = new SteppedPlayer();
        Assert.False(player.Skips(s_nonReferenceUnit, 0)); // no jump
        player.Seek(5_000_000);
        player.TakeSeek();
        Assert.True(player.Skips(s_nonReferenceUnit, 4_000_000));
        Assert.False(player.Skips(s_referenceUnit, 4_000_000)); // later pictures predict from it
        Assert.False(player.Skips(s_idrUnit, 4_000_000));
        Assert.False(player.Skips(s_nonReferenceUnit, 5_000_000 - FrameUs / 2)); // the picture on screen at the jump time
        player.Deliver(5_000_000);
        Assert.False(player.Skips(s_nonReferenceUnit, 4_000_000)); // the jump is over
    }

    private static object? Invoke(object module, string name, params object[] args) =>
        module.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args);

    private static void SetBacking(object o, string prop, object value) =>
        typeof(Emulator).GetField($"<{prop}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(o, value);

    /// <summary>An AvPlayer playing <paramref name="media"/> (an Emulator shell with only Vfs: app0:movie.mp4).</summary>
    private (AvPlayer Module, uint Handle) PlayerWith(MediaPlayerBase media)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "movie.mp4"), [0]);
        var emu = (Emulator)RuntimeHelpers.GetUninitializedObject(typeof(Emulator));
        SetBacking(emu, "Vfs", new Vfs(_root));
        emu.Vfs.Mount("app0", _root);
        MediaPlayers.Backend = _ => media;
        var module = new AvPlayer { Emu = emu };
        uint init = fixture.Memory.Alloc(0x100, "avplayer init");
        GuestMemory.Fill(init, 0, 0x44);
        GuestMemory.W32(init + 0x38, 2);
        uint handle = (uint)Invoke(module, "sceAvPlayerInit", init)!;
        uint path = fixture.Memory.Alloc(0x40, "avplayer path");
        GuestMemory.WriteCString(path, "app0:movie.mp4");
        Assert.Equal(0, (int)Invoke(module, "sceAvPlayerAddSource", handle, path)!);
        Assert.Equal(0, (int)Invoke(module, "sceAvPlayerStart", handle)!);
        return (module, handle);
    }

    [Fact]
    public void AJumpIsRefusedWhileAnotherIsInProgress()
    {
        MediaPlayerBase.LegacyJumps = false;
        var media = new SteppedPlayer();
        (AvPlayer module, uint handle) = PlayerWith(media);
        Assert.Equal(0, (int)Invoke(module, "sceAvPlayerJumpToTime", handle, 5_000UL)!);
        media.TakeSeek();
        Assert.Equal(ErrInvalidArgument, (int)Invoke(module, "sceAvPlayerJumpToTime", handle, 6_000UL)!);
        media.Deliver(4_000_000); // still on the way
        Assert.Equal(ErrInvalidArgument, (int)Invoke(module, "sceAvPlayerJumpToTime", handle, 6_000UL)!);
        media.Deliver(5_000_000); // there
        Assert.Equal(0, (int)Invoke(module, "sceAvPlayerJumpToTime", handle, 7_000UL)!);
        Assert.Equal(0, (int)Invoke(module, "sceAvPlayerStop", handle)!);
    }

    /// <summary>
    /// CurrentTime answers a jump's time right after it (the sync clock is set to it), not the last picture's: Killzone:
    /// Mercenary holds its title movie on a loop by jumping to 14.5 s whenever CurrentTime passes 15 s, and with the
    /// last picture's 15.05 s still answered it jumped on every frame and the movie never moved.
    /// </summary>
    [Fact]
    public void CurrentTimeIsTheJumpsTimeRightAfterIt()
    {
        MediaPlayerBase.LegacyJumps = false;
        var media = new SteppedPlayer();
        (AvPlayer module, uint handle) = PlayerWith(media);
        Assert.Equal(0UL, (ulong)Invoke(module, "sceAvPlayerCurrentTime", handle)!);
        Assert.Equal(0, (int)Invoke(module, "sceAvPlayerJumpToTime", handle, 14_500UL)!);
        Assert.Equal(14_500UL, (ulong)Invoke(module, "sceAvPlayerCurrentTime", handle)!);
        Assert.Equal(0, (int)Invoke(module, "sceAvPlayerStop", handle)!);
    }

    /// <summary>
    /// The campaign intro's pattern over a time model, without threads or the host clock: a 30 fps movie with a sync
    /// sample every 150 pictures and a non-reference B picture every other one, a decoder that does 0.9 pictures per
    /// picture time (the phone's software decoder with the game's threads on the other cores), and a game that plays its
    /// own audio and jumps the movie to the audio time when the movie's time (sceAvPlayerCurrentTime: the last picture
    /// handed out) is more than 1 s behind. The player's clock is set to the jump time, as the HLE does. Returns the
    /// movie time reached after 60 s of audio, the pictures handed out before the latest accepted jump time, and the
    /// jumps accepted and the pictures decoded.
    /// </summary>
    private static (long ReachedUs, int BeforeJump, int Jumps, int Decoded) PlayAgainstTheGamesAudio()
    {
        const long TickUs = FrameUs; // one game frame
        const long RunUs = 60_000_000;
        const int SyncEvery = 150;
        const double DecodesPerTick = 0.9, SkipCost = 0.05;
        using var player = new SteppedPlayer();
        long clockOffsetUs = 0, clockBaseUs = 0, lastPictureUs = 0, jumpTargetUs = -1;
        int next = 0, beforeJump = 0, jumps = 0, decoded = 0;
        double budget = 0;
        for (long nowUs = 0; nowUs < RunUs; nowUs += TickUs)
        {
            // decoder thread: a jump starts again at the sync sample before its time
            long seek = player.TakeSeek();
            if (seek >= 0)
            {
                next = (int)(seek / FrameUs) / SyncEvery * SyncEvery;
            }

            budget += DecodesPerTick;
            while (budget > 0 && player.Queued < 12)
            {
                long timeUs = next * FrameUs;
                byte[] unit = next % SyncEvery == 0 ? s_idrUnit : next % 2 == 1 ? s_nonReferenceUnit : s_referenceUnit;
                next++;
                if (player.Skips(unit, timeUs))
                {
                    budget -= SkipCost;
                    continue;
                }

                budget -= 1;
                decoded++;
                player.Deliver(timeUs);
            }

            // the game: the picture due on the player's clock, then its audio check
            if (player.TakeVideo(clockOffsetUs + nowUs - clockBaseUs) is { } frame)
            {
                lastPictureUs = frame.TimeUs;
                if (jumpTargetUs >= 0 && frame.TimeUs + FrameUs <= jumpTargetUs)
                {
                    beforeJump++;
                }
            }

            if (nowUs - lastPictureUs > 1_000_000 && !player.JumpInProgress)
            {
                player.Seek(nowUs);
                jumps++;
                jumpTargetUs = nowUs;
                clockOffsetUs = nowUs;
                clockBaseUs = nowUs;
            }
        }

        return (lastPictureUs, beforeJump, jumps, decoded);
    }

    [Fact]
    public void AMovieKeptInStepWithTheGamesAudioKeepsGoing()
    {
        MediaPlayerBase.LegacyJumps = true;
        (long legacyReached, int legacyBeforeJump, int legacyJumps, int legacyDecoded) = PlayAgainstTheGamesAudio();
        MediaPlayerBase.LegacyJumps = false;
        (long reached, int beforeJump, int jumps, int decoded) = PlayAgainstTheGamesAudio();
        string numbers = $"before the fix: movie at {legacyReached / 1000} ms, {legacyJumps} jumps, {legacyBeforeJump} pictures from before a jump's time, {legacyDecoded} decoded; "
            + $"now: {reached / 1000} ms, {jumps} jumps, {beforeJump} pictures from before a jump's time, {decoded} decoded";
        // before: the pictures from the sync sample to the jump time reached the game, which saw the movie still behind
        // and jumped again at once
        Assert.True(legacyBeforeJump > 0, numbers);
        // now: nothing from before a jump's time is shown, a jump is not repeated while it is on its way, and the movie
        // keeps up with the audio
        Assert.True(beforeJump == 0, numbers);
        Assert.True(jumps * 2 <= legacyJumps, numbers);
        Assert.True(reached >= 50_000_000 && reached >= legacyReached, numbers);
    }

    [Fact]
    public void AStatesMovieIsFoundOnAnotherDevice()
    {
        string movie = Path.Combine(_root, "fs", "ux0", "app", "PCSC00045", "game_data", "movie.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(movie)!);
        File.WriteAllBytes(movie, [0]);
        string fsRoot = Path.Combine(_root, "fs");
        // saved on the phone, loaded on the desktop
        Assert.Equal(movie, AvPlayer.HostPathOnThisDevice("/storage/emulated/0/Android/data/io.github.vitasharp/files/fs/ux0/app/PCSC00045/game_data/movie.mp4", fsRoot));
        // saved on this device: as it is
        Assert.Equal(movie, AvPlayer.HostPathOnThisDevice(movie, fsRoot));
        // a movie the game served through its callbacks (a temp file) and one that is not installed here stay as they are
        Assert.Equal("/tmp/vitasharp-avplayer-1.mp4", AvPlayer.HostPathOnThisDevice("/tmp/vitasharp-avplayer-1.mp4", fsRoot));
        Assert.Equal("/x/fs/ux0/app/PCSB00000/m.mp4", AvPlayer.HostPathOnThisDevice("/x/fs/ux0/app/PCSB00000/m.mp4", fsRoot));
    }
}
