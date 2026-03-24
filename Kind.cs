namespace Stateforward.Hsm;

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
