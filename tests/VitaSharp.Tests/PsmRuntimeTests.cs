using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader.Self;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>What the PlayStation Mobile runtime (PCSI00011) needs from the firmware on its way to the Mono runtime.</summary>
[Collection("guest")]
public sealed class PsmRuntimeTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "psmrt");

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void AllocUnmapMemBlockTakesBudgetWithoutMapping()
    {
        using ProcessScope.Scope scope = ProcessScope.Enter(_harness.Emu);
        SysMem sysmem = _harness.Module<SysMem>();
        uint name = _harness.Str("ScePsmDummyMemBlock");
        uint info = _harness.Alloc(0x10);
        GuestMemory.W32(info, 0x10);

        // the syscall's checks (FW 3.74 sysmem): a name, and a size in whole MiB
        Assert.Equal(SceError.ILLEGAL_ADDR, Call(sysmem, "sceKernelAllocUnmapMemBlock", 0u, 0x2800000u));
        Assert.Equal(SceError.ILLEGAL_MEMBLOCK_SIZE, Call(sysmem, "sceKernelAllocUnmapMemBlock", name, 0x2800800u));

        Assert.Equal(0, Call(sysmem, "sceKernelGetFreeMemorySize", info));
        uint freeBefore = GuestMemory.R32(info + 4);
        int uid = Call(sysmem, "sceKernelAllocUnmapMemBlock", name, 0x2800000u);
        Assert.True(uid > 0, $"0x{uid:X8}");
        MemBlock block = _harness.Emu.Kernel.Objects.Get<MemBlock>(uid)!;
        Assert.Equal("ScePsmDummyMemBlock", block.Name);
        Assert.Equal(SysMem.TypeUserRx, block.Type);

        // the process has 40 MiB less to allocate, and no address of it belongs to the block
        Assert.Equal(0, Call(sysmem, "sceKernelGetFreeMemorySize", info));
        Assert.Equal(freeBefore - 0x2800000u, GuestMemory.R32(info + 4));
        Assert.NotEqual(uid, Call(sysmem, "sceKernelFindMemBlockByAddr", 0u, 0u));

        Assert.Equal(0, Call(sysmem, "sceKernelFreeMemBlock", uid));
        Assert.Equal(0, Call(sysmem, "sceKernelGetFreeMemorySize", info));
        Assert.Equal(freeBefore, GuestMemory.R32(info + 4));
    }

    private const int NotPermitted = unchecked((int)0xC05A1000);
    private const ulong GameAuthorityId = 0x210000101CC404D8UL;

    private int Sysmodule(string function, uint id) => Call(_harness.Module<SystemMisc>(), function, id);

    [Fact]
    public void SysmodulePermissionsFollowTheConsolesTable()
    {
        using ProcessScope.Scope scope = ProcessScope.Enter(_harness.Emu);
        // a game (authority 0x21..., no system or non-game capability)
        _harness.Emu.Access = new ProgramAccess(GameAuthorityId, new byte[ProgramAccess.CapabilitySize], false);
        Assert.Equal(NotPermitted, Sysmodule("sceSysmoduleLoadModule", 0x3E));   // libshacccg: system or PSM programs
        Assert.Equal(NotPermitted, Sysmodule("sceSysmoduleLoadModule", 0x42));   // libpsm_devagent
        Assert.Equal(NotPermitted, Sysmodule("sceSysmoduleLoadModule", 0x20));   // a non-game program's library
        Assert.Equal(NotPermitted, Sysmodule("sceSysmoduleLoadModule", 0x05));   // libperf: development kits only
        Assert.Equal(NotPermitted, Sysmodule("sceSysmoduleLoadModule", 0x56));   // no such module
        Assert.Equal(0, Sysmodule("sceSysmoduleLoadModule", 0x0B));              // libngs: anyone
        // the internal syscall is privileged: any caller gets the internal modules (the PSM runtime's psmkdc)
        Assert.Equal(0, Sysmodule("sceSysmoduleLoadModuleInternal", 0x80000027));
        Assert.Equal(NotPermitted, Sysmodule("sceSysmoduleLoadModuleInternal", 0x8000002A));
        // the public syscall keeps 16 bits of the id: an internal id there names a public module
        Assert.Equal(0, Sysmodule("sceSysmoduleLoadModule", 0x80000027));

        // the PSM runtime (its authority ID makes it a PSM program)
        _harness.Emu.Access = new ProgramAccess(ProgramAccess.PsmRuntimeAuthorityId, new byte[ProgramAccess.CapabilitySize], false);
        Assert.True(_harness.Emu.Access.IsPsmProgram);
        Assert.Equal(0, Sysmodule("sceSysmoduleLoadModule", 0x3E));
        Assert.Equal(NotPermitted, Sysmodule("sceSysmoduleLoadModule", 0x20));

        // a non-game program (capability bit 3) and a system program (bit 1); still no development tools on retail
        byte[] nonGame = new byte[ProgramAccess.CapabilitySize];
        nonGame[0] = 0x10;
        _harness.Emu.Access = new ProgramAccess(0x2800000000000099UL, nonGame, false);
        Assert.True(_harness.Emu.Access.IsNonGameProgram);
        Assert.False(_harness.Emu.Access.IsSystemProgram);
        Assert.Equal(0, Sysmodule("sceSysmoduleLoadModule", 0x34));
        Assert.Equal(NotPermitted, Sysmodule("sceSysmoduleLoadModule", 0x3F));
        byte[] system = new byte[ProgramAccess.CapabilitySize];
        system[0] = 0x40;
        _harness.Emu.Access = new ProgramAccess(0x2800000000000025UL, system, false);
        Assert.True(_harness.Emu.Access.IsSystemProgram);
        Assert.Equal(0, Sysmodule("sceSysmoduleLoadModule", 0x40));
        Assert.Equal(0, Sysmodule("sceSysmoduleLoadModule", 0x36));
        Assert.Equal(NotPermitted, Sysmodule("sceSysmoduleLoadModule", 0x09));
    }

    [Fact]
    public void ProgramAccessWithoutCapabilityFollowsWhatTheProgramIs()
    {
        // the system software: system and non-game program
        var system = ProgramAccess.Of(0x2800000000000001UL, fromSystemSoftware: true, retailApplication: true);
        Assert.True(system.IsSystemProgram);
        Assert.True(system.IsNonGameProgram);
        // a retail application (an NPDRM SELF): neither, the PSM runtime a PSM program by its authority ID
        var game = ProgramAccess.Of(GameAuthorityId, fromSystemSoftware: false, retailApplication: true);
        Assert.False(game.IsSystemProgram);
        Assert.False(game.IsNonGameProgram);
        Assert.False(game.IsPsmProgram);
        var runtime = ProgramAccess.Of(ProgramAccess.PsmRuntimeAuthorityId, fromSystemSoftware: false, retailApplication: true);
        Assert.True(runtime.IsPsmProgram);
        Assert.False(runtime.IsSystemProgram);
        // homebrew as HENkaku authenticates fake SELFs: unsafe (vita-make-fself's default authority, or a plain ELF)
        // the system class, safe (-s / -ss) the game class
        Assert.True(ProgramAccess.Of(0x2F00000000000001UL, false, false).IsSystemProgram);
        Assert.True(ProgramAccess.Of(0, false, false).IsNonGameProgram);
        Assert.False(ProgramAccess.Of(0x2F00000000000002UL, false, false).IsSystemProgram);
        Assert.False(ProgramAccess.Of(0x2F00000000000003UL, false, false).IsNonGameProgram);
        Assert.True(new ProgramAccess(0x2800000000000001UL, null, true).IsSystemProgram);
        Assert.False(new ProgramAccess(0x2F00000000000001UL, null, false).IsSystemProgram);
        Assert.False(new ProgramAccess(0x210000101CD20006UL, null, false).IsPsmProgram);
        Assert.True(new ProgramAccess(0x210000101CD2000AUL, null, false).IsPsmProgram);
        Assert.False(new ProgramAccess(0x210000101CD2000BUL, null, false).IsPsmProgram);
    }
}
