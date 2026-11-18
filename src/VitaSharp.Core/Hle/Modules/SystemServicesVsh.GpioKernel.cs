using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceGpioForDriver (os0:kd/lowio.skprx 3.74, read in the module, study only): the two GPIO banks (bus 0..1, else
// 0x803F0100) of 32 ports each (else 0x803F0101). Each bank has a direction register (the module keeps a copy: a clear
// bit is an output, mode 1), an output latch written through set/clear registers, the input levels, and two bits of
// interrupt mode per port (0..3, else 0x803F0102). A read of an output port gives its latch, of an input port the pin.
// Nothing drives the emulated unit's pins and no GPIO interrupt is ever raised: inputs read 0 and no interrupt is
// pending.
public sealed partial class SystemServicesVsh
{
    const int GpioBuses = 2, GpioPorts = 32;

    /// <summary>SceGpio: a bus above 1 (0x803F0100).</summary>
    const int GPIO_ERR_BUS = unchecked((int)0x803F0100);

    /// <summary>SceGpio: a port above 31 (0x803F0101).</summary>
    const int GPIO_ERR_PORT = unchecked((int)0x803F0101);

    /// <summary>SceGpio: a mode out of range (0x803F0102).</summary>
    const int GPIO_ERR_MODE = unchecked((int)0x803F0102);

    [SnapshotMissing] // states of builds before it: every port an output, latches clear
    readonly uint[] _gpioDirection = new uint[GpioBuses];

    [SnapshotMissing]
    readonly uint[] _gpioLatch = new uint[GpioBuses];

    [SnapshotMissing]
    readonly ulong[] _gpioInterruptMode = new ulong[GpioBuses];

    /// <summary>ksceGpioSetPortMode(int bus, int port, int mode): 1 output, 0 input; the port's previous mode.</summary>
    [Hle(Nid = 0x372022A4, Library = "SceGpioForDriver")]
    int ksceGpioSetPortMode(uint bus, uint port, uint mode)
    {
        int check = GpioCheck(bus, port);
        if (check != 0)
        {
            return check;
        }

        if (mode >= 2)
        {
            return GPIO_ERR_MODE;
        }

        uint bit = 1u << (int)port;
        int previous = (_gpioDirection[bus] & bit) == 0 ? 1 : 0;
        _gpioDirection[bus] = (_gpioDirection[bus] & ~bit) | ((mode ^ 1) << (int)port);
        return previous;
    }

    /// <summary>ksceGpioGetPortMode(int bus, int port): 1 output, 0 input.</summary>
    [Hle(Nid = 0xC4CB78E2, Library = "SceGpioForDriver")]
    int ksceGpioGetPortMode(uint bus, uint port)
    {
        int check = GpioCheck(bus, port);
        if (check != 0)
        {
            return check;
        }

        return (_gpioDirection[bus] & (1u << (int)port)) == 0 ? 1 : 0;
    }

    /// <summary>ksceGpioPortSet(int bus, int port): sets the port's output latch; 0.</summary>
    [Hle(Nid = 0xD454A584, Library = "SceGpioForDriver")]
    int ksceGpioPortSet(uint bus, uint port)
    {
        int check = GpioCheck(bus, port);
        if (check != 0)
        {
            return check;
        }

        _gpioLatch[bus] |= 1u << (int)port;
        return 0;
    }

    /// <summary>ksceGpioPortClear(int bus, int port): clears the port's output latch; 0.</summary>
    [Hle(Nid = 0xF6310435, Library = "SceGpioForDriver")]
    int ksceGpioPortClear(uint bus, uint port)
    {
        int check = GpioCheck(bus, port);
        if (check != 0)
        {
            return check;
        }

        _gpioLatch[bus] &= ~(1u << (int)port);
        return 0;
    }

    /// <summary>ksceGpioPortRead(int bus, int port): the latch of an output port, the pin of an input port (0). The
    /// module does not check the port; it is taken modulo 32 as the shift does.</summary>
    [Hle(Nid = 0x129DF5AC, Library = "SceGpioForDriver")]
    int ksceGpioPortRead(uint bus, uint port)
    {
        if (bus >= GpioBuses)
        {
            return GPIO_ERR_BUS;
        }

        int shift = (int)(port & 31);
        bool output = (_gpioDirection[bus] & (1u << shift)) == 0;
        return output ? (int)((_gpioLatch[bus] >> shift) & 1) : 0;
    }

    /// <summary>ksceGpioPortReset(int bus, int port): flips the port's output latch; its previous value.</summary>
    [Hle(Nid = 0xA1B5A462, Library = "SceGpioForDriver")]
    int ksceGpioPortReset(uint bus, uint port)
    {
        int check = GpioCheck(bus, port);
        if (check != 0)
        {
            return check;
        }

        uint bit = 1u << (int)port;
        int previous = (_gpioLatch[bus] & bit) != 0 ? 1 : 0;
        _gpioLatch[bus] ^= bit;
        return previous;
    }

    /// <summary>ksceGpioSetIntrMode(int bus, int port, int mode): the port's interrupt mode (0..3); 0.</summary>
    [Hle(Nid = 0xBBEA1DDC, Library = "SceGpioForDriver")]
    int ksceGpioSetIntrMode(uint bus, uint port, uint mode)
    {
        int check = GpioCheck(bus, port);
        if (check != 0)
        {
            return check;
        }

        if (mode >= 4)
        {
            return GPIO_ERR_MODE;
        }

        int shift = (int)port * 2;
        _gpioInterruptMode[bus] = (_gpioInterruptMode[bus] & ~(3ul << shift)) | ((ulong)mode << shift);
        return 0;
    }

    /// <summary>ksceGpioGetIntrMode(int bus, int port): the port's interrupt mode.</summary>
    [Hle(Nid = 0xE97A3B31, Library = "SceGpioForDriver")]
    int ksceGpioGetIntrMode(uint bus, uint port)
    {
        int check = GpioCheck(bus, port);
        if (check != 0)
        {
            return check;
        }

        return (int)((_gpioInterruptMode[bus] >> ((int)port * 2)) & 3);
    }

    /// <summary>ksceGpioQueryIntr(int bus, int port): whether the port has an interrupt pending: none (0).</summary>
    [Hle(Nid = 0x010DC295, Library = "SceGpioForDriver")]
    int ksceGpioQueryIntr(uint bus, uint port)
    {
        return GpioCheck(bus, port);
    }

    /// <summary>ksceGpioAcquireIntr(int bus, int port): takes (and clears) the port's pending interrupt: none (0).</summary>
    [Hle(Nid = 0x35AAD77A, Library = "SceGpioForDriver")]
    int ksceGpioAcquireIntr(uint bus, uint port)
    {
        return GpioCheck(bus, port);
    }

    static int GpioCheck(uint bus, uint port)
    {
        if (bus >= GpioBuses)
        {
            return GPIO_ERR_BUS;
        }

        return port >= GpioPorts ? GPIO_ERR_PORT : 0;
    }
}
