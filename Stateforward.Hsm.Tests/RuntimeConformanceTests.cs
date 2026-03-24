using Xunit;

namespace Stateforward.Hsm.Tests;

public sealed class RuntimeConformanceTests
{
    private sealed class TestMachine : Instance
    {
    }

    [Fact]
    public void CompositeInitialEntersNestedLeafState()
    {
        var model = Hsm.Define(
            "CompositeInitial",
            Hsm.State(
                "parent",
                Hsm.State("child"),
                Hsm.Initial(Hsm.Target("child"))),
            Hsm.Initial(Hsm.Target("parent")));

        var machine = Hsm.Start(new Context(), new TestMachine(), model);
        Assert.Equal("/CompositeInitial/parent/child", machine.State);
    }

    [Fact]
    public async Task AnyEventIsFallbackAndSpecificPrecedesWildcard()
    {
        var trace = new List<string>();
        var model = Hsm.Define(
            "AnyEventRules",
            Hsm.Initial(Hsm.Target("ready")),
            Hsm.State(
                "ready",
                Hsm.Transition(
                    Hsm.On("special"),
                    Hsm.Target("../special"),
                    Hsm.Effect<TestMachine>((_, _, _) => trace.Add("special"))),
                Hsm.Transition(
                    Hsm.On(Event.AnyName),
                    Hsm.Target("../fallback"),
                    Hsm.Effect<TestMachine>((_, _, evt) => trace.Add("fallback:" + evt.Name)))),
            Hsm.State("special", Hsm.Transition(Hsm.On("reset"), Hsm.Target("../ready"))),
            Hsm.State("fallback"));

        var machine = Hsm.Start(new Context(), new TestMachine(), model);
        await machine.Dispatch(new Event("special"));
        Assert.Equal("/AnyEventRules/special", machine.State);

        await machine.Dispatch(new Event("reset"));
        Assert.Equal("/AnyEventRules/ready", machine.State);

        await machine.Dispatch(new Event("other"));
        Assert.Equal("/AnyEventRules/fallback", machine.State);
        Assert.Equal(new[] { "special", "fallback:other" }, trace);
    }

    [Fact]
    public async Task FirstPassingGuardWinsAndChoiceRoutesByGuardOrder()
    {
        var trace = new List<string>();
        var guardedModel = Hsm.Define(
            "GuardRules",
            Hsm.Initial(Hsm.Target("ready")),
            Hsm.State(
                "ready",
                Hsm.Transition(
                    Hsm.On("guarded"),
                    Hsm.Guard<TestMachine>((_, _, _) => false),
                    Hsm.Target("../first")),
                Hsm.Transition(
                    Hsm.On("guarded"),
                    Hsm.Guard<TestMachine>((_, _, _) => true),
                    Hsm.Target("../second"),
                    Hsm.Effect<TestMachine>((_, _, _) => trace.Add("guard_second")))),
            Hsm.State("first"),
            Hsm.State("second"));

        var guardedMachine = Hsm.Start(new Context(), new TestMachine(), guardedModel);
        await guardedMachine.Dispatch(new Event("guarded"));
        Assert.Equal("/GuardRules/second", guardedMachine.State);
        Assert.Equal(new[] { "guard_second" }, trace);

        var choiceModel = Hsm.Define(
            "ChoiceRules",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle"),
            Hsm.State("positive"),
            Hsm.State("non_positive"),
            Hsm.Transition(
                Hsm.On("choose"),
                Hsm.Source("idle"),
                Hsm.Target("/ChoiceRules/decision")),
            Hsm.Choice(
                "decision",
                Hsm.Transition(
                    Hsm.Target("/ChoiceRules/positive"),
                    Hsm.Guard<TestMachine>((_, _, evt) => evt.Data is int value && value > 0)),
                Hsm.Transition(Hsm.Target("/ChoiceRules/non_positive"))));

        var choiceMachine = Hsm.Start(new Context(), new TestMachine(), choiceModel);
        await choiceMachine.Dispatch(new Event("choose", data: 3));
        Assert.Equal("/ChoiceRules/positive", choiceMachine.State);
    }

