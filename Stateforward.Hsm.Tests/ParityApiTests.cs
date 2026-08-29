using Xunit;

namespace Stateforward.Hsm.Tests;

public sealed class ParityApiTests
{
    private sealed class TestMachine : Instance
    {
        public string Marker => "machine";
        public string Echo(string value) => value;
        public string Join(string prefix, params string[] rest) => prefix + ":" + string.Join(",", rest);
        public Exception? LastError { get; private set; }
        public override void OnRuntimeError(Exception error) => LastError = error;
    }

    [Fact]
    public void MakeKindAndIsKindSupportCanonicalKindInheritance()
    {
        var baseKind = Hsm.MakeKind();
        var secondBase = Hsm.MakeKind();
        var derived = Hsm.MakeKind(baseKind, secondBase);
        var leaf = Hsm.MakeKind(derived);

        Assert.True(Hsm.IsKind(derived, baseKind));
        Assert.True(Hsm.IsKind(derived, secondBase));
        Assert.True(Hsm.IsKind(leaf, derived));
        Assert.True(Hsm.IsKind(leaf, baseKind));
        Assert.False(Hsm.IsKind(baseKind, derived));

        Assert.True(Hsm.IsKind(Kind.FinalState, Kind.State));
        Assert.True(Hsm.IsKind(Kind.FinalState, Kind.Vertex));
        Assert.True(Hsm.IsKind(Kind.FinalState, Kind.Element));
        Assert.True(Hsm.IsKind(Kind.ErrorEvent, Kind.CompletionEvent));
        Assert.True(Hsm.IsKind(Kind.ErrorEvent, Kind.Event));
        Assert.False(Hsm.IsKind(Kind.State, Kind.FinalState));
    }

    [Fact]
    public void ConfigIDAliasMatchesExistingIdProperty()
    {
        var idConfig = new Config { Id = "existing-id" };
        var aliasConfig = new Config { ID = "alias-id" };

        Assert.Equal("existing-id", idConfig.Id);
        Assert.Equal("existing-id", idConfig.ID);
        Assert.Equal("alias-id", aliasConfig.Id);
        Assert.Equal("alias-id", aliasConfig.ID);

        var model = Hsm.Define(
            "ConfigIDAlias",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle"));

        var machine = Hsm.Start(new Context(), new TestMachine(), model, aliasConfig);

        Assert.Equal("alias-id", Hsm.ID(machine));
    }

    [Fact]
    public async Task MakeGroupCreatesCanonicalGroupsAndSupportsExplicitID()
    {
        var model = Hsm.Define(
            "MakeGroupParity",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle", Hsm.Transition(Hsm.On("go"), Hsm.Target("../done"))),
            Hsm.State("done"));

        var context = new Context();
        var alpha = Hsm.Start(context, new TestMachine(), model, new Config { Id = "alpha" });
        var bravo = Hsm.Start(context, new TestMachine(), model, new Config { Id = "bravo" });

        var nested = Hsm.MakeGroup(Hsm.MakeGroup(alpha), bravo);
        Assert.Equal(new[] { alpha, bravo }, nested.Instances);
        Assert.StartsWith("group_", Hsm.ID(Hsm.MakeGroup(alpha)));

        var identified = Hsm.MakeGroup("fleet", nested);
        Assert.Equal("fleet", Hsm.ID(identified));
        Assert.Equal("fleet", Hsm.TakeSnapshot(context, identified).ID);

        await identified.Dispatch(new Event("go"));
        Assert.Equal("/MakeGroupParity/done", alpha.State);
        Assert.Equal("/MakeGroupParity/done", bravo.State);

        Assert.Throws<ValidationException>(() => Hsm.MakeGroup("", alpha));
    }

    [Fact]
    public async Task GroupDispatchFromBehaviorDefersSiblingFanoutUntilProducerIsIdle()
    {
        Group? group = null;
        var trace = new List<string>();
        var model = Hsm.Define(
            "BehaviorGroupDispatch",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Transition(
                    Hsm.On("send"),
                    Hsm.Target("../sent"),
                    Hsm.Effect<TestMachine>((_, _, _) => group!.Dispatch(new Event("fanout")).GetAwaiter().GetResult())),
                Hsm.Transition(
                    Hsm.On("fanout"),
                    Hsm.Target("../done"),
                    Hsm.Effect<TestMachine>((_, instance, _) => trace.Add(Hsm.ID(instance))))),
            Hsm.State(
                "sent",
                Hsm.Transition(
                    Hsm.On("fanout"),
                    Hsm.Target("../done"),
                    Hsm.Effect<TestMachine>((_, instance, _) => trace.Add(Hsm.ID(instance))))),
            Hsm.State("done"));
        var context = new Context();
        var alpha = Hsm.Start(context, new TestMachine(), model, new Config { Id = "alpha" });
        var bravo = Hsm.Start(context, new TestMachine(), model, new Config { Id = "bravo" });
        group = Hsm.MakeGroup("fleet", alpha, bravo);

        await alpha.Dispatch(new Event("send"));
        await Hsm.AfterIdle(context);

        Assert.Equal("/BehaviorGroupDispatch/done", alpha.State);
        Assert.Equal("/BehaviorGroupDispatch/done", bravo.State);
        Assert.Equal(new[] { "alpha", "bravo" }, trace);
    }

