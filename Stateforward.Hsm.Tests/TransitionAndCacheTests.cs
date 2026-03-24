using Xunit;

namespace Stateforward.Hsm.Tests;

public sealed class TransitionAndCacheTests
{
    private sealed class TestMachine : Instance
    {
    }

    [Fact]
    public async Task InternalTransitionRunsEffectWithoutExitOrEntry()
    {
        var trace = new List<string>();
        var model = Hsm.Define(
            "InternalTransitionRules",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Entry<TestMachine>((_, _, _) => trace.Add("entry")),
                Hsm.Exit<TestMachine>((_, _, _) => trace.Add("exit")),
                Hsm.Transition(
                    Hsm.On("tick"),
                    Hsm.Effect<TestMachine>((_, _, _) => trace.Add("effect")))));

        var machine = Hsm.Start(new Context(), new TestMachine(), model);
        trace.Clear();

        await machine.Dispatch(new Event("tick"));

        Assert.Equal("/InternalTransitionRules/idle", machine.State);
        Assert.Equal(new[] { "effect" }, trace);
    }

    [Fact]
    public async Task SelfTransitionExecutesExitEffectEntryOrder()
    {
        var trace = new List<string>();
        var model = Hsm.Define(
            "SelfTransitionRules",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Entry<TestMachine>((_, _, _) => trace.Add("entry")),
                Hsm.Exit<TestMachine>((_, _, _) => trace.Add("exit")),
                Hsm.Transition(
                    Hsm.On("loop"),
                    Hsm.Target("."),
                    Hsm.Effect<TestMachine>((_, _, _) => trace.Add("effect")))));

        var machine = Hsm.Start(new Context(), new TestMachine(), model);
        trace.Clear();

        await machine.Dispatch(new Event("loop"));

        Assert.Equal("/SelfTransitionRules/idle", machine.State);
        Assert.Equal(new[] { "exit", "effect", "entry" }, trace);
    }

    [Fact]
    public async Task LocalTransitionToDescendantExitsLeafWithoutReenteringComposite()
    {
        var trace = new List<string>();
        var model = Hsm.Define(
            "LocalTransitionRules",
            Hsm.Initial(Hsm.Target("parent")),
            Hsm.State(
                "parent",
                Hsm.Entry<TestMachine>((_, _, _) => trace.Add("parent_entry")),
                Hsm.Exit<TestMachine>((_, _, _) => trace.Add("parent_exit")),
                Hsm.State(
                    "first",
                    Hsm.Entry<TestMachine>((_, _, _) => trace.Add("first_entry")),
                    Hsm.Exit<TestMachine>((_, _, _) => trace.Add("first_exit"))),
                Hsm.State(
                    "second",
                    Hsm.Entry<TestMachine>((_, _, _) => trace.Add("second_entry")),
                    Hsm.Exit<TestMachine>((_, _, _) => trace.Add("second_exit"))),
                Hsm.Initial(Hsm.Target("first")),
                Hsm.Transition(Hsm.On("switch"), Hsm.Target("second"))));

        var machine = Hsm.Start(new Context(), new TestMachine(), model);
        trace.Clear();

        await machine.Dispatch(new Event("switch"));

        Assert.Equal("/LocalTransitionRules/parent/second", machine.State);
        Assert.Equal(new[] { "first_exit", "second_entry" }, trace);
    }

    [Fact]
    public async Task ExternalSiblingTransitionExitsSourceAndEntersTarget()
    {
        var trace = new List<string>();
        var model = Hsm.Define(
            "ExternalTransitionRules",
            Hsm.Initial(Hsm.Target("parent")),
            Hsm.State(
                "parent",
                Hsm.Entry<TestMachine>((_, _, _) => trace.Add("parent_entry")),
                Hsm.Exit<TestMachine>((_, _, _) => trace.Add("parent_exit")),
                Hsm.State(
                    "first",
                    Hsm.Entry<TestMachine>((_, _, _) => trace.Add("first_entry")),
                    Hsm.Exit<TestMachine>((_, _, _) => trace.Add("first_exit")),
                    Hsm.Transition(Hsm.On("switch"), Hsm.Target("../second"))),
                Hsm.State(
                    "second",
                    Hsm.Entry<TestMachine>((_, _, _) => trace.Add("second_entry")),
                    Hsm.Exit<TestMachine>((_, _, _) => trace.Add("second_exit"))),
                Hsm.Initial(Hsm.Target("first"))));

        var machine = Hsm.Start(new Context(), new TestMachine(), model);
        trace.Clear();

        await machine.Dispatch(new Event("switch"));

        Assert.Equal("/ExternalTransitionRules/parent/second", machine.State);
        Assert.Equal(new[] { "first_exit", "second_entry" }, trace);
    }

    [Fact]
    public void ModelBuildPrecomputesTransitionAndDeferredLookupTables()
    {
        var model = Hsm.Define(
            "CacheRules",
            Hsm.Initial(Hsm.Target("parent")),
            Hsm.State(
                "parent",
                Hsm.Defer("wait"),
                Hsm.State("child"),
                Hsm.Initial(Hsm.Target("child")),
                Hsm.Transition(Hsm.On("advance"), Hsm.Target("../done"))),
            Hsm.State("done"));

        Assert.True(model.TransitionMap.TryGetValue("/CacheRules/parent/child", out var childBuckets));
        Assert.True(childBuckets.TryGetValue("advance", out var transitions));
        Assert.Single(transitions);

        Assert.True(model.DeferredMap.TryGetValue("/CacheRules/parent/child", out var deferred));
        Assert.Contains("wait", deferred);

        var transition = transitions[0];
        Assert.True(transition.Paths.TryGetValue("/CacheRules/parent/child", out var path));
        Assert.Equal(new[] { "/CacheRules/done" }, path.Enter);
        Assert.Equal(new[] { "/CacheRules/parent/child", "/CacheRules/parent" }, path.Exit);
    }
}
