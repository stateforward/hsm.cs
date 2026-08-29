using System.Diagnostics;
using Stateforward.Hsm;

var iterations = ParseIntArg(args, "--iterations", 200_000);
var warmupIterations = ParseIntArg(args, "--warmup", 10_000);

var scenarios = new[]
{
    Scenario.NestedStatesNoEntryExit(iterations, warmupIterations),
    Scenario.NestedStatesEntryExitEffect(iterations, warmupIterations),
    Scenario.DeepNesting3Levels(iterations, warmupIterations),
    Scenario.CrossHierarchy(iterations, warmupIterations),
    Scenario.InvalidEventHandling(iterations, warmupIterations)
};

Console.WriteLine($"Stateforward.Hsm Benchmark");
Console.WriteLine($".NET: {Environment.Version}");
Console.WriteLine($"Iterations: {iterations:N0}");
Console.WriteLine($"Warmup: {warmupIterations:N0}");
Console.WriteLine();
Console.WriteLine($"{"Scenario",-36} {"Transitions",12} {"Mean ns/op",12} {"Trans/sec",14} {"Alloc B/op",12}");

foreach (var scenario in scenarios)
{
    var result = await scenario.RunAsync().ConfigureAwait(false);
    Console.WriteLine(
        $"{result.Name,-36} {result.TransitionCount,12:N0} {result.NanosecondsPerTransition,12:N1} {result.TransitionsPerSecond,14:N0} {result.BytesPerTransition,12:N2}");
}

return;

static int ParseIntArg(string[] args, string name, int fallback)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(args[i + 1], out var value) &&
            value > 0)
        {
            return value;
        }
    }

    return fallback;
}

internal sealed class BenchMachine : Instance
{
    public int Counter;
}

internal sealed class Scenario
{
    private readonly Func<Model> _buildModel;
    private readonly Event _event1;
    private readonly Event _event2;
    private readonly int _iterations;
    private readonly int _warmupIterations;

    private Scenario(string name, Func<Model> buildModel, Event event1, Event event2, int iterations, int warmupIterations)
    {
        Name = name;
        _buildModel = buildModel;
        _event1 = event1;
        _event2 = event2;
        _iterations = iterations;
        _warmupIterations = warmupIterations;
    }

    public string Name { get; }

