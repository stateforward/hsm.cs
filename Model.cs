using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;

namespace Stateforward.Hsm;

public sealed class ValidationException : Exception
{
    public ValidationException(string message) : base(message)
    {
    }
}

public class HsmRuntimeException : Exception
{
    public HsmRuntimeException(string message) : base(message)
    {
    }
}

public sealed class MissingHsmException : HsmRuntimeException
{
    public MissingHsmException() : base("missing hsm in context")
    {
    }
}

public sealed class MissingOperationException : HsmRuntimeException
{
    public MissingOperationException(string operationName) : base($"missing operation '{operationName}'")
    {
    }
}

public sealed class InvalidOperationSignatureException : HsmRuntimeException
{
    public InvalidOperationSignatureException(string operationName) : base($"invalid operation '{operationName}'")
    {
    }
}

public sealed class AlreadyStartedException : HsmRuntimeException
{
    public AlreadyStartedException() : base("hsm already started")
    {
    }
}

public abstract class Element
{
    protected Element(Kind kind, string? id = null)
    {
        Kind = kind;
        Id = string.IsNullOrWhiteSpace(id)
            ? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)
            : id;
    }

    public string Id { get; }
    public Kind Kind { get; protected init; }
}

public abstract class NamedElement : Element
{
    protected NamedElement(Kind kind, string qualifiedName, string? id = null) : base(kind, id)
    {
        QualifiedName = PathUtil.Join(qualifiedName);
    }

    public string QualifiedName { get; }
    public string Name => PathUtil.Name(QualifiedName);
    public string OwnerQualifiedName => PathUtil.Parent(QualifiedName);
}

public class Event
{
    public const string AnyName = "*";
    private string _name;

    public Event(string name, Kind kind = Kind.Event, object? data = null, string? source = null, string? id = null, string? target = null, object? schema = null, string? qualifiedName = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ValidationException("event name cannot be empty");
        }

        _name = name;
        Kind = kind;
        Data = data;
        Source = source;
        ID = id ?? string.Empty;
        Target = target;
        Schema = schema;
        QualifiedName = qualifiedName;
    }

    public string Name
    {
        get => _name;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ValidationException("event name cannot be empty");
            }

            _name = value;
        }
    }

    public string? QualifiedName { get; set; }
    public Kind Kind { get; set; }
    public string ID { get; set; }
    public object? Data { get; init; }
    public string? Source { get; set; }
    public string? Target { get; set; }
    public object? Schema { get; set; }

    public Event WithData(object? data) => new(Name, Kind, data, Source, ID, Target, Schema, QualifiedName);
    public Event WithDataAndID(object? data, string id) => new(Name, Kind, data, null, id, null, Schema, QualifiedName);
}

public class CompletionEvent : Event
{
    public CompletionEvent(string name, object? data = null, string? source = null)
        : base(name, Kind.CompletionEvent, data, source)
    {
    }
}

public sealed class InitialEvent : CompletionEvent
{
    public const string EventName = "hsm.initial";

    public InitialEvent(object? data = null)
        : base(EventName, data)
    {
    }
}

public sealed class ErrorEvent : Event
{
    public ErrorEvent(object? error = null, string name = "hsm.error")
        : base(name, Kind.ErrorEvent, error)
    {
    }
}

public sealed class AttributeChange
{
    public required string Name { get; init; }
    public object? Old { get; init; }
    public object? New { get; init; }
}

public sealed class CallData
{
    public required string Name { get; init; }
    public required IReadOnlyList<object?> Args { get; init; }
}

public sealed class EventSnapshot
{
    public required string Name { get; init; }
    public required Kind Kind { get; init; }
    public string? Target { get; init; }
    public bool Guard { get; init; }
    public object? Schema { get; init; }
}

public sealed class Snapshot
{
    public string ID { get; init; } = string.Empty;
    public string QualifiedName { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, object?> Attributes { get; init; } =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());
    public int QueueLen { get; init; }
    public IReadOnlyList<EventSnapshot> Events { get; init; } = Array.Empty<EventSnapshot>();
}

public class Vertex : NamedElement
{
    protected Vertex(Kind kind, string qualifiedName, string? id = null) : base(kind, qualifiedName, id)
    {
    }

    public List<Transition> Transitions { get; } = new();
}

public class State : Vertex
{
    public State(string qualifiedName, Kind kind = Kind.State, string? id = null) : base(kind, qualifiedName, id)
    {
    }

