using System.Buffers.Binary;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The kernel event log of SceDebugForDriver (sysmem 3.74: 16 records of 0xC0 bytes, CPU 0 writing records 0..3 round
/// robin), the bridge's _vshEventLogPut that feeds it, and SceWlanBtForDriver's power sequence of the Wi-Fi/Bluetooth
/// chip, which writes its switches to that log.
/// </summary>
[Collection("guest")]
public sealed class EventLogWlanBtKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x80010016), TooLong = unchecked((int)0x80010021);
    private const int Perm = unchecked((int)0x80010058), TooBig = unchecked((int)0x8002710B);

    private readonly HleHarness _harness = new(fixture, "evlog");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    private VshBridge Bridge => _harness.Module<VshBridge>();

    [Theory]
    [InlineData("SceDebugForDriver", 0xCC5365D3u)]
    [InlineData("SceDebugForDriver", 0x912CF2BAu)]
    [InlineData("SceDebugForDriver", 0x95B38C6Cu)]
    [InlineData("SceWlanBtForDriver", 0xC5AA6F43u)]
    [InlineData("SceWlanBtForDriver", 0xA7FDE9DCu)]
    [InlineData("SceWlanBtForDriver", 0x0FC89113u)]
    [InlineData("SceWlanBtForDriver", 0x4C96C3B7u)]
    public void EveryExportHasAnEntry(string library, uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == library).ToList();
        Assert.Single(bindings);
        Assert.False(bindings[0].Default);
    }

    [Fact]
    public void APutWritesTheRecordAsTheModule()
    {
        uint data = _harness.Alloc(8);
        GuestMemory.W32(data, 0xAABBCCDD);
        GuestMemory.W32(data + 4, 0x11223344);
        Assert.Equal(0, Call(Services, "ksceEventLogPut", 0x10005, 0x40010003, 0x4E21, 3, 0x55u, data, 8u));
        ReadOnlySpan<byte> record = Services.EventLogRecordsBytes[..0xC0];
        Assert.Equal(0x48u, BinaryPrimitives.ReadUInt32LittleEndian(record));
        Assert.Equal(10u, BinaryPrimitives.ReadUInt32LittleEndian(record[4..]));
        Assert.Equal("NPXS19999"u8.ToArray(), record.Slice(8, 9).ToArray());
        Assert.Equal(0x4E21, BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]));
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(record[0x16..]));
        Assert.Equal(0x10005, BinaryPrimitives.ReadInt32LittleEndian(record[0x18..]));
        Assert.Equal(0x40010003, BinaryPrimitives.ReadInt32LittleEndian(record[0x1C..]));
        Assert.Equal(0x55u, BinaryPrimitives.ReadUInt32LittleEndian(record[0x38..]));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(record[0x3C..]));
        Assert.Equal(0xAABBCCDDu, BinaryPrimitives.ReadUInt32LittleEndian(record[0x40..]));
    }

    /// <summary>sysmem 3.74 copies the same 10 bytes from its read-only data into every record, whatever process puts
    /// it: the name is not a process's title id (the process is the id at +0x18).</summary>
    [Fact]
    public void TheRecordNameIsTheModulesConstantForEveryProcess()
    {
        Assert.Equal(0, Call(Services, "ksceEventLogPut", 0x10005, 0, 1, 1, 0u, 0u, 0u));
        Assert.Equal(0, Call(Services, "ksceEventLogPut", 0x10003, 0, 1, 1, 0u, 0u, 0u));
        byte[] expected = [.. "NPXS19999"u8, 0];
        Assert.Equal(expected, Services.EventLogRecordsBytes.Slice(8, 10).ToArray());
        Assert.Equal(expected, Services.EventLogRecordsBytes.Slice(0xC0 + 8, 10).ToArray());
        Assert.Equal(expected[..9], SystemServicesVsh.SysmemEventLogName.ToArray());
        Assert.NotEqual(BinaryPrimitives.ReadInt32LittleEndian(Services.EventLogRecordsBytes[0x18..]),
            BinaryPrimitives.ReadInt32LittleEndian(Services.EventLogRecordsBytes[(0xC0 + 0x18)..]));
    }

    [Fact]
    public void APutChecksItsData()
    {
        Assert.Equal(Invalid, Call(Services, "ksceEventLogPut", 0, 0, 1, 1, 0u, 0u, 4u));
        Assert.Equal(TooLong, Call(Services, "ksceEventLogPut", 0, 0, 1, 1, 0u, _harness.Alloc(0x81), 0x81u));
        Assert.Equal(0, Call(Services, "ksceEventLogPut", 0, 0, 1, 1, 0u, 0u, 0u));
        Assert.Equal(0, Call(Services, "ksceEventLogPutForCurrentThread", 1, 2, 0u, _harness.Alloc(0x80), 0x80u));
        Assert.Equal(0xC0u, BinaryPrimitives.ReadUInt32LittleEndian(Services.EventLogRecordsBytes[0xC0..]));
    }

    [Fact]
    public void CpuZeroWritesFourRecordsRoundRobin()
    {
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(0, Call(Services, "ksceEventLogPut", 0, 0, 1, i, 0u, 0u, 0u));
        }

        ReadOnlySpan<byte> log = Services.EventLogRecordsBytes;
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(log[0x16..])); // the fifth put took record 0 again
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(log[(0xC0 + 0x16)..]));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(log[(4 * 0xC0)..])); // CPU 1's records stay empty
    }

    [Fact]
    public void GetInfoCopiesTheRecordsThatFit()
    {
        uint buffer = _harness.Alloc(0x200);
        uint count = _harness.Word(0xFF);
        Assert.Equal(Invalid, Call(Services, "ksceEventLogGetInfo", 0u, 0x200u, count));
        Assert.Equal(0, Call(Services, "ksceEventLogGetInfo", buffer, 0x200u, count));
        Assert.Equal(0u, GuestMemory.R32(count));
        Call(Services, "ksceEventLogPut", 0, 0, 1, 1, 0u, _harness.Alloc(0x80), 0x80u); // 0xC0 bytes
        Call(Services, "ksceEventLogPut", 0, 0, 1, 2, 0u, 0u, 0u); // 0x40 bytes
        Assert.Equal(0x100, Call(Services, "ksceEventLogGetInfo", buffer, 0x200u, count));
        Assert.Equal(2u, GuestMemory.R32(count));
        Assert.Equal(0x40u, GuestMemory.R32(buffer + 0xC0));
        Assert.Equal(0x40, Call(Services, "ksceEventLogGetInfo", buffer, 0x80u, 0u)); // only the small one fits
    }

    [Fact]
    public void TheBridgePutGoesToTheKernelLog()
    {
        uint data = _harness.Word(0x5A5A5A5A);
        uint opt = _harness.Alloc(0x10);
        GuestMemory.W32(opt, data);
        GuestMemory.W32(opt + 4, 4);
        GuestMemory.W32(opt + 8, 4);
        Assert.Equal(Perm, Call(Bridge, "_vshEventLogPut", 0x30, 1, 9u, opt));
        _harness.Emu.IsSystemProgram = true;
        Assert.Equal(0, Call(Bridge, "_vshEventLogPut", 0x30, 1, 9u, opt));
        ReadOnlySpan<byte> record = Services.EventLogRecordsBytes;
        Assert.Equal(0x30, BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]));
        Assert.Equal(0x10005, BinaryPrimitives.ReadInt32LittleEndian(record[0x18..]));
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(record[0x38..]));
        Assert.Equal(0x5A5A5A5Au, BinaryPrimitives.ReadUInt32LittleEndian(record[0x40..]));
        GuestMemory.W32(opt + 8, 0x7C1);
        Assert.Equal(TooBig, Call(Bridge, "_vshEventLogPut", 0x30, 1, 9u, opt));
    }

    [Fact]
    public void TheMonitorIsKept()
    {
        Assert.Equal(0, Call(Services, "ksceWlanBtAttachMonitor", 0x81001234u));
        Assert.Equal(0x81001234u, Services.WlanBtMonitor);
        Assert.Equal(0, Call(Services, "ksceWlanBtDetachMonitor"));
        Assert.Equal(0u, Services.WlanBtMonitor);
    }

    [Fact]
    public void WifiAndBluetoothPowerTheChipTogether()
    {
        Assert.Equal(0, Call(Services, "ksceWlanBtGetConfiguration"));
        Assert.Equal(0, Call(Services, "ksceWlanBtSetConfiguration", 3)); // Wi-Fi on: the first power-up
        Assert.Equal(0x103, Call(Services, "ksceWlanBtGetConfiguration"));
        Assert.Equal(0, Call(Services, "ksceWlanBtSetConfiguration", 5)); // Bluetooth joins the powered chip
        Assert.Equal(0x107, Call(Services, "ksceWlanBtGetConfiguration"));
        Assert.Equal(0, Call(Services, "ksceWlanBtSetConfiguration", 4)); // Wi-Fi off: Bluetooth keeps it up
        Assert.Equal(0x5, Call(Services, "ksceWlanBtGetConfiguration"));
        Assert.Equal(0, Call(Services, "ksceWlanBtSetConfiguration", 6)); // the last side off: the chip off
        Assert.Equal(0, Call(Services, "ksceWlanBtGetConfiguration"));

        // each switch went to the event log, code 0x4E21, subcodes 1 (Wi-Fi on), 3, 2, 4
        ReadOnlySpan<byte> log = Services.EventLogRecordsBytes;
        int[] subcodes = [1, 3, 2, 4];
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(0x4E21, BinaryPrimitives.ReadUInt16LittleEndian(log[(i * 0xC0 + 0x14)..]));
            Assert.Equal(subcodes[i], BinaryPrimitives.ReadUInt16LittleEndian(log[(i * 0xC0 + 0x16)..]));
        }
    }

    [Fact]
    public void TheSuspendStepsNeedBothSides()
    {
        Call(Services, "ksceWlanBtSetConfiguration", 3);
        Call(Services, "ksceWlanBtSetConfiguration", 5);
        Assert.Equal(0, Call(Services, "ksceWlanBtSetConfiguration", 8)); // Wi-Fi suspended alone: nothing
        Assert.Equal(0x107, Call(Services, "ksceWlanBtGetConfiguration"));
        Call(Services, "ksceWlanBtSetConfiguration", 11); // both suspended
        Assert.Equal(0x117, Call(Services, "ksceWlanBtGetConfiguration"));
        Call(Services, "ksceWlanBtSetConfiguration", 7);
        Call(Services, "ksceWlanBtSetConfiguration", 10); // both suspending: the chip restarted, resumed
        Assert.Equal(0x11F, Call(Services, "ksceWlanBtGetConfiguration"));
        Assert.Equal(0, Call(Services, "ksceWlanBtSetConfiguration", 9)); // not a request: nothing
        Assert.Equal(0x11F, Call(Services, "ksceWlanBtGetConfiguration"));
    }

    [Fact]
    public void TheWakeUpSourcesKeepTheChipOrForceItOff()
    {
        Call(Services, "ksceWlanBtSetConfiguration", 3);
        Assert.Equal(0, Call(Services, "ksceWlanBtSetConfiguration", 13)); // Wi-Fi off as a wake-up source
        Assert.Equal(0x43, Call(Services, "ksceWlanBtGetConfiguration"));
        Assert.Equal(0, Call(Services, "ksceWlanBtSetConfiguration", 14)); // the source dropped, none left
        Assert.Equal(0, Call(Services, "ksceWlanBtGetConfiguration"));

        Call(Services, "ksceWlanBtSetConfiguration", 5);
        Assert.Equal(0, Call(Services, "ksceWlanBtSetConfiguration", 17)); // forced off asked, Bluetooth still on
        Assert.Equal(0x105, Call(Services, "ksceWlanBtGetConfiguration"));
        Assert.Equal(0, Call(Services, "ksceWlanBtSetConfiguration", 18)); // both off: the forced power-off
        Assert.Equal(0, Call(Services, "ksceWlanBtGetConfiguration"));
    }

    [Fact]
    public void BluetoothHasNoDevice()
    {
        foreach (uint nid in new[] { 0x10D68FAEu, 0x780A5557u })
        {
            var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
                .Where(b => b.Nid == nid && b.Library == "SceBtForDriver").ToList();
            Assert.Single(bindings);
            Assert.True(bindings[0].Default);
        }

        Assert.Equal(unchecked((int)0x802F5001), Call(Services, "ksceBtGetVidPid", 0u, 0u, _harness.Alloc(4)));
        Assert.Equal(unchecked((int)0x802F1802), Call(Services, "BtRegisterUsbPairing"));
    }
}
