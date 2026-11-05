using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetScope.Domain;

/// <summary>
/// Single source of truth for how envelopes are read and written. Strictness is deliberate:
/// no comments, no trailing commas, no case-insensitive matching, no numeric strings,
/// no unmapped members and no nulls for required members.
/// </summary>
public static class EnvelopeJson
{
    private static readonly JsonElement Contract = LoadContract();

    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
        // `required` only forces the property to appear; a JSON null would still produce an
        // object with a null identifier. EnvelopeIntegrity checks nulls explicitly so every
        // rejection carries one readable message instead of a serializer-internal one.
        RespectNullableAnnotations = false,
        // An absent optional reference must be omitted, never written as JSON null; the
        // published schema allows either a non-empty string or no property at all.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Reads one envelope strictly. Throws <see cref="JsonException"/> when the text is not
    /// valid JSON, when a member is missing, unknown, mistyped, or explicitly null.
    /// </summary>
    public static EventEnvelope Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        ValidateContract(document.RootElement);
        var envelope = JsonSerializer.Deserialize<EventEnvelope>(json, Options)
            ?? throw new JsonException("The envelope document deserialized to null.");

        EnvelopeIntegrity.EnsureComplete(envelope);
        return envelope;
    }

    public static string Serialize(EventEnvelope envelope)
    {
        EnvelopeIntegrity.EnsureComplete(envelope);
        var json = JsonSerializer.Serialize(envelope, Options);
        using var document = JsonDocument.Parse(json);
        ValidateContract(document.RootElement);
        return json;
    }

    private static JsonElement LoadContract()
    {
        using var stream = typeof(EnvelopeJson).Assembly.GetManifestResourceStream("NetScope.Domain.event-envelope.schema.json")
            ?? throw new InvalidOperationException("The embedded core envelope contract is missing.");
        using var document = JsonDocument.Parse(stream);
        var unsupported = SchemaConformance.FindUnsupportedKeywords(document.RootElement);
        if (unsupported.Count != 0)
        {
            throw new InvalidOperationException("The embedded contract contains unsupported keywords.");
        }
        return document.RootElement.Clone();
    }

    private static void ValidateContract(JsonElement value)
    {
        var problems = SchemaConformance.Validate(value, Contract);
        if (problems.Count != 0)
        {
            throw new JsonException("The envelope violates the core contract: " + string.Join("; ", problems));
        }
        RejectDuplicateMembers(value, "$");
    }

    private static void RejectDuplicateMembers(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in value.EnumerateObject())
            {
                if (!names.Add(member.Name))
                {
                    throw new JsonException($"Duplicate JSON member at {path}; ambiguous events are not accepted.");
                }
                RejectDuplicateMembers(member.Value, path + "." + member.Name);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) RejectDuplicateMembers(item, path + "[]");
        }
    }
}
