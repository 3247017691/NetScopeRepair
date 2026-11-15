using NetScope.Repair;

namespace NetScope.Broker;

/// <summary>What the broker asks the transaction layer to do for one already-validated plan.</summary>
public sealed record BrokerTransactionExecutionRequest
{
    public required string TransactionId { get; init; }

    public required RepairPlan Plan { get; init; }

    public required RepairAuthorizationBinding Authorization { get; init; }

    public required string UserSid { get; init; }

    public required string LogonSessionId { get; init; }

    public required DateTimeOffset NowUtc { get; init; }
}

public sealed record BrokerTransactionExecutionOutcome
{
    /// <summary>True only when the transaction reached a successful terminal state.</summary>
    public required bool Executed { get; init; }

    /// <summary>True when any native write was attempted (the result may still be a rollback).</summary>
    public required bool WroteAnyField { get; init; }

    public required RepairTransactionState? State { get; init; }

    public required IReadOnlyList<BrokerRefusal> Refusals { get; init; }

    public string? Detail { get; init; }
}

/// <summary>
/// The broker's transaction boundary. Its default registration refuses execution: no verified
/// platform adapter and no trusted signed deployment exist in this delivery. A handler can only
/// reach the journal/coordinator when the deployment gate allows it.
/// </summary>
public interface IBrokerTransactionHandler
{
    Task<BrokerTransactionExecutionOutcome> ExecuteAsync(
        BrokerTransactionExecutionRequest request, CancellationToken cancellationToken);
}

/// <summary>Preserves the T14 behaviour: validated, but no privileged execution path.</summary>
public sealed class UnavailableBrokerTransactionHandler : IBrokerTransactionHandler
{
    public Task<BrokerTransactionExecutionOutcome> ExecuteAsync(
        BrokerTransactionExecutionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new BrokerTransactionExecutionOutcome
        {
            Executed = false,
            WroteAnyField = false,
            State = null,
            Refusals = new[]
            {
                BrokerRefusal.Of(
                    BrokerRefusalCode.ExecutionNotImplemented,
                    "This broker validates the transaction but has no privileged execution path; no write was attempted."),
            },
            Detail = "execution_not_implemented",
        });
}

/// <summary>
/// Wires the authoritative journal and the T13/T15 transaction coordinator behind the broker. It
/// refuses before creating a journal unless the trust decision and the fixed directory policy
/// both allow it; a corrupt journal is never auto-truncated here, and unresolved transactions must
/// be user-confirmed before another one starts.
/// </summary>
public sealed class RepairBrokerTransactionHandler : IBrokerTransactionHandler
{
    private readonly BrokerTrustDecision _trust;
    private readonly RepairJournalDirectoryPolicy _directoryPolicy;
    private readonly IRepairJournalPathProbe _pathProbe;
    private readonly Func<IRepairFieldAdapter> _adapterFactory;
    private readonly Func<IRepairObservationProbe> _observationFactory;
    private readonly PlatformCapabilityMatrix _capabilities;
    private readonly TimeProvider _clock;
    private readonly string _journalFileName;
    private readonly bool _developmentIsolation;

    public RepairBrokerTransactionHandler(
        BrokerTrustDecision trust,
        RepairJournalDirectoryPolicy directoryPolicy,
        IRepairJournalPathProbe pathProbe,
        Func<IRepairFieldAdapter> adapterFactory,
        Func<IRepairObservationProbe> observationFactory,
        PlatformCapabilityMatrix capabilities,
        TimeProvider? clock = null,
        string journalFileName = "transactions.journal",
        bool developmentIsolation = false)
    {
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        _directoryPolicy = directoryPolicy ?? throw new ArgumentNullException(nameof(directoryPolicy));
        _pathProbe = pathProbe ?? throw new ArgumentNullException(nameof(pathProbe));
        _adapterFactory = adapterFactory ?? throw new ArgumentNullException(nameof(adapterFactory));
        _observationFactory = observationFactory ?? throw new ArgumentNullException(nameof(observationFactory));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _clock = clock ?? TimeProvider.System;
        _journalFileName = journalFileName;
        _developmentIsolation = developmentIsolation;
    }

