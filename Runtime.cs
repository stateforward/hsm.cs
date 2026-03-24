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
        public IInstance? PrimaryInstance { get; set; }
    }

    private readonly SharedState _shared;
    private readonly CancellationTokenSource _source;

    public Context() : this(new SharedState(), new CancellationTokenSource())
    {
    }

    private Context(SharedState shared, CancellationTokenSource source)
    {
        _shared = shared;
        _source = source;
    }

    public CancellationToken CancellationToken => _source.Token;
    public bool IsDone => _source.IsCancellationRequested;

    public void Cancel()
    {
        if (!_source.IsCancellationRequested)
        {
            _source.Cancel();
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

    internal Context CreateLinked(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(
            _source.Token,
            cancellationToken.CanBeCanceled ? cancellationToken : CancellationToken.None);
        return new Context(_shared, source);
    }

    internal void Dispose() => _source.Dispose();
}

public class Clock
{
    public Clock(
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        DelayAsync = delayAsync;
        UtcNow = utcNow;
    }

    public static Clock System { get; } = new();
    public Func<TimeSpan, CancellationToken, Task>? DelayAsync { get; init; }
    public Func<DateTimeOffset>? UtcNow { get; init; }

    internal Task Delay(TimeSpan due, CancellationToken cancellationToken)
    {
        var normalized = due < TimeSpan.Zero ? TimeSpan.Zero : due;
        return (DelayAsync ?? Task.Delay)(normalized, cancellationToken);
    }

    internal DateTimeOffset Now() => (UtcNow ?? (() => DateTimeOffset.UtcNow))();
}

public sealed class Config
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public object? Data { get; init; }
    public TimeSpan ActivityTimeout { get; init; } = TimeSpan.FromMilliseconds(1);
    public Clock? Clock { get; init; }
}

public abstract class Instance : IInstance
{
    internal RuntimeEngine? Engine { get; set; }
    private Context? _detachedContext;

    public virtual string State => Engine?.State ?? string.Empty;
    public virtual Context Context => Engine?.Context ?? (_detachedContext ??= new Context());
    public virtual Task Dispatch(Event @event) => Engine?.Dispatch(@event) ?? Task.CompletedTask;
    public virtual Task Stop() => Engine?.StopAsync() ?? Task.CompletedTask;
    public virtual Task Restart(object? data = null) => Engine?.RestartAsync(data) ?? Task.CompletedTask;
}

public sealed class Group : Instance
{
    private readonly IReadOnlyList<IInstance> _instances;
    private readonly Context _context;
    private readonly string _id = $"group_{Guid.NewGuid():N}";

    public Group(params IInstance[] instances) : this((IEnumerable<IInstance>)instances)
    {
    }

    public Group(IEnumerable<IInstance> instances)
    {
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
    public override Task Dispatch(Event @event) => Task.WhenAll(_instances.Select(instance => instance.Dispatch(@event)));
    public override Task Stop() => Task.WhenAll(_instances.Select(instance => instance.Stop()));
    public override Task Restart(object? data = null) => Task.WhenAll(_instances.Select(instance => instance.Restart(data)));

    internal Snapshot TakeSnapshot() => new()
    {
        ID = _id,
        QualifiedName = string.Empty,
        State = string.Empty,
        Attributes = new Dictionary<string, object?>(),
        QueueLen = 0,
        Events = Array.Empty<EventSnapshot>()
    };
}

internal static class Runtime
{
    public static Clock DefaultClock { get; set; } = Clock.System;

    public static TInstance New<TInstance>(TInstance instance, Model model, Config? config = null)
        where TInstance : Instance
    {
        if (instance.Engine is not null)
        {
            throw new AlreadyStartedException();
        }

        instance.Engine = new RuntimeEngine(new Context(), instance, model, config ?? new Config());
        return instance;
    }

    public static TInstance Start<TInstance>(Context context, TInstance instance, TInstance? _ = null)
        where TInstance : Instance => Start(context, instance, data: null);

