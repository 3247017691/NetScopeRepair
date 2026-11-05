using System.Security;
using NetScope.Application;

namespace NetScope.Adapters;

/// <summary>
/// Reads the Apex (mihomo-based) shared_preferences.json read-only. When a runtime API seam
/// supplies already-minimized facts the evidence is labelled <see cref="ConfigProvenance.RuntimeApi"/>;
/// when a supplied start reference names this exact file the disk evidence is labelled
/// <see cref="ConfigProvenance.ProcessStartReference"/>; otherwise it is DiskOnly. Provenance is
/// never upgraded by inference. Only five whitelisted facts are read; subscriptions, credentials,
/// provider lists and full configuration text are never touched.
/// </summary>
public sealed class ApexAdapter : IClientConfigAdapter
{
    private readonly IConfigFileSource _files;
    private readonly IApexRuntimeApi? _runtimeApi;
    private readonly IProcessStartReferenceSource? _startReferences;

    public ApexAdapter(
        IConfigFileSource files,
        string? configPath = null,
        IApexRuntimeApi? runtimeApi = null,
        IProcessStartReferenceSource? startReferences = null)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        ConfigPath = string.IsNullOrWhiteSpace(configPath) ? DefaultConfigPath() : configPath;
        _runtimeApi = runtimeApi;
        _startReferences = startReferences;
    }

    public string ClientId => KnownClientIds.Apex;

    /// <summary>Resolved local path. It is displayed in the local UI only and replaced on export.</summary>
    public string ConfigPath { get; }

    /// <summary>
    /// %APPDATA%\com.nfsqapp.clients.apex\农夫山泉-极速版\shared_preferences.json, derived without
    /// hard-coding a user name.
    /// </summary>
    public static string DefaultConfigPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "com.nfsqapp.clients.apex", "农夫山泉-极速版", "shared_preferences.json");
    }

    public Task<ClientConfigEvidence> ReadAsync(DateTimeOffset readAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var runtime = TryReadRuntime();
        var runtimeFacts = runtime is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : ApexConfigParser.NormalizeRuntimeFacts(runtime.Facts);

        if (runtimeFacts.Count > 0)
        {
            var runtimeGaps = new List<string>
            {
                "runtime API 证据不包含磁盘 mtime/哈希；磁盘文件未作为本证据来源。",
            };
            if (runtime!.Error is not null)
            {
                runtimeGaps.Add("runtime API 报告证据不完整；仅保留可验证的白名单事实。");
            }

            return Task.FromResult(new ClientConfigEvidence
            {
                ClientId = ClientId,
                ParserVersion = ApexConfigParser.ParserVersion,
                Provenance = ConfigProvenance.RuntimeApi,
                SourcePath = null,
                ReadAt = readAt,
                FileModifiedAt = null,
                ContentHash = null,
                Valid = true,
                ValidityReason = null,
                Facts = runtimeFacts,
                Gaps = runtimeGaps,
            });
        }

        return Task.FromResult(ReadDisk(readAt, runtime is null ? null : runtime.Error ?? "runtime_api_no_whitelisted_facts"));
    }

    private ClientConfigEvidence ReadDisk(DateTimeOffset readAt, string? runtimeError)
    {
        var fallbackGap = runtimeError is null
            ? null
            : "runtime API 未提供可用证据，已降级为磁盘证据；运行态配置可能不同。";

        ConfigFileReadResult read;
        try
        {
            read = _files.Read(ConfigPath);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ConfigEvidence.Unavailable(
                ClientId,
                ApexConfigParser.ParserVersion,
                ConfigProvenance.DiskOnly,
                ConfigPath,
                readAt,
                $"配置文件读取失败（{exception.GetType().Name}）",
                WithFallback(new[] { $"配置文件读取失败：{exception.GetType().Name}" }, fallbackGap));
        }

        if (!read.Exists)
        {
            var reason = string.IsNullOrWhiteSpace(read.Error) ? "配置文件不存在" : $"配置文件不可读：{read.Error}";
            return ConfigEvidence.Unavailable(
                ClientId,
                ApexConfigParser.ParserVersion,
                ConfigProvenance.DiskOnly,
                ConfigPath,
                readAt,
                reason,
                WithFallback(new[] { reason }, fallbackGap));
        }

        if (read.Content is null || read.Content.Length == 0)
        {
            const string reason = "配置文件为空或内容不可读";
            return ConfigEvidence.Unavailable(
                ClientId,
                ApexConfigParser.ParserVersion,
                ConfigProvenance.DiskOnly,
                ConfigPath,
                readAt,
                reason,
                WithFallback(new[] { reason }, fallbackGap));
        }

        if (read.Content.Length > ConfigEvidence.MaxBytes)
        {
            var reason = $"配置文件超过 {ConfigEvidence.MaxBytes} 字节读取上限，未解析";
            return ConfigEvidence.Unavailable(
                ClientId,
                ApexConfigParser.ParserVersion,
                ConfigProvenance.DiskOnly,
                ConfigPath,
                readAt,
                reason,
                WithFallback(new[] { reason }, fallbackGap));
        }

        var provenance = ConfigProvenance.DiskOnly;
        string? startReferenceGap = null;
        var startReference = TryReadStartReference();
        if (startReference is not null)
        {
            if (SamePath(startReference.ConfigPath, ConfigPath))
            {
                provenance = ConfigProvenance.ProcessStartReference;
            }
            else
            {
                startReferenceGap = "启动参数引用的是另一个配置路径，未用于升级来源";
            }
        }

        var hash = ConfigEvidence.Hash(read.Content);
        if (!ConfigEvidence.TryDecodeUtf8(read.Content, out var text, out var decodeReason))
        {
            return new ClientConfigEvidence
            {
                ClientId = ClientId,
                ParserVersion = ApexConfigParser.ParserVersion,
                Provenance = provenance,
                SourcePath = ConfigPath,
                ReadAt = readAt,
                FileModifiedAt = read.LastModifiedAt,
                ContentHash = hash,
                Valid = false,
                ValidityReason = decodeReason,
                Facts = new Dictionary<string, string>(StringComparer.Ordinal),
                Gaps = WithFallback(new[] { decodeReason! }, fallbackGap, startReferenceGap),
            };
        }

        var parsed = ApexConfigParser.Parse(text);
        return new ClientConfigEvidence
        {
            ClientId = ClientId,
            ParserVersion = ApexConfigParser.ParserVersion,
            Provenance = provenance,
            SourcePath = ConfigPath,
            ReadAt = readAt,
            FileModifiedAt = read.LastModifiedAt,
            ContentHash = hash,
            Valid = parsed.Valid,
            ValidityReason = parsed.ValidityReason,
            Facts = parsed.Facts,
            Gaps = WithFallback(parsed.Gaps, fallbackGap, startReferenceGap),
        };
    }

    private ApexRuntimeEvidence? TryReadRuntime()
    {
        if (_runtimeApi is null)
        {
            return null;
        }

        try
        {
            return _runtimeApi.TryRead();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ApexRuntimeEvidence { Error = $"runtime_api_error:{exception.GetType().Name}" };
        }
    }

    private ProcessStartReference? TryReadStartReference()
    {
        if (_startReferences is null)
        {
            return null;
        }

        try
        {
            return _startReferences.TryRead(KnownClientIds.Apex);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> WithFallback(
        IReadOnlyList<string> gaps,
        string? fallbackGap,
        string? startReferenceGap = null)
    {
        var merged = new List<string>(gaps);
        if (!string.IsNullOrWhiteSpace(fallbackGap))
        {
            merged.Add(fallbackGap);
        }

        if (!string.IsNullOrWhiteSpace(startReferenceGap))
        {
            merged.Add(startReferenceGap);
        }

        return merged;
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return false;
        }
    }
}
