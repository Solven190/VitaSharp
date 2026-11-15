using Silk.NET.Vulkan;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// A scene rendering that continues on attachments an earlier rendering wrote (the scene resumed after a mid-scene
/// break: a region copy, a programmable-blending pass, a present, an idle submit) loads what that rendering stored, so
/// it must wait for those writes: Vulkan orders nothing between two render passes (or dynamic renderings) by itself,
/// and the implicit external dependency of a render pass has no access masks. Without it, the load of the next pass
/// on a tiler can read the colour or depth/stencil of the previous one before its store lands: on the Mali-G57
/// Uncharted's fog/light pass resumed on a half-stored light buffer and depth, and 5-9% of the frames showed the fog
/// brighter or the scene with the depth of another moment for one frame (the GPU bench replay of the dump flagged
/// 43 of 420 frames; 0 with a barrier after every mid-scene rendering). Desktop GPUs ran the same commands without
/// the barrier and happened to finish one pass before the next.
/// The images a rendering wrote are remembered until a dependency covers them; the next rendering that attaches one
/// of them records a memory barrier from attachment writes to attachment and input-attachment reads and writes
/// first. Renderings on other images (the next scene of another surface) are not held back by it.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    /// <summary>Attachment images written by renderings since the last attachment barrier.</summary>
    private readonly HashSet<ulong> _attachmentsWritten = new();
    private long _statAttachmentBarriers;

    /// <summary>Renderings that waited for an earlier rendering's writes to their attachments (tests, PERF gpu).</summary>
    public long AttachmentBarriers => _statAttachmentBarriers;

    /// <summary>A rendering on these images was recorded (default handles are ignored).</summary>
    private void NoteAttachmentsWritten(Image color, Image depth, Image word)
    {
        foreach (Image image in stackalloc Image[3] { color, depth, word })
        {
            if (image.Handle != 0)
            {
                _attachmentsWritten.Add(image.Handle);
            }
        }
    }

    /// <summary>Before a rendering on these images: waits for an earlier rendering's writes to any of them.</summary>
    private void SyncAttachmentsBeforeRendering(CommandBuffer cmd, Image color, Image depth, Image word)
    {
        if (_attachmentsWritten.Count == 0
            || !(_attachmentsWritten.Contains(color.Handle) || _attachmentsWritten.Contains(depth.Handle) || (word.Handle != 0 && _attachmentsWritten.Contains(word.Handle))))
        {
            return;
        }

        var memoryBarrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
            DstAccessMask = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentReadBit
                | AccessFlags.DepthStencilAttachmentWriteBit | AccessFlags.InputAttachmentReadBit,
        };
        _vk.CmdPipelineBarrier(cmd,
            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit | PipelineStageFlags.FragmentShaderBit,
            0, 1, &memoryBarrier, 0, null, 0, null);
        // a memory barrier covers every image: all earlier attachment writes are now ordered before what follows
        _attachmentsWritten.Clear();
        _statAttachmentBarriers++;
    }
}
