using System.Text;
using NetScope.Application;
using NetScope.Collectors.Windows;
using NetScope.Domain;
using NetScope.Diagnostics;
using NetScope.Storage;

namespace NetScope.Cli;

/// <summary>
/// Read-only command line entry point for M0.
/// <para>
/// Every command only reads. <c>check</c> inspects the published fixture, <c>snapshot</c> reads
/// the local network state once, <c>demo</c> runs the synthetic pipeline into a local directory
/// and <c>replay</c> reads a captured session. None of them can change a setting.
/// </para>
/// </summary>
internal static class Program
{
    private const int Success = 0;

    private const int ContractFailure = 1;

    private const int MissingInput = 2;

    private const int UnsupportedContract = 3;

    private const int BadArguments = 4;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // A redirected or headless console may refuse the encoding change; output still works.
        }

        var command = args.Length == 0 ? "check" : args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        try
        {
            return command switch
            {
                "check" => RunCheck(rest),
                "snapshot" => await RunSnapshotAsync().ConfigureAwait(false),
                "demo" => await RunDemoAsync(rest).ConfigureAwait(false),
                "replay" => RunReplay(rest),
                "help" or "--help" or "-h" => Usage(),
                _ => UnknownCommand(command),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("CANCELLED: the operation was cancelled; no partial result is claimed.");
            return ContractFailure;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"FAILED: local input or persistence failed ({exception.GetType().Name}); no successful save is claimed.");
            return ContractFailure;
        }
    }

    private static int RunCheck(string[] args)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var fixturePath = args.Length > 0
            ? args[0]
            : Path.Combine(baseDirectory, "fixtures", "synthetic-incident.ndjson");
        var schemaPath = Path.Combine(baseDirectory, "contracts", "event-envelope.schema.json");

        Console.WriteLine("NetScope Repair CLI - M0 / contract inspection");
        Console.WriteLine("DATA SOURCE: local NDJSON only. No network probe was performed and no system setting was read or changed.");
        Console.WriteLine($"fixture: {fixturePath}");
        Console.WriteLine($"schema : {schemaPath}");
        Console.WriteLine();

        if (!File.Exists(fixturePath) || !File.Exists(schemaPath))
        {
            Console.Error.WriteLine("FAILED: the fixture or the contract file is missing next to the executable.");
            return MissingInput;
        }

        var unsupportedKeywords = ContractInspection.FindUnsupportedKeywords(schemaPath);
        if (unsupportedKeywords.Count > 0)
        {
            Console.Error.WriteLine("FAILED: the contract uses keywords this build cannot enforce.");
            foreach (var keyword in unsupportedKeywords)
            {
                Console.Error.WriteLine($"  - {keyword}");
            }

            return UnsupportedContract;
        }

        var result = ContractInspection.Inspect(fixturePath, schemaPath, requireSynthetic: args.Length == 0);

        Console.WriteLine($"{"seq",3}  {"event_type",-16}  {"status",-14}  {"success",-7}  {"synthetic",-9}  event_id");

        foreach (var envelope in result.Accepted)
        {
            Console.WriteLine(
                $"{envelope.Sequence,3}  {envelope.EventType,-16}  {envelope.Status,-14}  "
                + $"{(envelope.Status.IsSuccess() ? "yes" : "no"),-7}  {envelope.Synthetic,-9}  {envelope.EventId}");
        }

        Console.WriteLine();
        Console.WriteLine(
            $"events={result.Accepted.Count} ok={result.SuccessCount} "
            + $"partial={result.PartialCount} observation_gap={result.ObservationGapCount} "
            + $"rejected_lines={result.RejectedCount}");
        Console.WriteLine("A partial result, timeout, refusal, unsupported call or unknown outcome is never counted as success.");
        Console.WriteLine("An observation gap is never rendered as an empty-but-healthy result.");
        Console.WriteLine("A line that fails the contract is reported and excluded from every total above.");

        if (!result.Passed)
        {
            Console.WriteLine();
            Console.Error.WriteLine($"FAILED: {result.Problems.Count} problem(s) found.");
            foreach (var problem in result.Problems)
            {
                Console.Error.WriteLine($"  - {problem}");
            }

            return ContractFailure;
        }

        Console.WriteLine();
        Console.WriteLine("OK: every envelope was read and conforms to the published contract.");
        return Success;
    }

    private static async Task<int> RunSnapshotAsync()
    {
        var clock = new SystemClock();
        var now = clock.UtcNow;

        var context = new CollectionContext
        {
            SessionId = $"session-{now:yyyyMMddTHHmmss}",
            BootId = $"boot-{Environment.TickCount64}",
            SnapshotId = $"snapshot-{now:yyyyMMddTHHmmss}",
            ObservedAt = now,
            MonotonicMs = clock.MonotonicMs,
            Synthetic = false,
        };

        Console.WriteLine("NetScope Repair CLI - read-only snapshot");
        Console.WriteLine("READ ONLY: no route, interface, DNS, proxy or registry value is modified; no network request is sent.");
        Console.WriteLine();

        var snapshot = await new WindowsSnapshotCollector().CollectAsync(context, CancellationToken.None);

        Console.WriteLine("domain status");
        foreach (var pair in snapshot.Completeness())
        {
            Console.WriteLine($"  {pair.Key,-12} {pair.Value}");
        }

        Console.WriteLine();
        Console.WriteLine($"interfaces ({snapshot.Interfaces.Value?.Count ?? 0})");
        Console.WriteLine($"  {"idx",3}  {"family",-7}  {"kind",-9}  {"status",-10}  {"metric",6}  {"auto",-5}  {"noDefRt",-7}  name");

        foreach (var item in (snapshot.Interfaces.Value ?? Array.Empty<InterfaceState>()).OrderBy(entry => entry.IfIndex))
        {
            Console.WriteLine(
                $"  {item.IfIndex,3}  {item.Family,-7}  {item.Kind,-9}  {item.OperationalStatus,-10}  "
                + $"{item.Metric?.ToString() ?? "-",6}  {Format(item.AutomaticMetric),-5}  {Format(item.DisableDefaultRoutes),-7}  {item.Name}");
        }

        if (snapshot.Interfaces.Reason is { Length: > 0 } interfaceReason)
        {
            Console.WriteLine($"  note: {interfaceReason}");
        }

        Console.WriteLine();
        var routes = snapshot.Routes.Value ?? Array.Empty<RouteEntry>();
        Console.WriteLine($"routes ({routes.Count}): IPv4 default={routes.Count(route => route.Family == "ipv4" && route.DestinationPrefix == "0.0.0.0/0")}, "
            + $"IPv6 default={routes.Count(route => route.Family == "ipv6" && route.DestinationPrefix == "::/0")}");

        foreach (var route in routes
                     .Where(route => route.DestinationPrefix is "0.0.0.0/0" or "::/0")
                     .OrderBy(route => route.TotalMetric)
                     .Take(6))
        {
            Console.WriteLine(
                $"  {route.DestinationPrefix,-12} if={route.IfIndex,-3} route_metric={route.RouteMetric,-3} "
                + $"if_metric={route.InterfaceMetric?.ToString() ?? "-",-3} total={route.TotalMetric?.ToString() ?? "-",-3} next={route.NextHop} protocol={route.Protocol}");
        }

        Console.WriteLine();
        Console.WriteLine("proxy layers");
        var proxies = snapshot.Proxies.Value;
        if (proxies is null)
        {
            Console.WriteLine($"  unavailable: {snapshot.Proxies.Reason}");
        }
        else
        {
            foreach (var (name, layer) in new (string, Collected<ProxyLayer>)[]
                     {
                         ("tool_process_environment", proxies.ToolProcessEnvironment),
                         ("user_environment", proxies.UserEnvironment),
                         ("machine_environment", proxies.MachineEnvironment),
                         ("wininet", proxies.WinInet),
                         ("winhttp", proxies.WinHttp),
                     })
            {
                Console.WriteLine(
                    $"  {name,-24} {layer.Status,-12} enabled={Format(layer.Value?.Enabled)} "
                    + $"values={layer.Value?.Variables.Count ?? 0}{(layer.Reason is null ? string.Empty : "  reason=" + layer.Reason)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("A high metric, a reserved-range address or two clients running at once is a clue, not a root cause.");
        return Success;
    }

    private static async Task<int> RunDemoAsync(string[] args)
    {
        var outputDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetScopeRepair",
            "demo");

        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] is "--out" or "-o" && index + 1 < args.Length)
            {
                outputDirectory = args[index + 1];
                index++;
            }
        }

        return await DemoPipeline.RunAsync(outputDirectory, Console.Out, CancellationToken.None).ConfigureAwait(false);
    }

    private static int RunReplay(string[] args)
    {
        var path = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "fixtures", "synthetic-incident.ndjson");

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"FAILED: no session log at {path}");
            return MissingInput;
        }

        Console.WriteLine("NetScope Repair CLI - offline replay");
        Console.WriteLine($"log: {path}");
        Console.WriteLine();

        var result = new ReplayService().Replay(path);
        var summary = result.Summary;

        Console.WriteLine($"events={summary.EventCount} synthetic={summary.SyntheticCount}");
        Console.WriteLine($"ok={summary.SuccessCount} partial={summary.PartialCount} observation_gap={summary.ObservationGapCount}");
        Console.WriteLine($"duplicates={result.Log.DuplicateCount} gaps={result.Log.SequenceGaps.Count} problems={result.Log.Problems.Count}");
        Console.WriteLine("by event type");

        foreach (var pair in summary.ByType)
        {
            Console.WriteLine($"  {WireNames.Of(pair.Key),-16} {pair.Value}");
        }

        foreach (var gap in result.Log.SequenceGaps)
        {
            Console.WriteLine($"  gap: sequences {gap.Expected}..{gap.Found - 1} are missing ({gap.Missing} event(s))");
        }

        foreach (var problem in result.Log.Problems)
        {
            Console.WriteLine($"  problem: line {problem.LineNumber}: {problem.Message}{(problem.IsTruncatedTail ? " [truncated tail]" : string.Empty)}");
        }

        foreach (var problem in result.EvidenceProblems) Console.WriteLine($"  evidence_problem: {problem}");
        var ruleFailures = 0;
        if (result.IsClean && result.Evidence is not null)
        {
            // Recompute separately per snapshot. A snapshot is never combined with another
            // cycle's probes; recorded diagnoses may still cite evidence across cycles.
            var scopes = result.SnapshotScopes.Count > 0
                ? result.SnapshotScopes.Select(scope => (SnapshotId: scope.Snapshot.SnapshotId, scope.Evidence)).ToArray()
                : new[] { (SnapshotId: "unscoped", Evidence: result.Evidence) };
            var evaluations = scopes
                .Select(scope => (scope.SnapshotId, Evaluation: DiagnosticEngine.CreateDefault().EvaluateDetailed(scope.Evidence)))
                .ToArray();
            ruleFailures = evaluations.Sum(item => item.Evaluation.Failures.Count);
            var recomputed = evaluations.Sum(item => item.Evaluation.Diagnoses.Count);

            Console.WriteLine(
                $"typed_evidence=yes recorded_diagnoses={result.Diagnoses.Count} "
                + $"recomputed_diagnoses={recomputed} rule_errors={ruleFailures}");
            if (result.SnapshotScopes.Count > 1)
            {
                Console.WriteLine($"snapshot_scopes={result.SnapshotScopes.Count} "
                    + $"unscoped_evidence={(result.UnscopedEvidence is null ? "no" : "yes")}");
                Console.WriteLine("note: 每个快照单独重算；已保存的跨周期诊断只作为记录展示，不并入单快照证据。");
            }

            foreach (var (snapshotId, evaluation) in evaluations)
            {
                var prefix = result.SnapshotScopes.Count > 1 ? $"  [{snapshotId}] " : "  ";
                foreach (var diagnosis in evaluation.Diagnoses)
                    Console.WriteLine($"{prefix}{diagnosis.RuleId} [{WireNames.Of(diagnosis.Level)}] {diagnosis.Summary}");
                foreach (var failure in evaluation.Failures)
                    Console.WriteLine($"{prefix}rule_error: {failure.RuleId} {failure.ErrorType}");
            }
        }
        else if (result.Evidence is null)
            Console.WriteLine("typed_evidence=no: legacy event summary only; diagnostic premises were not recorded by this format.");
        Console.WriteLine();
        Console.WriteLine("Replay reads saved evidence only; it cannot reproduce the outage or change the machine.");
        return result.IsClean && ruleFailures == 0 ? Success : ContractFailure;
    }

    private static string Format(bool? value) => value is null ? "-" : value.Value ? "yes" : "no";

    private static int Usage()
    {
        Console.WriteLine("netscope - NetScope Repair M0 read-only CLI");
        Console.WriteLine();
        Console.WriteLine("  netscope check                 inspect the published contract and the synthetic fixture");
        Console.WriteLine("  netscope snapshot              read the local network state once (read-only, no elevation)");
        Console.WriteLine("  netscope demo [--out <dir>]    run the synthetic pipeline and write a session log + index");
        Console.WriteLine("  netscope replay [<file>]       replay a captured NDJSON session offline");
        Console.WriteLine("  netscope help                  show this text");
        Console.WriteLine();
        Console.WriteLine("No command in M0 can change a network setting, start a repair or request elevation.");
        return Success;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"FAILED: unknown command '{command}'.");
        Console.Error.WriteLine("Run 'netscope help' for the supported commands.");
        return BadArguments;
    }
}
