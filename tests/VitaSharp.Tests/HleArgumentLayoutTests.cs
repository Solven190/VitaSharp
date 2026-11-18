using VitaSharp.Core.Cpu;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// HLE functions called through their generated bindings with the registers and stack a guest call sets up (AAPCS-VFP),
/// so a C# signature whose argument order, count or width differs from the firmware's export fails here: the
/// arguments land in the wrong parameters and outputs are written at the wrong addresses.
/// </summary>
[Collection("guest")]
public sealed unsafe class HleArgumentLayoutTests(GuestMemoryFixture fixture)
{
    private const uint Sentinel = 0xA5A5A5A5;

    /// <summary>Guest register state for one call: core registers r0-r3, stack words from sp, VFP registers.</summary>
    private sealed class GuestCall
    {
        public readonly uint[] Core = new uint[4];
        public readonly uint[] Stack = new uint[8];
        public readonly float[] Single = new float[16];
        public readonly double[] Double = new double[8];
        public bool UseDouble;
    }

    /// <summary>Runs the binding named <paramref name="name"/> of <paramref name="module"/> on <paramref name="call"/>;
    /// returns r0 and d0 afterwards.</summary>
    private (uint R0, double D0) Run(HleModule module, string name, GuestCall call)
    {
        HleBinding binding = HleBindings.For(module.GetType())!.Single(b => (b.Name ?? b.Method) == name);
        uint stack = fixture.Memory.Alloc((uint)(call.Stack.Length * 4), "abi-stack");
        for (int i = 0; i < call.Stack.Length; i++)
        {
            GuestMemory.W32(stack + (uint)(i * 4), call.Stack[i]);
        }

        CpuState* state = CpuState.Allocate();
        try
        {
            for (int i = 0; i < 4; i++)
            {
                state->R[i] = call.Core[i];
            }

            state->R[13] = stack;
            if (call.UseDouble)
            {
                for (int i = 0; i < call.Double.Length; i++)
                {
                    ((double*)state->D)[i] = call.Double[i];
                }
            }
            else
            {
                for (int i = 0; i < call.Single.Length; i++)
                {
                    ((float*)state->D)[i] = call.Single[i];
                }
            }
            var context = new HleContext { Cpu = state };
            binding.Bind(module)(context);
            return (state->R[0], ((double*)state->D)[0]);
        }
        finally
        {
            CpuState.Free(state);
        }
    }

    private uint Alloc(uint size, string tag)
    {
        uint address = fixture.Memory.Alloc(size, tag);
        GuestMemory.Fill(address, 0, size);
        return address;
    }

    private uint Text(string text)
    {
        uint address = Alloc((uint)text.Length + 1, "abi-text");
        GuestMemory.WriteCString(address, text, text.Length + 1);
        return address;
    }

    private int AddOverlay(Fios2User fios, byte order, string destination, string source)
    {
        uint overlay = Alloc(0x258, "abi-overlay");
        GuestMemory.Write(overlay + 1, order);
        GuestMemory.WriteCString(overlay + 0x10, destination, 292);
        GuestMemory.WriteCString(overlay + 0x134, source, 292);
        uint id = Alloc(4, "abi-overlay-id");
        var call = new GuestCall();
        call.Core[0] = 0;
        call.Core[1] = overlay;
        call.Core[2] = id;
        Assert.Equal(0u, Run(fios, "sceFiosOverlayAddForProcess02", call).R0);
        return (int)GuestMemory.R32(id);
    }

    /// <summary>sceLocationStartHeadingCallback(handle, float difference, callback, userdata) (FW 3.74 SceLibLocation: the
    /// difference is a float, checked against 0-180, so it is in s0 and the callback in r1, the userdata in r2). Reading
    /// four core registers took the userdata (r2) as the callback: a null userdata was refused as a null callback.</summary>
    [Fact]
    public void LocationHeadingCallbackTakesTheDifferenceAsFloat()
    {
        var location = new Location();
        uint handleOut = Alloc(4, "abi-loc-handle");
        var open = new GuestCall();
        open.Core[0] = handleOut;
        open.Core[1] = 1;
        open.Core[2] = 1;
        Assert.Equal(0u, Run(location, "sceLocationOpen", open).R0);
        uint handle = GuestMemory.R32(handleOut);

        var call = new GuestCall();
        call.Core[0] = handle;
        call.Core[1] = 0x81000100; // callback
        call.Core[2] = 0;          // userdata may be null
        call.Core[3] = 0;
        call.Single[0] = 10f;
        Assert.Equal(Location.ERR_DISABLE_APPLICATION, (int)Run(location, "sceLocationStartHeadingCallback", call).R0);

        call.Core[1] = 0;          // a null callback, with userdata set
        call.Core[2] = 0x1234;
        Assert.Equal(Location.ERR_INVALID_ADDRESS, (int)Run(location, "sceLocationStartHeadingCallback", call).R0);

        call.Core[0] = 999;        // the callback is checked before the handle
        Assert.Equal(Location.ERR_INVALID_ADDRESS, (int)Run(location, "sceLocationStartHeadingCallback", call).R0);
        call.Core[1] = 0x81000100;
        Assert.Equal(Location.ERR_INVALID_HANDLE, (int)Run(location, "sceLocationStartHeadingCallback", call).R0);
    }

