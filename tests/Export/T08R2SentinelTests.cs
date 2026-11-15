using System.Text.Json;
using System.Text.Json.Nodes;
using NetScope.Application;
using NetScope.Domain;
using NetScope.Storage;
using NetScope.Storage.Export;

namespace NetScope.Export.Tests;

/// <summary>
/// QA-022 full-package sentinel scan. A synthetic secret is injected into every text surface an
/// export can carry (events, snapshot domains, proxy layers, records, diagnoses, timeline source
/// and extra exception-style payload members) and must be absent from every package file, while
/// event ids, envelope references and typed evidence stay readable.
/// </summary>
public sealed class T08R2SentinelTests
{
    private const string Token = "SYNTHETIC_T08R2_TOKEN";
    private const string Password = "{{PASSWORD_T08R2}}";
    private const string Cookie = "SYNTHETIC_T08R2_COOKIE";
    private const string Bearer = "SYNTHETIC_T08R2_BEARER";
    private const string QuerySecret = "{{SECRET_T08R2_QUERY}}";
    private const string UserName = "sentinel-user";
    private const string DeviceName = "SYNTHETIC-DESKTOP-T08R2";
    private const string InterfaceName = "Synthetic-Uplink-SENTINEL";
    private const string PrivateAddress = "192.168.99.10";
    private const string GatewayAddress = "10.66.0.7";
    private const string InternalHost = "sentinel.corp.internal";
    private const string PublicHost = "api.synthetic-target.example";
    private const string SubscriptionHost = "sub.synthetic.invalid";
    private const string UrlPath = "synthetic-private-path";

    private static readonly string[] Sentinels =
    {
        Token, Password, Cookie, Bearer, QuerySecret, UserName, DeviceName, InterfaceName,
        PrivateAddress, GatewayAddress, InternalHost, PublicHost, SubscriptionHost, UrlPath,
    };

