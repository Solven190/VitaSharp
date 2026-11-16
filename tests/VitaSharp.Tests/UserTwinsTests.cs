using System.Reflection;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// The user twins (<see cref="UserTwins"/>): exports the FW 3.74 firmware builds from the same function as another
/// export of their module (the pairs below, from the decompiled modules, compared instruction by instruction). Every
/// pair is bound and forwards to its other NID, and one twin per library is run next to the NID it forwards to on the
/// same registers and memory (result and what it wrote).
/// </summary>
[Collection("guest")]
public sealed class UserTwinsTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint BufferSize = 0x400;

    private readonly HleHarness _harness = new(fixture, "utwins");

    public void Dispose() => _harness.Dispose();

    /// <summary>The firmware's pairs: twin NID -> the export with the same body (libkernel 0x58DDAC4F: its import stub
    /// of sceKernelDeleteThread).</summary>
    private static readonly Dictionary<uint, uint> s_pairs = new()
    {
        [0x08E8D89D] = 0x513940B4,
        [0x16268321] = 0x5AD6CBE8,
        [0x188B27D3] = 0x8B4BEA99,
        [0x2C3EF0F5] = 0xA200696D,
        [0x3449647C] = 0xD3BCDADD,
        [0x398B0940] = 0x3E8B077C,
        [0x3D49ABFA] = 0x8B4BEA99,
        [0x4121F2A1] = 0x8B4BEA99,
        [0x5541AF3C] = 0x5730F4A4,
        [0x58DDAC4F] = 0x1BBDE3D9,
        [0x592C3D4A] = 0x8B4BEA99,
        [0x61634358] = 0x838F1DAB,
        [0x6F9C4CC1] = 0xA7819967,
        [0x7575795A] = 0xA200696D,
        [0x766E2AE1] = 0x8B2FEFAA,
        [0x791774B7] = 0x1A53A505,
        [0x87D67D0B] = 0x838F1DAB,
        [0x8A804C00] = 0xD0831EFE,
        [0x8D4FDF78] = 0x8B4BEA99,
        [0x935C25D1] = 0x3E8B077C,
        [0x94C1EA04] = 0xD0831EFE,
        [0x9CF6BDA3] = 0x7D2A014E,
        [0x9FEE1E0E] = 0xC1542070,
        [0xA3A80BFA] = 0xD0831EFE,
        [0xA62BBCE3] = 0x3E8B077C,
        [0xA7F4F4AB] = 0x777FE4C2,
        [0xB0122008] = 0xE99EE305,
        [0xBBAD980D] = 0xE8E8DF18,
        [0xC6DB0740] = 0x4535A358,
        [0xC81AD91B] = 0x27932A1B,
        [0xD494B4CE] = 0x8B4BEA99,
        [0xD7ED457F] = 0xD3BCDADD,
        [0xE03981D6] = 0xAB1462C5,
        [0xEC0BD12F] = 0x1A53A505,
        [0xFC8B048C] = 0xD7B43E47,
    };

    /// <summary>The NID a twin forwards to: the constant loaded right before its call to Forward (matched by the
    /// method token, so a coverage run's instrumentation calls ahead of it do not count).</summary>
    private static uint ForwardedNid(MethodInfo twin)
    {
        MethodInfo forward = typeof(UserTwins).GetMethod("Forward", BindingFlags.Instance | BindingFlags.NonPublic)!;
        byte[] il = twin.GetMethodBody()!.GetILAsByteArray()!;
        for (int i = 5; i + 5 <= il.Length; i++)
        {
            if (il[i] == 0x28 && il[i - 5] == 0x20 && BitConverter.ToInt32(il, i + 1) == forward.MetadataToken)
            {
                return BitConverter.ToUInt32(il, i - 4);
            }
        }

        throw new InvalidOperationException($"{twin.Name} has no Forward call with a constant");
    }

    [Fact]
    public void EveryFirmwarePairIsBoundAndForwardsToItsOtherNid()
    {
        var found = new Dictionary<uint, uint>();
        foreach (MethodInfo twin in typeof(UserTwins).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            HleAttribute? attribute = twin.GetCustomAttribute<HleAttribute>();
            if (attribute == null)
            {
                continue;
            }

            found[attribute.Nid] = ForwardedNid(twin);
        }

        Assert.Equal(s_pairs.OrderBy(p => p.Key), found.OrderBy(p => p.Key));
        var unbound = new List<string>();
        foreach ((uint twin, uint target) in s_pairs)
        {
            if (!_harness.Emu.Hle.IsImplemented(twin) || !_harness.Emu.Hle.IsImplemented(target))
            {
                unbound.Add($"0x{twin:X8} -> 0x{target:X8}");
            }
        }

        Assert.True(unbound.Count == 0, string.Join("\n", unbound));
    }

    /// <summary>One twin per library and the registers it runs with (buffer = a 0x400-byte guest buffer, refilled with
    /// the same bytes before each run).</summary>
    private static readonly (uint Twin, Func<uint, uint[]> Args)[] s_samples =
    [
        (0x58DDAC4F, _ => [0x7FFFFFF1]),
        (0x6F9C4CC1, _ => [0, 1, 0]),
        (0xBBAD980D, b => [b]),
        (0xC6DB0740, _ => [0]),
        (0x61634358, b => [b, b + 0x200]),
        (0x8D4FDF78, _ => [0]),
    ];

    [Fact]
    public void OneTwinPerLibraryMatchesTheNidItForwardsTo()
    {
        uint buffer = _harness.Alloc(BufferSize);
        byte[] pattern = Enumerable.Range(0, (int)BufferSize).Select(i => (byte)(i * 7 + 1)).ToArray();
        _harness.OnGuest(thread =>
        {
            foreach ((uint twin, Func<uint, uint[]> args) in s_samples)
            {
                (uint, uint, byte[]) viaTwin = Run(thread, twin, args(buffer), buffer, pattern);
                (uint, uint, byte[]) viaTarget = Run(thread, s_pairs[twin], args(buffer), buffer, pattern);
                Assert.True(viaTwin.Item1 == viaTarget.Item1, $"0x{twin:X8}: r0 0x{viaTwin.Item1:X8} != 0x{viaTarget.Item1:X8}");
                Assert.True(viaTwin.Item3.AsSpan().SequenceEqual(viaTarget.Item3), $"0x{twin:X8}: wrote different memory");
            }
        });
    }

    private (uint R0, uint R1, byte[] Memory) Run(VitaSharp.Core.Kernel.GuestThread thread, uint nid, uint[] args, uint buffer, byte[] pattern)
    {
        pattern.CopyTo(GuestMemory.Span(buffer, pattern.Length));
        Action<HleContext> implementation = _harness.Emu.Hle.ImplOf(nid) ?? throw new InvalidOperationException($"0x{nid:X8} not implemented");
        unsafe
        {
            for (int i = 0; i < 4; i++)
            {
                thread.Core.State->R[i] = i < args.Length ? args[i] : 0;
            }

            implementation(thread.Hle);
            return (thread.Core.State->R[0], thread.Core.State->R[1], GuestMemory.Span(buffer, pattern.Length).ToArray());
        }
    }
}
