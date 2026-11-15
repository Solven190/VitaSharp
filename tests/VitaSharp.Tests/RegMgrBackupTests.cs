using VitaSharp.Core.Firmware;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader.Self;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>sceRegMgrResetRegistryLv, sceRegMgrDbBackup and sceRegMgrDbRestore as SceRegistryMgr 3.74 has them.</summary>
[Collection("guest")]
public sealed class RegMgrBackupTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int NotSystemProgram = unchecked((int)0x80010058);
    private const int Invalid = unchecked((int)0x800D0004);

    private readonly HleHarness _harness = new(fixture, "regmgr-backup");

    public void Dispose() => _harness.Dispose();

    private void RunAs(bool systemProgram)
    {
        byte[] capability = new byte[ProgramAccess.CapabilitySize];
        capability[0] = systemProgram ? (byte)0x40 : (byte)0;
        _harness.Emu.Access = new ProgramAccess(0x2800000000000025UL, capability, false);
    }

    /// <summary>Keys like the template's: level 0x10 / 0x20 / 0x30, the never-reset 0x1000, the flagged 0x2020,
    /// a development-only key (mask 0x2C) and backup levels 0 / 30 / 99 / 5.</summary>
    private Registry Keys()
    {
        Registry registry = Registry.For(_harness.Root);
        registry.Define("/CONFIG/TESTR/low", Registry.KeyType.Int, 4, level: 0x10, backupLevel: 99);
        registry.Define("/CONFIG/TESTR/mid", Registry.KeyType.Int, 4, level: 0x20, backupLevel: 30);
        registry.Define("/CONFIG/TESTR/high", Registry.KeyType.Int, 4, level: 0x30, backupLevel: 0);
        registry.Define("/CONFIG/TESTR/never", Registry.KeyType.Int, 4, level: 0x1000);
        registry.Define("/CONFIG/TESTR/flagged", Registry.KeyType.Int, 4, level: 0x2020, backupLevel: 99);
        registry.Define("/CONFIG/TESTR/devonly", Registry.KeyType.Int, 4, level: 0x10, modelMask: 0x2C);
        registry.Define("/CONFIG/TESTR/SUB/inner", Registry.KeyType.Int, 4, level: 0x10);
        registry.Define("/CONFIG/TESTR/unit", Registry.KeyType.Int, 4, level: 0x10, backupLevel: 5);
        registry.Define("/CONFIG/TESTRX/other", Registry.KeyType.Int, 4, level: 0x10);
        foreach (string name in new[] { "low", "mid", "high", "never", "flagged", "devonly", "unit" })
        {
            Assert.True(registry.SetInt("/CONFIG/TESTR/", name, 7));
        }

        Assert.True(registry.SetInt("/CONFIG/TESTR/SUB/", "inner", 7));
        Assert.True(registry.SetInt("/CONFIG/TESTRX/", "other", 7));
        return registry;
    }

    private static int Value(Registry registry, string category, string name)
    {
        Assert.True(registry.TryGetInt(category, name, out int value));
        return value;
    }

    [Fact]
    public void ResetChecksCallerPathAndLevel()
    {
        RegMgrSystem regmgr = _harness.Module<RegMgrSystem>();
        Keys();
        RunAs(false);
        Assert.Equal(NotSystemProgram, Call(regmgr, "sceRegMgrResetRegistryLv", _harness.Str("/CONFIG/TESTR"), 0x10));
        RunAs(true);
        Assert.Equal(Invalid, Call(regmgr, "sceRegMgrResetRegistryLv", 0u, 0x10));
        Assert.Equal(RegMgrSystem.ErrPathTooLong, Call(regmgr, "sceRegMgrResetRegistryLv", _harness.Str("/" + new string('A', 0x100)), 0x10));
        Assert.Equal(RegMgrSystem.ErrBadLevel, Call(regmgr, "sceRegMgrResetRegistryLv", _harness.Str("/CONFIG/TESTR"), 5));
        Assert.Equal(RegMgrSystem.ErrBadLevel, Call(regmgr, "sceRegMgrResetRegistryLv", _harness.Str("/CONFIG/TESTR"), 0x11));
        Assert.Equal(RegMgrSystem.ErrNoCategory, Call(regmgr, "sceRegMgrResetRegistryLv", _harness.Str("/CONFIG/NOSUCH"), 0x10));
    }

    [Fact]
    public void ResetLevelsChooseTheKeys()
    {
        RegMgrSystem regmgr = _harness.Module<RegMgrSystem>();
        Registry registry = Keys();
        RunAs(true);

        // level 0x20: keys whose own level is 0x20 or more (0x2020 counts as 0x20), only under the category
        Assert.Equal(0, Call(regmgr, "sceRegMgrResetRegistryLv", _harness.Str("/CONFIG/TESTR/"), 0x20));
        Assert.Equal(7, Value(registry, "/CONFIG/TESTR/", "low"));
        Assert.Equal(0, Value(registry, "/CONFIG/TESTR/", "mid"));
        Assert.Equal(0, Value(registry, "/CONFIG/TESTR/", "high"));
        Assert.Equal(0, Value(registry, "/CONFIG/TESTR/", "flagged"));
        Assert.Equal(7, Value(registry, "/CONFIG/TESTR/", "never"));
        Assert.Equal(7, Value(registry, "/CONFIG/TESTRX/", "other")); // a sibling with the same prefix is not under it

        // level 0x10: every key, sub-categories included, except the never-reset and other units' keys
        Assert.Equal(0, Call(regmgr, "sceRegMgrResetRegistryLv", _harness.Str("/CONFIG/TESTR"), 0x10));
        Assert.Equal(0, Value(registry, "/CONFIG/TESTR/", "low"));
        Assert.Equal(0, Value(registry, "/CONFIG/TESTR/SUB/", "inner"));
        Assert.Equal(7, Value(registry, "/CONFIG/TESTR/", "never"));
        Assert.Equal(7, Value(registry, "/CONFIG/TESTR/", "devonly"));

        // level 0x40: only keys flagged 0x2000
        Assert.True(registry.SetInt("/CONFIG/TESTR/", "low", 3));
        Assert.True(registry.SetInt("/CONFIG/TESTR/", "flagged", 3));
        Assert.Equal(0, Call(regmgr, "sceRegMgrResetRegistryLv", _harness.Str("/CONFIG/TESTR"), 0x40));
        Assert.Equal(3, Value(registry, "/CONFIG/TESTR/", "low"));
        Assert.Equal(0, Value(registry, "/CONFIG/TESTR/", "flagged"));
    }

    [Fact]
    public void BackupResetsLowBackupLevelsAndRestoreKeepsTheUnitsKeys()
    {
        RegMgrSystem regmgr = _harness.Module<RegMgrSystem>();
        Registry registry = Keys();
        RunAs(false);
        Assert.Equal(NotSystemProgram, Call(regmgr, "sceRegMgrDbBackup", _harness.Str("ux0:/backup.reg"), 30));
        Assert.Equal(NotSystemProgram, Call(regmgr, "sceRegMgrDbRestore", _harness.Str("ux0:/backup.reg")));
        RunAs(true);
        Assert.Equal(Invalid, Call(regmgr, "sceRegMgrDbBackup", 0u, 30));
        Assert.Equal(Invalid, Call(regmgr, "sceRegMgrDbBackup", _harness.Str("ux0:/" + new string('a', 0x100)), 30));
        Assert.Equal(RegMgrSystem.ErrBadBackupLevel, Call(regmgr, "sceRegMgrDbBackup", _harness.Str("ux0:/backup.reg"), 31));
        Assert.Equal(Invalid, Call(regmgr, "sceRegMgrDbRestore", 0u));
        Assert.Equal(SceError.IO_ENOENT, Call(regmgr, "sceRegMgrDbRestore", _harness.Str("ux0:/missing.reg")));

        // level 99: backup levels 0 and 30 go in at their default, 99 keeps its value
        Assert.Equal(0, Call(regmgr, "sceRegMgrDbBackup", _harness.Str("ux0:/backup.reg"), 99));
        Assert.Equal(7, Value(registry, "/CONFIG/TESTR/", "high")); // the backup does not change the registry

        foreach (string name in new[] { "low", "mid", "high", "never", "unit" })
        {
            Assert.True(registry.SetInt("/CONFIG/TESTR/", name, 9));
        }

        Assert.Equal(0, Call(regmgr, "sceRegMgrDbRestore", _harness.Str("ux0:/backup.reg")));
        Assert.Equal(7, Value(registry, "/CONFIG/TESTR/", "low"));  // backup level 99 >= 99: kept in the backup
        Assert.Equal(0, Value(registry, "/CONFIG/TESTR/", "mid"));  // 30 < 99: default in the backup
        Assert.Equal(0, Value(registry, "/CONFIG/TESTR/", "high")); // 0 < 99
        Assert.Equal(7, Value(registry, "/CONFIG/TESTR/", "never")); // reset level 0x1000: never reset by a backup
        Assert.Equal(9, Value(registry, "/CONFIG/TESTR/", "unit"));  // backup level 5: the unit keeps its own value

        // level 0: a full backup
        Assert.True(registry.SetInt("/CONFIG/TESTR/", "mid", 4));
        Assert.Equal(0, Call(regmgr, "sceRegMgrDbBackup", _harness.Str("ux0:/full.reg"), 0));
        Assert.True(registry.SetInt("/CONFIG/TESTR/", "mid", 5));
        Assert.Equal(0, Call(regmgr, "sceRegMgrDbRestore", _harness.Str("ux0:/full.reg")));
        Assert.Equal(4, Value(registry, "/CONFIG/TESTR/", "mid"));
    }
}
