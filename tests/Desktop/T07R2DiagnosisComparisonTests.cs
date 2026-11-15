using NetScope.Application;
using NetScope.Desktop.Services;
using NetScope.Desktop.ViewModels;
using NetScope.Domain;

namespace NetScope.Desktop.Tests;

/// <summary>
/// T07-R2 diagnosis detail: three paths are compared only when the recorded target/method/family/
/// window really match; Partial/Unsupported reasons stay visible and expandable; IPv4/IPv6
/// capabilities use the T04-R2 gap codes instead of claiming a success.
/// </summary>
public sealed class T07R2DiagnosisComparisonTests
{
    [Fact]
    public void Three_paths_with_the_same_options_family_and_window_are_marked_comparable()
    {
        var probeSet = new[]
        {
            T07R2Data.Probe(ProbePath.SystemDefault, EventStatus.Ok, startedMonotonicMs: 1000),
            T07R2Data.Probe(ProbePath.NoExplicitProxy, EventStatus.Ok, startedMonotonicMs: 1020),
            T07R2Data.Probe(ProbePath.ExplicitHttpProxy, EventStatus.Ok, startedMonotonicMs: 1040),
        };
        var diagnosis = T07R2Data.Diagnosis("diagnosis-compare", probeSet.Select(probe => probe.ProbeId).ToArray());
        var viewModel = Apply(probeSet, Array.Empty<EvidenceRecord>(), diagnosis, T07R2Data.Snapshot(
            "snapshot-1", T07R2Data.BaseTime, 1000));

        Assert.Equal(3, viewModel.PathRows.Count);
        Assert.Equal("三路径可比较", viewModel.ComparisonVerdictText);
        Assert.Contains("RequestOptionsKey", viewModel.ComparisonDetailText);
        Assert.Contains("方法 GET", viewModel.ComparisonDetailText);
        Assert.Contains("地址族 ipv4", viewModel.ComparisonDetailText);
        Assert.Contains(viewModel.FamilyRows, row => row.PathText.Contains("无显式代理", StringComparison.Ordinal)
            && row.CapabilityText.Contains("已强制 ipv4", StringComparison.Ordinal));
        Assert.Contains("DSH", viewModel.ProbeBoundaryText);
        Assert.Contains("Antigravity", viewModel.ProbeBoundaryText);
        Assert.Contains("Chrome", viewModel.ProbeBoundaryText);
        Assert.Contains("不是", viewModel.ProbeBoundaryText);
    }

    [Fact]
    public void Missing_address_family_makes_the_group_not_comparable()
    {
        var probeSet = new[]
        {
            T07R2Data.Probe(ProbePath.SystemDefault, EventStatus.Ok, family: null, timingComparable: false,
                startedMonotonicMs: 1000),
            T07R2Data.Probe(ProbePath.NoExplicitProxy, EventStatus.Ok, family: null, timingComparable: false,
                startedMonotonicMs: 1020),
            T07R2Data.Probe(ProbePath.ExplicitHttpProxy, EventStatus.Ok, family: null, timingComparable: false,
                startedMonotonicMs: 1040),
        };
        var diagnosis = T07R2Data.Diagnosis("diagnosis-family", probeSet.Select(probe => probe.ProbeId).ToArray());
        var viewModel = Apply(probeSet, Array.Empty<EvidenceRecord>(), diagnosis, T07R2Data.Snapshot(
            "snapshot-1", T07R2Data.BaseTime, 1000));

        Assert.Equal("三路径不可比", viewModel.ComparisonVerdictText);
        Assert.Contains("地址族未指定", viewModel.ComparisonDetailText);
        Assert.Contains(viewModel.FamilyRows, row => row.CapabilityText.Contains("未指定地址族", StringComparison.Ordinal));
    }

