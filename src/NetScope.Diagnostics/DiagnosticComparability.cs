using System.Net;
using System.Net.Sockets;
using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Diagnostics;

/// <summary>Conservative association rules for observations; absent metadata never proves a match.</summary>
internal static class DiagnosticComparability
{
    public const double MaximumWindowMs = 60_000;
    public const double ContemporaneousMs = 2_000;

    public static bool Present(string? value) => !string.IsNullOrWhiteSpace(value);

    public static bool Same(string? left, string? right) =>
        Present(left) && Present(right) && string.Equals(left, right, StringComparison.Ordinal);

    public static bool Monotonic(double? value) => value.HasValue && double.IsFinite(value.Value) && value.Value >= 0;

    public static bool KnownFamily(string? value) => value is "ipv4" or "ipv6";

    /// <summary>R1-B: a requested policy is any / ipv4 / ipv6; the null family means no constraint.</summary>
    public static bool KnownPolicy(string? value)
    {
        var normalized = ProbeFamilyPolicy.Normalize(value);
        return normalized is "any" or "ipv4" or "ipv6";
    }

    public static string RequestedPolicy(ProbeResult probe) => ProbeFamilyPolicy.Normalize(
        probe.RequestedAddressFamily ?? probe.AddressFamily);

    /// <summary>
    /// The target family is only proven when the producer observed it. Legacy events (no R1-B
    /// fields at all) keep their old AddressFamily reading as an explicit migration fallback.
    /// </summary>
    public static string? TargetFamily(ProbeResult probe)
    {
        if (PresentationHasR1BFields(probe)) return probe.TargetAddressFamily;
        return KnownFamily(probe.AddressFamily) ? probe.AddressFamily : null;
    }

    /// <summary>Family of the real local socket; legacy events fall back to the old raw field.</summary>
    public static string? ConnectionFamily(ProbeResult probe)
    {
        if (PresentationHasR1BFields(probe)) return probe.LocalConnectionAddressFamily;
        return KnownFamily(probe.AddressFamily) ? probe.AddressFamily : null;
    }

    private static bool PresentationHasR1BFields(ProbeResult probe) =>
        probe.RequestedAddressFamily is not null || probe.TargetAddressFamilyStatus is not null;

    public static bool Trusted(EvidenceRecord record) =>
        record.Status == EventStatus.Ok && !record.IsUserReport
        && Present(record.Source) && Present(record.Provenance);

    public static bool TrustedRuntime(EvidenceRecord record) => Trusted(record)
        && record.Provenance is "runtime_api" or "runtime-api";

    public static bool Complete(ProbeResult probe) =>
        Present(probe.TargetId) && KnownPolicy(RequestedPolicy(probe)) && Present(probe.Method)
        && Present(probe.RequestOptionsKey) && Present(probe.ObservationWindowId) && Present(probe.BootId)
        && Monotonic(probe.StartedMonotonicMs) && ValidDuration(probe)
        && (probe.Path == ProbePath.NoExplicitProxy || Present(probe.ProxyEndpointId));

    private static bool ValidDuration(ProbeResult probe) => double.IsFinite(probe.DurationMs) && probe.DurationMs >= 0
        && probe.DurationMs <= (DateTimeOffset.MaxValue - probe.StartedAt).TotalMilliseconds;

    public static bool SameProbeScope(ProbeResult left, ProbeResult right) =>
        Complete(left) && Complete(right) && left.TimingComparable && right.TimingComparable
        && !left.TopologyChanged && !right.TopologyChanged
        && Same(left.TargetId, right.TargetId) && left.Path == right.Path
        && string.Equals(left.ProxyEndpointId, right.ProxyEndpointId, StringComparison.Ordinal)
        && string.Equals(RequestedPolicy(left), RequestedPolicy(right), StringComparison.Ordinal) && Same(left.Method, right.Method)
        && Same(left.RequestOptionsKey, right.RequestOptionsKey)
        && Same(left.ObservationWindowId, right.ObservationWindowId) && Same(left.BootId, right.BootId)
        && Close(left.StartedAt, left.StartedMonotonicMs, right.StartedAt, right.StartedMonotonicMs, MaximumWindowMs);

