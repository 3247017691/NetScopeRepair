using System.Text.Json.Serialization;
using NetScope.Domain;

namespace NetScope.Repair;

/// <summary>
/// Declared platform experiment asset. This delivery contains the plan for the experiments, not
/// their execution: every row stays <see cref="Executed"/> = false and <see cref="Result"/> =
/// "not_run". A row cannot become evidence for an available capability by itself.
/// </summary>
public sealed record PlatformExperimentRow
{
    [JsonPropertyName("experiment_id")]
    public required string ExperimentId { get; init; }

    [JsonPropertyName("action")]
    public required RepairActionKind Action { get; init; }

    [JsonPropertyName("store")]
    public required RepairStore Store { get; init; }

    [JsonPropertyName("family")]
    public required RepairAddressFamily Family { get; init; }

    [JsonPropertyName("windows_build")]
    public required int WindowsBuild { get; init; }

    [JsonPropertyName("driver_key")]
    public required string DriverKey { get; init; }

    [JsonPropertyName("api_id")]
    public required string ApiId { get; init; }

    [JsonPropertyName("executed")]
    public required bool Executed { get; init; }

    /// <summary>not_run / pass / fail / blocked.</summary>
    [JsonPropertyName("result")]
    public required string Result { get; init; }

    [JsonPropertyName("checks")]
    public IReadOnlyList<string> Checks { get; init; } = Array.Empty<string>();

    [JsonPropertyName("raw_log_path")]
    public string? RawLogPath { get; init; }

    [JsonPropertyName("owner")]
    public string? Owner { get; init; }
}

public sealed record PlatformExperimentManifest
{
    [JsonPropertyName("manifest_version")]
    public required string ManifestVersion { get; init; }

    /// <summary>Must be "not_run" in this delivery; no platform experiment has been executed.</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("not_executed_reason")]
    public required string NotExecutedReason { get; init; }

    [JsonPropertyName("prerequisites")]
    public required IReadOnlyList<string> Prerequisites { get; init; }

    [JsonPropertyName("rows")]
    public required IReadOnlyList<PlatformExperimentRow> Rows { get; init; }
}

/// <summary>
/// Validator for the experiment asset. It rejects a manifest that claims execution or a pass,
/// that omits the VM prerequisites, or that misses a required check for an action.
/// </summary>
public static class PlatformExperimentManifestValidator
{
    public static IReadOnlyList<string> RequiredPrerequisites { get; } = new[]
    {
        "isolated_vm",
        "console",
        "verified_checkpoint",
        "recovery_owner",
    };

    public static IReadOnlyList<string> Validate(PlatformExperimentManifest manifest)
    {
        var issues = new List<string>();

        if (!string.Equals(manifest.Status, "not_run", StringComparison.Ordinal))
        {
            issues.Add("平台实验资产状态必须是 not_run；本轮没有隔离 VM，不能声称实验已执行或已通过。");
        }

        foreach (var prerequisite in RequiredPrerequisites)
        {
            if (!manifest.Prerequisites.Contains(prerequisite, StringComparer.Ordinal))
            {
                issues.Add("缺少实验前置条件：" + prerequisite + "。");
            }
        }

        if (string.IsNullOrWhiteSpace(manifest.NotExecutedReason))
        {
            issues.Add("必须写明本轮未执行平台实验的具体原因。");
        }

        foreach (var action in RepairActionRules.ForwardActions)
        {
            if (!manifest.Rows.Any(row => row.Action == action))
            {
                issues.Add("缺少动作 " + WireNames.Of(action) + " 的平台实验行。");
            }
        }

        if (!manifest.Rows.Any(row => row.Action == RepairActionKind.RecoverTransaction))
        {
            issues.Add("缺少 RecoverTransaction 的平台实验/关闭项。");
        }

        foreach (var row in manifest.Rows)
        {
            if (row.Executed)
            {
                issues.Add(row.ExperimentId + " 声称已执行；没有隔离 VM 证据时不能标记 executed。");
            }

            if (!string.Equals(row.Result, "not_run", StringComparison.Ordinal))
            {
                issues.Add(row.ExperimentId + " 的结果是 '" + row.Result + "'；本轮只能是 not_run。");
            }

            if (row.Store != RepairStore.ActiveStore || row.Family != RepairAddressFamily.Ipv4)
            {
                issues.Add(row.ExperimentId + " 的 store/family 超出 V1 范围；PersistentStore/IPv6 需要独立实验。");
            }

            foreach (var check in RepairExperimentChecks.RequiredFor(row.Action))
            {
                if (!row.Checks.Contains(check, StringComparer.Ordinal))
                {
                    issues.Add(row.ExperimentId + " 缺少必需检查项：" + check + "。");
                }
            }

            if (row.Action == RepairActionKind.SetTunIpv4AutomaticMetric
                && !row.Checks.Contains(RepairExperimentChecks.CouplingClosed, StringComparer.Ordinal))
            {
                issues.Add(row.ExperimentId + " 缺少 AutomaticMetric/InterfaceMetric 耦合检查。");
            }
        }

        return issues;
    }
}