    [Fact]
    public void T04_family_gap_codes_are_shown_as_unsupported_or_unknown_never_as_success()
    {
        var probeSet = new[]
        {
            T07R2Data.Probe(ProbePath.SystemDefault, EventStatus.Unsupported, timingComparable: false,
                transportError: ProbeComparisonExplainer.FamilyGapSystemDefault,
                notice: "No request was sent. Reason: " + ProbeComparisonExplainer.FamilyGapSystemDefault,
                startedMonotonicMs: 1000),
            T07R2Data.Probe(ProbePath.NoExplicitProxy, EventStatus.Ok, startedMonotonicMs: 1020),
            T07R2Data.Probe(ProbePath.ExplicitHttpProxy, EventStatus.Unsupported, timingComparable: false,
                transportError: ProbeComparisonExplainer.FamilyGapThroughProxy,
                notice: "No request was sent. Reason: " + ProbeComparisonExplainer.FamilyGapThroughProxy,
                startedMonotonicMs: 1040),
        };
        var diagnosis = T07R2Data.Diagnosis("diagnosis-gap", probeSet.Select(probe => probe.ProbeId).ToArray());
        var viewModel = Apply(probeSet, Array.Empty<EvidenceRecord>(), diagnosis, T07R2Data.Snapshot(
            "snapshot-1", T07R2Data.BaseTime, 1000));

        Assert.Equal("三路径不可比", viewModel.ComparisonVerdictText);
        Assert.Contains("存在不可比状态", viewModel.ComparisonDetailText);
        Assert.Contains(viewModel.FamilyRows, row => row.CapabilityText.Contains(
            ProbeComparisonExplainer.FamilyGapSystemDefault, StringComparison.Ordinal)
            && row.CapabilityText.Contains("未知", StringComparison.Ordinal));
        Assert.Contains(viewModel.FamilyRows, row => row.CapabilityText.Contains(
            ProbeComparisonExplainer.FamilyGapThroughProxy, StringComparison.Ordinal)
            && row.CapabilityText.Contains("Unsupported 缺口", StringComparison.Ordinal));
        Assert.Contains(viewModel.PathRows, row => row.StatusText == "不支持" && row.ClassificationText == "不确定");
        Assert.Contains(viewModel.Reasons, reason => reason.StatusText == "不支持");
    }

    [Fact]
    public void Unsupported_stage_and_unknown_attribution_are_rendered_without_inventing_a_stage()
    {
        var probeSet = new[]
        {
            T07R2Data.Probe(ProbePath.SystemDefault, EventStatus.Failed, failedStage: "resolve",
                transportError: "name_resolution_failed", startedMonotonicMs: 1000),
            T07R2Data.Probe(ProbePath.NoExplicitProxy, EventStatus.Ok, startedMonotonicMs: 1020),
            T07R2Data.Probe(ProbePath.ExplicitHttpProxy, EventStatus.Timeout, failedStage: null,
                transportError: "probe_timeout", startedMonotonicMs: 1040),
        };
        var diagnosis = T07R2Data.Diagnosis("diagnosis-stage", probeSet.Select(probe => probe.ProbeId).ToArray());
        var viewModel = Apply(probeSet, Array.Empty<EvidenceRecord>(), diagnosis, T07R2Data.Snapshot(
            "snapshot-1", T07R2Data.BaseTime, 1000));

        Assert.Contains(viewModel.PathRows, row => row.StageText.StartsWith("resolve", StringComparison.Ordinal));
        Assert.Contains(viewModel.PathRows, row => row.StageText == "未记录（不能归因）");
        var timeoutRow = Assert.Single(viewModel.PathRows, row => row.PathText.Contains("显式 HTTP 代理", StringComparison.Ordinal));
        Assert.Equal("unknown（无法确定）", timeoutRow.EndpointText);
    }

    [Fact]
    public void Partial_reasons_are_aggregated_counted_and_expandable()
    {
        var snapshot = T07R2Data.Snapshot("snapshot-1", T07R2Data.BaseTime, 1000,
            new[] { T07R2Data.Interface("guid-a") }, EventStatus.Partial,
            "2 interface observation(s) could not be fully read");
        var probe = T07R2Data.Probe(ProbePath.NoExplicitProxy, EventStatus.Timeout, failedStage: "connect",
            transportError: "probe_timeout", startedMonotonicMs: 1020);
        var records = new[]
        {
            PartialRecord("record-1"),
            PartialRecord("record-2"),
        };
        var diagnosis = T07R2Data.Diagnosis("diagnosis-reasons",
            new[] { snapshot.SnapshotId, probe.ProbeId, records[0].EvidenceId, records[1].EvidenceId });
        var viewModel = Apply(new[] { probe }, records, diagnosis, snapshot);

        Assert.True(viewModel.HasReasons);
        Assert.Equal(4, viewModel.Reasons.Sum(reason => reason.Count));
        Assert.Contains("（4 条）", viewModel.ReasonHeaderText);
        var aggregated = Assert.Single(viewModel.Reasons, reason => reason.Count == 2);
        Assert.Equal("×2", aggregated.CountText);
        Assert.Contains("client_process", aggregated.SourceText);

        Assert.True(viewModel.ReasonsVisible);
        Assert.True(viewModel.AreReasonsExpanded);
        viewModel.ToggleReasonsCommand.Execute(null);
        Assert.False(viewModel.AreReasonsExpanded);
        Assert.False(viewModel.ReasonsVisible);
        viewModel.ToggleReasonsCommand.Execute(null);
        Assert.True(viewModel.ReasonsVisible);
    }

