using System.Collections.Concurrent;

namespace Stateforward.Hsm.Tests;

internal sealed class TestClockHarness
{
    private readonly ConcurrentQueue<PendingDelay> _pending = new();
    private readonly SemaphoreSlim _signal = new(0);

    public Clock Clock { get; }

    public TestClockHarness(DateTimeOffset? now = null)
    {
        var currentNow = now ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Clock = new Clock(
            async (duration, cancellationToken) =>
            {
                var pending = new PendingDelay(duration, cancellationToken);
                _pending.Enqueue(pending);
                _signal.Release();
                await pending.Task.ConfigureAwait(false);
            },
            () => currentNow);
    }

    public async Task<PendingDelay> NextAsync(string reason)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await _signal.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new Xunit.Sdk.XunitException($"expected pending delay for {reason}");
        }

        if (_pending.TryDequeue(out var pending))
        {
            return pending;
        }

        throw new Xunit.Sdk.XunitException($"expected pending delay for {reason}");
    }

    public void AssertNoPending(string reason)
    {
        if (_pending.TryPeek(out _))
        {
            throw new Xunit.Sdk.XunitException($"expected no pending delays for {reason}");
        }
    }

    internal sealed class PendingDelay
    {
        private readonly TaskCompletionSource<bool> _source = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingDelay(TimeSpan duration, CancellationToken cancellationToken)
        {
            Duration = duration;
            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() => _source.TrySetCanceled(cancellationToken));
            }
        }

        public TimeSpan Duration { get; }
        public Task Task => _source.Task;
        public void Trigger() => _source.TrySetResult(true);
    }
}
