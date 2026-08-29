using Xunit;

namespace Stateforward.Hsm.Tests;

public sealed class HistoryAndLifecycleTests
{
    private sealed class TestMachine : Instance
    {
    }

    [Fact]
    public async Task NewAndStoppedMachinesHaveNoActiveStateAndCanStartAgain()
    {
        var model = Hsm.Define(
            "EmptyLifecycleState",
            Hsm.State("idle"),
            Hsm.Initial(Hsm.Target("idle")));
        var context = new Context();
        var machine = Hsm.New(new TestMachine(), model);

        Assert.Equal(string.Empty, machine.State);

        Hsm.Start(context, machine);
        Assert.Equal("/EmptyLifecycleState/idle", machine.State);

        await machine.Stop();
        Assert.Equal(string.Empty, machine.State);

        Hsm.Start(context, machine);
        Assert.Equal("/EmptyLifecycleState/idle", machine.State);
    }

    [Fact]
    public async Task LifecycleBehaviorsReceiveCanonicalInitialAndFinalEvents()
    {
        var events = new List<string>();
        var model = Hsm.Define(
            "LifecycleEvents",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Entry<TestMachine>((_, _, evt) => events.Add(evt.Name)),
                Hsm.Exit<TestMachine>((_, _, evt) => events.Add(evt.Name))));
        var machine = Hsm.Start(new Context(), new TestMachine(), model);

        await machine.Stop();

