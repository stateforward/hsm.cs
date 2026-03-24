using Xunit;

namespace Stateforward.Hsm.Tests;

public sealed class TimerAndActivityTests
{
    private sealed class TestMachine : Instance
    {
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string reason)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task AfterAtEveryAndWhenUseInjectedClock()
    {
        var afterHarness = new TestClockHarness();
        var afterModel = Hsm.Define(
            "AfterRules",
            Hsm.Initial(Hsm.Target("foo")),
            Hsm.State(
                "foo",
                Hsm.Transition(
                    Hsm.After<TestMachine>((_, _, _) => TimeSpan.FromMinutes(5)),
                    Hsm.Target("../bar"))),
            Hsm.State("bar"));

        var afterMachine = Hsm.Start(new Context(), new TestMachine(), afterModel, new Config { Clock = afterHarness.Clock });
        var afterPending = await afterHarness.NextAsync("after");
        Assert.Equal(TimeSpan.FromMinutes(5), afterPending.Duration);
        afterPending.Trigger();
        await WaitUntilAsync(() => afterMachine.State == "/AfterRules/bar", "after transition");
        Assert.Equal("/AfterRules/bar", afterMachine.State);

        var negativeHarness = new TestClockHarness();
        var negativeModel = Hsm.Define(
            "NegativeAfter",
            Hsm.Initial(Hsm.Target("foo")),
            Hsm.State(
                "foo",
                Hsm.Transition(
                    Hsm.After<TestMachine>((_, _, _) => TimeSpan.FromMinutes(-1)),
                    Hsm.Target("../bar"))),
            Hsm.State("bar"));

        var negativeMachine = Hsm.Start(new Context(), new TestMachine(), negativeModel, new Config { Clock = negativeHarness.Clock });
        negativeHarness.AssertNoPending("negative after");
        Assert.Equal("/NegativeAfter/foo", negativeMachine.State);

        var atNow = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var atHarness = new TestClockHarness(atNow);
        var atTarget = atNow.AddHours(2);
        var atModel = Hsm.Define(
            "AtRules",
            Hsm.Initial(Hsm.Target("foo")),
            Hsm.State(
                "foo",
                Hsm.Transition(
                    Hsm.At<TestMachine>((_, _, _) => atTarget),
                    Hsm.Target("../bar"))),
            Hsm.State("bar"));

        var atMachine = Hsm.Start(new Context(), new TestMachine(), atModel, new Config { Clock = atHarness.Clock });
        var atPending = await atHarness.NextAsync("at");
        Assert.Equal(TimeSpan.FromHours(2), atPending.Duration);
        atPending.Trigger();
        await WaitUntilAsync(() => atMachine.State == "/AtRules/bar", "at transition");
        Assert.Equal("/AtRules/bar", atMachine.State);

        var everyHarness = new TestClockHarness();
        var ticks = 0;
        var everyModel = Hsm.Define(
            "EveryRules",
            Hsm.Initial(Hsm.Target("foo")),
            Hsm.State(
                "foo",
                Hsm.Transition(
                    Hsm.Every<TestMachine>((_, _, _) => TimeSpan.FromMinutes(10)),
                    Hsm.Effect<TestMachine>((_, _, _) => ticks++))));

        Hsm.Start(new Context(), new TestMachine(), everyModel, new Config { Clock = everyHarness.Clock });
        var firstTick = await everyHarness.NextAsync("every first");
        Assert.Equal(TimeSpan.FromMinutes(10), firstTick.Duration);
        firstTick.Trigger();
        await WaitUntilAsync(() => ticks == 1, "first every tick");
        Assert.Equal(1, ticks);

        var secondTick = await everyHarness.NextAsync("every second");
        secondTick.Trigger();
        await WaitUntilAsync(() => ticks == 2, "second every tick");
        Assert.Equal(2, ticks);

        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var whenModel = Hsm.Define(
            "WhenRules",
            Hsm.Initial(Hsm.Target("foo")),
            Hsm.State(
                "foo",
                Hsm.Transition(
                    Hsm.When<TestMachine>(async (_, _, _, cancellationToken) =>
                    {
                        await signal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }),
                    Hsm.Target("../bar"))),
            Hsm.State("bar"));

        var whenMachine = Hsm.Start(new Context(), new TestMachine(), whenModel);
        Assert.Equal("/WhenRules/foo", whenMachine.State);
        signal.TrySetResult(true);
        await WaitUntilAsync(() => whenMachine.State == "/WhenRules/bar", "when transition");
        Assert.Equal("/WhenRules/bar", whenMachine.State);
    }

    [Fact]
    public async Task ActivityIsCanceledOnExit()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = Hsm.Define(
            "ActivityRules",
            Hsm.Initial(Hsm.Target("running")),
            Hsm.State(
                "running",
                Hsm.Activity<TestMachine>((ctx, _, _) =>
                {
                    started.TrySetResult(true);
                    ctx.CancellationToken.WaitHandle.WaitOne();
                    cancelled.TrySetResult(true);
                }),
                Hsm.Transition(Hsm.On("finish"), Hsm.Target("../done"))),
            Hsm.State("done"));

        var machine = Hsm.Start(new Context(), new TestMachine(), model);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await machine.Dispatch(new Event("finish"));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("/ActivityRules/done", machine.State);
    }
}
