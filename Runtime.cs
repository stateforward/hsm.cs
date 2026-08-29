using System.Collections.ObjectModel;
using System.Reflection;
using System.Threading;

namespace Stateforward.Hsm;

public interface IInstance
{
    string State { get; }
    Context Context { get; }
    Task Dispatch(Event @event);
    Task Stop();
    Task Restart(object? data = null);
}

public sealed class Context
{
    private sealed class SharedState
    {
        public object Gate { get; } = new();
        public List<IInstance> Instances { get; } = new();
        public List<Task> Fanouts { get; } = new();
        public IInstance? PrimaryInstance { get; set; }
    }

    private readonly SharedState _shared;
    private readonly CancellationTokenSource _source;
    internal RuntimeEngine? ActivityProducer { get; }
    internal int? ActivityGeneration { get; }
    internal bool HasStaleActivityProducer => ActivityProducer is not null
        && ActivityGeneration is int generation
        && !ActivityProducer.IsCurrentActivityGeneration(generation);

    public Context() : this(new SharedState(), new CancellationTokenSource())
    {
    }

    private Context(
        SharedState shared,
        CancellationTokenSource source,
        RuntimeEngine? activityProducer = null,
        int? activityGeneration = null)
    {
        _shared = shared;
        _source = source;
        ActivityProducer = activityProducer;
        ActivityGeneration = activityGeneration;
    }

    public CancellationToken CancellationToken => _source.Token;
    public bool IsDone => _source.IsCancellationRequested;

    public void Cancel()
    {
        if (!RuntimeEngine.TryAcquireActivityLease(this, out _, out _, out var activityLease))
        {
            return;
        }
        using (activityLease)
        {
        if (!_source.IsCancellationRequested)
        {
            _source.Cancel();
        }
        }
    }

    internal void Register(IInstance instance)
    {
        lock (_shared.Gate)
        {
            if (_shared.Instances.Contains(instance))
            {
                return;
            }

            _shared.Instances.Add(instance);
            _shared.PrimaryInstance ??= instance;
        }
    }

    internal void Unregister(IInstance instance)
    {
        lock (_shared.Gate)
        {
            _shared.Instances.Remove(instance);
            if (ReferenceEquals(_shared.PrimaryInstance, instance))
            {
                _shared.PrimaryInstance = _shared.Instances.FirstOrDefault();
            }
        }
    }

    internal IInstance? PrimaryInstance
    {
        get
        {
            lock (_shared.Gate)
            {
                return _shared.PrimaryInstance;
            }
        }
    }

    internal IReadOnlyList<IInstance> SnapshotInstances()
    {
        lock (_shared.Gate)
        {
            return _shared.Instances.ToArray();
        }
    }

    internal void ScheduleFanout(Task fanout, RuntimeEngine producer)
    {
        lock (_shared.Gate) _shared.Fanouts.Add(fanout);
        _ = fanout.ContinueWith(
            completed =>
            {
                lock (_shared.Gate) _shared.Fanouts.Remove(completed);
                if (completed.IsFaulted)
                {
                    producer.ReportAsyncError(completed.Exception!.GetBaseException());
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal async Task DrainFanouts()
    {
        while (true)
        {
            Task[] fanouts;
            lock (_shared.Gate)
            {
                fanouts = _shared.Fanouts.ToArray();
                _shared.Fanouts.Clear();
            }

            if (fanouts.Length == 0) return;
            await Task.WhenAll(fanouts).ConfigureAwait(false);
        }
    }

    internal Context CreateLinked(
        CancellationToken cancellationToken,
        RuntimeEngine? activityProducer = null,
        int? activityGeneration = null)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(
            _source.Token,
            cancellationToken.CanBeCanceled ? cancellationToken : CancellationToken.None);
        return new Context(_shared, source, activityProducer, activityGeneration);
    }

    internal void Dispose() => _source.Dispose();
}

public class Clock
{
    public Clock(
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Func<DateTimeOffset>? utcNow = null)
        : this(true, delayAsync, utcNow)
    {
    }

    private Clock(
        bool inheritDefault,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _inheritDefault = inheritDefault;
        DelayAsync = delayAsync;
        UtcNow = utcNow;
    }

    private readonly bool _inheritDefault;

    public static Clock System { get; } = new(false);
    public Func<TimeSpan, CancellationToken, Task>? DelayAsync { get; init; }
    public Func<DateTimeOffset>? UtcNow { get; init; }

    internal Task Delay(TimeSpan due, CancellationToken cancellationToken)
    {
        var normalized = due < TimeSpan.Zero ? TimeSpan.Zero : due;
        var delayAsync = DelayAsync;
        if (delayAsync is null && _inheritDefault && !ReferenceEquals(this, Runtime.DefaultClock))
        {
            delayAsync = Runtime.DefaultClock.DelayAsync;
        }

        return (delayAsync ?? Task.Delay)(normalized, cancellationToken);
    }

    internal DateTimeOffset Now()
    {
        var utcNow = UtcNow;
        if (utcNow is null && _inheritDefault && !ReferenceEquals(this, Runtime.DefaultClock))
        {
            utcNow = Runtime.DefaultClock.UtcNow;
        }

        return (utcNow ?? (() => DateTimeOffset.UtcNow))();
    }
}

public sealed class Config
{
    public string? Id { get; init; }
    public string? ID
    {
        get => Id;
        init => Id = value;
    }

    public string? Name { get; init; }
    public object? Data { get; init; }
    public TimeSpan ActivityTimeout { get; init; } = TimeSpan.FromMilliseconds(1);
    public Clock? Clock { get; init; }
    public Queue? Queue { get; init; }
}

internal sealed class PendingEvent
{
    public PendingEvent(Event @event)
        : this(@event, null, null)
    {
    }

    public PendingEvent(Event @event, RuntimeEngine? activityProducer, int? activityGeneration)
    {
        Event = @event;
        ActivityProducer = activityProducer;
        ActivityGeneration = activityGeneration;
        Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Event Event { get; }
    public RuntimeEngine? ActivityProducer { get; }
    public int? ActivityGeneration { get; }
    public bool HasStaleActivityProducer => ActivityProducer is not null
        && ActivityGeneration is int generation
        && !ActivityProducer.IsCurrentActivityGeneration(generation);
    public TaskCompletionSource<bool> Completion { get; }
}

public class Queue
{
    private readonly Queue? _regularQueueOverride;
    private readonly System.Collections.Generic.Queue<PendingEvent> _regularQueue = new();
    private readonly Stack<PendingEvent> _priorityQueue = new();
    private readonly Func<Context, Event, Exception?>? _pushHook;
    private readonly Func<Context, (Event? Event, Exception? Error)>? _popHook;
    private readonly Func<Context, (int Count, Exception? Error)>? _lenHook;
    private readonly Action? _clearHook;
    private readonly Dictionary<Event, System.Collections.Generic.Queue<PendingEvent>>? _pendingByEvent;

    public Queue(Queue? regularQueue = null)
    {
        _regularQueueOverride = regularQueue;
    }

    public Queue(
        Func<Context, Event, Exception?> push,
        Func<Context, (Event? Event, Exception? Error)> pop,
        Func<Context, (int Count, Exception? Error)> len,
        Action? clear = null)
    {
        _pushHook = push ?? throw new ArgumentNullException(nameof(push));
        _popHook = pop ?? throw new ArgumentNullException(nameof(pop));
        _lenHook = len ?? throw new ArgumentNullException(nameof(len));
        _clearHook = clear;
        _pendingByEvent = new Dictionary<Event, System.Collections.Generic.Queue<PendingEvent>>(ReferenceEqualityComparer.Instance);
    }

    internal virtual Exception? Push(Context context, PendingEvent pending)
    {
        if (pending.Event.Kind.IsCompletionPriority())
        {
            _priorityQueue.Push(pending);
            return null;
        }
        else if (_regularQueueOverride is not null)
        {
            return _regularQueueOverride.Push(context, pending);
        }
        else if (_pushHook is not null)
        {
            try
            {
                var error = _pushHook(context, pending.Event);
                if (error is null)
                {
                    RememberPending(pending);
                }

                return error;
            }
            catch (Exception error)
            {
                return error;
            }
        }
        else
        {
            _regularQueue.Enqueue(pending);
            return null;
        }
    }

    internal virtual (PendingEvent? Pending, Exception? Error) Pop(Context context)
    {
        if (_priorityQueue.Count > 0)
        {
            return (_priorityQueue.Pop(), null);
        }

        if (_regularQueueOverride is not null)
        {
            return _regularQueueOverride.Pop(context);
        }

        if (_popHook is not null)
        {
            try
            {
                var (eventFromQueue, error) = _popHook(context);
                if (error is not null || eventFromQueue is null)
                {
                    return (null, error);
                }

                return (TakePending(eventFromQueue) ?? new PendingEvent(eventFromQueue), null);
            }
            catch (Exception error)
            {
                return (null, error);
            }
        }

        return (_regularQueue.Count == 0 ? null : _regularQueue.Dequeue(), null);
    }

    internal virtual (int Count, Exception? Error) Len(Context context)
    {
        if (_regularQueueOverride is null && _lenHook is null)
        {
            return (_priorityQueue.Count + _regularQueue.Count, null);
        }

        var (count, error) = _regularQueueOverride is not null
            ? _regularQueueOverride.Len(context)
            : SafeLen(context);
        return (_priorityQueue.Count + count, error);
    }

    internal virtual void Clear()
    {
        _regularQueue.Clear();
        _regularQueueOverride?.Clear();
        _pendingByEvent?.Clear();
        _clearHook?.Invoke();
        _priorityQueue.Clear();
    }

    private void RememberPending(PendingEvent pending)
    {
        if (_pendingByEvent is null)
        {
            return;
        }

        if (!_pendingByEvent.TryGetValue(pending.Event, out var waiters))
        {
            waiters = new System.Collections.Generic.Queue<PendingEvent>();
            _pendingByEvent.Add(pending.Event, waiters);
        }

        waiters.Enqueue(pending);
    }

    private PendingEvent? TakePending(Event @event)
    {
        if (_pendingByEvent is null || !_pendingByEvent.TryGetValue(@event, out var waiters))
        {
            return null;
        }

        var pending = waiters.Dequeue();
        if (waiters.Count == 0)
        {
            _pendingByEvent.Remove(@event);
        }

        return pending;
    }

    private (int Count, Exception? Error) SafeLen(Context context)
    {
        try
        {
            return _lenHook!(context);
        }
        catch (Exception error)
        {
            return (0, error);
        }
    }
}

public abstract class Instance : IInstance
{
    internal RuntimeEngine? Engine;
    internal int EngineInstallState;
    private Context? _detachedContext;

    public virtual string State => Engine?.State ?? string.Empty;
    public virtual Context Context => Engine?.Context ?? (_detachedContext ??= new Context());
    public virtual Task Dispatch(Event @event) => Engine?.Dispatch(@event) ?? Task.FromException(new MissingHsmException());

    public virtual Task Stop() => Engine?.StopAsync() ?? Task.FromException(new MissingHsmException());
    public virtual Task Restart(object? data = null) => Engine?.RestartAsync(data) ?? Task.FromException(new MissingHsmException());
    public virtual void OnEventDeferred(Event @event)
    {
    }

    public virtual void OnEventRecalled(Event @event)
    {
    }

    public virtual void OnRuntimeError(Exception error)
    {
    }
}

public sealed class Group : Instance
{
    private readonly IReadOnlyList<IInstance> _instances;
    private readonly Context _context;
    private readonly string _id;

    public Group(params IInstance[] instances) : this(CreateGroupId(), instances)
    {
    }

    public Group(IEnumerable<IInstance> instances) : this(CreateGroupId(), instances)
    {
    }

    public Group(string groupId, params IInstance[] instances) : this(groupId, (IEnumerable<IInstance>)instances)
    {
    }

    public Group(string groupId, IEnumerable<IInstance> instances)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            throw new ValidationException("group id cannot be empty");
        }

        _id = groupId;
        var flattened = new List<IInstance>();
        foreach (var instance in instances)
        {
            switch (instance)
            {
                case null:
                    continue;
                case Group nested:
                    flattened.AddRange(nested._instances);
                    break;
                default:
                    flattened.Add(instance);
                    break;
            }
        }

        _instances = flattened.ToArray();
        _context = _instances.FirstOrDefault()?.Context ?? new Context();
    }

    public IReadOnlyList<IInstance> Instances => _instances;
    public override string State => string.Empty;
    public override Context Context => _context;
    public override Task Dispatch(Event @event) => Dispatch(null, @event);

    internal Task Dispatch(Context? provenanceContext, Event @event)
    {
        if (RuntimeEngine.HasStaleActivityProducer || provenanceContext?.HasStaleActivityProducer == true)
        {
            return Task.CompletedTask;
        }

        var targets = _instances.Where(Runtime.IsStarted).ToArray();
        var producer = RuntimeEngine.CurrentProducer;
        if (producer is null)
        {
            return DispatchTargets(targets, @event, provenanceContext);
        }

        var producerInstance = producer.Instance;
        if (targets.Contains(producerInstance))
        {
            producerInstance.Dispatch(@event).GetAwaiter().GetResult();
        }

        var fanout = DispatchAfterProducer();
        producer.Context.ScheduleFanout(fanout, producer);
        return Task.CompletedTask;

        async Task DispatchAfterProducer()
        {
            await producer.AfterIdle().ConfigureAwait(false);
            await DispatchTargets(
                    targets.Where(instance => !ReferenceEquals(instance, producerInstance)),
                    @event,
                    provenanceContext)
                .ConfigureAwait(false);
        }
    }

    private static async Task DispatchTargets(
        IEnumerable<IInstance> targets,
        Event @event,
        Context? provenanceContext)
    {
        foreach (var instance in targets)
        {
            var outgoing = Runtime.CopyEventForDispatch(@event);
            await (provenanceContext is not null && instance is Instance concrete && concrete.Engine is not null
                    ? concrete.Engine.Dispatch(outgoing, provenanceContext)
                    : instance.Dispatch(outgoing))
                .ConfigureAwait(false);
        }
    }

    public override Task Stop() => Task.WhenAll(_instances.Select(instance => instance.Stop()));
    public override Task Restart(object? data = null) => Task.WhenAll(_instances.Select(instance => instance.Restart(data)));
    internal Task Stop(Context context) => Task.WhenAll(_instances.Select(instance => Runtime.Stop(context, instance)));
    internal Task Restart(Context context, object? data) =>
        Task.WhenAll(_instances.Select(instance => Runtime.Restart(context, instance, data)));

    internal Snapshot TakeSnapshot() => new()
    {
        ID = _id,
        QualifiedName = string.Empty,
        State = string.Empty,
        Attributes = new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>()),
        QueueLen = 0,
        Events = Array.Empty<EventSnapshot>()
    };

    private static string CreateGroupId() => $"group_{Guid.NewGuid():N}";
}

internal static class Runtime
{
    public static Clock DefaultClock { get; set; } = Clock.System;

