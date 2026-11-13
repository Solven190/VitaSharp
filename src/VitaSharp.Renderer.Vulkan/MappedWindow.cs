namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Where a stage's mapped-memory binding starts in the CDRAM mirror (VulkanRenderer.Mapped.cs). The mirror is 288 MB
/// (256 MB of CDRAM and 32 MB of scratch), and a storage-buffer descriptor may not be larger than the device's
/// maxStorageBufferRange (VUID-VkWriteDescriptorSet-descriptorType-00333; the Vulkan minimum is 128 MB, which
/// lavapipe and SwiftShader report; the Mali-G57 and desktop drivers report 2 GB or more). Where the whole mirror
/// fits, it is bound from offset 0 as before. Otherwise the binding is a window of at most maxStorageBufferRange
/// bytes that holds every range the stage reads, and the slot headers become offsets inside that window. Window
/// starts are rounded down to half the window so that a frame uses only a few distinct windows (each one is a
/// separate descriptor set).
/// </summary>
public static class MappedWindow
{
    /// <summary>One range the stage reads, as [Lo, Hi) byte offsets into the mirror.</summary>
    public readonly record struct Span(ulong Lo, ulong Hi);

    /// <summary>
    /// Picks the window for <paramref name="spans"/> in a buffer of <paramref name="bufferBytes"/> bytes. Returns
    /// false when they do not all fit in one window of <paramref name="maxRange"/> bytes (the caller relocates them);
    /// <paramref name="alignment"/> is the device's minStorageBufferOffsetAlignment (a power of two).
    /// </summary>
    public static bool TryPlace(ReadOnlySpan<Span> spans, ulong bufferBytes, ulong maxRange, ulong alignment, out ulong windowBase, out ulong windowRange)
    {
        if (bufferBytes <= maxRange)
        {
            windowBase = 0;
            windowRange = bufferBytes;
            return true;
        }

        ulong lo = ulong.MaxValue;
        ulong hi = 0;
        foreach (Span span in spans)
        {
            lo = Math.Min(lo, span.Lo);
            hi = Math.Max(hi, Math.Min(span.Hi, bufferBytes));
        }

        if (spans.Length == 0)
        {
            lo = 0;
            hi = 0;
        }

        ulong align = Math.Max(1, alignment);
        ulong step = Math.Max(align, maxRange / 2 / align * align);
        // the coarse start first (few distinct windows per frame), then the tightest aligned one
        return Fits(lo / step * step, hi, bufferBytes, maxRange, out windowBase, out windowRange)
               || Fits(lo / align * align, hi, bufferBytes, maxRange, out windowBase, out windowRange);
    }

    private static bool Fits(ulong start, ulong hi, ulong bufferBytes, ulong maxRange, out ulong windowBase, out ulong windowRange)
    {
        ulong range = Math.Min(maxRange, bufferBytes - start);
        if (hi <= start + range)
        {
            windowBase = start;
            windowRange = range;
            return true;
        }

        windowBase = 0;
        windowRange = 0;
        return false;
    }
}
