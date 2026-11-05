using NetScope.Application;
using NetScope.Diagnostics.Rules;
using NetScope.Domain;

namespace NetScope.Unit.Tests;

public sealed class DiagnosticConfigurationRegressionTests
{
    [Fact]
    public void DX006_true_contemporaneous_runtime_disk_hash_difference_is_supported()
    {
        var file = ConfigFile();
        var runtime = RuntimeConfig(new string('b', 64));
        var result = new Dx006StaleConfiguration().Evaluate(EvidenceBuilder.Evidence(records: new[] { file, runtime }));
        Assert.NotNull(result);
        Assert.Equal(EvidenceGrade.Supported, result.Level);
        Assert.Equal(new[] { file.EvidenceId, runtime.EvidenceId }, result.EvidenceIds);
        Assert.Empty(result.CounterEvidenceIds);
        Assert.Equal(file.ObservationWindowId, result.Scope.ObservationWindowId);
        Assert.Equal(file.ObservedAt, result.Scope.ValidUntil);
    }

    [Fact]
    public void DX006_matching_effective_runtime_hash_is_counterevidence_even_when_file_predates_process()
    {
        var file = ConfigFile();
        var runtime = RuntimeConfig(new string('a', 64));
        var process = EvidenceBuilder.Record("process", "client_process", ("client", "client-1"),
            ("started_at", "2026-11-04T10:50:00+08:00"));
        var result = new Dx006StaleConfiguration().Evaluate(EvidenceBuilder.Evidence(records: new[] { file, process, runtime }));
        Assert.NotNull(result);
        Assert.Equal(EvidenceGrade.Contradicted, result.Level);
        Assert.Contains(runtime.EvidenceId, result.CounterEvidenceIds);
        Assert.Contains(file.EvidenceId, result.EvidenceIds);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("provenance")]
    [InlineData("disk_only")]
    [InlineData("status")]
    [InlineData("running")]
    [InlineData("path")]
    [InlineData("client")]
    [InlineData("window")]
    [InlineData("boot")]
    [InlineData("time")]
    [InlineData("monotonic")]
    [InlineData("hash")]
    public void DX006_untrusted_or_unrelated_runtime_differences_cannot_support_mismatch(string problem)
    {
        var runtime = RuntimeConfig(new string('b', 64));
        runtime = problem switch
        {
            "source" => runtime with { Source = null },
            "provenance" => runtime with { Provenance = null },
            "disk_only" => runtime with { Provenance = "disk_only" },
            "status" => runtime with { Status = EventStatus.Partial },
            "running" => ChangeFact(runtime, "running", "false"),
            "path" => ChangeFact(runtime, "config_path", "C:/different.yaml"),
            "client" => ChangeFact(runtime, "client", "other-client"),
            "window" => runtime with { ObservationWindowId = "old-window" },
            "boot" => runtime with { BootId = "old-boot" },
            "time" => runtime with { ObservedAt = runtime.ObservedAt!.Value.AddDays(-7) },
            "monotonic" => runtime with { MonotonicMs = null },
            _ => ChangeFact(runtime, "config_hash", ""),
        };
        var result = new Dx006StaleConfiguration().Evaluate(EvidenceBuilder.Evidence(records: new[] { ConfigFile(), runtime }));
        Assert.True(result is null || result.Level == EvidenceGrade.Candidate);
    }

