using NetScope.Application;

namespace NetScope.Adapters;

/// <summary>
/// Reads the Nice (FlClash fork) runtime configuration read-only. Evidence is always
/// <see cref="ConfigProvenance.DiskOnly"/>: the values come from the file, not from an API, and no
/// claim is made that the running client loaded this revision. Only the documented whitelist is
/// parsed; subscriptions, credentials, provider lists and full text are never read.
/// </summary>
public sealed class NiceAdapter : IClientConfigAdapter
{
    private readonly IConfigFileSource _files;

    public NiceAdapter(IConfigFileSource files, string? configPath = null)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        ConfigPath = string.IsNullOrWhiteSpace(configPath) ? DefaultConfigPath() : configPath;
    }

    public string ClientId => KnownClientIds.Nice;

    /// <summary>Resolved local path. It is displayed in the local UI only and replaced on export.</summary>
    public string ConfigPath { get; }

    /// <summary>%APPDATA%\com.core.NiceProxy\runtime.yaml, derived without hard-coding a user name.</summary>
    public static string DefaultConfigPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "com.core.NiceProxy", "runtime.yaml");
    }

    public Task<ClientConfigEvidence> ReadAsync(DateTimeOffset readAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ConfigFileReadResult read;
        try
        {
            read = _files.Read(ConfigPath);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Task.FromResult(ConfigEvidence.Unavailable(
                ClientId,
                NiceConfigParser.ParserVersion,
                ConfigProvenance.DiskOnly,
                ConfigPath,
                readAt,
                $"配置文件读取失败（{exception.GetType().Name}）",
                new[] { $"配置文件读取失败：{exception.GetType().Name}" }));
        }

        if (!read.Exists)
        {
            var reason = string.IsNullOrWhiteSpace(read.Error) ? "配置文件不存在" : $"配置文件不可读：{read.Error}";
            return Task.FromResult(ConfigEvidence.Unavailable(
                ClientId,
                NiceConfigParser.ParserVersion,
                ConfigProvenance.DiskOnly,
                ConfigPath,
                readAt,
                reason,
                new[] { reason }));
        }

        if (read.Content is null || read.Content.Length == 0)
        {
            const string reason = "配置文件为空或内容不可读";
            return Task.FromResult(ConfigEvidence.Unavailable(
                ClientId,
                NiceConfigParser.ParserVersion,
                ConfigProvenance.DiskOnly,
                ConfigPath,
                readAt,
                reason,
                new[] { reason }));
        }

        if (read.Content.Length > ConfigEvidence.MaxBytes)
        {
            var reason = $"配置文件超过 {ConfigEvidence.MaxBytes} 字节读取上限，未解析";
            return Task.FromResult(ConfigEvidence.Unavailable(
                ClientId,
                NiceConfigParser.ParserVersion,
                ConfigProvenance.DiskOnly,
                ConfigPath,
                readAt,
                reason,
                new[] { reason }));
        }

        var hash = ConfigEvidence.Hash(read.Content);
        if (!ConfigEvidence.TryDecodeUtf8(read.Content, out var text, out var decodeReason))
        {
            return Task.FromResult(new ClientConfigEvidence
            {
                ClientId = ClientId,
                ParserVersion = NiceConfigParser.ParserVersion,
                Provenance = ConfigProvenance.DiskOnly,
                SourcePath = ConfigPath,
                ReadAt = readAt,
                FileModifiedAt = read.LastModifiedAt,
                ContentHash = hash,
                Valid = false,
                ValidityReason = decodeReason,
                Facts = new Dictionary<string, string>(StringComparer.Ordinal),
                Gaps = new[] { decodeReason! },
            });
        }

        var parsed = NiceConfigParser.Parse(text);
        return Task.FromResult(new ClientConfigEvidence
        {
            ClientId = ClientId,
            ParserVersion = NiceConfigParser.ParserVersion,
            Provenance = ConfigProvenance.DiskOnly,
            SourcePath = ConfigPath,
            ReadAt = readAt,
            FileModifiedAt = read.LastModifiedAt,
            ContentHash = hash,
            Valid = parsed.Valid,
            ValidityReason = parsed.ValidityReason,
            Facts = parsed.Facts,
            Notices = parsed.Notices,
            Coverage = parsed.Coverage,
            Gaps = parsed.Gaps,
        });
    }
}
