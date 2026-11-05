using System.Text.Json.Serialization;

namespace NetScope.Domain;

/// <summary>
/// How strongly a statement is supported by evidence. These are not probabilities: a
/// <see cref="Verified"/> claim must carry a controlled verification method, its scope and its
/// limits, and a later success must not be used to upgrade an earlier observation.
/// </summary>
[JsonConverter(typeof(WireEnumJsonConverter<EvidenceGrade>))]
public enum EvidenceGrade
{
    /// <summary>A direct observation, with no explanation attached.</summary>
    [JsonStringEnumMemberName("observed")]
    Observed,

    /// <summary>A lead exists but the causal chain is not verified.</summary>
    [JsonStringEnumMemberName("candidate")]
    Candidate,

    /// <summary>Several comparable evidence items support the statement, within limits.</summary>
    [JsonStringEnumMemberName("supported")]
    Supported,

    /// <summary>Reproduced under controlled verification inside a stated scope.</summary>
    [JsonStringEnumMemberName("verified")]
    Verified,

    /// <summary>Evidence is missing, or the samples are not comparable.</summary>
    [JsonStringEnumMemberName("insufficient")]
    Insufficient,

    /// <summary>Current evidence argues against the statement.</summary>
    [JsonStringEnumMemberName("contradicted")]
    Contradicted,
}
