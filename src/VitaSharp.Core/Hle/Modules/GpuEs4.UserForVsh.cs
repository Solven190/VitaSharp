using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// SceGpuEs4UserForVsh (os0:us/libgpu_es4.suprx 3.74, the system's SGX services calls libgxm_es4 imports): each entry
// checks its arguments (PVRSRV_ERROR_INVALID_PARAMS = 3), builds the call's in buffer on its stack and runs one bridge
// call through PVRSRVBridgeCall: a PVRSRV_BRIDGE_PACKAGE { bridgeId, 0x1C, in, inSize, out, outSize, hKernelServices }
// for the SceGpuEs4ForUser syscall, with hKernelServices from the connection (**connection + 4), retried while the
// driver answers 0x18. A refused call is PVRSRV_ERROR_BRIDGE_CALL_FAILED (0x22); else the result is the handler's
// eError, and the entries with an output copy its words only when eError is PVRSRV_OK. The bridge indexes 0x1F-0x22 and
// 0x5B belong to the SGX device's services, which the driver only serves after its init server has run on the device:
// the emulator has no SGX device, so they are refused like every device call (the "default" part).
public sealed partial class GpuEs4
{
    /// <summary>PVRSRV_ERROR_INVALID_PARAMS: a NULL connection, device cookie or output.</summary>
    internal const int PvrInvalidParams = 3;

    /// <summary>PVRSRV_ERROR_BRIDGE_CALL_FAILED: the driver refused the bridge call.</summary>
    internal const int PvrBridgeCallFailed = 0x22;

    /// <summary>PVRSRVBridgeCall: the bridge call with the connection's hKernelServices and an in buffer of
    /// <paramref name="inSize"/> bytes built by the caller; 0 = the driver ran it (eError and the first output word
    /// given back), else <see cref="PvrBridgeCallFailed"/>.</summary>
    int UserBridgeCall(uint connection, uint bridgeIndex, uint inSize, uint outSize, out uint error, out uint second)
    {
        error = 0;
        second = 0;
        uint services = 0;
        uint connectionData = connection != 0 && GuestMemory.IsMapped(connection) ? GuestMemory.R32(connection) : 0;
        uint servicesRecord = connectionData != 0 && GuestMemory.IsMapped(connectionData) ? GuestMemory.R32(connectionData) : 0;
        if (servicesRecord != 0 && GuestMemory.IsMapped(servicesRecord + 4))
        {
            services = GuestMemory.R32(servicesRecord + 4);
        }

        int result = BridgeDispatch(BridgeIdBase + bridgeIndex, inSize, true, outSize, services, out error, out second);
        if (result != 0)
        {
            Log.InfoOnce($"pvrsrv-user-{bridgeIndex:X}", $"SceGpuEs4UserForVsh: bridge call 0x{BridgeIdBase + bridgeIndex:X4} refused (no SGX device): PVRSRV_ERROR_BRIDGE_CALL_FAILED");
            return PvrBridgeCallFailed;
        }

        return 0;
    }

    /// <summary>The entries without an output: their bridge call's eError, or one of the wrapper's errors.</summary>
    int UserBridgeResult(uint connection, uint devCookie, bool argumentsValid, uint bridgeIndex, uint inSize)
    {
        if (connection == 0 || devCookie == 0 || !argumentsValid)
        {
            return PvrInvalidParams;
        }

        return UserBridgeCall(connection, bridgeIndex, inSize, 8, out uint error, out _) != 0 ? PvrBridgeCallFailed : (int)error;
    }

    /// <summary>NID_3C09BFCA(connection, devCookie, value): bridge 0x101F with { devCookie, value }.</summary>
    [Hle("NID_3C09BFCA", Nid = 0x3C09BFCA, Library = "SceGpuEs4UserForVsh", Default = true)]
    int GpuEs4UserForVsh3C09BFCA(uint connection, uint devCookie, uint value)
    {
        return UserBridgeResult(connection, devCookie, value != 0, 0x1F, 0xC);
    }

    /// <summary>NID_8A27AB0A(connection, devCookie, uint32 *out): bridge 0x1020 with { devCookie }; the handler's word
    /// to *out on PVRSRV_OK.</summary>
    [Hle("NID_8A27AB0A", Nid = 0x8A27AB0A, Library = "SceGpuEs4UserForVsh", Default = true)]
    int GpuEs4UserForVsh8A27AB0A(uint connection, uint devCookie, uint output)
    {
        if (connection == 0 || devCookie == 0 || output == 0)
        {
            return PvrInvalidParams;
        }

        if (UserBridgeCall(connection, 0x20, 8, 8, out uint error, out uint second) != 0)
        {
            return PvrBridgeCallFailed;
        }

        if (error == 0)
        {
            GuestMemory.W32(output, second);
        }

        return (int)error;
    }

    /// <summary>NID_0A9556D9(connection, devCookie, value): bridge 0x1021 with { devCookie, value }.</summary>
    [Hle("NID_0A9556D9", Nid = 0x0A9556D9, Library = "SceGpuEs4UserForVsh", Default = true)]
    int GpuEs4UserForVsh0A9556D9(uint connection, uint devCookie, uint value)
    {
        return UserBridgeResult(connection, devCookie, value != 0, 0x21, 0xC);
    }

    /// <summary>NID_010DA2CB(connection, devCookie): bridge 0x1022 with { devCookie }.</summary>
    [Hle("NID_010DA2CB", Nid = 0x010DA2CB, Library = "SceGpuEs4UserForVsh", Default = true)]
    int GpuEs4UserForVsh010DA2CB(uint connection, uint devCookie)
    {
        return UserBridgeResult(connection, devCookie, true, 0x22, 8);
    }

    /// <summary>NID_3DC96093(connection, devCookie, a, b, c): bridge 0x105B with { devCookie, a, b, c }.</summary>
    [Hle("NID_3DC96093", Nid = 0x3DC96093, Library = "SceGpuEs4UserForVsh", Default = true)]
    int GpuEs4UserForVsh3DC96093(uint connection, uint devCookie, uint first, uint secondValue, uint third)
    {
        return UserBridgeResult(connection, devCookie, true, 0x5B, 0x14);
    }
}
