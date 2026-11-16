using System.Reflection;
using System.Text;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Tests;

/// <summary>
/// Save states of HLE modules taken while the game uses them (they were refused while in use): each test drives the
/// module through the calls a game makes, checks the save is no longer refused, saves the module, loads it into a
/// fresh module and runs the same calls on both, which must give the same results and outputs (the sound, the text,
/// the contexts go on after the load). SceAtrac's are in <see cref="AtracHleTests"/>.
/// </summary>
[Collection("guest")]
public sealed class SaveStateModulesInUseTests(GuestMemoryFixture fixture)
{
    private static object? Invoke<T>(T module, string name, params object[] args) =>
        typeof(T).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args);

    private static int Call<T>(T module, string name, params object[] args) => (int)Invoke(module, name, args)!;

    private static Ptr<T> P<T>(uint a) where T : unmanaged => new(a);

    private static byte[] Save(ISnapshotState state)
    {
        var w = new SnapshotWriter();
        state.SaveState(w);
        return w.Data.ToArray();
    }

    /// <summary>The module's state saved (not refused) and loaded into <paramref name="into"/>, whose state saved again
    /// is the same bytes.</summary>
    private static T SaveAndLoad<T>(T from, T into) where T : HleModule, ISnapshotState
    {
        Assert.Null(((ISnapshotState)from).CheckSnapshot());
        byte[] data = Save(from);
        var r = new SnapshotReader(data);
        ((ISnapshotState)into).LoadState(r);
        Assert.True(r.AtEnd, $"{typeof(T).Name}: {r.Remaining} bytes of its state were not read back");
        Assert.Equal(data, Save(into));
        return into;
    }

    private uint Ascii(string s)
    {
        uint p = fixture.Memory.Alloc(0x100, "cfg");
        byte[] b = Encoding.ASCII.GetBytes(s + "\0");
        b.CopyTo(GuestMemory.Span(p, b.Length));
        return p;
    }

    // ------------------------------------------------------------------ SceSas

    /// <summary>A looping VAG sample: blocks of varied nibbles, loop start on the second block, loop end on the last.</summary>
    private uint Vag(int blocks)
    {
        uint p = fixture.Memory.Alloc((uint)(blocks * 16), "vag");
        var rng = new Random(7);
        for (int k = 0; k < blocks; k++)
        {
            byte[] b = new byte[16];
            b[0] = (byte)((k % 4) << 4 | 2);
            b[1] = (byte)(k == 1 ? 4 : k == blocks - 1 ? 3 : 0);
            for (int i = 2; i < 16; i++)
            {
                b[i] = (byte)rng.Next(256);
            }

            b.CopyTo(GuestMemory.Span(p + (uint)(k * 16), 16));
        }

        return p;
    }

    private short[] SasCore(Sas sas, int grain)
    {
        uint o = fixture.Memory.Alloc((uint)(grain * 4), "sasout");
        Assert.Equal(0, Call(sas, "sceSasCore", o));
        return GuestMemory.Span<short>(o, grain * 2).ToArray();
    }

    /// <summary>
    /// FFX's pattern: sceSasInit, VAG voices with pitch, volume and a simple ADSR keyed on, the delay effect on the wet
    /// bus, sceSasCore every grain; a key off mid-way. Saved between grains and loaded, the mixer gives the same grains
    /// as without the load (voice source and resampler positions, envelope phase and height, the delay line), for the
    /// public context and for a context made by sceSasInitInternal.
    /// </summary>
    [Fact]
    public void SasMixesTheSameGrainsAfterALoad()
    {
        const int Grain = 256;
        var sas = new Sas();
        Assert.Equal(0, Call(sas, "sceSasInit", Ascii("numGrains=256 numVoices=8 numReverbs=1"), fixture.Memory.Alloc(0x10000, "saswork"), 0x10000u));
        uint vag = Vag(12);
        for (int v = 0; v < 3; v++)
        {
            Assert.Equal(0, Call(sas, "sceSasSetVoice", v, vag, 12u * 16, 1u));
            Assert.Equal(0, Call(sas, "sceSasSetPitch", v, 0x0C00 + v * 0x300));
            Assert.Equal(0, Call(sas, "sceSasSetVolume", v, 0x800, 0x600 + v * 0x100, 0x400, 0x400));
            Assert.Equal(0, Call(sas, "sceSasSetSimpleADSR", v, 0x0F7Fu - (uint)v, 0x1FC6u));
            Assert.Equal(0, Call(sas, "sceSasSetKeyOn", v));
        }

        Assert.Equal(0, Call(sas, "sceSasSetEffectType", 7));
        Assert.Equal(0, Call(sas, "sceSasSetEffectParam", 3u, 64u));
        Assert.Equal(0, Call(sas, "sceSasSetEffect", 1, 1));

        // a second mixer made by a context variant, playing one voice
        uint needed = fixture.Memory.Alloc(0x10, "sasneed"), ctxOut = needed + 4;
        uint config = Ascii("numGrains=256 numVoices=2");
        Assert.Equal(0, Call(sas, "sceSasGetNeededMemorySizeInternal", config, P<uint>(needed)));
        uint size = GuestMemory.R32(needed);
        Assert.Equal(0, Call(sas, "sceSasInitInternal", config, fixture.Memory.Alloc(size + 0x10, "sasctx"), size, P<uint>(ctxOut)));
        uint ctx = GuestMemory.R32(ctxOut);
        Assert.Equal(0, Call(sas, "sceSasSetVoiceInternal", ctx, 0, vag, 12u * 16, 1u));
        Assert.Equal(0, Call(sas, "sceSasSetKeyOnInternal", ctx, 0));

        for (int g = 0; g < 5; g++)
        {
            SasCore(sas, Grain);
        }

        Sas loaded = SaveAndLoad(sas, new Sas());
        uint outA = fixture.Memory.Alloc(Grain * 4, "sasA"), outB = fixture.Memory.Alloc(Grain * 4, "sasB");
        for (int g = 0; g < 12; g++)
        {
            if (g == 4)
            {
                Assert.Equal(0, Call(sas, "sceSasSetKeyOff", 1));
                Assert.Equal(0, Call(loaded, "sceSasSetKeyOff", 1));
            }

            Assert.Equal(SasCore(sas, Grain), SasCore(loaded, Grain));
            Assert.Equal(Call(sas, "sceSasGetEnvelope", 1), Call(loaded, "sceSasGetEnvelope", 1));
            Assert.Equal(0, Call(sas, "sceSasCoreInternal", ctx, outA, 0u, 0u));
            Assert.Equal(0, Call(loaded, "sceSasCoreInternal", ctx, outB, 0u, 0u));
            Assert.Equal(GuestMemory.Span<short>(outA, Grain * 2).ToArray(), GuestMemory.Span<short>(outB, Grain * 2).ToArray());
        }

        Assert.NotEqual(0, SasCore(loaded, Grain).Max(s => Math.Abs((int)s)));
    }

    // ------------------------------------------------------------------ SceLibLocation

    /// <summary>The handle a game opened (methods, the confirmation it asked for) is there after a load: the same
    /// method, the same dialog status and result, and the next handle number continues.</summary>
    [Fact]
    public void LocationHandlesSurviveALoad()
    {
        var location = new Location();
        uint p = fixture.Memory.Alloc(0x40, "loc");
        Assert.Equal(0, Call(location, "sceLocationInit"));
        Assert.Equal(0, Call(location, "sceLocationOpen", P<uint>(p), 1u, 2u));
        uint handle = GuestMemory.R32(p);
        Assert.Equal(0, Call(location, "sceLocationConfirm", handle));

        Location loaded = SaveAndLoad(location, new Location());
        foreach (Location m in new[] { location, loaded })
        {
            Assert.Equal(0, Call(m, "sceLocationGetMethod", handle, P<uint>(p + 4), P<uint>(p + 8)));
            Assert.Equal((1u, 2u), (GuestMemory.R32(p + 4), GuestMemory.R32(p + 8)));
            Assert.Equal(0, Call(m, "sceLocationConfirmGetStatus", handle, P<uint>(p + 12)));
            Assert.Equal(Location.DialogFinished, GuestMemory.R32(p + 12));
            Assert.Equal(0, Call(m, "sceLocationConfirmGetResult", handle, P<uint>(p + 16)));
            Assert.Equal(Location.ResultDisable, GuestMemory.R32(p + 16));
            Assert.Equal(Location.ERR_DISABLE_APPLICATION, Call(m, "sceLocationGetLocation", handle, p + 0x10));
        }

        Assert.Equal(0, Call(loaded, "sceLocationOpen", P<uint>(p + 20), 0u, 0u));
        Assert.Equal(handle + 1, GuestMemory.R32(p + 20));
        Assert.Equal(0, Call(loaded, "sceLocationClose", handle));
    }

    // ------------------------------------------------------------------ SceIme

    private const uint ImeHandler = 0x1234_5679;

    private (uint Param, uint Buffer) ImeParam(string initial)
    {
        uint param = fixture.Memory.Alloc(0x1000, "ime param");
        GuestMemory.Fill(param, 0, 0x40);
        uint init = param + 0x100, buffer = param + 0x400;
        byte[] text = Encoding.Unicode.GetBytes(initial + "\0");
        text.CopyTo(GuestMemory.Span(init, text.Length));
        GuestMemory.W32(param + 0x1C, fixture.Memory.Alloc(20 * 1024, "ime work"));
        GuestMemory.W32(param + 0x20, 0xA5A5);
        GuestMemory.W32(param + 0x24, ImeHandler);
        GuestMemory.W32(param + 0x2C, init);
        GuestMemory.W32(param + 0x30, 16);
        GuestMemory.W32(param + 0x34, buffer);
        return (param, buffer);
    }

    /// <summary>Records the events the handler receives (id, caret, text).</summary>
    private static List<(uint Id, uint Caret, string Text)> Handler(Ime ime)
    {
        var events = new List<(uint, uint, string)>();
        ime.GuestCallOverride = (fn, a) =>
        {
            Assert.Equal(ImeHandler, fn);
            uint e = a[1], id = GuestMemory.R32(e);
            string text = "";
            if (id == Ime.EVENT_UPDATE_TEXT)
            {
                uint s = GuestMemory.R32(e + 16);
                int n = 0;
                while (GuestMemory.Read<ushort>(s + (uint)n * 2) != 0)
                {
                    n++;
                }

                text = Encoding.Unicode.GetString(GuestMemory.Span(s, n * 2));
            }

            events.Add((id, GuestMemory.R32(e + 4), text));
            return 0;
        };
        return events;
    }

    /// <summary>
    /// The in-game keyboard open with events not yet delivered (the user typed and pressed Enter between two
    /// sceImeUpdate): saved and loaded, the next sceImeUpdate delivers the same events with the same text to the
    /// game's handler, and the session (text, caret, open) is the one saved.
    /// </summary>
    [Fact]
    public void ImeDeliversThePendingEventsAfterALoad()
    {
        var ime = new Ime();
        List<(uint Id, uint Caret, string Text)> seen = Handler(ime);
        (uint param, uint buffer) = ImeParam("ab");
        Assert.Equal(0, Call(ime, "sceImeOpen", param));
        Assert.Equal(0, Call(ime, "sceImeUpdate"));
        Assert.Single(seen);
        ime.HostTextChanged("abcd", 3);
        ime.HostEnter();

        var loaded = new Ime();
        List<(uint Id, uint Caret, string Text)> seenLoaded = Handler(loaded);
        SaveAndLoad(ime, loaded);
        Assert.True(loaded.IsOpen);
        Assert.Equal(0, Call(ime, "sceImeUpdate"));
        Assert.Equal(0, Call(loaded, "sceImeUpdate"));
        Assert.Equal(seen.Skip(1).ToList(), seenLoaded);
        Assert.Equal(new List<(uint Id, uint Caret, string Text)> { (Ime.EVENT_UPDATE_TEXT, 3u, "abcd"), (Ime.EVENT_PRESS_ENTER, 0u, "") }, seenLoaded);
        Assert.Equal("abcd", loaded.Text);
        Assert.Equal(3, loaded.Caret);
        Assert.Equal(0, Call(loaded, "sceImeClose"));
        Assert.Equal(Ime.ERR_NOT_OPENED, Call(loaded, "sceImeUpdate"));
        Assert.NotEqual(0u, buffer);
    }

    // ------------------------------------------------------------------ SceNetAdhocMatching / SceRudp

    /// <summary>An ad hoc matching context created and started (the game searching for players): after a load it is
    /// still running with the same hello data, so the calls on it answer as before (no peer, empty member list).</summary>
    [Fact]
    public void AdhocMatchingContextSurvivesALoad()
    {
        var m = new NetAdhocMatching();
        uint s = fixture.Memory.Alloc(0x100, "adhoc");
        Assert.Equal(0, Call(m, "sceNetAdhocMatchingInit", 0x1000u, s));
        int id = Call(m, "sceNetAdhocMatchingCreate", 1, 4, (ushort)7, 2048, 200u, 300u, 3, 400u, 0x8100_0000u);
        GuestMemory.W32(s + 0x40, 0xDEADBEEF);
        Assert.Equal(0, Call(m, "sceNetAdhocMatchingStart", id, 0x40, 0x4000, 0, 4, s + 0x40));

        NetAdhocMatching loaded = SaveAndLoad(m, new NetAdhocMatching());
        foreach (NetAdhocMatching x in new[] { m, loaded })
        {
            GuestMemory.W32(s + 0x80, 16);
            Assert.Equal(0, Call(x, "sceNetAdhocMatchingGetHelloOpt", id, s + 0x80, s + 0x84));
            Assert.Equal((4u, 0xDEADBEEFu), (GuestMemory.R32(s + 0x80), GuestMemory.R32(s + 0x84)));
            Assert.Equal(NetAdhocMatching.ERR_IS_RUNNING, Call(x, "sceNetAdhocMatchingStart", id, 0x40, 0x4000, 0, 0, 0u));
            Assert.Equal(NetAdhocMatching.ERR_PORT_IN_USE, Call(x, "sceNetAdhocMatchingCreate", 2, 4, (ushort)7, 2048, 0u, 0u, 3, 0u, 0u));
            Assert.Equal(0, Call(x, "sceNetAdhocMatchingGetMembers", id, s + 0x90, 0u));
        }

        Assert.Equal(id + 1, Call(loaded, "sceNetAdhocMatchingCreate", 2, 4, (ushort)8, 2048, 0u, 0u, 3, 0u, 0u));
    }

    /// <summary>RUDP contexts (bound, with a peer) and a poll holding them: after a load the context answers with the
    /// same local and remote info, the poll keeps its entries, and new ids continue.</summary>
    [Fact]
    public void RudpContextsAndPollsSurviveALoad()
    {
        var rudp = new Rudp();
        uint s = fixture.Memory.Alloc(0x100, "rudp");
        Assert.Equal(0, Call(rudp, "sceRudpInit", 0u, 0u));
        Assert.Equal(0, Call(rudp, "sceRudpCreateContext", 0u, 0u, P<int>(s)));
        int ctx = (int)GuestMemory.R32(s);
        Assert.Equal(0, Call(rudp, "sceRudpBind", ctx, 5, (ushort)3658, (byte)1));
        for (uint i = 0; i < 16; i++)
        {
            GuestMemory.Write((s + 0x20) + i, (byte)(i + 1));
        }

        Assert.NotEqual(0, Call(rudp, "sceRudpInitiate", ctx, s + 0x20, 16u, (ushort)9));
        int poll = Call(rudp, "sceRudpPollCreate", 4u);
        Assert.Equal(0, Call(rudp, "sceRudpPollControl", poll, 1, ctx, (ushort)1));

        Rudp loaded = SaveAndLoad(rudp, new Rudp());
        foreach (Rudp x in new[] { rudp, loaded })
        {
            Assert.Equal(0, Call(x, "sceRudpGetLocalInfo", ctx, s + 0x40, 0u, 0u, s + 0x44, s + 0x46));
            Assert.Equal(5u, GuestMemory.R32(s + 0x40));
            Assert.Equal((ushort)3658, GuestMemory.Read<ushort>(s + 0x44));
            Assert.Equal(0, Call(x, "sceRudpGetRemoteInfo", ctx, s + 0x50, s + 0x60, s + 0x64));
            Assert.Equal(GuestMemory.Span(s + 0x20, 16).ToArray(), GuestMemory.Span(s + 0x50, 16).ToArray());
            Assert.Equal((ushort)9, GuestMemory.Read<ushort>(s + 0x64));
            Assert.NotEqual(0, Call(x, "sceRudpPollControl", poll, 1, ctx, (ushort)1)); // already in the poll
            Assert.Equal(0, Call(x, "sceRudpPollControl", poll, 2, ctx, (ushort)3));
        }

        Assert.Equal(poll + 1, Call(loaded, "sceRudpPollCreate", 4u));
        Assert.Equal(0, loaded.BlockedPollWaits);
    }

    // ------------------------------------------------------------------ SceG729 / SceAudioenc / SceVideoenc / SceLibMp4Recorder

    /// <summary>G.729 decoder and encoder contexts the voice chat made are there after a load: the calls on them work
    /// (the decoder makes its host codec again at its next frame).</summary>
    [Fact]
    public void G729ContextsSurviveALoad()
    {
        var g = new G729();
        uint ctx = fixture.Memory.Alloc(0x2000, "g729"), enc = ctx + 0x1000, io = fixture.Memory.Alloc(0x1000, "g729io");
        GuestMemory.Fill(io, 0, 0x1000);
        Assert.Equal(0, Call(g, "sceG729DecodeInit", ctx));
        Assert.Equal(0, Call(g, "sceG729DecodeCore", ctx, io, io + 0x200));
        Assert.Equal(0, Call(g, "sceG729EncodeInit", enc, 1u));

        G729 loaded = SaveAndLoad(g, new G729());
        foreach (G729 x in new[] { g, loaded })
        {
            Assert.Equal(0, Call(x, "sceG729DecodeCore", ctx, io, io + 0x200));
            Assert.Equal(0, Call(x, "sceG729DecodeReset", ctx));
            Assert.Equal(0, Call(x, "sceG729EncodeCore", enc, io + 0x400, io + 0x600));
            Assert.Equal(0, Call(x, "sceG729EncodeReset", enc));
        }
    }

    private uint EncCtrl(uint bitRate)
    {
        uint c = fixture.Memory.Alloc(0x2000, "audioenc ctrl");
        GuestMemory.Fill(c, 0, 0x2000);
        uint info = c + 0x40, opt = c + 0x80, pcm = c + 0x400, es = c + 0x1000;
        GuestMemory.W32(c, 0x2C);
        GuestMemory.W32(c + 8, pcm);
        GuestMemory.W32(c + 0x14, es);
        GuestMemory.W32(c + 0x20, 16);
        GuestMemory.W32(c + 0x24, info);
        GuestMemory.W32(c + 0x28, opt);
        GuestMemory.W32(info, 0x10);
        GuestMemory.W32(info + 8, 8000);
        GuestMemory.W32(info + 12, bitRate);
        GuestMemory.W32(opt, 0x2C);
        return c;
    }

    /// <summary>A CELP encoder in use: after a load it encodes frames of the same sizes, the library still counts it
    /// (TermLibrary refused while it exists, a second one does not fit the stream count).</summary>
    [Fact]
    public void AudioencEncoderSurvivesALoad()
    {
        var a = new Audioenc();
        uint init = fixture.Memory.Alloc(0x10, "audioenc init");
        GuestMemory.W32(init, 8);
        GuestMemory.W32(init + 4, 1);
        Assert.Equal(0, Call(a, "sceAudioencInitLibrary", Audioenc.TYPE_CELP, init));
        uint ctrl = EncCtrl(5700);
        Assert.Equal(0, Call(a, "sceAudioencCreateEncoder", ctrl, Audioenc.TYPE_CELP));
        Assert.Equal(0, Call(a, "sceAudioencEncode", ctrl));

        Audioenc loaded = SaveAndLoad(a, new Audioenc());
        var results = new List<(uint, uint)>();
        foreach (Audioenc x in new[] { a, loaded })
        {
            Assert.Equal(0, Call(x, "sceAudioencEncodeNFrames", ctrl, 3u));
            results.Add((GuestMemory.R32(ctrl + 0xC), GuestMemory.R32(ctrl + 0x18)));
            Assert.Equal(Audioenc.ERR_A_HANDLE_IN_USE, Call(x, "sceAudioencTermLibrary", Audioenc.TYPE_CELP));
            Assert.Equal(Audioenc.ERR_ALL_HANDLES_IN_USE, Call(x, "sceAudioencCreateEncoder", EncCtrl(3850), Audioenc.TYPE_CELP));
        }

        Assert.Equal(results[0], results[1]);
        Assert.Equal(0, Call(loaded, "sceAudioencDeleteEncoder", ctrl));
    }

    /// <summary>An H.264 encoder in use (the Camera's recording): after a load its frame count goes on and it keeps
    /// encoding; the library stays initialised.</summary>
    [Fact]
    public void VideoencEncoderSurvivesALoad()
    {
        var v = new Videoenc();
        uint s = fixture.Memory.Alloc(0x1000, "videoenc"), ctrl = s, query = s + 0x40, pic = s + 0x100;
        Assert.Equal(0, Call(v, "sceVideoencInitLibrary", Videoenc.TYPE_HW_AVCENC_GUESS, s + 0x200));
        GuestMemory.W32(query, 640);
        GuestMemory.W32(query + 4, 368);
        Assert.Equal(0, Call(v, "sceAvcencCreateEncoderBasic", Videoenc.TYPE_HW_AVCENC_GUESS, ctrl, query));
        uint h = GuestMemory.R32(ctrl);
        Assert.Equal(0, Call(v, "sceAvcencEncode", ctrl, pic, pic + 0x40));
        Assert.Equal(0, Call(v, "sceAvcencEncode", ctrl, pic, pic + 0x40));

        Videoenc loaded = SaveAndLoad(v, new Videoenc());
        Assert.Equal(2, loaded.FramesOf(h));
        foreach (Videoenc x in new[] { v, loaded })
        {
            Assert.Equal(0, Call(x, "sceAvcencEncode", ctrl, pic, pic + 0x40));
            Assert.Equal(3, x.FramesOf(h));
        }

        Assert.Equal(0, Call(loaded, "sceAvcencDeleteEncoder", ctrl));
        Assert.Equal(0, Call(loaded, "sceVideoencTermLibrary", Videoenc.TYPE_HW_AVCENC_GUESS));
    }

    /// <summary>A recorder recording (mode set, samples added): after a load it takes samples on from the saved
    /// counts.</summary>
    [Fact]
    public void Mp4RecorderSurvivesALoad()
    {
        var m = new Mp4Recorder();
        uint s = fixture.Memory.Alloc(0x1000, "mp4rec"), rec = s, init = s + 0x40;
        GuestMemory.Fill(s, 0, 0x1000);
        GuestMemory.W32(rec, 0x10);
        Assert.Equal(0, Call(m, "sceMp4RecCreateRecorder", rec));
        GuestMemory.W32(init, 0x20);
        Assert.Equal(0, Call(m, "sceMp4RecInit", rec, init));
        Assert.Equal(0, Call(m, "sceMp4RecAddVideoSample", rec, s + 0x100, 640 * 368 * 3 / 2));
        Assert.Equal(0, Call(m, "sceMp4RecAddAudioSample", rec, s + 0x100, 4096));

        Mp4Recorder loaded = SaveAndLoad(m, new Mp4Recorder());
        foreach (Mp4Recorder x in new[] { m, loaded })
        {
            Assert.Equal(0, Call(x, "sceMp4RecAddVideoSample", rec, s + 0x100, 640 * 368 * 3 / 2));
            Assert.Equal((2L, 1L), x.Counts(rec));
        }

        Assert.Equal(0, Call(loaded, "sceMp4RecDeleteRecorder", rec));
    }

    // ------------------------------------------------------------------ SceTriggerUtil

    /// <summary>
    /// The registered events are the system's (on disk), not the game's memory: a save with events registered is not
    /// refused, and a load does not roll them back: an event unregistered after the save stays unregistered, one
    /// registered after it stays registered (as saved data and registry values).
    /// </summary>
    [Fact]
    public void TriggerUtilEventsAreTheSystemsAndAreNotRolledBack()
    {
        string root = Path.Combine(Path.GetTempPath(), "vitasharp-trigger-" + Guid.NewGuid().ToString("N"));
        try
        {
            TriggerUtil t = new() { FsRootOverride = root, TitleIdOverride = "ABCD12345", TitleOverride = "T" };
            uint param = fixture.Memory.Alloc(TriggerUtil.OneTimeParamSize, "trigger");
            GuestMemory.Fill(param, 0, TriggerUtil.OneTimeParamSize);
            GuestMemory.Write(param + 8, 0x1122334455667788UL);
            Assert.Equal(0, Call(t, "sceTriggerUtilRegisterOneTimeEvent", 0u, param, 1, 0, 0));
            Assert.Equal(0, Call(t, "sceTriggerUtilRegisterOneTimeEvent", 0u, param, 2, 0, 0));
            Assert.Null(((ISnapshotState)t).CheckSnapshot());
            byte[] state = Save(t);

            Assert.Equal(0, Call(t, "sceTriggerUtilUnregisterOneTimeEvent", 1, 0, 0));
            Assert.Equal(0, Call(t, "sceTriggerUtilRegisterOneTimeEvent", 0u, param, 3, 0, 0));
            var r = new SnapshotReader(state);
            ((ISnapshotState)t).LoadState(r);
            Assert.True(r.AtEnd);
            Assert.Equal([2, 3], t.Events.Keys.Order().ToArray());
            uint tick = fixture.Memory.Alloc(8, "tick");
            Assert.Equal(0, Call(t, "sceTriggerUtilGetOneTimeEventInfo", 2, tick, 0, 0));
            Assert.Equal(0x1122334455667788UL, GuestMemory.Read<ulong>(tick));
            Assert.Equal(TriggerUtil.ERR_NOT_REGISTERED, Call(t, "sceTriggerUtilGetOneTimeEventInfo", 1, tick, 0, 0));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
