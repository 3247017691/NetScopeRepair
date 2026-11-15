namespace NetScope.Broker;

/// <summary>Why the broker's bounded session clock refused to continue.</summary>
public enum BrokerBudgetStatus
{
    Ok,
    HandshakeTimeout,
    IdleTimeout,
    TotalBudgetExhausted,
    ForwardWindowClosed,
}

/// <summary>
/// Bounded session clock from design 05 §3. All comparisons use the broker's own monotonic clock;
/// a wall-clock change cannot extend a lease. The compensation reserve is never spendable by
/// forward actions or observation.
/// </summary>
public sealed class BrokerSessionBudget
{
    private readonly TimeProvider _time;
    private readonly long _origin;
    private long _lastActivity;
    private long _transactionStart;
    private bool _transactionStarted;

    public BrokerSessionBudget(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _origin = _time.GetTimestamp();
        _lastActivity = _origin;
    }

    public TimeSpan Elapsed => _time.GetElapsedTime(_origin);

    public TimeSpan IdleElapsed => _time.GetElapsedTime(_lastActivity);

    public TimeSpan TransactionElapsed => _transactionStarted ? _time.GetElapsedTime(_transactionStart) : TimeSpan.Zero;

    public bool TransactionStarted => _transactionStarted;

    /// <summary>Any accepted frame resets the idle window.</summary>
    public void Touch() => _lastActivity = _time.GetTimestamp();

    /// <summary>Starts the transaction budget; called only after identity and authorization.</summary>
    public void StartTransaction()
    {
        Touch();
        _transactionStart = _time.GetTimestamp();
        _transactionStarted = true;
    }

    public BrokerBudgetStatus CheckHandshake() =>
        Elapsed > BrokerProtocol.HandshakeTimeout ? BrokerBudgetStatus.HandshakeTimeout : BrokerBudgetStatus.Ok;

    public BrokerBudgetStatus CheckIdle() =>
        IdleElapsed > BrokerProtocol.IdleTimeout ? BrokerBudgetStatus.IdleTimeout : BrokerBudgetStatus.Ok;

    public BrokerBudgetStatus CheckTotal() =>
        Elapsed > BrokerProtocol.TotalTransactionBudget ? BrokerBudgetStatus.TotalBudgetExhausted : BrokerBudgetStatus.Ok;

    /// <summary>
    /// New forward changes and a new observation may start only before the 120 second point and
    /// only while the whole 180 second budget has not run out.
    /// </summary>
    public BrokerBudgetStatus CheckForwardAction()
    {
        if (CheckTotal() != BrokerBudgetStatus.Ok) return BrokerBudgetStatus.TotalBudgetExhausted;
        if (!_transactionStarted) return BrokerBudgetStatus.Ok;
        return TransactionElapsed >= BrokerProtocol.LatestForwardActionStart
            ? BrokerBudgetStatus.ForwardWindowClosed
            : BrokerBudgetStatus.Ok;
    }

    /// <summary>min(60 s, remaining forward budget); zero once the forward window is closed.</summary>
    public TimeSpan EffectiveObservationWindow()
    {
        if (!_transactionStarted) return BrokerProtocol.MaxTechnicalObservation;
        var remainingForward = BrokerProtocol.LatestForwardActionStart - TransactionElapsed;
        if (remainingForward <= TimeSpan.Zero) return TimeSpan.Zero;
        return remainingForward < BrokerProtocol.MaxTechnicalObservation
            ? remainingForward
            : BrokerProtocol.MaxTechnicalObservation;
    }

    /// <summary>The part of the 180 second budget still reserved for compensation.</summary>
    public TimeSpan RemainingCompensationReserve()
    {
        var remaining = BrokerProtocol.TotalTransactionBudget - TransactionElapsed;
        if (remaining <= TimeSpan.Zero) return TimeSpan.Zero;
        return remaining < BrokerProtocol.CompensationReserve ? remaining : BrokerProtocol.CompensationReserve;
    }

    /// <summary>
    /// The wait applied to the next frame read: the smaller of the idle window and the remaining
    /// handshake or total budget, so a silent peer cannot hold the broker open.
    /// </summary>
    public TimeSpan NextReadTimeout(BrokerSessionPhase phase)
    {
        var idle = BrokerProtocol.IdleTimeout - IdleElapsed;
        if (idle < TimeSpan.Zero) idle = TimeSpan.Zero;

        if (phase == BrokerSessionPhase.Handshake)
        {
            var handshake = BrokerProtocol.HandshakeTimeout - Elapsed;
            if (handshake < TimeSpan.Zero) handshake = TimeSpan.Zero;
            return handshake < idle ? handshake : idle;
        }

        var total = BrokerProtocol.TotalTransactionBudget - Elapsed;
        if (total < TimeSpan.Zero) total = TimeSpan.Zero;
        return total < idle ? total : idle;
    }
}

/// <summary>The two protocol phases an IPC read can happen in.</summary>
public enum BrokerSessionPhase
{
    Handshake,
    Transaction,
}
