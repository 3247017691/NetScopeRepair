using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Probes;

/// <summary>Runs fixed request options over bounded, separately recorded probe paths.</summary>
public sealed class HttpProbeRunner : IProbeRunner
{
    public static readonly TimeSpan ComparabilityWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1d);

    private readonly IProbeTransport _transport;
    private readonly IClock _clock;
    private readonly TimeProvider _timerProvider;

    public HttpProbeRunner(IProbeTransport transport, IClock clock, TimeProvider? timerProvider = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _timerProvider = timerProvider ?? TimeProvider.System;
    }

    public async Task<ProbeGroupResult> RunAsync(ProbeGroupPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var startedAt = _clock.UtcNow;
        var startedMonotonic = _clock.MonotonicMs;
        var paths = plan.Paths?.ToArray() ?? Array.Empty<ProbePath>();
        var optionsKey = plan.Target is not null && plan.Target.Method is not null
            && plan.Target.ExpectedStatusCodes is not null ? ProbeIdentity.OptionsKey(plan) : null;
        var invalidReason = InvalidPlanReason(plan);
        if (!IsValidTime(startedMonotonic))
        {
            invalidReason ??= "invalid_monotonic_clock";
        }

        if (invalidReason is not null)
        {
            return new ProbeGroupResult
            {
                ProbeGroupId = plan.ProbeGroupId,
                StartedAt = startedAt,
                DurationMs = 0,
                Results = paths.Select((path, index) => NotStarted(
                    plan, path, index, optionsKey, EventStatus.Unsupported, "not_started_" + invalidReason)).ToArray(),
                MissingPaths = paths,
                TimingComparable = false,
            };
        }

        // The provider's elapsed-time timer is independent of wall-clock adjustments.
        // The injected monotonic clock also checks queued paths and transport completion.
        var remainingBudgetMs = plan.GroupBudget.TotalMilliseconds
            - Elapsed(startedMonotonic, _clock.MonotonicMs);
        using var budgetSource = remainingBudgetMs <= 0
            ? new CancellationTokenSource()
            : new CancellationTokenSource(TimeSpan.FromMilliseconds(remainingBudgetMs), _timerProvider);
        if (remainingBudgetMs <= 0) budgetSource.Cancel();
        using var groupSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budgetSource.Token);
        using var semaphore = new SemaphoreSlim(plan.MaxConcurrency);
        var tasks = paths.Select((path, index) => RunOneAsync(
            plan, path, index, optionsKey!, startedMonotonic, semaphore,
            cancellationToken, budgetSource, groupSource.Token)).ToArray();
        var runs = await Task.WhenAll(tasks).ConfigureAwait(false);
        var endedMonotonic = _clock.MonotonicMs;
        var duration = Elapsed(startedMonotonic, endedMonotonic);
        var budgetExhausted = runs.Any(run => run.BudgetExhausted);
        var results = runs.Select(run => run.Result).ToArray();
        var comparable = results.Length > 0
            && runs.All(run => run.WasStarted && run.TimingValid)
            && !budgetExhausted
            && !cancellationToken.IsCancellationRequested
            && !string.IsNullOrWhiteSpace(plan.ObservationWindowId)
            && !string.IsNullOrWhiteSpace(plan.BootId)
            && ProbeFamilyPolicy.IsKnown(plan.AddressFamily)
            && IsValidTime(endedMonotonic) && endedMonotonic >= startedMonotonic
            && results.All(result => result.StartedMonotonicMs is { } time && IsValidTime(time)
                && !result.TopologyChanged
                && result.Status is EventStatus.Ok or EventStatus.Failed or EventStatus.Timeout or EventStatus.Unsupported)
            && results.Max(result => result.StartedMonotonicMs!.Value)
                - results.Min(result => result.StartedMonotonicMs!.Value) <= ComparabilityWindow.TotalMilliseconds;

        return new ProbeGroupResult
        {
            ProbeGroupId = plan.ProbeGroupId,
            StartedAt = startedAt,
            DurationMs = duration,
            Results = results.Select(result => result with { TimingComparable = comparable }).ToArray(),
            BudgetExhausted = budgetExhausted,
            MissingPaths = runs.Where(run => !run.WasStarted).Select(run => run.Result.Path).ToArray(),
            TimingComparable = comparable,
        };
    }

    private async Task<PathRun> RunOneAsync(
        ProbeGroupPlan plan, ProbePath path, int index, string optionsKey, double groupStartedMonotonic,
        SemaphoreSlim semaphore, CancellationToken userToken, CancellationTokenSource budgetSource,
        CancellationToken groupToken)
    {
        try
        {
            await semaphore.WaitAsync(groupToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return NotStartedRun();
        }

        try
        {
            var startedMonotonic = _clock.MonotonicMs;
            if (IsValidTime(startedMonotonic)
                && startedMonotonic - groupStartedMonotonic >= plan.GroupBudget.TotalMilliseconds)
            {
                budgetSource.Cancel();
            }

            if (groupToken.IsCancellationRequested)
            {
                return NotStartedRun();
            }

            if (!IsValidTime(startedMonotonic) || startedMonotonic < groupStartedMonotonic)
            {
                return new PathRun(NotStarted(plan, path, index, optionsKey, EventStatus.Unsupported,
                    "not_started_invalid_monotonic_clock"), false, false);
            }

            var startedAt = _clock.UtcNow;
            var request = new ProbeRequest
            {
                Endpoint = plan.Target.Endpoint,
                Method = plan.Target.Method.ToUpperInvariant(),
                Path = path,
                ExplicitProxyEndpoint = plan.ExplicitProxyEndpoint,
                AddressFamily = plan.AddressFamily,
                MaxResponseBytes = plan.MaxResponseBytes,
            };
            var outcome = await SendAsync(request, plan.PerProbeTimeout, userToken, budgetSource.Token, groupToken)
                .ConfigureAwait(false);
            var endedMonotonic = _clock.MonotonicMs;
            if (IsValidTime(endedMonotonic)
                && endedMonotonic - groupStartedMonotonic >= plan.GroupBudget.TotalMilliseconds)
            {
                budgetSource.Cancel();
            }

            if (userToken.IsCancellationRequested)
            {
                outcome = TransportOutcome.Failure(EventStatus.Cancelled, "probe_cancelled");
            }
            else if (budgetSource.IsCancellationRequested)
            {
                outcome = TransportOutcome.Failure(EventStatus.Cancelled, "group_budget_exhausted");
            }

            if (outcome.Status.IsSuccess() && outcome.HttpStatus is null)
            {
                outcome = outcome with { Status = EventStatus.Partial, TransportError = outcome.TransportError ?? "missing_http_status" };
            }

            return new PathRun(new ProbeResult
            {
                ProbeId = $"{plan.ProbeGroupId}-probe-{index + 1}",
                ProbeGroupId = plan.ProbeGroupId,
                TargetId = plan.Target.TargetId,
                Path = path,
                Status = outcome.Status,
                StartedAt = startedAt,
                StartedMonotonicMs = startedMonotonic,
                DurationMs = Elapsed(startedMonotonic, endedMonotonic),
                HttpStatus = outcome.HttpStatus,
                Classification = ProbeClassificationPolicy.Classify(outcome, plan.Target),
                TransportError = outcome.TransportError,
                OsErrorCode = outcome.OsErrorCode,
                ResolutionLocation = outcome.ResolutionLocation ?? "unknown",
                ResolvedAddresses = outcome.ResolvedAddresses,
                ResolutionSource = outcome.ResolutionSource,
                ResolvedAt = outcome.ResolvedAt,
                Connection = outcome.Connection,
                ProxyEndpointId = path == ProbePath.ExplicitHttpProxy ? plan.ProxyEndpointId : null,
                ProxyStack = ProxyStackFor(path),
                FailedStage = outcome.FailedStage,
                FailedEndpoint = outcome.FailedEndpoint ?? "unknown",
                ProxyIsLocal = path == ProbePath.ExplicitHttpProxy
                    ? outcome.ProxyIsLocal ?? ProbeEndpointPolicy.IsLocal(plan.ExplicitProxyEndpoint) : null,
                AddressFamily = plan.AddressFamily,
                RequestedAddressFamily = outcome.RequestedAddressFamily
                    ?? ProbeFamilyPolicy.Normalize(plan.AddressFamily),
                LocalConnectionAddressFamily = outcome.LocalConnectionAddressFamily,
                TargetAddressFamily = outcome.TargetAddressFamily,
                TargetAddressFamilyStatus = outcome.TargetAddressFamilyStatus
                    ?? DefaultTargetFamilyStatus(path, outcome.LocalConnectionAddressFamily),
                Method = plan.Target.Method.ToUpperInvariant(),
                RequestOptionsKey = optionsKey,
                ObservationWindowId = plan.ObservationWindowId,
                BootId = plan.BootId,
                TimingComparable = false,
                Notice = ComposeNotice(path, outcome.Notice),
            }, true, !userToken.IsCancellationRequested && budgetSource.IsCancellationRequested,
                IsValidTime(endedMonotonic) && endedMonotonic >= startedMonotonic);
        }
        finally
        {
            semaphore.Release();
        }

        PathRun NotStartedRun()
        {
            var userCancelled = userToken.IsCancellationRequested;
            return new PathRun(NotStarted(plan, path, index, optionsKey, EventStatus.Cancelled,
                userCancelled ? "not_started_group_cancelled" : "not_started_group_budget_exhausted"),
                false, !userCancelled && budgetSource.IsCancellationRequested);
        }
    }

    private async Task<TransportOutcome> SendAsync(
        ProbeRequest request, TimeSpan timeout, CancellationToken userToken,
        CancellationToken budgetToken, CancellationToken groupToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(groupToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            // Cancellation is passed to the transport and also bounds a transport that fails to
            // finish promptly after cancellation. Such a late result must not become an observation.
            return await _transport.SendAsync(request, timeoutSource.Token).WaitAsync(timeoutSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (userToken.IsCancellationRequested)
                return TransportOutcome.Failure(EventStatus.Cancelled, "probe_cancelled");
            if (budgetToken.IsCancellationRequested)
                return TransportOutcome.Failure(EventStatus.Cancelled, "group_budget_exhausted");
            if (timeoutSource.IsCancellationRequested)
                return TransportOutcome.Failure(EventStatus.Timeout, "probe_timeout");
            return TransportOutcome.Failure(EventStatus.Cancelled, "transport_cancelled");
        }
        catch (UnauthorizedAccessException)
        {
            return TransportOutcome.Failure(EventStatus.AccessDenied, "access_denied");
        }
        catch (Exception)
        {
            return TransportOutcome.Failure(EventStatus.Unknown, "transport_execution_error") with
            {
                Notice = "A local transport execution error prevented a reliable measurement; whether a request was sent is unknown.",
            };
        }
    }

    private ProbeResult NotStarted(ProbeGroupPlan plan, ProbePath path, int index, string? optionsKey,
        EventStatus status, string reason)
    {
        var time = _clock.MonotonicMs;
        return new ProbeResult
        {
            ProbeId = $"{plan.ProbeGroupId}-probe-{index + 1}",
            ProbeGroupId = plan.ProbeGroupId,
            TargetId = plan.Target?.TargetId ?? string.Empty,
            Path = path,
            Status = status,
            StartedAt = _clock.UtcNow,
            StartedMonotonicMs = IsValidTime(time) ? time : null,
            DurationMs = 0,
            Classification = ProbeClassification.Inconclusive,
            TransportError = reason,
            ResolutionLocation = "unknown",
            ProxyEndpointId = path == ProbePath.ExplicitHttpProxy ? plan.ProxyEndpointId : null,
            ProxyStack = ProxyStackFor(path),
            FailedEndpoint = "unknown",
            ProxyIsLocal = path == ProbePath.ExplicitHttpProxy ? ProbeEndpointPolicy.IsLocal(plan.ExplicitProxyEndpoint) : null,
            AddressFamily = plan.AddressFamily,
            RequestedAddressFamily = ProbeFamilyPolicy.Normalize(plan.AddressFamily),
            LocalConnectionAddressFamily = null,
            TargetAddressFamily = null,
            TargetAddressFamilyStatus = ProbeTargetFamilyStatus.NotAttempted,
            Method = plan.Target?.Method?.ToUpperInvariant(),
            RequestOptionsKey = optionsKey,
            ObservationWindowId = plan.ObservationWindowId,
            BootId = plan.BootId,
            TimingComparable = false,
            Notice = "No request was started for this path; nothing was measured. Reason: " + reason,
        };
    }

    private static string DefaultTargetFamilyStatus(ProbePath path, string? localFamily) => path switch
    {
        ProbePath.NoExplicitProxy => localFamily is null
            ? ProbeTargetFamilyStatus.UnknownAfterAttempt
            : ProbeTargetFamilyStatus.Observed,
        ProbePath.ExplicitHttpProxy => ProbeTargetFamilyStatus.UnknownThroughProxy,
        _ => ProbeTargetFamilyStatus.UnknownSystemDefault,
    };

    private static string? InvalidPlanReason(ProbeGroupPlan plan)
    {
        if (plan.GroupBudget <= TimeSpan.Zero || plan.GroupBudget > MaximumTimerDelay)
            return "invalid_group_budget";
        if (plan.PerProbeTimeout <= TimeSpan.Zero || plan.PerProbeTimeout > MaximumTimerDelay)
            return "invalid_probe_timeout";
        if (plan.MaxConcurrency <= 0) return "invalid_concurrency";
        if (plan.MaxResponseBytes < 0) return "invalid_response_limit";
        if (plan.Target is null || string.IsNullOrWhiteSpace(plan.Target.TargetId)
            || !Uri.TryCreate(plan.Target.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || string.IsNullOrWhiteSpace(plan.Target.Method)
            || plan.Target.ExpectedStatusCodes is null || plan.Target.ExpectedStatusCodes.Count == 0
            || plan.Target.ExpectedStatusCodes.Any(code => code is < 100 or > 599))
            return "invalid_request_options";
        try { _ = new HttpMethod(plan.Target.Method); }
        catch (FormatException) { return "invalid_request_options"; }
        return null;
    }

    private static bool IsValidTime(double value) => double.IsFinite(value) && value >= 0;
    private static double Elapsed(double start, double end) =>
        IsValidTime(start) && IsValidTime(end) && end >= start ? end - start : 0;

    private static string ProxyStackFor(ProbePath path) => path switch
    {
        ProbePath.SystemDefault => "dotnet-default-proxy",
        ProbePath.NoExplicitProxy => "no-application-proxy",
        ProbePath.ExplicitHttpProxy => "explicit-http-proxy",
        _ => "unknown",
    };

    /// <summary>
    /// Keeps the transport's own measured notice and still states the path caveat. On the system
    /// default path the transport notice already covers the whole caveat in more detail.
    /// </summary>
    private static string? ComposeNotice(ProbePath path, string? transportNotice)
    {
        var pathNotice = NoticeFor(path);
        if (string.IsNullOrWhiteSpace(transportNotice)) return pathNotice;
        return path == ProbePath.SystemDefault ? transportNotice : transportNotice + " " + pathNotice;
    }

    private static string NoticeFor(ProbePath path) => path switch
    {
        ProbePath.NoExplicitProxy =>
            "No explicit proxy does not prove physical-direct: with TUN enabled the request can still traverse the tunnel.",
        ProbePath.SystemDefault =>
            ".NET DefaultProxy may use process environment or platform proxy settings, including bypass. Actual proxy choice and DNS resolution location are unknown; PAC/auto-detection and browser behaviour are not verified.",
        ProbePath.ExplicitHttpProxy => "The request selected the plan's explicit proxy endpoint.",
        _ => "Unknown probe path.",
    };

    private sealed record PathRun(ProbeResult Result, bool WasStarted, bool BudgetExhausted, bool TimingValid = true);
}

