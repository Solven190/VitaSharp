using System.Diagnostics;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Kernel;

/// <summary>Blocking-wait helper with Vita semantics: timeout in microseconds, remaining time written back.</summary>
public static class Wait
{
    /// <summary>Wake-up latency of a blocking syscall on the Vita (kernel entry, scheduler, context switch on a 333 MHz
    /// Cortex-A9): tens of microseconds. Host wake-ups take ~1 us, which makes job systems that measure their own
    /// synchronisation-heavy work (physics steps with per-step time budgets) behave differently; 0 disables.
    /// VITASHARP_SYSCALL_LATENCY_US overrides it (timing bisection).</summary>
    public static int SyscallLatencyUs = int.TryParse(Environment.GetEnvironmentVariable(EnvVars.SyscallLatencyUs), out int lat) ? lat : 30;

    /// <summary>Statistics (PERF sched): host waits inside blocking syscalls, and wake-up latencies served.</summary>
    public static long BlockingWaits, Latencies;

    /// <summary>Serves the wake-up latency of a wait that <see cref="UntilGranted"/> ended, once the caller has left the
    /// sync object's lock.</summary>
    public static void WakeLatency() => Latency();

    private static void Latency()
    {
        if (SyscallLatencyUs <= 0 || GuestClock.Turbo)
        {
            return;
        }

        Interlocked.Increment(ref Latencies);
        PreciseSleep.Until(System.Diagnostics.Stopwatch.GetTimestamp() + SyscallLatencyUs * System.Diagnostics.Stopwatch.Frequency / 1_000_000);
    }

    /// <summary>
    /// Waits on <paramref name="sync"/> (which the caller holds) until <paramref name="ready"/> returns true.
    /// Returns OK, WAIT_TIMEOUT, or WAIT_DELETE/WAIT_CANCEL when <paramref name="abort"/> reports it.
    /// </summary>
    public static int Until(object sync, Func<bool> ready, Ptr<uint> timeout, Func<int>? abort = null, string what = "")
        => Until(sync, (ready, abort), static s => s.ready(), timeout, static s => s.abort?.Invoke() ?? 0, what);

    /// <summary>
    /// Same as <see cref="Until(object, Func{bool}, Ptr{uint}, Func{int}?, string)"/> with the predicate state passed
    /// explicitly, so sync objects can use static lambdas: every blocking syscall used to allocate two closures.
    /// </summary>
    public static int Until<T>(object sync, T state, Func<T, bool> ready, Ptr<uint> timeout, Func<T, int>? abort, string what = "")
        => UntilCore(sync, state, ready, timeout, timeout.IsNull ? null : timeout.Value, abort, what);

    /// <summary>Same as <see cref="Until(object, Func{bool}, Ptr{uint}, Func{int}?, string)"/> with the timeout passed by
    /// value (microseconds; null = wait forever). For APIs such as sceNetEpollWait or sceRudpPollWait whose timeout is an
    /// argument rather than a guest pointer.</summary>
    public static int Until(object sync, Func<bool> ready, long? timeoutUs, Func<int>? abort = null, string what = "")
        => UntilCore(sync, (ready, abort), static s => s.ready(), default, timeoutUs, static s => s.abort?.Invoke() ?? 0, what, out _, deferLatency: false);

    /// <summary>
    /// For a sync object whose releasing call hands the waiter its result (the kernel's way: sceKernelSetEventFlag
    /// satisfies a waiter and applies its clear mode before it returns): the same wait, but the wake-up latency is not
    /// served while the caller holds <paramref name="sync"/>. <paramref name="woken"/> says whether the thread slept; the
    /// caller then calls <see cref="WakeLatency"/> after leaving the lock. Serving it inside the lock kept the object
    /// locked for the latency, so a thread calling into the same object right after waking the waiter blocked on the
    /// host lock and depended on the host scheduler to run again (Metal Gear Solid HD: main's sceKernelClearEventFlag
    /// stalled tens of milliseconds behind the End Frame thread it had just woken, and cleared that thread's next
    /// "done" bit).
    /// </summary>
    public static int UntilGranted<T>(object sync, T state, Func<T, bool> ready, Ptr<uint> timeout, Func<T, int>? abort, string what, out bool woken)
        => UntilCore(sync, state, ready, timeout, timeout.IsNull ? null : timeout.Value, abort, what, out woken, deferLatency: true);

    private static int UntilCore<T>(object sync, T state, Func<T, bool> ready, Ptr<uint> timeout, long? limitUs, Func<T, int>? abort, string what)
        => UntilCore(sync, state, ready, timeout, limitUs, abort, what, out _, deferLatency: false);

    private static int UntilCore<T>(object sync, T state, Func<T, bool> ready, Ptr<uint> timeout, long? limitUs, Func<T, int>? abort, string what, out bool woken, bool deferLatency)
    {
        woken = false;
        GuestThread? self = GuestThread.Current;
        if (self != null)
        {
            self.Status = ThreadStatus.Waiting;
            self.WaitingOn = what;
            self.CurrentWaitSync = sync;
        }
        try
        {
            long start = GuestClock.Ticks; // timeouts run on guest time
            bool waited = false;
            while (true)
            {
                if (abort != null)
                {
                    int a = abort(state);
                    if (a != 0)
                    {
                        if (TakeCoreBack(sync, self))
                        {
                            continue; // the lock was left: look again with the core
                        }

                        return a;
                    }
                }
                if (ready(state))
                {
                    if (TakeCoreBack(sync, self))
                    {
                        continue; // the lock was left: another thread may have taken the object meanwhile
                    }

                    woken = waited;
                    // woken by, or found ready after, the object's last signaller: it is this thread's waker
                    if (self != null && sync is KernelObject kernelObject)
                    {
                        self.WokenBy(kernelObject.Signaller, kernelObject.SignallerProgress, kernelObject.SignallerCoreReleases);
                    }
                    if (waited && !deferLatency)
                    {
                        Latency();
                    }

                    // a wait that slept ended at the object's last signal, if it came during the wait
                    long signalled = waited && sync is KernelObject signalledObject ? signalledObject.SignalTicks : long.MinValue;
                    WriteRemaining(timeout, limitUs, start, signalled);
                    return SceError.OK;
                }
                if (self != null && self.Core.StopRequested)
                {
                    throw new GuestThreadExitException(0, false);
                }

                if (self != null && self.CanRunCallbacks && Volatile.Read(ref self.PendingCallbacks) > 0 && GuestThread.CallbackProcessor != null)
                {
                    if (TakeCoreBack(sync, self))
                    {
                        continue;
                    }

                    Monitor.Exit(sync);
                    try
                    {
                        GuestThread.CallbackProcessor(self);
                    }
                    finally
                    {
                        Monitor.Enter(sync);
                        // the callback ran as Running (and a wait inside it ended as Running): this wait goes on, so
                        // thread dumps and the watchdog show it waiting on its object again, not "Running"
                        self.Status = ThreadStatus.Waiting;
                        self.WaitingOn = what;
                        self.CurrentWaitSync = sync;
                    }
                    continue;
                }
                int sliceMs = 50;
                if (limitUs.HasValue)
                {
                    long leftUs = limitUs.Value - ElapsedUs(start);
                    if (leftUs <= 0)
                    {
                        if (TakeCoreBack(sync, self))
                        {
                            continue; // ready may have come while the lock was left
                        }

                        timeout.TrySet(0);
                        return SceError.WAIT_TIMEOUT;
                    }
                    sliceMs = (int)Math.Clamp((leftUs / GuestClock.Speed + 999) / 1000, 1, 50);
                    if (GuestClock.Turbo)
                    {
                        // turbo: give the event a moment, then jump guest time to the timeout instead of waiting it out
                        if (!Monitor.Wait(sync, 1))
                        {
                            GuestClock.WaitUntil(start + GuestClock.FromSeconds(limitUs.Value / 1e6));
                        }

                        continue;
                    }
                }
                // blocked: give the emulated core away; take one back before touching guest state again (never while
                // holding sync, or a core-holding signaller could deadlock against us)
                if (self != null)
                {
                    Scheduler.Release(self);
                    self.WokenBy(null, 0, 0); // a thread that blocks is no longer ahead of an earlier waker
                }

                Interlocked.Increment(ref BlockingWaits);
                Monitor.Wait(sync, sliceMs);
                waited = true;
                // no core is taken back just to look again: on the Vita a waiting thread uses no core until it is woken
                // or times out. Taking one after every 50 ms slice put every waiting thread of the process back in the
                // core queue, and under strict priority the low-priority threads never got the core (the shell on its
                // one system core: ScePafJobDefault and its job queues 94-100% waiting for a core, no frame after 30 s)
            }
        }
        finally
        {
            if (self != null)
            {
                self.Status = ThreadStatus.Running;
                self.WaitingOn = "";
                self.CurrentWaitSync = null;
            }
        }
    }

    /// <summary>
    /// The waiting thread takes an emulated core back before it leaves the wait (or runs callbacks): never while it
    /// holds <paramref name="sync"/>, or a core-holding signaller that wants the lock could deadlock against it. True
    /// when it had to leave the lock: the caller then looks at the object again (with the core), because another
    /// thread may have changed it meanwhile (a mutex seen free and taken by a running thread: the TestGame's two-thread
    /// mutex lost increments when the first look was trusted).
    /// </summary>
    private static bool TakeCoreBack(object sync, GuestThread? self)
    {
        if (self == null || self.HasCore)
        {
            return false;
        }

        Monitor.Exit(sync);
        try
        {
            Scheduler.Acquire(self);
        }
        finally
        {
            Monitor.Enter(sync);
        }

        return true;
    }

    private static long ElapsedUs(long startTicks) => (long)((GuestClock.Ticks - startTicks) * 1_000_000 / Stopwatch.Frequency * TimeScale.Wait);

    /// <summary>A wait that started at <paramref name="startTicks"/> with a limit of <paramref name="limitUs"/> has run
    /// out of guest time (the clock and scale every wait's timeout uses).</summary>
    internal static bool Expired(long startTicks, long limitUs) => ElapsedUs(startTicks) >= limitUs;

    /// <summary>Writes back the time left of a wait that ended at <paramref name="endedTicks"/> (the signal that woke it;
    /// a value outside the wait, such as long.MinValue, means now).</summary>
    private static void WriteRemaining(Ptr<uint> timeout, long? limitUs, long startTicks, long endedTicks = long.MinValue)
    {
        if (timeout.IsNull || !limitUs.HasValue)
        {
            return;
        }

        long now = GuestClock.Ticks;
        long ended = endedTicks >= startTicks && endedTicks <= now ? endedTicks : now;
        long left = limitUs.Value - (long)((ended - startTicks) * 1_000_000 / Stopwatch.Frequency * TimeScale.Wait);
        timeout.Value = (uint)Math.Max(0, left);
    }

    /// <summary>Delays at least this long (microseconds) spin their last half millisecond to end on time (frame limiters
    /// sleep the rest of a frame); shorter ones only sleep: they are polling loops (DelayThread(1 ms) around a mutex,
    /// Ren'Py's timer and save threads), where the spin took half a host core per thread and the host timer's
    /// ~0.3 ms overshoot does not matter (the thread still resumes at the deadline in guest time, CpuCore.WokeAt). The
    /// spin then costs at most a tenth of a core. VITASHARP_DELAY_SPIN=1 spins on every delay (the old behaviour),
    /// =0 on none.</summary>
    private static readonly ulong s_delaySpinMinUs = Environment.GetEnvironmentVariable(EnvVars.DelaySpin) switch { "1" => 0, "0" => ulong.MaxValue, _ => 5000 };

    /// <summary>Statistics (PERF delays): guest delays per thread name, by length (0, up to 100 us, up to 1 ms, up to
    /// 5 ms, longer).</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long[]> Delays = new();

    private static void CountDelay(GuestThread t, ulong us)
    {
        long[] a = Delays.GetOrAdd(t.Name ?? "?", static _ => new long[8]);
        Interlocked.Increment(ref a[us == 0 ? 0 : us <= 100 ? 1 : us <= 1000 ? 2 : us <= 5000 ? 3 : 4]);
    }

    /// <summary>How far ahead of the clock a delay may start because of <see cref="GuestClock.ObservedFloor"/> (a paced
    /// thread runs up to ~1 ms ahead before it sleeps; the cap keeps a stray reading from stretching every delay).</summary>
    private static readonly long s_maxObservedLead = Stopwatch.Frequency / 200; // 5 ms

