using Xunit;

namespace Stateforward.Hsm.Tests;

public sealed class ModelConformanceTests
{
    private sealed class TestMachine : Instance
    {
    }

    [Fact]
    public void TopLevelInitialIsRequired()
    {
        var error = Assert.Throws<ValidationException>(() => Hsm.Define(
            "BadTopLevelInitial",
            Hsm.State("idle")));

        Assert.Contains("initial state is required", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TopLevelEntryAndExitAreRejected()
    {
        var entryError = Assert.Throws<ValidationException>(() => Hsm.Define(
            "BadTopLevelEntry",
            Hsm.Entry<TestMachine>((_, _, _) => { }),
            Hsm.State("idle"),
            Hsm.Initial(Hsm.Target("idle"))));

        var exitError = Assert.Throws<ValidationException>(() => Hsm.Define(
            "BadTopLevelExit",
            Hsm.Exit<TestMachine>((_, _, _) => { }),
            Hsm.State("idle"),
            Hsm.Initial(Hsm.Target("idle"))));

        Assert.Contains("top level state machine", entryError.Message, StringComparison.Ordinal);
        Assert.Contains("top level state machine", exitError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InitialRejectsGuardAndMultipleTransitionsAndNonNestedTarget()
    {
        var guardError = Assert.Throws<ValidationException>(() => Hsm.Define(
            "BadInitialGuard",
            Hsm.State("idle"),
            Hsm.Initial(
                Hsm.Target("idle"),
                Hsm.Guard<TestMachine>((_, _, _) => true))));

        var multipleError = Assert.Throws<ValidationException>(() => Hsm.Define(
            "BadInitialMultiple",
            Hsm.State("idle"),
            Hsm.State("done"),
            Hsm.Initial(
                Hsm.Target("idle"),
                Hsm.Transition(Hsm.Target("done")))));

        var nestedError = Assert.Throws<ValidationException>(() => Hsm.Define(
            "BadNestedInitial",
            Hsm.State(
                "parent",
                Hsm.State("child"),
                Hsm.Initial(Hsm.Target("/BadNestedInitial/outside"))),
            Hsm.State("outside"),
            Hsm.Initial(Hsm.Target("parent"))));

        Assert.Contains("cannot have a guard", guardError.Message, StringComparison.Ordinal);
        Assert.Contains("cannot have multiple transitions", multipleError.Message, StringComparison.Ordinal);
        Assert.Contains("must target a nested state", nestedError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChoiceRequiresOutgoingTransitionsAndGuardlessFinalBranch()
    {
        var missingError = Assert.Throws<ValidationException>(() => Hsm.Define(
            "BadChoice",
            Hsm.State("idle", Hsm.Choice("branch")),
            Hsm.Initial(Hsm.Target("idle"))));

        var lastGuardError = Assert.Throws<ValidationException>(() => Hsm.Define(
            "BadChoiceDefault",
            Hsm.State(
                "idle",
                Hsm.Choice(
                    "branch",
                    Hsm.Transition(Hsm.Target("../left")),
                    Hsm.Transition(Hsm.Target("../right"), Hsm.Guard<TestMachine>((_, _, _) => true)))),
            Hsm.State("left"),
            Hsm.State("right"),
            Hsm.Initial(Hsm.Target("idle"))));

        Assert.Contains("at least one transition", missingError.Message, StringComparison.Ordinal);
        Assert.Contains("last transition of choice state", lastGuardError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingSourceAndTargetAreRejected()
    {
        var missingSource = Assert.Throws<ValidationException>(() => Hsm.Define(
            "MissingSource",
            Hsm.State("idle"),
            Hsm.State("done"),
            Hsm.Transition(
                Hsm.On("advance"),
                Hsm.Source("missing"),
                Hsm.Target("done")),
            Hsm.Initial(Hsm.Target("idle"))));

        var missingTarget = Assert.Throws<ValidationException>(() => Hsm.Define(
            "MissingTarget",
            Hsm.State("idle"),
            Hsm.Transition(
                Hsm.On("advance"),
                Hsm.Source("idle"),
                Hsm.Target("missing")),
            Hsm.Initial(Hsm.Target("idle"))));

        Assert.Contains("missing source", missingSource.Message, StringComparison.Ordinal);
        Assert.Contains("missing target", missingTarget.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TopLevelTargetRoutesFromTheActiveStateAndInternalRequiresEffect()
    {
        var rootModel = Hsm.Define(
            "RootTransition",
            Hsm.State("idle"),
            Hsm.State("done"),
            Hsm.Transition(Hsm.On("go"), Hsm.Target("done")),
            Hsm.Initial(Hsm.Target("idle")));
        var machine = Hsm.Start(new Context(), new TestMachine(), rootModel);

        await machine.Dispatch(new Event("go"));

        var internalTransition = Assert.Throws<ValidationException>(() => Hsm.Define(
            "BadInternal",
            Hsm.State(
                "idle",
                Hsm.Transition(Hsm.On("go"))),
            Hsm.Initial(Hsm.Target("idle"))));

        Assert.Equal("/RootTransition/done", machine.State);
        Assert.Contains("internal transitions require an effect", internalTransition.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateAttributesAndOperationsAndOnCallAndTimeSourcesAreValidated()
    {
        var duplicateAttribute = Assert.Throws<ValidationException>(() => Hsm.Define(
            "DupAttr",
            Hsm.Attribute("value", 1),
            Hsm.Attribute("value", 2),
            Hsm.State("idle"),
            Hsm.Initial(Hsm.Target("idle"))));

        var duplicateOperation = Assert.Throws<ValidationException>(() => Hsm.Define(
            "DupOp",
            Hsm.Operation("call", new Action(() => { })),
            Hsm.Operation("call", new Action(() => { })),
            Hsm.State("idle"),
            Hsm.Initial(Hsm.Target("idle"))));

        var missingOperation = Assert.Throws<ValidationException>(() => Hsm.Define(
            "MissingCall",
            Hsm.State(
                "idle",
                Hsm.Transition(Hsm.OnCall("missing"), Hsm.Target("../done"))),
            Hsm.State("done"),
            Hsm.Initial(Hsm.Target("idle"))));

        var badTemporalSource = Assert.Throws<ValidationException>(() => Hsm.Define(
            "BadTemporalSource",
            Hsm.State(
                "parent",
                Hsm.State("idle"),
                Hsm.Choice(
                    "branch",
                    Hsm.Transition(
                        Hsm.After<TestMachine>((_, _, _) => TimeSpan.FromSeconds(1)),
                        Hsm.Target("idle")),
                    Hsm.Transition(Hsm.Target("idle")))),
            Hsm.Initial(Hsm.Target("parent"))));

        Assert.Contains("already defined", duplicateAttribute.Message, StringComparison.Ordinal);
        Assert.Contains("already defined", duplicateOperation.Message, StringComparison.Ordinal);
        Assert.Contains("missing operation", missingOperation.Message, StringComparison.Ordinal);
        Assert.Contains("real state source", badTemporalSource.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompositeHistoryAttributeAndTemporalSourcesAreValidated()
    {
        Assert.Throws<ValidationException>(() => Hsm.Define(
            "MissingCompositeInitial",
            Hsm.Initial(Hsm.Target("container")),
            Hsm.State("container", Hsm.State("child"))));
        Assert.Throws<ValidationException>(() => Hsm.Define(
            "MissingHistoryDefault",
            Hsm.Initial(Hsm.Target("container/child")),
            Hsm.State(
                "container",
                Hsm.State("child"),
                Hsm.ShallowHistory("history"))));
        Assert.Throws<ValidationException>(() => Hsm.Define(
            "BadAttributeDefault",
            Hsm.Attribute("count", "wrong", typeof(int)),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle")));
        Assert.Throws<ValidationException>(() => Hsm.Define(
            "MissingTemporalAttribute",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle", Hsm.Transition(Hsm.After("delay")))));
        Assert.Throws<ValidationException>(() => Hsm.Define(
            "BadTemporalAttributeType",
            Hsm.Attribute("delay", 1),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle", Hsm.Transition(Hsm.After("delay")))));

        var directHistory = Hsm.Define(
            "DirectHistoryDefault",
            Hsm.Initial(Hsm.Target("container/child")),
            Hsm.State(
                "container",
                Hsm.State("child"),
                Hsm.ShallowHistory("history", Hsm.Target("child"))));
        Assert.NotNull(directHistory.Resolve("/DirectHistoryDefault/container/history"));

        var numericDefault = Hsm.Define(
            "NumericDefaultCompatibility",
            Hsm.Attribute("count", 1L, typeof(double)),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle"));
        Assert.NotNull(numericDefault);
    }

    [Fact]
    public void MetadataNamespaceAndSubmachineBoundariesAreValidated()
    {
        Assert.Throws<ValidationException>(() => Hsm.OnSet("bad/name"));
        Assert.Throws<ValidationException>(() => Hsm.OnCall("bad/name"));
        Assert.Throws<ValidationException>(() => Hsm.Entry("bad/name"));
        Assert.Throws<ValidationException>(() => Hsm.Guard("bad/name"));
        Assert.Throws<ValidationException>(() => Hsm.Define(
            "MissingBehaviorOperation",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle", Hsm.Entry("missing"))));
        Assert.Throws<ValidationException>(() => Hsm.Define(
            "SlashedAttribute",
            Hsm.Attribute("bad/name", 1),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle")));
        Assert.Throws<ValidationException>(() => Hsm.Define(
            "SlashedOperation",
            Hsm.Operation("bad/name"),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle")));
        Assert.Throws<ValidationException>(() => Hsm.Define(
            "MetadataCollision",
            Hsm.Attribute("shared", 1),
            Hsm.Operation("shared"),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle")));
        Assert.Throws<ValidationException>(() => Hsm.Define(
            "ImplicitAttributeCollision",
            Hsm.Operation("shared"),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle", Hsm.Transition(Hsm.OnSet("shared")))));

        var child = Hsm.Define(
            "BoundaryChild",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle"));
        Assert.Throws<ValidationException>(() => Hsm.Define(
            "InvalidBoundary",
            Hsm.Initial(Hsm.Target("drive")),
            Hsm.SubmachineState("drive", child, Hsm.State("nested"))));
        Assert.Throws<ValidationException>(() => Hsm.Define(
            "InvalidBoundaryTarget",
            Hsm.Initial(Hsm.Target("outside")),
            Hsm.State(
                "outside",
                Hsm.Transition(Hsm.On("enter"), Hsm.Target("../drive/idle"))),
            Hsm.SubmachineState("drive", child)));
        Assert.Throws<ValidationException>(() => Hsm.Define(
            "InvalidBoundarySource",
            Hsm.Initial(Hsm.Target("drive")),
            Hsm.SubmachineState("drive", child),
            Hsm.State("outside"),
            Hsm.Transition(
                Hsm.Source("drive/idle"),
                Hsm.On("leave"),
                Hsm.Target("outside"))));
        Assert.Throws<ValidationException>(() => Hsm.Define(
            "StateConnectionCollision",
            Hsm.EntryPoint("idle", Hsm.Target("idle")),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle")));
        Assert.Throws<ValidationException>(() => Hsm.Define(
            "ConnectionCollision",
            Hsm.EntryPoint("route", Hsm.Target("idle")),
            Hsm.ExitPoint("route"),
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State("idle")));
    }

    [Fact]
    public void FinalStateCannotHaveTransitions()
    {
        var error = Assert.Throws<ValidationException>(() => Hsm.Define(
            "BadFinalTransition",
            Hsm.Final("done"),
            Hsm.State("idle"),
            Hsm.Transition(
                Hsm.On("advance"),
                Hsm.Source("done"),
                Hsm.Target("idle")),
            Hsm.Initial(Hsm.Target("idle"))));

        Assert.Contains("final state", error.Message, StringComparison.Ordinal);
    }
}