    /// <summary>sceFiosKernelOverlayResolveSync02(pid, resolve_for_write, in_path, opt) (FW 3.74 SceFios2Kernel02, the
    /// syscall behind driver_us's sceFiosOverlayResolveSync02): opt = { out_path, max_path, reserved, out_path_buffer_size }.
    /// The syscall copies exactly out_path_buffer_size bytes of a zeroed buffer holding the resolved path. Reading r3 as
    /// the output wrote the path over the option structure itself and took the stack word above as the maximum.</summary>
    [Fact]
    public void FiosKernel02ResolveTakesAnOptionStructure()
    {
        var fios = new Fios2User();
        AddOverlay(fios, 1, "app0:", "ux0:patch/ABCD00001");
        uint output = Alloc(0x40, "abi-resolve-out");
        GuestMemory.Span(output, 0x40).Fill(0xEE);
        uint option = Alloc(0x18, "abi-resolve-opt");
        GuestMemory.W32(option, output);
        GuestMemory.W32(option + 4, 0x400);
        GuestMemory.W32(option + 0xC, 0x30);
        var call = new GuestCall();
        call.Core[0] = 0;
        call.Core[1] = 0;
        call.Core[2] = Text("app0:data/level1.bin");
        call.Core[3] = option;
        call.Stack[0] = Sentinel;

        Assert.Equal(0u, Run(fios, "sceFiosKernelOverlayResolveSync02", call).R0);
        Assert.Equal("ux0:patch/ABCD00001/data/level1.bin", GuestMemory.ReadCString(output));
        Assert.All(GuestMemory.Span(output + 36, 0x30 - 36).ToArray(), b => Assert.Equal(0, b)); // zero padding copied
        Assert.Equal(0xEEEEEEEEu, GuestMemory.R32(output + 0x30)); // nothing past out_path_buffer_size
        Assert.Equal(output, GuestMemory.R32(option));               // the option structure is only read
        Assert.Equal(0x400u, GuestMemory.R32(option + 4));
        Assert.Equal(0x30u, GuestMemory.R32(option + 0xC));

        // max_path bounds the resolved text (terminator included)
        GuestMemory.W32(option + 4, 9);
        Assert.Equal(0u, Run(fios, "sceFiosKernelOverlayResolveSync02", call).R0);
        Assert.Equal("ux0:patc", GuestMemory.ReadCString(output));

        // a copy size above the syscall's 0x400-byte buffer is refused before anything is written
        GuestMemory.W32(option + 4, 0x400);
        GuestMemory.W32(option + 0xC, 0x401);
        GuestMemory.Span(output, 0x40).Fill(0xEE);
        Assert.Equal(0x8002710Bu, Run(fios, "sceFiosKernelOverlayResolveSync02", call).R0);
        Assert.Equal(0xEEEEEEEEu, GuestMemory.R32(output));

        // a null out_path resolves without output
        GuestMemory.W32(option, 0);
        GuestMemory.W32(option + 0xC, 0x30);
        Assert.Equal(0u, Run(fios, "sceFiosKernelOverlayResolveSync02", call).R0);
        Assert.Equal(0xEEEEEEEEu, GuestMemory.R32(output));
    }

