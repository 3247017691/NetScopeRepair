using NetScope.Application;
using NetScope.Diagnostics;
using NetScope.Diagnostics.Rules;
using NetScope.Domain;

namespace NetScope.Unit.Tests;

/// <summary>Counterexamples from G0 and scope isolation, alongside genuine complete observations.</summary>
public sealed class DiagnosticScopeRegressionTests
{
    [Theory]
    [InlineData(EventStatus.Failed, "connect", "ConnectionRefused")]
    [InlineData(EventStatus.Timeout, "connect", "connection_timeout")]
    public void DX001_supports_only_the_proven_selected_local_proxy_failure(EventStatus status, string stage, string error)
    {
        var probe = LocalProxyFailure() with { Status = status, FailedStage = stage, TransportError = error };
        var result = new Dx001DeadProxyDependency().Evaluate(EvidenceBuilder.Evidence(probes: new[] { probe }));
        Assert.NotNull(result);
        Assert.Equal(EvidenceGrade.Supported, result.Level);
        Assert.Equal(new[] { probe.ProbeId }, result.EvidenceIds);
        Assert.Equal(ProbePath.ExplicitHttpProxy, result.Scope.Path);
        Assert.Equal("ipv4", result.Scope.AddressFamily);
        Assert.Equal("proxy-1", result.Scope.ProxyEndpointId);
        Assert.Equal("window-1", result.Scope.ObservationWindowId);
        Assert.Equal("boot-1", result.Scope.BootId);
        Assert.Equal(probe.RequestOptionsKey, result.Scope.RequestOptionsKey);
        Assert.Equal(probe.StartedAt, result.Scope.StartedAt);
        Assert.Equal(probe.StartedAt.AddMilliseconds(100), result.Scope.EndedAt);
        Assert.Equal(result.Scope.EndedAt, result.Scope.ValidUntil);
    }

    [Theory]
    [InlineData(EventStatus.Failed, "tls", "certificate_validation_failed", "target", true)]
    [InlineData(EventStatus.Failed, "tls", "ConnectionRefused", "proxy", true)]
    [InlineData(EventStatus.AccessDenied, "connect", "ConnectionRefused", "proxy", true)]
    [InlineData(EventStatus.Failed, "connect", "access_denied", "proxy", true)]
    [InlineData(EventStatus.Failed, "proxy_connect", "upstream_connect_failed", "target", true)]
    [InlineData(EventStatus.Failed, "http", "proxy_authentication_required", "proxy", true)]
    [InlineData(EventStatus.Failed, "connect", "ConnectionRefused", "proxy", false)]
    [InlineData(EventStatus.Failed, "connect", "ConnectionRefused", "target", true)]
    public void DX001_never_supports_TLS_permission_upstream_authentication_or_remote_failures(
        EventStatus status, string stage, string error, string endpoint, bool local)
    {
        var probe = LocalProxyFailure() with
        {
            Status = status, FailedStage = stage, TransportError = error, FailedEndpoint = endpoint, ProxyIsLocal = local,
        };
        Assert.Null(new Dx001DeadProxyDependency().Evaluate(EvidenceBuilder.Evidence(probes: new[] { probe })));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("locality")]
    [InlineData("failure_endpoint")]
    public void DX001_missing_endpoint_proof_cannot_support_the_local_proxy_claim(string missing)
    {
        var probe = LocalProxyFailure();
        probe = missing switch
        {
            "identity" => probe with { ProxyEndpointId = null },
            "locality" => probe with { ProxyIsLocal = null },
            _ => probe with { FailedEndpoint = "unknown" },
        };
        var result = new Dx001DeadProxyDependency().Evaluate(EvidenceBuilder.Evidence(probes: new[] { probe }));
        Assert.NotNull(result);
        Assert.Equal(EvidenceGrade.Candidate, result.Level);
        Assert.Empty(result.CounterEvidenceIds);
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("family")]
    [InlineData("method")]
    [InlineData("options")]
    [InlineData("window")]
    [InlineData("boot")]
    [InlineData("timing")]
    [InlineData("late")]
    [InlineData("monotonic")]
    [InlineData("target")]
    [InlineData("topology")]
    public void DX001_success_outside_the_failure_scope_is_not_counterevidence(string difference)
    {
        var failure = LocalProxyFailure();
        var success = ChangeProbe(HealthyProxy(), difference);
        var result = new Dx001DeadProxyDependency().Evaluate(EvidenceBuilder.Evidence(probes: new[] { failure, success }));
        Assert.NotNull(result);
        Assert.Equal(EvidenceGrade.Supported, result.Level);
        Assert.Empty(result.CounterEvidenceIds);
        Assert.DoesNotContain(success.ProbeId, result.EvidenceIds);
    }

