namespace NetScope.Application;

public enum BusinessObservationTarget
{
    Dsh,
    Antigravity,
    ChromeDomestic,
    ChromeOverseas,
    TunApplication,
}

public enum BusinessObservationResult
{
    Passed,
    Failed,
    NotTested,
}

public sealed record BusinessTargetResult
{
    public required BusinessObservationTarget Target { get; init; }

    public required BusinessObservationResult Result { get; init; }

    public string Detail { get; init; } = "";
}

public sealed record BusinessTargetRow
{
    public required string TargetText { get; init; }

    public required string ResultText { get; init; }

    public required BusinessObservationResult Result { get; init; }

    public required string Detail { get; init; }
}

public sealed record BusinessRoundView
{
    public required int Index { get; init; }

    public required string AtText { get; init; }

    public required IReadOnlyList<BusinessTargetRow> Results { get; init; }
}

public enum BusinessObservationStatus
{
    NotStarted,
    InProgress,
    Incomplete,
    Passed,
    Failed,
}

public sealed record BusinessObservationView
{
    public required BusinessObservationStatus Status { get; init; }

    public required string StatusText { get; init; }

    public required string WindowText { get; init; }

    public required string RoundsText { get; init; }

    public required bool IsWindowComplete { get; init; }

    public required bool IsComplete { get; init; }

    public required IReadOnlyList<BusinessTargetRow> Targets { get; init; }

    public required IReadOnlyList<BusinessRoundView> Rounds { get; init; }

    public required string Notice { get; init; }

    public static BusinessObservationView NotStarted { get; } = new()
    {
        Status = BusinessObservationStatus.NotStarted,
        StatusText = "未开始",
        WindowText = "计划窗口 10 分钟 / 3 轮",
        RoundsText = "0 / 3 轮",
        IsWindowComplete = false,
        IsComplete = false,
        Targets = Array.Empty<BusinessTargetRow>(),
        Rounds = Array.Empty<BusinessRoundView>(),
        Notice = "业务复测由低权主进程在技术提交后执行；未测试不算通过，不自动付费调用。",
    };
}

/// <summary>
/// The post-commit business observation (10 minutes, 3 rounds) executed by the low-privilege main
/// process. It only records local results: DSH, Antigravity, Chrome domestic/overseas and a TUN
/// application are each pass/fail/not_tested. It never makes a paid call and never upgrades a
/// technical commit into a business recovery claim.
/// </summary>
public sealed class RepairBusinessObservation
{
    public static TimeSpan Window { get; } = TimeSpan.FromMinutes(10);

    public const int RequiredRounds = 3;

    private static readonly BusinessObservationTarget[] AllTargets = Enum.GetValues<BusinessObservationTarget>();

    private readonly List<(DateTimeOffset AtUtc, IReadOnlyDictionary<BusinessObservationTarget, BusinessTargetResult> Results)> _rounds = new();
    private DateTimeOffset? _startedAt;

    public bool IsStarted => _startedAt is not null;

    public int RoundCount => _rounds.Count;

    public DateTimeOffset? StartedAt => _startedAt;

    public void Start(DateTimeOffset now)
    {
        _startedAt = now;
        _rounds.Clear();
    }

    public void Reset()
    {
        _startedAt = null;
        _rounds.Clear();
    }

    /// <summary>
    /// Records one round. A result outside the 10-minute window or after the third round is
    /// rejected instead of silently extending the observation. Missing targets become NotTested.
    /// </summary>
    public bool TryRecordRound(
        IEnumerable<BusinessTargetResult> results,
        DateTimeOffset now,
        out string rejection)
    {
        rejection = "";
        if (_startedAt is null)
        {
            rejection = "业务观察尚未开始。";
            return false;
        }

        if (now < _startedAt)
        {
            rejection = "轮次时间早于观察开始时间。";
            return false;
        }

        if (now > _startedAt.Value + Window)
        {
            rejection = "轮次超出 10 分钟观察窗口，未满窗口不能补记。";
            return false;
        }

        if (_rounds.Count >= RequiredRounds)
        {
            rejection = "已完成 3 轮，额外轮次不再计入本次观察。";
            return false;
        }

        var supplied = results.GroupBy(result => result.Target).ToDictionary(group => group.Key, group => group.First());
        var normalized = new Dictionary<BusinessObservationTarget, BusinessTargetResult>();
        foreach (var target in AllTargets)
        {
            normalized[target] = supplied.TryGetValue(target, out var result)
                ? result with { Target = target }
                : new BusinessTargetResult
                {
                    Target = target,
                    Result = BusinessObservationResult.NotTested,
                    Detail = "本轮未测试；未测试不算通过。",
                };
        }

        _rounds.Add((now, normalized));
        return true;
    }

