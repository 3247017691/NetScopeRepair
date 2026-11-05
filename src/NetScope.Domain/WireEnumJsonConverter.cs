using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetScope.Domain;

/// <summary>
/// Reads and writes an enum using only the wire names declared with
/// <see cref="JsonStringEnumMemberNameAttribute"/>.
/// <para>
/// This is deliberately stricter than <see cref="JsonStringEnumConverter{TEnum}"/>: the
/// built-in converter accepts JSON numbers and numeric strings by default, which would let
/// <c>"status": 0</c> become <see cref="EventStatus.Ok"/> and be counted as a success. The
/// persisted contract only ever contains the declared strings.
/// </para>
/// </summary>
public sealed class WireEnumJsonConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private static readonly Dictionary<string, TEnum> FromWire = BuildFromWire();

    private static readonly Dictionary<TEnum, string> ToWire = FromWire
        .ToDictionary(pair => pair.Value, pair => pair.Key);

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                $"{typeof(TEnum).Name} must be written as one of the declared names; "
                + $"the JSON token {reader.TokenType} is not accepted.");
        }

        var text = reader.GetString();
        if (text is not null && FromWire.TryGetValue(text, out var value))
        {
            return value;
        }

        throw new JsonException(
            $"'{text}' is not a defined {typeof(TEnum).Name} wire value; "
            + $"expected one of: {string.Join(", ", FromWire.Keys.Order(StringComparer.Ordinal))}.");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        if (!ToWire.TryGetValue(value, out var wireName))
        {
            throw new JsonException(
                $"{typeof(TEnum).Name} value {(int)(object)value} has no declared wire name and cannot be persisted.");
        }

        writer.WriteStringValue(wireName);
    }

    private static Dictionary<string, TEnum> BuildFromWire()
    {
        var map = new Dictionary<string, TEnum>(StringComparer.Ordinal);

        foreach (var field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var attribute = field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>();
            var wireName = attribute?.Name ?? field.Name;
            map[wireName] = (TEnum)field.GetValue(null)!;
        }

        return map;
    }
}
