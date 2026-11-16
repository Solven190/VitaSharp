namespace VitaSharp.Core.Hle.Modules;

// libgxm_es4 3.74's notification region: sceGxmInitialize allocates one 0x820-byte shared block (SceGpuEs4User
// memory, cleared) and keeps its CPU address; the public sceGxmGetNotificationRegion gives block + 0x20 and the two
// unnamed SceGxmInternal entries the block itself and block + 0x10. All three answer NULL while GXM is not initialized.
public sealed partial class Gxm
{
    /// <summary>Offset of the public notification region in the block (sceGxmGetNotificationRegion).</summary>
    const uint NotificationRegionOffset = 0x20;

    /// <summary>The block address, or 0 while GXM is not initialized.</summary>
    uint NotificationBlock => _initialized ? _notificationRegion : 0;

    /// <summary>sceGxmGetNotificationRegion(): the public notification words, block + 0x20.</summary>
    [Hle(ReturnsValue = true)]
    uint sceGxmGetNotificationRegion()
    {
        return NotificationBlock == 0 ? 0 : NotificationBlock + NotificationRegionOffset;
    }

    /// <summary>NID_9F7503E8 (SceGxmInternal)(): the notification block itself.</summary>
    [Hle("NID_9F7503E8", Nid = 0x9F7503E8, Library = "SceGxmInternal", ReturnsValue = true)]
    uint GxmNotificationBlock()
    {
        return NotificationBlock;
    }

    /// <summary>NID_338E3186 (SceGxmInternal)(): block + 0x10.</summary>
    [Hle("NID_338E3186", Nid = 0x338E3186, Library = "SceGxmInternal", ReturnsValue = true)]
    uint GxmNotificationBlockSecondHalf()
    {
        return NotificationBlock == 0 ? 0 : NotificationBlock + 0x10;
    }
}
