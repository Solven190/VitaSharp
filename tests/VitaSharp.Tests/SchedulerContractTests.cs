using System.Collections.Concurrent;
using VitaSharp.Core.Kernel;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The scheduler's contract with the guest, as threadmgr does it (FW 3.74, os0:kd/threadmgr.skprx): who gets an emulated
/// core first, by priority and by arrival; when a running thread gives its core away (a higher-priority thread
/// waiting, or an equal-priority one with every core busy); where a thread that gave its core away goes back in line
/// (the head of its priority when preempted, the tail when it made room for an equal-priority thread). A performance
/// change that alters the order among threads breaks one of these.
/// The order is forced by the test, never by host timing: holder threads keep the application's cores busy until they
/// are let go, each thread is seen in line for a core (<see cref="Scheduler.IsWaitingForCore"/>) before the next one
/// starts, and the outcome is the order the threads got their core in.
/// </summary>
[Collection("guest")]
public sealed class SchedulerContractTests(GuestMemoryFixture fx) : IDisposable
{
    private readonly HleHarness _h = new(fx, "sched");
    private readonly List<ManualResetEventSlim> _gates = [];
    private readonly ConcurrentQueue<string> _order = new();

    public void Dispose()
    {
        foreach (ManualResetEventSlim gate in _gates)
        {
            gate.Set();
        }

        _h.Dispose();
        foreach (ManualResetEventSlim gate in _gates)
        {
            gate.Dispose();
        }
    }

    private ManualResetEventSlim Gate()
    {
        var gate = new ManualResetEventSlim();
        _gates.Add(gate);
        return gate;
    }

    /// <summary>A thread that takes a core and keeps it (it never checks for preemption) until its gate opens.</summary>
    private GuestRun Holder(ManualResetEventSlim gate, string name)
    {
        bool running = false;
        GuestRun run = _h.Start(_ =>
        {
            Volatile.Write(ref running, true);
            gate.Wait();
        }, name, 64);
        Until(() => Volatile.Read(ref running), $"{name} to take a core");
        return run;
    }

    /// <summary>Fills the application's cores, all but <paramref name="leaveFree"/>, with holders.</summary>
    private List<GuestRun> FillCores(ManualResetEventSlim gate, int leaveFree = 0)
    {
        Assert.Equal(3, Scheduler.Cores); // the Vita gives applications three cores
        var holders = new List<GuestRun>();
        for (int i = 0; i < Scheduler.Cores - leaveFree; i++)
        {
            holders.Add(Holder(gate, $"holder{i}"));
        }

        return holders;
    }

    /// <summary>Starts a thread that writes its name down once it has a core, and makes sure it is in line first.</summary>
    private GuestRun Queued(string name, int priority, Action<GuestThread>? then = null)
    {
        GuestRun run = _h.Start(th =>
        {
            _order.Enqueue(name);
            then?.Invoke(th);
        }, name, priority);
        Until(() => Scheduler.IsWaitingForCore(run.Thread), $"{name} in line for a core");
        return run;
    }

    private void JoinAll(IEnumerable<GuestRun> runs)
    {
        foreach (GuestRun run in runs)
        {
            _h.Join(run);
        }
    }

    /// <summary>Freed cores go to the highest-priority threads waiting (lower number first), whatever the order the
    /// threads got in line in: with three cores freed at once, the three highest priorities run and the others stay in
    /// line until one of them is done.</summary>
    [Fact]
    public void FreedCoresGoToTheHighestPriorityWaiters()
    {
        ManualResetEventSlim gate = Gate(), hold = Gate();
        List<GuestRun> holders = FillCores(gate);
        GuestRun[] waiters = [.. new[] { 160, 100, 130, 191, 64 }.Select(priority => Queued($"p{priority}", priority, _ => hold.Wait()))];
        gate.Set(); // every holder lets go: three cores free at once
        Until(() => _order.Count == 3, "three waiters to take the freed cores");
        Assert.Equal(["p100", "p130", "p64"], _order.Order());
        Assert.True(Scheduler.IsWaitingForCore(waiters[0].Thread)); // p160
        Assert.True(Scheduler.IsWaitingForCore(waiters[3].Thread)); // p191
        hold.Set();
        JoinAll(holders);
        JoinAll(waiters);
        Assert.Equal(["p160", "p191"], _order.Skip(3).Order());
    }

    /// <summary>With one core freed at a time, the order is the full priority order.</summary>
    [Fact]
    public void OneFreedCoreAtATimeRunsTheWaitersInPriorityOrder()
    {
        ManualResetEventSlim rest = Gate(), first = Gate();
        GuestRun one = Holder(first, "holder-first");
        List<GuestRun> others = [Holder(rest, "holder-a"), Holder(rest, "holder-b")];
        GuestRun[] waiters = [Queued("p160", 160), Queued("p100", 100), Queued("p130", 130), Queued("p191", 191), Queued("p64", 64)];
        first.Set(); // one core: each waiter runs, finishes and hands it to the next
        JoinAll(waiters);
        Assert.Equal(["p64", "p100", "p130", "p160", "p191"], _order);
        rest.Set();
        _h.Join(one);
        JoinAll(others);
    }

