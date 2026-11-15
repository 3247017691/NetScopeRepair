using NetScope.Application;
using NetScope.Desktop.ViewModels;
using NetScope.Domain;

namespace NetScope.Desktop.Tests;

/// <summary>
/// T12 page view-model behavior with a fake replay result: recorded and recomputed diagnoses stay
/// visibly separate, admission/compatibility text is shown, and no write or network action exists.
/// </summary>
public sealed class T12CaseReplayViewModelTests
{
    [Fact]
    public void Applying_an_ok_result_binds_admission_recorded_recomputed_differences_and_incident()
    {
        var viewModel = new CaseReplayViewModel(new FixedCaseReplayService(Result(CaseReplayStatus.Ok)));

        viewModel.Apply(Result(CaseReplayStatus.Ok));

        Assert.True(viewModel.HasResult);
        Assert.Contains("已准入并完成重算", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Contains("准入：通过", viewModel.AdmissionText, StringComparison.Ordinal);
        Assert.Contains("typed evidence=是", viewModel.PackageText, StringComparison.Ordinal);
        Assert.Contains("本次已重算", viewModel.RuleSetText, StringComparison.Ordinal);
        Assert.Contains("确定性指纹", viewModel.FingerprintText, StringComparison.Ordinal);
        Assert.Single(viewModel.RecordedDiagnoses);
        Assert.Single(viewModel.RecomputedDiagnoses);
        Assert.Single(viewModel.Differences);
        Assert.Single(viewModel.SnapshotScopes);
        Assert.Contains("#1", viewModel.SnapshotScopes[0].Sequence, StringComparison.Ordinal);
        Assert.Single(viewModel.Incidents);
        Assert.Contains("跨周期=是", viewModel.Incidents[0].Scopes, StringComparison.Ordinal);
        Assert.NotEmpty(viewModel.Issues);
        Assert.NotEmpty(viewModel.Notices);
    }

    [Fact]
    public async Task Browse_and_replay_use_the_injected_package_path_and_fake_service()
    {
        var fake = new FixedCaseReplayService(Result(CaseReplayStatus.Ok));
        var viewModel = new CaseReplayViewModel(fake, () => @"C:\cases\synthetic.zip");

        await viewModel.BrowseAsync();
        Assert.Equal(@"C:\cases\synthetic.zip", viewModel.PackagePath);
        Assert.True(viewModel.ReplayCommand.CanExecute(null));

        await viewModel.ReplayAsync();

        Assert.Equal(@"C:\cases\synthetic.zip", fake.LastPath);
        Assert.True(viewModel.HasResult);
        Assert.Contains("回放完成", viewModel.StatusMessage ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void Rejected_result_is_shown_as_rejection_without_analysis()
    {
        var viewModel = new CaseReplayViewModel(new FixedCaseReplayService(Result(CaseReplayStatus.Rejected)));

        viewModel.Apply(Result(CaseReplayStatus.Rejected));

        Assert.Contains("拒绝", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Contains("准入：拒绝", viewModel.AdmissionText, StringComparison.Ordinal);
        Assert.Contains("本次未重算", viewModel.RuleSetText, StringComparison.Ordinal);
        Assert.Empty(viewModel.RecomputedDiagnoses);
        Assert.Empty(viewModel.Differences);
    }

    [Fact]
    public void Default_construction_is_local_and_makes_no_read_until_replay()
    {
        var viewModel = new CaseReplayViewModel();
        Assert.False(viewModel.HasResult);
        Assert.False(viewModel.ReplayCommand.CanExecute(null));
        Assert.Contains("不可信输入", viewModel.Notice, StringComparison.Ordinal);
    }

    private static CaseReplayResult Result(CaseReplayStatus status)
    {
        var at = DateTimeOffset.Parse("2026-11-05T01:00:00Z");
        var recorded = new Diagnosis
        {
            DiagnosisId = "diagnosis-recorded-1",
            RuleId = "DX-001",
            RuleVersion = "1.1.0",
            Level = EvidenceGrade.Supported,
            Summary = "recorded summary",
            EvidenceIds = new[] { "probe-1" },
            SuggestedNextStep = "recorded next step",
            Scope = new DiagnosisScope(),
        };
        var recomputed = new Diagnosis
        {
            DiagnosisId = "diagnosis-recomputed-1",
            RuleId = "DX-001",
            RuleVersion = "1.1.0",
            Level = EvidenceGrade.Candidate,
            Summary = "recomputed summary",
            EvidenceIds = new[] { "probe-1" },
            SuggestedNextStep = "recomputed next step",
            Scope = new DiagnosisScope(),
        };
        return new CaseReplayResult
        {
            Status = status,
            Admission = new CaseAdmissionSummary
            {
                Accepted = status != CaseReplayStatus.Rejected,
                EntryCount = 3,
                TotalUncompressedBytes = 4096,
                MaxObservedPathDepth = 1,
            },
            RuleSet = new CaseRuleSetInfo
            {
                RuleSetVersion = "diagnostics/test",
                AppVersion = "test",
                Rules = new[] { new CaseRuleVersion("DX-001", "1.1.0") },
            },
            Package = status == CaseReplayStatus.Rejected
                ? null
                : new CasePackageInfo
                {
                    PackageFormat = "netscope-incident-package",
                    PackageFormatVersion = "1",
                    EventCount = 12,
                    SnapshotCount = 1,
                    RecordedDiagnosisCount = 1,
                    HasTypedEvidence = true,
                    HasProbeResults = true,
                    HasClientEvidence = true,
                },
            RecordedDiagnoses = status == CaseReplayStatus.Rejected
                ? Array.Empty<CaseRecordedDiagnosis>()
                : new[]
                {
                    new CaseRecordedDiagnosis
                    {
                        ScopeSnapshotEventId = "snapshot-event-1",
                        Diagnosis = recorded,
                        RuleSupported = true,
                        VersionMatchesCurrent = true,
                        CurrentRuleVersion = "1.1.0",
                    },
                },
            RecomputedDiagnoses = status == CaseReplayStatus.Ok
                ? new[] { new CaseRecomputedDiagnosis { ScopeSnapshotEventId = "snapshot-event-1", Diagnosis = recomputed } }
                : Array.Empty<CaseRecomputedDiagnosis>(),
            Differences = status == CaseReplayStatus.Ok
                ? new[]
                {
                    new CaseDiagnosisDifference
                    {
                        RuleId = "DX-001",
                        ScopeSnapshotEventId = "snapshot-event-1",
                        Disposition = CaseDiagnosisDisposition.LevelChanged,
                        RecordedLevel = EvidenceGrade.Supported,
                        RecomputedLevel = EvidenceGrade.Candidate,
                        RecordedEvidenceIds = new[] { "probe-1" },
                        RecomputedEvidenceIds = new[] { "probe-1" },
                        Notes = new[] { "level differs" },
                    },
                }
                : Array.Empty<CaseDiagnosisDifference>(),
            SnapshotScopes = status == CaseReplayStatus.Rejected
                ? Array.Empty<CaseSnapshotReplay>()
                : new[]
                {
                    new CaseSnapshotReplay
                    {
                        SnapshotEventId = "snapshot-event-1",
                        Sequence = 1,
                        SnapshotId = "snapshot-1",
                        RecordCount = 2,
                        ProbeCount = 3,
                        RecordedDiagnosisCount = 1,
                        RecomputedDiagnosisCount = 1,
                        Interfaces = new[] { new CaseInterfaceRef { InterfaceGuid = "guid-1", IfIndex = 12 } },
                    },
                },
            Incidents = status == CaseReplayStatus.Rejected
                ? Array.Empty<CaseIncidentReplay>()
                : new[]
                {
                    new CaseIncidentReplay
                    {
                        IncidentId = "incident-1",
                        Trigger = "consecutive_failures",
                        States = new[] { "incident_open", "closed" },
                        StartedAt = at,
                        ClosedAt = at.AddSeconds(30),
                        PreWindowAvailable = true,
                        PostWindowComplete = true,
                        SnapshotScopeEventIds = new[] { "snapshot-event-1", "snapshot-event-2" },
                        SpansMultipleSnapshotCycles = true,
                    },
                },
            Issues = status == CaseReplayStatus.Rejected
                ? new[]
                {
                    new CaseReplayIssue
                    {
                        Code = "checksum_mismatch",
                        Severity = CaseReplayIssueSeverity.Blocking,
                        Message = "checksum failed",
                    },
                }
                : new[]
                {
                    new CaseReplayIssue
                    {
                        Code = "recorded_rule_version_mismatch",
                        Severity = CaseReplayIssueSeverity.Warning,
                        Message = "version note",
                    },
                },
            Notices = new[] { "recomputed_with_fixed_rule_set: test" },
            DeterministicFingerprint = status == CaseReplayStatus.Ok ? "fingerprint-ok" : "fingerprint-rejected",
        };
    }

    private sealed class FixedCaseReplayService(CaseReplayResult result) : ICaseReplayService
    {
        public string? LastPath { get; private set; }

        public CaseReplayResult Replay(string packagePath)
        {
            LastPath = packagePath;
            return result;
        }
    }
}
