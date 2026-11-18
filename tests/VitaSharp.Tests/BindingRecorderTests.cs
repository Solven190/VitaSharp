using VitaSharp.Core.Input;

namespace VitaSharp.Tests;

/// <summary>
/// Recording a controller binding on the Controls screen (<see cref="BindingRecorder"/>): an emulator hotkey takes the
/// whole combination held together (L3 + R3), never its first button on the way; a game control takes the first input.
/// The clock is the test's (milliseconds passed in), nothing depends on the host's.
/// </summary>
public sealed class BindingRecorderTests
{
    private static readonly InputBinding s_l3 = InputBinding.Of(PadButton.LeftStick);
    private static readonly InputBinding s_r3 = InputBinding.Of(PadButton.RightStick);

    private static PadSnapshot Pad(params PadButton[] held)
    {
        var pad = new PadSnapshot { IsGamepad = true };
        foreach (PadButton button in held)
        {
            pad.Buttons[(int)button] = true;
        }

        return pad;
    }

    private static BindingRecorder Hotkey(RecordMode mode = RecordMode.Replace)
        => new(VitaControl.PauseEmulation, mode, controller: true, Pad());

    [Fact]
    public void HotkeyPressedOneButtonAtATimeRecordsOnlyTheWholeCombination()
    {
        BindingRecorder recorder = Hotkey();
        Assert.True(recorder.RecordsCombination);
        Assert.Null(recorder.Update(Pad(PadButton.LeftStick), 0));
        Assert.Equal([s_l3], recorder.Parts);
        Assert.Null(recorder.Update(Pad(PadButton.LeftStick), 30));
        Assert.Null(recorder.Update(Pad(PadButton.LeftStick, PadButton.RightStick), 60));
        Assert.Equal([s_l3, s_r3], recorder.Parts);
        // released one at a time: still the pair, nothing is recorded until everything has been up for SettleMs
        Assert.Null(recorder.Update(Pad(PadButton.RightStick), 90));
        Assert.Null(recorder.Update(Pad(), 120));
        Assert.Null(recorder.Update(Pad(), 120 + BindingRecorder.SettleMs - 1));

        RecordedInput? recorded = recorder.Update(Pad(), 120 + BindingRecorder.SettleMs);
        Assert.NotNull(recorded);
        Assert.Null(recorded.Value.Single);
        Assert.Equal([s_l3, s_r3], recorded.Value.Combo!.Value.Parts);
        Assert.True(recorded.Value.Replace);
    }

    [Fact]
    public void AButtonThatSlipsWhileTheNextIsPressedDoesNotEndTheRecording()
    {
        // a stick click lets go for a moment while the other stick is pressed: L3, nothing, then L3 + R3
        BindingRecorder recorder = Hotkey();
        Assert.Null(recorder.Update(Pad(PadButton.LeftStick), 0));
        Assert.Null(recorder.Update(Pad(), 20));
        Assert.Null(recorder.Update(Pad(), 20 + BindingRecorder.SettleMs - 1));
        Assert.Null(recorder.Update(Pad(PadButton.LeftStick, PadButton.RightStick), 20 + BindingRecorder.SettleMs - 1));
        Assert.Null(recorder.Update(Pad(), 500));

        RecordedInput? recorded = recorder.Update(Pad(), 500 + BindingRecorder.SettleMs);
        Assert.NotNull(recorded);
        Assert.Null(recorded.Value.Single);
        Assert.Equal([s_l3, s_r3], recorded.Value.Combo!.Value.Parts);
    }

    [Fact]
    public void TheCombinationKeepsTheOrderTheButtonsWerePressed()
    {
        BindingRecorder recorder = Hotkey(RecordMode.Combo);
        recorder.Update(Pad(PadButton.RightStick), 0);
        recorder.Update(Pad(PadButton.RightStick, PadButton.LeftStick), 10);
        recorder.Update(Pad(), 20);
        RecordedInput? recorded = recorder.Update(Pad(), 20 + BindingRecorder.SettleMs);
        Assert.Equal([s_r3, s_l3], recorded!.Value.Combo!.Value.Parts);
        Assert.False(recorded.Value.Replace);
    }

