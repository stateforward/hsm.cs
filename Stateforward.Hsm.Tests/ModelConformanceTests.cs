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
                    Hsm.Transition(Hsm.Target("../../left")),
                    Hsm.Transition(Hsm.Target("../../right"), Hsm.Guard<TestMachine>((_, _, _) => true)))),
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
    public void TopLevelTargetRequiresSourceAndInternalRequiresEffect()
    {
        var topLevelTarget = Assert.Throws<ValidationException>(() => Hsm.Define(
            "BadTopLevelTarget",
            Hsm.State("idle"),
            Hsm.State("done"),
            Hsm.Transition(Hsm.On("go"), Hsm.Target("done")),
            Hsm.Initial(Hsm.Target("idle"))));

        var internalTransition = Assert.Throws<ValidationException>(() => Hsm.Define(
            "BadInternal",
            Hsm.State(
                "idle",
                Hsm.Transition(Hsm.On("go"))),
            Hsm.Initial(Hsm.Target("idle"))));

        Assert.Contains("top level transitions with a target must also define a source", topLevelTarget.Message, StringComparison.Ordinal);
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
                        Hsm.Target("../idle")),
                    Hsm.Transition(Hsm.Target("../idle")))),
            Hsm.Initial(Hsm.Target("parent"))));

        Assert.Contains("already defined", duplicateAttribute.Message, StringComparison.Ordinal);
        Assert.Contains("already defined", duplicateOperation.Message, StringComparison.Ordinal);
        Assert.Contains("missing operation", missingOperation.Message, StringComparison.Ordinal);
        Assert.Contains("real state source", badTemporalSource.Message, StringComparison.Ordinal);
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
