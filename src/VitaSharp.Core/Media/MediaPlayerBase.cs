namespace VitaSharp.Core.Media;

/// <summary>
/// The consumer side of SceAvPlayer's media player: a decoder thread (in the subclass) fills small bounded queues of
/// NV21 video frames and 16-bit audio chunks, so the guest's GetVideoData/GetAudioData calls never wait for the
/// decoder. Subclasses: <see cref="MediaPlayer"/> (FFmpeg demuxing and audio, H.264 through the pluggable
/// <see cref="IVideoDecoder"/>) and, on Android, a MediaExtractor/MediaCodec player (src/VitaSharp.Android/Media),
/// installed with <see cref="MediaPlayers.Backend"/>.
/// </summary>
public abstract class MediaPlayerBase : IDisposable
{
    protected const int MaxVideoFrames = 12;
    protected const int MaxAudioChunks = 48;

    protected readonly Queue<VideoFrame> VideoQueue = new();
    protected readonly Queue<AudioChunk> AudioQueue = new();
    protected readonly object SyncRoot = new();
    protected volatile bool StopRequested;
    /// <summary>Pending seek target (µs), -1 = none; guarded by <see cref="SyncRoot"/>.</summary>
    protected long SeekRequestUs = -1;
    /// <summary>The whole file was decoded (guarded by <see cref="SyncRoot"/>).</summary>
    protected bool DemuxDone;

    public int Width { get; protected set; }
    public int Height { get; protected set; }
    public double FrameRate { get; protected set; }
    public int Channels { get; protected set; }
    public int SampleRate { get; protected set; }
    public long DurationUs { get; protected set; }
    public abstract bool HasVideo { get; }
    public abstract bool HasAudio { get; }
    public bool Loop { get; set; }

    // ------------------------------------------------------------------ consumer side

    /// <summary>Next video frame with a timestamp at or before <paramref name="clockUs"/> (dropping late frames).</summary>
    public VideoFrame? TakeVideo(long clockUs)
    {
        lock (SyncRoot)
        {
            VideoFrame? videoFrame = null;
            while (VideoQueue.Count > 0 && VideoQueue.Peek().TimeUs <= clockUs)
            {
                if (videoFrame != null)
                {
                    ReturnBuffer(FreeNv12, videoFrame.Nv12); // dropped late frame
                }

                videoFrame = VideoQueue.Dequeue();
            }
            if (videoFrame != null)
            {
                Monitor.PulseAll(SyncRoot);
            }

            return videoFrame;
        }
    }

    // decoded planes/samples handed back by the consumer once copied out (no garbage per video frame or audio chunk);
    // guarded by SyncRoot, at most a few of each are kept
    protected readonly Stack<byte[]> FreeNv12 = new();
    protected readonly Stack<short[]> FreeSamples = new();

    protected T[] RentBuffer<T>(Stack<T[]> free, int length)
    {
        lock (SyncRoot)
        {
            while (free.Count > 0)
            {
                if (free.Pop() is var a && a.Length == length)
                {
                    return a; // buffers of another size are dropped
                }
            }
        }

        return new T[length];
    }

    protected void ReturnBuffer<T>(Stack<T[]> free, T[] buffer)
    {
        lock (SyncRoot)
        {
            if (free.Count < 4)
            {
                free.Push(buffer);
            }
        }
    }

    /// <summary>The frame's planes were copied out and are not read again: reuse them for a later frame.</summary>
    public void Recycle(VideoFrame videoFrame) => ReturnBuffer(FreeNv12, videoFrame.Nv12);

    /// <summary>The chunk's samples were copied out and are not read again.</summary>
    public void Recycle(AudioChunk audioChunk) => ReturnBuffer(FreeSamples, audioChunk.Samples);

    public AudioChunk? TakeAudio()
    {
        lock (SyncRoot)
        {
            if (AudioQueue.Count == 0)
            {
                return null;
            }

            AudioChunk audioChunk = AudioQueue.Dequeue();
            Monitor.PulseAll(SyncRoot);
            return audioChunk;
        }
    }

    /// <summary>Timestamp of the next queued video frame, or -1.</summary>
    public long PeekVideoTime()
    {
        lock (SyncRoot)
        {
            return VideoQueue.Count > 0 ? VideoQueue.Peek().TimeUs : -1;
        }
    }

    /// <summary>True once the file has been fully decoded and every queued frame consumed.</summary>
    public bool Finished
    {
        get
        {
            lock (SyncRoot)
            {
                return DemuxDone && VideoQueue.Count == 0 && AudioQueue.Count == 0;
            }
        }
    }

    public void Seek(long timeUs)
    {
        lock (SyncRoot)
        {
            SeekRequestUs = Math.Max(0, timeUs);
            JumpTargetUs = SeekRequestUs;
            VideoQueue.Clear();
            AudioQueue.Clear();
            DemuxDone = false;
            Monitor.PulseAll(SyncRoot);
        }
    }

    /// <summary>
    /// The time a jump goes to while its first picture is not out yet (-1 = none; guarded by <see cref="SyncRoot"/>).
    /// The firmware's player (libsceavplayer, FW 3.74) demuxes again from the sync sample before the jump time and
    /// discards what comes before it ("DiscardAU ... New PTS start"), and refuses another sceAvPlayerJumpToTime while
    /// it is still in its jump state. A player that handed those pictures out made a game that keeps its movie in step
    /// with its own audio see the movie further behind after each jump, and jump again: Killzone: Mercenary's campaign
    /// intro on the phone's software decoder went from 24 to 0.7 fps, decoding the same seconds again and again
    /// (6022 units for 70 s of a 30 fps movie, 14/11/2026).
    /// </summary>
    protected long JumpTargetUs = -1;