    public async Task<BrokerTransactionExecutionOutcome> ExecuteAsync(
        BrokerTransactionExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Real privileged execution needs a signed, trusted, elevated deployment. Development
        // isolation may use a writable test directory, but the outcome is marked synthetic.
        var development = _developmentIsolation && _trust.DevelopmentIsolationAllowed;
        if (!_trust.RealElevationEnabled && !development)
        {
            return Refuse(
                BrokerRefusalCode.RealElevationDisabled,
                "The deployment is not a signed, trusted, elevated installation; no privileged transaction may start.");
        }

        var root = development ? _directoryPolicy.DevelopmentRoot : _directoryPolicy.ProductionRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            return Refuse(BrokerRefusalCode.TrustedDeploymentUnavailable, "No journal directory is configured.");
        }

        var facts = _pathProbe.Inspect(root, development);
        var directory = _directoryPolicy.Evaluate(facts);
        if (!directory.Allowed)
        {
            return new BrokerTransactionExecutionOutcome
            {
                Executed = false,
                WroteAnyField = false,
                State = null,
                Refusals = directory.Refusals
                    .Select(refusal => BrokerRefusal.Of(
                        refusal.Code == RepairTransactionRefusalCode.JournalDirectoryUserWritable
                            ? BrokerRefusalCode.InstallDirectoryUserWritable
                            : BrokerRefusalCode.TrustedDeploymentUnavailable,
                        refusal.Detail))
                    .ToArray(),
                Detail = "journal_directory_refused",
            };
        }

        var journalPath = Path.Combine(directory.RootPath, _journalFileName);
        RepairJournal journal;
        try
        {
            journal = RepairJournal.Open(
                RepairFileJournalDevice.Open(journalPath, directory.IsDevelopmentIsolation));
        }
        catch (RepairJournalDeviceException exception)
        {
            return Refuse(
                exception.Failure == RepairJournalDeviceFailure.LockUnavailable
                    ? BrokerRefusalCode.TransactionJournalNotImplemented
                    : BrokerRefusalCode.TrustedDeploymentUnavailable,
                "The authoritative transaction journal could not be opened: " + exception.Failure);
        }

