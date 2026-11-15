namespace NetScope.Storage;

/// <summary>Documented quota thresholds. Percentages are of one pool's own hard limit.</summary>
public enum QuotaLevel
{
    Normal,
    Warning,
    ReducedDetail,
    StopOptionalDetail,
}

/// <summary>
/// What the storage layer may still record at the current quota level. Producers (session,
/// collector) can read this instead of silently sampling or silently dropping failures.
/// </summary>
public sealed record RetentionDirective(
    bool AllowRawSnapshotDetail,
    bool AllowOptionalDetail,
    bool KeepMinimalCountsOnly,
    int SamplingIntervalMultiplier,
    string Description);

/// <summary>
/// The storage defaults from design 04 §4: ordinary logs 7 days / 500 MB, pinned incidents a
/// separate 1 GB, 100 MB/day as a tuning target only. Percent thresholds are 80/90/100.
/// </summary>
public sealed record RetentionPolicy
{
    public const long Megabyte = 1024L * 1024;

    public static RetentionPolicy Default { get; } = new();

    public TimeSpan NormalRetention { get; init; } = TimeSpan.FromDays(7);

    public long NormalMaxBytes { get; init; } = 500 * Megabyte;

    /// <summary>Pinned incidents have their own cap; reaching it rejects new pins, it never unpins.</summary>
    public long PinnedMaxBytes { get; init; } = 1024 * Megabyte;

    /// <summary>Daily volume is a warning target, not an independent delete threshold.</summary>
    public long DailyBudgetBytes { get; init; } = 100 * Megabyte;

    public int WarningPercent { get; init; } = 80;

    public int ReducedDetailPercent { get; init; } = 90;

    public int StopOptionalDetailPercent { get; init; } = 100;

    /// <summary>
    /// Exported packages are handled by explicit export/delete, not by normal log retention
    /// (design 04 §4). Tests may opt in to including them in the normal pool.
    /// </summary>
    public bool IncludeExportsInNormalQuota { get; init; }

    public void Validate()
    {
        if (NormalRetention <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(NormalRetention), "Normal retention must be positive.");
        if (NormalMaxBytes <= 0 || PinnedMaxBytes <= 0 || DailyBudgetBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(NormalMaxBytes), "Quota limits must be positive.");
        if (WarningPercent is < 1 or > 99 || ReducedDetailPercent is < 2 or > 100
            || StopOptionalDetailPercent is < 3 or > 200
            || WarningPercent >= ReducedDetailPercent || ReducedDetailPercent >= StopOptionalDetailPercent)
        {
            throw new ArgumentOutOfRangeException(nameof(WarningPercent),
                "Quota percent thresholds must be increasing, between 1 and 100 (warning < reduced < stop).");
        }
    }

    /// <summary>80% warning, 90% reduced detail, 100% stop optional detail. One percent never creates extra levels.</summary>
    public QuotaLevel LevelFor(long bytes, long limit)
    {
        if (limit <= 0) return QuotaLevel.StopOptionalDetail;
        var percent = bytes * 100.0 / limit;
        if (percent >= StopOptionalDetailPercent) return QuotaLevel.StopOptionalDetail;
        if (percent >= ReducedDetailPercent) return QuotaLevel.ReducedDetail;
        if (percent >= WarningPercent) return QuotaLevel.Warning;
        return QuotaLevel.Normal;
    }

    public RetentionDirective DirectiveFor(QuotaLevel level) => level switch
    {
        QuotaLevel.Normal => new RetentionDirective(true, true, false, 1,
            "Normal storage: full detail may be recorded."),
        QuotaLevel.Warning => new RetentionDirective(true, true, false, 1,
            "Storage is at or above 80%: show the quota warning. Recording behaviour is unchanged."),
        QuotaLevel.ReducedDetail => new RetentionDirective(false, true, false, 2,
            "Storage is at or above 90%: stop redundant raw snapshot detail and reduce sampling frequency."),
        _ => new RetentionDirective(false, false, true, 4,
            "Storage is at or above 100%: stop optional detail and keep only the minimal writable index/counts."),
    };

