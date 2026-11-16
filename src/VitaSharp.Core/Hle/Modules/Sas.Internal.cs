using VitaSharp.Core.Hle.Audio;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The context variants of SceSas as the FW 3.74 libsas exports them (decompiled, study only): the second half of the
/// export table, each the public call with the mixer context first (the public sceSasCore is sceSasCoreInternal on the
/// context sceSasInit made, and so on). sceSasInitInternal makes a context at the caller's buffer aligned to 16 bytes
/// (the buffer must have exactly the size sceSasGetNeededMemorySizeInternal gives) and returns its handle;
/// sceSasExitInternal hands the buffer back and ends it. A context made this way can set any grain the format allows
/// (64..2048 in steps of 32). The volumes of sceSasCoreInternal are unsigned: 0/0 writes the grain, any other pair
/// mixes it into the buffer with those levels.
/// </summary>
public sealed partial class Sas
{
    /// <summary>NID_5F82207D(config, *size): sceSasGetNeededMemorySize; *size is cleared first and a missing pointer
    /// only checks the config.</summary>
    [Hle(Nid = 0x5F82207D, Library = "SceSas")]
    int sceSasGetNeededMemorySizeInternal(uint config, Ptr<uint> outSize)
    {
        outSize.TrySet(0);
        int result = ParseConfig(config, out SasConfig cfg);
        if (result != 0)
        {
            return result;
        }

        outSize.TrySet(NeededMemorySize(cfg));
        return 0;
    }

    /// <summary>NID_0AEA9963(grain): 0 when the grain is one the format allows (64..2048 in steps of 32), the invalid
    /// grain error otherwise (sceSasInitWithGrain checks it first).</summary>
    [Hle(Nid = 0x0AEA9963, Library = "SceSas")]
    int sceSasCheckGrainInternal(uint grain) => ValidGrain((int)Math.Min(grain, int.MaxValue), SasEngine.MaxGrain) ? 0 : ERR_INVALID_GRAIN;

    /// <summary>NID_5C5250C1(config, buffer, size, *context): a new mixer context in <paramref name="buffer"/>, which
    /// must be the size the config needs.</summary>
    [Hle(Nid = 0x5C5250C1, Library = "SceSas")]
    int sceSasInitInternal(uint config, uint buffer, uint bufferSize, Ptr<uint> outContext)
    {
        int result = ParseConfig(config, out SasConfig cfg);
        if (result != 0)
        {
            return result;
        }

        if (buffer == 0 || bufferSize != NeededMemorySize(cfg))
        {
            return ERR_BAD_ADDRESS;
        }

        uint handle = ContextHandle(buffer);
        var engine = new SasEngine(cfg.Grains, cfg.Voices);
        lock (_lock)
        {
            if (_engine != null && handle == ContextHandle(_buffer))
            {
                // the public context lives in this buffer: the firmware builds the new one over it
                _engine = engine;
                _maxGrain = SasEngine.MaxGrain;
                _bufferSize = bufferSize;
            }
            else
            {
                _contexts[handle] = new SasContext(engine, SasEngine.MaxGrain, buffer, bufferSize);
            }
        }

        outContext.TrySet(handle);
        return 0;
    }

    /// <summary>NID_799DD9C0(context, *buffer, *size): ends a context and hands back the buffer it was made in (both
    /// outputs cleared first).</summary>
    [Hle(Nid = 0x799DD9C0, Library = "SceSas")]
    int sceSasExitInternal(uint context, Ptr<uint> outBuffer, Ptr<uint> outBufferSize)
    {
        outBuffer.TrySet(0);
        outBufferSize.TrySet(0);
        lock (_lock)
        {
            if (Resolve(context, out _) == null)
            {
                return ERR_NOT_INIT;
            }

            if (_engine != null && context == ContextHandle(_buffer))
            {
                outBuffer.TrySet(_buffer);
                outBufferSize.TrySet(_bufferSize);
                _engine = null;
                return 0;
            }

            SasContext ended = _contexts[context];
            outBuffer.TrySet(ended.Buffer);
            outBufferSize.TrySet(ended.BufferSize);
            _contexts.Remove(context);
            return 0;
        }
    }

    /// <summary>NID_62CCF050(context, buffer, lvol, rvol): sceSasCore (0/0) or sceSasCoreWithMix; levels above 0x1000
    /// are refused.</summary>
    [Hle(Nid = 0x62CCF050, Library = "SceSas")]
    int sceSasCoreInternal(uint context, uint buffer, uint lvol, uint rvol)
    {
        if (lvol > VOL_MAX || rvol > VOL_MAX)
        {
            return WithEngine(context, _ => buffer == 0 ? ERR_BAD_ADDRESS : ERR_INVALID_VOLUME);
        }

        bool mix = lvol != 0 || rvol != 0;
        return Core(context, buffer, mix, (int)lvol, (int)rvol);
    }

    [Hle(Nid = 0xB47D5122, Library = "SceSas")]
    int sceSasSetGrainInternal(uint context, uint grain) => SetGrain(context, grain);

    [Hle(Nid = 0x26B2B2A9, Library = "SceSas")]
    int sceSasGetGrainInternal(uint context) => GetGrain(context);

    [Hle(Nid = 0x107D1850, Library = "SceSas")]
    int sceSasSetOutputmodeInternal(uint context, uint outputMode) => SetOutputMode(context, outputMode);