    /// <summary>A jump (<see cref="Seek"/>) has not reached its time yet: the firmware's jump state.</summary>
    public bool JumpInProgress
    {
        get
        {
            lock (SyncRoot)
            {
                return JumpTargetUs >= 0;
            }
        }
    }

    /// <summary>The longest a picture before a jump's time may start and still be the jump's first picture: one frame
    /// (the picture on screen at the jump time).</summary>
    protected long FrameDurationUs => FrameRate > 0 ? (long)(1_000_000 / FrameRate) : 33_367;

    /// <summary>
    /// True when the decoder thread may leave the access unit out while a jump decodes up to its time: a picture before
    /// the jump time that no other picture predicts from (every slice with nal_ref_idc 0, a B frame of a Vita movie)
    /// is never shown and never needed, so the way to the jump time costs only the reference pictures.
    /// </summary>
    protected bool SkipBeforeJump(ReadOnlySpan<byte> accessUnit, long timeUs)
    {
        long target;
        lock (SyncRoot)
        {
            target = JumpTargetUs;
        }

        return target >= 0 && timeUs + FrameDurationUs <= target && H264ReorderLimit.IsNonReference(accessUnit);
    }

    /// <summary>The end of the file came before the jump time: the jump is over (nothing is left to discard).</summary>
    protected void EndJump()
    {
        lock (SyncRoot)
        {
            JumpTargetUs = -1;
        }
    }

    // ------------------------------------------------------------------ producer side (decoder thread)

    /// <summary>Waits while both queues are full enough (or the file is done) and no seek or stop is pending.</summary>
    protected void WaitForRoom()
    {
        lock (SyncRoot)
        {
            while (!StopRequested && SeekRequestUs < 0 && (DemuxDone || (HasVideo ? VideoQueue.Count >= MaxVideoFrames : AudioQueue.Count >= MaxAudioChunks)
                                                   || AudioQueue.Count >= MaxAudioChunks))
            {
                Monitor.Wait(SyncRoot, 50);
            }
        }
    }

    /// <summary>Planar YUV 4:2:0 from a decoder into the NV21 frame layout the guest receives.</summary>
    protected void PushPicture(DecodedPicture decodedPicture, long timeUs)
    {
        int w = decodedPicture.Width & ~1, h = decodedPicture.Height & ~1, cw = (decodedPicture.Width + 1) / 2;
        byte[] nv21 = RentBuffer(FreeNv12, w * h * 3 / 2);
        for (int y = 0; y < h; y++)
        {
            decodedPicture.Y.AsSpan(y * decodedPicture.Width, w).CopyTo(nv21.AsSpan(y * w));
        }

        int uvBase = w * h;
        for (int y = 0; y < h / 2; y++)
        {
            int s = y * cw, d = uvBase + y * w;
            for (int x = 0; x < w / 2; x++)
            {
                nv21[d + 2 * x] = decodedPicture.V[s + x];
                nv21[d + 2 * x + 1] = decodedPicture.U[s + x];
            }
        }
        Enqueue(new VideoFrame(nv21, w, h, timeUs));
    }

    protected void Enqueue(VideoFrame videoFrame)
    {
        lock (SyncRoot)
        {
            while (!StopRequested && SeekRequestUs < 0 && VideoQueue.Count >= MaxVideoFrames)
            {
                Monitor.Wait(SyncRoot, 50);
            }

            if (SeekRequestUs >= 0)
            {
                return;
            }

            if (JumpTargetUs >= 0)
            {
                if (videoFrame.TimeUs + FrameDurationUs <= JumpTargetUs)
                {
                    // before the jump time: decoded only as a reference, never shown (the firmware's DiscardAU)
                    ReturnBufferLocked(FreeNv12, videoFrame.Nv12);
                    return;
                }

                JumpTargetUs = -1;
            }

            VideoQueue.Enqueue(videoFrame);
        }
    }

    private static void ReturnBufferLocked<T>(Stack<T[]> free, T[] buffer)
    {
        if (free.Count < 4)
        {
            free.Push(buffer);
        }
    }

    protected void Enqueue(AudioChunk chunk)
    {
        lock (SyncRoot)
        {
            while (!StopRequested && SeekRequestUs < 0 && AudioQueue.Count >= MaxAudioChunks)
            {
                Monitor.Wait(SyncRoot, 50);
            }

            if (SeekRequestUs >= 0)
            {
                return;
            }

            long chunkUs = chunk.SampleRate > 0 && chunk.Channels > 0 ? chunk.Samples.Length / chunk.Channels * 1_000_000L / chunk.SampleRate : 0;
            if (JumpTargetUs >= 0 && chunk.TimeUs + chunkUs <= JumpTargetUs)
            {
                // sound before the jump time is discarded with the pictures
                ReturnBufferLocked(FreeSamples, chunk.Samples);
                return;
            }

            if (JumpTargetUs >= 0 && !HasVideo)
            {
                JumpTargetUs = -1;
            }

            AudioQueue.Enqueue(chunk);
        }
    }

    public abstract void Dispose();
}

/// <summary>Opens SceAvPlayer media files. Front-ends without FFmpeg (Android) install their own player.</summary>
public static class MediaPlayers
{
    /// <summary>Opens a file, or null when this backend cannot play it (then FFmpeg is tried, when it is available).</summary>
    public static Func<string, MediaPlayerBase?>? Backend { get; set; }

    public static MediaPlayerBase? Open(string path) => Backend?.Invoke(path) ?? MediaPlayer.Open(path);
}