    [Fact]
    public async Task DispatchFallbackAndTargetedDispatchUseContextAndIdPatterns()
    {
        var model = Hsm.Define(
            "DispatchParity",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle", Hsm.Transition(Hsm.On("go"), Hsm.Target("../done"))),
            Hsm.State("done"));

        var singleContext = new Context();
        var primary = Hsm.Start(singleContext, new TestMachine(), model, new Config { Id = "primary" });

        await Hsm.Dispatch(singleContext, null, new Event("go"));
        Assert.Equal("/DispatchParity/done", primary.State);

        var sharedContext = new Context();
        var alpha = Hsm.Start(sharedContext, new TestMachine(), model, new Config { Id = "alpha" });
        var bravo = Hsm.Start(sharedContext, new TestMachine(), model, new Config { Id = "bravo" });
        var charlie = Hsm.Start(sharedContext, new TestMachine(), model, new Config { Id = "charlie" });

        await Hsm.DispatchTo(sharedContext, new Event("go"), "a*", "*lie");

        Assert.Equal("/DispatchParity/done", alpha.State);
        Assert.Equal("/DispatchParity/idle", bravo.State);
        Assert.Equal("/DispatchParity/done", charlie.State);

        Assert.True(Hsm.Match("alpha", "a*"));
        Assert.True(Hsm.Match("charlie", "*lie"));
        Assert.True(Hsm.Match("/DispatchParity/idle", "*/idle"));
        Assert.False(Hsm.Match("bravo", "a*", "*lie"));
    }

    [Fact]
    public async Task NativeSubmachineAndRedefineReplayModelsUnderTheirOwningRoot()
    {
        var child = Hsm.Define(
            "ReusableChild",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle", Hsm.Transition(Hsm.On("finish"), Hsm.Target("../done"))),
            Hsm.State("done"));
        var host = Hsm.Define(
            "ReplayHost",
            Hsm.Initial(Hsm.Target("drive")),
            Hsm.Submachine("drive", child));
        var derived = Hsm.Redefine(
            "DerivedReplayHost",
            host,
            Hsm.State("complete"));

        var machine = Hsm.Start(new Context(), new TestMachine(), derived);
        await machine.Dispatch(new Event("finish"));

        Assert.Equal("/DerivedReplayHost/drive/done", machine.State);
        Assert.Equal("/ReusableChild/idle", Hsm.Start(new Context(), new TestMachine(), child).State);
    }

    [Fact]
    public async Task NativeConnectionPointsRouteThroughSubmachineBoundaries()
    {
        var trace = new List<string>();
        var child = Hsm.Define(
            "ConnectionChild",
            Hsm.EntryPoint(
                "warm",
                "running",
                Hsm.Effect<TestMachine>((_, _, _) => trace.Add("entry-point"))),
            Hsm.ExitPoint(
                "done",
                Hsm.Effect<TestMachine>((_, _, _) => trace.Add("exit-point"))),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle"),
            Hsm.State(
                "running",
                Hsm.Entry<TestMachine>((_, _, _) => trace.Add("running-entry")),
                Hsm.Transition(Hsm.On("finish"), Hsm.ToExitPoint("done"))));
        var host = Hsm.Define(
            "ConnectionHost",
            Hsm.Initial(Hsm.Target("outside")),
            Hsm.State(
                "outside",
                Hsm.Transition(
                    Hsm.On("enter"),
                    Hsm.Target("../drive"),
                    Hsm.ToEntryPoint("warm"))),
            Hsm.Submachine(
                "drive",
                child,
                Hsm.Transition(
                    Hsm.OnExitPoint("done"),
                    Hsm.Target("../complete"))),
            Hsm.State("complete"));

        var machine = Hsm.Start(new Context(), new TestMachine(), host);
        await machine.Dispatch(new Event("enter"));
        Assert.Equal("/ConnectionHost/drive/running", machine.State);
        Assert.Equal(new[] { "entry-point", "running-entry" }, trace);

        await machine.Dispatch(new Event("finish"));
        Assert.Equal("/ConnectionHost/complete", machine.State);
        Assert.Equal(new[] { "entry-point", "running-entry", "exit-point" }, trace);
    }

