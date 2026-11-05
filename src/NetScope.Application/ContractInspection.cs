using System.Text.Json;
using NetScope.Domain;

namespace NetScope.Application;

/// <summary>Result of inspecting one NDJSON fixture against the published contract.</summary>
/// <param name="Accepted">Events that were read strictly and also conformed to the schema.</param>
/// <param name="Problems">Human-readable problems, including the line number of each.</param>
/// <param name="RejectedLineNumbers">Lines that were read or validated unsuccessfully.</param>
public sealed record ContractInspectionResult(
    IReadOnlyList<EventEnvelope> Accepted,
    IReadOnlyList<string> Problems,
    IReadOnlyList<int> RejectedLineNumbers)
{
    public int RejectedCount => RejectedLineNumbers.Count;

    public int SuccessCount => Accepted.Count(envelope => envelope.Status.IsSuccess());

    public int PartialCount => Accepted.Count(envelope => envelope.Status == EventStatus.Partial);

    public int ObservationGapCount => Accepted.Count(envelope => envelope.Status.IsObservationGap());

    public bool Passed => Problems.Count == 0;
}

/// <summary>
/// Read-only inspector shared by the CLI and the contract tests. It only reads the fixture and
/// the schema from disk; it performs no network access and touches no system configuration.
/// </summary>
public static class ContractInspection
{
    public static IReadOnlyList<string> FindUnsupportedKeywords(string schemaPath)
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath));
        return SchemaConformance.FindUnsupportedKeywords(schema.RootElement);
    }

    /// <summary>
    /// Reads every non-empty line, then admits a line only when strict deserialization and
    /// schema conformance both succeed. A rejected line is reported but is never counted as an
    /// accepted event or as a success, so a contract violation cannot inflate the totals.
    /// </summary>
    public static ContractInspectionResult Inspect(string fixturePath, string schemaPath, bool requireSynthetic = false)
    {
        using var schemaDocument = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var schema = schemaDocument.RootElement;

        var accepted = new List<EventEnvelope>();
        var problems = new List<string>();
        var rejected = new List<int>();
        var lines = File.ReadAllLines(fixturePath);

        for (var index = 0; index < lines.Length; index++)
        {
            var raw = lines[index].Trim();
            if (raw.Length == 0)
            {
                continue;
            }

            var lineNumber = index + 1;
            var lineProblems = new List<string>();

            EventEnvelope? envelope = null;
            try
            {
                envelope = EnvelopeJson.Deserialize(raw);
            }
            catch (JsonException exception)
            {
                lineProblems.Add($"the envelope could not be read - {exception.Message}");
            }

            if (envelope is not null)
            {
                using var valueDocument = JsonDocument.Parse(raw);
                foreach (var problem in SchemaConformance.Validate(valueDocument.RootElement, schema))
                {
                    lineProblems.Add(problem);
                }

                if (requireSynthetic && !envelope.Synthetic)
                {
                    lineProblems.Add("synthetic must be true for the T01 fixture set");
                }
            }

            if (lineProblems.Count == 0)
            {
                accepted.Add(envelope!);
                continue;
            }

            rejected.Add(lineNumber);
            foreach (var problem in lineProblems)
            {
                problems.Add($"line {lineNumber}: {problem}");
            }
        }

        return new ContractInspectionResult(accepted, problems, rejected);
    }
}