    public async Task<ScenarioResult> RunAsync()
    {
        var model = _buildModel();
        var context = new Context();
        var machine = Hsm.Start(context, new BenchMachine(), model);

        try
        {
            for (var i = 0; i < _warmupIterations; i++)
            {
                await machine.Dispatch(_event1).ConfigureAwait(false);
                await machine.Dispatch(_event2).ConfigureAwait(false);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var beforeAlloc = GC.GetTotalAllocatedBytes(precise: true);
            var stopwatch = Stopwatch.StartNew();

            for (var i = 0; i < _iterations; i++)
            {
                await machine.Dispatch(_event1).ConfigureAwait(false);
                await machine.Dispatch(_event2).ConfigureAwait(false);
            }

            stopwatch.Stop();
            var afterAlloc = GC.GetTotalAllocatedBytes(precise: true);
            var transitionCount = _iterations * 2L;
            var nsPerTransition = stopwatch.Elapsed.TotalMilliseconds * 1_000_000d / transitionCount;
            var transitionsPerSecond = transitionCount / stopwatch.Elapsed.TotalSeconds;
            var bytesPerTransition = (afterAlloc - beforeAlloc) / (double)transitionCount;

            return new ScenarioResult(Name, transitionCount, nsPerTransition, transitionsPerSecond, bytesPerTransition);
        }
        finally
        {
            await machine.Stop().ConfigureAwait(false);
        }
    }

    public static Scenario NestedStatesNoEntryExit(int iterations, int warmupIterations) =>
        new(
            "NestedStates_NoEntryExit",
            () => Hsm.Define(
                "BenchNested",
                Hsm.State(
                    "parent",
                    Hsm.State("child1"),
                    Hsm.State("child2"),
                    Hsm.Initial(Hsm.Target("child1")),
                    Hsm.Transition(Hsm.On("to2"), Hsm.Source("child1"), Hsm.Target("child2")),
                    Hsm.Transition(Hsm.On("to1"), Hsm.Source("child2"), Hsm.Target("child1"))),
                Hsm.Initial(Hsm.Target("/BenchNested/parent"))),
            new Event("to2"),
            new Event("to1"),
            iterations,
            warmupIterations);

    public static Scenario NestedStatesEntryExitEffect(int iterations, int warmupIterations) =>
        new(
            "NestedStates_EntryExitEffect",
            () => Hsm.Define(
                "BenchNestedEffects",
                Hsm.State(
                    "parent",
                    Hsm.Entry<BenchMachine>((_, machine, _) => machine.Counter++),
                    Hsm.Exit<BenchMachine>((_, machine, _) => machine.Counter++),
                    Hsm.State(
                        "child1",
                        Hsm.Entry<BenchMachine>((_, machine, _) => machine.Counter++),
                        Hsm.Exit<BenchMachine>((_, machine, _) => machine.Counter++)),
                    Hsm.State(
                        "child2",
                        Hsm.Entry<BenchMachine>((_, machine, _) => machine.Counter++),
                        Hsm.Exit<BenchMachine>((_, machine, _) => machine.Counter++)),
                    Hsm.Initial(Hsm.Target("child1")),
                    Hsm.Transition(Hsm.On("to2"), Hsm.Source("child1"), Hsm.Target("child2"), Hsm.Effect<BenchMachine>((_, machine, _) => machine.Counter++)),
                    Hsm.Transition(Hsm.On("to1"), Hsm.Source("child2"), Hsm.Target("child1"), Hsm.Effect<BenchMachine>((_, machine, _) => machine.Counter++))),
                Hsm.Initial(Hsm.Target("/BenchNestedEffects/parent"))),
            new Event("to2"),
            new Event("to1"),
            iterations,
            warmupIterations);

    public static Scenario DeepNesting3Levels(int iterations, int warmupIterations) =>
        new(
            "DeepNesting3Levels",
            () => Hsm.Define(
                "BenchDeep",
                Hsm.State(
                    "level1",
                    Hsm.State(
                        "level2",
                        Hsm.State("level3a"),
                        Hsm.State("level3b"),
                        Hsm.Initial(Hsm.Target("level3a")),
                        Hsm.Transition(Hsm.On("toB"), Hsm.Source("level3a"), Hsm.Target("level3b")),
                        Hsm.Transition(Hsm.On("toA"), Hsm.Source("level3b"), Hsm.Target("level3a"))),
                    Hsm.Initial(Hsm.Target("level2"))),
                Hsm.Initial(Hsm.Target("/BenchDeep/level1"))),
            new Event("toB"),
            new Event("toA"),
            iterations,
            warmupIterations);

    public static Scenario CrossHierarchy(int iterations, int warmupIterations) =>
        new(
            "CrossHierarchy",
            () => Hsm.Define(
                "BenchCross",
                Hsm.State(
                    "parent1",
                    Hsm.State("child1"),
                    Hsm.Initial(Hsm.Target("child1"))),
                Hsm.State(
                    "parent2",
                    Hsm.State("child2"),
                    Hsm.Initial(Hsm.Target("child2"))),
                Hsm.Transition(Hsm.On("to2"), Hsm.Source("parent1"), Hsm.Target("parent2")),
                Hsm.Transition(Hsm.On("to1"), Hsm.Source("parent2"), Hsm.Target("parent1")),
                Hsm.Initial(Hsm.Target("/BenchCross/parent1"))),
            new Event("to2"),
            new Event("to1"),
            iterations,
            warmupIterations);

    public static Scenario InvalidEventHandling(int iterations, int warmupIterations) =>
        new(
            "InvalidEventHandling",
            () => Hsm.Define(
                "BenchInvalid",
                Hsm.State(
                    "level1",
                    Hsm.State(
                        "level2",
                        Hsm.State(
                            "level3",
                            Hsm.Transition(Hsm.On("valid"), Hsm.Target("."))),
                        Hsm.Initial(Hsm.Target("level3"))),
                    Hsm.Initial(Hsm.Target("level2"))),
                Hsm.Initial(Hsm.Target("/BenchInvalid/level1"))),
            new Event("invalid1"),
            new Event("invalid2"),
            iterations,
            warmupIterations);
}

internal sealed record ScenarioResult(
    string Name,
    long TransitionCount,
    double NanosecondsPerTransition,
    double TransitionsPerSecond,
    double BytesPerTransition);
