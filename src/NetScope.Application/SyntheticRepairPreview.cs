using NetScope.Domain;
using NetScope.Repair;

namespace NetScope.Application;

/// <summary>
/// Synthetic preview used by the demo composition and by the fake UI loop. It builds a real T13
/// typed plan from synthetic facts so the preview/selection/no-op/gating code is the same code the
/// real path will use; it never reads the machine or writes anything, and the execution gate stays
/// disabled (synthetic plans cannot be authorized).
/// </summary>
public sealed class SyntheticRepairPreviewSource : IRepairPreviewSource
{
    public static readonly Guid TunGuid = Guid.Parse("7d4a2f0e-3b5c-4d6e-8f90-123456789abc");
    public const string BootId = "boot-t16-synthetic";
    public static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly Func<DateTimeOffset> _now;

    public SyntheticRepairPreviewSource(Func<DateTimeOffset>? now = null) => _now = now ?? (() => Now);

    public Task<RepairPlanView> PreviewAsync(RepairPreviewRequest request, CancellationToken cancellationToken)
    {
        var plan = Build(request);
        var view = RepairPreviewMapper.ToView(
            plan,
            new RepairGateView
            {
                Enabled = false,
                Text = "执行入口已禁用（Disabled）",
                Reasons = UnavailableRepairGateway.DeliveryReasons
                    .Concat(new[] { "合成演示计划：不是本机观测，也不是可执行计划。" })
                    .ToArray(),
            },
            request,
            targetDrifted: false,
            RepairInputTrust.Unverified);
        return Task.FromResult(view);
    }

    public RepairPlan Build(RepairPreviewRequest request)
    {
        var now = _now();
        var preflight = RepairPreflightEvaluator.Evaluate(PreflightRequest(request));
        var matrix = SyntheticMatrix();
        return new RepairPlanBuilder().Build(new RepairPlanRequest
        {
            PlanId = "plan-t16-synthetic",
            SnapshotId = request.SnapshotId,
            DiagnosisId = request.DiagnosisId,
            IncidentId = "incident-t16-synthetic",
            NowUtc = now,
            PlannerClock = DesktopClock(now),
            Preflight = preflight,
            Capabilities = matrix,
            ActionRequests = new[]
            {
                new RepairActionRequest
                {
                    Action = "set_tun_ipv4_automatic_metric",
                    Store = "active_store",
                    DesiredValue = "disabled",
                },
                new RepairActionRequest
                {
                    Action = "set_tun_ipv4_interface_metric",
                    Store = "active_store",
                    DesiredValue = "20",
                },
                new RepairActionRequest
                {
                    Action = "set_tun_ipv4_ignore_default_routes",
                    Store = "active_store",
                    DesiredValue = "false",
                },
            },
            PlanTtl = RepairLimits.DefaultPlanTtl,
            Synthetic = true,
        });
    }

    public static RepairPreflightRequest PreflightRequest(RepairPreviewRequest request) => new()
    {
        PreflightId = "preflight-t16-synthetic",
        SnapshotId = request.SnapshotId,
        DiagnosisId = request.DiagnosisId,
        NowUtc = request.NowUtc,
        CurrentBootId = BootId,
        EvaluatorClock = DesktopClock(request.NowUtc),
        Interface = InterfaceFact(request.NowUtc),
        Fields = Fields(request.NowUtc),
        Attribution = AttributionFact(request.NowUtc),
        Uplink = UplinkFact(request.NowUtc),
        Candidates = new[]
        {
            new RepairTargetCandidate
            {
                Interface = InterfaceFact(request.NowUtc),
                Attribution = AttributionFact(request.NowUtc),
            },
        },
        Synthetic = true,
    };

    public static RepairClockDomain DesktopClock(DateTimeOffset now) => new()
    {
        DomainId = "desktop-t16-synthetic",
        ProcessRole = "desktop-as-invoker",
        BootId = BootId,
        MonotonicOrigin = 1_000,
        StartedAtUtc = now.AddMinutes(-10),
    };