    /// <summary>Plain sleep that stays responsive to emulator shutdown.</summary>
    public static void Sleep(ulong microseconds)
    {
        GuestThread? self = GuestThread.Current;
        if (self != null)
        {
            CountDelay(self, microseconds);
        }

        if (microseconds == 0)
        {
            Thread.Yield();
            Latency();
            return;
        }
        if (microseconds < (ulong)SyscallLatencyUs)
        {
            microseconds = (ulong)SyscallLatencyUs; // a delay cannot be shorter than a reschedule
        }
        // guest time: follows the test speed factor, and turbo mode skips the wait
        Scheduler.Blocked blocked = Scheduler.Block();
        try
        {
            // the delay starts where the thread's emulated CPU time is, and it resumes at the deadline however late the
            // host wakes it (CpuCore.WokeAt): pacing sleeps and timer wake-ups overshoot by fractions of a millisecond
            // nor before the process time the guest has already read (GuestClock.Observed runs ahead of real time by
            // up to a paced thread's lead): on the Vita the process time measured around a delay is never shorter
            long start = Math.Max(self?.Core.BlockedAt ?? GuestClock.Ticks, Math.Min(GuestClock.ObservedFloor, GuestClock.Ticks + s_maxObservedLead));
            long end = start + GuestClock.FromSeconds(microseconds / 1e6 / TimeScale.Wait), slice = GuestClock.FromSeconds(0.05);
            bool spin = microseconds >= s_delaySpinMinUs, slept = false;
            // sceKernelDelayThreadCB: callbacks notified during the delay run inside it, as they arrive, and the delay
            // goes on. Jak and Daxter's vblank-callback thread sits in DelayThreadCB(0xFFFFFFFF) and counts vblanks in
            // its callback: run only on return, its callback never ran and the game's vsync counter stood still.
            bool callbacks = self != null && self.CanRunCallbacks && GuestThread.CallbackProcessor != null;
            while (GuestClock.Ticks < end)
            {
                if (self != null && self.Core.StopRequested)
                {
                    throw new GuestThreadExitException(0, false);
                }

                if (callbacks)
                {
                    if (Volatile.Read(ref self!.PendingCallbacks) > 0)
                    {
                        blocked.Dispose(); // the callback is guest code: it runs holding an emulated core
                        try
                        {
                            GuestThread.CallbackProcessor!(self);
                        }
                        finally
                        {
                            blocked = Scheduler.Block();
                        }
                        continue;
                    }
                    // Callback.Notify pulses the owner's WaitLock
                    long left = Math.Min(end, GuestClock.Ticks + slice) - GuestClock.Ticks;
                    int ms = (int)Math.Clamp(left * 1000.0 / System.Diagnostics.Stopwatch.Frequency / GuestClock.Speed + 1, 1, 50);
                    lock (self.WaitLock)
                    {
                        if (Volatile.Read(ref self.PendingCallbacks) == 0)
                        {
                            Monitor.Wait(self.WaitLock, ms);
                        }
                    }

                    continue;
                }
                // sleep in slices so a stop request is noticed; short delays really sleep instead of spinning a host core
                GuestClock.WaitUntil(Math.Min(end, GuestClock.Ticks + slice), spin);
                slept = true;
            }
            if (slept && self != null && Delays.TryGetValue(self.Name ?? "?", out long[]? st))
            {
                // statistics: delays that slept on the host, total requested and total overshoot past the deadline (us)
                Interlocked.Increment(ref st[5]);
                Interlocked.Add(ref st[6], (long)microseconds);
                Interlocked.Add(ref st[7], (long)((GuestClock.Ticks - end) * 1e6 / System.Diagnostics.Stopwatch.Frequency));
            }
            self?.Core.WokeAt(end);
        }
        finally
        {
            blocked.Dispose();
        }
    }
}

/// <summary>
/// sceKernel*Sema, as threadmgr does it (FW 3.74): a count and a wait queue, first come first served or, with
/// SCE_KERNEL_ATTR_TH_PRIO, by thread priority (first come first served among equal priorities). A signal hands the
/// units to the waiters in queue order, each one taking all it asked for, and stops at the first one they do not
/// cover; the waiters it reached return OK whenever their threads run again and never look at the count. A wait or a
/// poll takes units at once only when no waiter is ahead of it (any waiter in a FIFO semaphore, one of equal or higher
/// priority in a TH_PRIO one). A waiter whose timeout passes leaves the queue, and the units go on to the waiters behind
/// it (the class's wait-release handler); a cancel or a delete takes every waiter off the queue at once.
/// </summary>
public sealed partial class Semaphore : KernelObject
{
    public override int UidClass => 2;
    /// <summary>SCE_KERNEL_ATTR_TH_PRIO: waiters queue by thread priority instead of first come first served.</summary>
    public const uint AttrThreadPriority = 0x2000;
    public uint Attr;
    public int InitCount, Count, MaxCount;
    /// <summary>Threads in the wait queue (SceKernelSemaInfo.numWaitThreads).</summary>
    public int Waiters;
    private bool _deleted;

    /// <summary>One thread in sceKernelWaitSema.</summary>
    private sealed class WaitRequest
    {
        public int Need;
        public int Priority;
        public int ThreadUid;
        /// <summary>A signal (or a timed-out waiter ahead of it) gave it its units.</summary>
        public bool Granted;
        /// <summary>Off the queue without the units: WAIT_CANCEL, WAIT_DELETE or WAIT_TIMEOUT.</summary>
        public int Error;
        /// <summary>Guest time at the wait and its limit in microseconds (-1: none).</summary>
        public long StartTicks;
        public long LimitUs = -1;
    }

    /// <summary>The wait queue, in the order a signal serves it.</summary>
    private readonly List<WaitRequest> _queue = new();

    /// <summary>Waiters given their units that have not returned yet (their thread still has to wake up).</summary>
    private readonly List<WaitRequest> _granted = new();

    /// <summary>Waiters of a loaded save state whose threads have not issued their wait again yet: the reissued wait
    /// takes its place (and its outcome, if a signal or cancel reached it meanwhile).</summary>
    private readonly List<WaitRequest> _restored = new();

    private static int CurrentPriority() => GuestThread.Current?.Priority ?? 0;

    /// <summary>A waiter is ahead of a caller of this priority: a wait or poll may not take units before it.</summary>
    private bool WaiterAhead(int priority)
    {
        if (_queue.Count == 0)
        {
            return false;
        }

        return (Attr & AttrThreadPriority) == 0 || _queue[0].Priority <= priority;
    }

    public int WaitSema(int need, Ptr<uint> timeout)
    {
        if (need <= 0 || need > MaxCount)
        {
            return SceError.INVALID_ARGUMENT;
        }

        int result;
        bool woken;
        lock (this)
        {
            if (_deleted)
            {
                return SceError.WAIT_DELETE;
            }

            ReleaseExpired();
            GuestThread? self = GuestThread.Current;
            WaitRequest? request = TakeRestored(self);
            if (request != null && (request.Granted || request.Error != 0))
            {
                _granted.Remove(request);
                ObserveSignal();
                return request.Granted ? SceError.OK : request.Error;
            }

            if (request == null)
            {
                int priority = self?.Priority ?? 0;
                if (!WaiterAhead(priority) && Count >= need)
                {
                    Count -= need;
                    ObserveSignal();
                    return SceError.OK;
                }

                request = new WaitRequest { Need = need, Priority = priority, ThreadUid = self?.Uid ?? 0 };
                Enqueue(request);
                Waiters++;
            }

            request.StartTicks = GuestClock.Ticks;
            request.LimitUs = timeout.IsNull ? -1 : timeout.Value;
            try
            {
                result = Wait.UntilGranted(this, request, static r => r.Granted, timeout, static r => r.Granted ? 0 : r.Error,
                    WaitDescription, out woken);
            }
            finally
            {
                if (request.Granted)
                {
                    _granted.Remove(request);
                }
                else if (request.Error == 0 && _queue.Remove(request))
                {
                    // its own timeout (or the thread's end): the units go on to the waiters behind it
                    Waiters--;
                    if (Deliver())
                    {
                        Monitor.PulseAll(this);
                    }
                }
            }
            if (request.Granted)
            {
                result = SceError.OK; // a timeout or cancel that came after the units does not undo them
            }
            else if (request.Error == SceError.WAIT_TIMEOUT)
            {
                result = SceError.WAIT_TIMEOUT;
                timeout.TrySet(0);
            }
        }
        if (woken && result == SceError.OK)
        {
            Wait.WakeLatency();
        }

        return result;
    }

    private void Enqueue(WaitRequest request)
    {
        if ((Attr & AttrThreadPriority) == 0)
        {
            _queue.Add(request);
            return;
        }

        int index = 0;
        while (index < _queue.Count && _queue[index].Priority <= request.Priority)
        {
            index++;
        }

        _queue.Insert(index, request);
    }

    /// <summary>Hands the count to the waiters in queue order until the first one it does not cover. True when it
    /// reached one.</summary>
    private bool Deliver()
    {
        bool granted = false;
        while (_queue.Count > 0 && _queue[0].Need <= Count)
        {
            WaitRequest request = _queue[0];
            Count -= request.Need;
            request.Granted = true;
            _queue.RemoveAt(0);
            _granted.Add(request);
            Waiters--;
            granted = true;
        }

        return granted;
    }

    /// <summary>
    /// The kernel's timers at the waiters' guest-time deadlines: a waiter whose timeout has passed is off the queue
    /// (WAIT_TIMEOUT), however late its host thread is to notice, and each one leaving hands the count on to the
    /// waiters behind it, in the order the deadlines came. Called before every operation that looks at the queue.
    /// </summary>
    private void ReleaseExpired()
    {
        bool changed = false;
        while (true)
        {
            int earliest = -1;
            for (int i = 0; i < _queue.Count; i++)
            {
                WaitRequest request = _queue[i];
                if (request.LimitUs < 0 || !Wait.Expired(request.StartTicks, request.LimitUs))
                {
                    continue;
                }

                if (earliest < 0 || Deadline(request) < Deadline(_queue[earliest]))
                {
                    earliest = i;
                }
            }
            if (earliest < 0)
            {
                break;
            }

            _queue[earliest].Error = SceError.WAIT_TIMEOUT;
            _queue.RemoveAt(earliest);
            Waiters--;
            Deliver();
            changed = true;
        }
        if (changed)
        {
            Monitor.PulseAll(this); // the timed-out and the served waiters: nobody was woken by this thread
        }
    }

    /// <summary>A waiter's deadline, for the order the timers fire in (the limit in guest microseconds).</summary>
    private static double Deadline(WaitRequest request) => request.StartTicks + (double)request.LimitUs * System.Diagnostics.Stopwatch.Frequency / 1_000_000 / TimeScale.Wait;

    /// <summary>The place a loaded save state kept for the calling thread's wait, if any.</summary>
    private WaitRequest? TakeRestored(GuestThread? self)
    {
        if (_restored.Count == 0 || self == null)
        {
            return null;
        }

        for (int i = 0; i < _restored.Count; i++)
        {
            if (_restored[i].ThreadUid == self.Uid)
            {
                WaitRequest request = _restored[i];
                _restored.RemoveAt(i);
                return request;
            }
        }

        return null;
    }

    /// <summary>sceKernelPollSema: takes the units only when they are there and no waiter is ahead of the caller,
    /// else SEMA_ZERO.</summary>
    public int Poll(int need)
    {
        if (need <= 0 || need > MaxCount)
        {
            return SceError.INVALID_ARGUMENT;
        }

        lock (this)
        {
            ReleaseExpired();
            if (WaiterAhead(CurrentPriority()) || Count < need)
            {
                return SceError.SEMA_ZERO;
            }

            Count -= need;
            ObserveSignal();
            return SceError.OK;
        }
    }

    /// <summary>sceKernelSignalSema: adds the units (SEMA_OVF past the maximum, nothing changed) and hands them to
    /// the queue.</summary>
    public int Signal(int n)
    {
        lock (this)
        {
            ReleaseExpired();
            if (Count + n > MaxCount)
            {
                return SceError.SEMA_OVF;
            }

            Count += n;
            RecordSignal();
            if (Deliver())
            {
                WakeWaiters();
            }

            return SceError.OK;
        }
    }

