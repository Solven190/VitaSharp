using System.Reflection;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// SceShellSvc's event handler table as libshellsvc 3.74 keeps it: one slot { function, argument } per event below
/// 0x80, shared by register (D943CE15), unregister (7CBF442B) and dispatch (FB03FBC9). The exports are called by NID
/// through whichever HLE module serves them, so the test holds whatever module the table lives in.
/// </summary>
[Collection("guest")]
public sealed class ShellSvcEventTests(GuestMemoryFixture fixture)
{
    private const uint Register = 0xD943CE15, Unregister = 0x7CBF442B, Dispatch = 0xFB03FBC9;
    private const int BadEvent = unchecked((int)0x80100910), HandlerSet = unchecked((int)0x80100901),
        NoHandler = unchecked((int)0x80100962), NotRaised = unchecked((int)0x80100963);

    private static int CallNid(HleHarness harness, uint nid, params object[] args)
    {
        const BindingFlags All = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        foreach (HleModule module in harness.Emu.Hle.Modules)
        {
            foreach (MethodInfo method in module.GetType().GetMethods(All))
            {
                foreach (HleAttribute attribute in method.GetCustomAttributes<HleAttribute>())
                {
                    if (attribute.Nid == nid && attribute.Library == "SceShellSvc")
                    {
                        return HleHarness.Call(module, method.Name, args);
                    }
                }
            }
        }
        throw new MissingMethodException("SceShellSvc", $"NID {nid:X8}");
    }

    [Fact]
    public void RegisterUnregisterAndDispatchShareOneHandlerTable()
    {
        using var harness = new HleHarness(fixture, "shellsvc-events");
        ShellUtil shell = harness.Module<ShellUtil>();

        // handler(event, argument): *argument = event; return 0x2A
        uint handler = harness.Alloc(16);
        GuestMemory.W32(handler, 0xE5810000);
        GuestMemory.W32(handler + 4, 0xE3A0002A);
        GuestMemory.W32(handler + 8, 0xE12FFF1E);
        harness.Emu.Code.Invalidate(handler, 16);
        uint seen = harness.Word(0xFFFFFFFF);
        var results = new List<(string Step, int Result)>();

        harness.OnGuest(_ =>
        {
            results.Add(("dispatch bad event", CallNid(harness, Dispatch, 0x80)));
            results.Add(("unregister bad event", CallNid(harness, Unregister, 0x80)));
            results.Add(("dispatch without handler", CallNid(harness, Dispatch, 0x35)));
            results.Add(("register", CallNid(harness, Register, 0x35, handler, seen)));
            results.Add(("dispatch, flag down", CallNid(harness, Dispatch, 0x35)));
            HleHarness.Invoke(shell, "RaiseEvent", 0x35, true);
            results.Add(("dispatch, only the state flag up", CallNid(harness, Dispatch, 0x35)));
            HleHarness.Invoke(shell, "RaiseEvent", 0x35, false);
            results.Add(("dispatch, handler flag up", CallNid(harness, Dispatch, 0x35)));
            results.Add(("dispatch again, the poll lowered the flag", CallNid(harness, Dispatch, 0x35)));
            results.Add(("register over a handler", CallNid(harness, Register, 0x35, handler, seen)));
            results.Add(("unregister", CallNid(harness, Unregister, 0x35)));
            HleHarness.Invoke(shell, "RaiseEvent", 0x35, false);
            results.Add(("dispatch after unregister", CallNid(harness, Dispatch, 0x35)));
            results.Add(("register again", CallNid(harness, Register, 0x35, handler, seen)));
        });

        Assert.Equal(
        [
            ("dispatch bad event", BadEvent),
            ("unregister bad event", BadEvent),
            ("dispatch without handler", NoHandler),
            ("register", 0),
            ("dispatch, flag down", NotRaised),
            ("dispatch, only the state flag up", NotRaised),
            ("dispatch, handler flag up", 0x2A),
            ("dispatch again, the poll lowered the flag", NotRaised),
            ("register over a handler", HandlerSet),
            ("unregister", 0),
            ("dispatch after unregister", NoHandler),
            ("register again", 0),
        ], results);
        Assert.Equal(0x35u, GuestMemory.R32(seen));
    }
}