        using (journal)
        {
            if (!journal.IsUsable)
            {
                return Refuse(
                    BrokerRefusalCode.JournalUnavailable,
                    "The authoritative journal is corrupt or from an unsupported version; no write was attempted.");
            }

            if (journal.UnresolvedTransactionIds().Count > 0)
            {
                return Refuse(
                    BrokerRefusalCode.JournalUnavailable,
                    "An unresolved transaction is present; the user must confirm a new recovery plan first.");
            }

            var coordinator = new RepairTransactionCoordinator(journal, _clock);
            var result = await coordinator.ExecuteAsync(new RepairTransactionRequest
            {
                TransactionId = request.TransactionId,
                Plan = request.Plan,
                Authorization = request.Authorization,
                UserSid = request.UserSid,
                LogonSessionId = request.LogonSessionId,
                NowUtc = request.NowUtc,
                Adapter = _adapterFactory(),
                Observation = _observationFactory(),
                Capabilities = _capabilities,
                DevelopmentIsolation = directory.IsDevelopmentIsolation || development,
                Clock = _clock,
            }, cancellationToken).ConfigureAwait(false);

            return new BrokerTransactionExecutionOutcome
            {
                Executed = result.State == RepairTransactionState.Committed
                    || result.State == RepairTransactionState.NoOp,
                WroteAnyField = result.WroteAnyField,
                State = result.State,
                Refusals = result.Refusals.Select(MapRefusal).ToArray(),
                Detail = RepairTransactionStateRules.WireName(result.State),
            };
        }
    }

    private static BrokerTransactionExecutionOutcome Refuse(BrokerRefusalCode code, string detail) => new()
    {
        Executed = false,
        WroteAnyField = false,
        State = null,
        Refusals = new[] { BrokerRefusal.Of(code, detail) },
        Detail = detail,
    };

    private static BrokerRefusal MapRefusal(RepairTransactionRefusal refusal) => BrokerRefusal.Of(
        refusal.Code switch
        {
            RepairTransactionRefusalCode.JournalFlushFailed => BrokerRefusalCode.JournalUnavailable,
            RepairTransactionRefusalCode.JournalCorrupt => BrokerRefusalCode.JournalCorrupt,
            RepairTransactionRefusalCode.JournalVersionUnsupported => BrokerRefusalCode.JournalCorrupt,
            RepairTransactionRefusalCode.JournalSequenceGap => BrokerRefusalCode.JournalCorrupt,
            RepairTransactionRefusalCode.JournalLocked => BrokerRefusalCode.JournalUnavailable,
            RepairTransactionRefusalCode.JournalCapabilityClosed => BrokerRefusalCode.TrustedDeploymentUnavailable,
            RepairTransactionRefusalCode.JournalDirectoryNotFixed => BrokerRefusalCode.TrustedDeploymentUnavailable,
            RepairTransactionRefusalCode.JournalDirectoryUserWritable => BrokerRefusalCode.InstallDirectoryUserWritable,
            RepairTransactionRefusalCode.JournalDirectoryReparsePoint => BrokerRefusalCode.TrustedDeploymentUnavailable,
            RepairTransactionRefusalCode.UnresolvedTransactionPresent => BrokerRefusalCode.JournalUnavailable,
            RepairTransactionRefusalCode.CapabilityUnavailable => BrokerRefusalCode.CapabilityUnavailable,
            RepairTransactionRefusalCode.PlanNotExecutable => BrokerRefusalCode.PlanNotExecutable,
            RepairTransactionRefusalCode.AuthorizationInvalid => BrokerRefusalCode.AuthorizationMismatch,
            RepairTransactionRefusalCode.SyntheticNotAllowed => BrokerRefusalCode.SyntheticPlanNotExecutable,
            RepairTransactionRefusalCode.ExternalValueChanged or RepairTransactionRefusalCode.BeforeMismatch
                => BrokerRefusalCode.RequiresNewPreview,
            RepairTransactionRefusalCode.TargetDrift or RepairTransactionRefusalCode.TargetRebuilt
                or RepairTransactionRefusalCode.CrossBoot => BrokerRefusalCode.RequiresNewPreview,
            RepairTransactionRefusalCode.WriteFailed => BrokerRefusalCode.ExecutionWriteFailed,
            RepairTransactionRefusalCode.ReadbackMismatch => BrokerRefusalCode.ExecutionWriteFailed,
            RepairTransactionRefusalCode.ObservationFailed or RepairTransactionRefusalCode.ObservationUnavailable
                => BrokerRefusalCode.ExecutionObservationFailed,
            RepairTransactionRefusalCode.RollbackConflict => BrokerRefusalCode.ExecutionRollbackConflict,
            RepairTransactionRefusalCode.RollbackFailed => BrokerRefusalCode.ExecutionRollbackFailed,
            RepairTransactionRefusalCode.RecoveryUncertain => BrokerRefusalCode.ExecutionRecoveryRequired,
            RepairTransactionRefusalCode.Cancelled => BrokerRefusalCode.Cancelled,
            RepairTransactionRefusalCode.DeadlineExceeded => BrokerRefusalCode.ExecutionObservationFailed,
            _ => BrokerRefusalCode.ExecutionNotImplemented,
        },
        refusal.Detail);
}

/// <summary>Startup view of the authoritative journal; it reports only and never executes.</summary>
public sealed record BrokerRecoveryStartupScan
{
    public required bool JournalAvailable { get; init; }

    public required string Notice { get; init; }