    /// <summary>The count and waiters sceKernelGetSemaInfo reports (timed-out waiters already off the queue).</summary>
    internal (int Count, int Waiters) Info()
    {
        lock (this)
        {
            ReleaseExpired();
            return (Count, Waiters);
        }
    }

    /// <summary>sceKernelCancelSema: every waiter returns WAIT_CANCEL (numWait: how many), and the count becomes
    /// <paramref name="setCount"/> (negative: the initial count) without serving anyone. A count above the maximum is
    /// INVALID_ARGUMENT.</summary>
    public int Cancel(int setCount, Ptr<int> numWait)
    {
        if (setCount > MaxCount)
        {
            return SceError.INVALID_ARGUMENT;
        }

        lock (this)
        {
            ReleaseExpired();
            numWait.TrySet(Waiters);
            DequeueAll(SceError.WAIT_CANCEL);
            Count = setCount < 0 ? InitCount : setCount;
            WakeWaiters();
            return SceError.OK;
        }
    }

    public void Delete()
    {
        lock (this)
        {
            _deleted = true;
            DequeueAll(SceError.WAIT_DELETE);
            WakeWaiters();
        }
    }

    private void DequeueAll(int error)
    {
        foreach (WaitRequest request in _queue)
        {
            request.Error = error;
        }

        _queue.Clear();
        Waiters = 0;
    }
}

public sealed partial class EventFlag : KernelObject
{
    public override int UidClass => 3;
    public const uint WaitAnd = 0, WaitOr = 1, WaitClear = 2, WaitClearPat = 4;
    public const uint AttrMulti = 0x1000;
    /// <summary>SCE_KERNEL_ATTR_TH_PRIO: waiters queue by thread priority instead of first come first served.</summary>
    public const uint AttrThreadPriority = 0x2000;
    public uint Attr;
    public uint InitPattern, Pattern;
    /// <summary>Threads waiting and not satisfied yet (SceKernelEventFlagInfo.numWaitThreads).</summary>
    public int Waiters;
    private bool _deleted;
    private int _cancelled;

    /// <summary>One thread in sceKernelWaitEventFlag.</summary>
    private sealed class WaitRequest
    {
        public uint Bits;
        public uint Mode;
        public int Priority;
        public int CancelGeneration;
        /// <summary>A set satisfied it: the pattern it returns and the bits its clear mode took (save states).</summary>
        public bool Granted;
        public uint Result;
        public uint Consumed;
        /// <summary>A cancel or delete took it off the queue (it returns that error).</summary>
        public bool Dequeued;
        /// <summary>Guest time at the wait and its limit in microseconds (-1: none).</summary>
        public long StartTicks;
        public long LimitUs = -1;
        /// <summary>Its timeout had passed when a set reached it: off the queue, it returns WAIT_TIMEOUT.</summary>
        public bool Expired;
    }

    /// <summary>The wait queue, in the order the kernel checks it on a set.</summary>
    private readonly List<WaitRequest> _queue = new();

    /// <summary>Satisfied waiters that have not returned yet (their thread still has to wake up).</summary>
    private readonly List<WaitRequest> _granted = new();

    private bool Matches(uint bits, uint mode) => (mode & WaitOr) != 0 ? (Pattern & bits) != 0 : (Pattern & bits) == bits;

    /// <summary>A wait mode threadmgr accepts (FW 3.74 wait and poll): only the OR, CLEAR and CLEAR_PAT bits, and not
    /// both clears at once; any other mode is ILLEGAL_MODE.</summary>
    public static bool IsLegalMode(uint mode) => (mode & ~(WaitOr | WaitClear | WaitClearPat)) == 0
        && (mode & (WaitClear | WaitClearPat)) != (WaitClear | WaitClearPat);

    /// <summary>The checks threadmgr's wait makes before it looks the flag up: ILLEGAL_MODE, then INVALID_ARGUMENT for
    /// no bits.</summary>
    public static int CheckWaitArguments(uint bits, uint mode)
    {
        if (!IsLegalMode(mode))
        {
            return SceError.ILLEGAL_MODE;
        }

        return bits == 0 ? SceError.INVALID_ARGUMENT : SceError.OK;
    }

    /// <summary>The checks threadmgr's poll makes before it looks the flag up: INVALID_ARGUMENT for no bits, then
    /// ILLEGAL_MODE (the other order from the wait's).</summary>
    public static int CheckPollArguments(uint bits, uint mode)
    {
        if (bits == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        return IsLegalMode(mode) ? SceError.OK : SceError.ILLEGAL_MODE;
    }

    public int WaitFlag(uint bits, uint mode, Ptr<uint> outBits, Ptr<uint> timeout)
    {
        int invalid = CheckWaitArguments(bits, mode);
        if (invalid != SceError.OK)
        {
            return invalid;
        }

        int result;
        bool woken;
        lock (this)
        {
            if ((Attr & AttrMulti) == 0 && Waiters > 0)
            {
                return SceError.EVF_MULTI;
            }

            if (Matches(bits, mode))
            {
                outBits.TrySet(Pattern);
                Consume(bits, mode);
                ObserveSignal();
                return SceError.OK;
            }

            WaitRequest request = new()
            {
                Bits = bits,
                Mode = mode,
                Priority = GuestThread.Current?.Priority ?? 0,
                CancelGeneration = _cancelled,
                StartTicks = GuestClock.Ticks,
                LimitUs = timeout.IsNull ? -1 : timeout.Value,
            };
            Enqueue(request);
            Waiters++;
            try
            {
                result = Wait.UntilGranted(this, (flag: this, request), static t => t.request.Granted, timeout,
                    static t => t.request.Granted ? 0 : t.request.Expired ? SceError.WAIT_TIMEOUT : t.flag._deleted ? SceError.WAIT_DELETE : t.flag._cancelled != t.request.CancelGeneration ? SceError.WAIT_CANCEL : 0,
                    WaitDescription, out woken);
            }
            finally
            {
                if (request.Granted)
                {
                    _granted.Remove(request);
                }
                else if (!request.Dequeued)
                {
                    _queue.Remove(request);
                    Waiters--;
                }
            }
            if (request.Granted)
            {
                result = SceError.OK; // satisfied by a set: a timeout or cancel that came after it does not undo that
                outBits.TrySet(request.Result);
            }
            else if (request.Dequeued)
            {
                outBits.TrySet(request.Result); // the pattern when the cancel, delete or timeout woke it
                if (request.Expired)
                {
                    timeout.TrySet(0);
                }
            }
            else
            {
                outBits.TrySet(Pattern);
            }
        }
        if (woken && result == SceError.OK)
        {
            Wait.WakeLatency();
        }

        return result;
    }

    private void Enqueue(WaitRequest request)
    {
        if ((Attr & AttrThreadPriority) == 0)
        {
            _queue.Add(request);
            return;
        }

        int index = 0;
        while (index < _queue.Count && _queue[index].Priority <= request.Priority)
        {
            index++;
        }

        _queue.Insert(index, request);
    }

    private void Consume(uint bits, uint mode)
    {
        if ((mode & WaitClear) != 0)
        {
            Pattern = 0;
        }
        else if ((mode & WaitClearPat) != 0)
        {
            Pattern &= ~bits;
        }
    }

    /// <summary>sceKernelPollEventFlag. On a flag without SCE_EVENT_WAITMULTIPLE that a thread waits on, another
    /// thread's poll is refused like a second wait (EVF_MULTI, FW 3.74 threadmgr), without touching the pattern.</summary>
    public int Poll(uint bits, uint mode, Ptr<uint> outBits)
    {
        int invalid = CheckPollArguments(bits, mode);
        if (invalid != SceError.OK)
        {
            return invalid;
        }

        lock (this)
        {
            if ((Attr & AttrMulti) == 0 && Waiters > 0)
            {
                return SceError.EVF_MULTI;
            }

            outBits.TrySet(Pattern);
            if (!Matches(bits, mode))
            {
                return SceError.EVF_COND;
            }

            Consume(bits, mode);
            ObserveSignal();
            return SceError.OK;
        }
    }

    /// <summary>
    /// sceKernelSetEventFlag, as the kernel does it (threadmgr): ORs the bits in, then walks the wait queue in order and
    /// satisfies every waiter whose condition now holds, applying its clear mode right there (so the next waiter in
    /// the queue, and any call after this one, sees the pattern without the consumed bits), until the pattern is
    /// empty. A satisfied waiter returns that result whenever its thread runs again; it never re-checks the flag. A
    /// waiter whose timeout has passed in guest time is no longer in the kernel's queue (its timer took it off at the
    /// deadline), however late its host thread is to notice: it is not satisfied and returns WAIT_TIMEOUT.
    /// </summary>
    public void Set(uint bits)
    {
        lock (this)
        {
            uint patternBefore = Pattern;
            Pattern |= bits;
            RecordSignal();
            bool satisfied = false;
            bool expired = false;
            for (int i = 0; i < _queue.Count && Pattern != 0;)
            {
                WaitRequest request = _queue[i];
                if (request.LimitUs >= 0 && Wait.Expired(request.StartTicks, request.LimitUs))
                {
                    request.Expired = true;
                    request.Dequeued = true;
                    request.Result = patternBefore; // its timer fired before this set
                    _queue.RemoveAt(i);
                    Waiters--;
                    expired = true;
                    continue;
                }

                if (!Matches(request.Bits, request.Mode))
                {
                    i++;
                    continue;
                }

                uint before = Pattern;
                request.Result = Pattern;
                Consume(request.Bits, request.Mode);
                request.Consumed = before & ~Pattern;
                request.Granted = true;
                _queue.RemoveAt(i);
                _granted.Add(request);
                Waiters--;
                satisfied = true;
            }
            if (satisfied)
            {
                WakeWaiters();
            }
            else if (expired)
            {
                Monitor.PulseAll(this); // only timed-out waiters: nobody was woken by this thread
            }
        }
    }

    public void Clear(uint bits)
    {
        lock (this)
        {
            Pattern &= bits;
        }
    }

    public int Cancel(uint setPattern, Ptr<int> numWait)
    {
        lock (this)
        {
            numWait.TrySet(Waiters);
            Pattern = setPattern;
            _cancelled++;
            DequeueAll();
            WakeWaiters();
        }
        return SceError.OK;
    }

    public void Delete()
    {
        lock (this)
        {
            _deleted = true;
            DequeueAll();
            WakeWaiters();
        }
    }

    /// <summary>Cancel and delete release every waiter at once: a set that follows cannot satisfy them any more. Each
    /// one returns the pattern of this moment (threadmgr's wake writes the waiter's outBits under the flag's lock), not
    /// the one it finds when its thread runs again.</summary>
    private void DequeueAll()
    {
        foreach (WaitRequest request in _queue)
        {
            request.Dequeued = true;
            request.Result = Pattern;
        }

        _queue.Clear();
        Waiters = 0;
    }
}

/// <summary>
/// Mutex used both for SceKernelMutex and lightweight mutexes. A lightweight mutex's state lives in its guest work area
/// (SceKernelLwMutexWork), the only copy: +0 the owner's thread UID (0 = free; bit 31 <see cref="Contended"/>: a thread
/// sleeps on it), +8 the lock count, +12 the attributes, +16 the mutex UID (0 once deleted). As in the real SceLibKernel
/// (and the futex protocol of Linux's NPTL), an uncontended lock and unlock are atomic operations on that memory by
/// the caller itself, guest code that never enters the kernel (Hle/HleGuestCode: lock = compare-and-swap 0 -> own
/// UID, unlock = swap back to 0 unless a sleeper set <see cref="Contended"/>); only a lock that must wait and an
/// unlock that must wake someone come here. Both kinds keep the same kernel wait queue: a lightweight mutex's owner
/// word stays flagged while it has waiters, and the unlock that brings the count to 0 writes the head of the queue into
/// it as the owner (threadmgr 3.74's lightweight unlock), so the word is never free for guest code to take in between.
/// A normal mutex is threadmgr's (FW 3.74): a wait queue, first come first served or, with
/// SCE_KERNEL_MUTEX_ATTR_TH_PRIO, by thread priority (arrival order among equal priorities). The unlock that frees it
/// makes the head of the queue the owner with the count it asked for, inside the call; a lock or try-lock takes a free
/// mutex only when no waiter is ahead of it (a strictly higher priority caller of a TH_PRIO mutex). A count below 1,
/// or above 1 without SCE_KERNEL_MUTEX_ATTR_RECURSIVE, is ILLEGAL_COUNT in every call; a waiter whose timeout passes
/// leaves the queue at its guest-time deadline; a cancel or a delete takes every waiter off it at once.
/// </summary>
public sealed partial class Mutex : KernelObject
{
    public override int UidClass => IsLight ? 10 : 4;
    public const uint AttrRecursive = 0x02;
    /// <summary>SCE_KERNEL_MUTEX_ATTR_TH_PRIO: waiters queue by thread priority instead of first come first served.</summary>
    public const uint AttrThreadPriority = 0x2000;
    /// <summary>Owner word bit: at least one thread waits (or is about to) in <see cref="Lock"/>, so the owner's unlock
    /// must go through the kernel to wake it. Thread UIDs are positive, so the bit is free.</summary>
    public const uint Contended = 0x80000000;
    public const uint LwOwnerOffset = 0, LwCountOffset = 8, LwAttrOffset = 12, LwUidOffset = 16;
    public uint Attr;
    public int InitCount;
    private GuestThread? _owner;
    private int _lockCount;
    public int Waiters;
    public uint WorkArea; // lightweight mutex work area (0 for normal mutexes)
    /// <summary>The kernel's objects (lightweight mutexes: the owner word holds a UID, resolved through this).</summary>
    public ObjectTable? Threads;
    private bool _deleted;