    [Theory]
    [InlineData("route")]
    [InlineData("server")]
    [InlineData("unrelated_host_route")]
    public void DX002_route_or_server_is_only_an_unexplained_reserved_range_clue(string clue)
    {
        var snapshot = EvidenceBuilder.Snapshot(
            routes: clue == "server" ? null : new[] { EvidenceBuilder.Route(clue == "route" ? "198.18.0.0/15" : "198.18.9.9/32") },
            dns: clue == "server" ? new DnsState
            {
                ServersByInterface = new Dictionary<int, IReadOnlyList<string>> { [12] = new[] { "198.18.0.2" } },
            } : null);
        var result = new Dx002FakeIpContext().Evaluate(EvidenceBuilder.Evidence(snapshot: snapshot));
        Assert.NotNull(result);
        Assert.Equal(EvidenceGrade.Candidate, result.Level);
        Assert.Equal(new[] { snapshot.SnapshotId }, result.EvidenceIds);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("provenance")]
    [InlineData("disk_only")]
    [InlineData("status")]
    [InlineData("user_report")]
    [InlineData("client")]
    [InlineData("running")]
    [InlineData("mode")]
    [InlineData("family")]
    [InlineData("target")]
    [InlineData("window")]
    [InlineData("boot")]
    [InlineData("time")]
    [InlineData("monotonic")]
    public void DX002_missing_or_unrelated_runtime_context_never_confirms_fake_IP(string missing)
    {
        var runtime = FakeIpRuntime();
        runtime = missing switch
        {
            "source" => runtime with { Source = null },
            "provenance" => runtime with { Provenance = null },
            "disk_only" => runtime with { Provenance = "disk_only" },
            "status" => runtime with { Status = EventStatus.Unknown },
            "user_report" => runtime with { IsUserReport = true },
            "client" => WithoutFact(runtime, "client"),
            "running" => WithoutFact(runtime, "running"),
            "mode" => WithoutFact(runtime, "dns_mode"),
            "family" => runtime with { AddressFamily = "ipv6" },
            "target" => runtime with { TargetId = "other-target" },
            "window" => runtime with { ObservationWindowId = "other-window" },
            "boot" => runtime with { BootId = "other-boot" },
            "time" => runtime with { ObservedAt = runtime.ObservedAt!.Value.AddDays(7) },
            _ => runtime with { MonotonicMs = null },
        };
        var result = new Dx002FakeIpContext().Evaluate(EvidenceBuilder.Evidence(
            probes: new[] { ReservedAnswer() }, records: new[] { runtime }));
        Assert.NotNull(result);
        Assert.Equal(EvidenceGrade.Candidate, result.Level);
    }

    [Theory]
    [InlineData("timing")]
    [InlineData("method")]
    [InlineData("options")]
    [InlineData("monotonic")]
    [InlineData("wrong_family")]
    [InlineData("topology")]
    public void DX002_incomplete_probe_scope_cannot_confirm_a_running_mode_association(string missing)
    {
        var probe = ReservedAnswer();
        probe = missing switch
        {
            "timing" => probe with { TimingComparable = false },
            "method" => probe with { Method = null },
            "options" => probe with { RequestOptionsKey = null },
            "monotonic" => probe with { StartedMonotonicMs = null },
            "wrong_family" => probe with { AddressFamily = "ipv6" },
            _ => probe with { TopologyChanged = true },
        };
        var result = new Dx002FakeIpContext().Evaluate(EvidenceBuilder.Evidence(
            probes: new[] { probe }, records: new[] { FakeIpRuntime() }));
        Assert.NotNull(result);
        Assert.Equal(EvidenceGrade.Candidate, result.Level);
    }

    [Fact]
    public void DX002_explicit_contrary_running_mode_blocks_even_another_fake_IP_record()
    {
        var contrary = EvidenceBuilder.Record("runtime-contrary", "client_runtime_config",
            ("client", "synthetic"), ("running", "true"), ("dns_mode", "redir-host"));
        var result = new Dx002FakeIpContext().Evaluate(EvidenceBuilder.Evidence(
            probes: new[] { ReservedAnswer() }, records: new[] { FakeIpRuntime(), contrary }));
        Assert.NotNull(result);
        Assert.Equal(EvidenceGrade.Candidate, result.Level);
        Assert.Contains(contrary.EvidenceId, result.CounterEvidenceIds);
    }