    /// <summary>Threads of the same priority get a freed core in the order they got in line (threadmgr's ready queue is
    /// first come first served within a priority), not in whatever order the host wakes them.</summary>
    [Fact]
    public void EqualPriorityWaitersTakeTheCoreInArrivalOrder()
    {
        ManualResetEventSlim rest = Gate(), first = Gate();
        GuestRun one = Holder(first, "holder-first");
        List<GuestRun> others = [Holder(rest, "holder-a"), Holder(rest, "holder-b")];
        var waiters = new List<GuestRun>();
        for (int i = 0; i < 6; i++)
        {
            waiters.Add(Queued($"w{i}", 120));
        }

        first.Set();
        JoinAll(waiters);
        Assert.Equal(["w0", "w1", "w2", "w3", "w4", "w5"], _order);
        rest.Set();
        _h.Join(one);
        JoinAll(others);
    }

    /// <summary>
    /// When a running thread gives its core away at a check (<see cref="Scheduler.ShouldYield"/>): a higher-priority
    /// thread waits; an equal-priority one waits and every core is busy. A lower-priority waiter never takes the core
    /// of a running thread.
    /// </summary>
    [Fact]
    public void ARunningThreadYieldsOnlyToHigherOrEqualPriority()
    {
        ManualResetEventSlim gate = Gate(), runnerGate = Gate();
        List<GuestRun> holders = FillCores(gate, leaveFree: 1);
        bool running = false;
        GuestRun runner = _h.Start(_ =>
        {
            Volatile.Write(ref running, true);
            runnerGate.Wait();
        }, "runner", 120);
        Until(() => Volatile.Read(ref running), "the runner to take the last core");
        GuestThread r = runner.Thread;
        Assert.False(Scheduler.ShouldYield(r)); // nobody waiting
        GuestRun lower = Queued("p150", 150);
        Assert.False(Scheduler.ShouldYield(r)); // a lower priority does not preempt
        GuestRun equal = Queued("p120", 120);
        Assert.True(Scheduler.ShouldYield(r)); // equal priority, every core busy: it gets a turn
        GuestRun higher = Queued("p60", 60);
        Assert.True(Scheduler.ShouldYield(r)); // higher priority: preempts
        runnerGate.Set();
        gate.Set();
        JoinAll([runner, higher, equal, lower]);
        JoinAll(holders);
    }

    /// <summary>
    /// A thread preempted by a higher-priority one goes back to the head of its priority: once the higher-priority
    /// thread is done, it runs again before the equal-priority thread that was already waiting (threadmgr re-queues the
    /// preempted thread at the head of its ready queue; the wake-ups and new threads join the tail).
    /// </summary>
    [Fact]
    public void APreemptedThreadRunsAgainBeforeItsEqualPriorityWaiters()
    {
        ManualResetEventSlim gate = Gate(), go = Gate();
        List<GuestRun> holders = FillCores(gate, leaveFree: 1);
        bool running = false;
        GuestRun preempted = _h.Start(th =>
        {
            Volatile.Write(ref running, true);
            go.Wait();
            Scheduler.Yield(th); // its check: the higher-priority thread is waiting
            _order.Enqueue("preempted");
        }, "preempted", 120);
        Until(() => Volatile.Read(ref running), "the thread to be preempted to take the last core");
        GuestRun waiting = Queued("equal", 120);
        GuestRun higher = Queued("higher", 60);
        go.Set();
        JoinAll([preempted, higher, waiting]);
        Assert.Equal(["higher", "preempted", "equal"], _order);
        gate.Set();
        JoinAll(holders);
    }

    /// <summary>A thread that gives its core to an equal-priority thread (every core busy) goes to the tail of its
    /// priority: the threads of its priority that were waiting all run before it does again.</summary>
    [Fact]
    public void AThreadYieldingToItsEqualsGoesBehindThem()
    {
        ManualResetEventSlim gate = Gate(), go = Gate();
        List<GuestRun> holders = FillCores(gate, leaveFree: 1);
        bool running = false;
        GuestRun yielder = _h.Start(th =>
        {
            Volatile.Write(ref running, true);
            go.Wait();
            Scheduler.Yield(th);
            _order.Enqueue("yielder");
        }, "yielder", 120);
        Until(() => Volatile.Read(ref running), "the yielding thread to take the last core");
        GuestRun first = Queued("first", 120), second = Queued("second", 120);
        go.Set();
        JoinAll([yielder, first, second]);
        Assert.Equal(["first", "second", "yielder"], _order);
        gate.Set();
        JoinAll(holders);
    }
}
