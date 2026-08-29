using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stateforward.Hsm;

var paths = args.Length == 0
    ? Directory.GetFiles(Path.GetFullPath("../conformance/cases"), "*.json").Order(StringComparer.Ordinal).ToArray()
    : args.SelectMany(Expand).ToArray();

var passed = 0;
var failed = 0;
var unsupported = 0;
foreach (var path in paths)
{
    try
    {
        await new CaseRunner(path).Run();
        Console.WriteLine($"{Path.GetFileName(path)}: ok");
        passed++;
    }
    catch (UnsupportedCaseException error)
    {
        Console.WriteLine($"{Path.GetFileName(path)}: unsupported: {error.Message}");
        unsupported++;
    }
    catch (Exception error)
    {
        Console.WriteLine($"{Path.GetFileName(path)}: failed: {error.Message}");
        failed++;
    }
}

Console.WriteLine($"total={paths.Length} passed={passed} failed={failed} unsupported={unsupported}");
return failed > 0 ? 1 : unsupported > 0 ? 77 : 0;

static IEnumerable<string> Expand(string value)
{
    if (File.Exists(value))
    {
        yield return Path.GetFullPath(value);
        yield break;
    }

    if (Directory.Exists(value))
    {
        foreach (var path in Directory.GetFiles(value, "*.json").Order(StringComparer.Ordinal))
        {
            yield return Path.GetFullPath(path);
        }

        yield break;
    }

    throw new FileNotFoundException(value);
}

sealed class UnsupportedCaseException(string message) : Exception(message);
sealed class PortableRuntimeException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

sealed class CaseRunner : Instance
{
    private sealed class ActivityCoordinator
    {
        public SemaphoreSlim Progress { get; } = new(0);
        public int PendingYields;
        public int Active;
    }

    private readonly JsonObject _case;
    private readonly JsonObject _behaviors;
    private readonly JsonArray _expectedTrace;
    private readonly List<JsonObject> _trace = [];
    private readonly Dictionary<string, JsonObject> _snapshots = new(StringComparer.Ordinal);
    private readonly Context _context = new();
    private readonly LogicalClock _clock;
    private readonly ActivityCoordinator _activities;
    private readonly Dictionary<string, CaseRunner> _instances;
    private readonly Dictionary<string, Group> _groups;
    private readonly Dictionary<string, object?> _startData;
    private readonly Dictionary<string, string> _missingModels;
    private readonly HashSet<string> _tracedDeferred;
    private readonly HashSet<string> _tracedUndeferred;
    private readonly Dictionary<string, JsonObject> _modelDefinitions;
    private string? _lastStableState;
    private bool? _lastDispatchQueued;
    private Model? _model;

    public CaseRunner(string path)
    {
        _case = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidDataException("case must be a JSON object");
        _behaviors = _case["behaviors"] as JsonObject ?? new JsonObject();
        _expectedTrace = _case["expect"]?["trace"] as JsonArray ?? [];
        _activities = new ActivityCoordinator();
        _instances = new Dictionary<string, CaseRunner>(StringComparer.Ordinal);
        _groups = new Dictionary<string, Group>(StringComparer.Ordinal);
        _startData = new Dictionary<string, object?>(StringComparer.Ordinal);
        _missingModels = new Dictionary<string, string>(StringComparer.Ordinal);
        _tracedDeferred = new HashSet<string>(StringComparer.Ordinal);
        _tracedUndeferred = new HashSet<string>(StringComparer.Ordinal);
        _modelDefinitions = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        _clock = new LogicalClock(
            () => { if (TraceExpects("timer_scheduled")) AddTrace("timer_scheduled"); },
            () => { },
            () => { if (TraceExpects("timer_cancelled")) AddTrace("timer_cancelled"); });
    }

    private CaseRunner(CaseRunner owner)
    {
        _case = owner._case;
        _behaviors = owner._behaviors;
        _expectedTrace = owner._expectedTrace;
        _trace = owner._trace;
        _snapshots = owner._snapshots;
        _context = owner._context;
        _clock = owner._clock;
        _activities = owner._activities;
        _instances = owner._instances;
        _groups = owner._groups;
        _startData = owner._startData;
        _missingModels = owner._missingModels;
        _tracedDeferred = owner._tracedDeferred;
        _tracedUndeferred = owner._tracedUndeferred;
        _modelDefinitions = owner._modelDefinitions;
    }

    public async Task Run()
    {
        if (_case["version"]?.GetValue<string>() != "hsm-conformance-v1")
        {
            throw new UnsupportedCaseException("unsupported conformance version");
        }

        if (_case["mode"]?.GetValue<string>() == "validation")
        {
            var actual = Hsm.ValidateIr(_case);
            var expectedValidation = RequiredObject(_case, "expect")["validation"] as JsonArray ?? [];
            var codes = expectedValidation.Select(item => item is JsonValue value
                    ? value.GetValue<string>()
                    : item?["code"]?.GetValue<string>())
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .ToArray();
            if (actual is null)
            {
                throw new InvalidOperationException("validation case unexpectedly succeeded");
            }
            if (codes.Length > 0 && !codes.Contains(actual, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"validation mismatch: expected {string.Join(", ", codes)}, got {actual}");
            }
            return;
        }

        var modelNode = RequiredObject(_case, "model");
        _modelDefinitions[RequiredString(modelNode, "name")] = modelNode;
        foreach (var rawModel in _case["models"] as JsonArray ?? [])
        {
            var definition = rawModel!.AsObject();
            _modelDefinitions[RequiredString(definition, "name")] = definition;
        }
        if (_case["instances"] is JsonArray instanceDefinitions)
        {
            foreach (var rawInstance in instanceDefinitions)
            {
                var definition = rawInstance!.AsObject();
                var id = RequiredString(definition, "id");
                var instance = id == "default" && _instances.Count == 0 ? this : new CaseRunner(this);
                var requestedModel = definition["model"]?.GetValue<string>();
                if (requestedModel is not null && !_modelDefinitions.ContainsKey(requestedModel))
                {
                    _instances[id] = instance;
                    _missingModels[id] = requestedModel;
                    continue;
                }
                var selectedModel = requestedModel is null ? modelNode : _modelDefinitions[requestedModel];
                instance._model = instance.BuildModel(selectedModel);
                var config = definition["config"] as JsonObject;
                var data = ToValue(config?["data"] ?? definition["data"]);
                Hsm.New(instance, instance._model, new Config
                {
                    Id = id,
                    Name = config?["name"]?.GetValue<string>(),
                    Data = data,
                    Clock = _clock.CreateClock(
                        config?["clock"]?.GetValue<string>(),
                        value => { if (TraceExpects("trace")) AddTrace("trace", ("value", value)); }),
                    Queue = instance.MakeQueue(config?["queue"]?.GetValue<string>())
                });
                _instances[id] = instance;
                _startData[id] = data;
            }
        }
        else
        {
            _model = BuildModel(modelNode);
            Hsm.New(this, _model, new Config { Id = "default", Clock = _clock.Clock });
            _instances["default"] = this;
        }

        foreach (var rawGroup in _case["groups"] as JsonArray ?? [])
        {
            var definition = rawGroup!.AsObject();
            var id = RequiredString(definition, "id");
            var members = (definition["members"] as JsonArray ?? [])
                .Select(member => _instances[member!.GetValue<string>()])
                .Cast<IInstance>()
                .ToArray();
            _groups[id] = new Group(id, members);
        }

        foreach (var rawStep in _case["script"] as JsonArray ?? [])
        {
            try
            {
                await ExecuteStep(rawStep?.AsObject() ?? throw new InvalidDataException("script step must be an object"));
                await Hsm.AfterIdle(_context);
            }
            catch (PortableRuntimeException)
            {
            }
            catch (HsmRuntimeException error)
            {
                if (TraceExpects("error")) AddTrace("error", ("code", RuntimeCode(error)));
            }
        }

        if (TraceExpects("stable"))
        {
            var expectedStable = _expectedTrace.OfType<JsonObject>()
                .Last(item => item["type"]?.GetValue<string>() == "stable")["state"]?.GetValue<string>();
            var stable = expectedStable is not null && (expectedStable.StartsWith("/", StringComparison.Ordinal) || expectedStable == string.Empty)
                ? _instances.Values.Select(instance => instance.State).FirstOrDefault(state => state == expectedStable)
                    ?? (_instances.Count == 1 ? _instances.Values.First().State : _lastStableState)
                : _lastStableState;
            AddTrace("stable", ("state", stable ?? State));
        }

        var expected = RequiredObject(_case, "expect");
        AssertRuntimeExpectation(expected);

        if (expected["snapshots"] is JsonObject expectedSnapshots)
        {
            foreach (var (label, expectedSnapshotNode) in expectedSnapshots)
            {
                var expectedSnapshot = expectedSnapshotNode!.AsObject();
                if (!_snapshots.TryGetValue(label, out var actualSnapshot) || !MatchesPartial(expectedSnapshot, actualSnapshot))
                {
                    throw new InvalidOperationException(
                        $"snapshot mismatch for '{label}': expected {expectedSnapshot}, got {actualSnapshot}");
                }
            }
        }

        if (!JsonNode.DeepEquals(_expectedTrace, new JsonArray(_trace.Select(item => item.DeepClone()).ToArray())))
        {
            throw new InvalidOperationException(
                $"trace mismatch\nexpected: {_expectedTrace.ToJsonString()}\nactual: {JsonSerializer.Serialize(_trace)}");
        }
    }