    public string? InitialQualifiedName { get; internal set; }
    public List<Behavior> EntryBehaviors { get; } = new();
    public List<Behavior> ExitBehaviors { get; } = new();
    public List<Behavior> Activities { get; } = new();
    public HashSet<string> DeferredEvents { get; } = new(StringComparer.Ordinal);
}

public sealed class Model : State
{
    public Model(string qualifiedName, string? id = null) : base(qualifiedName, Kind.Model, id)
    {
    }

    public Dictionary<string, NamedElement> Members { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, AttributeDefinition> Attributes { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, OperationDefinition> Operations { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Event> Events { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Dictionary<string, List<Transition>>> TransitionMap { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, HashSet<string>> DeferredMap { get; } = new(StringComparer.Ordinal);

    public NamedElement? Resolve(string qualifiedName)
    {
        Members.TryGetValue(PathUtil.Join(qualifiedName), out var element);
        return element;
    }

    internal T? Resolve<T>(string qualifiedName) where T : NamedElement => Resolve(qualifiedName) as T;
}

public sealed class TransitionPath
{
    public TransitionPath(IEnumerable<string> enter, IEnumerable<string> exit)
    {
        Enter = enter.ToArray();
        Exit = exit.ToArray();
    }

    public IReadOnlyList<string> Enter { get; }
    public IReadOnlyList<string> Exit { get; }
}

public sealed class Transition : NamedElement
{
    internal Transition(string qualifiedName, string ownerQualifiedName, string? id = null) : base(Kind.Transition, qualifiedName, id)
    {
        OwnerQualifiedNameInternal = ownerQualifiedName;
    }

    internal string OwnerQualifiedNameInternal { get; }
    internal string? PendingSourceQualifiedName { get; set; }
    internal string? PendingTargetQualifiedName { get; set; }
    internal bool ExplicitSource { get; set; }
    internal bool ExplicitTarget { get; set; }
    internal List<string> PendingOnSetAttributes { get; } = new();
    internal List<string> PendingOnCallOperations { get; } = new();
    internal List<TemporalDefinition> TemporalDefinitions { get; } = new();
    internal List<Event> PendingEvents { get; } = new();

    public string SourceQualifiedName { get; internal set; } = string.Empty;
    public string TargetQualifiedName { get; internal set; } = string.Empty;
    public List<string> Events { get; } = new();
    public Constraint? Guard { get; internal set; }
    public List<Behavior> Effects { get; } = new();
    public TransitionKind TransitionKind { get; internal set; }
    public Dictionary<string, TransitionPath> Paths { get; } = new(StringComparer.Ordinal);
}

public sealed class Constraint : NamedElement
{
    internal Constraint(string qualifiedName, GuardInvoker evaluator, string? id = null) : base(Kind.Constraint, qualifiedName, id)
    {
        Evaluate = evaluator;
    }

    internal GuardInvoker Evaluate { get; }
}

public sealed class Behavior : NamedElement
{
    internal Behavior(string qualifiedName, bool concurrent, OperationInvoker operation, string? id = null)
        : base(concurrent ? Kind.ConcurrentBehavior : Kind.Behavior, qualifiedName, id)
    {
        Concurrent = concurrent;
        Invoke = operation;
    }

    public bool Concurrent { get; }
    internal OperationInvoker Invoke { get; }
}

public sealed class AttributeDefinition : NamedElement
{
    public AttributeDefinition(string qualifiedName, object? defaultValue, bool hasDefault, string? id = null)
        : base(Kind.Attribute, qualifiedName, id)
    {
        DefaultValue = defaultValue;
        HasDefault = hasDefault;
    }

    public object? DefaultValue { get; }
    public bool HasDefault { get; }
}

public sealed class OperationDefinition : NamedElement
{
    public OperationDefinition(string qualifiedName, Delegate callback, string? id = null)
        : base(Kind.Operation, qualifiedName, id)
    {
        Callback = callback;
    }

    public Delegate Callback { get; }
}

internal sealed class InitialPseudostate : Vertex
{
    public InitialPseudostate(string qualifiedName) : base(Kind.Initial, qualifiedName)
    {
    }
}

internal sealed class ChoicePseudostate : Vertex
{
    public ChoicePseudostate(string qualifiedName) : base(Kind.Choice, qualifiedName)
    {
    }
}

internal sealed class HistoryPseudostate : Vertex
{
    public HistoryPseudostate(string qualifiedName, Kind kind) : base(kind, qualifiedName)
    {
    }
}

internal sealed class FinalStateNode : State
{
    public FinalStateNode(string qualifiedName) : base(qualifiedName, Kind.FinalState)
    {
    }
}

internal enum TemporalKind
{
    After,
    At,
    Every,
    When
}

internal sealed class TemporalDefinition
{
    public required TemporalKind Kind { get; init; }
    public required string EventName { get; init; }
    public required Kind EventKind { get; init; }
    public DurationInvoker? Duration { get; init; }
    public TimeInvoker? Time { get; init; }
    public ConditionInvoker? Condition { get; init; }
}

internal delegate void OperationInvoker(Context ctx, Instance instance, Event @event);
internal delegate bool GuardInvoker(Context ctx, Instance instance, Event @event);
internal delegate TimeSpan DurationInvoker(Context ctx, Instance instance, Event @event);
internal delegate DateTimeOffset TimeInvoker(Context ctx, Instance instance, Event @event);
internal delegate Task ConditionInvoker(Context ctx, Instance instance, Event @event, CancellationToken cancellationToken);

internal static class PathUtil
{
    public static string Join(params string[] segments)
    {
        var parts = new List<string>();
        var absolute = false;

        foreach (var rawSegment in segments)
        {
            if (string.IsNullOrWhiteSpace(rawSegment))
            {
                continue;
            }

            var segment = rawSegment.Replace('\\', '/');
            if (segment.StartsWith("/", StringComparison.Ordinal))
            {
                absolute = true;
                parts.Clear();
            }

            foreach (var part in segment.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == ".")
                {
                    continue;
                }

                if (part == "..")
                {
                    if (parts.Count > 0)
                    {
                        parts.RemoveAt(parts.Count - 1);
                    }

                    continue;
                }

                parts.Add(part);
            }
        }

        return absolute || parts.Count > 0 ? "/" + string.Join("/", parts) : "/";
    }

    public static string Parent(string path)
    {
        var normalized = Join(path);
        if (normalized == "/")
        {
            return string.Empty;
        }

        var lastSlash = normalized.LastIndexOf('/');
        if (lastSlash <= 0)
        {
            return "/";
        }

        return normalized[..lastSlash];
    }

    public static string Name(string path)
    {
        var normalized = Join(path);
        if (normalized == "/")
        {
            return "/";
        }

        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash >= 0 ? normalized[(lastSlash + 1)..] : normalized;
    }

    public static string NormalizeForModel(string modelQualifiedName, string scopeQualifiedName, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (value == ".")
        {
            return Join(scopeQualifiedName);
        }

        if (value.StartsWith("/", StringComparison.Ordinal))
        {
            var normalizedAbsolute = Join(value);
            if (normalizedAbsolute == modelQualifiedName || normalizedAbsolute.StartsWith(modelQualifiedName + "/", StringComparison.Ordinal))
            {
                return normalizedAbsolute;
            }

            return Join(modelQualifiedName, normalizedAbsolute.TrimStart('/'));
        }

        return Join(scopeQualifiedName, value);
    }

    public static bool IsDescendantOrSelf(string candidate, string ancestor)
    {
        var normalizedCandidate = Join(candidate);
        var normalizedAncestor = Join(ancestor);
        return normalizedCandidate == normalizedAncestor
               || normalizedCandidate.StartsWith(normalizedAncestor + "/", StringComparison.Ordinal);
    }

    public static bool IsAncestor(string current, string target)
    {
        current = Join(current);
        target = Join(target);
        if (current == target)
        {
            return false;
        }

        if (current == "/")
        {
            return true;
        }

        var parent = Parent(target);
        while (!string.IsNullOrEmpty(parent))
        {
            if (parent == current)
            {
                return true;
            }

            if (parent == "/")
            {
                break;
            }

            parent = Parent(parent);
        }

        return false;
    }

    public static string LowestCommonAncestor(string left, string right)
    {
        left = Join(left);
        right = Join(right);

        if (left == right)
        {
            return Parent(left);
        }

        if (string.IsNullOrEmpty(left))
        {
            return right;
        }

        if (string.IsNullOrEmpty(right))
        {
            return left;
        }

        if (Parent(left) == Parent(right))
        {
            return Parent(left);
        }

        if (IsAncestor(left, right))
        {
            return left;
        }

        if (IsAncestor(right, left))
        {
            return right;
        }

        return LowestCommonAncestor(Parent(left), Parent(right));
    }

    public static IEnumerable<string> AncestorChain(string qualifiedName, string rootQualifiedName)
    {
        var current = Join(qualifiedName);
        var root = Join(rootQualifiedName);
        while (!string.IsNullOrEmpty(current))
        {
            yield return current;
            if (current == root)
            {
                yield break;
            }

            current = Parent(current);
        }
    }
}
