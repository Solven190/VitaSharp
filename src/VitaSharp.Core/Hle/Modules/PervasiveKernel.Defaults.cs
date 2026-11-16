using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// ScePervasiveForDriver's SoC identification getters (the misc words the module reads at start; the values of a
/// real unit are not documented, so the zeroed misc page answers: defaults with a log) and the UART baud rate setter
/// (it writes the divider word of the rate into the UART clock page 0xE3105000). The exports that hand their change
/// to the secure monitor (smc 0x110..0x114) are out of the project's scope (docs/roadmap.md).
/// </summary>
public sealed partial class PervasiveKernel
{
    const uint UartClockPage = 0xE3105000 - PhysicalBase;

    /// <summary>The rates kscePervasiveUartSetBaudrate accepts and the clock word it writes for each (mode &lt;&lt; 16 |
    /// divider of the 48 MHz UART clock: 16x oversampling in mode 1, 4x in mode 2).</summary>
    static readonly (uint Rate, uint Clock)[] s_uartRates =
    [
        (300, 0x12710),
        (600, 0x11388),
        (1200, 0x109C4),
        (2400, 0x104E2),
        (4800, 0x10271),
        (9600, 0x10139),
        (14400, 0x100D0),
        (19200, 0x1009C),
        (28800, 0x10068),
        (38400, 0x1004E),
        (57600, 0x10034),
        (115200, 0x1001A),
        (230400, 0x1000D),
        (250000, 0x1000C),
        (460800, 0x2001A),
        (921600, 0x2000D),
        (3000000, 0x10001),
    ];

    /// <summary>An identification word of the misc page, as the module read it at start (0: no documented value).</summary>
    uint Identification(string name, uint register)
    {
        uint value = ReadRegister(MiscPage + register);
        Log.WarnOnce($"pervasive-id-{register:X}", $"ScePervasive: {name} answers misc word 0x{register:X} = 0x{value:X} (no documented value of a real unit)");
        return value;
    }

    /// <summary>kscePervasiveUartSetBaudrate(port, rate): UART port 0..6's clock word (0xE3105000 + 4 * port) for one
    /// of the accepted rates; 0x803F0000 for another port or rate.</summary>
    [Hle(Nid = 0x8BAB45F8, Library = "ScePervasiveForDriver")]
    int kscePervasiveUartSetBaudrate(uint port, uint rate)
    {
        if (port > 6)
        {
            return InvalidArgument;
        }

        foreach ((uint accepted, uint clock) in s_uartRates)
        {
            if (accepted == rate)
            {
                lock (_registerLock)
                {
                    WriteRegister(UartClockPage + port * 4, clock);
                }

                return 0;
            }
        }

        return InvalidArgument;
    }

    /// <summary>kscePervasiveGetSoCRevision(): misc word 0 as read at start.</summary>
    [Hle(Nid = 0x714EEFB7, Library = "ScePervasiveForDriver", ReturnsValue = true, Default = true)]
    uint kscePervasiveGetSoCRevision() => Identification("kscePervasiveGetSoCRevision", 0);

    /// <summary>NID_41C3C299(): misc word 4 as read at start.</summary>
    [Hle(Nid = 0x41C3C299, Library = "ScePervasiveForDriver", ReturnsValue = true, Default = true)]
    uint NID_41C3C299() => Identification("NID_41C3C299", 4);

    /// <summary>NID_FB0EF8C7(): misc word 0x300 as read at start.</summary>
    [Hle(Nid = 0xFB0EF8C7, Library = "ScePervasiveForDriver", ReturnsValue = true, Default = true)]
    uint NID_FB0EF8C7() => Identification("NID_FB0EF8C7", 0x300);

    /// <summary>NID_443E2A3F(): misc word 0x304 as read at start.</summary>
    [Hle(Nid = 0x443E2A3F, Library = "ScePervasiveForDriver", ReturnsValue = true, Default = true)]
    uint NID_443E2A3F() => Identification("NID_443E2A3F", 0x304);

    /// <summary>NID_F58EF3A5(): misc word 0x308 as read at start.</summary>
    [Hle(Nid = 0xF58EF3A5, Library = "ScePervasiveForDriver", ReturnsValue = true, Default = true)]
    uint NID_F58EF3A5() => Identification("NID_F58EF3A5", 0x308);
}