    [Fact]
    public async Task NativeUnhandledExitPointsBecomeRuntimeErrors()
    {
        var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var child = Hsm.Define(
            "UnhandledConnectionChild",
            Hsm.ExitPoint("done"),
            Hsm.Initial(Hsm.Target("running")),
            Hsm.State("running", Hsm.Transition(Hsm.On("finish"), Hsm.ToExitPoint("done"))));
        var host = Hsm.Define(
            "UnhandledConnectionHost",
            Hsm.Initial(Hsm.Target("drive")),
            Hsm.Submachine(
                "drive",
                child,
                Hsm.Transition(
                    Hsm.On("hsm/error"),
                    Hsm.Effect<TestMachine>((_, _, evt) => observed.TrySetResult(Assert.IsAssignableFrom<Exception>(evt.Data))))));

        var machine = Hsm.Start(new Context(), new TestMachine(), host);
        await machine.Dispatch(new Event("finish"));

        var error = await observed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.IsType<UnhandledExitPointException>(error);
    }

    [Fact]
    public async Task AncestorExitPointHandlersAreClonedForEveryMatchingSubmachine()
    {
        var child = Hsm.Define(
            "ReusableExitChild",
            Hsm.ExitPoint("done"),
            Hsm.Initial(Hsm.Target("active")),
            Hsm.State("active", Hsm.Transition(Hsm.On("finish"), Hsm.ToExitPoint("done"))));
        var host = Hsm.Define(
            "RepeatedExitHost",
            Hsm.Initial(Hsm.Target("left")),
            Hsm.Submachine("left", child),
            Hsm.Submachine("right", child),
            Hsm.State("outside"),
            Hsm.Transition(Hsm.OnExitPoint("done"), Hsm.Target("outside")));
        var machine = Hsm.Start(new Context(), new TestMachine(), host);

        await machine.Dispatch(new Event("finish"));

        Assert.Equal("/RepeatedExitHost/outside", machine.State);
    }

    [Fact]
    public async Task CanonicalCompositionApisFlattenMetadataAndRouteConnectionPoints()
    {
        var child = Hsm.DefineSubmachine(
            "CanonicalChild",
            Hsm.Attribute("child_count", 7),
            Hsm.Operation("child_ping", new Func<string>(() => "child")),
            Hsm.EntryPoint("warm", Hsm.Target("active")),
            Hsm.ExitPoint("done"),
            Hsm.Initial(Hsm.Target("cold")),
            Hsm.State("cold"),
            Hsm.State(
                "active",
                Hsm.Transition(Hsm.On("finish"), Hsm.ExitPoint("done"))));
        var host = Hsm.Define(
            "CanonicalHost",
            Hsm.Initial(Hsm.Target("outside")),
            Hsm.State(
                "outside",
                Hsm.Transition(Hsm.On("enter"), Hsm.Target("../drive"), Hsm.EntryPoint("warm"))),
            Hsm.SubmachineState(
                "drive",
                child,
                Hsm.Transition(Hsm.ExitPoint("done"), Hsm.Target("../complete"))),
            Hsm.State("complete"));
        var context = new Context();
        var machine = Hsm.Start(context, new TestMachine(), host);

        Assert.Equal("child", Hsm.Call(context, machine, "child_ping"));
        Assert.Equal(7, Hsm.TakeSnapshot(context, machine).Attributes["/CanonicalHost/child_count"]);

        await machine.Dispatch(new Event("enter"));
        Assert.Equal("/CanonicalHost/drive/active", machine.State);
        await machine.Dispatch(new Event("finish"));
        Assert.Equal("/CanonicalHost/complete", machine.State);
    }

