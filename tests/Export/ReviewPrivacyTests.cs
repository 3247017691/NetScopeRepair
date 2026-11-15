using System.Net;
using System.Text.Json.Nodes;
using NetScope.Application;
using NetScope.Domain;
using NetScope.Storage;
using NetScope.Storage.Export;

namespace NetScope.Export.Tests;

/// <summary>
/// Adversarial review coverage for the P1 privacy gaps found after the M1 export review:
/// structured proxy host lists, NO_PROXY lists, interface identity/SSID, socks:// URLs,
/// extended-length UNC paths, IPv4-mapped IPv6 and interface/route/gateway subnet consistency.
/// The session is synthetic and only uses public package APIs.
/// </summary>
public sealed class ReviewPrivacyTests
{
    private const string SessionId = "session-privacy-review";
    private const string BootId = "boot-privacy-review";
    private const string SnapshotId = "snapshot-privacy-review";
    private const string StructuredProxyServer = "http=user:{{PASSWORD_review}}@proxy.branch.corp:8080;https=secure.branch.corp:8443";
    private const string NoProxyList = ".branch.corp,db.branch.corp:8080,10.0.0.0/8,*.local,*";
    private const string SocksUrl = "socks://SYNTHETIC_USER_REVIEW:{{PASSWORD_review}}@branch-proxy.corp:1080/private";
    private const string ExtendedUnc = @"\\?\UNC\branch-host\share\review.txt";

    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-11-05T02:00:00Z");

