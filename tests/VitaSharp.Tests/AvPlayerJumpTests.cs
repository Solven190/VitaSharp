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
    private readonly bool _newestPacing = MediaPlayerBase.NewestPicturePacing;

    public void Dispose()
    {
        MediaPlayerBase.LegacyJumps = _legacy;
        MediaPlayerBase.NewestPicturePacing = _newestPacing;
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
        SetBacking(emu, "Memory", fixture.Memory);
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
        // paused: the playback clock stands still, so the answers do not depend on the host clock
        Assert.Equal(0, (int)Invoke(module, "sceAvPlayerPause", handle)!);
        SetPlaybackClock(module, handle, 0);
        Assert.Equal(0UL, (ulong)Invoke(module, "sceAvPlayerCurrentTime", handle)!);
        Assert.Equal(0, (int)Invoke(module, "sceAvPlayerJumpToTime", handle, 14_500UL)!);
        Assert.Equal(14_500UL, (ulong)Invoke(module, "sceAvPlayerCurrentTime", handle)!);
        Assert.Equal(0, (int)Invoke(module, "sceAvPlayerStop", handle)!);
    }

    /// <summary>The playback clock of a paused player set to <paramref name="elapsedUs"/> since its start or jump.</summary>
    private static void SetPlaybackClock(AvPlayer module, uint handle, long elapsedUs)
    {
        object player = typeof(AvPlayer).GetMethod("Get", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(module, [handle])!;
        object clock = player.GetType().GetField("Clock")!.GetValue(player)!;
        long ticks = elapsedUs * System.Diagnostics.Stopwatch.Frequency / 1_000_000;
        clock.GetType().GetField("_accumulated", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(clock, ticks);
    }

    /// <summary>The playback clock of a playing player stopped at <paramref name="elapsedUs"/> since its start or jump,
    /// so a test steps it instead of the host clock.</summary>
    private static void FreezePlaybackClock(AvPlayer module, uint handle, long elapsedUs)
    {
        object player = typeof(AvPlayer).GetMethod("Get", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(module, [handle])!;
        object clock = player.GetType().GetField("Clock")!.GetValue(player)!;
        clock.GetType().GetMethod("Stop")!.Invoke(clock, null);
        long ticks = elapsedUs * System.Diagnostics.Stopwatch.Frequency / 1_000_000;
        clock.GetType().GetField("_accumulated", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(clock, ticks);
    }

    /// <summary>The timestamp (ms) of the picture GetVideoData answers now, or -1 when it answers none.</summary>
    private long PictureNow(AvPlayer module, uint handle, uint frameInfo)
    {
        if ((int)Invoke(module, "sceAvPlayerGetVideoData", handle, frameInfo)! != 1)
        {
            return -1;
        }

        return (long)GuestMemory.Read<ulong>(frameInfo + 8);
    }

    /// <summary>
    /// libsceavplayer hands out the next picture in order, one per GetVideoData call, also when several are late (it
    /// never skips to the newest): a decoder that delivers in bursts (the phone's MediaCodec) still shows every picture.
    /// </summary>
    [Fact]
    public void LatePicturesAreHandedOutInOrderOnePerCall()
    {
        MediaPlayerBase.LegacyJumps = false;
        MediaPlayerBase.NewestPicturePacing = false;
        var media = new SteppedPlayer();
        (AvPlayer module, uint handle) = PlayerWith(media);
        uint frameInfo = fixture.Memory.Alloc(0x40, "avplayer frame info");
        FreezePlaybackClock(module, handle, 400_000);
        for (int i = 0; i < 10; i++)
        {
            media.Deliver(i * FrameUs); // a burst: all ten are already due on the clock
        }

        for (int i = 0; i < 10; i++)
        {
            FreezePlaybackClock(module, handle, 400_000 + i * FrameUs);
            Assert.Equal(i * FrameUs / 1000, PictureNow(module, handle, frameInfo));
            Assert.Equal((ulong)(i * FrameUs / 1000), (ulong)Invoke(module, "sceAvPlayerCurrentTime", handle)!);
        }

        Assert.Equal(0, (int)Invoke(module, "sceAvPlayerStop", handle)!);
    }

    /// <summary>
    /// A picture is not handed out before the player's clock reaches it: a call sooner answers the picture already out
    /// (a game calling every 16 ms plays a 30 fps movie at its speed).
    /// </summary>
    [Fact]
    public void APictureIsNotHandedOutBeforeItsTime()
    {
        MediaPlayerBase.LegacyJumps = false;
        MediaPlayerBase.NewestPicturePacing = false;
        var media = new SteppedPlayer();
        (AvPlayer module, uint handle) = PlayerWith(media);
        uint frameInfo = fixture.Memory.Alloc(0x40, "avplayer frame info");
        media.Deliver(0);
        media.Deliver(FrameUs);
        FreezePlaybackClock(module, handle, 0);
        Assert.Equal(0, PictureNow(module, handle, frameInfo)); // the first picture is due at once
        FreezePlaybackClock(module, handle, 16_000);
        Assert.Equal(0, PictureNow(module, handle, frameInfo)); // the next is not due yet: still the first
        FreezePlaybackClock(module, handle, FrameUs);
        Assert.Equal(FrameUs / 1000, PictureNow(module, handle, frameInfo));
        Assert.Equal(0, (int)Invoke(module, "sceAvPlayerStop", handle)!);
    }

    /// <summary>
    /// A game that calls GetVideoData in a loop until CurrentTime is close to its own time (Killzone: Mercenary after a
    /// slow frame) catches up within the frame: each call hands out the next due picture, none is skipped.
    /// </summary>
    [Fact]
    public void AGameLoopingUntilCaughtUpCatchesUpAtOnce()
    {
        MediaPlayerBase.LegacyJumps = false;
        MediaPlayerBase.NewestPicturePacing = false;
        var media = new SteppedPlayer();
        (AvPlayer module, uint handle) = PlayerWith(media);
        uint frameInfo = fixture.Memory.Alloc(0x40, "avplayer frame info");
        for (int i = 0; i < 12; i++)
        {
            media.Deliver(i * FrameUs);
        }

        FreezePlaybackClock(module, handle, 0);
        Assert.Equal(0, PictureNow(module, handle, frameInfo));
        FreezePlaybackClock(module, handle, 500_000); // a slow frame: the game is 500 ms ahead
        int calls = 0;
        long shown = 0;
        while (500 - (long)(ulong)Invoke(module, "sceAvPlayerCurrentTime", handle)! > 200 && calls < 100)
        {
            long picture = PictureNow(module, handle, frameInfo);
            Assert.True(picture == shown + FrameUs / 1000 || picture == shown + FrameUs / 1000 + 1, $"picture {picture} after {shown}");
            shown = picture;
            calls++;
        }

        Assert.True(calls < 12, $"{calls} calls to catch up");
        Assert.Equal(0, (int)Invoke(module, "sceAvPlayerStop", handle)!);
    }

    /// <summary>
    /// Killzone: Mercenary's campaign intro (no sound; the game plays its own): the game reads CurrentTime every frame
    /// and asks for a picture only when it is more than 200 ms behind its own time; the decoder hands pictures out in
    /// bursts. With the newest due picture handed out (the code before), CurrentTime jumped to the player's clock and
    /// the game asked again only ~7 frames later: 3-5 different pictures a second at 30 fps (phone and desktop re-run of
    /// the phone capture, 14/11/2026: 2.7/s handed out, 27/s dropped as late, 3 GetVideoData/s). With the firmware's
    /// one picture per call it asks on every frame and every picture is shown. Returns the different pictures shown per
    /// second over 10 s of 30 fps frames.
    /// </summary>
    private double DistinctPicturesPerSecond(bool newestPacing)
    {
        MediaPlayerBase.LegacyJumps = false;
        MediaPlayerBase.NewestPicturePacing = newestPacing;
        var media = new SteppedPlayer();
        (AvPlayer module, uint handle) = PlayerWith(media);
        uint frameInfo = fixture.Memory.Alloc(0x40, "avplayer frame info");
        const int Frames = 300;
        const int BurstEvery = 8; // the decoder: 8 pictures at once every 8 frames
        int next = 0, distinct = 0;
        long shown = -1;
        for (int f = 0; f < Frames; f++)
        {
            long nowUs = f * FrameUs;
            if (f % BurstEvery == 0)
            {
                while (next * FrameUs <= nowUs + BurstEvery * FrameUs && media.Queued < 12)
                {
                    media.Deliver(next * FrameUs);
                    next++;
                }
            }

            FreezePlaybackClock(module, handle, nowUs);
            long currentMs = (long)(ulong)Invoke(module, "sceAvPlayerCurrentTime", handle)!;
            if (shown < 0 || nowUs / 1000 - currentMs > 200)
            {
                long picture = PictureNow(module, handle, frameInfo);
                if (picture >= 0 && picture != shown)
                {
                    distinct++;
                    shown = picture;
                }
            }
        }

        Assert.Equal(0, (int)Invoke(module, "sceAvPlayerStop", handle)!);
        return distinct / (Frames * FrameUs / 1e6);
    }

    /// <summary>The display index of the picture a decoder that gives pictures out in decode order (a reorder limit of 0
    /// on a stream with B frames: I0 P3 B1 B2 P6 B4 B5 ...) gives out <paramref name="n"/>-th.</summary>
    private static int DecodeOrderIndex(int n)
    {
        if (n == 0)
        {
            return 0;
        }

        int group = (n - 1) / 3, inGroup = (n - 1) % 3;
        return inGroup == 0 ? group * 3 + 3 : group * 3 + inGroup;
    }

    [Fact]
    public void PicturesGivenOutOfOrderAreHandedOutInDisplayOrder()
    {
        MediaPlayerBase.LegacyJumps = false;
        using var player = new SteppedPlayer();
        for (int n = 0; n < 10; n++)
        {
            player.Deliver(DecodeOrderIndex(n) * FrameUs);
        }

        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(i * FrameUs, player.TakeNextVideo(long.MaxValue)!.TimeUs);
        }
    }

    [Fact]
    public void APictureOlderThanTheOneHandedOutIsDropped()
    {
        MediaPlayerBase.LegacyJumps = false;
        using var player = new SteppedPlayer();
        player.Deliver(0);
        player.Deliver(3 * FrameUs);
        Assert.Equal(0, player.TakeNextVideo(long.MaxValue)!.TimeUs);
        Assert.Equal(3 * FrameUs, player.TakeNextVideo(long.MaxValue)!.TimeUs);
        player.Deliver(FrameUs); // came out after the one already shown: too late
        player.Deliver(4 * FrameUs);
        Assert.Equal(1, player.Queued);
        Assert.Equal(4 * FrameUs, player.TakeNextVideo(long.MaxValue)!.TimeUs);
    }

    [Fact]
    public void ALoopingMoviesFirstPictureComesAfterItsLast()
    {
        MediaPlayerBase.LegacyJumps = false;
        using var player = new SteppedPlayer();
        player.Deliver(150_000_000);
        player.Deliver(150_000_000 + FrameUs);
        player.Deliver(0); // the movie starts over
        Assert.Equal(150_000_000, player.TakeNextVideo(long.MaxValue)!.TimeUs);
        Assert.Equal(150_000_000 + FrameUs, player.TakeNextVideo(long.MaxValue)!.TimeUs);
        Assert.Equal(0, player.TakeNextVideo(long.MaxValue)!.TimeUs);
    }

    /// <summary>
    /// The campaign intro on the phone: the decoder gave pictures out in decode order (the B pictures after the P picture
    /// that follows them) and the game, asking when CurrentTime is more than 200 ms behind, showed ~18 different pictures
    /// a second, going forward and back (lossless frames of a phone capture, 14/11/2026). In display order it sees every
    /// picture, always forward.
    /// </summary>
    [Fact]
    public void AGameSeesEveryPictureForwardWhenTheDecoderGivesThemInDecodeOrder()
    {
        MediaPlayerBase.LegacyJumps = false;
        MediaPlayerBase.NewestPicturePacing = false;
        var media = new SteppedPlayer();
        (AvPlayer module, uint handle) = PlayerWith(media);
        uint frameInfo = fixture.Memory.Alloc(0x40, "avplayer frame info");
        const int Frames = 300;
        int next = 0, distinct = 0, backwards = 0;
        long shown = -1;
        for (int f = 0; f < Frames; f++)
        {
            long nowUs = f * FrameUs;
            while (DecodeOrderIndex(next) * FrameUs <= nowUs + 8 * FrameUs && media.Queued < 12)
            {
                media.Deliver(DecodeOrderIndex(next) * FrameUs);
                next++;
            }

            FreezePlaybackClock(module, handle, nowUs);
            long currentMs = (long)(ulong)Invoke(module, "sceAvPlayerCurrentTime", handle)!;
            while (shown < 0 || nowUs / 1000 - currentMs > 200)
            {
                long picture = PictureNow(module, handle, frameInfo);
                if (picture < 0 || picture == shown)
                {
                    break;
                }

                backwards += picture < shown ? 1 : 0;
                distinct++;
                shown = picture;
                currentMs = (long)(ulong)Invoke(module, "sceAvPlayerCurrentTime", handle)!;
            }
        }

        Assert.Equal(0, (int)Invoke(module, "sceAvPlayerStop", handle)!);
        double perSecond = distinct / (Frames * FrameUs / 1e6);
        Assert.True(backwards == 0 && perSecond >= 25, $"{perSecond:F1} pictures a second, {backwards} going back");
    }

    [Fact]
    public void AGameThatAsksWhenBehindSeesEveryPicture()
    {
        double before = DistinctPicturesPerSecond(newestPacing: true);
        double now = DistinctPicturesPerSecond(newestPacing: false);
        string numbers = $"different pictures a second: before the fix {before:F1}, now {now:F1}";
        Assert.True(before < 10, numbers);
        Assert.True(now >= 25, numbers);
    }

    /// <summary>
    /// sceAvPlayerGetVideoData gives no picture between a jump and its first picture (libsceavplayer answers 0 outside
    /// its play states): Killzone: Mercenary reads the picture's timestamp, and the picture from before the jump (past
    /// the loop's end) made it jump again on the next frame, so its scanner screen stood still.
    /// </summary>
    [Fact]
    public void NoPictureIsGivenBetweenAJumpAndItsFirstPicture()
    {
        MediaPlayerBase.LegacyJumps = false;
        var media = new SteppedPlayer();
        (AvPlayer module, uint handle) = PlayerWith(media);
        uint frameInfo = fixture.Memory.Alloc(0x40, "avplayer frame info");
        media.Deliver(0);
        Assert.Equal(1, (int)Invoke(module, "sceAvPlayerGetVideoData", handle, frameInfo)!);
        Assert.Equal(0, (int)Invoke(module, "sceAvPlayerJumpToTime", handle, 5_000UL)!);
        media.TakeSeek();
        Assert.Equal(0, (int)Invoke(module, "sceAvPlayerGetVideoData", handle, frameInfo)!);
        media.Deliver(4_000_000); // on the way: discarded
        Assert.Equal(0, (int)Invoke(module, "sceAvPlayerGetVideoData", handle, frameInfo)!);
        media.Deliver(5_000_000); // the jump's first picture
        Assert.Equal(1, (int)Invoke(module, "sceAvPlayerGetVideoData", handle, frameInfo)!);
        Assert.Equal(5_000UL, GuestMemory.Read<ulong>(frameInfo + 8)); // its timestamp (ms)
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