    /// <summary>sceFiosKernelOverlayResolveWithRangeSync02(pid, resolve_for_write, in_path, opt): opt (0x1C) =
    /// { out_path, max_path, u8 min_order, u8 max_order, reserved, out_path_buffer_size (+0x10) }. Exactly one binding
    /// serves the name (an alias with the old layout used to sit next to an expanded-argument binding).</summary>
    [Fact]
    public void FiosKernel02ResolveWithRangeTakesAnOptionStructure()
    {
        var fios = new Fios2User();
        AddOverlay(fios, 0x10, "app0:", "ux0:low");
        Assert.Single(HleBindings.For(typeof(Fios2User))!, b => (b.Name ?? b.Method) == "sceFiosKernelOverlayResolveWithRangeSync02");
        uint output = Alloc(0x40, "abi-range-out");
        GuestMemory.Span(output, 0x40).Fill(0xEE);
        uint option = Alloc(0x1C, "abi-range-opt");
        GuestMemory.W32(option, output);
        GuestMemory.W32(option + 4, 0x400);
        GuestMemory.Write<byte>(option + 8, 0x20);
        GuestMemory.Write<byte>(option + 9, 0x7F);
        GuestMemory.W32(option + 0x10, 0x20);
        var call = new GuestCall();
        call.Core[2] = Text("app0:x.bin");
        call.Core[3] = option;
        call.Stack[0] = Sentinel;
        call.Stack[1] = Sentinel;
        call.Stack[2] = Sentinel;

        Assert.Equal(0u, Run(fios, "sceFiosKernelOverlayResolveWithRangeSync02", call).R0);
        Assert.Equal("app0:x.bin", GuestMemory.ReadCString(output)); // order 0x10 is outside 0x20-0x7F
        Assert.Equal(0xEEEEEEEEu, GuestMemory.R32(output + 0x20));
        GuestMemory.Write<byte>(option + 8, 0x00);
        Assert.Equal(0u, Run(fios, "sceFiosKernelOverlayResolveWithRangeSync02", call).R0);
        Assert.Equal("ux0:low/x.bin", GuestMemory.ReadCString(output));
        Assert.Equal(output, GuestMemory.R32(option));
    }

    /// <summary>sceFiosKernelOverlayGetList02(pid, min_order, max_order, opt): opt = { out_ids, max_ids, SceSize *actual_ids,
    /// out_ids_buffer_size }. out_ids gets exactly out_ids_buffer_size bytes (ids, then zeros), actual_ids the number of
    /// matches. Reading r3 as out_ids wrote the ids over the option structure.</summary>
    [Fact]
    public void FiosKernel02GetListTakesAnOptionStructure()
    {
        var fios = new Fios2User();
        int first = AddOverlay(fios, 2, "app0:a", "ux0:a");
        int second = AddOverlay(fios, 3, "app0:b", "ux0:b");
        AddOverlay(fios, 0x90, "app0:c", "ux0:c");
        uint ids = Alloc(0x20, "abi-list-ids");
        GuestMemory.Span(ids, 0x20).Fill(0xEE);
        uint actual = Alloc(4, "abi-list-actual");
        uint option = Alloc(0x18, "abi-list-opt");
        GuestMemory.W32(option, ids);
        GuestMemory.W32(option + 4, 1);
        GuestMemory.W32(option + 8, actual);
        GuestMemory.W32(option + 0xC, 0x10);
        var call = new GuestCall();
        call.Core[0] = 0;
        call.Core[1] = 0;
        call.Core[2] = 0x7F;
        call.Core[3] = option;
        call.Stack[0] = Sentinel;
        call.Stack[1] = Sentinel;

        Assert.Equal(0u, Run(fios, "sceFiosKernelOverlayGetList02", call).R0);
        Assert.Equal(2u, GuestMemory.R32(actual));        // every match is counted
        Assert.Equal((uint)first, GuestMemory.R32(ids));  // max_ids = 1: one id, then zeros
        Assert.Equal(0u, GuestMemory.R32(ids + 4));
        Assert.Equal(0u, GuestMemory.R32(ids + 0xC));
        Assert.Equal(0xEEEEEEEEu, GuestMemory.R32(ids + 0x10)); // nothing past out_ids_buffer_size
        Assert.Equal(ids, GuestMemory.R32(option));
        Assert.Equal(1u, GuestMemory.R32(option + 4));

        GuestMemory.W32(option + 4, 8);
        Assert.Equal(0u, Run(fios, "sceFiosKernelOverlayGetList02", call).R0);
        Assert.Equal((uint)second, GuestMemory.R32(ids + 4));
        GuestMemory.W32(option + 0xC, 0x201);
        Assert.Equal(0x8002710Bu, Run(fios, "sceFiosKernelOverlayGetList02", call).R0);
    }
}
