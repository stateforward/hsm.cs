using System.Text.Json.Nodes;

namespace Stateforward.Hsm;

public static partial class Hsm
{
    public static string? ValidateIr(JsonObject testCase) => IrValidator.Validate(testCase);
}

internal static class IrValidator
{
    public static string? Validate(JsonObject testCase)
    {
        var models = new List<JsonObject> { Object(testCase["model"]) };
        models.AddRange(Array(testCase["models"]).Select(Object));

        var modelNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            var name = String(model, "name");
            if (!ValidName(name)) return "invalid_name";
            if (!modelNames.Add(name)) return "duplicate_model";
        }

        foreach (var model in models)
        {
            var error = ValidateModel(model, testCase, modelNames);
            if (error is not null) return error;
        }

        var behaviorError = ValidateBehaviors(testCase);
        if (behaviorError is not null) return behaviorError;

        var instanceIds = new HashSet<string>(StringComparer.Ordinal);
        var instances = Array(testCase["instances"]);
        if (instances.Count == 0) instanceIds.Add("default");
        foreach (var raw in instances)
        {
            var id = String(Object(raw), "id");
            if (!instanceIds.Add(id)) return "duplicate_instance";
        }

        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in Array(testCase["groups"]))
        {
            if (!groupIds.Add(String(Object(raw), "id"))) return "duplicate_group";
        }
        groupIds.Clear();
        foreach (var raw in Array(testCase["groups"]))
        {
            var group = Object(raw);
            if (!groupIds.Add(String(group, "id"))) return "duplicate_group";
            var members = Array(group["members"]);
            if (members.Count < 2) return "invalid_group_cardinality";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in members)
            {
                var id = member is JsonValue value
                    ? value.GetValue<string>()
                    : String(Object(member), "id");
                if (!seen.Add(id)) return "duplicate_group_member";
                if (!instanceIds.Contains(id)) return "unknown_group_member";
            }
        }

        return ValidateCycles(models);
    }

    private static string? ValidateModel(JsonObject model, JsonObject testCase, HashSet<string> modelNames)
    {
        var attributes = ObjectOrEmpty(model["attributes"]);
        foreach (var (name, raw) in attributes)
        {
            if (!ValidName(name)) return "invalid_name";
            if (raw is not JsonObject declaration) return "invalid_attribute";
            if (!declaration.ContainsKey("type") && !declaration.ContainsKey("default")) return "invalid_attribute";
            if (declaration.ContainsKey("type") && declaration.ContainsKey("default")
                && !MatchesType(declaration["default"], declaration["type"]?.GetValue<string>())) return "invalid_attribute";
        }

        foreach (var name in ObjectOrEmpty(model["operations"]).Select(pair => pair.Key))
        {
            if (!ValidName(name)) return "invalid_name";
        }
        foreach (var (_, declaration) in ObjectOrEmpty(model["operations"]))
        {
            if (MissingBehaviorReference(declaration, testCase)) return "missing_behavior";
        }

        if (!model.ContainsKey("initial")) return "missing_initial";
        if (model["initial"] is JsonObject initial && IsEmptyArray(initial, "effects")) return "empty_behavior_array";
        if (model["initial"] is JsonObject rootInitial && MissingBehaviorReference(rootInitial["effects"], testCase))
            return "missing_behavior";
        if (IsEmptyArray(model, "entry_points") || IsEmptyArray(model, "exit_points")) return "invalid_submachine_contents";

        var states = Array(model["states"]);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        var topNames = new HashSet<string>(StringComparer.Ordinal);
        var duplicate = IndexStates(states, "/" + String(model, "name"), paths, kinds, topNames);
        if (duplicate is not null) return duplicate;

        var pointsError = ValidateConnectionPoints(model, paths, topNames);
        if (pointsError is not null) return pointsError;
        foreach (var field in new[] { "entry_points", "exit_points" })
        {
            foreach (var point in Array(model[field]).Select(Object))
            {
                if (IsEmptyArray(point, "effects")) return "empty_behavior_array";
                if (MissingBehaviorReference(point["effects"], testCase)) return "missing_behavior";
            }
        }

        var modelError = ValidateTransitionList(
            Array(model["transitions"]), model, testCase, paths, kinds, "/" + String(model, "name"), true, attributes);
        if (modelError is not null) return modelError;

        var root = "/" + String(model, "name");
        var initialError = ValidateInitial(model["initial"], root, paths);
        if (initialError is not null) return initialError;
        return ValidateStates(states, root, root, model, testCase, paths, kinds, attributes, modelNames);
    }

    private static string? IndexStates(
        JsonArray states,
        string owner,
        HashSet<string> paths,
        Dictionary<string, string> kinds,
        HashSet<string> topNames)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in states)
        {
            var state = Object(raw);
            var name = String(state, "name");
            if (!ValidName(name)) return "invalid_name";
            if (!names.Add(name)) return "duplicate_state";
            if (owner.Count(c => c == '/') == 1) topNames.Add(name);
            var path = Join(owner, name);
            paths.Add(path);
            kinds[path] = state["kind"]?.GetValue<string>() ?? "state";
            var error = IndexStates(Array(state["states"]), path, paths, kinds, topNames);
            if (error is not null) return error;
        }
        return null;
    }

    private static string? ValidateStates(
        JsonArray states,
        string owner,
        string root,
        JsonObject model,
        JsonObject testCase,
        HashSet<string> paths,
        Dictionary<string, string> kinds,
        JsonObject attributes,
        HashSet<string> modelNames)
    {
        foreach (var raw in states)
        {
            var state = Object(raw);
            var name = String(state, "name");
            var path = Join(owner, name);
            var kind = state["kind"]?.GetValue<string>() ?? "state";

            foreach (var field in new[] { "entry", "exit", "activity" })
            {
                if (IsEmptyArray(state, field)) return "empty_behavior_array";
                var missing = MissingBehaviorReference(state[field], testCase);
                if (missing) return "missing_behavior";
            }
            if (state.ContainsKey("defer"))
            {
                if (Array(state["defer"]).Count == 0) return "empty_event_array";
                if (Array(state["defer"]).Any(value => !ValidEvent(value))) return "invalid_name";
            }

            if (kind == "submachine")
            {
                if (Array(state["states"]).Count > 0 || state.ContainsKey("entry") || state.ContainsKey("exit")
                    || state.ContainsKey("activity") || state.ContainsKey("defer")) return "invalid_submachine_contents";
                var machine = state["machine"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(machine) || !modelNames.Contains(machine)) return "missing_submachine_model";
                if (state.ContainsKey("initial")) return "invalid_submachine_initial";
            }
            else if (kind is "choice" or "shallow_history" or "deep_history")
            {
                if (state.ContainsKey("initial")) return "already has an initial state";
                if (kind is "shallow_history" or "deep_history" && owner == root) return "invalid_history_owner";
                if (state.ContainsKey("entry") || state.ContainsKey("exit") || state.ContainsKey("activity")
                    || state.ContainsKey("defer") || state.ContainsKey("states")) return "invalid_pseudostate_contents";
                if (kind is "shallow_history" or "deep_history" && Array(state["transitions"]).Count == 0)
                    return "history_missing_default";
                if (kind == "choice" && Array(state["transitions"]).Count == 0) return "choice_missing_transition";
                if (kind == "choice")
                {
                    var transitions = Array(state["transitions"]);
                    var fallback = transitions.Select(Object).Select((value, index) => (value, index))
                        .Where(pair => !pair.value.ContainsKey("guard")).ToArray();
                    if (fallback.Length == 0) return "choice_missing_fallback";
                    if (fallback.Any(pair => pair.index != transitions.Count - 1)) return "choice_default_not_last";
                }
            }
            else if (kind == "final")
            {
                if (new[] { "entry", "exit", "activity", "defer", "states", "initial", "transitions" }
                    .Any(state.ContainsKey)) return "invalid_final_transition";
            }

            var children = Array(state["states"]);
            if (children.Count > 0 && !state.ContainsKey("initial")) return "missing_initial";
            if (state["initial"] is JsonObject initial && IsEmptyArray(initial, "effects")) return "empty_behavior_array";
            if (state["initial"] is JsonObject initialWithEffects
                && MissingBehaviorReference(initialWithEffects["effects"], testCase)) return "missing_behavior";
            if (state.ContainsKey("initial"))
            {
                var initialError = ValidateInitial(state["initial"], path, paths);
                if (initialError is not null) return initialError;
            }

            var transitionOwner = kind is "choice" or "shallow_history" or "deep_history" ? owner : path;
            var transitionError = ValidateTransitionList(
                Array(state["transitions"]), model, testCase, paths, kinds, transitionOwner, false, attributes);
            if (transitionError is not null) return transitionError;
            var childError = ValidateStates(children, path, root, model, testCase, paths, kinds, attributes, modelNames);
            if (childError is not null) return childError;
        }
        return null;
    }

    private static string? ValidateTransitionList(
        JsonArray transitions,
        JsonObject model,
        JsonObject testCase,
        HashSet<string> paths,
        Dictionary<string, string> kinds,
        string owner,
        bool rootOwned,
        JsonObject attributes)
    {
        foreach (var raw in transitions)
        {
            var transition = Object(raw);
            if (IsEmptyArray(transition, "effects")) return "empty_behavior_array";
            if (MissingBehaviorReference(transition["effects"], testCase) || MissingBehaviorReference(transition["guard"], testCase))
                return "missing_behavior";
            if (transition.ContainsKey("entry_point") && !transition.ContainsKey("target")) return "invalid_entry_point_usage";
            if (!transition.ContainsKey("target") && !transition.ContainsKey("entry_point")
                && Array(transition["effects"]).Count == 0) return "missing_target";
            if (transition.ContainsKey("on") && transition.ContainsKey("trigger")) return "multiple_transition_triggers";
            var source = transition["source"]?.GetValue<string>();
            var triggerError = ValidateTrigger(transition, testCase, attributes);
            if (triggerError is not null) return triggerError;
            if (transition["trigger"] is JsonObject exitTrigger
                && exitTrigger["kind"]?.GetValue<string>() == "exit_point")
            {
                if (kinds.GetValueOrDefault(owner) != "submachine") return "invalid_exit_point_usage";
                var state = FindState(model, owner);
                var child = FindModel(testCase, state?["machine"]?.GetValue<string>());
                var exitName = exitTrigger["exit_point"]?.GetValue<string>() ?? string.Empty;
                if (child is null || !Array(child["exit_points"]).Select(Object)
                        .Any(point => String(point, "name") == exitName)) return "missing_exit_point";
            }
            if (rootOwned && !transition.ContainsKey("source")) return "missing_source";

            if (source is not null)
            {
                var resolved = ResolveTransitionPath(source, owner, paths, rootOwned);
                if (InsideSubmachine(resolved, kinds)) return "invalid_submachine_internal_source";
                if (!paths.Contains(resolved)) return "missing_source";
            }

            if (transition["target"] is JsonValue targetValue)
            {
                var target = targetValue.GetValue<string>();
                var modelRoot = "/" + String(model, "name");
                if (target.StartsWith('/') && !target.StartsWith(modelRoot + "/", StringComparison.Ordinal))
                    return "invalid_submachine_boundary_target";
                if (Array(model["entry_points"]).Select(Object)
                    .Any(point => String(point, "name") == target)) return "invalid_entry_point_internal_target";
                var resolved = target == "." && source is not null
                    ? ResolveTransitionPath(source, owner, paths, rootOwned)
                    : ResolveTransitionPath(target, owner, paths, rootOwned);
                if (InsideSubmachine(resolved, kinds)) return "invalid_submachine_internal_target";
                if (transition.ContainsKey("entry_point") && kinds.GetValueOrDefault(resolved) != "submachine")
                    return "invalid_entry_point_usage";
                if (!paths.Contains(resolved)) return "missing_target";
                if (transition["entry_point"] is JsonValue selector)
                {
                    var selectorName = selector.GetValue<string>();
                    if (!ValidName(selectorName)) return "invalid_name";
                    var targetState = FindState(model, resolved);
                    var child = FindModel(testCase, targetState?["machine"]?.GetValue<string>());
                    if (child is null || !Array(child["entry_points"]).Select(Object)
                        .Any(point => String(point, "name") == selectorName)) return "missing_entry_point";
                }
            }
        }
        return null;
    }

    private static string? ValidateTrigger(JsonObject transition, JsonObject testCase, JsonObject attributes)
    {
        if (transition["on"] is JsonNode shorthand && !ValidEvent(shorthand)) return "invalid_name";
        if (transition["trigger"] is not JsonObject trigger) return null;
        var kind = trigger["kind"]?.GetValue<string>() ?? string.Empty;
        var allowed = kind switch
        {
            "on" => new[] { "kind", "event", "events" },
            "on_set" => new[] { "kind", "attribute" },
            "on_call" => new[] { "kind", "operation" },
            "when" => new[] { "kind", "attribute", "behavior" },
            "completion" => new[] { "kind" },
            "exit_point" => new[] { "kind", "exit_point" },
            "after" or "every" or "at" => new[] { "kind", "duration_ms", "time_ms", "attribute", "behavior" },
            _ => System.Array.Empty<string>()
        };
        if (allowed.Length > 0 && trigger.Any(pair => !allowed.Contains(pair.Key))) return "extraneous_trigger_operand";

        if (kind == "on")
        {
            var count = (trigger.ContainsKey("event") ? 1 : 0) + (trigger.ContainsKey("events") ? 1 : 0);
            if (count == 0) return "missing_trigger_operand";
            if (count > 1) return "multiple_trigger_operands";
            if (trigger.ContainsKey("events") && Array(trigger["events"]).Count == 0) return "empty_event_array";
            if (trigger.ContainsKey("event") && !ValidEvent(trigger["event"])) return "invalid_name";
            if (Array(trigger["events"]).Any(value => !ValidEvent(value))) return "invalid_name";
        }
        else if (kind is "on_set" or "on_call" or "exit_point")
        {
            var field = kind == "on_set" ? "attribute" : kind == "on_call" ? "operation" : "exit_point";
            if (!trigger.ContainsKey(field)) return "missing_trigger_operand";
            var value = trigger[field]?.GetValue<string>() ?? string.Empty;
            if (!ValidName(value)) return "invalid_name";
            if (kind == "on_set" && !attributes.ContainsKey(value)) return "missing_attribute";
            if (kind == "on_call" && !ObjectOrEmpty(Object(testCase["model"])["operations"]).ContainsKey(value)) return "missing_operation";
        }
        else if (kind == "when")
        {
            var count = (trigger.ContainsKey("attribute") ? 1 : 0) + (trigger.ContainsKey("behavior") ? 1 : 0);
            if (count == 0) return "missing_trigger_operand";
            if (count > 1) return "multiple_trigger_operands";
            if (trigger["attribute"] is JsonValue attr)
            {
                var name = attr.GetValue<string>();
                if (!ValidName(name)) return "invalid_name";
                if (!attributes.ContainsKey(name)) return "missing_attribute";
            }
            if (trigger["behavior"] is JsonValue behavior
                && !ObjectOrEmpty(testCase["behaviors"]).ContainsKey(behavior.GetValue<string>())) return "missing_behavior";
        }
        else if (kind is "after" or "every" or "at")
        {
            var count = new[] { "duration_ms", "time_ms", "attribute", "behavior" }.Count(trigger.ContainsKey);
            if (count != 1 || (kind == "at" ? trigger.ContainsKey("duration_ms") : trigger.ContainsKey("time_ms")))
                return "invalid_timer_source";
            if (kind == "every" && trigger["duration_ms"]?.GetValue<double>() == 0) return "invalid_timer_source";
            if (trigger["attribute"] is JsonValue attr)
            {
                var name = attr.GetValue<string>();
                if (!attributes.TryGetPropertyValue(name, out var declarationNode)) return "missing_timer_attribute";
                var type = Object(declarationNode)["type"]?.GetValue<string>();
                if ((kind == "at" && type != "time_ms") || (kind != "at" && type == "time_ms"))
                    return "invalid_timer_attribute_type";
            }
            if (trigger["behavior"] is JsonValue behavior)
            {
                var id = behavior.GetValue<string>();
                if (!ObjectOrEmpty(testCase["behaviors"]).TryGetPropertyValue(id, out var program)) return "missing_behavior";
                var returned = Array(program).Select(Object).FirstOrDefault(op => op["op"]?.GetValue<string>() == "return_value");
                if (returned is not null && !IsNumber(returned["value"]))
                    return "invalid_timer_behavior_return";
            }
        }
        return null;
    }

    private static string? ValidateConnectionPoints(JsonObject model, HashSet<string> paths, HashSet<string> topNames)
    {
        var entry = new HashSet<string>(StringComparer.Ordinal);
        var exit = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in Array(model["entry_points"]))
        {
            var point = Object(raw);
            var name = String(point, "name");
            if (!ValidName(name)) return "invalid_name";
            if (!entry.Add(name)) return "duplicate_entry_point";
            if (topNames.Contains(name)) return "connection_point_name_collision";
        }
        foreach (var raw in Array(model["exit_points"]))
        {
            var point = Object(raw);
            var name = String(point, "name");
            if (!ValidName(name)) return "invalid_name";
            if (!exit.Add(name)) return "duplicate_exit_point";
            if (topNames.Contains(name) || entry.Contains(name)) return "connection_point_name_collision";
        }
        var root = "/" + String(model, "name");
        foreach (var raw in Array(model["entry_points"]))
        {
            var target = Object(raw)["target"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(target)) return "missing_target";
            if (entry.Contains(target)) return "invalid_entry_point_target";
            if (exit.Contains(target)) return "invalid_entry_point_target_kind";
            if (target.StartsWith('/') && !target.StartsWith(root + "/", StringComparison.Ordinal))
                return "invalid_entry_point_target";
            if (!paths.Contains(Resolve(target, root))) return "missing_target";
        }
        return null;
    }

    private static string? ValidateBehaviors(JsonObject testCase)
    {
        var required = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["trace"] = ["value"],
            ["set_attr"] = ["name", "value"],
            ["set_attr_from_event_data"] = ["name", "path"],
            ["get_attr"] = ["name"],
            ["return_attr"] = ["name"],
            ["return_value"] = ["value"],
            ["return_equals"] = ["name", "value"],
            ["event_name_equals"] = ["value"],
            ["event_data_equals"] = ["path", "value"],
            ["event_data_get"] = ["path"],
            ["event_application_metadata_equals"] = ["name", "value"],
            ["event_metadata_set"] = ["name", "value"],
            ["event_metadata_get"] = ["name"],
            ["event_metadata_equals"] = ["name", "value"],
            ["dispatch"] = ["event"],
            ["call"] = ["name"],
            ["sleep"] = ["millis"],
            ["snapshot"] = [],
            ["yield"] = []
        };
        foreach (var (_, rawProgram) in ObjectOrEmpty(testCase["behaviors"]))
        {
            var program = Array(rawProgram);
            if (program.Count == 0) return "missing_behavior";
            foreach (var raw in program)
            {
                var op = Object(raw);
                var kind = op["op"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(kind)) return "invalid_behavior_op_operand";
                if (kind == "raise")
                {
                    if (op.ContainsKey("event") == op.ContainsKey("code")) return "invalid_behavior_op_operand";
                    if (op.Any(pair => !new[] { "op", "event", "code", "value" }.Contains(pair.Key)))
                        return "invalid_behavior_op_operand";
                    continue;
                }
                if (!required.TryGetValue(kind, out var operands)) return "invalid_behavior_op_operand";
                if (operands.Any(operand => !op.ContainsKey(operand))) return "invalid_behavior_op_operand";
                var allowed = new HashSet<string>(operands.Append("op"), StringComparer.Ordinal);
                if (kind == "dispatch") allowed.UnionWith(["target", "instance", "group"]);
                if (op.Any(pair => !allowed.Contains(pair.Key))) return "invalid_behavior_op_operand";
                if (kind == "dispatch" && new[] { "target", "instance", "group" }.Count(op.ContainsKey) > 1)
                    return "invalid_behavior_op_operand";
            }
        }
        return null;
    }

    private static string? ValidateCycles(List<JsonObject> models)
    {
        var byName = models.ToDictionary(model => String(model, "name"), StringComparer.Ordinal);
        foreach (var model in models)
        {
            if (HasCycle(String(model, "name"), byName, [], [])) return "submachine_model_cycle";
        }
        return null;
    }

    private static bool HasCycle(string name, Dictionary<string, JsonObject> models, HashSet<string> visiting, HashSet<string> done)
    {
        if (visiting.Contains(name)) return true;
        if (!done.Add(name) || !models.TryGetValue(name, out var model)) return false;
        visiting.Add(name);
        foreach (var state in Descendants(Array(model["states"])))
        {
            if (state["kind"]?.GetValue<string>() == "submachine"
                && state["machine"] is JsonValue machine
                && HasCycle(machine.GetValue<string>(), models, visiting, done)) return true;
        }
        visiting.Remove(name);
        return false;
    }

    private static IEnumerable<JsonObject> Descendants(JsonArray states)
    {
        foreach (var raw in states)
        {
            var state = Object(raw);
            yield return state;
            foreach (var child in Descendants(Array(state["states"]))) yield return child;
        }
    }

    private static string? ValidateInitial(JsonNode? raw, string owner, HashSet<string> paths)
    {
        var target = raw is JsonObject value ? value["target"]?.GetValue<string>() : raw?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(target)) return "missing_target";
        return paths.Contains(Resolve(target, owner)) ? null : "missing_target";
    }

    private static bool MissingBehaviorReference(JsonNode? raw, JsonObject testCase)
    {
        var known = ObjectOrEmpty(testCase["behaviors"]);
        if (raw is JsonArray array) return array.Any(value => MissingBehaviorReference(value, testCase));
        if (raw is JsonValue scalar)
        {
            var id = scalar.TryGetValue<string>(out var text) ? text : null;
            return !string.IsNullOrWhiteSpace(id) && !known.ContainsKey(id);
        }
        if (raw is not JsonObject value) return false;
        var behavior = value["behavior"]?.GetValue<string>();
        return !string.IsNullOrWhiteSpace(behavior) && !known.ContainsKey(behavior);
    }

    private static string ResolveTransitionPath(string path, string owner, HashSet<string> paths, bool rootOwned)
    {
        if (path.StartsWith('/')) return Normalize(path);
        var direct = Resolve(path, owner);
        if (rootOwned || paths.Contains(direct) || path.StartsWith("..", StringComparison.Ordinal)) return direct;
        return Resolve(path, Parent(owner));
    }

    private static JsonObject? FindModel(JsonObject testCase, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var main = Object(testCase["model"]);
        if (String(main, "name") == name) return main;
        return Array(testCase["models"]).Select(Object)
            .FirstOrDefault(model => String(model, "name") == name);
    }

    private static JsonObject? FindState(JsonObject model, string path)
    {
        var root = "/" + String(model, "name");
        if (!path.StartsWith(root + "/", StringComparison.Ordinal)) return null;
        var states = Array(model["states"]);
        JsonObject? current = null;
        foreach (var part in path[(root.Length + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = states.Select(Object).FirstOrDefault(state => String(state, "name") == part);
            if (current is null) return null;
            states = Array(current["states"]);
        }
        return current;
    }

    private static bool InsideSubmachine(string path, Dictionary<string, string> kinds)
    {
        var current = Parent(path);
        while (current != "/")
        {
            if (kinds.GetValueOrDefault(current) == "submachine") return true;
            current = Parent(current);
        }
        return false;
    }

    private static bool ValidEvent(JsonNode? node)
    {
        var name = node is JsonValue value ? value.GetValue<string>() : Object(node)["name"]?.GetValue<string>();
        return !string.IsNullOrWhiteSpace(name) && !name.Contains('/');
    }

    private static bool MatchesType(JsonNode? value, string? type) => type switch
    {
        "boolean" => value is JsonValue node && node.TryGetValue<bool>(out _),
        "string" => value is JsonValue node && node.TryGetValue<string>(out _),
        "integer" or "duration_ms" or "time_ms" => value is JsonValue node && node.TryGetValue<long>(out _),
        "number" => IsNumber(value),
        "object" => value is JsonObject,
        "array" => value is JsonArray,
        _ => true
    };

    private static bool IsNumber(JsonNode? value) => value is JsonValue node
        && (node.TryGetValue<long>(out _) || node.TryGetValue<double>(out _));
    private static bool ValidName(string name) => !string.IsNullOrWhiteSpace(name) && !name.Contains('/');
    private static bool IsEmptyArray(JsonObject value, string field) => value.ContainsKey(field) && Array(value[field]).Count == 0;
    private static JsonArray Array(JsonNode? value) => value as JsonArray ?? [];
    private static JsonObject Object(JsonNode? value) => value as JsonObject ?? new JsonObject();
    private static JsonObject ObjectOrEmpty(JsonNode? value) => value as JsonObject ?? new JsonObject();
    private static string String(JsonObject value, string field) => value[field]?.GetValue<string>() ?? string.Empty;
    private static string Join(string owner, string name) => owner.TrimEnd('/') + "/" + name.Trim('/');
    private static string Parent(string path) => path.LastIndexOf('/') <= 0 ? "/" : path[..path.LastIndexOf('/')];
    private static string Resolve(string path, string owner)
    {
        if (path.StartsWith('/')) return Normalize(path);
        var parts = (owner + "/" + path).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>();
        foreach (var part in parts)
        {
            if (part == ".") continue;
            if (part == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); }
            else stack.Add(part);
        }
        return "/" + string.Join('/', stack);
    }
    private static string Normalize(string path) => Resolve(path.TrimStart('/'), "/");
}