    public static TInstance Start<TInstance>(Context context, TInstance instance, object? data = null)
        where TInstance : Instance
    {
        if (instance.Engine is null)
        {
            throw new ValidationException("instance has no bound model");
        }

        if (instance.Engine.IsStarted)
        {
            throw new AlreadyStartedException();
        }

        instance.Engine.BindContext(context);
        context.Register(instance);
        instance.Engine.Start(data);
        return instance;
    }

    public static TInstance Start<TInstance>(Context context, TInstance instance, Model model, Config? config = null)
        where TInstance : Instance
    {
        if (instance.Engine is not null)
        {
            throw new AlreadyStartedException();
        }

        New(instance, model, config);
        return Start(context, instance, config?.Data);
    }

    public static TInstance Started<TInstance>(Context context, TInstance instance, Model model, Config? config = null)
        where TInstance : Instance => Start(context, instance, model, config);

    public static Task Dispatch(Context context, IInstance? instance, Event @event)
    {
        if (instance is not null)
        {
            return instance.Dispatch(@event);
        }

        var resolved = FromContext(context);
        return resolved?.Dispatch(@event) ?? Task.CompletedTask;
    }

    public static Task Stop(Context context, IInstance instance) => instance.Stop();
    public static Task Restart(Context context, IInstance instance, object? data = null) => instance.Restart(data);
    public static Task DispatchAll(Context context, Event @event) => Task.WhenAll(context.SnapshotInstances().Select(instance => instance.Dispatch(@event)));

    public static Task DispatchTo(Context context, Event @event, params string[] idPatterns)
    {
        var targets = context.SnapshotInstances()
            .Where(instance => idPatterns.Length == 0 || Match(ID(instance), idPatterns))
            .ToArray();
        return targets.Length == 0
            ? Task.CompletedTask
            : Task.WhenAll(targets.Select(instance => instance.Dispatch(@event)));
    }

    public static T? Get<T>(Context context, IInstance? instance, string attributeName)
    {
        instance ??= FromContext(context);
        var value = instance switch
        {
            Group group when group.Instances.Count > 0 => Get<object?>(context, group.Instances[0], attributeName),
            Instance concrete when concrete.Engine is not null => concrete.Engine.GetAttribute(attributeName),
            _ => null
        };

        if (value is null)
        {
            return default;
        }

        return value is T typed ? typed : default;
    }

    public static Task Set(Context context, IInstance? instance, string attributeName, object? value)
    {
        instance ??= FromContext(context);
        return instance switch
        {
            Group group => Task.WhenAll(group.Instances.Select(child => Set(context, child, attributeName, value))),
            Instance concrete when concrete.Engine is not null => concrete.Engine.SetAttributeAsync(attributeName, value),
            _ => Task.CompletedTask
        };
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
            _ => new Snapshot()
        };

    public static Task AfterProcess(Context context, IInstance instance, Event? @event = null) =>
        instance is Instance concrete && concrete.Engine is not null
            ? concrete.Engine.AfterProcess(@event)
            : Task.CompletedTask;

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
    private sealed class PendingEvent
    {
        public PendingEvent(Event @event)
        {
            Event = @event;
            Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Event Event { get; }
        public TaskCompletionSource<bool> Completion { get; }
    }

    private sealed class StateScope
    {
        public StateScope(CancellationTokenSource cancellation)
        {
            Cancellation = cancellation;
        }

        public CancellationTokenSource Cancellation { get; }
    }

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
    private readonly Queue<PendingEvent> _regularQueue = new();
    private readonly Stack<PendingEvent> _priorityQueue = new();
    private readonly List<PendingEvent> _deferred = new();
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
    private State _currentState;

    public RuntimeEngine(Context context, Instance instance, Model model, Config config)
    {
        Context = context;
        _instance = instance;
        _model = model;
        _config = config;
        _currentState = model;

        QualifiedName = string.IsNullOrWhiteSpace(config.Name)
            ? model.QualifiedName
            : config.Name!.StartsWith("/", StringComparison.Ordinal)
                ? PathUtil.Join(config.Name)
                : PathUtil.Join("/", config.Name);

        var simpleName = PathUtil.Name(QualifiedName);
        ID = string.IsNullOrWhiteSpace(config.Id)
            ? $"{simpleName}_{Guid.NewGuid():N}"
            : config.Id!;

        ResetAttributes();
    }

