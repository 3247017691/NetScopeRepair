using NetScope.Application;
using NetScope.Diagnostics;
using NetScope.Diagnostics.Rules;
using NetScope.Domain;

namespace NetScope.Unit.Tests;

/// <summary>
/// Rule behaviour for the M0 catalog. Each rule is tested with a positive case, a negative case
/// and the case where the minimum evidence is missing, because that is where a diagnostic tool
/// usually starts inventing certainty.
/// </summary>
public sealed class DiagnosticRulesTests
{
    // ---------- DX-001 dead proxy dependency ----------

    [Fact]
    public void DX001_reports_supported_when_a_request_adopted_a_dead_endpoint()
    {
        var evidence = EvidenceBuilder.Evidence(probes: new[]
        {
            EvidenceBuilder.Probe(ProbePath.ExplicitHttpProxy, EventStatus.Failed, "connect", transportError: "ConnectionRefused", failedEndpoint: "proxy", proxyIsLocal: true),
        });

        var diagnosis = new Dx001DeadProxyDependency().Evaluate(evidence);

        Assert.NotNull(diagnosis);
        Assert.Equal("DX-001", diagnosis!.RuleId);
        Assert.Equal(EvidenceGrade.Supported, diagnosis.Level);
        Assert.Contains("支持", diagnosis.Summary, StringComparison.Ordinal);
        Assert.Contains("direct_connection_to_remote_endpoint", diagnosis.MissingEvidence);
        Assert.Empty(diagnosis.AllowedRepairActions);
    }

    [Fact]
    public void DX001_downgrades_to_candidate_when_the_same_path_also_succeeded()
    {
        var evidence = EvidenceBuilder.Evidence(probes: new[]
        {
            EvidenceBuilder.Probe(ProbePath.ExplicitHttpProxy, EventStatus.Failed, "connect", transportError: "ConnectionRefused", failedEndpoint: "proxy", proxyIsLocal: true),
            EvidenceBuilder.Probe(ProbePath.ExplicitHttpProxy, EventStatus.Ok, httpStatus: 204, group: "group-2", proxyIsLocal: true),
        });

        var diagnosis = new Dx001DeadProxyDependency().Evaluate(evidence);

        Assert.NotNull(diagnosis);
        Assert.Equal(EvidenceGrade.Candidate, diagnosis!.Level);
        Assert.NotEmpty(diagnosis.CounterEvidenceIds);
    }

