using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// What a parsed GXM program costs and how long it lives. The suite's renderer tests grew the test process by ~0.5 GB
/// each (11 GB for the 24 renderer tests): one of their programs had garbage in its table counts and was parsed into
/// lists of millions of zero entries, and the static program cache kept it (and every other program) after its
/// emulator was gone. A count never reaches past the program's own bytes, and the cache ends with the emulated system.
/// </summary>
[Collection("guest")]
public sealed class GxpProgramLifetimeTests(GuestMemoryFixture fixture)
{
    /// <summary>A 0x200-byte program whose five table counts are huge, with the tables near its end.</summary>
    private static byte[] ProgramWithGarbageCounts()
    {
        byte[] data = new byte[0x200];
        void W32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
        W32(0x08, (uint)data.Length);
        // count, then the self-relative offset of its table (the GXP header layout)
        W32(0x24, 0x7FFF_FFFF);
        W32(0x28, 0x1C0 - 0x28);  // parameters: 16 bytes each -> 4 fit
        W32(0x70, 0xFFFF_FFFF);
        W32(0x74, 0x1E0 - 0x74);  // literals: 8 bytes each -> 4 fit
        W32(0x78, 0x0100_0000);
        W32(0x7C, 0x1F0 - 0x7C);  // uniform buffers: 8 bytes each -> 2 fit
        W32(0x80, 0x0100_0000);
        W32(0x84, 0x1F8 - 0x84);  // dependent samplers: 4 bytes each -> 2 fit
        W32(0x90, 0x00FF_FFFF);
        W32(0x94, 0x1E8 - 0x94);  // containers: 8 bytes each -> 3 fit
        return data;
    }

    [Fact]
    public void TableCountsNeverReachPastTheProgram()
    {
        var program = new GxpProgram(ProgramWithGarbageCounts(), 0x81000000);
        Assert.Equal(4, program.Parameters.Count);
        Assert.Equal(4, program.Literals.Count);
        Assert.Equal(2, program.UniformBuffers.Count);
        Assert.Equal(2, program.DependentSamplers.Count);
        Assert.Equal(3, program.Containers.Count);
        // a table that starts outside the program has no entries
        byte[] outside = new byte[0x100];
        BinaryPrimitives.WriteUInt32LittleEndian(outside.AsSpan(0x90), 10);
        BinaryPrimitives.WriteUInt32LittleEndian(outside.AsSpan(0x94), 0x4000);
        Assert.Empty(new GxpProgram(outside, 0x81000000).Containers);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference ParseInAnEmulatorThatEnds()
    {
        using var harness = new HleHarness(fixture, "gxp-lifetime");
        byte[] bytes = ProgramWithGarbageCounts();
        uint address = harness.Alloc((uint)bytes.Length);
        bytes.CopyTo(GuestMemory.Span(address, bytes.Length));
        var info = GxmProgramInfo.Get(address);
        Assert.Same(info, GxmProgramInfo.Get(address)); // cached while the system runs
        Assert.True(GxmProgramInfo.CachedCount > 0);
        return new WeakReference(info);
    }

    [Fact]
    public void TheProgramCacheEndsWithTheEmulatedSystem()
    {
        WeakReference info = ParseInAnEmulatorThatEnds();
        Assert.Equal(0, GxmProgramInfo.CachedCount);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(info.IsAlive, "a parsed program outlived its emulator");
    }
}
