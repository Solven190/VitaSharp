using System.Buffers;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Gpu;

/// <summary>
/// A display-release hold of the GPU worker: a scene into a colour buffer still shown (or queued) by the display waits
/// for the flip away from it (its fragment sync object, <c>sceGxmBeginScene</c>). On the console only the fragment
/// phase waits: the vertex phase (index and vertex stream fetch, vertex uniform buffers) runs as soon as the scene is
/// kicked (<c>sceGxmEndScene</c> / <c>sceGxmMidSceneFlush</c>), and the game may reuse that memory right after (vita2d
/// resets its vertex pool at the start of every frame). The worker runs both phases of a draw at once, so while it is
/// held every draw queued behind the hold would read its geometry late, after the game rewrote it; those draws get a
/// <see cref="GeometrySnapshot"/> at the kick instead.
/// </summary>
public sealed class DisplayHold
{
    private volatile bool _done;

    /// <summary>The worker passed the hold (the buffer was released, or the wait gave up).</summary>
    public bool Done => _done;

    /// <summary>GPU worker, after the wait: from here on the draws behind the hold read guest memory themselves. Waits for
    /// a snapshot the guest is taking under <see cref="Lock"/>, so no draw behind the hold runs before it is complete.</summary>
    public void Release()
    {
        lock (Lock)
        {
            _done = true;
        }
    }

    /// <summary>Held by the guest while it snapshots the draws behind the hold, and by <see cref="Release"/>.</summary>
    public readonly object Lock = new();
}

/// <summary>
/// The geometry of the draws of a kicked scene that the GPU worker had not run yet because it was held
/// (<see cref="DisplayHold"/>): the guest memory ranges they read (index buffers and vertex streams), copied at the
/// kick. While the worker executes one of those draws, <see cref="GuestMemory.ReadSpan(uint, int)"/> serves reads that
/// lie inside a range from the copy (<see cref="Current"/>); other reads go to guest memory as usual.
/// </summary>
public sealed class GeometrySnapshot
{
    /// <summary>Upper bound of the bytes one snapshot copies; a larger scene is left to read guest memory (logged once).</summary>
    public const int MaxBytes = 64 << 20;

    /// <summary>Gaps up to this size between two ranges are copied too, so neighbouring draws share one range.</summary>
    private const uint MergeGap = 4096;

    /// <summary>Snapshots alive (not yet released by all their draws): <see cref="GuestMemory.ReadSpan(uint, int)"/>
    /// looks at <see cref="Current"/> only while there are any.</summary>
    private static int s_alive;

    /// <summary>True while any snapshot is alive (one static read on the guest memory read path when none is).</summary>
    public static bool AnyAlive => Volatile.Read(ref s_alive) != 0;

    /// <summary>GPU worker: the snapshot of the draw being executed (null outside one).</summary>
    [ThreadStatic]
    private static GeometrySnapshot? s_current;

    public static GeometrySnapshot? Current
    {
        get => s_current;
        set => s_current = value;
    }

    private readonly uint[] _starts, _ends;
    private readonly int[] _offsets;
    private readonly byte[] _data;
    private int _references;

    private GeometrySnapshot(uint[] starts, uint[] ends, int[] offsets, byte[] data, int references)
    {
        _starts = starts;
        _ends = ends;
        _offsets = offsets;
        _data = data;
        _references = references;
        Interlocked.Increment(ref s_alive);
    }

    /// <summary>The ranges copied (tests and traces).</summary>
    public int RangeCount => _starts.Length;

