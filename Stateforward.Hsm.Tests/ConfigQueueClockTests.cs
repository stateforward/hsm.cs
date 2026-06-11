using Xunit;

namespace Stateforward.Hsm.Tests;

public sealed class ConfigQueueClockTests
{
    private sealed class TestMachine : Instance
    {
    }

    [Fact]
    public void QueueHooksAreSynchronousApi()
    {
        Func<Context, Event, Exception?> push = (_, _) => null;
        Func<Context, (Event? Event, Exception? Error)> pop = _ => (null, null);
        Func<Context, (int Count, Exception? Error)> len = _ => (0, null);

        var queue = new Stateforward.Hsm.Queue(push, pop, len);

        Assert.NotNull(queue);
        var hookConstructor = typeof(Stateforward.Hsm.Queue)
            .GetConstructors()
            .Single(ctor =>
            {
                var parameters = ctor.GetParameters();
                return parameters.Length >= 3
                       && parameters[0].ParameterType.IsGenericType
                       && parameters[0].ParameterType.GetGenericTypeDefinition() == typeof(Func<,,>);
            });

        foreach (var parameter in hookConstructor.GetParameters().Take(3))
        {
            var returnType = parameter.ParameterType.GetGenericArguments().Last();
            Assert.False(typeof(Task).IsAssignableFrom(returnType));
        }
    }

    [Fact]
    public async Task CustomQueueReceivesRegularEventsOnlyAndCompletionEventsKeepPriority()
    {
        var pushed = new List<string>();
        var regularEvents = new System.Collections.Generic.Queue<Event>();
        var queue = new Stateforward.Hsm.Queue(
            (_, evt) =>
            {
                pushed.Add(evt.Name);
                regularEvents.Enqueue(evt);
                return null;
            },
            _ => regularEvents.Count == 0 ? (null, null) : (regularEvents.Dequeue(), null),
            _ => (regularEvents.Count, null));

        var trace = new List<string>();
        var model = Hsm.Define(
            "CustomQueuePriority",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Transition(
                    Hsm.On("trigger"),
                    Hsm.Effect<TestMachine>((_, sm, _) =>
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

        var machine = Hsm.Start(new Context(), new TestMachine(), model, new Config { Queue = queue });
        await machine.Dispatch(new Event("trigger"));

        Assert.Equal("/CustomQueuePriority/priority", machine.State);
        Assert.Equal(new[] { "trigger", "regular" }, pushed);
        Assert.Equal(new[] { "trigger", "priority" }, trace);
    }

    [Fact]
    public async Task QueueOperationErrorsAreRaisedAsRuntimePriorityErrorEvents()
    {
        var pushed = new List<string>();
        var queue = new Stateforward.Hsm.Queue(
            (_, evt) =>
            {
                pushed.Add(evt.Name);
                return new InvalidOperationException("queue push failed");
            },
            _ => (null, null),
            _ => (0, null));

        var model = Hsm.Define(
            "CustomQueuePushError",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Transition(Hsm.On("hsm.error"), Hsm.Target("../recovered"))),
            Hsm.State("recovered"));

        var machine = Hsm.Start(new Context(), new TestMachine(), model, new Config { Queue = queue });
        await machine.Dispatch(new Event("boom"));

        Assert.Equal("/CustomQueuePushError/recovered", machine.State);
        Assert.Equal(new[] { "boom" }, pushed);
    }

    [Fact]
    public async Task QueuePopErrorsAreRaisedAsRuntimePriorityErrorEvents()
    {
        var pushed = new List<string>();
        var regularEvents = new System.Collections.Generic.Queue<Event>();
        var failNextPop = true;
        var queue = new Stateforward.Hsm.Queue(
            (_, evt) =>
            {
                pushed.Add(evt.Name);
                regularEvents.Enqueue(evt);
                return null;
            },
            _ =>
            {
                if (failNextPop)
                {
                    failNextPop = false;
                    return (null, new InvalidOperationException("queue pop failed"));
                }

                return regularEvents.Count == 0 ? (null, null) : (regularEvents.Dequeue(), null);
            },
            _ => (regularEvents.Count, null));

        var model = Hsm.Define(
            "CustomQueuePopError",
            Hsm.Initial(Hsm.Target("idle")),
            Hsm.State(
                "idle",
                Hsm.Transition(Hsm.On("hsm.error"), Hsm.Target("../recovered")),
                Hsm.Transition(Hsm.On("go"), Hsm.Target("../regular"))),
            Hsm.State("recovered"),
            Hsm.State("regular"));

        var machine = Hsm.Start(new Context(), new TestMachine(), model, new Config { Queue = queue });
        await machine.Dispatch(new Event("go"));

        Assert.Equal("/CustomQueuePopError/recovered", machine.State);
        Assert.Equal(new[] { "go" }, pushed);
    }

    [Fact]
    public async Task PartialConfigClockInheritsUnspecifiedBehaviorFromDefaultClock()
    {
        var previousDefaultClock = Hsm.DefaultClock;
        try
        {
            var harness = new TestClockHarness();
            Hsm.DefaultClock = harness.Clock;

            var configNow = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var target = configNow.AddMinutes(3);
            var model = Hsm.Define(
                "PartialClockRules",
                Hsm.Initial(Hsm.Target("idle")),
                Hsm.State(
                    "idle",
                    Hsm.Transition(
                        Hsm.At<TestMachine>((_, _, _) => target),
                        Hsm.Target("../done"))),
                Hsm.State("done"));

            var machine = Hsm.Start(
                new Context(),
                new TestMachine(),
                model,
                new Config { Clock = new Clock(utcNow: () => configNow) });

            var pending = await harness.NextAsync("partial clock inherited delay");
            Assert.Equal(TimeSpan.FromMinutes(3), pending.Duration);
            pending.Trigger();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            while (machine.State != "/PartialClockRules/done")
            {
                await Task.Delay(10, timeout.Token);
            }

            Assert.Equal("/PartialClockRules/done", machine.State);
        }
        finally
        {
            Hsm.DefaultClock = previousDefaultClock;
        }
    }
}
