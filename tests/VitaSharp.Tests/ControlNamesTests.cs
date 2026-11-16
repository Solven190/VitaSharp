using Avalonia.Input;
using VitaSharp.Core.Input;
using VitaSharp.Core.Localization;
using VitaSharp.Desktop.Launcher;

namespace VitaSharp.Tests;

/// <summary>
/// The names of the launcher's Controls screen (<see cref="ControlNames"/>) and the key names it stores
/// (<see cref="KeyNames"/>). The stored names are read back by the game window as Silk.NET <c>Key</c> names, so every
/// name the launcher can produce, and every default keyboard binding, must parse as one (the check the ui-snapshot
/// tool ran by hand); the shown names follow the interface language and the button names printed on the controller.
/// </summary>
[Collection("language")]
public sealed class ControlNamesTests : IDisposable
{
    private readonly string _before = Loc.Culture.Name;

    public ControlNamesTests() => Loc.SetLanguage(Loc.English);

    public void Dispose() => Loc.SetLanguage(_before);

    [Fact]
    public void EveryLauncherKeyNameIsAGameWindowKey()
    {
        var bad = new List<string>();
        foreach (PhysicalKey pk in Enum.GetValues<PhysicalKey>())
        {
            string? name = KeyNames.FromAvalonia(pk, Key.None);
            if (name != null && !Enum.TryParse<Silk.NET.Input.Key>(name, out _))
            {
                bad.Add($"{pk} -> {name}");
            }
        }

        foreach (Key k in Enum.GetValues<Key>())
        {
            string? name = KeyNames.FromAvalonia(PhysicalKey.None, k);
            if (name != null && !Enum.TryParse<Silk.NET.Input.Key>(name, out _))
            {
                bad.Add($"{k} -> {name}");
            }
        }

        foreach (VitaControl c in VitaControls.All)
        {
            foreach (InputBinding b in DefaultMappings.Keyboard().Get(c))
            {
                if (b.Kind == BindingKind.Key && !Enum.TryParse<Silk.NET.Input.Key>(b.Key, out _))
                {
                    bad.Add($"default {c} -> {b.Key}");
                }
            }
        }

        Assert.Empty(bad);
    }

    [Fact]
    public void ThePhysicalPositionWinsOverTheLayoutKey()
    {
        // a binding is a key position: on AZERTY the key printed Q at the US A position stores "A"
        Assert.Equal("A", KeyNames.FromAvalonia(PhysicalKey.A, Key.Q));
        Assert.Equal("KeypadEnter", KeyNames.FromAvalonia(PhysicalKey.NumPadEnter, Key.Enter));
        Assert.Equal("SuperRight", KeyNames.FromAvalonia(PhysicalKey.MetaRight, Key.None));
        Assert.Equal("GraveAccent", KeyNames.FromAvalonia(PhysicalKey.Backquote, Key.None));
        Assert.Equal("Menu", KeyNames.FromAvalonia(PhysicalKey.ContextMenu, Key.None));
        Assert.Equal("F24", KeyNames.FromAvalonia(PhysicalKey.F24, Key.None));
        // a physical key without a name falls back to the virtual key, and that to null
        Assert.Equal("Keypad4", KeyNames.FromAvalonia(PhysicalKey.None, Key.NumPad4));
        Assert.Equal("Enter", KeyNames.FromAvalonia(PhysicalKey.None, Key.Enter));
        Assert.Equal("SuperLeft", KeyNames.FromAvalonia(PhysicalKey.None, Key.LWin));
        Assert.Equal("PageDown", KeyNames.FromAvalonia(PhysicalKey.None, Key.PageDown));
        Assert.Null(KeyNames.FromAvalonia(PhysicalKey.None, Key.VolumeUp));
    }

    [Fact]
    public void EveryVitaControlHasItsOwnNonEmptyLabelInBothLanguages()
    {
        foreach (string language in new[] { Loc.English, Loc.Portuguese })
        {
            Loc.SetLanguage(language);
            var seen = new Dictionary<string, VitaControl>();
            foreach (VitaControl c in VitaControls.All)
            {
                string label = ControlNames.Label(c);
                Assert.False(string.IsNullOrWhiteSpace(label), $"{c} has no label ({language})");
                Assert.False(seen.TryGetValue(label, out VitaControl other), $"{c} and {other} are both '{label}' ({language})");
                seen[label] = c;
            }
        }
    }

    [Fact]
    public void LabelsFollowTheInterfaceLanguage()
    {
        Assert.Equal("D-pad ↑", ControlNames.Label(VitaControl.Up));
        Assert.Equal("Left stick ←", ControlNames.Label(VitaControl.LStickLeft));
        Assert.Equal("Cross", ControlNames.Label(VitaControl.Cross));
        Assert.Equal("L3 (PS TV)", ControlNames.Label(VitaControl.L3));
        Loc.SetLanguage(Loc.Portuguese);
        Assert.Equal("Direcional ↑", ControlNames.Label(VitaControl.Up));
        Assert.Equal("Analógico esq. ←", ControlNames.Label(VitaControl.LStickLeft));
        Assert.Equal("Cruz", ControlNames.Label(VitaControl.Cross));
        Assert.Equal("Shift dir.", ControlNames.Key("ShiftRight"));
        Assert.Equal("Espaço", ControlNames.Key("Space"));
    }

