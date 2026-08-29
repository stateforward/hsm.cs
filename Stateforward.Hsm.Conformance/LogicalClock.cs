using Stateforward.Hsm;

sealed class LogicalClock
{
    private sealed class Registration
    {
        private readonly TaskCompletionSource<bool> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Action _cancelled;
        private readonly Action _fired;
        private CancellationTokenRegistration _cancellation;
        private int _status;

        public Registration(long due, long sequence, CancellationToken cancellationToken, Action fired, Action cancelled)
        {
            Due = due;
            Sequence = sequence;
            _fired = fired;
            _cancelled = cancelled;
            if (cancellationToken.CanBeCanceled)
            {
                _cancellation = cancellationToken.Register(Cancel);
            }
        }

        public long Due { get; }
        public long Sequence { get; }
        public Task Task => _completion.Task;
        public bool Pending => Volatile.Read(ref _status) == 0;

        public void Fire()
        {
            if (Interlocked.CompareExchange(ref _status, 1, 0) != 0) return;
            _cancellation.Dispose();
            _fired();
            _completion.TrySetResult(true);
        }

        private void Cancel()
        {
            if (Interlocked.CompareExchange(ref _status, 2, 0) != 0) return;
            _cancelled();
            _completion.TrySetCanceled();
        }
    }

    private readonly object _gate = new();
    private readonly List<Registration> _registrations = [];
    private readonly Action _scheduled;
    private readonly Action _fired;
    private readonly Action _cancelled;
    private long _nowMilliseconds;
    private long _sequence;

    public LogicalClock(Action scheduled, Action fired, Action cancelled)
    {
        _scheduled = scheduled;
        _fired = fired;
        _cancelled = cancelled;
        Clock = new Clock(Delay, Now);
    }

    public Clock Clock { get; }

    public Clock CreateClock(string? mode, Action<string> trace)
    {
        if (mode is null) return Clock;
        return new Clock(
            (duration, cancellationToken) => DelayConfigured(mode, duration, cancellationToken, trace),
            Now);
    }

    public async Task AdvanceAsync(int milliseconds, Func<Task> beforeFire, Func<Task> afterFire)
    {
        lock (_gate)
        {
            _nowMilliseconds += milliseconds;
        }

        while (true)
        {
            Registration? next;
            lock (_gate)
            {
                _registrations.RemoveAll(registration => !registration.Pending);
                next = _registrations
                    .Where(registration => registration.Due <= _nowMilliseconds)
                    .OrderBy(registration => registration.Due)
                    .ThenBy(registration => registration.Sequence)
                    .FirstOrDefault();
                if (next is not null) _registrations.Remove(next);
            }

            if (next is null) return;
            var processed = beforeFire();
            next.Fire();
            await processed.WaitAsync(TimeSpan.FromSeconds(1));
            await Task.Delay(1);
            await afterFire().WaitAsync(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }
    }

    private Task Delay(TimeSpan duration, CancellationToken cancellationToken)
    {
        Registration registration;
        lock (_gate)
        {
            var delay = Math.Max(0, (long)Math.Ceiling(duration.TotalMilliseconds));
            registration = new Registration(
                _nowMilliseconds + delay,
                _sequence++,
                cancellationToken,
                _fired,
                _cancelled);
            _registrations.Add(registration);
        }
        _scheduled();
        return registration.Task;
    }

    private Task DelayConfigured(
        string mode,
        TimeSpan duration,
        CancellationToken cancellationToken,
        Action<string> trace)
    {
        var pending = Delay(
            mode is "trace_no_sleep" or "trace_nonzero_sleep" ? TimeSpan.Zero : duration,
            cancellationToken);
        trace(mode == "trace_nonzero_sleep"
            ? "clock:sleep:nonzero"
            : $"clock:sleep:{duration.TotalMilliseconds:0.################}");
        return pending;
    }

    private DateTimeOffset Now()
    {
        lock (_gate)
        {
            return DateTimeOffset.UnixEpoch.AddMilliseconds(_nowMilliseconds);
        }
    }
}