    [Fact]
    public async Task Interface_identity_description_and_ssid_are_replaced_by_device_aliases()
    {
        var (entries, _) = await ExportPackageAsync();

        AssertAbsent(entries,
            "Alice-phone", "Alice-phone-fact", "HomeNet-Alice", "Synthetic-Ethernet",
            "proxy.branch.corp", "secure.branch.corp", "db.branch.corp", "branch.corp",
            "pac.branch.corp", "corp.internal", "SYNTHETIC_TOKEN_REVIEW",
            "branch-host", "review.txt", "branch-proxy.corp",
            "SYNTHETIC_USER_REVIEW", "{{PASSWORD_review}}",
            "198.18.0.2", "2001:db8:1:2::10");

        var snapshot = SnapshotJson(entries);
        var networkInterface = snapshot["interfaces"]!["value"]![0]!.AsObject();
        Assert.StartsWith("device-", networkInterface["name"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.StartsWith("device-", networkInterface["description"]!.GetValue<string>(), StringComparison.Ordinal);

        var record = EvidenceRecordJson(entries);
        Assert.StartsWith("device-", record["facts"]!["ssid"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.StartsWith("device-", record["facts"]!["interface_name"]!.GetValue<string>(), StringComparison.Ordinal);

        // A generic "name" and a rule identifier outside the interface context must not be touched.
        Assert.Equal("not-an-interface", record["facts"]!["name"]!.GetValue<string>());
        Assert.Equal("Synthetic Rule A", record["facts"]!["rule_name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Structured_proxy_server_keeps_schemes_and_ports_but_not_hosts()
    {
        var (entries, _) = await ExportPackageAsync();
        var snapshot = SnapshotJson(entries);
        var proxyServer = snapshot["proxies"]!["value"]!["tool_process_environment"]!["value"]!
            ["proxy_server"]!.GetValue<string>();

        var parts = proxyServer.Split(';');
        Assert.Equal(2, parts.Length);
        Assert.StartsWith("http=", parts[0], StringComparison.Ordinal);
        Assert.Contains(":8080", parts[0], StringComparison.Ordinal);
        Assert.StartsWith("https=", parts[1], StringComparison.Ordinal);
        Assert.Contains(":8443", parts[1], StringComparison.Ordinal);
        Assert.DoesNotContain("branch", proxyServer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoProxy_list_preserves_wildcard_port_cidr_prefix_and_list_semantics()
    {
        var (entries, _) = await ExportPackageAsync();
        var snapshot = SnapshotJson(entries);
        var variables = snapshot["proxies"]!["value"]!["tool_process_environment"]!["value"]!
            ["variables"]!.AsArray()
            .Select(item => item!.GetValue<string>())
            .ToArray();
        var noProxy = Assert.Single(variables, value => value.StartsWith("NO_PROXY=", StringComparison.Ordinal));
        var entriesAfter = noProxy["NO_PROXY=".Length..].Split(',');

        Assert.Equal(5, entriesAfter.Length);
        Assert.StartsWith(".", entriesAfter[0], StringComparison.Ordinal);
        Assert.DoesNotContain("branch", entriesAfter[0], StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(":8080", entriesAfter[1], StringComparison.Ordinal);
        Assert.DoesNotContain("branch", entriesAfter[1], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("10.0.0.0/8", entriesAfter[2]);
        Assert.StartsWith("*.", entriesAfter[3], StringComparison.Ordinal);
        Assert.Equal("*", entriesAfter[4]);
    }

    [Fact]
    public async Task Socks_urls_and_extended_unc_paths_lose_their_identity_but_keep_useful_structure()
    {
        var (entries, _) = await ExportPackageAsync();

        AssertAbsent(entries, "branch-proxy.corp", "branch-host", "review.txt", "SYNTHETIC_USER_REVIEW");
        var strings = AllStrings(entries);
        Assert.Contains(strings, value => value.Contains("socks://", StringComparison.Ordinal));
        Assert.Contains(strings, value => value.Contains("path-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ipv4_mapped_ipv6_stays_parseable_and_matches_the_bare_ipv4_alias()
    {
        var (entries, _) = await ExportPackageAsync();
        var resolved = ResolvedAddresses(entries, "probe-privacy-mapped");

        var mappedText = Assert.Single(resolved, value => value.StartsWith("::ffff:", StringComparison.Ordinal));
        var bareText = Assert.Single(resolved, value => !value.Contains(':', StringComparison.Ordinal));

        Assert.True(IPAddress.TryParse(mappedText, out var mapped));
        Assert.True(mapped!.IsIPv4MappedToIPv6);
        Assert.True(IPAddress.TryParse(bareText, out var bare));
        Assert.Equal(bare!.ToString(), mapped.MapToIPv4().ToString());
        AssertAbsent(entries, "::ffff:192.168.1.7", "192.168.1.7");
    }

    [Fact]
    public async Task Interface_route_and_gateway_keep_the_same_subnet_relation()
    {
        var (entries, _) = await ExportPackageAsync();
        var snapshot = SnapshotJson(entries);

        var interfaceText = snapshot["interfaces"]!["value"]![0]!["addresses"]![0]!.GetValue<string>();
        var (interfaceAddress, interfacePrefix) = ParseCidr(interfaceText);
        Assert.Equal(24, interfacePrefix);

        var route = snapshot["routes"]!["value"]!.AsArray()
            .Select(item => item!.AsObject())
            .Single(item => item["destination_prefix"]!.GetValue<string>().StartsWith("10.", StringComparison.Ordinal));
        var (routeAddress, routePrefix) = ParseCidr(route["destination_prefix"]!.GetValue<string>());
        Assert.Equal(24, routePrefix);

        var mappedNetwork = Network(interfaceAddress, interfacePrefix);
        Assert.Equal(routeAddress.ToString(), mappedNetwork.ToString());

        Assert.True(IPAddress.TryParse(route["next_hop"]!.GetValue<string>(), out var gateway));
        Assert.Equal(Network(interfaceAddress, interfacePrefix).ToString(), Network(gateway!, interfacePrefix).ToString());
        AssertAbsent(entries, "192.168.1.10", "192.168.1.0", "192.168.1.1");
    }

    [Fact]
    public async Task Ipv6_interface_route_and_gateway_keep_the_same_prefix_relation()
    {
        var (entries, _) = await ExportPackageAsync();
        var snapshot = SnapshotJson(entries);

        var networkInterface = snapshot["interfaces"]!["value"]!.AsArray()
            .Select(item => item!.AsObject())
            .Single(item => item["addresses"]!.AsArray()
                .Any(address => address!.GetValue<string>().Contains(':', StringComparison.Ordinal)));
        var interfaceText = networkInterface["addresses"]![0]!.GetValue<string>();
        var (interfaceAddress, interfacePrefix) = ParseCidr(interfaceText);
        Assert.Equal(64, interfacePrefix);

        var route = snapshot["routes"]!["value"]!.AsArray()
            .Select(item => item!.AsObject())
            .Single(item =>
            {
                var prefix = item["destination_prefix"]!.GetValue<string>();
                return prefix.Contains(':', StringComparison.Ordinal) && prefix.EndsWith("/64", StringComparison.Ordinal);
            });
        var (routeAddress, routePrefix) = ParseCidr(route["destination_prefix"]!.GetValue<string>());
        Assert.Equal(64, routePrefix);

        Assert.Equal(Network(interfaceAddress, interfacePrefix).ToString(), Network(routeAddress, routePrefix).ToString());
        Assert.True(IPAddress.TryParse(route["next_hop"]!.GetValue<string>(), out var gateway));
        Assert.Equal(Network(interfaceAddress, interfacePrefix).ToString(), Network(gateway!, interfacePrefix).ToString());
    }

    [Fact]
    public async Task Public_ipv4_aliases_are_no_longer_bounded_to_one_hundred_values()
    {
        var (entries, _) = await ExportPackageAsync();
        var resolved = ResolvedAddresses(entries, "probe-privacy-public");

        var originals = PublicAddresses();
        Assert.Equal(originals.Count, resolved.Count);
        Assert.All(resolved, value =>
        {
            Assert.StartsWith("203.", value, StringComparison.Ordinal);
            Assert.True(IPAddress.TryParse(value, out _));
        });
        Assert.True(resolved.Distinct(StringComparer.Ordinal).Count() > 100,
            "The public placeholder space must offer substantially more than the former 100 aliases.");
        AssertAbsent(entries, originals[0], originals[1]);
    }

    [Fact]
    public async Task Event_ids_status_sequence_and_rule_fields_are_not_rewritten()
    {
        var (entries, events) = await ExportPackageAsync();
        var exported = Envelopes(entries["events.ndjson"]);
        Assert.Equal(events.Count, exported.Count);

        var byId = exported.ToDictionary(
            envelope => envelope["event_id"]!.GetValue<string>(),
            StringComparer.Ordinal);
        foreach (var original in events)
        {
            Assert.True(byId.TryGetValue(original.EventId, out var envelope), original.EventId);
            Assert.Equal(WireNames.Of(original.Status), envelope!["status"]!.GetValue<string>());
            Assert.Equal(WireNames.Of(original.EventType), envelope["event_type"]!.GetValue<string>());
            Assert.Equal(original.Sequence, envelope["sequence"]!.GetValue<int>());
        }

        var record = EvidenceRecordJson(entries);
        Assert.Equal("Synthetic Rule A", record["facts"]!["rule_name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Redaction_report_states_the_bounded_address_alias_limits()
    {
        var (entries, _) = await ExportPackageAsync();
        var report = PackageReader.Json(PackageReader.Text(entries["redaction-report.json"])).AsObject();

        Assert.Equal("m1-alias-v3", report["policy_version"]!.GetValue<string>());
        Assert.NotNull(report["address_alias_limits"]);
        Assert.Contains(
            report["removed"]!.AsArray().Select(item => item!.GetValue<string>()),
            value => value.Contains("interface", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            report["removed"]!.AsArray().Select(item => item!.GetValue<string>()),
            value => value.Contains("NO_PROXY", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<(IReadOnlyDictionary<string, byte[]> Entries, IReadOnlyList<EventEnvelope> Events)>
        ExportPackageAsync()
    {
        using var workspace = new TempWorkspace();
        var sessionPath = workspace.File("privacy-review.ndjson");
        var probes = new[]
        {
            Probe("probe-privacy-mapped", 1000,
                new[] { "::ffff:192.168.1.7", "192.168.1.7" },
                "connect " + SocksUrl + " via " + ExtendedUnc + "; interface Alice-phone failed"),
            Probe("probe-privacy-public", 2000, PublicAddresses(), null),
        };
        var events = EvidenceSessionEvents.Create(
            new EvidenceSet
            {
                Snapshot = CreateSnapshot(),
                Probes = probes,
                Records = new[] { CreateRecord() },
            },
            Array.Empty<Diagnosis>(),
            SessionId,
            BootId,
            synthetic: true,
            Origin.AddMinutes(1),
            60000);

        using (var log = new NdjsonEventLog(sessionPath))
        {
            foreach (var envelope in events)
            {
                await log.AppendAsync(envelope, CancellationToken.None);
            }
        }

        var preview = new IncidentExporter().Preview(sessionPath);
        Assert.True(preview.CanExport, string.Join("; ", preview.BlockedReasons));
        var result = await new IncidentExporter().ExportAsync(
            sessionPath, workspace.Subdirectory("packages"), CancellationToken.None);
        return (PackageReader.Read(result.PackagePath), events);
    }

    private static NetworkSnapshot CreateSnapshot() => new()
    {
        SnapshotId = SnapshotId,
        ObservedAt = Origin,
        MonotonicMs = 0,
        Synthetic = true,
        Interfaces = Collected<IReadOnlyList<InterfaceState>>.Ok(new[]
        {
            new InterfaceState
            {
                InterfaceGuid = "interface-privacy",
                Name = "Alice-phone",
                IfIndex = 12,
                Family = "ipv4",
                Description = "Wi-Fi SSID HomeNet-Alice",
                Kind = "wifi",
                OperationalStatus = "up",
                Addresses = new[] { "192.168.1.10/24" },
                Metric = 25,
                AutomaticMetric = true,
                DisableDefaultRoutes = false,
                Luid = "luid-privacy",
            },
            new InterfaceState
            {
                InterfaceGuid = "interface-privacy-v6",
                Name = "Alice-phone-v6",
                IfIndex = 13,
                Family = "ipv6",
                Description = "Wi-Fi SSID HomeNet-Alice",
                Kind = "wifi",
                OperationalStatus = "up",
                Addresses = new[] { "2001:db8:1:2::10/64" },
                Metric = 30,
                AutomaticMetric = true,
                DisableDefaultRoutes = false,
                Luid = "luid-privacy-v6",
            },
        }),
        Routes = Collected<IReadOnlyList<RouteEntry>>.Ok(new[]
        {
            new RouteEntry
            {
                DestinationPrefix = "192.168.1.0/24",
                Family = "ipv4",
                NextHop = "192.168.1.1",
                IfIndex = 12,
                RouteMetric = 0,
                InterfaceMetric = 25,
                Protocol = "dhcp",
            },
            new RouteEntry
            {
                DestinationPrefix = "198.18.0.0/15",
                Family = "ipv4",
                NextHop = "198.18.0.2",
                IfIndex = 12,
                RouteMetric = 0,
                InterfaceMetric = 25,
                Protocol = "manual",
            },
            new RouteEntry
            {
                DestinationPrefix = "2001:db8:1:2::/64",
                Family = "ipv6",
                NextHop = "2001:db8:1:2::1",
                IfIndex = 13,
                RouteMetric = 0,
                InterfaceMetric = 30,
                Protocol = "manual",
            },
        }),
        Dns = Collected<DnsState>.Ok(new DnsState
        {
            ServersByInterface = new Dictionary<int, IReadOnlyList<string>>
            {
                [12] = new[] { "192.168.1.1", "198.18.0.2" },
            },
            SuffixSearchList = new[] { "corp.internal" },
        }),
        Proxies = Collected<ProxyState>.Ok(new ProxyState
        {
            ToolProcessEnvironment = Collected<ProxyLayer>.Ok(new ProxyLayer
            {
                Layer = "tool_process_environment",
                Provenance = "synthetic",
                Enabled = true,
                ProxyServer = StructuredProxyServer,
                Variables = new[]
                {
                    "NO_PROXY=" + NoProxyList,
                    "HTTPS_PROXY=http://SYNTHETIC_USER_REVIEW:{{PASSWORD_review}}@proxy.branch.corp:8080/private",
                    "ALL_PROXY=" + SocksUrl,
                },
                Notice = "proxy read at " + ExtendedUnc,
            }),
            UserEnvironment = Collected<ProxyLayer>.Ok(new ProxyLayer
            {
                Layer = "user_environment",
                Provenance = "synthetic",
                Enabled = false,
                Variables = Array.Empty<string>(),
            }),
            MachineEnvironment = Collected<ProxyLayer>.Ok(new ProxyLayer
            {
                Layer = "machine_environment",
                Provenance = "synthetic",
                Enabled = false,
                Variables = Array.Empty<string>(),
                Notice = "UNC " + ExtendedUnc + " and socks " + SocksUrl
                    + "; plain socks5h://proxy.branch.corp:1080"
                    + "; direct proxy.branch.corp:8080 and db.branch.corp",
            }),
            WinInet = Collected<ProxyLayer>.Ok(new ProxyLayer
            {
                Layer = "wininet",
                Provenance = "synthetic",
                Enabled = true,
                AutoConfigUrl = "https://pac.branch.corp/config.pac?token=SYNTHETIC_TOKEN_REVIEW",
                AutoDetect = true,
            }),
            WinHttp = Collected<ProxyLayer>.Unavailable(
                EventStatus.Unsupported,
                "Synthetic session does not read the WinHTTP store."),
        }),
    };

    private static EvidenceRecord CreateRecord() => new()
    {
        EvidenceId = "record-privacy",
        Kind = "client_runtime_config",
        Summary = "proxy " + StructuredProxyServer + "; UNC " + ExtendedUnc + "; socks " + SocksUrl
            + "; direct proxy.branch.corp:8080 and db.branch.corp",
        ObservedAt = Origin,
        MonotonicMs = 3000,
        BootId = BootId,
        Source = "synthetic.privacy",
        Provenance = "runtime_api",
        Status = EventStatus.Ok,
        TargetId = "target-privacy",
        AddressFamily = "ipv4",
        ObservationWindowId = "window-privacy",
        Facts = new Dictionary<string, string>
        {
            ["no_proxy"] = NoProxyList,
            ["rule_name"] = "Synthetic Rule A",
            ["name"] = "not-an-interface",
            ["interface_name"] = "Alice-phone-fact",
            ["ssid"] = "HomeNet-Alice",
            ["snapshot_id"] = SnapshotId,
            ["if_index"] = "12",
        },
    };

    private static ProbeResult Probe(string id, double monotonic, IReadOnlyList<string> resolved, string? notice) => new()
    {
        ProbeId = id,
        ProbeGroupId = "group-privacy",
        TargetId = "target-privacy",
        Path = ProbePath.NoExplicitProxy,
        Status = EventStatus.Ok,
        StartedAt = Origin.AddMilliseconds(monotonic),
        DurationMs = 10,
        Classification = ProbeClassification.ReachableExpected,
        HttpStatus = 204,
        ResolvedAddresses = resolved,
        AddressFamily = "ipv4",
        Method = "GET",
        RequestOptionsKey = "options-privacy",
        ObservationWindowId = "window-privacy",
        BootId = BootId,
        StartedMonotonicMs = monotonic,
        TimingComparable = true,
        Notice = notice,
    };

    private static IReadOnlyList<string> PublicAddresses() => Enumerable.Range(0, 120)
        .Select(index => $"8.{100 + index / 10}.{10 + index % 200}.{1 + index % 200}")
        .ToArray();

    private static JsonObject SnapshotJson(IReadOnlyDictionary<string, byte[]> entries) =>
        PackageReader.Json(PackageReader.Text(entries["snapshots/snapshot-001.json"])).AsObject();

    private static JsonObject EvidenceRecordJson(IReadOnlyDictionary<string, byte[]> entries) =>
        Envelopes(entries["events.ndjson"])
            .Select(envelope => envelope["payload"]?["evidence_record"]?.AsObject())
            .First(record => record is not null)!;

    private static IReadOnlyList<JsonObject> Envelopes(byte[] eventsBytes) => PackageReader
        .Text(eventsBytes)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => PackageReader.Json(line)!.AsObject())
        .ToArray();

    private static IReadOnlyList<string> ResolvedAddresses(IReadOnlyDictionary<string, byte[]> entries, string probeId)
    {
        var probe = Envelopes(entries["events.ndjson"])
            .Select(envelope => envelope["payload"]?["probe_result"]?.AsObject())
            .FirstOrDefault(result => result is not null && result["probe_id"]!.GetValue<string>() == probeId)
            ?? throw new InvalidDataException("The exported probe could not be found.");
        return probe["resolved_addresses"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
    }

    private static (IPAddress Address, int Prefix) ParseCidr(string value)
    {
        var slash = value.IndexOf('/');
        Assert.True(slash > 0, value);
        var address = IPAddress.Parse(value[..slash]);
        var prefix = int.Parse(value[(slash + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        return (address, prefix);
    }

    private static IPAddress Network(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        for (var index = 0; index < bytes.Length; index++)
        {
            var bits = Math.Clamp(prefix - index * 8, 0, 8);
            bytes[index] &= (byte)(bits == 0 ? 0 : 0xFF << (8 - bits));
        }

        return new IPAddress(bytes);
    }

    private static IReadOnlyList<string> AllStrings(IReadOnlyDictionary<string, byte[]> entries) => entries
        .SelectMany(entry => PackageReader.StringsIn(entry.Value, entry.Key))
        .ToArray();

    private static void AssertAbsent(IReadOnlyDictionary<string, byte[]> entries, params string[] markers)
    {
        foreach (var (name, bytes) in entries)
        {
            var strings = PackageReader.StringsIn(bytes, name);
            var raw = PackageReader.Text(bytes);
            foreach (var marker in markers)
            {
                Assert.DoesNotContain(marker, raw, StringComparison.Ordinal);
                Assert.All(strings, value => Assert.DoesNotContain(marker, value, StringComparison.Ordinal));
            }
        }
    }
}
