using System.Numerics;
using System.Reflection;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>The SceMotion functions of driver_us.suprx 3.74 behind the Ext getters, the packed state, the three
/// basic-orientation thresholds, the accelerometer zero point and the other motion clients' reads, with the sampling
/// and argument checks of the state reads.</summary>
[Collection("guest")]
public sealed class MotionExtTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int NotSampling = unchecked((int)0x80360206);
    private const int OutOfBounds = unchecked((int)0x80360205);
    private const int NullParameter = unchecked((int)0x80360209);
    private const int AngleOutOfRange = unchecked((int)0x8036020A);
    private const int PortOutOfRange = unchecked((int)0x8036020B);
    private const int NoController = unchecked((int)0x80360202);

    private readonly HleHarness _harness = new(fixture, "motion-ext");

    public void Dispose() => _harness.Dispose();

    private Sensors Motion() => _harness.Module<Sensors>();

    [Fact]
    public void GetStateAnswersOnlyWhileSampling()
    {
        Sensors motion = Motion();
        uint state = _harness.Alloc(0xF8);
        Assert.Equal(NotSampling, Call(motion, "sceMotionGetState", state));
        Assert.Equal(NotSampling, Call(motion, "sceMotionGetState", 0u)); // the sampling is checked first

        Assert.Equal(0, Call(motion, "sceMotionStartSampling"));
        Assert.Equal(NullParameter, Call(motion, "sceMotionGetState", 0u));
        Assert.Equal(0, Call(motion, "sceMotionGetState", state));
        Assert.Equal(-1f, GuestMemory.Read<float>(state + 12)); // at rest: lying flat, 1 g down the z axis

        Assert.Equal(0, Call(motion, "sceMotionStopSampling"));
        Assert.Equal(NotSampling, Call(motion, "sceMotionGetState", state));
    }

    [Fact]
    public void GetSensorStateChecksSamplingThenCountThenPointer()
    {
        Sensors motion = Motion();
        uint states = _harness.Alloc(64 * 0x40);
        Assert.Equal(NotSampling, Call(motion, "sceMotionGetSensorState", 0u, 0));
        Assert.Equal(0, Call(motion, "sceMotionStartSampling"));
        Assert.Equal(OutOfBounds, Call(motion, "sceMotionGetSensorState", 0u, 0));
        Assert.Equal(OutOfBounds, Call(motion, "sceMotionGetSensorState", states, 65));
        Assert.Equal(NullParameter, Call(motion, "sceMotionGetSensorState", 0u, 64));
        Assert.Equal(0, Call(motion, "sceMotionGetSensorState", states, 64));
    }

    [Fact]
    public void PackedStateExtOfTheBuiltInSensors()
    {
        Sensors motion = Motion();
        uint packed = _harness.Alloc(8);
        Assert.Equal(NotSampling, Call(motion, "sceMotionGetPackedStateExt", 0, packed)); // sceMotionGetState's answer

        Assert.Equal(0, Call(motion, "sceMotionStartSampling"));
        Assert.Equal(0, Call(motion, "sceMotionGetPackedStateExt", 0, packed));
        // at rest: x and y centred, z = 512 - 113 (-1 g), no rotation
        Assert.Equal((ushort)512, GuestMemory.Read<ushort>(packed));
        Assert.Equal((ushort)512, GuestMemory.Read<ushort>(packed + 2));
        Assert.Equal((ushort)399, GuestMemory.Read<ushort>(packed + 4));
        Assert.Equal((ushort)512, GuestMemory.Read<ushort>(packed + 6));
    }

    [Fact]
    public void PackedStateTruncatesAndHoldsTenBits()
    {
        MethodInfo pack = typeof(Sensors).GetMethod("PackState", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(Sensors), "PackState");

        var packed = ((ushort X, ushort Y, ushort Z, ushort Gyro))pack.Invoke(null, [new Vector3(0.5f, -0.5f, 10f), new Vector3(0, 1f, 0)])!;
        Assert.Equal((ushort)568, packed.X);     // 512 + 56.5, truncated
        Assert.Equal((ushort)455, packed.Y);     // 512 - 56.5, truncated toward zero
        Assert.Equal((ushort)0x3FF, packed.Z);   // held at 1023
        Assert.Equal((ushort)433, packed.Gyro);  // 512 - 246 / pi

        packed = ((ushort X, ushort Y, ushort Z, ushort Gyro))pack.Invoke(null, [new Vector3(-10f, 0, 0), new Vector3(0, -100f, 0)])!;
        Assert.Equal((ushort)0, packed.X);       // held at 0
        Assert.Equal((ushort)0x3FF, packed.Gyro);
    }

    [Theory]
    [InlineData("sceMotionGetPackedStateExt")]
    [InlineData("sceMotionGetDeadbandExt")]
    [InlineData("sceMotionGetTiltCorrectionExt")]
    [InlineData("sceMotionGetStateExt")]
    public void ExternalPortsCheckRangeThenPointerThenController(string function)
    {
        Sensors motion = Motion();
        uint value = _harness.Alloc(0xF8);
        Assert.Equal(PortOutOfRange, Call(motion, function, 5, 0u));
        Assert.Equal(PortOutOfRange, Call(motion, function, 5, value));
        Assert.Equal(NullParameter, Call(motion, function, 1, 0u));
        Assert.Equal(NoController, Call(motion, function, 1, value));
        Assert.Equal(NoController, Call(motion, function, 4, value));
        Assert.Equal(NoController, Call(motion, function, -1, value)); // passes the range check, no controller
    }

    [Fact]
    public void OtherExtFunctionsRefuseAPortAboveFour()
    {
        Sensors motion = Motion();
        Assert.Equal(PortOutOfRange, Call(motion, "sceMotionStartSamplingExt", 5));
        Assert.Equal(NoController, Call(motion, "sceMotionStartSamplingExt", 2));
        Assert.Equal(NoController, Call(motion, "sceMotionStartSamplingExt", -3));
        Assert.Equal(PortOutOfRange, Call(motion, "sceMotionSetDeadbandExt", 9, 1));
        Assert.Equal(0, Call(motion, "sceMotionStartSamplingExt", 0));
    }

    [Fact]
    public void DeadbandAndTiltCorrectionKeepTheValueTheyWereGiven()
    {
        Sensors motion = Motion();
        uint value = _harness.Alloc(4);
        Assert.Equal(0, Call(motion, "sceMotionGetDeadbandExt", 0, value));
        Assert.Equal(1u, GuestMemory.R32(value));
        Assert.Equal(NullParameter, Call(motion, "sceMotionGetDeadbandExt", 0, 0u));

        Assert.Equal(0, Call(motion, "sceMotionSetDeadband", 5));
        Assert.Equal(5, Call(motion, "sceMotionGetDeadband"));
        Assert.Equal(0, Call(motion, "sceMotionGetDeadbandExt", 0, value));
        Assert.Equal(5u, GuestMemory.R32(value));

        Assert.Equal(0, Call(motion, "sceMotionSetTiltCorrection", 0));
        Assert.Equal(0, Call(motion, "sceMotionGetTiltCorrectionExt", 0, value));
        Assert.Equal(0u, GuestMemory.R32(value));
        Assert.Equal(0, Call(motion, "sceMotionSetTiltCorrectionExt", 0, 3));
        Assert.Equal(3, Call(motion, "sceMotionGetTiltCorrection"));
    }

    [Fact]
    public void AngleThresholdKeepsOnlyAnglesAboveZeroUpToFortyFive()
    {
        Sensors motion = Motion();
        Assert.Equal(AngleOutOfRange, Call(motion, "sceMotionSetAngleThreshold", 0f));
        Assert.Equal(AngleOutOfRange, Call(motion, "sceMotionSetAngleThreshold", -1f));
        Assert.Equal(AngleOutOfRange, Call(motion, "sceMotionSetAngleThreshold", float.NaN));
        Assert.Equal(AngleOutOfRange, Call(motion, "sceMotionSetAngleThreshold", 45.5f));
        Assert.Equal(20f, (float)Invoke(motion, "sceMotionGetAngleThreshold")!);
        Assert.Equal(0, Call(motion, "sceMotionSetAngleThreshold", 45f));
        Assert.Equal(0, Call(motion, "sceMotionSetAngleThreshold", 0.5f));
        Assert.Equal(0.5f, (float)Invoke(motion, "sceMotionGetAngleThreshold")!);
    }

    [Fact]
    public void TheThreeThresholdsAndTheirFirstStoredFromNinety()
    {
        Sensors motion = Motion();
        uint thresholds = _harness.Alloc(12);

        // at start: 20 stored in all three, so the first reads back as 90 - 20
        Assert.Equal(0, Call(motion, "MotionGetAngleThresholds", thresholds));
        Assert.Equal(new[] { 70f, 20f, 20f }, ReadFloats(thresholds));

        Assert.Equal(0, Call(motion, "sceMotionSetAngleThreshold", 30f));
        Assert.Equal(0, Call(motion, "MotionGetAngleThresholds", thresholds));
        Assert.Equal(new[] { 60f, 30f, 30f }, ReadFloats(thresholds));

        // no range check: each one as given, the single getter answers the third
        GuestMemory.Write(thresholds, 10f);
        GuestMemory.Write(thresholds + 4, 15f);
        GuestMemory.Write(thresholds + 8, 80f);
        Assert.Equal(0, Call(motion, "MotionSetAngleThresholds", thresholds));
        Assert.Equal(80f, (float)Invoke(motion, "sceMotionGetAngleThreshold")!);
        GuestMemory.Fill(thresholds, 0, 12);
        Assert.Equal(0, Call(motion, "MotionGetAngleThresholds", thresholds));
        Assert.Equal(new[] { 10f, 15f, 80f }, ReadFloats(thresholds));
        Assert.Equal(NullParameter, Call(motion, "MotionSetAngleThresholds", 0u));
    }

    private static float[] ReadFloats(uint address) =>
        [GuestMemory.Read<float>(address), GuestMemory.Read<float>(address + 4), GuestMemory.Read<float>(address + 8)];

    [Fact]
    public void AccelerometerZeroPointIsTheOriginInAllThree()
    {
        Sensors motion = Motion();
        uint vectors = _harness.Alloc(36);
        GuestMemory.Fill(vectors, 0xFF, 36);
        Assert.Equal(0, Call(motion, "MotionGetAccelerometerZero", vectors, vectors + 12, vectors + 24));
        for (uint i = 0; i < 9; i++)
        {
            Assert.Equal(0f, GuestMemory.Read<float>(vectors + i * 4));
        }
    }

    [Fact]
    public void InternalClientStateChecksThePointerBeforeItsSampling()
    {
        Sensors motion = Motion();
        uint state = _harness.Alloc(0xF8);
        Assert.Equal(0, Call(motion, "sceMotionStartSampling")); // the program's switch is not this client's
        Assert.Equal(NullParameter, Call(motion, "MotionInternalGetState", 0u));
        Assert.Equal(NotSampling, Call(motion, "MotionInternalGetState", state));
        Assert.Equal(0, Call(motion, "MotionInternalStart"));
        Assert.Equal(0, Call(motion, "MotionInternalGetState", state));
        Assert.Equal(-1f, GuestMemory.Read<float>(state + 12));
    }

    [Fact]
    public void LocationClientReadsNeedItsOwnSampling()
    {
        Sensors motion = Motion();
        uint state = _harness.Alloc(0xF8);
        uint states = _harness.Alloc(64 * 0x40);
        Assert.Equal(0, Call(motion, "sceMotionStartSampling"));
        Assert.Equal(NotSampling, Call(motion, "sceMotionGetStateInternal", state));
        Assert.Equal(NotSampling, Call(motion, "MotionLocationGetSensorState", states, 1));

        Assert.Equal(0, Call(motion, "MotionLocationStart"));
        Assert.Equal(NullParameter, Call(motion, "sceMotionGetStateInternal", 0u));
        Assert.Equal(0, Call(motion, "sceMotionGetStateInternal", state));
        // the pointer before the count
        Assert.Equal(NullParameter, Call(motion, "MotionLocationGetSensorState", 0u, 0));
        Assert.Equal(OutOfBounds, Call(motion, "MotionLocationGetSensorState", states, 0));
        Assert.Equal(OutOfBounds, Call(motion, "MotionLocationGetSensorState", states, 65));
        Assert.Equal(0, Call(motion, "MotionLocationGetSensorState", states, 64));
        Assert.Equal(-1f, GuestMemory.Read<float>(states + 63 * 0x40 + 8));
    }
}