    internal static Event CopyEventForDispatch(Event @event) => new(
        @event.Name,
        @event.Kind,
        @event.Data,
        @event.Source,
        @event.ID,
        @event.Target,
        CopyEventSchema(@event.Schema),
        @event.QualifiedName);

    internal static bool IsStarted(IInstance instance) =>
        instance is Instance concrete && concrete.Engine?.IsStarted == true;

    internal static object? InvokeOperationReference(
        Context context,
        Instance instance,
        string operationName,
        Event @event)
    {
        var engine = instance.Engine ?? throw new MissingHsmException();
        return engine.InvokeOperationReference(context, operationName, @event);
    }

    private static object? CopyEventSchema(object? schema) => schema;

    internal static object? CopySnapshotValue(object? value) =>
        CopyValue(value, immutableCollections: true, new Dictionary<object, object?>(ReferenceEqualityComparer.Instance));

    internal static object? CopyMutableValue(object? value) =>
        CopyValue(value, immutableCollections: false, new Dictionary<object, object?>(ReferenceEqualityComparer.Instance));

    private static object? CopyValue(object? value, bool immutableCollections, Dictionary<object, object?> seen)
    {
        if (value is null || IsSnapshotScalar(value))
        {
            return value;
        }

        if (seen.TryGetValue(value, out var existing))
        {
            return existing;
        }

        switch (value)
        {
            case Array array:
                {
                    var elementType = value.GetType().GetElementType() ?? typeof(object);
                    var copy = Array.CreateInstance(elementType, array.Length);
                    seen[value] = copy;
                    for (var i = 0; i < array.Length; i++)
                    {
                        copy.SetValue(CopyValue(array.GetValue(i), immutableCollections, seen), i);
                    }

                    return immutableCollections
                        ? Array.AsReadOnly(copy.Cast<object?>().ToArray())
                        : copy;
                }
            case IDictionary<string, object?> typedDictionary:
                {
                    var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
                    seen[value] = copy;
                    foreach (var (key, item) in typedDictionary)
                    {
                        copy[key] = CopyValue(item, immutableCollections, seen);
                    }

                    return immutableCollections
                        ? new ReadOnlyDictionary<string, object?>(copy)
                        : copy;
                }
            case System.Collections.IDictionary dictionary:
                return CopyDictionary(value, dictionary, immutableCollections, seen);
            case System.Collections.IList list:
                return CopyList(value, list, immutableCollections, seen);
            case System.Collections.IEnumerable enumerable when value is not string:
                return CopyEnumerable(value, enumerable, immutableCollections, seen);
            case ICloneable cloneable:
                return cloneable.Clone();
            default:
                return value;
        }
    }

    private static bool IsSnapshotScalar(object value) =>
        value is string
            or Type
            or Uri
            or DateTime
            or DateTimeOffset
            or TimeSpan
            or Guid
            or decimal
            || value.GetType().IsPrimitive
            || value.GetType().IsEnum;

    private static object CopyDictionary(
        object original,
        System.Collections.IDictionary dictionary,
        bool immutableCollections,
        Dictionary<object, object?> seen)
    {
        System.Collections.IDictionary copy = TryCreateMutableDictionary(original)
            ?? new Dictionary<object, object?>();
        seen[original] = copy;

        foreach (System.Collections.DictionaryEntry entry in dictionary)
        {
            if (entry.Key is not null)
            {
                copy[entry.Key] = CopyValue(entry.Value, immutableCollections, seen);
            }
        }

        return immutableCollections
            ? new ReadOnlyDictionary<object, object?>(copy
                .Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(entry => entry.Key, entry => entry.Value))
            : copy;
    }

    private static object CopyList(
        object original,
        System.Collections.IList list,
        bool immutableCollections,
        Dictionary<object, object?> seen)
    {
        System.Collections.IList copy = TryCreateMutableList(original)
            ?? new System.Collections.ArrayList();
        seen[original] = copy;

        foreach (var item in list)
        {
            copy.Add(CopyValue(item, immutableCollections, seen));
        }

        return immutableCollections
            ? new ReadOnlyCollection<object?>(copy.Cast<object?>().ToArray())
            : copy;
    }

    private static IReadOnlyList<object?> CopyEnumerable(
        object original,
        System.Collections.IEnumerable enumerable,
        bool immutableCollections,
        Dictionary<object, object?> seen)
    {
        var copy = new List<object?>();
        seen[original] = copy;

        foreach (var item in enumerable)
        {
            copy.Add(CopyValue(item, immutableCollections, seen));
        }

        return immutableCollections
            ? copy.AsReadOnly()
            : copy;
    }

    private static System.Collections.IDictionary? TryCreateMutableDictionary(object original)
    {
        var type = original.GetType();
        if (type.IsInterface || type.IsAbstract)
        {
            return null;
        }

        try
        {
            return Activator.CreateInstance(type) as System.Collections.IDictionary;
        }
        catch
        {
            return null;
        }
    }

    private static System.Collections.IList? TryCreateMutableList(object original)
    {
        var type = original.GetType();
        if (type.IsInterface || type.IsAbstract || type.IsArray)
        {
            return null;
        }

        try
        {
            return Activator.CreateInstance(type) as System.Collections.IList;
        }
        catch
        {
            return null;
        }
    }

    public static TInstance New<TInstance>(TInstance instance, Model model, Config? config = null)
        where TInstance : Instance
    {
        if (!RuntimeEngine.TryAcquireActivityLease(null, out _, out _, out var activityLease))
        {
            return instance;
        }
        using (activityLease)
        {
            if (Volatile.Read(ref instance.Engine) is not null)
            {
                throw new AlreadyStartedException();
            }
            if (Interlocked.CompareExchange(ref instance.EngineInstallState, 1, 0) != 0)
            {
                throw new AlreadyStartedException();
            }
            try
            {
                if (Volatile.Read(ref instance.Engine) is not null)
                {
                    throw new AlreadyStartedException();
                }

                Volatile.Write(
                    ref instance.Engine,
                    new RuntimeEngine(new Context(), instance, model, config ?? new Config()));
                return instance;
            }
            finally
            {
                Volatile.Write(ref instance.EngineInstallState, 0);
            }
        }
    }

    public static TInstance Start<TInstance>(Context context, TInstance instance, TInstance? _ = null)
        where TInstance : Instance => Start(context, instance, data: null);

    public static TInstance Start<TInstance>(Context context, TInstance instance, object? data = null)
        where TInstance : Instance
    {
        if (!RuntimeEngine.TryAcquireActivityLease(context, out _, out _, out var activityLease))
        {
            return instance;
        }
        using (activityLease)
        {

        if (instance.Engine is null)
        {
            throw new MissingHsmException();
        }

        instance.Engine.Start(context, data);
        return instance;
        }
    }

    public static TInstance Start<TInstance>(Context context, TInstance instance, Model model, Config? config = null)
        where TInstance : Instance
    {
        if (!RuntimeEngine.TryAcquireActivityLease(context, out _, out _, out var activityLease))
        {
            return instance;
        }
        using (activityLease)
        {

        if (instance.Engine is not null)
        {
            throw new AlreadyStartedException();
        }

        New(instance, model, config);
        return Start(context, instance, config?.Data);
        }
    }

    public static TInstance Started<TInstance>(Context context, TInstance instance, Model model, Config? config = null)
        where TInstance : Instance => Start(context, instance, model, config);

    public static Task Dispatch(Context context, IInstance? instance, Event @event)
    {
        if (context.HasStaleActivityProducer)
        {
            return Task.CompletedTask;
        }

        if (instance is not null)
        {
            return DispatchWithContext(context, instance, @event);
        }

        var resolved = FromContext(context);
        return resolved is null ? Task.CompletedTask : DispatchWithContext(context, resolved, @event);
    }

    public static Task Stop(Context context, IInstance instance) => instance switch
    {
        Group group => group.Stop(context),
        Instance concrete when concrete.Engine is not null => concrete.Engine.StopAsync(context),
        _ => instance.Stop()
    };
    public static Task Restart(Context context, IInstance instance, object? data = null) => instance switch
    {
        Group group => group.Restart(context, data),
        Instance concrete when concrete.Engine is not null => concrete.Engine.RestartAsync(context, data),
        _ => instance.Restart(data)
    };
    public static async Task DispatchAll(Context context, Event @event)
    {
        if (context.HasStaleActivityProducer)
        {
            return;
        }

        foreach (var instance in context.SnapshotInstances().Where(IsStarted))
        {
            var outgoing = CopyEventForDispatch(@event);
            outgoing.Target ??= ID(instance);
            await DispatchWithContext(context, instance, outgoing).ConfigureAwait(false);
        }
    }

