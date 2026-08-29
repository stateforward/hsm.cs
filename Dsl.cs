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
        => DefineCore(name, allowUnresolvedOperations: false, partials);

    public static Model DefineSubmachine(string name, params IPartial[] partials)
        => DefineCore(name, allowUnresolvedOperations: true, partials);

    private static Model DefineCore(string name, bool allowUnresolvedOperations, params IPartial[] partials)
    {
        ValidateName(name, "model");
        var model = new Model(PathUtil.Join("/", name));
        model.AllowUnresolvedOperations = allowUnresolvedOperations;
        model.DefinitionPartials = partials.ToArray();
        model.Members[model.QualifiedName] = model;

        var builder = new ModelBuilder(model, allowUnresolvedOperations);
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
    public static IPartial Submachine(string name, params IPartial[] partials) => new StatePartial(name, partials, Kind.Submachine);
    public static IPartial Submachine(string name, Model machine, params IPartial[] partials) =>
        new SubmachinePartial(name, machine, partials);
    public static Model Redefine(string name, Model baseModel, params IPartial[] partials) =>
        Define(name, new IPartial[] { new ReplayModelPartial(baseModel) }.Concat(partials).ToArray());
    public static Model RedefineSubmachine(string name, Model baseModel, params IPartial[] partials) =>
        DefineSubmachine(name, new IPartial[] { new ReplayModelPartial(baseModel) }.Concat(partials).ToArray());
    public static Model Redefine(Model baseModel, params IPartial[] partials) =>
        Redefine(baseModel.Name, baseModel, partials);
    public static Model Redefine(Model baseModel, string name, params IPartial[] partials) =>
        Redefine(name, baseModel, partials);
    public static IPartial Final(string name) => new FinalPartial(name);
    public static IPartial ShallowHistory(string name, params IPartial[] partials) => new HistoryPartial(name, Kind.ShallowHistory, partials);
    public static IPartial DeepHistory(string name, params IPartial[] partials) => new HistoryPartial(name, Kind.DeepHistory, partials);
    public static IPartial Choice(string name, params IPartial[] partials) => new ChoicePartial(name, partials);
    public static IPartial EntryPoint(string name, string target, params IPartial[] effects) =>
        new EntryPointPartial(name, target, effects);
    public static IPartial EntryPoint(string name, params IPartial[] partials) =>
        new ContextualEntryPointPartial(name, partials);
    public static IPartial ExitPoint(string name, params IPartial[] effects) => new ExitPointPartial(name, effects);
    public static IPartial ToEntryPoint(string name) => new EntryPointTargetPartial(name);
    public static IPartial ToExitPoint(string name) => new ExitPointTargetPartial(name);
    public static IPartial OnExitPoint(string name) => new ExitPointTriggerPartial(name);
    public static IPartial Transition(params IPartial[] partials) => new TransitionPartial(partials);
    public static IPartial Initial(params IPartial[] partials) => new InitialPartial(partials);
    public static IPartial Source(string path) => new SourcePartial(path);
    public static IPartial Target(string path) => new TargetPartial(path);
    public static IPartial On(string eventName) => new OnPartial(new Event(eventName));
    public static IPartial On(Event @event) => new OnPartial(@event);
    public static IPartial OnCall(string operationName) => new OnCallPartial(operationName);
    public static IPartial OnSet(string attributeName) => new OnSetPartial(attributeName);
    public static IPartial Defer(params string[] eventNames) => new DeferPartial(eventNames);
    public static IPartial Attribute<T>(string name) => new AttributePartial(name, null, false, typeof(T));
    public static IPartial Attribute<T>(string name, T? defaultValue) => new AttributePartial(name, defaultValue, true, typeof(T));
    public static IPartial Attribute(string name, Type valueType) => new AttributePartial(name, null, false, valueType);
    public static IPartial Attribute(string name, object? defaultValue, Type valueType) => new AttributePartial(name, defaultValue, true, valueType);
    public static IPartial Operation(string name, Delegate callback) => new OperationPartial(name, callback);
    public static IPartial Operation(string name) => new OperationPartial(name, null);
    public static IPartial Entry<TInstance>(params Operation<TInstance>[] operations) where TInstance : Instance =>
        new BehaviorPartial(BehaviorTarget.Entry, operations.Select(Wrap).ToArray());
    public static IPartial Entry(params string[] operationNames) =>
        new OperationBehaviorPartial(BehaviorTarget.Entry, operationNames);
    public static IPartial Exit<TInstance>(params Operation<TInstance>[] operations) where TInstance : Instance =>
        new BehaviorPartial(BehaviorTarget.Exit, operations.Select(Wrap).ToArray());
    public static IPartial Exit(params string[] operationNames) =>
        new OperationBehaviorPartial(BehaviorTarget.Exit, operationNames);
    public static IPartial Activity<TInstance>(params Operation<TInstance>[] operations) where TInstance : Instance =>
        new BehaviorPartial(BehaviorTarget.Activity, operations.Select(WrapConcurrent).ToArray());
    public static IPartial Activity<TInstance>(params AsyncOperation<TInstance>[] operations) where TInstance : Instance =>
        new BehaviorPartial(BehaviorTarget.Activity, operations.Select(Wrap).ToArray());
    public static IPartial Activity(params string[] operationNames) =>
        new OperationBehaviorPartial(BehaviorTarget.Activity, operationNames);
    public static IPartial Effect<TInstance>(params Operation<TInstance>[] operations) where TInstance : Instance =>
        new BehaviorPartial(BehaviorTarget.Effect, operations.Select(Wrap).ToArray());
    public static IPartial Effect(params string[] operationNames) =>
        new OperationBehaviorPartial(BehaviorTarget.Effect, operationNames);
    public static IPartial Guard<TInstance>(Expression<TInstance> predicate) where TInstance : Instance =>
        new GuardPartial(Wrap(predicate), predicate.Method.Name);
    public static IPartial Guard(string operationName) => new OperationGuardPartial(operationName);
    public static IPartial After<TInstance>(DurationProvider<TInstance> duration) where TInstance : Instance =>
        new TemporalPartial(TemporalKind.After, duration.Method.Name, duration: Wrap(duration));
    public static IPartial After(string attributeName) =>
        new TemporalPartial(TemporalKind.After, AttributeTemporalName(attributeName), attributeName, duration: AttributeDuration(attributeName));
    public static IPartial At<TInstance>(TimeProvider<TInstance> time) where TInstance : Instance =>
        new TemporalPartial(TemporalKind.At, time.Method.Name, time: Wrap(time));
    public static IPartial At(string attributeName) =>
        new TemporalPartial(TemporalKind.At, AttributeTemporalName(attributeName), attributeName, time: AttributeTime(attributeName));
    public static IPartial Every<TInstance>(DurationProvider<TInstance> duration) where TInstance : Instance =>
        new TemporalPartial(TemporalKind.Every, duration.Method.Name, duration: Wrap(duration));
    public static IPartial Every(string attributeName) =>
        new TemporalPartial(TemporalKind.Every, AttributeTemporalName(attributeName), attributeName, duration: AttributeDuration(attributeName));
    public static IPartial When(string attributeName) => OnSet(attributeName);
    public static IPartial When<TInstance>(ConditionChannel<TInstance> condition) where TInstance : Instance =>
        new TemporalPartial(TemporalKind.When, condition.Method.Name, condition: Wrap(condition));

    internal sealed class ModelBuilder
    {
        private readonly Stack<NamedElement> _stack = new();
        private readonly Stack<(string From, string To)> _rebases = new();
        private readonly Stack<string> _behaviorScopes = new();
        private readonly List<Action> _pathFinalizers = new();
        private readonly List<Action> _compositionFinalizers = new();
        private readonly List<Action> _finalizers = new();
        private int _sequence;

        public ModelBuilder(Model model, bool allowUnresolvedOperations)
        {
            Model = model;
            AllowUnresolvedOperations = allowUnresolvedOperations;
            _stack.Push(model);
        }

        public Model Model { get; }
        public bool AllowUnresolvedOperations { get; }
        public Dictionary<string, EntryPointSpec> EntryPoints { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ExitPointSpec> ExitPoints { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ReplayedAttributes { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ReplayedOperations { get; } = new(StringComparer.Ordinal);
        public NamedElement Current => _stack.Peek();
        public string? BehaviorScope => _behaviorScopes.Count == 0 ? null : _behaviorScopes.Peek();
        public bool IsReplaying => _rebases.Count > 0;

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
        public void ResolveLater(Action action) => _pathFinalizers.Add(action);
        public void ComposeLater(Action action) => _compositionFinalizers.Add(action);
        public void PushBehaviorScope(string scope) => _behaviorScopes.Push(scope);
        public void PopBehaviorScope() => _behaviorScopes.Pop();

        public void PushRebase(string from, string to) => _rebases.Push((PathUtil.Join(from), PathUtil.Join(to)));
        public void PopRebase() => _rebases.Pop();

        public string NormalizePath(string scopeQualifiedName, string path)
        {
            if (path.StartsWith("/", StringComparison.Ordinal) && _rebases.Count > 0)
            {
                var (from, to) = _rebases.Peek();
                var normalized = PathUtil.Join(path);
                if (normalized == from) return to;
                if (normalized.StartsWith(from + "/", StringComparison.Ordinal))
                {
                    return PathUtil.Join(to, normalized[(from.Length + 1)..]);
                }
            }

            return PathUtil.NormalizeForModel(Model.QualifiedName, scopeQualifiedName, path);
        }

        public void FinalizeModel()
        {
            foreach (var finalizer in _pathFinalizers)
            {
                finalizer();
            }

            foreach (var finalizer in _compositionFinalizers)
            {
                finalizer();
            }

            foreach (var finalizer in _finalizers)
            {
                finalizer();
            }

            ValidateConnectionPointNamespaces();

            if (Model.InitialQualifiedName is null)
            {
                throw new ValidationException("initial state is required for state machine");
            }

            BuildCaches(Model);
        }

        private void ValidateConnectionPointNamespaces()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (boundary, name) in EntryPoints.Keys
                         .Select(key => (PathUtil.Parent(key), PathUtil.Name(key)[7..]))
                         .Concat(ExitPoints.Keys.Select(key => (PathUtil.Parent(key), PathUtil.Name(key)[6..]))))
            {
                var key = boundary + ":" + name;
                if (!names.Add(key))
                {
                    throw new ValidationException($"connection point name collision for '{name}' on '{boundary}'");
                }

                if (Model.Members.ContainsKey(PathUtil.Join(boundary, name)))
                {
                    throw new ValidationException($"connection point '{name}' conflicts with an existing model member");
                }
            }
        }
    }

    internal sealed record EntryPointSpec(string Boundary, string Target, IReadOnlyList<IPartial> Effects);
    internal sealed record ExitPointSpec(string Boundary, ChoicePseudostate Choice, IReadOnlyList<IPartial> Effects);

    private enum BehaviorTarget
    {
        Entry,
        Exit,
        Activity,
        Effect
    }

    private sealed class ReplayModelPartial : IBuildPartial
    {
        private readonly Model _source;

        public ReplayModelPartial(Model source)
        {
            _source = source;
        }

        public void Apply(ModelBuilder builder)
        {
            var target = builder.Find<State>() ?? builder.Find<Model>()
                ?? throw new ValidationException("model replay requires a state or model scope");
            builder.PushRebase(_source.QualifiedName, target.QualifiedName);
            builder.PushBehaviorScope(target.QualifiedName);
            try
            {
                foreach (var partial in _source.DefinitionPartials)
                {
                    ((IBuildPartial)partial).Apply(builder);
                }
            }
            finally
            {
                builder.PopBehaviorScope();
                builder.PopRebase();
            }
        }
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
        (ctx, instance, @event) =>
        {
            operation(ctx, (TInstance)instance, @event);
            return ValueTask.CompletedTask;
        };

    private static OperationInvoker Wrap<TInstance>(AsyncOperation<TInstance> operation) where TInstance : Instance =>
        (ctx, instance, @event) => operation(ctx, (TInstance)instance, @event);

    private static OperationInvoker WrapConcurrent<TInstance>(Operation<TInstance> operation) where TInstance : Instance =>
        (ctx, instance, @event) => new ValueTask(Task.Run(() => operation(ctx, (TInstance)instance, @event)));

    private static OperationInvoker WrapOperationReference(string operationName, bool concurrent) =>
        concurrent
            ? (ctx, instance, @event) => new ValueTask(Task.Run(
                async () => await AwaitOperationReference(ctx, instance, operationName, @event).ConfigureAwait(false)))
            : (ctx, instance, @event) => AwaitOperationReference(ctx, instance, operationName, @event);

    private static async ValueTask AwaitOperationReference(
        Context context,
        Instance instance,
        string operationName,
        Event @event)
    {
        switch (Runtime.InvokeOperationReference(context, instance, operationName, @event))
        {
            case Task task:
                await task.ConfigureAwait(false);
                break;
            case ValueTask pending:
                await pending.ConfigureAwait(false);
                break;
        }
    }

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

        if (model.Resolve<State>(transition.TargetQualifiedName) is { Kind: Kind.State or Kind.Submachine } targetState
            && model.Members.Values.OfType<State>().Any(child => child.OwnerQualifiedName == targetState.QualifiedName)
            && string.IsNullOrWhiteSpace(targetState.InitialQualifiedName))
        {
            throw new ValidationException($"composite state '{targetState.QualifiedName}' requires an initial transition");
        }

        if (transition.PendingEntryPoint is null
            && model.SubmachineBoundaries.Any(boundary =>
                transition.TargetQualifiedName != boundary
                && PathUtil.IsDescendantOrSelf(transition.TargetQualifiedName, boundary)
                && !PathUtil.IsDescendantOrSelf(transition.SourceQualifiedName, boundary)))
        {
            throw new ValidationException(
                $"transition '{transition.QualifiedName}' cannot target an internal submachine state directly");
        }

        if (transition.ExplicitSource
            && !transition.ComposedDefinition
            && transition.PendingExitPointTrigger is null
            && model.SubmachineBoundaries.Any(boundary =>
                transition.SourceQualifiedName != boundary
                && PathUtil.IsDescendantOrSelf(transition.SourceQualifiedName, boundary)))
        {
            throw new ValidationException(
                $"transition '{transition.QualifiedName}' cannot use an internal submachine state as its source");
        }

        foreach (var @event in transition.PendingEvents)
        {
            transition.Events.Add(@event.Name);
            RegisterEvent(model, @event);
        }

        foreach (var attributeName in transition.PendingOnSetAttributes)
        {
            var qualifiedName = ResolveScopedName(model.Attributes.Keys, transition.SourceQualifiedName, model.QualifiedName, attributeName);
            if (model.Members.TryGetValue(qualifiedName, out var member)
                && member is not AttributeDefinition
                && !(model.AllowUnresolvedOperations && member is State))
            {
                throw new ValidationException($"attribute '{qualifiedName}' conflicts with an existing model member");
            }
            if (!model.Attributes.ContainsKey(qualifiedName) && !model.AllowUnresolvedOperations)
            {
                var attribute = new AttributeDefinition(qualifiedName, null, false, typeof(object));
                model.Attributes[qualifiedName] = attribute;
                model.Members[qualifiedName] = attribute;
            }
            transition.Events.Add(qualifiedName);
            RegisterEvent(model, new Event(qualifiedName, Kind.ChangeEvent, source: qualifiedName, schema: typeof(AttributeChange)));
        }

        foreach (var operationName in transition.PendingOnCallOperations)
        {
            var qualifiedName = ResolveScopedName(model.Operations.Keys, transition.SourceQualifiedName, model.QualifiedName, operationName);
            if (!model.Operations.ContainsKey(qualifiedName) && !model.AllowUnresolvedOperations)
            {
                throw new ValidationException($"missing operation '{qualifiedName}' for OnCall()");
            }
            transition.Events.Add(qualifiedName);
            RegisterEvent(model, new Event(qualifiedName, Kind.CallEvent, source: qualifiedName, schema: typeof(CallData)));
        }

        foreach (var temporal in transition.TemporalDefinitions)
        {
            if (temporal.AttributeName is not null)
            {
                var qualifiedName = ResolveScopedName(
                    model.Attributes.Keys,
                    transition.SourceQualifiedName,
                    model.QualifiedName,
                    temporal.AttributeName);
                if (!model.Attributes.TryGetValue(qualifiedName, out var attribute))
                {
                    throw new ValidationException($"missing attribute '{qualifiedName}' for {temporal.Kind}()");
                }

                var compatible = attribute.ValueType == typeof(object)
                    || (temporal.Kind is TemporalKind.After or TemporalKind.Every
                        && attribute.ValueType == typeof(TimeSpan))
                    || (temporal.Kind == TemporalKind.At
                        && attribute.ValueType is not null
                        && (attribute.ValueType == typeof(DateTimeOffset) || attribute.ValueType == typeof(DateTime)));
                if (!compatible)
                {
                    throw new ValidationException(
                        $"attribute '{qualifiedName}' has an invalid type for {temporal.Kind}()");
                }
            }

            transition.Events.Add(temporal.EventName);
            RegisterEvent(model, new Event(temporal.EventName, temporal.EventKind));
        }

        if (transition.Events.Count == 0 && source.Kind != Kind.Initial && source.Kind != Kind.Choice && source.Kind != Kind.ShallowHistory && source.Kind != Kind.DeepHistory)
        {
            transition.Events.Add(CompletionEvent.EventName);
            RegisterEvent(model, new CompletionEvent(CompletionEvent.EventName));
        }

        if (transition.ReentryBoundary is not null)
        {
            transition.TransitionKind = TransitionKind.External;
        }
        else if (string.IsNullOrWhiteSpace(transition.TargetQualifiedName))
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

    private static string ResolveScopedName(
        IEnumerable<string> names,
        string sourceQualifiedName,
        string modelQualifiedName,
        string name)
    {
        if (name.StartsWith("/", StringComparison.Ordinal)) return PathUtil.Join(name);
        var known = names as ICollection<string> ?? names.ToArray();
        foreach (var scope in PathUtil.AncestorChain(sourceQualifiedName, modelQualifiedName))
        {
            var candidate = PathUtil.Join(scope, name);
            if (known.Contains(candidate)) return candidate;
        }
        return PathUtil.Join(modelQualifiedName, name);
    }

    private static void PrecomputeTransitionPaths(Model model, Transition transition, Vertex source)
    {
        transition.Paths.Clear();

        var lca = transition.ReentryBoundary is null
            ? PathUtil.LowestCommonAncestor(transition.SourceQualifiedName, transition.TargetQualifiedName)
            : PathUtil.Parent(transition.ReentryBoundary);
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
                    model.DeferredMap[vertex.QualifiedName].Add(deferredEvent);
                }
            }

            foreach (var transitions in transitionBuckets.Values)
            {
                var ordered = transitions
                    .Select((transition, index) => (transition, index))
                    .OrderByDescending(item => item.transition.SourceQualifiedName.Count(character => character == '/'))
                    .ThenBy(item => item.index)
                    .Select(item => item.transition)
                    .ToArray();
                transitions.Clear();
                transitions.AddRange(ordered);
            }
        }
    }

    private static string TransitionPathScope(Model model, Transition transition)
    {
        var scopeQualifiedName = transition.OwnerQualifiedNameInternal;
        var owner = model.Resolve<Vertex>(scopeQualifiedName);
        return owner?.Kind is Kind.Initial or Kind.Choice or Kind.ShallowHistory or Kind.DeepHistory
            ? PathUtil.Parent(scopeQualifiedName)
            : scopeQualifiedName;
    }

    private static void ResolveTransitionTarget(Model model, Transition transition)
    {
        var rawTarget = transition.PendingTargetPath;
        if (rawTarget is null
            || rawTarget.StartsWith("/", StringComparison.Ordinal)
            || rawTarget.StartsWith("..", StringComparison.Ordinal)
            || rawTarget == ".")
        {
            return;
        }

        foreach (var scope in PathUtil.AncestorChain(TransitionPathScope(model, transition), model.QualifiedName))
        {
            var candidate = PathUtil.NormalizeForModel(model.QualifiedName, scope, rawTarget);
            if (model.Resolve<Vertex>(candidate) is null) continue;
            transition.PendingTargetQualifiedName = candidate;
            return;
        }
    }

    private sealed class StatePartial : IBuildPartial
    {
        private readonly string _name;
        private readonly IReadOnlyList<IPartial> _partials;
        private readonly Kind _kind;

        public StatePartial(string name, IReadOnlyList<IPartial> partials, Kind kind = Kind.State)
        {
            ValidateName(name, "state");
            _name = name;
            _partials = partials;
            _kind = kind;
        }

        public void Apply(ModelBuilder builder)
        {
            var owner = builder.Find<State>() ?? builder.Find<Model>();
            if (owner is null)
            {
                throw new ValidationException("state must be called within Define() or State()");
            }

            var state = new State(PathUtil.Join(owner.QualifiedName, _name), _kind);
            builder.Register(state);
            builder.Push(state);
            foreach (var partial in _partials)
            {
                ((IBuildPartial)partial).Apply(builder);
            }

            builder.Pop();
        }
    }

    private sealed class SubmachinePartial : IBuildPartial
    {
        private readonly string _name;
        private readonly Model _machine;
        private readonly IReadOnlyList<IPartial> _partials;

        public SubmachinePartial(string name, Model machine, IReadOnlyList<IPartial> partials)
        {
            ValidateName(name, "submachine state");
            _name = name;
            _machine = machine ?? throw new ValidationException("submachine state requires a model");
            _partials = partials;
        }

        public void Apply(ModelBuilder builder)
        {
            if (_partials.Any(partial => partial is StatePartial
                or SubmachinePartial
                or FinalPartial
                or InitialPartial
                or ChoicePartial
                or HistoryPartial))
            {
                throw new ValidationException("SubmachineState() cannot directly contain states, initial, final, or pseudostate declarations");
            }

            var owner = builder.Find<State>() ?? builder.Find<Model>()
                ?? throw new ValidationException("SubmachineState() must be called within Define() or State()");
            builder.Model.SubmachineBoundaries.Add(PathUtil.Join(owner.QualifiedName, _name));
            new StatePartial(
                    _name,
                    new IPartial[] { new ReplayModelPartial(_machine) }.Concat(_partials).ToArray(),
                    Kind.Submachine)
                .Apply(builder);
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
            if (_partials.Count == 0)
            {
                throw new ValidationException($"history '{history.QualifiedName}' requires a default transition");
            }

            if (_partials.Any(partial => partial is TransitionPartial))
            {
                foreach (var partial in _partials) ((IBuildPartial)partial).Apply(builder);
            }
            else
            {
                new TransitionPartial(_partials).Apply(builder);
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

    private sealed class EntryPointPartial : IBuildPartial
    {
        private readonly string _name;
        private readonly string _target;
        private readonly IReadOnlyList<IPartial> _effects;

        public EntryPointPartial(string name, string target, IReadOnlyList<IPartial> effects)
        {
            ValidateName(name, "entry point");
            if (string.IsNullOrWhiteSpace(target))
            {
                throw new ValidationException("entry point target cannot be empty");
            }

            _name = name;
            _target = target;
            _effects = effects;
        }

        public void Apply(ModelBuilder builder)
        {
            var boundary = builder.Find<State>();
            if (boundary is null)
            {
                throw new ValidationException("EntryPoint() must be called within a State()");
            }

            var key = PathUtil.Join(boundary.QualifiedName, "$entry_" + _name);
            if (builder.EntryPoints.ContainsKey(key))
            {
                throw new ValidationException($"duplicate entry point '{_name}' on '{boundary.QualifiedName}'");
            }

            builder.EntryPoints[key] = new EntryPointSpec(
                boundary.QualifiedName,
                builder.NormalizePath(boundary.QualifiedName, _target),
                _effects);
        }
    }

    private sealed class ContextualEntryPointPartial : IBuildPartial
    {
        private readonly string _name;
        private readonly IReadOnlyList<IPartial> _partials;

        public ContextualEntryPointPartial(string name, IReadOnlyList<IPartial> partials)
        {
            ValidateName(name, "entry point");
            _name = name;
            _partials = partials;
        }

        public void Apply(ModelBuilder builder)
        {
            if (builder.Find<Transition>() is not null && _partials.Count == 0)
            {
                new EntryPointTargetPartial(_name).Apply(builder);
                return;
            }

            var targets = _partials.OfType<TargetPartial>().ToArray();
            if (targets.Length != 1)
            {
                throw new ValidationException("EntryPoint() declaration requires one Target()");
            }

            new EntryPointPartial(
                    _name,
                    targets[0].Path,
                    _partials.Where(partial => !ReferenceEquals(partial, targets[0])).ToArray())
                .Apply(builder);
        }
    }

    private sealed class ExitPointPartial : IBuildPartial
    {
        private readonly string _name;
        private readonly IReadOnlyList<IPartial> _effects;

        public ExitPointPartial(string name, IReadOnlyList<IPartial> effects)
        {
            ValidateName(name, "exit point");
            _name = name;
            _effects = effects;
        }

        public void Apply(ModelBuilder builder)
        {
            if (builder.Find<Transition>() is not null && _effects.Count == 0)
            {
                if (builder.Find<State>()?.Kind == Kind.Submachine)
                {
                    new ExitPointTriggerPartial(_name).Apply(builder);
                }
                else
                {
                    new ExitPointTargetPartial(_name).Apply(builder);
                }
                return;
            }

            var boundary = builder.Find<State>();
            if (boundary is null)
            {
                throw new ValidationException("ExitPoint() must be called within a State()");
            }

            var key = PathUtil.Join(boundary.QualifiedName, "$exit_" + _name);
            if (builder.ExitPoints.ContainsKey(key))
            {
                throw new ValidationException($"duplicate exit point '{_name}' on '{boundary.QualifiedName}'");
            }

            var choice = new ChoicePseudostate(key);
            builder.Register(choice);
            builder.ExitPoints[key] = new ExitPointSpec(boundary.QualifiedName, choice, _effects);

            builder.FinalizeLater(() =>
            {
                var fallback = new Transition(
                    PathUtil.Join(choice.QualifiedName, builder.NextName("unhandled")),
                    choice.QualifiedName)
                {
                    PendingSourceQualifiedName = choice.QualifiedName,
                    ExplicitSource = true,
                    ComposedDefinition = true,
                    ConnectionPointPriority = int.MaxValue
                };
                var behavior = new Behavior(
                    PathUtil.Join(fallback.QualifiedName, builder.NextName("effect")),
                    false,
                    (_, _, _) => throw new UnhandledExitPointException(_name),
                    boundary.QualifiedName);
                builder.Register(fallback);
                builder.Register(behavior);
                fallback.Effects.Add(behavior);
                choice.Transitions.Add(fallback);
                FinalizeTransition(builder.Model, fallback);
            });
        }
    }

    private sealed class EntryPointTargetPartial : IBuildPartial
    {
        private readonly string _name;

        public EntryPointTargetPartial(string name)
        {
            ValidateName(name, "entry point");
            _name = name;
        }

        public void Apply(ModelBuilder builder)
        {
            var transition = RequireTransition(builder, "ToEntryPoint()");
            transition.PendingEntryPoint = _name;
            builder.ComposeLater(() =>
            {
                var boundary = transition.PendingTargetQualifiedName;
                if (string.IsNullOrWhiteSpace(boundary))
                {
                    throw new ValidationException("ToEntryPoint() requires Target() to identify the submachine boundary");
                }

                var key = PathUtil.Join(boundary, "$entry_" + _name);
                if (!builder.EntryPoints.TryGetValue(key, out var entryPoint))
                {
                    throw new ValidationException($"missing entry point '{_name}' on '{boundary}'");
                }

                transition.PendingTargetQualifiedName = entryPoint.Target;
                transition.ExplicitTarget = true;
                var source = transition.PendingSourceQualifiedName ?? transition.OwnerQualifiedNameInternal;
                if (PathUtil.IsDescendantOrSelf(source, entryPoint.Boundary))
                {
                    transition.ReentryBoundary = entryPoint.Boundary;
                }

                ApplyTransitionEffects(builder, transition, entryPoint.Boundary, entryPoint.Effects);
            });
        }
    }

    private sealed class ExitPointTargetPartial : IBuildPartial
    {
        private readonly string _name;

        public ExitPointTargetPartial(string name)
        {
            ValidateName(name, "exit point");
            _name = name;
        }

        public void Apply(ModelBuilder builder)
        {
            var transition = RequireTransition(builder, "ToExitPoint()");
            transition.PendingExitPointTarget = _name;
            builder.ComposeLater(() =>
            {
                var source = transition.PendingSourceQualifiedName ?? transition.OwnerQualifiedNameInternal;
                var exitPoint = builder.ExitPoints
                    .Where(item => item.Key.EndsWith("/$exit_" + _name, StringComparison.Ordinal)
                                   && PathUtil.IsDescendantOrSelf(source, item.Value.Boundary))
                    .OrderByDescending(item => item.Value.Boundary.Length)
                    .Select(item => item.Value)
                    .FirstOrDefault()
                    ?? throw new ValidationException($"missing exit point '{_name}' for '{source}'");

                transition.PendingTargetQualifiedName = exitPoint.Choice.QualifiedName;
                transition.ExplicitTarget = true;
                ApplyTransitionEffects(builder, transition, exitPoint.Boundary, exitPoint.Effects);
            });
        }
    }

    private sealed class ExitPointTriggerPartial : IBuildPartial
    {
        private readonly string _name;

        public ExitPointTriggerPartial(string name)
        {
            ValidateName(name, "exit point");
            _name = name;
        }

        public void Apply(ModelBuilder builder)
        {
            var transition = RequireTransition(builder, "OnExitPoint()");
            transition.PendingExitPointTrigger = _name;
            builder.ComposeLater(() =>
            {
                var source = transition.PendingSourceQualifiedName ?? transition.OwnerQualifiedNameInternal;
                var matches = builder.ExitPoints.Values
                    .Where(spec => PathUtil.IsDescendantOrSelf(spec.Boundary, source)
                                   && spec.Choice.Name == "$exit_" + _name)
                    .OrderBy(spec => spec.Boundary.Length)
                    .ToArray();
                if (matches.Length == 0)
                {
                    throw new ValidationException($"missing exit point '{_name}' below '{source}'");
                }

                var originalOwner = builder.Model.Resolve<Vertex>(transition.OwnerQualifiedNameInternal);
                originalOwner?.Transitions.Remove(transition);
                MoveExitPointHandler(transition, matches[0], source);

                if (matches.Length > 1)
                {
                    builder.FinalizeLater(() =>
                    {
                        foreach (var match in matches.Skip(1))
                        {
                            var clone = CloneTransition(builder, transition, match.Choice);
                            MoveExitPointHandler(clone, match, source);
                            FinalizeTransition(builder.Model, clone);
                        }
                    });
                }
            });
        }
    }

    private static Transition RequireTransition(ModelBuilder builder, string operation)
    {
        return builder.Find<Transition>()
               ?? throw new ValidationException($"{operation} must be called within Transition()");
    }

    private static void ApplyTransitionEffects(
        ModelBuilder builder,
        Transition transition,
        string resolutionScope,
        IReadOnlyList<IPartial> effects)
    {
        builder.Push(transition);
        builder.PushBehaviorScope(resolutionScope);
        try
        {
            foreach (var effect in effects)
            {
                ((IBuildPartial)effect).Apply(builder);
            }
        }
        finally
        {
            builder.PopBehaviorScope();
            builder.Pop();
        }
    }

    private static void MoveExitPointHandler(Transition transition, ExitPointSpec exitPoint, string source)
    {
        transition.OwnerQualifiedNameInternal = exitPoint.Choice.QualifiedName;
        transition.PendingSourceQualifiedName = exitPoint.Choice.QualifiedName;
        transition.ExplicitSource = true;
        transition.ComposedDefinition = true;
        transition.ConnectionPointPriority =
            (exitPoint.Boundary.Count(character => character == '/') - source.Count(character => character == '/')) * 2
            + (transition.Guard is null ? 1 : 0);
        exitPoint.Choice.Transitions.Insert(
            exitPoint.Choice.Transitions.FindIndex(candidate => candidate.ConnectionPointPriority > transition.ConnectionPointPriority) is var index && index >= 0
                ? index
                : exitPoint.Choice.Transitions.Count,
            transition);
    }

    private static Transition CloneTransition(ModelBuilder builder, Transition source, ChoicePseudostate owner)
    {
        var clone = new Transition(PathUtil.Join(owner.QualifiedName, builder.NextName("transition")), owner.QualifiedName)
        {
            PendingTargetQualifiedName = source.PendingTargetQualifiedName ?? source.TargetQualifiedName,
            ExplicitTarget = source.ExplicitTarget,
            ComposedDefinition = true,
            Guard = source.Guard,
            ReentryBoundary = source.ReentryBoundary
        };
        clone.PendingEvents.AddRange(source.PendingEvents);
        clone.PendingOnSetAttributes.AddRange(source.PendingOnSetAttributes);
        clone.PendingOnCallOperations.AddRange(source.PendingOnCallOperations);
        clone.TemporalDefinitions.AddRange(source.TemporalDefinitions);
        clone.Effects.AddRange(source.Effects);
        builder.Register(clone);
        return clone;
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

            var transition = new Transition(PathUtil.Join(owner.QualifiedName, builder.NextName("transition")), owner.QualifiedName)
            {
                ComposedDefinition = builder.IsReplaying
            };
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
                ExplicitSource = true,
                ComposedDefinition = builder.IsReplaying
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

            transition.PendingSourceQualifiedName = builder.NormalizePath(TransitionPathScope(builder.Model, transition), _path);
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

        public string Path => _path;

        public void Apply(ModelBuilder builder)
        {
            var transition = builder.Find<Transition>();
            if (transition is null)
            {
                throw new ValidationException("Target() must be called within Transition()");
            }

            transition.PendingTargetQualifiedName = builder.NormalizePath(TransitionPathScope(builder.Model, transition), _path);
            transition.PendingTargetPath = _path;
            builder.ResolveLater(() => ResolveTransitionTarget(builder.Model, transition));
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
            ValidateName(attributeName, "OnSet attribute");
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
            ValidateName(operationName, "OnCall operation");
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
                    var behavior = new Behavior(
                        PathUtil.Join(transition.QualifiedName, builder.NextName("effect")),
                        false,
                        operation,
                        builder.BehaviorScope);
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

    private sealed class OperationBehaviorPartial : IBuildPartial
    {
        private readonly BehaviorTarget _target;
        private readonly IReadOnlyList<string> _operationNames;

        public OperationBehaviorPartial(BehaviorTarget target, IReadOnlyList<string> operationNames)
        {
            if (operationNames.Count == 0)
            {
                throw new ValidationException($"{target} requires at least one operation name");
            }

            foreach (var operationName in operationNames) ValidateName(operationName, $"{target} operation");
            _target = target;
            _operationNames = operationNames;
        }

        public void Apply(ModelBuilder builder)
        {
            var transition = _target == BehaviorTarget.Effect ? builder.Find<Transition>() : null;
            var state = transition is null ? builder.Find<State>() : null;
            if (transition is null && (state is null || state is Model))
            {
                throw new ValidationException($"{_target} operation references require a matching behavior owner");
            }

            var owner = (NamedElement?)transition ?? state!;
            var resolutionScope = builder.BehaviorScope
                ?? (transition?.OwnerQualifiedNameInternal ?? state!.QualifiedName);
            foreach (var operationName in _operationNames)
            {
                var behavior = new Behavior(
                    PathUtil.Join(owner.QualifiedName, builder.NextName(_target.ToString().ToLowerInvariant())),
                    _target == BehaviorTarget.Activity,
                    WrapOperationReference(operationName, _target == BehaviorTarget.Activity),
                    resolutionScope);
                builder.Register(behavior);
                if (transition is not null)
                {
                    transition.Effects.Add(behavior);
                }
                else
                {
                    switch (_target)
                    {
                        case BehaviorTarget.Entry:
                            state!.EntryBehaviors.Add(behavior);
                            break;
                        case BehaviorTarget.Exit:
                            state!.ExitBehaviors.Add(behavior);
                            break;
                        case BehaviorTarget.Activity:
                            state!.Activities.Add(behavior);
                            break;
                    }
                }

                builder.FinalizeLater(() =>
                {
                    var qualifiedName = ResolveScopedName(
                        builder.Model.Operations.Keys,
                        resolutionScope,
                        builder.Model.QualifiedName,
                        operationName);
                    if (!builder.Model.Operations.ContainsKey(qualifiedName) && !builder.AllowUnresolvedOperations)
                    {
                        throw new ValidationException($"missing operation '{qualifiedName}' for {_target}()");
                    }
                });
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

    private sealed class OperationGuardPartial : IBuildPartial
    {
        private readonly string _operationName;

        public OperationGuardPartial(string operationName)
        {
            ValidateName(operationName, "guard operation");
            _operationName = operationName;
        }

        public void Apply(ModelBuilder builder)
        {
            var transition = builder.Find<Transition>()
                ?? throw new ValidationException("guard must be called within a Transition");
            var resolutionScope = builder.BehaviorScope ?? transition.OwnerQualifiedNameInternal;
            var constraint = new Constraint(
                PathUtil.Join(transition.QualifiedName, _operationName),
                (ctx, instance, @event) =>
                    Runtime.InvokeOperationReference(ctx, instance, _operationName, @event) is true);
            builder.Register(constraint);
            transition.Guard = constraint;
            builder.FinalizeLater(() =>
            {
                var qualifiedName = ResolveScopedName(
                    builder.Model.Operations.Keys,
                    resolutionScope,
                    builder.Model.QualifiedName,
                    _operationName);
                if (!builder.Model.Operations.ContainsKey(qualifiedName) && !builder.AllowUnresolvedOperations)
                {
                    throw new ValidationException($"missing operation '{qualifiedName}' for Guard()");
                }
            });
        }
    }

    private sealed class TemporalPartial : IBuildPartial
    {
        private readonly TemporalKind _kind;
        private readonly string _name;
        private readonly string? _attributeName;
        private readonly DurationInvoker? _duration;
        private readonly TimeInvoker? _time;
        private readonly ConditionInvoker? _condition;

        public TemporalPartial(
            TemporalKind kind,
            string name,
            string? attributeName = null,
            DurationInvoker? duration = null,
            TimeInvoker? time = null,
            ConditionInvoker? condition = null)
        {
            _kind = kind;
            _name = string.IsNullOrWhiteSpace(name) ? kind.ToString().ToLowerInvariant() : name;
            _attributeName = attributeName;
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
                AttributeName = _attributeName,
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
        private readonly Type _valueType;

        public AttributePartial(string name, object? defaultValue, bool hasDefault, Type valueType)
        {
            ValidateName(name, "attribute");
            _name = name;
            _defaultValue = defaultValue;
            _hasDefault = hasDefault;
            _valueType = valueType;
        }

        public void Apply(ModelBuilder builder)
        {
            if (_hasDefault
                && _defaultValue is not null
                && _valueType != typeof(object)
                && !RuntimeEngine.IsCompatibleAttributeValue(_valueType, _defaultValue))
            {
                throw new ValidationException(
                    $"attribute '{_name}' requires a default value of type '{_valueType.Name}'");
            }

            var qualifiedName = PathUtil.Join(builder.Model.QualifiedName, _name);
            if (builder.IsReplaying) builder.ReplayedAttributes.Add(qualifiedName);
            if (builder.Model.Members.TryGetValue(qualifiedName, out var member)
                && member is not AttributeDefinition)
            {
                throw new ValidationException($"attribute '{qualifiedName}' conflicts with an existing model member");
            }
            if (builder.Model.Attributes.ContainsKey(qualifiedName))
            {
                if (!builder.IsReplaying && !builder.ReplayedAttributes.Remove(qualifiedName))
                {
                    throw new ValidationException($"attribute '{qualifiedName}' already defined");
                }
            }

            var attribute = new AttributeDefinition(qualifiedName, _defaultValue, _hasDefault, _valueType);
            builder.Model.Attributes[qualifiedName] = attribute;
            builder.Model.Members[qualifiedName] = attribute;
        }
    }

    private sealed class OperationPartial : IBuildPartial
    {
        private readonly string _name;
        private readonly Delegate? _callback;

        public OperationPartial(string name, Delegate? callback)
        {
            ValidateName(name, "operation");
            _name = name;
            _callback = callback;
        }

        public void Apply(ModelBuilder builder)
        {
            var qualifiedName = PathUtil.Join(builder.Model.QualifiedName, _name);
            if (builder.IsReplaying) builder.ReplayedOperations.Add(qualifiedName);
            if (builder.Model.Members.TryGetValue(qualifiedName, out var member)
                && member is not OperationDefinition)
            {
                throw new ValidationException($"operation '{qualifiedName}' conflicts with an existing model member");
            }
            if (builder.Model.Operations.ContainsKey(qualifiedName))
            {
                if (!builder.IsReplaying && !builder.ReplayedOperations.Remove(qualifiedName))
                {
                    throw new ValidationException($"operation '{qualifiedName}' already defined");
                }
            }

            var operation = new OperationDefinition(
                qualifiedName,
                _callback,
                builder.BehaviorScope);
            builder.Model.Operations[qualifiedName] = operation;
            builder.Model.Members[qualifiedName] = operation;
        }
    }
}