        Assert.Equal(new[] { "hsm/initial", "hsm/final" }, events);
    }

    [Fact]
    public async Task LifecycleCallsFromExitBehaviorsDoNotReenterLifecycleProcessing()
    {
        var stopExits = 0;
        var stopModel = Hsm.Define(
            "StopFromExit",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Exit<TestMachine>((_, machine, _) =>
                {
                    stopExits++;
                    machine.Stop().GetAwaiter().GetResult();
                })));
        var stopped = Hsm.Start(new Context(), new TestMachine(), stopModel);

        await stopped.Stop();

        Assert.Equal(1, stopExits);
        Assert.Equal(string.Empty, stopped.State);

        var restartExits = 0;
        var restartModel = Hsm.Define(
            "RestartFromExit",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Exit<TestMachine>((_, machine, _) =>
                {
                    restartExits++;
                    machine.Restart().GetAwaiter().GetResult();
                })));
        var restarted = Hsm.Start(new Context(), new TestMachine(), restartModel);

        await restarted.Restart();

        Assert.Equal(1, restartExits);
        Assert.Equal("/RestartFromExit/idle", restarted.State);
    }

    [Fact]
    public async Task DispatchFromExitBehaviorDoesNotDeadlockStop()
    {
        var model = Hsm.Define(
            "DispatchFromExit",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Exit<TestMachine>((_, machine, _) =>
                    machine.Dispatch(new Event("nested")).GetAwaiter().GetResult()),
                Hsm.Transition(Hsm.On("nested"), Hsm.Target("../wrong"))),
            Hsm.State("wrong"));
        var machine = Hsm.Start(new Context(), new TestMachine(), model);

        await machine.Stop().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(string.Empty, machine.State);
    }

    [Fact]
    public async Task DispatchFromExitBehaviorDoesNotWaitForAnExistingProcessorDuringStop()
    {
        using var effectEntered = new ManualResetEventSlim();
        using var releaseEffect = new ManualResetEventSlim();
        var model = Hsm.Define(
            "DispatchFromExitWithProcessor",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Exit<TestMachine>((_, machine, _) =>
                    machine.Dispatch(new Event("nested")).GetAwaiter().GetResult()),
                Hsm.Transition(
                    Hsm.On("hold"),
                    Hsm.Effect<TestMachine>((_, _, _) =>
                    {
                        effectEntered.Set();
                        releaseEffect.Wait();
                    })),
                Hsm.Transition(Hsm.On("nested"), Hsm.Target("../wrong"))),
            Hsm.State("wrong"));
        var machine = Hsm.Start(new Context(), new TestMachine(), model);

        var dispatch = machine.Dispatch(new Event("hold"));
        Assert.True(effectEntered.Wait(TimeSpan.FromSeconds(1)));
        var stop = Task.Run(() => machine.Stop());
        releaseEffect.Set();

        await Task.WhenAll(dispatch, stop).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(string.Empty, machine.State);
    }

    [Fact]
    public async Task DispatchFromExitBehaviorDoesNotSurviveRestart()
    {
        var model = Hsm.Define(
            "DispatchFromRestartExit",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Exit<TestMachine>((_, machine, _) =>
                    machine.Dispatch(new Event("nested")).GetAwaiter().GetResult()),
                Hsm.Transition(Hsm.On("nested"), Hsm.Target("../wrong"))),
            Hsm.State("wrong"));
        var machine = Hsm.Start(new Context(), new TestMachine(), model);

        await machine.Restart().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("/DispatchFromRestartExit/idle", machine.State);
    }

    [Fact]
    public async Task DispatchFromExitBehaviorDoesNotWaitForAnExistingProcessorDuringRestart()
    {
        using var effectEntered = new ManualResetEventSlim();
        using var restartCalled = new ManualResetEventSlim();
        using var releaseEffect = new ManualResetEventSlim();
        var model = Hsm.Define(
            "DispatchFromRestartExitWithProcessor",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Exit<TestMachine>((_, machine, _) =>
                    machine.Dispatch(new Event("nested")).GetAwaiter().GetResult()),
                Hsm.Transition(
                    Hsm.On("hold"),
                    Hsm.Effect<TestMachine>((_, _, _) =>
                    {
                        effectEntered.Set();
                        releaseEffect.Wait();
                    })),
                Hsm.Transition(Hsm.On("nested"), Hsm.Target("../wrong"))),
            Hsm.State("wrong"));
        var machine = Hsm.Start(new Context(), new TestMachine(), model);

        var dispatch = machine.Dispatch(new Event("hold"));
        Assert.True(effectEntered.Wait(TimeSpan.FromSeconds(1)));
        var restart = Task.Run(() =>
        {
            restartCalled.Set();
            return machine.Restart();
        });
        Assert.True(restartCalled.Wait(TimeSpan.FromSeconds(1)));
        await Task.Delay(100);
        releaseEffect.Set();

        await Task.WhenAll(dispatch, restart).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("/DispatchFromRestartExitWithProcessor/idle", machine.State);
    }

    [Fact]
    public async Task DispatchFromFreshEntryBehaviorIsProcessedDuringRestart()
    {
        var entries = 0;
        var model = Hsm.Define(
            "RestartEntryDispatch",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Entry<TestMachine>((_, machine, _) =>
                {
                    if (Interlocked.Increment(ref entries) == 2)
                    {
                        machine.Dispatch(new Event("advance")).GetAwaiter().GetResult();
                    }
                }),
                Hsm.Transition(Hsm.On("advance"), Hsm.Target("../done"))),
            Hsm.State("done"));
        var machine = Hsm.Start(new Context(), new TestMachine(), model);

        await machine.Restart().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(2, entries);
        Assert.Equal("/RestartEntryDispatch/done", machine.State);
    }

    [Fact]
    public async Task DispatchFromFreshEntryBehaviorDoesNotWaitForAnExistingProcessorDuringRestart()
    {
        using var effectEntered = new ManualResetEventSlim();
        using var restartCalled = new ManualResetEventSlim();
        using var releaseEffect = new ManualResetEventSlim();
        var entries = 0;
        var model = Hsm.Define(
            "RestartEntryDispatchWithProcessor",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Entry<TestMachine>((_, machine, _) =>
                {
                    if (Interlocked.Increment(ref entries) == 2)
                    {
                        machine.Dispatch(new Event("advance")).GetAwaiter().GetResult();
                    }
                }),
                Hsm.Transition(
                    Hsm.On("hold"),
                    Hsm.Effect<TestMachine>((_, _, _) =>
                    {
                        effectEntered.Set();
                        releaseEffect.Wait();
                    })),
                Hsm.Transition(Hsm.On("advance"), Hsm.Target("../done"))),
            Hsm.State("done"));
        var machine = Hsm.Start(new Context(), new TestMachine(), model);

        var dispatch = machine.Dispatch(new Event("hold"));
        Assert.True(effectEntered.Wait(TimeSpan.FromSeconds(1)));
        var restart = Task.Run(() =>
        {
            restartCalled.Set();
            return machine.Restart();
        });
        Assert.True(restartCalled.Wait(TimeSpan.FromSeconds(1)));
        await Task.Delay(100);
        releaseEffect.Set();

        await Task.WhenAll(dispatch, restart).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, entries);
        Assert.Equal("/RestartEntryDispatchWithProcessor/done", machine.State);
    }

    [Fact]
    public async Task FinalCompletionFromFreshEntryDoesNotWaitForAnExistingProcessorDuringRestart()
    {
        using var effectEntered = new ManualResetEventSlim();
        using var restartCalled = new ManualResetEventSlim();
        using var releaseEffect = new ManualResetEventSlim();
        var enterFinal = false;
        var model = Hsm.Define(
            "RestartFinalWithProcessor",
            Hsm.Initial(Hsm.Target("decision")),
            Hsm.Choice(
                "decision",
                Hsm.Transition(
                    Hsm.Target("done"),
                    Hsm.Guard<TestMachine>((_, _, _) => enterFinal)),
                Hsm.Transition(Hsm.Target("idle"))),
            Hsm.State(
                "idle",
                Hsm.Transition(
                    Hsm.On("hold"),
                    Hsm.Effect<TestMachine>((_, _, _) =>
                    {
                        effectEntered.Set();
                        releaseEffect.Wait();
                    }))),
            Hsm.Final("done"));
        var machine = Hsm.Start(new Context(), new TestMachine(), model);

        var dispatch = machine.Dispatch(new Event("hold"));
        Assert.True(effectEntered.Wait(TimeSpan.FromSeconds(1)));
        enterFinal = true;
        var restart = Task.Run(() =>
        {
            restartCalled.Set();
            return machine.Restart();
        });
        Assert.True(restartCalled.Wait(TimeSpan.FromSeconds(1)));
        await Task.Delay(100);
        releaseEffect.Set();

        await Task.WhenAll(dispatch, restart).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("/RestartFinalWithProcessor/done", machine.State);
    }

    [Fact]
    public async Task DispatchFromCancellationCallbackDoesNotSurviveRestartTeardown()
    {
        var activityContext = new TaskCompletionSource<Context>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = Hsm.Define(
            "CancellationRestart",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Activity<TestMachine>((ctx, _, _) =>
                {
                    activityContext.TrySetResult(ctx);
                    ctx.CancellationToken.WaitHandle.WaitOne();
                    cancellationObserved.TrySetResult(true);
                }),
                Hsm.Transition(Hsm.On("nested"), Hsm.Target("../wrong"))),
            Hsm.State("wrong"));
        var machine = Hsm.Start(new Context(), new TestMachine(), model);
        var context = await activityContext.Task.WaitAsync(TimeSpan.FromSeconds(1));
        using var registration = context.CancellationToken.Register(() =>
            machine.Dispatch(new Event("nested")).GetAwaiter().GetResult());

        await machine.Restart().WaitAsync(TimeSpan.FromSeconds(1));
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("/CancellationRestart/idle", machine.State);
    }

    [Fact]
    public async Task FreshEntryDispatchStartsAProcessorWhenThePreviousProcessorRetiresDuringRestart()
    {
        using var effectEntered = new ManualResetEventSlim();
        using var restartCalled = new ManualResetEventSlim();
        using var releaseEffect = new ManualResetEventSlim();
        var entries = 0;
        var model = Hsm.Define(
            "RestartProcessorHandoff",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Entry<TestMachine>((_, machine, _) =>
                {
                    if (Interlocked.Increment(ref entries) == 2)
                    {
                        machine.Dispatch(new Event("advance")).GetAwaiter().GetResult();
                    }
                }),
                Hsm.Exit<TestMachine>((_, _, _) => Thread.Sleep(100)),
                Hsm.Transition(
                    Hsm.On("hold"),
                    Hsm.Effect<TestMachine>((_, _, _) =>
                    {
                        effectEntered.Set();
                        releaseEffect.Wait();
                    })),
                Hsm.Transition(Hsm.On("advance"), Hsm.Target("../done"))),
            Hsm.State("done"));
        var machine = Hsm.Start(new Context(), new TestMachine(), model);

        var dispatch = machine.Dispatch(new Event("hold"));
        Assert.True(effectEntered.Wait(TimeSpan.FromSeconds(1)));
        var restart = Task.Run(() =>
        {
            restartCalled.Set();
            return machine.Restart();
        });
        Assert.True(restartCalled.Wait(TimeSpan.FromSeconds(1)));
        await Task.Delay(100);
        releaseEffect.Set();

        await Task.WhenAll(dispatch, restart).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, entries);
        Assert.Equal("/RestartProcessorHandoff/done", machine.State);
    }

    [Fact]
    public async Task DispatchFromCancelledActivityGenerationDoesNotEnterRestartedMachine()
    {
        var firstActivityStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restartReturned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleDispatchReturned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activityCount = 0;
        var model = Hsm.Define(
            "DelayedCancellationDispatch",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Activity<TestMachine>(async (ctx, machine, _) =>
                {
                    if (Interlocked.Increment(ref activityCount) != 1)
                    {
                        return;
                    }

                    firstActivityStarted.TrySetResult(true);
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, ctx.CancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    await restartReturned.Task;
                    await machine.Dispatch(new Event("stale"));
                    staleDispatchReturned.TrySetResult(true);
                }),
                Hsm.Transition(Hsm.On("stale"), Hsm.Target("../wrong"))),
            Hsm.State("wrong"));
        var machine = Hsm.Start(new Context(), new TestMachine(), model);
        await firstActivityStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await machine.Restart().WaitAsync(TimeSpan.FromSeconds(1));
        restartReturned.TrySetResult(true);
        await staleDispatchReturned.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(2, activityCount);
        Assert.Equal("/DelayedCancellationDispatch/idle", machine.State);
    }

    [Fact]
    public async Task OffProcessorOperationCallStartsAProcessorAfterThePreviousProcessorRetires()
    {
        using var effectEntered = new ManualResetEventSlim();
        using var releaseEffect = new ManualResetEventSlim();
        using var operationEntered = new ManualResetEventSlim();
        using var releaseOperation = new ManualResetEventSlim();
        var model = Hsm.Define(
            "OffProcessorCall",
            Hsm.Operation("ping", new Action(() =>
            {
                operationEntered.Set();
                releaseOperation.Wait();
            })),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Transition(
                    Hsm.On("hold"),
                    Hsm.Effect<TestMachine>((_, _, _) =>
                    {
                        effectEntered.Set();
                        releaseEffect.Wait();
                    })),
                Hsm.Transition(Hsm.OnCall("ping"), Hsm.Target("../done"))),
            Hsm.State("done"));
        var context = new Context();
        var machine = Hsm.Start(context, new TestMachine(), model);

        var dispatch = machine.Dispatch(new Event("hold"));
        Assert.True(effectEntered.Wait(TimeSpan.FromSeconds(1)));
        var call = Task.Run(() => Hsm.Call(context, machine, "ping"));
        Assert.True(operationEntered.Wait(TimeSpan.FromSeconds(1)));

        releaseEffect.Set();
        await dispatch.WaitAsync(TimeSpan.FromSeconds(1));
        releaseOperation.Set();
        await call.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("/OffProcessorCall/done", machine.State);
    }

    [Fact]
    public async Task ConcurrentOperationDepthDoesNotCorruptProcessorReentrancy()
    {
        using var effectEntered = new ManualResetEventSlim();
        using var releaseEffect = new ManualResetEventSlim();
        using var operationEntered = new ManualResetEventSlim();
        using var releaseOperation = new ManualResetEventSlim();
        var trace = new List<string>();
        var model = Hsm.Define(
            "ConcurrentDepth",
            Hsm.Operation("ping", new Action(() =>
            {
                operationEntered.Set();
                releaseOperation.Wait();
            })),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Transition(
                    Hsm.On("hold"),
                    Hsm.Effect<TestMachine>((_, machine, _) =>
                    {
                        trace.Add("effect-before");
                        effectEntered.Set();
                        releaseEffect.Wait();
                        machine.Dispatch(new Event("nested")).GetAwaiter().GetResult();
                        trace.Add("effect-after");
                    })),
                Hsm.Transition(
                    Hsm.On("nested"),
                    Hsm.Target("../done"),
                    Hsm.Effect<TestMachine>((_, _, _) => trace.Add("nested")))),
            Hsm.State("done"));
        var context = new Context();
        var machine = Hsm.Start(context, new TestMachine(), model);

        var dispatch = machine.Dispatch(new Event("hold"));
        Assert.True(effectEntered.Wait(TimeSpan.FromSeconds(1)));
        var call = Task.Run(() => Hsm.Call(context, machine, "ping"));
        Assert.True(operationEntered.Wait(TimeSpan.FromSeconds(1)));

        releaseEffect.Set();
        await dispatch.WaitAsync(TimeSpan.FromSeconds(1));
        releaseOperation.Set();
        await call.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(new[] { "effect-before", "effect-after", "nested" }, trace);
        Assert.Equal("/ConcurrentDepth/done", machine.State);
    }

    [Fact]
    public async Task AsyncNamedEffectCanDispatchBackIntoItsProcessor()
    {
        var model = Hsm.Define(
            "AsyncNamedEffect",
            Hsm.Operation(
                "effect",
                new Func<Context, TestMachine, Event, Task>(async (_, machine, _) =>
                {
                    await Task.Yield();
                    await machine.Dispatch(new Event("nested"));
                })),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Transition(Hsm.On("go"), Hsm.Effect("effect")),
                Hsm.Transition(Hsm.On("nested"), Hsm.Target("../done"))),
            Hsm.State("done"));
        var machine = Hsm.Start(new Context(), new TestMachine(), model);

        await machine.Dispatch(new Event("go")).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("/AsyncNamedEffect/done", machine.State);
    }

    [Fact]
    public async Task AsyncNamedEffectCanCallBackIntoItsProcessor()
    {
        var model = Hsm.Define(
            "AsyncNamedCall",
            Hsm.Operation("ping", new Action(() => { })),
            Hsm.Operation(
                "effect",
                new Func<Context, TestMachine, Event, Task>(async (ctx, machine, _) =>
                {
                    await Task.Yield();
                    Hsm.Call(ctx, machine, "ping");
                })),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Transition(Hsm.On("go"), Hsm.Effect("effect")),
                Hsm.Transition(Hsm.OnCall("ping"), Hsm.Target("../done"))),
            Hsm.State("done"));
        var machine = Hsm.Start(new Context(), new TestMachine(), model);

        await machine.Dispatch(new Event("go")).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("/AsyncNamedCall/done", machine.State);
    }

    [Fact]
    public async Task AsyncNamedEffectCanStopItsProcessor()
    {
        var model = Hsm.Define(
            "AsyncNamedStop",
            Hsm.Operation(
                "stop",
                new Func<Context, TestMachine, Event, Task>(async (_, machine, _) =>
                {
                    await Task.Yield();
                    await machine.Stop();
                })),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Transition(Hsm.On("go"), Hsm.Target("../wrong"), Hsm.Effect("stop"))),
            Hsm.State("wrong"));
        var machine = Hsm.Start(new Context(), new TestMachine(), model);

        await machine.Dispatch(new Event("go")).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(string.Empty, machine.State);
    }

    [Fact]
    public async Task AsyncNamedEffectCanRestartItsProcessor()
    {
        var entries = 0;
        var model = Hsm.Define(
            "AsyncNamedRestart",
            Hsm.Operation(
                "restart",
                new Func<Context, TestMachine, Event, Task>(async (_, machine, _) =>
                {
                    await Task.Yield();
                    await machine.Restart();
                })),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Entry<TestMachine>((_, _, _) => Interlocked.Increment(ref entries)),
                Hsm.Transition(Hsm.On("go"), Hsm.Target("../wrong"), Hsm.Effect("restart"))),
            Hsm.State("wrong"));
        var machine = Hsm.Start(new Context(), new TestMachine(), model);

        await machine.Dispatch(new Event("go")).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(2, entries);
        Assert.Equal("/AsyncNamedRestart/idle", machine.State);
    }

    [Fact]
    public async Task PublicAsyncOperationDispatchesOnCallOnlyAfterSuccess()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = Hsm.Define(
            "AsyncCallSuccess",
            Hsm.Operation("ping", new Func<Task<string>>(async () =>
            {
                await release.Task;
                return "pong";
            })),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle", Hsm.Transition(Hsm.OnCall("ping"), Hsm.Target("../done"))),
            Hsm.State("done"));
        var context = new Context();
        var machine = Hsm.Start(context, new TestMachine(), model);

        var pending = Assert.IsAssignableFrom<Task<object?>>(Hsm.Call(context, machine, "ping"));
        Assert.Equal("/AsyncCallSuccess/idle", machine.State);
        release.TrySetResult(true);

        Assert.Equal("pong", await pending.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal("/AsyncCallSuccess/done", machine.State);
    }

    [Fact]
    public async Task PublicAsyncOperationFailureDoesNotDispatchOnCall()
    {
        var model = Hsm.Define(
            "AsyncCallFailure",
            Hsm.Operation("ping", new Func<Task>(async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("boom");
            })),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle", Hsm.Transition(Hsm.OnCall("ping"), Hsm.Target("../wrong"))),
            Hsm.State("wrong"));
        var context = new Context();
        var machine = Hsm.Start(context, new TestMachine(), model);

        var pending = Assert.IsAssignableFrom<Task<object?>>(Hsm.Call(context, machine, "ping"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Assert.Equal("/AsyncCallFailure/idle", machine.State);
    }

    [Fact]
    public async Task PublicAsyncOperationsCanStopAndRestartTheirOwnedProcessor()
    {
        var stopModel = Hsm.Define(
            "AsyncCallStop",
            Hsm.Operation("stop", new Func<TestMachine, Task>(async machine =>
            {
                await Task.Yield();
                await machine.Stop();
            })),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle"));
        var stopContext = new Context();
        var stopped = Hsm.Start(stopContext, new TestMachine(), stopModel);

        await Assert.IsAssignableFrom<Task<object?>>(Hsm.Call(stopContext, stopped, "stop"))
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(string.Empty, stopped.State);

        var entries = 0;
        var restartModel = Hsm.Define(
            "AsyncCallRestart",
            Hsm.Operation("restart", new Func<TestMachine, Task>(async machine =>
            {
                await Task.Yield();
                await machine.Restart();
            })),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle", Hsm.Entry<TestMachine>((_, _, _) => Interlocked.Increment(ref entries))));
        var restartContext = new Context();
        var restarted = Hsm.Start(restartContext, new TestMachine(), restartModel);

        await Assert.IsAssignableFrom<Task<object?>>(Hsm.Call(restartContext, restarted, "restart"))
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(2, entries);
        Assert.Equal("/AsyncCallRestart/idle", restarted.State);
    }

    [Fact]
    public async Task PublicAsyncOperationsAwaitBusyProcessorLifecycleRequests()
    {
        async Task Run(bool restart)
        {
            using var effectEntered = new ManualResetEventSlim();
            using var releaseEffect = new ManualResetEventSlim();
            var lifecycleRequested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entries = 0;
            var name = restart ? "BusyAsyncCallRestart" : "BusyAsyncCallStop";
            var model = Hsm.Define(
                name,
                Hsm.Operation("lifecycle", new Func<TestMachine, Task>(async machine =>
                {
                    await Task.Yield();
                    if (restart)
                    {
                        await machine.Restart();
                    }
                    else
                    {
                        await machine.Stop();
                    }
                    lifecycleRequested.TrySetResult(true);
                })),
                Hsm.Initial(Hsm.Target("idle")),
                Hsm.State(
                    "idle",
                    Hsm.Entry<TestMachine>((_, _, _) => Interlocked.Increment(ref entries)),
                    Hsm.Transition(
                        Hsm.On("hold"),
                        Hsm.Effect<TestMachine>((_, _, _) =>
                        {
                            effectEntered.Set();
                            releaseEffect.Wait();
                        }))));
            var context = new Context();
            var machine = Hsm.Start(context, new TestMachine(), model);
            var dispatch = machine.Dispatch(new Event("hold"));
            Assert.True(effectEntered.Wait(TimeSpan.FromSeconds(1)));

            var call = Assert.IsAssignableFrom<Task<object?>>(Hsm.Call(context, machine, "lifecycle"));
            await lifecycleRequested.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(call.IsCompleted);
            releaseEffect.Set();
            await Task.WhenAll(dispatch, call).WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(restart ? $"/{name}/idle" : string.Empty, machine.State);
            Assert.Equal(restart ? 2 : 1, entries);
        }

        await Run(restart: false);
        await Run(restart: true);
    }

    [Fact]
    public async Task OperationCompletionFromRetiredActivityDoesNotDispatchOnCall()
    {
        using var operationEntered = new ManualResetEventSlim();
        using var releaseOperation = new ManualResetEventSlim();
        var callReturned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activityCount = 0;
        var model = Hsm.Define(
            "RetiredCallCompletion",
            Hsm.Operation("ping", new Action(() =>
            {
                operationEntered.Set();
                releaseOperation.Wait();
            })),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Activity<TestMachine>(async (ctx, machine, _) =>
                {
                    if (Interlocked.Increment(ref activityCount) != 1)
                    {
                        return;
                    }

                    await Task.Yield();
                    Hsm.Call(ctx, machine, "ping");
                    callReturned.TrySetResult(true);
                }),
                Hsm.Transition(Hsm.OnCall("ping"), Hsm.Target("../wrong"))),
            Hsm.State("wrong"));
        var machine = Hsm.Start(new Context(), new TestMachine(), model);
        Assert.True(operationEntered.Wait(TimeSpan.FromSeconds(1)));

        var restart = machine.Restart();
        releaseOperation.Set();
        await Task.WhenAll(restart, callReturned.Task).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(2, activityCount);
        Assert.Equal("/RetiredCallCompletion/idle", machine.State);
    }

    [Fact]
    public async Task RetiredActivityGenerationCannotMutatePeerInstance()
    {
        var firstActivityStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restartReturned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleWorkReturned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activityCount = 0;
        var calls = 0;
        TestMachine? destination = null;
        var destinationModel = Hsm.Define(
            "GenerationDestination",
            Hsm.Attribute("flag", false),
            Hsm.Operation("ping", new Action(() => Interlocked.Increment(ref calls))),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Transition(Hsm.On("stale"), Hsm.Target("../wrong"))),
            Hsm.State("wrong"));
        var sourceModel = Hsm.Define(
            "GenerationSource",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Activity<TestMachine>(async (ctx, _, _) =>
                {
                    if (Interlocked.Increment(ref activityCount) != 1)
                    {
                        return;
                    }

                    firstActivityStarted.TrySetResult(true);
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, ctx.CancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    await restartReturned.Task;
                    await Hsm.Set(ctx, destination!, "flag", true);
                    Hsm.Call(ctx, destination!, "ping");
                    await Hsm.Dispatch(ctx, destination!, new Event("stale"));
                    staleWorkReturned.TrySetResult(true);
                })));
        var context = new Context();
        destination = Hsm.Start(context, new TestMachine(), destinationModel);
        var source = Hsm.Start(context, new TestMachine(), sourceModel);
        await firstActivityStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await source.Restart().WaitAsync(TimeSpan.FromSeconds(1));
        restartReturned.TrySetResult(true);
        await staleWorkReturned.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(Hsm.Get<bool>(context, destination, "flag"));
        Assert.Equal(0, calls);
        Assert.Equal("/GenerationDestination/idle", destination.State);
    }

    [Fact]
    public async Task RetiredActivityGenerationCannotInstallPeerEngineWithNew()
    {
        var activityStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restartReturned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleNewReturned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var peerModel = Hsm.Define(
            "NewGenerationPeer",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle"));
        var peer = new TestMachine();
        var activityCount = 0;
        var sourceModel = Hsm.Define(
            "NewGenerationSource",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Activity<TestMachine>(async (ctx, _, _) =>
                {
                    if (Interlocked.Increment(ref activityCount) != 1) return;
                    activityStarted.TrySetResult(true);
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, ctx.CancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    await restartReturned.Task;
                    Hsm.New(peer, peerModel);
                    staleNewReturned.TrySetResult(true);
                })));
        var context = new Context();
        var source = Hsm.Start(context, new TestMachine(), sourceModel);
        await activityStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await source.Restart().WaitAsync(TimeSpan.FromSeconds(1));
        restartReturned.TrySetResult(true);
        await staleNewReturned.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Hsm.Start(context, peer, peerModel);

        Assert.Equal(2, activityCount);
        Assert.Equal("/NewGenerationPeer/idle", peer.State);
    }

    [Fact]
    public async Task QueuedPeerDispatchRevalidatesSourceGenerationBeforeProcessing()
    {
        using var pushEntered = new ManualResetEventSlim();
        using var releasePush = new ManualResetEventSlim();
        var events = new System.Collections.Generic.Queue<Event>();
        var queueGate = new object();
        var queue = new Queue(
            (_, @event) =>
            {
                if (@event.Name == "stale")
                {
                    pushEntered.Set();
                    releasePush.Wait();
                }
                lock (queueGate) events.Enqueue(@event);
                return null;
            },
            _ =>
            {
                lock (queueGate)
                {
                    return events.Count == 0 ? (null, null) : (events.Dequeue(), null);
                }
            },
            _ =>
            {
                lock (queueGate) return (events.Count, null);
            },
            () =>
            {
                lock (queueGate) events.Clear();
            });
        var destinationModel = Hsm.Define(
            "AtomicDestination",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle", Hsm.Transition(Hsm.On("stale"), Hsm.Target("../wrong"))),
            Hsm.State("wrong"));
        TestMachine? destination = null;
        var dispatchReturned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activityCount = 0;
        var sourceModel = Hsm.Define(
            "AtomicSource",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Activity<TestMachine>(async (ctx, _, _) =>
                {
                    if (Interlocked.Increment(ref activityCount) != 1)
                    {
                        return;
                    }

                    await Task.Yield();
                    await Hsm.Dispatch(ctx, destination!, new Event("stale"));
                    dispatchReturned.TrySetResult(true);
                })));
        var context = new Context();
        destination = Hsm.Start(
            context,
            new TestMachine(),
            destinationModel,
            new Config { Queue = queue });
        var source = Hsm.Start(context, new TestMachine(), sourceModel);
        Assert.True(pushEntered.Wait(TimeSpan.FromSeconds(1)));

        await source.Restart().WaitAsync(TimeSpan.FromSeconds(1));
        releasePush.Set();
        await dispatchReturned.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await destination.Dispatch(new Event("flush")).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(2, activityCount);
        Assert.Equal("/AtomicDestination/idle", destination.State);
    }

    [Fact]
    public async Task SourceRestartWaitsForPeerMutationHoldingItsGenerationLease()
    {
        using var guardEntered = new ManualResetEventSlim();
        using var releaseGuard = new ManualResetEventSlim();
        var destinationModel = Hsm.Define(
            "LeasedDestination",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Transition(
                    Hsm.On("leased"),
                    Hsm.Target("../done"),
                    Hsm.Guard<TestMachine>((_, _, _) =>
                    {
                        guardEntered.Set();
                        releaseGuard.Wait();
                        return true;
                    }))),
            Hsm.State("done"));
        TestMachine? destination = null;
        var dispatched = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activityCount = 0;
        var sourceModel = Hsm.Define(
            "LeasedSource",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Activity<TestMachine>(async (ctx, _, _) =>
                {
                    if (Interlocked.Increment(ref activityCount) != 1) return;
                    await Hsm.Dispatch(ctx, destination!, new Event("leased"));
                    dispatched.TrySetResult(true);
                })));
        var context = new Context();
        destination = Hsm.Start(context, new TestMachine(), destinationModel);
        var source = Hsm.Start(context, new TestMachine(), sourceModel);
        Assert.True(guardEntered.Wait(TimeSpan.FromSeconds(1)));

        var restart = Task.Run(async () => await source.Restart());
        try
        {
            await Task.Delay(25);
            Assert.False(restart.IsCompleted);
        }
        finally
        {
            releaseGuard.Set();
        }

        await Task.WhenAll(restart, dispatched.Task).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, activityCount);
        Assert.Equal("/LeasedDestination/done", destination.State);
    }

    [Fact]
    public async Task SourceRestartWaitsForPeerStartHoldingItsGenerationLease()
    {
        using var exitEntered = new ManualResetEventSlim();
        using var releaseExit = new ManualResetEventSlim();
        using var startCalled = new ManualResetEventSlim();
        var destinationModel = Hsm.Define(
            "LeasedStartDestination",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Exit<TestMachine>((_, _, _) =>
                {
                    exitEntered.Set();
                    releaseExit.Wait();
                })));
        var context = new Context();
        var destination = Hsm.Start(context, new TestMachine(), destinationModel);
        var startReturned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activityCount = 0;
        var sourceModel = Hsm.Define(
            "LeasedStartSource",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Activity<TestMachine>((ctx, _, _) =>
                {
                    if (Interlocked.Increment(ref activityCount) != 1) return;
                    startCalled.Set();
                    Hsm.Start(ctx, destination);
                    startReturned.TrySetResult(true);
                })));

        var stop = Task.Run(async () => await destination.Stop());
        Assert.True(exitEntered.Wait(TimeSpan.FromSeconds(1)));
        var source = Hsm.Start(context, new TestMachine(), sourceModel);
        Assert.True(startCalled.Wait(TimeSpan.FromSeconds(1)));
        await Task.Delay(25);

        var restart = source.Restart();
        try
        {
            await Task.Delay(25);
            Assert.False(restart.IsCompleted);
        }
        finally
        {
            releaseExit.Set();
        }

        await Task.WhenAll(stop, restart, startReturned.Task).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, activityCount);
        Assert.Equal("/LeasedStartDestination/idle", destination.State);
    }

    [Fact]
    public async Task RetiredActivityGenerationCannotMutatePeerLifecycles()
    {
        var firstActivityStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restartReturned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleWorkReturned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activityCount = 0;
        var restartEntries = new int[3];
        var context = new Context();

        Model Destination(string name, Action? entry = null) => Hsm.Define(
            name,
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle", entry is null ? [] : [Hsm.Entry<TestMachine>((_, _, _) => entry())]));

        var directStop = Hsm.Start(context, new TestMachine(), Destination("DirectStop"));
        var staticStop = Hsm.Start(context, new TestMachine(), Destination("StaticStop"));
        var groupStopMember = Hsm.Start(context, new TestMachine(), Destination("GroupStop"));
        var directRestart = Hsm.Start(
            context,
            new TestMachine(),
            Destination("DirectRestart", () => Interlocked.Increment(ref restartEntries[0])));
        var staticRestart = Hsm.Start(
            context,
            new TestMachine(),
            Destination("StaticRestart", () => Interlocked.Increment(ref restartEntries[1])));
        var groupRestartMember = Hsm.Start(
            context,
            new TestMachine(),
            Destination("GroupRestart", () => Interlocked.Increment(ref restartEntries[2])));
        var stopGroup = Hsm.MakeGroup("stop-group", directStop, groupStopMember);
        var restartGroup = Hsm.MakeGroup("restart-group", directRestart, groupRestartMember);
        var sourceModel = Hsm.Define(
            "LifecycleSource",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Activity<TestMachine>(async (ctx, _, _) =>
                {
                    if (Interlocked.Increment(ref activityCount) != 1)
                    {
                        return;
                    }

                    firstActivityStarted.TrySetResult(true);
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, ctx.CancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    await restartReturned.Task;
                    await directStop.Stop();
                    await Hsm.Stop(ctx, staticStop);
                    await stopGroup.Stop();
                    await directRestart.Restart();
                    await Hsm.Restart(ctx, staticRestart);
                    await restartGroup.Restart();
                    staleWorkReturned.TrySetResult(true);
                })));
        var source = Hsm.Start(context, new TestMachine(), sourceModel);
        await firstActivityStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await source.Restart().WaitAsync(TimeSpan.FromSeconds(1));
        restartReturned.TrySetResult(true);
        await staleWorkReturned.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("/DirectStop/idle", directStop.State);
        Assert.Equal("/StaticStop/idle", staticStop.State);
        Assert.Equal("/GroupStop/idle", groupStopMember.State);
        Assert.Equal("/DirectRestart/idle", directRestart.State);
        Assert.Equal("/StaticRestart/idle", staticRestart.State);
        Assert.Equal("/GroupRestart/idle", groupRestartMember.State);
        Assert.Equal(new[] { 1, 1, 1 }, restartEntries);
    }

    [Fact]
    public async Task StopInvalidatesActivityGenerationBeforeCancellationCallbacks()
    {
        var activityContext = new TaskCompletionSource<Context>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var destinationModel = Hsm.Define(
            "StopDestination",
            Hsm.Attribute("flag", false),
            Hsm.Operation("ping", new Action(() => Interlocked.Increment(ref calls))),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Transition(Hsm.On("stale"), Hsm.Target("../wrong"))),
            Hsm.State("wrong"));
        var sourceModel = Hsm.Define(
            "StopSource",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Activity<TestMachine>((ctx, _, _) =>
                {
                    activityContext.TrySetResult(ctx);
                    ctx.CancellationToken.WaitHandle.WaitOne();
                })));
        var context = new Context();
        var destination = Hsm.Start(context, new TestMachine(), destinationModel);
        var source = Hsm.Start(context, new TestMachine(), sourceModel);
        var linkedContext = await activityContext.Task.WaitAsync(TimeSpan.FromSeconds(1));
        using var registration = linkedContext.CancellationToken.Register(() =>
        {
            Hsm.Set(linkedContext, destination, "flag", true).GetAwaiter().GetResult();
            Hsm.Call(linkedContext, destination, "ping");
            Hsm.Dispatch(linkedContext, destination, new Event("stale")).GetAwaiter().GetResult();
        });

        await source.Stop().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(string.Empty, source.State);
        Assert.False(Hsm.Get<bool>(context, destination, "flag"));
        Assert.Equal(0, calls);
        Assert.Equal("/StopDestination/idle", destination.State);
    }

    [Fact]
    public void StopFromEntryBehaviorAbortsRemainingEntrySetup()
    {
        var trace = new List<string>();
        var model = Hsm.Define(
            "StopFromEntry",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Entry<TestMachine>(
                    (_, machine, _) =>
                    {
                        trace.Add("stop");
                        machine.Stop().GetAwaiter().GetResult();
                    },
                    (_, _, _) => trace.Add("late-entry")),
                Hsm.Activity<TestMachine>((_, _, _) => trace.Add("activity"))));

        var machine = Hsm.Start(new Context(), new TestMachine(), model);

        Assert.Equal(string.Empty, machine.State);
        Assert.Equal(new[] { "stop" }, trace);
    }

    [Fact]
    public async Task StoppingOneInstanceDoesNotCancelItsPeers()
    {
        var model = Hsm.Define(
            "IndependentStop",
            Hsm.State("idle", Hsm.Transition(Hsm.On("go"), Hsm.Target("../done"))),
            Hsm.State("done"),
            Hsm.Initial(Hsm.Target("idle")));
        var context = new Context();
        var first = Hsm.Start(context, new TestMachine(), model);
        var second = Hsm.Start(context, new TestMachine(), model);

        await first.Stop();
        await second.Dispatch(new Event("go"));

        Assert.Equal(string.Empty, first.State);
        Assert.Equal("/IndependentStop/done", second.State);
        Assert.False(context.IsDone);
    }

    [Fact]
    public async Task ContextLookupTracksOnlyStartedInstancesAcrossStopAndStart()
    {
        var model = Hsm.Define(
            "ContextLifecycle",
            Hsm.State("idle"),
            Hsm.Initial(Hsm.Target("idle")));
        var context = new Context();
        var first = Hsm.Start(context, new TestMachine(), model, new Config { Id = "first" });
        var second = Hsm.Start(context, new TestMachine(), model, new Config { Id = "second" });

        await first.Stop();

        Assert.Same(second, Hsm.FromContext(context));
        Assert.Equal(new[] { second }, Hsm.InstancesFromContext(context));

        Hsm.Start(context, first);

        Assert.Equal(new IInstance[] { second, first }, Hsm.InstancesFromContext(context));
    }

    [Fact]
    public async Task ConcurrentStartsBindExactlyOneContext()
    {
        var model = Hsm.Define(
            "ConcurrentStart",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle"));
        var machine = Hsm.New(new TestMachine(), model);
        var first = new Context();
        var second = new Context();
        using var ready = new CountdownEvent(2);
        using var go = new ManualResetEventSlim();

        Task<Exception?> Start(Context context) => Task.Run(() =>
        {
            ready.Signal();
            go.Wait();
            try
            {
                Hsm.Start(context, machine);
                return null;
            }
            catch (Exception error)
            {
                return error;
            }
        });

        var starts = new[] { Start(first), Start(second) };
        Assert.True(ready.Wait(TimeSpan.FromSeconds(1)));
        go.Set();
        var results = await Task.WhenAll(starts);

        Assert.Single(results, result => result is null);
        Assert.Single(results, result => result is AlreadyStartedException);
        Assert.Single(Hsm.InstancesFromContext(machine.Context));
        Assert.Empty(Hsm.InstancesFromContext(ReferenceEquals(machine.Context, first) ? second : first));
    }

    [Fact]
    public async Task ConcurrentNewInstallsExactlyOneEngine()
    {
        var model = Hsm.Define(
            "ConcurrentNew",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle"));
        var machine = new TestMachine();
        using var ready = new CountdownEvent(2);
        using var go = new ManualResetEventSlim();

        Task<Exception?> Create() => Task.Run(() =>
        {
            ready.Signal();
            go.Wait();
            try
            {
                Hsm.New(machine, model);
                return null;
            }
            catch (Exception error)
            {
                return error;
            }
        });

        var creates = new[] { Create(), Create() };
        Assert.True(ready.Wait(TimeSpan.FromSeconds(1)));
        go.Set();
        var results = await Task.WhenAll(creates);

        Assert.Single(results, result => result is null);
        Assert.Single(results, result => result is AlreadyStartedException);
        Hsm.Start(new Context(), machine);
        Assert.Equal("/ConcurrentNew/idle", machine.State);
    }

    [Fact]
    public async Task StopAndRestartSerializeWithAnInFlightTransition()
    {
        using var effectEntered = new ManualResetEventSlim();
        using var releaseEffect = new ManualResetEventSlim();
        var entries = 0;
        var model = Hsm.Define(
            "SerializedLifecycle",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Transition(
                    Hsm.On("go"),
                    Hsm.Target("../done"),
                    Hsm.Effect<TestMachine>((_, _, _) =>
                    {
                        effectEntered.Set();
                        releaseEffect.Wait();
                    }))),
            Hsm.State("done", Hsm.Entry<TestMachine>((_, _, _) => Interlocked.Increment(ref entries))));
        var context = new Context();
        var machine = Hsm.Start(context, new TestMachine(), model);

        var dispatch = machine.Dispatch(new Event("go"));
        Assert.True(effectEntered.Wait(TimeSpan.FromSeconds(1)));
        var stop = Task.Run(() => machine.Stop());
        Assert.False(stop.IsCompleted);
        releaseEffect.Set();
        await Task.WhenAll(dispatch, stop);

        Assert.Equal(string.Empty, machine.State);
        var entriesAfterStop = entries;
        await Task.Delay(20);
        Assert.Equal(entriesAfterStop, entries);

        Hsm.Start(context, machine);
        effectEntered.Reset();
        releaseEffect.Reset();
        dispatch = machine.Dispatch(new Event("go"));
        Assert.True(effectEntered.Wait(TimeSpan.FromSeconds(1)));
        var restart = Task.Run(() => machine.Restart());
        Assert.False(restart.IsCompleted);
        releaseEffect.Set();
        await Task.WhenAll(dispatch, restart);

        Assert.Equal("/SerializedLifecycle/idle", machine.State);
    }

    [Fact]
    public async Task LifecycleCallsFromEffectsDoNotDeadlockOrResumeTheInterruptedTransition()
    {
        var stopModel = Hsm.Define(
            "StopFromEffect",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Transition(
                    Hsm.On("stop"),
                    Hsm.Target("../wrong"),
                    Hsm.Effect<TestMachine>((_, machine, _) => machine.Stop().GetAwaiter().GetResult()))),
            Hsm.State("wrong"));
        var stopped = Hsm.Start(new Context(), new TestMachine(), stopModel);

        await stopped.Dispatch(new Event("stop")).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(string.Empty, stopped.State);

        var restartModel = Hsm.Define(
            "RestartFromEffect",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Transition(
                    Hsm.On("restart"),
                    Hsm.Target("../wrong"),
                    Hsm.Effect<TestMachine>((_, machine, _) => machine.Restart().GetAwaiter().GetResult()))),
            Hsm.State("wrong"));
        var restarted = Hsm.Start(new Context(), new TestMachine(), restartModel);

        await restarted.Dispatch(new Event("restart")).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("/RestartFromEffect/idle", restarted.State);
    }

    [Fact]
    public async Task InactiveRuntimeApisReportRuntimeErrorsNotValidationErrors()
    {
        var model = Hsm.Define(
            "InactiveRuntimeErrors",
            Hsm.Attribute("flag", false),
            Hsm.Operation("ping", new Action(() => { })),
            Hsm.State("idle"),
            Hsm.Initial(Hsm.Target("idle")));
        var context = new Context();
        var machine = Hsm.New(new TestMachine(), model);

        await Assert.ThrowsAsync<HsmRuntimeException>(() => machine.Dispatch(new Event("go")));
        await Assert.ThrowsAsync<HsmRuntimeException>(() => machine.Stop());
        await Assert.ThrowsAsync<HsmRuntimeException>(() => machine.Restart());
        await Assert.ThrowsAsync<HsmRuntimeException>(() => Hsm.Set(context, machine, "flag", true));
        Assert.Throws<HsmRuntimeException>(() => Hsm.Get<object?>(context, machine, "flag"));
        Assert.Throws<HsmRuntimeException>(() => Hsm.Call(context, machine, "ping"));
        Assert.Throws<HsmRuntimeException>(() => Hsm.TakeSnapshot(context, machine));

        Assert.Throws<MissingHsmException>(() => Hsm.Start(context, new TestMachine()));

        var unbound = new TestMachine();
        Assert.Throws<MissingHsmException>(() => Hsm.Get<object?>(context, unbound, "flag"));
        await Assert.ThrowsAsync<MissingHsmException>(() => Hsm.Set(context, unbound, "flag", true));
        Assert.Throws<MissingHsmException>(() => Hsm.TakeSnapshot(context, unbound));

        var started = Hsm.Start(context, new TestMachine(), model);
        await Assert.ThrowsAsync<AttributeHsmException>(() => Hsm.Set(context, started, string.Empty, true));
    }

    [Fact]
    public async Task ShallowHistoryRestoresDirectChildAndUsesChildInitial()
    {
        var model = Hsm.Define(
            "ShallowHistoryRules",
            Hsm.Initial(Hsm.Target("container")),
            Hsm.State(
                "container",
                Hsm.Initial(Hsm.Target("region")),
                Hsm.State(
                    "region",
                    Hsm.Initial(Hsm.Target("a1")),
                    Hsm.State("a1", Hsm.Transition(Hsm.On("next"), Hsm.Target("../a2"))),
                    Hsm.State("a2", Hsm.Transition(Hsm.On("leave"), Hsm.Target("/ShallowHistoryRules/outside")))),
                Hsm.ShallowHistory("history", Hsm.Target("region"))),
            Hsm.State(
                "outside",
                Hsm.Transition(Hsm.On("resume"), Hsm.Target("/ShallowHistoryRules/container/history"))));

        var machine = Hsm.Start(new Context(), new TestMachine(), model);
        await machine.Dispatch(new Event("next"));
        await machine.Dispatch(new Event("leave"));
        await machine.Dispatch(new Event("resume"));

        Assert.Equal("/ShallowHistoryRules/container/region/a1", machine.State);
    }

    [Fact]
    public async Task SourceQualifiedSiblingTransitionsIgnoreInactiveSources()
    {
        var model = Hsm.Define(
            "QualifiedSiblingSources",
            Hsm.Initial(Hsm.Target("container/hot")),
            Hsm.State(
                "container",
                Hsm.State("cold"),
                Hsm.State("hot"),
                Hsm.Transition(
                    Hsm.Source("/QualifiedSiblingSources/container/cold"),
                    Hsm.On("leave"),
                    Hsm.Target("/QualifiedSiblingSources/wrong")),
                Hsm.Transition(
                    Hsm.Source("/QualifiedSiblingSources/container/hot"),
                    Hsm.On("leave"),
                    Hsm.Target("/QualifiedSiblingSources/done"))),
            Hsm.State("wrong"),
            Hsm.State("done"));

        var machine = Hsm.Start(new Context(), new TestMachine(), model);
        await machine.Dispatch(new Event("leave"));

        Assert.Equal("/QualifiedSiblingSources/done", machine.State);
    }

    [Fact]
    public async Task DeepHistoryRestoresLeafStateExactly()
    {
        var model = Hsm.Define(
            "DeepHistoryRules",
            Hsm.Initial(Hsm.Target("container")),
            Hsm.State(
                "container",
                Hsm.Initial(Hsm.Target("region")),
                Hsm.State(
                    "region",
                    Hsm.Initial(Hsm.Target("a1")),
                    Hsm.State("a1", Hsm.Transition(Hsm.On("next"), Hsm.Target("../a2"))),
                    Hsm.State("a2", Hsm.Transition(Hsm.On("leave"), Hsm.Target("/DeepHistoryRules/outside")))),
                Hsm.DeepHistory("history", Hsm.Target("region"))),
            Hsm.State(
                "outside",
                Hsm.Transition(Hsm.On("resume"), Hsm.Target("/DeepHistoryRules/container/history"))));

        var machine = Hsm.Start(new Context(), new TestMachine(), model);
        await machine.Dispatch(new Event("next"));
        await machine.Dispatch(new Event("leave"));
        await machine.Dispatch(new Event("resume"));

        Assert.Equal("/DeepHistoryRules/container/region/a2", machine.State);
    }

    [Fact]
    public async Task HistoryUsesDefaultTransitionBeforeAnySnapshotExists()
    {
        var model = Hsm.Define(
            "HistoryDefaultRules",
            Hsm.Initial(Hsm.Target("outside")),
            Hsm.State(
                "container",
                Hsm.Initial(Hsm.Target("region")),
                Hsm.State(
                    "region",
                    Hsm.Initial(Hsm.Target("a1")),
                    Hsm.State("a1"),
                    Hsm.State("a2")),
                Hsm.ShallowHistory(
                    "history",
                    Hsm.Transition(Hsm.Target("region")))),
            Hsm.State(
                "outside",
                Hsm.Transition(Hsm.On("resume"), Hsm.Target("/HistoryDefaultRules/container/history"))));

        var machine = Hsm.Start(new Context(), new TestMachine(), model);
        await machine.Dispatch(new Event("resume"));

        Assert.Equal("/HistoryDefaultRules/container/region/a1", machine.State);
    }

    [Fact]
    public async Task RestartClearsHistorySnapshots()
    {
        var model = Hsm.Define(
            "RestartHistoryRules",
            Hsm.Initial(Hsm.Target("outside")),
            Hsm.State(
                "container",
                Hsm.Initial(Hsm.Target("region")),
                Hsm.State(
                    "region",
                    Hsm.Initial(Hsm.Target("a1")),
                    Hsm.State("a1", Hsm.Transition(Hsm.On("next"), Hsm.Target("../a2"))),
                    Hsm.State("a2", Hsm.Transition(Hsm.On("leave"), Hsm.Target("/RestartHistoryRules/outside")))),
                Hsm.DeepHistory(
                    "history",
                    Hsm.Transition(Hsm.Target("region")))),
            Hsm.State(
                "outside",
                Hsm.Transition(Hsm.On("enter"), Hsm.Target("/RestartHistoryRules/container")),
                Hsm.Transition(Hsm.On("resume"), Hsm.Target("/RestartHistoryRules/container/history"))));

        var context = new Context();
        var machine = Hsm.Start(context, new TestMachine(), model);
        await machine.Dispatch(new Event("enter"));
        await machine.Dispatch(new Event("next"));
        await machine.Dispatch(new Event("leave"));

        await Hsm.Restart(context, machine);
        await machine.Dispatch(new Event("resume"));

        Assert.Equal("/RestartHistoryRules/container/region/a1", machine.State);
    }

    [Fact]
    public async Task TopLevelFinalCompletesWithoutCancellingPeerContextAndIgnoresFurtherDispatch()
    {
        var model = Hsm.Define(
            "FinalCancellationRules",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle", Hsm.Transition(Hsm.On("finish"), Hsm.Target("../done"))),
            Hsm.Final("done"));

        var context = new Context();
        var machine = Hsm.Start(context, new TestMachine(), model);
        await machine.Dispatch(new Event("finish"));

        Assert.False(context.IsDone);
        Assert.Equal("/FinalCancellationRules/done", machine.State);

        await machine.Dispatch(new Event("ignored"));
        Assert.Equal("/FinalCancellationRules/done", machine.State);
    }

    [Fact]
    public async Task EffectExceptionsRaiseErrorEvents()
    {
        var model = Hsm.Define(
            "EffectErrorRules",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Transition(
                    Hsm.On("boom"),
                    Hsm.Effect<TestMachine>((_, _, _) => throw new InvalidOperationException("boom"))),
                Hsm.Transition(Hsm.On("hsm/error"), Hsm.Target("../recovered"))),
            Hsm.State("recovered"));

        var machine = Hsm.Start(new Context(), new TestMachine(), model);
        await machine.Dispatch(new Event("boom"));

        Assert.Equal("/EffectErrorRules/recovered", machine.State);
    }

    [Fact]
    public async Task GuardExceptionsRaiseErrorEvents()
    {
        var model = Hsm.Define(
            "GuardErrorRules",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Transition(
                    Hsm.On("boom"),
                    Hsm.Guard<TestMachine>((_, _, _) => throw new InvalidOperationException("boom")),
                    Hsm.Target("../never")),
                Hsm.Transition(Hsm.On("hsm/error"), Hsm.Target("../recovered"))),
            Hsm.State("never"),
            Hsm.State("recovered"));

        var machine = Hsm.Start(new Context(), new TestMachine(), model);
        await machine.Dispatch(new Event("boom"));

        Assert.Equal("/GuardErrorRules/recovered", machine.State);
    }

    [Fact]
    public void FromContextReturnsFirstStartedInstance()
    {
        var model = Hsm.Define(
            "ContextRules",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle"));

        var context = new Context();
        var first = Hsm.Start(context, new TestMachine(), model);
        var second = Hsm.Start(context, new TestMachine(), model);

        Assert.Same(first, Hsm.FromContext(context));
        Assert.Contains(second, Hsm.InstancesFromContext(context));
    }
}
