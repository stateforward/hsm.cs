using System.Threading;

namespace Stateforward.Hsm;

public delegate void Operation<in TInstance>(Context ctx, TInstance instance, Event @event)
    where TInstance : Instance;

public delegate bool Expression<in TInstance>(Context ctx, TInstance instance, Event @event)
    where TInstance : Instance;

public delegate TimeSpan DurationProvider<in TInstance>(Context ctx, TInstance instance, Event @event)
    where TInstance : Instance;

public delegate DateTimeOffset TimeProvider<in TInstance>(Context ctx, TInstance instance, Event @event)
    where TInstance : Instance;

public delegate Task ConditionChannel<in TInstance>(
    Context ctx,
    TInstance instance,
    Event @event,
    CancellationToken cancellationToken)
    where TInstance : Instance;

public static class Hsm
{
    public static ulong MakeKind(params ulong[] bases) => KindUtility.MakeKind(bases);
    public static bool IsKind(ulong kind, params ulong[] bases) => KindUtility.IsKind(kind, bases);
    public static bool IsKind(Kind kind, params Kind[] bases) => KindUtility.IsKind(kind, bases);

    public static Clock DefaultClock
    {
        get => Runtime.DefaultClock;
        set => Runtime.DefaultClock = value ?? Clock.System;
    }

    public static Model Define(string name, params IPartial[] partials) => Dsl.Define(name, partials);
    public static IPartial State(string name, params IPartial[] partials) => Dsl.State(name, partials);
    public static IPartial Final(string name) => Dsl.Final(name);
    public static IPartial ShallowHistory(string name, params IPartial[] partials) => Dsl.ShallowHistory(name, partials);
    public static IPartial DeepHistory(string name, params IPartial[] partials) => Dsl.DeepHistory(name, partials);
    public static IPartial Choice(string name, params IPartial[] transitions) => Dsl.Choice(name, transitions);
    public static IPartial Transition(params IPartial[] partials) => Dsl.Transition(partials);
    public static IPartial Initial(params IPartial[] partials) => Dsl.Initial(partials);
    public static IPartial Source(string path) => Dsl.Source(path);
    public static IPartial Target(string path) => Dsl.Target(path);
    public static IPartial On(string eventName) => Dsl.On(eventName);
    public static IPartial On(Event @event) => Dsl.On(@event);
    public static IPartial OnCall(string operationName) => Dsl.OnCall(operationName);
    public static IPartial OnSet(string attributeName) => Dsl.OnSet(attributeName);
    public static IPartial When(string attributeName) => Dsl.When(attributeName);
    public static IPartial When<TInstance>(ConditionChannel<TInstance> condition) where TInstance : Instance => Dsl.When(condition);
    public static IPartial After(string attributeName) => Dsl.After(attributeName);
    public static IPartial After<TInstance>(DurationProvider<TInstance> duration) where TInstance : Instance => Dsl.After(duration);
    public static IPartial At(string attributeName) => Dsl.At(attributeName);
    public static IPartial At<TInstance>(TimeProvider<TInstance> time) where TInstance : Instance => Dsl.At(time);
    public static IPartial Every(string attributeName) => Dsl.Every(attributeName);
    public static IPartial Every<TInstance>(DurationProvider<TInstance> duration) where TInstance : Instance => Dsl.Every(duration);
    public static IPartial Entry<TInstance>(params Operation<TInstance>[] ops) where TInstance : Instance => Dsl.Entry(ops);
    public static IPartial Exit<TInstance>(params Operation<TInstance>[] ops) where TInstance : Instance => Dsl.Exit(ops);
    public static IPartial Activity<TInstance>(params Operation<TInstance>[] ops) where TInstance : Instance => Dsl.Activity(ops);
    public static IPartial Effect<TInstance>(params Operation<TInstance>[] ops) where TInstance : Instance => Dsl.Effect(ops);
    public static IPartial Guard<TInstance>(Expression<TInstance> predicate) where TInstance : Instance => Dsl.Guard(predicate);
    public static IPartial Defer(params string[] eventNames) => Dsl.Defer(eventNames);
    public static IPartial Attribute<T>(string name, T? defaultValue = default) => Dsl.Attribute(name, defaultValue);
    public static IPartial Operation(string name, Delegate callback) => Dsl.Operation(name, callback);
    public static Group MakeGroup(params IInstance[] instances) => new(instances);
    public static Group MakeGroup(string groupId, params IInstance[] instances) => new(groupId, instances);

    public static TInstance New<TInstance>(TInstance instance, Model model, Config? config = null)
        where TInstance : Instance => Runtime.New(instance, model, config);

    public static TInstance Start<TInstance>(Context context, TInstance instance, object? data = null)
        where TInstance : Instance => Runtime.Start(context, instance, data);

    public static TInstance Start<TInstance>(Context context, TInstance instance, Model model, Config? config = null)
        where TInstance : Instance => Runtime.Start(context, instance, model, config);

    public static TInstance Started<TInstance>(Context context, TInstance instance, Model model, Config? config = null)
        where TInstance : Instance => Runtime.Started(context, instance, model, config);

    public static Task Dispatch(Context context, IInstance? instance, Event @event) => Runtime.Dispatch(context, instance, @event);

    public static Task Stop(Context context, IInstance instance) => Runtime.Stop(context, instance);
    public static Task Restart(Context context, IInstance instance, object? data = null) => Runtime.Restart(context, instance, data);
    public static Task DispatchAll(Context context, Event @event) => Runtime.DispatchAll(context, @event);

    public static Task DispatchTo(Context context, Event @event, params string[] idPatterns) => Runtime.DispatchTo(context, @event, idPatterns);

    public static T? Get<T>(Context context, IInstance? instance, string attributeName) => Runtime.Get<T>(context, instance, attributeName);
    public static Task Set(Context context, IInstance? instance, string attributeName, object? value) => Runtime.Set(context, instance, attributeName, value);
    public static object? Call(Context context, IInstance? instance, string operationName, params object?[] args) => Runtime.Call(context, instance, operationName, args);
    public static Snapshot TakeSnapshot(Context context, IInstance instance) => Runtime.TakeSnapshot(context, instance);
    public static Task AfterProcess(Context context, IInstance instance, Event? @event = null) => Runtime.AfterProcess(context, instance, @event);
    public static Task AfterDispatch(Context context, IInstance instance, Event @event) => Runtime.AfterDispatch(context, instance, @event);
    public static Task AfterEntry(Context context, IInstance instance, string statePath) => Runtime.AfterEntry(context, instance, statePath);
    public static Task AfterExit(Context context, IInstance instance, string statePath) => Runtime.AfterExit(context, instance, statePath);
    public static Task AfterExecuted(Context context, IInstance instance, string statePath) => Runtime.AfterExecuted(context, instance, statePath);
    public static IInstance? FromContext(Context context) => Runtime.FromContext(context);
    public static IReadOnlyList<IInstance> InstancesFromContext(Context context) => Runtime.InstancesFromContext(context);
    public static string ID(IInstance instance) => Runtime.ID(instance);
    public static string QualifiedName(IInstance instance) => Runtime.QualifiedName(instance);
    public static string Name(IInstance instance) => Runtime.Name(instance);
    public static bool Match(string value, params string[] patterns) => Runtime.Match(value, patterns);
}