    [Fact]
    public void CanonicalRedefineAllowsOneLaterMetadataOverride()
    {
        var source = Hsm.Define(
            "MetadataBase",
            Hsm.Attribute("count", 1),
            Hsm.Operation("label", new Func<string>(() => "base")),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle"));
        var derived = Hsm.Redefine(
            source,
            "MetadataDerived",
            Hsm.Attribute("count", 2),
            Hsm.Operation("label", new Func<string>(() => "derived")));
        var context = new Context();
        var machine = Hsm.Start(context, new TestMachine(), derived);

        Assert.Equal(2, Hsm.Get<int>(context, machine, "count"));
        Assert.Equal("derived", Hsm.Call(context, machine, "label"));
        Assert.Throws<ValidationException>(() => Hsm.Redefine(
            source,
            "BadMetadataDerived",
            Hsm.Attribute("count", 2),
            Hsm.Attribute("count", 3)));
    }

    [Fact]
    public async Task DispatchCompletesWhenCurrentStateDefersEvent()
    {
        var model = Hsm.Define(
            "DeferredDispatchCompletionParity",
            Hsm.Initial(Hsm.Target("holding")),
            Hsm.State(
                "holding",
                Hsm.Defer("wait"),
                Hsm.Transition(Hsm.On("release"), Hsm.Target("../ready"))),
            Hsm.State(
                "ready",
                Hsm.Transition(Hsm.On("wait"), Hsm.Target("../done"))),
            Hsm.State("done"));

        var context = new Context();
        var machine = Hsm.Start(context, new TestMachine(), model);

        await Hsm.Dispatch(context, machine, new Event("wait"));
        Assert.Equal("/DeferredDispatchCompletionParity/holding", machine.State);

        await Hsm.Dispatch(context, machine, new Event("release"));
        Assert.Equal("/DeferredDispatchCompletionParity/done", machine.State);
    }

    [Fact]
    public void SnapshotAndEventHelpersExposeParitySurface()
    {
        var @event = new Event("go", Kind.CallEvent, source: "/source", target: "/target", schema: typeof(string));
        var withData = @event.WithData(123);
        var withDataAndId = @event.WithDataAndID("payload", "evt-1");

        Assert.Equal("go", withData.Name);
        Assert.Equal(Kind.CallEvent, withData.Kind);
        Assert.Equal(123, withData.Data);
        Assert.Equal("/source", withData.Source);
        Assert.Equal("/target", withData.Target);
        Assert.Equal(typeof(string), withData.Schema);

        Assert.Equal("go", withDataAndId.Name);
        Assert.Equal("evt-1", withDataAndId.ID);
        Assert.Equal("payload", withDataAndId.Data);
        Assert.Null(withDataAndId.Source);
        Assert.Null(withDataAndId.Target);

        var model = Hsm.Define(
            "SnapshotParity",
            Hsm.Attribute("declared", 7),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle", Hsm.Transition(Hsm.On("go"), Hsm.Target("../done"))),
            Hsm.State("done"));

        var context = new Context();
        var machine = Hsm.Start(context, new TestMachine(), model, new Config { Id = "snapshot" });
        var snapshot = Hsm.TakeSnapshot(context, machine);

        Assert.Equal("snapshot", snapshot.ID);
        Assert.Equal("/SnapshotParity", snapshot.QualifiedName);
        Assert.Equal("/SnapshotParity/idle", snapshot.State);
        Assert.Equal(0, snapshot.QueueLen);
        Assert.Equal(7, snapshot.Attributes["/SnapshotParity/declared"]);
        var available = Assert.Single(snapshot.Events);
        Assert.Equal("go", available.Name);
        Assert.Equal(Kind.Event, available.Kind);
        Assert.Equal("/SnapshotParity/done", available.Target);
        Assert.False(available.Guard);
    }

    [Fact]
    public async Task OnSetSupportsImplicitAttributes()
    {
        var model = Hsm.Define(
            "ImplicitAttributeParity",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Transition(Hsm.OnSet("config_value"), Hsm.Target("../done"))),
            Hsm.State("done"));

        var context = new Context();
        var machine = Hsm.Start(context, new TestMachine(), model);

        await Hsm.Set(context, machine, "config_value", 42);

        Assert.Equal("/ImplicitAttributeParity/done", machine.State);
        Assert.Equal(42, Hsm.Get<int>(context, machine, "config_value"));
    }

