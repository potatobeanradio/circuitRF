using System.Text.Json;

namespace CircuitRF.Ui.Tests.Em3d;

/// <summary>
/// brief-em3d-7 gate 2 — a JSON Schema draft-07 validator for exactly the keywords Palace 0.18.1's
/// schema uses (testdata/em3d/palace-schema/0.18.1.json): <c>$ref</c> to <c>#/$defs/…</c>, type, const,
/// enum, properties / additionalProperties / required, items / additionalItems / minItems / maxItems /
/// contains, the numeric bounds, minLength, allOf / anyOf / oneOf / not, and if / then / else.
///
/// <para><b>An unknown keyword is a FAILURE of the validator, not something it skips</b> — a keyword
/// silently ignored is a constraint silently unchecked, and the gate would then pass a configuration
/// Palace rejects. Annotation keywords (title, description, default, $comment, the x-palace-* ones) are
/// listed and ignored on purpose. Held honest by the planted violations in PalaceBackendTests.</para>
/// </summary>
internal sealed class DraftSevenSchema(JsonElement root)
{
    private static readonly HashSet<string> Annotations =
        ["$schema", "$id", "title", "description", "default", "$comment", "examples", "markdownDescription",
         "x-palace-advanced", "x-palace-deprecated", "$defs"];

    /// <summary>Every violation, each with the path it was found at; empty when the instance is valid.</summary>
    public List<string> Validate(JsonElement instance)
    {
        var errors = new List<string>();
        Check(root, instance, "$", errors);
        return errors;
    }

    private bool Valid(JsonElement schema, JsonElement instance)
    {
        var e = new List<string>();
        Check(schema, instance, "$", e);
        return e.Count == 0;
    }

    private void Check(JsonElement schema, JsonElement inst, string path, List<string> errors)
    {
        if (schema.ValueKind == JsonValueKind.True) return;
        if (schema.ValueKind == JsonValueKind.False) { errors.Add($"{path}: nothing is allowed here"); return; }

        if (schema.TryGetProperty("$ref", out var reference))
        {
            // Draft-07: a $ref's siblings are ignored.
            Check(Resolve(reference.GetString()!), inst, path, errors);
            return;
        }

        foreach (var kw in schema.EnumerateObject())
        {
            switch (kw.Name)
            {
                case "type":
                    if (!TypeMatches(kw.Value, inst)) errors.Add($"{path}: is {inst.ValueKind}, not {kw.Value}");
                    break;
                case "const":
                    if (!JsonEqual(kw.Value, inst)) errors.Add($"{path}: is not {kw.Value}");
                    break;
                case "enum":
                    if (!kw.Value.EnumerateArray().Any(v => JsonEqual(v, inst))) errors.Add($"{path}: {inst} is not one of {kw.Value}");
                    break;
                case "properties" or "additionalProperties" or "required":
                    if (inst.ValueKind == JsonValueKind.Object) CheckObjectKeyword(schema, kw, inst, path, errors);
                    break;
                case "items" or "additionalItems" or "minItems" or "maxItems" or "contains":
                    if (inst.ValueKind == JsonValueKind.Array) CheckArrayKeyword(schema, kw, inst, path, errors);
                    break;
                case "minimum" or "maximum" or "exclusiveMinimum" or "exclusiveMaximum":
                    if (inst.ValueKind == JsonValueKind.Number)
                    {
                        double v = inst.GetDouble(), b = kw.Value.GetDouble();
                        bool ok = kw.Name switch
                        {
                            "minimum"          => v >= b,
                            "maximum"          => v <= b,
                            "exclusiveMinimum" => v > b,
                            _                  => v < b,
                        };
                        if (!ok) errors.Add($"{path}: {v} breaks {kw.Name} {b}");
                    }
                    break;
                case "minLength":
                    if (inst.ValueKind == JsonValueKind.String && inst.GetString()!.Length < kw.Value.GetInt32())
                        errors.Add($"{path}: shorter than {kw.Value}");
                    break;
                case "allOf":
                    foreach (var sub in kw.Value.EnumerateArray()) Check(sub, inst, path, errors);
                    break;
                case "anyOf":
                    if (!kw.Value.EnumerateArray().Any(sub => Valid(sub, inst))) errors.Add($"{path}: matches none of anyOf");
                    break;
                case "oneOf":
                {
                    int n = kw.Value.EnumerateArray().Count(sub => Valid(sub, inst));
                    if (n != 1) errors.Add($"{path}: matches {n} of oneOf, not exactly one");
                    break;
                }
                case "not":
                    if (Valid(kw.Value, inst)) errors.Add($"{path}: matches a 'not'");
                    break;
                case "if":
                {
                    bool cond = Valid(kw.Value, inst);
                    if (cond && schema.TryGetProperty("then", out var then)) Check(then, inst, path, errors);
                    if (!cond && schema.TryGetProperty("else", out var @else)) Check(@else, inst, path, errors);
                    break;
                }
                case "then" or "else":
                    break;
                default:
                    if (!Annotations.Contains(kw.Name))
                        throw new NotSupportedException($"schema keyword '{kw.Name}' at {path} is not implemented");
                    break;
            }
        }
    }