    [Fact]
    public void DX002_trustworthy_running_adapter_and_actual_reserved_answer_are_sufficient()
    {
        var snapshot = EvidenceBuilder.Snapshot(interfaces: new[]
        {
            EvidenceBuilder.Interface(ifIndex: 40) with { Kind = "tunnel" },
        });
        var adapter = EvidenceBuilder.Record("adapter", "fake_ip_adapter", ("snapshot_id", snapshot.SnapshotId),
            ("if_index", "40"), ("running", "true"), ("fake_ip", "true")) with { Provenance = "adapter_runtime" };
        var result = new Dx002FakeIpContext().Evaluate(EvidenceBuilder.Evidence(
            probes: new[] { ReservedAnswer() }, records: new[] { adapter }, snapshot: snapshot));
        Assert.NotNull(result);
        Assert.Equal(EvidenceGrade.Supported, result.Level);
        Assert.Contains(adapter.EvidenceId, result.EvidenceIds);
        Assert.Contains(snapshot.SnapshotId, result.EvidenceIds);
    }

    [Theory]
    [InlineData("unknown_interface")]
    [InlineData("ethernet")]
    [InlineData("down")]
    [InlineData("wrong_snapshot")]
    public void DX002_unreliable_adapter_context_does_not_confirm_mapping(string problem)
    {
        var snapshot = EvidenceBuilder.Snapshot(interfaces: new[]
        {
            EvidenceBuilder.Interface(ifIndex: 40) with
            {
                Kind = problem == "ethernet" ? "ethernet" : "tunnel",
                OperationalStatus = problem == "down" ? "down" : "up",
            },
        });
        var adapter = EvidenceBuilder.Record("adapter", "fake_ip_adapter",
            ("snapshot_id", problem == "wrong_snapshot" ? "other-snapshot" : snapshot.SnapshotId),
            ("if_index", problem == "unknown_interface" ? "999" : "40"), ("running", "true"), ("fake_ip", "true"))
            with { Provenance = "adapter_runtime" };
        var result = new Dx002FakeIpContext().Evaluate(EvidenceBuilder.Evidence(
            probes: new[] { ReservedAnswer() }, records: new[] { adapter }, snapshot: snapshot));
        Assert.NotNull(result);
        Assert.Equal(EvidenceGrade.Candidate, result.Level);
    }

    [Theory]
    [InlineData(EventStatus.Cancelled)]
    [InlineData(EventStatus.Unsupported)]
    [InlineData(EventStatus.AccessDenied)]
    [InlineData(EventStatus.Partial)]
    [InlineData(EventStatus.Unknown)]
    public void DX008_success_plus_observation_gap_is_not_a_network_failure(EventStatus gapStatus)
    {
        // Even a badly classified producer cannot turn a gap status into a network failure.
        var gap = HealthyProxy() with { Status = gapStatus, Classification = ProbeClassification.TransportFailure };
        Assert.Null(new Dx008IntermittentFailure().Evaluate(EvidenceBuilder.Evidence(probes: new[] { HealthyProxy(), gap })));
    }

    [Theory]
    [InlineData("group_budget_exhausted")]
    [InlineData("user_cancelled")]
    [InlineData("permission_denied")]
    [InlineData("AccessDenied")]
    [InlineData("not_started")]
    public void DX008_timeout_or_failed_with_an_observation_gap_reason_is_excluded(string reason)
    {
        var gap = NetworkTimeout() with { TransportError = reason };
        Assert.Null(new Dx008IntermittentFailure().Evaluate(EvidenceBuilder.Evidence(probes: new[] { HealthyProxy(), gap })));
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("family")]
    [InlineData("method")]
    [InlineData("options")]
    [InlineData("window")]
    [InlineData("boot")]
    [InlineData("late")]
    [InlineData("target")]
    public void DX008_never_merges_different_known_scopes(string difference)
    {
        Assert.Null(new Dx008IntermittentFailure().Evaluate(EvidenceBuilder.Evidence(probes: new[]
        {
            HealthyProxy(), ChangeProbe(NetworkTimeout(), difference),
        })));
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("family")]
    [InlineData("method")]
    [InlineData("options")]
    [InlineData("window")]
    [InlineData("boot")]
    [InlineData("monotonic")]
    [InlineData("timing")]
    public void DX008_missing_comparability_proof_cannot_produce_an_observed_fault(string missing)
    {
        ProbeResult Remove(ProbeResult probe) => missing switch
        {
            "endpoint" => probe with { ProxyEndpointId = null },
            "family" => probe with { AddressFamily = "carrier-pigeon" },
            "method" => probe with { Method = null },
            "options" => probe with { RequestOptionsKey = null },
            "window" => probe with { ObservationWindowId = null },
            "boot" => probe with { BootId = null },
            "monotonic" => probe with { StartedMonotonicMs = null },
            _ => probe with { TimingComparable = false },
        };
        var result = new Dx008IntermittentFailure().Evaluate(EvidenceBuilder.Evidence(probes: new[]
        {
            Remove(HealthyProxy()), Remove(NetworkTimeout()),
        }));
        Assert.NotNull(result);
        Assert.Equal(EvidenceGrade.Candidate, result.Level);
        Assert.Contains("comparable_samples", result.MissingEvidence);
    }

