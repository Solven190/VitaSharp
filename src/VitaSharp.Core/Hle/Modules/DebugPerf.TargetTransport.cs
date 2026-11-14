namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceLibTargetTransport: the development kit's host transport (the PSM runtime's libmono_bridge imports six of its
/// functions for the Mono debugger link). No retail firmware module exports the library (FW 3.74: none of os0/vs0), and
/// vita-headers has no names for the NIDs. The module loader leaves an import nobody exports as the importer's own stub,
/// "mvn r0, #0; bx lr" (the six stubs of libmono_bridge.suprx are exactly that): every call returns -1 and touches
/// nothing, which is what these entries answer.
/// </summary>
public sealed partial class DebugPerf
{
    const uint UnresolvedStubResult = 0xFFFFFFFF;

    [Hle("NID_11EBD107", Nid = 0x11EBD107, Library = "SceLibTargetTransport", Default = true, ReturnsValue = true)]
    [Hle("NID_1AD7239E", Nid = 0x1AD7239E, Library = "SceLibTargetTransport", Default = true, ReturnsValue = true)]
    [Hle("NID_4CF1664B", Nid = 0x4CF1664B, Library = "SceLibTargetTransport", Default = true, ReturnsValue = true)]
    [Hle("NID_4F644C0F", Nid = 0x4F644C0F, Library = "SceLibTargetTransport", Default = true, ReturnsValue = true)]
    [Hle("NID_9D3FC23C", Nid = 0x9D3FC23C, Library = "SceLibTargetTransport", Default = true, ReturnsValue = true)]
    [Hle("NID_D45C74BE", Nid = 0xD45C74BE, Library = "SceLibTargetTransport", Default = true, ReturnsValue = true)]
    uint TargetTransportUnresolved() => UnresolvedStubResult;
}
