using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>
/// Guest time decided by the test: the guest clock stops when this is made and moves only by <see cref="Advance"/>, so a
/// wait's timeout passes exactly when the test says, never because the host was slow to run a step (a 50 ms timeout of
/// a clock that follows the host ran out on a loaded machine before the test reached its signal). Disposing it starts
/// the clock again from where it stopped. Tests in the "guest" collection run one at a time, so the shared clock is
/// theirs while it is stopped.
/// </summary>
public sealed class ControlledGuestClock : IDisposable
{
    private ControlledGuestClock()
    {
        GuestClock.Freeze();
    }

    public static ControlledGuestClock Stop() => new();

    /// <summary>Moves guest time forward by <paramref name="microseconds"/>.</summary>
    public void Advance(long microseconds) => GuestClock.AdvanceFrozen(microseconds);

    public void Dispose() => GuestClock.Thaw();
}
