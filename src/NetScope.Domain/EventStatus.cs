using System.Text.Json.Serialization;

namespace NetScope.Domain;

/// <summary>
/// Outcome of one collection or probe attempt. Only <see cref="Ok"/> is a success; the
/// remaining values must never be collapsed into "nothing happened".
/// </summary>
[JsonConverter(typeof(WireEnumJsonConverter<EventStatus>))]
public enum EventStatus
{
    [JsonStringEnumMemberName("ok")]
    Ok,

    [JsonStringEnumMemberName("partial")]
    Partial,

    [JsonStringEnumMemberName("failed")]
    Failed,

    [JsonStringEnumMemberName("timeout")]
    Timeout,

    [JsonStringEnumMemberName("cancelled")]
    Cancelled,

    [JsonStringEnumMemberName("unsupported")]
    Unsupported,

    [JsonStringEnumMemberName("access_denied")]
    AccessDenied,

    [JsonStringEnumMemberName("unknown")]
    Unknown,
}

public static class EventStatusExtensions
{
    /// <summary>
    /// True only for <see cref="EventStatus.Ok"/>. A partial result, a timeout, a refused
    /// access or an unsupported call is not a success even when it produced no error text.
    /// </summary>
    public static bool IsSuccess(this EventStatus status) => status == EventStatus.Ok;

    /// <summary>
    /// True when the status means "no usable observation was obtained", so callers must
    /// report a gap instead of an empty-but-fine data set.
    /// </summary>
    public static bool IsObservationGap(this EventStatus status) => status
        is EventStatus.Failed
        or EventStatus.Timeout
        or EventStatus.Cancelled
        or EventStatus.Unsupported
        or EventStatus.AccessDenied
        or EventStatus.Unknown;

    /// <summary>True when the attempt produced at least part of the requested data.</summary>
    public static bool ProducedData(this EventStatus status) =>
        status is EventStatus.Ok or EventStatus.Partial;
}
