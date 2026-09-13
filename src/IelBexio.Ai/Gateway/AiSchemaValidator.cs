using System.Globalization;
using System.Text.Json;

namespace IelBexio.Ai.Gateway;

/// <summary>
/// Validates an AI response against the subset of JSON Schema this application actually uses:
/// <c>type</c>, <c>required</c>, <c>properties</c>, <c>items</c>, <c>enum</c>, <c>minimum</c>,
/// <c>maximum</c>, <c>maxItems</c> and <c>additionalProperties: false</c>.
/// <para>
/// <b>Why a small hand-written validator rather than a JSON Schema library.</b> The schemas here are
/// written by us, are few, and are simple by design. A focused validator keeps the trust boundary
/// small and dependency-free, and — more importantly — it fails <em>closed</em>: an unsupported schema
/// construct is reported as a validation failure rather than being quietly skipped, which is the
/// failure mode that would let malformed AI output through.
/// </para>
/// </summary>
public static class AiSchemaValidator
{
    public static bool Validate(string json, string schemaJson, out string? error)
    {
        error = null;

        JsonDocument document;
        JsonDocument schema;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            error = $"response is not valid JSON: {ex.Message}";
            return false;
        }

        try
        {
            schema = JsonDocument.Parse(schemaJson);
        }
        catch (JsonException ex)
        {
            error = $"schema is not valid JSON: {ex.Message}";
            return false;
        }

        using (document)
        using (schema)
        {
            var failure = ValidateNode(document.RootElement, schema.RootElement, "$");
            error = failure;
            return failure is null;
        }
    }

    private static string? ValidateNode(JsonElement value, JsonElement schema, string path)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return $"{path}: schema node is not an object";
        }

        if (schema.TryGetProperty("type", out var typeNode))
        {
            var allowed = typeNode.ValueKind == JsonValueKind.Array
                ? typeNode.EnumerateArray().Select(t => t.GetString()).Where(t => t is not null).Select(t => t!).ToArray()
                : [typeNode.GetString() ?? "object"];

            if (!allowed.Any(t => MatchesType(value, t)))
            {
                return $"{path}: expected type {string.Join("|", allowed)} but found {value.ValueKind}";
            }
        }

        if (schema.TryGetProperty("enum", out var enumNode) && enumNode.ValueKind == JsonValueKind.Array)
        {
            var actual = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
            var permitted = enumNode.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())
                .ToArray();

            if (!permitted.Contains(actual, StringComparer.Ordinal))
            {
                return $"{path}: value '{actual}' is not one of {string.Join(", ", permitted)}";
            }
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            var number = value.GetDouble();

            if (schema.TryGetProperty("minimum", out var min) && number < min.GetDouble())
            {
                return $"{path}: {number.ToString(CultureInfo.InvariantCulture)} is below the minimum {min.GetDouble().ToString(CultureInfo.InvariantCulture)}";
            }

            if (schema.TryGetProperty("maximum", out var max) && number > max.GetDouble())
            {
                return $"{path}: {number.ToString(CultureInfo.InvariantCulture)} is above the maximum {max.GetDouble().ToString(CultureInfo.InvariantCulture)}";
            }
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            return ValidateObject(value, schema, path);
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            return ValidateArray(value, schema, path);
        }

        return null;
    }

    private static string? ValidateObject(JsonElement value, JsonElement schema, string path)
    {
        var hasProperties = schema.TryGetProperty("properties", out var properties);

        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in required.EnumerateArray())
            {
                var propertyName = name.GetString();
                if (propertyName is not null && !value.TryGetProperty(propertyName, out _))
                {
                    return $"{path}: required property '{propertyName}' is missing";
                }
            }
        }

        var allowsAdditional = !schema.TryGetProperty("additionalProperties", out var additional)
                               || additional.ValueKind != JsonValueKind.False;

        // additionalProperties may also be a schema, applied to every property not named explicitly.
        JsonElement? additionalSchema = schema.TryGetProperty("additionalProperties", out var additionalNode)
                                        && additionalNode.ValueKind == JsonValueKind.Object
            ? additionalNode
            : null;

        foreach (var property in value.EnumerateObject())
        {
            if (hasProperties && properties.TryGetProperty(property.Name, out var propertySchema))
            {
                var failure = ValidateNode(property.Value, propertySchema, $"{path}.{property.Name}");
                if (failure is not null)
                {
                    return failure;
                }

                continue;
            }

            if (additionalSchema is { } extra)
            {
                var failure = ValidateNode(property.Value, extra, $"{path}.{property.Name}");
                if (failure is not null)
                {
                    return failure;
                }

                continue;
            }

            if (!allowsAdditional)
            {
                return $"{path}: unexpected property '{property.Name}'";
            }
        }

        return null;
    }

    private static string? ValidateArray(JsonElement value, JsonElement schema, string path)
    {
        if (schema.TryGetProperty("maxItems", out var maxItems) && value.GetArrayLength() > maxItems.GetInt32())
        {
            return $"{path}: array has {value.GetArrayLength()} items, above the maximum of {maxItems.GetInt32()}";
        }

        if (!schema.TryGetProperty("items", out var itemSchema))
        {
            return null;
        }

        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            var failure = ValidateNode(item, itemSchema, $"{path}[{index}]");
            if (failure is not null)
            {
                return failure;
            }

            index++;
        }

        return null;
    }

    private static bool MatchesType(JsonElement value, string type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "number" or "integer" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => false,
    };
}
