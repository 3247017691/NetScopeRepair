using System.Text.Json.Serialization;
using NetScope.Domain;

namespace NetScope.Repair;

[JsonConverter(typeof(WireEnumJsonConverter<CapabilityStatus>))]
public enum CapabilityStatus
{
    /// <summary>
    /// Only after a completed platform experiment covered the exact Windows build × driver ×
    /// API × store × action combination, including readback, restore and side effects.
    /// </summary>
    [JsonStringEnumMemberName("available")]
    Available,

    /// <summary>The implementation or the experiment record is missing; the action stays off.</summary>
    [JsonStringEnumMemberName("unavailable")]
    Unavailable,

    /// <summary>Product policy forbids this target even if a platform experiment exists.</summary>
    [JsonStringEnumMemberName("disallowed")]
    Disallowed,
}

/// <summary>Identity of one platform combination that an action capability is verified against.</summary>
public sealed record PlatformSignature
{
    public required int WindowsBuild { get; init; }

    public required string DriverKey { get; init; }

    public required string DriverVersion { get; init; }

    public required string ApiId { get; init; }

    public required RepairActionKind Action { get; init; }

    public required RepairStore Store { get; init; }

    public required RepairAddressFamily Family { get; init; }

    public string Key =>
        WindowsBuild.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "|" + DriverKey + "|" + DriverVersion + "|" + ApiId
        + "|" + WireNames.Of(Action) + "|" + WireNames.Of(Store) + "|" + WireNames.Of(Family);
}

/// <summary>
/// Checks a platform experiment must have performed before a combination can be marked
/// available. A missing check means the side-effect/closure boundary was not proven.
/// </summary>
public static class RepairExperimentChecks
{
    public const string Setter = "setter";
    public const string GetterReadback = "getter_readback";
    public const string ExactRestore = "exact_restore";
    public const string NonTargetFields = "non_target_fields";
    public const string PersistentStoreSideEffect = "persistent_store_side_effect";
    public const string CouplingClosed = "coupling_closed";

    public static IReadOnlyList<string> RequiredFor(RepairActionKind action)
    {
        var common = new[]
        {
            Setter,
            GetterReadback,
            ExactRestore,
            NonTargetFields,
            PersistentStoreSideEffect,
        };

        return action == RepairActionKind.SetTunIpv4AutomaticMetric
            ? common.Append(CouplingClosed).ToArray()
            : common;
    }
}

/// <summary>
/// Capability record for one action/platform combination. There is no implicit availability:
/// an available record must cite an experiment id, evidence and every required check.
/// </summary>
public sealed record PlatformCapability
{
    public required string CapabilityId { get; init; }

    public required PlatformSignature Signature { get; init; }

    public required CapabilityStatus Status { get; init; }

    public required string ReasonCode { get; init; }

    public required string Reason { get; init; }

    public string? ExperimentId { get; init; }

    public DateTimeOffset? VerifiedAtUtc { get; init; }

    public IReadOnlyList<string> EvidenceIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> VerifiedChecks { get; init; } = Array.Empty<string>();

    public bool HasCompleteVerification
    {
        get
        {
            var required = RepairExperimentChecks.RequiredFor(Signature.Action);
            return Status == CapabilityStatus.Available
                && !string.IsNullOrWhiteSpace(ExperimentId)
                && VerifiedAtUtc is not null
                && EvidenceIds.Count > 0
                && required.All(check => VerifiedChecks.Contains(check, StringComparer.Ordinal));
        }
    }

    public bool IsAvailable => Status == CapabilityStatus.Available && HasCompleteVerification;

    public static PlatformCapability Unavailable(
        PlatformSignature signature,
        string reasonCode,
        string reason,
        string capabilityId = "unverified") => new()
        {
            CapabilityId = capabilityId,
            Signature = signature,
            Status = CapabilityStatus.Unavailable,
            ReasonCode = reasonCode,
            Reason = reason,
        };

    public static PlatformCapability Disallowed(
        PlatformSignature signature,
        string reasonCode,
        string reason) => new()
        {
            CapabilityId = "policy:" + reasonCode,
            Signature = signature,
            Status = CapabilityStatus.Disallowed,
            ReasonCode = reasonCode,
            Reason = reason,
        };

    /// <summary>Used only by a future verified experiment import, never by this delivery.</summary>
    public static PlatformCapability Verified(
        PlatformSignature signature,
        string capabilityId,
        string experimentId,
        DateTimeOffset verifiedAtUtc,
        IEnumerable<string> evidenceIds,
        IEnumerable<string> checks)
    {
        var evidence = evidenceIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        var checkList = checks.Distinct(StringComparer.Ordinal).ToArray();

        if (string.IsNullOrWhiteSpace(capabilityId)
            || string.IsNullOrWhiteSpace(experimentId)
            || evidence.Length == 0
            || checkList.Length == 0)
        {
            throw new ArgumentException(
                "A verified capability needs an id, an experiment id, evidence ids and verified checks.");
        }

        return new PlatformCapability
        {
            CapabilityId = capabilityId,
            Signature = signature,
            Status = CapabilityStatus.Available,
            ReasonCode = "verified_platform_experiment",
            Reason = "已验证的组合：实验 " + experimentId + " 覆盖所需读写、恢复与副作用检查。",
            ExperimentId = experimentId,
            VerifiedAtUtc = verifiedAtUtc,
            EvidenceIds = evidence,
            VerifiedChecks = checkList,
        };
    }
}