    public Context Context { get; private set; }
    public string ID { get; }
    public string QualifiedName { get; }
    public string State => _currentState.QualifiedName;
    public bool IsStarted { get; private set; }

    private Clock Clock => _config.Clock ?? Runtime.DefaultClock;

    public void BindContext(Context context) => Context = context;

    public void Start(object? data)
    {
        if (IsStarted)
        {
            throw new AlreadyStartedException();
        }

        IsStarted = true;
        _currentState = EnterVertex(_model, new InitialEvent(data), true);
    }

    public Task Dispatch(Event @event)
    {
        if (!IsStarted || Context.IsDone)
        {
            return Task.CompletedTask;
        }

        PendingEvent pending;
        var startProcessor = false;
        Task processingTask;
        lock (_gate)
        {
            pending = new PendingEvent(@event);
            Enqueue(pending);
            Notify(_observers?.Dispatched, @event.Name);
            if (_processing)
            {
                if (_processingThreadId == Environment.CurrentManagedThreadId)
                {
                    return Task.CompletedTask;
                }

                return _processingCompletion?.Task ?? Task.CompletedTask;
            }

            _processing = true;
            _processingCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            processingTask = _processingCompletion.Task;
            startProcessor = true;
        }

        if (startProcessor)
        {
            _ = Task.Run(ProcessQueueWorker);
        }

        return processingTask;
    }

    public Task StopAsync()
    {
        if (!IsStarted)
        {
            return Task.CompletedTask;
        }

        lock (_gate)
        {
            ExitToAncestor(_currentState.QualifiedName, _model.QualifiedName, new CompletionEvent("hsm.final"));
            CancelScopes();
            Context.Cancel();
        }

        return Task.CompletedTask;
    }

    public Task RestartAsync(object? data)
    {
        if (!IsStarted)
        {
            return Task.CompletedTask;
        }

        lock (_gate)
        {
            ExitToAncestor(_currentState.QualifiedName, _model.QualifiedName, new CompletionEvent("hsm.final"));
            CancelScopes();
            _regularQueue.Clear();
            _priorityQueue.Clear();
            _deferred.Clear();
            _historyShallow.Clear();
            _historyDeep.Clear();
            ResetAttributes();
            _currentState = EnterVertex(_model, new InitialEvent(data), true);
        }

        return Task.CompletedTask;
    }

    public object? GetAttribute(string attributeName)
    {
        var qualifiedName = QualifyAttribute(attributeName);
        _attributes.TryGetValue(qualifiedName, out var value);
        return value;
    }

    public Task SetAttributeAsync(string attributeName, object? value)
    {
        if (string.IsNullOrWhiteSpace(attributeName))
        {
            throw new ValidationException("attribute name cannot be empty");
        }

        var qualifiedName = QualifyAttribute(attributeName);
        var hadValue = _attributes.TryGetValue(qualifiedName, out var previous);
        if (hadValue && Equals(previous, value))
        {
            return Task.CompletedTask;
        }

        _attributes[qualifiedName] = value;
        var change = new AttributeChange
        {
            Name = qualifiedName,
            Old = hadValue ? previous : null,
            New = value
        };
        return Dispatch(new Event(qualifiedName, Kind.ChangeEvent, change, qualifiedName));
    }

    public object? CallOperation(Context context, string operationName, params object?[] args)
    {
        if (string.IsNullOrWhiteSpace(operationName))
        {
            throw new InvalidOperationSignatureException(operationName);
        }

        var qualifiedName = QualifyOperation(operationName);
        if (!_model.Operations.TryGetValue(qualifiedName, out var operation))
        {
            throw new MissingOperationException(qualifiedName);
        }

        var eventData = new CallData
        {
            Name = qualifiedName,
            Args = args
        };
        Dispatch(new Event(qualifiedName, Kind.CallEvent, eventData, qualifiedName)).GetAwaiter().GetResult();
        return InvokeOperation(operation, context, args);
    }