    public static bool SameRecordWindow(EvidenceRecord left, EvidenceRecord right) =>
        Same(left.ObservationWindowId, right.ObservationWindowId) && Same(left.BootId, right.BootId)
        && left.ObservedAt.HasValue && right.ObservedAt.HasValue
        && Close(left.ObservedAt.Value, left.MonotonicMs, right.ObservedAt.Value, right.MonotonicMs, ContemporaneousMs);

    public static bool SameRecordProbeWindow(EvidenceRecord record, ProbeResult probe) =>
        Same(record.ObservationWindowId, probe.ObservationWindowId) && Same(record.BootId, probe.BootId)
        && KnownFamily(record.AddressFamily) && Same(record.AddressFamily, ConnectionFamily(probe))
        && record.ObservedAt.HasValue
        && Close(record.ObservedAt.Value, record.MonotonicMs, probe.StartedAt, probe.StartedMonotonicMs, ContemporaneousMs);

    public static bool Close(DateTimeOffset leftAt, double? leftMs, DateTimeOffset rightAt, double? rightMs, double limitMs)
    {
        if (!Monotonic(leftMs) || !Monotonic(rightMs))
        {
            return false;
        }

        var wallDelta = (leftAt - rightAt).TotalMilliseconds;
        var monotonicDelta = leftMs!.Value - rightMs!.Value;
        return Math.Abs(wallDelta) <= limitMs && Math.Abs(monotonicDelta) <= limitMs
            && Math.Abs(wallDelta - monotonicDelta) <= ContemporaneousMs;
    }

    public static DiagnosisScope Scope(IEnumerable<ProbeResult> samples)
    {
        var items = samples.ToArray();
        var first = items[0];
        var started = items.Min(probe => probe.StartedAt);
        DateTimeOffset? ended = items.All(ValidDuration)
            ? items.Max(probe => probe.StartedAt.AddMilliseconds(probe.DurationMs))
            : null;
        return new DiagnosisScope
        {
            ProbeGroupId = items.All(probe => Same(probe.ProbeGroupId, first.ProbeGroupId)) ? first.ProbeGroupId : null,
            TargetId = first.TargetId,
            Path = first.Path,
            AddressFamily = RequestedPolicy(first),
            ProxyEndpointId = first.ProxyEndpointId,
            ObservationWindowId = first.ObservationWindowId,
            BootId = first.BootId,
            RequestOptionsKey = first.RequestOptionsKey,
            StartedAt = started,
            EndedAt = ended,
            ValidUntil = ended,
        };
    }

    public static DiagnosisScope Scope(EvidenceRecord record) => new()
    {
        TargetId = record.TargetId,
        AddressFamily = record.AddressFamily,
        ProxyEndpointId = record.ProxyEndpointId,
        ObservationWindowId = record.ObservationWindowId,
        BootId = record.BootId,
        StartedAt = record.ObservedAt,
        EndedAt = record.ObservedAt,
        ValidUntil = record.ObservedAt,
    };

    public static string? NormalizeEndpoint(string? endpoint)
    {
        if (endpoint is null || !Present(endpoint) || !IPEndPoint.TryParse(endpoint, out var parsed) || parsed.Port is <= 0 or > 65535)
        {
            return null;
        }

        var address = parsed.Address.IsIPv4MappedToIPv6 ? parsed.Address.MapToIPv4() : parsed.Address;
        return new IPEndPoint(address, parsed.Port).ToString();
    }

    public static string EndpointFamily(string endpoint) =>
        IPEndPoint.Parse(endpoint).Address.AddressFamily == AddressFamily.InterNetwork ? "ipv4" : "ipv6";
}

