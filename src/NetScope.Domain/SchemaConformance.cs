using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetScope.Domain;

/// <summary>
/// Dependency-free checker for exactly the JSON Schema keywords used by the published
/// contracts. It is deliberately narrow: when a schema starts using a keyword this type does
/// not implement, <see cref="FindUnsupportedKeywords"/> reports it so a test fails loudly
/// instead of the contract silently drifting away from the validator.
/// </summary>
public static class SchemaConformance
{
    private static readonly HashSet<string> SupportedKeywords = new(StringComparer.Ordinal)
    {
        "$schema",
        "$id",
        "title",
        "description",
        "const",
        "enum",
        "type",
        "required",
        "properties",
        "additionalProperties",
        "items",
        "minItems",
        "maxItems",
        "minLength",
        "maxLength",
        "minimum",
        "maximum",
        "format",
    };

    /// <summary>
    /// Shape of an RFC 3339 timestamp. The captured components are range-checked separately,
    /// because a regular expression alone accepts dates such as 2026-02-30 or 25:61:61.
    /// </summary>
    private static readonly Regex TimestampShape = new(
        @"^(?<year>\d{4})-(?<month>\d{2})-(?<day>\d{2})[Tt](?<hour>\d{2}):(?<minute>\d{2}):(?<second>\d{2})"
        + @"(?:\.(?<fraction>\d+))?(?<zone>[Zz]|(?<sign>[+-])(?<offsetHour>\d{2}):(?<offsetMinute>\d{2}))$",
        RegexOptions.CultureInvariant);

    /// <summary>Returns dotted paths of schema keywords this validator cannot enforce.</summary>
    public static IReadOnlyList<string> FindUnsupportedKeywords(JsonElement schema)
    {
        var found = new List<string>();
        Walk(schema, "$", found);
        return found;
    }

    /// <summary>Returns human-readable problems; an empty list means the value conforms.</summary>
    public static IReadOnlyList<string> Validate(JsonElement value, JsonElement schema)
    {
        var errors = new List<string>();
        ValidateNode(value, schema, "$", errors);
        return errors;
    }