    public Snapshot TakeSnapshot()
    {
        Dictionary<string, object?> attributes;
        int queueLen;
        string currentState;

        lock (_gate)
        {
            attributes = new Dictionary<string, object?>(_attributes, StringComparer.Ordinal);
            queueLen = _regularQueue.Count + _priorityQueue.Count + _deferred.Count;
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
                        Schema = eventDefinition.Schema
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
            Events = events
        };
    }

    public Task AfterProcess(Event? @event = null) =>
        @event is null
            ? RegisterWaiter(null, registry => registry.Cycles, null)
            : RegisterWaiter(@event.Name, null, registry => registry.Processed);
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

    private void CancelScopes()
    {
        foreach (var scope in _activeScopes.Values)
        {
            scope.Cancellation.Cancel();
            scope.Cancellation.Dispose();
        }

        _activeScopes.Clear();
    }

    private void Enqueue(PendingEvent pending)
    {
        if (pending.Event.Kind.IsCompletionPriority())
        {
            _priorityQueue.Push(pending);
        }
        else
        {
            _regularQueue.Enqueue(pending);
        }
    }

    private PendingEvent? Dequeue()
    {
        if (_priorityQueue.Count > 0)
        {
            return _priorityQueue.Pop();
        }

        if (_regularQueue.Count == 0)
        {
            return null;
        }

        return _regularQueue.Dequeue();
    }