    [Hle(Nid = 0x0B96AF29, Library = "SceSas")]
    int sceSasGetOutputmodeInternal(uint context) => GetOutputMode(context);

    [Hle(Nid = 0x0C294FAF, Library = "SceSas")]
    int sceSasSetVoiceInternal(uint context, int voiceNumber, uint vagBuf, uint size, uint loopflag) =>
        SetVoice(context, voiceNumber, vagBuf, size, loopflag);

    [Hle(Nid = 0xEB6708B0, Library = "SceSas")]
    int sceSasSetVoicePCMInternal(uint context, int voiceNumber, uint pcmBuf, int size, int loopsize) =>
        SetVoicePcm(context, voiceNumber, pcmBuf, size, loopsize);

    [Hle(Nid = 0xBD2907EA, Library = "SceSas")]
    int sceSasSetNoiseInternal(uint context, int voiceNumber, uint clock) => SetNoise(context, voiceNumber, clock);

    [Hle(Nid = 0x868E1C88, Library = "SceSas")]
    int sceSasSetVolumeInternal(uint context, int voiceNumber, int left, int right, int wetLeft, int wetRight) =>
        SetVolume(context, voiceNumber, left, right, wetLeft, wetRight);

    [Hle(Nid = 0x81AF90AC, Library = "SceSas")]
    int sceSasSetPitchInternal(uint context, int voiceNumber, int pitch) => SetPitch(context, voiceNumber, pitch);

    [Hle(Nid = 0x1744ACD7, Library = "SceSas")]
    int sceSasSetDistortionInternal(uint context, int voiceNumber, int distortion) => SetDistortion(context, voiceNumber, distortion);

    [Hle(Nid = 0x44514A50, Library = "SceSas")]
    int sceSasSetADSRInternal(uint context, int voiceNumber, uint flag, uint attack, uint decay, uint sustain, uint release) =>
        SetAdsr(context, voiceNumber, flag, attack, decay, sustain, release);

    [Hle(Nid = 0xE9F3944D, Library = "SceSas")]
    int sceSasSetADSRmodeInternal(uint context, int voiceNumber, uint flag, uint attack, uint decay, uint sustain, uint release) =>
        SetAdsrMode(context, voiceNumber, flag, attack, decay, sustain, release);

    [Hle(Nid = 0xFE263F55, Library = "SceSas")]
    int sceSasSetSLInternal(uint context, int voiceNumber, uint level) => SetSustainLevel(context, voiceNumber, level);

    [Hle(Nid = 0xBF95B455, Library = "SceSas")]
    int sceSasSetSimpleADSRInternal(uint context, int voiceNumber, uint adsr1, uint adsr2) =>
        SetSimpleAdsr(context, voiceNumber, adsr1, adsr2);

    [Hle(Nid = 0xB26DF4BF, Library = "SceSas")]
    int sceSasSetKeyOnInternal(uint context, int voiceNumber) => SetKeyOn(context, voiceNumber);

    [Hle(Nid = 0x4636ACC2, Library = "SceSas")]
    int sceSasSetKeyOffInternal(uint context, int voiceNumber) => SetKeyOff(context, voiceNumber);

    [Hle(Nid = 0x79665BCA, Library = "SceSas")]
    int sceSasSetPauseInternal(uint context, int voiceNumber, uint pauseFlag) => SetPause(context, voiceNumber, pauseFlag);

    [Hle(Nid = 0x0F771045, Library = "SceSas")]
    int sceSasGetEndStateInternal(uint context, int voiceNumber) => GetEndState(context, voiceNumber);

    [Hle(Nid = 0xBEEBC820, Library = "SceSas")]
    int sceSasGetPauseStateInternal(uint context, int voiceNumber) => GetPauseState(context, voiceNumber);

    [Hle(Nid = 0xE3A5D1D4, Library = "SceSas")]
    int sceSasGetEnvelopeInternal(uint context, int voiceNumber) => GetEnvelope(context, voiceNumber);

    [Hle(Nid = 0xE04ACC5A, Library = "SceSas")]
    int sceSasSetEffectInternal(uint context, int drySwitch, int wetSwitch) => SetEffect(context, drySwitch, wetSwitch);

    [Hle(Nid = 0xDAF4773A, Library = "SceSas")]
    int sceSasSetEffectTypeInternal(uint context, int type) => SetEffectType(context, type);

    [Hle(Nid = 0x50D722D7, Library = "SceSas")]
    int sceSasSetEffectVolumeInternal(uint context, int left, int right) => SetEffectVolume(context, left, right);

    [Hle(Nid = 0xCB23EA6D, Library = "SceSas")]
    int sceSasSetEffectParamInternal(uint context, uint delayTime, uint feedback) => SetEffectParam(context, delayTime, feedback);

    [Hle(Nid = 0x40ED8367, Library = "SceSas")]
    int sceSasGetDryPeakInternal(uint context, Ptr<int> left, Ptr<int> right) => Peak(context, left, right, PeakKind.Dry);

    [Hle(Nid = 0x0DE54FF9, Library = "SceSas")]
    int sceSasGetWetPeakInternal(uint context, Ptr<int> left, Ptr<int> right) => Peak(context, left, right, PeakKind.Wet);

    [Hle(Nid = 0x3D4F37FF, Library = "SceSas")]
    int sceSasGetPreMasterPeakInternal(uint context, Ptr<int> left, Ptr<int> right) => Peak(context, left, right, PeakKind.PreMaster);
}
