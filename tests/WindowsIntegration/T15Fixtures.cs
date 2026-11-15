using System.Text;
using NetScope.Broker;
using NetScope.Domain;
using NetScope.Repair;

namespace NetScope.WindowsIntegration.Tests;

/// <summary>In-memory journal device with fault injection and an append log; no disk is touched.</summary>
internal sealed class FakeJournalDevice : IRepairJournalDevice
{
    private byte[] _data = Array.Empty<byte>();
    private int _appendCalls;

    public string CanonicalPath { get; init; } = "memory://journal";

    public bool IsDevelopmentIsolation { get; init; } = true;

    public long Length => _data.Length;

    public bool HoldsExclusiveLock { get; init; } = true;

    public List<byte[]> Appended { get; } = new();

    public List<long> Truncations { get; } = new();

    /// <summary>1-based append call index that should fail; 0 means no failure.</summary>
    public int FailAppendCall { get; set; }

    public int AppendCalls => _appendCalls;

    public Action<int>? OnAppend { get; set; }

    public byte[] Snapshot() => _data.ToArray();

    public void Restore(byte[] data)
    {
        _data = data.ToArray();
        Appended.Clear();
    }

    /// <summary>Mimics a crash: further appends throw but the already durable bytes stay.</summary>
    public bool Crashed { get; set; }

    public byte[] ReadAll() => _data.ToArray();

    public void Append(byte[] data)
    {
        _appendCalls++;
        if (Crashed)
            throw new RepairJournalDeviceException(RepairJournalDeviceFailure.WriteFailure, "device is crashed");
        if (FailAppendCall > 0 && _appendCalls == FailAppendCall)
            throw new RepairJournalDeviceException(RepairJournalDeviceFailure.FlushFailure, "injected flush failure");
        _data = _data.Concat(data).ToArray();
        Appended.Add(data);
        OnAppend?.Invoke(_appendCalls);
    }

    public void Truncate(long length)
    {
        _data = _data[..checked((int)length)];
        Truncations.Add(length);
    }

    public void Dispose()
    {
    }

    public RepairJournal OpenJournal(DateTimeOffset? createdAt = null) => RepairJournal.Open(this, createdAt);

    public static string Text(byte[] data) => Encoding.UTF8.GetString(data);
}

/// <summary>
/// Recording fake of the native field seam. Values are held in memory; every get/set is logged so
/// tests can prove exactly which writes happened (never a real network write).
/// </summary>
internal sealed class FakeFieldAdapter : IRepairFieldAdapter
{
    private readonly Dictionary<RepairFieldKey, RepairFieldValue> _values = new();

    public FakeFieldAdapter(IEnumerable<RepairAction> actions)
    {
        foreach (var action in actions)
        {
            _values[Key(action)] = action.Before;
        }
    }

    public string AdapterId => "fake-t15";

    public bool IsPlatformVerified { get; init; } = true;

    public List<string> Calls { get; } = new();

    public List<(RepairField Field, string Value)> Writes { get; } = new();

    public int GetCalls { get; private set; }

    public int SetCalls { get; private set; }

    /// <summary>Overrides the boot id returned by every read.</summary>
    public string? BootIdOverride { get; set; }

    /// <summary>Returned for the next Set instead of a successful write.</summary>
    public Queue<RepairFieldWrite> SetResponses { get; } = new();

    /// <summary>Scripted response for a specific 1-based Set call.</summary>
    public Dictionary<int, RepairFieldWrite> SetResponsesByCall { get; } = new();

    /// <summary>When true every fresh read fails as unsupported.</summary>
    public bool FailGet { get; set; }

    /// <summary>Invoked before each Set so a test can inject an external change or clock advance.</summary>
    public Action<RepairAction, int>? BeforeSet { get; set; }

    /// <summary>Invoked before each Get so a test can inject a concurrent external value.</summary>
    public Action<RepairAction, int>? BeforeGet { get; set; }

    public void SetCurrent(RepairAction action, RepairFieldValue value) => _values[Key(action)] = value;

    public IReadOnlyDictionary<RepairFieldKey, RepairFieldValue> Values => _values;