    /// <summary>The copy of [<paramref name="addr"/>, +<paramref name="length"/>) when the snapshot holds all of it.</summary>
    public bool TryGet(uint addr, long length, out ReadOnlySpan<byte> data)
    {
        ulong end = (ulong)addr + (ulong)Math.Max(length, 0);
        int lo = 0, hi = _starts.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (_starts[mid] > addr)
            {
                hi = mid - 1;
            }
            else if (_ends[mid] <= addr)
            {
                lo = mid + 1;
            }
            else
            {
                if (end > _ends[mid])
                {
                    break;
                }

                data = new ReadOnlySpan<byte>(_data, _offsets[mid] + (int)(addr - _starts[mid]), (int)(end - addr));
                return true;
            }
        }
        data = default;
        return false;
    }

    /// <summary>True when the draw being executed on this thread reads [<paramref name="addr"/>, +<paramref name="length"/>)
    /// from its snapshot (renderers must not keep such a read in a cache keyed by guest memory pages).</summary>
    public static bool Serves(uint addr, long length) => AnyAlive && s_current is { } current && current.TryGet(addr, length, out _);

    /// <summary>One draw that used the snapshot was executed (or dropped); the last one gives the copy back.</summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _references) == 0)
        {
            ArrayPool<byte>.Shared.Return(_data);
            Interlocked.Decrement(ref s_alive);
        }
    }

    /// <summary>
    /// Guest thread, at the kick of a held scene: copies the index buffers and vertex streams of <paramref name="draws"/>
    /// and gives each draw the snapshot (<see cref="DrawCommand.Geometry"/>). The ranges are the ones a renderer reads:
    /// the indices, and for each stream the program reads, the vertices from the lowest to the highest index used (the
    /// instances, for an instanced stream), with room for the widest attribute. Null when there was nothing to copy, or
    /// more than <see cref="MaxBytes"/>.
    /// </summary>
    public static GeometrySnapshot? Take(List<DrawCommand> draws)
    {
        if (draws.Count == 0)
        {
            return null;
        }

        var ranges = new List<(uint Start, uint End)>(draws.Count * 2);
        Span<uint> attributeEnds = stackalloc uint[16];
        foreach (DrawCommand draw in draws)
        {
            AddRanges(draw, ranges, attributeEnds);
        }

        if (ranges.Count == 0)
        {
            return null;
        }

        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(uint Start, uint End)>(ranges.Count);
        foreach ((uint start, uint end) in ranges)
        {
            if (merged.Count > 0 && start <= merged[^1].End + MergeGap)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
            }
            else
            {
                merged.Add((start, end));
            }
        }

        long total = 0;
        foreach ((uint start, uint end) in merged)
        {
            total += end - start;
        }

        if (total > MaxBytes)
        {
            Log.WarnOnce("gxm-geometry-snapshot-size", $"[gpu] a held scene's geometry is {total / 1048576} MB: left in guest memory (the draws read it when the GPU worker gets to them)");
            return null;
        }

        byte[] data = ArrayPool<byte>.Shared.Rent((int)Math.Max(total, 1));
        uint[] starts = new uint[merged.Count], ends = new uint[merged.Count];
        int[] offsets = new int[merged.Count];
        int offset = 0;
        for (int i = 0; i < merged.Count; i++)
        {
            (uint start, uint end) = merged[i];
            starts[i] = start;
            ends[i] = end;
            offsets[i] = offset;
            GuestMemory.ReadSpan(start, (int)(end - start)).CopyTo(data.AsSpan(offset));
            offset += (int)(end - start);
        }

        var snapshot = new GeometrySnapshot(starts, ends, offsets, data, draws.Count);
        foreach (DrawCommand draw in draws)
        {
            draw.Geometry = snapshot;
        }

        return snapshot;
    }

    private static void AddRanges(DrawCommand draw, List<(uint Start, uint End)> ranges, Span<uint> attributeEnds)
    {
        if (draw.IndexAddress == 0 || draw.IndexCount == 0)
        {
            return;
        }

        uint indexBytes = draw.IndexCount * (draw.Index32 ? 4u : 2u);
        AddRange(ranges, draw.IndexAddress, indexBytes);
        uint minIndex = uint.MaxValue, maxIndex = 0;
        if (draw.Index32)
        {
            foreach (uint index in GuestMemory.ReadSpan<uint>(draw.IndexAddress, (int)draw.IndexCount))
            {
                minIndex = Math.Min(minIndex, index);
                maxIndex = Math.Max(maxIndex, index);
            }
        }
        else
        {
            foreach (ushort index in GuestMemory.ReadSpan<ushort>(draw.IndexAddress, (int)draw.IndexCount))
            {
                minIndex = Math.Min(minIndex, index);
                maxIndex = Math.Max(maxIndex, index);
            }
        }

        GxmVertexProgram program = draw.VertexProgram;
        attributeEnds.Clear();
        uint used = 0;
        foreach (GxmVertexAttribute attribute in program.Attributes)
        {
            if (attribute.StreamIndex >= 16)
            {
                continue;
            }

            // at most four bytes per component, a register (four components) at least: wider than any format reads
            uint bytes = 4u * Math.Max(4u, (attribute.ComponentCount + 3u) & ~3u);
            attributeEnds[attribute.StreamIndex] = Math.Max(attributeEnds[attribute.StreamIndex], attribute.Offset + bytes);
            used |= 1u << attribute.StreamIndex;
        }

        bool indexUsed = (program.Info.Program.Flags & Gxm.Shader.GxpProgram.FlagIndexUsed) != 0;
        for (int s = 0; s < 16 && s < program.Streams.Length; s++)
        {
            uint addr = draw.Streams[s];
            if ((used & (1u << s)) == 0 || addr == 0)
            {
                continue;
            }

            GxmVertexStream stream = program.Streams[s];
            bool instanced = stream.IndexSource is 2 or 3;
            uint first = instanced || indexUsed ? 0 : minIndex;
            uint last = instanced ? Math.Max(draw.InstanceCount, 1) - 1 : maxIndex;
            ulong start = addr + (ulong)first * stream.Stride;
            ulong length = (ulong)(last - first) * stream.Stride + attributeEnds[s] + 16;
            if (start + length <= uint.MaxValue)
            {
                AddRange(ranges, (uint)start, (uint)length);
            }
        }
    }

    private static void AddRange(List<(uint Start, uint End)> ranges, uint start, uint length)
    {
        if (length > 0 && (ulong)start + length <= uint.MaxValue)
        {
            ranges.Add((start, start + length));
        }
    }
}