    [Fact]
    public void DX008_reports_valid_failure_denominator_and_observation_gaps_separately()
    {
        var gap = HealthyProxy() with { Status = EventStatus.AccessDenied, TransportError = "access_denied", TimingComparable = false };
        var result = new Dx008IntermittentFailure().Evaluate(EvidenceBuilder.Evidence(probes: new[]
        {
            HealthyProxy(), NetworkTimeout(), gap,
        }));
        Assert.NotNull(result);
        Assert.Equal(EvidenceGrade.Observed, result.Level);
        Assert.Contains("1/2 次有效网络失败", result.Summary, StringComparison.Ordinal);
        Assert.Contains("观察缺口 1/3", result.Summary, StringComparison.Ordinal);
        Assert.Contains("access_denied", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void DX008_unproven_measured_sample_downgrades_the_same_scope_instead_of_disappearing()
    {
        var failure = NetworkTimeout() with { TimingComparable = false };
        var result = new Dx008IntermittentFailure().Evaluate(EvidenceBuilder.Evidence(probes: new[] { HealthyProxy(), failure }));
        Assert.NotNull(result);
        Assert.Equal(EvidenceGrade.Candidate, result.Level);
        Assert.Contains(failure.ProbeId, result.EvidenceIds);
        Assert.Contains("comparable_samples", result.MissingEvidence);
    }

    [Fact]
    public void DX008_topology_change_on_a_gap_still_downgrades_the_whole_observation_window()
    {
        var gap = HealthyProxy() with
        {
            Status = EventStatus.AccessDenied,
            TransportError = "access_denied",
            TimingComparable = false,
            TopologyChanged = true,
        };
        var result = new Dx008IntermittentFailure().Evaluate(EvidenceBuilder.Evidence(probes: new[]
        {
            HealthyProxy(), NetworkTimeout(), gap,
        }));
        Assert.NotNull(result);
        Assert.Equal(EvidenceGrade.Candidate, result.Level);
        Assert.Contains(gap.ProbeId, result.CounterEvidenceIds);
        Assert.Contains("topology_unchanged_window", result.MissingEvidence);
        Assert.Contains("观察缺口 1/3", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void QA020_IPv4_success_and_IPv6_failure_are_separate_outcomes()
    {
        var ipv4 = HealthyProxy();
        var ipv6 = NetworkTimeout() with { AddressFamily = "ipv6", RequestOptionsKey = "ipv6-options" };
        var diagnoses = DiagnosticEngine.CreateDefault().Evaluate(EvidenceBuilder.Evidence(probes: new[] { ipv4, ipv6 }));
        Assert.DoesNotContain(diagnoses, result => result.RuleId == "DX-008");
        Assert.DoesNotContain(diagnoses, result => result.Level == EvidenceGrade.Verified);
    }

    [Fact]
    public void QA020_a_genuine_IPv6_interruption_keeps_its_scope_and_excludes_IPv4_counts()
    {
        var ipv4 = HealthyProxy();
        var ipv6Good = HealthyProxy() with { AddressFamily = "ipv6", RequestOptionsKey = "ipv6-options" };
        var ipv6Bad = NetworkTimeout() with { AddressFamily = "ipv6", RequestOptionsKey = "ipv6-options" };
        var result = new Dx008IntermittentFailure().Evaluate(EvidenceBuilder.Evidence(probes: new[] { ipv4, ipv6Good, ipv6Bad }));
        Assert.NotNull(result);
        Assert.Equal(EvidenceGrade.Observed, result.Level);
        Assert.Equal("ipv6", result.Scope.AddressFamily);
        Assert.Equal(2, result.EvidenceIds.Count);
        Assert.DoesNotContain(ipv4.ProbeId, result.EvidenceIds);
        Assert.Contains("1/2 次有效网络失败", result.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("family")]
    [InlineData("window")]
    [InlineData("boot")]
    [InlineData("time")]
    [InlineData("monotonic")]
    [InlineData("missing_identity")]
    public void DX011_unrelated_or_untimed_success_cannot_contradict_a_listening_sample(string difference)
    {
        var record = EvidenceBuilder.Record("listen", "listening_ports", ("count", "0"));
        record = difference switch
        {
            "endpoint" => record with { ProxyEndpointId = "other-proxy" },
            "family" => record with { AddressFamily = "ipv6" },
            "window" => record with { ObservationWindowId = "other-window" },
            "boot" => record with { BootId = "other-boot" },
            "time" => record with { ObservedAt = record.ObservedAt!.Value.AddDays(7) },
            "monotonic" => record with { MonotonicMs = null },
            _ => record with { ProxyEndpointId = null },
        };
        Assert.Null(new Dx011CollectionContradiction().Evaluate(EvidenceBuilder.Evidence(
            probes: new[] { HealthyProxy() }, records: new[] { record })));
    }

    [Fact]
    public void Default_engine_references_only_actual_input_evidence_and_never_emits_actions_or_verified()
    {
        var snapshot = EvidenceBuilder.Snapshot(routes: new[] { EvidenceBuilder.Route("198.18.0.0/15") });
        var probes = new[] { LocalProxyFailure(), HealthyProxy() };
        var records = new[] { FakeIpRuntime(), EvidenceBuilder.Record("bind", "port_bind_failure", ("error", "access_denied")) };
        var actualIds = probes.Select(probe => probe.ProbeId).Concat(records.Select(record => record.EvidenceId))
            .Append(snapshot.SnapshotId).ToHashSet(StringComparer.Ordinal);
        var diagnoses = DiagnosticEngine.CreateDefault().Evaluate(EvidenceBuilder.Evidence(probes, records, snapshot));
        Assert.NotEmpty(diagnoses);
        Assert.All(diagnoses, diagnosis =>
        {
            Assert.All(diagnosis.EvidenceIds.Concat(diagnosis.CounterEvidenceIds), id => Assert.Contains(id, actualIds));
            Assert.Empty(diagnosis.AllowedRepairActions);
            Assert.NotEqual(EvidenceGrade.Verified, diagnosis.Level);
        });
    }

    private static ProbeResult LocalProxyFailure() => EvidenceBuilder.Probe(ProbePath.ExplicitHttpProxy,
        EventStatus.Failed, "connect", transportError: "ConnectionRefused", failedEndpoint: "proxy", proxyIsLocal: true);

    private static ProbeResult HealthyProxy() => EvidenceBuilder.Probe(ProbePath.ExplicitHttpProxy,
        EventStatus.Ok, httpStatus: 204, proxyIsLocal: true);

    private static ProbeResult NetworkTimeout() => EvidenceBuilder.Probe(ProbePath.ExplicitHttpProxy,
        EventStatus.Timeout, "connect", transportError: "Timeout");

    private static ProbeResult ReservedAnswer() => EvidenceBuilder.Probe(ProbePath.NoExplicitProxy,
        EventStatus.Timeout, "connect", resolvedAddresses: new[] { "198.18.0.11" });

    private static EvidenceRecord FakeIpRuntime() => EvidenceBuilder.Record("runtime", "client_runtime_config",
        ("client", "synthetic"), ("running", "true"), ("dns_mode", "fake-ip"));

    private static EvidenceRecord WithoutFact(EvidenceRecord record, string key) => record with
    {
        Facts = record.Facts.Where(pair => pair.Key != key).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
    };

    private static ProbeResult ChangeProbe(ProbeResult probe, string difference) => difference switch
    {
        "endpoint" => probe with { ProxyEndpointId = "proxy-2" },
        "family" => probe with { AddressFamily = "ipv6" },
        "method" => probe with { Method = "HEAD" },
        "options" => probe with { RequestOptionsKey = "different-options" },
        "window" => probe with { ObservationWindowId = "window-2" },
        "boot" => probe with { BootId = "boot-2" },
        "timing" => probe with { TimingComparable = false },
        "late" => probe with { StartedAt = probe.StartedAt.AddDays(7), StartedMonotonicMs = probe.StartedMonotonicMs + TimeSpan.FromDays(7).TotalMilliseconds },
        "monotonic" => probe with { StartedMonotonicMs = probe.StartedMonotonicMs + 100_000 },
        "target" => probe with { TargetId = "other-target" },
        "topology" => probe with { TopologyChanged = true },
        _ => throw new ArgumentOutOfRangeException(nameof(difference)),
    };
}