    [Fact]
    public void AHotkeyPressedAloneIsASingleBindingOnceReleased()
    {
        BindingRecorder recorder = Hotkey();
        Assert.Null(recorder.Update(Pad(PadButton.Guide), 0));
        Assert.Null(recorder.Update(Pad(), 10));
        RecordedInput? recorded = recorder.Update(Pad(), 10 + BindingRecorder.SettleMs);
        Assert.Equal(InputBinding.Of(PadButton.Guide), recorded!.Value.Single);
        Assert.Null(recorded.Value.Combo);
    }

    [Fact]
    public void ButtonsHeldWhenTheRecordingStartedAreNotPartOfIt()
    {
        var recorder = new BindingRecorder(VitaControl.PauseEmulation, RecordMode.Replace, controller: true, Pad(PadButton.South));
        Assert.Null(recorder.Update(Pad(PadButton.South), 0));
        Assert.Empty(recorder.Parts);
        Assert.Null(recorder.Update(Pad(PadButton.South, PadButton.Start), 10));
        Assert.Null(recorder.Update(Pad(PadButton.South), 20));
        RecordedInput? recorded = recorder.Update(Pad(PadButton.South), 20 + BindingRecorder.SettleMs);
        Assert.Equal(InputBinding.Of(PadButton.Start), recorded!.Value.Single);
    }

    [Fact]
    public void AGameControlTakesTheFirstInputAtOnce()
    {
        var recorder = new BindingRecorder(VitaControl.Cross, RecordMode.Replace, controller: true, Pad());
        Assert.False(recorder.RecordsCombination);
        RecordedInput? recorded = recorder.Update(Pad(PadButton.South), 0);
        Assert.Equal(InputBinding.Of(PadButton.South), recorded!.Value.Single);
    }

    [Fact]
    public void ReplacingAHotkeyLeavesOnlyTheCombinationAndItFiresOnlyWithBothHeld()
    {
        ControllerProfile profile = DefaultMappings.For("pad", "Pad", ControllerKind.PlayStation, true);
        profile.Set(VitaControl.PauseEmulation, InputBinding.Of(PadButton.Guide));
        BindingRecorder recorder = Hotkey();
        recorder.Update(Pad(PadButton.LeftStick), 0);
        recorder.Update(Pad(PadButton.LeftStick, PadButton.RightStick), 10);
        recorder.Update(Pad(), 20);
        BindingRecorder.Apply(profile, VitaControl.PauseEmulation, recorder.Update(Pad(), 20 + BindingRecorder.SettleMs)!.Value);

        Assert.Equal(["pad:leftstick&pad:rightstick"], profile.Bindings[nameof(VitaControl.PauseEmulation)]);
        var compiled = new CompiledProfile(profile);
        Assert.False(InputMapper.HotkeyHeld(compiled, VitaControl.PauseEmulation, Pad(PadButton.LeftStick), null));
        Assert.False(InputMapper.HotkeyHeld(compiled, VitaControl.PauseEmulation, Pad(PadButton.RightStick), null));
        Assert.False(InputMapper.HotkeyHeld(compiled, VitaControl.PauseEmulation, Pad(PadButton.Guide), null));
        Assert.True(InputMapper.HotkeyHeld(compiled, VitaControl.PauseEmulation, Pad(PadButton.LeftStick, PadButton.RightStick), null));
    }

    [Fact]
    public void AddingAHotkeyCombinationKeepsTheOtherBindings()
    {
        ControllerProfile profile = DefaultMappings.For("pad", "Pad", ControllerKind.PlayStation, true);
        profile.Set(VitaControl.PauseEmulation, InputBinding.Of(PadButton.Guide));
        BindingRecorder recorder = Hotkey(RecordMode.Combo);
        recorder.Update(Pad(PadButton.Back, PadButton.Start), 0);
        recorder.Update(Pad(), 10);
        BindingRecorder.Apply(profile, VitaControl.PauseEmulation, recorder.Update(Pad(), 10 + BindingRecorder.SettleMs)!.Value);

        Assert.Equal(["pad:guide", "pad:back&pad:start"], profile.Bindings[nameof(VitaControl.PauseEmulation)]);
    }
}