    public Task<RepairFieldRead> GetAsync(
        InterfaceIdentity identity, RepairStore store, RepairField field, CancellationToken cancellationToken)
    {
        GetCalls++;
        var key = new RepairFieldKey(identity.InterfaceGuid, identity.Family, store, field);
        BeforeGet?.Invoke(new RepairAction
        {
            ActionId = "get-" + field,
            Kind = RepairActionRules.ForwardActions.First(kind => RepairActionRules.FieldOf(kind) == field),
            Field = field,
            Store = store,
            Family = identity.Family,
            TargetFingerprint = identity.TargetFingerprint,
            Identity = identity,
            Before = _values.TryGetValue(key, out var value) ? value : new InterfaceMetricValue { RawValue = 0, Kind = MetricValueKind.Unknown },
            Desired = new InterfaceMetricValue { RawValue = 0, Kind = MetricValueKind.Unknown },
            BeforeSource = "fake",
            BeforeSourceVersion = "1",
            BeforeStatus = EventStatus.Ok,
            BeforeObservedAtUtc = DateTimeOffset.UtcNow,
            BeforeTtl = TimeSpan.FromSeconds(30),
            Capability = BrokerPlanFixtures.SyntheticMatrix().Resolve(
                RepairActionKind.SetTunIpv4InterfaceMetric, RepairStore.ActiveStore, RepairAddressFamily.Ipv4),
            Eligibility = RepairActionEligibility.Allowed,
            Risk = RepairRiskLevel.Low,
            RollbackCondition = "fake",
            ObservationPlan = "fake",
            ValidUntilUtc = DateTimeOffset.UtcNow.AddMinutes(5),
        }, GetCalls);

        Calls.Add("get:" + WireNames.Of(field));
        if (FailGet) return Task.FromResult(RepairFieldRead.Failed(EventStatus.Unsupported, "injected get failure"));
        if (!_values.TryGetValue(key, out var current))
            return Task.FromResult(RepairFieldRead.Failed(EventStatus.Unknown, "missing fake value"));
        return Task.FromResult(new RepairFieldRead
        {
            Status = EventStatus.Ok,
            Value = current,
            Source = "fake-t15",
            ObservedAtUtc = DateTimeOffset.UtcNow,
            BootId = BootIdOverride ?? identity.BootId,
        });
    }

    public Task<RepairFieldWrite> SetAsync(
        InterfaceIdentity identity, RepairStore store, RepairField field, RepairFieldValue value,
        CancellationToken cancellationToken)
    {
        SetCalls++;
        var key = new RepairFieldKey(identity.InterfaceGuid, identity.Family, store, field);
        var action = new RepairAction
        {
            ActionId = "set-" + field,
            Kind = RepairActionRules.ForwardActions.First(kind => RepairActionRules.FieldOf(kind) == field),
            Field = field,
            Store = store,
            Family = identity.Family,
            TargetFingerprint = identity.TargetFingerprint,
            Identity = identity,
            Before = _values.TryGetValue(key, out var before) ? before : value,
            Desired = value,
            BeforeSource = "fake",
            BeforeSourceVersion = "1",
            BeforeStatus = EventStatus.Ok,
            BeforeObservedAtUtc = DateTimeOffset.UtcNow,
            BeforeTtl = TimeSpan.FromSeconds(30),
            Capability = BrokerPlanFixtures.SyntheticMatrix().Resolve(
                RepairActionKind.SetTunIpv4InterfaceMetric, RepairStore.ActiveStore, RepairAddressFamily.Ipv4),
            Eligibility = RepairActionEligibility.Allowed,
            Risk = RepairRiskLevel.Low,
            RollbackCondition = "fake",
            ObservationPlan = "fake",
            ValidUntilUtc = DateTimeOffset.UtcNow.AddMinutes(5),
        };
        BeforeSet?.Invoke(action, SetCalls);
        Calls.Add("set:" + WireNames.Of(field) + ":" + value.CanonicalText);

        if (SetResponsesByCall.TryGetValue(SetCalls, out var scriptedByCall))
            return Task.FromResult(scriptedByCall);
        if (SetResponses.Count > 0)
        {
            var scripted = SetResponses.Dequeue();
            return Task.FromResult(scripted);
        }

        _values[key] = value;
        Writes.Add((field, value.CanonicalText));
        return Task.FromResult(new RepairFieldWrite
        {
            Status = RepairFieldWriteStatus.Written,
            Readback = value,
            AtUtc = DateTimeOffset.UtcNow,
        });
    }

    private static RepairFieldKey Key(RepairAction action) =>
        new(action.Identity.InterfaceGuid, action.Identity.Family, action.Store, action.Field);
}

internal sealed class FakeObservationProbe : IRepairObservationProbe
{
    public bool Passed { get; init; } = true;

    public string Detail { get; init; } = "fake observation passed";

    /// <summary>Invoked during observation so tests can cancel the token or mutate the clock.</summary>
    public Action? DuringObservation { get; init; }

    public int Calls { get; private set; }

    public TimeSpan LastWindow { get; private set; }