    [Fact]
    public void DX006_inconsistent_runtime_samples_remain_candidate_and_keep_matching_counterevidence()
    {
        var matching = RuntimeConfig(new string('a', 64));
        var differing = RuntimeConfig(new string('b', 64)) with { EvidenceId = "different-runtime" };
        var result = new Dx006StaleConfiguration().Evaluate(EvidenceBuilder.Evidence(records: new[] { ConfigFile(), differing, matching }));
        Assert.NotNull(result);
        Assert.Equal(EvidenceGrade.Candidate, result.Level);
        Assert.Contains(matching.EvidenceId, result.CounterEvidenceIds);
        Assert.Contains(differing.EvidenceId, result.EvidenceIds);
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("empty_endpoint")]
    [InlineData("invalid_endpoint")]
    [InlineData("invalid_port")]
    [InlineData("owner")]
    [InlineData("permission")]
    [InlineData("unknown_error")]
    [InlineData("status")]
    [InlineData("owner_status")]
    [InlineData("source")]
    [InlineData("provenance")]
    [InlineData("window")]
    [InlineData("boot")]
    [InlineData("family")]
    [InlineData("time")]
    [InlineData("monotonic")]
    public void DX005_incomplete_or_unrelated_ownership_never_supports_contention(string problem)
    {
        var failure = BindFailure();
        var owner = Ownership();
        switch (problem)
        {
            case "endpoint": owner = ChangeFact(owner, "endpoint", "127.0.0.1:9090"); break;
            case "empty_endpoint": failure = ChangeFact(failure, "endpoint", ""); owner = ChangeFact(owner, "endpoint", ""); break;
            case "invalid_endpoint": failure = ChangeFact(failure, "endpoint", "unknown"); owner = ChangeFact(owner, "endpoint", "unknown"); break;
            case "invalid_port": failure = ChangeFact(failure, "endpoint", "127.0.0.1:0"); owner = ChangeFact(owner, "endpoint", "127.0.0.1:0"); break;
            case "owner": owner = ChangeFact(owner, "owner_process", " "); break;
            case "permission": failure = ChangeFact(failure, "error", "access_denied"); break;
            case "unknown_error": failure = ChangeFact(failure, "error", ""); break;
            case "status": failure = failure with { Status = EventStatus.AccessDenied }; break;
            case "owner_status": owner = owner with { Status = EventStatus.Unknown }; break;
            case "source": owner = owner with { Source = null }; break;
            case "provenance": failure = failure with { Provenance = null }; break;
            case "window": owner = owner with { ObservationWindowId = "other-window" }; break;
            case "boot": owner = owner with { BootId = "other-boot" }; break;
            case "family": owner = owner with { AddressFamily = "ipv6" }; break;
            case "time": owner = owner with { ObservedAt = owner.ObservedAt!.Value.AddDays(7) }; break;
            case "monotonic": owner = owner with { MonotonicMs = null }; break;
        }
        var result = new Dx005PortOwnershipConflict().Evaluate(EvidenceBuilder.Evidence(records: new[] { failure, owner }));
        Assert.NotNull(result);
        Assert.Equal(EvidenceGrade.Candidate, result.Level);
        Assert.Empty(result.CounterEvidenceIds);
    }

    [Fact]
    public void DX005_access_denied_with_missing_endpoint_and_owner_reproduces_C14_without_support()
    {
        var failure = EvidenceBuilder.Record("bind", "port_bind_failure", ("error", "access_denied"));
        var owner = EvidenceBuilder.Record("owner", "port_ownership");
        var result = new Dx005PortOwnershipConflict().Evaluate(EvidenceBuilder.Evidence(records: new[] { failure, owner }));
        Assert.NotNull(result);
        Assert.Equal(EvidenceGrade.Candidate, result.Level);
        Assert.DoesNotContain(owner.EvidenceId, result.EvidenceIds);
    }

    [Fact]
    public void DX005_same_normalized_IPv6_endpoint_with_owner_and_contention_is_supported()
    {
        var failure = ChangeFact(BindFailure(), "endpoint", "[0:0:0:0:0:0:0:1]:7890") with { AddressFamily = "ipv6" };
        var owner = ChangeFact(Ownership(), "endpoint", "[::1]:7890") with { AddressFamily = "ipv6" };
        var result = new Dx005PortOwnershipConflict().Evaluate(EvidenceBuilder.Evidence(records: new[] { failure, owner }));
        Assert.NotNull(result);
        Assert.Equal(EvidenceGrade.Supported, result.Level);
        Assert.Equal("ipv6", result.Scope.AddressFamily);
    }

    private static EvidenceRecord ConfigFile() => EvidenceBuilder.Record("config-file", "client_config_file",
        ("client", "client-1"), ("config_path", "C:/config.yaml"), ("config_hash", new string('a', 64)),
        ("mtime", "2026-10-01T09:00:00+08:00"));

    private static EvidenceRecord RuntimeConfig(string hash) => EvidenceBuilder.Record("runtime", "client_runtime_config",
        ("client", "client-1"), ("config_path", "C:/config.yaml"), ("config_hash", hash), ("running", "true"));

    private static EvidenceRecord BindFailure() => EvidenceBuilder.Record("bind", "port_bind_failure",
        ("endpoint", "127.0.0.1:7890"), ("error", "address_in_use"));

    private static EvidenceRecord Ownership() => EvidenceBuilder.Record("owner", "port_ownership",
        ("endpoint", "127.0.0.1:7890"), ("owner_process", "synthetic-proxy"));

    private static EvidenceRecord ChangeFact(EvidenceRecord record, string key, string value) => record with
    {
        Facts = record.Facts.Where(pair => pair.Key != key).Append(new KeyValuePair<string, string>(key, value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
    };
}
