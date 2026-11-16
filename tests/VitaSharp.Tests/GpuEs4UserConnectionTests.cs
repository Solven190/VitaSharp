using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceGpuEs4User's connection and its first bridge-call wrappers as libgpu_es4.suprx 3.74 has them: the argument
/// checks (PVRSRV_ERROR_INVALID_PARAMS = 3 and the call-specific ones) come first; every bridge call but the services
/// connect/disconnect is refused by the driver before its SGX init server ran (0x22), which the emulator never runs.
/// Found by name so the proof without them still builds.
/// </summary>
[Collection("guest")]
public sealed class GpuEs4UserConnectionTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int InvalidParams = 3;
    private const int BridgeCallFailed = 0x22;

    private readonly HleHarness _harness = new(fixture, "gpu-es4-user-connection");

    public void Dispose() => _harness.Dispose();

    private GpuEs4 Gpu => _harness.Module<GpuEs4>();

    [Theory]
    [InlineData(0x228252A2u, true)]
    [InlineData(0xA5B14369u, false)]
    [InlineData(0x4447D217u, true)]
    [InlineData(0x70800B46u, true)]
    [InlineData(0x5C3A6DFEu, true)]
    [InlineData(0x0D27286Fu, true)]
    [InlineData(0x116449D2u, true)]
    [InlineData(0x416C8813u, true)]
    [InlineData(0x7DCCBCAFu, true)]
    [InlineData(0x73E945A1u, true)]
    [InlineData(0x0162A405u, true)]
    [InlineData(0x071E62F0u, true)]
    [InlineData(0x0BFE1685u, true)]
    [InlineData(0x0EA7458Du, true)]
    [InlineData(0x156A6B70u, true)]
    [InlineData(0x1E0A9C4Au, true)]
    [InlineData(0x5273D097u, true)]
    [InlineData(0x5564C1BEu, true)]
    [InlineData(0x56893E98u, true)]
    [InlineData(0x7E965369u, true)]
    public void TheWrappersHaveAnEntry(uint nid, bool isDefault)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Nid == nid).ToList();
        Assert.NotEmpty(bindings);
        Assert.All(bindings, b => Assert.Equal(isDefault, b.Default));
        Assert.NotNull(_harness.Emu.Hle.ImplOf(nid));
    }

    /// <summary>PVRSRVConnect, then { connection, device cookie } as PVRSRVAcquireDeviceData would build it.</summary>
    private (uint Connection, uint Device) Connect()
    {
        uint output = _harness.Word(0);
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4UserConnect", output, 0u)); // connected; the device list refused
        uint connection = GuestMemory.R32(output);
        Assert.NotEqual(0u, connection);
        uint device = _harness.Alloc(8);
        GuestMemory.W32(device, connection);
        GuestMemory.W32(device + 4, 0x1234);
        return (connection, device);
    }

    [Fact]
    public void TheConnectionReachesTheServicesAndDisconnectsOnce()
    {
        Assert.Equal(4, Call(Gpu, "GpuEs4UserConnect", 0u, 0u));
        Assert.Equal(InvalidParams, Call(Gpu, "GpuEs4UserDisconnect", 0u));
        (uint connection, _) = Connect();
        uint services = GuestMemory.R32(connection);
        Assert.Equal(GpuEs4.KernelServicesHandle, GuestMemory.R32(services + 4));
        uint again = _harness.Word(0);
        Assert.Equal(InvalidParams, Call(Gpu, "GpuEs4UserConnect", again, 0u)); // already connected
        Assert.Equal(0, Call(Gpu, "GpuEs4UserDisconnect", connection));
        Assert.Equal(InvalidParams, Call(Gpu, "GpuEs4UserDisconnect", connection));
        Connect();
    }

    [Fact]
    public void TheDeviceCallsAreRefusedAfterTheirChecks()
    {
        (uint connection, uint device) = Connect();
        uint count = _harness.Word(0xAAAAAAAA);
        uint devices = _harness.Alloc(0x14);
        Assert.Equal(InvalidParams, Call(Gpu, "GpuEs4UserEnumerateDevices", connection, 0u, devices));
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4UserEnumerateDevices", connection, count, devices));
        Assert.Equal(0xAAAAAAAAu, GuestMemory.R32(count));
        uint acquired = _harness.Alloc(8);
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4UserAcquireDeviceData", connection, 0u, acquired, 7u));
        Assert.Equal(unchecked((int)0x804C0000), Call(Gpu, "GpuEs4UserWaitEvent", 1u, 0u));

        uint record = _harness.Word(GuestMemory.R32(connection)); // a pointer to the services record
        uint empty = _harness.Word(0);
        uint words = _harness.Alloc(16);
        uint output = _harness.Word(0xAAAAAAAA);
        Assert.Equal(InvalidParams, Call(Gpu, "GpuEs4User0D27286F", record, 0u, 0u, 2, 0u, 0u, 0u));
        Assert.Equal(InvalidParams, Call(Gpu, "GpuEs4User0D27286F", empty, 0u, words, 2, 0u, 0u, 0u));
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4User0D27286F", record, 0u, words, 2, 0u, output, 0u));
        Assert.Equal(InvalidParams, Call(Gpu, "GpuEs4User116449D2", 0u, 1u));
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4User116449D2", record, 1u));
        Assert.Equal(InvalidParams, Call(Gpu, "GpuEs4User416C8813", record, 0u));
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4User416C8813", record, output));
        Assert.Equal(0xAAAAAAAAu, GuestMemory.R32(output));
        Assert.Equal(InvalidParams, Call(Gpu, "GpuEs4User7DCCBCAF", empty, 1u));
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4User7DCCBCAF", record, 1u));

        uint info = _harness.Alloc(0x98);
        Assert.Equal(InvalidParams, Call(Gpu, "GpuEs4User73E945A1", connection, 0u));
        Assert.Equal(0, Call(Gpu, "GpuEs4User73E945A1", connection, info)); // first word 0: nothing to ask
        GuestMemory.W32(info, 1);
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4User73E945A1", connection, info));

        uint block = _harness.Alloc(0x108);
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4User0162A405", device, block, output));
        Assert.Equal(InvalidParams, Call(Gpu, "GpuEs4User071E62F0", 0u, 0u, 8u, 0x1000u, 0x10u, 0u, output));
        Assert.Equal(0x1D, Call(Gpu, "GpuEs4User071E62F0", device, 0u, 0u, 0x1000u, 0x10u, 0u, output));
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4User071E62F0", device, 0u, 8u, 0x1000u, 0x10u, 0u, output));
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4User0BFE1685", device, 1u, 0u, 2u));
        Assert.Equal(InvalidParams, Call(Gpu, "GpuEs4User0EA7458D", device, 0u, output, 0u));
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4User0EA7458D", device, 1u, output, 0u));
        Assert.Equal(InvalidParams, Call(Gpu, "GpuEs4User156A6B70", device, 0u));
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4User156A6B70", device, 1u));
        Assert.Equal(InvalidParams, Call(Gpu, "GpuEs4User1E0A9C4A", device, output, 0u, block));
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4User1E0A9C4A", device, output, count, block));
        Assert.Equal(InvalidParams, Call(Gpu, "GpuEs4User5273D097", device, 0u));
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4User5273D097", device, block));
        Assert.Equal(InvalidParams, Call(Gpu, "GpuEs4User5564C1BE", words, 0x1Bu, device, 0u, 0u, 0u, 0u, 0u));
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4User5564C1BE", words, 0x1Au, device, 0u, 0u, 0u, 0u, 0u));
        Assert.Equal(InvalidParams, Call(Gpu, "GpuEs4User56893E98", device, 0u, 0u, 0u, 0u, 0u, 0u, output));
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4User56893E98", device, 0u, 0u, 0u, 0u, 1u, 0u, output));
        Assert.Equal(BridgeCallFailed, Call(Gpu, "GpuEs4User7E965369", device, 1u));
        Assert.Equal(0xAAAAAAAAu, GuestMemory.R32(output));
    }
}