    public bool IsLight => WorkArea != 0;

    /// <summary>The owner word for stores (its page is marked written).</summary>
    private ref int OwnerWord => ref GuestMemory.Ref<int>(WorkArea + LwOwnerOffset);
    /// <summary>The owner word, read without marking its page written.</summary>
    private unsafe int OwnerWordValue => Volatile.Read(ref *(int*)GuestMemory.Ptr(WorkArea + LwOwnerOffset));
    private uint OwnerUidWord => (uint)OwnerWordValue & ~Contended;

    /// <summary>The owning thread (a lightweight mutex: the UID in its work area).</summary>
    public GuestThread? Owner
    {
        get => IsLight ? (OwnerUidWord is var u and not 0 ? Threads?.Get<GuestThread>((int)u) : null) : _owner;
        set
        {
            if (IsLight)
            {
                Volatile.Write(ref OwnerWord, value?.Uid ?? 0);
            }
            else
            {
                _owner = value;
            }
        }
    }

    public int LockCount
    {
        get => IsLight ? (int)GuestMemory.R32(WorkArea + LwCountOffset) : _lockCount;
        set
        {
            if (IsLight)
            {
                GuestMemory.W32(WorkArea + LwCountOffset, (uint)value);
            }
            else
            {
                _lockCount = value;
            }
        }
    }

    public void InitOwner(GuestThread? t, int count)
    {
        lock (this)
        {
            if (count > 0)
            {
                Owner = t;
                LockCount = count;
            }
            else if (IsLight)
            {
                Owner = null;
                LockCount = 0;
            }
        }
    }

    private int FailedToOwn => IsLight ? SceError.LW_MUTEX_FAILED_TO_OWN : SceError.MUTEX_FAILED_TO_OWN;
    private int Recursive => IsLight ? SceError.LW_MUTEX_RECURSIVE : SceError.MUTEX_RECURSIVE;
    private int LockOverflow => IsLight ? SceError.LW_MUTEX_LOCK_OVF : SceError.MUTEX_LOCK_OVF;
    private int UnlockUnderflow => IsLight ? SceError.LW_MUTEX_UNLOCK_UDF : SceError.MUTEX_UNLOCK_UDF;
    private int NotOwned => IsLight ? SceError.LW_MUTEX_NOT_OWNED : SceError.MUTEX_NOT_OWNED;

    /// <summary>A lightweight mutex's owner word can be taken: free, or held by a thread that has ended.</summary>
    private bool LightFree(int w) => w == 0 || Threads?.Get<GuestThread>(w & (int)~Contended) is not { } o || o.Status == ThreadStatus.Dormant;

    /// <summary>The calling thread owns the mutex.</summary>
    private bool IsOwner(GuestThread self) => IsLight ? OwnerUidWord == (uint)self.Uid : _owner == self;

    /// <summary>No owner, or one that has ended.</summary>
    private bool Free => IsLight ? LightFree(OwnerWordValue) : OwnerFree;

    /// <summary>
    /// Makes <paramref name="threadUid"/> the owner of a free mutex with <paramref name="count"/> (under this object's
    /// lock). A lightweight mutex's owner word is compared and swapped from the free value seen, since a guest fast-path
    /// lock may take it meanwhile (false), and keeps <see cref="Contended"/> while the queue has waiters, so the owner's
    /// unlock comes to the kernel to hand it over.
    /// </summary>
    private bool TakeFree(GuestThread? thread, int threadUid, int count)
    {
        if (!IsLight)
        {
            if (!OwnerFree)
            {
                return false;
            }

            _owner = thread;
            _lockCount = count;
            return true;
        }

        while (true)
        {
            int w = OwnerWordValue;
            if (!LightFree(w))
            {
                return false;
            }

            int mine = threadUid | (_queue.Count > 0 ? unchecked((int)Contended) : 0);
            if (Interlocked.CompareExchange(ref OwnerWord, mine, w) == w)
            {
                LockCount = count;
                return true;
            }
        }
    }

    /// <summary>
    /// A lightweight mutex with a waiter just queued: flags the owner word <see cref="Contended"/> (the owner's guest
    /// fast-path unlock then comes to the kernel, which hands the mutex to the head of the queue). A word found free (its
    /// owner unlocked in guest code meanwhile, or ended) goes to the head of the queue at once. True when that handed
    /// it to a waiter.
    /// </summary>
    private bool FlagContended()
    {
        while (true)
        {
            int w = OwnerWordValue;
            if (LightFree(w))
            {
                if (HandOver())
                {
                    return true;
                }

                continue; // a guest fast-path lock took it meanwhile: flag the new owner
            }
            if ((w & unchecked((int)Contended)) != 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref OwnerWord, w | unchecked((int)Contended), w) == w)
            {
                return false;
            }
        }
    }

    /// <summary>One thread in sceKernelLockMutex or sceKernelLockLwMutex.</summary>
    private sealed class WaitRequest
    {
        public int Count;
        public int Priority;
        public int ThreadUid;
        /// <summary>The thread to make the owner (null for a restored waiter until it is resolved).</summary>
        public GuestThread? Thread;
        /// <summary>An unlock (or a cancel's or the owner's end) made it the owner.</summary>
        public bool Granted;
        /// <summary>Off the queue without the mutex: WAIT_CANCEL, WAIT_DELETE or WAIT_TIMEOUT.</summary>
        public int Error;
        /// <summary>Guest time at the wait and its limit in microseconds (-1: none).</summary>
        public long StartTicks;
        public long LimitUs = -1;
        /// <summary>A condition wait taking its mutex back: a save state reissues the cond wait, not this lock.</summary>
        public bool Reacquire;
    }

    /// <summary>The wait queue, in the order an unlock hands the mutex over.</summary>
    private readonly List<WaitRequest> _queue = new();

    /// <summary>Waiters made the owner that have not returned yet (their thread still has to wake up).</summary>
    private readonly List<WaitRequest> _granted = new();

    /// <summary>Waiters of a loaded save state whose threads have not issued their lock again yet.</summary>
    private readonly List<WaitRequest> _restored = new();

    /// <summary>threadmgr's count check (before the owner): a count below 1, or above 1 on a mutex without
    /// SCE_KERNEL_MUTEX_ATTR_RECURSIVE, is ILLEGAL_COUNT.</summary>
    private bool IllegalCount(int count) => count < 1 || (count > 1 && (Attr & AttrRecursive) == 0);

    /// <summary>A normal mutex with no owner, or one whose owner has ended.</summary>
    private bool OwnerFree => _owner == null || _owner.Status == ThreadStatus.Dormant;

    /// <summary>A waiter is ahead of a caller of this priority: the caller may not take a free mutex before it (any
    /// waiter of a FIFO mutex, one of equal or higher priority of a TH_PRIO one).</summary>
    private bool WaiterAhead(int priority)
    {
        if (_queue.Count == 0)
        {
            return false;
        }

        return (Attr & AttrThreadPriority) == 0 || _queue[0].Priority <= priority;
    }

    private void Enqueue(WaitRequest request)
    {
        if ((Attr & AttrThreadPriority) == 0)
        {
            _queue.Add(request);
            return;
        }

        int index = 0;
        while (index < _queue.Count && _queue[index].Priority <= request.Priority)
        {
            index++;
        }

        _queue.Insert(index, request);
    }

    /// <summary>Hands a free mutex to the head of the queue with the count it asked for. True when it did.</summary>
    private bool HandOver()
    {
        if (_queue.Count == 0 || !Free)
        {
            return false;
        }

        WaitRequest request = _queue[0];
        _queue.RemoveAt(0);
        if (!TakeFree(request.Thread, request.ThreadUid, request.Count))
        {
            _queue.Insert(0, request); // a guest fast-path lock took the lightweight mutex first
            return false;
        }

        Waiters--;
        request.Granted = true;
        _granted.Add(request);
        return true;
    }

    /// <summary>
    /// The owner's unlock that brings the count to 0 (threadmgr 3.74, the normal and the lightweight unlock alike): the
    /// head of the queue becomes the owner with the count it asked for inside the call, before any thread runs, so the
    /// mutex is never free in between for a caller to take ahead of the queue (a lightweight mutex's owner word goes
    /// straight from the old owner to the new one, as threadmgr writes the work area); with nobody queued it is free.
    /// </summary>
    private void ReleaseByOwner()
    {
        RecordSignal();
        if (_queue.Count > 0)
        {
            WaitRequest request = _queue[0];
            _queue.RemoveAt(0);
            Waiters--;
            if (IsLight)
            {
                // the word is the owner's, flagged Contended since a waiter queued: no guest code changes it meanwhile
                Volatile.Write(ref OwnerWord, request.ThreadUid | (_queue.Count > 0 ? unchecked((int)Contended) : 0));
                LockCount = request.Count;
            }
            else
            {
                _owner = request.Thread;
                _lockCount = request.Count;
            }

            request.Granted = true;
            _granted.Add(request);
            WakeWaiters();
            return;
        }

        if (IsLight)
        {
            LockCount = 0;
            Interlocked.Exchange(ref OwnerWord, 0);
        }
        else
        {
            _lockCount = 0;
            _owner = null;
        }
    }

    /// <summary>Wait predicate: an owner that ended without unlocking leaves the mutex to the head of the queue (the
    /// woken head may be another waiter). True when <paramref name="request"/> got it.</summary>
    private bool HandOverFromEndedOwner(WaitRequest request)
    {
        if (HandOver())
        {
            Monitor.PulseAll(this);
        }

        return request.Granted;
    }

    /// <summary>
    /// The kernel's timers at the waiters' guest-time deadlines: a waiter whose timeout has passed is off the queue
    /// (WAIT_TIMEOUT), however late its host thread is to notice, so a later unlock does not make it the owner.
    /// </summary>
    private void ReleaseExpired()
    {
        bool changed = false;
        for (int i = _queue.Count - 1; i >= 0; i--)
        {
            WaitRequest request = _queue[i];
            if (request.LimitUs < 0 || !Wait.Expired(request.StartTicks, request.LimitUs))
            {
                continue;
            }

            request.Error = SceError.WAIT_TIMEOUT;
            _queue.RemoveAt(i);
            Waiters--;
            changed = true;
        }
        if (changed)
        {
            HandOver();
            Monitor.PulseAll(this);
        }
    }

    /// <summary>The place a loaded save state kept for the calling thread's lock, if any.</summary>
    private WaitRequest? TakeRestored(GuestThread self)
    {
        for (int i = 0; i < _restored.Count; i++)
        {
            if (_restored[i].ThreadUid == self.Uid)
            {
                WaitRequest request = _restored[i];
                _restored.RemoveAt(i);
                return request;
            }
        }

        return null;
    }

