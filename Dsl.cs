namespace Stateforward.Hsm;

public interface IPartial
{
}

internal interface IBuildPartial : IPartial
{
    void Apply(Dsl.ModelBuilder builder);
}

internal static class Dsl
{
    public static Model Define(string name, params IPartial[] partials)
    {
        ValidateName(name, "model");
        var model = new Model(PathUtil.Join("/", name));
        model.Members[model.QualifiedName] = model;

        var builder = new ModelBuilder(model);
        foreach (var partial in partials)
        {
            if (partial is not IBuildPartial buildPartial)
            {
                throw new ValidationException("unsupported partial implementation");
            }

            buildPartial.Apply(builder);
        }

        builder.FinalizeModel();
        return model;
    }

    public static IPartial State(string name, params IPartial[] partials) => new StatePartial(name, partials);
    public static IPartial Final(string name) => new FinalPartial(name);
    public static IPartial ShallowHistory(string name, params IPartial[] partials) => new HistoryPartial(name, Kind.ShallowHistory, partials);
    public static IPartial DeepHistory(string name, params IPartial[] partials) => new HistoryPartial(name, Kind.DeepHistory, partials);
    public static IPartial Choice(string name, params IPartial[] partials) => new ChoicePartial(name, partials);
    public static IPartial Transition(params IPartial[] partials) => new TransitionPartial(partials);
    public static IPartial Initial(params IPartial[] partials) => new InitialPartial(partials);
    public static IPartial Source(string path) => new SourcePartial(path);
    public static IPartial Target(string path) => new TargetPartial(path);
    public static IPartial On(string eventName) => new OnPartial(new Event(eventName));
    public static IPartial On(Event @event) => new OnPartial(@event);
    public static IPartial OnCall(string operationName) => new OnCallPartial(operationName);
    public static IPartial OnSet(string attributeName) => new OnSetPartial(attributeName);
    public static IPartial Defer(params string[] eventNames) => new DeferPartial(eventNames);
    public static IPartial Attribute<T>(string name, T? defaultValue = default) => new AttributePartial(name, defaultValue, true);
    public static IPartial Operation(string name, Delegate callback) => new OperationPartial(name, callback);
    public static IPartial Entry<TInstance>(params Operation<TInstance>[] operations) where TInstance : Instance =>
        new BehaviorPartial(BehaviorTarget.Entry, operations.Select(Wrap).ToArray());
    public static IPartial Exit<TInstance>(params Operation<TInstance>[] operations) where TInstance : Instance =>
        new BehaviorPartial(BehaviorTarget.Exit, operations.Select(Wrap).ToArray());
    public static IPartial Activity<TInstance>(params Operation<TInstance>[] operations) where TInstance : Instance =>
        new BehaviorPartial(BehaviorTarget.Activity, operations.Select(Wrap).ToArray());
    public static IPartial Effect<TInstance>(params Operation<TInstance>[] operations) where TInstance : Instance =>
        new BehaviorPartial(BehaviorTarget.Effect, operations.Select(Wrap).ToArray());
    public static IPartial Guard<TInstance>(Expression<TInstance> predicate) where TInstance : Instance =>
        new GuardPartial(Wrap(predicate), predicate.Method.Name);
    public static IPartial After<TInstance>(DurationProvider<TInstance> duration) where TInstance : Instance =>
        new TemporalPartial(TemporalKind.After, duration.Method.Name, duration: Wrap(duration));
    public static IPartial After(string attributeName) =>
        new TemporalPartial(TemporalKind.After, AttributeTemporalName(attributeName), duration: AttributeDuration(attributeName));
    public static IPartial At<TInstance>(TimeProvider<TInstance> time) where TInstance : Instance =>
        new TemporalPartial(TemporalKind.At, time.Method.Name, time: Wrap(time));
    public static IPartial At(string attributeName) =>
        new TemporalPartial(TemporalKind.At, AttributeTemporalName(attributeName), time: AttributeTime(attributeName));
    public static IPartial Every<TInstance>(DurationProvider<TInstance> duration) where TInstance : Instance =>
        new TemporalPartial(TemporalKind.Every, duration.Method.Name, duration: Wrap(duration));
    public static IPartial Every(string attributeName) =>
        new TemporalPartial(TemporalKind.Every, AttributeTemporalName(attributeName), duration: AttributeDuration(attributeName));
    public static IPartial When(string attributeName) => OnSet(attributeName);
    public static IPartial When<TInstance>(ConditionChannel<TInstance> condition) where TInstance : Instance =>
        new TemporalPartial(TemporalKind.When, condition.Method.Name, condition: Wrap(condition));

