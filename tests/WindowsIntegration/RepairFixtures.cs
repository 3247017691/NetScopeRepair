using NetScope.Domain;
using NetScope.Repair;

namespace NetScope.WindowsIntegration.Tests;

/// <summary>
/// Synthetic-only fixtures. Nothing in these tests reads the machine's real interfaces,
/// clients, registry or proxy configuration, and nothing writes to the system.
/// </summary>
internal static class RepairFixtures
{
    public static readonly Guid TunGuid = Guid.Parse("7d4a2f0e-3b5c-4d6e-8f90-123456789abc");
    public static readonly Guid OtherTunGuid = Guid.Parse("8e5b3f1a-4c6d-4e7f-9a01-23456789abcd");
    public static readonly Guid ProtectedGuid = Guid.Parse("1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d");

    public const string BootId = "boot-t13";

    public static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    public static DateTimeOffset Observed => Now.AddSeconds(-5);

    public static RepairClockDomain DesktopClock => new()
    {
        DomainId = "desktop-test",
        ProcessRole = "desktop-as-invoker",
        BootId = BootId,
        MonotonicOrigin = 1_000,
        StartedAtUtc = Now.AddMinutes(-10),
    };

    public static RepairClockDomain BrokerClock => new()
    {
        DomainId = "broker-test",
        ProcessRole = "broker",
        BootId = BootId,
        MonotonicOrigin = 900_000,
        StartedAtUtc = Now.AddMinutes(-1),
    };

    public static PlatformEnvironment TestEnvironment { get; } = new()
    {
        WindowsBuild = 26100,
        DriverKey = "wintun",
        DriverVersion = "0.14.0",
        ApiId = "iphelper:SetIpInterfaceEntry",
        MatrixVersion = "t13-test/1.0.0",
    };

    public static SourcedFact<T> Fact<T>(
        T? value,
        string factId,
        EventStatus status = EventStatus.Ok,
        DateTimeOffset? observedAt = null,
        string? bootId = null,
        string? clockDomainId = null,
        TimeSpan? ttl = null,
        double? monotonicMs = null,
        string? reason = null,
        IEnumerable<string>? evidence = null)
        where T : class => new()
        {
            FactId = factId,
            Source = "synthetic-t13",
            SourceVersion = "1.0.0",
            Status = status,
            ObservedAtUtc = observedAt ?? Observed,
            ClockDomainId = clockDomainId ?? "desktop-test",
            BootId = bootId ?? BootId,
            Ttl = ttl ?? RepairLimits.DefaultFactTtl,
            MonotonicMs = monotonicMs ?? 100,
            Value = value,
            Reason = reason ?? (status == EventStatus.Ok ? null : "synthetic status " + status),
            EvidenceIds = evidence?.ToArray() ?? new[] { factId + ":evidence" },
        };

    public static InterfaceMetricValue Metric(uint raw, MetricValueKind kind = MetricValueKind.ManualConfigured) =>
        new() { RawValue = raw, Kind = kind };

    public static AutomaticMetricValue Automatic(bool enabled) => new() { Enabled = enabled };

    public static IgnoreDefaultRoutesValue Ignore(bool ignore) => new() { Ignore = ignore };

    public static InterfaceIdentity Identity(
        Guid? guid = null,
        RepairAddressFamily family = RepairAddressFamily.Ipv4,
        InterfaceKind kind = InterfaceKind.Tun,
        ulong? luid = 0x0000000100000001UL,
        int? ifIndex = 42,
        int? compartmentId = 0,
        string? deviceInstanceId = @"ROOT\NET\0001",
        string? bootId = null,
        string? name = "Meta Tunnel") => new()
        {
            InterfaceGuid = guid ?? TunGuid,
            Luid = luid,
            IfIndex = ifIndex,
            Family = family,
            CompartmentId = compartmentId,
            DeviceInstanceId = deviceInstanceId,
            Kind = kind,
            Name = name,
            AdapterDescription = name,
            DriverKey = "wintun",
            DriverVersion = "0.14.0",
            BootId = bootId ?? BootId,
        };