    [Fact]
    public void DX001_stays_a_candidate_when_only_configuration_exists()
    {
        var evidence = EvidenceBuilder.Evidence(snapshot: EvidenceBuilder.Snapshot(proxies: EvidenceBuilder.Proxies(configured: true)));

        var diagnosis = new Dx001DeadProxyDependency().Evaluate(evidence);

        Assert.NotNull(diagnosis);
        Assert.Equal(EvidenceGrade.Candidate, diagnosis!.Level);
        Assert.Contains("probe_on_explicit_proxy_path", diagnosis.MissingEvidence);
        Assert.Contains("候选", diagnosis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void DX001_stays_silent_without_proxy_configuration_or_probes()
    {
        Assert.Null(new Dx001DeadProxyDependency().Evaluate(EvidenceBuilder.Evidence()));
    }

    // ---------- DX-002 fake-IP context ----------

    [Fact]
    public void DX002_explains_a_fake_ip_mapping_as_context_not_pollution()
    {
        var snapshot = EvidenceBuilder.Snapshot(
            routes: new[] { EvidenceBuilder.Route("198.18.0.0/15") },
            dns: new DnsState
            {
                ServersByInterface = new Dictionary<int, IReadOnlyList<string>> { [40] = new[] { "198.18.0.2" } },
            });

        var evidence = EvidenceBuilder.Evidence(
            probes: new[]
            {
                EvidenceBuilder.Probe(ProbePath.NoExplicitProxy, EventStatus.Timeout, resolvedAddresses: new[] { "198.18.0.11" }),
            },
            records: new[]
            {
                EvidenceBuilder.Record("e-runtime", "client_runtime_config", ("client", "synthetic"), ("running", "true"), ("dns_mode", "fake-ip")),
            },
            snapshot: snapshot);

        var diagnosis = new Dx002FakeIpContext().Evaluate(evidence);

        Assert.NotNull(diagnosis);
        Assert.Equal(EvidenceGrade.Supported, diagnosis!.Level);
        Assert.Contains("fake-IP", diagnosis.Summary, StringComparison.Ordinal);
        Assert.Contains("不是污染证据", diagnosis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void DX002_keeps_an_unexplained_reserved_address_at_candidate_level()
    {
        var evidence = EvidenceBuilder.Evidence(
            probes: new[]
            {
                EvidenceBuilder.Probe(ProbePath.NoExplicitProxy, EventStatus.Timeout, resolvedAddresses: new[] { "198.18.0.11" }),
            },
            snapshot: EvidenceBuilder.Snapshot());

        var diagnosis = new Dx002FakeIpContext().Evaluate(evidence);

        Assert.NotNull(diagnosis);
        Assert.Equal(EvidenceGrade.Candidate, diagnosis!.Level);
        Assert.Contains("不能直接称为污染", diagnosis.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("已确认", diagnosis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void DX002_is_silent_when_no_reserved_range_appears()
    {
        var evidence = EvidenceBuilder.Evidence(
            probes: new[]
            {
                EvidenceBuilder.Probe(ProbePath.SystemDefault, EventStatus.Ok, httpStatus: 204, resolvedAddresses: new[] { "93.184.216.34" }),
            },
            snapshot: EvidenceBuilder.Snapshot(routes: new[] { EvidenceBuilder.Route("0.0.0.0/0") }));

        Assert.Null(new Dx002FakeIpContext().Evaluate(evidence));
    }

    // ---------- DX-005 port ownership conflict ----------

    [Fact]
    public void DX005_supports_a_conflict_only_with_a_bind_failure_and_ownership()
    {
        var evidence = EvidenceBuilder.Evidence(records: new[]
        {
            EvidenceBuilder.Record("e-bind", "port_bind_failure", ("endpoint", "127.0.0.1:7890"), ("error", "address_in_use")),
            EvidenceBuilder.Record("e-owner", "port_ownership", ("endpoint", "127.0.0.1:7890"), ("owner_process", "mihomo")),
        });

        var diagnosis = new Dx005PortOwnershipConflict().Evaluate(evidence);

        Assert.NotNull(diagnosis);
        Assert.Equal(EvidenceGrade.Supported, diagnosis!.Level);
        Assert.Contains("e-owner", diagnosis.EvidenceIds);
    }

    [Fact]
    public void DX005_keeps_a_bare_bind_failure_at_candidate_level()
    {
        var evidence = EvidenceBuilder.Evidence(records: new[]
        {
            EvidenceBuilder.Record("e-bind", "port_bind_failure", ("endpoint", "127.0.0.1:7890"), ("error", "address_in_use")),
        });

        var diagnosis = new Dx005PortOwnershipConflict().Evaluate(evidence);

        Assert.NotNull(diagnosis);
        Assert.Equal(EvidenceGrade.Candidate, diagnosis!.Level);
        Assert.Contains("port_ownership", diagnosis.MissingEvidence);
    }

    [Fact]
    public void DX005_never_calls_two_clients_on_one_endpoint_a_conflict()
    {
        var evidence = EvidenceBuilder.Evidence(records: new[]
        {
            EvidenceBuilder.Record("e-nice", "client_configuration", ("client", "nice"), ("address", "127.0.0.1"), ("port", "7890")),
            EvidenceBuilder.Record("e-mihomo", "client_configuration", ("client", "mihomo"), ("address", "127.0.0.1"), ("port", "7890")),
        });

        var diagnosis = new Dx005PortOwnershipConflict().Evaluate(evidence);

        Assert.NotNull(diagnosis);
        Assert.Equal(EvidenceGrade.Candidate, diagnosis!.Level);
        Assert.Contains("不足以判定", diagnosis.Summary, StringComparison.Ordinal);
        Assert.Contains("actual_bind_failure", diagnosis.MissingEvidence);
    }

    [Fact]
    public void DX005_is_silent_when_clients_use_different_ports()
    {
        var evidence = EvidenceBuilder.Evidence(records: new[]
        {
            EvidenceBuilder.Record("e-nice", "client_configuration", ("client", "nice"), ("address", "127.0.0.1"), ("port", "7890")),
            EvidenceBuilder.Record("e-mihomo", "client_configuration", ("client", "mihomo"), ("address", "127.0.0.1"), ("port", "9090")),
        });

        Assert.Null(new Dx005PortOwnershipConflict().Evaluate(evidence));
    }

    // ---------- DX-006 stale configuration ----------

    [Fact]
    public void DX006_keeps_file_age_as_an_unverified_candidate()
    {
        var evidence = EvidenceBuilder.Evidence(records: new[]
        {
            EvidenceBuilder.Record("e-file", "client_config_file", ("client", "nice"), ("mtime", "2026-10-01T09:00:00+08:00")),
            EvidenceBuilder.Record("e-process", "client_process", ("client", "nice"), ("started_at", "2026-11-04T10:50:00+08:00")),
        });

        var diagnosis = new Dx006StaleConfiguration().Evaluate(evidence);

        Assert.NotNull(diagnosis);
        Assert.Equal(EvidenceGrade.Candidate, diagnosis!.Level);
        Assert.Contains("单凭 mtime 不能证明", diagnosis.Summary, StringComparison.Ordinal);
        Assert.Contains("runtime_api_configuration", diagnosis.MissingEvidence);
    }

    [Fact]
    public void DX006_is_silent_when_the_file_is_newer_than_the_process()
    {
        var evidence = EvidenceBuilder.Evidence(records: new[]
        {
            EvidenceBuilder.Record("e-file", "client_config_file", ("client", "nice"), ("mtime", "2026-11-04T11:00:00+08:00")),
            EvidenceBuilder.Record("e-process", "client_process", ("client", "nice"), ("started_at", "2026-11-04T10:50:00+08:00")),
        });

        Assert.Null(new Dx006StaleConfiguration().Evaluate(evidence));
    }

    [Fact]
    public void DX006_is_silent_without_process_evidence()
    {
        var evidence = EvidenceBuilder.Evidence(records: new[]
        {
            EvidenceBuilder.Record("e-file", "client_config_file", ("client", "nice"), ("mtime", "2026-10-01T09:00:00+08:00")),
        });

        Assert.Null(new Dx006StaleConfiguration().Evaluate(evidence));
    }

    // ---------- DX-008 intermittent failure ----------

    [Fact]
    public void DX008_reports_counts_and_forbids_treating_the_next_success_as_a_fix()
    {
        var evidence = EvidenceBuilder.Evidence(probes: new[]
        {
            EvidenceBuilder.Probe(ProbePath.ExplicitHttpProxy, EventStatus.Ok, httpStatus: 204),
            EvidenceBuilder.Probe(ProbePath.ExplicitHttpProxy, EventStatus.Timeout),
        });

        var diagnosis = new Dx008IntermittentFailure().Evaluate(evidence);

        Assert.NotNull(diagnosis);
        Assert.Equal(EvidenceGrade.Observed, diagnosis!.Level);
        Assert.Contains("1/2", diagnosis.Summary, StringComparison.Ordinal);
        Assert.Contains("不能当作修复证据", diagnosis.Summary, StringComparison.Ordinal);
        Assert.Equal(2, diagnosis.EvidenceIds.Count);
    }

    [Fact]
    public void DX008_downgrades_when_the_sample_window_changed_topology()
    {
        var evidence = EvidenceBuilder.Evidence(probes: new[]
        {
            EvidenceBuilder.Probe(ProbePath.ExplicitHttpProxy, EventStatus.Ok, httpStatus: 204),
            EvidenceBuilder.Probe(ProbePath.ExplicitHttpProxy, EventStatus.Timeout, topologyChanged: true, group: "group-2"),
        });

        var diagnosis = new Dx008IntermittentFailure().Evaluate(evidence);

        Assert.NotNull(diagnosis);
        Assert.Equal(EvidenceGrade.Candidate, diagnosis!.Level);
        Assert.NotEmpty(diagnosis.CounterEvidenceIds);
        Assert.Contains("topology_unchanged_window", diagnosis.MissingEvidence);
    }

    [Fact]
    public void DX008_is_silent_when_a_path_is_uniformly_healthy_or_uniformly_broken()
    {
        var allGood = EvidenceBuilder.Evidence(probes: new[]
        {
            EvidenceBuilder.Probe(ProbePath.ExplicitHttpProxy, EventStatus.Ok, httpStatus: 204),
            EvidenceBuilder.Probe(ProbePath.ExplicitHttpProxy, EventStatus.Ok, httpStatus: 204, group: "group-2"),
        });

        var allBad = EvidenceBuilder.Evidence(probes: new[]
        {
            EvidenceBuilder.Probe(ProbePath.ExplicitHttpProxy, EventStatus.Timeout),
            EvidenceBuilder.Probe(ProbePath.ExplicitHttpProxy, EventStatus.Timeout, group: "group-2"),
        });

        Assert.Null(new Dx008IntermittentFailure().Evaluate(allGood));
        Assert.Null(new Dx008IntermittentFailure().Evaluate(allBad));
    }

    // ---------- DX-010 routing no-op ----------

    [Fact]
    public void DX010_contradicts_a_change_whose_target_values_are_already_in_place()
    {
        var evidence = EvidenceBuilder.Evidence(
            snapshot: EvidenceBuilder.Snapshot(interfaces: new[] { EvidenceBuilder.Interface(metric: 1) }),
            changes: new[]
            {
                new ProposedChange
                {
                    Field = "interface_metric",
                    TargetInterfaceGuid = "guid-1",
                    DesiredValue = "1",
                },
            });

        var diagnosis = new Dx010RoutingNoOp().Evaluate(evidence);

        Assert.NotNull(diagnosis);
        Assert.Equal(EvidenceGrade.Contradicted, diagnosis!.Level);
        Assert.Contains("no-op", diagnosis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void DX010_is_silent_when_the_proposed_value_differs()
    {
        var evidence = EvidenceBuilder.Evidence(
            snapshot: EvidenceBuilder.Snapshot(interfaces: new[] { EvidenceBuilder.Interface(metric: 1) }),
            changes: new[]
            {
                new ProposedChange
                {
                    Field = "interface_metric",
                    TargetInterfaceGuid = "guid-1",
                    DesiredValue = "5",
                },
            });

        Assert.Null(new Dx010RoutingNoOp().Evaluate(evidence));
    }

    [Fact]
    public void DX010_is_silent_without_a_proposed_change()
    {
        var evidence = EvidenceBuilder.Evidence(
            snapshot: EvidenceBuilder.Snapshot(interfaces: new[] { EvidenceBuilder.Interface(metric: 1) }));

        Assert.Null(new Dx010RoutingNoOp().Evaluate(evidence));
    }

    // ---------- DX-011 collection contradiction ----------

    [Fact]
    public void DX011_refuses_to_report_a_closed_port_from_an_empty_list()
    {
        var evidence = EvidenceBuilder.Evidence(
            probes: new[]
            {
                EvidenceBuilder.Probe(ProbePath.ExplicitHttpProxy, EventStatus.Ok, httpStatus: 204, proxyIsLocal: true),
            },
            records: new[]
            {
                EvidenceBuilder.Record("e-listen", "listening_ports", ("count", "0"), ("endpoint", "127.0.0.1:7890")),
            });

        var diagnosis = new Dx011CollectionContradiction().Evaluate(evidence);

        Assert.NotNull(diagnosis);
        Assert.Equal(EvidenceGrade.Insufficient, diagnosis!.Level);
        Assert.Contains("不能据此报告", diagnosis.Summary, StringComparison.Ordinal);
        Assert.NotEmpty(diagnosis.CounterEvidenceIds);
    }

    [Fact]
    public void DX011_is_silent_when_the_connection_also_failed()
    {
        var evidence = EvidenceBuilder.Evidence(
            probes: new[]
            {
                EvidenceBuilder.Probe(ProbePath.ExplicitHttpProxy, EventStatus.Failed, "connect"),
            },
            records: new[]
            {
                EvidenceBuilder.Record("e-listen", "listening_ports", ("count", "0")),
            });

        Assert.Null(new Dx011CollectionContradiction().Evaluate(evidence));
    }

    [Fact]
    public void DX011_is_silent_when_the_list_was_populated()
    {
        var evidence = EvidenceBuilder.Evidence(
            probes: new[]
            {
                EvidenceBuilder.Probe(ProbePath.ExplicitHttpProxy, EventStatus.Ok, httpStatus: 204, proxyIsLocal: true),
            },
            records: new[]
            {
                EvidenceBuilder.Record("e-listen", "listening_ports", ("count", "7")),
            });

        Assert.Null(new Dx011CollectionContradiction().Evaluate(evidence));
    }

    // ---------- engine ----------

    [Fact]
    public void Engine_runs_the_catalog_in_a_stable_order_and_assigns_ids()
    {
        var evidence = EvidenceBuilder.Evidence(probes: new[]
        {
            EvidenceBuilder.Probe(ProbePath.ExplicitHttpProxy, EventStatus.Failed, "connect", transportError: "ConnectionRefused", failedEndpoint: "proxy", proxyIsLocal: true),
            EvidenceBuilder.Probe(ProbePath.ExplicitHttpProxy, EventStatus.Ok, httpStatus: 204, group: "group-2", proxyIsLocal: true),
        });

        var diagnoses = DiagnosticEngine.CreateDefault().Evaluate(evidence);

        Assert.NotEmpty(diagnoses);
        Assert.Equal(diagnoses.Select(item => item.RuleId).OrderBy(id => id, StringComparer.Ordinal), diagnoses.Select(item => item.RuleId));
        Assert.All(diagnoses, diagnosis => Assert.StartsWith("diagnosis-", diagnosis.DiagnosisId, StringComparison.Ordinal));
        Assert.All(diagnoses, diagnosis => Assert.NotEmpty(diagnosis.EvidenceIds));
        Assert.All(diagnoses, diagnosis => Assert.Empty(diagnosis.AllowedRepairActions));
        Assert.All(diagnoses, diagnosis => Assert.False(string.IsNullOrWhiteSpace(diagnosis.SuggestedNextStep)));
    }

    [Fact]
    public void Engine_is_deterministic_for_the_same_evidence()
    {
        var evidence = EvidenceBuilder.Evidence(probes: new[]
        {
            EvidenceBuilder.Probe(ProbePath.ExplicitHttpProxy, EventStatus.Timeout),
        });

        var first = DiagnosticEngine.CreateDefault().Evaluate(evidence);
        var second = DiagnosticEngine.CreateDefault().Evaluate(evidence);

        Assert.Equal(
            first.Select(item => $"{item.DiagnosisId}:{item.RuleId}:{item.Level}"),
            second.Select(item => $"{item.DiagnosisId}:{item.RuleId}:{item.Level}"));
    }

    [Fact]
    public void A_failing_rule_is_reported_instead_of_aborting_the_run()
    {
        var engine = new DiagnosticEngine(new IDiagnosticRule[]
        {
            new Dx004Stub(),
            new ThrowingRule(),
            new TrailingRule(),
        });

        var outcome = engine.EvaluateDetailed(EvidenceBuilder.Evidence());

        var failure = Assert.Single(outcome.Failures);
        Assert.Equal("DX-THROW", failure.RuleId);
        Assert.Equal("InvalidOperationException", failure.ErrorType);
        Assert.Contains("执行失败", failure.Summary, StringComparison.Ordinal);
        Assert.Equal("DX-ZZZ", Assert.Single(outcome.Diagnoses).RuleId);

        // Legacy callers cannot silently overlook rule failures; successful results remain available.
        var exception = Assert.Throws<DiagnosticEvaluationException>(() => engine.Evaluate(EvidenceBuilder.Evidence()));
        Assert.Single(exception.Outcome.Failures);
        Assert.Equal("DX-ZZZ", Assert.Single(exception.Outcome.Diagnoses).RuleId);
    }

    [Fact]
    public void Empty_evidence_produces_no_diagnosis()
    {
        Assert.Empty(DiagnosticEngine.CreateDefault().Evaluate(EvidenceSet.Empty));
    }

    [Fact]
    public void Catalog_contains_exactly_the_rules_promised_for_m0()
    {
        var ids = RuleCatalog.All.Select(rule => rule.RuleId).OrderBy(id => id, StringComparer.Ordinal).ToArray();

        Assert.Equal(
            new[] { "DX-001", "DX-002", "DX-005", "DX-006", "DX-008", "DX-010", "DX-011" },
            ids);
    }

    private sealed class ThrowingRule : IDiagnosticRule
    {
        public string RuleId => "DX-THROW";

        public string Version => "1.0.0";

        public Diagnosis? Evaluate(EvidenceSet evidence) => throw new InvalidOperationException("boom");
    }

    private sealed class Dx004Stub : IDiagnosticRule
    {
        public string RuleId => "DX-004";

        public string Version => "1.0.0";

        public Diagnosis? Evaluate(EvidenceSet evidence) => null;
    }

    private sealed class TrailingRule : IDiagnosticRule
    {
        public string RuleId => "DX-ZZZ";

        public string Version => "1.0.0";

        public Diagnosis? Evaluate(EvidenceSet evidence) => new()
        {
            DiagnosisId = "pending",
            RuleId = RuleId,
            RuleVersion = Version,
            Level = EvidenceGrade.Observed,
            Summary = "trailing rule still ran",
            EvidenceIds = new[] { "trailing-evidence" },
            SuggestedNextStep = "none",
            Scope = new DiagnosisScope(),
        };
    }
}