    /// <summary>The lock count and waiters sceKernelGetMutexInfo reports (timed-out waiters already off the queue).</summary>
    internal (int Count, int Waiters, GuestThread? Owner) Info()
    {
        lock (this)
        {
            ReleaseExpired();
            return (LockCount, Waiters, Owner);
        }
    }

    /// <summary>
    /// sceKernelTryLockMutex / sceKernelTryLockLwMutex. A normal mutex checks the count first (ILLEGAL_COUNT), then the
    /// owner. A lightweight one in SceLibKernel's order (FW 3.74, user mode): a count below 1 is ILLEGAL_COUNT, then a
    /// held mutex is the owner's recursion (LW_MUTEX_RECURSIVE without the attribute) or LW_MUTEX_FAILED_TO_OWN, and only
    /// a free one refuses a count above 1 without the recursive attribute. Neither takes it ahead of the queue.
    /// </summary>
    public int TryLock(GuestThread self, int count)
    {
        lock (this)
        {
            if (IsLight ? count < 1 : IllegalCount(count))
            {
                return SceError.ILLEGAL_COUNT;
            }

            ReleaseExpired();
            if (IsOwner(self))
            {
                return LockAgain(count);
            }

            if (HandOver())
            {
                WakeWaiters(); // the owner had ended: the head of the queue takes it first
            }

            if (!Free)
            {
                return FailedToOwn;
            }

            if (IllegalCount(count))
            {
                return SceError.ILLEGAL_COUNT;
            }

            if (WaiterAhead(self.Priority) || !TakeFree(self, self.Uid, count))
            {
                return FailedToOwn;
            }

            return SceError.OK;
        }
    }

    /// <summary>The owner locks again: a recursive mutex adds the count (LOCK_OVF past int.MaxValue), any other is
    /// RECURSIVE.</summary>
    private int LockAgain(int count)
    {
        if ((Attr & AttrRecursive) == 0)
        {
            return Recursive;
        }

        int held = LockCount;
        if (held + count < 0)
        {
            return LockOverflow;
        }

        LockCount = held + count;
        return SceError.OK;
    }

    public int Lock(GuestThread self, int count, Ptr<uint> timeout)
    {
        return LockQueued(self, count, timeout, reacquire: false);
    }

    /// <summary>A condition wait takes its mutex back with the count it had (the cond's thread is not reissued as a
    /// lock by a save state: see <see cref="WaitRequest.Reacquire"/>).</summary>
    internal int Reacquire(GuestThread self, int count) => LockQueued(self, count, default, reacquire: true);

    /// <summary>
    /// sceKernelLockMutex and the kernel side of sceKernelLockLwMutex (threadmgr 3.74: the count check, the owner's
    /// recursion, then the wait queue). A lightweight lock gets here when its guest fast path could not take the work
    /// area (held, a count other than 1, a deleted mutex).
    /// </summary>
    private int LockQueued(GuestThread self, int count, Ptr<uint> timeout, bool reacquire)
    {
        int result;
        bool woken;
        lock (this)
        {
            if (IllegalCount(count))
            {
                return SceError.ILLEGAL_COUNT;
            }

            if (_deleted)
            {
                return SceError.WAIT_DELETE;
            }

            ReleaseExpired();
            WaitRequest? request = TakeRestored(self);
            if (request != null && (request.Granted || request.Error != 0))
            {
                _granted.Remove(request);
                ObserveSignal();
                return request.Granted ? SceError.OK : request.Error;
            }

            if (request == null)
            {
                if (IsOwner(self))
                {
                    return LockAgain(count);
                }

                if (!WaiterAhead(self.Priority) && TakeFree(self, self.Uid, count))
                {
                    ObserveSignal();
                    return SceError.OK;
                }

                request = new WaitRequest { Count = count, Priority = self.Priority, ThreadUid = self.Uid, Thread = self, Reacquire = reacquire };
                Enqueue(request);
                Waiters++;
            }

            request.Thread = self;
            if (IsLight && FlagContended())
            {
                WakeWaiters(); // the owner had unlocked in guest code: the head of the queue has it
            }

            request.StartTicks = GuestClock.Ticks;
            request.LimitUs = timeout.IsNull ? -1 : timeout.Value;
            try
            {
                // an owner that ends without unlocking leaves the mutex to the head of the queue: looked at on every
                // wake-up slice, as before the queue
                result = Wait.UntilGranted(this, (mutex: this, request), static s => s.request.Granted || s.mutex.HandOverFromEndedOwner(s.request),
                    timeout, static s => s.request.Granted ? 0 : s.request.Error, WaitDescription, out woken);
            }
            finally
            {
                if (request.Granted)
                {
                    _granted.Remove(request);
                }
                else if (request.Error == 0 && _queue.Remove(request))
                {
                    Waiters--; // its own timeout (or the thread's end)
                }
            }
            if (request.Granted)
            {
                result = SceError.OK; // a timeout or cancel that came after the hand-over does not undo it
            }
            else if (request.Error == SceError.WAIT_TIMEOUT)
            {
                result = SceError.WAIT_TIMEOUT;
                timeout.TrySet(0);
            }
        }
        if (woken && result == SceError.OK)
        {
            Wait.WakeLatency();
        }

        return result;
    }

    /// <summary>sceKernelUnlockMutex and the kernel side of sceKernelUnlockLwMutex (threadmgr 3.74): ILLEGAL_COUNT, then
    /// NOT_OWNED, then UNLOCK_UDF; the unlock that brings the count to 0 hands the mutex to the head of the queue.</summary>
    public int Unlock(GuestThread self, int count)
    {
        lock (this)
        {
            if (IllegalCount(count))
            {
                return SceError.ILLEGAL_COUNT;
            }

            ReleaseExpired();
            if (!IsOwner(self))
            {
                return NotOwned;
            }

            int held = LockCount;
            if (count > held)
            {
                return UnlockUnderflow;
            }

            if (held - count > 0)
            {
                LockCount = held - count;
            }
            else
            {
                ReleaseByOwner();
            }

            return SceError.OK;
        }
    }

    /// <summary>Fully releases the mutex (for condition waits) and returns the previous lock count.</summary>
    public int ReleaseAll(GuestThread self)
    {
        lock (this)
        {
            if (!IsOwner(self))
            {
                return -1;
            }

            int c = LockCount;
            ReleaseExpired();
            ReleaseByOwner();
            return c;
        }
    }

    /// <summary>sceKernelCancelMutex (threadmgr 3.74): a negative count is the initial count; a count above 1 needs
    /// SCE_KERNEL_MUTEX_ATTR_RECURSIVE (ILLEGAL_COUNT, nothing changed). Every waiter returns WAIT_CANCEL (numWait: how
    /// many) without the mutex, and the count goes to the caller (0: the mutex is free).</summary>
    public int Cancel(GuestThread? self, int newCount, Ptr<int> numWait)
    {
        lock (this)
        {
            if (newCount < 0)
            {
                newCount = InitCount;
            }

            if (newCount > 1 && (Attr & AttrRecursive) == 0)
            {
                return SceError.ILLEGAL_COUNT;
            }

            ReleaseExpired();
            numWait.TrySet(Waiters);
            DequeueAll(SceError.WAIT_CANCEL);
            LockCount = newCount;
            Owner = newCount > 0 ? self : null;
            WakeWaiters();
            return SceError.OK;
        }
    }

    public void Delete()
    {
        lock (this)
        {
            _deleted = true;
            DequeueAll(SceError.WAIT_DELETE);
            LockCount = 0;
            Owner = null;
            // the guest fast paths check the UID word: a deleted mutex's calls come to the kernel (UNKNOWN_LW_MUTEX_ID)
            if (IsLight)
            {
                GuestMemory.W32(WorkArea + LwUidOffset, 0);
            }

            WakeWaiters();
        }
    }

    private void DequeueAll(int error)
    {
        foreach (WaitRequest request in _queue)
        {
            request.Error = error;
        }

        Waiters -= _queue.Count;
        _queue.Clear();
    }
}

/// <summary>Condition variable bound to a mutex (SceKernelCond and lightweight conds). Waiters queue in arrival order;
/// a signal releases the first one, SignalTo a specific thread, SignalAll every waiter. A lightweight cond publishes the
/// number of queued waiters in its work area (+0 its UID, +4 its mutex's, +8 <see cref="LwQueuedOffset"/>), so a signal
/// with nobody to wake, the common case, is guest code that never enters the kernel (Hle/HleGuestCode). A waiter counts
/// itself there before its mutex is released: a signaller holding the mutex always sees it. Games may copy a work area
/// (MotorStorm RC waits through a copy on a thread's stack and signals through another one); only the work area the
/// cond was created in carries the count, so it also holds its own address (+12 <see cref="LwSelfOffset"/>) and the
/// guest fast path takes a copy (a different address there) to the kernel.</summary>
public sealed partial class CondVar : KernelObject
{
    public override int UidClass => WorkArea != 0 ? 11 : 5;
    public uint Attr;
    public Mutex Mutex = null!;
    public uint WorkArea;
    public int Waiters;
    private bool _deleted;
    public const uint LwQueuedOffset = 8, LwSelfOffset = 12;

    private void PublishQueued(int n)
    {
        if (WorkArea != 0)
        {
            GuestMemory.W32(WorkArea + LwQueuedOffset, (uint)n);
        }
    }

    private sealed class Waiter
    {
        public GuestThread Thread = null!;
        public bool Released;
    }

    private readonly LinkedList<Waiter> _queue = new();
    /// <summary>Queue nodes of finished waits, reused by later waits (guarded by this object's lock).</summary>
    private readonly Stack<LinkedListNode<Waiter>> _freeNodes = new();

    public int WaitCond(GuestThread self, Ptr<uint> timeout)
    {
        int count;
        lock (this)
        {
            PublishQueued(_queue.Count + 1); // this waiter, before its mutex goes
            // a wait reissued by a save state load released its mutex before the state was saved
            count = self._condReissueCount > 0 ? self._condReissueCount : Mutex.ReleaseAll(self);
            self._condReissueCount = 0;
            if (count < 0)
            {
                PublishQueued(_queue.Count);
                return Mutex.IsLight ? SceError.LW_MUTEX_NOT_OWNED : SceError.MUTEX_NOT_OWNED;
            }
            self._condWaitCount = count; // save states: the lock count the reissued wait gives back
            Waiters++;
            LinkedListNode<Waiter> node = _freeNodes.Count > 0 ? _freeNodes.Pop() : new LinkedListNode<Waiter>(new Waiter());
            Waiter w = node.Value;
            w.Thread = self;
            w.Released = false;
            _queue.AddLast(node);
            int r;
            try
            {
                r = Wait.Until(this, (c: this, w), static t => t.w.Released, timeout,
                    static t => t.c._deleted ? SceError.WAIT_DELETE : 0, WaitDescription);
            }
            finally
            {
                Waiters--;
                if (!w.Released)
                {
                    _queue.Remove(node);
                    PublishQueued(_queue.Count);
                }
                w.Thread = null!;
                if (_freeNodes.Count < 64)
                {
                    _freeNodes.Push(node);
                }
            }
            // reacquire outside our lock to avoid lock-order inversion with the mutex
            Monitor.Exit(this);
            try
            {
                Mutex.Reacquire(self, count);
            }
            finally
            {
                Monitor.Enter(this);
            }
            return r;
        }
    }

    private void Release(LinkedListNode<Waiter> node)
    {
        node.Value.Released = true;
        _queue.Remove(node);
        PublishQueued(_queue.Count);
    }

    public void Signal()
    {
        lock (this)
        {
            if (_queue.First is { } first)
            {
                Release(first);
            }

            WakeWaiters();
        }
    }

    /// <summary>Releases <paramref name="target"/> if it waits on this condition; false if it does not.</summary>
    public bool SignalTo(GuestThread target)
    {
        lock (this)
        {
            for (LinkedListNode<Waiter>? n = _queue.First; n != null; n = n.Next)
            {
                if (n.Value.Thread != target)
                {
                    continue;
                }

                Release(n);
                WakeWaiters();
                return true;
            }
            return false;
        }
    }

    public void SignalAll()
    {
        lock (this)
        {
            while (_queue.First is { } first)
            {
                Release(first);
            }

            WakeWaiters();
        }
    }

