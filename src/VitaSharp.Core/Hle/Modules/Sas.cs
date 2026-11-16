using VitaSharp.Core.Hle.Audio;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceSas (libsas): the PSP-heritage software voice mixer (VAG/HEVAG ADPCM, PCM and noise voices, pitch, ADSR, dry/wet
/// volumes, one effect unit). sceSasCore writes one grain of interleaved stereo S16 at 48 kHz into a game buffer.
/// There is no header: the prototypes are the PSP ones without the SasCore pointer plus a config string at init
/// (docs/hle/G1-media-libs.md), and the error codes are the PSP's 0x8042xxxx. sceSysmoduleLoadModule(SCE_SYSMODULE_SAS)
/// prefers the real libsas.suprx by LLE when present; this HLE is the fallback.
/// <para>
/// The FW 3.74 libsas (decompiled, study only) keeps each mixer in a context at the start of the caller's buffer
/// (aligned to 16 bytes): every public call is its context variant (the second half of the export table, listed in
/// Sas.Internal.cs) on the context sceSasInit made. The HLE keeps the public context in its own fields and the
/// contexts made by the context variants in <see cref="_contexts"/>.
/// </para>
/// </summary>
public sealed partial class Sas : HleModule
{
    internal const int ERR_INVALID_GRAIN = unchecked((int)0x80420001), ERR_INVALID_MAX_VOICES = unchecked((int)0x80420002),
        ERR_INVALID_OUTPUT_MODE = unchecked((int)0x80420003), ERR_BAD_ADDRESS = unchecked((int)0x80420005),
        ERR_INVALID_VOICE = unchecked((int)0x80420010), ERR_INVALID_NOISE_FREQ = unchecked((int)0x80420011),
        ERR_INVALID_PITCH = unchecked((int)0x80420012), ERR_INVALID_ADSR_CURVE_MODE = unchecked((int)0x80420013),
        ERR_INVALID_PARAMETER = unchecked((int)0x80420014), ERR_INVALID_LOOP_POS = unchecked((int)0x80420015),
        ERR_VOICE_PAUSED = unchecked((int)0x80420016), ERR_INVALID_VOLUME = unchecked((int)0x80420018),
        ERR_INVALID_ADSR_RATE = unchecked((int)0x80420019), ERR_INVALID_PCM_SIZE = unchecked((int)0x8042001A),
        ERR_REV_INVALID_TYPE = unchecked((int)0x80420020), ERR_REV_INVALID_FEEDBACK = unchecked((int)0x80420021),
        ERR_REV_INVALID_DELAY_TIME = unchecked((int)0x80420022), ERR_REV_INVALID_VOLUME = unchecked((int)0x80420023),
        ERR_NOT_INIT = unchecked((int)0x80420100), ERR_ALREADY_INIT = unchecked((int)0x80420101);
    const int VOL_MAX = 0x1000, PITCH_MIN = 1, PITCH_MAX = 0x4000, NOISE_MAX = 63, MAX_VOICES = 256;
    const int DEFAULT_GRAIN = 256, DEFAULT_VOICES = 32;

    readonly object _lock = new();
    SasEngine? _engine;
    int _maxGrain;
    uint _buffer, _bufferSize;

    internal SasEngine? Engine => _engine;

    record struct SasConfig(int Grains, bool GrainsGiven, int Voices, int Reverbs);

    /// <summary>A mixer made by a context variant (sceSasInitInternal): its engine and what it was made with.</summary>
    sealed class SasContext(SasEngine engine, int maxGrain, uint buffer, uint bufferSize)
    {
        public SasEngine Engine { get; } = engine;

        public int MaxGrain { get; } = maxGrain;

        public uint Buffer { get; } = buffer;

        public uint BufferSize { get; } = bufferSize;
    }

    /// <summary>The contexts the context variants made, by handle (the 16-byte aligned start of their buffer).</summary>
    readonly Dictionary<uint, SasContext> _contexts = new();

    /// <summary>The handle of a context: the firmware places it at the caller's buffer aligned to 16 bytes.</summary>
    static uint ContextHandle(uint buffer) => (buffer + 0xF) & ~0xFu;

    /// <summary>The handle the public calls use: the context sceSasInit made, 0 when there is none.</summary>
    uint PublicContext
    {
        get
        {
            lock (_lock)
            {
                return _engine == null ? 0 : ContextHandle(_buffer);
            }
        }
    }

    /// <summary>The engine and the init's grain limit of a context handle (call under the lock); null when the handle is
    /// no live context.</summary>
    SasEngine? Resolve(uint handle, out int maxGrain)
    {
        maxGrain = 0;
        if (handle == 0)
        {
            return null;
        }

        if (_engine != null && handle == ContextHandle(_buffer))
        {
            maxGrain = _maxGrain;
            return _engine;
        }

        if (_contexts.TryGetValue(handle, out SasContext? context))
        {
            maxGrain = context.MaxGrain;
            return context.Engine;
        }

        return null;
    }

    /// <summary>"numGrains=256 numVoices=32 numReverbs=1": whitespace/comma separated key=value pairs; unknown keys are
    /// ignored. Keys and defaults are from the SDK's sample usage (unconfirmed).</summary>
    static int ParseConfig(uint config, out SasConfig cfg)
    {
        cfg = new SasConfig(DEFAULT_GRAIN, false, DEFAULT_VOICES, 1);
        if (config == 0)
        {
            return ERR_BAD_ADDRESS;
        }

        string text = GuestMemory.ReadCString(config, 1024);
        foreach (string token in text.Split([' ', '\t', ',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = token.IndexOf('=');
            if (eq <= 0 || !TryParseInt(token[(eq + 1)..], out int value))
            {
                Log.WarnOnce("sas-cfg-" + token, $"sceSas: unparsed config token '{token}'");
                continue;
            }

            switch (token[..eq].ToLowerInvariant())
            {
                case "numgrains":
                    cfg = cfg with { Grains = value, GrainsGiven = true };
                    break;
                case "numvoices":
                    cfg = cfg with { Voices = value };
                    break;
                case "numreverbs":
                    cfg = cfg with { Reverbs = value };
                    break;
                default:
                    Log.WarnOnce("sas-cfg-" + token, $"sceSas: unknown config key '{token}'");
                    break;
            }
        }

        if (cfg.Voices is < 1 or > MAX_VOICES)
        {
            return ERR_INVALID_MAX_VOICES;
        }

        if (cfg.GrainsGiven && !ValidGrain(cfg.Grains, SasEngine.MaxGrain))
        {
            return ERR_INVALID_GRAIN;
        }

        return 0;
    }

    static bool TryParseInt(string text, out int value) =>
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out value)
            : int.TryParse(text, out value);

    /// <summary>PSP rule: 64..2048 samples in steps of 32 (and here also at most the init's numGrains).</summary>
    static bool ValidGrain(int grain, int max) => grain >= 64 && grain <= Math.Min(max, SasEngine.MaxGrain) && grain % 32 == 0;

    /// <summary>The bytes the HLE asks for a config (it keeps its state on the host: any stable size works).</summary>
    static uint NeededMemorySize(SasConfig cfg) => (uint)(0x1000 + cfg.Voices * 0x100 + cfg.Grains * 8 + 0xFF) & ~0xFFu;

    [Hle]
    int sceSasGetNeededMemorySize(uint config, Ptr<uint> outSize)
    {
        int result = ParseConfig(config, out SasConfig cfg);
        if (result != 0)
        {
            return result;
        }

        if (outSize.IsNull)
        {
            return ERR_BAD_ADDRESS;
        }

        outSize.Value = NeededMemorySize(cfg);
        return 0;
    }

    [Hle]
    int sceSasInit(uint config, uint buffer, uint bufferSize) => InitCore(config, 0, buffer, bufferSize);

    [Hle]
    int sceSasInitWithGrain(uint config, uint grain, uint buffer, uint bufferSize) => InitCore(config, (int)grain, buffer, bufferSize, true);

    int InitCore(uint config, int grain, uint buffer, uint bufferSize, bool explicitGrain = false)
    {
        int result = ParseConfig(config, out SasConfig cfg);
        if (result != 0)
        {
            return result;
        }

        if (buffer == 0)
        {
            return ERR_BAD_ADDRESS;
        }

        int max = cfg.GrainsGiven ? cfg.Grains : SasEngine.MaxGrain;
        if (!explicitGrain)
        {
            grain = cfg.Grains;
        }
        else if (!ValidGrain(grain, max))
        {
            return ERR_INVALID_GRAIN;
        }

        lock (_lock)
        {
            if (_engine != null)
            {
                return ERR_ALREADY_INIT;
            }

            _contexts.Remove(ContextHandle(buffer));
            _engine = new SasEngine(grain, cfg.Voices);
            _maxGrain = max;
            _buffer = buffer;
            _bufferSize = bufferSize;
        }

        Log.Debug($"sceSasInit: grain {grain}, {cfg.Voices} voices, {cfg.Reverbs} reverbs");
        return 0;
    }

    [Hle]
    int sceSasExit(Ptr<uint> outBuffer, Ptr<uint> outBufferSize)
    {
        lock (_lock)
        {
            if (_engine == null)
            {
                return ERR_NOT_INIT;
            }

            outBuffer.TrySet(_buffer);
            outBufferSize.TrySet(_bufferSize);
            _engine = null;
        }

        return 0;
    }

    // ---- helpers

    int WithEngine(uint context, Func<SasEngine, int> operation)
    {
        lock (_lock)
        {
            SasEngine? engine = Resolve(context, out _);
            return engine == null ? ERR_NOT_INIT : operation(engine);
        }
    }

    int WithVoice(uint context, int voice, Func<SasVoice, int> operation)
    {
        lock (_lock)
        {
            SasEngine? engine = Resolve(context, out _);
            if (engine == null)
            {
                return ERR_NOT_INIT;
            }

            if (voice < 0 || voice >= engine.Voices.Length)
            {
                return ERR_INVALID_VOICE;
            }

            return operation(engine.Voices[voice]);
        }
    }

    // ---- mixing

    [Hle]
    int sceSasCore(uint output) => Core(PublicContext, output, false, VOL_MAX, VOL_MAX);

    [Hle]
    int sceSasCoreWithMix(uint inOut, int lvol, int rvol) => CoreWithMix(PublicContext, inOut, lvol, rvol);

    int CoreWithMix(uint context, uint inOut, int lvol, int rvol)
    {
        if (Math.Abs(lvol) > VOL_MAX || Math.Abs(rvol) > VOL_MAX)
        {
            return WithEngine(context, _ => ERR_INVALID_VOLUME);
        }

        return Core(context, inOut, true, lvol, rvol);
    }

    int Core(uint context, uint output, bool mix, int lvol, int rvol) => WithEngine(context, engine =>
    {
        if (output == 0)
        {
            return ERR_BAD_ADDRESS;
        }

        if (engine.OutputMode != 0)
        {
            Log.WarnOnce("sas-outmode", $"sceSasCore: output mode {engine.OutputMode} rendered as stereo");
        }

        engine.Mix(GuestMemory.Span<short>(output, engine.Grain * 2), mix, lvol, rvol);
        return 0;
    });

    // ---- voices

    [Hle]
    int sceSasSetVoice(int iVoiceNum, uint vagBuf, uint size, uint loopflag) => SetVoice(PublicContext, iVoiceNum, vagBuf, size, loopflag);

    int SetVoice(uint context, int voiceNumber, uint vagBuf, uint size, uint loopflag) => WithVoice(context, voiceNumber, voice =>
    {
        if (size == 0 || (size & 0xF) != 0 || loopflag > 1)
        {
            return ERR_INVALID_PARAMETER;
        }

        if (vagBuf == 0)
        {
            return ERR_BAD_ADDRESS;
        }

        // a playing voice keeps its position when the game only refreshes size/loop on the same data
        bool changed = voice.Type != SasVoiceType.Vag || voice.Addr != vagBuf;
        voice.Type = SasVoiceType.Vag;
        voice.Addr = vagBuf;
        voice.Size = size;
        voice.Loop = loopflag != 0;
        if (changed && voice.On)
        {
            voice.ResetSource();
        }

        return 0;
    });

    [Hle]
    int sceSasSetVoicePCM(int iVoiceNum, uint pcmBuf, int size, int loopsize) => SetVoicePcm(PublicContext, iVoiceNum, pcmBuf, size, loopsize);

    int SetVoicePcm(uint context, int voiceNumber, uint pcmBuf, int size, int loopsize) => WithVoice(context, voiceNumber, voice =>
    {
        if (size <= 0)
        {
            return ERR_INVALID_PCM_SIZE;
        }

        if (loopsize > size)
        {
            return ERR_INVALID_LOOP_POS;
        }

        if (pcmBuf == 0)
        {
            return ERR_BAD_ADDRESS;
        }

        bool changed = voice.Type != SasVoiceType.Pcm || voice.Addr != pcmBuf;
        voice.Type = SasVoiceType.Pcm;
        voice.Addr = pcmBuf;
        voice.Size = (uint)size;
        voice.PcmLoopSize = Math.Max(0, loopsize);
        if (changed && voice.On)
        {
            voice.ResetSource();
        }

        return 0;
    });

    [Hle]
    int sceSasSetNoise(int iVoiceNum, uint uClk) => SetNoise(PublicContext, iVoiceNum, uClk);

    int SetNoise(uint context, int voiceNumber, uint clock) => WithVoice(context, voiceNumber, voice =>
    {
        if (clock > NOISE_MAX)
        {
            return ERR_INVALID_NOISE_FREQ;
        }

        voice.Type = SasVoiceType.Noise;
        voice.NoiseClock = (int)clock;
        return 0;
    });

    [Hle]
    int sceSasSetPitch(int iVoiceNum, int pitch) => SetPitch(PublicContext, iVoiceNum, pitch);

    int SetPitch(uint context, int voiceNumber, int pitch) => WithVoice(context, voiceNumber, voice =>
    {
        if (pitch is < PITCH_MIN or > PITCH_MAX)
        {
            return ERR_INVALID_PITCH;
        }

        voice.Pitch = pitch;
        return 0;
    });

    [Hle]
    int sceSasSetVolume(int iVoiceNum, int l, int r, int wl, int wr) => SetVolume(PublicContext, iVoiceNum, l, r, wl, wr);

    int SetVolume(uint context, int voiceNumber, int left, int right, int wetLeft, int wetRight) => WithVoice(context, voiceNumber, voice =>
    {
        if (Math.Abs(left) > VOL_MAX || Math.Abs(right) > VOL_MAX || Math.Abs(wetLeft) > VOL_MAX || Math.Abs(wetRight) > VOL_MAX)
        {
            return ERR_INVALID_VOLUME;
        }

        voice.VolL = left;
        voice.VolR = right;
        voice.WetL = wetLeft;
        voice.WetR = wetRight;
        return 0;
    });

    [Hle]
    int sceSasSetDistortion(int iVoiceNum, int distortion) => SetDistortion(PublicContext, iVoiceNum, distortion);

    int SetDistortion(uint context, int voiceNumber, int distortion) => WithVoice(context, voiceNumber, voice =>
    {
        Log.WarnOnce("sas-distortion", "sceSasSetDistortion: stored, not applied (semantics unknown)");
        voice.Distortion = distortion;
        return 0;
    });

    [Hle]
    int sceSasSetADSR(int iVoiceNum, uint flag, uint a, uint d, uint s, uint r) => SetAdsr(PublicContext, iVoiceNum, flag, a, d, s, r);

    int SetAdsr(uint context, int voiceNumber, uint flag, uint attack, uint decay, uint sustain, uint release) => WithVoice(context, voiceNumber, voice =>
    {
        uint[] rates = [attack, decay, sustain, release];
        for (int i = 0; i < 4; i++)
        {
            if ((flag & (1u << i)) != 0 && rates[i] > int.MaxValue)
            {
                return ERR_INVALID_ADSR_RATE;
            }
        }

        for (int i = 0; i < 4; i++)
        {
            if ((flag & (1u << i)) != 0)
            {
                voice.Env.Rates[i] = (int)rates[i];
            }
        }

        return 0;
    });

    [Hle]
    int sceSasSetADSRmode(int iVoiceNum, uint flag, uint a, uint d, uint s, uint r) => SetAdsrMode(PublicContext, iVoiceNum, flag, a, d, s, r);

    int SetAdsrMode(uint context, int voiceNumber, uint flag, uint attack, uint decay, uint sustain, uint release) => WithVoice(context, voiceNumber, voice =>
    {
        uint[] modes = [attack, decay, sustain, release];
        for (int i = 0; i < 4; i++)
        {
            if ((flag & (1u << i)) != 0 && !SasEnvelope.ValidMode(i, modes[i]))
            {
                return ERR_INVALID_ADSR_CURVE_MODE;
            }
        }

        for (int i = 0; i < 4; i++)
        {
            if ((flag & (1u << i)) != 0)
            {
                voice.Env.Modes[i] = (int)modes[i];
            }
        }

        return 0;
    });

    [Hle]
    int sceSasSetSL(int iVoiceNum, uint sl) => SetSustainLevel(PublicContext, iVoiceNum, sl);

    int SetSustainLevel(uint context, int voiceNumber, uint level) => WithVoice(context, voiceNumber, voice =>
    {
        if (level > SasEnvelope.Max)
        {
            return ERR_INVALID_PARAMETER;
        }

        voice.Env.SustainLevel = (int)level;
        return 0;
    });

    [Hle]
    int sceSasSetSimpleADSR(int iVoiceNum, uint adsr1, uint adsr2) => SetSimpleAdsr(PublicContext, iVoiceNum, adsr1, adsr2);

    int SetSimpleAdsr(uint context, int voiceNumber, uint adsr1, uint adsr2) => WithVoice(context, voiceNumber, voice =>
    {
        voice.Env.SetSimple(adsr1 & 0xFFFF, adsr2 & 0xFFFF);
        return 0;
    });

    [Hle]
    int sceSasSetKeyOn(int iVoiceNum) => SetKeyOn(PublicContext, iVoiceNum);

    int SetKeyOn(uint context, int voiceNumber) => WithVoice(context, voiceNumber, voice =>
    {
        if (voice.Paused)
        {
            return ERR_VOICE_PAUSED;
        }

        voice.KeyOn();
        return 0;
    });

    [Hle]
    int sceSasSetKeyOff(int iVoiceNum) => SetKeyOff(PublicContext, iVoiceNum);

    int SetKeyOff(uint context, int voiceNumber) => WithVoice(context, voiceNumber, voice =>
    {
        if (voice.Paused)
        {
            return ERR_VOICE_PAUSED;
        }

        voice.KeyOff();
        return 0;
    });

    [Hle]
    int sceSasSetPause(int iVoiceNum, uint pauseFlag) => SetPause(PublicContext, iVoiceNum, pauseFlag);

    int SetPause(uint context, int voiceNumber, uint pauseFlag) => WithVoice(context, voiceNumber, voice =>
    {
        voice.Paused = pauseFlag != 0;
        return 0;
    });

    [Hle]
    int sceSasGetEndState(int iVoiceNum) => GetEndState(PublicContext, iVoiceNum);

    int GetEndState(uint context, int voiceNumber) => WithVoice(context, voiceNumber, voice => voice.Ended ? 1 : 0);

    [Hle]
    int sceSasGetPauseState(int iVoiceNum) => GetPauseState(PublicContext, iVoiceNum);

    int GetPauseState(uint context, int voiceNumber) => WithVoice(context, voiceNumber, voice => voice.Paused ? 1 : 0);

    [Hle]
    int sceSasGetEnvelope(int iVoiceNum) => GetEnvelope(PublicContext, iVoiceNum);

    int GetEnvelope(uint context, int voiceNumber) => WithVoice(context, voiceNumber, voice => voice.Env.Height);

    // ---- effect

    /// <summary>Nonzero switches are treated as on (the PSP accepts only 0/1; unconfirmed on the Vita).</summary>
    [Hle]
    int sceSasSetEffect(int drySwitch, int wetSwitch) => SetEffect(PublicContext, drySwitch, wetSwitch);

    int SetEffect(uint context, int drySwitch, int wetSwitch) => WithEngine(context, engine =>
    {
        engine.DryOn = drySwitch != 0;
        engine.WetOn = wetSwitch != 0;
        return 0;
    });

    [Hle]
    int sceSasSetEffectType(int type) => SetEffectType(PublicContext, type);

    int SetEffectType(uint context, int type) => WithEngine(context, engine =>
    {
        if (type is < SasEngine.EffectOff or > 8)
        {
            return ERR_REV_INVALID_TYPE;
        }

        if (type is not (SasEngine.EffectOff or SasEngine.EffectEcho or SasEngine.EffectDelay))
        {
            Log.WarnOnce("sas-reverb" + type, $"sceSasSetEffectType({type}): reverb not modelled, the wet send passes through");
        }

        if (engine.EffectType != type)
        {
            engine.ClearEffect();
        }

        engine.EffectType = type;
        return 0;
    });

    [Hle]
    int sceSasSetEffectParam(uint delayTime, uint feedback) => SetEffectParam(PublicContext, delayTime, feedback);

    int SetEffectParam(uint context, uint delayTime, uint feedback) => WithEngine(context, engine =>
    {
        if (delayTime > 127)
        {
            return ERR_REV_INVALID_DELAY_TIME;
        }

        if (feedback > 127)
        {
            return ERR_REV_INVALID_FEEDBACK;
        }

        engine.EffectDelayTime = (int)delayTime;
        engine.EffectFeedback = (int)feedback;
        return 0;
    });

    [Hle]
    int sceSasSetEffectVolume(int valL, int valR) => SetEffectVolume(PublicContext, valL, valR);

    int SetEffectVolume(uint context, int left, int right) => WithEngine(context, engine =>
    {
        if (Math.Abs(left) > VOL_MAX || Math.Abs(right) > VOL_MAX)
        {
            return ERR_REV_INVALID_VOLUME;
        }

        engine.EffectVolL = left;
        engine.EffectVolR = right;
        return 0;
    });

    // ---- global settings

    [Hle]
    int sceSasSetGrain(uint grain) => SetGrain(PublicContext, grain);

    int SetGrain(uint context, uint grain)
    {
        lock (_lock)
        {
            SasEngine? engine = Resolve(context, out int maxGrain);
            if (engine == null)
            {
                return ERR_NOT_INIT;
            }

            if (!ValidGrain((int)Math.Min(grain, int.MaxValue), maxGrain))
            {
                return ERR_INVALID_GRAIN;
            }

            engine.Grain = (int)grain;
            return 0;
        }
    }

    [Hle]
    int sceSasGetGrain() => GetGrain(PublicContext);

    int GetGrain(uint context) => WithEngine(context, engine => engine.Grain);

    [Hle]
    int sceSasSetOutputmode(uint outputmode) => SetOutputMode(PublicContext, outputmode);

    int SetOutputMode(uint context, uint outputMode) => WithEngine(context, engine =>
    {
        if (outputMode > 1)
        {
            return ERR_INVALID_OUTPUT_MODE;
        }

        engine.OutputMode = (int)outputMode;
        return 0;
    });

    [Hle]
    int sceSasGetOutputmode() => GetOutputMode(PublicContext);

    int GetOutputMode(uint context) => WithEngine(context, engine => engine.OutputMode);

    [Hle]
    int sceSasGetDryPeak(Ptr<int> pPeakL, Ptr<int> pPeakR) => Peak(PublicContext, pPeakL, pPeakR, PeakKind.Dry);

    [Hle]
    int sceSasGetWetPeak(Ptr<int> pPeakL, Ptr<int> pPeakR) => Peak(PublicContext, pPeakL, pPeakR, PeakKind.Wet);

    [Hle]
    int sceSasGetPreMasterPeak(Ptr<int> pPeakL, Ptr<int> pPeakR) => Peak(PublicContext, pPeakL, pPeakR, PeakKind.PreMaster);

    enum PeakKind
    {
        Dry,
        Wet,
        PreMaster,
    }

    int Peak(uint context, Ptr<int> left, Ptr<int> right, PeakKind kind) => WithEngine(context, engine =>
    {
        if (left.IsNull && right.IsNull)
        {
            return ERR_BAD_ADDRESS;
        }

        (int peakLeft, int peakRight) = kind switch
        {
            PeakKind.Dry => (engine.DryPeakL, engine.DryPeakR),
            PeakKind.Wet => (engine.WetPeakL, engine.WetPeakR),
            _ => (engine.PreMasterPeakL, engine.PreMasterPeakR),
        };
        left.TrySet(peakLeft);
        right.TrySet(peakRight);
        return 0;
    });
}