    public static SourcedFact<InterfaceIdentity> InterfaceFact(
        InterfaceIdentity? identity = null,
        EventStatus status = EventStatus.Ok,
        DateTimeOffset? observedAt = null,
        string? bootId = null,
        TimeSpan? ttl = null) =>
        Fact(identity ?? Identity(), "interface:target", status, observedAt, bootId, ttl: ttl);

    public static TunAttribution VerifiedAttribution() => new()
    {
        Verdict = TunAttributionVerdict.Verified,
        Basis = TunAttributionBasis.ClientRuntimeApi,
        SourceId = "nice-runtime-api/1.0",
        ClientId = "nice",
        EvidenceIds = new[] { "ev:tun-owner" },
    };

    public static TunAttribution UntrustedAttribution(TunAttributionBasis basis) => new()
    {
        Verdict = TunAttributionVerdict.Verified,
        Basis = basis,
        SourceId = "heuristic-demo",
        ClientId = "nice",
        EvidenceIds = new[] { "ev:heuristic" },
    };

    public static SourcedFact<TunAttribution> AttributionFact(
        TunAttribution? attribution = null,
        EventStatus status = EventStatus.Ok,
        DateTimeOffset? observedAt = null,
        string? bootId = null,
        TimeSpan? ttl = null) =>
        Fact(attribution ?? VerifiedAttribution(), "attribution:tun", status, observedAt, bootId, ttl: ttl);

    public static UplinkAssessment Uplink(
        Guid? protectedGuid = null,
        bool known = true,
        InterfaceKind protectedKind = InterfaceKind.Ethernet,
        bool? targetOwnsDefaultRoute = false) => new()
        {
            ProtectedUplinkKnown = known,
            ProtectedInterfaceGuid = protectedGuid ?? ProtectedGuid,
            ProtectedKind = protectedKind,
            TargetOwnsDefaultRoute = targetOwnsDefaultRoute,
            EvidenceIds = new[] { "ev:uplink" },
        };

    public static SourcedFact<UplinkAssessment> UplinkFact(
        UplinkAssessment? uplink = null,
        EventStatus status = EventStatus.Ok,
        DateTimeOffset? observedAt = null,
        string? bootId = null,
        TimeSpan? ttl = null) =>
        Fact(uplink ?? Uplink(), "uplink:protected", status, observedAt, bootId, ttl: ttl);

    public static SourcedFact<RepairFieldState> FieldFact(
        RepairField field,
        RepairFieldValue value,
        RepairStore store = RepairStore.ActiveStore,
        EventStatus status = EventStatus.Ok,
        DateTimeOffset? observedAt = null,
        string? bootId = null,
        TimeSpan? ttl = null) =>
        Fact(
            new RepairFieldState { Field = field, Store = store, Value = value },
            "field:" + WireNames.Of(field) + ":" + WireNames.Of(store),
            status,
            observedAt,
            bootId,
            ttl: ttl);

    public static IReadOnlyList<SourcedFact<RepairFieldState>> Fields(
        RepairFieldValue? metric = null,
        RepairFieldValue? automatic = null,
        RepairFieldValue? ignore = null,
        EventStatus metricStatus = EventStatus.Ok,
        EventStatus automaticStatus = EventStatus.Ok,
        EventStatus ignoreStatus = EventStatus.Ok,
        DateTimeOffset? observedAt = null,
        string? bootId = null,
        TimeSpan? ttl = null,
        RepairField? omit = null)
    {
        var fields = new List<SourcedFact<RepairFieldState>>();
        if (omit != RepairField.InterfaceMetric)
        {
            fields.Add(FieldFact(RepairField.InterfaceMetric, metric ?? Metric(35), status: metricStatus, observedAt: observedAt, bootId: bootId, ttl: ttl));
        }

        if (omit != RepairField.AutomaticMetric)
        {
            fields.Add(FieldFact(RepairField.AutomaticMetric, automatic ?? Automatic(false), status: automaticStatus, observedAt: observedAt, bootId: bootId, ttl: ttl));
        }

        if (omit != RepairField.IgnoreDefaultRoutes)
        {
            fields.Add(FieldFact(RepairField.IgnoreDefaultRoutes, ignore ?? Ignore(false), status: ignoreStatus, observedAt: observedAt, bootId: bootId, ttl: ttl));
        }

        return fields;
    }