    public static InterfaceIdentity Identity() => new()
    {
        InterfaceGuid = TunGuid,
        Luid = 0x0000000100000001UL,
        IfIndex = 42,
        Family = RepairAddressFamily.Ipv4,
        CompartmentId = 0,
        DeviceInstanceId = @"ROOT\NET\0001",
        Kind = InterfaceKind.Tun,
        Name = "Meta Tunnel（合成）",
        AdapterDescription = "合成 TUN",
        DriverKey = "wintun",
        DriverVersion = "0.14.0",
        BootId = BootId,
    };

    private static SourcedFact<T> Fact<T>(T value, string factId, DateTimeOffset now) where T : class => new()
    {
        FactId = factId,
        Source = "synthetic-t16",
        SourceVersion = "1.0.0",
        Status = EventStatus.Ok,
        ObservedAtUtc = now.AddSeconds(-5),
        ClockDomainId = "desktop-t16-synthetic",
        BootId = BootId,
        Ttl = RepairLimits.DefaultFactTtl,
        MonotonicMs = 100,
        Value = value,
        EvidenceIds = new[] { factId + ":evidence" },
    };

    private static SourcedFact<InterfaceIdentity> InterfaceFact(DateTimeOffset now) =>
        Fact(Identity(), "interface:target", now);

    private static SourcedFact<TunAttribution> AttributionFact(DateTimeOffset now) => Fact(new TunAttribution
    {
        Verdict = TunAttributionVerdict.Verified,
        Basis = TunAttributionBasis.ClientRuntimeApi,
        SourceId = "synthetic-runtime-api/1.0",
        ClientId = "synthetic",
        EvidenceIds = new[] { "ev:tun-owner" },
    }, "attribution:tun", now);

    private static SourcedFact<UplinkAssessment> UplinkFact(DateTimeOffset now) => Fact(new UplinkAssessment
    {
        ProtectedUplinkKnown = true,
        ProtectedInterfaceGuid = Guid.Parse("1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d"),
        ProtectedKind = InterfaceKind.Ethernet,
        TargetOwnsDefaultRoute = false,
        EvidenceIds = new[] { "ev:uplink" },
    }, "uplink:protected", now);

    private static IReadOnlyList<SourcedFact<RepairFieldState>> Fields(DateTimeOffset now) => new[]
    {
        FieldFact(RepairField.InterfaceMetric, new InterfaceMetricValue
        {
            RawValue = 35,
            Kind = MetricValueKind.ManualConfigured,
        }, now),
        FieldFact(RepairField.AutomaticMetric, new AutomaticMetricValue { Enabled = true }, now),
        FieldFact(RepairField.IgnoreDefaultRoutes, new IgnoreDefaultRoutesValue { Ignore = false }, now),
    };

    private static SourcedFact<RepairFieldState> FieldFact(
        RepairField field, RepairFieldValue value, DateTimeOffset now) => Fact(
        new RepairFieldState { Field = field, Store = RepairStore.ActiveStore, Value = value },
        "field:" + WireNames.Of(field) + ":active_store",
        now);

    private static PlatformCapabilityMatrix SyntheticMatrix()
    {
        var environment = new PlatformEnvironment
        {
            WindowsBuild = 26100,
            DriverKey = "wintun",
            DriverVersion = "0.14.0",
            ApiId = "iphelper:SetIpInterfaceEntry",
            MatrixVersion = "t16-synthetic/1.0.0",
        };
        var matrix = PlatformCapabilityMatrix.ForSyntheticTests(environment, Array.Empty<PlatformCapability>());
        return PlatformCapabilityMatrix.ForSyntheticTests(environment, new[]
        {
            Capability(matrix, RepairActionKind.SetTunIpv4InterfaceMetric, environment,
                extraChecks: new[] { RepairExperimentChecks.CouplingClosed }),
            Capability(matrix, RepairActionKind.SetTunIpv4AutomaticMetric, environment),
        });
    }

    private static PlatformCapability Capability(
        PlatformCapabilityMatrix matrix, RepairActionKind action, PlatformEnvironment environment,
        IEnumerable<string>? extraChecks = null)
    {
        var signature = matrix.SignatureFor(action, RepairStore.ActiveStore, RepairAddressFamily.Ipv4);
        return PlatformCapability.Verified(
            signature,
            "cap-synthetic:" + WireNames.Of(action),
            "experiment-synthetic",
            Now,
            new[] { "ev:experiment" },
            RepairExperimentChecks.RequiredFor(action).Concat(extraChecks ?? Array.Empty<string>()));
    }
}