    [Fact]
    public async Task AttributesAndOperationsDriveTransitions()
    {
        var attributeModel = Hsm.Define(
            "AttributeRules",
            Hsm.Attribute("message", ""),
            Hsm.State(
                "idle",
                Hsm.Transition(Hsm.OnSet("message"), Hsm.Target("../updated"))),
            Hsm.State("updated"),
            Hsm.Initial(Hsm.Target("idle")));

        var attributeContext = new Context();
        var attributeMachine = Hsm.Start(attributeContext, new TestMachine(), attributeModel);
        await Hsm.Set(attributeContext, attributeMachine, "message", "hello");
        Assert.Equal("/AttributeRules/updated", attributeMachine.State);
        Assert.Equal("hello", Hsm.Get<string>(attributeContext, attributeMachine, "message"));

        await Hsm.Restart(attributeContext, attributeMachine);
        Assert.Equal("/AttributeRules/idle", attributeMachine.State);
        Assert.Equal(string.Empty, Hsm.Get<string>(attributeContext, attributeMachine, "message"));

        await Hsm.Set(attributeContext, attributeMachine, "message", string.Empty);
        Assert.Equal("/AttributeRules/idle", attributeMachine.State);

        var callCount = 0;
        var operationModel = Hsm.Define(
            "OperationRules",
            Hsm.Operation("activate", new Func<string>(() =>
            {
                callCount++;
                return "ok";
            })),
            Hsm.State(
                "idle",
                Hsm.Transition(Hsm.OnCall("activate"), Hsm.Target("../done"))),
            Hsm.State("done"),
            Hsm.Initial(Hsm.Target("idle")));

        var operationMachine = Hsm.Start(new Context(), new TestMachine(), operationModel);
        var result = Hsm.Call(operationMachine.Context, operationMachine, "activate");
        Assert.Equal("ok", result);
        Assert.Equal(1, callCount);
        Assert.Equal("/OperationRules/done", operationMachine.State);
    }

    [Fact]
    public async Task CompletionEventsPreemptRegularEventsAndDeferredEventsReplay()
    {
        var trace = new List<string>();
        var completionModel = Hsm.Define(
            "CompletionPriority",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Transition(
                    Hsm.On("trigger"),
                    Hsm.Effect<TestMachine>((ctx, sm, _) =>
                    {
                        trace.Add("trigger");
                        sm.Dispatch(new Event("regular")).GetAwaiter().GetResult();
                        sm.Dispatch(new Event("priority", Kind.CompletionEvent)).GetAwaiter().GetResult();
                    })),
                Hsm.Transition(
                    Hsm.On("priority"),
                    Hsm.Target("../priority"),
                    Hsm.Effect<TestMachine>((_, _, _) => trace.Add("priority"))),
                Hsm.Transition(
                    Hsm.On("regular"),
                    Hsm.Target("../regular"),
                    Hsm.Effect<TestMachine>((_, _, _) => trace.Add("regular")))),
            Hsm.State("priority"),
            Hsm.State("regular"));

        var completionMachine = Hsm.Start(new Context(), new TestMachine(), completionModel);
        await completionMachine.Dispatch(new Event("trigger"));
        Assert.Equal("/CompletionPriority/priority", completionMachine.State);
        Assert.Equal(new[] { "trigger", "priority" }, trace);

        trace.Clear();
        var deferredModel = Hsm.Define(
            "DeferredReplay",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Defer("resume"),
                Hsm.Transition(
                    Hsm.On("activate"),
                    Hsm.Target("../ready"),
                    Hsm.Effect<TestMachine>((_, _, _) => trace.Add("activate")))),
            Hsm.State(
                "ready",
                Hsm.Transition(
                    Hsm.On("resume"),
                    Hsm.Target("../done"),
                    Hsm.Effect<TestMachine>((_, _, _) => trace.Add("resume")))),
            Hsm.State("done"));

        var deferredMachine = Hsm.Start(new Context(), new TestMachine(), deferredModel);
        var resumeDispatch = deferredMachine.Dispatch(new Event("resume"));
        Assert.Equal("/DeferredReplay/idle", deferredMachine.State);
        await deferredMachine.Dispatch(new Event("activate"));
        await resumeDispatch;
        Assert.Equal("/DeferredReplay/done", deferredMachine.State);
        Assert.Equal(new[] { "activate", "resume" }, trace);
    }

    [Fact]
    public async Task IdentityAndDispatchAllAndGroupFlatteningWork()
    {
        var model = Hsm.Define(
            "IdentityRules",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle", Hsm.Transition(Hsm.On("broadcast"), Hsm.Target("../received"))),
            Hsm.State("received"));

        var sharedContext = new Context();
        var alpha = Hsm.Start(sharedContext, new TestMachine(), model, new Config { Id = "alpha" });
        var bravo = Hsm.Start(sharedContext, new TestMachine(), model, new Config { Id = "bravo" });
        var charlie = Hsm.Start(sharedContext, new TestMachine(), model, new Config { Id = "charlie" });

        Assert.Equal("alpha", Hsm.ID(alpha));
        Assert.Equal("/IdentityRules", Hsm.QualifiedName(alpha));
        Assert.Equal("IdentityRules", Hsm.Name(alpha));
        Assert.Equal("/IdentityRules/idle", alpha.State);

        var group = new Group(alpha, new Group(bravo, charlie));
        await Hsm.DispatchAll(sharedContext, new Event("broadcast"));

        Assert.Equal("/IdentityRules/received", alpha.State);
        Assert.Equal("/IdentityRules/received", bravo.State);
        Assert.Equal("/IdentityRules/received", charlie.State);
        Assert.Equal(3, Hsm.InstancesFromContext(sharedContext).Count);

        await group.Restart();
        Assert.Equal("/IdentityRules/idle", alpha.State);
        Assert.Equal("/IdentityRules/idle", bravo.State);
        Assert.Equal("/IdentityRules/idle", charlie.State);
    }
}