    private void CheckObjectKeyword(JsonElement schema, JsonProperty kw, JsonElement inst, string path, List<string> errors)
    {
        switch (kw.Name)
        {
            case "required":
                foreach (var name in kw.Value.EnumerateArray())
                    if (!inst.TryGetProperty(name.GetString()!, out _)) errors.Add($"{path}: '{name}' is required");
                break;
            case "properties":
                foreach (var p in inst.EnumerateObject())
                    if (kw.Value.TryGetProperty(p.Name, out var sub)) Check(sub, p.Value, $"{path}.{p.Name}", errors);
                break;
            case "additionalProperties":
                var declared = schema.TryGetProperty("properties", out var props)
                    ? props.EnumerateObject().Select(p => p.Name).ToHashSet() : [];
                foreach (var p in inst.EnumerateObject().Where(p => !declared.Contains(p.Name)))
                    Check(kw.Value, p.Value, $"{path}.{p.Name}", errors);
                break;
        }
    }

    private void CheckArrayKeyword(JsonElement schema, JsonProperty kw, JsonElement inst, string path, List<string> errors)
    {
        var items = inst.EnumerateArray().ToList();
        switch (kw.Name)
        {
            case "minItems": if (items.Count < kw.Value.GetInt32()) errors.Add($"{path}: fewer than {kw.Value} items"); break;
            case "maxItems": if (items.Count > kw.Value.GetInt32()) errors.Add($"{path}: more than {kw.Value} items"); break;
            case "contains":
                if (!items.Any(i => Valid(kw.Value, i))) errors.Add($"{path}: no item matches 'contains'");
                break;
            case "items":
                if (kw.Value.ValueKind == JsonValueKind.Array)
                {
                    var tuple = kw.Value.EnumerateArray().ToList();
                    for (int i = 0; i < items.Count && i < tuple.Count; i++) Check(tuple[i], items[i], $"{path}[{i}]", errors);
                }
                else
                    for (int i = 0; i < items.Count; i++) Check(kw.Value, items[i], $"{path}[{i}]", errors);
                break;
            case "additionalItems":
                // Only meaningful beside a tuple-form "items"; with a single-schema "items" it is ignored.
                if (schema.TryGetProperty("items", out var it) && it.ValueKind == JsonValueKind.Array)
                    for (int i = it.GetArrayLength(); i < items.Count; i++) Check(kw.Value, items[i], $"{path}[{i}]", errors);
                break;
        }
    }

    private JsonElement Resolve(string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
            throw new NotSupportedException($"only local references are implemented, not '{reference}'");
        var node = root;
        foreach (string part in reference[2..].Split('/')) node = node.GetProperty(part);
        return node;
    }

    private static bool TypeMatches(JsonElement type, JsonElement inst)
        => type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Any(t => One(t.GetString()!, inst))
            : One(type.GetString()!, inst);

    private static bool One(string type, JsonElement inst) => type switch
    {
        "object"  => inst.ValueKind == JsonValueKind.Object,
        "array"   => inst.ValueKind == JsonValueKind.Array,
        "string"  => inst.ValueKind == JsonValueKind.String,
        "boolean" => inst.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null"    => inst.ValueKind == JsonValueKind.Null,
        "number"  => inst.ValueKind == JsonValueKind.Number,
        "integer" => inst.ValueKind == JsonValueKind.Number && inst.GetDouble() == Math.Floor(inst.GetDouble()),
        _         => throw new NotSupportedException($"type '{type}'"),
    };

    private static bool JsonEqual(JsonElement a, JsonElement b)
    {
        if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number) return a.GetDouble() == b.GetDouble();
        if (a.ValueKind != b.ValueKind) return false;
        return a.ValueKind switch
        {
            JsonValueKind.String => a.GetString() == b.GetString(),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => a.GetRawText() == b.GetRawText(),
        };
    }
}