    public static RepairTargetCandidate Candidate(
        InterfaceIdentity? identity = null,
        TunAttribution? attribution = null) => new()
        {
            Interface = InterfaceFact(identity ?? Identity()),
            Attribution = AttributionFact(attribution ?? VerifiedAttribution()),
        };

    public static RepairPreflightRequest PreflightRequest(
        InterfaceIdentity? identity = null,
        IReadOnlyList<SourcedFact<RepairFieldState>>? fields = null,
        SourcedFact<TunAttribution>? attribution = null,
        SourcedFact<UplinkAssessment>? uplink = null,
        IReadOnlyList<RepairTargetCandidate>? candidates = null,
        DateTimeOffset? now = null,
        string? currentBootId = null,
        bool synthetic = true,
        string preflightId = "preflight-t13",
        string snapshotId = "snapshot-t13")
    {
        var target = identity ?? Identity();
        var interfaceFact = InterfaceFact(target);
        var attributionFact = attribution ?? AttributionFact();
        var candidateList = candidates ?? new[]
        {
            new RepairTargetCandidate { Interface = interfaceFact, Attribution = attributionFact },
        };

        return new RepairPreflightRequest
        {
            PreflightId = preflightId,
            SnapshotId = snapshotId,
            DiagnosisId = "diagnosis-t13",
            NowUtc = now ?? Now,
            CurrentBootId = currentBootId ?? BootId,
            EvaluatorClock = DesktopClock,
            Interface = interfaceFact,
            Fields = fields ?? Fields(),
            Attribution = attributionFact,
            Uplink = uplink ?? UplinkFact(),
            Candidates = candidateList,
            Synthetic = synthetic,
        };
    }

    public static RepairPreflight Preflight(RepairPreflightRequest? request = null) =>
        RepairPreflightEvaluator.Evaluate(request ?? PreflightRequest());

    public static RepairActionRequest Action(string action, string desired, string store = "active_store") => new()
    {
        Action = action,
        Store = store,
        DesiredValue = desired,
    };

    public static RepairPlanRequest PlanRequest(
        RepairPreflight preflight,
        IEnumerable<RepairActionRequest> actions,
        PlatformCapabilityMatrix? capabilities = null,
        DateTimeOffset? now = null,
        bool? synthetic = null) => new()
        {
            PlanId = "plan-t13",
            SnapshotId = preflight.SnapshotId,
            DiagnosisId = preflight.DiagnosisId,
            IncidentId = "incident-t13",
            NowUtc = now ?? Now,
            PlannerClock = DesktopClock,
            Preflight = preflight,
            Capabilities = capabilities ?? PlatformCapabilityMatrix.UnverifiedDefault,
            ActionRequests = actions.ToArray(),
            Synthetic = synthetic ?? preflight.Synthetic,
        };

    public static RepairPlan Build(RepairPreflight preflight, params RepairActionRequest[] actions) =>
        new RepairPlanBuilder().Build(PlanRequest(preflight, actions));

    public static PlatformCapability VerifiedCapability(
        RepairActionKind action,
        bool includeRequiredChecks = true,
        IEnumerable<string>? extraChecks = null)
    {
        var matrix = PlatformCapabilityMatrix.ForSyntheticTests(TestEnvironment, Array.Empty<PlatformCapability>());
        var signature = matrix.SignatureFor(action, RepairStore.ActiveStore, RepairAddressFamily.Ipv4);
        var checks = includeRequiredChecks
            ? RepairExperimentChecks.RequiredFor(action)
            : new[] { RepairExperimentChecks.Setter };
        return PlatformCapability.Verified(
            signature,
            "cap-synthetic:" + WireNames.Of(action),
            "experiment-synthetic",
            Now,
            new[] { "ev:experiment" },
            checks.Concat(extraChecks ?? Array.Empty<string>()));
    }

    public static PlatformCapabilityMatrix SyntheticMatrix(params PlatformCapability[] capabilities) =>
        PlatformCapabilityMatrix.ForSyntheticTests(TestEnvironment, capabilities);
}
