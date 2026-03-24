using Xunit;

namespace Stateforward.Hsm.Tests;

public sealed class HistoryAndLifecycleTests
{
    private sealed class TestMachine : Instance
    {
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
                Hsm.ShallowHistory("history")),
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
                Hsm.DeepHistory("history")),
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
                Hsm.State(
                    "region",
                    Hsm.Initial(Hsm.Target("a1")),
                    Hsm.State("a1"),
                    Hsm.State("a2")),
                Hsm.ShallowHistory(
                    "history",
                    Hsm.Transition(Hsm.Target("../region")))),
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
                Hsm.State(
                    "region",
                    Hsm.Initial(Hsm.Target("a1")),
                    Hsm.State("a1", Hsm.Transition(Hsm.On("next"), Hsm.Target("../a2"))),
                    Hsm.State("a2", Hsm.Transition(Hsm.On("leave"), Hsm.Target("/RestartHistoryRules/outside")))),
                Hsm.DeepHistory(
                    "history",
                    Hsm.Transition(Hsm.Target("../region")))),
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
    public async Task TopLevelFinalCancelsContextAndIgnoresFurtherDispatch()
    {
        var model = Hsm.Define(
            "FinalCancellationRules",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle", Hsm.Transition(Hsm.On("finish"), Hsm.Target("../done"))),
            Hsm.Final("done"));

        var context = new Context();
        var machine = Hsm.Start(context, new TestMachine(), model);
        await machine.Dispatch(new Event("finish"));

        Assert.True(context.IsDone);
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
                Hsm.Transition(Hsm.On("hsm.error"), Hsm.Target("../recovered"))),
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
                Hsm.Transition(Hsm.On("hsm.error"), Hsm.Target("../recovered"))),
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