    [Fact]
    public void FaceButtonsUseTheSymbolsPrintedOnThatController()
    {
        // PlayStation pads print the Vita's own symbols; other families keep their letters (South = A on Xbox)
        Assert.Equal("Cross", ControlNames.Describe(InputBinding.Of(PadButton.South), ControllerKind.PlayStation));
        Assert.Equal("Circle", ControlNames.Describe(InputBinding.Of(PadButton.East), ControllerKind.PlayStation));
        Assert.Equal("Square", ControlNames.Describe(InputBinding.Of(PadButton.West), ControllerKind.PlayStation));
        Assert.Equal("Triangle", ControlNames.Describe(InputBinding.Of(PadButton.North), ControllerKind.PlayStation));
        var south = InputBinding.Of(PadButton.South);
        Assert.Equal(south.Describe(ControllerKind.Xbox), ControlNames.Describe(south, ControllerKind.Xbox));
        Assert.NotEqual("Cross", ControlNames.Describe(south, ControllerKind.Xbox));
        Assert.Equal("D-pad →", ControlNames.Describe(InputBinding.Of(PadButton.DpadRight), ControllerKind.Xbox));
        Assert.Equal("Touchpad click", ControlNames.Describe(InputBinding.Of(PadButton.Touchpad), ControllerKind.PlayStation));
    }

    [Fact]
    public void AxesJoysticksAndHatsAreDescribedByDirection()
    {
        Assert.Equal("Left stick ←", ControlNames.Describe(InputBinding.Of(PadAxis.LeftX, -1), ControllerKind.Xbox));
        Assert.Equal("Left stick ↓", ControlNames.Describe(InputBinding.Of(PadAxis.LeftY, 1), ControllerKind.Xbox));
        Assert.Equal("Right stick →", ControlNames.Describe(InputBinding.Of(PadAxis.RightX, 1), ControllerKind.Xbox));
        Assert.Equal("Right stick ↑", ControlNames.Describe(InputBinding.Of(PadAxis.RightY, -1), ControllerKind.Xbox));
        var trigger = InputBinding.Of(PadAxis.RightTrigger);
        Assert.Equal(trigger.Describe(ControllerKind.Xbox), ControlNames.Describe(trigger, ControllerKind.Xbox));
        Assert.Equal("Button 3", ControlNames.Describe(InputBinding.JoyButton(3), ControllerKind.Generic));
        Assert.Equal("Axis 2-", ControlNames.Describe(InputBinding.JoyAxis(2, -1), ControllerKind.Generic));
        Assert.Equal("Axis 2+", ControlNames.Describe(InputBinding.JoyAxis(2, 1), ControllerKind.Generic));
        // a full-range axis (raw trigger, Dir 2) has no sign
        Assert.Equal("Axis 5", ControlNames.Describe(InputBinding.JoyAxis(5, 2), ControllerKind.Generic));
        // hat direction bits: 1 up, 2 right, 4 down, 8 left
        Assert.Equal("Hat 0 ↑", ControlNames.Describe(InputBinding.JoyHat(0, 1), ControllerKind.Generic));
        Assert.Equal("Hat 0 →", ControlNames.Describe(InputBinding.JoyHat(0, 2), ControllerKind.Generic));
        Assert.Equal("Hat 1 ↓", ControlNames.Describe(InputBinding.JoyHat(1, 4), ControllerKind.Generic));
        Assert.Equal("Hat 1 ←", ControlNames.Describe(InputBinding.JoyHat(1, 8), ControllerKind.Generic));
        Assert.Equal("Hat 1 3", ControlNames.Describe(InputBinding.JoyHat(1, 3), ControllerKind.Generic));
        Assert.Equal("", ControlNames.Describe(default(InputBinding), ControllerKind.Generic));
    }

    [Fact]
    public void CombosJoinTheirParts()
    {
        var combo = new InputCombo([InputBinding.Of(PadButton.Back), InputBinding.Of(PadButton.South)]);
        string back = ControlNames.Describe(InputBinding.Of(PadButton.Back), ControllerKind.PlayStation);
        Assert.Equal(back + " + Cross", ControlNames.Describe(combo, ControllerKind.PlayStation));
    }

    [Fact]
    public void KeysAndControllerFamiliesHaveShortReadableNames()
    {
        Assert.Equal("1", ControlNames.Key("Number1"));
        Assert.Equal("Num 7", ControlNames.Key("Keypad7"));
        Assert.Equal("Num Add", ControlNames.Key("KeypadAdd"));
        Assert.Equal("Left Ctrl", ControlNames.Key("ControlLeft"));
        Assert.Equal("Right Win", ControlNames.Key("SuperRight"));
        Assert.Equal("←", ControlNames.Key("Left"));
        Assert.Equal("Mouse button 4", ControlNames.Key("Mouse4"));
        Assert.Equal("Esc", ControlNames.Key("Escape"));
        Assert.Equal("`", ControlNames.Key("GraveAccent"));
        Assert.Equal("\\", ControlNames.Key("BackSlash"));
        Assert.Equal("F5", ControlNames.Key("F5"));
        Assert.Equal("Space", ControlNames.Describe(InputBinding.OfKey("Space"), ControllerKind.Keyboard));
        Assert.Equal("Keyboard", ControlNames.Kind(ControllerKind.Keyboard));
        Assert.Equal("Generic controller", ControlNames.Kind(ControllerKind.Generic));
        Assert.Equal("Xbox", ControlNames.Kind(ControllerKind.Xbox));
        Assert.StartsWith("PlayStation", ControlNames.Kind(ControllerKind.PlayStation));
        Assert.StartsWith("Nintendo", ControlNames.Kind(ControllerKind.Nintendo));
        Assert.Equal("standard gamepad", ControlNames.Model("standard gamepad"));
        Assert.Equal("virtual", ControlNames.Model("virtual"));
        Assert.Equal("DualSense Wireless Controller", ControlNames.Model("DualSense Wireless Controller"));
    }
}
