using Xunit;

namespace Stateforward.Hsm.Tests;

public sealed class SnapshotImmutabilityTests
{
    private sealed class TestMachine : Instance
    {
    }

    [Fact]
    public void AttributeSnapshotValuesAreDetachedFromRuntimeState()
    {
        var model = Hsm.Define(
            "SnapshotAttributeImmutability",
            Hsm.Attribute(
                "bag",
                new Dictionary<string, object?>
                {
                    ["items"] = new List<object?> { "runtime" }
                }),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle"));

        var context = new Context();
        var machine = Hsm.Start(context, new TestMachine(), model);

        var snapshot = Hsm.TakeSnapshot(context, machine);
        var snapshotBag = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            snapshot.Attributes["/SnapshotAttributeImmutability/bag"]);
        var snapshotItems = Assert.IsAssignableFrom<IReadOnlyList<object?>>(snapshotBag["items"]);

        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, object?>)snapshotBag).Add("extra", true));
        Assert.Throws<NotSupportedException>(() => ((IList<object?>)snapshotItems)[0] = "snapshot");

        var later = Hsm.TakeSnapshot(context, machine);
        var laterBag = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            later.Attributes["/SnapshotAttributeImmutability/bag"]);
        var laterItems = Assert.IsAssignableFrom<IReadOnlyList<object?>>(laterBag["items"]);

        Assert.Equal("runtime", laterItems[0]);
        Assert.False(laterBag.ContainsKey("extra"));
    }

    [Fact]
    public void EventSnapshotSchemasAreDetachedFromModelDefinitions()
    {
        var schema = new Dictionary<string, object?>
        {
            ["fields"] = new List<object?> { "model" }
        };
        var model = Hsm.Define(
            "SnapshotSchemaImmutability",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Transition(
                    Hsm.On(new Event("go", schema: schema)),
                    Hsm.Target("../done"))),
            Hsm.State("done"));

        var context = new Context();
        var machine = Hsm.Start(context, new TestMachine(), model);

        var snapshot = Hsm.TakeSnapshot(context, machine);
        var snapshotSchema = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            Assert.Single(snapshot.Events).Schema);
        var snapshotFields = Assert.IsAssignableFrom<IReadOnlyList<object?>>(snapshotSchema["fields"]);

        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, object?>)snapshotSchema).Add("extra", true));
        Assert.Throws<NotSupportedException>(() => ((IList<object?>)snapshotFields)[0] = "snapshot");

        var later = Hsm.TakeSnapshot(context, machine);
        var laterSchema = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            Assert.Single(later.Events).Schema);
        var laterFields = Assert.IsAssignableFrom<IReadOnlyList<object?>>(laterSchema["fields"]);

        Assert.Equal("model", laterFields[0]);
        Assert.False(laterSchema.ContainsKey("extra"));
        Assert.Equal("model", Assert.IsType<List<object?>>(schema["fields"])[0]);
        Assert.False(schema.ContainsKey("extra"));
    }

    [Fact]
    public void GetReturnsDetachedMutableAttributeCopies()
    {
        var model = Hsm.Define(
            "GetAttributeImmutability",
            Hsm.Attribute(
                "bag",
                new Dictionary<string, object?>
                {
                    ["items"] = new List<object?> { "runtime" }
                }),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle"));

        var context = new Context();
        var machine = Hsm.Start(context, new TestMachine(), model);

        var first = Assert.IsType<Dictionary<string, object?>>(
            Hsm.Get<Dictionary<string, object?>>(context, machine, "bag"));
        var firstItems = Assert.IsType<List<object?>>(first["items"]);

        first["extra"] = true;
        firstItems[0] = "get";

        var second = Assert.IsType<Dictionary<string, object?>>(
            Hsm.Get<Dictionary<string, object?>>(context, machine, "bag"));
        var secondItems = Assert.IsType<List<object?>>(second["items"]);

        Assert.Equal("runtime", secondItems[0]);
        Assert.False(second.ContainsKey("extra"));
    }
}