    public void Delete()
    {
        lock (this)
        {
            _deleted = true;
            // the guest fast paths check the UID word: a deleted cond's signals come to the kernel (UNKNOWN_LW_COND_ID)
            if (WorkArea != 0)
            {
                GuestMemory.W32(WorkArea, 0);
            }

            WakeWaiters();
        }
    }
}

public sealed partial class MsgPipe : KernelObject
{
    public override int UidClass => 7;
    /// <summary>SCE_KERNEL_MSG_PIPE_MODE_*: ASAP (0, move what fits / what is there) or FULL (1, the whole size),
    /// plus DONT_WAIT (0x10): on the blocking forms the kernel does the transfer later and the call returns at once
    /// (<see cref="TransferLater"/>). Not the PSP's order (there 0 was \"all\"): Jak and Daxter's renderer thread
    /// receives with mode 0 into a 0x600-byte buffer and waited forever for 12-byte messages.</summary>
    public const uint WaitAsap = 0, WaitAll = 1, DontWait = 0x10;

    /// <summary>The mode bits threadmgr accepts (any other: ILLEGAL_MODE).</summary>
    public const uint ValidModes = WaitAll | DontWait;

    /// <summary>A transfer the kernel does later for a DONT_WAIT call (one of threadmgr's "SceKernelMsgPipeAsync"
    /// requests).</summary>
    private sealed class LaterTransfer(bool send, Segments buffers, uint mode, Ptr<uint> result, Action completed)
    {
        public readonly bool Send = send;
        public readonly Segments Buffers = buffers;
        public readonly bool Asap = (mode & WaitAll) == 0;
        public readonly Ptr<uint> Result = result;
        public readonly Action Completed = completed;
        public int Done;
    }

    private readonly List<LaterTransfer> _later = new();
    public uint Attr;
    public int BufferSize;
    /// <summary>UID of the thread that created the pipe (sceKernelGetMsgPipeCreatorId).</summary>
    public int CreatorThreadUid;
    private readonly Queue<byte> _data = new();
    public int SendWaiters, RecvWaiters;
    public int FreeSize
    {
        get
        {
            lock (this)
            {
                return Math.Max(0, BufferSize - _data.Count);
            }
        }
    }
    private bool _deleted;
    private int _cancelled;
    /// <summary>Deleted while a handle opened by name was still open (<see cref="ObjectTable.Delete(MsgPipe)"/>).</summary>
    public bool IsDeleted
    {
        get
        {
            lock (this)
            {
                return _deleted;
            }
        }
    }

    /// <summary>Guest buffers taken as one contiguous byte range in order (MsgPipeVector entries {base, size}).</summary>
    public readonly struct Segments(uint[] addrs, int[] sizes)
    {
        public readonly uint[] Addrs = addrs;
        public readonly int[] Sizes = sizes;
        public int Total => Sizes.Sum();
        public static Segments One(uint addr, int size) => new([addr], [size]);

        /// <summary>Visits the guest spans covering [offset, offset + count).</summary>
        public void ForEach(int offset, int count, Action<uint, int> f)
        {
            for (int i = 0; i < Addrs.Length && count > 0; i++)
            {
                if (offset >= Sizes[i])
                {
                    offset -= Sizes[i];
                    continue;
                }
                int n = Math.Min(Sizes[i] - offset, count);
                f(Addrs[i] + (uint)offset, n);
                count -= n;
                offset = 0;
            }
        }
    }

    public int Send(uint src, int size, uint mode, Ptr<uint> result, Ptr<uint> timeout, bool poll)
        => Send(Segments.One(src, size), mode, result, timeout, poll);

    public int Send(Segments src, uint mode, Ptr<uint> result, Ptr<uint> timeout, bool poll)
    {
        int size = src.Total;
        lock (this)
        {
            if (_deleted)
            {
                result.TrySet(0);
                return SceError.MPP_DELETED;
            }

            // threadmgr refuses a message larger than the pipe's buffer, in either mode; a pipe without a buffer takes
            // any size (FW 3.74 send, 0x81013DFC: the size check is skipped for capacity 0)
            if (!Unbuffered && size > BufferSize)
            {
                return SceError.ILLEGAL_SIZE;
            }

            int gen = _cancelled;
            int sent = 0;
            bool asap = (mode & WaitAll) == 0;
            SendWaiters++;
            try
            {
                if (Unbuffered)
                {
                    return SendUnbuffered(src, size, asap, result, timeout, poll, gen);
                }

                while (sent < size)
                {
                    // ASAP moves what fits; FULL goes into the buffer only whole (threadmgr copies the rest of a FULL
                    // message only when all of it fits, and the Try form fails with MPP_FULL without sending any)
                    int free = BufferSize - _data.Count;
                    if (asap ? free > 0 : free >= size - sent)
                    {
                        int n = Math.Min(free, size - sent);
                        src.ForEach(sent, n, (a, len) =>
                        {
                            foreach (byte b in GuestMemory.Span(a, len))
                            {
                                _data.Enqueue(b);
                            }
                        });
                        sent += n;
                        WakeWaiters();
                        if (asap)
                        {
                            break;
                        }

                        continue;
                    }
                    if (poll)
                    {
                        break;
                    }

                    int need = asap ? 1 : size - sent;
                    int r = Wait.Until(this, (p: this, gen, need), static t => t.p.BufferSize - t.p._data.Count >= t.need, timeout,
                        static t => t.p._deleted ? SceError.WAIT_DELETE : t.p._cancelled != t.gen ? SceError.WAIT_CANCEL : 0, WaitDescription);
                    if (r != SceError.OK)
                    {
                        result.TrySet((uint)sent);
                        return r;
                    }
                }
            }
            finally
            {
                SendWaiters--;
            }
            result.TrySet((uint)sent);
            ServeLater();
            if (asap ? sent == 0 && size > 0 : sent < size)
            {
                return SceError.MPP_FULL;
            }

            return SceError.OK;
        }
    }

    public int Receive(uint dst, int size, uint mode, Ptr<uint> result, Ptr<uint> timeout, bool poll)
        => Receive(Segments.One(dst, size), mode, result, timeout, poll);

    public int Receive(Segments dst, uint mode, Ptr<uint> result, Ptr<uint> timeout, bool poll)
    {
        int size = dst.Total;
        lock (this)
        {
            if (_deleted)
            {
                result.TrySet(0);
                return SceError.MPP_DELETED;
            }
            int gen = _cancelled;
            int got = 0;
            bool asap = (mode & WaitAll) == 0;
            RecvWaiters++;
            try
            {
                while (got < size)
                {
                    if (_data.Count > 0 || OfferedBytes > 0)
                    {
                        int n = Math.Min(_data.Count, size - got);
                        dst.ForEach(got, n, (a, len) =>
                        {
                            Span<byte> span = GuestMemory.Span(a, len);
                            for (int i = 0; i < len; i++)
                            {
                                span[i] = _data.Dequeue();
                            }
                        });
                        got += n;
                        // a pipe without a buffer: the rest straight from the senders waiting with their data
                        got += TakeOffered(dst, got, size - got);
                        WakeWaiters();
                        if (asap)
                        {
                            break;
                        }

                        continue;
                    }
                    if (poll)
                    {
                        break;
                    }

                    // a waiting receiver of a pipe without a buffer is where a sender's data can go
                    int demand = Unbuffered ? size - got : 0;
                    _receiveDemand += demand;
                    if (demand > 0)
                    {
                        ServeLater();
                        WakeWaiters();
                    }

                    int r = Wait.Until(this, (p: this, gen), static t => t.p._data.Count > 0 || t.p.OfferedBytes > 0, timeout,
                        static t => t.p._deleted ? SceError.WAIT_DELETE : t.p._cancelled != t.gen ? SceError.WAIT_CANCEL : 0, WaitDescription);
                    _receiveDemand -= demand;
                    if (r != SceError.OK)
                    {
                        result.TrySet((uint)got);
                        return r;
                    }
                }
            }
            finally
            {
                RecvWaiters--;
            }
            result.TrySet((uint)got);
            ServeLater();
            if (got == 0 && size > 0)
            {
                return SceError.MPP_EMPTY;
            }

            return SceError.OK;
        }
    }

    /// <summary>
    /// A pipe created with size 0 has no buffer (FW 3.74 threadmgr allocates no memory block for it and skips the size
    /// check of a send): a send hands its data straight to receivers. Here the bytes the waiting receivers asked for
    /// go through <see cref="_data"/> at once (only those receivers can take them), and a sender that has more waits
    /// with the rest offered (<see cref="Offer"/>), which a receiver, a polling one too, copies from the sender's own
    /// buffers.
    /// </summary>
    private bool Unbuffered => BufferSize == 0;

    /// <summary>Bytes the receivers blocked on a pipe without a buffer still want.</summary>
    private int _receiveDemand;

    /// <summary>The rest of a blocked send on a pipe without a buffer, taken by receivers from the sender's buffers.</summary>
    private sealed class Offer(Segments source, int start, int size)
    {
        public readonly Segments Source = source;
        public readonly int Start = start;
        public readonly int Size = size;
        public int Taken;
    }

    private readonly List<Offer> _offers = new();

    private int OfferedBytes
    {
        get
        {
            int total = 0;
            foreach (Offer offer in _offers)
            {
                total += offer.Size - offer.Taken;
            }

            return total;
        }
    }

    /// <summary>Copies up to <paramref name="count"/> offered bytes, oldest sender first, to <paramref name="dst"/> at
    /// <paramref name="offset"/>; returns how many.</summary>
    private int TakeOffered(Segments dst, int offset, int count)
    {
        int moved = 0;
        foreach (Offer offer in _offers)
        {
            int n = Math.Min(offer.Size - offer.Taken, count - moved);
            if (n <= 0)
            {
                continue;
            }

            byte[] bytes = System.Buffers.ArrayPool<byte>.Shared.Rent(n);
            int read = 0;
            offer.Source.ForEach(offer.Start + offer.Taken, n, (address, length) =>
            {
                GuestMemory.Span(address, length).CopyTo(bytes.AsSpan(read));
                read += length;
            });
            int written = 0;
            dst.ForEach(offset + moved, n, (address, length) =>
            {
                bytes.AsSpan(written, length).CopyTo(GuestMemory.Span(address, length));
                written += length;
            });
            System.Buffers.ArrayPool<byte>.Shared.Return(bytes);
            offer.Taken += n;
            moved += n;
        }

        return moved;
    }

    /// <summary>A send on a pipe without a buffer (<see cref="Unbuffered"/>): what the waiting receivers asked for goes
    /// at once; a blocking send then waits with the rest offered until a receiver took some (ASAP) or all (FULL) of it.
    /// The Try forms move only what waiting receivers asked for (MPP_FULL when that is nothing, or, FULL, not all).</summary>
    private int SendUnbuffered(Segments src, int size, bool asap, Ptr<uint> result, Ptr<uint> timeout, bool poll, int gen)
    {
        int room = Math.Max(0, _receiveDemand - _data.Count);
        // a Try FULL send moves its message only whole, as on a pipe with a buffer
        int sent = poll && !asap && room < size ? 0 : Math.Min(room, size);
        if (sent > 0)
        {
            src.ForEach(0, sent, (address, length) =>
            {
                foreach (byte value in GuestMemory.Span(address, length))
                {
                    _data.Enqueue(value);
                }
            });
            WakeWaiters();
        }

        if (sent < size && !(asap && sent > 0) && !poll)
        {
            var offer = new Offer(src, sent, size - sent);
            _offers.Add(offer);
            WakeWaiters();
            int r = Wait.Until(this, (p: this, offer, asap, gen), static t => t.asap ? t.offer.Taken > 0 : t.offer.Taken == t.offer.Size, timeout,
                static t => t.p._deleted ? SceError.WAIT_DELETE : t.p._cancelled != t.gen ? SceError.WAIT_CANCEL : 0, WaitDescription);
            _offers.Remove(offer);
            sent += offer.Taken;
            if (r != SceError.OK)
            {
                result.TrySet((uint)sent);
                return r;
            }
        }

        result.TrySet((uint)sent);
        if (asap ? sent == 0 && size > 0 : sent < size)
        {
            return SceError.MPP_FULL;
        }

        return SceError.OK;
    }

    public int Cancel(Ptr<int> numSend, Ptr<int> numRecv)
    {
        lock (this)
        {
            numSend.TrySet(SendWaiters + _later.Count(t => t.Send));
            numRecv.TrySet(RecvWaiters + _later.Count(t => !t.Send));
            _cancelled++;
            EndLater();
            WakeWaiters();
        }
        return SceError.OK;
    }

