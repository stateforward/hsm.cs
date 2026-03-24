using Xunit;

namespace Stateforward.Hsm.Tests;

public sealed class ParityApiTests
{
    private sealed class TestMachine : Instance
    {
        public string Marker => "machine";
        public string Echo(string value) => value;
        public string Join(string prefix, params string[] rest) => prefix + ":" + string.Join(",", rest);
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
    public async Task OnSetSupportsImplicitAttributesAndSlashedNames()
    {
        var model = Hsm.Define(
            "ImplicitAttributeParity",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Transition(Hsm.OnSet("config/value"), Hsm.Target("../done"))),
            Hsm.State("done"));

        var context = new Context();
        var machine = Hsm.Start(context, new TestMachine(), model);

        await Hsm.Set(context, machine, "config/value", 42);

        Assert.Equal("/ImplicitAttributeParity/done", machine.State);
        Assert.Equal(42, Hsm.Get<int>(context, machine, "config/value"));
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
    public void NewBindsAModelBeforeStart()
    {
        var model = Hsm.Define(
            "NewParity",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle"));

        var machine = Hsm.New(new TestMachine(), model, new Config { Id = "bound" });
        Assert.Equal("/NewParity", machine.State);

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
            Hsm.Operation("ops/args", new Func<int, int, int>((left, right) => left + right)),
            Hsm.Operation("ops/context", new Func<Context, string>(ctx => Hsm.ID(Hsm.FromContext(ctx)!))),
            Hsm.Operation("ops/instance", new Func<TestMachine, string>(machine => machine.Marker)),
            Hsm.Operation("ops/method", new Func<string, string>(machine.Echo)),
            Hsm.Operation("ops/params", new Func<string, string[], string>(machine.Join)),
            Hsm.Operation("ops/bad", new Func<CancellationToken, string>(_ => "bad")),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Transition(
                    Hsm.OnCall("ops/method"),
                    Hsm.Effect<TestMachine>((_, _, evt) => observed = Assert.IsType<CallData>(evt.Data)))));

        var context = new Context();
        Hsm.Start(context, machine, model, new Config { Id = "caller" });

        Assert.Equal(5, Hsm.Call(context, machine, "ops/args", 2, 3));
        Assert.Equal("caller", Hsm.Call(context, machine, "ops/context"));
        Assert.Equal("machine", Hsm.Call(context, machine, "ops/instance"));
        Assert.Equal("echo", Hsm.Call(context, machine, "ops/method", "echo"));
        Assert.Equal("root:a,b", Hsm.Call(context, machine, "ops/params", "root", "a", "b"));

        Assert.NotNull(observed);
        Assert.Equal("/CallParity/ops/method", observed!.Name);
        Assert.Equal(new object?[] { "echo" }, observed.Args);

        Assert.Throws<MissingOperationException>(() => Hsm.Call(context, machine, "ops/missing"));
        Assert.Throws<InvalidOperationSignatureException>(() => Hsm.Call(context, machine, "ops/bad"));
    }
}
