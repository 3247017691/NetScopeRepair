using System.Text.Json;

namespace NetScope.Domain;

/// <summary>
/// Produces the persisted spelling of a contract enum, so display code can never print
/// <c>ExplicitHttpProxy</c> where the wire contract requires <c>explicit-http-proxy</c>.
/// </summary>
public static class WireNames
{
    public static string Of<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        JsonSerializer.Serialize(value, EnvelopeJson.Options).Trim('"');
}
