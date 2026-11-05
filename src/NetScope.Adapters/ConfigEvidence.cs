using System.Security.Cryptography;
using System.Text;
using NetScope.Application;

namespace NetScope.Adapters;

/// <summary>Shared, value-safe construction of configuration evidence.</summary>
internal static class ConfigEvidence
{
    /// <summary>Bounded read: a configuration file larger than this is not parsed at all.</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    public static string Hash(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    /// <summary>Strict UTF-8 decode. The reason never echoes file content.</summary>
    public static bool TryDecodeUtf8(byte[] content, out string text, out string? reason)
    {
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(content);
            if (text.Length > 0 && text[0] == '\uFEFF')
            {
                text = text[1..];
            }

            reason = null;
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            reason = "配置文件不是有效的 UTF-8 文本";
            return false;
        }
    }

    /// <summary>Evidence for a file that could not be read at all. All value fields stay empty.</summary>
    public static ClientConfigEvidence Unavailable(
        string clientId,
        string parserVersion,
        NetScope.Application.ConfigProvenance provenance,
        string sourcePath,
        DateTimeOffset readAt,
        string validityReason,
        IReadOnlyList<string> gaps) => new()
        {
            ClientId = clientId,
            ParserVersion = parserVersion,
            Provenance = provenance,
            SourcePath = sourcePath,
            ReadAt = readAt,
            FileModifiedAt = null,
            ContentHash = null,
            Valid = false,
            ValidityReason = validityReason,
            Facts = new Dictionary<string, string>(StringComparer.Ordinal),
            Gaps = gaps,
        };
}