    public static Task DispatchTo(Context context, Event @event, params string[] idPatterns)
    {
        if (context.HasStaleActivityProducer)
        {
            return Task.CompletedTask;
        }

        var targets = context.SnapshotInstances()
            .Where(IsStarted)
            .Where(instance => idPatterns.Length == 0 || Match(ID(instance), idPatterns))
            .DistinctBy(instance => ID(instance))
            .ToArray();
        return DispatchTargets(context, targets, @event);
    }

    private static async Task DispatchTargets(Context context, IEnumerable<IInstance> targets, Event @event)
    {
        foreach (var instance in targets)
        {
            var outgoing = CopyEventForDispatch(@event);
            outgoing.Target ??= ID(instance);
            await DispatchWithContext(context, instance, outgoing).ConfigureAwait(false);
        }
    }

    private static Task DispatchWithContext(Context context, IInstance instance, Event @event) =>
        instance switch
        {
            Group group => group.Dispatch(context, @event),
            Instance concrete when concrete.Engine is not null => concrete.Engine.Dispatch(@event, context),
            _ => instance.Dispatch(@event)
        };

    public static T? Get<T>(Context context, IInstance? instance, string attributeName)
    {
        instance ??= FromContext(context);
        var value = instance switch
        {
            Group group when group.Instances.Count > 0 => Get<object?>(context, group.Instances[0], attributeName),
            Instance concrete when concrete.Engine is not null => Runtime.CopyMutableValue(concrete.Engine.GetAttribute(attributeName)),
            _ => throw new MissingHsmException()
        };

        if (value is null)
        {
            return default;
        }

        return value is T typed ? typed : default;
    }

    public static async Task Set(Context context, IInstance? instance, string attributeName, object? value)
    {
        instance ??= FromContext(context);
        switch (instance)
        {
            case Group group:
                {
                    await Task.WhenAll(group.Instances.Select(child => Set(context, child, attributeName, value)));
                    return;
                }
            case Instance concrete when concrete.Engine is not null:
                await concrete.Engine.SetAttributeAsync(context, attributeName, value);
                return;
            default:
                throw new MissingHsmException();
        }
    }

    public static object? Call(Context context, IInstance? instance, string operationName, params object?[] args)
    {
        instance ??= FromContext(context);
        return instance switch
        {
            Group group when group.Instances.Count > 0 => Call(context, group.Instances[0], operationName, args),
            Instance concrete when concrete.Engine is not null => concrete.Engine.CallOperation(context, operationName, args),
            _ => throw new MissingHsmException()
        };
    }

    public static Snapshot TakeSnapshot(Context context, IInstance instance) =>
        instance switch
        {
            Group group => group.TakeSnapshot(),
            Instance concrete when concrete.Engine is not null => concrete.Engine.TakeSnapshot(),
            _ => throw new MissingHsmException()
        };

    public static Task AfterProcess(Context context, IInstance instance, Event? @event = null) =>
        instance is Instance concrete && concrete.Engine is not null
            ? concrete.Engine.AfterProcess(@event)
            : Task.CompletedTask;

    public static Task AfterIdle(Context context, IInstance instance) =>
        instance is Instance concrete && concrete.Engine is not null
            ? concrete.Engine.AfterIdle()
            : Task.CompletedTask;

    public static Task AfterIdle(Context context) => context.DrainFanouts();

    public static Task AfterDispatch(Context context, IInstance instance, Event @event) =>
        instance is Instance concrete && concrete.Engine is not null
            ? concrete.Engine.AfterDispatch(@event)
            : Task.CompletedTask;

    public static Task AfterEntry(Context context, IInstance instance, string statePath) =>
        instance is Instance concrete && concrete.Engine is not null
            ? concrete.Engine.AfterEntry(statePath)
            : Task.CompletedTask;

    public static Task AfterExit(Context context, IInstance instance, string statePath) =>
        instance is Instance concrete && concrete.Engine is not null
            ? concrete.Engine.AfterExit(statePath)
            : Task.CompletedTask;

    public static Task AfterExecuted(Context context, IInstance instance, string statePath) =>
        instance is Instance concrete && concrete.Engine is not null
            ? concrete.Engine.AfterExecuted(statePath)
            : Task.CompletedTask;

    public static IInstance? FromContext(Context context) => context.PrimaryInstance;
    public static IReadOnlyList<IInstance> InstancesFromContext(Context context) => context.SnapshotInstances();

    public static string ID(IInstance instance) =>
        instance switch
        {
            Group group => group.TakeSnapshot().ID,
            Instance concrete when concrete.Engine is not null => concrete.Engine.ID,
            _ => string.Empty
        };

    public static string QualifiedName(IInstance instance) =>
        instance switch
        {
            Group group => group.TakeSnapshot().QualifiedName,
            Instance concrete when concrete.Engine is not null => concrete.Engine.QualifiedName,
            _ => string.Empty
        };

    public static string Name(IInstance instance)
    {
        var qualifiedName = QualifiedName(instance);
        return string.IsNullOrWhiteSpace(qualifiedName) ? string.Empty : PathUtil.Name(qualifiedName);
    }

    public static bool Match(string value, params string[] patterns)
    {
        if (patterns.Length == 0)
        {
            return false;
        }

        foreach (var pattern in patterns)
        {
            if (IsMatch(value ?? string.Empty, pattern ?? string.Empty))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMatch(string value, string pattern)
    {
        var valueIndex = 0;
        var patternIndex = 0;
        var starIndex = -1;
        var matchIndex = 0;

        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length && (pattern[patternIndex] == value[valueIndex]))
            {
                valueIndex++;
                patternIndex++;
                continue;
            }

            if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                matchIndex = valueIndex;
                continue;
            }

            if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                valueIndex = ++matchIndex;
                continue;
            }

            return false;
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }
}

internal sealed class RuntimeEngine
{
    private enum LifecycleOperation
    {
        None,
        Stop,
        RestartExit,
        RestartEnter
    }

    private static readonly AsyncLocal<RuntimeEngine?> ActivityProducer = new();
    private static readonly AsyncLocal<int?> ActivityGeneration = new();
    private static readonly AsyncLocal<GenerationLease?> ActiveGenerationLease = new();
    private static readonly AsyncLocal<ExecutionFrame?> Execution = new();
    internal static RuntimeEngine? CurrentProducer => Execution.Value?.BehaviorProducer;
    internal static bool HasStaleActivityProducer => ActivityProducer.Value is { } producer
        && ActivityGeneration.Value is int generation
        && !producer.IsCurrentActivityGeneration(generation);

    private sealed class StateScope
    {
        public StateScope(CancellationTokenSource cancellation)
        {
            Cancellation = cancellation;
        }

        public CancellationTokenSource Cancellation { get; }
        public List<CancellationTokenSource> Activities { get; } = [];
    }

    internal sealed class GenerationLease : IDisposable
    {
        private readonly GenerationLease? _previous;
        private readonly GenerationLease _root;
        private bool _disposed;
        private bool _active;

        internal GenerationLease(RuntimeEngine owner, int generation, GenerationLease? previous)
        {
            Owner = owner;
            Generation = generation;
            _previous = previous;
            _root = previous is not null
                && ReferenceEquals(previous.Owner, owner)
                && previous.Generation == generation
                && previous._root.IsActive
                    ? previous._root
                    : this;
            if (ReferenceEquals(_root, this)) _active = true;
        }

        internal RuntimeEngine Owner { get; }
        internal int Generation { get; }
        internal bool IsActive => _root._active;
        internal bool IsRoot => ReferenceEquals(_root, this);

        internal void Retire()
        {
            _root._active = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ActiveGenerationLease.Value = _previous;
            if (IsRoot) Owner.ReleaseGenerationLease(this);
        }
    }

    private readonly record struct ExecutionFrame(
        RuntimeEngine Owner,
        RuntimeEngine? BehaviorProducer,
        int OperationDepth,
        string? ResolutionScope);

    private sealed record GeneratedCall(
        Event Event,
        int LifecycleVersion,
        RuntimeEngine? ActivityProducer,
        int? ActivityGeneration);