    public required int UnresolvedCount { get; init; }

    public required IReadOnlyList<BrokerRecoveryPreview> Previews { get; init; }

    public static BrokerRecoveryStartupScan Unavailable(string detail) => new()
    {
        JournalAvailable = false,
        Notice = detail,
        UnresolvedCount = 0,
        Previews = Array.Empty<BrokerRecoveryPreview>(),
    };
}

public sealed record BrokerRecoveryPreview
{
    public required string TransactionId { get; init; }

    public required string SourceState { get; init; }

    public required int RestorableFieldCount { get; init; }

    public required int UncertainActionCount { get; init; }

    public required bool RequiresNewAuthorization { get; init; }

    public required string RecoveryPlanId { get; init; }
}

/// <summary>Reads the journal at broker startup and produces user-facing recovery previews only.</summary>
public sealed class RepairBrokerRecoveryStartup
{
    private readonly RepairJournalDirectoryPolicy _directoryPolicy;
    private readonly IRepairJournalPathProbe _pathProbe;
    private readonly TimeProvider _clock;
    private readonly string _journalFileName;
    private readonly bool _developmentIsolation;

    public RepairBrokerRecoveryStartup(
        RepairJournalDirectoryPolicy directoryPolicy,
        IRepairJournalPathProbe pathProbe,
        TimeProvider? clock = null,
        string journalFileName = "transactions.journal",
        bool developmentIsolation = false)
    {
        _directoryPolicy = directoryPolicy;
        _pathProbe = pathProbe;
        _clock = clock ?? TimeProvider.System;
        _journalFileName = journalFileName;
        _developmentIsolation = developmentIsolation;
    }

    public BrokerRecoveryStartupScan Scan(BrokerTrustDecision trust)
    {
        var development = _developmentIsolation && trust.DevelopmentIsolationAllowed;
        if (!trust.RealElevationEnabled && !development)
            return BrokerRecoveryStartupScan.Unavailable("no journal is available for this deployment");

        var root = development ? _directoryPolicy.DevelopmentRoot : _directoryPolicy.ProductionRoot;
        if (string.IsNullOrWhiteSpace(root)) return BrokerRecoveryStartupScan.Unavailable("no journal directory configured");

        var facts = _pathProbe.Inspect(root, development);
        var directory = _directoryPolicy.Evaluate(facts);
        if (!directory.Allowed) return BrokerRecoveryStartupScan.Unavailable("the journal directory is not allowed");

        var path = Path.Combine(directory.RootPath, _journalFileName);
        if (!File.Exists(path)) return BrokerRecoveryStartupScan.Unavailable("no transaction journal exists yet");

        try
        {
            using var journal = RepairJournal.Open(RepairFileJournalDevice.Open(path, directory.IsDevelopmentIsolation));
            var scan = RepairJournalRecoveryScanner.Scan(journal, _clock.GetUtcNow());
            var previews = scan.Unresolved.Select(summary =>
            {
                var plan = RepairRecoveryPlanner.Plan(summary, _clock.GetUtcNow());
                return new BrokerRecoveryPreview
                {
                    TransactionId = summary.TransactionId,
                    SourceState = RepairTransactionStateRules.WireName(summary.LatestState),
                    RestorableFieldCount = plan.RestorableFields.Count,
                    UncertainActionCount = plan.UncertainActionIds.Count,
                    RequiresNewAuthorization = plan.RequiresNewAuthorization,
                    RecoveryPlanId = plan.RecoveryPlanId,
                };
            }).ToArray();

            return new BrokerRecoveryStartupScan
            {
                JournalAvailable = true,
                Notice = scan.StartupNotice,
                UnresolvedCount = scan.Unresolved.Count,
                Previews = previews,
            };
        }
        catch (RepairJournalDeviceException exception)
        {
            return BrokerRecoveryStartupScan.Unavailable("the transaction journal is locked: " + exception.Failure);
        }
    }
}
