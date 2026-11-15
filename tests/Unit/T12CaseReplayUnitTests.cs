using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Unit.Tests;

/// <summary>
/// T12 Application-level semantics: a replay result only claims recomputation for supported
/// packages, and recorded facts remain separate from the recomputed list.
/// </summary>
public sealed class T12CaseReplayUnitTests
{
    [Theory]
    [InlineData(CaseReplayStatus.Ok, true)]
    [InlineData(CaseReplayStatus.Unsupported, false)]
    [InlineData(CaseReplayStatus.Rejected, false)]
    public void Recomputed_is_claimed_only_for_an_ok_result(CaseReplayStatus status, bool expected)
    {
        var result = Result(status);
        Assert.Equal(expected, result.Recomputed);
    }

    [Fact]
    public void Rejected_result_can_hold_no_recomputed_or_recorded_diagnoses()
    {
        var result = Result(CaseReplayStatus.Rejected) with
        {
            RecordedDiagnoses = Array.Empty<CaseRecordedDiagnosis>(),
            RecomputedDiagnoses = Array.Empty<CaseRecomputedDiagnosis>(),
            Differences = Array.Empty<CaseDiagnosisDifference>(),
            SnapshotScopes = Array.Empty<CaseSnapshotReplay>(),
            Incidents = Array.Empty<CaseIncidentReplay>(),
        };

        Assert.False(result.Recomputed);
        Assert.Empty(result.RecordedDiagnoses);
        Assert.Empty(result.RecomputedDiagnoses);
        Assert.Equal(CaseReplayStatus.Rejected, result.Status);
        Assert.Equal("fingerprint", result.DeterministicFingerprint);
    }

    [Fact]
    public void Unsupported_disposition_exists_for_unmatched_recorded_rule_versions()
    {
        var difference = new CaseDiagnosisDifference
        {
            RuleId = "DX-001",
            Disposition = CaseDiagnosisDisposition.Unsupported,
            RecordedRuleVersion = "0.0.0-legacy",
            RecomputedRuleVersion = "1.1.0",
            Notes = new[] { "not compared as current" },
        };

        Assert.Equal(CaseDiagnosisDisposition.Unsupported, difference.Disposition);
        Assert.NotEqual(difference.RecordedRuleVersion, difference.RecomputedRuleVersion);
    }

    private static CaseReplayResult Result(CaseReplayStatus status) => new()
    {
        Status = status,
        Admission = new CaseAdmissionSummary
        {
            Accepted = status != CaseReplayStatus.Rejected,
            EntryCount = 3,
            TotalUncompressedBytes = 1024,
            MaxObservedPathDepth = 1,
        },
        RuleSet = new CaseRuleSetInfo
        {
            RuleSetVersion = "diagnostics/test",
            AppVersion = "test",
            Rules = new[] { new CaseRuleVersion("DX-001", "1.1.0") },
        },
        DeterministicFingerprint = "fingerprint",
    };
}