    public void Delete()
    {
        lock (this)
        {
            _deleted = true;
            EndLater();
            WakeWaiters();
        }
    }

    /// <summary>
    /// DONT_WAIT on a blocking send or receive (FW 3.74 threadmgr, the vector forms at 0x81015004 / 0x8101533C): the
    /// kernel copies the buffer list, queues the transfer for its "SceKernelMsgPipeAsync" threads (which run it as a
    /// blocking call at the caller's priority) and the call returns at once with success; the data moves when the
    /// pipe allows, as the blocking call would have moved it. <paramref name="result"/> (null for none) gets the bytes
    /// moved when the transfer ends; <paramref name="completed"/> runs then, also when a cancel or delete ends it.
    /// </summary>
    public void TransferLater(bool send, Segments buffers, uint mode, Ptr<uint> result, Action completed)
    {
        lock (this)
        {
            if (_deleted)
            {
                result.TrySet(0);
                completed();
                return;
            }

            _later.Add(new LaterTransfer(send, buffers, mode, result, completed));
            ServeLater();
        }
    }

    /// <summary>The transfers queued by <see cref="TransferLater"/> still waiting for the pipe.</summary>
    public int LaterCount
    {
        get
        {
            lock (this)
            {
                return _later.Count;
            }
        }
    }

    /// <summary>Moves what the queued transfers can move now, in queue order, until none can go further.</summary>
    private void ServeLater()
    {
        bool moved = true;
        while (moved && _later.Count > 0)
        {
            moved = false;
            foreach (LaterTransfer transfer in _later.ToList())
            {
                int size = transfer.Buffers.Total;
                int count = transfer.Send ? SendNow(transfer, size) : ReceiveNow(transfer, size);
                if (count == 0)
                {
                    continue;
                }

                transfer.Done += count;
                moved = true;
                if (transfer.Asap || transfer.Done >= size)
                {
                    _later.Remove(transfer);
                    transfer.Result.TrySet((uint)transfer.Done);
                    transfer.Completed();
                }
            }
            if (moved)
            {
                WakeWaiters();
            }
        }
    }

    /// <summary>A queued send's bytes that fit now (FULL: only the whole rest at once, as the blocking send).</summary>
    private int SendNow(LaterTransfer transfer, int size)
    {
        int free = (Unbuffered ? _receiveDemand : BufferSize) - _data.Count;
        int rest = size - transfer.Done;
        if (rest <= 0 || (transfer.Asap ? free <= 0 : free < rest))
        {
            return 0;
        }

        int count = Math.Min(free, rest);
        transfer.Buffers.ForEach(transfer.Done, count, (address, length) =>
        {
            foreach (byte value in GuestMemory.Span(address, length))
            {
                _data.Enqueue(value);
            }
        });
        return count;
    }

    /// <summary>A queued receive's bytes that are there now (FULL keeps taking until it has them all).</summary>
    private int ReceiveNow(LaterTransfer transfer, int size)
    {
        int count = Math.Min(_data.Count, size - transfer.Done);
        if (count <= 0)
        {
            return 0;
        }

        transfer.Buffers.ForEach(transfer.Done, count, (address, length) =>
        {
            Span<byte> span = GuestMemory.Span(address, length);
            for (int i = 0; i < length; i++)
            {
                span[i] = _data.Dequeue();
            }
        });
        return count;
    }

    /// <summary>A cancel or delete ends the queued transfers with what they moved.</summary>
    private void EndLater()
    {
        foreach (LaterTransfer transfer in _later)
        {
            transfer.Result.TrySet((uint)transfer.Done);
            transfer.Completed();
        }
        _later.Clear();
    }

    // ---- sceKernelWaitEvent / PollEvent on a message pipe (SCE_KERNEL_EVENT_*): IN while data can be received, OUT
    // while there is room to send, DELETE once deleted, and OPEN / CLOSE when a handle was opened or closed by name
    // (latched until a wait reports them). The Browser's web engine (SceWebKitProcess) creates its IPC pipe and waits
    // for OPEN | DELETE until the Browser opens it; the wait failed with UNKNOWN_EVENT_ID and its IPC thread ended.
    public const uint EventIn = 0x1, EventOut = 0x2, EventOpen = 0x100, EventClose = 0x200;
    /// <summary>The message pipe's own "can receive" / "can send" bits: the web engine's IPC loop waits for 0x10218
    /// (received data | CLOSE | ERROR | DELETE).</summary>
    public const uint EventReceivable = 0x10000, EventSendable = 0x20000;
    private uint _latched;

    private uint Pattern => (_data.Count > 0 || OfferedBytes > 0 ? EventIn | EventReceivable : 0)
        | (_data.Count < (Unbuffered ? _receiveDemand : BufferSize) ? EventOut | EventSendable : 0)
        | (_deleted ? EventBits.Delete : 0) | _latched;

    /// <summary>A handle to the pipe was opened or closed (<see cref="EventOpen"/>, <see cref="EventClose"/>).</summary>
    public void Signal(uint ev)
    {
        lock (this)
        {
            _latched |= ev;
            WakeWaiters();
        }
    }

    private int TakeEvent(uint waitPattern, Ptr<uint> result)
    {
        uint hit = Pattern & waitPattern;
        result.TrySet(hit);
        if (hit == 0)
        {
            return SceError.EVENT_COND;
        }

        _latched &= ~hit;
        return SceError.OK;
    }