    [Fact]
    public async Task Every_package_surface_loses_the_synthetic_secrets_and_references_survive()
    {
        using var workspace = new TempWorkspace();
        var session = workspace.File("t08r2-sentinel.ndjson");
        await WriteSentinelSessionAsync(session);

        var sourceEvents = new NdjsonEventLog(session).Read().Events;
        Assert.Equal(4, sourceEvents.Count);

        var entries = T08R2Fixture.ExportOrThrow(session, workspace);

        // Whole-package scan: raw bytes and every JSON string value of every entry.
        T08R2Fixture.AssertAbsent(entries, Sentinels);
        Assert.Equal(7, entries.Count);

        // The sensitive surfaces really were present in the source: prove this is not a vacuous scan.
        var sourceText = string.Join("\n", sourceEvents.Select(envelope => EnvelopeJson.Serialize(envelope)));
        foreach (var sentinel in Sentinels)
        {
            Assert.Contains(sentinel, sourceText, StringComparison.Ordinal);
        }

        // Envelope identity, status and references are preserved for replay and cross-file links.
        var exported = T08R2Fixture.Envelopes(entries);
        Assert.Equal(sourceEvents.Count, exported.Count);
        for (var index = 0; index < sourceEvents.Count; index++)
        {
            var original = sourceEvents[index];
            var copy = exported[index];
            Assert.Equal(original.EventId, copy["event_id"]!.GetValue<string>());
            Assert.Equal(original.Sequence, copy["sequence"]!.GetValue<int>());
            Assert.Equal(WireNames.Of(original.Status), copy["status"]!.GetValue<string>());
            Assert.Equal(WireNames.Of(original.EventType), copy["event_type"]!.GetValue<string>());
            Assert.True(JsonNode.DeepEquals(
                JsonSerializer.SerializeToNode(original.Refs, EnvelopeJson.Options),
                copy["refs"]), "envelope refs must survive redaction");
        }

        // The typed evidence metadata still points at existing source events only.
        var eventIds = exported.Select(envelope => envelope["event_id"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        var metadataPointers = exported
            .Select(envelope => envelope["payload"]?["evidence_metadata"]?["source_event_ids"] as JsonArray)
            .Where(array => array is not null)
            .SelectMany(array => array!)
            .Select(item => item!.GetValue<string>())
            .ToArray();
        Assert.NotEmpty(metadataPointers);
        Assert.All(metadataPointers, id => Assert.Contains(id, eventIds));

        // Redaction report exposes categories and the raised policy version, never a value.
        var report = PackageReader.Json(PackageReader.Text(entries["redaction-report.json"])).AsObject();
        Assert.Equal("m1-alias-v3", report["policy_version"]!.GetValue<string>());
        var categories = report["categories"]!.AsArray()
            .Select(item => item!["category"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        foreach (var expected in new[]
                 {
                     "user_name", "device_name", "local_path", "private_host", "public_target",
                     "private_ip", "proxy_credential", "authorization", "cookie", "token", "password",
                     "query_secret",
                 })
        {
            Assert.Contains(expected, categories);
        }

        var manifestText = PackageReader.Text(entries["manifest.json"]);
        Assert.DoesNotContain("\"salt\":", manifestText, StringComparison.Ordinal);
        Assert.Contains("\"salt_included\": false", manifestText, StringComparison.Ordinal);

        var versions = PackageReader.Json(manifestText).AsObject()["versions"]!.AsObject();
        Assert.Contains("synthetic-parser/9.9.9",
            versions["parser_versions"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Contains("9.9.9-synthetic",
            versions["rule_versions"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Contains("1.1",
            versions["source_versions"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Matches("^[0-9a-f]{7,40}$", versions["app_build_commit"]!.GetValue<string>());
        Assert.Equal("assembly_informational_version",
            versions["app_build_commit_source"]!.GetValue<string>());

        var provenance = PackageReader.Json(manifestText).AsObject()["diagnosis_provenance"]!.AsObject();
        Assert.Equal(1, provenance["recorded_in_package"]!.GetValue<int>());
        Assert.Equal(0, provenance["recomputed_by_exporter"]!.GetValue<int>());
    }

    [Fact]
    public async Task Exported_sentinel_session_replays_cleanly_and_keeps_typed_evidence()
    {
        using var workspace = new TempWorkspace();
        var session = workspace.File("t08r2-sentinel-replay.ndjson");
        await WriteSentinelSessionAsync(session);
        var entries = T08R2Fixture.ExportOrThrow(session, workspace);

        var exportedSession = workspace.File("t08r2-sentinel-exported.ndjson");
        await File.WriteAllBytesAsync(exportedSession, entries["events.ndjson"]);

        var replay = new ReplayService().Replay(exportedSession);
        Assert.True(replay.IsClean, string.Join("; ", replay.EvidenceProblems));
        Assert.NotNull(replay.Evidence);
        Assert.NotNull(replay.Evidence!.Snapshot);
        Assert.Single(replay.Evidence.Probes);
        Assert.Single(replay.Evidence.Records);
        Assert.Single(replay.Diagnoses);

        var missingEvidence = Assert.Single(replay.Diagnoses).MissingEvidence;
        Assert.DoesNotContain(InterfaceName, string.Join("; ", missingEvidence), StringComparison.Ordinal);
        Assert.StartsWith("device-", replay.Evidence.Snapshot!.Interfaces.Value!.Single().Name, StringComparison.Ordinal);
        Assert.Equal("[redacted:password]", replay.Evidence.Records.Single().Facts["password"]);
        Assert.Equal("[redacted:token]", replay.Evidence.Records.Single().Facts["api_key"]);
        Assert.DoesNotContain(UserName, replay.Evidence.Records.Single().Summary, StringComparison.Ordinal);
    }

    private static async Task WriteSentinelSessionAsync(string path)
    {
        var snapshot = T08R2Fixture.Snapshot("snapshot-t08r2-sentinel", T08R2Fixture.Origin, 0,
            interfaces: new[]
            {
                T08R2Fixture.Interface("interface-sentinel", InterfaceName, 12,
                    description: "Synthetic uplink C:\\Users\\sentinel-user\\uplink.ini",
                    addresses: new[] { PrivateAddress + "/24" }),
            },
            interfacesStatus: EventStatus.Partial,
            interfacesReason: "Synthetic partial read for " + InterfaceName,
            interfacesErrors: new[]
            {
                "GetIfEntry2 failed for adapter \"vEthernet (Default Switch)\"; user " + UserName
                + "; token=" + Token + "; path C:\\Users\\sentinel-user\\AppData\\Local\\adapter.ini",
            },
            routes: new[]
            {
                new RouteEntry
                {
                    DestinationPrefix = "0.0.0.0/0",
                    Family = "ipv4",
                    NextHop = GatewayAddress,
                    IfIndex = 12,
                    RouteMetric = 0,
                    InterfaceMetric = 25,
                    Protocol = "dhcp",
                },
            },
            winHttpStatus: EventStatus.Unsupported,
            proxyNotice: "Authorization: Bearer " + Bearer + " for https://" + PublicHost + "/" + UrlPath,
            proxyErrors: new[] { "Cookie: session=" + Cookie + "; proxy read from " + InternalHost },
            proxyServer: "http://" + UserName + ":" + Password + "@" + PrivateAddress + ":7890/"
                + UrlPath + "?access_token=" + QuerySecret,
            proxyVariables: new[]
            {
                "HTTPS_PROXY=http://" + UserName + ":" + Password + "@" + InternalHost + ":8080",
            },
            autoConfigUrl: "https://" + PublicHost + "/" + UrlPath + "?token=" + Token);

        var probe = T08R2Fixture.Probe("probe-t08r2-sentinel",
            "connect https://api.synthetic-target.invalid/" + UrlPath + "?access_token=" + Token
            + " failed for adapter \"vEthernet (Default Switch)\"; user " + UserName,
            "probe notice for " + InternalHost + " and C:\\Users\\sentinel-user\\client.ini",
            status: EventStatus.Timeout);

        var record = T08R2Fixture.Record("record-t08r2-sentinel",
            "读取 C:\\Users\\sentinel-user\\AppData\\Roaming\\client\\config.json 失败 for interface " + InterfaceName
            + "; subscription https://" + SubscriptionHost + "/link?token=" + QuerySecret,
            new Dictionary<string, string>
            {
                ["snapshot_id"] = "snapshot-t08r2-sentinel",
                ["user_name"] = UserName,
                ["computer_name"] = DeviceName,
                ["api_key"] = Token,
                ["password"] = Password,
                ["dns_servers"] = PrivateAddress + "," + GatewayAddress,
                ["parser_version"] = "synthetic-parser/9.9.9",
                ["rule_name"] = "Synthetic Rule A",
            });

        var diagnosis = new Diagnosis
        {
            DiagnosisId = "diagnosis-t08r2-sentinel",
            RuleId = "DX-T08R2",
            RuleVersion = "9.9.9-synthetic",
            Level = EvidenceGrade.Supported,
            Summary = "adapter \"vEthernet (Default Switch)\" timeout for https://" + PublicHost + "/" + UrlPath
                + "?access_token=" + Token + "; Cookie: session=" + Cookie,
            EvidenceIds = new[] { "probe-t08r2-sentinel", "record-t08r2-sentinel" },
            CounterEvidenceIds = Array.Empty<string>(),
            MissingEvidence = new[] { "evidence_for " + InterfaceName + " is missing" },
            AllowedRepairActions = Array.Empty<string>(),
            SuggestedNextStep = "read " + InternalHost + " and https://" + SubscriptionHost + "/link?token=" + QuerySecret,
            Scope = new DiagnosisScope
            {
                TargetId = "target-t08r2",
                AddressFamily = "ipv4",
                StartedAt = T08R2Fixture.Origin,
                EndedAt = T08R2Fixture.Origin.AddSeconds(30),
                ValidUntil = T08R2Fixture.Origin.AddSeconds(30),
                BootId = T08R2Fixture.BootId,
                ObservationWindowId = "window-t08r2",
            },
        };

        await T08R2Fixture.WriteAsync(path, new[]
        {
            new CycleEvidence
            {
                Snapshot = snapshot,
                Probes = new[] { probe },
                Records = new[] { record },
                Diagnoses = new[] { diagnosis },
            },
        }, transform: envelope =>
        {
            var payload = envelope.Payload.DeepClone().AsObject();
            if (payload["snapshot"] is not null)
            {
                payload["collector_error"] = "System.Exception: Authorization: Bearer " + Bearer
                    + " user " + UserName + " path C:\\Users\\sentinel-user\\crash.log";
            }
            else if (payload["probe_result"] is not null)
            {
                payload["exception_text"] = "HttpRequestException: connect to https://" + PublicHost + "/"
                    + UrlPath + "?token=" + Token + " failed";
            }
            else if (payload["diagnosis"] is not null)
            {
                payload["detail"] = "subscription https://" + SubscriptionHost + "/link?token=" + QuerySecret;
            }

            return envelope with
            {
                Payload = payload,
                Source = "synthetic.collector?access_token=" + Token,
            };
        });
    }
}
