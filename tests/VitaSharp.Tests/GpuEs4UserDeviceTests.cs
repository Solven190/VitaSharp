using System.Buffers.Binary;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The rest of SceGpuEs4User as libgpu_es4.suprx 3.74 has it: the device-data bridge-call wrappers (their checks, then
/// the driver's refusal before its SGX init server ran), libgpu's heap and its USSE/PDS code writers, the two template
/// writers reading the firmware module's code next to their export (a synthetic module here). Found by name so the
/// proof without them still builds.
/// </summary>
[Collection("guest")]
public sealed class GpuEs4UserDeviceTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int InvalidParams = 3;
    private const int BridgeCallFailed = 0x22;

    private readonly HleHarness _harness = new(fixture, "gpu-es4-user-device");

    public void Dispose() => _harness.Dispose();

    private GpuEs4 Gpu => _harness.Module<GpuEs4>();

    /// <summary>The module's stand-in for the firmware module's bytes (internal; set by reflection so the proof
    /// without the change still builds).</summary>
    private void SetCodeSource(Func<uint, int, int, byte[]?> source)
    {
        System.Reflection.FieldInfo field = typeof(GpuEs4).GetField("LibgpuCodeSource", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(GpuEs4), "LibgpuCodeSource");
        field.SetValue(Gpu, source);
    }

    private static byte[]? ExportRelativeBytes(byte[] elf, uint nid, int offset, int length)
    {
        System.Reflection.MethodInfo method = typeof(GpuEs4).GetMethod("ExportRelativeBytes", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(GpuEs4), "ExportRelativeBytes");
        return (byte[]?)method.Invoke(null, [elf, nid, offset, length]);
    }

    [Theory]
    [InlineData(0x804BA13Du, true)]
    [InlineData(0x83D972DFu, true)]
    [InlineData(0x8A997DBBu, true)]
    [InlineData(0x8ED7D698u, true)]
    [InlineData(0x94286660u, true)]
    [InlineData(0x9D5C4326u, true)]
    [InlineData(0xAD20D1A0u, true)]
    [InlineData(0xB000CA65u, true)]
    [InlineData(0xB26DF3D2u, true)]
    [InlineData(0xB6CFD213u, true)]
    [InlineData(0xB9178277u, true)]
    [InlineData(0xBDE2B9E1u, true)]
    [InlineData(0xD90B0707u, true)]
    [InlineData(0xF0CCE9DBu, true)]
    [InlineData(0x1A575DFDu, false)]
    [InlineData(0x1BE5985Au, false)]
    [InlineData(0x7AF40138u, false)]
    [InlineData(0x53CACDABu, false)]
    [InlineData(0x86FC3202u, false)]
    [InlineData(0xA5E331FAu, false)]
    [InlineData(0xE98914E4u, false)]
    public void TheExportsHaveAnEntry(uint nid, bool isDefault)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Nid == nid).ToList();
        Assert.NotEmpty(bindings);
        Assert.All(bindings, b => Assert.Equal(isDefault, b.Default));
        Assert.NotNull(_harness.Emu.Hle.ImplOf(nid));
    }

    [Fact]
    public void TheDeviceCallsAreRefusedAfterTheirChecks()
    {
        uint output = _harness.Word(0);
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4UserConnect", output, 0u));
        uint device = _harness.Alloc(8);
        GuestMemory.W32(device, GuestMemory.R32(output));
        GuestMemory.W32(device + 4, 0x1234);
        uint words = _harness.Alloc(0x300);
        uint result = _harness.Word(0xAAAAAAAA);
        uint result2 = _harness.Word(0xAAAAAAAA);
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4User804BA13D", device, 1u, result, result2));
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4User83D972DF", device, 1u, words));
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4User8A997DBB", device, words, result, result2));
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4User8ED7D698", device, 1u, 2u));
        Assert.Equal(InvalidParams, Call(Gpu, "GpuEs4User94286660", device, 0u));
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4User94286660", device, result));
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4User9D5C4326", device));
        Assert.Equal(InvalidParams, Call(Gpu, "GpuEs4UserAD20D1A0", device, 0u, 0u, 0u));
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4UserAD20D1A0", device, 1u, 0u, 0u));
        Assert.Equal(InvalidParams, Call(Gpu, "GpuEs4UserB000CA65", 0u, words));
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4UserB000CA65", device, words));
        Assert.Equal(InvalidParams, Call(Gpu, "GpuEs4UserB26DF3D2", device, 0u));
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4UserB26DF3D2", device, 1u));
        uint memory = _harness.Alloc(0x1C);
        GuestMemory.W32(memory + 4, 0x5555);
        Assert.Equal(InvalidParams, Call(Gpu, "GpuEs4UserB6CFD213", device, 0u));
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4UserB6CFD213", device, memory));
        Assert.Equal(0u, GuestMemory.R32(memory + 4)); // cleared before the call
        Assert.Equal(InvalidParams, Call(Gpu, "GpuEs4UserB9178277", 0u, 1u));
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4UserB9178277", device, 1u));
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4UserBDE2B9E1", device, 1u, words, result));
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4UserD90B0707", device, 1u, 2u));
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4UserF0CCE9DB", device, words, words));
        Assert.Equal(0xAAAAAAAAu, GuestMemory.R32(result));
        Assert.Equal(0xAAAAAAAAu, GuestMemory.R32(result2));
    }

    [Fact]
    public void TheHeapHandsOutAndTakesBackItsBlocks()
    {
        Assert.Equal(0u, Convert.ToUInt32(Invoke(Gpu, "GpuEs4UserHeapAlloc", 0u)));
        uint block = Convert.ToUInt32(Invoke(Gpu, "GpuEs4UserHeapAlloc", 0x1Cu));
        Assert.NotEqual(0u, block);
        Invoke(Gpu, "GpuEs4UserHeapFree", block);
        Invoke(Gpu, "GpuEs4UserHeapFree", block); // not the heap's any more: nothing
    }

    [Fact]
    public void TheCodeWritersBuildTheirWords()
    {
        uint code = _harness.Alloc(0x80);
        Assert.Equal(code + 8, Convert.ToUInt32(Invoke(Gpu, "GpuEs4UserWriteNop", code)));
        Assert.Equal(0u, GuestMemory.R32(code));
        Assert.Equal(0xFA4C0700u, GuestMemory.R32(code + 4));

        uint source = _harness.Alloc(24);
        GuestMemory.W32(source, 0xFFFFFFFF);
        GuestMemory.W32(source + 4, 0x12345678);
        Assert.Equal(code + 0x40, Convert.ToUInt32(Invoke(Gpu, "GpuEs4UserWriteLoads", code, source, 0x3FFFu)));
        Assert.Equal(0xFA440700u, GuestMemory.R32(code + 4));
        Assert.Equal(0x001FFFFFu, GuestMemory.R32(code + 8));
        Assert.Equal(0xFCA3F1F0u, GuestMemory.R32(code + 12));
        Assert.Equal(0x00345678u, GuestMemory.R32(code + 16));
        Assert.Equal(0xFCA04110u, GuestMemory.R32(code + 20));
        Assert.Equal(0x00A00000u, GuestMemory.R32(code + 48)); // slot 5 of a zero word
        Assert.Equal(0x0FE00082u, GuestMemory.R32(code + 0x38));
        Assert.Equal(0xFBE401F8u, GuestMemory.R32(code + 0x3C));

        uint state = _harness.Alloc(0x30);
        for (uint i = 0; i < 12; i++)
        {
            GuestMemory.W32(state + i * 4, 0x100 + i);
        }

        uint buffer = _harness.Alloc(0x80) + 4;
        uint program = (buffer + 15) & ~15u;
        GuestMemory.W32(program + 12, 0x77777777);
        Assert.Equal(program + 0x48, Convert.ToUInt32(Invoke(Gpu, "GpuEs4UserWriteProgram", state, buffer)));
        Assert.Equal(program, GuestMemory.R32(state));
        Assert.Equal(0x30u, GuestMemory.R32(state + 4));
        Assert.Equal(0x2000u, GuestMemory.R32(program));
        Assert.Equal(0x104u, GuestMemory.R32(program + 8));
        Assert.Equal(0x77777777u, GuestMemory.R32(program + 12)); // left as it was
        Assert.Equal(0x105u, GuestMemory.R32(program + 16));
        Assert.Equal(0x102u, GuestMemory.R32(program + 32));
        Assert.Equal(0xAF000000u, GuestMemory.R32(program + 68));
    }

    [Fact]
    public void TheTemplateWritersCopyTheModulesCodeNextToTheExport()
    {
        var asked = new List<(uint Nid, int Offset, int Length)>();
        SetCodeSource((nid, offset, length) =>
        {
            asked.Add((nid, offset, length));
            return Enumerable.Range(0, length).Select(i => (byte)(offset + i)).ToArray();
        });
        uint code = _harness.Alloc(0x40);
        Assert.Equal(code + 0x20, Convert.ToUInt32(Invoke(Gpu, "GpuEs4UserWriteTemplate", code)));
        Assert.Equal((0x86FC3202u, 0x20, 0x20), asked[^1]);
        Assert.Equal(0x20u, (uint)GuestMemory.Read<byte>(code));

        uint state = _harness.Alloc(0x30);
        for (uint i = 0; i < 12; i++)
        {
            GuestMemory.W32(state + i * 4, 0x100 + i);
        }

        uint program = _harness.Alloc(0xA0) + 4;
        Assert.Equal(program + 0x90, Convert.ToUInt32(Invoke(Gpu, "GpuEs4UserWriteProgramTemplate", state, program, 0, 0, 2)));
        Assert.Equal(0x950, asked[^1].Offset);
        Assert.Equal(0x30u, GuestMemory.R32(state + 4));
        Assert.Equal((program + 15) & ~15u, GuestMemory.R32(state));
        Assert.Equal(0x102u, GuestMemory.R32(program));
        Assert.Equal(0x108u, GuestMemory.R32(program + 8));
        Assert.Equal(0x10Au, GuestMemory.R32(program + 36));
        Assert.Equal((byte)0x80, GuestMemory.Read<byte>(program + 0x30)); // template byte

        Invoke(Gpu, "GpuEs4UserWriteProgramTemplate", state, program, 0, 1, 2);
        Assert.Equal(0xA48, asked[^1].Offset);
        Invoke(Gpu, "GpuEs4UserWriteProgramTemplate", state, program, 0, 1, 1);
        Assert.Equal(0xB40, asked[^1].Offset);
        Invoke(Gpu, "GpuEs4UserWriteProgramTemplate", state, program, 1, 0, 2);
        Assert.Equal(0x9B8, asked[^1].Offset);
        Assert.Equal(0x40u, GuestMemory.R32(state + 4));
        Assert.Equal(0x10Au, GuestMemory.R32(program + 48));
        Invoke(Gpu, "GpuEs4UserWriteProgramTemplate", state, program, 1, 0, 1);
        Assert.Equal(0xAB0, asked[^1].Offset);

        SetCodeSource((_, _, _) => null); // no module: zeros
        GuestMemory.Fill(code, 0xAA, 0x20);
        Invoke(Gpu, "GpuEs4UserWriteTemplate", code);
        Assert.Equal(0u, GuestMemory.R32(code));
    }

    [Fact]
    public void ExportRelativeBytesFollowTheModuleInfoExports()
    {
        byte[] elf = new byte[0x400];
        void W32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(offset), value);
        void W16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(offset), value);
        W32(0x18, 0x80); // e_entry: sce_module_info at the first segment's +0x80
        W32(0x1C, 0x34);
        W16(0x2A, 0x20);
        W16(0x2C, 1);
        W32(0x34 + 4, 0x100); // p_offset
        W32(0x34 + 8, 0x81000000); // p_vaddr
        W32(0x34 + 0x10, 0x300); // p_filesz
        W32(0x180 + 0x24, 0x10); // exports top / end
        W32(0x180 + 0x28, 0x30);
        W16(0x110, 0x20);
        W16(0x116, 1);
        W32(0x110 + 0x18, 0x81000040);
        W32(0x110 + 0x1C, 0x81000044);
        W32(0x140, 0xE98914E4);
        W32(0x144, 0x81000201); // Thumb
        elf[0x310] = 0x5A;
        byte[]? bytes = ExportRelativeBytes(elf, 0xE98914E4, 0x10, 4);
        Assert.NotNull(bytes);
        Assert.Equal(0x5A, bytes[0]);
        Assert.Null(ExportRelativeBytes(elf, 0x12345678, 0x10, 4));
        Assert.Null(ExportRelativeBytes(elf, 0xE98914E4, 0x400, 4)); // outside the module
    }
}