    [Fact]
    public async Task NamedOperationBehaviorsInvokeDirectlyWithoutOnCallEvents()
    {
        var trace = new List<string>();
        var workEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = Hsm.Define(
            "NamedOperationBehaviors",
            Hsm.Operation("enter", new Action<Context, TestMachine, Event>((_, _, evt) => trace.Add("enter:" + evt.Name))),
            Hsm.Operation("leave", new Action<Context, TestMachine, Event>((_, _, evt) => trace.Add("leave:" + evt.Name))),
            Hsm.Operation("work", new Action<Context, TestMachine, Event>((_, _, evt) =>
            {
                trace.Add("work:" + evt.Name);
                workEntered.TrySetResult(true);
            })),
            Hsm.Operation("effect", new Action<Context, TestMachine, Event>((_, _, evt) => trace.Add("effect:" + evt.Name))),
            Hsm.Operation("allowed", new Func<Context, TestMachine, Event, bool>((_, _, evt) =>
            {
                trace.Add("allowed:" + evt.Name);
                return true;
            })),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Entry("enter"),
                Hsm.Activity("work"),
                Hsm.Exit("leave"),
                Hsm.Transition(
                    Hsm.On("go"),
                    Hsm.Guard("allowed"),
                    Hsm.Target("../done"),
                    Hsm.Effect("effect")),
                Hsm.Transition(Hsm.OnCall("effect"), Hsm.Target("../wrong"))),
            Hsm.State("done"),
            Hsm.State("wrong"));

        var machine = Hsm.Start(new Context(), new TestMachine(), model);
        Assert.Null(machine.LastError);
        await workEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await machine.Dispatch(new Event("go"));