    internal sealed class ModelBuilder
    {
        private readonly Stack<NamedElement> _stack = new();
        private readonly List<Action> _finalizers = new();
        private int _sequence;

        public ModelBuilder(Model model)
        {
            Model = model;
            _stack.Push(model);
        }

        public Model Model { get; }
        public NamedElement Current => _stack.Peek();

        public void Push(NamedElement element) => _stack.Push(element);
        public void Pop() => _stack.Pop();

        public T? Find<T>() where T : NamedElement
        {
            foreach (var element in _stack)
            {
                if (element is T match)
                {
                    return match;
                }
            }

            return null;
        }

        public string ScopeQualifiedName()
        {
            var scope = Find<State>() ?? Find<Model>();
            return scope?.QualifiedName ?? Model.QualifiedName;
        }

        public string NextName(string prefix)
        {
            var current = _sequence;
            _sequence++;
            return prefix + "_" + current;
        }

        public void Register(NamedElement element)
        {
            if (Model.Members.ContainsKey(element.QualifiedName))
            {
                throw new ValidationException($"duplicate element '{element.QualifiedName}'");
            }

            Model.Members[element.QualifiedName] = element;
        }

        public void FinalizeLater(Action action) => _finalizers.Add(action);

        public void FinalizeModel()
        {
            foreach (var finalizer in _finalizers)
            {
                finalizer();
            }

            if (Model.InitialQualifiedName is null)
            {
                throw new ValidationException("initial state is required for state machine");
            }

            BuildCaches(Model);
        }
    }

    private enum BehaviorTarget
    {
        Entry,
        Exit,
        Activity,
        Effect
    }