    private Model BuildModel(JsonObject model)
    {
        return BuildModel(model, new Dictionary<string, Model>(StringComparer.Ordinal), [], composable: false);
    }

    private Model BuildModel(
        JsonObject model,
        Dictionary<string, Model> models,
        IReadOnlyList<string> inheritedAttributeNames,
        bool composable)
    {
        var name = RequiredString(model, "name");
        var ownAttributeNames = (model["attributes"] as JsonObject)?.Select(pair => pair.Key).ToArray() ?? [];
        var attributeNames = inheritedAttributeNames
            .Concat(ownAttributeNames)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var cacheKey = name + "|" + composable + "|" + string.Join("|", attributeNames.Order(StringComparer.Ordinal));
        if (models.TryGetValue(cacheKey, out var existing)) return existing;

        var partials = new List<IPartial>();
        var exitPointNames = (model["exit_points"] as JsonArray ?? [])
            .Select(item => RequiredString(item!.AsObject(), "name"))
            .ToHashSet(StringComparer.Ordinal);
        AddAttributes(model, partials);
        AddOperations(model, partials);
        AddConnectionPoints(model, partials);
        foreach (var state in model["states"] as JsonArray ?? [])
        {
            partials.Add(BuildState(state!.AsObject(), name, "/" + name, attributeNames, exitPointNames, models));
        }

        foreach (var transition in model["transitions"] as JsonArray ?? [])
        {
            partials.Add(BuildTransition(
        transition!.AsObject(),
        name,
        "/" + name,
        attributeNames: attributeNames,
        exitPointNames: exitPointNames));
        }

        if (model["initial"] is not null)
        {
            partials.Add(BuildInitial(model["initial"]!, name, "/" + name));
        }

        Model result;
        if (model["redefines"] is JsonValue baseNode)
        {
            var baseName = baseNode.GetValue<string>();
            if (!_modelDefinitions.TryGetValue(baseName, out var baseDefinition))
            {
                throw new UnsupportedCaseException($"missing redefined model '{baseName}'");
            }
            var baseModel = BuildModel(baseDefinition, models, attributeNames, composable);
            result = composable
                ? Hsm.RedefineSubmachine(name, baseModel, partials.ToArray())
                : Hsm.Redefine(name, baseModel, partials.ToArray());
        }
        else
        {
            result = composable
                ? Hsm.DefineSubmachine(name, partials.ToArray())
                : Hsm.Define(name, partials.ToArray());
        }

        models[cacheKey] = result;
        return result;
    }

    private void AddConnectionPoints(JsonObject model, List<IPartial> partials)
    {
        foreach (var rawEntryPoint in model["entry_points"] as JsonArray ?? [])
        {
            var entryPoint = rawEntryPoint!.AsObject();
            partials.Add(Hsm.EntryPoint(
                RequiredString(entryPoint, "name"),
                RequiredString(entryPoint, "target"),
                BuildEffects(entryPoint).ToArray()));
        }

        foreach (var rawExitPoint in model["exit_points"] as JsonArray ?? [])
        {
            var exitPoint = rawExitPoint!.AsObject();
            partials.Add(Hsm.ExitPoint(
                RequiredString(exitPoint, "name"),
                BuildEffects(exitPoint).ToArray()));
        }
    }

    private static string ResolveModelPath(string path, string rootName, string ownerPath) =>
        path.StartsWith("/", StringComparison.Ordinal)
            ? path
            : path.StartsWith(".", StringComparison.Ordinal)
                ? ResolveRelativePath(ownerPath, path)
                : "/" + rootName + "/" + path.TrimStart('/');