    /// <summary>
    /// True when <paramref name="text"/> is a real RFC 3339 timestamp: the calendar date must
    /// exist, the clock components must be in range and the offset must be a real-world offset.
    /// </summary>
    public static bool IsRfc3339Timestamp(string text)
    {
        var match = TimestampShape.Match(text);
        if (!match.Success)
        {
            return false;
        }

        var year = Read(match, "year");
        var month = Read(match, "month");
        var day = Read(match, "day");
        var hour = Read(match, "hour");
        var minute = Read(match, "minute");
        var second = Read(match, "second");

        if (year is < 1 or > 9999 || month is < 1 or > 12)
        {
            return false;
        }

        if (day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        // 0..23 and 0..59; second 60 is allowed for a leap second.
        if (hour > 23 || minute > 59 || second > 60)
        {
            return false;
        }

        if (!match.Groups["sign"].Success)
        {
            return true;
        }

        var offsetHour = Read(match, "offsetHour");
        var offsetMinute = Read(match, "offsetMinute");

        // Real-world civil offsets stay within -14:00..+14:00.
        return offsetHour <= 14 && offsetMinute <= 59 && (offsetHour != 14 || offsetMinute == 0);
    }

    private static int Read(Match match, string group) =>
        int.Parse(match.Groups[group].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);

    private static void Walk(JsonElement schema, string path, List<string> found)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var keyword in schema.EnumerateObject())
        {
            if (!SupportedKeywords.Contains(keyword.Name))
            {
                found.Add($"{path}.{keyword.Name}");
                continue;
            }

            if (keyword.Name == "properties" && keyword.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in keyword.Value.EnumerateObject())
                {
                    Walk(property.Value, $"{path}.properties.{property.Name}", found);
                }
            }
            else if (keyword.Name == "items")
            {
                Walk(keyword.Value, $"{path}.items", found);
            }
            else if (keyword.Name == "additionalProperties" && keyword.Value.ValueKind == JsonValueKind.Object)
            {
                found.Add($"{path}.additionalProperties (the object form is not supported)");
            }
        }
    }

    private static void ValidateNode(JsonElement value, JsonElement schema, string path, List<string> errors)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(value, constant))
        {
            errors.Add($"{path}: expected the constant {constant.GetRawText()}");
        }

        if (schema.TryGetProperty("enum", out var allowed) && allowed.ValueKind == JsonValueKind.Array)
        {
            var matched = false;
            foreach (var candidate in allowed.EnumerateArray())
            {
                if (!JsonElement.DeepEquals(value, candidate))
                {
                    continue;
                }

                matched = true;
                break;
            }

            if (!matched)
            {
                errors.Add($"{path}: value is not one of the allowed values");
            }
        }

        if (schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
        {
            var expected = type.GetString()!;
            if (!MatchesType(value, expected))
            {
                errors.Add($"{path}: expected type '{expected}' but found {value.ValueKind}");
                return;
            }
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                ValidateObject(value, schema, path, errors);
                break;
            case JsonValueKind.Array:
                ValidateArray(value, schema, path, errors);
                break;
            case JsonValueKind.String:
                ValidateString(value.GetString()!, schema, path, errors);
                break;
            case JsonValueKind.Number:
                ValidateNumber(value, schema, path, errors);
                break;
        }
    }

    private static void ValidateObject(JsonElement value, JsonElement schema, string path, List<string> errors)
    {
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in required.EnumerateArray())
            {
                var property = name.GetString();
                if (property is not null && !value.TryGetProperty(property, out _))
                {
                    errors.Add($"{path}: missing required property '{property}'");
                }
            }
        }

        var properties = default(JsonElement);
        var hasProperties = schema.TryGetProperty("properties", out properties)
            && properties.ValueKind == JsonValueKind.Object;

        var additional = default(JsonElement);
        var additionalAllowed = !(schema.TryGetProperty("additionalProperties", out additional)
            && additional.ValueKind == JsonValueKind.False);

        foreach (var member in value.EnumerateObject())
        {
            if (hasProperties && properties.TryGetProperty(member.Name, out var childSchema))
            {
                ValidateNode(member.Value, childSchema, $"{path}.{member.Name}", errors);
            }
            else if (!additionalAllowed)
            {
                errors.Add($"{path}: unexpected property");
            }
        }
    }

    private static void ValidateArray(JsonElement value, JsonElement schema, string path, List<string> errors)
    {
        var length = value.GetArrayLength();

        if (schema.TryGetProperty("minItems", out var minItems)
            && minItems.TryGetInt32(out var min)
            && length < min)
        {
            errors.Add($"{path}: has {length} item(s) but minItems is {min}");
        }

        if (schema.TryGetProperty("maxItems", out var maxItems)
            && maxItems.TryGetInt32(out var max)
            && length > max)
        {
            errors.Add($"{path}: has {length} item(s) but maxItems is {max}");
        }

        if (schema.TryGetProperty("items", out var itemSchema) && itemSchema.ValueKind == JsonValueKind.Object)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                ValidateNode(item, itemSchema, $"{path}[{index}]", errors);
                index++;
            }
        }
    }

    private static void ValidateString(string text, JsonElement schema, string path, List<string> errors)
    {
        if (schema.TryGetProperty("minLength", out var minLength)
            && minLength.TryGetInt32(out var min)
            && text.Length < min)
        {
            errors.Add($"{path}: length {text.Length} is below minLength {min}");
        }

        if (schema.TryGetProperty("maxLength", out var maxLength)
            && maxLength.TryGetInt32(out var max)
            && text.Length > max)
        {
            errors.Add($"{path}: length {text.Length} exceeds maxLength {max}");
        }

        if (schema.TryGetProperty("format", out var format)
            && format.ValueKind == JsonValueKind.String
            && format.GetString() == "date-time"
            && !IsRfc3339Timestamp(text))
        {
            errors.Add($"{path}: value is not a real RFC 3339 date-time");
        }
    }

    private static void ValidateNumber(JsonElement value, JsonElement schema, string path, List<string> errors)
    {
        if (!value.TryGetDouble(out var actual))
        {
            return;
        }

        if (schema.TryGetProperty("minimum", out var minimum)
            && minimum.TryGetDouble(out var min)
            && actual < min)
        {
            errors.Add($"{path}: {actual} is below minimum {min}");
        }

        if (schema.TryGetProperty("maximum", out var maximum)
            && maximum.TryGetDouble(out var max)
            && actual > max)
        {
            errors.Add($"{path}: {actual} exceeds maximum {max}");
        }
    }

    private static bool MatchesType(JsonElement value, string expected) => expected switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => false,
    };
}