    private static void ValidateName(string name, string kind, bool allowSlash = false)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ValidationException($"{kind} name cannot be empty");
        }

        if (!allowSlash && name.Contains('/', StringComparison.Ordinal))
        {
            throw new ValidationException($"{kind} name cannot contain '/'");
        }
    }

    private static void RegisterEvent(Model model, Event @event)
    {
        if (model.Events.TryGetValue(@event.Name, out var existing))
        {
            if (existing.Kind != @event.Kind)
            {
                throw new ValidationException($"event '{@event.Name}' already defined with a different kind");
            }

            return;
        }

        model.Events[@event.Name] = @event;
    }

    private static OperationInvoker Wrap<TInstance>(Operation<TInstance> operation) where TInstance : Instance =>
        (ctx, instance, @event) => operation(ctx, (TInstance)instance, @event);

    private static GuardInvoker Wrap<TInstance>(Expression<TInstance> expression) where TInstance : Instance =>
        (ctx, instance, @event) => expression(ctx, (TInstance)instance, @event);

    private static DurationInvoker Wrap<TInstance>(DurationProvider<TInstance> duration) where TInstance : Instance =>
        (ctx, instance, @event) => duration(ctx, (TInstance)instance, @event);

    private static TimeInvoker Wrap<TInstance>(TimeProvider<TInstance> time) where TInstance : Instance =>
        (ctx, instance, @event) => time(ctx, (TInstance)instance, @event);

    private static ConditionInvoker Wrap<TInstance>(ConditionChannel<TInstance> condition) where TInstance : Instance =>
        (ctx, instance, @event, cancellationToken) => condition(ctx, (TInstance)instance, @event, cancellationToken);

    private static string AttributeTemporalName(string attributeName)
    {
        if (string.IsNullOrWhiteSpace(attributeName))
        {
            throw new ValidationException("temporal attribute name cannot be empty");
        }

        return "attribute_" + attributeName.Replace('/', '_');
    }

    private static DurationInvoker AttributeDuration(string attributeName) =>
        (ctx, instance, _) => Runtime.Get<object?>(ctx, instance, attributeName) switch
        {
            TimeSpan duration => duration,
            null => throw new HsmRuntimeException($"attribute '{attributeName}' must be a TimeSpan"),
            var value => throw new HsmRuntimeException($"attribute '{attributeName}' must be a TimeSpan, got {value.GetType().Name}")
        };

    private static TimeInvoker AttributeTime(string attributeName) =>
        (ctx, instance, _) => Runtime.Get<object?>(ctx, instance, attributeName) switch
        {
            DateTimeOffset time => time,
            DateTime time => new DateTimeOffset(time),
            null => throw new HsmRuntimeException($"attribute '{attributeName}' must be a DateTimeOffset or DateTime"),
            var value => throw new HsmRuntimeException($"attribute '{attributeName}' must be a DateTimeOffset or DateTime, got {value.GetType().Name}")
        };

    private static void FinalizeTransition(Model model, Transition transition)
    {
        transition.SourceQualifiedName = string.IsNullOrWhiteSpace(transition.PendingSourceQualifiedName)
            ? transition.OwnerQualifiedNameInternal
            : transition.PendingSourceQualifiedName;

        transition.TargetQualifiedName = transition.PendingTargetQualifiedName ?? string.Empty;

        var source = model.Resolve<Vertex>(transition.SourceQualifiedName)
                     ?? throw new ValidationException($"missing source '{transition.SourceQualifiedName}'");

        if (source.Kind == Kind.FinalState)
        {
            throw new ValidationException($"final state '{source.QualifiedName}' cannot have transitions");
        }

        if (transition.ExplicitTarget && string.IsNullOrWhiteSpace(transition.TargetQualifiedName))
        {
            throw new ValidationException($"missing target for transition '{transition.QualifiedName}'");
        }

        if (!string.IsNullOrWhiteSpace(transition.TargetQualifiedName) && model.Resolve<Vertex>(transition.TargetQualifiedName) is null)
        {
            throw new ValidationException($"missing target '{transition.TargetQualifiedName}'");
        }

        if (!transition.ExplicitSource && transition.OwnerQualifiedNameInternal == model.QualifiedName && !string.IsNullOrWhiteSpace(transition.TargetQualifiedName))
        {
            throw new ValidationException("top level transitions with a target must also define a source");
        }

        foreach (var @event in transition.PendingEvents)
        {
            transition.Events.Add(@event.Name);
            RegisterEvent(model, @event);
        }

        foreach (var attributeName in transition.PendingOnSetAttributes)
        {
            var qualifiedName = PathUtil.Join(model.QualifiedName, attributeName);
            transition.Events.Add(qualifiedName);
            RegisterEvent(model, new Event(qualifiedName, Kind.ChangeEvent, source: qualifiedName, schema: typeof(AttributeChange)));
        }

        foreach (var operationName in transition.PendingOnCallOperations)
        {
            var qualifiedName = PathUtil.Join(model.QualifiedName, operationName);
            if (!model.Operations.ContainsKey(qualifiedName))
            {
                throw new ValidationException($"missing operation '{qualifiedName}' for OnCall()");
            }

            transition.Events.Add(qualifiedName);
            RegisterEvent(model, new Event(qualifiedName, Kind.CallEvent, source: qualifiedName, schema: typeof(CallData)));
        }

        foreach (var temporal in transition.TemporalDefinitions)
        {
            transition.Events.Add(temporal.EventName);
            RegisterEvent(model, new Event(temporal.EventName, temporal.EventKind));
        }

        if (transition.Events.Count == 0 && source.Kind != Kind.Initial && source.Kind != Kind.Choice && source.Kind != Kind.ShallowHistory && source.Kind != Kind.DeepHistory)
        {
            throw new ValidationException("completion transition not implemented");
        }

        if (string.IsNullOrWhiteSpace(transition.TargetQualifiedName))
        {
            transition.TransitionKind = TransitionKind.Internal;
        }
        else if (transition.TargetQualifiedName == transition.SourceQualifiedName)
        {
            transition.TransitionKind = TransitionKind.Self;
        }
        else if (PathUtil.IsAncestor(transition.SourceQualifiedName, transition.TargetQualifiedName))
        {
            transition.TransitionKind = TransitionKind.Local;
        }
        else
        {
            transition.TransitionKind = TransitionKind.External;
        }

        if (transition.TransitionKind == TransitionKind.Internal && transition.Effects.Count == 0)
        {
            throw new ValidationException("internal transitions require an effect");
        }

        if (transition.TemporalDefinitions.Count > 0 && source is not global::Stateforward.Hsm.State)
        {
            throw new ValidationException("time based triggers require a real state source");
        }

        PrecomputeTransitionPaths(model, transition, source);
    }

    private static void PrecomputeTransitionPaths(Model model, Transition transition, Vertex source)
    {
        transition.Paths.Clear();

        var lca = PathUtil.LowestCommonAncestor(transition.SourceQualifiedName, transition.TargetQualifiedName);
        var enter = new List<string>();
        var entering = transition.TargetQualifiedName;
        while (!string.IsNullOrWhiteSpace(entering) && entering != lca && entering != model.QualifiedName)
        {
            enter.Insert(0, entering);
            entering = PathUtil.Parent(entering);
        }

        if (transition.TransitionKind == TransitionKind.Self)
        {
            enter.Add(transition.SourceQualifiedName);
        }

        if (source.Kind == Kind.Initial)
        {
            transition.Paths[PathUtil.Parent(source.QualifiedName)] = new TransitionPath(enter, new[] { source.QualifiedName });
            return;
        }

        foreach (var vertex in model.Members.Values.OfType<Vertex>())
        {
            if (!PathUtil.IsDescendantOrSelf(vertex.QualifiedName, transition.SourceQualifiedName))
            {
                continue;
            }

            var exit = new List<string>();
            if (transition.TransitionKind != TransitionKind.Internal)
            {
                var exiting = vertex.QualifiedName;
                while (!string.IsNullOrWhiteSpace(exiting) && exiting != lca)
                {
                    exit.Add(exiting);
                    if (exiting == model.QualifiedName)
                    {
                        break;
                    }

                    exiting = PathUtil.Parent(exiting);
                }
            }

            transition.Paths[vertex.QualifiedName] = new TransitionPath(enter, exit);
        }
    }

    private static void BuildCaches(Model model)
    {
        model.TransitionMap.Clear();
        model.DeferredMap.Clear();

        foreach (var vertex in model.Members.Values.OfType<Vertex>())
        {
            var transitionBuckets = new Dictionary<string, List<Transition>>(StringComparer.Ordinal);
            model.TransitionMap[vertex.QualifiedName] = transitionBuckets;
            model.DeferredMap[vertex.QualifiedName] = new HashSet<string>(StringComparer.Ordinal);

            foreach (var ancestorQualifiedName in PathUtil.AncestorChain(vertex.QualifiedName, model.QualifiedName))
            {
                var ancestorVertex = model.Resolve<Vertex>(ancestorQualifiedName);
                if (ancestorVertex is not null)
                {
                    foreach (var transition in ancestorVertex.Transitions)
                    {
                        foreach (var eventName in transition.Events)
                        {
                            if (!transitionBuckets.TryGetValue(eventName, out var list))
                            {
                                list = new List<Transition>();
                                transitionBuckets[eventName] = list;
                            }

                            list.Add(transition);
                        }
                    }
                }

                if (model.Resolve<global::Stateforward.Hsm.State>(ancestorQualifiedName) is not global::Stateforward.Hsm.State ancestorState)
                {
                    continue;
                }

                foreach (var deferredEvent in ancestorState.DeferredEvents)
                {
                    var blockedByCurrentVertex = vertex.Transitions.Any(transition => transition.Events.Contains(deferredEvent, StringComparer.Ordinal));
                    if (!blockedByCurrentVertex)
                    {
                        model.DeferredMap[vertex.QualifiedName].Add(deferredEvent);
                    }
                }
            }
        }
    }

    private static string TransitionPathScope(Model model, Transition transition)
    {
        var scopeQualifiedName = transition.OwnerQualifiedNameInternal;
        var owner = model.Resolve<Vertex>(scopeQualifiedName);
        return owner?.Kind == Kind.Initial
            ? PathUtil.Parent(scopeQualifiedName)
            : scopeQualifiedName;
    }

    private sealed class StatePartial : IBuildPartial
    {
        private readonly string _name;
        private readonly IReadOnlyList<IPartial> _partials;

        public StatePartial(string name, IReadOnlyList<IPartial> partials)
        {
            ValidateName(name, "state");
            _name = name;
            _partials = partials;
        }

        public void Apply(ModelBuilder builder)
        {
            var owner = builder.Find<State>() ?? builder.Find<Model>();
            if (owner is null)
            {
                throw new ValidationException("state must be called within Define() or State()");
            }

            var state = new State(PathUtil.Join(owner.QualifiedName, _name));
            builder.Register(state);
            builder.Push(state);
            foreach (var partial in _partials)
            {
                ((IBuildPartial)partial).Apply(builder);
            }

            builder.Pop();
        }
    }

    private sealed class FinalPartial : IBuildPartial
    {
        private readonly string _name;

        public FinalPartial(string name)
        {
            ValidateName(name, "final");
            _name = name;
        }

        public void Apply(ModelBuilder builder)
        {
            var owner = builder.Find<State>() ?? builder.Find<Model>();
            if (owner is null)
            {
                throw new ValidationException("final must be called within Define() or State()");
            }

            var finalState = new FinalStateNode(PathUtil.Join(owner.QualifiedName, _name));
            builder.Register(finalState);
        }
    }

    private sealed class HistoryPartial : IBuildPartial
    {
        private readonly string _name;
        private readonly Kind _kind;
        private readonly IReadOnlyList<IPartial> _partials;

        public HistoryPartial(string name, Kind kind, IReadOnlyList<IPartial> partials)
        {
            ValidateName(name, "history");
            _name = name;
            _kind = kind;
            _partials = partials;
        }

        public void Apply(ModelBuilder builder)
        {
            var owner = builder.Find<State>();
            if (owner is null || owner is Model)
            {
                throw new ValidationException("history must be called within a nested State");
            }

            var history = new HistoryPseudostate(PathUtil.Join(owner.QualifiedName, _name), _kind);
            builder.Register(history);
            builder.Push(history);
            foreach (var partial in _partials)
            {
                ((IBuildPartial)partial).Apply(builder);
            }

            builder.Pop();
        }
    }

    private sealed class ChoicePartial : IBuildPartial
    {
        private readonly string _name;
        private readonly IReadOnlyList<IPartial> _partials;

        public ChoicePartial(string name, IReadOnlyList<IPartial> partials)
        {
            ValidateName(name, "choice");
            _name = name;
            _partials = partials;
        }

        public void Apply(ModelBuilder builder)
        {
            var owner = builder.Find<State>();
            if (owner is null)
            {
                throw new ValidationException("Choice() must be called within a State()");
            }

            var choice = new ChoicePseudostate(PathUtil.Join(owner.QualifiedName, _name));
            builder.Register(choice);
            builder.Push(choice);
            foreach (var partial in _partials)
            {
                ((IBuildPartial)partial).Apply(builder);
            }

            builder.Pop();
            builder.FinalizeLater(() =>
            {
                if (choice.Transitions.Count == 0)
                {
                    throw new ValidationException($"you must define at least one transition for choice '{choice.QualifiedName}'");
                }

                if (choice.Transitions[^1].Guard is not null)
                {
                    throw new ValidationException($"the last transition of choice state '{choice.QualifiedName}' cannot have a guard");
                }
            });
        }
    }

    private sealed class TransitionPartial : IBuildPartial
    {
        private readonly IReadOnlyList<IPartial> _partials;

        public TransitionPartial(IReadOnlyList<IPartial> partials)
        {
            _partials = partials;
        }

        public void Apply(ModelBuilder builder)
        {
            var owner = builder.Find<Vertex>();
            if (owner is null)
            {
                throw new ValidationException("transition must be called within a State() or Define()");
            }

            var transition = new Transition(PathUtil.Join(owner.QualifiedName, builder.NextName("transition")), owner.QualifiedName);
            builder.Register(transition);
            owner.Transitions.Add(transition);

            builder.Push(transition);
            foreach (var partial in _partials)
            {
                ((IBuildPartial)partial).Apply(builder);
            }

            builder.Pop();
            builder.FinalizeLater(() => FinalizeTransition(builder.Model, transition));
        }
    }

    private sealed class InitialPartial : IBuildPartial
    {
        private readonly IReadOnlyList<IPartial> _partials;

        public InitialPartial(IReadOnlyList<IPartial> partials)
        {
            _partials = partials;
        }

        public void Apply(ModelBuilder builder)
        {
            var owner = builder.Find<State>() ?? builder.Find<Model>();
            if (owner is null)
            {
                throw new ValidationException("initial must be called within a State or Model");
            }

            var initial = new InitialPseudostate(PathUtil.Join(owner.QualifiedName, ".initial"));
            builder.Register(initial);
            owner.InitialQualifiedName = initial.QualifiedName;

            var transition = new Transition(PathUtil.Join(initial.QualifiedName, builder.NextName("transition")), initial.QualifiedName)
            {
                PendingSourceQualifiedName = initial.QualifiedName,
                ExplicitSource = true
            };
            transition.Events.Add(InitialEvent.EventName);
            builder.Register(transition);
            initial.Transitions.Add(transition);

            builder.Push(initial);
            builder.Push(transition);
            foreach (var partial in _partials)
            {
                ((IBuildPartial)partial).Apply(builder);
            }

            builder.Pop();
            builder.Pop();

            builder.FinalizeLater(() =>
            {
                if (initial.Transitions.Count > 1)
                {
                    throw new ValidationException($"initial '{initial.QualifiedName}' cannot have multiple transitions");
                }

                FinalizeTransition(builder.Model, transition);
                if (transition.Guard is not null)
                {
                    throw new ValidationException($"initial '{initial.QualifiedName}' cannot have a guard");
                }

                if (string.IsNullOrWhiteSpace(transition.TargetQualifiedName))
                {
                    throw new ValidationException($"initial '{initial.QualifiedName}' requires a target");
                }

                if (!PathUtil.IsDescendantOrSelf(transition.TargetQualifiedName, owner.QualifiedName))
                {
                    throw new ValidationException($"initial '{initial.QualifiedName}' must target a nested state");
                }
            });
        }
    }

    private sealed class SourcePartial : IBuildPartial
    {
        private readonly string _path;

        public SourcePartial(string path)
        {
            _path = path;
        }

        public void Apply(ModelBuilder builder)
        {
            var transition = builder.Find<Transition>();
            if (transition is null)
            {
                throw new ValidationException("Source() must be called within Transition()");
            }

            transition.PendingSourceQualifiedName = PathUtil.NormalizeForModel(builder.Model.QualifiedName, TransitionPathScope(builder.Model, transition), _path);
            transition.ExplicitSource = true;
        }
    }

    private sealed class TargetPartial : IBuildPartial
    {
        private readonly string _path;

        public TargetPartial(string path)
        {
            _path = path;
        }

        public void Apply(ModelBuilder builder)
        {
            var transition = builder.Find<Transition>();
            if (transition is null)
            {
                throw new ValidationException("Target() must be called within Transition()");
            }

            transition.PendingTargetQualifiedName = PathUtil.NormalizeForModel(builder.Model.QualifiedName, TransitionPathScope(builder.Model, transition), _path);
            transition.ExplicitTarget = true;
        }
    }

    private sealed class OnPartial : IBuildPartial
    {
        private readonly Event _event;

        public OnPartial(Event @event)
        {
            _event = @event;
        }

        public void Apply(ModelBuilder builder)
        {
            var transition = builder.Find<Transition>();
            if (transition is null)
            {
                throw new ValidationException("On() must be called within Transition()");
            }

            transition.PendingEvents.Add(_event);
        }
    }

    private sealed class OnSetPartial : IBuildPartial
    {
        private readonly string _attributeName;

        public OnSetPartial(string attributeName)
        {
            if (string.IsNullOrWhiteSpace(attributeName))
            {
                throw new ValidationException("OnSet() requires a non-empty attribute name");
            }

            _attributeName = attributeName;
        }

        public void Apply(ModelBuilder builder)
        {
            var transition = builder.Find<Transition>();
            if (transition is null)
            {
                throw new ValidationException("OnSet() must be called within a Transition");
            }

            transition.PendingOnSetAttributes.Add(_attributeName);
        }
    }

    private sealed class OnCallPartial : IBuildPartial
    {
        private readonly string _operationName;

        public OnCallPartial(string operationName)
        {
            if (string.IsNullOrWhiteSpace(operationName))
            {
                throw new ValidationException("OnCall() requires a non-empty operation name");
            }

            _operationName = operationName;
        }

        public void Apply(ModelBuilder builder)
        {
            var transition = builder.Find<Transition>();
            if (transition is null)
            {
                throw new ValidationException("OnCall() must be called within a Transition");
            }

            transition.PendingOnCallOperations.Add(_operationName);
        }
    }

    private sealed class BehaviorPartial : IBuildPartial
    {
        private readonly BehaviorTarget _target;
        private readonly IReadOnlyList<OperationInvoker> _operations;

        public BehaviorPartial(BehaviorTarget target, IReadOnlyList<OperationInvoker> operations)
        {
            _target = target;
            _operations = operations;
        }

        public void Apply(ModelBuilder builder)
        {
            if (_target == BehaviorTarget.Effect)
            {
                var transition = builder.Find<Transition>();
                if (transition is null)
                {
                    throw new ValidationException("effect must be called within a Transition");
                }

                foreach (var operation in _operations)
                {
                    var behavior = new Behavior(PathUtil.Join(transition.QualifiedName, builder.NextName("effect")), false, operation);
                    builder.Register(behavior);
                    transition.Effects.Add(behavior);
                }

                return;
            }

            var state = builder.Find<State>();
            if (state is null)
            {
                throw new ValidationException(_target switch
                {
                    BehaviorTarget.Entry => "entry must be called within a State",
                    BehaviorTarget.Exit => "exit must be called within a State",
                    BehaviorTarget.Activity => "activity must be called within a State",
                    _ => "behavior must be called within a State"
                });
            }

            if (state is Model)
            {
                throw new ValidationException(_target switch
                {
                    BehaviorTarget.Entry => "entry actions are not allowed on top level state machine",
                    BehaviorTarget.Exit => "exit actions are not allowed on top level state machine",
                    BehaviorTarget.Activity => "activities are not allowed on top level state machine",
                    _ => "behaviors are not allowed on top level state machine"
                });
            }

            foreach (var operation in _operations)
            {
                var behavior = new Behavior(
                    PathUtil.Join(state.QualifiedName, builder.NextName(_target.ToString().ToLowerInvariant())),
                    _target == BehaviorTarget.Activity,
                    operation);
                builder.Register(behavior);
                switch (_target)
                {
                    case BehaviorTarget.Entry:
                        state.EntryBehaviors.Add(behavior);
                        break;
                    case BehaviorTarget.Exit:
                        state.ExitBehaviors.Add(behavior);
                        break;
                    case BehaviorTarget.Activity:
                        state.Activities.Add(behavior);
                        break;
                }
            }
        }
    }

    private sealed class GuardPartial : IBuildPartial
    {
        private readonly GuardInvoker _guard;
        private readonly string _name;

        public GuardPartial(GuardInvoker guard, string name)
        {
            _guard = guard;
            _name = string.IsNullOrWhiteSpace(name) ? "guard" : name;
        }

        public void Apply(ModelBuilder builder)
        {
            var transition = builder.Find<Transition>();
            if (transition is null)
            {
                throw new ValidationException("guard must be called within a Transition");
            }

            var constraint = new Constraint(PathUtil.Join(transition.QualifiedName, _name), _guard);
            builder.Register(constraint);
            transition.Guard = constraint;
        }
    }

    private sealed class TemporalPartial : IBuildPartial
    {
        private readonly TemporalKind _kind;
        private readonly string _name;
        private readonly DurationInvoker? _duration;
        private readonly TimeInvoker? _time;
        private readonly ConditionInvoker? _condition;

        public TemporalPartial(TemporalKind kind, string name, DurationInvoker? duration = null, TimeInvoker? time = null, ConditionInvoker? condition = null)
        {
            _kind = kind;
            _name = string.IsNullOrWhiteSpace(name) ? kind.ToString().ToLowerInvariant() : name;
            _duration = duration;
            _time = time;
            _condition = condition;
        }

        public void Apply(ModelBuilder builder)
        {
            var transition = builder.Find<Transition>();
            if (transition is null)
            {
                throw new ValidationException(_kind switch
                {
                    TemporalKind.After => "after must be called within a Transition",
                    TemporalKind.At => "at must be called within a Transition",
                    TemporalKind.Every => "Every() must be called within a Transition",
                    TemporalKind.When => "when must be called within a Transition",
                    _ => "temporal trigger must be called within a Transition"
                });
            }

            transition.TemporalDefinitions.Add(new TemporalDefinition
            {
                Kind = _kind,
                EventName = PathUtil.Join(transition.QualifiedName, _name, builder.NextName("time")),
                EventKind = Kind.TimeEvent,
                Duration = _duration,
                Time = _time,
                Condition = _condition
            });
        }
    }

    private sealed class DeferPartial : IBuildPartial
    {
        private readonly IReadOnlyList<string> _eventNames;

        public DeferPartial(IReadOnlyList<string> eventNames)
        {
            _eventNames = eventNames;
        }

        public void Apply(ModelBuilder builder)
        {
            var state = builder.Find<State>();
            if (state is null || state is Model)
            {
                throw new ValidationException("defer must be called within a State");
            }

            foreach (var eventName in _eventNames)
            {
                if (string.IsNullOrWhiteSpace(eventName))
                {
                    throw new ValidationException("deferred event name cannot be empty");
                }

                state.DeferredEvents.Add(eventName);
            }
        }
    }

    private sealed class AttributePartial : IBuildPartial
    {
        private readonly string _name;
        private readonly object? _defaultValue;
        private readonly bool _hasDefault;

        public AttributePartial(string name, object? defaultValue, bool hasDefault)
        {
            ValidateName(name, "attribute", allowSlash: true);
            _name = name;
            _defaultValue = defaultValue;
            _hasDefault = hasDefault;
        }

        public void Apply(ModelBuilder builder)
        {
            if (!ReferenceEquals(builder.Current, builder.Model))
            {
                throw new ValidationException("Attribute() must be declared at the model root");
            }

            var qualifiedName = PathUtil.Join(builder.Model.QualifiedName, _name);
            if (builder.Model.Attributes.ContainsKey(qualifiedName))
            {
                throw new ValidationException($"attribute '{qualifiedName}' already defined");
            }

            builder.Model.Attributes[qualifiedName] = new AttributeDefinition(qualifiedName, _defaultValue, _hasDefault);
        }
    }

    private sealed class OperationPartial : IBuildPartial
    {
        private readonly string _name;
        private readonly Delegate _callback;

        public OperationPartial(string name, Delegate callback)
        {
            ValidateName(name, "operation", allowSlash: true);
            _name = name;
            _callback = callback ?? throw new ValidationException("operation callback cannot be null");
        }

        public void Apply(ModelBuilder builder)
        {
            if (!ReferenceEquals(builder.Current, builder.Model))
            {
                throw new ValidationException("Operation() must be declared at the model root");
            }

            var qualifiedName = PathUtil.Join(builder.Model.QualifiedName, _name);
            if (builder.Model.Operations.ContainsKey(qualifiedName))
            {
                throw new ValidationException($"operation '{qualifiedName}' already defined");
            }

            builder.Model.Operations[qualifiedName] = new OperationDefinition(qualifiedName, _callback);
        }
    }
}