    private sealed class ObserverRegistry
    {
        public Dictionary<string, List<TaskCompletionSource<bool>>> Entered { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<TaskCompletionSource<bool>>> Exited { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<TaskCompletionSource<bool>>> Dispatched { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<TaskCompletionSource<bool>>> Processed { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<TaskCompletionSource<bool>>> Executed { get; } = new(StringComparer.Ordinal);
        public List<TaskCompletionSource<bool>> Cycles { get; } = new();
    }

    private readonly object _gate = new();
    private readonly object _lifecycleGate = new();
    private readonly object _generationGate = new();
    private readonly Queue _queue;
    private readonly List<PendingEvent> _deferred = new();
    private readonly HashSet<PendingEvent> _deferredQueued = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PendingEvent, string> _deferredOwners = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PendingEvent> _discardedDeferred = new(ReferenceEqualityComparer.Instance);
    private readonly List<GeneratedCall> _generatedCalls = new();
    private readonly Dictionary<string, object?> _attributes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StateScope> _activeScopes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _historyShallow = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _historyDeep = new(StringComparer.Ordinal);
    private readonly Model _model;
    private readonly Instance _instance;
    private readonly Config _config;
    private ObserverRegistry? _observers;
    private bool _processing;
    private TaskCompletionSource<bool>? _processingCompletion;
    private int? _processingThreadId;
    private bool _pauseAfterDeferred;
    private bool _rebuildDeferredQueue;
    private bool _skipDeferredReplay;
    private volatile int _lifecycleVersion;
    private int _activeGenerationLeases;
    private bool _generationRetiring;
    private volatile LifecycleOperation _lifecycleOperation;
    private LifecycleOperation _requestedLifecycleOperation;
    private object? _requestedLifecycleData;
    private bool _requestedLifecycleRetired;
    private State _currentState;

    private int CurrentOperationDepth => Execution.Value is { } execution
        && ReferenceEquals(execution.Owner, this)
            ? execution.OperationDepth
            : 0;
    private string? CurrentResolutionScope => Execution.Value is { } execution
        && ReferenceEquals(execution.Owner, this)
            ? execution.ResolutionScope
            : null;

    public RuntimeEngine(Context context, Instance instance, Model model, Config config)
    {
        Context = context;
        _instance = instance;
        _model = model;
        _config = config;
        _queue = new Queue(config.Queue);
        _currentState = model;

        QualifiedName = string.IsNullOrWhiteSpace(config.Name)
            ? model.QualifiedName
            : config.Name!;

        var simpleName = PathUtil.Name(QualifiedName);
        ID = string.IsNullOrWhiteSpace(config.Id)
            ? $"{simpleName}_{Guid.NewGuid():N}"
            : config.Id!;

        ResetAttributes();
    }

    public Context Context { get; private set; }
    internal Instance Instance => _instance;
    public string ID { get; }
    public string QualifiedName { get; }
    public string State => IsStarted ? _currentState.QualifiedName : string.Empty;
    public bool IsStarted { get; private set; }

    private ExecutionFrame? PushExecution(RuntimeEngine? behaviorProducer, string? resolutionScope, int depthDelta = 0)
    {
        var previous = Execution.Value;
        var sameOwner = previous is { } execution && ReferenceEquals(execution.Owner, this);
        Execution.Value = new ExecutionFrame(
            this,
            behaviorProducer,
            (sameOwner ? previous!.Value.OperationDepth : 0) + depthDelta,
            resolutionScope ?? (sameOwner ? previous!.Value.ResolutionScope : null));
        return previous;
    }

    private static void RestoreExecution(ExecutionFrame? previous) => Execution.Value = previous;

    internal void ReportAsyncError(Exception error)
    {
        try
        {
            _instance.OnRuntimeError(error);
        }
        catch
        {
        }

        if (IsStarted) _ = Dispatch(new ErrorEvent(error));
    }

    private Clock Clock => _config.Clock ?? Runtime.DefaultClock;

    public void Start(Context context, object? data)
    {
        if (!TryAcquireActivityLease(context, out _, out _, out var activityLease))
        {
            return;
        }
        using (activityLease)
        {
        lock (_lifecycleGate)
        {
            if (IsStarted)
            {
                throw new AlreadyStartedException();
            }

            Context = context;
            Context.Register(_instance);
            _queue.Clear();
            _deferred.Clear();
            _deferredQueued.Clear();
            _deferredOwners.Clear();
            _discardedDeferred.Clear();
            _generatedCalls.Clear();
            _historyShallow.Clear();
            _historyDeep.Clear();
            ResetAttributes();
            IsStarted = true;
            BeginInlineProcessing();
            try
            {
                _currentState = EnterVertex(_model, new InitialEvent(data), true);
            }
            catch (Exception error)
            {
                _instance.OnRuntimeError(error);
                _queue.Push(Context, new PendingEvent(new ErrorEvent(error)));
            }

            ProcessQueueWorker();
        }
        }
    }

    public Task Dispatch(Event @event)
    {
        return DispatchCore(@event, deferActivityProducer: false);
    }

    internal Task Dispatch(Event @event, Context provenanceContext) => DispatchCore(
        @event,
        deferActivityProducer: false,
        provenanceContext.ActivityProducer,
        provenanceContext.ActivityGeneration);

    private Task DispatchCore(
        Event @event,
        bool deferActivityProducer,
        RuntimeEngine? activityProducer = null,
        int? activityGeneration = null)
    {
        activityProducer ??= ActivityProducer.Value;
        activityGeneration ??= ActivityGeneration.Value;
        if (activityProducer is not null
            && activityGeneration is int generation
            && !activityProducer.IsCurrentActivityGeneration(generation))
        {
            return Task.CompletedTask;
        }

        if (_lifecycleOperation is LifecycleOperation.Stop or LifecycleOperation.RestartExit
            && (Monitor.IsEntered(_lifecycleGate) || ReferenceEquals(CurrentProducer, this)))
        {
            return Task.CompletedTask;
        }

        if (!IsStarted || Context.IsDone)
        {
            return !IsStarted
                ? Task.FromException(new HsmRuntimeException("dispatch requires a started HSM"))
                : Task.CompletedTask;
        }

        PendingEvent pending;
        var startProcessor = false;
        var processInline = false;
        Task processingTask;
        lock (_gate)
        {
            pending = new PendingEvent(
                Runtime.CopyEventForDispatch(@event),
                activityProducer,
                activityGeneration);
            var error = _queue.Push(Context, pending);
            if (error is not null && !@event.Kind.IsCompletionPriority())
            {
                _queue.Push(Context, new PendingEvent(new ErrorEvent(error), activityProducer, activityGeneration));
            }

            Notify(_observers?.Dispatched, @event.Name);
            if (_lifecycleOperation == LifecycleOperation.RestartEnter
                && (Monitor.IsEntered(_lifecycleGate) || ReferenceEquals(CurrentProducer, this)))
            {
                if (!_processing)
                {
                    _processing = true;
                    _processingCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _ = Task.Run(ProcessQueueWorker);
                }
                return Task.CompletedTask;
            }
            if (deferActivityProducer && ReferenceEquals(ActivityProducer.Value, this))
            {
                return Task.CompletedTask;
            }

            if (_processing)
            {
                if (_processingThreadId == Environment.CurrentManagedThreadId)
                {
                    processInline = CurrentOperationDepth > 0;
                    processingTask = Task.CompletedTask;
                }
                else if (ReferenceEquals(CurrentProducer, this) && ActivityProducer.Value is null)
                {
                    return Task.CompletedTask;
                }
                else
                {
                    return _processingCompletion?.Task ?? Task.CompletedTask;
                }
            }
            else
            {
                _processing = true;
                _processingCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                processingTask = _processingCompletion.Task;
                startProcessor = true;
            }
        }

        if (processInline) ProcessPendingInline();
        if (startProcessor)
        {
            _ = Task.Run(ProcessQueueWorker);
        }

        return processingTask;
    }

    public Task StopAsync() => StopAsync(null, generationRetired: false, allowBlocking: false);

    internal Task StopAsync(Context context) => StopAsync(context, generationRetired: false, allowBlocking: false);

    private Task StopAsync(Context? provenanceContext, bool generationRetired, bool allowBlocking)
    {
        if (!TryAcquireActivityLease(provenanceContext, out _, out _, out var activityLease))
        {
            return Task.CompletedTask;
        }
        using (activityLease)
        {
        if (IsAsyncBehaviorContinuation)
        {
            return RequestLifecycleOperation(LifecycleOperation.Stop, null);
        }

        if (!generationRetired && !allowBlocking && MustWaitForGenerationLeases())
        {
            return Task.Run(() => StopAsync(provenanceContext, generationRetired: false, allowBlocking: true));
        }
        if (!generationRetired) RetireActivityGeneration();

        Task processingTask;
        var ownsProcessing = false;
        var calledByProcessor = false;
        lock (_lifecycleGate)
        {
            if (_lifecycleOperation != LifecycleOperation.None)
            {
                return Task.CompletedTask;
            }
            if (!IsStarted)
            {
                return Task.FromException(new HsmRuntimeException("stop requires a started HSM"));
            }

            _lifecycleOperation = LifecycleOperation.Stop;
            try
            {
                lock (_gate)
                {
                    ownsProcessing = !_processing;
                    calledByProcessor = _processingThreadId == Environment.CurrentManagedThreadId;
                }
                if (ownsProcessing) BeginInlineProcessing();

                ExitToAncestor(_currentState.QualifiedName, _model.QualifiedName, new CompletionEvent(CompletionEvent.EventName));
                CancelScopes();
                lock (_gate)
                {
                    _queue.Clear();
                    _deferred.Clear();
                    _deferredQueued.Clear();
                    _deferredOwners.Clear();
                    _discardedDeferred.Clear();
                    _generatedCalls.Clear();
                    _historyShallow.Clear();
                    _historyDeep.Clear();
                    _pauseAfterDeferred = false;
                    _rebuildDeferredQueue = false;
                    _skipDeferredReplay = false;
                    ResetAttributes();
                    _currentState = _model;
                    IsStarted = false;
                    processingTask = _processingCompletion?.Task ?? Task.CompletedTask;
                }
                Context.Unregister(_instance);
            }
            finally
            {
                _lifecycleOperation = LifecycleOperation.None;
            }
        }

        if (ownsProcessing) ProcessQueueWorker();
        return calledByProcessor ? Task.CompletedTask : processingTask;
        }
    }

    public Task RestartAsync(object? data) => RestartAsync(data, null, generationRetired: false, allowBlocking: false);

    internal Task RestartAsync(Context context, object? data) =>
        RestartAsync(data, context, generationRetired: false, allowBlocking: false);

    private Task RestartAsync(object? data, Context? provenanceContext, bool generationRetired, bool allowBlocking)
    {
        if (!TryAcquireActivityLease(provenanceContext, out _, out _, out var activityLease))
        {
            return Task.CompletedTask;
        }
        using (activityLease)
        {
        if (IsAsyncBehaviorContinuation)
        {
            return RequestLifecycleOperation(LifecycleOperation.RestartExit, data);
        }

        if (!generationRetired && !allowBlocking && MustWaitForGenerationLeases())
        {
            return Task.Run(() => RestartAsync(data, provenanceContext, generationRetired: false, allowBlocking: true));
        }
        if (!generationRetired) RetireActivityGeneration();

        Task processingTask;
        var ownsProcessing = false;
        var calledByProcessor = false;
        lock (_lifecycleGate)
        {
            if (_lifecycleOperation != LifecycleOperation.None)
            {
                return Task.CompletedTask;
            }
            if (!IsStarted)
            {
                return Task.FromException(new HsmRuntimeException("restart requires a started HSM"));
            }

            _lifecycleOperation = LifecycleOperation.RestartExit;
            try
            {
                lock (_gate)
                {
                    _queue.Clear();
                    _deferred.Clear();
                    _deferredQueued.Clear();
                    _deferredOwners.Clear();
                    _discardedDeferred.Clear();
                    _generatedCalls.Clear();
                    _pauseAfterDeferred = false;
                    _rebuildDeferredQueue = false;
                    _skipDeferredReplay = false;
                    ownsProcessing = !_processing;
                    calledByProcessor = _processingThreadId == Environment.CurrentManagedThreadId;
                }
                if (ownsProcessing) BeginInlineProcessing();

                ExitToAncestor(_currentState.QualifiedName, _model.QualifiedName, new CompletionEvent(CompletionEvent.EventName));
                CancelScopes();
                _historyShallow.Clear();
                _historyDeep.Clear();
                ResetAttributes();
                _lifecycleOperation = LifecycleOperation.RestartEnter;
                _currentState = EnterVertex(_model, new InitialEvent(data), true);
            }
            catch (Exception error)
            {
                _queue.Push(Context, new PendingEvent(new ErrorEvent(error)));
            }
            finally
            {
                _lifecycleOperation = LifecycleOperation.None;
            }

            lock (_gate) processingTask = _processingCompletion?.Task ?? Task.CompletedTask;
        }

        if (ownsProcessing) ProcessQueueWorker();

        return calledByProcessor ? Task.CompletedTask : processingTask;
        }
    }

    private bool IsAsyncBehaviorContinuation =>
        ReferenceEquals(CurrentProducer, this)
        && ActivityProducer.Value is null
        && _processing
        && _processingThreadId != Environment.CurrentManagedThreadId;

    private Task RequestLifecycleOperation(LifecycleOperation operation, object? data)
    {
        var applyDirectly = false;
        lock (_gate)
        {
            if (_lifecycleOperation != LifecycleOperation.None || _requestedLifecycleOperation != LifecycleOperation.None)
            {
                return Task.CompletedTask;
            }
            if (!IsStarted)
            {
                return Task.FromException(new HsmRuntimeException(
                    operation == LifecycleOperation.Stop
                        ? "stop requires a started HSM"
                        : "restart requires a started HSM"));
            }
            if (!_processing)
            {
                applyDirectly = true;
            }
            else
            {
                RetireActivityGeneration();
                _requestedLifecycleOperation = operation;
                _requestedLifecycleData = data;
                _requestedLifecycleRetired = true;
            }
        }

        if (applyDirectly)
        {
            var previousExecution = Execution.Value;
            Execution.Value = null;
            try
            {
                return operation == LifecycleOperation.Stop
                    ? StopAsync()
                    : RestartAsync(data);
            }
            finally
            {
                Execution.Value = previousExecution;
            }
        }

        return Task.CompletedTask;
    }

    private void ApplyRequestedLifecycleOperation()
    {
        LifecycleOperation operation;
        object? data;
        bool generationRetired;
        lock (_gate)
        {
            operation = _requestedLifecycleOperation;
            data = _requestedLifecycleData;
            generationRetired = _requestedLifecycleRetired;
            _requestedLifecycleOperation = LifecycleOperation.None;
            _requestedLifecycleData = null;
            _requestedLifecycleRetired = false;
        }

        if (operation == LifecycleOperation.None || !IsStarted)
        {
            return;
        }

        var previousActivityProducer = ActivityProducer.Value;
        var previousActivityGeneration = ActivityGeneration.Value;
        var previousExecution = Execution.Value;
        ActivityProducer.Value = null;
        ActivityGeneration.Value = null;
        Execution.Value = null;
        try
        {
            if (operation == LifecycleOperation.Stop)
            {
                StopAsync(null, generationRetired, allowBlocking: true).GetAwaiter().GetResult();
            }
            else
            {
                RestartAsync(data, null, generationRetired, allowBlocking: true).GetAwaiter().GetResult();
            }
        }
        finally
        {
            ActivityProducer.Value = previousActivityProducer;
            ActivityGeneration.Value = previousActivityGeneration;
            Execution.Value = previousExecution;
        }
    }

    private void BeginInlineProcessing()
    {
        lock (_gate)
        {
            _processing = true;
            _processingThreadId = Environment.CurrentManagedThreadId;
            _processingCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void ProcessPendingInline()
    {
        if (ReferenceEquals(ActivityProducer.Value, this))
        {
            return;
        }

        if (!_processing || _processingThreadId != Environment.CurrentManagedThreadId)
        {
            return;
        }

        if (CurrentResolutionScope is not null
            && !PathUtil.IsDescendantOrSelf(_currentState.QualifiedName, CurrentResolutionScope))
        {
            return;
        }

        ProcessQueueWorker();
        BeginInlineProcessing();
    }

    public object? GetAttribute(string attributeName)
    {
        if (!IsStarted)
        {
            throw new HsmRuntimeException("get requires a started HSM");
        }

        var qualifiedName = QualifyAttribute(attributeName);
        if (!IsKnownAttribute(qualifiedName))
        {
            throw new AttributeHsmException($"unknown attribute '{attributeName}'");
        }

        _attributes.TryGetValue(qualifiedName, out var value);
        return value;
    }

    public async Task SetAttributeAsync(Context callingContext, string attributeName, object? value)
    {
        if (!TryAcquireActivityLease(callingContext, out _, out _, out var activityLease))
        {
            return;
        }
        using (activityLease)
        {

        if (!IsStarted)
        {
            throw new HsmRuntimeException("set requires a started HSM");
        }

        if (string.IsNullOrWhiteSpace(attributeName))
        {
            throw new AttributeHsmException("attribute name cannot be empty");
        }

        var qualifiedName = QualifyAttribute(attributeName);
        if (!IsKnownAttribute(qualifiedName))
        {
            throw new AttributeHsmException($"unknown attribute '{attributeName}'");
        }

        if (_model.Attributes.TryGetValue(qualifiedName, out var attribute)
            && attribute.ValueType != typeof(object)
            && value is not null
            && !IsCompatibleAttributeValue(attribute.ValueType, value))
        {
            throw new AttributeHsmException($"attribute '{attributeName}' has an incompatible value");
        }

        var hadValue = _attributes.TryGetValue(qualifiedName, out var previous);
        if (hadValue && Equals(previous, value))
        {
            return;
        }

        _attributes[qualifiedName] = value;
        var change = new AttributeChange
        {
            Name = qualifiedName,
            Old = hadValue ? previous : null,
            New = value
        };
        await DispatchCore(
            new Event(qualifiedName, Kind.ChangeEvent, change, qualifiedName),
            deferActivityProducer: ReferenceEquals(callingContext.ActivityProducer, this));
        }
    }

    internal static bool IsCompatibleAttributeValue(Type expected, object value) =>
        expected.IsInstanceOfType(value)
        || (expected == typeof(double)
            && value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal);

    public object? CallOperation(Context context, string operationName, params object?[] args)
    {
        if (!IsStarted)
        {
            throw new HsmRuntimeException("operation requires a started HSM");
        }

        if (string.IsNullOrWhiteSpace(operationName))
        {
            throw new InvalidOperationSignatureException(operationName);
        }

        var qualifiedName = QualifyOperation(operationName);
        if (!_model.Operations.TryGetValue(qualifiedName, out var operation))
        {
            throw new MissingOperationException(qualifiedName);
        }

        if (!TryAcquireActivityLease(
                context,
                out var activityProducer,
                out var activityGeneration,
                out var activityLease))
        {
            return null;
        }
        var leaseTransferred = false;
        try
        {

        var eventData = new CallData
        {
            Name = qualifiedName,
            Args = args
        };
        bool ownsProcessing;
        bool calledByProcessor;
        lock (_gate)
        {
            ownsProcessing = !_processing;
            calledByProcessor = _processingThreadId == Environment.CurrentManagedThreadId
                || ReferenceEquals(CurrentProducer, this) && ActivityProducer.Value is null;
        }

        if (ownsProcessing)
        {
            BeginInlineProcessing();
        }

        var lifecycleVersion = _lifecycleVersion;
        object? result;
        try
        {
            var previousExecution = PushExecution(this, operation.ResolutionScope, depthDelta: 1);
            try
            {
                result = InvokeOperation(operation, context, args, eventData);
            }
            finally
            {
                RestoreExecution(previousExecution);
            }
            var callEvent = new Event(qualifiedName, Kind.CallEvent, eventData, qualifiedName);
            if (IsAsyncOperationResult(result))
            {
                leaseTransferred = true;
                return CompleteAsyncOperation(
                    result!,
                    callEvent,
                    lifecycleVersion,
                    activityProducer,
                    activityGeneration,
                    ownsProcessing,
                    calledByProcessor,
                    activityLease);
            }
            EnqueueCallEvent(
                    callEvent,
                    lifecycleVersion,
                    activityProducer,
                    activityGeneration,
                    waitForProcessing: !ownsProcessing && !calledByProcessor,
                    deferGenerated: calledByProcessor)
                .GetAwaiter().GetResult();
        }
        catch
        {
            if (ownsProcessing)
            {
                ProcessQueueWorker();
            }

            throw;
        }

        if (ownsProcessing)
        {
            ProcessQueueWorker();
        }

        return result;
        }
        finally
        {
            if (!leaseTransferred) activityLease?.Dispose();
        }
    }

    private static bool IsAsyncOperationResult(object? result)
    {
        if (result is Task or ValueTask)
        {
            return true;
        }

        var type = result?.GetType();
        return type is not null
            && type.IsGenericType
            && type.GetGenericTypeDefinition() == typeof(ValueTask<>);
    }

    private async Task<object?> CompleteAsyncOperation(
        object result,
        Event callEvent,
        int lifecycleVersion,
        RuntimeEngine? activityProducer,
        int? activityGeneration,
        bool ownsProcessing,
        bool calledByProcessor,
        GenerationLease? activityLease)
    {
        try
        {
            var value = await AwaitOperationResult(result).ConfigureAwait(false);
            await EnqueueCallEvent(
                    callEvent,
                    lifecycleVersion,
                    activityProducer,
                    activityGeneration,
                    waitForProcessing: !ownsProcessing && !calledByProcessor,
                    deferGenerated: calledByProcessor)
                .ConfigureAwait(false);
            return value;
        }
        finally
        {
            activityLease?.Dispose();
            if (ownsProcessing)
            {
                ProcessQueueWorker();
            }
        }
    }

    private static async Task<object?> AwaitOperationResult(object result)
    {
        if (result is Task task)
        {
            await task.ConfigureAwait(false);
            return task.GetType().IsGenericType
                ? task.GetType().GetProperty("Result")?.GetValue(task)
                : null;
        }
        if (result is ValueTask valueTask)
        {
            await valueTask.ConfigureAwait(false);
            return null;
        }

        var asTask = result.GetType().GetMethod("AsTask", Type.EmptyTypes)
            ?? throw new InvalidOperationSignatureException("operation result");
        var genericTask = (Task)asTask.Invoke(result, null)!;
        await genericTask.ConfigureAwait(false);
        return genericTask.GetType().GetProperty("Result")?.GetValue(genericTask);
    }

    private Task EnqueueCallEvent(
        Event callEvent,
        int lifecycleVersion,
        RuntimeEngine? activityProducer,
        int? activityGeneration,
        bool waitForProcessing,
        bool deferGenerated)
    {
        var startProcessor = false;
        Task processingTask;
        lock (_gate)
        {
            if (lifecycleVersion != _lifecycleVersion
                || !IsStarted
                || activityProducer is not null
                    && activityGeneration is int generation
                    && !activityProducer.IsCurrentActivityGeneration(generation))
            {
                return waitForProcessing
                    ? _processingCompletion?.Task ?? Task.CompletedTask
                    : Task.CompletedTask;
            }

            if (deferGenerated && _processing)
            {
                _generatedCalls.Add(new GeneratedCall(
                    callEvent,
                    lifecycleVersion,
                    activityProducer,
                    activityGeneration));
                return Task.CompletedTask;
            }

            var error = _queue.Push(Context, new PendingEvent(callEvent, activityProducer, activityGeneration));
            if (error is not null)
            {
                _queue.Push(Context, new PendingEvent(new ErrorEvent(error), activityProducer, activityGeneration));
            }
            Notify(_observers?.Dispatched, callEvent.Name);

            if (_processing)
            {
                processingTask = _processingCompletion?.Task ?? Task.CompletedTask;
            }
            else
            {
                _processing = true;
                _processingCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                processingTask = _processingCompletion.Task;
                startProcessor = true;
            }
        }

        if (startProcessor)
        {
            _ = Task.Run(ProcessQueueWorker);
        }
        return waitForProcessing ? processingTask : Task.CompletedTask;
    }

    internal object? InvokeOperationReference(Context context, string operationName, Event @event)
    {
        var qualifiedName = QualifyOperation(operationName);
        if (!_model.Operations.TryGetValue(qualifiedName, out var operation))
        {
            throw new MissingOperationException(qualifiedName);
        }

        var callData = new CallData { Name = qualifiedName, Args = [@event] };
        var previousExecution = PushExecution(CurrentProducer, operation.ResolutionScope, depthDelta: 1);
        try
        {
            return InvokeOperation(operation, context, [@event], callData);
        }
        finally
        {
            RestoreExecution(previousExecution);
        }
    }

    public Snapshot TakeSnapshot()
    {
        if (!IsStarted)
        {
            throw new HsmRuntimeException("take snapshot requires a started HSM");
        }

        ReadOnlyDictionary<string, object?> attributes;
        int queueLen;
        string currentState;

        lock (_gate)
        {
            attributes = new ReadOnlyDictionary<string, object?>(_attributes.ToDictionary(
                pair => pair.Key,
                pair => Runtime.CopySnapshotValue(pair.Value),
                StringComparer.Ordinal));
            var (count, _) = _queue.Len(Context);
            queueLen = count + _deferred.Count;
            currentState = _currentState.QualifiedName;
        }

        var events = new List<EventSnapshot>();
        if (_model.TransitionMap.TryGetValue(currentState, out var buckets))
        {
            foreach (var (eventName, transitions) in buckets)
            {
                if (!_model.Events.TryGetValue(eventName, out var eventDefinition))
                {
                    continue;
                }

                foreach (var transition in transitions)
                {
                    events.Add(new EventSnapshot
                    {
                        Name = eventName,
                        Kind = eventDefinition.Kind,
                        Target = transition.TargetQualifiedName,
                        Guard = transition.Guard is not null,
                        Schema = Runtime.CopySnapshotValue(eventDefinition.Schema)
                    });
                }
            }
        }

        return new Snapshot
        {
            ID = ID,
            QualifiedName = QualifiedName,
            State = currentState,
            Attributes = attributes,
            QueueLen = queueLen,
            Events = events.AsReadOnly()
        };
    }

    public Task AfterProcess(Event? @event = null) =>
        @event is null
            ? RegisterWaiter(null, registry => registry.Cycles, null)
            : RegisterWaiter(@event.Name, null, registry => registry.Processed);
    public Task AfterIdle()
    {
        lock (_gate)
        {
            return _processingCompletion?.Task ?? Task.CompletedTask;
        }
    }
    public Task AfterDispatch(Event @event) => RegisterWaiter(@event.Name, null, registry => registry.Dispatched);
    public Task AfterEntry(string statePath) => RegisterWaiter(PathUtil.Join(statePath), null, registry => registry.Entered);
    public Task AfterExit(string statePath) => RegisterWaiter(PathUtil.Join(statePath), null, registry => registry.Exited);
    public Task AfterExecuted(string statePath) => RegisterWaiter(PathUtil.Join(statePath), null, registry => registry.Executed);

    private Task RegisterWaiter(
        string? key,
        Func<ObserverRegistry, List<TaskCompletionSource<bool>>?>? listSelector,
        Func<ObserverRegistry, Dictionary<string, List<TaskCompletionSource<bool>>>>? dictionarySelector)
    {
        lock (_gate)
        {
            _observers ??= new ObserverRegistry();
            var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (listSelector is not null)
            {
                listSelector(_observers)!.Add(waiter);
            }
            else if (dictionarySelector is not null && key is not null)
            {
                var dictionary = dictionarySelector(_observers);
                if (!dictionary.TryGetValue(key, out var waiters))
                {
                    waiters = new List<TaskCompletionSource<bool>>();
                    dictionary[key] = waiters;
                }

                waiters.Add(waiter);
            }

            return waiter.Task;
        }
    }

    private void ResetAttributes()
    {
        _attributes.Clear();
        foreach (var attribute in _model.Attributes.Values)
        {
            _attributes[attribute.QualifiedName] = attribute.HasDefault ? attribute.DefaultValue : null;
        }
    }

    private bool IsKnownAttribute(string qualifiedName)
    {
        if (_model.Attributes.ContainsKey(qualifiedName))
        {
            return true;
        }

        return _model.Events.TryGetValue(qualifiedName, out var @event)
               && @event.Kind == Kind.ChangeEvent;
    }

    private void CancelScopes()
    {
        foreach (var scope in _activeScopes.Values)
        {
            foreach (var activity in scope.Activities)
            {
                activity.Cancel();
                activity.Dispose();
            }
            scope.Cancellation.Cancel();
            scope.Cancellation.Dispose();
        }

        _activeScopes.Clear();
    }

    internal bool IsCurrentActivityGeneration(int generation) => generation == _lifecycleVersion;

    internal GenerationLease? TryAcquireGenerationLease(int generation)
    {
        var previous = ActiveGenerationLease.Value;
        if (previous is not null
            && ReferenceEquals(previous.Owner, this)
            && previous.Generation == generation
            && previous.IsActive)
        {
            var nested = new GenerationLease(this, generation, previous);
            ActiveGenerationLease.Value = nested;
            return nested;
        }

        lock (_generationGate)
        {
            if (_generationRetiring || generation != _lifecycleVersion)
            {
                return null;
            }

            _activeGenerationLeases++;
            var lease = new GenerationLease(this, generation, previous);
            ActiveGenerationLease.Value = lease;
            return lease;
        }
    }

    private void ReleaseGenerationLease(GenerationLease lease)
    {
        lock (_generationGate)
        {
            if (!lease.IsActive) return;
            lease.Retire();
            _activeGenerationLeases--;
            if (_activeGenerationLeases == 0) Monitor.PulseAll(_generationGate);
        }
    }

    private void RetireActivityGeneration()
    {
        lock (_generationGate)
        {
            while (_generationRetiring) Monitor.Wait(_generationGate);
            _generationRetiring = true;

            var current = ActiveGenerationLease.Value;
            if (current is not null
                && ReferenceEquals(current.Owner, this)
                && current.Generation == _lifecycleVersion
                && current.IsActive)
            {
                current.Retire();
                _activeGenerationLeases--;
            }

            while (_activeGenerationLeases > 0) Monitor.Wait(_generationGate);
            _lifecycleVersion++;
            _generationRetiring = false;
            Monitor.PulseAll(_generationGate);
        }
    }

    private bool MustWaitForGenerationLeases()
    {
        lock (_generationGate)
        {
            var current = ActiveGenerationLease.Value;
            return _activeGenerationLeases > 0
                && !(current is not null
                    && ReferenceEquals(current.Owner, this)
                    && current.Generation == _lifecycleVersion
                    && current.IsActive);
        }
    }

    internal static bool TryAcquireActivityLease(
        Context? context,
        out RuntimeEngine? producer,
        out int? generation,
        out GenerationLease? lease)
    {
        producer = context?.ActivityProducer ?? ActivityProducer.Value;
        generation = context?.ActivityGeneration ?? ActivityGeneration.Value;
        lease = null;
        if (producer is null || generation is not int value) return true;
        lease = producer.TryAcquireGenerationLease(value);
        return lease is not null;
    }

    private void ProcessQueueWorker()
    {
        try
        {
            lock (_gate)
            {
                _processingThreadId = Environment.CurrentManagedThreadId;
            }

            ApplyRequestedLifecycleOperation();
            FlushGeneratedCalls();

            while (true)
            {
                FlushGeneratedCalls();
                PendingEvent? pending;
                int lifecycleVersion;
                lock (_gate)
                {
                    var (nextPending, error) = _queue.Pop(Context);
                    if (error is not null)
                    {
                        _queue.Push(Context, new PendingEvent(new ErrorEvent(error)));
                        continue;
                    }

                    pending = nextPending;
                    lifecycleVersion = _lifecycleVersion;
                    if (pending is null)
                    {
                        if (_generatedCalls.Count > 0)
                        {
                            continue;
                        }

                        var completion = _processingCompletion;
                        _processing = false;
                        _processingCompletion = null;
                        _processingThreadId = null;
                        completion?.TrySetResult(true);
                        return;
                    }
                }

                bool stateChanged;
                GenerationLease? activityLease = null;
                var staleActivity = pending.ActivityProducer is not null
                    && pending.ActivityGeneration is int activityGeneration
                    && (activityLease = pending.ActivityProducer.TryAcquireGenerationLease(activityGeneration)) is null;
                using (activityLease)
                {
                lock (_lifecycleGate)
                {
                    if (lifecycleVersion != _lifecycleVersion || !IsStarted || staleActivity)
                    {
                        pending.Completion.TrySetResult(true);
                        stateChanged = false;
                    }
                    else
                    {
                        try
                        {
                            stateChanged = ProcessEvent(pending);
                        }
                        catch (Exception error)
                        {
                            _instance.OnRuntimeError(error);
                            pending.Completion.TrySetResult(true);
                            lock (_gate)
                            {
                                _queue.Push(Context, new PendingEvent(new ErrorEvent(error)));
                            }

                            stateChanged = false;
                        }
                    }
                }
                }

                ApplyRequestedLifecycleOperation();
                FlushGeneratedCalls();

                if (pending.Completion.Task.IsCompleted)
                {
                    Notify(_observers?.Processed, pending.Event.Name);
                    NotifyCycles();
                }

                if (_skipDeferredReplay)
                {
                    _skipDeferredReplay = false;
                }
                else
                {
                    ReplayDeferredIfEligible();
                }
                lock (_gate)
                {
                    if (_pauseAfterDeferred)
                    {
                        var (queued, queueError) = _queue.Len(Context);
                        if (queueError is null && queued > _deferredQueued.Count)
                        {
                            _pauseAfterDeferred = false;
                            continue;
                        }
                        if (_generatedCalls.Count > 0)
                        {
                            _pauseAfterDeferred = false;
                            continue;
                        }
                        _pauseAfterDeferred = false;
                        var completion = _processingCompletion;
                        _processing = false;
                        _processingCompletion = null;
                        _processingThreadId = null;
                        completion?.TrySetResult(true);
                        return;
                    }
                }
            }
        }
        catch (Exception error)
        {
            TaskCompletionSource<bool>? completion;
            lock (_gate)
            {
                completion = _processingCompletion;
                _processing = false;
                _processingCompletion = null;
                _processingThreadId = null;
            }

            completion?.TrySetResult(true);
            _ = Dispatch(new ErrorEvent(error));
        }
    }

    private void FlushGeneratedCalls()
    {
        lock (_gate)
        {
            foreach (var call in _generatedCalls)
            {
                if (call.LifecycleVersion != _lifecycleVersion
                    || !IsStarted
                    || call.ActivityProducer is not null
                        && call.ActivityGeneration is int generation
                        && !call.ActivityProducer.IsCurrentActivityGeneration(generation))
                {
                    continue;
                }

                var error = _queue.Push(Context, new PendingEvent(
                    call.Event,
                    call.ActivityProducer,
                    call.ActivityGeneration));
                if (error is not null)
                {
                    _queue.Push(Context, new PendingEvent(
                        new ErrorEvent(error),
                        call.ActivityProducer,
                        call.ActivityGeneration));
                }
                Notify(_observers?.Dispatched, call.Event.Name);
            }
            _generatedCalls.Clear();
        }
    }

    private void ReplayDeferredIfEligible()
    {
        lock (_gate)
        {
            if (_deferred.Count == 0)
            {
                return;
            }

            var (queued, queueError) = _queue.Len(Context);
            if (queueError is not null || queued > 0)
            {
                return;
            }

            var pending = _deferred[0];
            if (_model.DeferredMap.TryGetValue(_currentState.QualifiedName, out var deferredSet)
                && deferredSet.Contains(pending.Event.Name))
            {
                return;
            }

            _deferred.RemoveAt(0);
            _deferredOwners.Remove(pending);
            _instance.OnEventRecalled(pending.Event);
            if (!_deferredQueued.Contains(pending))
            {
                var error = _queue.Push(Context, pending);
                if (error is not null)
                {
                    _queue.Push(Context, new PendingEvent(new ErrorEvent(error)));
                }
                _deferredQueued.Add(pending);
            }
        }
    }

    private bool ProcessEvent(PendingEvent pending)
    {
        lock (_gate)
        {
            if (_discardedDeferred.Remove(pending))
            {
                pending.Completion.TrySetResult(true);
                return false;
            }
            if (_deferred.Contains(pending))
            {
                _deferredQueued.Remove(pending);
                var activeState = _currentState.QualifiedName;
                var stillDeferred = _model.DeferredMap.TryGetValue(activeState, out var currentDeferred)
                    && currentDeferred.Contains(pending.Event.Name);
                if (!stillDeferred)
                {
                    _deferred.Remove(pending);
                    _deferredOwners.Remove(pending);
                    _instance.OnEventRecalled(pending.Event);
                }
                else
                {
                    _skipDeferredReplay = true;
                    _pauseAfterDeferred = true;
                    if (_rebuildDeferredQueue)
                    {
                        _rebuildDeferredQueue = false;
                        for (var index = _deferred.Count - 1; index >= 0; index--)
                        {
                            var deferred = _deferred[index];
                            var rebuildError = _queue.Push(Context, deferred);
                            if (rebuildError is not null)
                            {
                                _queue.Push(Context, new PendingEvent(new ErrorEvent(rebuildError)));
                            }
                            _deferredQueued.Add(deferred);
                        }
                        _pauseAfterDeferred = true;
                    }
                    return false;
                }
            }
        }

        var currentQualifiedName = _currentState.QualifiedName;
        var deferDepth = NearestDeferDepth(currentQualifiedName, pending.Event.Name);

        if (TryRunTransitionBucket(currentQualifiedName, pending.Event.Name, pending, deferDepth))
        {
            return true;
        }

        if (_model.DeferredMap.TryGetValue(currentQualifiedName, out var deferredSet) && deferredSet.Contains(pending.Event.Name))
        {
            lock (_gate)
            {
                var hadDeferred = _deferred.Count > 0;
                _deferred.Add(pending);
                _deferredOwners[pending] = NearestDeferPath(currentQualifiedName, pending.Event.Name) ?? currentQualifiedName;
                if (hadDeferred)
                {
                    _rebuildDeferredQueue = true;
                }
                else
                {
                    var error = _queue.Push(Context, pending);
                    if (error is not null)
                    {
                        _queue.Push(Context, new PendingEvent(new ErrorEvent(error)));
                    }
                    _deferredQueued.Add(pending);
                    _pauseAfterDeferred = true;
                }
            }

            _instance.OnEventDeferred(pending.Event);
            return false;
        }

        if (pending.Event.Name != Event.AnyName && TryRunTransitionBucket(currentQualifiedName, Event.AnyName, pending, deferDepth))
        {
            return true;
        }

        pending.Completion.TrySetResult(true);
        return false;
    }

    private int NearestDeferDepth(string currentQualifiedName, string eventName)
    {
        foreach (var path in PathUtil.AncestorChain(currentQualifiedName, _model.QualifiedName))
        {
            if (_model.Resolve<State>(path) is State state && state.DeferredEvents.Contains(eventName))
            {
                return path.Count(character => character == '/');
            }
        }

        return -1;
    }

    private string? NearestDeferPath(string currentQualifiedName, string eventName)
    {
        foreach (var path in PathUtil.AncestorChain(currentQualifiedName, _model.QualifiedName))
        {
            if (_model.Resolve<State>(path) is State state && state.DeferredEvents.Contains(eventName)) return path;
        }
        return null;
    }

    private bool TryRunTransitionBucket(string currentQualifiedName, string eventName, PendingEvent pending, int deferDepth)
    {
        if (!_model.TransitionMap.TryGetValue(currentQualifiedName, out var buckets) ||
            !buckets.TryGetValue(eventName, out var transitions))
        {
            return false;
        }

        foreach (var transition in transitions)
        {
            if (!transition.Paths.ContainsKey(currentQualifiedName))
            {
                continue;
            }

            if (transition.OwnerQualifiedNameInternal != _model.QualifiedName
                && deferDepth > transition.OwnerQualifiedNameInternal.Count(character => character == '/'))
            {
                continue;
            }

            if (transition.Guard is not null)
            {
                if (!EvaluateGuard(transition, pending.Event))
                {
                    continue;
                }
            }

            PrepareDeferredReplay(currentQualifiedName, transition);
            var next = ExecuteTransitionFrom(currentQualifiedName, transition, pending.Event);
            _currentState = next;
            pending.Completion.TrySetResult(true);
            return next.QualifiedName != currentQualifiedName;
        }

        return false;
    }

    private void PrepareDeferredReplay(string currentQualifiedName, Transition transition)
    {
        if (_deferred.Count == 0
            || !transition.Paths.TryGetValue(currentQualifiedName, out var path)
            || path.Exit.Count == 0)
        {
            return;
        }

        var pending = _deferred[0];
        if (!_model.DeferredMap.TryGetValue(currentQualifiedName, out var currentDeferred)
            || !currentDeferred.Contains(pending.Event.Name))
        {
            return;
        }

        if (_model.DeferredMap.TryGetValue(transition.TargetQualifiedName, out var targetDeferred)
            && targetDeferred.Contains(pending.Event.Name))
        {
            return;
        }

        if (_deferredOwners.TryGetValue(pending, out var deferredOwner)
            && path.Exit.Any(exit => _model.Resolve<State>(exit)?.Kind == Kind.Submachine
                && deferredOwner != exit
                && PathUtil.IsDescendantOrSelf(deferredOwner, exit)))
        {
            _deferred.RemoveAt(0);
            _deferredOwners.Remove(pending);
            if (_deferredQueued.Remove(pending)) _discardedDeferred.Add(pending);
            pending.Completion.TrySetResult(true);
            return;
        }

        _deferred.RemoveAt(0);
        _deferredOwners.Remove(pending);
        _instance.OnEventRecalled(pending.Event);
        if (!_deferredQueued.Contains(pending))
        {
            var error = _queue.Push(Context, pending);
            if (error is not null)
            {
                _queue.Push(Context, new PendingEvent(new ErrorEvent(error)));
            }
            _deferredQueued.Add(pending);
        }
    }

    private State ExecuteTransitionFrom(string currentQualifiedName, Transition transition, Event @event)
    {
        var lifecycleVersion = _lifecycleVersion;
        if (!transition.Paths.TryGetValue(currentQualifiedName, out var path))
        {
            return _currentState;
        }

        var targetVertex = _model.Resolve<Vertex>(transition.TargetQualifiedName);
        if (path.Exit.Count > 0 && targetVertex is not HistoryPseudostate)
        {
            RecordHistory(currentQualifiedName);
        }

        foreach (var exiting in path.Exit)
        {
            if (_model.Resolve<State>(exiting) is State state)
            {
                ExitState(state, @event);
                if (lifecycleVersion != _lifecycleVersion) return _currentState;
            }
        }

        foreach (var effect in transition.Effects)
        {
            ExecuteBehavior(effect, @event);
            if (lifecycleVersion != _lifecycleVersion) return _currentState;
        }

        if (transition.TransitionKind == TransitionKind.Internal)
        {
            return _model.Resolve<State>(currentQualifiedName) ?? _currentState;
        }

        foreach (var entering in path.Enter)
        {
            if (_model.Resolve<Vertex>(entering) is not Vertex vertex)
            {
                continue;
            }

            var entered = EnterVertex(vertex, @event, entering == transition.TargetQualifiedName);
            if (lifecycleVersion != _lifecycleVersion) return _currentState;
            if (entering == transition.TargetQualifiedName)
            {
                return entered;
            }
        }

        return _model.Resolve<State>(transition.TargetQualifiedName) ?? _currentState;
    }

    private State EnterVertex(Vertex vertex, Event @event, bool defaultEntry)
    {
        switch (vertex)
        {
            case FinalStateNode finalState:
                Notify(_observers?.Entered, finalState.QualifiedName);
                Dispatch(new CompletionEvent(CompletionEvent.EventName, source: finalState.OwnerQualifiedName))
                    .GetAwaiter().GetResult();

                return finalState;

            case State state:
                return EnterState(state, @event, defaultEntry);

            case ChoicePseudostate choice:
                foreach (var transition in choice.Transitions)
                {
                    if (transition.Guard is not null)
                    {
                        if (!EvaluateGuard(transition, @event))
                        {
                            continue;
                        }
                    }

                    return ExecuteTransitionFrom(choice.QualifiedName, transition, @event);
                }

                return _currentState;

            case HistoryPseudostate history:
                return EnterHistory(history, @event);

            default:
                return _currentState;
        }
    }

    private State EnterState(State state, Event @event, bool defaultEntry)
    {
        var lifecycleVersion = _lifecycleVersion;
        foreach (var behavior in state.EntryBehaviors)
        {
            ExecuteBehavior(behavior, @event);
            if (lifecycleVersion != _lifecycleVersion || !IsStarted) return _currentState;
        }

        Notify(_observers?.Entered, state.QualifiedName);

        var cancellation = new CancellationTokenSource();
        var scope = new StateScope(cancellation);
        _activeScopes[state.QualifiedName] = scope;

        foreach (var activity in state.Activities)
        {
            var activityCancellation = new CancellationTokenSource();
            scope.Activities.Add(activityCancellation);
            ExecuteBehavior(activity, @event, activityCancellation.Token);
        }

        ScheduleTemporalTransitions(state, cancellation.Token);

        if (defaultEntry && !string.IsNullOrWhiteSpace(state.InitialQualifiedName))
        {
            var initial = _model.Resolve<InitialPseudostate>(state.InitialQualifiedName);
            if (initial is not null && initial.Transitions.Count > 0)
            {
                return ExecuteTransitionFrom(state.QualifiedName, initial.Transitions[0], @event);
            }
        }

        return state;
    }

    private State EnterHistory(HistoryPseudostate history, Event @event)
    {
        var parent = history.OwnerQualifiedName;
        if (string.IsNullOrWhiteSpace(parent))
        {
            return _currentState;
        }

        var resolved = history.Kind == Kind.ShallowHistory
            ? _historyShallow.GetValueOrDefault(parent, string.Empty)
            : _historyDeep.GetValueOrDefault(parent, string.Empty);

        if (!string.IsNullOrWhiteSpace(resolved) && !PathUtil.IsDescendantOrSelf(resolved, parent))
        {
            resolved = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(resolved))
        {
            var enterPath = BuildEnterPath(parent, resolved);
            State current = _currentState;
            for (var index = 0; index < enterPath.Count; index++)
            {
                var entering = _model.Resolve<Vertex>(enterPath[index]);
                if (entering is null)
                {
                    return current;
                }

                var defaultEntry = history.Kind == Kind.ShallowHistory && index == enterPath.Count - 1;
                current = EnterVertex(entering, @event, defaultEntry);
            }

            return current;
        }

        foreach (var transition in history.Transitions)
        {
            if (transition.Guard is not null && !EvaluateGuard(transition, @event))
            {
                continue;
            }

            return ExecuteTransitionFrom(history.QualifiedName, transition, @event);
        }

        var parentState = _model.Resolve<State>(parent);
        if (parentState is not null && !string.IsNullOrWhiteSpace(parentState.InitialQualifiedName))
        {
            var initial = _model.Resolve<InitialPseudostate>(parentState.InitialQualifiedName);
            if (initial is not null && initial.Transitions.Count > 0)
            {
                return ExecuteTransitionFrom(parentState.QualifiedName, initial.Transitions[0], @event);
            }
        }

        return _currentState;
    }

    private void ExecuteBehavior(Behavior behavior, Event @event, CancellationToken? cancellationToken = null)
    {
        if (!behavior.Concurrent)
        {
            var synchronousExecution = PushExecution(this, behavior.ResolutionScope ?? behavior.OwnerQualifiedName);
            try
            {
                behavior.Invoke(Context, _instance, @event).GetAwaiter().GetResult();
            }
            finally
            {
                RestoreExecution(synchronousExecution);
            }
            return;
        }

        var token = cancellationToken ?? Context.CancellationToken;
        var activityGeneration = _lifecycleVersion;
        var behaviorContext = Context.CreateLinked(token, this, activityGeneration);
        ValueTask pending;
        var previousProducer = ActivityProducer.Value;
        var previousGeneration = ActivityGeneration.Value;
        var previousExecution = PushExecution(this, behavior.ResolutionScope ?? behavior.OwnerQualifiedName);
        ActivityProducer.Value = this;
        ActivityGeneration.Value = activityGeneration;
        try
        {
            pending = behavior.Invoke(behaviorContext, _instance, @event);
        }
        catch (Exception error)
        {
            ActivityProducer.Value = previousProducer;
            ActivityGeneration.Value = previousGeneration;
            RestoreExecution(previousExecution);
            CompleteConcurrentBehavior(behavior, behaviorContext, token, error);
            return;
        }
        finally
        {
            ActivityProducer.Value = previousProducer;
            ActivityGeneration.Value = previousGeneration;
            RestoreExecution(previousExecution);
        }

        if (pending.IsCompleted)
        {
            Exception? error = null;
            try
            {
                pending.GetAwaiter().GetResult();
            }
            catch (Exception caught)
            {
                error = caught;
            }

            CompleteConcurrentBehavior(behavior, behaviorContext, token, error);
            return;
        }

        _ = AwaitConcurrentBehavior(pending, behavior, behaviorContext, token);
    }

    private bool EvaluateGuard(Transition transition, Event @event)
    {
        if (transition.Guard is null) return true;
        var previousExecution = PushExecution(CurrentProducer, transition.SourceQualifiedName);
        try
        {
            return transition.Guard.Evaluate(Context, _instance, @event);
        }
        finally
        {
            RestoreExecution(previousExecution);
        }
    }

    private async Task AwaitConcurrentBehavior(
        ValueTask pending,
        Behavior behavior,
        Context behaviorContext,
        CancellationToken cancellationToken)
    {
        Exception? error = null;
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (Exception caught)
        {
            error = caught;
        }

        ActivityProducer.Value = null;
        CompleteConcurrentBehavior(behavior, behaviorContext, cancellationToken, error);
    }

    private void CompleteConcurrentBehavior(
        Behavior behavior,
        Context behaviorContext,
        CancellationToken cancellationToken,
        Exception? error)
    {
        try
        {
            if (error is not null && !cancellationToken.IsCancellationRequested)
            {
                Dispatch(new ErrorEvent(error)).GetAwaiter().GetResult();
            }
        }
        finally
        {
            NotifyExecuted(behavior.QualifiedName);
            NotifyExecuted(behavior.OwnerQualifiedName);
            behaviorContext.Dispose();
            StartQueuedWork();
        }
    }

    private void StartQueuedWork()
    {
        lock (_gate)
        {
            if (_processing || !IsStarted || Context.IsDone)
            {
                return;
            }

            _processing = true;
            _processingCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _ = Task.Run(ProcessQueueWorker);
    }

    private void ScheduleTemporalTransitions(State state, CancellationToken cancellationToken)
    {
        var transitions = _model.Members.Values
            .OfType<Transition>()
            .Where(candidate => candidate.SourceQualifiedName == state.QualifiedName
                && candidate.TemporalDefinitions.Count > 0)
            .ToArray();
        if (transitions.Length == 0)
        {
            return;
        }

        var previousExecution = PushExecution(CurrentProducer, state.QualifiedName);
        try
        {
            foreach (var transition in transitions)
            {
                foreach (var temporal in transition.TemporalDefinitions)
                {
                    var temporalEvent = new Event(temporal.EventName, temporal.EventKind);
                    switch (temporal.Kind)
                    {
                        case TemporalKind.After:
                            ScheduleAfter(temporal, temporalEvent, cancellationToken);
                            break;
                        case TemporalKind.At:
                            ScheduleAt(temporal, temporalEvent, cancellationToken);
                            break;
                        case TemporalKind.Every:
                            ScheduleEvery(temporal, temporalEvent, cancellationToken);
                            break;
                        case TemporalKind.When:
                            ScheduleWhen(temporal, temporalEvent, cancellationToken);
                            break;
                    }
                }
            }
        }
        finally
        {
            RestoreExecution(previousExecution);
        }
    }

    private void ScheduleAfter(TemporalDefinition temporal, Event @event, CancellationToken cancellationToken)
    {
        if (temporal.Duration is null)
        {
            return;
        }

        TimeSpan duration;
        try
        {
            duration = temporal.Duration(Context, _instance, @event);
        }
        catch (Exception error)
        {
            Dispatch(new ErrorEvent(error)).GetAwaiter().GetResult();
            return;
        }
        if (duration < TimeSpan.Zero)
        {
            return;
        }

        _ = Run();
        async Task Run()
        {
            try
            {
                await Clock.Delay(duration, cancellationToken).ConfigureAwait(false);
                if (!cancellationToken.IsCancellationRequested)
                {
                    await Dispatch(@event).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    await Dispatch(new ErrorEvent(error)).ConfigureAwait(false);
                }
            }
        }
    }

    private void ScheduleAt(TemporalDefinition temporal, Event @event, CancellationToken cancellationToken)
    {
        if (temporal.Time is null)
        {
            return;
        }

        TimeSpan due;
        try
        {
            due = temporal.Time(Context, _instance, @event) - Clock.Now();
        }
        catch (Exception error)
        {
            Dispatch(new ErrorEvent(error)).GetAwaiter().GetResult();
            return;
        }
        if (due < TimeSpan.Zero)
        {
            due = TimeSpan.Zero;
        }

        _ = Run();
        async Task Run()
        {
            try
            {
                await Clock.Delay(due, cancellationToken).ConfigureAwait(false);
                if (!cancellationToken.IsCancellationRequested)
                {
                    await Dispatch(@event).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    await Dispatch(new ErrorEvent(error)).ConfigureAwait(false);
                }
            }
        }
    }

    private void ScheduleEvery(TemporalDefinition temporal, Event @event, CancellationToken cancellationToken)
    {
        if (temporal.Duration is null)
        {
            return;
        }

        _ = Run();
        async Task Run()
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var duration = temporal.Duration(Context, _instance, @event);
                    if (duration < TimeSpan.Zero)
                    {
                        return;
                    }

                    await Clock.Delay(duration, cancellationToken).ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    await Dispatch(@event).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    await Dispatch(new ErrorEvent(error)).ConfigureAwait(false);
                }
            }
        }
    }

    private void ScheduleWhen(TemporalDefinition temporal, Event @event, CancellationToken cancellationToken)
    {
        if (temporal.Condition is null)
        {
            return;
        }

        var conditionContext = Context.CreateLinked(cancellationToken);
        _ = Task.Run(async () =>
        {
            try
            {
                await temporal.Condition(conditionContext, _instance, @event, cancellationToken).ConfigureAwait(false);
                if (!cancellationToken.IsCancellationRequested)
                {
                    await Dispatch(@event).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    await Dispatch(new ErrorEvent(error)).ConfigureAwait(false);
                }
            }
            finally
            {
                conditionContext.Dispose();
            }
        });
    }

    private void ExitToAncestor(string currentQualifiedName, string ancestorQualifiedName, Event @event)
    {
        var current = _model.Resolve<State>(currentQualifiedName);
        while (current is not null && current.QualifiedName != ancestorQualifiedName && current.QualifiedName != _model.QualifiedName)
        {
            ExitState(current, @event);
            current = _model.Resolve<State>(PathUtil.Parent(current.QualifiedName));
        }
    }

    private void ExitState(State state, Event @event)
    {
        if (_activeScopes.Remove(state.QualifiedName, out var scope))
        {
            foreach (var activity in scope.Activities)
            {
                activity.Cancel();
                activity.Dispose();
            }
            scope.Cancellation.Cancel();
            scope.Cancellation.Dispose();
        }

        foreach (var behavior in state.ExitBehaviors)
        {
            ExecuteBehavior(behavior, @event);
        }

        Notify(_observers?.Exited, state.QualifiedName);
    }

    private void RecordHistory(string stateQualifiedName)
    {
        var child = stateQualifiedName;
        var parent = PathUtil.Parent(child);
        while (!string.IsNullOrWhiteSpace(parent) && parent != "/")
        {
            if (_model.Resolve<State>(parent) is not null)
            {
                _historyDeep[parent] = stateQualifiedName;
                _historyShallow[parent] = child;
            }

            if (parent == _model.QualifiedName)
            {
                return;
            }

            child = parent;
            parent = PathUtil.Parent(parent);
        }
    }

    private static List<string> BuildEnterPath(string parentQualifiedName, string targetQualifiedName)
    {
        var result = new List<string>();
        var current = targetQualifiedName;
        while (!string.IsNullOrWhiteSpace(current) && current != parentQualifiedName)
        {
            result.Insert(0, current);
            current = PathUtil.Parent(current);
        }

        return result;
    }

    private void NotifyExecuted(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        Notify(_observers?.Executed, key);
    }

    private void Notify(Dictionary<string, List<TaskCompletionSource<bool>>>? store, string key)
    {
        if (store is null)
        {
            return;
        }

        List<TaskCompletionSource<bool>>? waiters = null;
        lock (_gate)
        {
            if (store.TryGetValue(key, out waiters))
            {
                store.Remove(key);
            }
        }

        if (waiters is null)
        {
            return;
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetResult(true);
        }
    }

    private void NotifyCycles()
    {
        List<TaskCompletionSource<bool>>? waiters = null;
        lock (_gate)
        {
            if (_observers is null || _observers.Cycles.Count == 0)
            {
                return;
            }

            waiters = _observers.Cycles.ToList();
            _observers.Cycles.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetResult(true);
        }
    }

    private object? InvokeOperation(
        OperationDefinition operation,
        Context context,
        object?[] args,
        CallData callData)
    {
        var callback = operation.Callback;
        if (callback is null)
        {
            foreach (var method in _instance.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                         .Where(method => method.Name == operation.Name))
            {
                foreach (var useContext in new[] { true, false })
                {
                    if (!TryBuildCallArguments(
                            method.GetParameters(),
                            useContext,
                            useInstance: false,
                            context,
                            args,
                            out var instanceArgs))
                    {
                        continue;
                    }

                    try
                    {
                        return method.Invoke(_instance, instanceArgs);
                    }
                    catch (TargetInvocationException exception) when (exception.InnerException is not null)
                    {
                        throw exception.InnerException;
                    }
                }
            }

            throw new MissingOperationException(operation.QualifiedName);
        }

        var parameters = callback.Method.GetParameters();
        var candidates = new[]
        {
            (useContext: true, useInstance: true),
            (useContext: true, useInstance: false),
            (useContext: false, useInstance: true),
            (useContext: false, useInstance: false)
        };

        foreach (var candidate in candidates)
        {
            if (!TryBuildCallArguments(parameters, candidate.useContext, candidate.useInstance, context, args, out var callArgs))
            {
                continue;
            }

            try
            {
                var result = callback.DynamicInvoke(callArgs);
                if (result is Exception error)
                {
                    throw error;
                }

                return result;
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                throw exception.InnerException;
            }
        }

        foreach (var candidate in candidates)
        {
            if (!TryBuildCallArguments(
                    parameters,
                    candidate.useContext,
                    candidate.useInstance,
                    context,
                    [callData],
                    out var callArgs))
            {
                continue;
            }

            try
            {
                return callback.DynamicInvoke(callArgs);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                throw exception.InnerException;
            }
        }

        throw new InvalidOperationSignatureException(operation.QualifiedName);
    }

    private bool TryBuildCallArguments(
        ParameterInfo[] parameters,
        bool useContext,
        bool useInstance,
        Context context,
        object?[] args,
        out object?[] callArgs)
    {
        callArgs = Array.Empty<object?>();
        var values = new List<object?>();
        var parameterIndex = 0;

        if (useContext)
        {
            if (parameterIndex >= parameters.Length || !TryConvertArgument(context, parameters[parameterIndex].ParameterType, out var convertedContext))
            {
                return false;
            }

            values.Add(convertedContext);
            parameterIndex++;
        }

        if (useInstance)
        {
            if (parameterIndex >= parameters.Length || !TryConvertArgument(_instance, parameters[parameterIndex].ParameterType, out var convertedInstance))
            {
                return false;
            }

            values.Add(convertedInstance);
            parameterIndex++;
        }

        var variadic = parameters.Length > 0 && parameters[^1].GetCustomAttribute<ParamArrayAttribute>() is not null;
        var fixedRemaining = variadic ? parameters.Length - parameterIndex - 1 : parameters.Length - parameterIndex;

        if ((!variadic && fixedRemaining != args.Length) || (variadic && args.Length < fixedRemaining))
        {
            return false;
        }

        for (var i = 0; i < fixedRemaining; i++)
        {
            if (!TryConvertArgument(args[i], parameters[parameterIndex + i].ParameterType, out var converted))
            {
                return false;
            }

            values.Add(converted);
        }

        if (variadic)
        {
            var elementType = parameters[^1].ParameterType.GetElementType()!;
            var variadicCount = args.Length - fixedRemaining;
            var array = Array.CreateInstance(elementType, variadicCount);
            for (var i = 0; i < variadicCount; i++)
            {
                if (!TryConvertArgument(args[fixedRemaining + i], elementType, out var converted))
                {
                    return false;
                }

                array.SetValue(converted, i);
            }

            values.Add(array);
        }

        callArgs = values.ToArray();
        return true;
    }

    private static bool TryConvertArgument(object? value, Type targetType, out object? converted)
    {
        converted = null;
        if (value is null)
        {
            if (!targetType.IsValueType || Nullable.GetUnderlyingType(targetType) is not null)
            {
                return true;
            }

            return false;
        }

        var sourceType = value.GetType();
        if (targetType.IsAssignableFrom(sourceType))
        {
            converted = value;
            return true;
        }

        var nonNullable = Nullable.GetUnderlyingType(targetType) ?? targetType;
        try
        {
            converted = Convert.ChangeType(value, nonNullable);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string QualifyAttribute(string attributeName) =>
        QualifyScoped(_model.Attributes.Keys, attributeName);

    private string QualifyOperation(string operationName) =>
        QualifyScoped(_model.Operations.Keys, operationName);

    private string QualifyScoped(IEnumerable<string> names, string name)
    {
        if (name.StartsWith("/", StringComparison.Ordinal)) return PathUtil.Join(name);
        var known = names as ICollection<string> ?? names.ToArray();
        foreach (var scope in PathUtil.AncestorChain(CurrentResolutionScope ?? _currentState.QualifiedName, _model.QualifiedName))
        {
            var candidate = PathUtil.Join(scope, name);
            if (known.Contains(candidate)) return candidate;
        }
        return PathUtil.Join(_model.QualifiedName, name);
    }
}