    public int PollEvent(uint waitPattern, Ptr<uint> result)
    {
        if (waitPattern == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        lock (this)
        {
            return TakeEvent(waitPattern, result);
        }
    }

    public int WaitEvent(uint waitPattern, Ptr<uint> result, Ptr<uint> timeout)
    {
        if (waitPattern == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        lock (this)
        {
            int gen = _cancelled;
            int r = Wait.Until(this, (p: this, waitPattern, gen), static t => (t.p.Pattern & t.waitPattern) != 0, timeout,
                static t => t.p._cancelled != t.gen ? SceError.WAIT_CANCEL : 0, WaitDescription);
            if (r != SceError.OK)
            {
                result.TrySet(r == SceError.WAIT_TIMEOUT ? 0 : EventBits.Error);
                return r;
            }
            return TakeEvent(waitPattern, result);
        }
    }
}

public sealed partial class RwLock : KernelObject
{
    public override int UidClass => 12;
    public uint Attr;
    public int Readers;
    public GuestThread? Writer;
    public int WriteCount;
    public int ReadWaiters, WriteWaiters;
    private bool _deleted;
    private int _cancelled;

    public int LockRead(GuestThread self, Ptr<uint> timeout)
    {
        lock (this)
        {
            ReadWaiters++;
            int gen = _cancelled;
            try
            {
                int r = Wait.Until(this, (l: this, self, gen), static t => t.l.Writer == null || t.l.Writer == t.self, timeout,
                    static t => t.l._deleted ? SceError.WAIT_DELETE : t.l._cancelled != t.gen ? SceError.WAIT_CANCEL : 0, WaitDescription);
                if (r == SceError.OK)
                {
                    Readers++;
                }

                return r;
            }
            finally
            {
                ReadWaiters--;
            }
        }
    }

    public int TryLockRead(GuestThread self)
    {
        lock (this)
        {
            if (Writer != null && Writer != self)
            {
                return SceError.RW_LOCK_FAILED_TO_LOCK;
            }

            Readers++;
            return SceError.OK;
        }
    }

    public int UnlockRead()
    {
        lock (this)
        {
            if (Readers == 0)
            {
                return SceError.RW_LOCK_UNLOCK_UDF;
            }

            Readers--;
            WakeWaiters();
            return SceError.OK;
        }
    }

    public int LockWrite(GuestThread self, Ptr<uint> timeout)
    {
        lock (this)
        {
            if (Writer == self)
            {
                WriteCount++;
                return SceError.OK;
            }
            WriteWaiters++;
            int gen = _cancelled;
            try
            {
                int r = Wait.Until(this, (l: this, gen), static t => t.l.Writer == null && t.l.Readers == 0, timeout,
                    static t => t.l._deleted ? SceError.WAIT_DELETE : t.l._cancelled != t.gen ? SceError.WAIT_CANCEL : 0, WaitDescription);
                if (r == SceError.OK)
                {
                    Writer = self;
                    WriteCount = 1;
                }
                return r;
            }
            finally
            {
                WriteWaiters--;
            }
        }
    }

    public int TryLockWrite(GuestThread self)
    {
        lock (this)
        {
            if (Writer == self)
            {
                WriteCount++;
                return SceError.OK;
            }
            if (Writer != null || Readers > 0)
            {
                return SceError.RW_LOCK_FAILED_TO_LOCK;
            }

            Writer = self;
            WriteCount = 1;
            return SceError.OK;
        }
    }

    public int UnlockWrite(GuestThread self)
    {
        lock (this)
        {
            if (Writer != self)
            {
                return SceError.RW_LOCK_NOT_OWNED;
            }

            if (--WriteCount == 0)
            {
                Writer = null;
            }

            WakeWaiters();
            return SceError.OK;
        }
    }

    /// <summary>Wakes every waiter with WAIT_CANCEL and resets the lock; with <paramref name="keepWriteLock"/> the caller
    /// ends up holding it for writing (SCE_KERNEL_RW_LOCK_CANCEL_WITH_WRITE_LOCK).</summary>
    public int Cancel(GuestThread self, Ptr<uint> numRead, Ptr<uint> numWrite, bool keepWriteLock)
    {
        lock (this)
        {
            numRead.TrySet((uint)ReadWaiters);
            numWrite.TrySet((uint)WriteWaiters);
            _cancelled++;
            Readers = 0;
            Writer = keepWriteLock ? self : null;
            WriteCount = keepWriteLock ? 1 : 0;
            WakeWaiters();
            return SceError.OK;
        }
    }

    public void Delete()
    {
        lock (this)
        {
            _deleted = true;
            WakeWaiters();
        }
    }
}

public sealed partial class Callback : KernelObject
{
    public override int UidClass => 8;
    public GuestThread Owner = null!;
    public uint Function;
    public uint Arg;
    public uint Attr;
    public int NotifyCount;
    public int NotifyArg;
    /// <summary>UID of the thread that sent the last notification.</summary>
    public int NotifyId;

    public void Notify(int arg)
    {
        lock (this)
        {
            NotifyCount++;
            NotifyArg = arg;
            NotifyId = GuestThread.Current?.Uid ?? 0;
        }
        Interlocked.Increment(ref Owner.PendingCallbacks);
        lock (Owner.WaitLock)
        {
            Monitor.PulseAll(Owner.WaitLock);
        }

        object? s = Owner.CurrentWaitSync;
        if (s != null)
        {
            lock (s)
            {
                Monitor.PulseAll(s);
            }
        }
    }
}

/// <summary>Event bits shared by simple events and timers (psp2kern/kernel/threadmgr.h).</summary>
public static class EventBits
{
    public const uint Create = 0x4, Delete = 0x8, Error = 0x10, Timer = 0x8000, UserMask = 0xFF000000;
    public const uint AttrAutoReset = 0x100, AttrOpenable = 0x80, AttrThPrio = 0x2000;
    /// <summary>SCE_KERNEL_ATTR_NOTIFY_CB_WAKEUP_ONLY: accepted at creation (Injustice's NRS async IO events use 0x900);
    /// it only affects which callbacks a CB wait runs, nothing reads it here (Vita3K neither).</summary>
    public const uint AttrNotifyCbWakeupOnly = 0x800;
}

/// <summary>
/// Kernel timer: a microsecond counter on process time plus an optional (repeating) SET or PULSE event that
/// sceKernelWaitEvent/PollEvent can wait for (result EVENT_TIMER). Only the first waiter waits for the event.
/// </summary>
public sealed partial class TimerObject : KernelObject
{
    public override int UidClass => 6;
    public uint Attr;
    /// <summary>Process time in microseconds (sceKernelGetProcessTime base).</summary>
    public required Func<ulong> Clock;
    public bool Started, Repeat, Pulse, EventSet;
    public ulong Base, StoppedCount, Interval;
    public ulong NextEvent = ulong.MaxValue;
    private readonly LinkedList<object> _waiters = new();
    private bool _deleted;
    private int _cancelled;

    public int NumWaiters
    {
        get
        {
            lock (this)
            {
                return _waiters.Count;
            }
        }
    }

    public ulong Count()
    {
        lock (this)
        {
            return Started ? Clock() - Base : StoppedCount;
        }
    }

    /// <summary>Returns 1 when it was already running, else 0.</summary>
    public int Start()
    {
        lock (this)
        {
            if (Started)
            {
                return 1;
            }

            Started = true;
            Base = Clock() - StoppedCount;
            if (Interval != 0)
            {
                Schedule();
            }

            return 0;
        }
    }

    /// <summary>Returns 1 when it stopped a running timer, 0 when it was already stopped.</summary>
    public int Stop()
    {
        lock (this)
        {
            if (!Started)
            {
                return 0;
            }

            StoppedCount = Clock() - Base;
            Started = false;
            NextEvent = ulong.MaxValue;
            return 1;
        }
    }

    /// <summary>Replaces the count, returns the old one.</summary>
    public ulong SetTime(ulong value)
    {
        lock (this)
        {
            ulong now = Clock();
            ulong old = Started ? now - Base : StoppedCount;
            if (Started)
            {
                Base = now - value;
            }
            else
            {
                StoppedCount = value;
            }

            return old;
        }
    }

    private void Schedule()
    {
        NextEvent = Clock() + Interval;
        WakeWaiters();
    }

    public int SetEvent(int type, ulong interval, bool repeat)
    {
        if (type is not (0 or 1))
        {
            return SceError.ILLEGAL_TYPE;
        }

        lock (this)
        {
            Pulse = type == 1;
            Repeat = repeat;
            Interval = interval;
            if (Started)
            {
                Schedule();
            }

            return SceError.OK;
        }
    }

    private void Advance(ulong now)
    {
        if (Repeat && Interval != 0)
        {
            NextEvent += ((now - NextEvent - 1) / Interval + 1) * Interval;
        }
        else
        {
            NextEvent = ulong.MaxValue;
        }
    }

    private int TryTake(Ptr<uint> result, Ptr<ulong> userData, ulong now)
    {
        result.TrySet(0);
        userData.TrySet(0);
        if (NextEvent < now)
        {
            if (!Pulse)
            {
                EventSet = true; // a pulse is only seen by waiters
            }

            Advance(now);
        }
        if (!EventSet)
        {
            return SceError.EVENT_COND;
        }

        result.TrySet(EventBits.Timer);
        if ((Attr & EventBits.AttrAutoReset) != 0)
        {
            EventSet = false;
        }

        return SceError.OK;
    }

    public int Poll(Ptr<uint> result, Ptr<ulong> userData)
    {
        lock (this)
        {
            return TryTake(result, userData, Clock());
        }
    }

    public int Clear()
    {
        lock (this)
        {
            EventSet = false;
        }

        return SceError.OK;
    }

    private bool Fired(LinkedListNode<object> node, ulong now) => _waiters.First == node && (EventSet || now > NextEvent);

    public int Wait(Ptr<uint> result, Ptr<ulong> userData, Ptr<uint> timeout)
    {
        lock (this)
        {
            ulong now = Clock();
            int r = TryTake(result, userData, now);
            if (r != SceError.EVENT_COND)
            {
                return r;
            }

            LinkedListNode<object> node = _waiters.AddLast(new object());
            int gen = _cancelled;
            ulong start = now;
            long? limit = timeout.IsNull ? null : timeout.Value;
            try
            {
                while (true)
                {
                    if (_deleted)
                    {
                        WriteBack(timeout, limit, start);
                        return SceError.WAIT_DELETE;
                    }
                    if (_cancelled != gen)
                    {
                        WriteBack(timeout, limit, start);
                        return SceError.WAIT_CANCEL;
                    }
                    if (Fired(node, now))
                    {
                        break;
                    }

                    long? slice = limit.HasValue ? limit.Value - (long)(now - start) : null;
                    if (slice is <= 0)
                    {
                        timeout.TrySet(0);
                        return SceError.WAIT_TIMEOUT;
                    }
                    if (_waiters.First == node && NextEvent != ulong.MaxValue)
                    {
                        long toEvent = NextEvent >= now ? (long)(NextEvent - now) + 1 : 1;
                        slice = slice.HasValue ? Math.Min(slice.Value, toEvent) : toEvent;
                    }
                    // wakes on delete/cancel/reschedule (PulseAll) or when the slice to the next event ends
                    global::VitaSharp.Core.Kernel.Wait.Until(this, () => _deleted || _cancelled != gen, slice, null, WaitDescription);
                    now = Clock();
                }
                WriteBack(timeout, limit, start);
                result.TrySet(EventBits.Timer);
                userData.TrySet(0);
                EventSet = !Pulse && (Attr & EventBits.AttrAutoReset) == 0;
                Advance(now);
                return SceError.OK;
            }
            finally
            {
                _waiters.Remove(node);
                WakeWaiters();
            }
        }
    }

    private void WriteBack(Ptr<uint> timeout, long? limit, ulong start)
    {
        if (limit.HasValue)
        {
            timeout.TrySet((uint)Math.Max(0, limit.Value - (long)(Clock() - start)));
        }
    }

    public int Cancel(Ptr<uint> numWait)
    {
        lock (this)
        {
            numWait.TrySet((uint)_waiters.Count);
            _cancelled++;
            Repeat = Pulse = false;
            Interval = 0;
            NextEvent = ulong.MaxValue;
            WakeWaiters();
            return SceError.OK;
        }
    }

    public void Delete()
    {
        lock (this)
        {
            _deleted = true;
            WakeWaiters();
        }
    }
}

/// <summary>
/// Simple event (sceKernelCreateSimpleEvent): a 32-bit pattern plus the last user data. Set wakes, in queue order,
/// every waiter whose pattern matches (consuming the bits with AUTO_RESET); Pulse does the same and then restores the
/// previous pattern, so only threads already waiting see it.
/// </summary>
public sealed partial class SimpleEvent : KernelObject
{
    public override int UidClass => 13;
    public uint Attr;
    public uint Pattern;
    public ulong LastUserData;
    private bool AutoReset => (Attr & EventBits.AttrAutoReset) != 0;

    private sealed class Waiter
    {
        public uint WaitPattern;
        public Ptr<uint> Result;
        public Ptr<ulong> UserData;
        public int Priority;
        public int? Code;
        /// <summary>The waiter's address space: the thread that sets the event writes the results there (simple
        /// events are shared between processes).</summary>
        public AddressSpace? Space;
    }

    private readonly LinkedList<Waiter> _waiters = new();
    public int NumWaiters
    {
        get
        {
            lock (this)
            {
                return _waiters.Count;
            }
        }
    }

    private int TryTake(uint waitPattern, Ptr<uint> result, Ptr<ulong> userData)
    {
        if ((Pattern & waitPattern) == 0)
        {
            result.TrySet(0);
            return SceError.EVENT_COND;
        }
        result.TrySet(Pattern & waitPattern);
        if (AutoReset)
        {
            Pattern &= ~waitPattern;
        }

        userData.TrySet(LastUserData);
        return SceError.OK;
    }

    public int Poll(uint waitPattern, Ptr<uint> result, Ptr<ulong> userData)
    {
        if (waitPattern == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        lock (this)
        {
            return TryTake(waitPattern, result, userData);
        }
    }

    public int Wait(GuestThread? self, uint waitPattern, Ptr<uint> result, Ptr<ulong> userData, Ptr<uint> timeout)
    {
        if (waitPattern == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        lock (this)
        {
            int r = TryTake(waitPattern, result, userData);
            if (r != SceError.EVENT_COND)
            {
                return r;
            }

            var w = new Waiter { WaitPattern = waitPattern, Result = result, UserData = userData, Priority = self?.Priority ?? 0, Space = AddressSpace.Current };
            LinkedListNode<Waiter> node;
            if ((Attr & EventBits.AttrThPrio) != 0)
            {
                LinkedListNode<Waiter>? at = _waiters.First;
                while (at != null && at.Value.Priority <= w.Priority)
                {
                    at = at.Next;
                }

                node = at == null ? _waiters.AddLast(w) : _waiters.AddBefore(at, w);
            }
            else
            {
                node = _waiters.AddLast(w);
            }

            r = global::VitaSharp.Core.Kernel.Wait.Until(this, w, static w => w.Code.HasValue, timeout, null, WaitDescription);
            if (w.Code is { } code)
            {
                r = code;
            }
            else
            {
                _waiters.Remove(node);
            }

            if (r < 0 && r != SceError.WAIT_TIMEOUT)
            {
                result.TrySet(EventBits.Error);
                userData.TrySet(LastUserData);
            }
            return r;
        }
    }

    public int SetOrPulse(uint setPattern, ulong userData, bool pulse)
    {
        if ((setPattern & ~EventBits.UserMask) != 0)
        {
            return SceError.ILLEGAL_PATTERN;
        }

        lock (this)
        {
            uint oldPattern = Pattern;
            ulong oldData = LastUserData;
            Pattern |= setPattern;
            LastUserData = userData;
            for (LinkedListNode<Waiter>? n = _waiters.First; n != null;)
            {
                LinkedListNode<Waiter>? next = n.Next;
                Waiter w = n.Value;
                if ((Pattern & w.WaitPattern) != 0)
                {
                    using (AddressSpace.Use(w.Space))
                    {
                        w.Result.TrySet(Pattern & w.WaitPattern);
                        w.UserData.TrySet(LastUserData);
                    }
                    if (AutoReset)
                    {
                        Pattern &= ~w.WaitPattern;
                    }

                    w.Code = SceError.OK;
                    _waiters.Remove(n);
                }
                n = next;
            }
            if (pulse)
            {
                Pattern = oldPattern & ~setPattern;
                LastUserData = oldData;
            }
            WakeWaiters();
            return SceError.OK;
        }
    }

    /// <summary>Keeps only the bits of <paramref name="keepPattern"/> (Vita3K: only user bits may be passed).</summary>
    public int Clear(uint keepPattern)
    {
        if ((keepPattern & ~EventBits.UserMask) != 0)
        {
            return SceError.ILLEGAL_PATTERN;
        }

        lock (this)
        {
            Pattern &= keepPattern;
        }

        return SceError.OK;
    }

    /// <summary>Cancels every wait (WAIT_CANCEL) and replaces the whole pattern (the create bit too) and the user data.</summary>
    public int CancelWithSetPattern(uint setPattern, ulong userData, Ptr<uint> numWait)
    {
        if ((setPattern & ~EventBits.UserMask) != 0)
        {
            return SceError.ILLEGAL_PATTERN;
        }

        lock (this)
        {
            numWait.TrySet((uint)_waiters.Count);
            foreach (Waiter w in _waiters)
            {
                w.Code = SceError.WAIT_CANCEL;
            }

            _waiters.Clear();
            Pattern = setPattern;
            LastUserData = userData;
            WakeWaiters();
            return SceError.OK;
        }
    }

    public int Cancel(Ptr<uint> numWait)
    {
        lock (this)
        {
            numWait.TrySet((uint)_waiters.Count);
            foreach (Waiter w in _waiters)
            {
                w.Code = SceError.WAIT_CANCEL;
            }

            _waiters.Clear();
            WakeWaiters();
            return SceError.OK;
        }
    }

    public void Delete()
    {
        lock (this)
        {
            // waits that include the DELETE bit end successfully with it, the others with WAIT_DELETE
            foreach (Waiter w in _waiters)
            {
                if ((w.WaitPattern & EventBits.Delete) != 0)
                {
                    using (AddressSpace.Use(w.Space))
                    {
                        w.Result.TrySet((Pattern | EventBits.Delete) & w.WaitPattern);
                        w.UserData.TrySet(LastUserData);
                    }
                    w.Code = SceError.OK;
                }
                else
                {
                    w.Code = SceError.WAIT_DELETE;
                }
            }
            _waiters.Clear();
            WakeWaiters();
        }
    }
}

/// <summary>Handler registered by sceKernelRegisterThreadEventHandler (thread start/exit notifications).</summary>
public sealed partial class ThreadEventHandler : KernelObject
{
    public override int UidClass => 9;
    public int ThreadMask;
    public uint EventMask, Handler, Common;
}