        Assert.Equal("/NamedOperationBehaviors/done", machine.State);
        Assert.Equal(
            new[] { "enter:hsm/initial", "work:hsm/initial", "allowed:go", "leave:go", "effect:go" },
            trace);
    }

    [Fact]
    public async Task NamedOperationActivitiesDoNotBlockStart()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var model = Hsm.Define(
            "NamedOperationActivity",
            Hsm.Operation("hold", new Action<Context, TestMachine, Event>((_, _, _) =>
            {
                entered.TrySetResult(true);
                release.Wait();
            })),
            Hsm.Initial(Hsm.Target("active")),
            Hsm.State("active", Hsm.Activity("hold")));

        var start = Task.Run(() => Hsm.Start(new Context(), new TestMachine(), model));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(start.IsCompleted, "Start was blocked by a named operation activity");
        }
        finally
        {
            release.Set();
        }

        var machine = await start;
        await machine.Stop();
    }

    [Fact]
    public async Task WhenStringIsCanonicalOnSetAlias()
    {
        var model = Hsm.Define(
            "WhenAttributeAliasParity",
            Hsm.Attribute("flag", false),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Transition(Hsm.When("flag"), Hsm.Target("../changed"))),
            Hsm.State("changed"));

        var context = new Context();
        var machine = Hsm.Start(context, new TestMachine(), model);

        await Hsm.Set(context, machine, "flag", true);

        Assert.Equal("/WhenAttributeAliasParity/changed", machine.State);
        Assert.True(Hsm.Get<bool>(context, machine, "flag"));
    }

    [Fact]
    public async Task SetCompletesForDeclaredAttributeUpdatesAndUnchangedValues()
    {
        var model = Hsm.Define(
            "SetProcessedParity",
            Hsm.Attribute("count", 0),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Transition(Hsm.OnSet("count"), Hsm.Target("../updated"))),
            Hsm.State("updated"));

        var context = new Context();
        var machine = Hsm.Start(context, new TestMachine(), model);

        await Hsm.Set(context, machine, "count", 0);
        Assert.Equal("/SetProcessedParity/idle", machine.State);

        await Hsm.Set(context, machine, "count", 1);
        Assert.Equal("/SetProcessedParity/updated", machine.State);
        Assert.Equal(1, Hsm.Get<int>(context, machine, "count"));
    }

    [Fact]
    public async Task SetReportsRuntimeErrorsForUnknownAttributesAndExactTypeMismatches()
    {
        var model = Hsm.Define(
            "SetRejectedParity",
            Hsm.Attribute("count", 0),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Transition(Hsm.OnSet("count"), Hsm.Target("../updated"))),
            Hsm.State("updated"));

        var context = new Context();
        var machine = Hsm.Start(context, new TestMachine(), model);

        await Assert.ThrowsAsync<AttributeHsmException>(() => Hsm.Set(context, machine, "missing", 1));
        Assert.Equal("/SetRejectedParity/idle", machine.State);
        Assert.Equal(0, Hsm.Get<int>(context, machine, "count"));

        await Assert.ThrowsAsync<AttributeHsmException>(() => Hsm.Set(context, machine, "count", 1L));
        Assert.Equal("/SetRejectedParity/idle", machine.State);
        Assert.Equal(0, Hsm.Get<int>(context, machine, "count"));
    }

    [Fact]
    public async Task DispatchIsAsynchronousByDefault()
    {
        var enteredEffect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEffect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = Hsm.Define(
            "AsyncDispatchParity",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Transition(
                    Hsm.On("go"),
                    Hsm.Target("../done"),
                    Hsm.Effect<TestMachine>((_, _, _) =>
                    {
                        enteredEffect.TrySetResult(true);
                        releaseEffect.Task.GetAwaiter().GetResult();
                    }))),
            Hsm.State("done"));

        var context = new Context();
        var machine = Hsm.Start(context, new TestMachine(), model);
        var completion = machine.Dispatch(new Event("go"));

        await enteredEffect.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(completion.IsCompleted);
        Assert.Equal("/AsyncDispatchParity/idle", machine.State);

        releaseEffect.TrySetResult(true);
        await completion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("/AsyncDispatchParity/done", machine.State);
    }

    [Fact]
    public void NewBindsAModelWithoutActivatingStateBeforeStart()
    {
        var model = Hsm.Define(
            "NewParity",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle"));

        var machine = Hsm.New(new TestMachine(), model, new Config { Id = "bound" });
        Assert.Equal(string.Empty, machine.State);

        Hsm.Start(new Context(), machine);
        Assert.Equal("/NewParity/idle", machine.State);
        Assert.Equal("bound", Hsm.ID(machine));
    }

    [Fact]
    public void CallSupportsMultipleSignaturesAndParityErrors()
    {
        CallData? observed = null;
        var machine = new TestMachine();
        var model = Hsm.Define(
            "CallParity",
            Hsm.Operation("args", new Func<int, int, int>((left, right) => left + right)),
            Hsm.Operation("context", new Func<Context, string>(ctx => Hsm.ID(Hsm.FromContext(ctx)!))),
            Hsm.Operation("instance", new Func<TestMachine, string>(machine => machine.Marker)),
            Hsm.Operation("method", new Func<string, string>(machine.Echo)),
            Hsm.Operation("params", new Func<string, string[], string>(machine.Join)),
            Hsm.Operation("bad", new Func<CancellationToken, string>(_ => "bad")),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Transition(
                    Hsm.OnCall("method"),
                    Hsm.Effect<TestMachine>((_, _, evt) => observed = Assert.IsType<CallData>(evt.Data)))));

        var context = new Context();
        Hsm.Start(context, machine, model, new Config { Id = "caller" });

        Assert.Equal(5, Hsm.Call(context, machine, "args", 2, 3));
        Assert.Equal("caller", Hsm.Call(context, machine, "context"));
        Assert.Equal("machine", Hsm.Call(context, machine, "instance"));
        Assert.Equal("echo", Hsm.Call(context, machine, "method", "echo"));
        Assert.Equal("root:a,b", Hsm.Call(context, machine, "params", "root", "a", "b"));

        Assert.NotNull(observed);
        Assert.Equal("/CallParity/method", observed!.Name);
        Assert.Equal(new object?[] { "echo" }, observed.Args);

        Assert.Throws<MissingOperationException>(() => Hsm.Call(context, machine, "missing"));
        Assert.Throws<InvalidOperationSignatureException>(() => Hsm.Call(context, machine, "bad"));
    }

    [Fact]
    public void ContractOnlyOperationsResolvePublicInstanceMethods()
    {
        var model = Hsm.Define(
            "InstanceOperationContract",
            Hsm.Operation(nameof(TestMachine.Echo)),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle"));
        var context = new Context();
        var machine = Hsm.Start(context, new TestMachine(), model);

        Assert.Equal("hello", Hsm.Call(context, machine, nameof(TestMachine.Echo), "hello"));
    }
}
