using Xunit;

namespace Stateforward.Hsm.Tests;

public sealed class EventOwnershipTests
{
    private sealed class TestMachine : Instance
    {
    }

    private sealed class Payload
    {
        public List<string> SeenBy { get; } = new();
    }

    private sealed record Observation(
        string InstanceId,
        string Name,
        string? QualifiedName,
        string Source,
        string Target,
        string ID,
        Kind Kind,
        string SchemaOwner);

    [Fact]
    public async Task BehaviorCoreFieldMutationsAreIsolatedWhileApplicationMetadataAndDataRemainShared()
    {
        var payload = new Payload();
        var schema = new Dictionary<string, object?> { ["owner"] = "caller" };
        var observed = false;
        var model = Hsm.Define(
            "EventCallerOwnership",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Transition(
                    Hsm.On("go"),
                    Hsm.Target("../done"),
                    Hsm.Effect<TestMachine>((ctx, instance, evt) =>
                    {
                        observed = true;
                        evt.Name = "mutated-name";
                        evt.QualifiedName = "/mutated/qualified";
                        evt.Source = "/mutated/source";
                        evt.Target = "/mutated/target";
                        evt.ID = "mutated-id";
                        evt.Kind = Kind.ErrorEvent;
                        ((Dictionary<string, object?>)evt.Schema!)["owner"] = Hsm.ID(instance);
                        ((Payload)evt.Data!).SeenBy.Add(Hsm.ID(instance));
                    }))),
            Hsm.State("done"));

        var context = new Context();
        var machine = Hsm.Start(context, new TestMachine(), model, new Config { Id = "alpha" });
        var callerEvent = new Event(
            "go",
            Kind.CallEvent,
            payload,
            source: "/caller/source",
            id: "caller-id",
            target: "/caller/target",
            schema: schema,
            qualifiedName: "/caller/qualified");

        await machine.Dispatch(callerEvent);

        Assert.True(observed);
        Assert.Equal("/EventCallerOwnership/done", machine.State);
        Assert.Equal("go", callerEvent.Name);
        Assert.Equal("/caller/qualified", callerEvent.QualifiedName);
        Assert.Equal("/caller/source", callerEvent.Source);
        Assert.Equal("/caller/target", callerEvent.Target);
        Assert.Equal("caller-id", callerEvent.ID);
        Assert.Equal(Kind.CallEvent, callerEvent.Kind);
        Assert.Equal("alpha", schema["owner"]);
        Assert.Equal(new[] { "alpha" }, payload.SeenBy);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("to")]
    [InlineData("group")]
    public async Task BroadcastDispatchIsolatesCoreFieldsAndSharesApplicationMetadataAndData(string mode)
    {
        var observations = new List<Observation>();
        var gate = new object();
        var payload = new Payload();
        var model = Hsm.Define(
            "EventSiblingOwnership",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Transition(
                    Hsm.On("go"),
                    Hsm.Target("../done"),
                    Hsm.Effect<TestMachine>((ctx, instance, evt) =>
                    {
                        var instanceId = Hsm.ID(instance);
                        var schema = (Dictionary<string, object?>)evt.Schema!;
                        lock (gate)
                        {
                            observations.Add(new Observation(
                                instanceId,
                                evt.Name,
                                evt.QualifiedName,
                                evt.Source!,
                                evt.Target!,
                                evt.ID,
                                evt.Kind,
                                (string)schema["owner"]!));
                        }

                        evt.Name = $"mutated-name-{instanceId}";
                        evt.QualifiedName = $"/mutated/qualified/{instanceId}";
                        evt.Source = $"/mutated/source/{instanceId}";
                        evt.Target = $"/mutated/target/{instanceId}";
                        evt.ID = $"mutated-id-{instanceId}";
                        evt.Kind = Kind.ErrorEvent;
                        schema["owner"] = instanceId;
                        lock (gate)
                        {
                            payload.SeenBy.Add(instanceId);
                        }
                    }))),
            Hsm.State("done"));

        var context = new Context();
        var alpha = Hsm.Start(context, new TestMachine(), model, new Config { Id = "alpha" });
        var bravo = Hsm.Start(context, new TestMachine(), model, new Config { Id = "bravo" });
        var callerSchema = new Dictionary<string, object?> { ["owner"] = "caller" };
        var callerEvent = new Event(
            "go",
            Kind.CallEvent,
            payload,
            source: "/caller/source",
            id: "caller-id",
            target: "/caller/target",
            schema: callerSchema,
            qualifiedName: "/caller/qualified");

        if (mode == "all")
        {
            await Hsm.DispatchAll(context, callerEvent);
        }
        else if (mode == "to")
        {
            await Hsm.DispatchTo(context, callerEvent, "alpha", "bravo");
        }
        else
        {
            await new Group(alpha, bravo).Dispatch(callerEvent);
        }

        Assert.Equal(new[] { "alpha", "bravo" }, observations.Select(item => item.InstanceId).OrderBy(id => id));
        foreach (var observation in observations)
        {
            Assert.Equal("go", observation.Name);
            Assert.Equal("/caller/qualified", observation.QualifiedName);
            Assert.Equal("/caller/source", observation.Source);
            Assert.Equal("/caller/target", observation.Target);
            Assert.Equal("caller-id", observation.ID);
            Assert.Equal(Kind.CallEvent, observation.Kind);
        }
        Assert.Equal("caller", observations.Single(item => item.InstanceId == "alpha").SchemaOwner);
        Assert.Equal("alpha", observations.Single(item => item.InstanceId == "bravo").SchemaOwner);

        Assert.Equal("go", callerEvent.Name);
        Assert.Equal("/caller/qualified", callerEvent.QualifiedName);
        Assert.Equal("/caller/source", callerEvent.Source);
        Assert.Equal("/caller/target", callerEvent.Target);
        Assert.Equal("caller-id", callerEvent.ID);
        Assert.Equal(Kind.CallEvent, callerEvent.Kind);
        Assert.Equal("bravo", callerSchema["owner"]);
        Assert.Equal(new[] { "alpha", "bravo" }, payload.SeenBy.OrderBy(id => id));
    }
}
