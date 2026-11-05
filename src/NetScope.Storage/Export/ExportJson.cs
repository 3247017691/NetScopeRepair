using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetScope.Domain;

namespace NetScope.Storage.Export;

/// <summary>
/// Single JSON writer configuration for incident packages. Everything is snake_case, indented
/// with a fixed layout and UTF-8 without a BOM so two exports of the same session produce the
/// same bytes apart from the random incident id and (absent) salt-derived aliases.
/// </summary>
internal static class ExportJson
{
    public static readonly JsonSerializerOptions Options = new(EnvelopeJson.Options)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        IndentSize = 2,
        IndentCharacter = ' ',
    };

    public static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static byte[] Bytes(string text) => Utf8.GetBytes(text);

    public static byte[] Serialize(JsonNode node) => Bytes(node.ToJsonString(Options) + "\n");

    public static string Text(JsonNode node) => node.ToJsonString(Options);

    public static byte[] Compact(string text) => Utf8.GetBytes(text);

    public static string Sha256(byte[] bytes) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
}
