using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// SceUsbDevSerial (os0:kd/usbdev_serial.skprx, FW 3.74, read in the module, study only): the unit as a USB serial
// device of a PC. Its eight syscalls (NIDs without public names) start and stop the USB function (the device
// controller and "USBSerialDriver" on the controller's bus), open and close the port, send, receive and set the
// receive timeout. The emulated unit has no USB cable, so no host ever configures the function: start, stop, open
// and close behave as on a unit with nothing plugged in, and every transfer answers the module's "no host" error.

public sealed partial class SystemServicesVsh
{
    const int USBDEVSERIAL_ERR_BAD_MODE = unchecked((int)0x80243300), USBDEVSERIAL_ERR_CONTROLLER_STARTED = unchecked((int)0x80243002);
    const int USBDEVSERIAL_ERR_EINVAL = unchecked((int)0x80010016), USBDEVSERIAL_ERR_NO_HOST = unchecked((int)0x80010013),
        USBDEVSERIAL_ERR_NOT_OPEN = unchecked((int)0x80010009);

    /// <summary>The receive-timeout request of NID_918D359E, and the one it accepts without a host (then EINVAL).</summary>
    const int UsbDevSerialSetTimeout = 0x3517001, UsbDevSerialRequest8 = 0x3517008;

    /// <summary>The controller state words SceUdcd ORs together (cable disconnected, activated / deactivated).</summary>
    const uint UsbStateCableDisconnected = 0x10, UsbStateActivated = 0x200, UsbStateDeactivated = 0x100;

    /// <summary>Whether NID_DAE5C362 started the function (the controller driver is up).</summary>
    [SnapshotMissing] // states of builds before it: the call did not exist, so it never started
    bool _usbDevSerialStarted;

    /// <summary>How many times the port is open (NID_6F2B3960 counts up, NID_F0C3F3D4 down).</summary>
    [SnapshotMissing] // states of builds before it: never opened
    int _usbDevSerialOpenCount;

    /// <summary>The receive timeout in microseconds NID_918D359E sets; -1 (none) from the module's start.</summary>
    [SnapshotMissing(-1)] // states of builds before it: the module's initial value
    int _usbDevSerialTimeout = -1;

    /// <summary>The receive timeout the port uses (tests read it).</summary>
    internal int UsbDevSerialReceiveTimeout => _usbDevSerialTimeout;

    /// <summary>NID_DAE5C362(int mode): starts the device controller and the serial driver, then activates the
    /// function (product id 0xFFFF, the mode, 0 or 1; else 0x80243300). A second start finds the controller already
    /// started (0x80243002).</summary>
    [Hle("NID_DAE5C362", Nid = 0xDAE5C362, Library = "SceUsbDevSerial", Default = true)]
    int UsbDevSerialStart(uint mode)
    {
        if (mode >= 2)
        {
            return USBDEVSERIAL_ERR_BAD_MODE;
        }

        if (_usbDevSerialStarted)
        {
            return USBDEVSERIAL_ERR_CONTROLLER_STARTED;
        }

        _usbDevSerialStarted = true;
        Log.Info("SceUsbDevSerial: USB serial function started (no USB cable: no host will configure it)");
        return 0;
    }

    /// <summary>NID_9F497D4E(void): deactivates the function and stops both drivers: 0 when both stopped, 1 when a stop
    /// failed (nothing was started).</summary>
    [Hle("NID_9F497D4E", Nid = 0x9F497D4E, Library = "SceUsbDevSerial", Default = true)]
    int UsbDevSerialStop()
    {
        if (!_usbDevSerialStarted)
        {
            return 1;
        }

        _usbDevSerialStarted = false;
        return 0;
    }

    /// <summary>NID_C0FDECAF(void): the controller's state word (SceUdcd's cable, activation and connection bits ORed):
    /// cable disconnected, activated while the function is started.</summary>
    [Hle("NID_C0FDECAF", Nid = 0xC0FDECAF, Library = "SceUsbDevSerial", Default = true)]
    int UsbDevSerialGetState()
    {
        return (int)(UsbStateCableDisconnected | (_usbDevSerialStarted ? UsbStateActivated : UsbStateDeactivated));
    }

    /// <summary>NID_6F2B3960(void): opens the port (a count; the line is raised only when a host has configured the
    /// function, which never happens here). 0.</summary>
    [Hle("NID_6F2B3960", Nid = 0x6F2B3960, Library = "SceUsbDevSerial", Default = true)]
    int UsbDevSerialOpen()
    {
        _usbDevSerialOpenCount++;
        return 0;
    }

    /// <summary>NID_F0C3F3D4(void): closes the port once; 0x80010009 when it is not open.</summary>
    [Hle("NID_F0C3F3D4", Nid = 0xF0C3F3D4, Library = "SceUsbDevSerial", Default = true)]
    int UsbDevSerialClose()
    {
        if (_usbDevSerialOpenCount == 0)
        {
            return USBDEVSERIAL_ERR_NOT_OPEN;
        }

        _usbDevSerialOpenCount--;
        return 0;
    }

    /// <summary>NID_57FBB08C(void *buffer, uint size): receives up to size bytes. NULL is 0x80010016, size 0 returns 0;
    /// without a configured host (or with the port closed) 0x80010013.</summary>
    [Hle("NID_57FBB08C", Nid = 0x57FBB08C, Library = "SceUsbDevSerial", Default = true)]
    int UsbDevSerialReceive(uint buffer, uint size)
    {
        return UsbDevSerialTransfer(buffer, size);
    }

    /// <summary>NID_4437FF2D(const void *buffer, uint size): sends up to size bytes, with the checks of the receive.</summary>
    [Hle("NID_4437FF2D", Nid = 0x4437FF2D, Library = "SceUsbDevSerial", Default = true)]
    int UsbDevSerialSend(uint buffer, uint size)
    {
        return UsbDevSerialTransfer(buffer, size);
    }

    static int UsbDevSerialTransfer(uint buffer, uint size)
    {
        if (buffer == 0)
        {
            return USBDEVSERIAL_ERR_EINVAL;
        }

        return size == 0 ? 0 : USBDEVSERIAL_ERR_NO_HOST;
    }

    /// <summary>NID_918D359E(int request, const void *data, int size): request 0x3517001 with a 4-byte value sets the
    /// receive timeout (copied from the caller); any other use of it, and request 0x3517008, is 0x80010016. Every other
    /// request needs a configured host and an open port: 0x80010013.</summary>
    [Hle("NID_918D359E", Nid = 0x918D359E, Library = "SceUsbDevSerial", Default = true)]
    int UsbDevSerialControl(int request, uint data, int size)
    {
        if (request != UsbDevSerialSetTimeout && request != UsbDevSerialRequest8)
        {
            return USBDEVSERIAL_ERR_NO_HOST;
        }

        if (request != UsbDevSerialSetTimeout || data == 0 || size != 4)
        {
            return USBDEVSERIAL_ERR_EINVAL;
        }

        _usbDevSerialTimeout = GuestMemory.Read<int>(data);
        return 0;
    }
}