    /// <summary>Retention cutoff; a clock rollback must never move the cutoff backwards into fresh data.</summary>
    public DateTimeOffset RetentionCutoff(DateTimeOffset nowUtc) => nowUtc - NormalRetention;

    public RetentionEvaluation Evaluate(long normalBytes, long pinnedBytes, long dailyBytes,
        DateTimeOffset nowUtc, bool clockRollbackSuspected = false)
    {
        if (normalBytes < 0) throw new ArgumentOutOfRangeException(nameof(normalBytes));
        if (pinnedBytes < 0) throw new ArgumentOutOfRangeException(nameof(pinnedBytes));
        if (dailyBytes < 0) throw new ArgumentOutOfRangeException(nameof(dailyBytes));

        var normalLevel = LevelFor(normalBytes, NormalMaxBytes);
        var pinnedLevel = LevelFor(pinnedBytes, PinnedMaxBytes);
        var level = (QuotaLevel)Math.Max((int)normalLevel, (int)pinnedLevel);
        var warnings = new List<string>();
        if (normalLevel >= QuotaLevel.Warning)
            warnings.Add($"Ordinary log storage is at {Percent(normalBytes, NormalMaxBytes):0.0}% of {NormalMaxBytes} bytes.");
        if (pinnedLevel >= QuotaLevel.Warning)
            warnings.Add($"Pinned incident storage is at {Percent(pinnedBytes, PinnedMaxBytes):0.0}% of {PinnedMaxBytes} bytes.");
        if (dailyBytes > DailyBudgetBytes)
            warnings.Add($"Daily write volume {dailyBytes} bytes exceeds the {DailyBudgetBytes}-byte tuning target. This is a performance warning and never drops data by itself.");
        if (clockRollbackSuspected)
            warnings.Add("Wall clock appears to have moved backwards relative to stored files. Retention ages were clamped to zero; no data is deleted because of the rollback.");

        return new RetentionEvaluation
        {
            EvaluatedAt = nowUtc,
            NormalBytes = normalBytes,
            NormalLimit = NormalMaxBytes,
            NormalPercent = Percent(normalBytes, NormalMaxBytes),
            PinnedBytes = pinnedBytes,
            PinnedLimit = PinnedMaxBytes,
            PinnedPercent = Percent(pinnedBytes, PinnedMaxBytes),
            DailyBytes = dailyBytes,
            DailyLimit = DailyBudgetBytes,
            DailyBudgetExceeded = dailyBytes > DailyBudgetBytes,
            Level = level,
            Directive = DirectiveFor(level),
            OverNormalLimit = normalBytes >= NormalMaxBytes,
            OverPinnedLimit = pinnedBytes >= PinnedMaxBytes,
            ClockRollbackSuspected = clockRollbackSuspected,
            RetentionCutoff = RetentionCutoff(nowUtc),
            Warnings = warnings,
        };
    }

    private static double Percent(long bytes, long limit) => limit <= 0 ? 100 : bytes * 100.0 / limit;
}

/// <summary>Result of evaluating the current usage against the documented thresholds.</summary>
public sealed record RetentionEvaluation
{
    public required DateTimeOffset EvaluatedAt { get; init; }
    public required long NormalBytes { get; init; }
    public required long NormalLimit { get; init; }
    public required double NormalPercent { get; init; }
    public required long PinnedBytes { get; init; }
    public required long PinnedLimit { get; init; }
    public required double PinnedPercent { get; init; }
    public required long DailyBytes { get; init; }
    public required long DailyLimit { get; init; }
    public required bool DailyBudgetExceeded { get; init; }
    public required QuotaLevel Level { get; init; }
    public required RetentionDirective Directive { get; init; }
    public required bool OverNormalLimit { get; init; }
    public required bool OverPinnedLimit { get; init; }
    public required bool ClockRollbackSuspected { get; init; }
    public required DateTimeOffset RetentionCutoff { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}