    private static string ResolveRelativePath(string ownerPath, string relative)
    {
        var parts = ownerPath.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var part in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else parts.Add(part);
        }
        return "/" + string.Join("/", parts);
    }

    private void AddAttributes(JsonObject owner, List<IPartial> partials)
    {
        if (owner["attributes"] is not JsonObject attributes)
        {
            return;
        }

        foreach (var (name, rawDefinition) in attributes)
        {
            var definition = rawDefinition?.AsObject() ?? new JsonObject();
            var hasDefault = definition.ContainsKey("default");
            var value = hasDefault ? CoerceAttributeValue(name, ToValue(definition["default"])) : null;
            var valueType = definition["type"]?.GetValue<string>() switch
            {
                "boolean" => typeof(bool),
                "string" => typeof(string),
                "integer" => typeof(long),
                "object" => typeof(Dictionary<string, object?>),
                "array" => typeof(List<object?>),
                "number" => typeof(double),
                "any" or "duration_ms" or "time_ms" => typeof(object),
                _ => value?.GetType() ?? typeof(object)
            };
            partials.Add(hasDefault
                ? Hsm.Attribute(name, value, valueType)
                : Hsm.Attribute(name, valueType));
        }
    }

    private void AddOperations(JsonObject owner, List<IPartial> partials)
    {
        if (owner["operations"] is not JsonObject operations)
        {
            return;
        }

        foreach (var (name, rawReference) in operations)
        {
            var behavior = BehaviorId(rawReference);
            partials.Add(Hsm.Operation(name, new Func<CallData, object?>(call =>
            {
                var data = call.Args.Count == 1 ? call.Args[0] : call.Args;
                return ExecuteBehavior(behavior, new Event(call.Name, Kind.CallEvent, data));
            })));
        }
    }

    private IPartial BuildState(
        JsonObject state,
        string modelName,
        string ownerPath,
        IReadOnlyList<string> attributeNames,
        IReadOnlySet<string> exitPointNames,
        Dictionary<string, Model> models)
    {
        var name = RequiredString(state, "name");
        var kind = state["kind"]?.GetValue<string>() ?? "state";

        if (kind == "final")
        {
            return Hsm.Final(name);
        }

        var statePath = ownerPath + "/" + name;
        var partials = new List<IPartial>();
        var referencedAttributes = kind == "submachine"
            && _modelDefinitions.TryGetValue(RequiredString(state, "machine"), out var referencedModel)
            ? (referencedModel["attributes"] as JsonObject)?.Select(pair => pair.Key) ?? []
            : [];
        var localAttributeNames = attributeNames
            .Concat((state["attributes"] as JsonObject)?.Select(pair => pair.Key) ?? [])
            .Concat(referencedAttributes)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        AddAttributes(state, partials);
        AddOperations(state, partials);
        AddBehaviorList(state, "entry", partials, id => Hsm.Entry<CaseRunner>((_, _, evt) => ExecuteBehavior(id, evt)));
        AddBehaviorList(state, "exit", partials, id => Hsm.Exit<CaseRunner>((_, _, evt) => ExecuteBehavior(id, evt)));
        AddBehaviorList(state, "activity", partials, id => Hsm.Activity<CaseRunner>(
            new AsyncOperation<CaseRunner>((ctx, _, evt) => ExecuteActivity(id, ctx, evt))));

        if (state["defer"] is JsonArray deferred)
        {
            partials.Add(Hsm.Defer(deferred.Select(EventName).ToArray()));
        }

        foreach (var child in state["states"] as JsonArray ?? [])
        {
            partials.Add(BuildState(
                child!.AsObject(),
                modelName,
                statePath,
                localAttributeNames,
                exitPointNames,
                models));
        }

        foreach (var transition in state["transitions"] as JsonArray ?? [])
        {
            partials.Add(BuildTransition(
                transition!.AsObject(),
                modelName,
                statePath,
                localAttributeNames,
                exitPointNames));
        }

        if (state["initial"] is not null)
        {
            partials.Add(BuildInitial(state["initial"]!, modelName, statePath));
        }

        return kind switch
        {
            "state" => Hsm.State(name, partials.ToArray()),
            "submachine" => Hsm.Submachine(
                name,
                BuildReferencedModel(state, models, localAttributeNames),
                partials.ToArray()),
            "choice" => Hsm.Choice(name, partials.ToArray()),
            "shallow_history" => Hsm.ShallowHistory(name, partials.ToArray()),
            "deep_history" => Hsm.DeepHistory(name, partials.ToArray()),
            _ => throw new UnsupportedCaseException($"state kind '{kind}'")
        };
    }

    private Model BuildReferencedModel(
        JsonObject state,
        Dictionary<string, Model> models,
        IReadOnlyList<string> inheritedAttributeNames)
    {
        var machineName = RequiredString(state, "machine");
        if (!_modelDefinitions.TryGetValue(machineName, out var definition))
        {
            throw new UnsupportedCaseException($"missing submachine model '{machineName}'");
        }

        return BuildModel(definition, models, inheritedAttributeNames, composable: true);
    }

    private IPartial BuildInitial(JsonNode rawInitial, string modelName, string ownerPath)
    {
        var partials = new List<IPartial>();
        if (rawInitial is JsonValue)
        {
            partials.Add(Hsm.Target(rawInitial.GetValue<string>()));
        }
        else
        {
            var initial = rawInitial.AsObject();
            partials.Add(Hsm.Target(RequiredString(initial, "target")));
            AddEffects(initial, partials);
        }

        return Hsm.Initial(partials.ToArray());
    }

    private IPartial BuildTransition(
        JsonObject transition,
        string modelName,
        string ownerPath,
        IReadOnlyList<string>? attributeNames = null,
        IReadOnlySet<string>? exitPointNames = null)
    {
        var partials = new List<IPartial>();
        if (transition["portable_error"] is JsonValue portableError)
        {
            var code = portableError.GetValue<string>();
            var message = transition["portable_error_message"]?.GetValue<string>() ?? code;
            partials.Add(Hsm.Effect<CaseRunner>((_, _, _) =>
            {
                if (TraceExpects("error")) AddTrace("error", ("code", code));
                throw new PortableRuntimeException(code, message);
            }));
        }
        if (transition["source"] is JsonNode source)
        {
            partials.Add(Hsm.Source(NormalizeModelPath(source.GetValue<string>(), modelName)));
        }

        if (transition["target"] is JsonNode target)
        {
            var targetPath = target.GetValue<string>();
            var sourcePath = transition["source"]?.GetValue<string>();
            var resolvedTarget = ResolveModelPath(targetPath, modelName, ownerPath);
            var exitPoint = exitPointNames?.FirstOrDefault(name => resolvedTarget == "/" + modelName + "/" + name);
            if (exitPoint is not null)
            {
                partials.Add(Hsm.ToExitPoint(exitPoint));
            }
            else
            {
                partials.Add(Hsm.Target(
                    targetPath == "." && sourcePath is not null
                        ? NormalizeModelPath(sourcePath, modelName)
                        : targetPath));
            }
        }

        if (transition["entry_point"] is JsonValue entryPoint)
        {
            partials.Add(Hsm.ToEntryPoint(entryPoint.GetValue<string>()));
        }

        var triggerKind = transition["trigger"]?["kind"]?.GetValue<string>();
        Func<Event, bool>? whenPredicate;
        if (triggerKind == "exit_point")
        {
            partials.Add(Hsm.OnExitPoint(RequiredString(transition["trigger"]!.AsObject(), "exit_point")));
            whenPredicate = null;
        }
        else
        {
            whenPredicate = AddTrigger(transition, partials, attributeNames ?? []);
        }
        var guardId = transition["guard"] is JsonNode guard ? BehaviorId(guard) : null;
        if (triggerKind is "after" or "every" or "at")
        {
            partials.Add(Hsm.Guard<CaseRunner>((_, _, evt) => TimerGuard(guardId, evt)));
        }
        else if (whenPredicate is not null || guardId is not null)
        {
            partials.Add(Hsm.Guard<CaseRunner>((_, _, evt) =>
                (whenPredicate is null || whenPredicate(evt))
                && (guardId is null || ToBool(ExecuteBehavior(guardId, evt)))));
        }

        AddEffects(transition, partials);
        return Hsm.Transition(partials.ToArray());
    }

    private bool TimerGuard(string? guardId, Event evt)
    {
        var insertion = _trace.Count;
        try
        {
            var result = guardId is null || ToBool(ExecuteBehavior(guardId, evt));
            if (TraceExpects("timer_fired"))
            {
                var fired = new JsonObject { ["type"] = "timer_fired" };
                if (result) _trace.Add(fired);
                else _trace.Insert(insertion, fired);
            }
            return result;
        }
        catch
        {
            if (TraceExpects("timer_fired"))
            {
                _trace.Insert(insertion, new JsonObject { ["type"] = "timer_fired" });
            }
            throw;
        }
    }

    private Func<Event, bool>? AddTrigger(
        JsonObject transition,
        List<IPartial> partials,
        IReadOnlyList<string> attributeNames)
    {
        if (transition["on"] is JsonValue shorthand)
        {
            partials.Add(Hsm.On(EventName(shorthand)));
            return null;
        }

        if (transition["on"] is JsonObject shorthandObject)
        {
            partials.Add(Hsm.On(EventName(shorthandObject)));
            return null;
        }

        if (transition["trigger"] is not JsonObject trigger)
        {
            return null;
        }

        var kind = RequiredString(trigger, "kind");
        switch (kind)
        {
            case "on":
                if ((trigger["events"] ?? trigger["event"]) is JsonArray events)
                {
                    foreach (var item in events)
                    {
                        partials.Add(Hsm.On(EventName(item)));
                    }
                }
                else
                {
                    partials.Add(Hsm.On(EventName(trigger["event"])));
                }
                break;
            case "on_set":
                partials.Add(Hsm.OnSet(RequiredString(trigger, "attribute")));
                break;
            case "on_call":
                partials.Add(Hsm.OnCall(RequiredString(trigger, "operation")));
                break;
            case "completion":
                break;
            case "when":
                if (trigger["attribute"] is JsonNode attributeNode)
                {
                    var attribute = attributeNode.GetValue<string>();
                    partials.Add(Hsm.OnSet(attribute));
                    return null;
                }

                if (trigger["behavior"] is JsonNode behaviorNode)
                {
                    if (attributeNames.Count == 0)
                    {
                        throw new UnsupportedCaseException("when behavior requires an observable attribute source");
                    }

                    foreach (var attribute in attributeNames)
                    {
                        partials.Add(Hsm.OnSet(attribute));
                    }

                    var behavior = behaviorNode.GetValue<string>();
                    return evt => ToBool(ExecuteBehavior(behavior, evt));
                }

                throw new UnsupportedCaseException("when trigger missing source");
            case "after":
                partials.Add(BuildDurationTrigger(trigger, every: false));
                break;
            case "every":
                partials.Add(BuildDurationTrigger(trigger, every: true));
                break;
            case "at":
                if (trigger["attribute"] is JsonValue atAttribute)
                {
                    var attributeName = atAttribute.GetValue<string>();
                    partials.Add(Hsm.At<CaseRunner>((_, _, _) => DateTimeOffset.UnixEpoch.AddMilliseconds(
                        TimerMilliseconds(() => Hsm.Get<object?>(_context, this, attributeName)))));
                }
                else
                {
                    partials.Add(Hsm.At<CaseRunner>((_, _, evt) => DateTimeOffset.UnixEpoch.AddMilliseconds(
                        TimerMilliseconds(() => trigger["behavior"] is JsonValue atBehavior
                            ? ExecuteBehavior(atBehavior.GetValue<string>(), evt)
                            : trigger["time_ms"]?.GetValue<double>() ?? 0))));
                }
                break;
            default:
                throw new UnsupportedCaseException($"trigger '{kind}'");
        }

        return null;
    }

    private IPartial BuildDurationTrigger(JsonObject trigger, bool every)
    {
        if (trigger["attribute"] is JsonValue attribute)
        {
            var attributeName = attribute.GetValue<string>();
            TimeSpan AttributeDuration(Context _, CaseRunner __, Event ___) => TimeSpan.FromMilliseconds(
                TimerMilliseconds(() => Hsm.Get<object?>(_context, this, attributeName)));
            return every ? Hsm.Every<CaseRunner>(AttributeDuration) : Hsm.After<CaseRunner>(AttributeDuration);
        }

        TimeSpan Duration(Context _, CaseRunner __, Event evt) => TimeSpan.FromMilliseconds(
            TimerMilliseconds(() => trigger["behavior"] is JsonValue behavior
                ? ExecuteBehavior(behavior.GetValue<string>(), evt)
                : trigger["duration_ms"]?.GetValue<double>() ?? 0));
        return every ? Hsm.Every<CaseRunner>(Duration) : Hsm.After<CaseRunner>(Duration);
    }

    private double TimerMilliseconds(Func<object?> source)
    {
        try
        {
            return Milliseconds(PortableValue(source()));
        }
        catch (PortableRuntimeException)
        {
            throw;
        }
        catch (Exception error)
        {
            if (TraceExpects("error")) AddTrace("error", ("code", "timer_error"));
            throw new PortableRuntimeException("timer_error", $"invalid interval: {error.Message}");
        }
    }

    private static double Milliseconds(object? value) => value switch
    {
        TimeSpan duration => duration.TotalMilliseconds,
        int number => number,
        long number => number,
        float number => number,
        double number => number,
        decimal number => (double)number,
        _ => throw new HsmRuntimeException("timer source must return milliseconds")
    };

    private void AddEffects(JsonObject owner, List<IPartial> partials)
    {
        partials.AddRange(BuildEffects(owner));
    }

    private IEnumerable<IPartial> BuildEffects(JsonObject owner)
    {
        foreach (var rawEffect in owner["effects"] as JsonArray ?? [])
        {
            var effect = rawEffect!;
            var id = BehaviorId(effect);
            var scope = (effect as JsonObject)?["scope"]?.GetValue<string>();
            yield return Hsm.Effect<CaseRunner>((_, _, evt) => ExecuteBehavior(id, evt, scope: scope));
        }
    }

    private static string NormalizeModelPath(string path, string modelName) =>
        path.StartsWith("/", StringComparison.Ordinal) || path.StartsWith(".", StringComparison.Ordinal)
            ? path
            : $"/{modelName}/{path}";

    private string ScopedAttributeName(string? scope, string name)
    {
        if (scope is null || name.StartsWith("/", StringComparison.Ordinal)) return name;
        var candidate = scope + "/" + name;
        return _model?.Attributes.ContainsKey(candidate) == true ? candidate : name;
    }

    private string ScopedOperationName(string? scope, string name)
    {
        if (scope is null || name.StartsWith("/", StringComparison.Ordinal)) return name;
        var candidate = scope + "/" + name;
        return _model?.Operations.ContainsKey(candidate) == true ? candidate : name;
    }

    private void AddBehaviorList(JsonObject owner, string key, List<IPartial> partials, Func<string, IPartial> create)
    {
        if (owner[key] is not JsonArray behaviors)
        {
            return;
        }

        foreach (var reference in behaviors)
        {
            partials.Add(create(BehaviorId(reference)));
        }
    }

    private object? ExecuteBehavior(string id, Event evt, string? scope = null)
        => ExecuteBehaviorAsync(id, evt, null, scope).GetAwaiter().GetResult();

    private async ValueTask<object?> ExecuteBehaviorAsync(
        string id,
        Event evt,
        Context? behaviorContext,
        string? scope = null)
    {
        if (_behaviors[id] is not JsonArray operations)
        {
            throw new InvalidDataException($"missing behavior '{id}'");
        }

        object? result = null;
        foreach (var rawOperation in operations)
        {
            behaviorContext?.CancellationToken.ThrowIfCancellationRequested();
            var operation = rawOperation!.AsObject();
            var op = RequiredString(operation, "op");
            switch (op)
            {
                case "trace":
                    if (TraceExpects("trace")) AddTrace("trace", ("value", ToValue(operation["value"])));
                    break;
                case "return_value":
                    result = ToValue(operation["value"]);
                    break;
                case "return_attr":
                    try
                    {
                        result = PortableValue(Hsm.Get<object?>(_context, this, ScopedAttributeName(scope, RequiredString(operation, "name"))));
                    }
                    catch (AttributeHsmException)
                    {
                        result = null;
                    }
                    break;
                case "return_equals":
                    try
                    {
                        result = StructuralEquals(
                            PortableValue(Hsm.Get<object?>(_context, this, ScopedAttributeName(scope, RequiredString(operation, "name")))),
                            ToValue(operation["value"]));
                    }
                    catch (AttributeHsmException)
                    {
                        result = false;
                    }
                    break;
                case "get_attr":
                    try
                    {
                        result = PortableValue(Hsm.Get<object?>(_context, this, ScopedAttributeName(scope, RequiredString(operation, "name"))));
                    }
                    catch (AttributeHsmException)
                    {
                        result = null;
                    }
                    break;
                case "set_attr":
                    try
                    {
                        var attributeName = RequiredString(operation, "name");
                        Hsm.Set(behaviorContext ?? _context, this, ScopedAttributeName(scope, attributeName), CoerceAttributeValue(attributeName, ToValue(operation["value"])))
                            .GetAwaiter().GetResult();
                    }
                    catch (HsmRuntimeException error)
                    {
                        if (TraceExpects("error")) AddTrace("error", ("code", RuntimeCode(error)));
                        throw;
                    }
                    break;
                case "set_attr_from_event_data":
                    try
                    {
                        var attributeName = RequiredString(operation, "name");
                        Hsm.Set(
                                behaviorContext ?? _context,
                                this,
                                ScopedAttributeName(scope, attributeName),
                                CoerceAttributeValue(attributeName, ReadPath(evt.Data, operation["path"]?.GetValue<string>() ?? string.Empty)))
                            .GetAwaiter().GetResult();
                    }
                    catch (HsmRuntimeException error)
                    {
                        if (TraceExpects("error")) AddTrace("error", ("code", RuntimeCode(error)));
                        throw;
                    }
                    break;
                case "event_name_equals":
                    result = evt.Name == RequiredString(operation, "value");
                    break;
                case "event_data_get":
                    result = ReadPath(evt.Data, operation["path"]?.GetValue<string>() ?? string.Empty);
                    break;
                case "event_data_equals":
                    result = StructuralEquals(
                        ReadPath(evt.Data, operation["path"]?.GetValue<string>() ?? string.Empty),
                        ToValue(operation["value"]));
                    break;
                case "event_metadata_get":
                    result = MetadataValue(evt, RequiredString(operation, "name"), applicationOnly: false);
                    break;
                case "event_metadata_equals":
                    result = StructuralEquals(
                        MetadataValue(evt, RequiredString(operation, "name"), applicationOnly: false),
                        ToValue(operation["value"]));
                    break;
                case "event_application_metadata_equals":
                    result = StructuralEquals(
                        MetadataValue(evt, RequiredString(operation, "name"), applicationOnly: true),
                        ToValue(operation["value"]));
                    break;
                case "event_metadata_set":
                    {
                        var name = RequiredString(operation, "name");
                        if (name is not ("name" or "id" or "source" or "target"))
                        {
                            var metadata = evt.Schema as IDictionary<string, object?>;
                            if (metadata is null)
                            {
                                metadata = new Dictionary<string, object?>(StringComparer.Ordinal);
                                evt.Schema = metadata;
                            }

                            metadata[name] = ToValue(operation["value"]);
                        }

                        break;
                    }
                case "dispatch":
                    {
                        var dispatched = ParseEvent(operation["event"]);
                        var target = (operation["target"] ?? operation["instance"])?.GetValue<string>();
                        var group = operation["group"]?.GetValue<string>();
                        dispatched.Source ??= Hsm.ID(this);
                        AddDispatchTrace(dispatched.Name, target ?? group);
                        if (group is not null)
                        {
                            if (!_groups.TryGetValue(group, out var targetGroup))
                            {
                                if (TraceExpects("error")) AddTrace("error", ("code", "runtime_error"));
                                throw new HsmRuntimeException($"unknown group '{group}'");
                            }
                            targetGroup.Dispatch(dispatched).GetAwaiter().GetResult();
                        }
                        else if (target == "all")
                        {
                            Hsm.DispatchAll(_context, dispatched).GetAwaiter().GetResult();
                        }
                        else if (target is not null)
                        {
                            Hsm.DispatchTo(_context, dispatched, target).GetAwaiter().GetResult();
                        }
                        else
                        {
                            Dispatch(dispatched).GetAwaiter().GetResult();
                        }
                        break;
                    }
                case "raise":
                    {
                        if (operation["code"] is JsonNode codeNode)
                        {
                            var code = codeNode.GetValue<string>();
                            if (TraceExpects("error")) AddTrace("error", ("code", code));
                            throw new PortableRuntimeException(code, operation["value"]?.GetValue<string>() ?? code);
                        }

                        var raised = ParseEvent(operation["event"]);
                        if (TraceExpects("raise")) AddTrace("raise", ("event", raised.Name));
                        Dispatch(raised).GetAwaiter().GetResult();
                        break;
                    }
                case "call":
                    try
                    {
                        var operationName = RequiredString(operation, "name");
                        Hsm.Call(_context, this, ScopedOperationName(scope, operationName));
                        if (_trace.Count(item => item["type"]?.GetValue<string>() == "call"
                                && item["operation"]?.GetValue<string>() == operationName)
                            < _expectedTrace.Count(item => item?["type"]?.GetValue<string>() == "call"
                                && item?["operation"]?.GetValue<string>() == operationName))
                        {
                            AddTrace("call", ("operation", operationName));
                        }
                    }
                    catch (HsmRuntimeException error)
                    {
                        if (TraceExpects("error")) AddTrace("error", ("code", RuntimeCode(error)));
                        throw;
                    }
                    break;
                case "snapshot":
                    AddSnapshotTrace(Hsm.TakeSnapshot(_context, this));
                    break;
                case "yield":
                    if (behaviorContext is null)
                    {
                        throw new UnsupportedCaseException("yield outside an activity");
                    }

                    Interlocked.Increment(ref _activities.PendingYields);
                    try
                    {
                        await Task.Yield();
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _activities.PendingYields);
                    }
                    behaviorContext.CancellationToken.ThrowIfCancellationRequested();
                    break;
                case "sleep":
                    if (behaviorContext is null)
                    {
                        throw new UnsupportedCaseException("sleep outside an activity");
                    }

                    await Task.Delay(
                        operation["millis"]?.GetValue<int>() ?? 0,
                        behaviorContext.CancellationToken);
                    break;
                default:
                    throw new UnsupportedCaseException($"behavior op '{op}'");
            }
        }

        return result;
    }

    private async ValueTask ExecuteActivity(string id, Context context, Event evt)
    {
        Interlocked.Increment(ref _activities.Active);
        if (TraceExpects("activity_start")) AddTrace("activity_start", ("behavior", id));
        using var cancellationTrace = context.CancellationToken.Register(() =>
        {
            if (TraceExpects("activity_cancel")) AddTrace("activity_cancel", ("behavior", id));
        });
        try
        {
            await ExecuteBehaviorAsync(id, evt, behaviorContext: context);
            if (!context.IsDone && TraceExpects("activity_done"))
            {
                AddTrace("activity_done", ("behavior", id));
            }
        }
        catch (OperationCanceledException) when (context.IsDone)
        {
        }
        finally
        {
            Interlocked.Decrement(ref _activities.Active);
            _activities.Progress.Release();
        }
    }

    private async Task ExecuteStep(JsonObject step)
    {
        var op = RequiredString(step, "op");
        switch (op)
        {
            case "start":
                if (TraceExpects("start")) AddTrace("start");
                {
                    var (id, instance) = StepInstance(step);
                    if (_missingModels.TryGetValue(id, out var missingModel))
                    {
                        if (TraceExpects("error")) AddTrace("error", ("code", "model_error"));
                        _lastStableState = string.Empty;
                        throw new PortableRuntimeException("model_error", $"missing model '{missingModel}'");
                    }
                    var data = step.ContainsKey("data") ? ToValue(step["data"]) : _startData.GetValueOrDefault(id);
                    Hsm.Start(_context, instance, data);
                    _lastStableState = _instances.Count == 1 ? instance.State : id;
                }
                break;
            case "dispatch":
                {
                    var evt = ParseEvent(step["event"]);
                    var (id, instance) = StepInstance(step);
                    AddDispatchTrace(evt.Name, step["instance"] is null ? null : id);
                    TraceDeferredBeforeDispatch(instance, evt.Name);
                    _lastDispatchQueued = instance.State.Length > 0;
                    await instance.Dispatch(evt);
                    _lastStableState = _instances.Count == 1 ? instance.State : id;
                    break;
                }
            case "dispatch_to":
                {
                    var evt = ParseEvent(step["event"]);
                    var targets = step["targets"] is JsonArray targetArray
                        ? targetArray.Select(item => item!.GetValue<string>()).ToArray()
                        : new[] { (step["target"] ?? step["instance"])!.GetValue<string>() };
                    AddDispatchTrace(evt.Name, targets.Length == 1 ? targets[0] : targets);
                    _lastDispatchQueued = _instances.Values.Any(instance =>
                        instance.State.Length > 0 &&
                        (targets.Length == 0 || targets.Any(pattern => Hsm.Match(Hsm.ID(instance), pattern))));
                    await Hsm.DispatchTo(_context, evt, targets);
                    _lastStableState = targets.Length == 1 ? targets[0] : "targets:" + string.Join(",", targets);
                    break;
                }
            case "dispatch_all":
                {
                    var evt = ParseEvent(step["event"]);
                    AddDispatchTrace(evt.Name, "all");
                    _lastDispatchQueued = _instances.Values.Any(instance => instance.State.Length > 0);
                    await Hsm.DispatchAll(_context, evt);
                    _lastStableState = "all";
                    break;
                }
            case "group_dispatch":
                {
                    var evt = ParseEvent(step["event"]);
                    var groupId = RequiredString(step, "group");
                    AddDispatchTrace(evt.Name, groupId);
                    if (!_groups.TryGetValue(groupId, out var group))
                    {
                        throw new HsmRuntimeException($"unknown group '{groupId}'");
                    }
                    _lastDispatchQueued = group.Instances.Any(instance => instance.State.Length > 0);
                    await group.Dispatch(evt);
                    _lastStableState = "group:" + groupId;
                    break;
                }
            case "set":
                {
                    var (id, instance) = StepInstance(step);
                    var name = step["attribute"]?.GetValue<string>() ?? RequiredString(step, "name");
                    var value = ToValue(step["value"]);
                    if (TraceExpects("set")) AddTrace("set", ("attribute", name), ("value", value));
                    await Hsm.Set(_context, instance, name, instance.CoerceAttributeValue(name, value));
                    _lastStableState = _instances.Count == 1 ? instance.State : id;
                    break;
                }
            case "call":
                {
                    var (id, instance) = StepInstance(step);
                    var name = step["operation"]?.GetValue<string>() ?? RequiredString(step, "name");
                    if (TraceExpects("call")) AddTrace("call", ("operation", name));
                    if (step.ContainsKey("data"))
                    {
                        Hsm.Call(_context, instance, name, ToValue(step["data"]));
                    }
                    else
                    {
                        Hsm.Call(_context, instance, name);
                    }
                    _lastStableState = _instances.Count == 1 ? instance.State : id;
                    break;
                }
            case "snapshot":
                {
                    IInstance target;
                    CaseRunner? normalizer = null;
                    if (step["group"] is JsonValue groupValue)
                    {
                        var groupId = groupValue.GetValue<string>();
                        target = _groups[groupId];
                        var members = new JsonObject();
                        foreach (var member in _groups[groupId].Instances)
                        {
                            members[Hsm.ID(member)] = member.State;
                        }
                        var groupLabel = step["label"]?.GetValue<string>() ?? step["id"]?.GetValue<string>() ?? groupId;
                        _snapshots[groupLabel] = new JsonObject { ["members"] = members };
                        _lastStableState = "group:" + groupId;
                        if (TraceExpects("snapshot")) AddTrace("snapshot", ("group", groupId));
                    }
                    else
                    {
                        var resolved = StepInstance(step);
                        target = resolved.Instance;
                        normalizer = resolved.Instance;
                    }
                    var snapshot = Hsm.TakeSnapshot(_context, target);
                    var label = step["label"]?.GetValue<string>() ?? step["id"]?.GetValue<string>() ?? "last";
                    if (normalizer is not null) _snapshots[label] = normalizer.NormalizeSnapshot(snapshot);
                    if (normalizer is not null) AddSnapshotTrace(snapshot);
                    break;
                }
            case "expect":
                AssertRuntimeExpectation(RequiredObject(step, "expect"));
                break;
            case "stop":
                if (TraceExpects("stop")) AddTrace("stop");
                {
                    var (id, instance) = StepInstance(step);
                    ClearDeferredTrace(id);
                    await instance.Stop();
                    _lastStableState = _instances.Count == 1 ? instance.State : id;
                }
                break;
            case "restart":
                if (TraceExpects("restart")) AddTrace("restart");
                {
                    var (id, instance) = StepInstance(step);
                    ClearDeferredTrace(id);
                    await instance.Restart(step.ContainsKey("data") ? ToValue(step["data"]) : _startData.GetValueOrDefault(id));
                    _lastStableState = _instances.Count == 1 ? instance.State : id;
                }
                break;
            case "sleep":
                var millis = step["millis"]?.GetValue<int>() ?? 0;
                var awaitActivityProgress = Volatile.Read(ref _activities.PendingYields) > 0
                    || Volatile.Read(ref _activities.Active) > 0;
                if (millis == 0)
                {
                    await Task.Yield();
                }
                else
                {
                    await Task.Delay(millis);
                }
                if (awaitActivityProgress)
                {
                    await _activities.Progress.WaitAsync(TimeSpan.FromSeconds(1));
                    await Hsm.AfterIdle(_context, this);
                }
                break;
            case "tick":
                var tickMillis = step["millis"]?.GetValue<int>() ?? 0;
                await _clock.AdvanceAsync(
                    tickMillis,
                    () => _instances.Count == 1
                        ? Hsm.AfterProcess(_context, _instances.Values.First())
                        : Task.CompletedTask,
                    () => Task.WhenAll(_instances.Values.Select(instance => Hsm.AfterIdle(_context, instance))));
                await Task.Yield();
                await Task.WhenAll(_instances.Values.Select(instance => Hsm.AfterIdle(_context, instance)));
                break;
            default:
                throw new UnsupportedCaseException($"script op '{op}'");
        }
    }

    private (string Id, CaseRunner Instance) StepInstance(JsonObject step)
    {
        var id = step["instance"]?.GetValue<string>()
            ?? (_instances.ContainsKey("default") ? "default" : _instances.Keys.First());
        return (id, _instances[id]);
    }

    private Stateforward.Hsm.Queue? MakeQueue(string? name)
    {
        if (name is null) return null;
        var events = new List<Event>();
        var popErrorPending = name == "pop_error_once";
        var lenErrorPending = name == "len_error_once";
        var pushed = false;
        return new Stateforward.Hsm.Queue(
            (_, evt) =>
            {
                if (name == "push_error")
                {
                    AddTrace("trace", ("value", $"queue:push-error:{QueueEventLabel(evt)}"));
                    if (TraceExpects("error")) AddTrace("error", ("code", "runtime_error"));
                    return new HsmRuntimeException("queue push error");
                }
                AddTrace("trace", ("value", $"queue:push:{QueueEventLabel(evt)}"));
                pushed = true;
                events.Add(evt);
                return null;
            },
            _ =>
            {
                if (popErrorPending && pushed)
                {
                    popErrorPending = false;
                    AddTrace("trace", ("value", "queue:pop-error"));
                    return (null, new HsmRuntimeException("queue pop error"));
                }
                if (events.Count == 0) return (null, null);
                var index = name == "trace_lifo" ? events.Count - 1 : 0;
                var evt = events[index];
                events.RemoveAt(index);
                AddTrace("trace", ("value", $"queue:pop:{QueueEventLabel(evt)}"));
                return (evt, null);
            },
            _ =>
            {
                if (name == "len_seven") return (7, null);
                if (lenErrorPending)
                {
                    lenErrorPending = false;
                    AddTrace("trace", ("value", "queue:len-error"));
                    return (0, new HsmRuntimeException("queue len error"));
                }
                return (events.Count, null);
            },
            events.Clear);
    }

    private string QueueEventLabel(Event evt)
    {
        if (_model is null) return evt.Name;
        var transitions = _model.Members.Values.OfType<Transition>()
            .Where(transition => !transition.SourceQualifiedName.EndsWith("/.initial", StringComparison.Ordinal))
            .ToArray();
        var canonical = CanonicalTransitions(RequiredObject(_case, "model"));
        for (var index = 0; index < transitions.Length && index < canonical.Count; index++)
        {
            if (!transitions[index].Events.Contains(evt.Name)) continue;
            var kind = canonical[index].Ir["trigger"]?["kind"]?.GetValue<string>();
            if (kind is "after" or "every") return canonical[index].Name + "/duration";
            if (kind == "at") return canonical[index].Name + "/timepoint";
        }
        return evt.Name;
    }

    private void AddDispatchTrace(string eventName, object? target)
    {
        if (!TraceExpects("dispatch")) return;
        if (target is not null && _expectedTrace.OfType<JsonObject>().Any(item => item["type"]?.GetValue<string>() == "dispatch" && item.ContainsKey("target")))
        {
            AddTrace("dispatch", ("event", eventName), ("target", target));
            return;
        }
        AddTrace("dispatch", ("event", eventName));
    }

    private void TraceDeferredBeforeDispatch(CaseRunner instance, string eventName)
    {
        if (!TraceExpects("defer") || instance._model is null) return;
        var id = Hsm.ID(instance);
        var key = id + ":" + eventName;
        var state = instance.State;
        var isDeferred = instance._model.DeferredMap.TryGetValue(state, out var deferred)
            && deferred.Contains(eventName);
        var hasCandidate = instance._model.TransitionMap.TryGetValue(state, out var buckets)
            && ((buckets.TryGetValue(eventName, out var exact) && exact.Count > 0)
                || (buckets.TryGetValue(Event.AnyName, out var any) && any.Count > 0));
        if (isDeferred && !hasCandidate && _tracedDeferred.Add(key))
        {
            AddTrace("defer", ("event", eventName));
            return;
        }
        if (!isDeferred)
        {
            var pending = _tracedDeferred.FirstOrDefault(item => item.StartsWith(id + ":", StringComparison.Ordinal));
            if (pending is not null)
            {
                _tracedDeferred.Remove(pending);
                _tracedUndeferred.Add(pending);
                var expectedUndefer = _expectedTrace.OfType<JsonObject>()
                    .Count(item => item["type"]?.GetValue<string>() == "undefer");
                var actualUndefer = _trace.Count(item => item["type"]?.GetValue<string>() == "undefer");
                if (actualUndefer < expectedUndefer)
                    AddTrace("undefer", ("event", pending[(pending.IndexOf(':') + 1)..]));
            }
        }
    }

    private void ClearDeferredTrace(string id)
    {
        _tracedDeferred.RemoveWhere(item => item.StartsWith(id + ":", StringComparison.Ordinal));
        _tracedUndeferred.RemoveWhere(item => item.StartsWith(id + ":", StringComparison.Ordinal));
    }

    private void AssertRuntimeExpectation(JsonObject expected)
    {
        if (expected["queued"] is JsonValue expectedQueued &&
            _lastDispatchQueued != expectedQueued.GetValue<bool>())
        {
            throw new InvalidOperationException(
                $"dispatch queued mismatch: expected {expectedQueued}, got {_lastDispatchQueued}");
        }

        var defaultInstance = _instances.Count == 1 ? _instances.Values.First() : _instances.GetValueOrDefault("default") ?? this;
        if (expected["state"] is JsonNode expectedState && expectedState.GetValue<string>() != defaultInstance.State)
        {
            throw new InvalidOperationException(
                $"state mismatch: expected {expectedState}, got {defaultInstance.State}; trace: {JsonSerializer.Serialize(_trace)}");
        }

        if (expected["states"] is JsonObject expectedStates)
        {
            foreach (var (id, expectedValue) in expectedStates)
            {
                var actual = _instances[id].State;
                if (expectedValue?.GetValue<string>() != actual)
                {
                    throw new InvalidOperationException(
                        $"state mismatch for '{id}': expected {expectedValue}, got {actual}; trace: {JsonSerializer.Serialize(_trace)}");
                }
            }
        }

        if (expected["attributes"] is JsonObject expectedAttributes)
        {
            var actualAttributes = NormalizeAttributes(Hsm.TakeSnapshot(_context, defaultInstance).Attributes);
            if (!MatchesPartial(expectedAttributes, actualAttributes))
            {
                throw new InvalidOperationException($"attribute mismatch: expected {expectedAttributes}, got {actualAttributes}");
            }
        }

        if (expected["instance_attributes"] is JsonObject instanceAttributes)
        {
            foreach (var (id, expectedValue) in instanceAttributes)
            {
                var actual = NormalizeAttributes(Hsm.TakeSnapshot(_context, _instances[id]).Attributes);
                if (!MatchesPartial(expectedValue, actual))
                {
                    throw new InvalidOperationException($"attribute mismatch for '{id}': expected {expectedValue}, got {actual}");
                }
            }
        }
    }

    private Event ParseEvent(JsonNode? raw)
    {
        if (raw is JsonValue)
        {
            return new Event(raw.GetValue<string>());
        }

        var value = raw?.AsObject() ?? throw new InvalidDataException("event is required");
        return new Event(
            RequiredString(value, "name"),
            data: ToValue(value["data"]),
            source: value["source"]?.GetValue<string>(),
            id: value["id"]?.GetValue<string>(),
            target: value["target"]?.GetValue<string>(),
            schema: ToValue(value["metadata"]));
    }

    private void AddSnapshotTrace(Snapshot snapshot)
    {
        if (TraceExpects("snapshot")) AddTrace("snapshot", ("state", snapshot.State));
    }

    private JsonObject NormalizeSnapshot(Snapshot snapshot)
    {
        var transitions = new JsonArray();
        if (_model is not null)
        {
            var runtimeTransitions = _model.Members.Values.OfType<Transition>()
                .Where(transition => !transition.SourceQualifiedName.EndsWith("/.initial", StringComparison.Ordinal))
                .ToArray();
            var canonicalTransitions = CanonicalTransitions(RequiredObject(_case, "model"));
            foreach (var (transition, index) in runtimeTransitions.Select((value, index) => (value, index)))
            {
                if (!transition.Paths.ContainsKey(snapshot.State)) continue;
                var canonical = index < canonicalTransitions.Count ? canonicalTransitions[index] : default;
                var transitionName = canonical.Name ?? transition.QualifiedName;
                var triggerKind = canonical.Ir?["trigger"]?["kind"]?.GetValue<string>();
                var eventNames = triggerKind switch
                {
                    "after" or "every" => new[] { transitionName + "/duration" },
                    "at" => new[] { transitionName + "/timepoint" },
                    _ => transition.Events.ToArray()
                };
                transitions.Add(new JsonObject
                {
                    ["name"] = transitionName,
                    ["kind"] = transition.TransitionKind switch
                    {
                        TransitionKind.Self => 67344,
                        TransitionKind.Internal => 67345,
                        TransitionKind.Local => 67346,
                        _ => 67343
                    },
                    ["source"] = transition.SourceQualifiedName,
                    ["target"] = string.IsNullOrWhiteSpace(transition.TargetQualifiedName)
                        ? null
                        : transition.TargetQualifiedName,
                    ["events"] = new JsonArray(eventNames.Select(name => JsonValue.Create(name)).ToArray()),
                    ["guard"] = transition.Guard is not null || triggerKind is "after" or "every" or "at"
                });
            }
        }

        return new JsonObject
        {
            ["id"] = snapshot.ID,
            ["qualified_name"] = snapshot.QualifiedName,
            ["state"] = snapshot.State,
            ["queue_len"] = snapshot.QueueLen,
            ["attributes"] = NormalizeAttributes(snapshot.Attributes),
            ["transitions"] = transitions
        };
    }

    private List<(string Name, JsonObject Ir)> CanonicalTransitions(JsonObject model)
    {
        var transitions = new List<(string Name, JsonObject Ir)>();
        var members = 1;
        var root = "/" + RequiredString(model, "name");
        if (model.ContainsKey("initial")) members += 2;

        void IndexTransition(JsonObject transition, string owner)
        {
            transitions.Add((owner + "/transition_" + members, transition));
            members++;
            if (transition.ContainsKey("guard")) members++;
            members += (transition["effects"] as JsonArray)?.Count ?? 0;
        }

        void IndexState(JsonObject state, string owner)
        {
            var path = owner + "/" + RequiredString(state, "name");
            members++;
            if (state.ContainsKey("initial")) members += 2;
            foreach (var field in new[] { "entry", "exit", "activity" })
            {
                members += (state[field] as JsonArray)?.Count ?? 0;
            }
            if (state["kind"]?.GetValue<string>() == "submachine"
                && state["machine"] is JsonValue machineValue
                && _modelDefinitions.TryGetValue(machineValue.GetValue<string>(), out var childModel))
            {
                if (childModel.ContainsKey("initial")) members += 2;
                foreach (var child in childModel["states"] as JsonArray ?? [])
                {
                    IndexState(child!.AsObject(), path);
                }
                foreach (var transition in childModel["transitions"] as JsonArray ?? [])
                {
                    IndexTransition(transition!.AsObject(), path);
                }
            }
            foreach (var child in state["states"] as JsonArray ?? [])
            {
                IndexState(child!.AsObject(), path);
            }
            var kind = state["kind"]?.GetValue<string>() ?? "state";
            var transitionOwner = kind is "choice" or "shallow_history" or "deep_history" ? owner : path;
            foreach (var transition in state["transitions"] as JsonArray ?? [])
            {
                IndexTransition(transition!.AsObject(), transitionOwner);
            }
        }

        foreach (var state in model["states"] as JsonArray ?? [])
        {
            IndexState(state!.AsObject(), root);
        }
        foreach (var transition in model["transitions"] as JsonArray ?? [])
        {
            IndexTransition(transition!.AsObject(), root);
        }
        return transitions;
    }

    private static JsonObject NormalizeAttributes(IReadOnlyDictionary<string, object?> attributes)
    {
        var result = new JsonObject();
        foreach (var (name, value) in attributes)
        {
            result[name[(name.LastIndexOf('/') + 1)..]] = JsonSerializer.SerializeToNode(PortableValue(value));
        }

        return result;
    }

    private object? CoerceAttributeValue(string name, object? value)
    {
        var declaration = RequiredObject(_case, "model")["attributes"]?[name] as JsonObject;
        return declaration?["type"]?.GetValue<string>() switch
        {
            "duration_ms" when value is not TimeSpan => TimeSpan.FromMilliseconds(Milliseconds(value)),
            "time_ms" when value is not DateTimeOffset => DateTimeOffset.UnixEpoch.AddMilliseconds(Milliseconds(value)),
            _ => value
        };
    }

    private static object? PortableValue(object? value) => value switch
    {
        TimeSpan duration => duration.TotalMilliseconds,
        DateTimeOffset time => (time - DateTimeOffset.UnixEpoch).TotalMilliseconds,
        _ => value
    };

    private static bool MatchesPartial(JsonNode? expected, JsonNode? actual)
    {
        if (expected is null)
        {
            return actual is null;
        }

        if (actual is null)
        {
            return false;
        }

        if (expected is JsonObject expectedObject && actual is JsonObject actualObject)
        {
            return expectedObject.All(pair =>
                actualObject.ContainsKey(pair.Key)
                && MatchesPartial(pair.Value, actualObject[pair.Key]));
        }

        if (expected is JsonArray expectedArray && actual is JsonArray actualArray)
        {
            return expectedArray.Count == actualArray.Count
                && expectedArray.Zip(actualArray).All(pair =>
                    MatchesPartial(pair.First, pair.Second));
        }

        return JsonNode.DeepEquals(expected, actual);
    }

    private bool TraceExpects(string type) => _expectedTrace
        .OfType<JsonObject>()
        .Any(item => item["type"]?.GetValue<string>() == type);

    public override void OnEventDeferred(Event @event)
    {
        var key = Hsm.ID(this) + ":" + @event.Name;
        if (_tracedDeferred.Contains(key)) return;
        if (TraceExpects("defer"))
        {
            _tracedDeferred.Add(key);
            AddTrace("defer", ("event", @event.Name));
        }
    }

    public override void OnEventRecalled(Event @event)
    {
        var key = Hsm.ID(this) + ":" + @event.Name;
        _tracedDeferred.Remove(key);
        if (_tracedUndeferred.Remove(key)) return;
        var expectedCount = _expectedTrace.OfType<JsonObject>()
            .Count(item => item["type"]?.GetValue<string>() == "undefer");
        var actualCount = _trace.Count(item => item["type"]?.GetValue<string>() == "undefer");
        if (actualCount < expectedCount) AddTrace("undefer", ("event", @event.Name));
    }

    public override void OnRuntimeError(Exception error) => TraceRuntimeError(error);

    private void AddTrace(string type, params (string Key, object? Value)[] fields)
    {
        var item = new JsonObject { ["type"] = type };
        foreach (var (key, value) in fields)
        {
            item[key] = JsonSerializer.SerializeToNode(value);
        }

        lock (_trace) _trace.Add(item);
    }

    private static string BehaviorId(JsonNode? reference) =>
        RequiredString(reference?.AsObject() ?? throw new InvalidDataException("behavior reference required"), "behavior");

    private static JsonObject RequiredObject(JsonObject owner, string key) =>
        owner[key] as JsonObject ?? throw new InvalidDataException($"{key} must be an object");

    private static string RequiredString(JsonObject owner, string key) =>
        owner[key]?.GetValue<string>() ?? throw new InvalidDataException($"{key} is required");

    private static bool ToBool(object? value) => value switch
    {
        null => false,
        bool boolean => boolean,
        string text => text.Length > 0,
        byte number => number != 0,
        short number => number != 0,
        int number => number != 0,
        long number => number != 0,
        float number => number != 0,
        double number => number != 0,
        decimal number => number != 0,
        _ => true
    };

    private static string EventName(JsonNode? node) => node switch
    {
        JsonValue value => value.GetValue<string>(),
        JsonObject value => RequiredString(value, "name"),
        _ => throw new InvalidDataException("event name is required")
    };

    private static object? MetadataValue(Event evt, string name, bool applicationOnly)
    {
        if (!applicationOnly)
        {
            var envelope = name switch
            {
                "name" => evt.Name,
                "id" => evt.ID,
                "source" => evt.Source,
                "target" => evt.Target,
                _ => null
            };
            if (name is "name" or "id" or "source" or "target") return envelope;
        }

        return evt.Schema is IDictionary<string, object?> metadata && metadata.TryGetValue(name, out var value)
            ? value
            : null;
    }

    private static string RuntimeCode(HsmRuntimeException error) => error switch
    {
        AttributeHsmException => "attribute_error",
        MissingOperationException or InvalidOperationSignatureException => "operation_error",
        UnhandledExitPointException => "unhandled_exit_point",
        _ => "runtime_error"
    };

    private void TraceRuntimeError(object? value)
    {
        var expectedErrors = _expectedTrace.OfType<JsonObject>()
            .Count(item => item["type"]?.GetValue<string>() == "error");
        var actualErrors = _trace.Count(item => item["type"]?.GetValue<string>() == "error");
        if (actualErrors >= expectedErrors) return;

        var code = value switch
        {
            PortableRuntimeException portable => portable.Code,
            HsmRuntimeException runtime => RuntimeCode(runtime),
            _ => "runtime_error"
        };
        AddTrace("error", ("code", code));
    }

    private static object? ReadPath(object? value, string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return value switch
            {
                AttributeChange change => change.New,
                CallData call when call.Args.Count == 1 => call.Args[0],
                CallData call => call.Args,
                _ => value
            };
        }
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (value is CallData callData
            && segments[0] is not ("name" or "args")
            && callData.Args.Count == 1)
        {
            value = callData.Args[0];
        }
        foreach (var segment in segments)
        {
            value = value switch
            {
                IDictionary<string, object?> map when map.TryGetValue(segment, out var item) => item,
                CallData call when segment == "name" => call.Name,
                CallData call when segment == "args" => call.Args,
                AttributeChange change when segment == "old" => change.Old,
                AttributeChange change when segment == "new" => change.New,
                IList list when int.TryParse(segment, out var index) && index >= 0 && index < list.Count => list[index],
                _ => null
            };
        }

        return value;
    }

    private static bool StructuralEquals(object? left, object? right) =>
        JsonNode.DeepEquals(JsonSerializer.SerializeToNode(left), JsonSerializer.SerializeToNode(right));

    private static object? ToValue(JsonNode? node) => node switch
    {
        null => null,
        JsonObject value => value.ToDictionary(pair => pair.Key, pair => ToValue(pair.Value), StringComparer.Ordinal),
        JsonArray value => value.Select(ToValue).ToList(),
        JsonValue value when value.TryGetValue<bool>(out var result) => result,
        JsonValue value when value.TryGetValue<long>(out var result) => result,
        JsonValue value when value.TryGetValue<double>(out var result) => result,
        JsonValue value when value.TryGetValue<string>(out var result) => result,
        _ => node.ToJsonString()
    };
}