/// <summary>Environment identity used to look up capability records.</summary>
public sealed record PlatformEnvironment
{
    public required int WindowsBuild { get; init; }

    public required string DriverKey { get; init; }

    public required string DriverVersion { get; init; }

    public required string ApiId { get; init; }

    public required string MatrixVersion { get; init; }

    /// <summary>
    /// This delivery has no isolated VM evidence, so the environment deliberately contains no
    /// verified record. It is not a wildcard and cannot match a verified experiment.
    /// </summary>
    public static PlatformEnvironment Unverified { get; } = new()
    {
        WindowsBuild = 0,
        DriverKey = "unknown",
        DriverVersion = "unknown",
        ApiId = "unverified",
        MatrixVersion = "t13-capability-matrix/1.0.0",
    };
}

/// <summary>
/// Policy-first capability matrix. Family, store and the RecoverTransaction boundary are
/// rejected before any record is consulted, so a later verified record cannot widen the V1
/// scope (IPv4 ActiveStore TUN only).
/// </summary>
public sealed class PlatformCapabilityMatrix
{
    private readonly Dictionary<string, PlatformCapability> _records;

    private PlatformCapabilityMatrix(
        PlatformEnvironment environment,
        IEnumerable<PlatformCapability> records,
        bool isSynthetic)
    {
        Environment = environment;
        IsSynthetic = isSynthetic;
        _records = records.ToDictionary(record => record.Signature.Key, StringComparer.Ordinal);
    }

    public PlatformEnvironment Environment { get; }

    public bool IsSynthetic { get; }

    public int VerifiedCount => _records.Values.Count(record => record.IsAvailable);

    public IReadOnlyList<PlatformCapability> Records => _records.Values
        .OrderBy(record => record.Signature.Key, StringComparer.Ordinal)
        .ToArray();

    /// <summary>The delivery default: policy checks plus no verified combination at all.</summary>
    public static PlatformCapabilityMatrix UnverifiedDefault { get; } =
        new(PlatformEnvironment.Unverified, Array.Empty<PlatformCapability>(), isSynthetic: false);

    /// <summary>Test-only constructor; synthetic records may never be used to claim real support.</summary>
    public static PlatformCapabilityMatrix ForSyntheticTests(
        PlatformEnvironment environment,
        IEnumerable<PlatformCapability> records) => new(environment, records, isSynthetic: true);

    public PlatformSignature SignatureFor(
        RepairActionKind action,
        RepairStore store,
        RepairAddressFamily family) => new()
        {
            WindowsBuild = Environment.WindowsBuild,
            DriverKey = Environment.DriverKey,
            DriverVersion = Environment.DriverVersion,
            ApiId = Environment.ApiId,
            Action = action,
            Store = store,
            Family = family,
        };

    public PlatformCapability Resolve(
        RepairActionKind action,
        RepairStore store,
        RepairAddressFamily family)
    {
        var signature = SignatureFor(action, store, family);

        if (action == RepairActionKind.RecoverTransaction)
        {
            return PlatformCapability.Unavailable(
                signature,
                "transaction_journal_not_implemented",
                "事务 journal 与精确恢复尚未实现（T15），没有可信的 RecoverTransaction 能力。");
        }

        if (store != RepairStore.ActiveStore)
        {
            return PlatformCapability.Disallowed(
                signature,
                "persistent_store_not_in_v1",
                "V1 只开放通过重启持久性与传播验证的 ActiveStore；PersistentStore 需独立实验，禁止静默双写。");
        }

        if (family != RepairAddressFamily.Ipv4)
        {
            return PlatformCapability.Disallowed(
                signature,
                "family_not_in_v1",
                "V1 只开放 IPv4 TUN 字段；IPv6 需分族实验与分族证据。");
        }

        if (!_records.TryGetValue(signature.Key, out var record))
        {
            return PlatformCapability.Unavailable(
                signature,
                "no_verified_platform_experiment",
                DefaultReason(action));
        }

        if (!record.IsAvailable)
        {
            return record with
            {
                Status = CapabilityStatus.Unavailable,
                ReasonCode = "capability_record_incomplete",
                Reason = "能力记录 " + record.CapabilityId + " 不满足完整验证要求（实验、证据、检查项或时间缺失）。",
            };
        }

        return record;
    }

    public static string DefaultReason(RepairActionKind action) => action switch
    {
        RepairActionKind.SetTunIpv4InterfaceMetric =>
            "三字段平台实验尚未在隔离 VM 执行：IPv4 ActiveStore InterfaceMetric 的 setter、getter、读回、精确恢复、非目标字段与 PersistentStore 副作用均未验证。",
        RepairActionKind.SetTunIpv4AutomaticMetric =>
            "三字段平台实验尚未在隔离 VM 执行：AutomaticMetric 与 InterfaceMetric 的耦合影响、前态可还原性和原始 uint 语义均未验证。",
        RepairActionKind.SetTunIpv4IgnoreDefaultRoutes =>
            "三字段平台实验尚未在隔离 VM 执行：IgnoreDefaultRoutes 的 setter、读回、恢复与跨 store 副作用均未验证。",
        RepairActionKind.RecoverTransaction =>
            "事务 journal 与精确恢复尚未实现（T15），没有可信的 RecoverTransaction 能力。",
        _ => "没有已验证的平台实验记录。",
    };
}