    private void ProcessQueueWorker()
    {
        try
        {
            lock (_gate)
            {
                _processingThreadId = Environment.CurrentManagedThreadId;
            }

            while (true)
            {
                PendingEvent? pending;
                lock (_gate)
                {
                    pending = Dequeue();
                    if (pending is null)
                    {
                        var completion = _processingCompletion;
                        _processing = false;
                        _processingCompletion = null;
                        _processingThreadId = null;
                        completion?.TrySetResult(true);
                        return;
                    }
                }

                var stateChanged = ProcessEvent(pending);
                if (pending.Completion.Task.IsCompleted)
                {
                    Notify(_observers?.Processed, pending.Event.Name);
                    NotifyCycles();
                }

                if (stateChanged)
                {
                    ReplayDeferred();
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

    private void ReplayDeferred()
    {
        lock (_gate)
        {
            for (var index = 0; index < _deferred.Count; index++)
            {
                _regularQueue.Enqueue(_deferred[index]);
            }

            _deferred.Clear();
        }
    }

    private bool ProcessEvent(PendingEvent pending)
    {
        var currentQualifiedName = _currentState.QualifiedName;

        if (_model.DeferredMap.TryGetValue(currentQualifiedName, out var deferredSet) && deferredSet.Contains(pending.Event.Name))
        {
            lock (_gate)
            {
                _deferred.Add(pending);
            }

            return false;
        }

        if (TryRunTransitionBucket(currentQualifiedName, pending.Event.Name, pending))
        {
            return true;
        }

        if (pending.Event.Name != Event.AnyName && TryRunTransitionBucket(currentQualifiedName, Event.AnyName, pending))
        {
            return true;
        }

        pending.Completion.TrySetResult(true);
        return false;
    }

    private bool TryRunTransitionBucket(string currentQualifiedName, string eventName, PendingEvent pending)
    {
        if (!_model.TransitionMap.TryGetValue(currentQualifiedName, out var buckets) ||
            !buckets.TryGetValue(eventName, out var transitions))
        {
            return false;
        }

        foreach (var transition in transitions)
        {
            if (transition.Guard is not null)
            {
                try
                {
                    if (!transition.Guard.Evaluate(Context, _instance, pending.Event))
                    {
                        continue;
                    }
                }
                catch (Exception error)
                {
                    Dispatch(new ErrorEvent(error)).GetAwaiter().GetResult();
                    continue;
                }
            }

            var next = ExecuteTransitionFrom(currentQualifiedName, transition, pending.Event);
            _currentState = next;
            pending.Completion.TrySetResult(true);
            return next.QualifiedName != currentQualifiedName;
        }

        return false;
    }

    private State ExecuteTransitionFrom(string currentQualifiedName, Transition transition, Event @event)
    {
        if (!transition.Paths.TryGetValue(currentQualifiedName, out var path))
        {
            return _currentState;
        }

        foreach (var exiting in path.Exit)
        {
            if (_model.Resolve<State>(exiting) is State state)
            {
                ExitState(state, @event);
            }
        }

        foreach (var effect in transition.Effects)
        {
            ExecuteBehavior(effect, @event);
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
                RecordHistory(finalState.QualifiedName);
                Notify(_observers?.Entered, finalState.QualifiedName);
                if (finalState.OwnerQualifiedName == _model.QualifiedName)
                {
                    Context.Cancel();
                }

                return finalState;

            case State state:
                return EnterState(state, @event, defaultEntry);

            case ChoicePseudostate choice:
                foreach (var transition in choice.Transitions)
                {
                    if (transition.Guard is not null)
                    {
                        try
                        {
                            if (!transition.Guard.Evaluate(Context, _instance, @event))
                            {
                                continue;
                            }
                        }
                        catch (Exception error)
                        {
                            Dispatch(new ErrorEvent(error)).GetAwaiter().GetResult();
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
        RecordHistory(state.QualifiedName);

        foreach (var behavior in state.EntryBehaviors)
        {
            ExecuteBehavior(behavior, @event);
        }

        Notify(_observers?.Entered, state.QualifiedName);

        var cancellation = new CancellationTokenSource();
        _activeScopes[state.QualifiedName] = new StateScope(cancellation);

        foreach (var activity in state.Activities)
        {
            ExecuteBehavior(activity, @event, cancellation.Token);
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
            if (transition.Guard is not null && !transition.Guard.Evaluate(Context, _instance, @event))
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
            try
            {
                behavior.Invoke(Context, _instance, @event);
            }
            catch (Exception error)
            {
                Dispatch(new ErrorEvent(error)).GetAwaiter().GetResult();
            }

            return;
        }

        var token = cancellationToken ?? Context.CancellationToken;
        var behaviorContext = Context.CreateLinked(token);
        _ = Task.Run(() =>
        {
            try
            {
                behavior.Invoke(behaviorContext, _instance, @event);
            }
            catch (Exception error)
            {
                if (!token.IsCancellationRequested)
                {
                    Dispatch(new ErrorEvent(error)).GetAwaiter().GetResult();
                }
            }
            finally
            {
                NotifyExecuted(behavior.QualifiedName);
                NotifyExecuted(behavior.OwnerQualifiedName);
                behaviorContext.Dispose();
            }
        });
    }

    private void ScheduleTemporalTransitions(State state, CancellationToken cancellationToken)
    {
        foreach (var transition in state.Transitions.Where(candidate => candidate.TemporalDefinitions.Count > 0))
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

    private void ScheduleAfter(TemporalDefinition temporal, Event @event, CancellationToken cancellationToken)
    {
        if (temporal.Duration is null)
        {
            return;
        }

        var duration = temporal.Duration(Context, _instance, @event);
        if (duration < TimeSpan.Zero)
        {
            return;
        }

        _ = Task.Run(async () =>
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
        });
    }

    private void ScheduleAt(TemporalDefinition temporal, Event @event, CancellationToken cancellationToken)
    {
        if (temporal.Time is null)
        {
            return;
        }

        var due = temporal.Time(Context, _instance, @event) - Clock.Now();
        if (due < TimeSpan.Zero)
        {
            due = TimeSpan.Zero;
        }

        _ = Task.Run(async () =>
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
        });
    }

    private void ScheduleEvery(TemporalDefinition temporal, Event @event, CancellationToken cancellationToken)
    {
        if (temporal.Duration is null)
        {
            return;
        }

        _ = Task.Run(async () =>
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
        });
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

    private object? InvokeOperation(OperationDefinition operation, Context context, object?[] args)
    {
        var callback = operation.Callback;
        if (callback is null)
        {
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

    private string QualifyAttribute(string attributeName) => PathUtil.Join(_model.QualifiedName, attributeName);
    private string QualifyOperation(string operationName) => PathUtil.Join(_model.QualifiedName, operationName);
}