    public BusinessObservationView Evaluate(DateTimeOffset now)
    {
        if (_startedAt is not { } startedAt) return BusinessObservationView.NotStarted;

        var windowComplete = now >= startedAt + Window;
        var rounds = _rounds
            .Select((round, index) => new BusinessRoundView
            {
                Index = index + 1,
                AtText = round.AtUtc.ToLocalTime().ToString("HH:mm:ss"),
                Results = AllTargets.Select(target => ToRow(round.Results[target])).ToArray(),
            })
            .ToArray();

        var anyFailed = _rounds.Any(round => round.Results.Values.Any(result =>
            result.Result == BusinessObservationResult.Failed));
        var anyNotTested = _rounds.Any(round => round.Results.Values.Any(result =>
            result.Result == BusinessObservationResult.NotTested));
        var allPassed = _rounds.Count >= RequiredRounds
            && _rounds.All(round => round.Results.Values.All(result =>
                result.Result == BusinessObservationResult.Passed));

        var status = anyFailed
            ? BusinessObservationStatus.Failed
            : allPassed && windowComplete
                ? BusinessObservationStatus.Passed
                : _rounds.Count < RequiredRounds || !windowComplete
                    ? BusinessObservationStatus.InProgress
                    : BusinessObservationStatus.Incomplete;

        var targets = AllTargets
            .Select(target => SummarizeTarget(target, rounds))
            .ToArray();

        return new BusinessObservationView
        {
            Status = status,
            StatusText = status switch
            {
                BusinessObservationStatus.Passed => "业务观察通过（3 轮 / 10 分钟窗口内）",
                BusinessObservationStatus.Failed => "业务观察失败：至少一轮有目标失败，计入失败统计",
                BusinessObservationStatus.InProgress => "业务观察进行中：未满 3 轮或未满 10 分钟窗口，不宣称稳定",
                _ => "业务观察未完成：存在未测试目标，未测试不算通过",
            },
            WindowText = "窗口 " + startedAt.ToLocalTime().ToString("HH:mm:ss") + " → "
                + (startedAt + Window).ToLocalTime().ToString("HH:mm:ss") + "（10 分钟）",
            RoundsText = _rounds.Count + " / " + RequiredRounds + " 轮"
                + (windowComplete ? "，窗口已满" : "，窗口未满"),
            IsWindowComplete = windowComplete,
            IsComplete = status is BusinessObservationStatus.Passed or BusinessObservationStatus.Failed,
            Targets = targets,
            Rounds = rounds,
            Notice = "业务复测由低权主进程执行；技术 committed 不证明业务恢复，未测试不算通过，不自动付费调用。"
                + (anyNotTested ? " 当前存在未测试目标。" : ""),
        };
    }

    private static BusinessTargetRow SummarizeTarget(
        BusinessObservationTarget target, IReadOnlyList<BusinessRoundView> rounds)
    {
        var results = rounds
            .Select(round => round.Results.First(row => row.TargetText == TargetText(target)).Result)
            .ToArray();
        var result = results.Length == 0
            ? BusinessObservationResult.NotTested
            : results.Contains(BusinessObservationResult.Failed)
                ? BusinessObservationResult.Failed
                : results.All(value => value == BusinessObservationResult.Passed)
                    ? BusinessObservationResult.Passed
                    : BusinessObservationResult.NotTested;
        return new BusinessTargetRow
        {
            TargetText = TargetText(target),
            Result = result,
            ResultText = ResultText(result),
            Detail = result == BusinessObservationResult.NotTested
                ? "未测试/未完成；不算通过。"
                : result == BusinessObservationResult.Failed
                    ? "至少一轮失败，计入失败统计。"
                    : "全部已完成轮次通过。",
        };
    }

    private static BusinessTargetRow ToRow(BusinessTargetResult result) => new()
    {
        TargetText = TargetText(result.Target),
        Result = result.Result,
        ResultText = ResultText(result.Result),
        Detail = result.Detail,
    };

    public static string TargetText(BusinessObservationTarget target) => target switch
    {
        BusinessObservationTarget.Dsh => "DSH",
        BusinessObservationTarget.Antigravity => "Antigravity",
        BusinessObservationTarget.ChromeDomestic => "Chrome（国内站点）",
        BusinessObservationTarget.ChromeOverseas => "Chrome（国外站点）",
        BusinessObservationTarget.TunApplication => "TUN 应用",
        _ => target.ToString(),
    };

    public static string ResultText(BusinessObservationResult result) => result switch
    {
        BusinessObservationResult.Passed => "通过",
        BusinessObservationResult.Failed => "失败",
        _ => "未测试",
    };
}

/// <summary>QA-036: every attempt enters the denominator, including cancelled/denied/unfinished ones.</summary>
public enum RepairAttemptOutcome
{
    Committed,
    NoOp,
    Aborted,
    RolledBack,
    RollbackConflict,
    RollbackFailed,
    RecoveryRequired,
    Cancelled,
    Denied,
    Disconnected,
    IncompleteObservation,
}

public sealed class RepairFunnelCounter
{
    private readonly Dictionary<RepairAttemptOutcome, int> _counts = new();

    public void Record(RepairAttemptOutcome outcome) =>
        _counts[outcome] = _counts.GetValueOrDefault(outcome) + 1;

    public int Total => _counts.Values.Sum();

    public int CountOf(RepairAttemptOutcome outcome) => _counts.GetValueOrDefault(outcome);

    public IReadOnlyDictionary<RepairAttemptOutcome, int> Counts => _counts;

    /// <summary>Committed/no_op only count as completed; cancelled and denied stay in the denominator.</summary>
    public string DenominatorText => Total == 0
        ? "尚无修复尝试。"
        : "尝试 " + Total + " 次（分母含取消/拒绝/断连/观察未完成）："
          + string.Join("，", _counts.OrderBy(pair => pair.Key)
              .Select(pair => Label(pair.Key) + " " + pair.Value));

    public static string Label(RepairAttemptOutcome outcome) => outcome switch
    {
        RepairAttemptOutcome.Committed => "已提交",
        RepairAttemptOutcome.NoOp => "空操作",
        RepairAttemptOutcome.Aborted => "写前中止",
        RepairAttemptOutcome.RolledBack => "已回滚",
        RepairAttemptOutcome.RollbackConflict => "回滚冲突",
        RepairAttemptOutcome.RollbackFailed => "回滚失败",
        RepairAttemptOutcome.RecoveryRequired => "需恢复",
        RepairAttemptOutcome.Cancelled => "已取消",
        RepairAttemptOutcome.Denied => "拒绝/未授权",
        RepairAttemptOutcome.Disconnected => "断连",
        RepairAttemptOutcome.IncompleteObservation => "观察未完成",
        _ => outcome.ToString(),
    };
}
