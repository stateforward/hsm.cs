namespace Stateforward.Hsm;

using System.Threading;

public enum Kind
{
    Null = 0,
    Element,
    Namespace,
    Vertex,
    Model,
    State,
    FinalState,
    Pseudostate,
    Initial,
    Choice,
    ShallowHistory,
    DeepHistory,
    Transition,
    Event,
    TimeEvent,
    CompletionEvent,
    ErrorEvent,
    ChangeEvent,
    CallEvent,
    Behavior,
    ConcurrentBehavior,
    Constraint,
    Attribute,
    Operation
}

public enum TransitionKind
{
    Internal,
    Local,
    External,
    Self
}

public static class KindExtensions
{
    public static bool IsCompletionPriority(this Kind kind) =>
        kind == Kind.CompletionEvent || kind == Kind.ErrorEvent;
}

public static class KindUtility
{
    private const int IdBits = 8;
    private const ulong IdMask = (1UL << IdBits) - 1UL;
    private static long _nextCustomId = 128;

    public static ulong MakeKind(params ulong[] bases)
    {
        var id = (ulong)(Interlocked.Increment(ref _nextCustomId) & (long)IdMask);
        if (id == 0)
        {
            id = (ulong)(Interlocked.Increment(ref _nextCustomId) & (long)IdMask);
        }

        var result = id;
        var shift = IdBits;
        var seen = new HashSet<ulong> { id };

        foreach (var baseKind in bases)
        {
            foreach (var baseId in EnumerateKindIds(baseKind))
            {
                if (baseId == 0 || !seen.Add(baseId) || shift >= sizeof(ulong) * 8)
                {
                    continue;
                }

                result |= baseId << shift;
                shift += IdBits;
            }
        }

        return result;
    }

    public static bool IsKind(ulong kind, params ulong[] bases)
    {
        foreach (var candidate in bases)
        {
            if (candidate == 0)
            {
                continue;
            }

            var candidateId = candidate & IdMask;
            foreach (var kindId in EnumerateKindIds(kind))
            {
                if (kindId == candidateId)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool IsKind(Kind kind, params Kind[] bases)
    {
        foreach (var candidate in bases)
        {
            if (kind == candidate || BuiltinBases(kind).Contains(candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<ulong> EnumerateKindIds(ulong kind)
    {
        while (kind != 0)
        {
            var id = kind & IdMask;
            if (id != 0)
            {
                yield return id;
            }

            kind >>= IdBits;
        }
    }

    private static IEnumerable<Kind> BuiltinBases(Kind kind)
    {
        switch (kind)
        {
            case Kind.Model:
                yield return Kind.State;
                yield return Kind.Vertex;
                yield return Kind.Namespace;
                yield return Kind.Element;
                break;
            case Kind.State:
                yield return Kind.Vertex;
                yield return Kind.Namespace;
                yield return Kind.Element;
                break;
            case Kind.FinalState:
                yield return Kind.State;
                yield return Kind.Vertex;
                yield return Kind.Namespace;
                yield return Kind.Element;
                break;
            case Kind.Initial:
            case Kind.Choice:
            case Kind.ShallowHistory:
            case Kind.DeepHistory:
                yield return Kind.Pseudostate;
                yield return Kind.Vertex;
                yield return Kind.Element;
                break;
            case Kind.TimeEvent:
            case Kind.ChangeEvent:
            case Kind.CallEvent:
                yield return Kind.Event;
                yield return Kind.Element;
                break;
            case Kind.CompletionEvent:
                yield return Kind.Event;
                yield return Kind.Element;
                break;
            case Kind.ErrorEvent:
                yield return Kind.CompletionEvent;
                yield return Kind.Event;
                yield return Kind.Element;
                break;
            case Kind.ConcurrentBehavior:
                yield return Kind.Behavior;
                yield return Kind.Element;
                break;
            case Kind.Namespace:
            case Kind.Vertex:
            case Kind.Transition:
            case Kind.Event:
            case Kind.Behavior:
            case Kind.Constraint:
            case Kind.Attribute:
            case Kind.Operation:
                yield return Kind.Element;
                break;
        }
    }
}