    public Task<RepairObservationResult> ObserveAsync(
        RepairPlan plan, IReadOnlyList<RepairAction> actions, TimeSpan window, CancellationToken cancellationToken)
    {
        Calls++;
        LastWindow = window;
        DuringObservation?.Invoke();
        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);
        return Task.FromResult(new RepairObservationResult { Passed = Passed, Detail = Detail });
    }
}

internal static class T15Fixtures
{
    public const string UserSid = "S-1-5-21-1000-2000-3000-1001";
    public const string LogonSession = "luid:0x0000000012345678";

    public static DateTimeOffset Now => BrokerPlanFixtures.Now;

    public static (RepairPlan Plan, RepairAuthorizationBinding Authorization) Plan(
        int actions = 1, bool synthetic = false)
    {
        var (plan, authorization) = BrokerPlanFixtures.ValidPlan(synthetic: synthetic);
        var template = plan.Actions[0];
        var kinds = new[]
        {
            RepairActionKind.SetTunIpv4InterfaceMetric,
            RepairActionKind.SetTunIpv4AutomaticMetric,
            RepairActionKind.SetTunIpv4IgnoreDefaultRoutes,
        };
        var list = new List<RepairAction>();
        for (var index = 0; index < actions; index++)
        {
            var kind = kinds[index % kinds.Length];
            list.Add(index == 0 ? template : Action(template, kind, index));
        }

        if (actions <= 1) return (plan, authorization);
        var rebuilt = BrokerPlanFixtures.Rebuild(plan, list.ToArray());
        return rebuilt;
    }

    public static RepairAction Action(RepairAction template, RepairActionKind kind, int seed = 0)
    {
        var field = RepairActionRules.FieldOf(kind);
        RepairFieldValue before = kind switch
        {
            RepairActionKind.SetTunIpv4InterfaceMetric => new InterfaceMetricValue
            {
                RawValue = (uint)(30 + seed),
                Kind = MetricValueKind.ManualConfigured,
            },
            RepairActionKind.SetTunIpv4AutomaticMetric => new AutomaticMetricValue { Enabled = seed % 2 == 0 },
            _ => new IgnoreDefaultRoutesValue { Ignore = seed % 2 == 0 },
        };
        RepairFieldValue desired = kind switch
        {
            RepairActionKind.SetTunIpv4InterfaceMetric => new InterfaceMetricValue
            {
                RawValue = (uint)(10 + seed),
                Kind = MetricValueKind.ManualConfigured,
            },
            RepairActionKind.SetTunIpv4AutomaticMetric => new AutomaticMetricValue { Enabled = seed % 2 != 0 },
            _ => new IgnoreDefaultRoutesValue { Ignore = seed % 2 != 0 },
        };
        return template with
        {
            ActionId = RepairActionRules.ActionIdOf(kind),
            Kind = kind,
            Field = field,
            Before = before,
            Desired = desired,
            IsNoOp = false,
        };
    }

    public static RepairAuthorizationBinding Authorization(RepairPlan plan) =>
        BrokerPlanFixtures.Authorization(plan);

    public static RepairTransactionRequest Request(
        RepairJournal journal,
        RepairPlan plan,
        RepairAuthorizationBinding authorization,
        IRepairFieldAdapter adapter,
        IRepairObservationProbe? observation = null,
        TimeProvider? clock = null,
        bool developmentIsolation = true,
        string transactionId = "tx-t15") => new()
    {
        TransactionId = transactionId,
        Plan = plan,
        Authorization = authorization,
        UserSid = UserSid,
        LogonSessionId = LogonSession,
        NowUtc = Now,
        Adapter = adapter,
        Observation = observation ?? new FakeObservationProbe(),
        DevelopmentIsolation = developmentIsolation,
        Clock = clock,
    };

    public static RepairJournal OpenJournal(FakeJournalDevice device, DateTimeOffset? createdAt = null) =>
        device.OpenJournal(createdAt ?? Now);

    public static RepairRecoveryAuthorization RecoveryAuthorization(
        RepairRecoveryPlan plan,
        DateTimeOffset? now = null,
        string userSid = UserSid,
        string logonSession = LogonSession,
        DateTimeOffset? expiresAt = null,
        bool perActionConsent = true) => new()
    {
        RecoveryPlanId = plan.RecoveryPlanId,
        RecoveryPlanDigest = plan.Digest,
        UserSid = userSid,
        LogonSessionId = logonSession,
        Nonce = "recovery-nonce-synthetic",
        IssuedAtUtc = now ?? Now,
        ExpiresAtUtc = expiresAt ?? Now.AddMinutes(10),
        PerActionConsent = perActionConsent,
    };

    /// <summary>Creates a private temporary directory for real-file journal tests.</summary>
    public static string CreateJournalDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "netscope-t15-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}