    [Fact]
    public void A_diagnosis_without_probe_evidence_says_it_was_local_only()
    {
        var snapshot = T07R2Data.Snapshot("snapshot-1", T07R2Data.BaseTime, 1000,
            new[] { T07R2Data.Interface("guid-a") });
        var diagnosis = T07R2Data.Diagnosis("diagnosis-local", new[] { snapshot.SnapshotId });
        var viewModel = Apply(Array.Empty<ProbeResult>(), Array.Empty<EvidenceRecord>(), diagnosis, snapshot);

        Assert.False(viewModel.HasProbeComparison);
        Assert.Contains("没有引用外部探测结果", viewModel.ProbeEmptyText);
        Assert.Contains("未验证目标连通性", viewModel.ProbeEmptyText);
        Assert.True(viewModel.ShowReasonsEmpty);
        Assert.Contains("不代表采集完整", viewModel.ReasonsEmptyText);
    }

    [Fact]
    public void Two_probe_groups_name_only_the_latest_group_in_the_detail()
    {
        var first = new[]
        {
            T07R2Data.Probe(ProbePath.SystemDefault, EventStatus.Ok, group: "group-1", startedMonotonicMs: 1000),
            T07R2Data.Probe(ProbePath.NoExplicitProxy, EventStatus.Failed, group: "group-1", startedMonotonicMs: 1020),
            T07R2Data.Probe(ProbePath.ExplicitHttpProxy, EventStatus.Ok, group: "group-1", startedMonotonicMs: 1040),
        };
        var second = new[]
        {
            T07R2Data.Probe(ProbePath.SystemDefault, EventStatus.Ok, group: "group-2",
                startedAt: T07R2Data.BaseTime.AddSeconds(20), startedMonotonicMs: 21000),
            T07R2Data.Probe(ProbePath.NoExplicitProxy, EventStatus.Ok, group: "group-2",
                startedAt: T07R2Data.BaseTime.AddSeconds(20), startedMonotonicMs: 21020),
            T07R2Data.Probe(ProbePath.ExplicitHttpProxy, EventStatus.Ok, group: "group-2",
                startedAt: T07R2Data.BaseTime.AddSeconds(20), startedMonotonicMs: 21040),
        };
        var probes = first.Concat(second).ToArray();
        var diagnosis = T07R2Data.Diagnosis("diagnosis-groups", probes.Select(probe => probe.ProbeId).ToArray());
        var viewModel = Apply(probes, Array.Empty<EvidenceRecord>(), diagnosis,
            T07R2Data.Snapshot("snapshot-1", T07R2Data.BaseTime, 1000));

        Assert.Contains("2 组探测结果", viewModel.GroupNoteText);
        Assert.Contains("最近一组", viewModel.GroupNoteText);
        Assert.Contains("目标 target-lab", viewModel.ComparisonDetailText);
    }

    private static DiagnosesViewModel Apply(IReadOnlyList<ProbeResult> probes,
        IReadOnlyList<EvidenceRecord> records, Diagnosis diagnosis, NetworkSnapshot snapshot)
    {
        var viewModel = new DiagnosesViewModel();
        viewModel.Apply(new EvidenceSet { Snapshot = snapshot, Probes = probes, Records = records },
            new[] { diagnosis }, Array.Empty<RuleEvaluationFailure>(), T07R2Data.BaseTime);
        return viewModel;
    }

    private static EvidenceRecord PartialRecord(string id) => new()
    {
        EvidenceId = id,
        Kind = "client_process",
        Summary = "合成记录：进程字段部分可读。",
        Status = EventStatus.Partial,
        Source = "test.synthetic-evidence",
        TransformationVersion = "1",
        ObservedAt = T07R2Data.BaseTime,
        MonotonicMs = 1000,
        BootId = "boot-1",
        ObservationWindowId = "window-1",
    };
}
