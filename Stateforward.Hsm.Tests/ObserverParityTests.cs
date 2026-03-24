using Xunit;

namespace Stateforward.Hsm.Tests;

public sealed class ObserverParityTests
{
    private sealed class TestMachine : Instance
    {
    }

    [Fact]
    public async Task WaitersObserveDispatchProcessEntryExitAndExecution()
    {
        var activityStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = Hsm.Define(
            "ObserverParity",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle", Hsm.Transition(Hsm.On("go"), Hsm.Target("../running"))),
            Hsm.State(
                "running",
                Hsm.Activity<TestMachine>((ctx, _, _) =>
                {
                    activityStarted.TrySetResult(true);
                    ctx.CancellationToken.WaitHandle.WaitOne();
                }),
                Hsm.Transition(Hsm.On("stop"), Hsm.Target("../done"))),
            Hsm.State("done"));

        var context = new Context();
        var machine = Hsm.Start(context, new TestMachine(), model);

        var afterDispatch = Hsm.AfterDispatch(context, machine, new Event("go"));
        var afterProcess = Hsm.AfterProcess(context, machine, new Event("go"));
        var afterAnyProcess = Hsm.AfterProcess(context, machine);
        var afterEntry = Hsm.AfterEntry(context, machine, "/ObserverParity/running");

        await machine.Dispatch(new Event("go"));
        await Task.WhenAll(afterDispatch, afterProcess, afterAnyProcess, afterEntry).WaitAsync(TimeSpan.FromSeconds(1));
        await activityStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var afterExit = Hsm.AfterExit(context, machine, "/ObserverParity/running");
        var afterExecuted = Hsm.AfterExecuted(context, machine, "/ObserverParity/running");

        await machine.Dispatch(new Event("stop"));
        await Task.WhenAll(afterExit, afterExecuted).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("/ObserverParity/done", machine.State);
    }
}
